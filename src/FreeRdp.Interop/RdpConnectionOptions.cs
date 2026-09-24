using System.Text;

namespace FreeRdp.Interop;

/// <summary>
/// Connection parameters. They are translated into FreeRDP command line arguments so that
/// FreeRDP's own parser (client/common/cmdline.c) applies all defaults and dependencies.
/// </summary>
public sealed class RdpConnectionOptions
{
    /// <summary>Optional .rdp file. Settings given here override the file.</summary>
    public string? RdpFilePath { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3389;
    public string? UserName { get; set; }
    public string? Domain { get; set; }
    public string? Password { get; set; }

    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    /// <summary>Resize the remote desktop with the window (display control channel).</summary>
    public bool DynamicResolution { get; set; } = true;
    /// <summary>Remote DPI scaling in percent (100 - 500).</summary>
    public int DesktopScaleFactor { get; set; } = 100;

    public bool RedirectClipboard { get; set; } = true;
    public bool AudioPlayback { get; set; } = true;
    public bool RedirectDrives { get; set; }
    /// <summary>Allow H.264 (AVC444) in the graphics pipeline.</summary>
    public bool H264 { get; set; }
    public bool IgnoreCertificate { get; set; }
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// Keyboard type announced to the server (TS_UD_CS_CORE keyboardType/SubType/FunctionKey),
    /// e.g. 7/2/12 = Japanese 106/109 keyboard, 7/0/12 = English 101/102 keyboard with a Japanese
    /// layout. Null = FreeRDP default.
    /// </summary>
    public (uint Type, uint SubType, uint FunctionKeys)? KeyboardType { get; set; }

    /// <summary>Keyboard layout id announced to the server (e.g. 0x409 US, 0x411 Japanese). Null = input language.</summary>
    public uint? KeyboardLayout { get; set; }

    /// <summary>Additional FreeRDP command line arguments, e.g. "/gateway:g:gw.example.com".</summary>
    public string? ExtraArguments { get; set; }

    public IReadOnlyList<string> BuildArguments()
    {
        var args = new List<string> { "freerdp" };

        // FreeRDP loads a .rdp file given as the first argument (cmdline.c)
        if (!string.IsNullOrEmpty(RdpFilePath))
            args.Add(RdpFilePath);

        var host = Host.Trim();
        if (host.Length > 0)
        {
            if (host.Contains(':') && !host.StartsWith('['))
                host = $"[{host}]"; // IPv6 literal
            args.Add($"/v:{host}");
            args.Add($"/port:{Port}");
        }

        if (!string.IsNullOrWhiteSpace(UserName))
            args.Add($"/u:{UserName.Trim()}");
        if (!string.IsNullOrWhiteSpace(Domain))
            args.Add($"/d:{Domain.Trim()}");
        // The password is not passed as an argument (see RdpSession.Configure)

        args.Add($"/size:{Math.Max(Width, 200)}x{Math.Max(Height, 200)}");
        args.Add(DynamicResolution ? "+dynamic-resolution" : "-dynamic-resolution");
        if (DesktopScaleFactor is > 100 and <= 500)
        {
            args.Add($"/scale-desktop:{DesktopScaleFactor}");
            args.Add($"/scale-device:{DeviceScaleFor(DesktopScaleFactor)}");
        }

        args.Add(H264 ? "/gfx:AVC444" : "/gfx");
        args.Add(RedirectClipboard ? "+clipboard" : "-clipboard");
        if (AudioPlayback)
            args.Add("/sound");
        if (RedirectDrives)
            args.Add("+drives");
        if (IgnoreCertificate)
            args.Add("/cert:ignore");
        if (AutoReconnect)
            args.Add("+auto-reconnect");
        args.Add("/network:auto");
        // KeyboardType is applied through the settings API (RdpSession.Configure):
        // /kbd:subtype: does not accept 0, which is needed for English keyboards.

        args.AddRange(SplitArguments(ExtraArguments));
        return args;
    }

    /// <summary>Maps a desktop scale factor to the nearest allowed device scale factor (100/140/180).</summary>
    public static int DeviceScaleFor(int desktopScale) => desktopScale switch
    {
        < 125 => 100,
        < 165 => 140,
        _ => 180,
    };

    /// <summary>Splits a command line honouring double quotes.</summary>
    public static IEnumerable<string> SplitArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            yield break;

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                    yield return current.ToString();
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
            yield return current.ToString();
    }
}
