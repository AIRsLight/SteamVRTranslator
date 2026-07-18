using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SteamVRTranslator.App.Input;

public sealed class GlobalHotKey : IDisposable
{
    private const int HotKeyId = 0x5356;
    private const int WmHotKey = 0x0312;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private readonly DispatcherTimer _releasePoller = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private int _virtualKey;
    private bool _isPressed;

    public GlobalHotKey()
    {
        _releasePoller.Tick += OnReleasePoll;
    }

    public event EventHandler? Pressed;

    public event EventHandler? Released;

    public void Register(Window window, int virtualKey)
    {
        Unregister();
        _virtualKey = virtualKey;
        _windowHandle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source.AddHook(WndProc);
        if (!RegisterHotKey(_windowHandle, HotKeyId, 0, unchecked((uint)virtualKey)))
        {
            Unregister();
            throw new InvalidOperationException($"无法注册全局热键 VK 0x{virtualKey:X2}，可能已被其他程序占用。");
        }

        _releasePoller.Start();
    }

    public void Dispose() => Unregister();

    private void Unregister()
    {
        _releasePoller.Stop();
        _isPressed = false;
        if (_windowHandle != IntPtr.Zero)
        {
            _ = UnregisterHotKey(_windowHandle, HotKeyId);
        }

        _source?.RemoveHook(WndProc);
        _source = null;
        _windowHandle = IntPtr.Zero;
        _virtualKey = 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            if (!_isPressed)
            {
                _isPressed = true;
                Pressed?.Invoke(this, EventArgs.Empty);
            }
        }

        return IntPtr.Zero;
    }

    private void OnReleasePoll(object? sender, EventArgs e)
    {
        if (_isPressed && _virtualKey != 0 && (GetAsyncKeyState(_virtualKey) & 0x8000) == 0)
        {
            _isPressed = false;
            Released?.Invoke(this, EventArgs.Empty);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
