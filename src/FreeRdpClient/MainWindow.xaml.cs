using FreeRdp.Interop;
using FreeRdpClient.Input;
using FreeRdpClient.Models;
using FreeRdpClient.Views;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace FreeRdpClient;

public sealed partial class MainWindow : Window
{
    private SessionView? _fullScreenView;
    private TabViewItem? _fullScreenItem;

    public MainWindow()
    {
        InitializeComponent();

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();
        else
            Root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];

        var scale = GetDpiForWindow(Hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1280 * scale), (int)(860 * scale)));

        KeyboardHook.Install(Hwnd);

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;

        AddConnectionTab();
    }

    public IntPtr Hwnd { get; }

    private IEnumerable<SessionView> Sessions =>
        Tabs.TabItems.OfType<TabViewItem>().Select(SessionOf).OfType<SessionView>()
            .Concat(_fullScreenView != null ? [_fullScreenView] : []);

    // ------------------------------------------------------------------
    // Tabs

    // TabView does not refresh the displayed content when TabViewItem.Content of the selected tab
    // is replaced, so every tab gets a fixed host and only the host's child is swapped.
    private static Border HostOf(TabViewItem item) => (Border)item.Content;

    private static SessionView? SessionOf(TabViewItem? item) => item == null ? null : HostOf(item).Child as SessionView;

    private TabViewItem AddConnectionTab(ConnectionProfile? profile = null)
    {
        var item = new TabViewItem
        {
            Content = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        ShowConnectionPage(item, profile);
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
        return item;
    }

    private void ShowConnectionPage(TabViewItem item, ConnectionProfile? profile)
    {
        var page = new ConnectionPage();
        if (profile != null && profile.RdpFilePath == null)
            page.LoadProfile(profile);
        page.ConnectRequested += (_, request) => StartSession(item, request.Profile, request.Password);

        item.Header = "新しい接続";
        item.IconSource = new FontIconSource { Glyph = "" };
        HostOf(item).Child = page;
    }

    private void StartSession(TabViewItem item, ConnectionProfile profile, string? password)
    {
        var view = new SessionView(profile, password);
        view.CloseRequested += (_, _) =>
        {
            if (ReferenceEquals(_fullScreenView, view))
                ExitFullScreen();
            ShowConnectionPage(item, view.Profile);
        };
        view.FullScreenToggleRequested += (_, _) => ToggleFullScreen(view, item);
        view.StateChanged += (_, state) => UpdateHeader(item, view, state);

        HostOf(item).Child = view;
        UpdateHeader(item, view, RdpConnectionState.Connecting);
    }

    private static void UpdateHeader(TabViewItem item, SessionView view, RdpConnectionState state)
    {
        item.Header = view.Title;
        item.IconSource = new FontIconSource
        {
            Glyph = state switch
            {
                RdpConnectionState.Connected => "",
                RdpConnectionState.Disconnected => "",
                _ => "",
            },
        };
        ToolTipService.SetToolTip(item, view.Profile.Summary);
    }

    /// <summary>Opens a .rdp file in a new tab and connects.</summary>
    public void OpenRdpFile(string path)
    {
        var item = AddConnectionTab();
        StartSession(item, new ConnectionProfile { RdpFilePath = path }, null);
    }

    private void OnAddTabClick(TabView sender, object args) => AddConnectionTab();

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        var item = args.Tab;
        sender.TabItems.Remove(item);
        if (SessionOf(item) is { } view)
            await view.CloseAsync();

        if (sender.TabItems.Count == 0)
            Close();
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionOf(Tabs.SelectedItem as TabViewItem) is { } view)
            DispatcherQueue.TryEnqueue(view.FocusSession);
    }

    // ------------------------------------------------------------------
    // Full screen

    private void ToggleFullScreen(SessionView view, TabViewItem item)
    {
        if (_fullScreenView != null)
        {
            ExitFullScreen();
            return;
        }

        HostOf(item).Child = null;
        _fullScreenView = view;
        _fullScreenItem = item;
        FullScreenHost.Child = view;
        FullScreenHost.Visibility = Visibility.Visible;
        Tabs.Visibility = Visibility.Collapsed;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        view.IsFullScreen = true;
    }

    private void ExitFullScreen()
    {
        var view = _fullScreenView;
        var item = _fullScreenItem;
        if (view == null || item == null)
            return;

        _fullScreenView = null;
        _fullScreenItem = null;
        FullScreenHost.Child = null;
        FullScreenHost.Visibility = Visibility.Collapsed;
        Tabs.Visibility = Visibility.Visible;
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (Tabs.TabItems.Contains(item))
            HostOf(item).Child = view;
        view.IsFullScreen = false;
    }

    // ------------------------------------------------------------------
    // Window

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        var active = args.WindowActivationState != WindowActivationState.Deactivated;
        var current = _fullScreenView ?? SessionOf(Tabs.SelectedItem as TabViewItem);
        current?.OnWindowActivated(active);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        KeyboardHook.Uninstall();

        var sessions = Sessions.Select(v => v.DetachSession()).OfType<RdpSession>().ToList();
        foreach (var s in sessions)
            s.Disconnect();

        // Give FreeRDP a moment to close the connections cleanly.
        Task.Run(() => Parallel.ForEach(sessions, s => s.Dispose())).Wait(TimeSpan.FromSeconds(5));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
