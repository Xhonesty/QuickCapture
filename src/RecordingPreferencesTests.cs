using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickCapture;

internal static class RecordingPreferencesTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Settings exactly match default and resized main panels and retain only the Settings title", async () =>
        {
            await WithMain(async main =>
            {
                foreach (var size in new[] { (480d, 680d), (560d, 720d) })
                {
                    main.Width = size.Item1; main.Height = size.Item2; main.UpdateLayout();
                    var window = new SettingsWindow(main, Config(main), Keys(main));
                    try
                    {
                        window.Show(); await Task.Delay(60); window.UpdateLayout();
                        Ensure(window.ActualWidth == main.ActualWidth && window.ActualHeight == main.ActualHeight, "Window dimensions differ");
                        Ensure(window.Title == "设置" && Named<TextBlock>(window, "SettingsTitle").Text == "设置", "Settings title still includes the application name");
                        var title = Named<TextBlock>(window, "SettingsTitle");
                        Ensure(Visuals<Border>((DependencyObject)title.Parent).All(border => border.Width != 22), "Settings title retained its application icon");
                        var scroll = Visuals<ScrollViewer>(window).First(viewer => viewer.Content is StackPanel);
                        scroll.ScrollToBottom(); await Task.Delay(20); window.UpdateLayout();
                        var save = Named<Button>(window, "SaveSettings"); var origin = save.TranslatePoint(new Point(), window);
                        Ensure(origin.Y + save.ActualHeight <= window.ActualHeight, "Fixed footer is clipped");
                        Ensure(Visuals<TextBlock>(scroll).Any(text => text.Text.Contains("FFmpeg / ffprobe")), "Scrolling cannot reach the media hint");
                    }
                    finally { window.Close(); }
                }
            });
        });
        await check("Format-specific FPS previews cancel safely, save independently, synchronize the main selector and survive restart", async () =>
        {
            await WithMain(async main =>
            {
                var config = Config(main); var mainFps = (ComboBox)main.FindName("FpsBox");
                mainFps.SelectedIndex = 2;
                Ensure(Settings.Load().GetRecordingFps(RecordingFormat.Mp4) == 60, "Main FPS change was not saved");
                string before = File.ReadAllText(Paths.SettingsFile);
                var cancel = new SettingsWindow(main, config, Keys(main));
                await Dialog(cancel, () =>
                {
                    var format = Named<ComboBox>(cancel, "DefaultRecordingFormat"); var fps = Named<ComboBox>(cancel, "DefaultRecordingFps");
                    Ensure(Fps(fps) == 60, "Main MP4 rate did not synchronize to settings");
                    format.SelectedIndex = 2; Ensure(fps.Items.Count == 5 && Fps(fps) == 10, "GIF did not restore its own rate/options");
                    fps.SelectedIndex = 3; Ensure(mainFps.Items.Count == 5 && Fps(mainFps) == 20, "Settings GIF preview did not synchronize to the main panel");
                    format.SelectedIndex = 1; Ensure(fps.Items.Count == 3 && Fps(fps) == 15, "WebM inherited the GIF rate");
                    fps.SelectedIndex = 2; format.SelectedIndex = 0; Ensure(Fps(fps) == 60, "Switching formats lost the draft MP4 rate");
                    Ensure(File.ReadAllText(Paths.SettingsFile) == before, "Preview wrote settings before Save");
                    Named<Button>(cancel, "取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask;
                });
                Ensure(Fps(mainFps) == 60 && File.ReadAllText(Paths.SettingsFile) == before, "Cancel failed to restore FPS");
                var save = new SettingsWindow(main, config, Keys(main));
                var saved = await Dialog(save, () =>
                {
                    var format = Named<ComboBox>(save, "DefaultRecordingFormat"); var fps = Named<ComboBox>(save, "DefaultRecordingFps");
                    fps.SelectedIndex = 0; format.SelectedIndex = 1; fps.SelectedIndex = 2;
                    format.SelectedIndex = 2; fps.SelectedIndex = 0;
                    Named<Button>(save, "保存设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask;
                });
                var loaded = Settings.Load();
                Ensure(saved == true && loaded.RecordingFormat == RecordingFormat.Gif && loaded.Mp4Fps == 15 && loaded.WebMFps == 60 && loaded.GifFps == 5, "Save mixed per-format rates");
                Ensure(Fps(mainFps) == 5 && mainFps.Items.Count == 5, "Main selector failed to adopt the saved GIF rate");
                mainFps.SelectedIndex = 3; loaded = Settings.Load();
                Ensure(loaded.GifFps == 20 && loaded.Mp4Fps == 15 && loaded.WebMFps == 60, "Main GIF change overwrote other formats");
                main.Close();
                var restarted = new MainWindow(); restarted.Closing += (_, e) => e.Cancel = false;
                try { restarted.Show(); await Task.Delay(100); Ensure(Fps((ComboBox)restarted.FindName("FpsBox")) == 20, "Restart lost the synchronized GIF rate"); }
                finally { restarted.Close(); }
            });
        });
        await check("Legacy FPS settings migrate to MP4 and WebM while preserving the GIF rate", () =>
        {
            string? previous = Paths.TestRoot; Paths.TestRoot = Path.Combine(previous!, "FpsMigration-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Paths.Data);
                File.WriteAllText(Paths.SettingsFile, "{\"FramesPerSecond\":60,\"GifFps\":20,\"RecordingFormat\":1}");
                var config = Settings.Load();
                Ensure(config.Mp4Fps == 60 && config.WebMFps == 60 && config.GifFps == 20, "Legacy rates were discarded");
                config.RecordingFormat = RecordingFormat.Gif; config.Save(); config = Settings.Load();
                Ensure(config.FramesPerSecond == 20 && config.Mp4Fps == 60 && config.WebMFps == 60, "Saving an active GIF corrupted video rates");
            }
            finally { Paths.TestRoot = previous; }
            return Task.CompletedTask;
        });
        await check("MP4, WebM and GIF exports produce each selectable frame rate and retain audio/copy semantics", async () =>
        {
            var tools = await MediaTools.DetectAsync(new Settings());
            string directory = Path.Combine(Paths.TestRoot!, "FpsExports"); Directory.CreateDirectory(directory);
            string source = Path.Combine(directory, "source.mp4");
            var generated = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=128x72:rate=30:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, CancellationToken.None);
            Ensure(generated.ExitCode == 0, generated.Error); var info = await VideoExportService.ProbeAsync(source, tools);
            var native = new VideoExportRequest(source, "unused.mp4", RecordingFormat.Mp4, ExportQuality.Medium, 0, info.Duration, FramesPerSecond: 30);
            Ensure(native.OriginalMp4(info) && !(native with { FramesPerSecond = 60 }).OriginalMp4(info), "A changed FPS incorrectly copies the native master");
            foreach (var format in Enum.GetValues<RecordingFormat>())
            foreach (int fps in RecordingFrameRates.For(format))
            {
                string output = Path.Combine(directory, $"{format}-{fps}.{VideoExportService.Extension(format)}");
                var request = new VideoExportRequest(source, output, format, ExportQuality.Low, 0, info.Duration, FramesPerSecond: fps, Overwrite: true);
                await VideoExportService.ExportAsync(request, info, tools); var actual = await VideoExportService.ProbeAsync(output, tools);
                if (format == RecordingFormat.Gif)
                {
                    // GIF stores hundredths of a second. Inspect every frame's timestamp;
                    // ffprobe's avg_frame_rate can report the shortest frame delay instead.
                    var timing = await MediaTools.RunAsync(tools.Ffprobe!, new[] { "-v", "error", "-show_entries", "frame=pts_time", "-of", "json", output }, CancellationToken.None);
                    Ensure(timing.ExitCode == 0, timing.Error);
                    using var frames = JsonDocument.Parse(timing.Output);
                    var timestamps = frames.RootElement.GetProperty("frames").EnumerateArray().Select(frame => double.Parse(frame.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture)).ToArray();
                    Ensure(timestamps.Length == (int)Math.Round(info.Duration * fps), $"GIF {fps} FPS exported the wrong number of frames");
                    for (int index = 0; index < timestamps.Length; index++)
                        Ensure(Math.Abs(timestamps[index] - (double)index / fps) <= 0.0051, $"GIF {fps} FPS frame {index} has incorrect timing");
                    Ensure(Math.Abs(actual.Duration - info.Duration) <= 0.011, "GIF frame-delay rounding changed the playback duration");
                }
                else Ensure(Math.Abs(actual.Fps - fps) <= 0.01, $"{format} {fps} FPS exported as {actual.Fps}");
                Ensure(Math.Abs(actual.Duration - info.Duration) <= 0.25, "FPS conversion changed the selected duration");
                Ensure(actual.HasAudio == (format != RecordingFormat.Gif), "FPS conversion changed audio semantics");
                var decode = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-i", output, "-f", "null", "-" }, CancellationToken.None);
                Ensure(decode.ExitCode == 0, "FPS output could not be decoded");
            }
        });
    }
    private static async Task WithMain(Func<MainWindow, Task> action)
    {
        string? previousRoot = Paths.TestRoot; string previousTheme = ThemeService.Current;
        Paths.TestRoot = Path.Combine(previousRoot!, "Preferences-" + Guid.NewGuid().ToString("N"));
        new Settings { Theme = "Light", Mp4Fps = 30, WebMFps = 15, GifFps = 10, ScreenshotHotkey = "Ctrl+Alt+Shift+F8", RecordingHotkey = "Ctrl+Alt+Shift+F9" }.Save();
        var main = new MainWindow(); main.Closing += (_, e) => e.Cancel = false;
        try { main.Show(); await Task.Delay(100); await action(main); }
        finally { if (main.IsVisible) main.Close(); Paths.TestRoot = previousRoot; ThemeService.Apply(previousTheme); }
    }
    private static Settings Config(MainWindow main) => (Settings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
    private static HotkeyService Keys(MainWindow main) => (HotkeyService)typeof(MainWindow).GetField("_hotkeys", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
    private static int Fps(ComboBox box) => int.Parse(box.SelectedItem!.ToString()!.Split(' ')[0]);
    private static async Task<bool?> Dialog(SettingsWindow dialog, Func<Task> action)
    {
        var done = new TaskCompletionSource();
        dialog.Loaded += async (_, _) => { try { await Task.Delay(60); await action(); done.SetResult(); } catch (Exception ex) { done.SetException(ex); } finally { if (dialog.IsVisible) dialog.Close(); } };
        var result = dialog.ShowDialog(); await done.Task; return result;
    }
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Visuals<T>(root).First(item => item.Name == name || item is Button button && button.Content as string == name);
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var item in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
