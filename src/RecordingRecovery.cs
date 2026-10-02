using System;
using System.IO;
using System.Text.Json;

namespace QuickCapture;

internal sealed record RecordingMetadata(ExportQuality Quality, int Fps, bool HasAudio, double Duration);
internal static class RecordingRecovery
{
    internal static string DirectoryPath => Path.Combine(Paths.Data, "Recordings");
    internal static string NewMaster()
    { Directory.CreateDirectory(DirectoryPath); return Path.Combine(DirectoryPath, $"master-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.mp4"); }
    internal static bool Owns(string path) => Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(Path.GetFullPath(DirectoryPath), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).StartsWith("master-", StringComparison.Ordinal) && Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
    internal static void Remember(string path, RecordingMetadata metadata)
    { if (!Owns(path)) throw new ArgumentException("不是本程序的录屏母版。"); File.WriteAllText(path + ".json", JsonSerializer.Serialize(metadata)); }
    internal static RecordingMetadata Load(string path)
    {
        try { return JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(path + ".json")) ?? new(ExportQuality.Medium, 30, true, 0); }
        catch { return new(ExportQuality.Medium, 30, true, 0); }
    }
    internal static void Remove(string path)
    {
        // Only remove our owned intermediate files, never imported recordings or exports.
        if (!Owns(path)) return;
        File.Delete(path); File.Delete(path + ".json");
    }
}
