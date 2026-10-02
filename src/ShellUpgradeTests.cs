using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal static class ShellUpgradeTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Compact main defaults keep core actions visible at current display DPI", async () =>
        {
            var main = new MainWindow();
            try
            {
                main.Show(); await Task.Delay(150); main.UpdateLayout();
                UpgradeTests.Ensure(Math.Abs(main.Width - 560) <= 1 && Math.Abs(main.Height - 640) <= 1, $"Default window differs from the requested 560x640: {main.Width}x{main.Height}");
                foreach (string name in new[] { "ScreenshotButton", "ShotMode", "RecordButton", "RecordMode", "RepeatButton", "ThemeToggle" })
                {
                    var control = (FrameworkElement)main.FindName(name); var origin = control.TranslatePoint(new Point(), main);
                    UpgradeTests.Ensure(control.IsVisible && control.ActualHeight >= 32 && origin.X >= 0 && origin.Y >= 0 && origin.X + control.ActualWidth <= main.ActualWidth && origin.Y + control.ActualHeight <= main.ActualHeight, name + " is clipped or too small");
                }
                UpgradeTests.Ensure(main.MinWidth <= main.Width && main.MinHeight <= main.Height, "Minimum window exceeds defaults");
            }
            finally { main.Hide(); }
        });
        await check("Live window previews track same-title selection, minimization and actual capture target", async () =>
        {
            var owner = new Window { Width = 440, Height = 300 }; Ui.Theme(owner); owner.Show();
            // Give DWM an explicitly painted client surface instead of relying on an empty Window's native background.
            var first = new Window { Title = "Same title", Width = 240, Height = 160, Left = 30, Top = 80, Topmost = true, WindowStyle = WindowStyle.None, Background = Brushes.Crimson, Content = new Border { Background = Brushes.Crimson } };
            var second = new Window { Title = "Same title", Width = 260, Height = 180, Left = 300, Top = 80, Topmost = true, WindowStyle = WindowStyle.None, Background = Brushes.LimeGreen, Content = new Border { Background = Brushes.LimeGreen } };
            first.Show(); second.Show();
            // Use the same explicit native placement as the recording fixture, so a hidden test-process
            // startup cannot leave a nominally visible source behind the desktop or another topmost window.
            var workArea = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            Native.Place(first, new(workArea.X + 40, workArea.Y + 80, 240, 160));
            Native.Place(second, new(workArea.X + 320, workArea.Y + 80, 260, 180));
            var one = new WindowInteropHelper(first).Handle; var two = new WindowInteropHelper(second).Handle;
            var picker = new WindowPicker(owner, new[] { new WindowItem(one, first.Title, "test-app", Environment.ProcessId), new WindowItem(two, second.Title, "test-app", Environment.ProcessId) });
            try
            {
                // Record the identical source before any picker HWND or thumbnail exists. Do not fail early:
                // before/after and a direct WPF render must all be available to distinguish fixture and picker failures.
                second.Activate(); second.UpdateLayout(); await Task.Delay(180);
                UiChangeTests.RenderElement((FrameworkElement)second.Content, "picker-target-wpf.png");
                var wpf = Describe(CaptureService.Load(Path.Combine(UiChangeTests.PreviewDirectory, "picker-target-wpf.png")), "picker-target-wpf.png");
                var baselineImage = await RecorderService.CaptureWindowAsync(two, Path.Combine(Paths.TestRoot!, "picker-baseline-temp.png"));
                var baseline = SaveDiagnostic(baselineImage, "picker-target-baseline.png");
                UpgradeTests.Ensure(Native.VisibleWindowBounds(two, out var baselineBounds), "Diagnostic target bounds unavailable before picker");
                var baselineDesktop = SaveDiagnostic(await CaptureService.CaptureAsync(baselineBounds), "picker-target-baseline-desktop.png");
                picker.Show(); picker.SelectForTest(0); await Task.Delay(450);
                UpgradeTests.Ensure(picker.PreviewAvailable && picker.PreviewTarget == one, "First live window preview missing");
                picker.SelectForTest(1); await Task.Delay(450);
                UpgradeTests.Ensure(picker.PreviewAvailable && picker.PreviewTarget == two && picker.Selected?.Handle == two, "Same-title preview retained previous target");
                // Verify the placeholder on a different target. The accepted capture target stays rendered throughout selection.
                first.WindowState = WindowState.Minimized; picker.SelectForTest(0); await Task.Delay(450);
                UpgradeTests.Ensure(!picker.PreviewAvailable && picker.PreviewTarget == one, "Minimized window kept a stale thumbnail");
                picker.SelectForTest(1); await Task.Delay(450);
                UpgradeTests.Ensure(picker.PreviewAvailable && picker.PreviewTarget == two && picker.Selected?.Handle == two, "Returning from a minimized target did not restore the selected live preview");
                var selected = picker.Selected!.Handle;
                UpgradeTests.Ensure(!Native.IsIconic(selected) && !Native.CaptureProtected(selected), "Picker accepted a minimized target or excluded the target itself from capture");
                UpgradeTests.Ensure(!Native.Windows().Any(w => w.Handle == one || w.Handle == two || w.Handle == new WindowInteropHelper(picker).Handle), "Own windows enter capture target list");
                // Match the production flow: finish selection, release previews/highlight, then capture the retained HWND.
                picker.Close(); second.Activate(); second.UpdateLayout(); await Task.Delay(180);
                var captured = await RecorderService.CaptureWindowAsync(selected, Path.Combine(Paths.TestRoot!, "picker-target-temp.png"));
                var after = SaveDiagnostic(captured, "picker-selected-window.png");
                UpgradeTests.Ensure(Native.VisibleWindowBounds(two, out var afterBounds), "Diagnostic target bounds unavailable after picker");
                var afterDesktop = SaveDiagnostic(await CaptureService.CaptureAsync(afterBounds), "picker-target-after-desktop.png");
                string diagnosticsPath = Path.Combine(Path.GetDirectoryName(UiChangeTests.PreviewDirectory)!, "window-picker-capture-diagnostics.json");
                File.WriteAllText(diagnosticsPath, JsonSerializer.Serialize(new
                {
                    target = new { hwnd = two.ToInt64().ToString("X"), second.Width, second.Height, second.ActualWidth, second.ActualHeight, second.IsActive, second.IsVisible, minimized = Native.IsIconic(two), captureProtected = Native.CaptureProtected(two), baselineBounds, afterBounds },
                    wpf, baseline, baselineDesktop, after, afterDesktop
                }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                var bytes = UpgradeTests.Bytes(captured); int p = ((captured.PixelHeight / 2) * captured.PixelWidth + captured.PixelWidth / 2) * 4;
                UpgradeTests.Ensure(bytes[p + 1] > 180 && bytes[p + 2] < 70, $"Final capture does not match selected green window. Before RGB ({baseline.Red}, {baseline.Green}, {baseline.Blue}); after RGB ({after.Red}, {after.Green}, {after.Blue}); direct WPF RGB ({wpf.Red}, {wpf.Green}, {wpf.Blue}); desktop before RGB ({baselineDesktop.Red}, {baselineDesktop.Green}, {baselineDesktop.Blue}), after RGB ({afterDesktop.Red}, {afterDesktop.Green}, {afterDesktop.Blue}). See {diagnosticsPath}");
            }
            finally { picker.Close(); first.Close(); second.Close(); owner.Close(); }
        });
    }
    private sealed record CaptureDiagnostic(int Width, int Height, byte Red, byte Green, byte Blue, string Path);
    private static CaptureDiagnostic Describe(BitmapSource image, string filename)
    {
        var bytes = UpgradeTests.Bytes(image); int p = ((image.PixelHeight / 2) * image.PixelWidth + image.PixelWidth / 2) * 4;
        return new(image.PixelWidth, image.PixelHeight, bytes[p + 2], bytes[p + 1], bytes[p], Path.Combine(UiChangeTests.PreviewDirectory, filename));
    }
    private static CaptureDiagnostic SaveDiagnostic(BitmapSource image, string filename)
    {
        Directory.CreateDirectory(UiChangeTests.PreviewDirectory);
        string path = Path.Combine(UiChangeTests.PreviewDirectory, filename); if (File.Exists(path)) File.Delete(path); CaptureService.Save(image, path);
        return Describe(image, filename);
    }
}
