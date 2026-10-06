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
    private ScreenshotToolbar? _toolbar;
    private readonly List<(Color Color, Border Swatch)> _swatches = new();
    private readonly List<Border> _customColorMarkers = new();
    private readonly Dictionary<AnnotationTool, ComboBox> _brushControls = new();
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
    public ScreenshotToolbar CreateToolbar(Action? reselect = null)
    {
        _toolbar = new ScreenshotToolbar(); var panel = _toolbar.MainTools;
        _definitions = ToolCatalog.Create(this, reselect);
        string previousGroup = "";
        foreach (var definition in _definitions)
        {
            if (definition.Id == "color") continue; // K and the property-row palette share the same action.
            if (definition.Id == "reselect" && reselect == null) continue;
            if (previousGroup != "" && previousGroup != definition.Group)
            {
                panel.Children.Add(ScreenshotToolbar.Separator());
            }
            previousGroup = definition.Group;
            var button = ScreenshotToolbar.IconButton(definition.Icon, definition.Name,
                () => { if (definition.CanExecute?.Invoke() != false) definition.Execute(); }, definition.Options != null);
            button.Name = "Tool_" + definition.Id; AutomationProperties.SetAutomationId(button, button.Name); AutomationProperties.SetName(button, definition.Name);
            button.ToolTip = $"{definition.Name} ({definition.Shortcut}{(definition.AlternativeKeys == "" ? "" : " / " + definition.AlternativeKeys)})";
            ToolTipService.SetPlacement(button, PlacementMode.Top); _buttons[definition.Id] = button;
            if (definition.Tool is { } tool) _toolButtons[tool] = button;
            panel.Children.Add(button);
        }
        _confirmText = Ui.Button("完成文字", ConfirmText); _cancelText = Ui.Button("取消文字", CancelText);
        _confirmText.Focusable = _cancelText.Focusable = false;
        foreach (var action in new[] { _confirmText, _cancelText }) { action.Height = 30; action.Padding = new Thickness(6, 0, 6, 0); action.Margin = new Thickness(2, 0, 0, 0); action.FontSize = 11; }
        AutomationProperties.SetName(_confirmText, "完成文字"); AutomationProperties.SetName(_cancelText, "取消文字");
        foreach (var definition in _definitions.Where(d => d.Options != null && d.Tool != null))
            _toolbar.AddProperties(definition.Tool!.Value, definition.Options!());
        _toolbar.AddProperties(AnnotationTool.Crop, HintOptions("拖动手柄裁剪 · 方向键 1 px · Shift+方向键 10 px"));
        _toolbar.AddProperties(AnnotationTool.Select, HintOptions("单击选择标注 · 拖动移动 / 手柄缩放 · Delete 删除"));
        var paletteButton = _toolbar.OptionsFor(AnnotationTool.Rectangle).Children.OfType<Button>().First(b => b.Name == "Property_palette");
        _palette = new ColorPalette(ApplyColor, RefreshColors, ClearFill, StartEyedropper);
        _flyouts["color"] = new HoverToolOptions(paletteButton, _palette, () => { }, hover: false, dismissed: () => _textEditor.Focus());
        SelectTool(Tool); UpdateButtons(); RefreshColors(); return _toolbar;
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
        foreach (var (value, swatch) in _swatches)
        {
            swatch.BorderThickness = new Thickness(value == color ? 2 : 1);
            swatch.SetResourceReference(Border.BorderBrushProperty, value == color ? "ToolbarSelectedForeground" : "BorderBrush");
        }
        foreach (var marker in _customColorMarkers) marker.Background = new SolidColorBrush(color);
        if (_shapeSizes != null && _filled != null)
        {
            _syncingOptions = true; _shapeSizes.SelectedIndex = Array.IndexOf(BrushSizes, (int)(Surface.Selected?.Width ?? _shapeWidth)); _filled.IsChecked = CurrentFill != null; _syncingOptions = false;
        }
        _syncingOptions = true;
        foreach (var (tool, sizes) in _brushControls)
            sizes.SelectedIndex = Array.IndexOf(BrushSizes, (int)(Surface.Selected?.Tool == tool ? Surface.Selected.Width : tool == AnnotationTool.Arrow ? _arrowWidth : _freehandWidth));
        if (Surface.Selected?.Tool is AnnotationTool.Mosaic or AnnotationTool.MosaicBrush && _mosaicMode != null && _mosaicSize != null)
        {
            _mosaicMode.SelectedIndex = Surface.Selected.Tool == AnnotationTool.MosaicBrush ? 1 : 0;
            _mosaicSize.SelectedIndex = Array.IndexOf(BrushSizes, (int)Surface.Selected.Width);
        }
        _syncingOptions = false;
        var currentShape = Surface.Selected is { Tool: AnnotationTool.Rectangle } item ? item.Shape : _shape;
        foreach (var (value, choice) in _shapeButtons) ScreenshotToolbar.Selected(choice, value == currentShape);
        _toolbar?.ShowProperties(EditingText ? AnnotationTool.Text : Tool == AnnotationTool.Select ? Surface.Selected?.Tool switch { AnnotationTool.MosaicBrush => AnnotationTool.Mosaic, { } selectedTool => selectedTool, _ => AnnotationTool.Select } : Tool);
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
        if (_confirmText != null && _cancelText != null) _confirmText.Visibility = _cancelText.Visibility = EditingText ? Visibility.Visible : Visibility.Hidden;
    }
    internal void ShowColors()
    {
        RefreshColors();
        if (_flyouts.TryGetValue("color", out var colors))
        {
            var row = _toolbar!.Properties.Children.OfType<WrapPanel>().FirstOrDefault(p => p.Visibility == Visibility.Visible);
            colors.Popup.PlacementTarget = row?.Children.OfType<Button>().FirstOrDefault(b => b.Name == "Property_palette") ?? ButtonFor("rectangle");
            colors.Show();
        }
    }
    internal void ShowShapes() { if (Surface.Selected?.Tool != AnnotationTool.Rectangle) SelectTool(AnnotationTool.Rectangle); RefreshColors(); }
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
            ScreenshotToolbar.Selected(button, key == tool);
        }
        HideOptions();
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
        SyncTextOptions(); _toolbar?.ShowProperties(AnnotationTool.Text);
    }
    internal WrapPanel CreateTextOptions()
    {
        var panel = OptionRow();
        _textFonts = Choice(new[] { "Microsoft YaHei UI", "SimSun", "Segoe UI", "Arial", "Consolas" }, 146, "字体");
        _textFonts.SelectedIndex = 0;
        _textSizes = Choice(new double[] { 12, 16, 20, 24, 28, 32, 40, 48, 64, 72, 96, 144 }, 64, "字号（原始像素）"); _textSizes.SelectedItem = 24d;
        _textWeight = new CheckBox { Content = "粗体", ToolTip = "粗体", Margin = new Thickness(6, 0, 8, 0) };
        _textAlignments = Choice(new[] { "左对齐", "居中", "右对齐" }, 84, "文字对齐"); _textAlignments.SelectedIndex = 0;
        panel.Children.Add(_textFonts); panel.Children.Add(_textSizes); panel.Children.Add(_textWeight); panel.Children.Add(_textAlignments);
        void Apply() { if (_syncingText) return; SetTextStyle(_textFonts.SelectedItem as string ?? _textFamily, _textSizes.SelectedItem is double size ? size : _textSize, _textWeight.IsChecked == true, new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right }[Math.Max(0, _textAlignments.SelectedIndex)]); }
        _textFonts.SelectionChanged += (_, _) => Apply(); _textSizes.SelectionChanged += (_, _) => Apply();
        _textWeight.Checked += (_, _) => Apply(); _textWeight.Unchecked += (_, _) => Apply(); _textAlignments.SelectionChanged += (_, _) => Apply();
        _textFonts.DropDownClosed += (_, _) => _textEditor.Focus(); _textSizes.DropDownClosed += (_, _) => _textEditor.Focus(); _textAlignments.DropDownClosed += (_, _) => _textEditor.Focus();
        _textWeight.Click += (_, _) => _textEditor.Focus();
        AddColors(panel, compact: true); panel.Children.Add(_confirmText!); panel.Children.Add(_cancelText!);
        panel.PreviewKeyDown += KeyDown; SyncTextOptions(); return panel;
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
        SyncStepOptions(); _toolbar?.ShowProperties(AnnotationTool.Step);
    }
    internal WrapPanel CreateStepOptions()
    {
        var panel = OptionRow(); panel.Children.Add(OptionLabel("直径"));
        _stepSizes = Choice(new[] { 16, 24, 32, 40, 48, 64, 96, 128, 160 }, 64, "编号直径（原始像素）"); _stepSizes.Name = "StepSize"; _stepSizes.SelectedItem = _settings.StepSize; panel.Children.Add(_stepSizes);
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
        _stepValue = NumberInput("StepNumber", "选中编号的新数字"); panel.Children.Add(_stepValue);
        _applyStep = PropertyAction("修改选中数字", () => { try { SetStepNumber(ParseStep(_stepValue.Text)); } catch (Exception ex) { Ui.Error(_owner, ex); } }); panel.Children.Add(_applyStep);
        panel.Children.Add(OptionLabel("下一个")); _stepStart = NumberInput("StepStart", "下一个编号 / 重置起始数字"); panel.Children.Add(_stepStart);
        panel.Children.Add(PropertyAction("设置起始数字", () => { try { ResetStepStart(ParseStep(_stepStart.Text)); } catch (Exception ex) { Ui.Error(_owner, ex); } }));
        AddColors(panel); SyncStepOptions(); return panel;
    }
    private static TextBox NumberInput(string name, string tooltip) => new() { Name = name, ToolTip = tooltip, Width = 50, MinHeight = 0, Height = 30, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(2, 0, 4, 0) };
    private static Button PropertyAction(string name, Action action)
    {
        var button = Ui.Button(name, action); button.Height = 30; button.FontSize = 11; button.Padding = new Thickness(6, 0, 6, 0); button.Margin = new Thickness(2, 0, 4, 0); return button;
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
    internal WrapPanel CreateShapeOptions()
    {
        var panel = OptionRow();
        foreach (var (shape, name) in new[] { (AnnotationShape.Rectangle, "矩形"), (AnnotationShape.RoundedRectangle, "圆角矩形"), (AnnotationShape.Ellipse, "椭圆 / 圆"), (AnnotationShape.Triangle, "三角形"), (AnnotationShape.Diamond, "菱形") })
        {
            var path = new System.Windows.Shapes.Path { Data = AnnotationGeometry.Shape(shape, new Rect(0, 0, 18, 16)), Width = 20, Height = 18, StrokeThickness = 1.7, Stretch = Stretch.Uniform };
            var choice = ScreenshotToolbar.IconButton("square", name + " · Shift 等比例", () => SelectShape(shape));
            path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = choice });
            choice.Content = path; panel.Children.Add(choice); _shapeButtons[shape] = choice;
        }
        panel.Children.Add(ScreenshotToolbar.Separator());
        _filled = new CheckBox { Content = "填充", Margin = new Thickness(2, 0, 8, 0) }; panel.Children.Add(_filled);
        _filled.Checked += (_, _) => { if (_syncingOptions) return; _fillColor ??= _color; if (Surface.Selected is { Tool: AnnotationTool.Rectangle } selected) Surface.SetSelected(selected with { FillColor = _fillColor }); RefreshColors(); };
        _filled.Unchecked += (_, _) => { if (!_syncingOptions) ClearFill(); };
        panel.Children.Add(OptionLabel("描边")); _shapeSizes = Sizes(_shapeWidth); panel.Children.Add(_shapeSizes);
        _shapeSizes.SelectionChanged += (_, _) =>
        {
            if (_syncingOptions || _shapeSizes.SelectedIndex < 0) return; _shapeWidth = BrushSizes[_shapeSizes.SelectedIndex];
            if (Surface.Selected is { Tool: AnnotationTool.Rectangle } selected) Surface.SetSelected(selected with { Width = _shapeWidth });
        };
        AddColors(panel); return panel;
    }
    internal void SelectShape(AnnotationShape shape)
    {
        _shape = shape;
        if (Surface.Selected is { Tool: AnnotationTool.Rectangle } selected) Surface.SetSelected(selected with { Shape = shape });
        else SelectTool(AnnotationTool.Rectangle);
        RefreshColors();
    }
    private static WrapPanel OptionRow() => new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock OptionLabel(string text) => new() { Text = text, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private static ComboBox Choice(System.Collections.IEnumerable values, double width, string tooltip) => new() { Width = width, MinWidth = 0, Height = 30, ToolTip = tooltip, ItemsSource = values, Margin = new Thickness(2, 0, 4, 0), FontSize = 12 };
    private static ComboBox Sizes(int width) { var choice = Choice(new[] { "3 px", "6 px", "12 px", "24 px", "48 px" }, 70, "画笔粗细（原始像素）"); choice.SelectedIndex = Array.IndexOf(BrushSizes, width); return choice; }
    private static WrapPanel HintOptions(string text) { var row = OptionRow(); row.Children.Add(OptionLabel(text)); return row; }
    private void AddColors(WrapPanel panel, bool compact = false)
    {
        panel.Children.Add(ScreenshotToolbar.Separator());
        foreach (var color in (compact ? new[] { "#FF6373", "#E7BE55", "#609F59", "#568DE3", "#202020" } : new[] { "#FF6373", "#D94B35", "#E7BE55", "#609F59", "#568DE3", "#202020", "#FFFFFF" }).Select(hex => (Color)ColorConverter.ConvertFromString(hex)))
        {
            var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Padding = new Thickness(3), BorderThickness = new Thickness(1), Background = Brushes.Transparent };
            swatch.Child = new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color) };
            var button = ScreenshotToolbar.IconButton("palette", AnnotationColors.Hex(color), () => ApplyColor(color)); button.Width = 28; button.Padding = new Thickness(0); button.Content = swatch;
            AutomationProperties.SetName(button, "颜色 " + AnnotationColors.Hex(color)); panel.Children.Add(button); _swatches.Add((color, swatch));
        }
        var palette = ScreenshotToolbar.IconButton("palette", "自定义调色盘 / 填充颜色 (K)", ShowColors); palette.Name = "Property_palette";
        var icon = (UIElement)palette.Content; palette.Content = null; var contents = new Grid(); contents.Children.Add(icon);
        var marker = new Border { Width = 8, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        marker.SetResourceReference(Border.BorderBrushProperty, "ToolbarBackground"); marker.BorderThickness = new Thickness(.5); contents.Children.Add(marker); _customColorMarkers.Add(marker);
        palette.Content = contents; panel.Children.Add(palette);
        panel.Children.Add(ScreenshotToolbar.IconButton("pipette", "画布吸管 · 自定义调色盘可切换屏幕吸管", () => StartEyedropper(false)));
    }
    private int _arrowWidth = 3;
    internal WrapPanel CreateArrowOptions()
    {
        var panel = OptionRow(); panel.Children.Add(OptionLabel("箭头粗细")); var sizes = Sizes(_arrowWidth); panel.Children.Add(sizes);
        _brushControls[AnnotationTool.Arrow] = sizes;
        sizes.SelectionChanged += (_, _) => { if (_syncingOptions || sizes.SelectedIndex < 0) return; _arrowWidth = BrushSizes[sizes.SelectedIndex]; if (Surface.Selected is { Tool: AnnotationTool.Arrow } selected) Surface.SetSelected(selected with { Width = _arrowWidth }); };
        AddColors(panel); return panel;
    }
    internal WrapPanel CreateFreehandOptions()
    {
        var panel = OptionRow(); panel.Children.Add(OptionLabel("涂鸦")); var size = Sizes(_freehandWidth); panel.Children.Add(size);
        _brushControls[AnnotationTool.Freehand] = size;
        size.SelectionChanged += (_, _) => { if (!_syncingOptions && size.SelectedIndex >= 0) { _freehandWidth = BrushSizes[size.SelectedIndex]; if (Surface.Selected is { Tool: AnnotationTool.Freehand } selected) Surface.SetSelected(selected with { Width = _freehandWidth }); } };
        AddColors(panel); return panel;
    }
    internal WrapPanel CreateMosaicOptions()
    {
        var panel = OptionRow(); _mosaicMode = Choice(new[] { "框选马赛克", "涂鸦马赛克" }, 130, "马赛克模式"); _mosaicMode.SelectedIndex = 0; panel.Children.Add(_mosaicMode);
        _mosaicSizeLabel = OptionLabel("画笔粗细"); panel.Children.Add(_mosaicSizeLabel); _mosaicSize = Sizes(_mosaicWidth); panel.Children.Add(_mosaicSize);
        _mosaicMode.SelectionChanged += (_, _) => UpdateMosaicSize();
        _mosaicSize.SelectionChanged += (_, _) => { if (!_syncingOptions && _mosaicSize.SelectedIndex >= 0) { _mosaicWidth = BrushSizes[_mosaicSize.SelectedIndex]; if (Surface.Selected is { Tool: AnnotationTool.MosaicBrush } selected) Surface.SetSelected(selected with { Width = _mosaicWidth }); } };
        UpdateMosaicSize(); return panel;
    }
    private void UpdateMosaicSize()
    {
        if (_mosaicSize == null || _mosaicSizeLabel == null) return;
        _mosaicSize.IsEnabled = MosaicFreehand; _mosaicSizeLabel.Opacity = MosaicFreehand ? 1 : .45;
    }
    internal WrapPanel OptionsFor(AnnotationTool tool) => _toolbar!.OptionsFor(tool);
    private void HideOptions() { foreach (var options in _flyouts.Values) options.Hide(); }
    private void OwnerDeactivated(object? sender, EventArgs e) => HideOptions();
    private bool IsBrush => Tool == AnnotationTool.Freehand || Tool == AnnotationTool.Mosaic && MosaicFreehand;
    private Annotation Stroke(Point start, Point end) => new(Tool == AnnotationTool.Mosaic && MosaicFreehand ? AnnotationTool.MosaicBrush : Tool,
        start, ConstrainEnd(start, end), _color, Points: IsBrush ? _points.ToArray() : null, Width: IsBrush ? (Tool == AnnotationTool.Freehand ? _freehandWidth : _mosaicWidth) : Tool == AnnotationTool.Rectangle ? _shapeWidth : Tool == AnnotationTool.Arrow ? _arrowWidth : 3, Shape: _shape, FillColor: Tool == AnnotationTool.Rectangle ? _fillColor : null);
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
        if (_finishing || !FinishText()) return; _finishing = true; UpdateButtons();
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
        foreach (var options in _flyouts.Values) options.Dispose(); _flyouts.Clear();
        Surface.SelectionChanged -= RefreshColors; Handles.Dispose(); CancelStroke();
    }
}
