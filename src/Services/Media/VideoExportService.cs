using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuickCapture;

internal static class VideoExportService
{
    internal static string Extension(RecordingFormat format) => format.ToString().ToLowerInvariant();
    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    internal static async Task<VideoInfo> ProbeAsync(string path, MediaCapabilities tools, CancellationToken cancellationToken = default)
    {
        if (tools.Ffprobe == null) throw new InvalidOperationException("缺少 ffprobe，无法读取视频信息。请设置媒体工具目录或运行 tools/setup-media.ps1。");
        var result = await MediaTools.RunAsync(tools.Ffprobe, new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,width,height,avg_frame_rate,duration", "-of", "json", path }, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidDataException("无法读取录屏：" + result.Error);
        using var document = JsonDocument.Parse(result.Output); double duration = 0, fps = 30; int width = 0, height = 0; bool audio = false; string codec = "", audioCodec = "";
        double Value(JsonElement element, string name) => element.TryGetProperty(name, out var value) && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : 0;
        if (document.RootElement.TryGetProperty("format", out var container)) duration = Value(container, "duration");
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            string type = stream.GetProperty("codec_type").GetString() ?? "";
            if (type == "audio") { audio = true; audioCodec = stream.GetProperty("codec_name").GetString() ?? ""; }
            if (type != "video" || width > 0) continue;
            width = (int)Value(stream, "width"); height = (int)Value(stream, "height"); codec = stream.GetProperty("codec_name").GetString() ?? "";
            if (duration <= 0) duration = Value(stream, "duration");
            if (stream.TryGetProperty("avg_frame_rate", out var rate)) { var parts = rate.GetString()!.Split('/'); if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out double numerator) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out double denominator) && denominator > 0) fps = numerator / denominator; }
        }
        if (width <= 0 || height <= 0 || duration < 0 || !double.IsFinite(duration)) throw new InvalidDataException("录屏中没有可用的视频画面。");
        return new(duration, width, height, fps > 0 ? fps : 30, audio, codec, audioCodec);
    }
    internal static void Validate(VideoExportRequest request, VideoInfo info, MediaCapabilities tools)
    {
        if (!File.Exists(request.Source)) throw new FileNotFoundException("原始录屏不存在。", request.Source);
        if (!Enum.IsDefined(request.Format) || !Enum.IsDefined(request.Quality)) throw new ArgumentException("无效的导出格式或质量。");
        if (!double.IsFinite(request.Start) || !double.IsFinite(request.End) || request.Start < 0 || request.End > info.Duration + 0.001 || request.End < request.Start || (info.Duration > 0 && request.End - request.Start < Math.Min(1 / info.Fps, info.Duration))) throw new ArgumentException("请选择有效的起止时间，至少保留一帧。");
        if (request.Speed is not (0.5 or 1 or 1.5 or 2)) throw new ArgumentException("不支持该倍率。");
        if (request.GifFps < 5 || request.GifFps > 30) throw new ArgumentException("GIF 帧率需为 5–30 FPS。");
        if (request.FramesPerSecond is int fps && Array.IndexOf(RecordingFrameRates.For(request.Format), fps) < 0) throw new ArgumentException("不支持该格式的导出帧率。");
        request.Crop?.Validate(info);
        var objects = VideoObjectRules.Validate(request.Objects, info);
        if (objects.Length > 0 && !tools.CanEffects) throw new InvalidOperationException("视频效果需要 FFmpeg 解码与帧率处理能力，请检查媒体工具。");
        foreach (var item in objects.Where(o => o.Kind == VideoObjectKind.PictureInPicture)) if (!File.Exists(RecordingEditStore.AssetPath(request.Source, item.Asset!))) throw new FileNotFoundException("画中画素材缺失，请重新定位素材。");
        var normalized = VideoCuts.Normalize(request.Deleted, info.Duration, info.Fps);
        if (request.Deleted != null && !normalized.SequenceEqual(request.Deleted)) throw new ArgumentException("删除边界必须按原片帧率对齐并合并重叠区间。");
        var speeds = VideoTiming.Normalize(request.SpeedRanges, info.Duration, info.Fps);
        if (request.SpeedRanges != null && !speeds.SequenceEqual(request.SpeedRanges)) throw new ArgumentException("变速区间必须按帧对齐、消除重叠并合并同倍率相邻区间。");
        if (VideoCuts.Kept(request.Start, request.End, request.Deleted).Sum(r => r.End - r.Start) < Math.Min(1 / info.Fps, info.Duration) - .000001) throw new ArgumentException("至少保留一帧，不能导出空视频。");
        if (Path.GetFullPath(request.Source).Equals(Path.GetFullPath(request.Destination), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("导出目标不能覆盖正在编辑的原始录屏，请使用其他文件名。");
        if (Path.GetExtension(request.Destination).ToLowerInvariant() != "." + Extension(request.Format)) throw new ArgumentException("导出文件扩展名与格式不一致。");
        if (File.Exists(request.Destination) && !request.Overwrite) throw new IOException("目标文件已存在。");
        if (!request.OriginalMp4(info) && !tools.Supports(request.Format)) throw new InvalidOperationException("该格式需要可用的 FFmpeg / ffprobe 和对应编码器。可保存完整 MP4 原片，或补齐媒体工具后重试。");
    }
    internal static IReadOnlyList<string> Arguments(VideoExportRequest request, VideoInfo info, string temp, bool composed = false)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", request.Source };
        if (composed)
        {
            args.RemoveRange(args.Count - 2, 2);
            args.AddRange(new[] { "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{info.Width}x{info.Height}", "-framerate", Number(info.Fps), "-i", "pipe:0", "-i", request.Source });
        }
        bool audio = info.HasAudio && !request.Mute && request.Format != RecordingFormat.Gif;
        string prefix = "", videoInput = "[v0]", audioInput = "[a0]";
        var pieces = VideoTiming.Segments(request.Start, request.End, request.Deleted, request.SpeedRanges, request.Speed);
        var streams = new System.Text.StringBuilder();
        for (int i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i];
            prefix += $"[0:v:0]trim=start={Number(piece.Start)}:end={Number(piece.End)},setpts=(PTS-STARTPTS)/{Number(piece.Rate)}[v{i}];";
            streams.Append($"[v{i}]");
            if (audio) { prefix += $"[0:a:0]atrim=start={Number(piece.Start)}:end={Number(piece.End)},asetpts=PTS-STARTPTS,atempo={Number(piece.Rate)},apad,atrim=duration={Number(piece.Duration)}[a{i}];"; streams.Append($"[a{i}]"); }
        }
        if (pieces.Length > 1)
        {
            prefix += streams + $"concat=n={pieces.Length}:v=1:a={(audio ? 1 : 0)}[joinedv]" + (audio ? "[joineda]" : "") + ";";
            videoInput = "[joinedv]"; audioInput = "[joineda]";
        }
        string video = "null";
        if (request.Crop is { } crop) video += $",crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}:exact=1";
        if (request.Format == RecordingFormat.Gif)
        {
            int inputWidth = request.Crop?.Width ?? info.Width;
            int width = request.Quality == ExportQuality.Low ? 480 : request.Quality == ExportQuality.Medium ? 720 : inputWidth;
            int colors = request.Quality == ExportQuality.Low ? 64 : request.Quality == ExportQuality.Medium ? 128 : 256;
            // Older fps filters can repeat the final frame up to the original
            // trim-end timestamp. Bound the retimed stream before encoding.
            video += $",fps={request.FramesPerSecond ?? request.GifFps},trim=duration={Number(request.OutputDuration)},scale={Math.Min(width, inputWidth)}:-1:flags=lanczos,split[frames][paletteinput];[paletteinput]palettegen=max_colors={colors}:reserve_transparent=0[palette];[frames][palette]paletteuse=dither=sierra2_4a[out]";
            args.AddRange(new[] { "-filter_complex", prefix + videoInput + video, "-map", "[out]", "-an", "-loop", "0" });
        }
        else
        {
            if (request.FramesPerSecond is int fps) video += $",fps={fps}";
            video += $",trim=duration={Number(request.OutputDuration)}";
            string graph = prefix + $"{videoInput}{video},scale=trunc(iw/2)*2:trunc(ih/2)*2[outv]";
            if (audio) graph += $";{audioInput}apad,atrim=duration={Number(request.OutputDuration)}[outa]";
            args.AddRange(new[] { "-filter_complex", graph, "-map", "[outv]" });
            if (audio) args.AddRange(new[] { "-map", "[outa]", "-c:a", request.Format == RecordingFormat.Mp4 ? "aac" : "libopus", "-b:a", "128k" }); else args.Add("-an");
            int crf = request.Quality == ExportQuality.High ? 18 : request.Quality == ExportQuality.Medium ? 23 : 28;
            if (request.Format == RecordingFormat.Mp4) args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", "-movflags", "+faststart" });
            else args.AddRange(new[] { "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", (crf + 8).ToString(CultureInfo.InvariantCulture), "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p" });
        }
        if (composed) { int graphIndex = args.IndexOf("-filter_complex") + 1; args[graphIndex] = args[graphIndex].Replace("[0:a:0]", "[1:a:0]", StringComparison.Ordinal); }
        args.AddRange(new[] { "-progress", "pipe:1", "-nostats", temp }); return args;
    }
    internal static async Task ExportAsync(VideoExportRequest request, VideoInfo info, MediaCapabilities tools, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Validate(request, info, tools); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Destination))!);
        string temp = request.Destination + $".{Guid.NewGuid():N}.partial.{Extension(request.Format)}";
        try
        {
            if (request.OriginalMp4(info))
            {
                await using var input = File.OpenRead(request.Source); await using var output = File.Create(temp); await input.CopyToAsync(output, cancellationToken); progress?.Report(0.9);
            }
            else
            {
                if (request.Objects is { Count: > 0 }) await VideoComposedExport.RunAsync(request, info, tools, temp, progress, cancellationToken);
                else
                {
                var result = await MediaTools.RunAsync(tools.Ffmpeg!, Arguments(request, info, temp), cancellationToken, line =>
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && double.TryParse(line[12..], CultureInfo.InvariantCulture, out double microseconds) && request.OutputDuration > 0) progress?.Report(Math.Clamp(microseconds / 1_000_000 / request.OutputDuration, 0, 0.95));
                });
                if (result.ExitCode != 0) throw new InvalidOperationException("导出失败，原始录屏已保留。\n" + result.Error[^Math.Min(result.Error.Length, 1800)..]);
                }
                var exported = await ProbeAsync(temp, tools, cancellationToken);
                if (exported.Width <= 0 || exported.Duration <= 0) throw new InvalidDataException("导出文件验证失败，原始录屏已保留。");
                if (request.Crop is { } applied && request.Format != RecordingFormat.Gif && (exported.Width != applied.Width || exported.Height != applied.Height)) throw new InvalidDataException("导出视频尺寸与裁剪范围不一致，原始录屏已保留。");
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temp, request.Destination, request.Overwrite); progress?.Report(1);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
