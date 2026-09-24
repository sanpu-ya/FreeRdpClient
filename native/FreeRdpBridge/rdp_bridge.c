/**
 * FreeRdpBridge - thin C API on top of FreeRDP 3 for use from .NET.
 *
 * Structure follows client/SDL/SDL3 (RDP thread + UI thread), keyboard handling
 * follows client/Windows/wf_event.c and the pointer handling follows
 * client/Windows/wf_graphics.c.
 */
#define RDPB_EXPORTS
#include "rdp_bridge.h"

#include <winpr/wtypes.h>
#include <winpr/crt.h>
#include <winpr/synch.h>
#include <winpr/thread.h>
#include <winpr/string.h>

#include <freerdp/freerdp.h>
#include <freerdp/addin.h>
#include <freerdp/client.h>
#include <freerdp/client/cmdline.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/cliprdr.h>
#include <freerdp/client/disp.h>
#include <freerdp/channels/channels.h>
#include <freerdp/channels/cliprdr.h>
#include <freerdp/channels/disp.h>
#include <freerdp/channels/rdpgfx.h>
#include <freerdp/codec/color.h>
#include <freerdp/event.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/gdi/gfx.h>
#include <freerdp/input.h>
#include <freerdp/locale/keyboard.h>
#include <freerdp/scancode.h>
#include <freerdp/settings.h>
#include <freerdp/version.h>
#include <freerdp/log.h>

#include <windows.h>

#define TAG CLIENT_TAG("dotnet.bridge")

#define DEAD_CURSOR_SLOTS 32
#define CF_UNICODETEXT_ID 13

typedef struct
{
	rdpClientContext common;
	rdpb_session* session;
} bridgeContext;

typedef struct
{
	rdpPointer pointer;
	HCURSOR cursor;
} bridgePointer;

struct rdpb_session
{
	rdpb_callbacks cb;
	rdpContext* context;
	HANDLE thread;

	/* Guards "active": input and display control are only sent while connected. */
	SRWLOCK lock;
	BOOL active;

	/* Credentials supplied from on_authenticate */
	CRITICAL_SECTION auth_lock;
	char* auth_user;
	char* auth_password;
	char* auth_domain;
	BOOL auth_set;

	/* Keyboard state (UI thread only) */
	BOOL keystates[512];

	/* Keyboard type chosen by the client (see bridge_on_connection_state_change) */
	UINT32 kbd_type;
	UINT32 kbd_subtype;
	UINT32 kbd_fnkeys;

	/* Display control */
	DispClientContext* disp;
	volatile LONG disp_ready;
	volatile LONG gfx_active;

	/* Clipboard */
	CRITICAL_SECTION clip_lock;
	CliprdrClientContext* cliprdr;
	BOOL clip_ready;
	WCHAR* clip_text;
	UINT32 clip_length;

	/* Pointer */
	CRITICAL_SECTION cursor_lock;
	HCURSOR current_cursor;
	HCURSOR dead_cursors[DEAD_CURSOR_SLOTS];
	size_t dead_count;
};

static rdpb_session* get_session(rdpContext* context)
{
	if (!context)
		return nullptr;
	return ((bridgeContext*)context)->session;
}

static char* dup_or_null(const char* str)
{
	if (!str)
		return nullptr;
	return _strdup(str);
}

/* ------------------------------------------------------------------------ */
/* Pointer                                                                  */
/* ------------------------------------------------------------------------ */

static HCURSOR create_cursor(const BYTE* bgra, UINT32 width, UINT32 height, UINT32 hotX,
                             UINT32 hotY)
{
	BITMAPV5HEADER bi = { 0 };
	bi.bV5Size = sizeof(bi);
	bi.bV5Width = (LONG)width;
	bi.bV5Height = -(LONG)height;
	bi.bV5Planes = 1;
	bi.bV5BitCount = 32;
	bi.bV5Compression = BI_BITFIELDS;
	bi.bV5RedMask = 0x00FF0000;
	bi.bV5GreenMask = 0x0000FF00;
	bi.bV5BlueMask = 0x000000FF;
	bi.bV5AlphaMask = 0xFF000000;

	void* bits = nullptr;
	HDC hdc = GetDC(nullptr);
	HBITMAP color = CreateDIBSection(hdc, (BITMAPINFO*)&bi, DIB_RGB_COLORS, &bits, nullptr, 0);
	ReleaseDC(nullptr, hdc);
	if (!color || !bits)
		return nullptr;

	memcpy(bits, bgra, (size_t)width * height * 4);

	/* Monochrome bitmaps are WORD aligned. An all zero AND mask lets the alpha channel decide. */
	const size_t maskStride = ((width + 15) / 16) * 2;
	BYTE* maskBits = calloc(maskStride * height, 1);
	HBITMAP mask = CreateBitmap((int)width, (int)height, 1, 1, maskBits);

	ICONINFO ii = { 0 };
	ii.fIcon = FALSE;
	ii.xHotspot = hotX;
	ii.yHotspot = hotY;
	ii.hbmMask = mask;
	ii.hbmColor = color;
	HCURSOR cursor = CreateIconIndirect(&ii);

	DeleteObject(mask);
	DeleteObject(color);
	free(maskBits);
	return cursor;
}

static BOOL bridge_pointer_new(rdpContext* context, rdpPointer* pointer)
{
	bridgePointer* ptr = (bridgePointer*)pointer;
	if (!context || !ptr)
		return FALSE;

	ptr->cursor = nullptr;
	rdpGdi* gdi = context->gdi;
	const UINT32 w = pointer->width;
	const UINT32 h = pointer->height;
	if (!gdi || (w == 0) || (h == 0))
		return TRUE;

	BYTE* data = winpr_aligned_malloc((size_t)w * h * 4, 16);
	if (!data)
		return FALSE;

	if (freerdp_image_copy_from_pointer_data(data, PIXEL_FORMAT_BGRA32, w * 4, 0, 0, w, h,
	                                         pointer->xorMaskData, pointer->lengthXorMask,
	                                         pointer->andMaskData, pointer->lengthAndMask,
	                                         pointer->xorBpp, &gdi->palette))
	{
		ptr->cursor = create_cursor(data, w, h, pointer->xPos, pointer->yPos);
	}

	winpr_aligned_free(data);
	return TRUE;
}

static void bridge_pointer_free(rdpContext* context, rdpPointer* pointer)
{
	rdpb_session* s = get_session(context);
	bridgePointer* ptr = (bridgePointer*)pointer;
	if (!s || !ptr || !ptr->cursor)
		return;

	HCURSOR cursor = ptr->cursor;
	ptr->cursor = nullptr;

	/* The UI thread may still be using the cursor, so destruction is deferred. */
	EnterCriticalSection(&s->cursor_lock);
	if (s->dead_count == DEAD_CURSOR_SLOTS)
	{
		for (size_t i = 0; i < s->dead_count; i++)
		{
			if (s->dead_cursors[i] != s->current_cursor)
			{
				DestroyCursor(s->dead_cursors[i]);
				memmove(&s->dead_cursors[i], &s->dead_cursors[i + 1],
				        (s->dead_count - i - 1) * sizeof(HCURSOR));
				s->dead_count--;
				break;
			}
		}
	}

	if (s->dead_count < DEAD_CURSOR_SLOTS)
		s->dead_cursors[s->dead_count++] = cursor;
	else
		DestroyCursor(cursor);
	LeaveCriticalSection(&s->cursor_lock);
}

