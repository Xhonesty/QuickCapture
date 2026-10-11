using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture;

internal sealed class VideoObjectOverlay : FrameworkElement
{
    internal VideoObject[] Objects { get; set; } = Array.Empty<VideoObject>();
    internal VideoObject? Selected { get; set; }
    private VideoObjectKind? _tool;
    internal VideoObjectKind? Tool { get => _tool; set { if (_tool == value) return; _tool = value; ToolChanged?.Invoke(value); InvalidateVisual(); } }
    internal event Action<VideoObjectKind?>? ToolChanged;
    internal VideoInfo Info { get; set; } = new(0, 0, 0, 30, false);
    internal Rect View { get; set; }
    internal bool ShowSelection { get; set; } = true;
    internal bool MosaicBrush { get; set; }
    internal Func<bool>? InputBlocked { get; set; }
    internal VideoObject? DraftStyle { get; set; }
    private bool PathTool => Tool == VideoObjectKind.Brush || Tool == VideoObjectKind.Mosaic && MosaicBrush;
    internal double Time { get; set; }
    internal event Action<VideoObject?>? SelectionChanged;
    internal event Action<VideoObjectKind, Point, Point, VideoPoint[]>? Drawn;
    internal event Action<VideoObject, bool>? Transformed;
    internal event Action? DeleteRequested;
    internal event Action? InteractionStarted;
    internal event Action<Point, VideoObject?>? TextRequested;
    internal bool ForceNewText { get; set; }
    private Point _anchor, _end;
    private VideoObject? _before;
    private (int X, int Y)? _handle;
    private readonly List<VideoPoint> _points = new();
    private bool _dragging;
    internal bool Drawing => _dragging && _before == null;
    internal VideoObjectOverlay()
    {
        Name = "VideoObjectOverlay"; Focusable = true; Cursor = Cursors.Arrow;
        SizeChanged += (_, _) => InvalidateVisual();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Cancel(); e.Handled = true; } else if (e.Key == Key.Delete && !_dragging) { DeleteRequested?.Invoke(); e.Handled = true; } };
        LostMouseCapture += (_, _) => { if (_dragging) Cancel(); };
    }
    private double Scale => View.Width > 0 ? ActualWidth / View.Width : 1;
    private Point Source(Point p) => new(Math.Clamp(p.X / Scale + View.X, 0, Info.Width), Math.Clamp(p.Y / Scale + View.Y, 0, Info.Height));
    private Point Display(Point p) => new((p.X - View.X) * Scale, (p.Y - View.Y) * Scale);
    private Rect Display(Rect r) => new(Display(r.TopLeft), new Size(r.Width * Scale, r.Height * Scale));
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var accent = TryFindResource("Accent") as Brush ?? Brushes.Turquoise;
        if (Selected != null && ShowSelection)
        {
            var bounds = Display(Selected.Bounds); dc.DrawRectangle(null, new Pen(accent, 1.5) { DashStyle = DashStyles.Dash }, bounds);
            foreach (var (x, y) in Handles(Selected)) dc.DrawRectangle(Brushes.White, new Pen(accent, 1), new Rect(bounds.X + (x + 1) * bounds.Width / 2 - 4, bounds.Y + (y + 1) * bounds.Height / 2 - 4, 8, 8));
        }
        if (!Drawing || Tool == null) return;
        var pen = new Pen(DraftStyle is { } style ? new SolidColorBrush(VideoObjectEditor.EffectiveColor(style, false)) : accent, (DraftStyle?.Stroke ?? 2) * Scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        Point a = Display(_anchor), b = Display(_end); var rect = new Rect(a, b);
        if (PathTool)
        {
            var path = new StreamGeometry(); using (var c = path.Open()) { c.BeginFigure(Display(new Point(_points[0].X, _points[0].Y)), false, false); foreach (var p in _points.Skip(1)) c.LineTo(Display(new Point(p.X, p.Y)), true, false); }
            if (Tool == VideoObjectKind.Mosaic) pen.Brush = new SolidColorBrush(Color.FromArgb(100, 0, 200, 170));
            else if (DraftStyle is { OutlineEnabled: true } outlined)
            {
                var color = (Color)ColorConverter.ConvertFromString(outlined.OutlineColor); color.A = (byte)Math.Round(color.A * outlined.OutlineAlpha);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), (outlined.Stroke + 2 * outlined.OutlineWidth) * Scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, path);
            }
            if (_points.Count == 1) { if (Tool != VideoObjectKind.Mosaic && DraftStyle is { OutlineEnabled: true } dot) { var color = (Color)ColorConverter.ConvertFromString(dot.OutlineColor); color.A = (byte)Math.Round(color.A * dot.OutlineAlpha); dc.DrawEllipse(new SolidColorBrush(color), null, a, (dot.Stroke / 2 + dot.OutlineWidth) * Scale, (dot.Stroke / 2 + dot.OutlineWidth) * Scale); } dc.DrawEllipse(pen.Brush, null, a, pen.Thickness / 2, pen.Thickness / 2); }
            else dc.DrawGeometry(null, pen, path);
        }
        else if (Tool is VideoObjectKind.Arrow or VideoObjectKind.Line)
        {
            var path = new StreamGeometry();
            using (var context = path.Open())
            {
                context.BeginFigure(a, false, false); context.LineTo(b, true, false);
                double angle = Math.Atan2(a.Y - b.Y, a.X - b.X), head = Math.Max(12, DraftStyle?.Stroke * 4 ?? 16) * Scale;
                if (Tool == VideoObjectKind.Arrow)
                {
                    context.BeginFigure(b, false, false); context.LineTo(new(b.X + head * Math.Cos(angle - .45), b.Y + head * Math.Sin(angle - .45)), true, false);
                    context.BeginFigure(b, false, false); context.LineTo(new(b.X + head * Math.Cos(angle + .45), b.Y + head * Math.Sin(angle + .45)), true, false);
                }
            }
            if (DraftStyle is { OutlineEnabled: true } outlined)
            {
                var color = (Color)ColorConverter.ConvertFromString(outlined.OutlineColor); color.A = (byte)Math.Round(color.A * outlined.OutlineAlpha);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), (outlined.Stroke + 2 * outlined.OutlineWidth) * Scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, path);
            }
            if (DraftStyle?.StrokeEnabled != false) dc.DrawGeometry(null, pen, path);
        }
        else dc.DrawGeometry(Tool == VideoObjectKind.Mosaic ? new SolidColorBrush(Color.FromArgb(60, 0, 200, 170)) : DraftStyle is { Fill: not "none" } filled ? new SolidColorBrush(VideoObjectEditor.EffectiveColor(filled, true)) : null, DraftStyle?.StrokeEnabled == false ? null : pen,
            AnnotationGeometry.Shape(Tool == VideoObjectKind.Ellipse ? AnnotationShape.Ellipse : Tool == VideoObjectKind.RoundedRectangle ? AnnotationShape.RoundedRectangle : AnnotationShape.Rectangle, rect));
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); if (InputBlocked?.Invoke() == true) { e.Handled = true; return; } if (!IsEnabled || Info.Width < 1) return;
        Focus(); InteractionStarted?.Invoke(); _anchor = _end = Source(e.GetPosition(this)); _before = null; _handle = HandleAt(e.GetPosition(this));
        var text = Objects.OrderByDescending(o => o.Layer).FirstOrDefault(o => o.Active(Time, Info.Fps) && o.Bounds.Contains(_anchor));
        if (_handle == null && (Tool == VideoObjectKind.Text || (Tool == null && e.ClickCount == 2 && text?.Kind == VideoObjectKind.Text)))
        { var existing = ForceNewText ? null : text?.Kind == VideoObjectKind.Text ? text : null; ForceNewText = false; TextRequested?.Invoke(_anchor, existing); e.Handled = true; return; }
        if (Tool == null || _handle != null)
        {
            if (Selected != null)
            {
                var bounds = Display(Selected.Bounds); var p = e.GetPosition(this);
                foreach (var h in Handles(Selected)) if (Math.Abs(p.X - (bounds.X + (h.X + 1) * bounds.Width / 2)) <= 7 && Math.Abs(p.Y - (bounds.Y + (h.Y + 1) * bounds.Height / 2)) <= 7) { _handle = h; break; }
            }
            if (_handle == null) { Selected = Objects.OrderByDescending(o => o.Layer).FirstOrDefault(o => o.Active(Time, Info.Fps) && o.Bounds.Contains(_anchor)); SelectionChanged?.Invoke(Selected); }
            if (Selected == null) { InvalidateVisual(); return; } _before = Selected;
        }
        _points.Clear(); _points.Add(new(_anchor.X, _anchor.Y)); _dragging = true; CaptureMouse(); e.Handled = true; InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (InputBlocked?.Invoke() == true) return;
        if (!_dragging)
        {
            var handle = Selected == null ? null : HandleAt(e.GetPosition(this));
            Cursor = handle is { } direction ? HandleCursor(direction) : Tool == null ? Cursors.Arrow : Cursors.Cross;
            return;
        }
        _end = Source(e.GetPosition(this));
        if (_before != null)
        {
            Vector delta = _end - _anchor; Rect after;
            if (_handle is { } h)
            {
                if (_before.Kind == VideoObjectKind.Text)
                {
                    ResizeText(_before, h, delta, false);
                    InvalidateVisual(); e.Handled = true; return;
                }
                double left = _before.X, right = _before.X + _before.Width, top = _before.Y, bottom = _before.Y + _before.Height;
                if (h.X < 0) left = Math.Clamp(left + delta.X, 0, right - 1); if (h.X > 0) right = Math.Clamp(right + delta.X, left + 1, Info.Width);
                if (h.Y < 0) top = Math.Clamp(top + delta.Y, 0, bottom - 1); if (h.Y > 0) bottom = Math.Clamp(bottom + delta.Y, top + 1, Info.Height);
                double width = right - left, height = bottom - top;
                if (_before.Kind == VideoObjectKind.PictureInPicture)
                {
                    double ratio = _before.Width / _before.Height;
                    if (h.X == 0) width = height * ratio; else height = width / ratio;
                    double factor = Math.Min(1, Math.Min(Info.Width / width, Info.Height / height)); width *= factor; height *= factor;
                    if (h.X < 0) left = right - width; if (h.Y < 0) top = bottom - height;
                    left = Math.Clamp(left, 0, Info.Width - width); top = Math.Clamp(top, 0, Info.Height - height);
                }
                after = new(left, top, width, height);
            }
            else after = new(Math.Clamp(_before.X + delta.X, 0, Info.Width - _before.Width), Math.Clamp(_before.Y + delta.Y, 0, Info.Height - _before.Height), _before.Width, _before.Height);
            Selected = _before.Transform(after).FitText(Info); Transformed?.Invoke(Selected, false);
        }
        else if (PathTool && _points.Count < 100000 && (_end - new Point(_points[^1].X, _points[^1].Y)).Length >= .5) _points.Add(new(_end.X, _end.Y));
        InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e); if (InputBlocked?.Invoke() == true) { e.Handled = true; return; } if (!_dragging) return;
        _dragging = false; ReleaseMouseCapture();
        if (_before != null && Selected != null) Transformed?.Invoke(Selected, true);
        else if (Tool is { } tool && (PathTool || (_end - _anchor).Length >= 2)) Drawn?.Invoke(tool, _anchor, _end, _points.ToArray());
        _before = null; InvalidateVisual(); e.Handled = true;
    }
    internal void Cancel()
    { if (!_dragging) { Tool = null; InvalidateVisual(); return; } _dragging = false; ReleaseMouseCapture(); if (_before != null) { Selected = _before; Transformed?.Invoke(_before, false); } _before = null; Tool = null; InvalidateVisual(); }
    internal static (int X, int Y)[] Handles(VideoObject item) => AnnotationSurface.HandleDirections;
    internal void ResizeText(VideoObject before, (int X, int Y) handle, Vector delta, bool commit)
    {
        Selected = VideoTextLayout.Resize(before, handle, delta, Info, View);
        Transformed?.Invoke(Selected, commit); InvalidateVisual();
    }
    internal (int X, int Y)? HandleAt(Point p)
    {
        if (Selected == null || !ShowSelection) return null;
        var bounds = Display(Selected.Bounds);
        foreach (var h in Handles(Selected))
            if (Math.Abs(p.X - (bounds.X + (h.X + 1) * bounds.Width / 2)) <= 7 && Math.Abs(p.Y - (bounds.Y + (h.Y + 1) * bounds.Height / 2)) <= 7) return h;
        return null;
    }
    internal static Cursor HandleCursor((int X, int Y) h) => h.X == 0 ? Cursors.SizeNS : h.Y == 0 ? Cursors.SizeWE : h.X == h.Y ? Cursors.SizeNWSE : Cursors.SizeNESW;
}
