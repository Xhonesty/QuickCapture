using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace QuickCapture;

// All values are seconds in the unmodified recording, independent of preview speed.
internal sealed class TrimTimeline : Canvas
{
    private readonly Border _track = new(), _range = new();
    private readonly Thumb _left = new(), _right = new();
    private readonly Rectangle _playhead = new() { Width = 2, IsHitTestVisible = false };
    private double _duration, _start, _end, _position, _fps = 30;
    private VideoRange[] _deleted = Array.Empty<VideoRange>();
    private readonly System.Collections.Generic.List<Border> _cuts = new();
    internal event Action? EditStarted;
    internal event Action? EditCompleted;
    internal void SetDeleted(VideoRange[] deleted) { _deleted = deleted; ArrangeTrack(); }
    public event Action? RangeChanged;
    public event Action<double>? SeekRequested;
    public event Action<double>? RangePreviewRequested;
    internal double Start => _start;
    internal double End => _end;
    internal double Duration => _duration;
    public TrimTimeline()
    {
        Height = 48; MinWidth = 100; Background = Brushes.Transparent;
        _track.CornerRadius = _range.CornerRadius = UiDesign.Radius;
        _track.SetResourceReference(Border.BackgroundProperty, "TimelineTrack");
        _range.SetResourceReference(Border.BackgroundProperty, "Accent");
        _range.Opacity = 0.45; Children.Add(_track); Children.Add(_range);
        _playhead.SetResourceReference(Shape.FillProperty, "TextPrimary"); Children.Add(_playhead);
        Configure(_left, "TrimStart", "起点：拖动调整；方向键调整一帧，Shift 调整十帧", true);
        Configure(_right, "TrimEnd", "终点：拖动调整；方向键调整一帧，Shift 调整十帧", false);
        SizeChanged += (_, _) => ArrangeTrack();
        IsEnabledChanged += (_, _) => { _left.Opacity = _right.Opacity = IsEnabled ? 1 : .55; _range.Opacity = IsEnabled ? .45 : .2; };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Thumb && _duration > 0) { SeekRequested?.Invoke(Math.Clamp((e.GetPosition(this).X - 8) / Span, 0, 1) * _duration); e.Handled = true; } };
    }
    private double Span => Math.Max(1, ActualWidth - 16);
    private void Configure(Thumb thumb, string name, string tooltip, bool start)
    {
        thumb.Name = name; thumb.Width = 16; thumb.Height = 36; thumb.Cursor = Cursors.SizeWE; thumb.ToolTip = tooltip; thumb.Focusable = true;
        var frame = new FrameworkElementFactory(typeof(Border)); frame.SetValue(Border.CornerRadiusProperty, UiDesign.Radius);
        frame.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); frame.SetResourceReference(Border.BorderBrushProperty, "Accent"); frame.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = frame }; Children.Add(thumb);
        thumb.GotKeyboardFocus += (_, _) => thumb.Opacity = .75;
        thumb.LostKeyboardFocus += (_, _) => thumb.Opacity = IsEnabled ? 1 : .55;
        double anchor = 0, value = 0;
        thumb.DragStarted += (_, _) => { EditStarted?.Invoke(); anchor = Mouse.GetPosition(this).X; value = start ? _start : _end; RangePreviewRequested?.Invoke(value); };
        thumb.DragCompleted += (_, _) => EditCompleted?.Invoke();
        thumb.DragDelta += (_, _) =>
        {
            double time = value + (Mouse.GetPosition(this).X - anchor) / Span * _duration;
            if (start) SetRange(time, _end); else SetRange(_start, time);
            RangePreviewRequested?.Invoke(start ? _start : _end);
        };
        thumb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right)) return;
            double delta = (e.Key == Key.Left ? -1 : 1) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1) / _fps;
            if (start) SetRange(_start + delta, _end); else SetRange(_start, _end + delta);
            RangePreviewRequested?.Invoke(start ? _start : _end); e.Handled = true;
        };
    }
    internal void Initialize(double duration, double fps)
    { _duration = Math.Max(0, duration); _fps = fps > 0 ? fps : 30; _start = 0; _end = _duration; ArrangeTrack(); RangeChanged?.Invoke(); }
    internal void SetRange(double start, double end)
    {
        start = VideoCuts.Snap(start, _fps, _duration); end = VideoCuts.Snap(end, _fps, _duration);
        double minimum = Math.Min(_duration, 1 / _fps);
        _end = Math.Clamp(end, minimum, _duration); _start = Math.Clamp(start, 0, Math.Max(0, _end - minimum));
        ArrangeTrack(); RangeChanged?.Invoke();
    }
    internal void SetPosition(double position) { _position = Math.Clamp(position, 0, _duration); ArrangeTrack(); }
    private void ArrangeTrack()
    {
        double X(double seconds) => 8 + (_duration > 0 ? seconds / _duration * Span : 0);
        _track.Width = Span; _track.Height = 12; SetLeft(_track, 8); SetTop(_track, 18);
        _range.Width = Math.Max(0, X(_end) - X(_start)); _range.Height = 12; SetLeft(_range, X(_start)); SetTop(_range, 18);
        foreach (var cut in _cuts) Children.Remove(cut); _cuts.Clear();
        foreach (var interval in _deleted)
        {
            var cut = new Border { Width = Math.Max(1, X(interval.End) - X(interval.Start)), Height = 12, Opacity = .75, IsHitTestVisible = false, CornerRadius = new CornerRadius(2) };
            cut.SetResourceReference(Border.BackgroundProperty, "Danger"); SetLeft(cut, X(interval.Start)); SetTop(cut, 18); Children.Insert(2, cut); _cuts.Add(cut);
        }
        SetLeft(_left, X(_start) - 8); SetTop(_left, 6); SetLeft(_right, X(_end) - 8); SetTop(_right, 6);
        _playhead.Height = 42; SetLeft(_playhead, X(_position) - 1); SetTop(_playhead, 3);
    }
}
