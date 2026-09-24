using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeRdp.Interop;

/// <summary>
/// One RDP connection backed by FreeRdpBridge.dll.
/// Events and handlers are invoked on FreeRDP threads; UI code must marshal them itself.
/// </summary>
public sealed unsafe class RdpSession : IDisposable
{
    private readonly Lock _handleLock = new();
    private readonly CancellationTokenSource _cts = new();
    private GCHandle _self;
    private IntPtr _handle;
    private int _disposed;

    public RdpSession()
    {
        _self = GCHandle.Alloc(this);

        var callbacks = new NativeMethods.Callbacks
        {
            User = GCHandle.ToIntPtr(_self),
            OnState = &OnStateCallback,
            OnDesktopResize = &OnDesktopResizeCallback,
            OnFrame = &OnFrameCallback,
            OnAuthenticate = &OnAuthenticateCallback,
            OnVerifyCertificate = &OnVerifyCertificateCallback,
            OnPointer = &OnPointerCallback,
            OnClipboardText = &OnClipboardTextCallback,
            OnGatewayMessage = &OnGatewayMessageCallback,
        };

        _handle = NativeMethods.rdpb_new(&callbacks);
        if (_handle == IntPtr.Zero)
        {
            _self.Free();
            throw new InvalidOperationException("Failed to create the FreeRDP context.");
        }
    }

    /// <summary>FreeRDP version string, e.g. "3.32.0".</summary>
    public static string Version => NativeMethods.FromUtf8(NativeMethods.rdpb_version()) ?? "";

    /// <summary>
    /// Sends WLog output to a file. Must be called before the first session is created.
    /// </summary>
    public static void ConfigureLogging(string directory, string fileName, string level = "INFO")
    {
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("WLOG_APPENDER", "FILE");
        Environment.SetEnvironmentVariable("WLOG_FILEAPPENDER_OUTPUT_FILE_PATH", directory);
        Environment.SetEnvironmentVariable("WLOG_FILEAPPENDER_OUTPUT_FILE_NAME", fileName);
        Environment.SetEnvironmentVariable("WLOG_LEVEL", level);
    }

    public FrameBuffer Frame { get; } = new();

    public RdpConnectionState State { get; private set; } = RdpConnectionState.Disconnected;

    public event EventHandler<RdpStateChangedEventArgs>? StateChanged;
    public event Action<RdpPointerAction, IntPtr>? PointerChanged;
    public event Action<string>? RemoteClipboardText;

    public Func<RdpAuthenticationRequest, CancellationToken, Task<RdpCredentials?>>? AuthenticationHandler { get; set; }
    public Func<RdpCertificateRequest, CancellationToken, Task<CertificateDecision>>? CertificateHandler { get; set; }
    public Func<RdpGatewayMessage, CancellationToken, Task<bool>>? GatewayMessageHandler { get; set; }

    // ------------------------------------------------------------------
    // Configuration

    /// <summary>Applies FreeRDP command line arguments (argv[0] is the program name).</summary>
    public void ParseArguments(IReadOnlyList<string> args)
    {
        var encoded = new byte[args.Count][];
        var handles = new GCHandle[args.Count];
        var argv = new IntPtr[args.Count];
        try
        {
            for (var i = 0; i < args.Count; i++)
            {
                encoded[i] = Encoding.UTF8.GetBytes(args[i] + '\0');
                handles[i] = GCHandle.Alloc(encoded[i], GCHandleType.Pinned);
                argv[i] = handles[i].AddrOfPinnedObject();
            }

            int status;
            fixed (IntPtr* p = argv)
                status = NativeMethods.rdpb_parse_arguments(Handle, args.Count, (byte**)p);

            if (status != 0)
                throw new ArgumentException($"FreeRDP rejected the connection arguments (status {status}).");
        }
        finally
        {
            foreach (var h in handles)
                if (h.IsAllocated)
                    h.Free();
            foreach (var e in encoded)
                if (e != null)
                    Array.Clear(e); // may contain the password
        }
    }

    public void Configure(RdpConnectionOptions options)
    {
        ParseArguments(options.BuildArguments());

        // Set directly instead of /p: so the password does not go through the command line parser
        if (!string.IsNullOrEmpty(options.Password) && !SetSetting("FreeRDP_Password", options.Password))
            throw new ArgumentException("Failed to set the password.");

        if (options.KeyboardLayout is { } layout && !SetSetting("FreeRDP_KeyboardLayout", layout.ToString()))
            throw new ArgumentException("Failed to set the keyboard layout.");

        if (options.KeyboardType is { } kbd &&
            !(SetSetting("FreeRDP_KeyboardType", kbd.Type.ToString()) &&
              SetSetting("FreeRDP_KeyboardSubType", kbd.SubType.ToString()) &&
              SetSetting("FreeRDP_KeyboardFunctionKey", kbd.FunctionKeys.ToString())))
            throw new ArgumentException("Failed to set the keyboard type.");
    }

