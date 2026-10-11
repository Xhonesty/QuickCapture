using System;
using System.Globalization;
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
    private readonly List<ColorDisplay> _customColorMarkers = new();
    private readonly Dictionary<AnnotationTool, ComboBox> _brushControls = new();
    private readonly Dictionary<string, HoverToolOptions> _flyouts = new();
    private ComboBox? _mosaicMode, _mosaicSize;
    private TextBlock? _mosaicSizeLabel;
    private double _freehandWidth = 4, _mosaicWidth = 24;
    private int _mosaicBlock = 16;
    internal bool MosaicFreehand => _mosaicMode?.SelectedIndex == 1;
    private Point? _start;
    private string? _lastSave;
    private string? _projectPath;
    private ScreenshotFormat _projectFormat;
    private int _projectQuality;
    private (ScreenshotFormat Format, int Quality) _savedSettings;
    private bool _finishing;
    private IReadOnlyList<ToolDefinition> _definitions = Array.Empty<ToolDefinition>();
    private readonly Dictionary<string, Button> _buttons = new();
    private Int32Rect? _cropBefore;
    private Point _cropPointer;
    private Vector _cropScale;
    internal CropHandles Handles { get; }
    internal Button ButtonFor(string id) => _buttons[id];
    private Color _color = Colors.Red;
    private Color? _fillColor;
    private AnnotationShape _shape = AnnotationShape.Rectangle;
    private double _shapeWidth = 4;
    private bool _shapeStroke = true, _outlineTarget, _textItalic, _brushOutline, _textOutline;
    private Color _brushOutlineColor = Colors.Black, _textOutlineColor = Colors.Black, _outlineDefaultBefore;
    private double _brushOutlineWidth = 4, _textOutlineWidth = 4;
    private TextFrameStyle _textFrame = new(), _frameDefaultBefore = new();
    private bool? _frameColorTarget;
    private bool _newTextRequested;
    private (CheckBox Enabled, CheckBox Filled, ComboBox Shape, ComboBox Width, ColorDisplay Color, ColorDisplay Fill)? _frameControls;
    private readonly Dictionary<AnnotationTool, (CheckBox Enabled, ComboBox Width, ColorDisplay Color)> _outlineControls = new();
    private Button? _shapeColorButton;
    private readonly List<(ColorDisplay Marker, bool Fill, bool Outline)> _colorControls = new();
    private string _colorLabel = "标注颜色";
    private ColorPalette? _palette;
    private bool _colorEditing, _colorPicking, _finishingColor;
    private bool _colorReleasePending;
    private Annotation? _colorTextDraftBefore;
    private Annotation? _colorObjectBefore;
    private Color _colorDefaultBefore, _textColorBefore;
    private Color? _fillDefaultBefore;
    internal bool ColorEditing => _colorEditing;
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
    private Button? _textWeight, _textSlant;
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
    internal bool PickingColor => _colorPicking || _eyedropper.Active;
    internal Color CurrentColor => _textEditor.Draft?.Color ?? Surface.Selected?.Color ?? _color;
    internal Color? CurrentFill => Surface.Selected is { Tool: AnnotationTool.Rectangle } selected ? selected.FillColor : _fillColor;
    internal AnnotationShape Shape => _shape;
    internal ColorPalette Palette => _palette!;
    public AnnotationSurface Surface { get; }
    public AnnotationTool Tool { get; private set; } = AnnotationTool.Crop;
    public event Action? ToolChanged;

    public ScreenshotEditor(Window owner, BitmapSource image, Settings settings, Action<string> saved, Action complete, Int32Rect? crop = null, ScreenshotProject? project = null)
    {
        _owner = owner; _settings = settings; _saved = saved; _complete = complete;
        _nextStep = project?.NextStep ?? settings.StepStart;
        _projectPath = project?.ExportPath; _projectFormat = project?.Format ?? settings.ScreenshotFormat; _projectQuality = project?.Quality ?? settings.ScreenshotQuality;
        _inputMethodEnabled = InputMethod.GetIsInputMethodEnabled(owner);
        // Tool gestures bypass IME; the in-place TextBox explicitly enables it.
        InputMethod.SetIsInputMethodEnabled(owner, false);
        Surface = new AnnotationSurface(image, project?.Crop ?? crop) { Cursor = Cursors.SizeAll };
        if (project != null) Surface.Restore(project.Annotations);
        // IsInputMethodEnabled does not inherit from the window. A focused
        // canvas must disable it explicitly, while text inputs enable it.
        InputMethod.SetIsInputMethodEnabled(Surface, false);
        _textEditor = new InPlaceTextEditor(Surface);
        _textEditor.Changed += () => { SyncTextOptions(); UpdateButtons(); RefreshColors(); };
        Handles = new CropHandles(Surface, owner, () => SelectTool(AnnotationTool.Crop));
        _eyedropper = new PixelEyedropper(owner, Surface, color =>
        {
            Handles.IsHitTestVisible = !_colorEditing; _colorPicking = false;
            if (!_colorEditing) return;
            if (color is { } selected) { selected.A = _palette!.Draft.A; _palette.PreviewColor(selected); }
            _palette!.SetStatus(color == null ? "已取消取色，颜色草稿保留。" : "已取色，点击应用颜色确认。");
            if (_flyouts.TryGetValue("color", out var options)) options.Show();
        }, message => { _palette?.SetStatus(message); if (_flyouts.TryGetValue("color", out var colors)) colors.Show(); });
        Surface.Changed += () => { _lastSave = null; UpdateButtons(); };
        Surface.SelectionChanged += RefreshColors;
        Surface.MouseLeftButtonDown += BeginAnnotation;
        Surface.MouseMove += UpdateAnnotation;
        Surface.MouseLeftButtonUp += EndAnnotation;
        Surface.LostMouseCapture += (_, _) => { if (_start != null || _cropBefore != null || _objectBefore != null) CancelStroke(); };
        _owner.PreviewKeyDown += KeyDown;
        _owner.PreviewMouseLeftButtonDown += ColorGestureDown;
        _owner.PreviewMouseLeftButtonUp += ColorGestureUp;
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
        var paletteButton = _shapeColorButton!;
        _palette = new ColorPalette(color => { PreviewColor(color); FinishColor(true); }, ChangeColorTarget, ClearFill,
            StartEyedropper, previewColor: PreviewColor, cancel: () => FinishColor(false));
        _flyouts["color"] = new HoverToolOptions(paletteButton, _palette, BeginColor, hover: false,
            dismissed: () => { if (!_colorPicking) FinishColor(false); });
        SelectTool(Tool); UpdateButtons(); RefreshColors(); return _toolbar;
    }
    private bool CanEditFill => Surface.Selected is { Tool: AnnotationTool.Rectangle } || Surface.Selected == null && Tool == AnnotationTool.Rectangle;
    private bool PrimaryFill => CanEditFill;
    private bool TextTarget => EditingText || Surface.Selected?.Tool == AnnotationTool.Text || Surface.Selected == null && Tool == AnnotationTool.Text;
    private Color CurrentOutline => _textEditor.Draft?.OutlineColor ?? Surface.Selected?.OutlineColor ?? (TextTarget ? _textOutlineColor : _brushOutlineColor);
    private TextFrameStyle CurrentTextFrame => (_textEditor.Draft ?? (Surface.Selected?.Tool == AnnotationTool.Text ? Surface.Selected : null)) is { } text ? text.TextFrame ?? new() : _textFrame;
    private void BeginColor()
    {
        if (_colorEditing) return;
        CancelStroke(); _colorEditing = true; _colorObjectBefore = Surface.Selected;
        _colorTextDraftBefore = _textEditor.Draft;
        _textEditor.Input.IsEnabled = false;
        Handles.IsHitTestVisible = false;
        _colorDefaultBefore = _color; _fillDefaultBefore = _fillColor; _textColorBefore = CurrentColor;
        _outlineDefaultBefore = TextTarget ? _textOutlineColor : _brushOutlineColor;
        _frameDefaultBefore = _textFrame;
        ChangeColorTarget();
    }
    private void ChangeColorTarget()
    {
        if (_frameColorTarget != null)
        {
            _frameColorTarget = _palette?.EditingFill == true; var frame = CurrentTextFrame;
            _palette?.SetCurrent(_frameColorTarget == true ? frame.FillColor ?? Colors.White : frame.StrokeColor, true, frame.Fill != "none");
            _palette?.SetLabel(_frameColorTarget == true ? "框内填充" : "边框颜色"); return;
        }
        bool fill = _palette?.EditingFill == true && CanEditFill;
        _palette?.SetCurrent(_outlineTarget ? CurrentOutline : fill ? CurrentFill ?? CurrentColor : CurrentColor, !_outlineTarget && CanEditFill, CurrentFill != null);
        _palette?.SetLabel(_outlineTarget ? "描边颜色" : CanEditFill ? fill ? "形状颜色" : "描边颜色" : _colorLabel);
    }
    internal void PreviewColor(Color color)
    {
        if (!_colorEditing) return;
        if (_frameColorTarget is { } fill)
        {
            var frame = CurrentTextFrame; _textFrame = fill ? frame with { Fill = TextFrameStyle.Hex(color) } : frame with { Color = TextFrameStyle.Hex(color) };
            if (EditingText) _textEditor.UpdateFrame(_textFrame);
            else if (Surface.Selected is { Tool: AnnotationTool.Text } text) Surface.SetSelected(text with { TextFrame = _textFrame }, false);
            RefreshColors(); return;
        }
        if (_outlineTarget)
        {
            if (TextTarget) _textOutlineColor = color; else _brushOutlineColor = color;
            if (EditingText) { var draft = _textEditor.Draft!; _textEditor.UpdateDecoration(draft.Italic, draft.OutlineEnabled, color, draft.OutlineWidth); }
            else if (Surface.Selected is { } outlined) Surface.SetSelected(outlined with { OutlineColor = color }, false);
            RefreshColors(); return;
        }
        if (EditingText)
        {
            _color = color; var draft = _textEditor.Draft!;
            _textEditor.UpdateStyle(draft.FontFamily, draft.FontSize, draft.Bold, draft.Alignment, color); return;
        }
        if (_palette?.EditingFill == true && CanEditFill)
        {
            _fillColor = color;
            if (Surface.Selected is { Tool: AnnotationTool.Rectangle } selected) Surface.SetSelected(selected with { FillColor = color }, false);
        }
        else
        {
            _color = color;
            if (Surface.Selected is { } selected && selected.Tool is not (AnnotationTool.Mosaic or AnnotationTool.MosaicBrush))
                Surface.SetSelected(selected with { Color = color }, false);
        }
        RefreshColors();
    }
    internal void FinishColor(bool commit)
    {
        if (!_colorEditing || _finishingColor) return;
        _finishingColor = true; _colorEditing = false; _colorPicking = false; _eyedropper.Cancel();
        _colorReleasePending |= Mouse.LeftButton == MouseButtonState.Pressed;
        _textEditor.Input.IsEnabled = true;
        Handles.IsHitTestVisible = true;
        var beforeColor = _colorDefaultBefore; var beforeFill = _fillDefaultBefore; var afterColor = _color; var afterFill = _fillColor;
        bool textTarget = TextTarget; var beforeOutline = _outlineDefaultBefore; var afterOutline = textTarget ? _textOutlineColor : _brushOutlineColor;
        var beforeFrame = _frameDefaultBefore; var afterFrame = _textFrame;
        if (!commit)
        {
            _color = beforeColor; _fillColor = beforeFill;
            _textFrame = beforeFrame;
            if (textTarget) _textOutlineColor = beforeOutline; else _brushOutlineColor = beforeOutline;
            if (_colorObjectBefore != null) Surface.SetSelected(_colorObjectBefore, false);
            if (EditingText) { var draft = _textEditor.Draft!; _textEditor.UpdateStyle(draft.FontFamily, draft.FontSize, draft.Bold, draft.Alignment, _textColorBefore); }
            if (EditingText && _colorTextDraftBefore is { } textBefore) _textEditor.UpdateDecoration(textBefore.Italic, textBefore.OutlineEnabled, textBefore.OutlineColor ?? Colors.Black, textBefore.OutlineWidth);
            if (EditingText && _colorTextDraftBefore is { } frameBefore) _textEditor.UpdateFrame(frameBefore.TextFrame);
        }
        else
        {
            AnnotationColors.Remember(_palette!.Draft);
            Action<bool>? defaults = beforeColor == afterColor && beforeFill == afterFill && beforeOutline == afterOutline && beforeFrame == afterFrame ? null :
                forward => { _color = forward ? afterColor : beforeColor; _fillColor = forward ? afterFill : beforeFill; _textFrame = forward ? afterFrame : beforeFrame; if (textTarget) _textOutlineColor = forward ? afterOutline : beforeOutline; else _brushOutlineColor = forward ? afterOutline : beforeOutline; RefreshColors(); };
            if (!EditingText)
            {
                if (_colorObjectBefore != null) Surface.CommitSelected(_colorObjectBefore, defaults);
                else if (defaults != null) Surface.RememberDefaults(defaults);
            }
        }
        _colorObjectBefore = null;
        _colorTextDraftBefore = null;
        if (_flyouts.TryGetValue("color", out var options)) options.Hide();
        _frameColorTarget = null; _finishingColor = false; RefreshColors(); _textEditor.Focus();
    }
    internal void ApplyColor(Color color)
    {
        BeginColor(); _palette?.PreviewColor(color); FinishColor(true);
    }
    private void ApplyPrimaryColor(Color color)
    {
        FinishColor(false); _outlineTarget = false; _palette?.SelectTarget(PrimaryFill); ApplyColor(color);
    }
    private void ClearFill()
    {
        if (_frameColorTarget != null)
        {
            BeginColor(); _textFrame = CurrentTextFrame with { Fill = "none" };
            if (EditingText) _textEditor.UpdateFrame(_textFrame);
            else if (Surface.Selected is { Tool: AnnotationTool.Text } text) Surface.SetSelected(text with { TextFrame = _textFrame }, false);
            FinishColor(true); return;
        }
        BeginColor(); _fillColor = null;
        if (Surface.Selected is { Tool: AnnotationTool.Rectangle } selected) Surface.SetSelected(selected with { FillColor = null }, false);
        FinishColor(true);
    }
    private void RefreshColors()
    {
        SyncTextOptions();
        SyncStepOptions();
        bool fill = _palette?.EditingFill == true && CanEditFill;
        Color color = _outlineTarget ? CurrentOutline : fill ? CurrentFill ?? CurrentColor : CurrentColor;
        if (!_colorEditing) ChangeColorTarget();
        color = PrimaryFill ? CurrentFill ?? CurrentColor : CurrentColor;
        foreach (var (value, swatch) in _swatches)
        {
            swatch.BorderThickness = new Thickness(value == color ? 2 : 1);
            swatch.SetResourceReference(Border.BorderBrushProperty, value == color ? "ToolbarSelectedForeground" : "BorderBrush");
        }
        foreach (var marker in _customColorMarkers) marker.SetColor(color);
        foreach (var entry in _colorControls) entry.Marker.SetColor(entry.Outline ? CurrentOutline : entry.Fill ? CurrentFill : CurrentColor);
        if (_shapeSizes != null && _filled != null)
        {
            _syncingOptions = true; _shapeSizes.Text = (Surface.Selected?.Width ?? _shapeWidth).ToString("0.###", CultureInfo.InvariantCulture); _filled.IsChecked = CurrentFill != null; _syncingOptions = false;
        }
        _syncingOptions = true;
        foreach (var (tool, sizes) in _brushControls)
            sizes.Text = (Surface.Selected?.Tool == tool ? Surface.Selected.Width : tool == AnnotationTool.Arrow ? _arrowWidth : _freehandWidth).ToString("0.###", CultureInfo.InvariantCulture);
        if (Surface.Selected?.Tool is AnnotationTool.Mosaic or AnnotationTool.MosaicBrush && _mosaicMode != null && _mosaicSize != null)
        {
            _mosaicMode.SelectedIndex = Surface.Selected.Tool == AnnotationTool.MosaicBrush ? 1 : 0;
            _mosaicSize.Text = Surface.Selected.Width.ToString("0.###", CultureInfo.InvariantCulture);
        }
        foreach (var (tool, controls) in _outlineControls)
        {
            var outlined = tool == AnnotationTool.Text ? _textEditor.Draft ?? (Surface.Selected?.Tool == tool ? Surface.Selected : null) : Surface.Selected?.Tool == tool ? Surface.Selected : null;
            controls.Enabled.IsChecked = outlined?.OutlineEnabled ?? (tool == AnnotationTool.Text ? _textOutline : _brushOutline);
            controls.Width.Text = (outlined?.OutlineWidth ?? (tool == AnnotationTool.Text ? _textOutlineWidth : _brushOutlineWidth)).ToString("0.###", CultureInfo.InvariantCulture);
            controls.Color.SetColor(outlined?.OutlineColor ?? (tool == AnnotationTool.Text ? _textOutlineColor : _brushOutlineColor));
        }
        if (_frameControls is { } frameControls)
        {
            var frame = CurrentTextFrame; frameControls.Enabled.IsChecked = frame.Enabled; frameControls.Filled.IsChecked = frame.Fill != "none";
            frameControls.Shape.SelectedIndex = frame.Shape == AnnotationShape.RoundedRectangle ? 1 : 0; frameControls.Width.Text = frame.Width.ToString("0.###", CultureInfo.InvariantCulture);
            frameControls.Color.SetColor(frame.StrokeColor); frameControls.Fill.SetColor(frame.FillColor);
        }
        _syncingOptions = false;
        var currentShape = Surface.Selected is { Tool: AnnotationTool.Rectangle } item ? item.Shape : _shape;
        foreach (var (value, choice) in _shapeButtons) ScreenshotToolbar.Selected(choice, value == currentShape);
        _toolbar?.ShowProperties(EditingText ? AnnotationTool.Text : Tool == AnnotationTool.Select ? Surface.Selected?.Tool switch { AnnotationTool.MosaicBrush => AnnotationTool.Mosaic, { } selectedTool => selectedTool, _ => AnnotationTool.Select } : Tool);
    }
    internal void StartEyedropper(bool screen)
    {
        BeginColor(); CancelStroke(); _colorPicking = true; HideOptions(); Handles.IsHitTestVisible = false;
        Mouse.Capture(null);
        // Starting after the popup's initiating click finishes prevents that
        // click/capture release from confirming or cancelling the picker.
        _owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!_colorEditing || !_colorPicking) return;
            _eyedropper.Begin(screen);
        }));
    }
    internal void CancelEyedropper() { _colorPicking = false; _eyedropper.Cancel(); Handles.IsHitTestVisible = !_colorEditing; if (_colorEditing) { _palette?.SetStatus("已取消取色，颜色草稿保留。"); _flyouts["color"].Show(); } }
    private void UpdateButtons()
    {
        foreach (var definition in _definitions)
            if (_buttons.TryGetValue(definition.Id, out var button)) button.IsEnabled = !_finishing && definition.CanExecute?.Invoke() != false;
        if (_confirmText != null && _cancelText != null) _confirmText.Visibility = _cancelText.Visibility = EditingText ? Visibility.Visible : Visibility.Collapsed;
    }
    internal void ShowColors()
    {
        _frameColorTarget = null;
        _outlineTarget = false;
        _colorLabel = TextTarget ? "文字颜色" : Tool == AnnotationTool.Freehand || Surface.Selected?.Tool == AnnotationTool.Freehand ? "画笔颜色" : "标注颜色";
        if (!_colorEditing) _palette?.SelectTarget(PrimaryFill);
        RefreshColors();
        if (_flyouts.TryGetValue("color", out var colors))
        {
            var row = _toolbar!.Properties.Children.OfType<WrapPanel>().FirstOrDefault(p => p.Visibility == Visibility.Visible);
            colors.Popup.PlacementTarget = row == null ? ButtonFor("rectangle") : ColorEntries(row).FirstOrDefault() ?? ButtonFor("rectangle");
            colors.Show();
        }
    }
    private static IEnumerable<ColorDisplay> ColorEntries(Panel panel)
    {
        foreach (UIElement child in panel.Children)
        {
            if (child is ColorDisplay entry) yield return entry;
            if (child is Panel group) foreach (var nested in ColorEntries(group)) yield return nested;
        }
    }
    private void ShowTargetColor(Button anchor, bool fill, bool outline, string label)
    {
        FinishColor(false); _frameColorTarget = null; _outlineTarget = outline; _colorLabel = label; _palette?.SelectTarget(fill);
        if (_flyouts.TryGetValue("color", out var colors)) { colors.Popup.PlacementTarget = anchor; colors.Show(); }
    }
    internal void ShowShapes() { if (Surface.Selected?.Tool != AnnotationTool.Rectangle) SelectTool(AnnotationTool.Rectangle); RefreshColors(); }
    internal void Undo() { FinishColor(false); CancelText(); CancelStroke(); Surface.Undo(); RefreshColors(); }
    internal void Redo() { FinishColor(false); CancelText(); CancelStroke(); Surface.Redo(); RefreshColors(); }
    internal void Pin() { if (!FinishText()) return; HideOptions(); _toolbar?.HideProperties(); PinManager.Create(Surface.Export(), _settings, _saved, ScreenshotProjects.Snapshot(Surface,_nextStep,_projectFormat,_projectQuality)); _complete(); }
    internal void ExtractText()
    {
        if (!FinishText()) return; CancelStroke(); HideOptions(); _toolbar?.HideProperties();
        if (_ocr != null) { _ocr.Activate(); return; }
        _ocr = new OcrWindow(_owner, OcrImage, _settings);
        _ocr.Closed += (_, _) => _ocr = null; _ocr.Show();
    }
    internal BitmapSource OcrImage()
    {
        var image = new CroppedBitmap(Surface.Original, Surface.CropBounds); image.Freeze(); return image;
    }
    internal void Complete() { if (EditingText) CancelText(); else _complete(); HideOptions(); _toolbar?.HideProperties(); }
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
            FontFamily: _textFamily, FontSize: _textSize, Bold: _textBold, Alignment: _textAlignment, Italic: _textItalic, OutlineEnabled: _textOutline, OutlineColor: _textOutlineColor, OutlineWidth: _textOutlineWidth, TextFrame: _textFrame);
        _newTextRequested = false;
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
        _textWeight = ScreenshotToolbar.IconButton("bold", "粗体", () => { var current = _textEditor.Draft ?? Surface.Selected; SetTextStyle(current?.FontFamily ?? _textFamily, current?.FontSize ?? _textSize, !(current?.Bold ?? _textBold), current?.Alignment ?? _textAlignment); _textEditor.Focus(); });
        _textSlant = ScreenshotToolbar.IconButton("italic", "斜体", () => { var current = _textEditor.Draft ?? Surface.Selected; bool italic = !(current?.Italic ?? _textItalic); SyncDecorationDefaults(AnnotationTool.Text); _textItalic = italic; ApplyDecoration(AnnotationTool.Text); _textEditor.Focus(); });
        AutomationProperties.SetName(_textWeight, "粗体"); AutomationProperties.SetName(_textSlant, "斜体");
        _textAlignments = Choice(new[] { "左对齐", "居中", "右对齐" }, 84, "文字对齐"); _textAlignments.SelectedIndex = 0;
        panel.Children.Add(_textFonts); panel.Children.Add(_textSizes); panel.Children.Add(_textWeight); panel.Children.Add(_textSlant); panel.Children.Add(_textAlignments);
        void Apply() { if (_syncingText) return; SetTextStyle(_textFonts.SelectedItem as string ?? _textFamily, _textSizes.SelectedItem is double size ? size : _textSize, _textEditor.Draft?.Bold ?? Surface.Selected?.Bold ?? _textBold, new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right }[Math.Max(0, _textAlignments.SelectedIndex)]); }
        _textFonts.SelectionChanged += (_, _) => Apply(); _textSizes.SelectionChanged += (_, _) => Apply(); _textAlignments.SelectionChanged += (_, _) => Apply();
        _textFonts.DropDownClosed += (_, _) => _textEditor.Focus(); _textSizes.DropDownClosed += (_, _) => _textEditor.Focus(); _textAlignments.DropDownClosed += (_, _) => _textEditor.Focus();
        AddOutlineOptions(panel, AnnotationTool.Text);
        AddTextFrameOptions(panel);
        panel.Children.Add(Ui.Button("新增文字", () => { if (!FinishText()) return; Surface.Deselect(); SelectTool(AnnotationTool.Text); _newTextRequested = true; }));
        AddColors(panel, compact: true, label: "文字颜色"); panel.Children.Add(_confirmText!); panel.Children.Add(_cancelText!);
        panel.PreviewKeyDown += KeyDown; SyncTextOptions(); return panel;
    }
    private void SyncTextOptions()
    {
        if (_textFonts == null || _textSizes == null || _textWeight == null || _textAlignments == null) return;
        var draft = _textEditor.Draft ?? (Surface.Selected is { Tool: AnnotationTool.Text } selected ? selected : null);
        _syncingText = true;
        _textFonts.SelectedItem = draft?.FontFamily ?? _textFamily; _textSizes.SelectedItem = draft?.FontSize ?? _textSize;
        ScreenshotToolbar.Selected(_textWeight, draft?.Bold ?? _textBold); if (_textSlant != null) ScreenshotToolbar.Selected(_textSlant, draft?.Italic ?? _textItalic);
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
        var panel = OptionRow();
        _stepSizes = Choice(new[] { 16, 24, 32, 40, 48, 64, 96, 128, 160 }, 64, "编号直径（原始像素）"); _stepSizes.Name = "StepSize"; _stepSizes.SelectedItem = _settings.StepSize; panel.Children.Add(ThicknessOptions.Group("直径", _stepSizes));
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
        _stepStart = NumberInput("StepStart", "下一个编号 / 重置起始数字"); panel.Children.Add(ThicknessOptions.Group("下一个", _stepStart));
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
        _shapeColorButton = ColorControl(panel, "形状颜色", true, false); _shapeColorButton.Name = "Property_palette";
        var stroke = new CheckBox { Content = "描边", IsChecked = _shapeStroke, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) }; panel.Children.Add(stroke);
        stroke.Checked += (_, _) => { if (!_syncingOptions) { _shapeStroke = true; if (Surface.Selected is { Tool: AnnotationTool.Rectangle } item) Surface.SetSelected(item with { StrokeEnabled = true }); } };
        stroke.Unchecked += (_, _) => { if (!_syncingOptions) { _shapeStroke = false; if (Surface.Selected is { Tool: AnnotationTool.Rectangle } item) Surface.SetSelected(item with { StrokeEnabled = false }); } };
        Surface.SelectionChanged += () => { _syncingOptions = true; stroke.IsChecked = Surface.Selected?.StrokeEnabled ?? _shapeStroke; _syncingOptions = false; };
        ColorControl(panel, "描边颜色", false, false);
        _shapeSizes = Sizes(_shapeWidth, "描边粗细", width => { if (_syncingOptions) return; _shapeWidth = width; if (Surface.Selected is { Tool: AnnotationTool.Rectangle } item) Surface.SetSelected(item with { Width = width }); });
        panel.Children.Add(ThicknessOptions.Group("描边粗细", _shapeSizes)); return panel;
    }
    private Button ColorControl(WrapPanel panel, string label, bool fill, bool outline)
    {
        ColorDisplay? button = null;
        button = new ColorDisplay(label, () => ShowTargetColor(button!, fill, outline, label)); button.SetColor(outline ? CurrentOutline : fill ? CurrentFill : CurrentColor);
        AutomationProperties.SetName(button, label); panel.Children.Add(ThicknessOptions.Group(label, button));
        _colorControls.Add((button, fill, outline));
        return button;
    }
    private void ApplyDecoration(AnnotationTool tool)
    {
        bool text = tool == AnnotationTool.Text;
        var item = text ? _textEditor.Draft ?? (Surface.Selected?.Tool == tool ? Surface.Selected : null) : Surface.Selected?.Tool == tool ? Surface.Selected : null;
        bool enabled = text ? _textOutline : _brushOutline; var color = text ? _textOutlineColor : _brushOutlineColor; double width = text ? _textOutlineWidth : _brushOutlineWidth;
        if (text && EditingText) _textEditor.UpdateDecoration(_textItalic, enabled, color, width);
        else if (item != null) Surface.SetSelected(item with { Italic = text && _textItalic, OutlineEnabled = enabled, OutlineColor = color, OutlineWidth = width });
        RefreshColors();
    }
    private void AddOutlineOptions(WrapPanel panel, AnnotationTool tool)
    {
        bool text = tool == AnnotationTool.Text;
        var toggle = new CheckBox { Content = "描边", IsChecked = text ? _textOutline : _brushOutline, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 6, 0) }; panel.Children.Add(toggle);
        void Apply() { if (_syncingOptions) return; SyncDecorationDefaults(tool); if (text) _textOutline = toggle.IsChecked == true; else _brushOutline = toggle.IsChecked == true; ApplyDecoration(tool); }
        toggle.Checked += (_, _) => Apply(); toggle.Unchecked += (_, _) => Apply();
        ColorDisplay? button = null; button = new ColorDisplay("描边颜色", () => ShowTargetColor(button!, false, true, "描边颜色")); button.SetColor(Colors.Black);
        panel.Children.Add(ThicknessOptions.Group("描边颜色", button)); AutomationProperties.SetName(button, "描边颜色");
        var size = Sizes(text ? _textOutlineWidth : _brushOutlineWidth, "描边粗细", n => { if (_syncingOptions) return; SyncDecorationDefaults(tool); if (text) _textOutlineWidth = n; else _brushOutlineWidth = n; ApplyDecoration(tool); });
        panel.Children.Add(ThicknessOptions.Group("描边粗细", size)); _outlineControls[tool] = (toggle, size, button);
    }
    private void AddTextFrameOptions(WrapPanel panel)
    {
        var enabled = new CheckBox { Name = "ScreenshotTextFrameEnabled", Content = "文字边框", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 6, 0) };
        var filled = new CheckBox { Content = "框内填充", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 6, 0) };
        var shape = Choice(new[] { "矩形", "圆角矩形" }, 94, "边框形状");
        var frameGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 6, 2) }; frameGroup.Children.Add(enabled); frameGroup.Children.Add(shape); panel.Children.Add(frameGroup);
        void Change(Func<TextFrameStyle, TextFrameStyle> mutate)
        {
            if (_syncingOptions) return; _textFrame = mutate(CurrentTextFrame); _textFrame.Validate();
            if (EditingText) _textEditor.UpdateFrame(_textFrame);
            else if (Surface.Selected is { Tool: AnnotationTool.Text } text) Surface.SetSelected(text with { TextFrame = _textFrame });
            RefreshColors();
        }
        enabled.Checked += (_, _) => Change(f => f with { Enabled = true }); enabled.Unchecked += (_, _) => Change(f => f with { Enabled = false });
        shape.SelectionChanged += (_, _) => Change(f => f with { Shape = shape.SelectedIndex == 1 ? AnnotationShape.RoundedRectangle : AnnotationShape.Rectangle });
        ColorDisplay AddColor(string label, bool fill)
        {
            ColorDisplay? button = null;
            button = new ColorDisplay(label, () =>
            {
                FinishColor(false); _frameColorTarget = fill; _outlineTarget = false; _palette?.SelectTarget(fill);
                if (_flyouts.TryGetValue("color", out var popup)) { popup.Popup.PlacementTarget = button; popup.Show(); }
            });
            panel.Children.Add(ThicknessOptions.Group(label, button)); return button;
        }
        var color = AddColor("边框颜色", false); var width = Sizes(_textFrame.Width, "边框粗细", n => Change(f => f with { Width = n })); panel.Children.Add(ThicknessOptions.Group("边框粗细", width));
        panel.Children.Add(filled); var fillColor = AddColor("填充颜色", true);
        filled.Checked += (_, _) => Change(f => f with { Fill = f.Fill == "none" ? "#FFFFFF" : f.Fill }); filled.Unchecked += (_, _) => Change(f => f with { Fill = "none" });
        _frameControls = (enabled, filled, shape, width, color, fillColor);
    }
    private void SyncDecorationDefaults(AnnotationTool tool)
    {
        var item = tool == AnnotationTool.Text ? _textEditor.Draft ?? (Surface.Selected?.Tool == tool ? Surface.Selected : null) : Surface.Selected?.Tool == tool ? Surface.Selected : null;
        if (item == null) return;
        if (tool == AnnotationTool.Text) { _textItalic = item.Italic; _textOutline = item.OutlineEnabled; _textOutlineColor = item.OutlineColor ?? Colors.Black; _textOutlineWidth = item.OutlineWidth; }
        else { _brushOutline = item.OutlineEnabled; _brushOutlineColor = item.OutlineColor ?? Colors.Black; _brushOutlineWidth = item.OutlineWidth; }
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
    private static ComboBox Sizes(double width, string label, Action<double> changed) => ThicknessOptions.Create(width, label, changed);
    private void AddColors(WrapPanel panel, bool compact = false, string label = "颜色")
    {
        ColorDisplay? color = null;
        color = new ColorDisplay(label, () => ShowTargetColor(color!, false, false, label)) { Name = "Property_palette" };
        _customColorMarkers.Add(color); panel.Children.Add(ThicknessOptions.Group(label, color));
    }
    private double _arrowWidth = 4;
    internal WrapPanel CreateArrowOptions()
    {
        var panel = OptionRow(); var sizes = Sizes(_arrowWidth, "箭头粗细", n => { if (_syncingOptions) return; _arrowWidth = n; if (Surface.Selected is { Tool: AnnotationTool.Arrow } item) Surface.SetSelected(item with { Width = n }); });
        panel.Children.Add(ThicknessOptions.Group("箭头粗细", sizes)); _brushControls[AnnotationTool.Arrow] = sizes; AddColors(panel); return panel;
    }
    internal WrapPanel CreateFreehandOptions()
    {
        var panel = OptionRow(); var size = Sizes(_freehandWidth, "画笔粗细", n => { if (_syncingOptions) return; _freehandWidth = n; if (Surface.Selected is { Tool: AnnotationTool.Freehand } item) Surface.SetSelected(item with { Width = n }); });
        panel.Children.Add(ThicknessOptions.Group("画笔粗细", size)); _brushControls[AnnotationTool.Freehand] = size; AddColors(panel, label: "画笔颜色"); AddOutlineOptions(panel, AnnotationTool.Freehand); return panel;
    }
    internal WrapPanel CreateMosaicOptions()
    {
        var panel = OptionRow(); _mosaicMode = Choice(new[] { "框选马赛克", "画笔马赛克" }, 130, "马赛克模式"); _mosaicMode.SelectedIndex = 0; panel.Children.Add(_mosaicMode);
        _mosaicSizeLabel = OptionLabel("画笔粗细"); _mosaicSize = Sizes(_mosaicWidth, "画笔粗细", n => { if (_syncingOptions) return; _mosaicWidth = n; if (Surface.Selected is { Tool: AnnotationTool.MosaicBrush } item) Surface.SetSelected(item with { Width = n }); });
        var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(_mosaicSizeLabel); row.Children.Add(_mosaicSize); panel.Children.Add(row);
        _mosaicMode.SelectionChanged += (_, _) => { if (!_syncingOptions && Surface.Selected is { } item && item.Tool is AnnotationTool.Mosaic or AnnotationTool.MosaicBrush) Surface.SetSelected(item with { Tool = MosaicFreehand ? AnnotationTool.MosaicBrush : AnnotationTool.Mosaic, Points = MosaicFreehand ? item.Points ?? new[] { item.Start, item.End } : null }); UpdateMosaicSize(); };
        var block = ThicknessOptions.CreateValue(_mosaicBlock, "像素块：2–128 原始像素", new[] { "4", "8", "12", "16", "24", "32", "48", "64" }, 2, 128, value => { if (_syncingOptions || value != Math.Truncate(value)) return; int n = (int)value; _mosaicBlock = n; if (Surface.Selected is { } item && item.Tool is AnnotationTool.Mosaic or AnnotationTool.MosaicBrush) Surface.SetSelected(item with { BlockSize = n }); });
        Surface.SelectionChanged += () => { if (Surface.Selected is { } item && item.Tool is AnnotationTool.Mosaic or AnnotationTool.MosaicBrush) { _syncingOptions = true; block.Text = item.BlockSize.ToString(); _syncingOptions = false; } };
        panel.Children.Add(ThicknessOptions.Group("像素块", block)); UpdateMosaicSize(); return panel;
    }
    private void UpdateMosaicSize()
    {
        if (_mosaicSize == null || _mosaicSizeLabel == null) return;
        _mosaicSize.IsEnabled = MosaicFreehand; _mosaicSizeLabel.Opacity = MosaicFreehand ? 1 : .45;
    }
    internal WrapPanel OptionsFor(AnnotationTool tool) => _toolbar!.OptionsFor(tool);
    private void HideOptions() { foreach (var options in _flyouts.Values) options.Hide(); }
    private void OwnerDeactivated(object? sender, EventArgs e) { CancelStroke(); if (_colorPicking) CancelEyedropper(); else HideOptions(); }
    private void ColorGestureUp(object sender, MouseButtonEventArgs e) { if (_colorReleasePending) { _colorReleasePending = false; e.Handled = true; } }
    private void ColorGestureDown(object sender, MouseButtonEventArgs e) { if (_colorReleasePending || ColorEditing && !PickingColor) e.Handled = true; }
    private bool IsBrush => Tool == AnnotationTool.Freehand || Tool == AnnotationTool.Mosaic && MosaicFreehand;
    private Annotation Stroke(Point start, Point end) => new(Tool == AnnotationTool.Mosaic && MosaicFreehand ? AnnotationTool.MosaicBrush : Tool,
        start, ConstrainEnd(start, end), _color, Points: IsBrush ? _points.ToArray() : null, Width: IsBrush ? (Tool == AnnotationTool.Freehand ? _freehandWidth : _mosaicWidth) : Tool == AnnotationTool.Rectangle ? _shapeWidth : Tool == AnnotationTool.Arrow ? _arrowWidth : 3, Shape: _shape, FillColor: Tool == AnnotationTool.Rectangle ? _fillColor : null, OutlineEnabled: Tool == AnnotationTool.Freehand && _brushOutline, OutlineColor: _brushOutlineColor, OutlineWidth: _brushOutlineWidth, StrokeEnabled: Tool != AnnotationTool.Rectangle || _shapeStroke, BlockSize: _mosaicBlock);
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
        if (_finishing || PickingColor || ColorEditing || _colorReleasePending) { e.Handled = true; return; }
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
            if (!_newTextRequested && Surface.SelectAt(point, 2 / Surface.DisplayScale.X) && Surface.Selected is { Tool: AnnotationTool.Text } existing) BeginText(point, existing);
            else { Surface.Deselect(); BeginText(point); }
            return;
        }
        _start = point; _points.Clear(); if (IsBrush) _points.Add(point);
        Surface.CaptureMouse(); Surface.Preview = Stroke(point, point); Surface.InvalidateVisual();
    }
    private void UpdateAnnotation(object sender, MouseEventArgs e)
    {
        if (PickingColor || ColorEditing) return;
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
        if (PickingColor || ColorEditing) { e.Handled = true; return; }
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
        if (_colorEditing)
        {
            if (e.Key == Key.Escape) { if (_colorPicking) { _colorPicking = false; _flyouts["color"].Show(); } else FinishColor(false); e.Handled = true; }
            return;
        }
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
        if (key == Key.S && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { e.Handled=true; SaveAs(); return; }
        if (modifiers != ModifierKeys.None) return;
        var definition = _definitions.FirstOrDefault(d => d.Shortcut == key);
        if (definition != null && definition.CanExecute?.Invoke() != false) { e.Handled = true; definition.Execute(); }
    }
    internal string SaveImage() { string path=SaveImageAsync().GetAwaiter().GetResult(); _saved(path); return path; }
    private Task<string> SaveImageAsync()
    {
        if (!FinishText()) throw new InvalidOperationException("请先完成输入法选词。");
        if (_lastSave != null && _savedSettings == (_settings.ScreenshotFormat, _settings.ScreenshotQuality)) return Task.FromResult(_lastSave);
        string path = _projectPath ?? Paths.NewCapture(_settings.OutputDirectory, ImageExportService.Extension(_settings.ScreenshotFormat));
        var format = _projectPath != null ? _projectFormat : _settings.ScreenshotFormat; int quality = _projectPath != null ? _projectQuality : _settings.ScreenshotQuality;
        var snapshot = ScreenshotProjects.Snapshot(Surface,_nextStep,format,quality,path); var rendered = Surface.Export();
        return SaveSnapshotAsync(snapshot,rendered,path,format,quality,_projectPath != null);
    }
    private async Task<string> SaveSnapshotAsync(ScreenshotProject snapshot, BitmapSource rendered, string path, ScreenshotFormat format, int quality, bool overwrite)
    {
        await ScreenshotProjects.SaveAsync(snapshot,rendered,path,format,quality,overwrite).ConfigureAwait(false);
        _lastSave = path; _savedSettings = (_settings.ScreenshotFormat, _settings.ScreenshotQuality);
        // Synchronous SaveImage remains available to existing fixtures; callback is dispatched by async production callers.
        return path;
    }
    internal bool Saving => _finishing;
    internal async void SaveAndComplete()
    {
        if (_finishing || !FinishText()) return; _finishing = true; UpdateButtons(); HideOptions(); _toolbar?.HideProperties();
        try
        {
            if (_projectPath != null) { string saved = await SaveImageAsync(); _saved(saved); _finishing=false; UpdateButtons(); }
            else
            {
                var rendered=Surface.Export(); var snapshot=ScreenshotProjects.Snapshot(Surface,_nextStep,_settings.ScreenshotFormat,_settings.ScreenshotQuality);
                var dialog = new ScreenshotSaveWindow(_owner,rendered,_settings,(path,format,quality,overwrite)=>ScreenshotProjects.SaveAsync(snapshot,rendered,path,format,quality,overwrite));
                if (dialog.ShowDialog() == true) { _saved(dialog.SavedPath!); _finishing=false; _complete(); }
                else { _finishing = false; UpdateButtons(); }
            }
        }
        catch (Exception ex) { _finishing = false; UpdateButtons(); Ui.Error(_owner, ex); }
    }
    internal void SaveAs()
    {
        if (_finishing || !FinishText()) return; _finishing=true; UpdateButtons(); HideOptions(); _toolbar?.HideProperties();
        try
        {
            var rendered=Surface.Export(); var snapshot=ScreenshotProjects.Snapshot(Surface,_nextStep,_projectFormat,_projectQuality);
            var dialog=new ScreenshotSaveWindow(_owner,rendered,_settings,(path,format,quality,overwrite)=>
            {
                if (_projectPath != null && System.IO.Path.GetFullPath(path).Equals(System.IO.Path.GetFullPath(_projectPath),StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("另存为请使用不同文件名。");
                return ScreenshotProjects.SaveAsync(snapshot,rendered,path,format,quality,overwrite);
            });
            if(dialog.ShowDialog()==true) { _projectPath=dialog.SavedPath!; _projectFormat=dialog.SavedFormat; _projectQuality=dialog.SavedQuality; _lastSave=null; _saved(_projectPath); }
        }
        catch(Exception ex) { Ui.Error(_owner,ex); }
        finally { _finishing=false; UpdateButtons(); }
    }
    internal async Task CopyAndCompleteAsync()
    {
        if (_finishing || !FinishText()) return; _finishing = true; UpdateButtons(); HideOptions(); _toolbar?.HideProperties();
        try
        {
            var bitmap = Surface.Export();
            await ImageExportService.CopyAsync(bitmap, _settings.ScreenshotFormat, _settings.ScreenshotQuality);
            if (_settings.AutoSaveScreenshot) _saved(await SaveImageAsync());
            _finishing=false; _complete();
        }
        catch (Exception ex) { _finishing = false; UpdateButtons(); Ui.Error(_owner, ex); }
    }
    public void Dispose()
    {
        FinishColor(false);
        _ocr?.Close(); _ocr = null;
        _textEditor.Cancel();
        _eyedropper.Dispose();
        _owner.PreviewKeyDown -= KeyDown;
        _owner.PreviewMouseLeftButtonDown -= ColorGestureDown;
        _owner.PreviewMouseLeftButtonUp -= ColorGestureUp;
        InputMethod.SetIsInputMethodEnabled(_owner, _inputMethodEnabled);
        _owner.Deactivated -= OwnerDeactivated;
        foreach (var options in _flyouts.Values) options.Dispose(); _flyouts.Clear();
        Surface.SelectionChanged -= RefreshColors; Handles.Dispose(); CancelStroke();
    }
}
