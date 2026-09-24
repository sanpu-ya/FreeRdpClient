namespace FreeRdp.Interop;

public enum RdpConnectionState
{
    Connecting = 0,
    Connected = 1,
    Disconnected = 2,
    Reconnecting = 3,
}

/// <summary>Reason passed to AuthenticateEx (rdp_auth_reason).</summary>
public enum RdpAuthReason
{
    Nla = 0,
    Tls = 1,
    Rdp = 2,
    GatewayHttp = 3,
    GatewayRdg = 4,
    GatewayRpc = 5,
    SmartcardPin = 6,
    Rdstls = 7,
    FidoPin = 8,
}

public enum RdpPointerAction
{
    Set = 0,
    Hide = 1,
    Default = 2,
}

public enum CertificateDecision : uint
{
    Reject = 0,
    AcceptPermanently = 1,
    AcceptOnce = 2,
}

/// <summary>Mouse flags from freerdp/input.h</summary>
[Flags]
public enum PointerFlags : ushort
{
    None = 0,
    WheelNegative = 0x0100,
    Wheel = 0x0200,
    HWheel = 0x0400,
    Move = 0x0800,
    Button1 = 0x1000, // left
    Button2 = 0x2000, // right
    Button3 = 0x4000, // middle
    Down = 0x8000,
}

[Flags]
public enum ExtendedPointerFlags : ushort
{
    None = 0,
    Button1 = 0x0001, // XBUTTON1
    Button2 = 0x0002, // XBUTTON2
    Down = 0x8000,
}

/// <summary>Channels that are currently usable (rdpb_channel_flags).</summary>
[Flags]
public enum RdpChannelStatus : uint
{
    None = 0,
    GraphicsPipeline = 0x01,
    DisplayControl = 0x02,
    Clipboard = 0x04,
}

public sealed class RdpStateChangedEventArgs(RdpConnectionState state, uint lastError, uint errorInfo, string? message) : EventArgs
{
    public RdpConnectionState State { get; } = state;
    public uint LastError { get; } = lastError;
    public uint ErrorInfo { get; } = errorInfo;
    /// <summary>FreeRDP error text; null for normal disconnects (logoff, user disconnect).</summary>
    public string? Message { get; } = message;
}

public sealed record RdpCredentials(string UserName, string Password, string? Domain);

public sealed record RdpAuthenticationRequest(RdpAuthReason Reason, string? UserName, string? Domain);

public sealed record RdpCertificateRequest(
    string Host,
    ushort Port,
    string CommonName,
    string Subject,
    string Issuer,
    string Fingerprint,
    string? OldFingerprint,
    uint Flags)
{
    public bool Changed => OldFingerprint != null;
    /// <summary>VERIFY_CERT_FLAG_GATEWAY</summary>
    public bool IsGateway => (Flags & 0x20) != 0;
    /// <summary>VERIFY_CERT_FLAG_MISMATCH: host name does not match the certificate</summary>
    public bool IsNameMismatch => (Flags & 0x80) != 0;
}

public sealed record RdpGatewayMessage(uint Type, bool DisplayMandatory, bool ConsentMandatory, string Message);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty)
            return other;
        if (other.IsEmpty)
            return this;
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new PixelRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }
}
