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
        if (doc.Version is < 1 or > 6) throw new InvalidDataException("录屏剪辑配置版本不支持，原片和配置已保留。");
        var file = new FileInfo(source);
        if (!doc.Source.Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase) || doc.Length != file.Length || doc.Modified != file.LastWriteTimeUtc.Ticks) throw new InvalidDataException("原片已改变，无法应用旧剪辑配置。请清理配置后重新编辑。");
        return doc.Edit;
    });
    internal static async Task SaveAsync(string source, RecordingEditState state)
    {
        await Gate.WaitAsync();
        try { await Task.Run(() =>
        {
            bool extended = state.SpeedRanges != null || state.ToolDefaults != null || !state.SnapEnabled || state.Objects?.Any(o => o.StrokeOpacity != null || o.FillOpacity != null || !o.StrokeEnabled || o.Kind == VideoObjectKind.Line) == true;
            bool styled = (state.Objects ?? Array.Empty<VideoObject>()).Concat(state.ToolDefaults ?? Array.Empty<VideoObject>()).Any(o => o.Italic || o.OutlineEnabled || o.OutlineOpacity != null || o.MosaicBrush);
            bool framed = (state.Objects ?? Array.Empty<VideoObject>()).Concat(state.ToolDefaults ?? Array.Empty<VideoObject>()).Any(o => o.TextFrame != null || o.Name != null) || state.Objects?.Count(o => o.Kind == VideoObjectKind.PictureInPicture) > 1;
            int version = framed ? 6 : styled ? 5 : extended ? 4 : state.Objects?.Any(o => o.Kind == VideoObjectKind.Text) == true ? 3 : 2;
            if (RecordingRecovery.Owns(source)) { RecordingRecovery.Remember(source, ReadMetadata(source) with { Edit = state, Crop = state.Crop, Version = version }); return; }
            var file = new FileInfo(source); var doc = new Document(version, Path.GetFullPath(source), file.Length, file.LastWriteTimeUtc.Ticks, state); ImageExportService.AtomicWrite(FileFor(source), JsonSerializer.SerializeToUtf8Bytes(doc), true);
        }); }
        finally { Gate.Release(); }
    }
    private static RecordingMetadata ReadMetadata(string source)
    {
        if (!File.Exists(source + ".json")) return RecordingRecovery.Load(source);
        var metadata = JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(source + ".json")) ?? throw new InvalidDataException("录屏恢复配置损坏，原片和配置已保留。");
        if (metadata.Version is < 1 or > 6) throw new InvalidDataException("录屏恢复配置版本不支持，原片和配置已保留。");
        return metadata;
    }
    internal static void Remove(string source)
    {
        if (!File.Exists(FileFor(source))) return;
        if (RecordingRecovery.Owns(source)) RecordingRecovery.Remember(source, ReadMetadata(source) with { Edit = null, Crop = null });
        else File.Delete(FileFor(source));
    }
    private sealed record Association(string Source, long Length, long Modified);
    private static string AssociationFile(string export) => FileFor(export) + ".source.json";
    internal static Task AssociateAsync(string export, string source) => Task.Run(() =>
    {
        var file = new FileInfo(export);
        ImageExportService.AtomicWrite(AssociationFile(export), JsonSerializer.SerializeToUtf8Bytes(new Association(Path.GetFullPath(source), file.Length, file.LastWriteTimeUtc.Ticks)), true);
    });
    internal static string ResolveSource(string export)
    {
        try { var doc = JsonSerializer.Deserialize<Association>(File.ReadAllText(AssociationFile(export))); var file = new FileInfo(export); if (doc != null && File.Exists(doc.Source) && doc.Length == file.Length && doc.Modified == file.LastWriteTimeUtc.Ticks) return doc.Source; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return export;
    }
    internal static string AssetDirectory(string source) => FileFor(source) + ".assets";
    internal static string AssetPath(string source, string asset)
    { if (asset != Path.GetFileName(asset) || asset.Contains(':')) throw new InvalidDataException("素材路径无效。"); return Path.Combine(AssetDirectory(source), asset); }
    internal static async Task<string> ImportAssetAsync(string source, string input, CancellationToken token)
    {
        string directory = AssetDirectory(source); Directory.CreateDirectory(directory);
        string name = Guid.NewGuid().ToString("N") + Path.GetExtension(input).ToLowerInvariant();
        string destination = AssetPath(source, name);
        try { await using var original = File.OpenRead(input); await using var output = File.Create(destination); await original.CopyToAsync(output, token); return name; }
        catch { File.Delete(destination); throw; }
    }
}
