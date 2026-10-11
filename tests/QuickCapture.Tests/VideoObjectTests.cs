using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace QuickCapture;

internal static class VideoObjectTests
{
    private static void Ensure(bool value, string message) => UpgradeTests.Ensure(value, message);
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "VideoObjects", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var tools = await MediaTools.DetectAsync(new Settings());
        string source = Path.Combine(directory, "source.mp4"), video = Path.Combine(directory, "pip.mp4"), image = Path.Combine(directory, "alpha.png");
        await Fixture(source, 320, 180, 30, 3, true, tools);
        await Fixture(video, 64, 48, 30, .8, false, tools);
        using (var bitmap = new SKBitmap(64, 48)) { using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent); using var paint = new SKPaint { Color = SKColors.Yellow }; canvas.DrawRect(16, 0, 48, 48, paint); using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100); using var output = File.Create(image); encoded.SaveTo(output); }
        var info = await VideoExportService.ProbeAsync(source, tools);
        string pictureAsset = await RecordingEditStore.ImportAssetAsync(source, image, CancellationToken.None), videoAsset = await RecordingEditStore.ImportAssetAsync(source, video, CancellationToken.None);
        var objects = new[] {
            new VideoObject("rect", VideoObjectKind.Rectangle, 25, 20, 65, 45, .2, 2.8, 0, Color:"#FFFFFF", Fill:"#FF0000", Opacity:.75),
            new VideoObject("round", VideoObjectKind.RoundedRectangle, 110, 15, 65, 40, .2, 2.8, 1, Color:"#FFFFFF", Fill:"#00FFFF"),
            new VideoObject("ellipse", VideoObjectKind.Ellipse, 185, 15, 60, 40, .2, 2.8, 2, Color:"#FFFFFF", Fill:"#00FF00"),
            new VideoObject("arrow", VideoObjectKind.Arrow, 40, 80, 90, 40, .2, 2.8, 3, Color:"#FFFFFF", Stroke:8),
            new VideoObject("brush", VideoObjectKind.Brush, 145, 70, 35, 55, .2, 2.8, 4, Color:"#FF00FF", Stroke:9, Points:new[]{new VideoPoint(145,70),new VideoPoint(180,100),new VideoPoint(150,125)}),
            new VideoObject("mosaic", VideoObjectKind.Mosaic, 220, 80, 80, 70, .2, 2.8, 5, BlockSize:16),
            new VideoObject("pip", VideoObjectKind.PictureInPicture, 20, 130, 64, 48, .2, 2.8, 6, Asset:pictureAsset, AssetWidth:64, AssetHeight:48)
        };
        await check("Object schema rejects invalid coordinates, times, duplicate IDs, unsafe assets and future projects; legacy edits load", async () =>
        {
            VideoObjectRules.Validate(objects, info);
            foreach (var bad in new[] { objects[0] with { X = -1 }, objects[0] with { End = 9 }, objects[0] with { Opacity = double.NaN }, objects[0] with { Start = .21 }, objects[^1] with { Asset = "../secret.png" } })
            { bool rejected = false; try { bad.Validate(info); } catch (InvalidDataException) { rejected = true; } Ensure(rejected, "Unsafe object accepted"); }
            bool duplicate = false; try { VideoObjectRules.Validate(new[] { objects[0], objects[0] }, info); } catch (InvalidDataException) { duplicate = true; } Ensure(duplicate, "Duplicate identifiers accepted");
            var state = new RecordingEditState(0, 3, Array.Empty<VideoRange>(), Objects: objects); await RecordingEditStore.SaveAsync(source, state); var restored = await RecordingEditStore.LoadAsync(source);
            Ensure(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(state), "Object properties didn't roundtrip");
            var legacy = JsonSerializer.Deserialize<RecordingEditState>("{\"Start\":0,\"End\":3,\"Deleted\":[]}"); Ensure(legacy!.Objects == null, "Old project incompatible");
            var mapped = new RecordingEditState(0, 3, new[] { new VideoRange(.5, 1), new VideoRange(1.5, 2) }, 2);
            Ensure(Math.Abs(VideoObjectRules.SourceTime(.25, mapped) - 1) < .000001 && Math.Abs(VideoObjectRules.SourceTime(.5, mapped) - 2) < .000001, "Deleted/speed time map wrong");
        });
        await check("Mosaic processes real pixels and covers first and final included frames without leaking outside its interval", async () =>
        {
            using var reader = new VideoFrameReader(tools.Ffmpeg!, source, info.Width, info.Height, info.Fps); var raw = (await reader.FrameAsync(.2, CancellationToken.None))!;
            var mosaic = objects.Single(o => o.Kind == VideoObjectKind.Mosaic); var material = new Dictionary<string, SKBitmap>();
            foreach (var (time, active) in new[] { (5 / 30d, false), (6 / 30d, true), (83 / 30d, true), (84 / 30d, false) })
            {
                var pixels = (byte[])raw.Clone(); VideoObjectRenderer.Render(pixels, info, new[] { mosaic }, time, material);
                Ensure(pixels.SequenceEqual(raw) != active, "Mosaic frame boundary leaked");
                if (active) { int a = (85 * info.Width + 225) * 4, b = (90 * info.Width + 230) * 4; Ensure(pixels.AsSpan(a, 3).SequenceEqual(pixels.AsSpan(b, 3)), "Mosaic uses a mask rather than real block pixels"); }
            }
        });
        await check("Shared preview compositor, transparent image, layer order and video material end are exact", async () =>
        {
            using var reader = new VideoFrameReader(tools.Ffmpeg!, source, info.Width, info.Height, info.Fps); var raw = (byte[])(await reader.FrameAsync(.4, CancellationToken.None))!.Clone();
            using var assets = new VideoObjectFrames(source, tools.Ffmpeg!, info.Fps); var material = await assets.AtAsync(objects, .4, CancellationToken.None); var expected = (byte[])raw.Clone(); VideoObjectRenderer.Render(expected, info, objects, .4, material);
            int transparent = (150 * info.Width + 25) * 4, opaque = (150 * info.Width + 65) * 4;
            Ensure(raw.AsSpan(transparent, 3).SequenceEqual(expected.AsSpan(transparent, 3)) && expected[opaque + 1] > 230 && expected[opaque + 2] > 230, "Alpha image doesn't preserve underlying pixels");
            var completion = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously); using var preview = new VideoObjectPreview(source, tools.Ffmpeg!, info); preview.FrameReady += frame => completion.TrySetResult(frame); preview.Failed += error => completion.TrySetException(new Exception(error)); preview.Request(.4, objects, true);
            byte[] actual = UpgradeTests.Bytes(await completion.Task.WaitAsync(TimeSpan.FromSeconds(15))); Ensure(actual.SequenceEqual(expected), "Preview and export compositor differ");
            var pip = objects[^1] with { Asset = videoAsset, AssetVideo = true, AssetDuration = .8, AssetStart = .2 };
            Ensure(pip.Active(.2, info.Fps) && pip.Active(23 / 30d, info.Fps) && !pip.Active(.8, info.Fps), "Video material freezes after its end");
            var ended = (byte[])raw.Clone(); VideoObjectRenderer.Render(ended, info, new[] { pip }, .9, await assets.AtAsync(new[] { pip }, .9, CancellationToken.None)); Ensure(ended.SequenceEqual(raw), "Ended PIP still displays");
        });
        await check("All effects are encoded in MP4/WebM/GIF with crop, speed, multiple cuts, mute and source preservation", async () =>
        {
            byte[] before = SHA256.HashData(File.ReadAllBytes(source)); var cuts = new[] { new VideoRange(.6, 1), new VideoRange(1.8, 2.2) }; var crop = new VideoCrop(20, 10, 280, 160);
            foreach (var format in Enum.GetValues<RecordingFormat>())
            {
                string target = Path.Combine(directory, "effects." + VideoExportService.Extension(format)); var request = new VideoExportRequest(source, target, format, ExportQuality.High, 0, 3, 1.5, format == RecordingFormat.WebM, FramesPerSecond: 15, Crop: crop, Deleted: cuts, Objects: objects);
                var watch = Stopwatch.StartNew(); await VideoExportService.ExportAsync(request, info, tools); var result = await VideoExportService.ProbeAsync(target, tools);
                Ensure(result.Width == crop.Width && result.Height == crop.Height && Math.Abs(result.Duration - request.OutputDuration) < .13 && result.HasAudio == (format == RecordingFormat.Mp4), "Effect output dimensions/time/audio wrong");
                using var decoder = new VideoFrameReader(tools.Ffmpeg!, target, result.Width, result.Height, 15); var pixels = (await decoder.FrameAsync(.2, CancellationToken.None))!;
                int rect = ((35 - crop.Y) * result.Width + 60 - crop.X) * 4, round = ((35 - crop.Y) * result.Width + 140 - crop.X) * 4, brush = ((100 - crop.Y) * result.Width + 175 - crop.X) * 4;
                Ensure(pixels[rect + 2] > 170 && pixels[round] > 180 && pixels[round + 1] > 180 && pixels[brush] > 150 && pixels[brush + 2] > 150, "Shapes or brush missing in encoded file");
                File.WriteAllText(Path.Combine(directory, "performance-" + format + ".json"), JsonSerializer.Serialize(new { format, seconds = watch.Elapsed.TotalSeconds, sourceDuration = info.Duration, outputDuration = result.Duration }));
                var decode = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-v", "error", "-i", target, "-f", "null", "-" }, CancellationToken.None); Ensure(decode.ExitCode == 0 && string.IsNullOrWhiteSpace(decode.Error), "Encoded effects fail decoding");
                if (format == RecordingFormat.Mp4) { var probe = await MediaTools.RunAsync(tools.Ffprobe!, new[] { "-v", "error", "-show_entries", "stream=duration", "-of", "json", target }, CancellationToken.None); using var json = JsonDocument.Parse(probe.Output); var times = json.RootElement.GetProperty("streams").EnumerateArray().Select(e => double.Parse(e.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); Ensure(times.Max() - times.Min() < .11, "Composed audio/video drift"); }
            }
            Ensure(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(before), "Effects mutated source");
        });
        await check("Video PIP exports at material offset and hides after its end; different frame rates and portrait remain aligned", async () =>
        {
            foreach (int fps in new[] { 15, 60 })
            {
                string portrait = Path.Combine(directory, "portrait-" + fps + ".mp4"); await Fixture(portrait, 180, 320, fps, 1.2, false, tools); var portraitInfo = await VideoExportService.ProbeAsync(portrait, tools);
                var asset = await RecordingEditStore.ImportAssetAsync(portrait, video, CancellationToken.None);
                var pip = new VideoObject("p", VideoObjectKind.PictureInPicture, 30, 50, 96, 72, 0, portraitInfo.Duration, 0, Asset: asset, AssetVideo: true, AssetWidth: 64, AssetHeight: 48, AssetDuration: .8, AssetStart: .2);
                var mosaic = new VideoObject("m", VideoObjectKind.Mosaic, 20, 180, 90, 80, 0, portraitInfo.Duration, 1, BlockSize: 24);
                string target = Path.Combine(directory, "portrait-pip-" + fps + ".mp4"); await VideoExportService.ExportAsync(new(portrait, target, RecordingFormat.Mp4, ExportQuality.High, 0, portraitInfo.Duration, Objects: new[] { pip, mosaic }, FramesPerSecond: fps), portraitInfo, tools);
                var result = await VideoExportService.ProbeAsync(target, tools); Ensure(Math.Abs(result.Fps - fps) < .001 && result.Width == 180 && result.Height == 320, "Portrait/fps changed");
                using var exported = new VideoFrameReader(tools.Ffmpeg!, target, 180, 320, fps); using var original = new VideoFrameReader(tools.Ffmpeg!, portrait, 180, 320, fps);
                var after = (await exported.FrameAsync(.9, CancellationToken.None))!; var baseFrame = (await original.FrameAsync(.9, CancellationToken.None))!;
                int point = (80 * 180 + 60) * 4; Ensure(Enumerable.Range(0, 3).All(c => Math.Abs(after[point + c] - baseFrame[point + c]) < 25), "PIP froze after material end");
            }
        });
        await check("Object export cancellation, missing assets, unavailable media and failing output retain original/project", async () =>
        {
            byte[] original = SHA256.HashData(File.ReadAllBytes(source)); using var cancel = new CancellationTokenSource(); string target = Path.Combine(directory, "cancel.mp4");
            var run = VideoExportService.ExportAsync(new(source, target, RecordingFormat.Mp4, ExportQuality.High, 0, 3, Objects: objects), info, tools, cancellationToken: cancel.Token); await Task.Delay(120); cancel.Cancel();
            bool cancelled = false; try { await run; } catch (OperationCanceledException) { cancelled = true; } Ensure(cancelled && !File.Exists(target) && !Directory.GetFiles(directory, "*.partial*").Any(), "Cancellation committed or leaked output");
            bool missing = false; try { VideoExportService.Validate(new(source, target, RecordingFormat.Mp4, ExportQuality.High, 0, 3, Objects: new[] { objects[^1] with { Asset = "missing.png" } }), info, tools); } catch (FileNotFoundException) { missing = true; } Ensure(missing, "Missing material silently ignored");
            bool unsupported = false; try { VideoExportService.Validate(new(source, target, RecordingFormat.Mp4, ExportQuality.High, 0, 3, Objects: objects), info, new(null, null, "", "")); } catch (InvalidOperationException) { unsupported = true; } Ensure(unsupported, "Missing media effects accepted");
            string blocked = Path.Combine(directory, "blocked"); File.WriteAllText(blocked, "locked"); bool failed = false; try { await VideoExportService.ExportAsync(new(source, Path.Combine(blocked, "file.mp4"), RecordingFormat.Mp4, ExportQuality.High, 0, 3, Objects: objects), info, tools); } catch (IOException) { failed = true; }
            Ensure(failed && SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(original) && (await RecordingEditStore.LoadAsync(source))!.Objects!.Length == objects.Length, "Failure lost original or objects");
        });
        await check("Object editing history, selection, geometry, four tracks, live themes and configuration reopening", async () =>
        {
            var owner = new Window(); owner.Show(); RecordingEditWindow? dialog = null;
            try
            {
                dialog = new(owner, source, new Settings { OutputDirectory = directory }, new(ExportQuality.Medium, 30, true, 3)); dialog.Show(); await dialog.Initialization;
                Ensure(dialog.ObjectEditor.Objects.Length == objects.Length && dialog.PreserveMaster, "Objects not reopened");
                dialog.ObjectEditor.Restore(Array.Empty<VideoObject>());
                Ensure(dialog.ObjectEditor.Tracks.Visibility == Visibility.Collapsed, "Unused tools expose empty tracks");
                dialog.ObjectEditor.SetTool(VideoObjectKind.Brush);
                Ensure(dialog.ObjectEditor.Tracks.VisibleCategories.SequenceEqual(new[] { 1 }) && dialog.ObjectEditor.Tracks.Visibility == Visibility.Visible, "Choosing brush didn't show only its track");
                dialog.ObjectEditor.SetTool(VideoObjectKind.Rectangle);
                Ensure(dialog.ObjectEditor.Tracks.VisibleCategories.SequenceEqual(new[] { 0 }), "Switching empty tools retained an unused track");
                dialog.ObjectEditor.Overlay.Cancel();
                Ensure(dialog.ObjectEditor.Tracks.Visibility == Visibility.Collapsed, "Cancelling unused tool retained its track");
                dialog.ObjectEditor.Restore(objects); dialog.UpdateLayout();
                var tracks = dialog.ObjectEditor.Tracks;
                Ensure(Math.Abs(tracks.ActualWidth - dialog.Timeline.ActualWidth) < .01 && Math.Abs(tracks.X(0) - 8) < .01 && Math.Abs(tracks.X(info.Duration) - (dialog.Timeline.ActualWidth - 8)) < .01, "Tool/main time scales differ");
                Ensure(tracks.TranslatePoint(new Point(0, tracks.ActualHeight), dialog).Y <= dialog.Timeline.TranslatePoint(new Point(0, 0), dialog).Y + .01, "Tool tracks aren't above the main timeline");
                var brush = dialog.ObjectEditor.Objects.Single(o => o.Kind == VideoObjectKind.Brush); var moved = brush.Transform(new Rect(100, 60, 70, 80));
                dialog.ObjectEditor.Replace(moved, true); dialog.UndoEdit(); Ensure(dialog.ObjectEditor.Objects.Single(o => o.Id == brush.Id).X == brush.X, "Object undo wrong"); dialog.RedoEdit(); Ensure(dialog.ObjectEditor.Objects.Single(o => o.Id == brush.Id).X == 100, "Object redo wrong");
                dialog.ObjectEditor.Select(dialog.ObjectEditor.Objects[^1], true); dialog.ObjectEditor.Delete(); Ensure(!tracks.VisibleCategories.Contains(3), "Deleting last PIP retained its track"); dialog.UndoEdit(); Ensure(dialog.ObjectEditor.Objects.Any(o => o.Kind == VideoObjectKind.PictureInPicture) && tracks.VisibleCategories.Contains(3), "Delete undo lost material/track");
                foreach (string theme in new[] { "Light", "Dark" }) { ThemeService.Apply(theme); dialog.Width = 1060; dialog.Height = Math.Min(950, SystemParameters.WorkArea.Height - 24); dialog.ObjectEditor.Select(dialog.ObjectEditor.Objects.Single(o => o.Kind == VideoObjectKind.Mosaic), true); await Task.Delay(650); dialog.UpdateLayout(); UiChangeTests.Render(dialog, "video-objects-" + theme.ToLowerInvariant() + ".png"); Ensure(dialog.ObjectEditor.Tracks.ActualHeight >= 70 && dialog.ObjectEditor.Overlay.ActualWidth > 10, "Object tracks/viewport missing"); }
                await dialog.ConfigurationSaved; dialog.Close(); dialog = new(owner, source, new Settings { OutputDirectory = directory }, new(ExportQuality.Medium, 30, true, 3)); dialog.Show(); await dialog.Initialization; Ensure(dialog.ObjectEditor.Objects.Single(o => o.Id == brush.Id).X == 100, "Saved transformed objects lost");
            }
            finally { dialog?.Close(); owner.Close(); }
        });
        await check("Default direct save uses MP4/WebM/GIF defaults, recents association, cancellation and retry after output failure", async () =>
        {
            string master = RecordingRecovery.NewMaster(); File.Copy(source, master); RecordingRecovery.Remember(master, new(ExportQuality.Medium, 30, true, 3)); var owner = new Window(); owner.Show();
            try
            {
                foreach (var format in Enum.GetValues<RecordingFormat>())
                {
                    var settings = new Settings { OutputDirectory = directory, RecordingFormat = format, RecordingQuality = ExportQuality.High }; settings.SetRecordingFps(format, format == RecordingFormat.Gif ? 10 : 15);
                    var window = new RecordingSaveWindow(owner, master, settings, RecordingRecovery.Load(master)); window.Show(); await Task.Delay(60); await window.Completion;
                    Ensure(window.SavedPath != null && File.Exists(window.SavedPath), "Default save failed for " + format); var result = await VideoExportService.ProbeAsync(window.SavedPath!, tools);
                    Ensure(Math.Abs(result.Fps - settings.GetRecordingFps(format)) < .01 && Math.Abs(result.Duration - 3) < .15 && RecordingEditStore.ResolveSource(window.SavedPath!) == master, "Direct save defaults/association wrong"); window.Close();
                }
                string blocked = Path.Combine(directory, "blocked-save"); File.WriteAllText(blocked, "block"); var retrySettings = new Settings { OutputDirectory = blocked };
                var retry = new RecordingSaveWindow(owner, master, retrySettings, RecordingRecovery.Load(master)); retry.Show(); await Task.Delay(60); await retry.Completion; Ensure(retry.SavedPath == null && File.Exists(master), "Failure lost master");
                UpgradeTests.Find<TextBlock>(retry, b => b.Name == "DirectSaveStatus").Text = string.Concat(Enumerable.Repeat("保存失败，原片已保留。输出目录不可写，请修复后重试。", 60)); retry.UpdateLayout();
                var retryButton = UpgradeTests.Find<Button>(retry, b => b.Name == "RetryDirectSave"); Ensure(retryButton.IsVisible && retryButton.TranslatePoint(new Point(0, retryButton.ActualHeight), retry).Y < retry.ActualHeight - 10, "Long errors hide retry/continue editing controls"); UiChangeTests.Render(retry, "recording-save-failed.png");
                retrySettings.OutputDirectory = directory; UpgradeTests.Find<Button>(retry, b => b.Name == "RetryDirectSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await retry.Completion; Ensure(retry.SavedPath != null, "Direct save retry failed"); retry.Close();
                var cancelled = new RecordingSaveWindow(owner, master, new Settings { OutputDirectory = directory, RecordingFormat = RecordingFormat.WebM }, RecordingRecovery.Load(master)); cancelled.Show(); UpgradeTests.Find<Button>(cancelled, b => b.Name == "CancelDirectSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await cancelled.Completion; Ensure(cancelled.SavedPath == null && File.Exists(master), "Cancel didn't keep master"); cancelled.Close();
            }
            finally { owner.Close(); }
        });
        await check("1080p effects export keeps dispatcher responsive; sequential and backward preview reuse matches compositor", async () =>
        {
            string full = Path.Combine(directory, "full-hd.mp4"); await Fixture(full, 1920, 1080, 30, 3, true, tools);
            var fullInfo = await VideoExportService.ProbeAsync(full, tools);
            string asset = await RecordingEditStore.ImportAssetAsync(full, image, CancellationToken.None);
            var effects = new[] { objects[0] with { X = 240, Y = 120, Width = 400, Height = 240 }, objects[4].Transform(new Rect(700, 240, 320, 360)), objects[5] with { X = 1100, Y = 400, Width = 700, Height = 500, BlockSize = 24 }, objects[^1] with { X = 80, Y = 800, Width = 320, Height = 240, Asset = asset } };
            int ticks = 0; double longestTick = 0; var clock = Stopwatch.StartNew(); double last = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { double now = clock.Elapsed.TotalMilliseconds; longestTick = Math.Max(longestTick, now - last); last = now; ticks++; }; timer.Start();
            var exportWatch = Stopwatch.StartNew();
            try { await VideoExportService.ExportAsync(new(full, Path.Combine(directory, "full-hd-effects.mp4"), RecordingFormat.Mp4, ExportQuality.High, 0, 3, Objects: effects), fullInfo, tools); }
            finally { timer.Stop(); exportWatch.Stop(); }
            Ensure(ticks > 5, "Effects encoding blocked the WPF dispatcher");
            var latencies = new List<double>(); var ready = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var preview = new VideoObjectPreview(full, tools.Ffmpeg!, fullInfo); preview.FrameReady += frame => ready.TrySetResult(frame); preview.Failed += error => ready.TrySetException(new Exception(error));
            using var reader = new VideoFrameReader(tools.Ffmpeg!, full, 1920, 1080, 30); using var materials = new VideoObjectFrames(full, tools.Ffmpeg!, 30);
            foreach (double time in new[] { .4, .5, 2.2, .3 })
            {
                ready = new(TaskCreationOptions.RunContinuationsAsynchronously); var watch = Stopwatch.StartNew(); preview.Request(time, effects, true); var frame = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); latencies.Add(watch.Elapsed.TotalMilliseconds);
                var expected = (byte[])(await reader.FrameAsync(time, CancellationToken.None))!.Clone(); VideoObjectRenderer.Render(expected, fullInfo, effects, time, await materials.AtAsync(effects, time, CancellationToken.None));
                Ensure(UpgradeTests.Bytes(frame).SequenceEqual(expected), "Reused decoder returned a stale effect frame after seek");
            }
            File.WriteAllText(Path.Combine(directory, "performance-1080p.json"), JsonSerializer.Serialize(new { width = 1920, height = 1080, sourceFps = 30, sourceSeconds = 3, exportSeconds = exportWatch.Elapsed.TotalSeconds, dispatcherTicks = ticks, longestDispatcherGapMs = longestTick, previewLatencyMs = latencies, processPeakMegabytes = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d }));
        });
        File.WriteAllText(Path.Combine(Paths.TestRoot!, "video-objects-fixtures.json"), JsonSerializer.Serialize(new { source, video, image, directory }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static async Task Fixture(string path, int width, int height, int fps, double duration, bool audio, MediaCapabilities tools)
    {
        string d = duration.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var args = new List<string> { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={fps}:duration={d}" };
        if (audio) args.AddRange(new[] { "-f", "lavfi", "-i", $"sine=frequency=440:duration={d}", "-c:a", "aac", "-shortest" });
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path }); var result = await MediaTools.RunAsync(tools.Ffmpeg!, args, CancellationToken.None); Ensure(result.ExitCode == 0, "Fixture failed: " + result.Error);
    }
}