static void notify_pointer(rdpb_session* s, int32_t action, HCURSOR cursor)
{
	EnterCriticalSection(&s->cursor_lock);
	s->current_cursor = cursor;
	LeaveCriticalSection(&s->cursor_lock);

	if (s->cb.on_pointer)
		s->cb.on_pointer(s->cb.user, action, cursor);
}

static BOOL bridge_pointer_set(rdpContext* context, rdpPointer* pointer)
{
	rdpb_session* s = get_session(context);
	bridgePointer* ptr = (bridgePointer*)pointer;
	if (!s || !ptr)
		return FALSE;

	if (ptr->cursor)
		notify_pointer(s, RDPB_POINTER_SET, ptr->cursor);
	else
		notify_pointer(s, RDPB_POINTER_HIDE, nullptr);
	return TRUE;
}

static BOOL bridge_pointer_set_null(rdpContext* context)
{
	rdpb_session* s = get_session(context);
	if (!s)
		return FALSE;
	notify_pointer(s, RDPB_POINTER_HIDE, nullptr);
	return TRUE;
}

static BOOL bridge_pointer_set_default(rdpContext* context)
{
	rdpb_session* s = get_session(context);
	if (!s)
		return FALSE;
	notify_pointer(s, RDPB_POINTER_DEFAULT, nullptr);
	return TRUE;
}

static BOOL bridge_pointer_set_position(rdpContext* context, UINT32 x, UINT32 y)
{
	WINPR_UNUSED(x);
	WINPR_UNUSED(y);
	return context != nullptr;
}

static BOOL bridge_register_pointer(rdpGraphics* graphics)
{
	rdpPointer pointer = { 0 };
	pointer.size = sizeof(bridgePointer);
	pointer.New = bridge_pointer_new;
	pointer.Free = bridge_pointer_free;
	pointer.Set = bridge_pointer_set;
	pointer.SetNull = bridge_pointer_set_null;
	pointer.SetDefault = bridge_pointer_set_default;
	pointer.SetPosition = bridge_pointer_set_position;
	graphics_register_pointer(graphics, &pointer);
	return TRUE;
}

/* ------------------------------------------------------------------------ */
/* Painting                                                                 */
/* ------------------------------------------------------------------------ */

static BOOL bridge_begin_paint(rdpContext* context)
{
	rdpGdi* gdi = context->gdi;
	if (!gdi || !gdi->primary || !gdi->primary->hdc)
		return FALSE;

	HGDI_WND hwnd = gdi->primary->hdc->hwnd;
	if (!hwnd || !hwnd->invalid)
		return FALSE;

	hwnd->invalid->null = TRUE;
	hwnd->ninvalid = 0;
	return TRUE;
}

static void emit_frame(rdpb_session* s, rdpGdi* gdi, INT32 x1, INT32 y1, INT32 x2, INT32 y2)
{
	const INT32 width = (INT32)gdi->width;
	const INT32 height = (INT32)gdi->height;
	if (x1 < 0)
		x1 = 0;
	if (y1 < 0)
		y1 = 0;
	if (x2 > width)
		x2 = width;
	if (y2 > height)
		y2 = height;
	if ((x2 <= x1) || (y2 <= y1))
		return;

	s->cb.on_frame(s->cb.user, gdi->primary_buffer, gdi->stride, gdi->width, gdi->height, x1, y1,
	               x2 - x1, y2 - y1);
}

static BOOL bridge_end_paint(rdpContext* context)
{
	rdpb_session* s = get_session(context);
	rdpGdi* gdi = context->gdi;
	if (!s || !gdi || !gdi->primary || !gdi->primary->hdc)
		return FALSE;

	HGDI_WND hwnd = gdi->primary->hdc->hwnd;
	if (!hwnd || !hwnd->invalid || hwnd->invalid->null)
		return TRUE;

	const INT32 ninvalid = hwnd->ninvalid;
	if ((ninvalid < 1) || !s->cb.on_frame)
		return TRUE;

	const HGDI_RGN cinvalid = hwnd->cinvalid;

	if (ninvalid <= 16)
	{
		for (INT32 i = 0; i < ninvalid; i++)
			emit_frame(s, gdi, cinvalid[i].x, cinvalid[i].y, cinvalid[i].x + cinvalid[i].w,
			           cinvalid[i].y + cinvalid[i].h);
	}
	else
	{
		INT32 x1 = INT32_MAX;
		INT32 y1 = INT32_MAX;
		INT32 x2 = INT32_MIN;
		INT32 y2 = INT32_MIN;
		for (INT32 i = 0; i < ninvalid; i++)
		{
			x1 = MIN(x1, cinvalid[i].x);
			y1 = MIN(y1, cinvalid[i].y);
			x2 = MAX(x2, cinvalid[i].x + cinvalid[i].w);
			y2 = MAX(y2, cinvalid[i].y + cinvalid[i].h);
		}
		emit_frame(s, gdi, x1, y1, x2, y2);
	}

	return TRUE;
}

static BOOL bridge_desktop_resize(rdpContext* context)
{
	rdpb_session* s = get_session(context);
	rdpSettings* settings = context->settings;
	if (!s || !settings || !context->gdi)
		return FALSE;

	const UINT32 w = freerdp_settings_get_uint32(settings, FreeRDP_DesktopWidth);
	const UINT32 h = freerdp_settings_get_uint32(settings, FreeRDP_DesktopHeight);
	if (!gdi_resize(context->gdi, w, h))
		return FALSE;

	if (s->cb.on_desktop_resize)
		s->cb.on_desktop_resize(s->cb.user, context->gdi->width, context->gdi->height);
	return TRUE;
}

static BOOL bridge_keyboard_set_indicators(rdpContext* context, UINT16 led_flags)
{
	WINPR_UNUSED(led_flags);
	return context != nullptr;
}

static BOOL bridge_keyboard_set_ime_status(rdpContext* context, UINT16 imeId, UINT32 imeState,
                                           UINT32 imeConvMode)
{
	WINPR_UNUSED(imeId);
	WINPR_UNUSED(imeState);
	WINPR_UNUSED(imeConvMode);
	return context != nullptr;
}

/* ------------------------------------------------------------------------ */
/* Clipboard (text only)                                                    */
/* ------------------------------------------------------------------------ */

