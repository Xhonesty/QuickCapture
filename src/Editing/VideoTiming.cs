using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickCapture;

internal sealed record VideoSpeedRange(double Start, double End, double Rate)
{
    public override string ToString() => $"{Start:F2}–{End:F2} 秒 · {Rate:0.#}×";
}
internal sealed record VideoTimedSegment(double Start, double End, double Rate)
{
    internal double Duration => (End - Start) / Rate;
}

// One source-time plan is shared by transport, objects and all export formats.
internal static class VideoTiming
{
    internal static VideoSpeedRange[] Normalize(System.Collections.Generic.IEnumerable<VideoSpeedRange>? ranges, double duration, double fps)
    {
        var result = Array.Empty<VideoSpeedRange>();
        foreach (var range in ranges ?? Array.Empty<VideoSpeedRange>())
        {
            if (!double.IsFinite(range.Start + range.End) || range.End <= range.Start || range.Start < 0 || range.End > duration + .001 || range.Rate is not (.5 or 1 or 1.5 or 2)) throw new ArgumentException("变速区间的边界或倍率无效。");
            var next = range with { Start = VideoCuts.Snap(range.Start, fps, duration), End = VideoCuts.Snap(range.End, fps, duration) };
            if (next.End - next.Start >= .5 / fps) result = Apply(result, next);
        }
        return result;
    }
    internal static VideoSpeedRange[] Apply(IEnumerable<VideoSpeedRange> ranges, VideoSpeedRange next)
    {
        var pieces = new List<VideoSpeedRange>();
        foreach (var old in ranges)
        {
            if (old.End <= next.Start || old.Start >= next.End) pieces.Add(old);
            else { if (old.Start < next.Start) pieces.Add(old with { End = next.Start }); if (old.End > next.End) pieces.Add(old with { Start = next.End }); }
        }
        if (next.Rate != 1) pieces.Add(next);
        var result = new List<VideoSpeedRange>();
        foreach (var piece in pieces.OrderBy(r => r.Start))
            if (result.Count > 0 && Math.Abs(result[^1].End - piece.Start) < .000001 && result[^1].Rate == piece.Rate) result[^1] = result[^1] with { End = piece.End };
            else result.Add(piece);
        return result.ToArray();
    }
    internal static double RateAt(double sourceTime, IEnumerable<VideoSpeedRange>? ranges, double legacySpeed = 1) => ranges == null ? legacySpeed : ranges.FirstOrDefault(r => sourceTime >= r.Start && sourceTime < r.End)?.Rate ?? 1;
    internal static VideoTimedSegment[] Segments(double start, double end, IEnumerable<VideoRange>? deleted, IEnumerable<VideoSpeedRange>? speeds, double legacySpeed = 1)
    {
        var rates = speeds?.ToArray(); var result = new List<VideoTimedSegment>();
        foreach (var kept in VideoCuts.Kept(start, end, deleted))
        {
            var points = new[] { kept.Start, kept.End }.Concat((rates ?? Array.Empty<VideoSpeedRange>()).SelectMany(r => new[] { r.Start, r.End }).Where(t => t > kept.Start && t < kept.End)).Distinct().Order().ToArray();
            for (int i = 0; i < points.Length - 1; i++) result.Add(new(points[i], points[i + 1], RateAt((points[i] + points[i + 1]) / 2, rates, legacySpeed)));
        }
        return result.ToArray();
    }
    internal static VideoTimedSegment[] Segments(RecordingEditState state) => Segments(state.Start, state.End, state.Deleted, state.SpeedRanges, state.Speed);
    internal static double SourceTime(double outputTime, RecordingEditState state)
    {
        double remaining = Math.Max(0, outputTime);
        foreach (var piece in Segments(state)) { if (remaining < piece.Duration - .000000001) return piece.Start + Math.Max(0, remaining) * piece.Rate; remaining = Math.Max(0, remaining - piece.Duration); }
        return state.End;
    }
    internal static double OutputTime(double sourceTime, RecordingEditState state) => Segments(state).Sum(r => Math.Max(0, Math.Min(sourceTime, r.End) - r.Start) / r.Rate);
}
