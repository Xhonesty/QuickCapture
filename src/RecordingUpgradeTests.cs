using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenRecorderLib;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace QuickCapture;

internal static class RecordingUpgradeTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Recording controls fit ordinary / edge regions at high DPI and negative monitor origins", () =>
        {
            var bar = new RecordingBar("Ctrl+Alt+R", () => { }, () => { });
            try
            {
                var stop = UpgradeTests.Find<System.Windows.Controls.Button>((DependencyObject)bar.Content, button => button.Name == "RecordingStop");
                var pause = UpgradeTests.Find<System.Windows.Controls.Button>((DependencyObject)bar.Content, button => button.Name == "RecordingPause");
                UpgradeTests.Ensure(!stop.IsEnabled && !pause.IsEnabled, "Controls allow stopping before the first recorded frame");
                bar.SetRecording(); UpgradeTests.Ensure(stop.IsEnabled && pause.IsEnabled, "Recording controls remain disabled after startup");
                bar.SetPaused(); UpgradeTests.Ensure(stop.IsEnabled && pause.IsEnabled, "A paused recording cannot be stopped");
                bar.SetSaving(); bar.SetRecording();
                UpgradeTests.Ensure(!stop.IsEnabled && !pause.IsEnabled, "A late recording notification re-enabled finishing controls");
            }
            finally { bar.Close(); }
            foreach (double scale in new[] { 1d, 1.5, 2d })
            {
                var available = new Drawing.Rectangle(-2560, -300, 2560, 1440);
                var size = new Drawing.Size((int)(332 * scale), (int)(60 * scale));
                foreach (var selection in new[]
                {
                    new Drawing.Rectangle(-2000, 100, 800, 480),
                    new Drawing.Rectangle(-2555, -295, 24, 24),
                    new Drawing.Rectangle(-800, 1000, 795, 135),
                    new Drawing.Rectangle(-2555, 700, 520, 400),
                    new Drawing.Rectangle(-2555, -295, 2550, 1430)
                })
                {
                    var placed = RecordingBar.PlacementBounds(selection, size, available, scale);
                    UpgradeTests.Ensure(available.Contains(placed), $"Controls leave monitor at {scale}x: {placed}");
                    if (selection.Width < available.Width - size.Width && selection.Height < available.Height - size.Height)
                        UpgradeTests.Ensure(!placed.IntersectsWith(selection), "Controls cover an available outside position");
                }
            }
            return Task.CompletedTask;
        });

        await check("Native recording pause / resume excludes pause duration, mixed audio and overlaid controls; paused stop is idempotent", async () =>
        {
            string directory = Path.Combine(Paths.TestRoot!, "RecordingUpgrade"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "pause-resume.mp4");
            if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".partial.mp4")) File.Delete(path + ".partial.mp4");
            var tools = await MediaTools.DetectAsync(new Settings());
            UpgradeTests.Ensure(tools.HasVideoTools, "FFmpeg / FFprobe are required for the real pause verification");
            var monitor = Forms.Screen.PrimaryScreen!;
            var scene = new Window
            {
                Title = "QuickCapture pause verification", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, Width = 576, Height = 320, Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(212, 42, 62))
            };
            using var recorder = new RecorderService(); RecordingBar? bar = null;
            var states = new List<RecordingState>(); int starts = 0;
            recorder.Started += () => Interlocked.Increment(ref starts);
            recorder.StateChanged += state => { lock (states) states.Add(state); scene.Dispatcher.BeginInvoke(() => bar?.SetState(state)); };
            try
            {
                scene.Show(); Native.Place(scene, new(monitor.WorkingArea.X + 80, monitor.WorkingArea.Y + 80, 576, 320)); await Task.Delay(200);
                var handle = new WindowInteropHelper(scene).Handle;
                UpgradeTests.Ensure(Native.GetWindowRect(handle, out var sceneBounds), "Cannot determine recording test bounds");
                var region = new SavedRegion(sceneBounds.Left + 16, sceneBounds.Top + 16, 480, 240, monitor.DeviceName);
                bar = new RecordingBar("Ctrl+Alt+R", () => { }, () => recorder.TogglePause(), () => recorder.Elapsed, () => region.Rectangle, true);
                bar.Show(); await Task.Delay(80);
                // Intentionally overlap the crop. Affinity must remove the bar
                // from native capture instead of relying on outside placement.
                if (bar.CaptureExcluded) Native.Place(bar, new(region.X + 30, region.Y + 30, 332, 60), false);
                else { bar.Hide(); }
                var options = new Settings { HardwareEncoding = false, SystemAudio = true, Microphone = true, Cursor = false, FramesPerSecond = 30 };
                await recorder.StartAsync(RecorderService.RegionSource(region), options, path).WaitAsync(TimeSpan.FromSeconds(20));
                await Task.Delay(1000);
                UpgradeTests.Ensure(recorder.Pause() && recorder.IsPaused && !recorder.Pause(), "Pause or duplicate pause state is incorrect");
                var pausedAt = recorder.Elapsed; scene.Background = new SolidColorBrush(Color.FromRgb(36, 219, 57));
                await Task.Delay(1100);
                UpgradeTests.Ensure(Math.Abs((recorder.Elapsed - pausedAt).TotalMilliseconds) < 5, "Paused recording duration keeps increasing");
                scene.Background = new SolidColorBrush(Color.FromRgb(35, 81, 220)); await Task.Delay(120);
                UpgradeTests.Ensure(recorder.Resume() && !recorder.IsPaused && !recorder.Resume(), "Resume or duplicate resume state is incorrect");
                await Task.Delay(1100);
                UpgradeTests.Ensure(recorder.Pause(), "Cannot pause before immediate stop");
                Task<string> firstStop = recorder.StopAsync(), secondStop = recorder.StopAsync();
                UpgradeTests.Ensure(ReferenceEquals(firstStop, secondStop) && !recorder.Resume() && !recorder.Pause(), "Stop / pause race forwards duplicate native operations");
                await firstStop.WaitAsync(TimeSpan.FromSeconds(30));
                UpgradeTests.Ensure(starts == 1 && recorder.State == RecordingState.Completed, "Resume raised a second start or final state is incorrect");
                UpgradeTests.Ensure(File.Exists(path) && new FileInfo(path).Length > 4096 && !File.Exists(path + ".partial.mp4"), "Paused stop did not finalize the MP4");
                var info = await VideoExportService.ProbeAsync(path, tools);
                UpgradeTests.Ensure(info.HasAudio && Math.Abs(info.Duration - recorder.Elapsed.TotalSeconds) < 0.4 && info.Duration < 2.7,
                    $"Video retained paused time or audio was lost: duration={info.Duration:F3}, active={recorder.Elapsed.TotalSeconds:F3}");
                var probe = await MediaTools.RunAsync(tools.Ffprobe!, new[] { "-v", "error", "-show_entries", "stream=codec_type,duration,start_time", "-of", "json", path }, CancellationToken.None);
                UpgradeTests.Ensure(probe.ExitCode == 0, probe.Error);
                using var json = JsonDocument.Parse(probe.Output); double videoDuration = 0, audioDuration = 0;
                foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
                {
                    if (!stream.TryGetProperty("duration", out var duration) || !double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out double seconds)) continue;
                    if (stream.GetProperty("codec_type").GetString() == "video") videoDuration = seconds;
                    if (stream.GetProperty("codec_type").GetString() == "audio") audioDuration = seconds;
                }
                UpgradeTests.Ensure(videoDuration > 1.5 && audioDuration > 1.5 && Math.Abs(videoDuration - audioDuration) < 0.16,
                    $"Paused capture audio / video clocks diverge: video={videoDuration:F3}, audio={audioDuration:F3}");
                var decoded = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-i", path, "-f", "null", "-" }, CancellationToken.None);
                UpgradeTests.Ensure(decoded.ExitCode == 0 && decoded.Error.Trim().Length == 0, "Paused recording cannot fully decode: " + decoded.Error);
                string pixelsPath = Path.Combine(directory, "pause-sample.rgb");
                var pixels = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-i", path, "-vf", "crop=2:2:60:50,fps=8,format=rgb24", "-c:v", "rawvideo", "-f", "rawvideo", pixelsPath }, CancellationToken.None);
                UpgradeTests.Ensure(pixels.ExitCode == 0, pixels.Error); byte[] bytes = File.ReadAllBytes(pixelsPath);
                int red = 0, blue = 0, green = 0, other = 0;
                for (int i = 0; i + 11 < bytes.Length; i += 12)
                {
                    if (bytes[i] > 150 && bytes[i + 1] < 100 && bytes[i + 2] < 100) red++;
                    else if (bytes[i] < 100 && bytes[i + 1] < 130 && bytes[i + 2] > 150) blue++;
                    else if (bytes[i] < 100 && bytes[i + 1] > 150 && bytes[i + 2] < 100) green++;
                    else other++;
                }
                UpgradeTests.Ensure(red >= 3 && blue >= 3 && green == 0 && other <= 1,
                    $"Output includes pause pixels, controls or black frames: red={red}, blue={blue}, paused-green={green}, other={other}");
                File.WriteAllText(Path.Combine(directory, "pause-verification.json"), JsonSerializer.Serialize(new
                {
                    activeSeconds = recorder.Elapsed.TotalSeconds, info.Duration, videoDuration, audioDuration, states = states.Select(s => s.ToString()).ToArray(),
                    bar.CaptureExcluded, fallback = !bar.CaptureExcluded, red, blue, green, other
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { recorder.Dispose(); bar?.Close(); scene.Close(); }
        });
    }
}