static UINT clip_send_format_list(rdpb_session* s)
{
	/* caller holds clip_lock */
	CliprdrClientContext* cliprdr = s->cliprdr;
	if (!cliprdr || !s->clip_ready)
		return CHANNEL_RC_OK;

	CLIPRDR_FORMAT format = { 0 };
	format.formatId = CF_UNICODETEXT_ID;
	format.formatName = nullptr;

	CLIPRDR_FORMAT_LIST list = { 0 };
	list.common.msgType = CB_FORMAT_LIST;
	list.numFormats = s->clip_text ? 1 : 0;
	list.formats = s->clip_text ? &format : nullptr;
	return cliprdr->ClientFormatList(cliprdr, &list);
}

static UINT clip_monitor_ready(CliprdrClientContext* cliprdr, const CLIPRDR_MONITOR_READY* ready)
{
	WINPR_UNUSED(ready);
	rdpb_session* s = (rdpb_session*)cliprdr->custom;

	CLIPRDR_GENERAL_CAPABILITY_SET general = { 0 };
	general.capabilitySetType = CB_CAPSTYPE_GENERAL;
	general.capabilitySetLength = CB_CAPSTYPE_GENERAL_LEN;
	general.version = CB_CAPS_VERSION_2;
	general.generalFlags = CB_USE_LONG_FORMAT_NAMES;

	CLIPRDR_CAPABILITIES caps = { 0 };
	caps.cCapabilitiesSets = 1;
	caps.capabilitySets = (CLIPRDR_CAPABILITY_SET*)&general;

	UINT rc = cliprdr->ClientCapabilities(cliprdr, &caps);
	if (rc != CHANNEL_RC_OK)
		return rc;

	EnterCriticalSection(&s->clip_lock);
	s->clip_ready = TRUE;
	rc = clip_send_format_list(s);
	LeaveCriticalSection(&s->clip_lock);
	return rc;
}

static UINT clip_server_capabilities(CliprdrClientContext* cliprdr,
                                     const CLIPRDR_CAPABILITIES* capabilities)
{
	WINPR_UNUSED(cliprdr);
	WINPR_UNUSED(capabilities);
	return CHANNEL_RC_OK;
}

static UINT clip_server_format_list(CliprdrClientContext* cliprdr,
                                    const CLIPRDR_FORMAT_LIST* formatList)
{
	CLIPRDR_FORMAT_LIST_RESPONSE response = { 0 };
	response.common.msgType = CB_FORMAT_LIST_RESPONSE;
	response.common.msgFlags = CB_RESPONSE_OK;
	UINT rc = cliprdr->ClientFormatListResponse(cliprdr, &response);
	if (rc != CHANNEL_RC_OK)
		return rc;

	BOOL hasText = FALSE;
	for (UINT32 i = 0; i < formatList->numFormats; i++)
	{
		if (formatList->formats[i].formatId == CF_UNICODETEXT_ID)
			hasText = TRUE;
	}

	if (!hasText)
		return CHANNEL_RC_OK;

	/* Text is fetched eagerly so the local clipboard can be updated right away. */
	CLIPRDR_FORMAT_DATA_REQUEST request = { 0 };
	request.common.msgType = CB_FORMAT_DATA_REQUEST;
	request.requestedFormatId = CF_UNICODETEXT_ID;
	return cliprdr->ClientFormatDataRequest(cliprdr, &request);
}

static UINT clip_server_format_list_response(CliprdrClientContext* cliprdr,
                                             const CLIPRDR_FORMAT_LIST_RESPONSE* response)
{
	WINPR_UNUSED(cliprdr);
	WINPR_UNUSED(response);
	return CHANNEL_RC_OK;
}

static UINT clip_server_lock(CliprdrClientContext* cliprdr, const CLIPRDR_LOCK_CLIPBOARD_DATA* d)
{
	WINPR_UNUSED(cliprdr);
	WINPR_UNUSED(d);
	return CHANNEL_RC_OK;
}

static UINT clip_server_unlock(CliprdrClientContext* cliprdr,
                               const CLIPRDR_UNLOCK_CLIPBOARD_DATA* d)
{
	WINPR_UNUSED(cliprdr);
	WINPR_UNUSED(d);
	return CHANNEL_RC_OK;
}

static UINT clip_server_format_data_request(CliprdrClientContext* cliprdr,
                                            const CLIPRDR_FORMAT_DATA_REQUEST* request)
{
	rdpb_session* s = (rdpb_session*)cliprdr->custom;

	CLIPRDR_FORMAT_DATA_RESPONSE response = { 0 };
	response.common.msgType = CB_FORMAT_DATA_RESPONSE;
	response.common.msgFlags = CB_RESPONSE_FAIL;

	EnterCriticalSection(&s->clip_lock);
	BYTE* copy = nullptr;
	if ((request->requestedFormatId == CF_UNICODETEXT_ID) && s->clip_text)
	{
		const size_t size = ((size_t)s->clip_length + 1) * sizeof(WCHAR);
		copy = malloc(size);
		if (copy)
		{
			memcpy(copy, s->clip_text, size);
			response.common.msgFlags = CB_RESPONSE_OK;
			response.common.dataLen = (UINT32)size;
			response.requestedFormatData = copy;
		}
	}
	LeaveCriticalSection(&s->clip_lock);

	const UINT rc = cliprdr->ClientFormatDataResponse(cliprdr, &response);
	free(copy);
	return rc;
}

static UINT clip_server_format_data_response(CliprdrClientContext* cliprdr,
                                             const CLIPRDR_FORMAT_DATA_RESPONSE* response)
{
	rdpb_session* s = (rdpb_session*)cliprdr->custom;
	if (!(response->common.msgFlags & CB_RESPONSE_OK) || !response->requestedFormatData)
		return CHANNEL_RC_OK;

	const WCHAR* text = (const WCHAR*)response->requestedFormatData;
	UINT32 length = response->common.dataLen / sizeof(WCHAR);
	while ((length > 0) && (text[length - 1] == 0))
		length--;

	if (s->cb.on_clipboard_text)
		s->cb.on_clipboard_text(s->cb.user, (const uint16_t*)text, length);
	return CHANNEL_RC_OK;
}

static UINT clip_server_file_contents_request(CliprdrClientContext* cliprdr,
                                              const CLIPRDR_FILE_CONTENTS_REQUEST* request)
{
	CLIPRDR_FILE_CONTENTS_RESPONSE response = { 0 };
	response.common.msgType = CB_FILECONTENTS_RESPONSE;
	response.common.msgFlags = CB_RESPONSE_FAIL;
	response.streamId = request->streamId;
	return cliprdr->ClientFileContentsResponse(cliprdr, &response);
}

static UINT clip_server_file_contents_response(CliprdrClientContext* cliprdr,
                                               const CLIPRDR_FILE_CONTENTS_RESPONSE* response)
{
	WINPR_UNUSED(cliprdr);
	WINPR_UNUSED(response);
	return CHANNEL_RC_OK;
}

