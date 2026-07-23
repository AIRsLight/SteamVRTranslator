using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace SteamVRTranslator.App.Input;

internal sealed class DesktopPushToTalkHotKey : IDisposable
{
    private const int LowLevelKeyboardHook = 13;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const int SystemKeyDown = 0x0104;
    private const int SystemKeyUp = 0x0105;

    private readonly SynchronizationContext? _synchronizationContext =
        SynchronizationContext.Current;
    private readonly HookProcedure _hookProcedure;
    private IntPtr _hook;
    private int _virtualKey;
    private bool _pressed;
    private bool _disposed;

    public DesktopPushToTalkHotKey()
    {
        _hookProcedure = HookCallback;
    }

    public event EventHandler<bool>? PressedChanged;

    public void Apply(bool enabled, int virtualKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        virtualKey = NormalizeVirtualKey(virtualKey);
        if (_pressed)
        {
            _pressed = false;
            Publish(false);
        }

        _virtualKey = virtualKey;
        if (!enabled)
        {
            Uninstall();
            return;
        }
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(LowLevelKeyboardHook, _hookProcedure, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法安装桌面 PTT 键盘监听。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_pressed)
        {
            _pressed = false;
            Publish(false);
        }
        Uninstall();
    }

    public static int NormalizeVirtualKey(int virtualKey) =>
        virtualKey is >= 1 and <= 254 ? virtualKey : 0xA2;

    public static string GetDisplayName(int virtualKey)
    {
        virtualKey = NormalizeVirtualKey(virtualKey);
        var modifierName = virtualKey switch
        {
            0xA0 => "Left Shift",
            0xA1 => "Right Shift",
            0xA2 => "Left Ctrl",
            0xA3 => "Right Ctrl",
            0xA4 => "Left Alt",
            0xA5 => "Right Alt",
            0x5B => "Left Windows",
            0x5C => "Right Windows",
            _ => null
        };
        if (modifierName is not null)
        {
            return modifierName;
        }

        var scanCode = MapVirtualKey((uint)virtualKey, MapVirtualKeyToScanCodeExtended);
        if (scanCode != 0)
        {
            var keyData = (int)((scanCode & 0xff) << 16);
            if ((scanCode & 0xff00) != 0)
            {
                keyData |= 1 << 24;
            }

            var name = new StringBuilder(64);
            if (GetKeyNameText(keyData, name, name.Capacity) > 0)
            {
                return name.ToString();
            }
        }

        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        return key == Key.None ? $"VK 0x{virtualKey:X2}" : key.ToString();
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _hook != IntPtr.Zero)
        {
            var keyboard = Marshal.PtrToStructure<LowLevelKeyboardInput>(data);
            if (keyboard.VirtualKey == _virtualKey)
            {
                var value = message.ToInt32();
                if ((value == KeyDown || value == SystemKeyDown) && !_pressed)
                {
                    _pressed = true;
                    Publish(true);
                }
                else if ((value == KeyUp || value == SystemKeyUp) && _pressed)
                {
                    _pressed = false;
                    Publish(false);
                }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private void Publish(bool pressed)
    {
        if (_synchronizationContext is null)
        {
            PressedChanged?.Invoke(this, pressed);
            return;
        }
        _synchronizationContext.Post(
            static state =>
            {
                var (owner, value) = ((DesktopPushToTalkHotKey Owner, bool Value))state!;
                owner.PressedChanged?.Invoke(owner, value);
            },
            (this, pressed));
    }

    private void Uninstall()
    {
        var hook = _hook;
        _hook = IntPtr.Zero;
        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        HookProcedure callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr message,
        IntPtr data);

    private const uint MapVirtualKeyToScanCodeExtended = 4;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(int keyData, StringBuilder text, int size);

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        public int VirtualKey;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }
}