    public void LoadRdpFile(string path)
    {
        var status = NativeMethods.rdpb_load_rdp_file(Handle, path);
        if (status != 0)
            throw new IOException($"Failed to load '{path}' (status {status}).");
    }

    /// <summary>Reads a boolean FreeRDP setting, e.g. "FreeRDP_SupportGraphicsPipeline".</summary>
    public bool? GetBool(string name) =>
        Read(h => NativeMethods.rdpb_get_bool(h, name, out var v) != 0 ? v != 0 : (bool?)null);

    /// <summary>Reads an unsigned integer FreeRDP setting, e.g. "FreeRDP_DesktopWidth".</summary>
    public uint? GetUInt32(string name) =>
        Read(h => NativeMethods.rdpb_get_uint32(h, name, out var v) != 0 ? v : (uint?)null);

    /// <summary>Reads a string FreeRDP setting. Secrets (passwords etc.) are never returned.</summary>
    public string? GetString(string name) => Read(h =>
    {
        var size = NativeMethods.rdpb_get_string(h, name, null, 0);
        if (size == 0)
            return null;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
            size = NativeMethods.rdpb_get_string(h, name, p, (uint)buffer.Length);
        return size == 0 || size > buffer.Length ? null : Encoding.UTF8.GetString(buffer, 0, (int)size - 1);
    });

    /// <summary>Channels that are currently active.</summary>
    public RdpChannelStatus ChannelStatus => Read(h => (RdpChannelStatus)NativeMethods.rdpb_get_channel_status(h));

    private T? Read<T>(Func<IntPtr, T?> read)
    {
        lock (_handleLock)
        {
            return _handle == IntPtr.Zero ? default : read(_handle);
        }
    }

    /// <summary>Sets a FreeRDP setting by name, e.g. ("FreeRDP_GfxH264", "true").</summary>
    public bool SetSetting(string name, string value) => NativeMethods.rdpb_set_setting(Handle, name, value) != 0;

    // ------------------------------------------------------------------
    // Connection

    public void Connect()
    {
        if (NativeMethods.rdpb_connect(Handle) == 0)
            throw new InvalidOperationException("The session was already started.");
    }

    public void Disconnect()
    {
        lock (_handleLock)
        {
            if (_handle != IntPtr.Zero)
                NativeMethods.rdpb_disconnect(_handle);
        }
    }

    // ------------------------------------------------------------------
    // Input (UI thread)

    public void SendKey(bool down, uint scanCode, bool extended) =>
        Invoke(h => NativeMethods.rdpb_send_key(h, down ? 1 : 0, scanCode, extended ? 1 : 0));

    public void SendUnicode(bool down, char c) => Invoke(h => NativeMethods.rdpb_send_unicode(h, down ? 1 : 0, c));

    public void SendMouse(PointerFlags flags, int x, int y) =>
        Invoke(h => NativeMethods.rdpb_send_mouse(h, (ushort)flags, x, y));

    public void SendExtendedMouse(ExtendedPointerFlags flags, int x, int y) =>
        Invoke(h => NativeMethods.rdpb_send_extended_mouse(h, (ushort)flags, x, y));

    public void SendCtrlAltDel() => Invoke(NativeMethods.rdpb_send_ctrl_alt_del);

    public void FocusIn() => Invoke(NativeMethods.rdpb_focus_in);

    public void ReleaseAllKeys() => Invoke(NativeMethods.rdpb_release_all_keys);

    public bool CanResize => Invoke(NativeMethods.rdpb_can_resize) != 0;

    /// <summary>Requests a new remote resolution (dynamic resolution).</summary>
    public bool Resize(int width, int height, int desktopScale = 100, int deviceScale = 100) =>
        Invoke(h => NativeMethods.rdpb_resize(h, (uint)width, (uint)height, (uint)desktopScale, (uint)deviceScale)) != 0;

    /// <summary>Announces local clipboard text to the server (null = no text).</summary>
    public void SetClipboardText(string? text)
    {
        lock (_handleLock)
        {
            if (_handle == IntPtr.Zero)
                return;
            fixed (char* p = text)
                NativeMethods.rdpb_clipboard_set_text(_handle, text == null ? null : p, (uint)(text?.Length ?? 0));
        }
    }

    private int Invoke(Func<IntPtr, int> action)
    {
        lock (_handleLock)
        {
            return _handle == IntPtr.Zero ? 0 : action(_handle);
        }
    }

