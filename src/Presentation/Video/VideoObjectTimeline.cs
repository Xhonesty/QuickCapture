using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture;

internal sealed class VideoObjectTimeline : FrameworkElement
{
    internal VideoObject[] Objects { get; set; } = Array.Empty<VideoObject>();
    internal VideoObject? Selected { get; set; }
    internal double Duration { get; set; }
    internal double Fps { get; set; } = 30;
    internal double Position { get; set; }
    internal event Action<VideoObject>? SelectedObject;
    internal event Action<VideoObject, bool>? Changed;
    internal event Action? InteractionStarted;
    private VideoObject? _before;
    private bool _start;
    internal VideoObjectKind? ActiveTool { get; set; }
    private const double RowHeight = 28;
    internal int[] VisibleCategories { get; private set; } = Array.Empty<int>();
    private sealed record TrackRow(int Category, string? Id, string Label);
    private TrackRow[] _rows = Array.Empty<TrackRow>();
    private double Span => Math.Max(1, ActualWidth - 16);
    internal void RefreshRows()
    {
        VisibleCategories = Objects.Select(o => Category(o.Kind)).Concat(ActiveTool is { } tool ? new[] { Category(tool) } : Array.Empty<int>()).Distinct().OrderBy(row => row).ToArray();
        _rows = VisibleCategories.SelectMany(category =>
        {
            var items = Objects.Where(o => Category(o.Kind) == category).OrderBy(o => o.Layer).ToArray();
            if (category is 3 or 4 && items.Length > 0) return items.Select((o, i) => new TrackRow(category, o.Id, o.Name ?? (category == 3 ? "画中画 " : "文字 ") + (i + 1)));
            return new[] { new TrackRow(category, null, new[] { "形状", "画笔", "马赛克", "画中画", "文字" }[category]) };
        }).ToArray();
        Height = _rows.Length * RowHeight;
        Visibility = _rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        InvalidateVisual();
    }
    internal VideoObjectTimeline() { Name = "VideoObjectTimeline"; Height = 0; Visibility = Visibility.Collapsed; ToolTip = "工具时间轴与上方主时间轴对齐，均使用原片时间；选中区间后拖动两端调整，终点不包含该帧。"; SizeChanged += (_, _) => InvalidateVisual(); LostMouseCapture += (_, _) => { if (_before != null) { var original = _before; _before = null; Selected = original; Changed?.Invoke(original, false); InvalidateVisual(); } }; }
    internal static int Category(VideoObjectKind kind) => kind switch { VideoObjectKind.Brush => 1, VideoObjectKind.Mosaic => 2, VideoObjectKind.PictureInPicture => 3, VideoObjectKind.Text => 4, _ => 0 };
    private int Row(VideoObject o) => Array.FindIndex(_rows, row => row.Id == o.Id || row.Id == null && row.Category == Category(o.Kind));
    internal int RowCount => _rows.Length;
    internal double X(double time) => 8 + time / Math.Max(.001, Duration) * Span;
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var track = TryFindResource("TimelineTrack") as Brush ?? Brushes.Gray;
        var accent = TryFindResource("Accent") as Brush ?? Brushes.Turquoise;
        var text = TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;
        for (int row = 0; row < _rows.Length; row++)
        {
            var label = new FormattedText(_rows[row].Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 11, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(8, row * RowHeight)); dc.DrawRoundedRectangle(track, null, new Rect(8, row * RowHeight + 16, Span, 8), 3, 3);
        }
        foreach (var o in Objects.OrderBy(o => o.Layer))
        {
            bool selected = Selected?.Id == o.Id;
            var fill = accent.Clone(); fill.Opacity = selected ? .8 : .35;
            double effectiveEnd = o.AssetVideo ? Math.Min(o.End, o.Start + o.AssetDuration - o.AssetStart) : o.End;
            dc.DrawRoundedRectangle(fill, selected ? new Pen(accent, 1) : null, new Rect(X(o.Start), Row(o) * RowHeight + 14, Math.Max(2, X(effectiveEnd) - X(o.Start)), 12), 3, 3);
            if (effectiveEnd < o.End) dc.DrawRectangle(null, new Pen(text, 1) { DashStyle = DashStyles.Dot }, new Rect(X(effectiveEnd), Row(o) * RowHeight + 15, X(o.End) - X(effectiveEnd), 10));
            if (selected) foreach (double boundary in new[] { o.Start, o.End }) dc.DrawRectangle(accent, null, new Rect(X(boundary) - 3, Row(o) * RowHeight + 11, 6, 17));
        }
        dc.DrawLine(new Pen(text, 1), new Point(X(Position), 0), new Point(X(Position), Height));
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled || Duration <= 0) return;
        var p = e.GetPosition(this); int row = (int)(p.Y / RowHeight);
        if (Selected != null && Row(Selected) == row && (Math.Abs(p.X - X(Selected.Start)) < 8 || Math.Abs(p.X - X(Selected.End)) < 8))
        { _before = Selected; _start = Math.Abs(p.X - X(Selected.Start)) <= Math.Abs(p.X - X(Selected.End)); InteractionStarted?.Invoke(); CaptureMouse(); }
        else { var item = Objects.OrderByDescending(o => o.Layer).FirstOrDefault(o => Row(o) == row && p.X >= X(o.Start) - 3 && p.X <= X(o.End) + 3); if (item != null) { Selected = item; SelectedObject?.Invoke(item); } }
        InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_before == null) return;
        double time = VideoCuts.Snap((e.GetPosition(this).X - 8) / Span * Duration, Fps, Duration);
        Selected = _start ? _before with { Start = Math.Clamp(time, 0, _before.End - 1 / Fps) } : _before with { End = Math.Clamp(time, _before.Start + 1 / Fps, Duration) };
        Changed?.Invoke(Selected, false); InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { if (_before == null || Selected == null) return; _before = null; ReleaseMouseCapture(); Changed?.Invoke(Selected, true); e.Handled = true; }
}
