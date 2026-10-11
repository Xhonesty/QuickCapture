using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuickCapture;

internal static class VideoCuts
{
    internal static double Snap(double time, double fps, double duration) => Math.Clamp(Math.Round(time * fps) / fps, 0, duration);
    internal static VideoRange[] Normalize(IEnumerable<VideoRange>? ranges, double duration, double fps)
    {
        if (ranges == null) return Array.Empty<VideoRange>();
        var result = new List<VideoRange>();
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            if (!double.IsFinite(range.Start) || !double.IsFinite(range.End) || range.End < range.Start) throw new ArgumentException("删除片段的边界无效。");
            double start = Snap(range.Start, fps, duration), end = Snap(range.End, fps, duration);
            if (end - start < .5 / fps) continue;
            if (result.Count > 0 && start <= result[^1].End + .001 / fps) result[^1] = result[^1] with { End = Math.Max(end, result[^1].End) };
            else result.Add(new(start, end));
        }
        return result.ToArray();
    }
    internal static VideoRange[] Kept(double start, double end, IEnumerable<VideoRange>? deleted)
    {
        var result = new List<VideoRange>(); double position = start;
        foreach (var r in (deleted ?? Array.Empty<VideoRange>()).OrderBy(r => r.Start))
        {
            if (r.End <= position || r.Start >= end) continue;
            if (r.Start > position) result.Add(new(position, Math.Min(r.Start, end)));
            position = Math.Max(position, r.End); if (position >= end) break;
        }
        if (position < end - .000001) result.Add(new(position, end)); return result.ToArray();
    }
    internal static double Next(double position, double start, double end, IEnumerable<VideoRange>? deleted)
    { foreach (var r in Kept(start, end, deleted)) { if (position < r.Start) return r.Start; if (position < r.End) return position; } return end; }
}
