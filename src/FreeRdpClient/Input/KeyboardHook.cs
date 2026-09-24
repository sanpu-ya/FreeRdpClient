using System.Runtime.InteropServices;
using FreeRdpClient.Interop;

namespace FreeRdpClient.Input;

/// <summary>
/// Low level keyboard hook (WH_KEYBOARD_LL), the same approach as wfreerdp: while a session
/// has keyboard focus every key - including Windows key combinations and Alt+Tab - is delivered
/// as a raw scan code and swallowed locally.
/// </summary>
internal static unsafe class KeyboardHook
{
    public interface ITarget
    {
        /// <summary>Handles a key. Return true to swallow it locally.</summary>
        bool OnKey(uint vk, uint scanCode, bool extended, bool down, bool injected);
    }

    private static IntPtr _hook;
    private static ITarget? _target;
    private static IntPtr _window;

    /// <summary>Installs the hook on the calling (UI) thread. The thread must pump messages.</summary>
    public static void Install(IntPtr mainWindow)
    {
        _window = mainWindow;
        if (_hook != IntPtr.Zero)
            return;
        _hook = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, &HookProc, Win32.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            System.Diagnostics.Debug.WriteLine($"SetWindowsHookEx failed: {Marshal.GetLastPInvokeError()}");
    }

    public static void Uninstall()
    {
        if (_hook != IntPtr.Zero)
            Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public static bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>The session that currently owns the keyboard, or null.</summary>
    public static ITarget? Target
    {
        get => _target;
        set => _target = value;
    }

    public static void Release(ITarget target)
    {
        if (ReferenceEquals(_target, target))
            _target = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var target = _target;
            if (nCode == Win32.HC_ACTION && target != null && Win32.GetForegroundWindow() == _window)
            {
                var info = (Win32.KBDLLHOOKSTRUCT*)lParam;
                var down = (info->flags & Win32.LLKHF_UP) == 0;
                var extended = (info->flags & Win32.LLKHF_EXTENDED) != 0;
                var injected = (info->flags & Win32.LLKHF_INJECTED) != 0;
                if (target.OnKey(info->vkCode, info->scanCode, extended, down, injected))
                    return 1;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Keyboard hook failed: {ex}");
        }

        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