    private IntPtr Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            return _handle;
        }
    }

    // ------------------------------------------------------------------
    // Lifetime

    /// <summary>Disconnects and waits for the RDP thread. Blocks; do not call on the UI thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Unblock handlers waiting for user input (authentication, certificates).
        _cts.Cancel();

        IntPtr handle;
        lock (_handleLock)
        {
            handle = _handle;
            _handle = IntPtr.Zero;
        }

        if (handle != IntPtr.Zero)
            NativeMethods.rdpb_free(handle);

        if (_self.IsAllocated)
            _self.Free();
        Frame.Dispose();
        _cts.Dispose();
    }

    public Task DisposeAsync() => Task.Run(Dispose);

    // ------------------------------------------------------------------
    // Native callbacks (FreeRDP threads)

    private static RdpSession? FromUser(IntPtr user)
    {
        if (user == IntPtr.Zero)
            return null;
        return GCHandle.FromIntPtr(user).Target as RdpSession;
    }

    private T WaitForHandler<T>(Task<T> task)
    {
        task.Wait(_cts.Token);
        return task.Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStateCallback(IntPtr user, int state, uint lastError, uint errorInfo, byte* message)
    {
        try
        {
            var session = FromUser(user);
            if (session == null)
                return;
            session.State = (RdpConnectionState)state;
            session.StateChanged?.Invoke(session,
                new RdpStateChangedEventArgs((RdpConnectionState)state, lastError, errorInfo, NativeMethods.FromUtf8(message)));
        }
        catch (Exception ex)
        {
            ReportCallbackException(ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDesktopResizeCallback(IntPtr user, uint width, uint height)
    {
        try
        {
            FromUser(user)?.Frame.Resize((int)width, (int)height);
        }
        catch (Exception ex)
        {
            ReportCallbackException(ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFrameCallback(IntPtr user, byte* buffer, uint stride, uint width, uint height, int x, int y, int w, int h)
    {
        try
        {
            FromUser(user)?.Frame.Update(buffer, (int)stride, (int)width, (int)height, x, y, w, h);
        }
        catch (Exception ex)
        {
            ReportCallbackException(ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnAuthenticateCallback(IntPtr user, int reason, byte* username, byte* domain)
    {
        try
        {
            var session = FromUser(user);
            var handler = session?.AuthenticationHandler;
            if (session == null || handler == null)
                return 0;

            var request = new RdpAuthenticationRequest((RdpAuthReason)reason,
                NativeMethods.FromUtf8(username), NativeMethods.FromUtf8(domain));
            var credentials = session.WaitForHandler(handler(request, session._cts.Token));
            if (credentials == null)
                return 0;

            lock (session._handleLock)
            {
                if (session._handle == IntPtr.Zero)
                    return 0;
                NativeMethods.rdpb_set_credentials(session._handle, credentials.UserName, credentials.Password, credentials.Domain);
            }
            return 1;
        }
        catch (Exception ex)
        {
            ReportCallbackException(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint OnVerifyCertificateCallback(IntPtr user, byte* host, ushort port, byte* commonName, byte* subject,
        byte* issuer, byte* fingerprint, byte* oldFingerprint, uint flags)
    {
        try
        {
            var session = FromUser(user);
            var handler = session?.CertificateHandler;
            if (session == null || handler == null)
                return (uint)CertificateDecision.Reject;

            var request = new RdpCertificateRequest(
                NativeMethods.FromUtf8(host) ?? "",
                port,
                NativeMethods.FromUtf8(commonName) ?? "",
                NativeMethods.FromUtf8(subject) ?? "",
                NativeMethods.FromUtf8(issuer) ?? "",
                NativeMethods.FromUtf8(fingerprint) ?? "",
                NativeMethods.FromUtf8(oldFingerprint),
                flags);
            return (uint)session.WaitForHandler(handler(request, session._cts.Token));
        }
        catch (Exception ex)
        {
            ReportCallbackException(ex);
            return (uint)CertificateDecision.Reject;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerCallback(IntPtr user, int action, IntPtr cursor)
    {
        try
        {
            FromUser(user)?.PointerChanged?.Invoke((RdpPointerAction)action, cursor);
        }
        catch (Exception ex)
        {
            ReportCallbackException(ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClipboardTextCallback(IntPtr user, char* text, uint length)
    {
        try
        {
            FromUser(user)?.RemoteClipboardText?.Invoke(new string(text, 0, (int)length));
        }
        catch (Exception ex)
        {
            ReportCallbackException(ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnGatewayMessageCallback(IntPtr user, uint type, int displayMandatory, int consentMandatory, char* message, uint length)
    {
        try
        {
            var session = FromUser(user);
            var handler = session?.GatewayMessageHandler;
            if (session == null || handler == null)
                return consentMandatory == 0 ? 1 : 0;

            var request = new RdpGatewayMessage(type, displayMandatory != 0, consentMandatory != 0,
                message == null ? "" : new string(message, 0, (int)length));
            return session.WaitForHandler(handler(request, session._cts.Token)) ? 1 : 0;
        }
        catch (Exception ex)
        {
            ReportCallbackException(ex);
            return 0;
        }
    }

    private static void ReportCallbackException(Exception ex)
    {
        if (ex is OperationCanceledException or AggregateException { InnerException: OperationCanceledException })
            return;
        System.Diagnostics.Debug.WriteLine($"[FreeRdp.Interop] callback failed: {ex}");
    }
}
