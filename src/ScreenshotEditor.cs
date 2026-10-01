using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

// Both the in-place selection and window/full-desktop editor use this session.
internal sealed class ScreenshotEditor : IDisposable
{
    private readonly Window _owner;
    private readonly Settings _settings;
    private readonly Action<string> _saved;
    private readonly Action _complete;
    private readonly Dictionary<AnnotationTool, Button> _toolButtons = new();
    private readonly List<Point> _points = new();
    private readonly Dictionary<AnnotationTool, HoverToolOptions> _options = new();
    private ComboBox? _mosaicMode, _mosaicSize;
    private TextBlock? _mosaicSizeLabel;
    private static readonly int[] BrushSizes = { 3, 6, 12, 24, 48 };
    private int _freehandWidth = 6, _mosaicWidth = 24;
    internal bool MosaicFreehand => _mosaicMode?.SelectedIndex == 1;
    private Point? _start;
    private string? _lastSave;
    private bool _finishing;
    private Color _color = Color.FromRgb(255, 99, 115);
    public AnnotationSurface Surface { get; }
    public AnnotationTool Tool { get; private set; } = AnnotationTool.Arrow;
    public event Action? ToolChanged;

    public ScreenshotEditor(Window owner, BitmapSource image, Settings settings, Action<string> saved, Action complete)
    {
        _owner = owner; _settings = settings; _saved = saved; _complete = complete;
        Surface = new AnnotationSurface(image) { Cursor = Cursors.Cross };
        Surface.Changed += () => _lastSave = null;
        Surface.MouseLeftButtonDown += BeginAnnotation;
        Surface.MouseMove += UpdateAnnotation;
        Surface.MouseLeftButtonUp += EndAnnotation;
        Surface.LostMouseCapture += (_, _) => { if (_start != null) CancelStroke(); };
        _owner.PreviewKeyDown += KeyDown;
        _owner.Deactivated += OwnerDeactivated;
    }
    public WrapPanel CreateToolbar(Action? reselect = null)
    {
        var panel = new WrapPanel();
        Button Add(string text, Action action)
        {
            var button = Ui.Button(text, action); button.Padding = new Thickness(9, 8, 9, 8);
            button.Margin = new Thickness(0, 2, 4, 2); button.FontSize = 12; panel.Children.Add(button); return button;
        }
        foreach (var (label, tool) in new[] { ("箭头", AnnotationTool.Arrow), ("矩形", AnnotationTool.Rectangle), ("文字", AnnotationTool.Text), ("涂鸦", AnnotationTool.Freehand), ("马赛克", AnnotationTool.Mosaic) })
            _toolButtons[tool] = Add(label, () => SelectTool(tool));
        CreateHoverOptions();
        var colors = new ComboBox { Width = 80, MinWidth = 80, Margin = new Thickness(0, 2, 8, 2), ToolTip = "标注颜色", ItemsSource = new[] { "红色", "绿色", "黄色", "白色", "黑色" }, SelectedIndex = 0 };
        colors.SelectionChanged += (_, _) => _color = new[] { Color.FromRgb(255, 99, 115), Color.FromRgb(130, 226, 187), Colors.Gold, Colors.White, Colors.Black }[colors.SelectedIndex]; panel.Children.Add(colors);
        Add("撤销", Surface.Undo).ToolTip = "Ctrl+Z";
        Add("重做", Surface.Redo).ToolTip = "Ctrl+Y";
        Add("贴图", () => { new PinWindow(Surface.Export()).Show(); _complete(); });
        if (reselect != null) Add("重新框选", reselect);
        Add("取消", _complete).ToolTip = "Esc";
        Add("保存", SaveAndComplete).ToolTip = "Ctrl+S";
        var copy = Add("复制 ↵", async () => await CopyAndCompleteAsync());
        copy.SetResourceReference(Control.BackgroundProperty, "Accent"); copy.SetResourceReference(Control.ForegroundProperty, "AccentForeground"); copy.ToolTip = "Enter / Ctrl+C";
        SelectTool(Tool); return panel;
    }
    public void SelectTool(AnnotationTool tool)
    {
        CancelStroke();
        Tool = tool;
        foreach (var (key, button) in _toolButtons)
        {
            button.SetResourceReference(Control.BackgroundProperty, key == tool ? "Accent" : "ButtonBackground");
            button.SetResourceReference(Control.ForegroundProperty, key == tool ? "AccentForeground" : "TextPrimary");
        }
        foreach (var (key, options) in _options) if (key != tool) options.Hide();
        ToolChanged?.Invoke();
    }
    private void CreateHoverOptions()
    {
        TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 0, 0, 7), FontSize = 12 };
        ComboBox Sizes(int width) => new() { Width = 180, ToolTip = "画笔粗细（原始像素）", ItemsSource = new[] { "3 px", "6 px", "12 px", "24 px", "48 px" }, SelectedIndex = Array.IndexOf(BrushSizes, width) };
        var freehand = new StackPanel(); freehand.Children.Add(Label("涂鸦粗细"));
        var freehandSize = Sizes(_freehandWidth); freehand.Children.Add(freehandSize);
        freehandSize.SelectionChanged += (_, _) => { if (freehandSize.SelectedIndex >= 0) { _freehandWidth = BrushSizes[freehandSize.SelectedIndex]; SelectTool(AnnotationTool.Freehand); } };
        AddOptions(AnnotationTool.Freehand, freehand);
        var mosaic = new StackPanel(); mosaic.Children.Add(Label("马赛克模式"));
        _mosaicMode = new ComboBox { Width = 180, ToolTip = "马赛克模式", ItemsSource = new[] { "框选马赛克", "涂鸦马赛克" }, SelectedIndex = 0 }; mosaic.Children.Add(_mosaicMode);
        _mosaicSizeLabel = Label("画笔粗细"); _mosaicSizeLabel.Margin = new Thickness(0, 12, 0, 7); mosaic.Children.Add(_mosaicSizeLabel);
        _mosaicSize = Sizes(_mosaicWidth); mosaic.Children.Add(_mosaicSize);
        _mosaicMode.SelectionChanged += (_, _) => { UpdateMosaicSize(); SelectTool(AnnotationTool.Mosaic); };
        _mosaicSize.SelectionChanged += (_, _) => { if (_mosaicSize.SelectedIndex >= 0) { _mosaicWidth = BrushSizes[_mosaicSize.SelectedIndex]; SelectTool(AnnotationTool.Mosaic); } };
        UpdateMosaicSize(); AddOptions(AnnotationTool.Mosaic, mosaic);
        void AddOptions(AnnotationTool tool, StackPanel content) => _options[tool] = new(_toolButtons[tool], content, () =>
        { foreach (var (other, options) in _options) if (other != tool) options.Hide(); });
    }
    private void UpdateMosaicSize()
    {
        if (_mosaicSize == null || _mosaicSizeLabel == null) return;
        _mosaicSize.Visibility = _mosaicSizeLabel.Visibility = MosaicFreehand ? Visibility.Visible : Visibility.Collapsed;
    }
    internal HoverToolOptions OptionsFor(AnnotationTool tool) => _options[tool];
    private void HideOptions() { foreach (var options in _options.Values) options.Hide(); }
    private void OwnerDeactivated(object? sender, EventArgs e) => HideOptions();
    private bool IsBrush => Tool == AnnotationTool.Freehand || Tool == AnnotationTool.Mosaic && MosaicFreehand;
    private Annotation Stroke(Point start, Point end) => new(Tool == AnnotationTool.Mosaic && MosaicFreehand ? AnnotationTool.MosaicBrush : Tool,
        start, end, _color, Points: IsBrush ? _points.ToArray() : null, Width: IsBrush ? (Tool == AnnotationTool.Freehand ? _freehandWidth : _mosaicWidth) : 3);
    private void CancelStroke()
    {
        _start = null; _points.Clear(); Surface.Preview = null; Surface.InvalidateVisual();
        if (Surface.IsMouseCaptured) Surface.ReleaseMouseCapture();
    }
    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, Surface.Width), Math.Clamp(p.Y, 0, Surface.Height));
    private void BeginAnnotation(object sender, MouseButtonEventArgs e)
    {
        if (_finishing) return;
        HideOptions();
        e.Handled = true; Surface.Focus(); var point = Clamp(e.GetPosition(Surface));
        if (Tool == AnnotationTool.Text)
        {
            var text = PromptWindow.Ask(_owner, "文字标注", "输入文字（可换行）", "");
            if (!string.IsNullOrWhiteSpace(text)) Surface.Add(new(Tool, point, point, _color, text));
            return;
        }
        _start = point; _points.Clear(); if (IsBrush) _points.Add(point);
        Surface.CaptureMouse(); Surface.Preview = Stroke(point, point); Surface.InvalidateVisual();
    }
    private void UpdateAnnotation(object sender, MouseEventArgs e)
    {
        if (_start is not { } start) return;
        var point = Clamp(e.GetPosition(Surface));
        if (IsBrush && (_points[^1] - point).Length >= 0.75) _points.Add(point);
        Surface.Preview = Stroke(start, point); Surface.InvalidateVisual(); e.Handled = true;
    }
    private void EndAnnotation(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start) return;
        var end = Clamp(e.GetPosition(Surface)); if (IsBrush && (_points[^1] - end).Length >= 0.75) _points.Add(end);
        var annotation = Stroke(start, end); _start = null; Surface.ReleaseMouseCapture(); e.Handled = true;
        if (IsBrush || (end - start).Length > 2) Surface.Add(annotation);
        else { Surface.Preview = null; Surface.InvalidateVisual(); }
        _points.Clear();
    }
    private async void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await CopyAndCompleteAsync(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Z: CancelStroke(); Surface.Undo(); e.Handled = true; break;
                case Key.Y: CancelStroke(); Surface.Redo(); e.Handled = true; break;
                case Key.S: e.Handled = true; SaveAndComplete(); break;
                case Key.C: e.Handled = true; await CopyAndCompleteAsync(); break;
            }
        }
    }
    internal string SaveImage()
    {
        if (_lastSave != null) return _lastSave;
        string path = Paths.NewCapture(_settings.OutputDirectory, "png");
        CaptureService.Save(Surface.Export(), path); _lastSave = path; _saved(path); return path;
    }
    private void SaveAndComplete()
    {
        if (_finishing) return; _finishing = true;
        try { SaveImage(); _complete(); }
        catch (Exception ex) { _finishing = false; Ui.Error(_owner, ex); }
    }
    private async Task CopyAndCompleteAsync()
    {
        if (_finishing) return; _finishing = true;
        try
        {
            var bitmap = Surface.Export();
            for (int attempt = 0; ; attempt++)
            {
                try { Clipboard.SetImage(bitmap); break; }
                catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { await Task.Delay(80); }
            }
            if (_settings.AutoSaveScreenshot) SaveImage();
            _complete();
        }
        catch (Exception ex) { _finishing = false; Ui.Error(_owner, ex); }
    }
    public void Dispose()
    {
        _owner.PreviewKeyDown -= KeyDown;
        _owner.Deactivated -= OwnerDeactivated;
        foreach (var options in _options.Values) options.Dispose(); _options.Clear();
        CancelStroke();
    }
}
