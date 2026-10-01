using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal sealed class EditorWindow : Window
{
    private readonly AnnotationSurface _surface;
    private readonly Settings _settings;
    private readonly Action<string> _saved;
    private AnnotationTool _tool = AnnotationTool.Arrow;
    private Color _color = Color.FromRgb(255, 99, 115);
    private Point? _start;
    private string? _lastSave;
    private readonly TextBlock _hint = new() { Foreground = new SolidColorBrush(Color.FromRgb(159, 177, 195)), Margin = new Thickness(16, 10, 16, 10) };
    public EditorWindow(BitmapSource image, Settings settings, Action<string> saved)
    {
        Ui.Theme(this);
        _settings = settings; _saved = saved;
        Title = $"轻截 · 标注截图 · {image.PixelWidth} × {image.PixelHeight}";
        Width = Math.Min(1180, SystemParameters.WorkArea.Width - 60); Height = Math.Min(850, SystemParameters.WorkArea.Height - 60);
        MinWidth = 640; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _surface = new AnnotationSurface(image);
        var root = new DockPanel();
        var toolbar = new WrapPanel { Margin = new Thickness(12) }; DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        foreach (var (label, tool) in new[] { ("箭头", AnnotationTool.Arrow), ("矩形", AnnotationTool.Rectangle), ("文字", AnnotationTool.Text), ("马赛克", AnnotationTool.Mosaic) })
        {
            var button = Ui.Button(label, () => { _tool = tool; UpdateHint(); }); toolbar.Children.Add(button);
        }
        var colors = new ComboBox { Width = 85, MinWidth = 85, Margin = new Thickness(0, 0, 10, 0) };
        colors.ItemsSource = new[] { "红色", "绿色", "黄色", "白色" }; colors.SelectedIndex = 0;
        colors.SelectionChanged += (_, _) => _color = new[] { Color.FromRgb(255, 99, 115), Color.FromRgb(130, 226, 187), Colors.Gold, Colors.White }[colors.SelectedIndex]; toolbar.Children.Add(colors);
        toolbar.Children.Add(Ui.Button("撤销", () => { _surface.Undo(); _lastSave = null; }));
        toolbar.Children.Add(Ui.Button("重做", () => { _surface.Redo(); _lastSave = null; }));
        toolbar.Children.Add(Ui.Button("贴图", () => new PinWindow(_surface.Export()).Show()));
        toolbar.Children.Add(Ui.Button("保存", () => SaveAndClose()));
        var copy = Ui.Button("复制  ↵", async () => await CopyAndCloseAsync()); copy.Background = (Brush)FindResource("Accent"); copy.Foreground = Brushes.Black; toolbar.Children.Add(copy);
        var bottom = new DockPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var zoom = new Slider { Minimum = 0.25, Maximum = 2, Value = Math.Min(1, Math.Min((Width - 50) / image.PixelWidth, (Height - 160) / image.PixelHeight)), Width = 140, Margin = new Thickness(12), ToolTip = "缩放预览（导出保持原始像素）" };
        DockPanel.SetDock(zoom, Dock.Right); bottom.Children.Add(zoom); bottom.Children.Add(_hint);
        var scale = new ScaleTransform(zoom.Value, zoom.Value); _surface.LayoutTransform = scale;
        zoom.ValueChanged += (_, _) => { scale.ScaleX = zoom.Value; scale.ScaleY = zoom.Value; };
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(9, 13, 18)), Content = _surface };
        root.Children.Add(scroll); Content = root;
        _surface.MouseLeftButtonDown += (_, e) =>
        {
            var point = Clamp(e.GetPosition(_surface));
            if (_tool == AnnotationTool.Text)
            {
                var text = PromptWindow.Ask(this, "文字标注", "输入文字（可换行）", "");
                if (!string.IsNullOrWhiteSpace(text)) { _surface.Add(new(_tool, point, point, _color, text)); _lastSave = null; }
                return;
            }
            _start = point; _surface.CaptureMouse();
        };
        _surface.MouseMove += (_, e) =>
        {
            if (_start is not { } start) return;
            _surface.Preview = new(_tool, start, Clamp(e.GetPosition(_surface)), _color); _surface.InvalidateVisual();
        };
        _surface.MouseLeftButtonUp += (_, e) =>
        {
            if (_start is not { } start) return;
            var end = Clamp(e.GetPosition(_surface)); _start = null; _surface.ReleaseMouseCapture();
            if ((end - start).Length > 2) { _surface.Add(new(_tool, start, end, _color)); _lastSave = null; }
            else { _surface.Preview = null; _surface.InvalidateVisual(); }
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { e.Handled = true; await CopyAndCloseAsync(); }
            else if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.Z) { _surface.Undo(); _lastSave = null; e.Handled = true; }
                if (e.Key == Key.Y) { _surface.Redo(); _lastSave = null; e.Handled = true; }
                if (e.Key == Key.S) { SaveAndClose(); e.Handled = true; }
                if (e.Key == Key.C) { e.Handled = true; await CopyAndCloseAsync(); }
            }
        };
        UpdateHint();
    }
    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _surface.Width), Math.Clamp(p.Y, 0, _surface.Height));
    private void UpdateHint() => _hint.Text = $"当前：{new[] { "箭头", "矩形", "文字", "马赛克" }[(int)_tool]}   ·   Ctrl+Z 撤销   ·   Ctrl+S 保存   ·   Enter 复制   ·   Esc 取消";
    private string Save()
    {
        if (_lastSave != null) return _lastSave;
        string path = Paths.NewCapture(_settings.OutputDirectory, "png");
        CaptureService.Save(_surface.Export(), path); _lastSave = path; _saved(path); return path;
    }
    private void SaveAndClose()
    {
        try { Save(); Close(); } catch (Exception ex) { Ui.Error(this, ex); }
    }
    private async Task CopyAndCloseAsync()
    {
        try
        {
            var bitmap = _surface.Export();
            for (int attempt = 0; ; attempt++)
            {
                try { Clipboard.SetImage(bitmap); break; }
                catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { await Task.Delay(80); }
            }
            if (_settings.AutoSaveScreenshot) Save();
            Close();
        }
        catch (Exception ex) { Ui.Error(this, ex); }
    }
}

internal sealed class PinWindow : Window
{
    public PinWindow(BitmapSource image)
    {
        Ui.Theme(this);
        Title = "轻截 · 贴图"; Topmost = true; ShowInTaskbar = false;
        WindowStyle = WindowStyle.ToolWindow; Width = Math.Min(image.PixelWidth + 20, 650); Height = Math.Min(image.PixelHeight + 42, 500);
        Content = new Image { Source = image, Stretch = Stretch.Uniform, ToolTip = "拖动移动 · 右键或 Esc 关闭" };
        MouseLeftButtonDown += (_, _) => DragMove(); MouseRightButtonDown += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
