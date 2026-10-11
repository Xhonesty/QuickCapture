using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace QuickCapture;

internal sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    public event Action<int>? Pressed;
    private (uint Mods, uint Key)? _shot, _record;
    private CaptureSession? _capture;
    public HotkeyService(IntPtr hwnd) { _source = HwndSource.FromHwnd(hwnd); _source.AddHook(Hook); }
    public static (uint Mods, uint Key) Parse(string text)
    {
        uint mods = 0; Key key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 2; break;
                case "alt": mods |= 1; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default:
                    if (key != Key.None) throw new ArgumentException("快捷键需要一个普通按键。");
                    if (part.Length == 1 && part[0] is >= '0' and <= '9') key = Key.D0 + (part[0] - '0');
                    else if (!Enum.TryParse(part, true, out key) || key == Key.None)
                        throw new ArgumentException("快捷键格式示例：Ctrl+Alt+S");
                    break;
            }
        }
        if (mods == 0 || IsModifier(key) || KeyInterop.VirtualKeyFromKey(key) == 0)
            throw new ArgumentException("快捷键需要修饰键和一个普通按键，例如 Ctrl+Alt+S。");
        return (mods | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
    internal static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    internal static string Format(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)key - (int)Key.D0).ToString() : key == Key.Return ? "Enter" : key.ToString());
        string text = string.Join('+', parts);
        Parse(text);
        return text;
    }
    // Keep existing registrations reserved. Windows sends their combinations as
    // WM_HOTKEY rather than KeyDown, so route those messages into the input box.
    internal IDisposable BeginCapture(Action<Key, ModifierKeys> captured)
    {
        if (_capture != null) throw new InvalidOperationException("正在设置快捷键。");
        return _capture = new CaptureSession(this, captured);
    }
    public void Configure(string shot, string record)
    {
        var a = Parse(shot); var b = Parse(record);
        if (a == b) throw new ArgumentException("截图和录屏快捷键不能相同。");
        Native.UnregisterHotKey(_source.Handle, 1); Native.UnregisterHotKey(_source.Handle, 2);
        bool ok1 = Native.RegisterHotKey(_source.Handle, 1, a.Mods, a.Key);
        bool ok2 = ok1 && Native.RegisterHotKey(_source.Handle, 2, b.Mods, b.Key);
        if (!ok2)
        {
            Native.UnregisterHotKey(_source.Handle, 1); Native.UnregisterHotKey(_source.Handle, 2);
            if (_shot is { } oldA) Native.RegisterHotKey(_source.Handle, 1, oldA.Mods, oldA.Key);
            if (_record is { } oldB) Native.RegisterHotKey(_source.Handle, 2, oldB.Mods, oldB.Key);
            throw new InvalidOperationException(_shot == null ? "快捷键被其他程序占用，请在设置中更换组合。仍可使用界面按钮。" : "快捷键被其他程序占用，请更换组合。原来的快捷键已保留。");
        }
        _shot = a; _record = b;
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() is 1 or 2)
        {
            handled = true;
            if (_capture is { } capture)
            {
                long packed = lParam.ToInt64();
                capture.Captured(KeyInterop.KeyFromVirtualKey((int)((packed >> 16) & 0xFFFF)), (ModifierKeys)(packed & 0xF));
            }
            else Pressed?.Invoke(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }
    public void Dispose() { _capture?.Dispose(); Native.UnregisterHotKey(_source.Handle, 1); Native.UnregisterHotKey(_source.Handle, 2); _source.RemoveHook(Hook); }
    private sealed class CaptureSession(HotkeyService owner, Action<Key, ModifierKeys> captured) : IDisposable
    {
        internal Action<Key, ModifierKeys> Captured { get; } = captured;
        public void Dispose() { if (owner._capture == this) owner._capture = null; }
    }
}
