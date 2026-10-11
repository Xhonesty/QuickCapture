using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal static class InstallationTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string root = Path.Combine(Paths.TestRoot!, "InstallationChecks");
        string user = Path.Combine(root, "User");
        string pictures = Path.Combine(user, "Pictures");
        string app = Path.Combine(root, "Programs", "QuickCapture");
        void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        await check("Installed storage remains stable across application upgrades", () =>
        {
            var first = Paths.ResolveStorage(app, true, user, pictures);
            var next = Paths.ResolveStorage(app + "-new", true, user, pictures);
            Ensure(first == next && first.Data == Path.Combine(user, "QuickCapture", "Data"), "User data depends on the install directory");
            Ensure(first.Output == Path.Combine(pictures, "QuickCapture"), "Installed captures are not in Pictures");
            var fallback = Paths.ResolveStorage(app, true, user, "");
            Ensure(fallback.Output.StartsWith(user, StringComparison.OrdinalIgnoreCase), "Missing Pictures has no writable fallback");
            return Task.CompletedTask;
        });
        await check("Portable layout and isolated diagnostics remain compatible", () =>
        {
            var portable = Paths.ResolveStorage(app, false, user, pictures);
            Ensure(portable == (Path.Combine(app, "Data"), Path.Combine(app, "Captures")), "Portable layout changed");
            Ensure(Paths.ResolveStorage(app, true, user, pictures, root) == Paths.ResolveStorage(app, false, user, pictures, root), "Tests write to real user directories");
            return Task.CompletedTask;
        });
        await check("Existing settings and screenshot project survive restart", async () =>
        {
            string? previous = Paths.TestRoot;
            Paths.TestRoot = root;
            try
            {
                var settings = new Settings { OutputDirectory = Path.Combine(root, "UserCaptures"), Theme = "Light", ScreenshotQuality = 83, WebMFps = 60, GifFps = 20 };
                settings.Save();
                string image = Paths.NewCapture(settings.OutputDirectory, "png");
                var bitmap = BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8);
                bitmap.Freeze();
                await ScreenshotProjects.SaveAsync(new(bitmap, Array.Empty<Annotation>(), new Int32Rect(0, 0, 2, 2)), bitmap, image, ScreenshotFormat.Png, 83, true);
                var restored = Settings.Load();
                var project = await ScreenshotProjects.LoadAsync(image);
                Ensure(restored.Theme == "Light" && restored.ScreenshotQuality == 83 && restored.WebMFps == 60 && restored.OutputDirectory == settings.OutputDirectory, "Settings not retained");
                Ensure(project.Original.PixelWidth == 2 && project.ExportPath == image, "Project association not retained");
            }
            finally { Paths.TestRoot = previous; }
        });
        await check("Installed bundle finds FFmpeg and local OCR models", async () =>
        {
            var tools = await MediaTools.DetectAsync(new Settings(), includeSystem: false, includeBundled: true);
            Ensure(tools.CanEdit, "Bundled media tools not found");
            foreach (string model in new[] { "eng", "chi_sim", "chi_tra" })
                Ensure(File.Exists(Path.Combine(OcrService.ModelDirectory, model + ".traineddata")), "Missing bundled OCR model");
        });
        await check("Recording masters and imported edit state remain restorable", async () =>
        {
            string? previous = Paths.TestRoot;
            Paths.TestRoot = root;
            try
            {
                string master = RecordingRecovery.NewMaster();
                File.WriteAllBytes(master, new byte[64]);
                RecordingRecovery.Remember(master, new(ExportQuality.High, 60, true, 8));
                var edit = new RecordingEditState(0, 8, new[] { new VideoRange(2, 3) }, 1.5);
                await RecordingEditStore.SaveAsync(master, edit);
                var metadata = RecordingRecovery.Load(master);
                var restored = await RecordingEditStore.LoadAsync(master);
                Ensure(metadata.Fps == 60 && metadata.HasAudio && restored?.Deleted.Length == 1 && restored.Speed == 1.5, "Master metadata not restored");
                string imported = Path.Combine(root, "imported.mp4");
                File.WriteAllBytes(imported, new byte[64]);
                var modified = File.GetLastWriteTimeUtc(imported);
                await RecordingEditStore.SaveAsync(imported, edit);
                restored = await RecordingEditStore.LoadAsync(imported);
                Ensure(restored?.Deleted[0].Start == 2 && File.GetLastWriteTimeUtc(imported) == modified && new FileInfo(imported).Length == 64, "Imported source changed or edit not restored");
            }
            finally { Paths.TestRoot = previous; }
        });
        await check("Actual application renders with isolated installed settings", async () =>
        {
            var main = new MainWindow();
            try
            {
                main.Show(); await Task.Delay(250);
                ThemeService.Apply("Light"); await Task.Delay(100);
                UiChangeTests.Render(main, Paths.IsInstalled ? "installer-app-light.png" : "installer-portable-app-light.png");
                ThemeService.Apply("Dark"); await Task.Delay(100);
                UiChangeTests.Render(main, Paths.IsInstalled ? "installer-app-dark.png" : "installer-portable-app-dark.png");
            }
            finally { main.Close(); }
        });
        var layout = Paths.ResolveStorage(AppContext.BaseDirectory, Paths.IsInstalled,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        File.WriteAllText(Path.Combine(Paths.TestRoot!, "installation-layout.json"), JsonSerializer.Serialize(new
        {
            installed = Paths.IsInstalled,
            version = typeof(App).Assembly.GetName().Version!.ToString(),
            actualLayout = new { data = layout.Data, output = layout.Output },
            userDirectoriesWritten = false
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }
}
