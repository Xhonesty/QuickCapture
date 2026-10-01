using System;
using System.Windows.Input;
using System.Windows.Interop;

namespace QuickCapture;

internal sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    public event Action<int>? Pressed;
    private (uint Mods, uint Key)? _shot, _record;
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
                    if (key != Key.None || !Enum.TryParse(part, true, out key) || key == Key.None)
                        throw new ArgumentException("快捷键格式示例：Ctrl+Alt+S");
                    break;
            }
        }
        if (mods == 0 || key == Key.None || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            throw new ArgumentException("快捷键需要修饰键和一个普通按键，例如 Ctrl+Alt+S。");
        return (mods | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(key));
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
        if (msg == 0x0312) { handled = true; Pressed?.Invoke(wParam.ToInt32()); }
        return IntPtr.Zero;
    }
    public void Dispose() { Native.UnregisterHotKey(_source.Handle, 1); Native.UnregisterHotKey(_source.Handle, 2); _source.RemoveHook(Hook); }
}
