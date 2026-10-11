using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace QuickCapture;

internal static class ImageClipboard
{
    internal static async Task SetAsync(byte[] encoded, byte[] bmp, string registered, string mime)
    {
        // Fully rendered native formats remain readable after repeated BMP
        // reads and after the application exits; Windows owns transferred data.
        using var owner = new HwndSource(new HwndSourceParameters("QuickCapture clipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        var memory = new List<(uint Format, IntPtr Handle)>(); IntPtr bitmapHandle = IntPtr.Zero;
        bool opened = false;
        try
        {
            memory.Add((Format(registered), Allocate(encoded)));
            memory.Add((Format(mime), Allocate(encoded)));
            memory.Add((8, Allocate(bmp[14..])));
            using (var stream = new MemoryStream(bmp, false))
            using (var bitmap = new System.Drawing.Bitmap(stream)) bitmapHandle = bitmap.GetHbitmap();
            for (int attempt = 0; ; attempt++)
            {
                if (OpenClipboard(owner.Handle)) { opened = true; break; }
                if (attempt >= 4) throw new Win32Exception(Marshal.GetLastWin32Error(), "剪贴板正被其他程序占用，请重试。");
                await Task.Delay(80);
            }
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
            for (int i = 0; i < memory.Count; i++)
            {
                var (format, handle) = memory[i];
                if (SetClipboardData(format, handle) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                memory[i] = (format, IntPtr.Zero);
            }
            if (SetClipboardData(2, bitmapHandle) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmapHandle = IntPtr.Zero;
        }
        finally
        {
            if (opened) CloseClipboard();
            foreach (var (_, handle) in memory) if (handle != IntPtr.Zero) GlobalFree(handle);
            if (bitmapHandle != IntPtr.Zero) Native.DeleteObject(bitmapHandle);
        }
    }
    private static uint Format(string name)
    {
        uint id = RegisterClipboardFormat(name); return id != 0 ? id : throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static IntPtr Allocate(byte[] bytes)
    {
        var memory = GlobalAlloc(0x42, (UIntPtr)bytes.Length); if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero) { GlobalFree(memory); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        catch { GlobalUnlock(memory); GlobalFree(memory); throw; }
        GlobalUnlock(memory); return memory;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
