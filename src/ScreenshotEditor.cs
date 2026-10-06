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
    private Color? _fillColor;
    private AnnotationShape _shape = AnnotationShape.Rectangle;
    private int _shapeWidth = 3;
    private ColorPalette? _palette;
    private Border? _colorIndicator;
    private ComboBox? _shapeSizes;
    private CheckBox? _filled;
    private bool _syncingOptions;
    private readonly Dictionary<AnnotationShape, Button> _shapeButtons = new();
    private readonly PixelEyedropper _eyedropper;
    private readonly InPlaceTextEditor _textEditor;
    private string _textFamily = "Microsoft YaHei UI";
    private double _textSize = 24;
    private bool _textBold;
    private TextAlignment _textAlignment;
    private ComboBox? _textFonts, _textSizes, _textAlignments;
    private CheckBox? _textWeight;
    private Button? _confirmText, _cancelText;
    private bool _syncingText;
    private int _nextStep;
    private TextBox? _stepValue, _stepStart;
    private Button? _applyStep;
    private ComboBox? _stepSizes;
    private bool _syncingStep;
    private OcrWindow? _ocr;
    internal Canvas TextOverlay => _textEditor.Overlay;
    internal TextBox? TextInput => EditingText ? _textEditor.Input : null;
    internal bool EditingText => _textEditor.Active;
    private Annotation? _objectBefore;
    private Point _objectPointer;
    private (int X, int Y)? _objectHandle;
    internal bool PickingColor => _eyedropper.Active;
    internal Color CurrentColor => _textEditor.Draft?.Color ?? Surface.Selected?.Color ?? _color;
    internal Color? CurrentFill => Surface.Selected is { Tool: AnnotationTool.Rectangle } selected ? selected.FillColor : _fillColor;
    internal AnnotationShape Shape => _shape;
    internal ColorPalette Palette => _palette!;
    public AnnotationSurface Surface { get; }
    public AnnotationTool Tool { get; private set; } = AnnotationTool.Crop;
    public event Action? ToolChanged;

    public ScreenshotEditor(Window owner, BitmapSource image, Settings settings, Action<string> saved, Action complete, Int32Rect? crop = null)
    {
        _owner = owner; _settings = settings; _saved = saved; _complete = complete;
        _nextStep = settings.StepStart;
        _inputMethodEnabled = InputMethod.GetIsInputMethodEnabled(owner);
        // Tool gestures bypass IME; the in-place TextBox explicitly enables it.
        InputMethod.SetIsInputMethodEnabled(owner, false);
        Surface = new AnnotationSurface(image, crop) { Cursor = Cursors.SizeAll };
        // IsInputMethodEnabled does not inherit from the window. A focused
        // canvas must disable it explicitly, while text inputs enable it.
        InputMethod.SetIsInputMethodEnabled(Surface, false);
        _textEditor = new InPlaceTextEditor(Surface);
        _textEditor.Changed += () => { SyncTextOptions(); UpdateButtons(); RefreshColors(); };
        Handles = new CropHandles(Surface, owner, () => SelectTool(AnnotationTool.Crop));
        _eyedropper = new PixelEyedropper(owner, Surface, color =>
        {
            Handles.IsHitTestVisible = true;
            if (color is { } selected) ApplyColor(selected);
            RefreshColors(); _textEditor.Focus();
        }, message => { _palette?.SetStatus(message); ShowColors(); });
        Surface.Changed += () => { _lastSave = null; UpdateButtons(); };
        Surface.SelectionChanged += RefreshColors;
        Surface.MouseLeftButtonDown += BeginAnnotation;
        Surface.MouseMove += UpdateAnnotation;
        Surface.MouseLeftButtonUp += EndAnnotation;
        Surface.LostMouseCapture += (_, _) => { if (_start != null || _cropBefore != null || _objectBefore != null) CancelStroke(); };
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
            if (definition.Id == "color")
            {
                // Show the applied color on the toolbar without replacing its icon.
                var icon = (UIElement)button.Content; button.Content = null;
                var grid = new Grid(); grid.Children.Add(icon);
                _colorIndicator = new Border { Width = 17, Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(1) };
                grid.Children.Add(_colorIndicator); button.Content = new Viewbox { Width = UiDesign.Number("IconSize") + 4, Height = UiDesign.Number("IconSize") + 4, Child = grid, IsHitTestVisible = false };
            }
            button.Name = "Tool_" + definition.Id; AutomationProperties.SetAutomationId(button, button.Name); AutomationProperties.SetName(button, definition.Name);
            button.ToolTip = $"{definition.Name} ({definition.Shortcut}{(definition.AlternativeKeys == "" ? "" : " / " + definition.AlternativeKeys)})";
            ToolTipService.SetPlacement(button, PlacementMode.Top); _buttons[definition.Id] = button;
            if (definition.Tool is { } tool) _toolButtons[tool] = button;
            panel.Children.Add(button);
        }
        foreach (var definition in _definitions.Where(d => d.Options != null))
        {
            var flyout = new HoverToolOptions(_buttons[definition.Id], definition.Options!(), () =>
            { foreach (var (id, other) in _flyouts) if (id != definition.Id) other.Hide(); }, hover: definition.Id is not ("color" or "text" or "step"), dismissed: () => _textEditor.Focus());
            _flyouts[definition.Id] = flyout; if (definition.Tool is { } tool) _options[tool] = flyout;
        }
        _confirmText = Ui.Button("完成文字", ConfirmText); _cancelText = Ui.Button("取消文字", CancelText);
        _confirmText.Focusable = _cancelText.Focusable = false;
        AutomationProperties.SetName(_confirmText, "完成文字"); AutomationProperties.SetName(_cancelText, "取消文字");
        panel.Children.Add(_confirmText); panel.Children.Add(_cancelText);
        SelectTool(Tool); UpdateButtons(); RefreshColors(); return panel;
    }
    internal StackPanel CreateColorOptions()
    {
        _palette = new ColorPalette(ApplyColor, RefreshColors, ClearFill, StartEyedropper); RefreshColors(); return _palette;
    }
    private bool CanEditFill => Surface.Selected is { Tool: AnnotationTool.Rectangle } || Surface.Selected == null && Tool == AnnotationTool.Rectangle;
    internal void ApplyColor(Color color)
    {
        AnnotationColors.Remember(color);
        if (EditingText)
        {
            _color = color; var draft = _textEditor.Draft!;
            _textEditor.UpdateStyle(draft.FontFamily, draft.FontSize, draft.Bold, draft.Alignment, color); _textEditor.Focus(); return;
        }
        if (_palette?.EditingFill == true && CanEditFill)
        {
            _fillColor = color; if (Surface.Selected is { Tool: AnnotationTool.Rectangle } selected) Surface.SetSelected(selected with { FillColor = color });
        }
        else
        {
            _color = color; if (Surface.Selected is { } selected && selected.Tool is not (AnnotationTool.Mosaic or AnnotationTool.MosaicBrush)) Surface.SetSelected(selected with { Color = color });
        }
        RefreshColors();
    }
    private void ClearFill()
    {
        _fillColor = null; if (Surface.Selected is { Tool: AnnotationTool.Rectangle } selected) Surface.SetSelected(selected with { FillColor = null }); RefreshColors();
    }
    private void RefreshColors()
    {
        SyncTextOptions();
        SyncStepOptions();
        bool fill = _palette?.EditingFill == true && CanEditFill;
        Color color = fill ? CurrentFill ?? CurrentColor : CurrentColor;
        _palette?.SetCurrent(color, CanEditFill, CurrentFill != null);
        if (_colorIndicator != null) _colorIndicator.Background = new SolidColorBrush(color);
        if (_buttons.TryGetValue("color", out var button)) button.ToolTip = $"标注颜色 (K) · {(fill ? "填充" : "描边 / 画笔")} {AnnotationColors.Hex(color)}";
        if (_shapeSizes != null && _filled != null)
        {
            _syncingOptions = true; _shapeSizes.SelectedIndex = Array.IndexOf(BrushSizes, (int)(Surface.Selected?.Width ?? _shapeWidth)); _filled.IsChecked = CurrentFill != null; _syncingOptions = false;
        }
        var currentShape = Surface.Selected is { Tool: AnnotationTool.Rectangle } item ? item.Shape : _shape;
        foreach (var (value, choice) in _shapeButtons) choice.SetResourceReference(Control.BackgroundProperty, value == currentShape ? "Accent" : "ButtonBackground");
    }
    internal void StartEyedropper(bool screen)
    {
        CancelStroke(); HideOptions(); Handles.IsHitTestVisible = false; _eyedropper.Begin(screen);
    }
    internal void CancelEyedropper() => _eyedropper.Cancel();
    private void UpdateButtons()
    {
        foreach (var definition in _definitions)
            if (_buttons.TryGetValue(definition.Id, out var button)) button.IsEnabled = !_finishing && definition.CanExecute?.Invoke() != false;
        if (_confirmText != null && _cancelText != null) _confirmText.Visibility = _cancelText.Visibility = EditingText ? Visibility.Visible : Visibility.Collapsed;
    }
    internal void ShowColors() { RefreshColors(); if (_flyouts.TryGetValue("color", out var colors)) colors.Show(); }
    internal void ShowShapes() { if (Surface.Selected?.Tool != AnnotationTool.Rectangle) SelectTool(AnnotationTool.Rectangle); if (_flyouts.TryGetValue("rectangle", out var shapes)) shapes.Show(); }
    internal void Undo() { CancelText(); CancelStroke(); Surface.Undo(); }
    internal void Redo() { CancelText(); CancelStroke(); Surface.Redo(); }
    internal void Pin() { if (!FinishText()) return; PinManager.Create(Surface.Export(), _settings, _saved); _complete(); }
    internal void ExtractText()
    {
        if (!FinishText()) return; CancelStroke(); HideOptions();
        if (_ocr != null) { _ocr.Activate(); return; }
        _ocr = new OcrWindow(_owner, OcrImage, _settings);
        _ocr.Closed += (_, _) => _ocr = null; _ocr.Show();
    }
    internal BitmapSource OcrImage()
    {
        var image = new CroppedBitmap(Surface.Original, Surface.CropBounds); image.Freeze(); return image;
    }
    internal void Complete() { if (EditingText) CancelText(); else _complete(); }
    public void SelectTool(AnnotationTool tool)
    {
        if (EditingText && tool == AnnotationTool.Text) { ShowTextOptions(); return; }
        if (!FinishText()) return;
        CancelStroke();
        if (PickingColor) CancelEyedropper();
        if (tool != AnnotationTool.Select) Surface.Deselect();
        Tool = tool; Surface.Cursor = tool is AnnotationTool.Crop or AnnotationTool.Select ? Cursors.SizeAll : Cursors.Cross;
        foreach (var (key, button) in _toolButtons)
        {
            button.SetResourceReference(Control.BackgroundProperty, key == tool ? "Accent" : "ButtonBackground");
            button.SetResourceReference(Control.ForegroundProperty, key == tool ? "AccentForeground" : "TextPrimary");
        }
        foreach (var (key, options) in _options) if (key != tool) options.Hide();
        RefreshColors();
        ToolChanged?.Invoke();
    }
    internal void BeginText(Point viewportPoint, Annotation? existing = null)
    {
        if (!FinishText()) return;
        var defaults = new Annotation(AnnotationTool.Text, viewportPoint, viewportPoint, _color,
            FontFamily: _textFamily, FontSize: _textSize, Bold: _textBold, Alignment: _textAlignment);
        _textEditor.Begin(viewportPoint, defaults, existing); HideOptions(); _textEditor.Focus();
    }
    internal void ConfirmText() { if (_textEditor.Confirm()) { HideOptions(); Surface.Focus(); } }
    internal void CancelText() { _textEditor.Cancel(); HideOptions(); Surface.Focus(); }
    private bool FinishText() { if (!EditingText) return true; ConfirmText(); return !EditingText; }
    internal void SetTextStyle(string family, double size, bool bold, TextAlignment alignment)
    {
        _textFamily = family; _textSize = Math.Clamp(size, 8, 144); _textBold = bold; _textAlignment = alignment;
        if (EditingText) _textEditor.UpdateStyle(_textFamily, _textSize, bold, alignment, CurrentColor);
        else if (Surface.Selected is { Tool: AnnotationTool.Text } selected)
            Surface.SetSelected(selected with { FontFamily = _textFamily, FontSize = _textSize, Bold = bold, Alignment = alignment });
        SyncTextOptions();
    }
    internal void ShowTextOptions()
    {
        if (!EditingText && Surface.Selected?.Tool != AnnotationTool.Text && Tool != AnnotationTool.Text) SelectTool(AnnotationTool.Text);
        SyncTextOptions(); if (_flyouts.TryGetValue("text", out var options)) options.Show();
    }
    internal StackPanel CreateTextOptions()
    {
        var panel = new StackPanel { Width = 220 };
        panel.Children.Add(OptionLabel("字体与字号 · 原始像素"));
        _textFonts = new ComboBox { ItemsSource = new[] { "Microsoft YaHei UI", "SimSun", "Segoe UI", "Arial", "Consolas" }, ToolTip = "字体", SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 7) };
        _textSizes = new ComboBox { ItemsSource = new double[] { 12, 16, 20, 24, 28, 32, 40, 48, 64, 72, 96, 144 }, ToolTip = "字号（原始像素）", SelectedItem = 24d, Margin = new Thickness(0, 0, 0, 7) };
        _textWeight = new CheckBox { Content = "粗体", Margin = new Thickness(0, 0, 0, 7) };
        _textAlignments = new ComboBox { ItemsSource = new[] { "左对齐", "居中", "右对齐" }, SelectedIndex = 0, ToolTip = "文字对齐", Margin = new Thickness(0, 0, 0, 7) };
        panel.Children.Add(_textFonts); panel.Children.Add(_textSizes); panel.Children.Add(_textWeight); panel.Children.Add(_textAlignments);
        void Apply() { if (_syncingText) return; SetTextStyle(_textFonts.SelectedItem as string ?? _textFamily, _textSizes.SelectedItem is double size ? size : _textSize, _textWeight.IsChecked == true, (TextAlignment)new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right }[Math.Max(0, _textAlignments.SelectedIndex)]); }
        _textFonts.SelectionChanged += (_, _) => Apply(); _textSizes.SelectionChanged += (_, _) => Apply();
        _textWeight.Checked += (_, _) => Apply(); _textWeight.Unchecked += (_, _) => Apply(); _textAlignments.SelectionChanged += (_, _) => Apply();
        _textFonts.DropDownClosed += (_, _) => _textEditor.Focus(); _textSizes.DropDownClosed += (_, _) => _textEditor.Focus(); _textAlignments.DropDownClosed += (_, _) => _textEditor.Focus();
        _textWeight.Click += (_, _) => _textEditor.Focus();
        panel.Children.Add(Ui.Button("文字颜色 / 吸管", ShowColors));
        panel.Children.Add(OptionLabel("Enter 换行 · Ctrl+Enter 完成\nEsc 取消本次编辑 · 双击文字重编"));
        panel.PreviewKeyDown += KeyDown;
        SyncTextOptions(); return panel;
    }
    private void SyncTextOptions()
    {
        if (_textFonts == null || _textSizes == null || _textWeight == null || _textAlignments == null) return;
        var draft = _textEditor.Draft ?? (Surface.Selected is { Tool: AnnotationTool.Text } selected ? selected : null);
        _syncingText = true;
        _textFonts.SelectedItem = draft?.FontFamily ?? _textFamily; _textSizes.SelectedItem = draft?.FontSize ?? _textSize;
        _textWeight.IsChecked = draft?.Bold ?? _textBold;
        _textAlignments.SelectedIndex = (draft?.Alignment ?? _textAlignment) switch { TextAlignment.Center => 1, TextAlignment.Right => 2, _ => 0 };
        _syncingText = false;
    }
    internal int NextStep => _nextStep;
    internal void AddStep(Point point)
    {
        if (_nextStep > 9999) throw new InvalidOperationException("编号已达到 9999，请在步骤编号面板设置新的起始数字。");
        double side = Math.Min(_settings.StepSize, Math.Min(Surface.Width, Surface.Height));
        var start = new Point(Math.Clamp(point.X - side / 2, 0, Surface.Width - side), Math.Clamp(point.Y - side / 2, 0, Surface.Height - side));
        Surface.Add(new(AnnotationTool.Step, start, start + new Vector(side, side), _color, StepNumber: _nextStep));
        _nextStep++; SyncStepOptions();
    }
    internal void ResetStepStart(int value)
    {
        if (value is < 1 or > 9999) throw new ArgumentException("编号范围为 1–9999。");
        _nextStep = _settings.StepStart = value; _settings.Save(); SyncStepOptions();
    }
    internal void SetStepNumber(int value)
    {
        if (value is < 1 or > 9999) throw new ArgumentException("编号范围为 1–9999。");
        if (Surface.Selected is { Tool: AnnotationTool.Step } selected) Surface.SetSelected(selected with { StepNumber = value });
    }
    internal void ShowStepOptions()
    {
        if (Surface.Selected?.Tool != AnnotationTool.Step) SelectTool(AnnotationTool.Step);
        SyncStepOptions(); if (_flyouts.TryGetValue("step", out var options)) options.Show();
    }
    internal StackPanel CreateStepOptions()
    {
        var panel = new StackPanel { Width = 220 };
        panel.Children.Add(OptionLabel("步骤编号 · 连续单击添加"));
        _stepSizes = new ComboBox { Name = "StepSize", ItemsSource = new[] { 16, 24, 32, 40, 48, 64, 96, 128, 160 }, SelectedItem = _settings.StepSize, ToolTip = "编号直径（原始像素）", Margin = new Thickness(0, 0, 0, 8) }; panel.Children.Add(_stepSizes);
        _stepSizes.SelectionChanged += (_, _) =>
        {
            if (_syncingStep || _stepSizes.SelectedItem is not int size) return;
            _settings.StepSize = size;
            if (Surface.Selected is { Tool: AnnotationTool.Step } selected)
            {
                var bounds = AnnotationGeometry.Bounds(selected); double side = Math.Min(size, Math.Min(Surface.SourceWidth, Surface.SourceHeight));
                var start = new Point(Math.Clamp(bounds.Left + (bounds.Width - side) / 2, 0, Surface.SourceWidth - side), Math.Clamp(bounds.Top + (bounds.Height - side) / 2, 0, Surface.SourceHeight - side));
                Surface.SetSelected(selected with { Start = start, End = start + new Vector(side, side) });
            }
            _settings.Save();
        };
        panel.Children.Add(OptionLabel("选中编号的数字"));
        _stepValue = new TextBox { Name = "StepNumber", ToolTip = "选中编号的新数字", Margin = new Thickness(0, 0, 0, 6) }; panel.Children.Add(_stepValue);
        _applyStep = Ui.Button("修改选中数字", () => { try { SetStepNumber(ParseStep(_stepValue.Text)); } catch (Exception ex) { Ui.Error(_owner, ex); } }); panel.Children.Add(_applyStep);
        panel.Children.Add(OptionLabel("下一个编号 / 重置起始数字"));
        _stepStart = new TextBox { Name = "StepStart", Margin = new Thickness(0, 0, 0, 6) }; panel.Children.Add(_stepStart);
        panel.Children.Add(Ui.Button("设置起始数字", () => { try { ResetStepStart(ParseStep(_stepStart.Text)); } catch (Exception ex) { Ui.Error(_owner, ex); } }));
        panel.Children.Add(Ui.Button("编号颜色 / 吸管", ShowColors));
        panel.Children.Add(OptionLabel("E 移动 / 缩放 · Delete 删除\n双击编号修改数字 · 删除不重排"));
        SyncStepOptions(); return panel;
    }
    private static int ParseStep(string text) => int.TryParse(text, out int value) && value is >= 1 and <= 9999 ? value : throw new ArgumentException("请输入 1–9999 的整数。");
    private void SyncStepOptions()
    {
        if (_stepValue == null || _stepStart == null || _stepSizes == null) return;
        _syncingStep = true;
        var selected = Surface.Selected is { Tool: AnnotationTool.Step } step ? step : null;
        _stepValue.IsEnabled = selected != null; _stepValue.Text = selected?.StepNumber.ToString() ?? "";
        if (_applyStep != null) _applyStep.IsEnabled = selected != null;
        _stepStart.Text = _nextStep.ToString();
        _stepSizes.SelectedItem = selected != null ? (int)Math.Round(AnnotationGeometry.Bounds(selected).Width) : _settings.StepSize;
        _syncingStep = false;
    }
    internal StackPanel CreateShapeOptions()
    {
        var panel = new StackPanel { Width = 205 }; panel.Children.Add(OptionLabel("形状 · Shift 等比例"));
        var choices = new WrapPanel(); panel.Children.Add(choices);
        foreach (var (shape, name) in new[] { (AnnotationShape.Rectangle, "矩形"), (AnnotationShape.RoundedRectangle, "圆角矩形"), (AnnotationShape.Ellipse, "椭圆 / 圆"), (AnnotationShape.Triangle, "三角形"), (AnnotationShape.Diamond, "菱形") })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var path = new System.Windows.Shapes.Path { Data = AnnotationGeometry.Shape(shape, new Rect(0, 0, 18, 16)), Width = 20, Height = 18, StrokeThickness = 1.7, Margin = new Thickness(0, 0, 7, 0), Stretch = Stretch.Uniform }; path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextPrimary");
            row.Children.Add(path); row.Children.Add(new TextBlock { Text = name });
            var choice = Ui.Button("", () => SelectShape(shape)); choice.Content = row; choice.ToolTip = name; choice.Width = 200; choice.Margin = new Thickness(0, 0, 0, 3); choices.Children.Add(choice); _shapeButtons[shape] = choice;
        }
        panel.Children.Add(OptionLabel("描边粗细")); _shapeSizes = Sizes(_shapeWidth); panel.Children.Add(_shapeSizes);
        _shapeSizes.SelectionChanged += (_, _) =>
        {
            if (_syncingOptions || _shapeSizes.SelectedIndex < 0) return; _shapeWidth = BrushSizes[_shapeSizes.SelectedIndex];
            if (Surface.Selected is { Tool: AnnotationTool.Rectangle } selected) Surface.SetSelected(selected with { Width = _shapeWidth });
        };
        _filled = new CheckBox { Content = "颜色填充", Margin = new Thickness(0, 7, 0, 7) }; panel.Children.Add(_filled);
        _filled.Checked += (_, _) => { if (_syncingOptions) return; _fillColor ??= _color; if (Surface.Selected is { Tool: AnnotationTool.Rectangle } selected) Surface.SetSelected(selected with { FillColor = _fillColor }); RefreshColors(); };
        _filled.Unchecked += (_, _) => { if (!_syncingOptions) ClearFill(); };
        panel.Children.Add(Ui.Button("描边 / 填充调色盘", ShowColors)); return panel;
    }
    internal void SelectShape(AnnotationShape shape)
    {
        _shape = shape;
        if (Surface.Selected is { Tool: AnnotationTool.Rectangle } selected) Surface.SetSelected(selected with { Shape = shape });
        else SelectTool(AnnotationTool.Rectangle);
        foreach (var (value, button) in _shapeButtons) button.SetResourceReference(Control.BackgroundProperty, value == shape ? "Accent" : "ButtonBackground");
        RefreshColors();
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
        start, ConstrainEnd(start, end), _color, Points: IsBrush ? _points.ToArray() : null, Width: IsBrush ? (Tool == AnnotationTool.Freehand ? _freehandWidth : _mosaicWidth) : Tool == AnnotationTool.Rectangle ? _shapeWidth : 3, Shape: _shape, FillColor: Tool == AnnotationTool.Rectangle ? _fillColor : null);
    private Point ConstrainEnd(Point start, Point end) => AnnotationGeometry.Constrain(start, end, new Size(Surface.Width, Surface.Height), Tool == AnnotationTool.Rectangle && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    private void CancelStroke()
    {
        _start = null; _points.Clear(); Surface.Preview = null; Surface.InvalidateVisual();
        if (_objectBefore is { } item) { _objectBefore = null; Surface.SetSelected(item, false); }
        if (_cropBefore is { } before) { _cropBefore = null; Surface.CommitCrop(before); }
        if (Surface.IsMouseCaptured) Surface.ReleaseMouseCapture();
    }
    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, Surface.Width), Math.Clamp(p.Y, 0, Surface.Height));
    private void BeginAnnotation(object sender, MouseButtonEventArgs e)
    {
        if (_finishing || PickingColor) return;
        HideOptions();
        e.Handled = true; Surface.Focus(); var point = Clamp(e.GetPosition(Surface));
        if (EditingText && !FinishText()) return;
        if (e.ClickCount == 2 && Surface.SelectAt(point, 3 / Surface.DisplayScale.X) && Surface.Selected is { Tool: AnnotationTool.Text } text)
        { BeginText(point, text); return; }
        if (e.ClickCount == 2 && Surface.Selected is { Tool: AnnotationTool.Step }) { SelectTool(AnnotationTool.Select); ShowStepOptions(); return; }
        if (Tool == AnnotationTool.Select)
        {
            _objectHandle = Surface.SelectedHandle(point);
            if (_objectHandle == null) Surface.SelectAt(point, 6 / Surface.DisplayScale.X);
            if (Surface.Selected is { } selected) { _objectBefore = selected; _objectPointer = point; Surface.CaptureMouse(); }
            return;
        }
        Surface.Deselect();
        if (Tool == AnnotationTool.Step) { AddStep(point); return; }
        if (Tool == AnnotationTool.Crop)
        {
            _cropBefore = Surface.CropBounds; _cropPointer = e.GetPosition(_owner); _cropScale = CropHandles.ScaleInOwner(Surface, _owner); Surface.CaptureMouse(); return;
        }
        if (Tool == AnnotationTool.Text)
        {
            if (Surface.SelectAt(point, 2 / Surface.DisplayScale.X) && Surface.Selected is { Tool: AnnotationTool.Text } existing) BeginText(point, existing);
            else { Surface.Deselect(); BeginText(point); }
            return;
        }
        _start = point; _points.Clear(); if (IsBrush) _points.Add(point);
        Surface.CaptureMouse(); Surface.Preview = Stroke(point, point); Surface.InvalidateVisual();
    }
    private void UpdateAnnotation(object sender, MouseEventArgs e)
    {
        if (PickingColor) return;
        if (_objectBefore is { } item)
        {
            Point objectPoint = Clamp(e.GetPosition(Surface)); var delta = objectPoint - _objectPointer; var bounds = AnnotationGeometry.Bounds(item); Annotation updated;
            if (_objectHandle is { } handle)
            {
                double left = bounds.Left, right = bounds.Right, top = bounds.Top, bottom = bounds.Bottom;
                const double minimum = 8;
                // An object can remain partly outside a later crop. Resize its
                // original source bounds instead of constructing inverted ranges
                // from the smaller viewport.
                if (handle.X < 0) left = Math.Clamp(left + delta.X, 0, right - Math.Min(minimum, bounds.Width));
                if (handle.X > 0) right = Math.Clamp(right + delta.X, left + Math.Min(minimum, bounds.Width), Surface.SourceWidth);
                if (handle.Y < 0) top = Math.Clamp(top + delta.Y, 0, bottom - Math.Min(minimum, bounds.Height));
                if (handle.Y > 0) bottom = Math.Clamp(bottom + delta.Y, top + Math.Min(minimum, bounds.Height), Surface.SourceHeight);
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && handle.X != 0 && handle.Y != 0)
                {
                    double side = Math.Min(right - left, bottom - top);
                    if (handle.X < 0) left = right - side; else right = left + side;
                    if (handle.Y < 0) top = bottom - side; else bottom = top + side;
                }
                updated = AnnotationGeometry.Resize(item, bounds, new Rect(left, top, right - left, bottom - top));
            }
            else
            {
                double dx = Math.Clamp(delta.X, -bounds.Left, Math.Max(-bounds.Left, Surface.SourceWidth - bounds.Right));
                double dy = Math.Clamp(delta.Y, -bounds.Top, Math.Max(-bounds.Top, Surface.SourceHeight - bounds.Bottom));
                updated = AnnotationGeometry.Move(item, new Vector(dx, dy));
            }
            Surface.SetSelected(updated, false); e.Handled = true; return;
        }
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
        if (PickingColor) { e.Handled = true; return; }
        if (_objectBefore is { } item) { _objectBefore = null; Surface.CommitSelected(item); Surface.ReleaseMouseCapture(); e.Handled = true; return; }
        if (_cropBefore is { } before) { _cropBefore = null; Surface.CommitCrop(before); Surface.ReleaseMouseCapture(); e.Handled = true; return; }
        if (_start is not { } start) return;
        var end = Clamp(e.GetPosition(Surface)); if (IsBrush && (_points[^1] - end).Length >= 0.75) _points.Add(end);
        var annotation = Stroke(start, end); _start = null; Surface.ReleaseMouseCapture(); e.Handled = true;
        if (IsBrush || (end - start).Length > 2 && (Tool != AnnotationTool.Rectangle || Math.Abs(annotation.End.X - annotation.Start.X) >= 2 && Math.Abs(annotation.End.Y - annotation.Start.Y) >= 2)) Surface.Add(annotation);
        else { Surface.Preview = null; Surface.InvalidateVisual(); }
        _points.Clear();
    }
    private async void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || _finishing) return;
        if (PickingColor) { e.Handled = true; if (e.Key == Key.Escape) CancelEyedropper(); return; }
        if (EditingText)
        {
            if (_textEditor.Composing || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
            if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { ConfirmText(); e.Handled = true; }
            return;
        }
        if (Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is ComboBox) return;
        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (Surface.Selected != null && modifiers == ModifierKeys.None && key is Key.Delete or Key.Back) { CancelStroke(); Surface.DeleteSelected(); e.Handled = true; return; }
        if (Tool == AnnotationTool.Select && Surface.Selected is { } selected && modifiers is ModifierKeys.None or ModifierKeys.Shift && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            int step = modifiers == ModifierKeys.Shift ? 10 : 1;
            var bounds = AnnotationGeometry.Bounds(selected);
            double dx = Math.Clamp(key == Key.Left ? -step : key == Key.Right ? step : 0, -bounds.Left, Math.Max(-bounds.Left, Surface.SourceWidth - bounds.Right));
            double dy = Math.Clamp(key == Key.Up ? -step : key == Key.Down ? step : 0, -bounds.Top, Math.Max(-bounds.Top, Surface.SourceHeight - bounds.Bottom));
            Surface.SetSelected(AnnotationGeometry.Move(selected, new Vector(dx, dy))); e.Handled = true; return;
        }
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
        if (!FinishText()) throw new InvalidOperationException("请先完成输入法选词。");
        if (_lastSave != null && _savedSettings == (_settings.ScreenshotFormat, _settings.ScreenshotQuality)) return _lastSave;
        string path = Paths.NewCapture(_settings.OutputDirectory, ImageExportService.Extension(_settings.ScreenshotFormat));
        ImageExportService.Save(Surface.Export(), path, _settings.ScreenshotFormat, _settings.ScreenshotQuality); _lastSave = path; _savedSettings = (_settings.ScreenshotFormat, _settings.ScreenshotQuality); _saved(path); return path;
    }
    internal void SaveAndComplete()
    {
        if (_finishing || !FinishText()) return; _finishing = true; UpdateButtons();
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
        if (_finishing || !FinishText()) return; _finishing = true;
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
        _ocr?.Close(); _ocr = null;
        _textEditor.Cancel();
        _eyedropper.Dispose();
        _owner.PreviewKeyDown -= KeyDown;
        InputMethod.SetIsInputMethodEnabled(_owner, _inputMethodEnabled);
        _owner.Deactivated -= OwnerDeactivated;
        foreach (var options in _flyouts.Values) options.Dispose(); _flyouts.Clear(); _options.Clear();
        Surface.SelectionChanged -= RefreshColors; Handles.Dispose(); CancelStroke();
    }
}
