using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
namespace QuickCapture;

// A 22-DIP ruler plus the 48-DIP editor; the main rail stays 12 DIP.
internal sealed class TrimTimeline : Canvas
{
    private const double Offset = 22;
    private readonly Border _track = new(), _range = new();
    private readonly Thumb _left = new(), _right = new(), _deleteLeft = new(), _deleteRight = new(), _playhead = new();
    private readonly System.Windows.Shapes.Rectangle _draftFill = new() { Height = 20, RadiusX = 5, RadiusY = 5,
        StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false, Visibility = Visibility.Collapsed, Name = "DraftIntervalBand" };
    private readonly TextBlock _draftText = new() { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _guide = new() { Width = 1, Height = 66, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock _guideText = new() { FontSize = 10, Visibility = Visibility.Collapsed, IsHitTestVisible = false, Padding = new Thickness(4, 2, 4, 2) };
    private readonly List<UIElement> _bands = new();
    private VideoRange? _draft;
    private double _draftLower, _draftUpper, _maximumDelete, _draftRate = 1.5;
    internal VideoRange? Draft => _draft;
    internal bool DraftIsSpeed { get; private set; }
    internal bool SnapEnabled { get; set; } = true;
    internal Func<IEnumerable<double>>? ObjectBoundaries { get; set; }
    private readonly TimelineSnapper _snap = new();
    private TimelineSnapTarget[] _targets = Array.Empty<TimelineSnapTarget>();
    private string _moving = "";
    private double _duration, _start, _end, _position, _fps = 30;
    private VideoRange[] _deleted = Array.Empty<VideoRange>();
    private VideoSpeedRange[] _speeds = Array.Empty<VideoSpeedRange>();
    internal event Action? DraftChanged, EditStarted, EditCompleted;
    internal event Action<int>? SpeedSelected, DeleteSelected;
    public event Action? RangeChanged;
    public event Action<double>? SeekRequested, RangePreviewRequested;
    internal double Start => _start;
    internal double End => _end;
    internal double Duration => _duration;
    private double Span => Math.Max(1, ActualWidth - 16);
    private double X(double seconds) => 8 + (_duration > 0 ? seconds / _duration * Span : 0);
    public TrimTimeline()
    {
        Name = "TrimTimeline"; Height = 70; MinWidth = 100; Background = Brushes.Transparent;
        _track.CornerRadius = _range.CornerRadius = new CornerRadius(4);
        _track.SetResourceReference(Border.BackgroundProperty, "TimelineTrack"); _range.SetResourceReference(Border.BackgroundProperty, "Accent");
        _range.Opacity = .65; Children.Add(_track); Children.Add(_range);
        Configure(_left, "TrimStart", true, false); Configure(_right, "TrimEnd", false, false);
        Children.Add(_draftFill); Children.Add(_draftText); Configure(_deleteLeft, "DeleteStartHandle", true, true); Configure(_deleteRight, "DeleteEndHandle", false, true);
        Panel.SetZIndex(_draftFill, 3); Panel.SetZIndex(_draftText, 4);
        _playhead.Width = 10; _playhead.Height = 42; _playhead.Cursor = Cursors.SizeWE; _playhead.Focusable = true; _playhead.Name = "TimelinePlayhead"; _playhead.ToolTip = "播放头：拖动预览原片；Alt 临时关闭吸附";
        var line = new FrameworkElementFactory(typeof(Border)); line.SetValue(Border.WidthProperty, 2d); line.SetResourceReference(Border.BackgroundProperty, "TextPrimary"); _playhead.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = line }; Children.Add(_playhead);
        _playhead.DragStarted += (_, _) => { StartSnap("playhead"); EditStarted?.Invoke(); };
        _playhead.DragDelta += (_, _) => MovePlayhead(Mouse.GetPosition(this).X);
        _playhead.DragCompleted += (_, _) => { EndSnap(); EditCompleted?.Invoke(); };
        _guide.SetResourceReference(Border.BackgroundProperty, "Accent"); _guideText.SetResourceReference(TextBlock.BackgroundProperty, "PanelBackground"); _guideText.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); Children.Add(_guide); Children.Add(_guideText);
        Panel.SetZIndex(_guide, 10); Panel.SetZIndex(_guideText, 11); SizeChanged += (_, _) => ArrangeTrack();
        MouseLeftButtonDown += (_, e) => { if (e.Handled || _duration <= 0 || IsHandle(e.OriginalSource as DependencyObject) || _draft != null) return; StartSnap("playhead"); MovePlayhead(e.GetPosition(this).X); CaptureMouse(); e.Handled = true; };
        MouseMove += (_, e) => { if (Mouse.Captured == this) { MovePlayhead(e.GetPosition(this).X); e.Handled = true; } };
        MouseLeftButtonUp += (_, e) => { if (Mouse.Captured == this) { ReleaseMouseCapture(); EndSnap(); e.Handled = true; } };
        LostMouseCapture += (_, _) => { if (!IsMouseCaptureWithin) EndSnap(); };
    }
    private static bool IsHandle(DependencyObject? element) { while (element != null) { if (element is Thumb or Button) return true; element = VisualTreeHelper.GetParent(element); } return false; }
    internal void SetDeleted(VideoRange[] deleted) { _deleted = deleted; ArrangeTrack(); }
    internal void SetSpeeds(VideoSpeedRange[] speeds) { _speeds = speeds; ArrangeTrack(); }
    internal void SetDraftRate(double rate) { _draftRate = rate; ArrangeTrack(); }
    private void Configure(Thumb thumb, string name, bool start, bool draft)
    {
        thumb.Name = name; thumb.Width = draft ? 14 : 16; thumb.Height = draft ? 44 : 36; thumb.Cursor = Cursors.SizeWE; thumb.Focusable = true; thumb.ToolTip = "拖动预览原片；方向键一帧，Shift 十帧；Alt 临时关闭吸附";
        if (draft) thumb.Visibility = Visibility.Collapsed; SetHandleColor(thumb, draft ? "Danger" : "Accent", draft ? 3 : 2); Children.Add(thumb); Panel.SetZIndex(thumb, 5);
        double anchor = 0, value = 0;
        thumb.DragStarted += (_, _) => { if (draft && _draft == null) return; anchor = Mouse.GetPosition(this).X; value = draft ? start ? _draft!.Start : _draft!.End : start ? _start : _end; StartSnap(draft ? start ? "draftStart" : "draftEnd" : start ? "start" : "end"); EditStarted?.Invoke(); RangePreviewRequested?.Invoke(value); };
        thumb.DragDelta += (_, _) =>
        {
            if (draft && _draft == null) return;
            double time = value + (Mouse.GetPosition(this).X - anchor) / Span * _duration;
            double min = draft ? start ? Math.Max(_draftLower, _draft!.End - _maximumDelete) : _draft!.Start + 1 / _fps : start ? 0 : _start + 1 / _fps;
            double max = draft ? start ? _draft!.End - 1 / _fps : Math.Min(_draftUpper, _draft!.Start + _maximumDelete) : start ? _end - 1 / _fps : _duration;
            time = Snapped(time, min, max);
            if (draft) MoveDraftBoundary(start, time); else { if (start) SetRange(time, _end); else SetRange(_start, time); RangePreviewRequested?.Invoke(start ? _start : _end); }
        };
        thumb.DragCompleted += (_, _) => { EndSnap(); EditCompleted?.Invoke(); };
        thumb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right)) return;
            double delta = (e.Key == Key.Left ? -1 : 1) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1) / _fps;
            if (draft && _draft != null) MoveDraftBoundary(start, (start ? _draft.Start : _draft.End) + delta);
            else if (!draft) { if (start) SetRange(_start + delta, _end); else SetRange(_start, _end + delta); RangePreviewRequested?.Invoke(start ? _start : _end); } e.Handled = true;
        };
    }
    private static void SetHandleColor(Thumb thumb, string resource, int border)
    { var frame = new FrameworkElementFactory(typeof(Border)); frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); frame.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); frame.SetResourceReference(Border.BorderBrushProperty, resource); frame.SetValue(Border.BorderThicknessProperty, new Thickness(border)); thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = frame }; }
    private void StartSnap(string moving)
    {
        _moving = moving; _snap.Reset(); var targets = new List<TimelineSnapTarget> { new(0, "原片起点"), new(_duration, "原片终点") };
        if (moving != "start") targets.Add(new(_start, "保留起点")); if (moving != "end") targets.Add(new(_end, "保留终点"));
        foreach (var r in _deleted) { targets.Add(new(r.Start, "删除起点")); targets.Add(new(r.End, "删除终点")); }
        foreach (var r in _speeds) { targets.Add(new(r.Start, "变速起点")); targets.Add(new(r.End, "变速终点")); }
        if (_draft != null) { if (moving != "draftStart") targets.Add(new(_draft.Start, "区间起点")); if (moving != "draftEnd") targets.Add(new(_draft.End, "区间终点")); }
        foreach (double time in ObjectBoundaries?.Invoke() ?? Array.Empty<double>()) targets.Add(new(time, "对象边界"));
        double own = moving switch { "start" => _start, "end" => _end, "draftStart" => _draft?.Start ?? -1, "draftEnd" => _draft?.End ?? -1, _ => _position };
        if (moving != "playhead" && Math.Abs(_position - own) > .000001) targets.Add(new(_position, "播放头", 1));
        _targets = targets.Where(t => Math.Abs(t.Time - own) > .000001).ToArray();
    }
    private double Snapped(double time, double min, double max)
    {
        bool Valid(TimelineSnapTarget t) => _moving switch { "start" => VideoCuts.Kept(t.Time, _end, _deleted).Sum(r => r.End - r.Start) >= 1 / _fps - .000001, "end" => VideoCuts.Kept(_start, t.Time, _deleted).Sum(r => r.End - r.Start) >= 1 / _fps - .000001, _ => true };
        double snapped = _snap.Resolve(time, _duration, Span, _targets.Where(Valid), min, max, SnapEnabled && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        if (_snap.Target is { } target) { _guide.Visibility = _guideText.Visibility = Visibility.Visible; SetLeft(_guide, X(target.Time)); SetTop(_guide, 2); _guideText.Text = $"{target.Label} {target.Time:F3}s"; SetLeft(_guideText, Math.Clamp(X(target.Time) + 5, 0, Math.Max(0, ActualWidth - 150))); SetTop(_guideText, 0); }
        else _guide.Visibility = _guideText.Visibility = Visibility.Collapsed;
        return snapped;
    }
    private void EndSnap() { _snap.Reset(); _guide.Visibility = _guideText.Visibility = Visibility.Collapsed; }
    private void MovePlayhead(double x) { double time = VideoCuts.Snap(Snapped((x - 8) / Span * _duration, 0, _duration), _fps, _duration); SeekRequested?.Invoke(time); }
    internal bool BeginDraft(VideoRange bounds, VideoRange range, double totalKept, bool speed = false)
    {
        double frame = 1 / _fps; _draftLower = Math.Ceiling((bounds.Start - .000001) * _fps) / _fps; _draftUpper = Math.Floor((bounds.End + .000001) * _fps) / _fps; _maximumDelete = speed ? _duration : Math.Floor((totalKept - frame + .000001) * _fps) / _fps;
        if (_draftUpper - _draftLower < frame - .000001 || _maximumDelete < frame - .000001) return false;
        double start = Math.Clamp(VideoCuts.Snap(range.Start, _fps, _duration), _draftLower, _draftUpper - frame), end = Math.Clamp(VideoCuts.Snap(range.End, _fps, _duration), start + frame, Math.Min(_draftUpper, start + _maximumDelete));
        _draft = new(start, end); DraftIsSpeed = speed; _left.IsEnabled = _right.IsEnabled = _playhead.IsEnabled = false;
        _deleteLeft.Name = speed ? "SpeedStartHandle" : "DeleteStartHandle"; _deleteRight.Name = speed ? "SpeedEndHandle" : "DeleteEndHandle";
        _deleteLeft.ToolTip = (speed ? "变速起点" : "红色删除起点") + "：拖动预览原片；方向键一帧，Shift 十帧；Alt 临时关闭吸附";
        _deleteRight.ToolTip = (speed ? "变速终点" : "红色删除终点") + "：拖动预览原片；方向键一帧，Shift 十帧；Alt 临时关闭吸附";
        string color = speed ? "Accent" : "Danger";
        _draftFill.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, speed ? "TimelinePending" : "TimelineDelete");
        _draftFill.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, color);
        _draftText.SetResourceReference(TextBlock.ForegroundProperty, speed ? "BadgeForeground" : "TextPrimary");
        SetHandleColor(_deleteLeft, color, 2); SetHandleColor(_deleteRight, color, 2);
        _draftFill.Visibility = _draftText.Visibility = _deleteLeft.Visibility = _deleteRight.Visibility = Visibility.Visible; ArrangeTrack(); DraftChanged?.Invoke(); return true;
    }
    internal void MoveDraftBoundary(bool start, double seconds)
    {
        if (_draft == null) return; double frame = 1 / _fps, snapped = VideoCuts.Snap(seconds, _fps, _duration);
        _draft = start ? _draft with { Start = Math.Clamp(snapped, Math.Max(_draftLower, _draft.End - _maximumDelete), _draft.End - frame) } : _draft with { End = Math.Clamp(snapped, _draft.Start + frame, Math.Min(_draftUpper, _draft.Start + _maximumDelete)) };
        ArrangeTrack(); DraftChanged?.Invoke(); RangePreviewRequested?.Invoke(start ? _draft.Start : _draft.End);
    }
    internal void EndDraft() { _draft = null; DraftIsSpeed = false; ToolTip = null; _draftFill.Visibility = _draftText.Visibility = _deleteLeft.Visibility = _deleteRight.Visibility = Visibility.Collapsed; _left.IsEnabled = _right.IsEnabled = _playhead.IsEnabled = true; EndSnap(); ArrangeTrack(); DraftChanged?.Invoke(); }
    internal void Initialize(double duration, double fps) { _duration = Math.Max(0, duration); _fps = fps > 0 ? fps : 30; _start = 0; _end = _duration; ArrangeTrack(); RangeChanged?.Invoke(); }
    internal void SetRange(double start, double end)
    {
        if (_draft != null) return; start = VideoCuts.Snap(start, _fps, _duration); end = VideoCuts.Snap(end, _fps, _duration); double minimum = Math.Min(_duration, 1 / _fps);
        double nextEnd = Math.Clamp(end, minimum, _duration), nextStart = Math.Clamp(start, 0, Math.Max(0, nextEnd - minimum)); if (VideoCuts.Kept(nextStart, nextEnd, _deleted).Sum(r => r.End - r.Start) < minimum - .000001) return;
        _end = nextEnd; _start = nextStart; ArrangeTrack(); RangeChanged?.Invoke();
    }
    internal void SetPosition(double position) { _position = Math.Clamp(position, 0, _duration); SetLeft(_playhead, X(_position) - 5); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (_duration <= 0) return; var text = TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;
        foreach (var (time, label) in RulerTicks)
        {
            double x = X(time); var value = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 10, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(value, new Point(Math.Clamp(x - value.Width / 2, 0, Math.Max(0, ActualWidth - value.Width)), 0));
            dc.DrawLine(new Pen(text, .5), new Point(x, 15), new Point(x, 18));
        }
    }
    internal IReadOnlyList<(double Time, string Label)> RulerTicks
    {
        get
        {
            var ticks = new List<(double, string)>(); if (_duration <= 0) return ticks;
            double raw = _duration / Math.Max(1, Math.Floor(Span / 64)), power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(.01, raw))));
            double step = new[] { 1d, 2, 5, 10 }.Select(n => n * power).First(n => n >= raw - .000001);
            if (_duration >= 1) step = Math.Max(1, step);
            string format = _duration >= 3600 ? @"hh\:mm\:ss" : step < 1 ? @"mm\:ss\.ff" : @"mm\:ss";
            for (double t = 0; t <= _duration + .000001; t += step) ticks.Add((t, TimeSpan.FromSeconds(t).ToString(format)));
            return ticks;
        }
    }
    private void ArrangeTrack()
    {
        _track.Width = Span; _track.Height = 12; SetLeft(_track, 8); SetTop(_track, Offset + 18); _range.Width = Math.Max(0, X(_end) - X(_start)); _range.Height = 12; SetLeft(_range, X(_start)); SetTop(_range, Offset + 18);
        foreach (var band in _bands) Children.Remove(band); _bands.Clear();
        void Band(double start, double end, string color, string caption, Action choose, bool speed)
        {
            if (end <= start) return;
            double width = Math.Max(1, X(end) - X(start));
            var fill = new Border { Width = width, Height = speed ? 18 : 6, CornerRadius = new CornerRadius(speed ? 5 : 2), BorderThickness = speed ? new Thickness(1) : new Thickness(0), ClipToBounds = true };
            fill.SetResourceReference(Border.BackgroundProperty, speed ? "TimelineSpeedBackground" : color);
            fill.SetResourceReference(Border.BorderBrushProperty, "Accent");
            if (speed && width >= 32)
            {
                var captionText = new TextBlock { Text = caption, FontSize = 10, Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                captionText.SetResourceReference(TextBlock.ForegroundProperty, "BadgeForeground"); fill.Child = captionText;
            }
            if (!speed) { var drawing = new GeometryDrawing(Brushes.White, null, Geometry.Parse("M0,0 L5,0 L0,5 Z M8,0 L8,5 L3,10 L0,10 Z")); fill.OpacityMask = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0,0,8,8), ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0,0,8,8) }; }
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            var button = new Button { Width = width, Height = speed ? 20 : 12, MinHeight = 0, Margin = new Thickness(0), Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Content = fill,
                Template = new ControlTemplate(typeof(Button)) { VisualTree = presenter }, ToolTip = $"{caption} · {start:F2}–{end:F2} 秒" };
            button.Click += (_, e) => { if (_draft == null) choose(); e.Handled = true; };
            SetLeft(button, X(start)); SetTop(button, Offset + (speed ? 14 : 34)); Panel.SetZIndex(button, 2); Children.Add(button); _bands.Add(button);
        }
        for (int i = 0; i < _speeds.Length; i++)
        {
            int index = i; var r = _speeds[i]; double start = Math.Max(_start, r.Start), end = Math.Min(_end, r.End);
            if (end <= start) continue;
            foreach (var piece in VideoCuts.Kept(start, end, _deleted)) Band(piece.Start, piece.End, "Accent", $"{r.Rate:0.#}×", () => SpeedSelected?.Invoke(index), true);
        }
        for (int i = 0; i < _deleted.Length; i++) { int index = i; var r = _deleted[i]; Band(r.Start, r.End, "Danger", "删除", () => DeleteSelected?.Invoke(index), false); }
        SetLeft(_left, X(_start) - 8); SetTop(_left, Offset + 6); SetLeft(_right, X(_end) - 8); SetTop(_right, Offset + 6); SetLeft(_playhead, X(_position) - 5); SetTop(_playhead, Offset + 3); Panel.SetZIndex(_playhead, 6);
        if (_draft is { } draft)
        {
            _draftFill.Width = Math.Max(1, X(draft.End) - X(draft.Start)); SetLeft(_draftFill, X(draft.Start)); SetTop(_draftFill, Offset + 14);
            _draftText.Text = DraftIsSpeed ? $"{_draftRate:0.#}× · 待确认" : "删除 · 待确认";
            _draftText.Width = Math.Max(0, _draftFill.Width - 16); _draftText.Visibility = _draftFill.Width >= 80 ? Visibility.Visible : Visibility.Collapsed;
            SetLeft(_draftText, X(draft.Start) + 8); SetTop(_draftText, Offset + 17);
            ToolTip = $"{_draftText.Text} · {draft.Start:F2}–{draft.End:F2} 秒";
            SetLeft(_deleteLeft, X(draft.Start) - 7); SetTop(_deleteLeft, Offset + 2); SetLeft(_deleteRight, X(draft.End) - 7); SetTop(_deleteRight, Offset + 2);
        }
        InvalidateVisual();
    }
}
