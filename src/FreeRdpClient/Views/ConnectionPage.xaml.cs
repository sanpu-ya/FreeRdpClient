using FreeRdp.Interop;
using FreeRdpClient.Models;
using FreeRdpClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;

namespace FreeRdpClient.Views;

public sealed partial class ConnectionPage : UserControl
{
    private static readonly (string Label, string Value)[] Resolutions =
    [
        ("ウィンドウに合わせる (動的に変更)", ConnectionProfile.DynamicResolution),
        ("1920 × 1200", "1920x1200"),
        ("1920 × 1080", "1920x1080"),
        ("1600 × 1200", "1600x1200"),
        ("1600 × 900", "1600x900"),
        ("1440 × 900", "1440x900"),
        ("1366 × 768", "1366x768"),
        ("1280 × 1024", "1280x1024"),
        ("1280 × 720", "1280x720"),
        ("1024 × 768", "1024x768"),
    ];

    private Guid _profileId = Guid.NewGuid();

    public ConnectionPage()
    {
        InitializeComponent();

        try
        {
            VersionText.Text = $"FreeRDP {RdpSession.Version} · .NET {Environment.Version}";
        }
        catch (DllNotFoundException)
        {
            VersionText.Text = "FreeRdpBridge.dll が見つかりません";
        }

        foreach (var (label, value) in Resolutions)
            ResolutionBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        ResolutionBox.SelectedIndex = 0;

        foreach (var keyboard in Enum.GetValues<HardwareKeyboard>())
            KeyboardBox.Items.Add(new ComboBoxItem { Content = KeyboardNames.Name(keyboard), Tag = keyboard });
        KeyboardBox.SelectedIndex = 0;

        foreach (var layout in Enum.GetValues<RemoteKeyboardLayout>())
            LayoutBox.Items.Add(new ComboBoxItem { Content = KeyboardNames.Name(layout), Tag = layout });
        LayoutBox.SelectedIndex = 0;

        ConnectionStore.Changed += OnStoreChanged;
        Unloaded += (_, _) => ConnectionStore.Changed -= OnStoreChanged;
        RefreshRecent();

        if (ConnectionStore.Profiles.FirstOrDefault() is { } last)
            LoadProfile(last);

        Loaded += (_, _) => HostBox.Focus(FocusState.Programmatic);
    }

    /// <summary>The user wants to connect. The password is null if none was entered.</summary>
    public event EventHandler<(ConnectionProfile Profile, string? Password)>? ConnectRequested;

