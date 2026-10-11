using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;

namespace QuickCapture;

internal sealed class VideoObjectEditor : StackPanel
{
    private readonly Window _owner;
    private readonly string _source;
    private readonly Func<double> _position, _end;
    private readonly Action _pause;
    private readonly CancellationToken _token;
    private readonly ComboBox _list = new() { Width = 170, Name = "VideoObjectList", ToolTip = "对象与图层" };
    private readonly WrapPanel _properties = new();
    private readonly WrapPanel _tools = new();
    private readonly Dictionary<VideoObjectKind, Button> _buttons = new();
    private readonly Dictionary<VideoObjectKind, VideoObject> _defaults = new();
    private readonly Button _topLayer, _bottomLayer;
    private Popup? _colorPopup;
    private ColorPalette? _palette;
    private PixelEyedropper? _eyedropper;
    private VideoObject? _colorBefore;
    private bool _colorFill, _colorOutline, _colorCreating, _colorText, _defaultExisted, _picking;
    private int _pickerGeneration;
    private bool _colorReleasePending;
    internal bool PickingColor => _picking;
    internal Func<Task<BitmapSource>>? FrameProvider { get; set; }
    internal bool ColorEditing => _colorBefore != null;
    internal ColorPalette? Palette => _palette;
    internal PixelEyedropper? Eyedropper => _eyedropper;
    internal VideoObject[] ToolDefaults => _defaults.Values.OrderBy(o => o.Kind).ToArray();
    private readonly TextBlock _hint = UiDesign.Text("", true);
    private readonly Button _select;
    private readonly DispatcherTimer _propertyTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _refreshing;
    private string? _editingId;
    private UIElement? _external;
    private VideoInfo _info = new(0, 0, 0, 30, false);
    private MediaCapabilities _capabilities = new(null, null, "", "");
    internal ScreenshotToolbar Toolbar { get; } = new();
    internal VideoObjectOverlay Overlay { get; } = new();
    internal VideoTextInput TextEditor { get; } = new();
    internal VideoObjectTimeline Tracks { get; } = new();
    internal VideoObject[] Objects { get; private set; } = Array.Empty<VideoObject>();
    internal VideoObject[] PreviewObjects => TextEditor.Active ? Objects.Where(o => o.Id != _editingId).ToArray() : Objects;
    internal bool Importing { get; private set; }
    internal event Action<bool>? Changed;
    internal event Action<double>? SeekRequested;
    internal event Action? AvailabilityChanged;
    internal VideoObjectEditor(Window owner, string source, Func<double> position, Func<double> end, Action pause, CancellationToken token)
    {
        _owner = owner; _source = source; _position = position; _end = end; _pause = pause; _token = token;
        Overlay.InputBlocked = () => ColorEditing || PickingColor || _colorReleasePending;
        owner.PreviewMouseLeftButtonDown += (_, e) => { if (_colorReleasePending || ColorEditing && !PickingColor) e.Handled = true; };
        owner.PreviewMouseLeftButtonUp += (_, e) => { if (_colorReleasePending) { _colorReleasePending = false; e.Handled = true; } };
        Name = "VideoObjectEditor"; Margin = new Thickness(8, 4, 8, 8);
        Toolbar.MaxWidth = double.PositiveInfinity; Toolbar.HorizontalAlignment = HorizontalAlignment.Stretch;
        Toolbar.MainPanel.HorizontalAlignment = HorizontalAlignment.Stretch; Toolbar.PropertyPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        Toolbar.MainPanel.Effect = Toolbar.PropertyPanel.Effect = null;
        Toolbar.MainPanel.CornerRadius = Toolbar.PropertyPanel.CornerRadius = new CornerRadius(8);
        Toolbar.MainTools.HorizontalAlignment = HorizontalAlignment.Center;
        Toolbar.Properties.Children.Add(_properties); Children.Add(Toolbar);
        _hint.Visibility = Visibility.Collapsed; _hint.Margin = new Thickness(0, 3, 0, 0); Children.Add(_hint);
        _select = Button("scan", "选择对象 (E) · 拖动移动 / 手柄缩放 / Delete", () => SetTool(null));
        Toolbar.MainTools.Children.Add(_select); Toolbar.MainTools.Children.Add(ScreenshotToolbar.Separator());
        Toolbar.MainTools.Children.Add(_tools);
        var shape = Button("square", "形状 (R)", () => ShapeMenu(), true);
        _buttons[VideoObjectKind.Rectangle] = shape; _tools.Children.Add(shape);
        AddTool(VideoObjectKind.Brush, "pencil", "画笔 (B)"); AddTool(VideoObjectKind.Text, "type", "文字 (T) · 点击输入 / 双击重编");
        _tools.Children.Add(ScreenshotToolbar.Separator()); AddTool(VideoObjectKind.Mosaic, "mosaic-pixels", "马赛克 (M)");
        var pip = Button("picture-in-picture", "画中画 (P) · 图片 / 本地视频", () => ImportMenu(), true);
        _buttons[VideoObjectKind.PictureInPicture] = pip; _tools.Children.Add(pip);
        _topLayer = Button("bring-front", "置于顶层", () => MoveToEdge(true));
        _bottomLayer = Button("send-back", "置于底层", () => MoveToEdge(false));
        _propertyTimer.Tick += (_, _) => FlushProperties();
        _list.SelectionChanged += (_, _) => { if (!_refreshing) Select(_list.SelectedItem as VideoObject, true); };
        Overlay.SelectionChanged += o => Select(o, false); Tracks.SelectedObject += o => Select(o, true);
        Overlay.InteractionStarted += _pause; Tracks.InteractionStarted += _pause;
        Overlay.Drawn += Create; Overlay.Transformed += Replace; Tracks.Changed += Replace; Overlay.DeleteRequested += Delete;
        Overlay.TextRequested += BeginText;
        Overlay.ToolChanged += tool => { Tracks.ActiveTool = tool; Tracks.RefreshRows(); UpdateButtons(); if (!TextEditor.Active) ShowProperties(Overlay.Selected ?? (tool is { } kind ? Template(kind) : null)); };
        TextEditor.Confirmed += item =>
        {
            string? before = _editingId; _editingId = null;
            if (string.IsNullOrWhiteSpace(item.Text)) { if (before != null) { Objects = Objects.Where(o => o.Id != before).ToArray(); Changed?.Invoke(true); } Select(null, false); Refresh(); Changed?.Invoke(false); }
            else { if (before == null) Add(item); else Replace(item, true); if (!item.Active(_position(), _info.Fps)) SeekRequested?.Invoke(item.Start); }
            Overlay.Tool = VideoObjectKind.Text; Overlay.Cursor = Cursors.Cross; Overlay.Focus(); UpdateButtons();
            AvailabilityChanged?.Invoke();
        };
        TextEditor.Cancelled += () => { string? before = _editingId; _editingId = null; Select(Objects.FirstOrDefault(o => o.Id == before), false); Changed?.Invoke(false); AvailabilityChanged?.Invoke(); };
        TextEditor.Updated += () => { Overlay.Selected = null; Overlay.InvalidateVisual(); AvailabilityChanged?.Invoke(); };
        owner.Closed += (_, _) => { _propertyTimer.Stop(); FinishColor(false); _eyedropper?.Dispose(); };
        owner.Deactivated += (_, _) => { if (_picking) CancelPicker(); };
    }
    internal static Button Button(string icon, string tooltip, Action action, bool dropdown = false)
    {
        var button = ScreenshotToolbar.IconButton(icon, tooltip, action, dropdown); button.Focusable = true;
        button.Name = "VideoTool" + icon.Replace("-", "");
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        ToolTipService.SetPlacement(button, System.Windows.Controls.Primitives.PlacementMode.Top); return button;
    }
    private void AddTool(VideoObjectKind kind, string icon, string tooltip)
    { var button = Button(icon, tooltip, () => SetTool(kind)); _buttons[kind] = button; _tools.Children.Add(button); }
    private void ShapeMenu()
    {
        var menu = Menu();
        foreach (var (kind, label) in new[] { (VideoObjectKind.Rectangle, "矩形"), (VideoObjectKind.RoundedRectangle, "圆角矩形"), (VideoObjectKind.Ellipse, "椭圆"), (VideoObjectKind.Arrow, "箭头"), (VideoObjectKind.Line, "线条") })
        { var option = new MenuItem { Header = label }; option.Click += (_, _) => SetTool(kind); menu.Items.Add(option); }
        menu.PlacementTarget = _buttons[VideoObjectKind.Rectangle]; menu.IsOpen = true;
    }
    private void ImportMenu()
    {
        var menu = Menu();
        foreach (string label in new[] { "导入图片", "导入视频" })
        { var option = new MenuItem { Header = label }; option.Click += async (_, _) => await ImportAsync(false, label == "导入视频"); menu.Items.Add(option); }
        menu.PlacementTarget = _buttons[VideoObjectKind.PictureInPicture]; menu.IsOpen = true;
    }
    internal void OpenImportMenu() => ImportMenu();
    internal static ContextMenu Menu()
    { var menu = new ContextMenu(); menu.SetResourceReference(Control.BackgroundProperty, "PanelBackground"); menu.SetResourceReference(Control.ForegroundProperty, "TextPrimary"); return menu; }
    private void UpdateButtons()
    {
        VideoObjectKind? current = Overlay.Tool ?? TextEditor.Draft?.Kind ?? Overlay.Selected?.Kind;
        _select.Tag = current == null ? "selected" : null;
        foreach (var (kind, button) in _buttons) button.Tag = current == kind || (kind == VideoObjectKind.Rectangle && current is VideoObjectKind.RoundedRectangle or VideoObjectKind.Ellipse or VideoObjectKind.Arrow or VideoObjectKind.Line) ? "selected" : null;
        _topLayer.IsEnabled = _bottomLayer.IsEnabled = Overlay.Selected != null && !TextEditor.Active && !ColorEditing;
    }
    internal void Initialize(VideoInfo info, MediaCapabilities tools)
    { _info = info; _capabilities = tools; Overlay.Info = info; Tracks.Duration = info.Duration; Tracks.Fps = info.Fps; _tools.IsEnabled = tools.CanEffects; if (!tools.CanEffects) Hint("对象编辑需要可用的 FFmpeg / ffprobe。"); Refresh(); }
    internal void SetObjectAvailability(bool enabled) => _tools.IsEnabled = enabled && _capabilities.CanEffects;
    internal void Restore(VideoObject[]? objects)
    { FinishColor(false); TextEditor.Cancel(); Objects = VideoObjectRules.Validate(objects, _info); Select(null, false); Refresh(); }
    internal void RestoreDefaults(VideoObject[]? defaults)
    {
        _defaults.Clear();
        foreach (var item in defaults ?? Array.Empty<VideoObject>()) { if (item.Kind == VideoObjectKind.PictureInPicture) continue; item.Validate(_info); StoreDefault(item); }
    }
    // Defaults describe a future tool operation, never an object in the timeline.
    private void StoreDefault(VideoObject item) => _defaults[item.Kind] = item with { Id = "new", Name = null };
    internal void AddLayerButtons(WrapPanel tools) { tools.Children.Add(_topLayer); tools.Children.Add(_bottomLayer); }
    internal void SetLayerAvailability(bool enabled) { _topLayer.IsEnabled = _bottomLayer.IsEnabled = enabled && Overlay.Selected != null && !TextEditor.Active && !ColorEditing; }
    internal void UpdateTime(double time) { Overlay.Time = time; Tracks.Position = time; Overlay.InvalidateVisual(); Tracks.InvalidateVisual(); }
    internal void SetView(Rect view) { Overlay.View = view; TextEditor.SetView(view); Overlay.InvalidateVisual(); }
    internal bool CommitText() { FinishColor(false); FlushProperties(); return TextEditor.Confirm(); }
    internal void ShowExternalProperties(UIElement? content)
    {
        if (_external != null) Toolbar.Properties.Children.Remove(_external);
        _external = content;
        if (content != null) { _properties.Visibility = Visibility.Collapsed; Toolbar.Properties.Children.Add(content); Toolbar.PropertyPanel.Visibility = Visibility.Visible; }
        else ShowProperties(Overlay.Selected ?? (Overlay.Tool is { } kind ? Template(kind) : null));
    }
    internal void SetTool(VideoObjectKind? tool)
    {
        if (!_capabilities.CanEffects || !IsEnabled || !CommitText()) return;
        _pause(); Hint(""); ShowExternalProperties(null); Overlay.Cancel(); Overlay.Selected = null; Select(null, false);
        Overlay.Tool = tool; Overlay.Cursor = tool == null ? Cursors.Arrow : Cursors.Cross;
        Overlay.ForceNewText = false;
        if (tool == null) ShowProperties(null); else ShowProperties(Template(tool.Value));
        UpdateButtons(); Overlay.Focus();
    }
    private VideoObject Template(VideoObjectKind kind)
    {
        double start = VideoCuts.Snap(Math.Max(0, Math.Min(_position(), _end() - 1 / _info.Fps)), _info.Fps, _info.Duration);
        var item = _defaults.TryGetValue(kind, out var stored) ? stored : new VideoObject("new", kind, 0, 0, Math.Min(720, Math.Max(1, _info.Width * .5)), Math.Min(80, Math.Max(1, _info.Height)), start, _end(), 0, Stroke: kind == VideoObjectKind.Mosaic ? 24 : 4);
        return item with { Id = "new", Name = null, Text = kind == VideoObjectKind.Text ? "" : item.Text, X = 0, Y = 0, Start = start, End = _end(), Width = Math.Min(item.Width, Math.Max(1, _info.Width)), Height = Math.Min(item.Height, Math.Max(1, _info.Height)), Points = kind == VideoObjectKind.Brush || kind == VideoObjectKind.Mosaic && item.MosaicBrush ? new[] { new VideoPoint(0, 0) } : item.Points };
    }
    internal void BeginText(Point sourcePoint, VideoObject? existing)
    {
        if (!IsEnabled || !_capabilities.CanEffects || !CommitText()) return;
        _pause(); ShowExternalProperties(null); _editingId = existing?.Id;
        var template = Template(VideoObjectKind.Text);
        double minimumWidth = Math.Min(_info.Width, Math.Max(80, template.FontSize + 2 * AnnotationGeometry.TextInset(template.TextAnnotation()) + 4));
        double x = Math.Clamp(sourcePoint.X, 0, _info.Width - (existing == null ? minimumWidth : 1)), y = Math.Clamp(sourcePoint.Y, 0, _info.Height - 1);
        var item = existing ?? Template(VideoObjectKind.Text) with { Id = Guid.NewGuid().ToString("N"), X = x, Y = y, Width = Math.Min(Template(VideoObjectKind.Text).Width, _info.Width - x), Layer = Objects.Length == 0 ? 0 : Objects.Max(o => o.Layer) + 1 };
        Overlay.Tool = VideoObjectKind.Text; Overlay.Selected = null;
        TextEditor.Begin(item, _info, Overlay.View); if (!item.Active(_position(), _info.Fps)) SeekRequested?.Invoke(item.Start);
        ShowProperties(TextEditor.Draft); UpdateButtons(); Changed?.Invoke(false);
    }
    private void Create(VideoObjectKind kind, Point start, Point end, VideoPoint[] points)
    {
        bool path = kind == VideoObjectKind.Brush || kind == VideoObjectKind.Mosaic && Overlay.MosaicBrush;
        Rect bounds = path ? AnnotationGeometry.Bounds(new(AnnotationTool.Freehand, start, end, Colors.Red, Points: points.Select(p => new Point(p.X, p.Y)).ToArray())) : new Rect(start, end);
        bounds = new Rect(Math.Clamp(bounds.X, 0, _info.Width - 1), Math.Clamp(bounds.Y, 0, _info.Height - 1), Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        var item = Template(kind) with { Id = Guid.NewGuid().ToString("N"), X = bounds.X, Y = bounds.Y, Width = Math.Min(bounds.Width, _info.Width - bounds.X), Height = Math.Min(bounds.Height, _info.Height - bounds.Y), Layer = Objects.Length == 0 ? 0 : Objects.Max(o => o.Layer) + 1, Points = path ? points : null, ReverseX = end.X < start.X, ReverseY = end.Y < start.Y };
        Add(item);
    }
    internal void Add(VideoObject item)
    {
        if (item.Name == null && item.Kind is VideoObjectKind.Text or VideoObjectKind.PictureInPicture)
        {
            string label = item.Kind == VideoObjectKind.Text ? "文字" : "画中画"; int number = 1 + Objects.Count(o => o.Kind == item.Kind);
            while (Objects.Any(o => o.Label == $"{label} {number}")) number++;
            item = item with { Name = $"{label} {number}" };
        }
        item.Validate(_info); item = item.FitText(_info); item.Validate(_info); Objects = VideoObjectRules.Validate(Objects.Append(item), _info); Overlay.Tool = null; Select(item, false); Refresh(); Changed?.Invoke(true);
    }
    internal void Replace(VideoObject item, bool commit)
    {
        item.Validate(_info); item = item.FitText(_info); item.Validate(_info); Objects = Objects.Select(o => o.Id == item.Id ? item : o).OrderBy(o => o.Layer).ToArray();
        Overlay.Selected = Tracks.Selected = item; Refresh(!commit); if (commit) ShowProperties(item); Changed?.Invoke(commit);
    }
    private void Refresh(bool duringDrag = false)
    {
        Overlay.Objects = Tracks.Objects = Objects; Tracks.RefreshRows(); Overlay.InvalidateVisual(); UpdateButtons();
        if (duringDrag) return;
        _refreshing = true; _list.ItemsSource = Objects; _list.SelectedItem = Objects.FirstOrDefault(o => o.Id == Overlay.Selected?.Id); _refreshing = false;
    }
    internal void Select(VideoObject? item, bool seek)
    {
        FinishColor(false);
        if (TextEditor.Active && !CommitText()) return;
        FlushProperties(); Overlay.Selected = Tracks.Selected = item; Overlay.Tool = null;
        _refreshing = true; _list.SelectedItem = item; _refreshing = false; ShowProperties(item); UpdateButtons(); Overlay.InvalidateVisual(); Tracks.InvalidateVisual();
        if (item != null && seek) { _pause(); SeekRequested?.Invoke(item.Start); }
    }
    internal void Delete()
    { if (!CommitText() || Overlay.Selected is not { } item) return; _pause(); Objects = Objects.Where(o => o.Id != item.Id).ToArray(); Select(null, false); Refresh(); Changed?.Invoke(true); }
    private void Layer(int direction)
    {
        if (Overlay.Selected is not { } selected) return;
        var sorted = Objects.OrderBy(o => o.Layer).ToArray(); int i = Array.FindIndex(sorted, o => o.Id == selected.Id), other = i + direction;
        if (other < 0 || other >= sorted.Length) return;
        (sorted[i], sorted[other]) = (sorted[other], sorted[i]); Objects = sorted.Select((o, index) => o with { Layer = index }).ToArray(); Select(Objects.First(o => o.Id == selected.Id), false); Refresh(); Changed?.Invoke(true);
    }
    internal void MoveToEdge(bool top)
    {
        if (!CommitText() || Overlay.Selected is not { } selected) return;
        _pause(); var sorted = Objects.OrderBy(o => o.Layer).Where(o => o.Id != selected.Id).ToList();
        sorted.Insert(top ? sorted.Count : 0, selected);
        var reordered = sorted.Select((o, i) => o with { Layer = i }).ToArray();
        if (Objects.SequenceEqual(reordered)) return;
        Objects = reordered; Select(Objects.First(o => o.Id == selected.Id), false); Refresh(); Changed?.Invoke(true);
    }
    internal void FlushProperties() { if (!_propertyTimer.IsEnabled) return; _propertyTimer.Stop(); Changed?.Invoke(true); }
    private void Hint(string value) { _hint.Text = value; _hint.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; }
    internal static Color EffectiveColor(VideoObject item, bool fill)
    {
        string hex = fill ? item.Fill : item.Color;
        var color = hex == "none" ? Colors.Transparent : (Color)ColorConverter.ConvertFromString(hex);
        color.A = (byte)Math.Round(color.A * (fill ? item.FillAlpha : item.StrokeAlpha)); return color;
    }
    private bool? _colorFrameFill;
    internal void OpenColor(Button anchor, VideoObject item, bool fill, string label, bool outline = false, bool? frameFill = null)
    {
        FinishColor(false); FlushProperties(); _pause();
        _colorBefore = item; _colorFill = fill; _colorOutline = outline; _colorCreating = item.Id == "new"; _colorText = TextEditor.Active; _defaultExisted = _defaults.ContainsKey(item.Kind);
        _colorFrameFill = frameFill;
        _palette = new ColorPalette(color => { PreviewColor(color); FinishColor(true); }, () => { }, () => { if (ColorItem() is { } current) SetColorItem(frameFill == true ? current with { TextFrame = (current.TextFrame ?? new()) with { Fill = "none" } } : current with { Fill = "none" }); FinishColor(true); }, screen => { _ = StartFramePicker(); }, label, frameFill ?? fill, PreviewColor, () => FinishColor(false));
        _palette.SetCurrent(frameFill is { } frameTarget ? FrameColor(item, frameTarget) : outline ? OutlineColor(item) : EffectiveColor(item, fill && item.Fill != "none"), frameFill ?? fill, frameFill != null ? item.TextFrame?.Fill is not (null or "none") : item.Fill != "none");
        _palette.SetPickerAvailable(FrameProvider != null && _info.Width > 0 && _info.Height > 0, "当前原片帧不可用");
        var border = _palette.CreateHost(anchor);
        _colorPopup = new Popup { Child = border, PlacementTarget = anchor, Placement = PlacementMode.Bottom, AllowsTransparency = true, StaysOpen = false };
        _colorPopup.Closed += (_, _) => { if (!_picking) FinishColor(false); };
        _colorPopup.IsOpen = true; AvailabilityChanged?.Invoke();
    }
    private VideoObject? ColorItem()
    {
        if (_colorBefore is not { } before) return null;
        if (_colorText) return TextEditor.Draft is { } draft && draft.Id == before.Id ? draft : null;
        if (_colorCreating) return _defaults.GetValueOrDefault(before.Kind, before);
        return Objects.FirstOrDefault(o => o.Id == before.Id);
    }
    private void SetColorItem(VideoObject item)
    {
        if (_colorText) TextEditor.Update(item);
        else if (_colorCreating) StoreDefault(item);
        else { Objects = Objects.Select(o => o.Id == item.Id ? item : o).ToArray(); Overlay.Selected = Tracks.Selected = item; Refresh(true); }
        Changed?.Invoke(false);
    }
    internal void PreviewColor(Color color)
    {
        if (_colorBefore == null) return;
        string rgb = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (ColorItem() is not { } item) { FinishColor(false); return; }
        if (_colorFrameFill is { } fill) { var frame = item.TextFrame ?? new(); SetColorItem(item with { TextFrame = fill ? frame with { Fill = TextFrameStyle.Hex(color) } : frame with { Color = TextFrameStyle.Hex(color) } }); }
        else SetColorItem(_colorOutline ? item with { OutlineColor = rgb, OutlineOpacity = color.A / 255d } : _colorFill ? item with { Fill = rgb, FillOpacity = color.A / 255d } : item with { Color = rgb, StrokeOpacity = color.A / 255d });
    }
    internal void FinishColor(bool commit)
    {
        if (_colorBefore is not { } before) return;
        _colorReleasePending |= Mouse.LeftButton == MouseButtonState.Pressed;
        var item = ColorItem(); _colorBefore = null; _picking = false; _pickerGeneration++; _eyedropper?.Cancel();
        if (item == null) commit = false;
        if (commit && item is { } current && (_colorFrameFill == null ? !_colorFill || current.Fill != "none" : _colorFrameFill != true || current.TextFrame?.Fill is not (null or "none"))) AnnotationColors.Remember(_colorFrameFill is { } frameTarget ? FrameColor(current, frameTarget) : _colorOutline ? OutlineColor(current) : EffectiveColor(current, _colorFill));
        if (_colorPopup != null) { _colorPopup.IsOpen = false; _colorPopup = null; }
        if (!commit && item != null)
        {
            if (_colorText) TextEditor.Update(before);
            else if (_colorCreating) { if (_defaultExisted) StoreDefault(before); else _defaults.Remove(before.Kind); }
            else { Objects = Objects.Select(o => o.Id == before.Id ? before : o).ToArray(); Overlay.Selected = Tracks.Selected = before; }
        }
        else if (commit && _colorText && item != null) StoreDefault(item);
        if (item == null && Overlay.Selected?.Id == before.Id) Overlay.Selected = Tracks.Selected = null;
        Refresh(); ShowProperties(TextEditor.Active ? TextEditor.Draft : Overlay.Selected ?? (Overlay.Tool is { } kind ? Template(kind) : null));
        Changed?.Invoke(commit); AvailabilityChanged?.Invoke();
    }
    internal async Task StartFramePicker()
    {
        if (_colorBefore == null || _palette == null || _picking) return;
        if (FrameProvider == null) { _palette.SetStatus("当前原片帧不可用，无法取色。"); return; }
        _pause(); _picking = true; int generation = ++_pickerGeneration; var palette = _palette;
        palette.SetStatus("正在读取原片帧…"); Hint("正在读取原片帧，Esc 可取消取色。");
        if (_colorPopup != null) _colorPopup.IsOpen = false; AvailabilityChanged?.Invoke();
        try
        {
            var frame = await FrameProvider(); if (_colorBefore == null || _token.IsCancellationRequested || generation != _pickerGeneration) return;
            // Let the palette's native popup release its input before the
            // picker captures the editor window.
            Mouse.Capture(null);
            await Dispatcher.Yield(DispatcherPriority.Input);
            if (_colorBefore == null || _token.IsCancellationRequested || generation != _pickerGeneration) return;
            _eyedropper?.Dispose();
            _eyedropper = new PixelEyedropper(_owner, (FrameworkElement)Overlay, color =>
            {
                if (_colorBefore == null || generation != _pickerGeneration) return;
                _picking = false; Hint("");
                if (color is { } chosen) { chosen.A = palette.Draft.A; palette.PreviewColor(chosen); }
                if (_colorBefore == null || generation != _pickerGeneration) return;
                palette.SetStatus(color == null ? "已取消取色，颜色草稿保留。" : "已取色，点击应用颜色确认。");
                if (_colorPopup != null) _colorPopup.IsOpen = true; AvailabilityChanged?.Invoke();
            }, message => palette.SetStatus(message));
            palette.SetStatus("原片取色：单击确认 · Esc / 右键取消；保持暂停"); Hint("原片取色：单击画面取色，Esc / 右键取消。");
            _eyedropper.BeginFrame(frame, point => FramePoint(point, Overlay.View, Overlay.ActualWidth, Overlay.ActualHeight), visible => { Overlay.ShowSelection = visible; Overlay.InvalidateVisual(); });
        }
        catch (OperationCanceledException) { if (generation == _pickerGeneration) CancelPicker(); }
        catch (Exception ex)
        {
            if (generation != _pickerGeneration || _colorBefore == null) return;
            CancelPicker(); palette.SetStatus("原片帧读取失败：" + ex.Message);
        }
    }
    internal void CancelPicker()
    {
        if (!_picking) return;
        _picking = false; _pickerGeneration++; _eyedropper?.Cancel(); Hint("");
        if (_colorBefore == null) return;
        _palette?.SetStatus("已取消取色，颜色草稿保留。");
        if (_colorPopup != null && _owner.IsVisible) _colorPopup.IsOpen = true;
        AvailabilityChanged?.Invoke();
    }
    internal static Point FramePoint(Point local, Rect sourceView, double displayWidth, double displayHeight)
        => new(sourceView.X + local.X / Math.Max(.001, displayWidth) * sourceView.Width, sourceView.Y + local.Y / Math.Max(.001, displayHeight) * sourceView.Height);
    private static Color OutlineColor(VideoObject item) { var color = (Color)ColorConverter.ConvertFromString(item.OutlineColor); color.A = (byte)Math.Round(color.A * item.OutlineAlpha); return color; }
    private static Color FrameColor(VideoObject item, bool fill) => fill ? (item.TextFrame ?? new()).FillColor ?? Colors.White : (item.TextFrame ?? new()).StrokeColor;
    private void ShowProperties(VideoObject? item)
    {
        if (_external != null) return;
        Hint("");
        Overlay.DraftStyle = item;
        _properties.Children.Clear(); _properties.Visibility = Visibility.Visible;
        Toolbar.PropertyPanel.Visibility = item == null ? Visibility.Collapsed : Visibility.Visible; if (item == null) return;
        bool draft = TextEditor.Active, creating = item.Id == "new";
        VideoObject Current() => draft ? TextEditor.Draft ?? item : creating ? _defaults.GetValueOrDefault(item.Kind, item) with { Id = "new", Name = null } : Objects.FirstOrDefault(o => o.Id == item.Id) ?? item;
        void Change(Func<VideoObject, VideoObject> mutate)
        {
            if (_refreshing) return;
            try
            {
                var updated = mutate(Current());
                if (!creating || updated.Kind != VideoObjectKind.PictureInPicture) updated.Validate(_info);
                updated = updated.FitText(_info);
                Overlay.DraftStyle = updated; Overlay.InvalidateVisual();
                if (draft) { TextEditor.Update(updated); StoreDefault(updated); }
                else if (creating) { StoreDefault(updated); _propertyTimer.Stop(); _propertyTimer.Start(); }
                else { Objects = Objects.Select(o => o.Id == updated.Id ? updated : o).ToArray(); Overlay.Selected = Tracks.Selected = updated; Refresh(true); Changed?.Invoke(false); _propertyTimer.Stop(); _propertyTimer.Start(); }
                foreach (var button in _properties.Children.OfType<Button>().Where(b => b.ToolTip is string tip && tip.StartsWith("颜色 #", StringComparison.Ordinal))) button.Tag = (string)button.ToolTip == "颜色 " + updated.Color ? "selected" : null;
                Hint("");
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException or OverflowException) { Hint(ex.Message); }
        }
        TextBox Field(string label, string value, double width, Action<string> apply)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 8, 2) };
            if (label.Length > 0) row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var box = new TextBox { Text = value, Width = width, ToolTip = label, Padding = new Thickness(6, 4, 6, 4), MinHeight = 30 }; row.Children.Add(box); _properties.Children.Add(row);
            box.TextChanged += (_, _) => apply(box.Text);
            box.LostKeyboardFocus += (_, _) => FlushProperties();
            box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { FlushProperties(); e.Handled = true; } }; return box;
        }
        void Number(string label, double value, double width, Func<VideoObject, double, VideoObject> set)
        { Field(label, value.ToString("0.###", CultureInfo.InvariantCulture), width, text => { if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) Change(o => set(o, number)); }); }
        ComboBox Choice(string label, string[] values, int index, double width, Action<int> apply)
        { var choice = new ComboBox { ItemsSource = values, SelectedIndex = index, Width = width, MinWidth = 0, ToolTip = label, Margin = new Thickness(0, 2, 8, 2), MinHeight = 30 }; _properties.Children.Add(choice); choice.SelectionChanged += (_, _) => { if (choice.SelectedIndex >= 0) apply(choice.SelectedIndex); }; return choice; }
        if (item.Kind is VideoObjectKind.Rectangle or VideoObjectKind.RoundedRectangle or VideoObjectKind.Ellipse or VideoObjectKind.Arrow or VideoObjectKind.Line)
        {
            var kinds = new[] { VideoObjectKind.Rectangle, VideoObjectKind.RoundedRectangle, VideoObjectKind.Ellipse, VideoObjectKind.Arrow, VideoObjectKind.Line };
            Choice("形状", new[] { "矩形", "圆角矩形", "椭圆", "箭头", "线条" }, Array.IndexOf(kinds, item.Kind), 110, i => { if (creating) SetTool(kinds[i]); else { Change(o => o with { Kind = kinds[i] }); FlushProperties(); ShowProperties(Current()); } });
        }
        if (item.Kind == VideoObjectKind.Text)
        {
            string[] fonts = new[] { item.FontFamily, "Microsoft YaHei UI", "SimSun", "Segoe UI", "Arial", "Consolas" }.Distinct().ToArray();
            Choice("字体", fonts, 0, 146, i => Change(o => o with { FontFamily = fonts[i] }));
            var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 8, 2) };
            fontRow.Children.Add(new TextBlock { Text = "字号", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var fontSize = new ComboBox { Name = "VideoFontSize", IsEditable = true, IsTextSearchEnabled = false, Width = 78, MinWidth = 60, MinHeight = 30, Style = (Style)_owner.FindResource("VideoFontSizeCombo"), ItemsSource = new[] { "12", "14", "16", "18", "20", "24", "28", "32", "36", "48", "60", "72", "96" }, Text = item.FontSize.ToString("0.###", CultureInfo.InvariantCulture), ToolTip = "字号：4–300 原片像素；可选择常用值或输入" };
            InputMethod.SetIsInputMethodEnabled(fontSize, false); fontRow.Children.Add(fontSize); fontRow.Children.Add(new TextBlock { Text = " px", VerticalAlignment = VerticalAlignment.Center }); _properties.Children.Add(fontRow);
            fontSize.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => { if (double.TryParse(fontSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && n >= 4 && n <= 300) Change(o => o with { FontSize = n }); }));
            fontSize.LostKeyboardFocus += (_, _) => FlushProperties();
            var bold = Button("bold", "粗体", () => { Change(o => o with { Bold = !o.Bold }); ShowProperties(Current()); }); bold.Tag = item.Bold ? "selected" : null; _properties.Children.Add(bold);
            var italic = Button("italic", "斜体", () => { Change(o => o with { Italic = !o.Italic }); ShowProperties(Current()); }); italic.Tag = item.Italic ? "selected" : null; _properties.Children.Add(italic);
            var alignments = new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right };
            Choice("文字对齐", new[] { "左对齐", "居中", "右对齐" }, Array.IndexOf(alignments, item.Alignment), 88, i => Change(o => o with { Alignment = alignments[i] }));
        }
        void WidthChoice(string label, double value, Func<VideoObject, double, VideoObject> set)
        {
            var choice = ThicknessOptions.Create(value, label, n => Change(o => set(o, n)));
            choice.LostKeyboardFocus += (_, _) => FlushProperties(); _properties.Children.Add(ThicknessOptions.Group(label, choice));
        }
        void ColorButton(string label, bool fill, bool outline = false, string? name = null)
        {
            var color = outline ? OutlineColor(item) : EffectiveColor(item, fill);
            ColorDisplay? button = null; button = new ColorDisplay(label, () => OpenColor(button!, Current(), fill, label, outline)); button.Name = name ?? (outline ? "VideoOutlineColor" : fill ? "VideoFillColor" : "VideoStrokeColor");
            button.SetColor(fill && item.Fill == "none" ? null : color);
            button.ToolTip = label + (fill && item.Fill == "none" ? " · 无填充" : " · " + AnnotationColors.Hex(color)) + " · 调色盘 / 原片吸管";
            _properties.Children.Add(ThicknessOptions.Group(label, button));
        }
        bool closedShape = item.Kind is VideoObjectKind.Rectangle or VideoObjectKind.RoundedRectangle or VideoObjectKind.Ellipse;
        bool shape = closedShape || item.Kind is VideoObjectKind.Arrow or VideoObjectKind.Line;
        if (item.Kind is not (VideoObjectKind.Mosaic or VideoObjectKind.PictureInPicture))
        {
            ColorButton(shape ? "形状颜色" : item.Kind == VideoObjectKind.Text ? "文字颜色" : "画笔颜色", closedShape, name: "VideoPrimaryColor");
            if (item.Kind == VideoObjectKind.Brush) WidthChoice("画笔粗细", item.Stroke, (o, n) => o with { Stroke = n });
            if (closedShape)
            {
                var enabled = new CheckBox { Content = "描边", IsChecked = item.StrokeEnabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
                enabled.Checked += (_, _) => Change(o => o with { StrokeEnabled = true }); enabled.Unchecked += (_, _) => Change(o => o with { StrokeEnabled = false }); _properties.Children.Add(enabled);
                ColorButton("描边颜色", false); WidthChoice("描边粗细", item.Stroke, (o, n) => o with { Stroke = n });
            }
            else
            {
                if (item.Kind is VideoObjectKind.Arrow or VideoObjectKind.Line) WidthChoice("粗细", item.Stroke, (o, n) => o with { Stroke = n });
                var enabled = new CheckBox { Content = "描边", IsChecked = item.OutlineEnabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
                enabled.Checked += (_, _) => Change(o => o with { OutlineEnabled = true }); enabled.Unchecked += (_, _) => Change(o => o with { OutlineEnabled = false }); _properties.Children.Add(enabled);
                ColorButton("描边颜色", false, true); WidthChoice("描边粗细", item.OutlineWidth, (o, n) => o with { OutlineWidth = n });
            }
        }
        if (item.Kind == VideoObjectKind.Mosaic)
        {
            Overlay.MosaicBrush = item.MosaicBrush;
            Choice("马赛克模式", new[] { "框选马赛克", "画笔马赛克" }, item.MosaicBrush ? 1 : 0, 130, i => { Change(o => o with { MosaicBrush = i == 1, Points = i == 1 ? o.Points ?? new[] { new VideoPoint(o.X, o.Y), new VideoPoint(o.X + o.Width, o.Y + o.Height) } : null }); Overlay.MosaicBrush = i == 1; ShowProperties(Current()); });
            if (item.MosaicBrush) WidthChoice("画笔粗细", item.Stroke, (o, n) => o with { Stroke = n });
            var block = ThicknessOptions.CreateValue(item.BlockSize, "像素块：2–128 原始像素", new[] { "4", "8", "12", "16", "24", "32", "48", "64" }, 2, 128, n => { if (n == Math.Truncate(n)) Change(o => o with { BlockSize = (int)n }); });
            _properties.Children.Add(ThicknessOptions.Group("像素块", block));
        }
        else if (item.Kind == VideoObjectKind.PictureInPicture) Number("透明度%", item.Opacity * 100, 58, (o, n) => o with { Opacity = n / 100 });
        if (item.Kind == VideoObjectKind.Text)
        {
            var frame = item.TextFrame ?? new();
            var enabled = new CheckBox { Name = "VideoTextFrameEnabled", Content = "文字边框", IsChecked = frame.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
            enabled.Checked += (_, _) => Change(o => o with { TextFrame = (o.TextFrame ?? new()) with { Enabled = true } });
            enabled.Unchecked += (_, _) => Change(o => o with { TextFrame = (o.TextFrame ?? new()) with { Enabled = false } }); _properties.Children.Add(enabled);
            var frameShape = Choice("边框形状", new[] { "矩形", "圆角矩形" }, frame.Shape == AnnotationShape.RoundedRectangle ? 1 : 0, 98, i => Change(o => o with { TextFrame = (o.TextFrame ?? new()) with { Shape = i == 0 ? AnnotationShape.Rectangle : AnnotationShape.RoundedRectangle } }));
            _properties.Children.Remove(enabled); _properties.Children.Remove(frameShape);
            var frameGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 6, 2) }; frameGroup.Children.Add(enabled); frameGroup.Children.Add(frameShape); _properties.Children.Add(frameGroup);
            void FrameButton(string label, bool fill)
            {
                ColorDisplay? button = null; button = new ColorDisplay(label, () => OpenColor(button!, Current(), false, label, frameFill: fill)); button.Name = fill ? "VideoTextFrameFill" : "VideoTextFrameColor";
                button.SetColor(fill && Current().TextFrame?.Fill is null or "none" ? null : FrameColor(Current(), fill));
                _properties.Children.Add(ThicknessOptions.Group(label, button));
            }
            FrameButton("边框颜色", false); WidthChoice("边框粗细", frame.Width, (o, n) => o with { TextFrame = (o.TextFrame ?? new()) with { Width = n } }); FrameButton("框内填充", true);
            _properties.Children.Add(Ui.Button("新增文字", () => { SetTool(VideoObjectKind.Text); Overlay.ForceNewText = true; }));
        }
        if (item.AssetVideo) Number("素材起点", item.AssetStart, 68, (o, n) => o with { AssetStart = n });
        if (!creating && !draft)
        {
            _list.Margin = new Thickness(0, 2, 8, 2); _properties.Children.Add(_list);
            _properties.Children.Add(Button("x", "删除对象 (Delete)", Delete));
            _properties.Children.Add(Button("layer-up", "前移一层", () => Layer(1))); _properties.Children.Add(Button("layer-down", "后移一层", () => Layer(-1)));
        }
        if (draft)
        {
            var confirm = Ui.Button("完成文字", () => TextEditor.Confirm()); confirm.ToolTip = "Ctrl+Enter · 完成文字"; confirm.Style = (Style)_owner.FindResource("RecordingPrimaryButton"); _properties.Children.Add(confirm);
            _properties.Children.Add(Ui.Button("取消", TextEditor.Cancel));
        }
        else if (item.Kind == VideoObjectKind.Text && !creating) _properties.Children.Add(Button("type", "重新编辑文字 · 双击画面文字", () => BeginText(new(item.X, item.Y), Current())));
        if (item.Kind == VideoObjectKind.PictureInPicture)
        {
            if (creating) { _properties.Children.Add(Ui.Button("导入素材", async () => await ImportAsync(false))); return; }
            foreach (var (icon, label, x, y) in new[] { ("position-top-left", "左上", -1, -1), ("position-top-right", "右上", 1, -1), ("position-bottom-left", "左下", -1, 1), ("position-bottom-right", "右下", 1, 1), ("position-center", "中心", 0, 0) })
                _properties.Children.Add(Button(icon, "画中画位置：" + label, () => Replace(PositionPicture(Current(), Overlay.View, _info, x, y), true)));
            _properties.Children.Add(Ui.Button("重新定位素材", async () => await ImportAsync(true)));
            _properties.ToolTip = item.AssetVideo ? "素材默认静音；播放结束后小窗消失。" : "图片素材随项目保留。";
            if (!File.Exists(RecordingEditStore.AssetPath(_source, item.Asset!))) Hint("素材缺失，请重新定位素材。");
        }
    }
    internal static VideoObject PositionPicture(VideoObject current, Rect view, VideoInfo info, int x, int y)
    {
        if (view.IsEmpty || view.Width <= 0 || view.Height <= 0) view = new Rect(0, 0, info.Width, info.Height);
        double fit = Math.Min(1, Math.Min(view.Width / current.Width, view.Height / current.Height)); double w = current.Width * fit, h = current.Height * fit;
        double marginX = Math.Min(12, Math.Max(0, (view.Width - w) / 2)), marginY = Math.Min(12, Math.Max(0, (view.Height - h) / 2));
        return current with { Width = w, Height = h, X = Math.Clamp(x == 0 ? view.X + (view.Width - w) / 2 : x < 0 ? view.X + marginX : view.Right - w - marginX, 0, info.Width - w), Y = Math.Clamp(y == 0 ? view.Y + (view.Height - h) / 2 : y < 0 ? view.Y + marginY : view.Bottom - h - marginY, 0, info.Height - h) };
    }
    private async Task ImportAsync(bool relocate, bool? videoOnly = null)
    {
        if (Importing || !_capabilities.CanEffects) return;
        var original = relocate ? Overlay.Selected : null;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "导入画中画素材", Filter = videoOnly == true ? "视频|*.mp4;*.webm;*.mov;*.mkv;*.gif" : videoOnly == false ? "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp" : "图片或视频|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.mp4;*.webm;*.mov;*.mkv;*.gif" };
        if (!relocate) SetTool(VideoObjectKind.PictureInPicture);
        _pause(); if (picker.ShowDialog(_owner) != true) { if (!relocate) Overlay.Tool = null; return; }
        Importing = true; IsEnabled = false; AvailabilityChanged?.Invoke(); Hint("正在读取并复制素材…");
        try
        {
            bool video = new[] { ".mp4", ".webm", ".mov", ".mkv", ".gif" }.Contains(Path.GetExtension(picker.FileName).ToLowerInvariant());
            int width, height; double duration = 0;
            if (video) { var info = await VideoExportService.ProbeAsync(picker.FileName, _capabilities, _token); width = info.Width; height = info.Height; duration = info.Duration; if (duration <= 0) throw new InvalidDataException("视频素材没有可用时长。"); }
            else { var size = await Task.Run(() => { using var bitmap = SKBitmap.Decode(picker.FileName) ?? throw new InvalidDataException("图片格式不可读。"); return (bitmap.Width, bitmap.Height); }, _token); width = size.Width; height = size.Height; }
            if ((long)width * height > 33_177_600) throw new InvalidDataException("素材超过 8K 像素规模，请先缩小素材。");
            string asset = await RecordingEditStore.ImportAssetAsync(_source, picker.FileName, _token); _token.ThrowIfCancellationRequested();
            double factor = Math.Min(_info.Width * .3 / width, _info.Height * .3 / height);
            double time = VideoCuts.Snap(Math.Min(_position(), _end() - 1 / _info.Fps), _info.Fps, _info.Duration);
            var item = original == null ? new VideoObject(Guid.NewGuid().ToString("N"), VideoObjectKind.PictureInPicture, _info.Width - width * factor, _info.Height - height * factor, width * factor, height * factor, time, _end(), Objects.Length == 0 ? 0 : Objects.Max(o => o.Layer) + 1) : original;
            item = item with { Asset = asset, AssetVideo = video, AssetWidth = width, AssetHeight = height, AssetDuration = duration, AssetStart = 0, Height = item.Width * height / width };
            if (item.Height > _info.Height) item = item with { Height = _info.Height, Width = _info.Height * width / (double)height };
            item = item with { X = Math.Clamp(item.X, 0, _info.Width - item.Width), Y = Math.Clamp(item.Y, 0, _info.Height - item.Height) };
            Hint(""); if (original == null) Add(item); else Replace(item, true);
        }
        catch (OperationCanceledException) { Hint(""); }
        catch (Exception ex) { ErrorLog.Write(ex); Hint("素材读取失败：" + ex.Message); }
        finally { if (!relocate) Overlay.Tool = null; Importing = false; IsEnabled = true; AvailabilityChanged?.Invoke(); }
    }
}
