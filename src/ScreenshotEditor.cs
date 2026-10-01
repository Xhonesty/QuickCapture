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
        _owner.PreviewKeyDown += KeyDown;
    }
    public WrapPanel CreateToolbar(Action? reselect = null)
    {
        var panel = new WrapPanel();
        Button Add(string text, Action action)
        {
            var button = Ui.Button(text, action); button.Padding = new Thickness(9, 8, 9, 8);
            button.Margin = new Thickness(0, 2, 4, 2); button.FontSize = 12; panel.Children.Add(button); return button;
        }
        foreach (var (label, tool) in new[] { ("箭头", AnnotationTool.Arrow), ("矩形", AnnotationTool.Rectangle), ("文字", AnnotationTool.Text), ("马赛克", AnnotationTool.Mosaic) })
            _toolButtons[tool] = Add(label, () => SelectTool(tool));
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
        Tool = tool;
        foreach (var (key, button) in _toolButtons)
        {
            button.SetResourceReference(Control.BackgroundProperty, key == tool ? "Accent" : "ButtonBackground");
            button.SetResourceReference(Control.ForegroundProperty, key == tool ? "AccentForeground" : "TextPrimary");
        }
        ToolChanged?.Invoke();
    }
    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, Surface.Width), Math.Clamp(p.Y, 0, Surface.Height));
    private void BeginAnnotation(object sender, MouseButtonEventArgs e)
    {
        if (_finishing) return;
        e.Handled = true; Surface.Focus(); var point = Clamp(e.GetPosition(Surface));
        if (Tool == AnnotationTool.Text)
        {
            var text = PromptWindow.Ask(_owner, "文字标注", "输入文字（可换行）", "");
            if (!string.IsNullOrWhiteSpace(text)) Surface.Add(new(Tool, point, point, _color, text));
            return;
        }
        _start = point; Surface.CaptureMouse();
    }
    private void UpdateAnnotation(object sender, MouseEventArgs e)
    {
        if (_start is not { } start) return;
        Surface.Preview = new(Tool, start, Clamp(e.GetPosition(Surface)), _color); Surface.InvalidateVisual(); e.Handled = true;
    }
    private void EndAnnotation(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start) return;
        var end = Clamp(e.GetPosition(Surface)); _start = null; Surface.ReleaseMouseCapture(); e.Handled = true;
        if ((end - start).Length > 2) Surface.Add(new(Tool, start, end, _color));
        else { Surface.Preview = null; Surface.InvalidateVisual(); }
    }
    private async void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await CopyAndCompleteAsync(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Z: Surface.Undo(); e.Handled = true; break;
                case Key.Y: Surface.Redo(); e.Handled = true; break;
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
        if (Surface.IsMouseCaptured) Surface.ReleaseMouseCapture();
    }
}
