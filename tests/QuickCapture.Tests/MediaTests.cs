using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace QuickCapture;

internal static class MediaTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "Exports"); Directory.CreateDirectory(directory);
        var tools = await MediaTools.DetectAsync(new Settings());
        var bundled = await MediaTools.DetectAsync(new Settings { MediaToolsPath = Path.Combine(AppContext.BaseDirectory, "Tools", "media") }, includeSystem: false, includeBundled: false);
        string source = Path.Combine(directory, "fixture.mp4"); VideoInfo info = new(4.8, 320, 180, 30, true);
        await check("System and bundled media tools detect codecs; real H264/AAC fixture probes correctly", async () =>
        {
            UpgradeTests.Ensure(tools.CanEdit && tools.Supports(RecordingFormat.WebM) && tools.Supports(RecordingFormat.Gif) && bundled.CanEdit && bundled.Supports(RecordingFormat.WebM) && bundled.Supports(RecordingFormat.Gif), "Missing export capabilities");
            var result = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=4.8", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4.8", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, CancellationToken.None);
            UpgradeTests.Ensure(result.ExitCode == 0, result.Error); info = await VideoExportService.ProbeAsync(source, tools);
            UpgradeTests.Ensure(info.HasAudio && info.VideoCodec == "h264" && info.AudioCodec == "aac" && info.Width == 320 && info.Height == 180 && Math.Abs(info.Duration - 4.8) < 0.08, "Fixture metadata incorrect");
            File.WriteAllText(Path.Combine(directory, "tools.json"), JsonSerializer.Serialize(new { system = tools.Ffmpeg, ffprobe = tools.Ffprobe, bundled = bundled.Ffmpeg }));
        });
        foreach (double speed in new[] { 0.5, 1d, 1.5, 2d })
        {
            await check($"MP4 actual trim at {speed}x keeps A/V duration and audio pitch", async () =>
            {
                string target = Path.Combine(directory, $"trim-{speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}x.mp4");
                var request = new VideoExportRequest(source, target, RecordingFormat.Mp4, ExportQuality.Medium, 0.7, 3.1, speed, Overwrite: true);
                await VideoExportService.ExportAsync(request, info, tools);
                var output = await VideoExportService.ProbeAsync(target, tools);
                UpgradeTests.Ensure(output.HasAudio && output.VideoCodec == "h264" && Math.Abs(output.Duration - request.OutputDuration) < 0.12 && output.Width == 320, "Trim / speed produced incorrect duration, audio or dimensions");
                await VerifyAudioAsync(target, request.OutputDuration, tools);
                await DecodeAsync(target, tools);
            });
        }
        await check("WebM VP9/Opus export with bundled tools and GIF palette FPS / no audio", async () =>
        {
            foreach (var format in new[] { RecordingFormat.WebM, RecordingFormat.Gif })
            {
                string target = Path.Combine(directory, "export." + VideoExportService.Extension(format));
                var request = new VideoExportRequest(source, target, format, ExportQuality.Low, 0.6, 2.6, GifFps: 10, Overwrite: true);
                await VideoExportService.ExportAsync(request, info, bundled); var output = await VideoExportService.ProbeAsync(target, bundled);
                UpgradeTests.Ensure(Math.Abs(output.Duration - 2) < 0.12 && output.Width == 320, "Format export has wrong duration or dimensions");
                if (format == RecordingFormat.WebM) UpgradeTests.Ensure(output.VideoCodec == "vp9" && output.AudioCodec == "opus" && output.HasAudio, "Wrong WebM codecs");
                else UpgradeTests.Ensure(!output.HasAudio && output.VideoCodec == "gif" && Math.Abs(output.Fps - 10) < 0.1, "GIF retained audio or ignored FPS");
                await DecodeAsync(target, bundled);
            }
        });
        await check("Muted exports remove audio; overwrite and invalid trim protect source / existing output", async () =>
        {
            string target = Path.Combine(directory, "muted.mp4"); var request = new VideoExportRequest(source, target, RecordingFormat.Mp4, ExportQuality.Low, 0.4, 2.4, Mute: true, Overwrite: true);
            await VideoExportService.ExportAsync(request, info, tools); UpgradeTests.Ensure(!(await VideoExportService.ProbeAsync(target, tools)).HasAudio, "Mute did not remove audio");
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(source)), outputHash = SHA256.HashData(File.ReadAllBytes(target));
            foreach (var bad in new[] { request with { Start = -1 }, request with { End = info.Duration + 1 }, request with { Start = 2, End = 1 }, request with { Destination = source }, request with { Overwrite = false } })
            { bool rejected = false; try { await VideoExportService.ExportAsync(bad, info, tools); } catch (Exception ex) when (ex is ArgumentException or IOException) { rejected = true; } UpgradeTests.Ensure(rejected, "Unsafe request accepted"); }
            UpgradeTests.Ensure(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))) && outputHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(target))), "Rejected export modified a file");
        });
        await check("Cancellation kills encoder, cleans only partial output and retains recoverable master", async () =>
        {
            string master = RecordingRecovery.NewMaster(); File.Copy(source, master); RecordingRecovery.Remember(master, new(ExportQuality.Medium, 30, true, info.Duration));
            string target = Path.Combine(directory, "cancelled.webm"); if (File.Exists(target)) File.Delete(target);
            using var cancellation = new CancellationTokenSource(); cancellation.CancelAfter(60); bool canceled = false;
            try { await VideoExportService.ExportAsync(new(master, target, RecordingFormat.WebM, ExportQuality.High, 0, info.Duration, 0.5), info, bundled, cancellationToken: cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
            UpgradeTests.Ensure(canceled && !File.Exists(target) && !Directory.EnumerateFiles(directory, "*.partial.*").Any() && File.Exists(master), "Cancel lost master or left partial / final output");
            UpgradeTests.Ensure(RecordingRecovery.Load(master).Duration == info.Duration, "Recovery metadata missing");
            RecordingRecovery.Remove(source); UpgradeTests.Ensure(File.Exists(source), "Recovery cleanup deleted an imported file");
            RecordingRecovery.Remove(master); UpgradeTests.Ensure(!File.Exists(master) && !File.Exists(master + ".json"), "Owned recovery cleanup failed");
        });
        await check("Missing media tools downgrade to byte-identical native MP4 and reject edited formats", async () =>
        {
            var missing = await MediaTools.DetectAsync(new Settings { MediaToolsPath = Path.Combine(directory, "missing-tools") }, includeSystem: false, includeBundled: false);
            UpgradeTests.Ensure(!missing.HasVideoTools, "Missing dependency detection failed");
            string target = Path.Combine(directory, "native-copy.mp4"); var request = new VideoExportRequest(source, target, RecordingFormat.Mp4, ExportQuality.Medium, 0, info.Duration, Overwrite: true);
            await VideoExportService.ExportAsync(request, info, missing);
            UpgradeTests.Ensure(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(target))), "Native fallback reencoded or lost data");
            foreach (var edit in new[] { request with { Start = 0.4 }, request with { Speed = 2 }, request with { Mute = true }, request with { Format = RecordingFormat.Gif, Destination = Path.ChangeExtension(target, "gif") } })
            { bool rejected = false; try { VideoExportService.Validate(edit, info, missing); } catch (InvalidOperationException) { rejected = true; } UpgradeTests.Ensure(rejected, "Missing dependency silently dropped an edit"); }
        });
        await check("Recording editor preview, real timeline drags, temporary format defaults and successful UI export", async () =>
        {
            var owner = new Window { Width = 500, Height = 400 }; Ui.Theme(owner); owner.Show();
            var settings = new Settings { OutputDirectory = directory, RecordingFormat = RecordingFormat.WebM, RecordingQuality = ExportQuality.High, GifFps = 20, ScreenshotFormat = ScreenshotFormat.Bmp };
            var dialog = new RecordingEditWindow(owner, source, settings, new(ExportQuality.Medium, 30, true, info.Duration));
            try
            {
                await UpgradeTests.WithDialogAsync(dialog, async () =>
                {
                    await dialog.Initialization; await Task.Delay(450); dialog.UpdateLayout();
                    var grips = dialog.Timeline.Children.OfType<Thumb>().ToArray(); var point = grips[0].PointToScreen(new(8, 18));
                    await HotkeyCaptureTests.DragAsync(dialog, point, point + new Vector(85, 0)); UpgradeTests.Ensure(dialog.Timeline.Start > 0.2, "Timeline start drag did not trim");
                    point = grips[1].PointToScreen(new(8, 18)); await HotkeyCaptureTests.DragAsync(dialog, point, point + new Vector(-85, 0)); UpgradeTests.Ensure(dialog.Timeline.End < info.Duration - 0.2, "Timeline end drag did not trim");
                    var format = UpgradeTests.Find<ComboBox>(dialog, b => b.Name == "RecordingFormat"); UpgradeTests.Ensure(format.SelectedIndex == 1, "Recording default format ignored");
                    format.SelectedIndex = 2;
                    foreach (string theme in new[] { "Dark", "Light" }) { ThemeService.Apply(theme); UiChangeTests.Render(dialog, $"recording-editor-{theme.ToLowerInvariant()}.png"); }
                    format.SelectedIndex = 0; UpgradeTests.Find<TextBox>(dialog, b => b.Name == "RecordingFilename").Text = "录屏 验证-" + Guid.NewGuid().ToString("N") + ".bad";
                    await dialog.ExportAsync();
                });
                UpgradeTests.Ensure(dialog.SavedPath?.EndsWith(".mp4") == true && File.Exists(dialog.SavedPath) && File.Exists(source), "UI export failed or deleted imported master");
                UpgradeTests.Ensure(settings.RecordingFormat == RecordingFormat.WebM && settings.RecordingQuality == ExportQuality.High && settings.ScreenshotFormat == ScreenshotFormat.Bmp, "Temporary recording export changed defaults");
            }
            finally { dialog.Close(); owner.Close(); ThemeService.Apply("Dark"); }
        });
    }
    private static async Task DecodeAsync(string path, MediaCapabilities tools)
    { var decoded = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-i", path, "-f", "null", "-" }, CancellationToken.None); UpgradeTests.Ensure(decoded.ExitCode == 0 && decoded.Error == "", "Export cannot fully decode: " + decoded.Error); }
    private static async Task VerifyAudioAsync(string path, double expectedDuration, MediaCapabilities tools)
    {
        string pcm = path + ".pcm";
        try
        {
            var result = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-i", path, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", pcm }, CancellationToken.None); UpgradeTests.Ensure(result.ExitCode == 0, result.Error);
            byte[] bytes = File.ReadAllBytes(pcm); double duration = bytes.Length / (4d * 48000); UpgradeTests.Ensure(Math.Abs(duration - expectedDuration) < 0.12, "Audio / video lengths diverge after speed change");
            int begin = Math.Min(bytes.Length / 4 - 1, 4800), end = Math.Min(bytes.Length / 4, begin + 24000); int crossings = 0;
            for (int i = begin + 1; i < end; i++) if (BitConverter.ToSingle(bytes, (i - 1) * 4) <= 0 && BitConverter.ToSingle(bytes, i * 4) > 0) crossings++;
            double pitch = crossings * 48000d / (end - begin); UpgradeTests.Ensure(Math.Abs(pitch - 440) < 12, $"Audio pitch changed to {pitch:F1} Hz");
        }
        finally { if (File.Exists(pcm)) File.Delete(pcm); }
    }
}
