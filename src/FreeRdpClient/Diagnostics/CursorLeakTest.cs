#if DEBUG
using System.Runtime.InteropServices;
using System.Text;
using FreeRdpClient.Interop;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace FreeRdpClient.Diagnostics;

/// <summary>
/// Debug-only measurement of GDI/USER object usage of the cursor path
/// ("FreeRdpClient.exe --cursor-leak-test &lt;result file&gt;"). No connection is needed.
/// </summary>
internal static unsafe class CursorLeakTest
{
    private sealed partial class Probe : ContentControl
    {
        public void SetCursor(InputCursor? cursor) => ProtectedCursor = cursor;
    }

    public static string Run(Panel host)
    {
        const int n = 500;
        var log = new StringBuilder();
        void Mark(string step) => log.AppendLine($"{step,-58} GDI={Gdi(),6} USER={User(),6}");

        var probe = new Probe { Width = 10, Height = 10 };
        host.Children.Add(probe);
        Mark("start");

        // 1. Same sequence as create_cursor() / DestroyCursor() in rdp_bridge.c
        for (var i = 0; i < n; i++)
            DestroyIcon(CreateCursor32());
        Mark($"create + destroy HCURSOR x{n}");

        // 2. InputCursor objects for one HCURSOR, kept alive
        var cursor = CreateCursor32();
        var kept = new List<InputCursor?>();
        for (var i = 0; i < n; i++)
            kept.Add(CursorInterop.FromHCursor(cursor));
        Mark($"InputCursor.CreateFromHCursor x{n} (same HCURSOR, kept)");

        // 3. Assigning them to a control
        foreach (var c in kept)
            probe.SetCursor(c);
        Mark($"ProtectedCursor = cursor x{n}");

        // 4. Drop the managed references
        probe.SetCursor(null);
        kept.Clear();
        Collect();
        Mark("after dropping references + GC");

        // 5. The pattern RdpDisplay used: a new InputCursor for every pointer change
        for (var i = 0; i < n; i++)
            probe.SetCursor(CursorInterop.FromHCursor(cursor));
        Mark($"new InputCursor per change x{n} (no GC)");
        probe.SetCursor(null);
        Collect();
        Mark("after GC");

        // 6. Explicit Dispose of each InputCursor
        for (var i = 0; i < n; i++)
        {
            var c = CursorInterop.FromHCursor(cursor);
            probe.SetCursor(c);
            probe.SetCursor(null);
            c?.Dispose();
        }
        Mark($"new InputCursor per change + Dispose x{n}");

        DestroyIcon(cursor);
        host.Children.Remove(probe);
        Collect();
        Mark("end");
        return log.ToString();
    }

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static uint Gdi() => GetGuiResources(GetCurrentProcess(), 0);
    private static uint User() => GetGuiResources(GetCurrentProcess(), 1);

    private static IntPtr CreateCursor32()
    {
        const int size = 32;
        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)sizeof(BITMAPV5HEADER),
            bV5Width = size,
            bV5Height = -size,
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = 3, // BI_BITFIELDS
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
        };

        var dc = GetDC(IntPtr.Zero);
        var color = CreateDIBSection(dc, &header, 0, out var bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, dc);
        new Span<uint>((void*)bits, size * size).Fill(0x80FF0000);

        var maskBits = stackalloc byte[size * size / 8];
        var mask = CreateBitmap(size, size, 1, 1, maskBits);
        var info = new ICONINFO { fIcon = 0, xHotspot = 1, yHotspot = 1, hbmMask = mask, hbmColor = color };
        var cursor = CreateIconIndirect(&info);
        DeleteObject(mask);
        DeleteObject(color);
        return cursor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPV5HEADER
    {
        public uint bV5Size;
        public int bV5Width;
        public int bV5Height;
        public ushort bV5Planes;
        public ushort bV5BitCount;
        public uint bV5Compression;
        public uint bV5SizeImage;
        public int bV5XPelsPerMeter;
        public int bV5YPelsPerMeter;
        public uint bV5ClrUsed;
        public uint bV5ClrImportant;
        public uint bV5RedMask;
        public uint bV5GreenMask;
        public uint bV5BlueMask;
        public uint bV5AlphaMask;
        public uint bV5CSType;
        public fixed int bV5Endpoints[9];
        public uint bV5GammaRed;
        public uint bV5GammaGreen;
        public uint bV5GammaBlue;
        public uint bV5Intent;
        public uint bV5ProfileData;
        public uint bV5ProfileSize;
        public uint bV5Reserved;
    }

    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, BITMAPV5HEADER* info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte* bits);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ICONINFO* info);
    [DllImport("user32.dll")] private static extern int DestroyIcon(IntPtr icon);
}
#endif
