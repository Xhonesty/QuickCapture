using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace QuickCapture;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    internal delegate bool EnumWindowProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc proc, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmBounds(IntPtr hwnd, uint attribute, out RECT value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmFlag(IntPtr hwnd, uint attribute, out int value, int size);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
    internal static void Place(Window window, Rectangle bounds, bool activate = true)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(hwnd, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040u | (activate ? 0u : 0x0010u));
    }
    internal static bool ExcludeFromCapture(Window window) => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
        && SetWindowDisplayAffinity(new WindowInteropHelper(window).EnsureHandle(), 0x11);
    internal static bool CaptureProtected(IntPtr hwnd) => GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity != 0;
    internal static void MakeClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        long style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new IntPtr(style | 0x20 | 0x08000000)); // WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
    }
    internal static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        long style = GetWindowLongPtr(hwnd, -20).ToInt64(), flags = 0x20 | 0x08000000;
        SetWindowLongPtr(hwnd, -20, new IntPtr(enabled ? style | flags : style & ~flags));
    }
    internal static bool IsClickThrough(Window window) => (GetWindowLongPtr(new WindowInteropHelper(window).EnsureHandle(), -20).ToInt64() & 0x20) != 0;
    internal static bool VisibleWindowBounds(IntPtr hwnd, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || DwmFlag(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (DwmBounds(hwnd, 9, out var rect, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(hwnd, out rect)) return false;
        bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return bounds.Width > 40 && bounds.Height > 40;
    }
    internal static List<WindowTarget> SnapTargets()
    {
        var targets = new List<WindowTarget>();
        // EnumWindows supplies top-to-bottom Z order; use bounds before overlays open.
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId || !VisibleWindowBounds(hwnd, out var bounds)) return true;
            var title = new StringBuilder(512); GetWindowText(hwnd, title, title.Capacity);
            if (title.Length > 0) targets.Add(new(hwnd, title.ToString(), bounds));
            return true;
        }, IntPtr.Zero);
        return targets;
    }
    internal static List<WindowItem> Windows()
    {
        var list = new List<WindowItem>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId || !IsWindowVisible(hwnd) || DwmFlag(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var text = new StringBuilder(512); GetWindowText(hwnd, text, text.Capacity);
            if (text.Length > 0 && (IsIconic(hwnd) || GetWindowRect(hwnd, out var r) && r.Right - r.Left > 40 && r.Bottom - r.Top > 40))
            {
                string app = "未知应用";
                try { using var process = Process.GetProcessById((int)pid); app = process.ProcessName; } catch (Exception) { }
                list.Add(new(hwnd, text.ToString(), app, (int)pid));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
    internal static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}

internal sealed record WindowItem(IntPtr Handle, string Title, string ApplicationName = "", int ProcessId = 0)
{
    public override string ToString() => Title;
}
