using FreeRdp.Interop;
using FreeRdpClient.Services;

namespace FreeRdpClient.Models;

/// <summary>A saved connection (stored as JSON, passwords go to the Windows Credential Manager).</summary>
public sealed class ConnectionProfile
{
    public const string DynamicResolution = "dynamic";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3389;
    public string? UserName { get; set; }
    public string? Domain { get; set; }
    public bool SavePassword { get; set; }

    /// <summary>"dynamic" or "WIDTHxHEIGHT"</summary>
    public string Resolution { get; set; } = DynamicResolution;
    public bool MatchLocalScaling { get; set; } = true;

    public bool RedirectClipboard { get; set; } = true;
    public bool AudioPlayback { get; set; } = true;
    public bool RedirectDrives { get; set; }
    public bool H264 { get; set; }
    public bool IgnoreCertificate { get; set; }
    public bool SendWindowsKeys { get; set; } = true;
    public HardwareKeyboard Keyboard { get; set; } = HardwareKeyboard.Auto;
    public RemoteKeyboardLayout KeyboardLayout { get; set; } = RemoteKeyboardLayout.Auto;
    public string? ExtraArguments { get; set; }

    /// <summary>Set when the profile was opened from a .rdp file (not persisted as a profile).</summary>
    public string? RdpFilePath { get; set; }

    public DateTimeOffset LastConnected { get; set; }

    public string DisplayName =>
        RdpFilePath != null ? Path.GetFileNameWithoutExtension(RdpFilePath)
        : Port == 3389 ? Host : $"{Host}:{Port}";

    public string Summary =>
        string.IsNullOrWhiteSpace(UserName) ? DisplayName
        : string.IsNullOrWhiteSpace(Domain) ? $"{UserName}@{DisplayName}"
        : $"{Domain}\\{UserName}@{DisplayName}";

    public bool IsDynamicResolution => Resolution == DynamicResolution;

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();

    public bool TryGetFixedResolution(out int width, out int height)
    {
        width = height = 0;
        var parts = Resolution.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height);
    }

    /// <summary>Builds the FreeRDP options. <paramref name="viewWidth"/> etc. are physical pixels.</summary>
    public RdpConnectionOptions ToOptions(string? password, int viewWidth, int viewHeight, double rasterizationScale)
    {
        var options = new RdpConnectionOptions
        {
            RdpFilePath = RdpFilePath,
            Host = Host,
            Port = Port,
            UserName = UserName,
            Domain = Domain,
            Password = password,
            DynamicResolution = IsDynamicResolution,
            RedirectClipboard = RedirectClipboard,
            AudioPlayback = AudioPlayback,
            RedirectDrives = RedirectDrives,
            H264 = H264,
            IgnoreCertificate = IgnoreCertificate,
            ExtraArguments = ExtraArguments,
            KeyboardType = KeyboardNames.ToRdpKeyboardType(Keyboard, KeyboardLayout),
            KeyboardLayout = KeyboardNames.ToRdpLayoutId(KeyboardLayout),
            DesktopScaleFactor = MatchLocalScaling ? (int)Math.Round(rasterizationScale * 100) : 100,
        };

        if (TryGetFixedResolution(out var w, out var h))
        {
            options.Width = w;
            options.Height = h;
        }
        else
        {
            options.Width = Math.Max(viewWidth, 640) & ~1;
            options.Height = Math.Max(viewHeight, 480);
        }

        return options;
    }
}