static void clip_init(rdpb_session* s, CliprdrClientContext* cliprdr)
{
	cliprdr->custom = s;
	cliprdr->MonitorReady = clip_monitor_ready;
	cliprdr->ServerCapabilities = clip_server_capabilities;
	cliprdr->ServerFormatList = clip_server_format_list;
	cliprdr->ServerFormatListResponse = clip_server_format_list_response;
	cliprdr->ServerLockClipboardData = clip_server_lock;
	cliprdr->ServerUnlockClipboardData = clip_server_unlock;
	cliprdr->ServerFormatDataRequest = clip_server_format_data_request;
	cliprdr->ServerFormatDataResponse = clip_server_format_data_response;
	cliprdr->ServerFileContentsRequest = clip_server_file_contents_request;
	cliprdr->ServerFileContentsResponse = clip_server_file_contents_response;

	EnterCriticalSection(&s->clip_lock);
	s->cliprdr = cliprdr;
	s->clip_ready = FALSE;
	LeaveCriticalSection(&s->clip_lock);
}

static void clip_uninit(rdpb_session* s, CliprdrClientContext* cliprdr)
{
	EnterCriticalSection(&s->clip_lock);
	if (s->cliprdr == cliprdr)
	{
		s->cliprdr = nullptr;
		s->clip_ready = FALSE;
	}
	LeaveCriticalSection(&s->clip_lock);
	cliprdr->custom = nullptr;
}

/* ------------------------------------------------------------------------ */
/* Channels                                                                 */
/* ------------------------------------------------------------------------ */

static UINT bridge_disp_caps(DispClientContext* disp, UINT32 maxNumMonitors,
                             UINT32 maxMonitorAreaFactorA, UINT32 maxMonitorAreaFactorB)
{
	WINPR_UNUSED(maxNumMonitors);
	WINPR_UNUSED(maxMonitorAreaFactorA);
	WINPR_UNUSED(maxMonitorAreaFactorB);
	rdpb_session* s = (rdpb_session*)disp->custom;
	if (s)
		InterlockedExchange(&s->disp_ready, 1);
	return CHANNEL_RC_OK;
}

static void bridge_on_channel_connected(void* context, const ChannelConnectedEventArgs* e)
{
	rdpb_session* s = get_session((rdpContext*)context);
	if (!s || !e)
		return;

	if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
	{
		clip_init(s, (CliprdrClientContext*)e->pInterface);
	}
	else if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
	{
		DispClientContext* disp = (DispClientContext*)e->pInterface;
		disp->custom = s;
		disp->DisplayControlCaps = bridge_disp_caps;
		AcquireSRWLockExclusive(&s->lock);
		s->disp = disp;
		ReleaseSRWLockExclusive(&s->lock);
	}
	else
	{
		if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0)
			InterlockedExchange(&s->gfx_active, 1);
		freerdp_client_OnChannelConnectedEventHandler(context, e);
	}
}

static void bridge_on_channel_disconnected(void* context, const ChannelDisconnectedEventArgs* e)
{
	rdpb_session* s = get_session((rdpContext*)context);
	if (!s || !e)
		return;

	if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
	{
		clip_uninit(s, (CliprdrClientContext*)e->pInterface);
	}
	else if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
	{
		AcquireSRWLockExclusive(&s->lock);
		s->disp = nullptr;
		InterlockedExchange(&s->disp_ready, 0);
		ReleaseSRWLockExclusive(&s->lock);
	}
	else
	{
		if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0)
			InterlockedExchange(&s->gfx_active, 0);
		freerdp_client_OnChannelDisconnectedEventHandler(context, e);
	}
}

/* ------------------------------------------------------------------------ */
/* Connection callbacks                                                     */
/* ------------------------------------------------------------------------ */

static void apply_keyboard_layout(rdpSettings* settings)
{
	/* Same detection as wf_pre_connect */
	DWORD layout = freerdp_settings_get_uint32(settings, FreeRDP_KeyboardLayout);
	if (layout == 0)
	{
		CHAR name[KL_NAMELENGTH + 1] = { 0 };
		if (GetKeyboardLayoutNameA(name))
			layout = strtoul(name, nullptr, 16);
		if (layout == 0)
			layout = (DWORD)(((uintptr_t)GetKeyboardLayout(0) >> 16) & 0xFFFF);
		if (layout == 0)
			layout = KBD_US;
		(void)freerdp_settings_set_uint32(settings, FreeRDP_KeyboardLayout, layout);
	}

	/* Japanese / Korean keyboards need the matching keyboard type, otherwise keys like
	 * Hankaku/Zenkaku or the yen key are mapped incorrectly on the server. */
	if (freerdp_settings_get_uint32(settings, FreeRDP_KeyboardType) == 4)
	{
		const DWORD lang = layout & 0xFFFF;
		if (lang == 0x0411)
		{
			(void)freerdp_settings_set_uint32(settings, FreeRDP_KeyboardType, 7);
			(void)freerdp_settings_set_uint32(settings, FreeRDP_KeyboardSubType, 2);
			(void)freerdp_settings_set_uint32(settings, FreeRDP_KeyboardFunctionKey, 12);
		}
		else if (lang == 0x0412)
		{
			(void)freerdp_settings_set_uint32(settings, FreeRDP_KeyboardType, 8);
			(void)freerdp_settings_set_uint32(settings, FreeRDP_KeyboardFunctionKey, 12);
		}
	}
}

/* freerdp_connect() forces keyboard type 7 / sub type 2 (Japanese 106/109) for Japanese layouts
 * after PreConnect, which breaks English (101/102) keyboards with a Japanese layout (type 7 /
 * sub type 0). The client's choice is restored right before the client core data is sent. */
static void bridge_on_connection_state_change(void* context, const ConnectionStateChangeEventArgs* e)
{
	rdpContext* ctx = (rdpContext*)context;
	rdpb_session* s = get_session(ctx);
	if (!s || !e || !ctx->settings)
		return;

	WLog_DBG(TAG, "connection state changed: %d", e->state);
	if (e->state != CONNECTION_STATE_MCS_CREATE_REQUEST)
		return;

	rdpSettings* settings = ctx->settings;
	if (freerdp_settings_get_uint32(settings, FreeRDP_KeyboardSubType) != s->kbd_subtype ||
	    freerdp_settings_get_uint32(settings, FreeRDP_KeyboardType) != s->kbd_type)
	{
		WLog_INFO(TAG, "Keyboard type restored to %" PRIu32 "/%" PRIu32 " (fn %" PRIu32 ")",
		          s->kbd_type, s->kbd_subtype, s->kbd_fnkeys);
	}
	(void)freerdp_settings_set_uint32(settings, FreeRDP_KeyboardType, s->kbd_type);
	(void)freerdp_settings_set_uint32(settings, FreeRDP_KeyboardSubType, s->kbd_subtype);
	(void)freerdp_settings_set_uint32(settings, FreeRDP_KeyboardFunctionKey, s->kbd_fnkeys);
}

