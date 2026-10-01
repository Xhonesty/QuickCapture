using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace QuickCapture;

internal sealed class SelectionWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection = new() { Stroke = new SolidColorBrush(Color.FromRgb(130, 226, 187)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(25, 130, 226, 187)) };
    private readonly TextBlock _size = new() { Background = new SolidColorBrush(Color.FromRgb(18, 23, 30)), Padding = new Thickness(8, 4, 8, 4) };
    private readonly Forms.Screen _screen;
    private readonly Action<SelectionResult?> _finish;
    private Point? _start;
    public SelectionWindow(Forms.Screen screen, BitmapSource image, bool recording, Action<SelectionResult?> finish)
    {
        Ui.Theme(this);
        _screen = screen; _finish = finish;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Cursor = Cursors.Cross; Width = screen.Bounds.Width; Height = screen.Bounds.Height;
        var root = new Grid();
        root.Children.Add(new Image { Source = image, Stretch = Stretch.Fill });
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(115, 0, 0, 0)) });
        root.Children.Add(_canvas);
        var tip = new Border { Background = new SolidColorBrush(Color.FromRgb(18, 23, 30)), CornerRadius = new CornerRadius(8), Padding = new Thickness(20, 12, 20, 12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 0, 0), IsHitTestVisible = false };
        tip.Child = new TextBlock { Text = recording ? "拖动选择录屏区域 · 松开开始 · Esc 取消" : "拖动选择截图区域 · 松开进入标注 · Esc 取消", FontSize = 15 };
        root.Children.Add(tip); Content = root;
        Loaded += (_, _) => { Native.Place(this, screen.Bounds); Activate(); Focus(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _finish(null); e.Handled = true; } };
        MouseLeftButtonDown += (_, e) =>
        {
            _start = e.GetPosition(_canvas); CaptureMouse();
            if (!_canvas.Children.Contains(_selection)) { _canvas.Children.Add(_selection); _canvas.Children.Add(_size); }
            Update(e.GetPosition(_canvas));
        };
        MouseMove += (_, e) => { if (_start != null) Update(e.GetPosition(_canvas)); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_start is not { } start) return;
            ReleaseMouseCapture(); _start = null;
            var end = e.GetPosition(_canvas);
            var region = RegionFromPoints(start, end);
            var rect = region.Rectangle;
            if (rect.Width < 4 || rect.Height < 4) return;
            var crop = new CroppedBitmap(image, new Int32Rect(rect.X - screen.Bounds.X, rect.Y - screen.Bounds.Y, rect.Width, rect.Height)); crop.Freeze();
            _finish(new(region, crop));
        };
    }
    internal SavedRegion RegionFromPoints(Point start, Point end)
    {
        var a = PointToScreen(start); var b = PointToScreen(new Point(Math.Clamp(end.X, 0, ActualWidth), Math.Clamp(end.Y, 0, ActualHeight)));
        int left = (int)Math.Round(Math.Min(a.X, b.X)), top = (int)Math.Round(Math.Min(a.Y, b.Y));
        int width = (int)Math.Round(Math.Abs(a.X - b.X)), height = (int)Math.Round(Math.Abs(a.Y - b.Y));
        var rect = Drawing.Rectangle.Intersect(new(left, top, width, height), _screen.Bounds);
        return new(rect.X, rect.Y, rect.Width, rect.Height, _screen.DeviceName);
    }
    private void Update(Point end)
    {
        if (_start is not { } start) return;
        end = new Point(Math.Clamp(end.X, 0, ActualWidth), Math.Clamp(end.Y, 0, ActualHeight));
        double x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y), w = Math.Abs(start.X - end.X), h = Math.Abs(start.Y - end.Y);
        Canvas.SetLeft(_selection, x); Canvas.SetTop(_selection, y); _selection.Width = w; _selection.Height = h;
        var scale = VisualTreeHelper.GetDpi(this);
        _size.Text = $"{Math.Round(w * scale.DpiScaleX)} × {Math.Round(h * scale.DpiScaleY)} px";
        Canvas.SetLeft(_size, Math.Max(0, Math.Min(x, ActualWidth - 170))); Canvas.SetTop(_size, Math.Max(0, y - 32));
    }
    public static async Task<SelectionResult?> SelectAsync(bool recording)
    {
        var task = new TaskCompletionSource<SelectionResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var screens = Forms.Screen.AllScreens;
        var images = new List<BitmapSource>();
        foreach (var screen in screens) images.Add(await CaptureService.CaptureAsync(screen.Bounds));
        var windows = new List<SelectionWindow>(); bool finished = false;
        void Finish(SelectionResult? result)
        {
            if (finished) return; finished = true;
            foreach (var window in windows) window.Close();
            task.TrySetResult(result);
        }
        for (int i = 0; i < screens.Length; i++)
        {
            var window = new SelectionWindow(screens[i], images[i], recording, Finish);
            windows.Add(window); window.Show();
        }
        return await task.Task;
    }
}

internal sealed record SelectionResult(SavedRegion Region, BitmapSource Image);
