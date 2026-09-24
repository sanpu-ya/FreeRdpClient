using FreeRdp.Interop;
using FreeRdpClient.Input;
using FreeRdpClient.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace FreeRdpClient.Controls;

/// <summary>
/// Shows the remote desktop and forwards mouse / keyboard input to an <see cref="RdpSession"/>.
/// The frame is presented through a WriteableBitmap scaled uniformly into the available space.
/// </summary>
public sealed unsafe partial class RdpDisplay : ContentControl, KeyboardHook.ITarget
{
    private readonly Image _image;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _resizeTimer;
    private RdpSession? _session;
    private WriteableBitmap? _bitmap;
    private IBuffer? _bitmapBuffer;
    private byte* _bitmapPixels;
    private bool _hasFocus;
    private (int Width, int Height, int Scale) _lastRequestedSize;
    private int _resizeRetries;

    public RdpDisplay()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        // The image size is set explicitly (UpdateImageLayout) so the remote desktop is drawn
        // 1:1 in physical pixels whenever it fits; any resampling makes text blurry.
        _image = new Image
        {
            Stretch = Stretch.Fill,
            UseLayoutRounding = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ContentControl's default template does not draw Background, so the black backdrop
        // (letterbox area, behind the image) is an explicit panel.
        var backdrop = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        backdrop.Children.Add(_image);
        Content = backdrop;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        AllowFocusOnInteraction = true;

        _resizeTimer = _dispatcher.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(250);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += (_, _) => RequestRemoteResize();

        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerMoved += OnPointerMoved;
        PointerWheelChanged += OnPointerWheelChanged;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        SizeChanged += (_, e) =>
        {
            // The image may be a few pixels larger than the control (see UpdateImageLayout)
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            UpdateImageLayout();
            _resizeRetries = 0;
            if (DynamicResolution)
                _resizeTimer.Start();
        };
        Unloaded += (_, _) => KeyboardHook.Release(this);
        Loaded += (_, _) =>
        {
            // DPI changes (window moved to another monitor)
            XamlRoot.Changed -= OnXamlRootChanged;
            XamlRoot.Changed += OnXamlRootChanged;
            UpdateImageLayout();
        };
    }