static BOOL bridge_pre_connect(freerdp* instance)
{
	rdpContext* context = instance->context;
	rdpSettings* settings = context->settings;

	if (!freerdp_settings_set_uint32(settings, FreeRDP_OsMajorType, OSMAJORTYPE_WINDOWS))
		return FALSE;
	if (!freerdp_settings_set_uint32(settings, FreeRDP_OsMinorType, OSMINORTYPE_WINDOWS_NT))
		return FALSE;

	/* XP servers require a width divisible by 4 (see wf_pre_connect). Round down instead of up
	 * so the desktop never becomes wider than the area it is shown in (scaling blurs text). */
	const UINT32 width = freerdp_settings_get_uint32(settings, FreeRDP_DesktopWidth);
	if (!freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, MAX(width & ~3u, 200)))
		return FALSE;

	apply_keyboard_layout(settings);

	/* restored in bridge_on_connection_state_change */
	rdpb_session* s = get_session(context);
	s->kbd_type = freerdp_settings_get_uint32(settings, FreeRDP_KeyboardType);
	s->kbd_subtype = freerdp_settings_get_uint32(settings, FreeRDP_KeyboardSubType);
	s->kbd_fnkeys = freerdp_settings_get_uint32(settings, FreeRDP_KeyboardFunctionKey);

	if (PubSub_SubscribeChannelConnected(context->pubSub, bridge_on_channel_connected) < 0)
		return FALSE;
	if (PubSub_SubscribeChannelDisconnected(context->pubSub, bridge_on_channel_disconnected) < 0)
		return FALSE;
	return TRUE;
}

static BOOL bridge_post_connect(freerdp* instance)
{
	rdpContext* context = instance->context;
	rdpb_session* s = get_session(context);

	if (!gdi_init(instance, PIXEL_FORMAT_BGRA32))
		return FALSE;

	if (!bridge_register_pointer(context->graphics))
		return FALSE;

	rdpUpdate* update = context->update;
	update->BeginPaint = bridge_begin_paint;
	update->EndPaint = bridge_end_paint;
	update->DesktopResize = bridge_desktop_resize;
	update->SetKeyboardIndicators = bridge_keyboard_set_indicators;
	update->SetKeyboardImeStatus = bridge_keyboard_set_ime_status;

	AcquireSRWLockExclusive(&s->lock);
	s->active = TRUE;
	ReleaseSRWLockExclusive(&s->lock);

	if (s->cb.on_desktop_resize)
		s->cb.on_desktop_resize(s->cb.user, context->gdi->width, context->gdi->height);
	if (s->cb.on_state)
		s->cb.on_state(s->cb.user, RDPB_STATE_CONNECTED, 0, 0, nullptr);
	return TRUE;
}

static void bridge_post_disconnect(freerdp* instance)
{
	if (!instance || !instance->context)
		return;

	rdpb_session* s = get_session(instance->context);
	if (s)
	{
		AcquireSRWLockExclusive(&s->lock);
		s->active = FALSE;
		ReleaseSRWLockExclusive(&s->lock);
	}
	gdi_free(instance);
}

static void bridge_post_final_disconnect(freerdp* instance)
{
	if (!instance || !instance->context)
		return;

	PubSub_UnsubscribeChannelConnected(instance->context->pubSub, bridge_on_channel_connected);
	PubSub_UnsubscribeChannelDisconnected(instance->context->pubSub,
	                                      bridge_on_channel_disconnected);
}

static BOOL bridge_authenticate_ex(freerdp* instance, char** username, char** password,
                                   char** domain, rdp_auth_reason reason)
{
	rdpb_session* s = get_session(instance->context);
	if (!s || !s->cb.on_authenticate)
		return FALSE;

	EnterCriticalSection(&s->auth_lock);
	s->auth_set = FALSE;
	LeaveCriticalSection(&s->auth_lock);

	const int32_t ok = s->cb.on_authenticate(s->cb.user, (int32_t)reason,
	                                         username ? *username : nullptr,
	                                         domain ? *domain : nullptr);
	if (!ok)
	{
		freerdp_set_last_error_if_not(instance->context, FREERDP_ERROR_CONNECT_CANCELLED);
		return FALSE;
	}

	EnterCriticalSection(&s->auth_lock);
	BOOL rc = s->auth_set;
	if (rc)
	{
		if (username)
		{
			free(*username);
			*username = dup_or_null(s->auth_user);
		}
		if (password)
		{
			free(*password);
			*password = dup_or_null(s->auth_password);
		}
		if (domain)
		{
			free(*domain);
			*domain = dup_or_null(s->auth_domain);
		}
	}
	LeaveCriticalSection(&s->auth_lock);
	return rc;
}

static DWORD bridge_verify_certificate_ex(freerdp* instance, const char* host, UINT16 port,
                                          const char* common_name, const char* subject,
                                          const char* issuer, const char* fingerprint, DWORD flags)
{
	rdpb_session* s = get_session(instance->context);
	if (!s || !s->cb.on_verify_certificate)
		return 0;
	return s->cb.on_verify_certificate(s->cb.user, host, port, common_name, subject, issuer,
	                                   fingerprint, nullptr, flags);
}

static DWORD bridge_verify_changed_certificate_ex(freerdp* instance, const char* host, UINT16 port,
                                                  const char* common_name, const char* subject,
                                                  const char* issuer, const char* new_fingerprint,
                                                  const char* old_subject, const char* old_issuer,
                                                  const char* old_fingerprint, DWORD flags)
{
	WINPR_UNUSED(old_subject);
	WINPR_UNUSED(old_issuer);
	rdpb_session* s = get_session(instance->context);
	if (!s || !s->cb.on_verify_certificate)
		return 0;
	return s->cb.on_verify_certificate(s->cb.user, host, port, common_name, subject, issuer,
	                                   new_fingerprint,
	                                   old_fingerprint ? old_fingerprint : "", flags);
}

static BOOL bridge_present_gateway_message(freerdp* instance, UINT32 type, BOOL isDisplayMandatory,
                                           BOOL isConsentMandatory, size_t length,
                                           const WCHAR* message)
{
	rdpb_session* s = get_session(instance->context);
	if (!s)
		return FALSE;
	if (!s->cb.on_gateway_message)
		return !isConsentMandatory;
	return s->cb.on_gateway_message(s->cb.user, type, isDisplayMandatory, isConsentMandatory,
	                                (const uint16_t*)message, (uint32_t)(length / sizeof(WCHAR))) !=
	       0;
}

/* ------------------------------------------------------------------------ */
/* RDP thread                                                               */
/* ------------------------------------------------------------------------ */

static BOOL is_user_disconnect(UINT32 errorInfo)
{
	switch (errorInfo)
	{
		case ERRINFO_SUCCESS:
		case ERRINFO_RPC_INITIATED_DISCONNECT:
		case ERRINFO_RPC_INITIATED_LOGOFF:
		case ERRINFO_LOGOFF_BY_USER:
		case ERRINFO_RPC_INITIATED_DISCONNECT_BY_USER:
			return TRUE;
		default:
			return FALSE;
	}
}

