using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenRecorderLib;
using Forms = System.Windows.Forms;

namespace QuickCapture;

internal static class SelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Diagnostics"); Directory.CreateDirectory(directory);
        Paths.TestRoot = directory;
        if (File.Exists(Paths.SettingsFile)) File.Delete(Paths.SettingsFile);
        var results = new List<object>(); int failures = 0;
        HashSet<string>? passedNames = null;
        string? recorderRegression = Array.IndexOf(args, "--recorder-regression") >= 0 ? "Window MP4 recording and asynchronous finalization" : null;
        var recheckNames = args.Where(arg => arg.StartsWith("--recheck=", StringComparison.Ordinal)).Select(arg => arg[10..]).ToHashSet();
        if (recorderRegression != null) recheckNames.Add(recorderRegression);
        if (Array.IndexOf(args, "--failed-only") >= 0)
        {
            string previousPath = Path.Combine(directory, "results.json");
            using var previous = JsonDocument.Parse(File.ReadAllText(previousPath));
            passedNames = previous.RootElement.EnumerateArray().Where(item => item.GetProperty("passed").GetBoolean()).Select(item => item.GetProperty("name").GetString()!).ToHashSet();
            passedNames.ExceptWith(recheckNames);
            foreach (var item in previous.RootElement.EnumerateArray().Where(item => item.GetProperty("passed").GetBoolean() && !recheckNames.Contains(item.GetProperty("name").GetString()!))) results.Add(item.Clone());
        }
        async Task Check(string name, Func<Task> action)
        {
            if (passedNames?.Contains(name) == true) return;
            try { await action(); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.ToString() }); }
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        Directory.CreateDirectory(UiChangeTests.PreviewDirectory);
        ThemeService.Apply(ThemeService.Current);
        if (Array.IndexOf(args, "--mosaic-preview") >= 0) { MosaicCandidatePreview.Render(); return 0; }
        if (Array.IndexOf(args, "--mosaic-refined-preview") >= 0) { MosaicCandidatePreview.Render(refined: true); return 0; }
        if (Array.IndexOf(args, "--mosaic-icon-only") >= 0)
        {
            await MosaicIconTests.RunAsync(Check);
            System.Windows.Input.Mouse.Capture(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            return failures == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--settings-tabs-only") >= 0)
        {
            await SettingsCategoryTests.RunAsync(Check);
            await RecordingPreferencesTests.RunAsync(Check, validateExports: false);
            await PanelOptimizationTests.RunAsync(Check);
            await RecentFilesTests.RunAsync(Check);
            System.Windows.Input.Mouse.Capture(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            return failures == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--screenshot-projects-only") >= 0 || Array.IndexOf(args, "--screenshot-restart-only") >= 0) { await ScreenshotProjectTests.RunAsync(Check, Array.IndexOf(args, "--screenshot-restart-only") >= 0); return failures == 0 ? 0 : 1; }
        if (Array.IndexOf(args, "--video-cuts-only") >= 0) { await VideoCutTests.RunAsync(Check); return failures == 0 ? 0 : 1; }
        if (Array.IndexOf(args, "--audio-devices-only") >= 0) { await AudioDeviceTests.RunAsync(Check); return failures == 0 ? 0 : 1; }
        if (Array.IndexOf(args, "--recording-edit-only") >= 0)
        {
            await RecordingReeditTests.RunAsync(Check);
            await RecentFilesTests.RunAsync(Check);
            System.Windows.Input.Mouse.Capture(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            return failures == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--context-toolbar-only") >= 0)
        {
            await ToolbarPreviewTests.RunAsync(Check);
            await ContextToolbarTests.RunAsync(Check);
            System.Windows.Input.Mouse.Capture(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            return failures == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--editor-ui-only") >= 0)
        {
            await RecordingEditorThemeTests.RunAsync(Check, Array.IndexOf(args, "--pointer") >= 0);
            System.Windows.Input.Mouse.Capture(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            return failures == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--toolbar-only") >= 0)
        {
            await ToolbarPreviewTests.RunAsync(Check);
            await InPlaceTextTests.RunAsync(Check);
            await UpgradeTests.RunAsync(Check);
            await RecentFilesTests.RunAsync(Check);
            System.Windows.Input.Mouse.Capture(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            return failures == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--office-only") >= 0)
        {
            await OfficeToolsTests.RunAsync(Check, Array.IndexOf(args, "--pointer") >= 0);
            // Synthetic menu clicks may retain native mouse capture until the
            // dispatcher processes closing popups. Release it while the WPF
            // input worker is still alive, before application shutdown.
            System.Windows.Input.Mouse.Capture(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            return failures == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--preferences-only") >= 0)
        {
            await RecordingPreferencesTests.RunAsync(Check);
            await RecentFilesTests.RunAsync(Check);
            await PanelOptimizationTests.RunAsync(Check);
            await Check("Actual optimized panel screenshots", () => AcceptanceArtifacts.RunDemoAsync(panelOnly: true));
            return failures == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--panel-only") >= 0)
        {
            await RecentFilesTests.RunAsync(Check);
            await PanelOptimizationTests.RunAsync(Check);
            await Check("Actual optimized panel screenshots", () => AcceptanceArtifacts.RunDemoAsync(panelOnly: true));
            return failures == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--text-only") >= 0) { await InPlaceTextTests.RunAsync(Check); return failures == 0 ? 0 : 1; }
        if (Array.IndexOf(args, "--interface-only") >= 0)
        {
            await InPlaceTextTests.RunAsync(Check);
            await RecentFilesTests.RunAsync(Check);
            await Check("Actual interface screenshots", () => AcceptanceArtifacts.RunDemoAsync());
            return failures == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--annotation-only") >= 0) { await AnnotationUpgradeTests.RunAsync(Check); return failures == 0 ? 0 : 1; }
        if (Array.IndexOf(args, "--recording-only") >= 0) { await RecordingUpgradeTests.RunAsync(Check); return failures == 0 ? 0 : 1; }
        if (Array.IndexOf(args, "--crop-only") >= 0) { await VideoCropTests.RunAsync(Check); return failures == 0 ? 0 : 1; }
        if (Array.IndexOf(args, "--shell-only") >= 0) { await ShellUpgradeTests.RunAsync(Check); return failures == 0 ? 0 : 1; }
        if (Array.IndexOf(args, "--upgrade-only") >= 0 || Array.IndexOf(args, "--media-only") >= 0)
        {
            if (Array.IndexOf(args, "--upgrade-only") >= 0) await UpgradeTests.RunAsync(Check);
            else await MediaTests.RunAsync(Check);
            return failures == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--ui-only") >= 0)
        {
            await UiChangeTests.RunAsync(Check);
            await UpgradeTests.RunAsync(Check);
            File.WriteAllText(Path.Combine(directory, "summary.txt"), $"UI failures: {failures}\nCompleted: {DateTime.Now:O}\n");
            return failures == 0 ? 0 : 1;
        }
        await Check("Hotkey parsing and conflict rollback", () =>
        {
            Ensure(HotkeyService.Parse("Ctrl+Alt+S") == (0x4003u, 0x53u), "Wrong hotkey mapping");
            bool rejected = false; try { HotkeyService.Parse("S"); } catch (ArgumentException) { rejected = true; }
            Ensure(rejected, "Unmodified hotkey accepted");
            var w1 = new Window(); var w2 = new Window();
            try
            {
                using var first = new HotkeyService(new System.Windows.Interop.WindowInteropHelper(w1).EnsureHandle());
                using var second = new HotkeyService(new System.Windows.Interop.WindowInteropHelper(w2).EnsureHandle());
                first.Configure("Ctrl+Alt+F10", "Ctrl+Alt+F11"); second.Configure("Ctrl+Shift+F10", "Ctrl+Shift+F11");
                bool conflict = false; try { second.Configure("Ctrl+Alt+F10", "Ctrl+Shift+F12"); } catch (InvalidOperationException) { conflict = true; }
                Ensure(conflict, "Occupied hotkey accepted");
                bool rollback = false; try { first.Configure("Ctrl+Shift+F10", "Ctrl+Alt+F12"); } catch (InvalidOperationException) { rollback = true; }
                Ensure(rollback, "Previous hotkey registration was lost");
            }
            finally { w1.Close(); w2.Close(); }
            return Task.CompletedTask;
        });
        await Check("Annotations, undo/redo and pixel-preserving PNG export", () =>
        {
            var pixels = new byte[640 * 400 * 4];
            for (int y = 0; y < 400; y++) for (int x = 0; x < 640; x++)
            { int p = (y * 640 + x) * 4; pixels[p] = (byte)(x % 256); pixels[p + 1] = (byte)(y % 256); pixels[p + 2] = (byte)((x + y) % 256); pixels[p + 3] = 255; }
            var source = BitmapSource.Create(640, 400, 144, 144, PixelFormats.Bgra32, null, pixels, 640 * 4);
            var editor = new AnnotationSurface(source);
            editor.Add(new(AnnotationTool.Arrow, new(30, 30), new(180, 110), Colors.Red));
            editor.Add(new(AnnotationTool.Rectangle, new(210, 40), new(420, 150), Colors.Lime));
            editor.Add(new(AnnotationTool.Text, new(30, 200), new(30, 200), Colors.White, "轻截 · PNG 640 × 400"));
            editor.Add(new(AnnotationTool.Mosaic, new(450, 180), new(630, 390), Colors.White));
            Ensure(editor.Count == 4, "Missing annotations"); editor.Undo(); Ensure(editor.Count == 3, "Undo failed"); editor.Redo(); Ensure(editor.Count == 4, "Redo failed");
            string path = Path.Combine(directory, "annotations.png"); if (File.Exists(path)) File.Delete(path);
            var output = editor.Export(); CaptureService.Save(output, path);
            var loaded = CaptureService.Load(path); Ensure(loaded.PixelWidth == 640 && loaded.PixelHeight == 400, "Export changed physical dimensions");
            var outPixels = new byte[pixels.Length]; output.CopyPixels(outPixels, 640 * 4, 0);
            int sample = (185 * 640 + 455) * 4; Ensure(outPixels[sample] != pixels[sample], "Mosaic did not replace original pixels");
            int untouched = (395 * 640 + 5) * 4; Ensure(outPixels[untouched] == pixels[untouched], "Original image was rescaled during export");
            return Task.CompletedTask;
        });
        await Check("Negative-origin monitor crop and stale-region validation", () =>
        {
            var screen = Forms.Screen.PrimaryScreen!;
            var region = new SavedRegion(screen.Bounds.X + 10, screen.Bounds.Y + 20, 320, 240, screen.DeviceName);
            var source = (DisplayRecordingSource)RecorderService.RegionSource(region);
            Ensure(source.SourceRect.Left == 10 && source.SourceRect.Top == 20, "Crop is not relative to monitor origin");
            var negative = RecorderService.RelativeCrop(new(-1900, -100, 400, 300, "test"), new(-1920, -120, 1920, 1080));
            Ensure(negative.Left == 20 && negative.Top == 20, "Negative monitor origin incorrectly handled");
            bool rejected = false; try { RecorderService.RegionSource(region with { DeviceName = "removed-monitor" }); } catch (InvalidOperationException) { rejected = true; }
            Ensure(rejected, "Stale monitor region accepted"); return Task.CompletedTask;
        });
        var scene = new Window { Title = "QuickCapture verification scene", Width = 640, Height = 400, Left = 60, Top = 60, Topmost = true, Background = new SolidColorBrush(Color.FromRgb(22, 33, 50)), WindowStyle = WindowStyle.None };
        var canvas = new Canvas(); scene.Content = canvas;
        canvas.Children.Add(new TextBlock { Text = "QUICKCAPTURE\nNative capture verification", FontSize = 30, Foreground = Brushes.White, Margin = new Thickness(30) });
        var box = new System.Windows.Shapes.Rectangle { Width = 80, Height = 80, Fill = new SolidColorBrush(Color.FromRgb(130, 226, 187)) }; canvas.Children.Add(box); Canvas.SetTop(box, 180);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) }; int frame = 0;
        timer.Tick += (_, _) => Canvas.SetLeft(box, 30 + (frame++ * 3 % 480));
        scene.Show(); timer.Start(); await Task.Delay(350);
        await Check("Desktop screenshot acquisition", async () =>
        {
            var rect = Forms.Screen.PrimaryScreen!.Bounds;
            var image = await CaptureService.CaptureAsync(new(rect.X + 30, rect.Y + 30, 640, 400));
            string path = Path.Combine(directory, "desktop.png"); if (File.Exists(path)) File.Delete(path); CaptureService.Save(image, path);
            Ensure(image.PixelWidth == 640 && image.PixelHeight == 400, "Screenshot dimensions incorrect");
            var bytes = UpgradeTests.Bytes(image); int center = ((image.PixelHeight / 2) * image.PixelWidth + image.PixelWidth / 2) * 4;
            Ensure(bytes[center] == 50 && bytes[center + 1] == 33 && bytes[center + 2] == 22, "Desktop screenshot missed the visible verification scene");
        });
        await Check("Selection overlay physical coordinates at current monitor DPI", async () =>
        {
            var screen = Forms.Screen.PrimaryScreen!;
            var image = await CaptureService.CaptureAsync(screen.Bounds);
            var selection = new SelectionWindow(screen, image, false, _ => { });
            try
            {
                selection.Show(); await Task.Delay(180);
                var dpi = VisualTreeHelper.GetDpi(selection);
                var region = selection.RegionFromPoints(new(10, 20), new(210, 120));
                Ensure(Math.Abs(region.Width - Math.Round(200 * dpi.DpiScaleX)) <= 1, "Horizontal DPI conversion mismatch");
                Ensure(Math.Abs(region.Height - Math.Round(100 * dpi.DpiScaleY)) <= 1, "Vertical DPI conversion mismatch");
                Ensure(Math.Abs(region.X - screen.Bounds.X - Math.Round(10 * dpi.DpiScaleX)) <= 1, "Monitor origin mismatch");
                File.WriteAllText(Path.Combine(directory, "display.json"), JsonSerializer.Serialize(new { dpi = dpi.DpiScaleX, region, monitors = Forms.Screen.AllScreens.Length }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { selection.Close(); }
        });
        var hwnd = new System.Windows.Interop.WindowInteropHelper(scene).Handle;
        await Check("Windows Graphics Capture window screenshot", async () =>
        {
            var image = await RecorderService.CaptureWindowAsync(hwnd, Path.Combine(directory, "window-temp.png"));
            string path = Path.Combine(directory, "window.png"); if (File.Exists(path)) File.Delete(path); CaptureService.Save(image, path);
            Ensure(image.PixelWidth > 100 && image.PixelHeight > 100, "Window screenshot empty");
            var bytes = UpgradeTests.Bytes(image); int center = ((image.PixelHeight / 2) * image.PixelWidth + image.PixelWidth / 2) * 4;
            Ensure(bytes[center] == 50 && bytes[center + 1] == 33 && bytes[center + 2] == 22, "Window screenshot missed the source's first rendered frame");
        });
        async Task Record(string name, RecordingSourceBase source, bool audio, bool hardware = false, bool microphone = false)
        {
            string path = Path.Combine(directory, name + ".mp4"); if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".partial.mp4")) File.Delete(path + ".partial.mp4");
            using var recorder = new RecorderService();
            var options = new Settings { HardwareEncoding = hardware, SystemAudio = audio, Microphone = microphone };
            await recorder.StartAsync(source, options, path).WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(3200); await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Ensure(new FileInfo(path).Length > 4096, "Recording empty"); Ensure(!File.Exists(path + ".partial.mp4"), "Final file not committed");
        }
        await Check("Window MP4 recording and asynchronous finalization", () => Record("window-video", new WindowRecordingSource(hwnd), false));
        await Check("Region MP4 recording", () =>
        {
            var bounds = Forms.Screen.PrimaryScreen!.Bounds;
            return Record("region-video", RecorderService.RegionSource(new(bounds.X + 30, bounds.Y + 30, 640, 400, Forms.Screen.PrimaryScreen.DeviceName)), false);
        });
        await Check("Odd-sized region output and hardware encoder", () =>
        {
            var bounds = Forms.Screen.PrimaryScreen!.Bounds;
            return Record("odd-region-video", RecorderService.RegionSource(new(bounds.X + 30, bounds.Y + 30, 641, 401, Forms.Screen.PrimaryScreen.DeviceName)), false, true);
        });
        if (Array.IndexOf(args, "--audio") >= 0)
            await Check("System loopback audio MP4 recording", () => Record("system-audio-video", new WindowRecordingSource(hwnd), true));
        if (Array.IndexOf(args, "--microphone") >= 0)
            await Check("Microphone and system audio mixed MP4 recording", () => Record("mixed-audio-video", new WindowRecordingSource(hwnd), true, false, true));
        await Check("Region recording border, click-through frame and native crop pixels", () => FeatureTests.CheckRecordingFrameAsync(scene, directory));
        timer.Stop(); scene.Close();
        await Check("Main window layout rendering", async () =>
        {
            var main = new MainWindow(); main.Show(); await Task.Delay(250);
            RenderWindow(main, "main-window.png");
            var editor = new EditorWindow(CaptureService.Load(Path.Combine(directory, "annotations.png")), new Settings(), _ => { });
            editor.Show(); await Task.Delay(150); RenderWindow(editor, "editor-window.png"); editor.Close();
            using var hotkeys = new HotkeyService(new System.Windows.Interop.WindowInteropHelper(main).Handle);
            var settings = new SettingsWindow(main, new Settings(), hotkeys);
            settings.Show(); await Task.Delay(150); RenderWindow(settings, "settings-window.png"); settings.Close();
            var bar = new RecordingBar("Ctrl+Alt+R", () => { }); bar.Show(); bar.SetRecording(); await Task.Delay(150); RenderWindow(bar, "recording-bar.png"); bar.Close();
            main.Hide();
            void RenderWindow(Window window, string filename)
            {
                UiChangeTests.Render(window, filename);
            }
        });
        await UiChangeTests.RunAsync(Check);
        await UpgradeTests.RunAsync(Check);
        await MediaTests.RunAsync(Check);
        await AnnotationUpgradeTests.RunAsync(Check);
        await RecordingUpgradeTests.RunAsync(Check);
        await VideoCropTests.RunAsync(Check);
        await ShellUpgradeTests.RunAsync(Check);
        File.WriteAllText(Path.Combine(directory, "summary.txt"), $"Failures: {failures}\nCompleted: {DateTime.Now:O}\nSee results.json and rendered artifacts.\n");
        return failures == 0 ? 0 : 1;
    }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
