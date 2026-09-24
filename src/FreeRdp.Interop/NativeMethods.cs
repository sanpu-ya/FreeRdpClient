using System.Runtime.InteropServices;

namespace FreeRdp.Interop;

/// <summary>P/Invoke declarations for FreeRdpBridge.dll (see native/FreeRdpBridge/rdp_bridge.h).</summary>
internal static unsafe partial class NativeMethods
{
    private const string Bridge = "FreeRdpBridge.dll";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        public IntPtr User;
        public delegate* unmanaged[Cdecl]<IntPtr, int, uint, uint, byte*, void> OnState;
        public delegate* unmanaged[Cdecl]<IntPtr, uint, uint, void> OnDesktopResize;
        public delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, uint, uint, int, int, int, int, void> OnFrame;
        public delegate* unmanaged[Cdecl]<IntPtr, int, byte*, byte*, int> OnAuthenticate;
        public delegate* unmanaged[Cdecl]<IntPtr, byte*, ushort, byte*, byte*, byte*, byte*, byte*, uint, uint> OnVerifyCertificate;
        public delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void> OnPointer;
        public delegate* unmanaged[Cdecl]<IntPtr, char*, uint, void> OnClipboardText;
        public delegate* unmanaged[Cdecl]<IntPtr, uint, int, int, char*, uint, int> OnGatewayMessage;
    }

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial byte* rdpb_version();

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr rdpb_new(Callbacks* callbacks);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void rdpb_free(IntPtr session);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_parse_arguments(IntPtr session, int argc, byte** argv);

    [LibraryImport(Bridge, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_load_rdp_file(IntPtr session, string path);

    [LibraryImport(Bridge, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_set_setting(IntPtr session, string name, string value);

    [LibraryImport(Bridge, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void rdpb_set_credentials(IntPtr session, string? username, string? password, string? domain);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_connect(IntPtr session);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void rdpb_disconnect(IntPtr session);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_send_key(IntPtr session, int down, uint scancode, int extended);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_send_unicode(IntPtr session, int down, ushort code);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_send_mouse(IntPtr session, ushort flags, int x, int y);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_send_extended_mouse(IntPtr session, ushort flags, int x, int y);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_send_ctrl_alt_del(IntPtr session);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_focus_in(IntPtr session);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_release_all_keys(IntPtr session);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_resize(IntPtr session, uint width, uint height, uint desktopScale, uint deviceScale);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_can_resize(IntPtr session);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_clipboard_set_text(IntPtr session, char* text, uint length);

    [LibraryImport(Bridge, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_get_bool(IntPtr session, string name, out int value);

    [LibraryImport(Bridge, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int rdpb_get_uint32(IntPtr session, string name, out uint value);

    [LibraryImport(Bridge, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint rdpb_get_string(IntPtr session, string name, byte* buffer, uint size);

    [LibraryImport(Bridge)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint rdpb_get_channel_status(IntPtr session);

    internal static string? FromUtf8(byte* str) =>
        str == null ? null : Marshal.PtrToStringUTF8((IntPtr)str);
}
