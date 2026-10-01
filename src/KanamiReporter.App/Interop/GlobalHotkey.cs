using System.Runtime.InteropServices;

namespace KanamiReporter.App;

internal sealed class GlobalHotkey : IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;

    private const int WmHotkey = 0x0312;
    private const int GwlWndproc = -4;
    private const int HotkeyId = 0x4B52;

    private readonly IntPtr _windowHandle;
    private readonly Action _callback;
    private readonly WndProc _windowProc;
    private IntPtr _previousWindowProc;
    private bool _disposed;

    public GlobalHotkey(IntPtr windowHandle, uint modifiers, uint virtualKey, Action callback)
    {
        _windowHandle = windowHandle;
        _callback = callback;
        _windowProc = WindowProc;

        Marshal.SetLastPInvokeError(0);
        _previousWindowProc = SetWindowLongPtr(_windowHandle, GwlWndproc, Marshal.GetFunctionPointerForDelegate(_windowProc));
        if (_previousWindowProc == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            throw new InvalidOperationException("无法挂接主窗口消息处理。");
        }

        if (!RegisterHotKey(_windowHandle, HotkeyId, modifiers, virtualKey))
        {
            SetWindowLongPtr(_windowHandle, GwlWndproc, _previousWindowProc);
            _previousWindowProc = IntPtr.Zero;
            throw new InvalidOperationException("无法注册全局热键 Ctrl+Alt+R。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterHotKey(_windowHandle, HotkeyId);

        if (_previousWindowProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_windowHandle, GwlWndproc, _previousWindowProc);
            _previousWindowProc = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    private IntPtr WindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _callback();
            return IntPtr.Zero;
        }

        return CallWindowProc(_previousWindowProc, windowHandle, message, wParam, lParam);
    }

    private static IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : SetWindowLong32(windowHandle, index, value);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previousWindowProc, IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
