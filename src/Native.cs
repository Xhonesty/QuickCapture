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
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
    internal static void Place(Window window, Rectangle bounds)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(hwnd, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
    }
    internal static void ExcludeFromCapture(Window window) => SetWindowDisplayAffinity(new WindowInteropHelper(window).EnsureHandle(), 0x11);
    internal static List<WindowItem> Windows()
    {
        var list = new List<WindowItem>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            var text = new StringBuilder(512); GetWindowText(hwnd, text, text.Capacity);
            if (text.Length > 0 && GetWindowRect(hwnd, out var r) && r.Right - r.Left > 40 && r.Bottom - r.Top > 40)
                list.Add(new(hwnd, text.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }
    internal static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}

internal sealed record WindowItem(IntPtr Handle, string Title)
{
    public override string ToString() => Title;
}
