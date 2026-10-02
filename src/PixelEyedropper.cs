using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace QuickCapture;

// Canvas sampling is in source pixels. WPF converts mouse input through zoom,
// scrolling and monitor DPI; screen sampling uses physical desktop coordinates.
internal sealed class PixelEyedropper : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    private readonly Window _owner;
    private readonly AnnotationSurface _surface;
    private readonly Action<Color?> _complete;
    private readonly Action<string> _failed;
    private readonly Popup _preview;
    private readonly Image _magnified = new() { Width = 99, Height = 99, Stretch = Stretch.Fill };
    private readonly Border _sample = new() { Width = 22, Height = 22, CornerRadius = new(3), Margin = new(0, 0, 7, 0) };
    private readonly TextBlock _hex = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private byte[]? _pixels;
    private int _width, _height;
    private bool _screen;
    private Color? _value;
    internal bool Active { get; private set; }
    internal Color? SampledColor => _value;
    internal Rect? PreviewPhysicalBounds => _preview.IsOpen && PresentationSource.FromVisual(_preview.Child) is HwndSource source && Native.GetWindowRect(source.Handle, out var bounds)
        ? new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top) : null;
    internal PixelEyedropper(Window owner, AnnotationSurface surface, Action<Color?> complete, Action<string> failed)
    {
        _owner = owner; _surface = surface; _complete = complete; _failed = failed;
        var contents = new StackPanel();
        var grid = new Grid { Width = 99, Height = 99 }; grid.Children.Add(_magnified);
        grid.Children.Add(new Border { Width = 11, Height = 11, BorderThickness = new(1), BorderBrush = Brushes.White, IsHitTestVisible = false }); contents.Children.Add(grid);
        var sample = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 7, 0, 0) }; sample.Children.Add(_sample); sample.Children.Add(_hex); contents.Children.Add(sample);
        contents.Children.Add(UiDesign.Text("单击确认 · Esc 取消", true));
        var border = UiDesign.Panel(contents, true); border.Margin = new(0); border.IsHitTestVisible = false;
        _preview = new Popup { Child = border, PlacementTarget = owner, Placement = PlacementMode.Relative, AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false, Focusable = false };
        RenderOptions.SetBitmapScalingMode(_magnified, BitmapScalingMode.NearestNeighbor);
        owner.PreviewMouseMove += Move; owner.PreviewMouseLeftButtonDown += Click; owner.PreviewMouseRightButtonDown += RightClick; owner.PreviewKeyDown += Key; owner.Deactivated += Deactivated;
        _timer.Tick += (_, _) => UpdateScreen();
    }
    internal void Begin(bool screen)
    {
        if (Active) Cancel();
        _screen = screen; _value = null;
        if (!screen)
        {
            var snapshot = new FormatConvertedBitmap(_surface.Export(), PixelFormats.Bgra32, null, 0); _width = snapshot.PixelWidth; _height = snapshot.PixelHeight;
            _pixels = new byte[_width * _height * 4]; snapshot.CopyPixels(_pixels, _width * 4, 0);
        }
        Active = true; _surface.ShowSelection = false; _surface.InvalidateVisual(); _owner.Activate();
        Mouse.OverrideCursor = Cursors.Cross;
        if (!Mouse.Capture(_owner, CaptureMode.Element)) { Fail("无法捕获取色输入，请重新使用画布吸管。"); return; }
        if (screen) { UpdateScreen(); if (Active) _timer.Start(); }
        else UpdateCanvas(Mouse.GetPosition(_surface));
    }
    private void PositionPreview(Point ownerPoint)
    {
        if (_screen && GetPhysicalCursorPos(out var cursor))
        {
            PositionScreenPreview(cursor, Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).Bounds);
            return;
        }
        _preview.HorizontalOffset = ownerPoint.X + 20; _preview.VerticalOffset = ownerPoint.Y + 20; _preview.IsOpen = true;
    }
    private void PositionScreenPreview(POINT cursor, System.Drawing.Rectangle screen)
    {
        _preview.IsOpen = true;
        if (_preview.Child is FrameworkElement child) child.UpdateLayout();
        if (PresentationSource.FromVisual(_preview.Child) is not HwndSource source || !Native.GetWindowRect(source.Handle, out var previous)) return;
        int width = previous.Right - previous.Left, height = previous.Bottom - previous.Top;
        if (width <= 0 || height <= 0) return;
        // Use the popup's actual physical size: it can differ from the owner's
        // DPI after moving across monitors. Choose a quadrant, then clamp it.
        const int gap = 14;
        var sample = new System.Drawing.Rectangle(cursor.X - 4, cursor.Y - 4, 9, 9);
        System.Drawing.Rectangle? safe = null;
        foreach (var (x, y) in new[]
        {
            (cursor.X + gap, cursor.Y + gap), (cursor.X - gap - width, cursor.Y + gap),
            (cursor.X + gap, cursor.Y - gap - height), (cursor.X - gap - width, cursor.Y - gap - height)
        })
        {
            var candidate = new System.Drawing.Rectangle(Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - width)), Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - height)), width, height);
            if (!candidate.IntersectsWith(sample)) { safe = candidate; break; }
        }
        // An unusually small display may have no safe space for the magnifier.
        // Hide the preview in that case, preserving accurate screen sampling.
        if (safe is not { } bounds) { _preview.IsOpen = false; return; }
        var ownerPoint = _owner.PointFromScreen(new(bounds.X, bounds.Y));
        _preview.HorizontalOffset = ownerPoint.X; _preview.VerticalOffset = ownerPoint.Y;
        // Relative Popup placement may be automatically pushed into the cursor
        // at a monitor edge. Set its final native position in physical pixels.
        Native.SetWindowPos(source.Handle, IntPtr.Zero, bounds.X, bounds.Y, 0, 0, 0x0015);
        var oldBounds = System.Drawing.Rectangle.FromLTRB(previous.Left, previous.Top, previous.Right, previous.Bottom);
        if (oldBounds.IntersectsWith(sample)) DwmFlush();
    }
    internal Color SampleCanvas(Point sourcePoint)
    {
        if (_pixels == null) throw new InvalidOperationException("画布吸管尚未启动。");
        int x = Math.Clamp((int)Math.Floor(sourcePoint.X), 0, _width - 1), y = Math.Clamp((int)Math.Floor(sourcePoint.Y), 0, _height - 1), index = (y * _width + x) * 4;
        return Color.FromRgb(_pixels[index + 2], _pixels[index + 1], _pixels[index]);
    }
    private void UpdateCanvas(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X >= _width || point.Y >= _height) { _value = null; _preview.IsOpen = false; return; }
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y); var magnified = new byte[9 * 9 * 4];
        for (int py = 0; py < 9; py++) for (int px = 0; px < 9; px++)
        {
            int source = (Math.Clamp(y + py - 4, 0, _height - 1) * _width + Math.Clamp(x + px - 4, 0, _width - 1)) * 4, target = (py * 9 + px) * 4;
            Array.Copy(_pixels!, source, magnified, target, 4); magnified[target + 3] = 255;
        }
        Show(SampleCanvas(point), magnified, _surface.TranslatePoint(point, _owner));
    }
    private void UpdateScreen()
    {
        if (!Active || !_screen) return;
        if (!GetPhysicalCursorPos(out var cursor)) { Fail("系统无法读取屏幕坐标。画布吸管仍可使用。"); return; }
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).Bounds;
        // Move an existing magnifier before reading pixels if the pointer has
        // entered it; the preview must never feed its own pixels back to GDI.
        if (_preview.IsOpen) PositionScreenPreview(cursor, screen);
        var pixels = new byte[9 * 9 * 4]; IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) { Fail("系统或权限不支持屏幕取色。画布吸管仍可使用。"); return; }
        try
        {
            for (int y = 0; y < 9; y++) for (int x = 0; x < 9; x++)
            {
                uint color = GetPixel(dc, Math.Clamp(cursor.X + x - 4, screen.Left, screen.Right - 1), Math.Clamp(cursor.Y + y - 4, screen.Top, screen.Bottom - 1));
                if (color == uint.MaxValue) { Fail("该屏幕画面无法读取，可能受到保护。请使用画布吸管。"); return; }
                int p = (y * 9 + x) * 4; pixels[p] = (byte)(color >> 16); pixels[p + 1] = (byte)(color >> 8); pixels[p + 2] = (byte)color; pixels[p + 3] = 255;
            }
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        int center = (4 * 9 + 4) * 4;
        Show(Color.FromRgb(pixels[center + 2], pixels[center + 1], pixels[center]), pixels, _owner.PointFromScreen(new(cursor.X, cursor.Y)));
    }
    private void Show(Color color, byte[] pixels, Point ownerPoint)
    {
        _value = color; _sample.Background = new SolidColorBrush(color); _hex.Text = AnnotationColors.Hex(color);
        var bitmap = BitmapSource.Create(9, 9, 96, 96, PixelFormats.Bgra32, null, pixels, 36); bitmap.Freeze(); _magnified.Source = bitmap; PositionPreview(ownerPoint);
    }
    private void Move(object sender, MouseEventArgs e) { if (!Active) return; e.Handled = true; if (_screen) UpdateScreen(); else UpdateCanvas(e.GetPosition(_surface)); }
    private void Click(object sender, MouseButtonEventArgs e)
    {
        if (!Active) return; e.Handled = true;
        if (_screen) UpdateScreen(); else UpdateCanvas(e.GetPosition(_surface));
        if (Active && _value is { } color) Finish(color);
    }
    private void RightClick(object sender, MouseButtonEventArgs e) { if (Active) { e.Handled = true; Cancel(); } }
    private void Key(object sender, KeyEventArgs e) { if (!Active) return; e.Handled = true; if (e.Key == System.Windows.Input.Key.Escape) Cancel(); }
    private void Deactivated(object? sender, EventArgs e) { if (Active) Cancel(); }
    private void Fail(string message) { Finish(null); _failed(message); }
    internal void Confirm() { if (Active && _value is { } value) Finish(value); }
    internal void Cancel() { if (Active) Finish(null); }
    private void Finish(Color? color)
    {
        Active = false; _timer.Stop(); _preview.IsOpen = false; _pixels = null; _surface.ShowSelection = true; _surface.InvalidateVisual();
        if (Mouse.Captured == _owner) Mouse.Capture(null); Mouse.OverrideCursor = null; _complete(color);
    }
    public void Dispose()
    {
        Cancel(); _owner.PreviewMouseMove -= Move; _owner.PreviewMouseLeftButtonDown -= Click; _owner.PreviewMouseRightButtonDown -= RightClick; _owner.PreviewKeyDown -= Key; _owner.Deactivated -= Deactivated;
    }
}
