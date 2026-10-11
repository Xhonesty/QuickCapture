using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace QuickCapture;

internal static class ArchitectureTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "Architecture"); Directory.CreateDirectory(directory);
        await check("Production assembly excludes tests and demos; shared resources and shell load", async () =>
        {
            Ensure(!typeof(App).Assembly.GetTypes().Any(t => t.Name.EndsWith("Tests", StringComparison.Ordinal) || t.Name is "SelfTest" or "AcceptanceArtifacts" or "MosaicCandidatePreview"), "Diagnostic code is still in the production assembly");
            var main = new MainWindow();
            try { main.Show(); await Task.Delay(150); main.UpdateLayout(); Ensure(main.ActualWidth > 0 && Application.Current.FindResource("ColorDisplayButton") != null, "Shared theme or shell missing"); UiChangeTests.Render(main, "architecture-shell.png"); }
            finally { main.Close(); }
        });
        var image = UpgradeTests.TestPixels(160, 90);
        await check("PNG JPG WebP BMP output remains usable after module separation", () =>
        {
            foreach (var format in Enum.GetValues<ScreenshotFormat>())
            {
                string path = Path.Combine(directory, "image." + ImageExportService.Extension(format));
                ImageExportService.Save(image, path, format, 90);
                var restored = CaptureService.Load(path);
                Ensure(restored.PixelWidth == 160 && restored.PixelHeight == 90, "Image output dimensions changed: " + format);
            }
            return Task.CompletedTask;
        });
        var text = new VideoObject("text", VideoObjectKind.Text, 2, 2, 140, 65, 0, 1.4, 1,
            Text: "共享文字\nShared text", FontSize: 16, Bold: true, Italic: true, OutlineEnabled: true, OutlineWidth: 1,
            TextFrame: new TextFrameStyle(true, Width: 1, Fill: "#40808080"));
        var info = new VideoInfo(1.4, 160, 90, 30, true);
        await check("Shared text geometry and screenshot/video project state survive save and reopen", async () =>
        {
            text = text.FitText(info);
            Ensure(Math.Abs(AnnotationGeometry.TextContentHeight(text.TextAnnotation()) - VideoTextLayout.RequiredHeight(text)) < .01, "Screenshot and video text layouts diverged");
            string shot = Path.Combine(directory, "project.png");
            await ScreenshotProjects.SaveAsync(new(image, new[] { text.TextAnnotation() }, new Int32Rect(0, 0, 160, 90)), image, shot, ScreenshotFormat.Png, 90, true);
            var restored = await ScreenshotProjects.LoadAsync(shot);
            Ensure(restored.Annotations.Length == 1 && restored.Annotations[0].Italic && restored.Annotations[0].TextFrame == text.TextFrame, "Screenshot styles were lost");
            string video = Path.Combine(directory, "state.mp4"); File.WriteAllBytes(video, new byte[64]);
            var state = new RecordingEditState(0, 1.4, Array.Empty<VideoRange>(), Objects: new[] { text });
            await RecordingEditStore.SaveAsync(video, state);
            var loaded = await RecordingEditStore.LoadAsync(video);
            Ensure(loaded?.Objects is { Length: 1 } && loaded.Objects[0] == text, "Video object state was lost");
        });
        var tools = await MediaTools.DetectAsync(new Settings { MediaToolsPath = Path.Combine(AppContext.BaseDirectory, "Tools", "media") }, includeSystem: false, includeBundled: false);
        string source = Path.Combine(directory, "fixture.mp4");
        await check("Bundled shared media libraries probe H264 AAC without system FFmpeg", async () =>
        {
            Ensure(tools.CanEdit && tools.Supports(RecordingFormat.WebM) && tools.Supports(RecordingFormat.Gif), "Bundled shared tools lack required capabilities");
            var generated = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=1.4", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1.4", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, CancellationToken.None);
            Ensure(generated.ExitCode == 0, generated.Error);
            info = await VideoExportService.ProbeAsync(source, tools);
            Ensure(info.VideoCodec == "h264" && info.AudioCodec == "aac" && info.HasAudio, "Probe changed source metadata");
        });
        await check("Shared FFmpeg exports composited text crop speed audio to MP4 WebM GIF", async () =>
        {
            foreach (var format in Enum.GetValues<RecordingFormat>())
            {
                string target = Path.Combine(directory, "composed." + VideoExportService.Extension(format));
                var request = new VideoExportRequest(source, target, format, ExportQuality.Low, .2, 1.2, 1.5,
                    GifFps: 10, Overwrite: true, Crop: new(0, 0, 128, 64), Objects: new[] { text });
                await VideoExportService.ExportAsync(request, info, tools);
                var output = await VideoExportService.ProbeAsync(target, tools);
                Ensure(output.Width == 128 && output.Height == 64 && Math.Abs(output.Duration - request.OutputDuration) < .16, "Export timing or crop changed: " + format);
                Ensure(output.HasAudio == (format != RecordingFormat.Gif), "Export audio changed: " + format);
                var decoded = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-i", target, "-f", "null", "-" }, CancellationToken.None);
                Ensure(decoded.ExitCode == 0, "Export cannot decode: " + decoded.Error);
            }
        });
    }
    private static void Ensure(bool condition, string message) => UpgradeTests.Ensure(condition, message);
}
