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
internal sealed record VideoRange(double Start, double End)
{ public override string ToString() => $"删除 {Start:F3} – {End:F3} 秒"; }
internal sealed record RecordingEditState(double Start, double End, VideoRange[] Deleted, double Speed = 1, bool Mute = false, VideoCrop? Crop = null, RecordingFormat Format = RecordingFormat.Mp4, ExportQuality Quality = ExportQuality.Medium, int Fps = 30);
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
internal static class RecordingEditStore
{
    private sealed record Document(int Version, string Source, long Length, long Modified, RecordingEditState Edit);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string FileFor(string source) => RecordingRecovery.Owns(source) ? source + ".json" : Path.Combine(Paths.Data, "RecordingEdits", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant()))) + ".json");
    internal static Task<RecordingEditState?> LoadAsync(string source) => Task.Run(() =>
    {
        string path = FileFor(source); if (!File.Exists(path)) return null;
        if (RecordingRecovery.Owns(source))
        {
            var metadata = ReadMetadata(source);
            return metadata.Edit;
        }
        var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) ?? throw new InvalidDataException("录屏剪辑配置损坏，原片和配置已保留。");
        if (doc.Version != 1) throw new InvalidDataException("录屏剪辑配置版本不支持，原片和配置已保留。");
        var file = new FileInfo(source);
        if (!doc.Source.Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase) || doc.Length != file.Length || doc.Modified != file.LastWriteTimeUtc.Ticks) throw new InvalidDataException("原片已改变，无法应用旧剪辑配置。请清理配置后重新编辑。");
        return doc.Edit;
    });
    internal static async Task SaveAsync(string source, RecordingEditState state)
    {
        await Gate.WaitAsync();
        try { await Task.Run(() =>
        {
            if (RecordingRecovery.Owns(source)) { RecordingRecovery.Remember(source, ReadMetadata(source) with { Edit = state, Crop = state.Crop }); return; }
            var file = new FileInfo(source); var doc = new Document(1, Path.GetFullPath(source), file.Length, file.LastWriteTimeUtc.Ticks, state); ImageExportService.AtomicWrite(FileFor(source), JsonSerializer.SerializeToUtf8Bytes(doc), true);
        }); }
        finally { Gate.Release(); }
    }
    private static RecordingMetadata ReadMetadata(string source)
    {
        if (!File.Exists(source + ".json")) return RecordingRecovery.Load(source);
        var metadata = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(source + ".json")) ?? throw new InvalidDataException("录屏恢复配置损坏，原片和配置已保留。");
        if (metadata.Version is < 1 or > 2) throw new InvalidDataException("录屏恢复配置版本不支持，原片和配置已保留。");
        return metadata;
    }
    internal static void Remove(string source)
    {
        if (!File.Exists(FileFor(source))) return;
        if (RecordingRecovery.Owns(source)) RecordingRecovery.Remember(source, ReadMetadata(source) with { Edit = null, Crop = null });
        else File.Delete(FileFor(source));
    }
}
