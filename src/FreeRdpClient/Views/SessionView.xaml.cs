using FreeRdp.Interop;
using FreeRdpClient.Models;
using FreeRdpClient.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace FreeRdpClient.Views;

/// <summary>One remote session: owns the <see cref="RdpSession"/> and its UI.</summary>
public sealed partial class SessionView : UserControl
{
    // FreeRDP error codes (freerdp/error.h) that mean the credentials were wrong
    private static readonly uint[] AuthenticationErrors = [0x00020009, 0x00020014, 0x00020015];

    private readonly ConnectionProfile _profile;
    private readonly DispatcherQueueTimer _barTimer;
    private string? _password;
    private RdpSession? _session;
    private bool _userDisconnect;
    private bool _started;
    private bool _isFullScreen;
    private string? _lastRemoteClipboard;

    public SessionView(ConnectionProfile profile, string? password)
    {
        InitializeComponent();
        _profile = profile;
        _password = password;

        TitleText.Text = BarTitle.Text = profile.DisplayName;

        _barTimer = DispatcherQueue.CreateTimer();
        _barTimer.Interval = TimeSpan.FromMilliseconds(800);
        _barTimer.IsRepeating = false;
        _barTimer.Tick += (_, _) => FullScreenBar.Visibility = Visibility.Collapsed;

        Display.FullScreenToggleRequested += (_, _) => FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
        Display.PointerPositionChanged += OnDisplayPointerMoved;
        Display.SessionFocused += (_, _) => SyncLocalClipboard();
        Display.DisplayScaleChanged += (_, _) => UpdateScaleStatus();

        Loaded += OnLoaded;
    }

    public string Title => _profile.DisplayName;

    public ConnectionProfile Profile => _profile;

    public RdpConnectionState State => _session?.State ?? RdpConnectionState.Disconnected;

    /// <summary>The user wants to leave the session (back to the connection form).</summary>
    public event EventHandler? CloseRequested;

    public event EventHandler? FullScreenToggleRequested;

    /// <summary>The connection state changed (for the tab header).</summary>
    public event EventHandler<RdpConnectionState>? StateChanged;

    public bool IsFullScreen
    {
        get => _isFullScreen;
        set
        {
            _isFullScreen = value;
            Toolbar.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            FullScreenBar.Visibility = Visibility.Collapsed;
            Display.FocusSession();
        }
    }

    private void UpdateScaleStatus()
    {
        if (_session?.State != RdpConnectionState.Connected)
            return;

        var scaled = Display.DisplayScale < 1.0;
        StatusText.Text = scaled ? $"縮小表示 {Display.DisplayScale:P0}" : "";
        ToolTipService.SetToolTip(StatusText, scaled
            ? "リモートの解像度が表示領域より大きいため縮小しています (文字がぼやけます)。\n解像度を「ウィンドウに合わせる」にするか、ウィンドウを大きくすると等倍で表示されます。"
            : null);
    }

    public void OnWindowActivated(bool active) => Display.OnWindowActivated(active);

