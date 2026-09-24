using FreeRdp.Interop;
using Microsoft.UI.Xaml;

namespace FreeRdpClient;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled: {e.Exception}");
        };
    }

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeRdpClient");

    public static MainWindow? MainWindow => (Current as App)?._window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // FreeRDP (WLog) output goes to %LOCALAPPDATA%\FreeRdpClient\logs
        RdpSession.ConfigureLogging(Path.Combine(DataDirectory, "logs"), $"freerdp-{DateTime.Now:yyyyMMdd}.log");

        _window = new MainWindow();

        // "FreeRdpClient.exe file.rdp" opens the file directly
        var cmdArgs = Environment.GetCommandLineArgs();
        var rdpFile = cmdArgs.Skip(1).FirstOrDefault(a => a.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

        _window.Activate();
        if (rdpFile != null)
            _window.OpenRdpFile(rdpFile);
    }
}
