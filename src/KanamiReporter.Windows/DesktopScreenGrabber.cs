using System.Runtime.InteropServices;

namespace KanamiReporter.Windows;

/// <summary>
/// GDI 抓屏：先用 PrintWindow 让窗口把内容画进我们的位图，失败再退回从屏幕 DC 直接 BitBlt。
/// 这条路不经过 DWM 合成，实测在"Windows 图形捕获收到了帧、却读不出像素"的机器上仍能拿到画面，
/// 所以它是有图形捕获可用性问题时的兜底手段。
/// 代价：只能拿到屏幕上真实可见的内容，窗口被挡住或最小化时就抓不到游戏本体。
/// </summary>
internal sealed class DesktopScreenGrabber : IDisposable
{
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const int BitmapInfoHeaderSize = 40;

    private nint _screenDc;
    private nint _memoryDc;
    private nint _bitmap;
    private nint _previousBitmap;
    private nint _bits;
    private byte[] _buffer = [];
    private int _width;
    private int _height;

    /// <summary>让窗口把自身内容画进位图。抓到的数组由本对象复用，调用方要立刻用掉。</summary>
    public bool TryGrabWindow(nint window, int width, int height, out byte[] bgra)
    {
        if (!Ensure(width, height) || !PrintWindow(window, _memoryDc, PrintWindowRenderFullContent))
        {
            bgra = [];
            return false;
        }

        return TryRead(out bgra);
    }

    /// <summary>从屏幕 DC 抓一块区域（窗口被挡住时抓到的是挡住它的内容）。</summary>
    public bool TryGrabScreen(int x, int y, int width, int height, out byte[] bgra)
    {
        if (!Ensure(width, height) || !BitBlt(_memoryDc, 0, 0, width, height, _screenDc, x, y, SourceCopy | CaptureBlt))
        {
            bgra = [];
            return false;
        }

        return TryRead(out bgra);
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private bool Ensure(int width, int height)
    {
        if (_bitmap != nint.Zero && width == _width && height == _height)
        {
            return true;
        }

        Release();
        _width = width;
        _height = height;
        _screenDc = GetDC(nint.Zero);
        _memoryDc = CreateCompatibleDC(_screenDc);
        var info = new BitmapInfo
        {
            header = new BitmapInfoHeader
            {
                biSize = BitmapInfoHeaderSize,
                biWidth = width,
                biHeight = -height,   // 负高度 = 自上而下，字节顺序与 BGRA 一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };

        _bitmap = CreateDIBSection(_memoryDc, ref info, 0, out _bits, nint.Zero, 0);
        if (_bitmap == nint.Zero)
        {
            return false;
        }

        _previousBitmap = SelectObject(_memoryDc, _bitmap);
        _buffer = new byte[width * height * 4];
        return true;
    }

    private bool TryRead(out byte[] bgra)
    {
        if (_bits == nint.Zero || _buffer.Length == 0)
        {
            bgra = [];
            return false;
        }

        Marshal.Copy(_bits, _buffer, 0, _buffer.Length);
        bgra = _buffer;
        return true;
    }

    private void Release()
    {
        if (_bitmap != nint.Zero)
        {
            _ = SelectObject(_memoryDc, _previousBitmap);
            _ = DeleteObject(_bitmap);
            _bitmap = nint.Zero;
        }

        if (_memoryDc != nint.Zero)
        {
            _ = DeleteDC(_memoryDc);
            _memoryDc = nint.Zero;
        }

        if (_screenDc != nint.Zero)
        {
            _ = ReleaseDC(nint.Zero, _screenDc);
            _screenDc = nint.Zero;
        }

        _bits = nint.Zero;
        _width = 0;
        _height = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader header;
        public uint colors;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(nint window, nint dc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint value);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
