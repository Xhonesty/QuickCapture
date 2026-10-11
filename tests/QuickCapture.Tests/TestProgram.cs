using System;
using System.IO;
using System.Windows;

namespace QuickCapture;

internal static class TestProgram
{
    [STAThread]
    public static int Main()
    {
        // All pack URIs and theme resources still come from the production app.
        try
        {
            var app = new TestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/QuickCapture;component/AppResources.xaml", UriKind.Relative) });
            return app.Run();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "runner-startup-error.txt"), ex.ToString());
            return 1;
        }
    }
}

internal sealed class TestApp : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (Array.IndexOf(e.Args, "--ocr-worker") == 0) { Shutdown(OcrService.RunWorker(e.Args)); return; }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window window) window.Icon = Ui.AppIcon; }));
        try
        {
            if (Array.IndexOf(e.Args, "--editor-demo") >= 0) { await RecordingTimelineTests.ShowDemoAsync(e.Args); return; }
            if (Array.IndexOf(e.Args, "--text-demo") >= 0) { await AcceptanceArtifacts.RunDemoAsync(true); return; }
            if (Array.IndexOf(e.Args, "--objects-demo") >= 0)
            {
                Paths.TestRoot = Path.Combine(AppContext.BaseDirectory, "Diagnostics");
                using var fixtures = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Paths.TestRoot, "video-objects-fixtures.json")));
                string source = fixtures.RootElement.GetProperty("source").GetString()!;
                var owner = new Window { Width = 300, Height = 200 }; Ui.Theme(owner); owner.Show();
                var dialog = new RecordingEditWindow(owner, source, new Settings { OutputDirectory = Path.GetDirectoryName(source)! }, new(ExportQuality.Medium, 30, true, 3));
                dialog.Closed += (_, _) => { owner.Close(); Shutdown(); }; dialog.Show(); return;
            }
            if (Array.IndexOf(e.Args, "--flow-demo") >= 0)
            {
                Paths.TestRoot = Path.Combine(AppContext.BaseDirectory, "Diagnostics", "FlowDemo");
                if (!File.Exists(Paths.SettingsFile))
                {
                    var screen = System.Windows.Forms.Screen.PrimaryScreen!;
                    new Settings { RecordingStartMode = RecordingStartMode.Manual, LastRegion = new(screen.Bounds.X + 100, screen.Bounds.Y + 100, 640, 360, screen.DeviceName) }.Save();
                }
                new MainWindow().Show(); return;
            }
            Shutdown(await SelfTest.RunAsync(e.Args));
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); ErrorLog.Write(ex); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { OcrService.CancelActive(); base.OnExit(e); }
}
