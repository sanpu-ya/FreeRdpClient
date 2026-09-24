using System.Runtime.InteropServices;

namespace FreeRdp.Interop;

/// <summary>
/// Copy of the remote desktop (BGRA32, top-down) shared between the RDP thread and the UI.
/// The RDP thread copies updated regions in from the FreeRDP GDI buffer while painting;
/// the UI takes the accumulated dirty region with <see cref="Present"/>.
/// </summary>
public sealed unsafe class FrameBuffer : IDisposable
{
    public unsafe delegate void PresentCallback(byte* pixels, int stride, int width, int height, PixelRect dirty, bool resized);

    private readonly Lock _lock = new();
    private byte* _pixels;
    private int _width;
    private int _height;
    private PixelRect _dirty;
    private bool _resized;
    private bool _pending;

    /// <summary>
    /// Raised (on the RDP thread) when new content is available and no presentation is pending.
    /// Handlers should schedule <see cref="Present"/> on the UI thread.
    /// </summary>
    public event Action? Invalidated;

    public int Width => _width;
    public int Height => _height;

    internal void Resize(int width, int height)
    {
        lock (_lock)
        {
            Reallocate(width, height);
        }
        Signal();
    }

    internal void Update(byte* src, int srcStride, int width, int height, int x, int y, int w, int h)
    {
        lock (_lock)
        {
            if (width != _width || height != _height)
                Reallocate(width, height);

            var dstStride = _width * 4;
            var s = src + (long)y * srcStride + x * 4;
            var d = _pixels + (long)y * dstStride + x * 4;
            for (var row = 0; row < h; row++)
            {
                CopyOpaque((uint*)s, (uint*)d, w);
                s += srcStride;
                d += dstStride;
            }

            _dirty = _dirty.Union(new PixelRect(x, y, w, h));
        }
        Signal();
    }

    /// <summary>
    /// Copies BGRA pixels and forces alpha to 0xFF. The remote desktop is always opaque, but
    /// servers (e.g. xrdp) may send undefined alpha values; the UI treats the buffer as
    /// premultiplied BGRA, where such pixels render washed out.
    /// </summary>
    private static void CopyOpaque(uint* src, uint* dst, int count)
    {
        var i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            var alpha = new System.Numerics.Vector<uint>(0xFF000000u);
            var step = System.Numerics.Vector<uint>.Count;
            for (; i <= count - step; i += step)
            {
                var v = System.Numerics.Vector.Load(src + i);
                System.Numerics.Vector.Store(v | alpha, dst + i);
            }
        }

        for (; i < count; i++)
            dst[i] = src[i] | 0xFF000000u;
    }

    /// <summary>Hands the dirty region to <paramref name="callback"/> (under the frame lock).</summary>
    /// <returns>false if there was nothing to present.</returns>
    public bool Present(PresentCallback callback)
    {
        lock (_lock)
        {
            _pending = false;
            if (_pixels == null || _dirty.IsEmpty)
                return false;

            callback(_pixels, _width * 4, _width, _height, _dirty, _resized);
            _dirty = default;
            _resized = false;
            return true;
        }
    }

    private void Reallocate(int width, int height)
    {
        if (_pixels != null)
            NativeMemory.Free(_pixels);

        _width = Math.Max(width, 0);
        _height = Math.Max(height, 0);
        var size = (nuint)_width * (nuint)_height * 4;
        _pixels = size == 0 ? null : (byte*)NativeMemory.AllocZeroed(size);

        // Opaque black until the server paints.
        if (_pixels != null)
        {
            var p = (uint*)_pixels;
            new Span<uint>(p, _width * _height).Fill(0xFF000000);
        }

        _dirty = new PixelRect(0, 0, _width, _height);
        _resized = true;
    }

    private void Signal()
    {
        bool raise;
        lock (_lock)
        {
            raise = !_pending;
            _pending = true;
        }

        if (raise)
            Invalidated?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_pixels != null)
                NativeMemory.Free(_pixels);
            _pixels = null;
            _width = _height = 0;
        }
    }
}