static void report_disconnect(rdpb_session* s)
{
	rdpContext* context = s->context;
	freerdp* instance = context->instance;

	const UINT32 last = freerdp_get_last_error(context);
	const UINT32 info = freerdp_error_info(instance);

	char* message = nullptr;
	size_t len = 0;
	if (!is_user_disconnect(info))
	{
		winpr_asprintf(&message, &len, "%s [0x%08" PRIX32 "]", freerdp_get_error_info_string(info),
		               info);
	}
	else if ((last != FREERDP_ERROR_SUCCESS) && (last != FREERDP_ERROR_CONNECT_CANCELLED))
	{
		winpr_asprintf(&message, &len, "%s [0x%08" PRIX32 "]\n%s",
		               freerdp_get_last_error_name(last), last,
		               freerdp_get_last_error_string(last));
	}

	if (s->cb.on_state)
		s->cb.on_state(s->cb.user, RDPB_STATE_DISCONNECTED, last, info, message);
	free(message);
}

static DWORD WINAPI bridge_thread(LPVOID arg)
{
	rdpb_session* s = (rdpb_session*)arg;
	rdpContext* context = s->context;
	freerdp* instance = context->instance;

	if (s->cb.on_state)
		s->cb.on_state(s->cb.user, RDPB_STATE_CONNECTING, 0, 0, nullptr);

	if (!freerdp_connect(instance))
	{
		report_disconnect(s);
		return 1;
	}

	while (!freerdp_shall_disconnect_context(context))
	{
		/* win8 and server 2k12 need the synchronize event twice (see sdl_client_thread_run) */
		if (freerdp_focus_required(instance))
		{
			rdpb_focus_in(s);
			rdpb_focus_in(s);
		}

		HANDLE handles[MAXIMUM_WAIT_OBJECTS] = { 0 };
		const DWORD count = freerdp_get_event_handles(context, handles, ARRAYSIZE(handles));
		if (count == 0)
		{
			WLog_ERR(TAG, "freerdp_get_event_handles failed");
			break;
		}

		const DWORD status = WaitForMultipleObjects(count, handles, FALSE, INFINITE);
		if (status == WAIT_FAILED)
		{
			WLog_ERR(TAG, "WaitForMultipleObjects failed: %" PRIu32, GetLastError());
			break;
		}

		if (!freerdp_check_event_handles(context))
		{
			if (freerdp_shall_disconnect_context(context))
				break;

			if (s->cb.on_state)
				s->cb.on_state(s->cb.user, RDPB_STATE_RECONNECTING, 0, 0, nullptr);

			if (client_auto_reconnect(instance))
			{
				if (s->cb.on_state)
					s->cb.on_state(s->cb.user, RDPB_STATE_CONNECTED, 0, 0, nullptr);
				continue;
			}

			WLog_ERR(TAG, "Failed to check FreeRDP event handles");
			break;
		}
	}

	freerdp_disconnect(instance);
	report_disconnect(s);
	return 0;
}

/* ------------------------------------------------------------------------ */
/* Client entry points                                                      */
/* ------------------------------------------------------------------------ */

static BOOL bridge_global_init(void)
{
	WSADATA wsaData = { 0 };
	WSAStartup(MAKEWORD(2, 2), &wsaData);
	return freerdp_register_addin_provider(freerdp_channels_load_static_addin_entry, 0) ==
	       CHANNEL_RC_OK;
}

static void bridge_global_uninit(void)
{
	WSACleanup();
}

static BOOL bridge_client_new(freerdp* instance, rdpContext* context)
{
	/* libfreerdp raises ConnectionStateChange but does not register the event type
	 * (FreeRDP_Events in libfreerdp/core/freerdp.c), so subscribing would fail. */
	if (!PubSub_FindEventType(context->pubSub, "ConnectionStateChange"))
	{
		wEventType events[] = { DEFINE_EVENT_ENTRY(ConnectionStateChange) };
		PubSub_AddEventTypes(context->pubSub, events, ARRAYSIZE(events));
	}
	if (PubSub_SubscribeConnectionStateChange(context->pubSub, bridge_on_connection_state_change) <
	    0)
		return FALSE;

	instance->PreConnect = bridge_pre_connect;
	instance->PostConnect = bridge_post_connect;
	instance->PostDisconnect = bridge_post_disconnect;
	instance->PostFinalDisconnect = bridge_post_final_disconnect;
	instance->AuthenticateEx = bridge_authenticate_ex;
	instance->VerifyCertificateEx = bridge_verify_certificate_ex;
	instance->VerifyChangedCertificateEx = bridge_verify_changed_certificate_ex;
	instance->PresentGatewayMessage = bridge_present_gateway_message;
	instance->GetAccessToken = client_failsafe_get_access_token;
	return TRUE;
}

static void bridge_client_free(freerdp* instance, rdpContext* context)
{
	WINPR_UNUSED(instance);
	WINPR_UNUSED(context);
}

static int bridge_client_start(rdpContext* context)
{
	WINPR_UNUSED(context);
	return 0;
}

static int bridge_client_stop(rdpContext* context)
{
	WINPR_UNUSED(context);
	return 0;
}

/* ------------------------------------------------------------------------ */
/* Public API                                                               */
/* ------------------------------------------------------------------------ */

const char* rdpb_version(void)
{
	return freerdp_get_version_string();
}

rdpb_session* rdpb_new(const rdpb_callbacks* callbacks)
{
	if (!callbacks)
		return nullptr;

	rdpb_session* s = calloc(1, sizeof(rdpb_session));
	if (!s)
		return nullptr;

	s->cb = *callbacks;
	InitializeSRWLock(&s->lock);
	InitializeCriticalSection(&s->auth_lock);
	InitializeCriticalSection(&s->clip_lock);
	InitializeCriticalSection(&s->cursor_lock);

	RDP_CLIENT_ENTRY_POINTS entry = { 0 };
	entry.Version = 1;
	entry.Size = sizeof(RDP_CLIENT_ENTRY_POINTS_V1);
	entry.GlobalInit = bridge_global_init;
	entry.GlobalUninit = bridge_global_uninit;
	entry.ContextSize = sizeof(bridgeContext);
	entry.ClientNew = bridge_client_new;
	entry.ClientFree = bridge_client_free;
	entry.ClientStart = bridge_client_start;
	entry.ClientStop = bridge_client_stop;

	s->context = freerdp_client_context_new(&entry);
	if (!s->context)
	{
		rdpb_free(s);
		return nullptr;
	}

	((bridgeContext*)s->context)->session = s;
	return s;
}

void rdpb_free(rdpb_session* s)
{
	if (!s)
		return;

	if (s->thread)
	{
		freerdp_abort_connect_context(s->context);
		WaitForSingleObject(s->thread, INFINITE);
		CloseHandle(s->thread);
		s->thread = nullptr;
	}

	if (s->context)
		freerdp_client_context_free(s->context);

	for (size_t i = 0; i < s->dead_count; i++)
		DestroyCursor(s->dead_cursors[i]);

	free(s->auth_user);
	if (s->auth_password)
		memset(s->auth_password, 0, strlen(s->auth_password));
	free(s->auth_password);
	free(s->auth_domain);
	free(s->clip_text);

	DeleteCriticalSection(&s->auth_lock);
	DeleteCriticalSection(&s->clip_lock);
	DeleteCriticalSection(&s->cursor_lock);
	free(s);
}

