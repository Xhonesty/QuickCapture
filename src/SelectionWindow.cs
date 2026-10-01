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
    private readonly Rectangle _selection = new() { StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Hidden };
    private readonly TextBlock _size = new() { Padding = new Thickness(8, 4, 8, 4), IsHitTestVisible = false, Visibility = Visibility.Hidden };
    private readonly SelectionMask _mask = new();
    private readonly Border _tip;
    private readonly Forms.Screen _screen;
    private readonly BitmapSource _image;
    private readonly bool _recording;
    private readonly Settings? _settings;
    private readonly Action<string>? _saved;
    private readonly Action<SelectionResult?> _finish;
    private readonly Action<SelectionWindow, bool>? _modeChanged;
    private readonly IReadOnlyList<WindowTarget> _snapTargets;
    private readonly TextBlock _hint;
    private SavedRegion? _hoverRegion, _pressedWindow;
    private bool _dragging;
    private Point? _start;
    private Border? _toolbar;
    private bool _locked, _closed;
    internal ScreenshotEditor? Editor { get; private set; }
    internal Rect SelectionBounds { get; private set; }
    internal Rect ToolbarBounds { get; private set; }
    internal SavedRegion? HoveredRegion => _hoverRegion;

    public SelectionWindow(Forms.Screen screen, BitmapSource image, bool recording, Action<SelectionResult?> finish,
        Settings? settings = null, Action<string>? saved = null, Action<SelectionWindow, bool>? modeChanged = null, IReadOnlyList<WindowTarget>? snapTargets = null)
    {
        Ui.Theme(this);
        _screen = screen; _image = image; _recording = recording; _finish = finish;
        _settings = settings; _saved = saved; _modeChanged = modeChanged;
        _snapTargets = snapTargets ?? Array.Empty<WindowTarget>();
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Cursor = Cursors.Cross; Width = screen.Bounds.Width; Height = screen.Bounds.Height;
        var root = new Grid();
        root.Children.Add(new Image { Source = image, Stretch = Stretch.Fill });
        root.Children.Add(_mask); root.Children.Add(_canvas);
        _selection.SetResourceReference(Shape.StrokeProperty, "SelectionBrush");
        _size.SetResourceReference(TextBlock.BackgroundProperty, "PanelBackground");
        _size.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _canvas.Children.Add(_selection); _canvas.Children.Add(_size);
        _tip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(20, 12, 20, 12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 0, 0), IsHitTestVisible = false };
        _tip.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        _hint = new TextBlock { Text = SelectionHint, FontSize = 15 }; _tip.Child = _hint;
        root.Children.Add(_tip); Content = root;
        Loaded += (_, _) => { Native.Place(this, screen.Bounds); Activate(); Focus(); UpdateWindowHover(PointFromScreen(new Point(Forms.Cursor.Position.X, Forms.Cursor.Position.Y))); };
        SizeChanged += (_, _) => PositionToolbar();
        Closed += (_, _) => { _closed = true; Editor?.Dispose(); _finish(null); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _finish(null); e.Handled = true; } };
        MouseLeftButtonDown += (_, e) =>
        {
            BeginSelection(e.GetPosition(_canvas));
            if (_start != null) CaptureMouse();
        };
        MouseMove += (_, e) => MoveSelection(e.GetPosition(_canvas));
        MouseLeftButtonUp += (_, e) => FinishSelection(e.GetPosition(_canvas));
    }
    private string SelectionHint => _recording ? "拖动选择录屏区域 · 松开开始 · Esc 取消" :
        _settings?.SnapToWindow == true ? "悬停吸附窗口 · 单击确认 · 拖动自由框选 · Esc 取消" : "拖动框选 · 原位标注 · Enter 复制 · Esc 取消";
    internal void UpdateWindowHover(Point point)
    {
        if (_locked || Editor != null || _start != null) return;
        var physical = PointToScreen(point);
        _hoverRegion = !_recording && _settings?.SnapToWindow == true
            ? WindowSnapper.Find(new Drawing.Point((int)Math.Round(physical.X), (int)Math.Round(physical.Y)), _screen.Bounds, _screen.DeviceName, _snapTargets) : null;
        if (_hoverRegion is { } region)
        {
            var dpi = VisualTreeHelper.GetDpi(this); var origin = PointFromScreen(new Point(region.X, region.Y));
            SetSelection(new Rect(origin, new Size(region.Width / dpi.DpiScaleX, region.Height / dpi.DpiScaleY)));
            _size.Text = $"{region.Width} × {region.Height} px";
        }
        else { _mask.Selection = null; _selection.Visibility = _size.Visibility = Visibility.Hidden; }
    }
    internal void BeginSelection(Point point)
    {
        if (_locked || Editor != null) return;
        UpdateWindowHover(point); _pressedWindow = _hoverRegion;
        _start = point; _dragging = false;
        if (_pressedWindow == null) Update(point);
    }
    internal void MoveSelection(Point point)
    {
        if (_start is not { } start) { UpdateWindowHover(point); return; }
        if ((point - start).Length >= 4) _dragging = true;
        if (_dragging || _pressedWindow == null) Update(point);
    }
    internal void FinishSelection(Point point)
    {
        if (_start is not { } start) return;
        var region = !_dragging && (point - start).Length < 4 && _pressedWindow != null ? _pressedWindow : RegionFromPoints(start, point);
        _start = null; _pressedWindow = null; _hoverRegion = null; ReleaseMouseCapture();
        if (region.Width < 4 || region.Height < 4) { UpdateWindowHover(point); return; }
        if (_recording || _settings == null) _finish(new(region, Crop(region)));
        else BeginEditing(region);
    }
    private BitmapSource Crop(SavedRegion region)
    {
        var crop = new CroppedBitmap(_image, new Int32Rect(region.X - _screen.Bounds.X, region.Y - _screen.Bounds.Y, region.Width, region.Height)); crop.Freeze(); return crop;
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
        var bounds = new Rect(start, end); SetSelection(bounds);
        var dpi = VisualTreeHelper.GetDpi(this);
        _size.Text = $"{Math.Round(bounds.Width * dpi.DpiScaleX)} × {Math.Round(bounds.Height * dpi.DpiScaleY)} px";
    }
    private void SetSelection(Rect bounds)
    {
        SelectionBounds = bounds; _mask.Selection = bounds;
        _selection.Visibility = _size.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selection, bounds.Left); Canvas.SetTop(_selection, bounds.Top); _selection.Width = bounds.Width; _selection.Height = bounds.Height;
        Canvas.SetLeft(_size, Math.Clamp(bounds.Left, 0, Math.Max(0, ActualWidth - 170)));
        Canvas.SetTop(_size, bounds.Top >= 32 ? bounds.Top - 32 : bounds.Top + 4);
    }
    internal void BeginEditing(SavedRegion region)
    {
        if (region.DeviceName != _screen.DeviceName || !_screen.Bounds.Contains(region.Rectangle) || region.Width < 4 || region.Height < 4)
            throw new ArgumentException("截图区域不在此显示器内。");
        RemoveEditor();
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = PointFromScreen(new Point(region.X, region.Y));
        SetSelection(new Rect(origin, new Size(region.Width / dpi.DpiScaleX, region.Height / dpi.DpiScaleY)));
        _size.Text = $"{region.Width} × {region.Height} px";
        _tip.Visibility = Visibility.Collapsed; Cursor = Cursors.Arrow;
        Editor = new ScreenshotEditor(this, Crop(region), _settings ?? new Settings(), _saved ?? (_ => { }), () => _finish(null));
        // WPF uses DIPs, annotations use source pixels: cancel the monitor scale
        // so the selected image remains at exactly its original screen location.
        Editor.Surface.LayoutTransform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
        Canvas.SetLeft(Editor.Surface, origin.X); Canvas.SetTop(Editor.Surface, origin.Y);
        _canvas.Children.Insert(0, Editor.Surface);
        _toolbar = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 5, 4, 5), BorderThickness = new Thickness(1), Child = Editor.CreateToolbar(Reselect), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.25 } };
        _toolbar.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); _toolbar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _canvas.Children.Add(_toolbar); PositionToolbar();
        Editor.Surface.Focus(); _modeChanged?.Invoke(this, true);
    }
    private void PositionToolbar()
    {
        if (_toolbar == null || ActualWidth <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = _screen.WorkingArea;
        var available = new Rect((work.X - _screen.Bounds.X) / dpi.DpiScaleX + 8, (work.Y - _screen.Bounds.Y) / dpi.DpiScaleY + 8,
            Math.Max(1, work.Width / dpi.DpiScaleX - 16), Math.Max(1, work.Height / dpi.DpiScaleY - 16));
        _toolbar.MaxWidth = Math.Min(930, available.Width);
        _toolbar.Measure(new Size(_toolbar.MaxWidth, double.PositiveInfinity));
        var size = _toolbar.DesiredSize;
        var point = ToolbarPlacement.Place(SelectionBounds, size, available);
        Canvas.SetLeft(_toolbar, point.X); Canvas.SetTop(_toolbar, point.Y); ToolbarBounds = new(point, size);
    }
    private void RemoveEditor()
    {
        if (Editor != null) { Editor.Dispose(); _canvas.Children.Remove(Editor.Surface); Editor = null; }
        if (_toolbar != null) { _canvas.Children.Remove(_toolbar); _toolbar = null; }
    }
    internal void Reselect()
    {
        RemoveEditor(); _hoverRegion = null; _pressedWindow = null; _mask.Selection = null; _selection.Visibility = _size.Visibility = Visibility.Hidden;
        _tip.Visibility = Visibility.Visible; Cursor = Cursors.Cross; _modeChanged?.Invoke(this, false); Activate(); Focus();
    }
    public static Task<SelectionResult?> SelectAsync(bool recording) => OpenAsync(recording, null, null);
    public static async Task EditScreenshotAsync(Settings settings, Action<string> saved) => await OpenAsync(false, settings, saved);
    private static async Task<SelectionResult?> OpenAsync(bool recording, Settings? settings, Action<string>? saved)
    {
        var task = new TaskCompletionSource<SelectionResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var screens = Forms.Screen.AllScreens;
        var snapTargets = !recording && settings?.SnapToWindow == true ? Native.SnapTargets() : new List<WindowTarget>();
        var images = new List<BitmapSource>();
        foreach (var screen in screens) images.Add(await CaptureService.CaptureAsync(screen.Bounds));
        var windows = new List<SelectionWindow>(); bool finished = false;
        void Finish(SelectionResult? result)
        {
            if (finished) return; finished = true;
            foreach (var window in windows) if (!window._closed) window.Close();
            task.TrySetResult(result);
        }
        void ModeChanged(SelectionWindow active, bool editing)
        {
            foreach (var window in windows)
            {
                if (window == active || window._closed) continue;
                window._locked = editing;
                if (editing) window.Hide(); else window.Show();
            }
            active.Activate(); active.Focus();
        }
        try
        {
            for (int i = 0; i < screens.Length; i++)
            {
                var window = new SelectionWindow(screens[i], images[i], recording, Finish, settings, saved, ModeChanged, snapTargets);
                windows.Add(window); window.Show();
            }
            return await task.Task;
        }
        finally { finished = true; foreach (var window in windows) if (!window._closed) window.Close(); }
    }
}

internal sealed class SelectionMask : FrameworkElement
{
    private Rect? _selection;
    private readonly Brush _shade = new SolidColorBrush(Color.FromArgb(115, 0, 0, 0));
    public Rect? Selection { get => _selection; set { _selection = value; InvalidateVisual(); } }
    public SelectionMask() { IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        var whole = new Rect(0, 0, ActualWidth, ActualHeight);
        if (_selection is not { } selection) { dc.DrawRectangle(_shade, null, whole); return; }
        var visible = Geometry.Combine(new RectangleGeometry(whole), new RectangleGeometry(selection), GeometryCombineMode.Exclude, null);
        dc.DrawGeometry(_shade, null, visible);
    }
}

internal sealed record SelectionResult(SavedRegion Region, BitmapSource Image);
