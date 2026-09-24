/**
 * FreeRdpBridge - thin C API on top of FreeRDP 3 for use from .NET (P/Invoke).
 *
 * The bridge owns an rdpClientContext, runs the RDP event loop on its own thread
 * and reports everything back through plain C callbacks. It follows the structure
 * of the FreeRDP SDL client (RDP thread + separate UI thread) and reuses the
 * keyboard handling of the Windows client (wfreerdp).
 *
 * All strings are UTF-8 unless the parameter is typed as uint16_t* (UTF-16LE).
 * Callbacks are invoked on FreeRDP threads, never on the caller's UI thread.
 */
#ifndef RDP_BRIDGE_H
#define RDP_BRIDGE_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C"
{
#endif

#if defined(RDPB_EXPORTS)
#define RDPB_API __declspec(dllexport)
#else
#define RDPB_API __declspec(dllimport)
#endif

	typedef struct rdpb_session rdpb_session;

	enum rdpb_state
	{
		RDPB_STATE_CONNECTING = 0,
		RDPB_STATE_CONNECTED = 1,
		RDPB_STATE_DISCONNECTED = 2,
		RDPB_STATE_RECONNECTING = 3
	};

	enum rdpb_pointer_action
	{
		RDPB_POINTER_SET = 0,     /* hcursor is valid */
		RDPB_POINTER_HIDE = 1,    /* hide the local cursor */
		RDPB_POINTER_DEFAULT = 2  /* use the default arrow */
	};

	typedef struct
	{
		void* user;

		/* Connection state changes. message is only set for RDPB_STATE_DISCONNECTED. */
		void(__cdecl* on_state)(void* user, int32_t state, uint32_t last_error, uint32_t error_info,
		                        const char* message);

		/* The remote desktop size changed (also called once after connecting). */
		void(__cdecl* on_desktop_resize)(void* user, uint32_t width, uint32_t height);

		/* A region of the frame buffer was updated. The buffer (BGRA32, top-down) is only valid
		 * during the call. */
		void(__cdecl* on_frame)(void* user, const uint8_t* buffer, uint32_t stride, uint32_t width,
		                        uint32_t height, int32_t x, int32_t y, int32_t w, int32_t h);

		/* Credentials are required. Call rdpb_set_credentials() before returning non-zero.
		 * Return 0 to cancel the connection. */
		int32_t(__cdecl* on_authenticate)(void* user, int32_t reason, const char* username,
		                                  const char* domain);

		/* Certificate verification. Return 0 = reject, 1 = accept and store, 2 = accept for this
		 * session only. old_fingerprint is non-NULL if the certificate changed. */
		uint32_t(__cdecl* on_verify_certificate)(void* user, const char* host, uint16_t port,
		                                         const char* common_name, const char* subject,
		                                         const char* issuer, const char* fingerprint,
		                                         const char* old_fingerprint, uint32_t flags);

		/* Remote pointer changes (see enum rdpb_pointer_action). hcursor stays valid for a
		 * while after it was replaced, the bridge destroys it lazily. */
		void(__cdecl* on_pointer)(void* user, int32_t action, void* hcursor);

		/* Text copied on the remote side (UTF-16LE, not terminated). */
		void(__cdecl* on_clipboard_text)(void* user, const uint16_t* text, uint32_t length);

		/* Gateway message. Return non-zero to accept / continue. */
		int32_t(__cdecl* on_gateway_message)(void* user, uint32_t type, int32_t display_mandatory,
		                                     int32_t consent_mandatory, const uint16_t* message,
		                                     uint32_t length);
	} rdpb_callbacks;

	RDPB_API const char* rdpb_version(void);

	RDPB_API rdpb_session* rdpb_new(const rdpb_callbacks* callbacks);
	/* Aborts a running connection, waits for the RDP thread and frees everything.
	 * Must not be called from inside a callback. */
	RDPB_API void rdpb_free(rdpb_session* session);

	/* Configuration (before rdpb_connect) */
	RDPB_API void* rdpb_get_settings(rdpb_session* session);
	/* Parses FreeRDP command line arguments (argv[0] is the program name). Returns 0 on success. */
	RDPB_API int32_t rdpb_parse_arguments(rdpb_session* session, int32_t argc, const char** argv);
	/* Loads a .rdp connection file. Returns 0 on success. */
	RDPB_API int32_t rdpb_load_rdp_file(rdpb_session* session, const char* path);
	/* Sets a setting by name, e.g. ("FreeRDP_GfxH264", "true"). Returns non-zero on success. */
	RDPB_API int32_t rdpb_set_setting(rdpb_session* session, const char* name, const char* value);
	RDPB_API void rdpb_set_credentials(rdpb_session* session, const char* username,
	                                   const char* password, const char* domain);

	/* Starts the RDP thread. Returns non-zero on success. */
	RDPB_API int32_t rdpb_connect(rdpb_session* session);
	/* Requests a disconnect, does not wait. */
	RDPB_API void rdpb_disconnect(rdpb_session* session);

	/* Input */
	/* Low level key event as delivered by WH_KEYBOARD_LL (scan code + LLKHF_EXTENDED). */
	RDPB_API int32_t rdpb_send_key(rdpb_session* session, int32_t down, uint32_t scancode,
	                               int32_t extended);
	RDPB_API int32_t rdpb_send_unicode(rdpb_session* session, int32_t down, uint16_t code);
	RDPB_API int32_t rdpb_send_mouse(rdpb_session* session, uint16_t flags, int32_t x, int32_t y);
	RDPB_API int32_t rdpb_send_extended_mouse(rdpb_session* session, uint16_t flags, int32_t x,
	                                          int32_t y);
	RDPB_API int32_t rdpb_send_ctrl_alt_del(rdpb_session* session);
	/* Synchronizes lock keys and releases all keys still pressed. */
	RDPB_API int32_t rdpb_focus_in(rdpb_session* session);
	RDPB_API int32_t rdpb_release_all_keys(rdpb_session* session);

	/* Dynamic resolution (display control channel). Returns non-zero if a layout was sent. */
	RDPB_API int32_t rdpb_resize(rdpb_session* session, uint32_t width, uint32_t height,
	                             uint32_t desktop_scale, uint32_t device_scale);
	RDPB_API int32_t rdpb_can_resize(rdpb_session* session);

	/* Settings / status (for display). Names are FreeRDP key names, e.g. "FreeRDP_DesktopWidth".
	 * Secrets (passwords, tokens, PINs) cannot be read. Return non-zero on success. */
	RDPB_API int32_t rdpb_get_bool(rdpb_session* session, const char* name, int32_t* value);
	RDPB_API int32_t rdpb_get_uint32(rdpb_session* session, const char* name, uint32_t* value);
	/* Copies the string (UTF-8, terminated) into buffer. Returns the required size including the
	 * terminator (0 = not set / not allowed). */
	RDPB_API uint32_t rdpb_get_string(rdpb_session* session, const char* name, char* buffer,
	                                  uint32_t size);

	enum rdpb_channel_flags
	{
		RDPB_CHANNEL_GFX = 0x01,       /* RDP 8 graphics pipeline active */
		RDPB_CHANNEL_DISP = 0x02,      /* display control (dynamic resolution) ready */
		RDPB_CHANNEL_CLIPBOARD = 0x04  /* clipboard channel ready */
	};
	RDPB_API uint32_t rdpb_get_channel_status(rdpb_session* session);

	/* Clipboard: local clipboard now contains text (NULL / 0 = no text). */
	RDPB_API int32_t rdpb_clipboard_set_text(rdpb_session* session, const uint16_t* text,
	                                         uint32_t length);

#ifdef __cplusplus
}
#endif

#endif /* RDP_BRIDGE_H */
