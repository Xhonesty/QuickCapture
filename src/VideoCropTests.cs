using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture;

internal static class VideoCropTests
{
    // Run after MediaTests so its real H.264/AAC recording is shared, rather than recorded again.
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "Exports"), source = Path.Combine(directory, "fixture.mp4");
        var tools = await MediaTools.DetectAsync(new Settings());
        var info = await VideoExportService.ProbeAsync(source, tools);
        await check("Video crop aligns source coordinates, bounds and encoder dimensions; edits require media tools", () =>
        {
            var legacy = JsonSerializer.Deserialize<RecordingMetadata>("{\"Quality\":1,\"Fps\":30,\"HasAudio\":true,\"Duration\":4.8}");
            UpgradeTests.Ensure(legacy is { Crop: null, Fps: 30, HasAudio: true }, "Legacy recording metadata did not load without the optional crop field");
            var aligned = VideoCrop.FromSelection(new Rect(33, 19, 163, 101), 320, 180);
            UpgradeTests.Ensure(aligned == new VideoCrop(32, 20, 164, 100), "Crop did not align to source chroma pixels"); aligned.Validate(info);
            var edge = VideoCrop.FromSelection(new Rect(319, 179, 50, 50), 320, 180); edge.Validate(info);
            UpgradeTests.Ensure(edge.X + edge.Width <= 320 && edge.Y + edge.Height <= 180, "Crop left source frame");
            var request = new VideoExportRequest(source, Path.Combine(directory, "crop-check.mp4"), RecordingFormat.Mp4, ExportQuality.Medium, 0, info.Duration, Crop: aligned, Overwrite: true);
            UpgradeTests.Ensure(!request.OriginalMp4(info), "Crop incorrectly used the original-file copy path");
            foreach (var bad in new[] { new VideoCrop(-2, 0, 100, 100), new VideoCrop(0, 0, 321, 100), new VideoCrop(3, 0, 100, 100), new VideoCrop(0, 0, 0, 100) })
            { bool rejected = false; try { VideoExportService.Validate(request with { Crop = bad }, info, tools); } catch (ArgumentException) { rejected = true; } UpgradeTests.Ensure(rejected, "Invalid crop was accepted"); }
            bool toolsRejected = false; try { VideoExportService.Validate(request, info, new MediaCapabilities(null, null, "", "")); } catch (InvalidOperationException) { toolsRejected = true; }
            UpgradeTests.Ensure(toolsRejected, "Missing media tools silently discarded crop");
            string gifGraph = string.Join(" ", VideoExportService.Arguments(request with { Format = RecordingFormat.Gif, Quality = ExportQuality.High }, info, "crop.gif"));
            UpgradeTests.Ensure(gifGraph.Contains("crop=164:100:32:20:exact=1") && gifGraph.Contains("scale=164:-1"), "GIF scaling ignored the cropped source width");
            return Task.CompletedTask;
        });
        await check("Actual spatial crop plus trim / speed preserves pixels, even dimensions, audio sync and source file", async () =>
        {
            byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
            var crop = new VideoCrop(32, 20, 164, 100);
            string target = Path.Combine(directory, "spatial-crop.mp4");
            var request = new VideoExportRequest(source, target, RecordingFormat.Mp4, ExportQuality.High, 0.8, 3.2, Speed: 1.5, Crop: crop, Overwrite: true);
            double progress = 0; await VideoExportService.ExportAsync(request, info, tools, new Progress<double>(value => progress = value));
            var output = await VideoExportService.ProbeAsync(target, tools);
            UpgradeTests.Ensure(output.Width == crop.Width && output.Height == crop.Height && output.HasAudio && Math.Abs(output.Duration - request.OutputDuration) < 0.12, "Spatial crop changed dimensions, audio or selected duration");
            string expected = Path.Combine(directory, "crop-reference.png"), actual = Path.Combine(directory, "crop-exported.png"), pcm = Path.Combine(directory, "crop-audio.pcm");
            var reference = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-i", source, "-vf", "trim=start=0.8,crop=164:100:32:20:exact=1", "-frames:v", "1", expected }, CancellationToken.None);
            var frame = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-i", target, "-frames:v", "1", actual }, CancellationToken.None);
            UpgradeTests.Ensure(reference.ExitCode == 0 && frame.ExitCode == 0, reference.Error + frame.Error);
            byte[] a = UpgradeTests.Bytes(ImageExportService.Decode(File.ReadAllBytes(expected), ScreenshotFormat.Png)), b = UpgradeTests.Bytes(ImageExportService.Decode(File.ReadAllBytes(actual), ScreenshotFormat.Png));
            UpgradeTests.Ensure(a.Length == b.Length && a.Zip(b, (left, right) => Math.Abs(left - right)).Average() < 7, "Exported crop pixels disagree with the selected source region");
            var audio = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-i", target, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", pcm }, CancellationToken.None);
            UpgradeTests.Ensure(audio.ExitCode == 0 && Math.Abs(new FileInfo(pcm).Length / (4d * 48000) - request.OutputDuration) < 0.12, "Crop lost audio sync");
            File.Delete(pcm);
            await Task.Yield(); UpgradeTests.Ensure(progress == 1 && hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "Export progress incomplete or original video changed");
        });
        await check("Video editor real crop handle / move, ratios, applied playback viewport, Escape and reset", async () =>
        {
            string master = RecordingRecovery.NewMaster(); File.Copy(source, master);
            RecordingRecovery.Remember(master, new(ExportQuality.Medium, 30, true, info.Duration));
            var owner = new Window { Width = 500, Height = 400 }; Ui.Theme(owner); owner.Show();
            var dialog = new RecordingEditWindow(owner, master, new Settings { OutputDirectory = directory }, RecordingRecovery.Load(master));
            RecordingEditWindow? reopened = null;
            VideoCrop? confirmed = null;
            try
            {
                await UpgradeTests.WithDialogAsync(dialog, async () =>
                {
                    await dialog.Initialization; await Task.Delay(250); dialog.UpdateLayout(); dialog.BeginCrop(); dialog.UpdateLayout();
                    var ratio = UpgradeTests.Find<ComboBox>(dialog, b => b.Name == "VideoCropRatio"); ratio.SelectedIndex = 2;
                    var overlay = dialog.CropOverlay;
                    UpgradeTests.Ensure(Math.Abs(overlay.Selection.Width / overlay.Selection.Height - 16d / 9) < 0.001, "16:9 ratio ignored");
                    Rect before = overlay.Selection; Point handle = overlay.PointToScreen(overlay.SourceToPreview(before.BottomRight) - new Vector(2, 2));
                    await HotkeyCaptureTests.DragAsync(dialog, handle, handle + new Vector(-65, -36));
                    UpgradeTests.Ensure(overlay.Selection.Width < before.Width - 20 && Math.Abs(overlay.Selection.Width / overlay.Selection.Height - 16d / 9) < 0.001, "Real crop handle did not resize with ratio");
                    ratio.SelectedIndex = 0; before = overlay.Selection;
                    Point middle = overlay.PointToScreen(overlay.SourceToPreview(new Point(before.X + before.Width / 2, before.Y + before.Height / 2)));
                    await HotkeyCaptureTests.DragAsync(dialog, middle, middle + new Vector(25, 15));
                    UpgradeTests.Ensure(overlay.Selection.X > before.X + 3 && overlay.Selection.Y > before.Y + 3, "Real crop movement did not map preview pixels to source");
                    var expected = VideoCrop.FromSelection(overlay.Selection, info.Width, info.Height); dialog.ConfirmCrop(); dialog.UpdateLayout();
                    UpgradeTests.Ensure(dialog.AppliedCrop == expected && overlay.Visibility == Visibility.Collapsed, "Confirm did not apply crop");
                    UpgradeTests.Ensure(RecordingRecovery.Load(master).Crop == expected, "Confirmed crop did not persist in owned master metadata"); confirmed = expected;
                    var media = UpgradeTests.Find<MediaElement>(dialog, element => element.Name == "RecordingPreview"); var canvas = (Canvas)media.Parent; Rect clip = canvas.Clip.Bounds;
                    double scale = media.Width / info.Width;
                    UpgradeTests.Ensure(Math.Abs(clip.Width - expected.Width * scale) < 0.01 && Math.Abs(clip.Height - expected.Height * scale) < 0.01 && Math.Abs(Canvas.GetLeft(media) + expected.X * scale - clip.X) < 0.01 && Math.Abs(Canvas.GetTop(media) + expected.Y * scale - clip.Y) < 0.01, "Applied playback preview displays a different source crop");
                    dialog.BeginCrop(); overlay.SetSelection(new Rect(0, 0, 100, 80)); await HotkeyCaptureTests.PressAsync(dialog, Key.Escape);
                    UpgradeTests.Ensure(dialog.AppliedCrop == expected && overlay.Visibility == Visibility.Collapsed && RecordingRecovery.Load(master).Crop == expected, "Escape committed or persisted the crop draft");
                });
                var restored = new RecordingEditWindow(owner, master, new Settings { OutputDirectory = directory }, RecordingRecovery.Load(master)); reopened = restored;
                await UpgradeTests.WithDialogAsync(restored, async () =>
                {
                    await restored.Initialization; restored.UpdateLayout();
                    UpgradeTests.Ensure(restored.AppliedCrop == confirmed && confirmed != null, "Reopening the owned master did not restore the confirmed crop");
                    restored.BeginCrop(); UpgradeTests.Ensure(restored.CropOverlay.Selection == confirmed!.Bounds, "Reopened crop handles did not restore source pixel bounds");
                    restored.ResetCrop(); restored.ConfirmCrop();
                    UpgradeTests.Ensure(restored.AppliedCrop == null && RecordingRecovery.Load(master).Crop == null && File.Exists(master), "Reset did not persist the original frame or lost the master");
                });
            }
            finally { reopened?.Close(); dialog.Close(); owner.Close(); RecordingRecovery.Remove(master); }
        });
    }
}