void* rdpb_get_settings(rdpb_session* s)
{
	if (!s || !s->context)
		return nullptr;
	return s->context->settings;
}

int32_t rdpb_parse_arguments(rdpb_session* s, int32_t argc, const char** argv)
{
	if (!s || !s->context || (argc < 1) || !argv)
		return -1;

	return freerdp_client_settings_parse_command_line(s->context->settings, argc, (char**)argv,
	                                                  FALSE);
}

int32_t rdpb_load_rdp_file(rdpb_session* s, const char* path)
{
	if (!s || !s->context || !path)
		return -1;
	return freerdp_client_settings_parse_connection_file(s->context->settings, path);
}

int32_t rdpb_set_setting(rdpb_session* s, const char* name, const char* value)
{
	if (!s || !s->context || !name || !value)
		return 0;
	return freerdp_settings_set_value_for_name(s->context->settings, name, value) ? 1 : 0;
}

void rdpb_set_credentials(rdpb_session* s, const char* username, const char* password,
                          const char* domain)
{
	if (!s)
		return;

	EnterCriticalSection(&s->auth_lock);
	free(s->auth_user);
	if (s->auth_password)
		memset(s->auth_password, 0, strlen(s->auth_password));
	free(s->auth_password);
	free(s->auth_domain);
	s->auth_user = dup_or_null(username);
	s->auth_password = dup_or_null(password);
	s->auth_domain = dup_or_null((domain && domain[0]) ? domain : nullptr);
	s->auth_set = TRUE;
	LeaveCriticalSection(&s->auth_lock);
}

int32_t rdpb_connect(rdpb_session* s)
{
	if (!s || !s->context || s->thread)
		return 0;

	s->thread = CreateThread(nullptr, 0, bridge_thread, s, 0, nullptr);
	return s->thread ? 1 : 0;
}

void rdpb_disconnect(rdpb_session* s)
{
	if (!s || !s->context)
		return;
	freerdp_abort_connect_context(s->context);
}

/* Runs fn with the shared lock held if the session is connected. */
#define WITH_ACTIVE_INPUT(s, expr)                  \
	do                                              \
	{                                               \
		BOOL rc_ = FALSE;                           \
		if (!(s))                                   \
			return 0;                               \
		AcquireSRWLockShared(&(s)->lock);           \
		if ((s)->active && (s)->context->input)     \
		{                                           \
			rdpInput* input = (s)->context->input;  \
			rc_ = (expr);                           \
		}                                           \
		ReleaseSRWLockShared(&(s)->lock);           \
		return rc_ ? 1 : 0;                         \
	} while (0)

static BOOL send_scancode(rdpInput* input, BOOL down, BOOL repeat, UINT32 scancode)
{
	return freerdp_input_send_keyboard_event_ex(input, down, repeat, scancode);
}

static BOOL send_key_locked(rdpb_session* s, rdpInput* input, BOOL down, UINT32 scancode,
                            BOOL extended)
{
	/* Mirrors wf_ll_kbd_proc */
	UINT32 rdp_scancode = MAKE_RDP_SCANCODE((BYTE)scancode, extended);
	const size_t index = (scancode & 0xFF) | (extended ? 0x100 : 0);
	const BOOL repeat = down && s->keystates[index];
	s->keystates[index] = down;

	if (rdp_scancode == RDP_SCANCODE_NUMLOCK_EXTENDED)
	{
		/* Windows sends NumLock as extended - rdp doesn't */
		rdp_scancode = RDP_SCANCODE_NUMLOCK;
	}
	else if (rdp_scancode == RDP_SCANCODE_NUMLOCK)
	{
		/* Windows sends Pause as if it was a RDP NumLock. It must be sent as a one-shot
		 * Ctrl+NumLock */
		s->keystates[index] = FALSE;
		if (!down)
			return TRUE;
		return send_scancode(input, TRUE, FALSE, RDP_SCANCODE_LCONTROL) &&
		       send_scancode(input, TRUE, FALSE, RDP_SCANCODE_NUMLOCK) &&
		       send_scancode(input, FALSE, FALSE, RDP_SCANCODE_LCONTROL) &&
		       send_scancode(input, FALSE, FALSE, RDP_SCANCODE_NUMLOCK);
	}
	else if (rdp_scancode == RDP_SCANCODE_RSHIFT_EXTENDED)
	{
		/* right shift (x36) should not be extended */
		rdp_scancode = RDP_SCANCODE_RSHIFT;
	}

	return send_scancode(input, down, repeat, rdp_scancode);
}

int32_t rdpb_send_key(rdpb_session* s, int32_t down, uint32_t scancode, int32_t extended)
{
	WITH_ACTIVE_INPUT(s, send_key_locked(s, input, down != 0, scancode, extended != 0));
}

int32_t rdpb_send_unicode(rdpb_session* s, int32_t down, uint16_t code)
{
	WITH_ACTIVE_INPUT(
	    s, freerdp_input_send_unicode_keyboard_event(input, down ? 0 : KBD_FLAGS_RELEASE, code));
}

static UINT16 clamp_coord(int32_t v)
{
	if (v < 0)
		return 0;
	if (v > UINT16_MAX)
		return UINT16_MAX;
	return (UINT16)v;
}

int32_t rdpb_send_mouse(rdpb_session* s, uint16_t flags, int32_t x, int32_t y)
{
	WITH_ACTIVE_INPUT(s, freerdp_input_send_mouse_event(input, flags, clamp_coord(x),
	                                                    clamp_coord(y)));
}

int32_t rdpb_send_extended_mouse(rdpb_session* s, uint16_t flags, int32_t x, int32_t y)
{
	WITH_ACTIVE_INPUT(s, freerdp_input_send_extended_mouse_event(input, flags, clamp_coord(x),
	                                                             clamp_coord(y)));
}

static BOOL send_ctrl_alt_del(rdpInput* input)
{
	return send_scancode(input, TRUE, FALSE, RDP_SCANCODE_LCONTROL) &&
	       send_scancode(input, TRUE, FALSE, RDP_SCANCODE_LMENU) &&
	       send_scancode(input, TRUE, FALSE, RDP_SCANCODE_DELETE) &&
	       send_scancode(input, FALSE, FALSE, RDP_SCANCODE_DELETE) &&
	       send_scancode(input, FALSE, FALSE, RDP_SCANCODE_LMENU) &&
	       send_scancode(input, FALSE, FALSE, RDP_SCANCODE_LCONTROL);
}

int32_t rdpb_send_ctrl_alt_del(rdpb_session* s)
{
	WITH_ACTIVE_INPUT(s, send_ctrl_alt_del(input));
}