    public void FocusSession() => Display.FocusSession();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            // re-parented (full screen): take the keyboard back
            if (_session?.State == RdpConnectionState.Connected)
                Display.FocusSession();
            return;
        }
        _started = true;
        SafeStart();
    }

    private void SafeStart()
    {
        try
        {
            Start();
        }
        catch (Exception ex)
        {
            ShowFailure("接続を開始できませんでした", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Session lifecycle

    private void Start()
    {
        _userDisconnect = false;
        ShowOverlay("接続しています...", _profile.DisplayName, busy: true);

        RdpSession session;
        try
        {
            session = new RdpSession();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            ShowFailure("FreeRDP を読み込めませんでした", ex.Message);
            return;
        }

        session.StateChanged += OnSessionStateChanged;
        session.RemoteClipboardText += OnRemoteClipboardText;
        session.AuthenticationHandler = (request, token) => RunOnUi(() => AuthenticateAsync(request, token));
        session.CertificateHandler = (request, token) => RunOnUi(() => Dialogs.VerifyCertificateAsync(XamlRoot, request, token));
        session.GatewayMessageHandler = (message, token) => RunOnUi(() => Dialogs.ShowGatewayMessageAsync(XamlRoot, message, token));

        var (width, height) = Display.PhysicalSize;
        if (width < 200 || height < 200)
        {
            var scale = XamlRoot.RasterizationScale;
            width = (int)(XamlRoot.Size.Width * scale);
            height = (int)(XamlRoot.Size.Height * scale);
        }

        var options = _profile.ToOptions(_password, width, height, Display.RasterizationScale);
        try
        {
            session.Configure(options);
        }
        catch (Exception ex)
        {
            session.Dispose();
            ShowFailure("接続設定が正しくありません", ex.Message + "\n追加の FreeRDP オプションを確認してください。");
            return;
        }

        Display.DynamicResolution = options.DynamicResolution;
        Display.DesktopScale = options.DesktopScaleFactor;
        Display.SendWindowsKeys = _profile.SendWindowsKeys;
        Display.Attach(session);
        _session = session;

        session.Connect();
    }

    private void OnSessionStateChanged(object? sender, RdpStateChangedEventArgs e)
    {
        var session = sender as RdpSession;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(session, _session))
                return;

            StateChanged?.Invoke(this, e.State);
            switch (e.State)
            {
                case RdpConnectionState.Connecting:
                    ShowOverlay("接続しています...", _profile.DisplayName, busy: true);
                    break;

                case RdpConnectionState.Reconnecting:
                    StatusText.Text = "再接続しています...";
                    ShowOverlay("再接続しています...", "ネットワーク接続が切断されました。", busy: true);
                    break;

                case RdpConnectionState.Connected:
                    OnConnected();
                    break;

                case RdpConnectionState.Disconnected:
                    OnDisconnected(e);
                    break;
            }
        });
    }

    private void OnConnected()
    {
        Overlay.Visibility = Visibility.Collapsed;
        StatusText.Text = "";
        Display.OnConnected();
        UpdateScaleStatus();
        Display.FocusSession();

        if (_profile.RedirectClipboard)
        {
            Clipboard.ContentChanged -= OnLocalClipboardChanged;
            Clipboard.ContentChanged += OnLocalClipboardChanged;
            SyncLocalClipboard();
        }
    }

    private void OnDisconnected(RdpStateChangedEventArgs e)
    {
        Clipboard.ContentChanged -= OnLocalClipboardChanged;
        Display.Detach();

        var session = _session;
        _session = null;
        if (session != null)
            _ = session.DisposeAsync();

        if (_userDisconnect)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (AuthenticationErrors.Contains(e.LastError))
            _password = null; // prompt again on reconnect

        if (e.Message == null)
            ShowFailure("リモート セッションが終了しました", "");
        else
            ShowFailure("接続が切断されました", e.Message);
        StatusText.Text = "切断";
    }

    /// <summary>Disconnects and releases the session (tab closed / window closing).</summary>
    public async Task CloseAsync()
    {
        _userDisconnect = true;
        Clipboard.ContentChanged -= OnLocalClipboardChanged;
        Display.Detach();
        var session = _session;
        _session = null;
        if (session != null)
            await session.DisposeAsync();
    }

    /// <summary>Synchronous variant for application shutdown.</summary>
    public RdpSession? DetachSession()
    {
        _userDisconnect = true;
        Clipboard.ContentChanged -= OnLocalClipboardChanged;
        Display.Detach();
        var session = _session;
        _session = null;
        return session;
    }

    // ------------------------------------------------------------------
    // Overlay

    private void ShowOverlay(string title, string message, bool busy)
    {
        Overlay.Visibility = Visibility.Visible;
        Progress.IsActive = busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ErrorIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        OverlayTitle.Text = title;
        OverlayMessage.Text = message;
        OverlayMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ReconnectButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowFailure(string title, string message)
    {
        ShowOverlay(title, message, busy: false);
        StateChanged?.Invoke(this, RdpConnectionState.Disconnected);
    }

    // ------------------------------------------------------------------
    // Prompts (called from the RDP thread through RunOnUi)

    private Task<T> RunOnUi<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    tcs.TrySetResult(await func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetCanceled();
        }
        return tcs.Task;
    }

    private async Task<RdpCredentials?> AuthenticateAsync(RdpAuthenticationRequest request, CancellationToken token)
    {
        var target = _profile.RdpFilePath != null ? _profile.DisplayName : $"{_profile.Host}:{_profile.Port}";
        var (credentials, save) = await Dialogs.AskCredentialsAsync(XamlRoot, target, request,
            offerSave: _profile.RdpFilePath == null, token);

        if (credentials != null && save && _profile.RdpFilePath == null)
        {
            _profile.UserName = credentials.UserName;
            _profile.Domain = credentials.Domain;
            _profile.SavePassword = true;
            CredentialStore.Write(_profile, credentials.Password);
            ConnectionStore.Save(_profile);
        }

        if (credentials != null)
            _password = credentials.Password;
        return credentials;
    }

    // ------------------------------------------------------------------
    // Clipboard (text)

    private void OnRemoteClipboardText(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _lastRemoteClipboard = text;
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clipboard write failed: {ex.Message}");
            }
        });
    }

    private void OnLocalClipboardChanged(object? sender, object e) => SyncLocalClipboard();

    private async void SyncLocalClipboard()
    {
        var session = _session;
        if (session == null || !_profile.RedirectClipboard || session.State != RdpConnectionState.Connected)
            return;

        string? text = null;
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
                text = await content.GetTextAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard read failed: {ex.Message}");
            return;
        }

        // Text that just came from the server does not need to be announced back.
        if (text != null && text == _lastRemoteClipboard)
            return;

        session.SetClipboardText(text);
    }

    // ------------------------------------------------------------------
    // Toolbar

    private void OnCtrlAltDelClick(object sender, RoutedEventArgs e)
    {
        _session?.SendCtrlAltDel();
        Display.FocusSession();
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        _userDisconnect = true;
        if (_session != null)
            _session.Disconnect();
        else
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnReconnectClick(object sender, RoutedEventArgs e) => SafeStart();

    private void OnBackClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnDisplayPointerMoved(object? sender, Windows.Foundation.Point position)
    {
        if (!_isFullScreen)
            return;
        if (position.Y <= 2)
        {
            _barTimer.Stop();
            FullScreenBar.Visibility = Visibility.Visible;
        }
        else if (FullScreenBar.Visibility == Visibility.Visible && position.Y > FullScreenBar.ActualHeight + 40)
        {
            _barTimer.Start();
        }
    }

    private void OnFullScreenBarPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_infoOpen)
            _barTimer.Start();
    }

    // ------------------------------------------------------------------
    // Connection information

    private bool _infoOpen;

    private void OnInfoFlyoutOpening(object? sender, object e)
    {
        if (sender is not Flyout flyout)
            return;
        _infoOpen = true;
        _barTimer.Stop();
        flyout.Content = BuildInfoPanel(CollectInfo());
    }

    private void OnInfoFlyoutClosed(object? sender, object e)
    {
        _infoOpen = false;
        if (_isFullScreen)
            FullScreenBar.Visibility = Visibility.Collapsed;
        Display.FocusSession();
    }

    private static string OnOff(bool? value, string on = "有効", string off = "無効") =>
        value switch { true => on, false => off, null => "-" };

    private static string ProtocolName(uint? protocol) => protocol switch
    {
        null => "-",
        0 => "RDP 標準セキュリティ",
        _ => string.Join(" + ", new (uint Flag, string Name)[]
            {
                (0x01, "TLS"), (0x02, "NLA (CredSSP)"), (0x04, "RDSTLS"), (0x08, "NLA 拡張 (HYBRID_EX)"), (0x10, "Azure AD (RDSAAD)"),
            }.Where(p => (protocol & p.Flag) != 0).Select(p => p.Name)),
    };

    /// <summary>
    /// Requested options (profile) next to the values FreeRDP actually uses after negotiation.
    /// </summary>
    private List<(string Section, List<(string Label, string Value)> Items)> CollectInfo()
    {
        var s = _session;
        var p = _profile;
        var connected = s?.State == RdpConnectionState.Connected;
        var channels = s?.ChannelStatus ?? RdpChannelStatus.None;

        string Actual(Func<RdpSession, string?> read) => s == null ? "-" : read(s) ?? "-";

        var user = s?.GetString("FreeRDP_Username");
        var domain = s?.GetString("FreeRDP_Domain");
        var gateway = s?.GetBool("FreeRDP_GatewayEnabled") == true ? s.GetString("FreeRDP_GatewayHostname") ?? "有効" : "使用しない";

        var state = s?.State switch
        {
            RdpConnectionState.Connected => "接続中",
            RdpConnectionState.Connecting => "接続しています",
            RdpConnectionState.Reconnecting => "再接続しています",
            _ => "切断",
        };

        var frame = s?.Frame;
        var resolutionMode = p.IsDynamicResolution ? "ウィンドウに合わせる (動的)" : $"固定 {p.Resolution.Replace("x", " × ")}";
        var dynamicState = !p.IsDynamicResolution ? "使用しない"
            : channels.HasFlag(RdpChannelStatus.DisplayControl) ? "有効"
            : connected ? "サーバーが未対応 (固定解像度で表示中)" : "-";

        var gfx = channels.HasFlag(RdpChannelStatus.GraphicsPipeline)
            ? "RDP 8 グラフィックス パイプライン" + (s?.GetBool("FreeRDP_GfxH264") == true ? " (H.264 を要求)" : " (RemoteFX / Progressive)")
            : connected ? "従来のビットマップ転送" : "-";

        var clipboard = !p.RedirectClipboard ? "共有しない"
            : channels.HasFlag(RdpChannelStatus.Clipboard) ? "共有中 (テキスト)"
            : connected ? "要求済み (サーバー側で無効)" : "-";

        var layout = s?.GetUInt32("FreeRDP_KeyboardLayout");
        var kbType = s?.GetUInt32("FreeRDP_KeyboardType");

        return
        [
            ("接続",
            [
                ("状態", state),
                ("接続先", p.RdpFilePath != null
                    ? $"{Path.GetFileName(p.RdpFilePath)} ({Actual(x => x.GetString("FreeRDP_ServerHostname"))}:{Actual(x => x.GetUInt32("FreeRDP_ServerPort")?.ToString())})"
                    : $"{p.Host}:{p.Port}"),
                ("ユーザー", string.IsNullOrEmpty(user) ? "-" : string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}"),
                ("セキュリティ", ProtocolName(s?.GetUInt32("FreeRDP_SelectedProtocol"))),
                ("ゲートウェイ", gateway),
                ("FreeRDP", RdpSession.Version),
            ]),
            ("画面",
            [
                ("解像度 (設定)", resolutionMode),
                ("リモートの解像度", frame is { Width: > 0 } ? $"{frame.Width} × {frame.Height}" : "-"),
                ("動的解像度", dynamicState),
                ("表示倍率", Display.DisplayScale >= 1.0 ? "100% (等倍)" : $"{Display.DisplayScale:P0} (縮小)"),
                ("リモートのスケール", Actual(x => x.GetUInt32("FreeRDP_DesktopScaleFactor") is { } v ? $"{v}%" : null)),
                ("色深度", Actual(x => x.GetUInt32("FreeRDP_ColorDepth") is { } v ? $"{v} bit" : null)),
                ("グラフィックス", gfx),
            ]),
            ("ローカル リソース",
            [
                ("クリップボード", clipboard),
                ("音声", OnOff(s?.GetBool("FreeRDP_AudioPlayback"), "この PC で再生", "再生しない")),
                ("ドライブ", OnOff(s?.GetBool("FreeRDP_RedirectDrives"), "共有", "共有しない")),
                ("Windows キー", p.SendWindowsKeys ? "リモートへ送る" : "この PC で処理"),
                ("キーボード レイアウト", layout is { } l ? KeyboardNames.LayoutName(l) : "-"),
                ("キーボードの種類", kbType is { } t ? KeyboardNames.TypeName(t, s?.GetUInt32("FreeRDP_KeyboardSubType") ?? 0) : "-"),
            ]),
            ("詳細",
            [
                ("証明書の検証", OnOff(s?.GetBool("FreeRDP_IgnoreCertificate") is { } ignore ? !ignore : null)),
                ("自動再接続", OnOff(s?.GetBool("FreeRDP_AutoReconnectionEnabled"))),
                ("追加オプション", string.IsNullOrWhiteSpace(p.ExtraArguments) ? "なし" : p.ExtraArguments),
            ]),
        ];
    }

    private static FrameworkElement BuildInfoPanel(List<(string Section, List<(string Label, string Value)> Items)> info)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 380, MaxWidth = 520 };

        foreach (var (section, items) in info)
        {
            panel.Children.Add(new TextBlock { Text = section, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });

            var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4, Margin = new Thickness(8, 0, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < items.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = items[i].Label, Opacity = 0.7 };
                var value = new TextBlock { Text = items[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }
            panel.Children.Add(grid);
        }

        var copy = new Button { Content = "テキストとしてコピー", HorizontalAlignment = HorizontalAlignment.Right };
        copy.Click += (_, _) =>
        {
            var text = string.Join(Environment.NewLine + Environment.NewLine, info.Select(s =>
                $"[{s.Section}]" + Environment.NewLine + string.Join(Environment.NewLine, s.Items.Select(i => $"{i.Label}: {i.Value}"))));
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        };
        panel.Children.Add(copy);

        return new ScrollViewer { Content = panel, MaxHeight = 560, Padding = new Thickness(0, 0, 12, 0) };
    }
}
