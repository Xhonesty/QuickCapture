using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace QuickCapture;

internal sealed record MediaCapabilities(string? Ffmpeg, string? Ffprobe, string Encoders, string Filters)
{
    internal bool HasVideoTools => Ffmpeg != null && Ffprobe != null;
    private bool HasFilter(string name) => Regex.IsMatch(Filters, @"(?m)^\s*[TSC.]+\s+" + Regex.Escape(name) + @"\s");
    private bool HasFilters(params string[] names) => names.All(HasFilter);
    internal bool Supports(RecordingFormat format) => HasVideoTools && HasFilters("trim", "setpts", "scale") && (format switch
    {
        RecordingFormat.Mp4 => Encoders.Contains("libx264", StringComparison.Ordinal) && Encoders.Contains(" aac ", StringComparison.Ordinal) && HasFilters("atrim", "asetpts", "atempo"),
        RecordingFormat.WebM => Encoders.Contains("libvpx-vp9", StringComparison.Ordinal) && Encoders.Contains("libopus", StringComparison.Ordinal) && HasFilters("atrim", "asetpts", "atempo"),
        RecordingFormat.Gif => Encoders.Contains(" gif ", StringComparison.Ordinal) && HasFilters("fps", "split", "palettegen", "paletteuse"),
        _ => false
    });
    internal bool CanEdit => Supports(RecordingFormat.Mp4);
}

internal static class MediaTools
{
    private static readonly Dictionary<string, MediaCapabilities> Cache = new();
    internal static string? Find(string name, string configured, bool includeSystem = true, bool includeBundled = true)
    {
        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) directories.Add(configured);
        if (includeSystem) directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        if (includeBundled) directories.Add(Path.Combine(AppContext.BaseDirectory, "Tools", "media"));
        foreach (string directory in directories)
        {
            try { string path = Path.GetFullPath(Path.Combine(directory.Trim().Trim('"'), name)); if (File.Exists(path)) return path; } catch (ArgumentException) { }
        }
        return null;
    }
    internal static async Task<MediaCapabilities> DetectAsync(Settings settings, CancellationToken cancellationToken = default, bool includeSystem = true, bool includeBundled = true)
    {
        string? ffmpeg = Find("ffmpeg.exe", settings.MediaToolsPath, includeSystem, includeBundled), probe = Find("ffprobe.exe", settings.MediaToolsPath, includeSystem, includeBundled);
        string key = $"{ffmpeg}|{probe}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        string encoders = "", filters = "";
        if (ffmpeg != null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var encoderResult = await RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" }, timeout.Token);
                var filterResult = await RunAsync(ffmpeg, new[] { "-hide_banner", "-filters" }, timeout.Token);
                if (encoderResult.ExitCode == 0 && filterResult.ExitCode == 0) { encoders = encoderResult.Output + encoderResult.Error; filters = filterResult.Output + filterResult.Error; }
                else ffmpeg = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { ErrorLog.Write(ex); ffmpeg = null; }
        }
        if (probe != null)
        {
            try { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10)); if ((await RunAsync(probe, new[] { "-version" }, timeout.Token)).ExitCode != 0) probe = null; }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { ErrorLog.Write(ex); probe = null; }
        }
        var result = new MediaCapabilities(ffmpeg, probe, encoders, filters);
        // A broken / incomplete system installation can use the bundled executables.
        string bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "media");
        if (includeBundled && (!result.CanEdit || !result.Supports(RecordingFormat.WebM) || !result.Supports(RecordingFormat.Gif)) && !Path.GetFullPath(settings.MediaToolsPath == "" ? AppContext.BaseDirectory : settings.MediaToolsPath).Equals(Path.GetFullPath(bundled), StringComparison.OrdinalIgnoreCase))
        {
            var fallback = await DetectAsync(new Settings { MediaToolsPath = bundled }, cancellationToken, false, false);
            if (fallback.CanEdit && fallback.Supports(RecordingFormat.WebM) && fallback.Supports(RecordingFormat.Gif)) result = fallback;
        }
        // Do not cache failures: copying missing tools into place fixes the next attempt.
        if (result.HasVideoTools) Cache[key] = result;
        return result;
    }
    internal sealed record ProcessResult(int ExitCode, string Output, string Error);
    internal static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, Action<string>? outputLine = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start }; process.Start();
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        async Task<string> ReadOutput()
        {
            var lines = new System.Text.StringBuilder();
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line) { lines.AppendLine(line); outputLine?.Invoke(line); }
            return lines.ToString();
        }
        var output = ReadOutput();
        try { await process.WaitForExitAsync(cancellationToken); return new(process.ExitCode, await output, await error); }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            throw;
        }
    }
}
