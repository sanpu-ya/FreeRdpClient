using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using WinRT;

namespace FreeRdpClient.Interop;

/// <summary>
/// Creates WinUI <see cref="InputCursor"/> objects from Win32 HCURSORs using
/// IInputCursorStaticsInterop (Microsoft.UI.Input.InputCursor.Interop.h).
/// </summary>
internal static unsafe class CursorInterop
{
    private static readonly Guid IID_IInputCursorStaticsInterop = new("ac6f5065-90c4-46ce-beb7-05e138e54117");
    private static IntPtr _statics;
    private static InputCursor? _hidden;

    public static InputCursor? FromHCursor(IntPtr hcursor)
    {
        if (hcursor == IntPtr.Zero)
            return null;

        var statics = GetStatics();
        if (statics == IntPtr.Zero)
            return null;

        // vtable: IUnknown (3) + IInspectable (3) + CreateFromHCursor
        var vtbl = *(IntPtr**)statics;
        var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtbl[6];
        IntPtr result;
        var hr = create(statics, hcursor, &result);
        if (hr < 0 || result == IntPtr.Zero)
            return null;

        try
        {
            return InputCursor.FromAbi(result);
        }
        finally
        {
            Marshal.Release(result);
        }
    }

    /// <summary>A fully transparent cursor, used when the server hides the pointer.</summary>
    public static InputCursor? Hidden
    {
        get
        {
            if (_hidden != null)
                return _hidden;

            // 32x32 monochrome: AND mask all ones (keep screen), XOR mask all zeros (no change)
            var and = stackalloc byte[32 * 4];
            var xor = stackalloc byte[32 * 4];
            new Span<byte>(and, 32 * 4).Fill(0xFF);
            new Span<byte>(xor, 32 * 4).Clear();
            var handle = Win32.CreateCursor(Win32.GetModuleHandleW(null), 0, 0, 32, 32, and, xor);
            _hidden = FromHCursor(handle); // the handle is kept alive for the lifetime of the process
            return _hidden;
        }
    }

    private static IntPtr GetStatics()
    {
        if (_statics != IntPtr.Zero)
            return _statics;

        var factory = ActivationFactory.Get("Microsoft.UI.Input.InputCursor");
        var iid = IID_IInputCursorStaticsInterop;
        if (Marshal.QueryInterface(factory.ThisPtr, in iid, out var statics) >= 0)
            _statics = statics; // kept for the lifetime of the process
        return _statics;
    }
}