    /// <summary>
    /// Always takes the whole available area. The image is sized from this control's size, so
    /// sizing to the content would shrink the control and the image in a loop.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        UpdateImageLayout();
        if (DynamicResolution)
            _resizeTimer.Start();
    }

    /// <summary>Current display scale of the remote desktop (1.0 = pixel exact).</summary>
    public double DisplayScale { get; private set; } = 1.0;

    /// <summary>Raised when <see cref="DisplayScale"/> changes.</summary>
    public event EventHandler? DisplayScaleChanged;

    /// <summary>
    /// Sizes the image to the bitmap's physical pixel size. It is only scaled down (keeping the
    /// aspect ratio) when the remote desktop is larger than the available area.
    /// </summary>
    private void UpdateImageLayout()
    {
        if (_bitmap == null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
        var width = _bitmap.PixelWidth / rasterScale;
        var height = _bitmap.PixelHeight / rasterScale;
        var fit = Math.Min(1.0, Math.Min(ActualWidth / width, ActualHeight / height));

        // With dynamic resolution the remote desktop follows the control, so a size mismatch only
        // lasts until the server has resized: keep it 1:1 anchored top-left (like mstsc) instead
        // of briefly scaling. Clipping or a black margin is visible during that time.
        var followsControl = DynamicResolution && _session is { State: RdpConnectionState.Connected, CanResize: true };

        // Scaling by almost 1 blurs everything while hardly saving any space. If the desktop is
        // only slightly larger than the area, show it 1:1 and let a few edge pixels be clipped.
        if (followsControl || fit >= 0.97)
            fit = 1.0;

        _image.HorizontalAlignment = followsControl ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        _image.VerticalAlignment = followsControl ? VerticalAlignment.Top : VerticalAlignment.Center;
        _image.Width = width * fit;
        _image.Height = height * fit;

        if (Math.Abs(fit - DisplayScale) > 0.0001)
        {
            DisplayScale = fit;
            DisplayScaleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Resize the remote desktop with the control.</summary>
    public bool DynamicResolution { get; set; }

    /// <summary>Send the Windows keys to the remote session (otherwise they stay local).</summary>
    public bool SendWindowsKeys { get; set; } = true;

    /// <summary>Remote DPI scale in percent, used for dynamic resolution (0 = do not send).</summary>
    public int DesktopScale { get; set; } = 100;

    /// <summary>Ctrl+Alt+Enter / Ctrl+Alt+Break was pressed.</summary>
    public event EventHandler? FullScreenToggleRequested;

    /// <summary>Pointer position in control coordinates (for auto-hiding overlays).</summary>
    public event EventHandler<Point>? PointerPositionChanged;

    /// <summary>The control received keyboard focus (e.g. to sync the clipboard).</summary>
    public event EventHandler? SessionFocused;

    /// <summary>Size of the control in physical pixels.</summary>
    public (int Width, int Height) PhysicalSize
    {
        get
        {
            var scale = XamlRoot?.RasterizationScale ?? 1.0;
            return ((int)Math.Round(ActualWidth * scale), (int)Math.Round(ActualHeight * scale));
        }
    }

    public void Attach(RdpSession session)
    {
        Detach();
        _session = session;
        _session.Frame.Invalidated += OnFrameInvalidated;
        _session.PointerChanged += OnPointerChanged;
        _lastRequestedSize = default;
    }

    public void Detach()
    {
        if (_session != null)
        {
            _session.Frame.Invalidated -= OnFrameInvalidated;
            _session.PointerChanged -= OnPointerChanged;
        }
        _session = null;
        KeyboardHook.Release(this);
        ProtectedCursor = null;
    }

    public void FocusSession() => Focus(FocusState.Programmatic);

    // ------------------------------------------------------------------
    // Rendering

    private void OnFrameInvalidated() => _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, Present);

    private void Present()
    {
        var session = _session;
        if (session == null)
            return;

        try
        {
            if (session.Frame.Present(CopyToBitmap))
                _bitmap?.Invalidate();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CopyToBitmap(byte* pixels, int stride, int width, int height, PixelRect dirty, bool resized)
    {
        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height);
            _bitmapBuffer = _bitmap.PixelBuffer;
            _bitmapPixels = PixelBufferAccess.GetPointer(_bitmapBuffer);
            _image.Source = _bitmap;
            UpdateImageLayout();
            dirty = new PixelRect(0, 0, width, height);
        }

        var rowBytes = dirty.Width * 4;
        var offset = (long)dirty.Y * stride + dirty.X * 4;
        var src = pixels + offset;
        var dst = _bitmapPixels + offset; // same layout: width * 4 bytes per row
        for (var y = 0; y < dirty.Height; y++)
        {
            System.Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
            src += stride;
            dst += stride;
        }
    }

    // ------------------------------------------------------------------
    // Dynamic resolution

    private void RequestRemoteResize()
    {
        if (_session == null || !DynamicResolution || _session.State != RdpConnectionState.Connected)
            return;

        var (width, height) = PhysicalSize;
        if (width < 200 || height < 200)
            return;

        var scale = DesktopScale is >= 100 and <= 500 ? DesktopScale : 100;
        var request = (width & ~1, height, scale);
        if (request == _lastRequestedSize && _session.Frame.Width == request.Item1 && _session.Frame.Height == height)
            return;

        if (_session.Resize(request.Item1, height, scale, RdpConnectionOptions.DeviceScaleFor(scale)))
        {
            _lastRequestedSize = request;
            _resizeRetries = 0;
            UpdateImageLayout(); // display control is ready: stop scaling while the server resizes
        }
        else if (++_resizeRetries < 25)
        {
            _resizeTimer.Start(); // display control channel not ready yet (or unsupported), retry for ~10s
        }
    }

    /// <summary>Called when the session is connected: sync the remote size with the control.</summary>
    public void OnConnected()
    {
        _lastRequestedSize = default;
        _resizeRetries = 0;
        if (DynamicResolution)
            _resizeTimer.Start();
    }

    // ------------------------------------------------------------------
    // Pointer

    private bool TryMapToRemote(PointerRoutedEventArgs e, out int x, out int y, out PointerPoint point)
    {
        point = e.GetCurrentPoint(_image);
        x = y = 0;
        var frame = _session?.Frame;
        if (frame == null || frame.Width == 0 || _image.ActualWidth <= 0 || _image.ActualHeight <= 0)
            return false;

        x = (int)Math.Clamp(point.Position.X * frame.Width / _image.ActualWidth, 0, frame.Width - 1);
        y = (int)Math.Clamp(point.Position.Y * frame.Height / _image.ActualHeight, 0, frame.Height - 1);
        return true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        CapturePointer(e.Pointer);
        SendButton(e);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        SendButton(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed && !props.IsMiddleButtonPressed)
            ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        PointerPositionChanged?.Invoke(this, e.GetCurrentPoint(this).Position);

        if (!TryMapToRemote(e, out var x, out var y, out var point))
            return;

        // A second button pressed while another one is held arrives as PointerMoved.
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.Other)
            SendButton(e);
        else
            _session!.SendMouse(PointerFlags.Move, x, y);
        e.Handled = true;
    }

    private void SendButton(PointerRoutedEventArgs e)
    {
        if (!TryMapToRemote(e, out var x, out var y, out var point))
            return;

        var session = _session!;
        switch (point.Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.LeftButtonPressed:
                session.SendMouse(PointerFlags.Down | PointerFlags.Button1, x, y);
                break;
            case PointerUpdateKind.LeftButtonReleased:
                session.SendMouse(PointerFlags.Button1, x, y);
                break;
            case PointerUpdateKind.RightButtonPressed:
                session.SendMouse(PointerFlags.Down | PointerFlags.Button2, x, y);
                break;
            case PointerUpdateKind.RightButtonReleased:
                session.SendMouse(PointerFlags.Button2, x, y);
                break;
            case PointerUpdateKind.MiddleButtonPressed:
                session.SendMouse(PointerFlags.Down | PointerFlags.Button3, x, y);
                break;
            case PointerUpdateKind.MiddleButtonReleased:
                session.SendMouse(PointerFlags.Button3, x, y);
                break;
            case PointerUpdateKind.XButton1Pressed:
                session.SendExtendedMouse(ExtendedPointerFlags.Down | ExtendedPointerFlags.Button1, x, y);
                break;
            case PointerUpdateKind.XButton1Released:
                session.SendExtendedMouse(ExtendedPointerFlags.Button1, x, y);
                break;
            case PointerUpdateKind.XButton2Pressed:
                session.SendExtendedMouse(ExtendedPointerFlags.Down | ExtendedPointerFlags.Button2, x, y);
                break;
            case PointerUpdateKind.XButton2Released:
                session.SendExtendedMouse(ExtendedPointerFlags.Button2, x, y);
                break;
            default:
                session.SendMouse(PointerFlags.Move, x, y);
                break;
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!TryMapToRemote(e, out var x, out var y, out var point))
            return;

        var delta = point.Properties.MouseWheelDelta;
        var flags = point.Properties.IsHorizontalMouseWheel ? PointerFlags.HWheel : PointerFlags.Wheel;

        // The rotation is a 9 bit two's complement value (WheelRotationMask 0x01FF).
        while (delta != 0)
        {
            var step = Math.Clamp(delta, -255, 255);
            _session!.SendMouse(flags | (PointerFlags)(ushort)(step & 0x1FF), x, y);
            delta -= step;
        }
        e.Handled = true;
    }

    private void OnPointerChanged(RdpPointerAction action, IntPtr cursor)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_session == null)
                return;
            ProtectedCursor = action switch
            {
                RdpPointerAction.Set => CursorInterop.FromHCursor(cursor) ?? InputSystemCursor.Create(InputSystemCursorShape.Arrow),
                RdpPointerAction.Hide => CursorInterop.Hidden,
                _ => InputSystemCursor.Create(InputSystemCursorShape.Arrow),
            };
        });
    }

    // ------------------------------------------------------------------
    // Keyboard

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = true;
        KeyboardHook.Target = this;
        _session?.FocusIn();
        SessionFocused?.Invoke(this, EventArgs.Empty);
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = false;
        KeyboardHook.Release(this);
        _session?.ReleaseAllKeys();
    }

    /// <summary>The main window was (de)activated.</summary>
    public void OnWindowActivated(bool active)
    {
        if (!_hasFocus)
            return;

        if (active)
        {
            KeyboardHook.Target = this;
            _session?.FocusIn();
            SessionFocused?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _session?.ReleaseAllKeys();
        }
    }

    bool KeyboardHook.ITarget.OnKey(uint vk, uint scanCode, bool extended, bool down, bool injected)
    {
        var session = _session;
        if (session == null || session.State != RdpConnectionState.Connected)
            return false;

        // Ctrl+Alt+Enter / Ctrl+Alt+Break toggles full screen (as in wfreerdp)
        if ((vk == Win32.VK_RETURN || vk == Win32.VK_CANCEL) &&
            (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0 &&
            (Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0)
        {
            if (down)
                FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (!SendWindowsKeys && (vk == Win32.VK_LWIN || vk == Win32.VK_RWIN))
            return false;

        // Characters injected by tools such as on-screen keyboards
        if (vk == Win32.VK_PACKET)
        {
            session.SendUnicode(down, (char)scanCode);
            return true;
        }

        if (scanCode == 0)
        {
            var mapped = Win32.MapVirtualKeyW(vk, Win32.MAPVK_VK_TO_VSC_EX);
            if (mapped == 0)
                return false;
            extended = (mapped & 0xFF00) == 0xE000;
            scanCode = mapped & 0xFF;
        }

        session.SendKey(down, scanCode, extended);
        return true;
    }
}
