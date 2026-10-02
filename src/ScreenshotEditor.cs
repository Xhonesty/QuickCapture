using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
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
    private readonly bool _inputMethodEnabled;
    private readonly Dictionary<AnnotationTool, Button> _toolButtons = new();
    private readonly List<Point> _points = new();
    private readonly Dictionary<AnnotationTool, HoverToolOptions> _options = new();
    private readonly Dictionary<string, HoverToolOptions> _flyouts = new();
    private ComboBox? _mosaicMode, _mosaicSize;
    private TextBlock? _mosaicSizeLabel;
    private static readonly int[] BrushSizes = { 3, 6, 12, 24, 48 };
    private int _freehandWidth = 6, _mosaicWidth = 24;
    internal bool MosaicFreehand => _mosaicMode?.SelectedIndex == 1;
    private Point? _start;
    private string? _lastSave;
    private (ScreenshotFormat Format, int Quality) _savedSettings;
    private bool _finishing;
    private IReadOnlyList<ToolDefinition> _definitions = Array.Empty<ToolDefinition>();
    private readonly Dictionary<string, Button> _buttons = new();
    private Int32Rect? _cropBefore;
    private Point _cropPointer;
    private Vector _cropScale;
    internal CropHandles Handles { get; }
    internal Button ButtonFor(string id) => _buttons[id];
    private Color _color = Color.FromRgb(255, 99, 115);
    public AnnotationSurface Surface { get; }
    public AnnotationTool Tool { get; private set; } = AnnotationTool.Crop;
    public event Action? ToolChanged;

    public ScreenshotEditor(Window owner, BitmapSource image, Settings settings, Action<string> saved, Action complete, Int32Rect? crop = null)
    {
        _owner = owner; _settings = settings; _saved = saved; _complete = complete;
        _inputMethodEnabled = InputMethod.GetIsInputMethodEnabled(owner);
        // Tool gestures must work with a Chinese IME active. Text annotation
        // uses its own prompt window, where normal text input remains enabled.
        InputMethod.SetIsInputMethodEnabled(owner, false);
        Surface = new AnnotationSurface(image, crop) { Cursor = Cursors.SizeAll };
        Handles = new CropHandles(Surface, owner, () => SelectTool(AnnotationTool.Crop));
        Surface.Changed += () => { _lastSave = null; UpdateButtons(); };
        Surface.MouseLeftButtonDown += BeginAnnotation;
        Surface.MouseMove += UpdateAnnotation;
        Surface.MouseLeftButtonUp += EndAnnotation;
        Surface.LostMouseCapture += (_, _) => { if (_start != null || _cropBefore != null) CancelStroke(); };
        _owner.PreviewKeyDown += KeyDown;
        _owner.Deactivated += OwnerDeactivated;
    }
    public WrapPanel CreateToolbar(Action? reselect = null)
    {
        var panel = new WrapPanel(); _definitions = ToolCatalog.Create(this, reselect);
        string previousGroup = "";
        foreach (var definition in _definitions)
        {
            if (definition.Id == "reselect" && reselect == null) continue;
            if (previousGroup != "" && previousGroup != definition.Group)
            {
                var separator = new Border { Width = 1, Height = 24, Margin = new Thickness(4, 6, 8, 6) }; separator.SetResourceReference(Border.BackgroundProperty, "BorderBrush"); panel.Children.Add(separator);
            }
            previousGroup = definition.Group;
            var button = Ui.Button("", () => { if (definition.CanExecute?.Invoke() != false) definition.Execute(); });
            button.Content = IconSet.Create(definition.Icon); button.Width = button.Height = UiDesign.Number("ControlHeight"); button.Padding = new Thickness(0); button.Margin = new Thickness(0, 0, 4, 0);
            button.Name = "Tool_" + definition.Id; AutomationProperties.SetAutomationId(button, button.Name); AutomationProperties.SetName(button, definition.Name);
            button.ToolTip = $"{definition.Name} ({definition.Shortcut}{(definition.AlternativeKeys == "" ? "" : " / " + definition.AlternativeKeys)})";
            ToolTipService.SetPlacement(button, PlacementMode.Top); _buttons[definition.Id] = button;
            if (definition.Tool is { } tool) _toolButtons[tool] = button;
            panel.Children.Add(button);
        }
        foreach (var definition in _definitions.Where(d => d.Options != null))
        {
            var flyout = new HoverToolOptions(_buttons[definition.Id], definition.Options!(), () =>
            { foreach (var (id, other) in _flyouts) if (id != definition.Id) other.Hide(); });
            _flyouts[definition.Id] = flyout; if (definition.Tool is { } tool) _options[tool] = flyout;
        }
        SelectTool(Tool); UpdateButtons(); return panel;
    }
    internal StackPanel CreateColorOptions()
    {
        var colors = new StackPanel(); colors.Children.Add(UiDesign.Text("标注颜色 (K)", true));
        var choices = new ComboBox { Width = 180, ItemsSource = new[] { "红色", "绿色", "黄色", "白色", "黑色" }, SelectedIndex = 0, ToolTip = "标注颜色" }; colors.Children.Add(choices);
        choices.SelectionChanged += (_, _) => _color = new[] { Color.FromRgb(255, 99, 115), Color.FromRgb(130, 226, 187), Colors.Gold, Colors.White, Colors.Black }[choices.SelectedIndex];
        return colors;
    }
    private void UpdateButtons()
    {
        foreach (var definition in _definitions)
            if (_buttons.TryGetValue(definition.Id, out var button)) button.IsEnabled = !_finishing && definition.CanExecute?.Invoke() != false;
    }
    internal void ShowColors() { if (_flyouts.TryGetValue("color", out var colors)) colors.Show(); }
    internal void Undo() { CancelStroke(); Surface.Undo(); }
    internal void Redo() { CancelStroke(); Surface.Redo(); }
    internal void Pin() { new PinWindow(Surface.Export()).Show(); _complete(); }
    internal void Complete() => _complete();
    public void SelectTool(AnnotationTool tool)
    {
        CancelStroke();
        Tool = tool; Surface.Cursor = tool == AnnotationTool.Crop ? Cursors.SizeAll : Cursors.Cross;
        foreach (var (key, button) in _toolButtons)
        {
            button.SetResourceReference(Control.BackgroundProperty, key == tool ? "Accent" : "ButtonBackground");
            button.SetResourceReference(Control.ForegroundProperty, key == tool ? "AccentForeground" : "TextPrimary");
        }
        foreach (var (key, options) in _options) if (key != tool) options.Hide();
        ToolChanged?.Invoke();
    }
    private static TextBlock OptionLabel(string text) => new() { Text = text, Margin = new Thickness(0, 0, 0, 8), FontSize = UiDesign.Number("FontHelp") };
    private static ComboBox Sizes(int width) => new() { Width = 180, ToolTip = "画笔粗细（原始像素）", ItemsSource = new[] { "3 px", "6 px", "12 px", "24 px", "48 px" }, SelectedIndex = Array.IndexOf(BrushSizes, width) };
    internal StackPanel CreateFreehandOptions()
    {
        var freehand = new StackPanel(); freehand.Children.Add(OptionLabel("涂鸦粗细"));
        var freehandSize = Sizes(_freehandWidth); freehand.Children.Add(freehandSize);
        freehandSize.SelectionChanged += (_, _) => { if (freehandSize.SelectedIndex >= 0) { _freehandWidth = BrushSizes[freehandSize.SelectedIndex]; SelectTool(AnnotationTool.Freehand); } };
        return freehand;
    }
    internal StackPanel CreateMosaicOptions()
    {
        var mosaic = new StackPanel(); mosaic.Children.Add(OptionLabel("马赛克模式"));
        _mosaicMode = new ComboBox { Width = 180, ToolTip = "马赛克模式", ItemsSource = new[] { "框选马赛克", "涂鸦马赛克" }, SelectedIndex = 0 }; mosaic.Children.Add(_mosaicMode);
        _mosaicSizeLabel = OptionLabel("画笔粗细"); _mosaicSizeLabel.Margin = new Thickness(0, 12, 0, 8); mosaic.Children.Add(_mosaicSizeLabel);
        _mosaicSize = Sizes(_mosaicWidth); mosaic.Children.Add(_mosaicSize);
        _mosaicMode.SelectionChanged += (_, _) => { UpdateMosaicSize(); SelectTool(AnnotationTool.Mosaic); };
        _mosaicSize.SelectionChanged += (_, _) => { if (_mosaicSize.SelectedIndex >= 0) { _mosaicWidth = BrushSizes[_mosaicSize.SelectedIndex]; SelectTool(AnnotationTool.Mosaic); } };
        UpdateMosaicSize(); return mosaic;
    }
    private void UpdateMosaicSize()
    {
        if (_mosaicSize == null || _mosaicSizeLabel == null) return;
        _mosaicSize.Visibility = _mosaicSizeLabel.Visibility = MosaicFreehand ? Visibility.Visible : Visibility.Collapsed;
    }
    internal HoverToolOptions OptionsFor(AnnotationTool tool) => _options[tool];
    private void HideOptions() { foreach (var options in _flyouts.Values) options.Hide(); }
    private void OwnerDeactivated(object? sender, EventArgs e) => HideOptions();
    private bool IsBrush => Tool == AnnotationTool.Freehand || Tool == AnnotationTool.Mosaic && MosaicFreehand;
    private Annotation Stroke(Point start, Point end) => new(Tool == AnnotationTool.Mosaic && MosaicFreehand ? AnnotationTool.MosaicBrush : Tool,
        start, end, _color, Points: IsBrush ? _points.ToArray() : null, Width: IsBrush ? (Tool == AnnotationTool.Freehand ? _freehandWidth : _mosaicWidth) : 3);
    private void CancelStroke()
    {
        _start = null; _points.Clear(); Surface.Preview = null; Surface.InvalidateVisual();
        if (_cropBefore is { } before) { _cropBefore = null; Surface.CommitCrop(before); }
        if (Surface.IsMouseCaptured) Surface.ReleaseMouseCapture();
    }
    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, Surface.Width), Math.Clamp(p.Y, 0, Surface.Height));
    private void BeginAnnotation(object sender, MouseButtonEventArgs e)
    {
        if (_finishing) return;
        HideOptions();
        e.Handled = true; Surface.Focus(); var point = Clamp(e.GetPosition(Surface));
        if (Tool == AnnotationTool.Crop)
        {
            _cropBefore = Surface.CropBounds; _cropPointer = e.GetPosition(_owner); _cropScale = CropHandles.ScaleInOwner(Surface, _owner); Surface.CaptureMouse(); return;
        }
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
        if (_cropBefore is { } crop)
        {
            var delta = e.GetPosition(_owner) - _cropPointer;
            Surface.SetCrop(CropGeometry.Move(crop, (int)Math.Round(delta.X / _cropScale.X), (int)Math.Round(delta.Y / _cropScale.Y), Surface.SourceWidth, Surface.SourceHeight), false); e.Handled = true; return;
        }
        if (_start is not { } start) return;
        var point = Clamp(e.GetPosition(Surface));
        if (IsBrush && (_points[^1] - point).Length >= 0.75) _points.Add(point);
        Surface.Preview = Stroke(start, point); Surface.InvalidateVisual(); e.Handled = true;
    }
    private void EndAnnotation(object sender, MouseButtonEventArgs e)
    {
        if (_cropBefore is { } before) { _cropBefore = null; Surface.CommitCrop(before); Surface.ReleaseMouseCapture(); e.Handled = true; return; }
        if (_start is not { } start) return;
        var end = Clamp(e.GetPosition(Surface)); if (IsBrush && (_points[^1] - end).Length >= 0.75) _points.Add(end);
        var annotation = Stroke(start, end); _start = null; Surface.ReleaseMouseCapture(); e.Handled = true;
        if (IsBrush || (end - start).Length > 2) Surface.Add(annotation);
        else { Surface.Preview = null; Surface.InvalidateVisual(); }
        _points.Clear();
    }
    private async void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || _finishing || Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is ComboBox) return;
        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (Tool == AnnotationTool.Crop && modifiers is ModifierKeys.None or ModifierKeys.Shift && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            int step = modifiers == ModifierKeys.Shift ? 10 : 1;
            Surface.SetCrop(CropGeometry.Move(Surface.CropBounds, key == Key.Left ? -step : key == Key.Right ? step : 0, key == Key.Up ? -step : key == Key.Down ? step : 0, Surface.SourceWidth, Surface.SourceHeight)); e.Handled = true; return;
        }
        if (key == Key.Enter && modifiers == ModifierKeys.None) { e.Handled = true; await CopyAndCompleteAsync(); return; }
        if (modifiers == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.Z: Undo(); e.Handled = true; break;
                case Key.Y: Redo(); e.Handled = true; break;
                case Key.S: e.Handled = true; SaveAndComplete(); break;
                case Key.C: e.Handled = true; await CopyAndCompleteAsync(); break;
            }
            return;
        }
        if (modifiers != ModifierKeys.None) return;
        var definition = _definitions.FirstOrDefault(d => d.Shortcut == key);
        if (definition != null && definition.CanExecute?.Invoke() != false) { e.Handled = true; definition.Execute(); }
    }
    internal string SaveImage()
    {
        if (_lastSave != null && _savedSettings == (_settings.ScreenshotFormat, _settings.ScreenshotQuality)) return _lastSave;
        string path = Paths.NewCapture(_settings.OutputDirectory, ImageExportService.Extension(_settings.ScreenshotFormat));
        ImageExportService.Save(Surface.Export(), path, _settings.ScreenshotFormat, _settings.ScreenshotQuality); _lastSave = path; _savedSettings = (_settings.ScreenshotFormat, _settings.ScreenshotQuality); _saved(path); return path;
    }
    internal void SaveAndComplete()
    {
        if (_finishing) return; _finishing = true; UpdateButtons();
        try
        {
            var dialog = new ScreenshotSaveWindow(_owner, Surface.Export(), _settings);
            if (dialog.ShowDialog() == true) { _saved(dialog.SavedPath!); _complete(); }
            else { _finishing = false; UpdateButtons(); }
        }
        catch (Exception ex) { _finishing = false; UpdateButtons(); Ui.Error(_owner, ex); }
    }
    internal async Task CopyAndCompleteAsync()
    {
        if (_finishing) return; _finishing = true;
        try
        {
            var bitmap = Surface.Export();
            await ImageExportService.CopyAsync(bitmap, _settings.ScreenshotFormat, _settings.ScreenshotQuality);
            if (_settings.AutoSaveScreenshot) SaveImage();
            _complete();
        }
        catch (Exception ex) { _finishing = false; UpdateButtons(); Ui.Error(_owner, ex); }
    }
    public void Dispose()
    {
        _owner.PreviewKeyDown -= KeyDown;
        InputMethod.SetIsInputMethodEnabled(_owner, _inputMethodEnabled);
        _owner.Deactivated -= OwnerDeactivated;
        foreach (var options in _flyouts.Values) options.Dispose(); _flyouts.Clear(); _options.Clear();
        Handles.Dispose(); CancelStroke();
    }
}