static UINT16 local_toggle_states(void)
{
	UINT16 flags = 0;
	if (GetKeyState(VK_NUMLOCK) & 1)
		flags |= KBD_SYNC_NUM_LOCK;
	if (GetKeyState(VK_CAPITAL) & 1)
		flags |= KBD_SYNC_CAPS_LOCK;
	if (GetKeyState(VK_SCROLL) & 1)
		flags |= KBD_SYNC_SCROLL_LOCK;
	if (GetKeyState(VK_KANA) & 1)
		flags |= KBD_SYNC_KANA_LOCK;
	return flags;
}

int32_t rdpb_focus_in(rdpb_session* s)
{
	WITH_ACTIVE_INPUT(s, freerdp_input_send_focus_in_event(input, local_toggle_states()));
}

static BOOL release_all_locked(rdpb_session* s, rdpInput* input)
{
	BOOL rc = TRUE;
	for (size_t i = 0; i < ARRAYSIZE(s->keystates); i++)
	{
		if (!s->keystates[i])
			continue;
		s->keystates[i] = FALSE;
		const UINT32 code = MAKE_RDP_SCANCODE((BYTE)(i & 0xFF), (i & 0x100) != 0);
		if (code == RDP_SCANCODE_NUMLOCK)
			continue;
		rc &= send_scancode(input, FALSE, FALSE,
		                    code == RDP_SCANCODE_NUMLOCK_EXTENDED ? RDP_SCANCODE_NUMLOCK
		                    : code == RDP_SCANCODE_RSHIFT_EXTENDED ? RDP_SCANCODE_RSHIFT
		                                                           : code);
	}
	return rc;
}

int32_t rdpb_release_all_keys(rdpb_session* s)
{
	WITH_ACTIVE_INPUT(s, release_all_locked(s, input));
}

static BOOL send_layout(rdpb_session* s, uint32_t width, uint32_t height, uint32_t desktop_scale,
                        uint32_t device_scale)
{
	DispClientContext* disp = s->disp;
	if (!disp || !disp->SendMonitorLayout || !InterlockedCompareExchange(&s->disp_ready, 1, 1))
		return FALSE;

	width = MAX(DISPLAY_CONTROL_MIN_MONITOR_WIDTH, MIN(DISPLAY_CONTROL_MAX_MONITOR_WIDTH, width));
	height =
	    MAX(DISPLAY_CONTROL_MIN_MONITOR_HEIGHT, MIN(DISPLAY_CONTROL_MAX_MONITOR_HEIGHT, height));
	width &= ~1u; /* width must be even */

	DISPLAY_CONTROL_MONITOR_LAYOUT layout = { 0 };
	layout.Flags = DISPLAY_CONTROL_MONITOR_PRIMARY;
	layout.Left = 0;
	layout.Top = 0;
	layout.Width = width;
	layout.Height = height;
	layout.Orientation = ORIENTATION_LANDSCAPE;
	layout.DesktopScaleFactor = desktop_scale;
	layout.DeviceScaleFactor = device_scale;

	return disp->SendMonitorLayout(disp, 1, &layout) == CHANNEL_RC_OK;
}

int32_t rdpb_resize(rdpb_session* s, uint32_t width, uint32_t height, uint32_t desktop_scale,
                    uint32_t device_scale)
{
	WITH_ACTIVE_INPUT(s, (WINPR_UNUSED(input), send_layout(s, width, height, desktop_scale,
	                                                       device_scale)));
}

int32_t rdpb_can_resize(rdpb_session* s)
{
	if (!s)
		return 0;
	AcquireSRWLockShared(&s->lock);
	const BOOL rc = s->active && s->disp && InterlockedCompareExchange(&s->disp_ready, 1, 1);
	ReleaseSRWLockShared(&s->lock);
	return rc ? 1 : 0;
}

static BOOL is_secret_name(const char* name)
{
	static const char* secrets[] = { "Password", "Token", "Secret", "Pin", "Cookie",
		                             "PrivateKey", "Credentials" };
	for (size_t i = 0; i < ARRAYSIZE(secrets); i++)
	{
		if (strstr(name, secrets[i]))
			return TRUE;
	}
	return FALSE;
}

static SSIZE_T lookup_key(rdpb_session* s, const char* name)
{
	if (!s || !s->context || !s->context->settings || !name || is_secret_name(name))
		return -1;
	return freerdp_settings_get_key_for_name(name);
}

int32_t rdpb_get_bool(rdpb_session* s, const char* name, int32_t* value)
{
	const SSIZE_T key = lookup_key(s, name);
	if ((key < 0) || !value)
		return 0;
	*value = freerdp_settings_get_bool(s->context->settings, (FreeRDP_Settings_Keys_Bool)key) ? 1
	                                                                                            : 0;
	return 1;
}

int32_t rdpb_get_uint32(rdpb_session* s, const char* name, uint32_t* value)
{
	const SSIZE_T key = lookup_key(s, name);
	if ((key < 0) || !value)
		return 0;
	*value = freerdp_settings_get_uint32(s->context->settings, (FreeRDP_Settings_Keys_UInt32)key);
	return 1;
}

uint32_t rdpb_get_string(rdpb_session* s, const char* name, char* buffer, uint32_t size)
{
	const SSIZE_T key = lookup_key(s, name);
	if (key < 0)
		return 0;
	const char* str =
	    freerdp_settings_get_string(s->context->settings, (FreeRDP_Settings_Keys_String)key);
	if (!str)
		return 0;

	const size_t len = strlen(str) + 1;
	if (buffer && (size >= len))
		memcpy(buffer, str, len);
	return (uint32_t)len;
}

uint32_t rdpb_get_channel_status(rdpb_session* s)
{
	if (!s)
		return 0;

	uint32_t flags = 0;
	if (InterlockedCompareExchange(&s->gfx_active, 1, 1))
		flags |= RDPB_CHANNEL_GFX;
	if (rdpb_can_resize(s))
		flags |= RDPB_CHANNEL_DISP;

	EnterCriticalSection(&s->clip_lock);
	if (s->cliprdr && s->clip_ready)
		flags |= RDPB_CHANNEL_CLIPBOARD;
	LeaveCriticalSection(&s->clip_lock);
	return flags;
}

int32_t rdpb_clipboard_set_text(rdpb_session* s, const uint16_t* text, uint32_t length)
{
	if (!s)
		return 0;

	EnterCriticalSection(&s->clip_lock);
	free(s->clip_text);
	s->clip_text = nullptr;
	s->clip_length = 0;

	if (text)
	{
		s->clip_text = calloc((size_t)length + 1, sizeof(WCHAR));
		if (s->clip_text)
		{
			memcpy(s->clip_text, text, (size_t)length * sizeof(WCHAR));
			s->clip_length = length;
		}
	}

	const UINT rc = clip_send_format_list(s);
	LeaveCriticalSection(&s->clip_lock);
	return rc == CHANNEL_RC_OK ? 1 : 0;
}