    private void OnStoreChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshRecent);

    private void RefreshRecent()
    {
        var items = ConnectionStore.Profiles.ToList();
        RecentList.ItemsSource = items;
        NoRecentText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void LoadProfile(ConnectionProfile profile)
    {
        _profileId = profile.Id;
        HostBox.Text = profile.Host;
        PortBox.Value = profile.Port;
        UserBox.Text = profile.UserName ?? "";
        DomainBox.Text = profile.Domain ?? "";
        SavePasswordBox.IsChecked = profile.SavePassword;
        PasswordBox.Password = profile.SavePassword ? CredentialStore.Read(profile) ?? "" : "";

        // Drop the extra entry of a previously loaded profile
        while (ResolutionBox.Items.Count > Resolutions.Length)
            ResolutionBox.Items.RemoveAt(ResolutionBox.Items.Count - 1);

        var index = Array.FindIndex(Resolutions, r => r.Value == profile.Resolution);
        if (index < 0 && profile.TryGetFixedResolution(out var w, out var h))
        {
            // A size that is no longer in the list (saved by an older version): keep it selectable
            ResolutionBox.Items.Add(new ComboBoxItem { Content = $"{w} × {h} (保存済み)", Tag = profile.Resolution });
            index = ResolutionBox.Items.Count - 1;
        }
        ResolutionBox.SelectedIndex = index < 0 ? 0 : index;
        ScalingSwitch.IsOn = profile.MatchLocalScaling;
        ClipboardBox.IsChecked = profile.RedirectClipboard;
        AudioBox.IsChecked = profile.AudioPlayback;
        DrivesBox.IsChecked = profile.RedirectDrives;
        WinKeysBox.IsChecked = profile.SendWindowsKeys;
        KeyboardBox.SelectedIndex = Math.Max(0, Array.IndexOf(Enum.GetValues<HardwareKeyboard>(), profile.Keyboard));
        LayoutBox.SelectedIndex = Math.Max(0, Array.IndexOf(Enum.GetValues<RemoteKeyboardLayout>(), profile.KeyboardLayout));
        H264Box.IsChecked = profile.H264;
        IgnoreCertBox.IsChecked = profile.IgnoreCertificate;
        ExtraArgsBox.Text = profile.ExtraArguments ?? "";
        ErrorBar.IsOpen = false;
    }

    private ConnectionProfile? ReadProfile(bool requireHost = true)
    {
        var host = HostBox.Text.Trim();
        var port = double.IsNaN(PortBox.Value) ? 3389 : (int)PortBox.Value;

        // "host:port" (but not an IPv6 literal)
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host[(colon + 1)..], out var p))
        {
            port = p;
            host = host[..colon];
        }
        else if (host.StartsWith('[') && host.Contains("]:"))
        {
            var end = host.IndexOf("]:", StringComparison.Ordinal);
            if (int.TryParse(host[(end + 2)..], out p))
                port = p;
            host = host[1..end];
        }

        if (host.Length == 0 && requireHost)
        {
            ShowError("接続先のコンピューター名を入力してください。");
            HostBox.Focus(FocusState.Programmatic);
            return null;
        }

        return new ConnectionProfile
        {
            Id = _profileId,
            Host = host,
            Port = port,
            UserName = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim(),
            Domain = string.IsNullOrWhiteSpace(DomainBox.Text) ? null : DomainBox.Text.Trim(),
            SavePassword = SavePasswordBox.IsChecked == true,
            Resolution = (ResolutionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? ConnectionProfile.DynamicResolution,
            MatchLocalScaling = ScalingSwitch.IsOn,
            RedirectClipboard = ClipboardBox.IsChecked == true,
            AudioPlayback = AudioBox.IsChecked == true,
            RedirectDrives = DrivesBox.IsChecked == true,
            SendWindowsKeys = WinKeysBox.IsChecked == true,
            Keyboard = (KeyboardBox.SelectedItem as ComboBoxItem)?.Tag is HardwareKeyboard kb ? kb : HardwareKeyboard.Auto,
            KeyboardLayout = (LayoutBox.SelectedItem as ComboBoxItem)?.Tag is RemoteKeyboardLayout kl ? kl : RemoteKeyboardLayout.Auto,
            H264 = H264Box.IsChecked == true,
            IgnoreCertificate = IgnoreCertBox.IsChecked == true,
            ExtraArguments = string.IsNullOrWhiteSpace(ExtraArgsBox.Text) ? null : ExtraArgsBox.Text.Trim(),
        };
    }

    private void Connect()
    {
        ErrorBar.IsOpen = false;
        var profile = ReadProfile();
        if (profile == null)
            return;

        var password = string.IsNullOrEmpty(PasswordBox.Password) ? null : PasswordBox.Password;
        if (profile.SavePassword && password != null)
            CredentialStore.Write(profile, password);
        else if (!profile.SavePassword)
            CredentialStore.Delete(profile);

        ConnectionStore.Save(profile);
        ConnectRequested?.Invoke(this, (profile, password));
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void OnConnectClick(object sender, RoutedEventArgs e) => Connect();

    private void OnFormKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            Connect();
        }
    }

    private void OnHostChanged(object sender, TextChangedEventArgs e) => ErrorBar.IsOpen = false;

    private void OnRecentItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ConnectionProfile profile)
            LoadProfile(profile);
    }

    private void OnRecentDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConnectionProfile profile)
        {
            LoadProfile(profile);
            Connect();
        }
    }

    private void OnRecentConnectClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ConnectionProfile profile)
        {
            LoadProfile(profile);
            Connect();
        }
    }

    private void OnRecentDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ConnectionProfile profile)
            ConnectionStore.Remove(profile);
    }

    private async void OnOpenRdpFileClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".rdp");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;

        var profile = ReadProfile(requireHost: false)!;
        profile.RdpFilePath = file.Path;
        profile.Host = "";
        profile.UserName = null;
        profile.Domain = null;
        ConnectRequested?.Invoke(this, (profile, null));
    }
}
