using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace QuickCapture;

internal static class UpgradeTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Second crop preserves every annotation, mosaic grid, pixels and undo history", () =>
        {
            var source = TestPixels(640, 400); var surface = new AnnotationSurface(source, new(30, 40, 560, 320));
            surface.Add(new(AnnotationTool.Rectangle, new(20, 25), new(170, 120), Colors.Red));
            surface.Add(new(AnnotationTool.Arrow, new(190, 30), new(260, 90), Colors.Lime));
            surface.Add(new(AnnotationTool.Text, new(20, 180), new(20, 180), Colors.Yellow, "裁剪"));
            surface.Add(new(AnnotationTool.Freehand, new(250, 180), new(290, 240), Colors.Lime, Points: new[] { new Point(250, 180), new Point(270, 220), new Point(290, 240) }, Width: 8));
            surface.Add(new(AnnotationTool.Mosaic, new(330, 30), new(480, 160), Colors.White));
            surface.Add(new(AnnotationTool.MosaicBrush, new(350, 200), new(470, 250), Colors.White, Points: new[] { new Point(350, 200), new Point(470, 250) }, Width: 18));
            var full = surface.Export(); surface.SetCrop(new(43, 57, 520, 280)); var cropped = surface.Export();
            Ensure(Bytes(cropped).SequenceEqual(Bytes(new CroppedBitmap(full, new(13, 17, 520, 280)))), "Crop moved annotations or mosaic blocks");
            surface.SetCrop(new(500, 300, 100, 90)); Ensure(surface.Count == 6, "Clipping removed annotations");
            surface.Undo(); surface.Undo(); Ensure(Bytes(surface.Export()).SequenceEqual(Bytes(full)), "Undo failed to restore exact pixels and annotations");
            surface.Redo(); surface.Undo(); surface.Undo(); Ensure(surface.Count == 5, "Unified crop / annotation undo ordering failed");
            return Task.CompletedTask;
        });
        await check("Real pointer drags all eight crop handles and moves selection; keys move physical pixels", async () =>
        {
            var screen = Forms.Screen.PrimaryScreen!; var selection = new SelectionWindow(screen, TestPixels(screen.Bounds.Width, screen.Bounds.Height), false, _ => { }, new Settings { AutoSaveScreenshot = false });
            try
            {
                selection.Show(); await Task.Delay(100); var initial = new Int32Rect(200, 200, 340, 240);
                selection.BeginEditing(new(screen.Bounds.X + initial.X, screen.Bounds.Y + initial.Y, initial.Width, initial.Height, screen.DeviceName));
                var editor = selection.Editor!; Ensure(editor.Handles.HandleCount == 8, "Missing crop handles");
                var keyLog = new System.Text.StringBuilder();
                selection.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) => keyLog.AppendLine($"{e.Key}/{e.SystemKey} modifiers={Keyboard.Modifiers}, event={e.KeyboardDevice.Modifiers}, shift={e.KeyboardDevice.IsKeyDown(Key.LeftShift)}, handled={e.Handled}")), true);
                var directions = new[] { (-1,-1), (0,-1), (1,-1), (-1,0), (1,0), (-1,1), (0,1), (1,1) };
                int i = 0;
                foreach (var (x, y) in directions)
                {
                    editor.Surface.SetCrop(initial, false); selection.UpdateLayout();
                    var grip = (Thumb)editor.Handles.Children[i++]; var point = grip.PointToScreen(new(grip.ActualWidth / 2, grip.ActualHeight / 2));
                    await HotkeyCaptureTests.DragAsync(selection, point, point + new Vector(13, 9));
                    Ensure(editor.Surface.CropBounds == CropGeometry.Resize(initial, x, y, 13, 9, screen.Bounds.Width, screen.Bounds.Height), $"Handle {x},{y} did not resize by physical pixels: {editor.Surface.CropBounds}");
                    editor.Undo(); Ensure(editor.Surface.CropBounds == initial, "Drag did not create one reversible crop edit");
                }
                var middle = editor.Surface.PointToScreen(new(160, 100)); await HotkeyCaptureTests.DragAsync(selection, middle, middle + new Vector(21, 15));
                Ensure(editor.Surface.CropBounds == new Int32Rect(221, 215, 340, 240), "Whole selection drag failed");
                editor.Surface.Focus(); await HotkeyCaptureTests.PressAsync(selection, Key.Right); await HotkeyCaptureTests.PressAsync(selection, Key.Down, ModifierKeys.Shift);
                Ensure(editor.Surface.CropBounds == new Int32Rect(222, 225, 340, 240), "Arrow / Shift arrow did not move 1 / 10 source pixels: " + editor.Surface.CropBounds + "\n" + keyLog);
                foreach (string theme in new[] { "Dark", "Light" }) { ThemeService.Apply(theme); UiChangeTests.Render(selection, $"crop-handles-{theme.ToLowerInvariant()}.png"); }
            }
            finally { selection.Close(); ThemeService.Apply("Dark"); }
        });
        await check("Unified icon catalog and actual single-key shortcuts; typing does not switch tools", async () =>
        {
            var owner = new Window { Width = 760, Height = 380 }; Ui.Theme(owner);
            using var editor = new ScreenshotEditor(owner, TestPixels(300, 160), new Settings(), _ => { }, () => { });
            var root = new StackPanel(); var toolbar = editor.CreateToolbar(); root.Children.Add(toolbar); var input = new TextBox(); root.Children.Add(input); owner.Content = root;
            try
            {
                owner.Show(); await HotkeyCaptureTests.ActivateAsync(owner);
                var tools = ToolCatalog.Create(editor, null); Ensure(tools.Select(t => t.Shortcut).Distinct().Count() == tools.Count, "Duplicate single-key shortcuts");
                foreach (var definition in tools.Where(t => t.Id != "reselect")) { var button = editor.ButtonFor(definition.Id); Ensure(button.Content is Viewbox && button.ToolTip!.ToString()!.Contains(definition.Shortcut.ToString()), "Button is not an icon with a shortcut tooltip"); }
                editor.ButtonFor("crop").Focus();
                foreach (var (key, tool) in new[] { (Key.R,AnnotationTool.Rectangle), (Key.A,AnnotationTool.Arrow), (Key.T,AnnotationTool.Text), (Key.B,AnnotationTool.Freehand), (Key.M,AnnotationTool.Mosaic), (Key.C,AnnotationTool.Crop) }) { await HotkeyCaptureTests.PressAsync(owner, key); Ensure(editor.Tool == tool, $"Shortcut {key} failed"); }
                input.Focus(); Ensure(input.IsKeyboardFocused, "Text field could not receive focus");
                await HotkeyCaptureTests.PressAsync(owner, Key.R); Ensure(editor.Tool == AnnotationTool.Crop, $"Typing hijacked tool shortcut: tool={editor.Tool}");
                // Text composition can depend on the user's active keyboard / IME;
                // assert the editor leaves the actual text-input key unhandled.
                var textKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), 0, Key.T) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                input.RaiseEvent(textKey); Ensure(!textKey.Handled && editor.Tool == AnnotationTool.Crop, "Text input was consumed by a tool shortcut");
            }
            finally { owner.Close(); }
        });
        await check("PNG JPG WebP BMP actual headers, dimensions, quality, extension and atomic overwrite", () =>
        {
            var source = TestPixels(311, 173);
            foreach (var format in Enum.GetValues<ScreenshotFormat>())
            {
                var encoded = ImageExportService.Encode(source, format, 90); var decoded = ImageExportService.Decode(encoded, format);
                Ensure(decoded.PixelWidth == 311 && decoded.PixelHeight == 173, "Image codec changed dimensions");
                bool header = format switch { ScreenshotFormat.Png => encoded.Take(8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }), ScreenshotFormat.Jpg => encoded[0] == 255 && encoded[1] == 216, ScreenshotFormat.Bmp => encoded[0] == 66 && encoded[1] == 77, _ => System.Text.Encoding.ASCII.GetString(encoded, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(encoded, 8, 4) == "WEBP" };
                Ensure(header, "Encoded bytes do not match selected format");
                if (format is ScreenshotFormat.Png or ScreenshotFormat.Bmp) Ensure(Bytes(source).SequenceEqual(Bytes(decoded)), "Lossless format changed image pixels");
                else Ensure(encoded.Length > ImageExportService.Encode(source, format, 15).Length, "Quality slider does not affect encoding");
                string path = Path.Combine(Paths.TestRoot!, "image-" + ImageExportService.Extension(format) + "." + ImageExportService.Extension(format));
                if (File.Exists(path)) File.Delete(path); ImageExportService.Save(source, path, format, 90);
                Ensure(ImageExportService.CorrectExtension("example.bad", format) == "example." + ImageExportService.Extension(format), "Wrong filename extension");
                bool blocked = false; try { ImageExportService.Save(source, path, format, 90); } catch (IOException) { blocked = true; } Ensure(blocked && File.ReadAllBytes(path).SequenceEqual(encoded), "Overwrite guard changed existing file");
                ImageExportService.Save(source, path, format, 90, true);
            }
            Ensure(!Directory.EnumerateFiles(Paths.TestRoot!, "*.partial").Any(), "Image save left a partial file"); return Task.CompletedTask;
        });
        await check("Clipboard preserves selected encoding plus bitmap / DIB compatibility for all formats", async () =>
        {
            var source = TestPixels(129, 81);
            foreach (var format in Enum.GetValues<ScreenshotFormat>())
            {
                await ImageExportService.CopyAsync(source, format, 67);
                var clipboard = Clipboard.GetDataObject(); string registered = format switch { ScreenshotFormat.Png => "PNG", ScreenshotFormat.Jpg => "JFIF", ScreenshotFormat.WebP => "WebP", _ => "BMP" };
                Ensure(clipboard != null && clipboard.GetDataPresent(registered, false) && clipboard.GetDataPresent(DataFormats.Bitmap), "Clipboard lost selected encoding or standard bitmap");
                var encoded = ((MemoryStream)clipboard!.GetData(registered, false)).ToArray(); Ensure(encoded.SequenceEqual(ImageExportService.Encode(source, format, 67)), "Clipboard did not honor selected format / quality");
                var compatible = Clipboard.GetImage(); Ensure(compatible != null && compatible.PixelWidth == 129 && compatible.PixelHeight == 81, $"Clipboard bitmap unreadable for {format}: {compatible?.PixelWidth}x{compatible?.PixelHeight}");
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    compatible = Clipboard.GetImage(); var expected = Bytes(ImageExportService.Decode(encoded, format)); var actual = compatible != null ? Bytes(compatible) : Array.Empty<byte>();
                    int difference = Enumerable.Range(0, Math.Min(actual.Length, expected.Length)).FirstOrDefault(i => actual[i] != expected[i], -1);
                    Ensure(compatible != null && actual.SequenceEqual(expected), $"Repeated clipboard reads changed pixels or lost lossy quality: {format}, read {repeat}, size {compatible?.PixelWidth}x{compatible?.PixelHeight}, format {compatible?.Format}, first difference {difference}: {(difference >= 0 ? actual[difference] : -1)} / {(difference >= 0 ? expected[difference] : -1)}");
                }
                var dib = (MemoryStream)clipboard.GetData(DataFormats.Dib, false); var dibBytes = dib.ToArray(); File.WriteAllBytes(Path.Combine(Paths.TestRoot!, "clipboard.dib"), dibBytes);
                Ensure(BitConverter.ToInt32(dibBytes, 0) is 12 or 40 or 108 or 124, "Unexpected DIB header: " + Convert.ToHexString(dibBytes[..Math.Min(20, dibBytes.Length)]));
            }
        });
        await check("Screenshot save dialog follows defaults, temporarily switches quality/format, and writes correct file", async () =>
        {
            var owner = new Window(); Ui.Theme(owner); owner.Show(); var settings = new Settings { ScreenshotFormat = ScreenshotFormat.WebP, ScreenshotQuality = 78, RecordingFormat = RecordingFormat.Gif, RecordingQuality = ExportQuality.Low };
            var dialog = new ScreenshotSaveWindow(owner, TestPixels(380, 200), settings);
            try
            {
                await WithDialogAsync(dialog, () =>
                {
                    var format = Find<ComboBox>(dialog, b => b.Name == "ImageFormat"); var quality = Find<Slider>(dialog, b => b.Name == "ImageQuality"); var filename = Find<TextBox>(dialog, b => b.Name == "ImageFilename");
                    Ensure(format.SelectedIndex == 2 && quality.Value == 78, "Save dialog ignored screenshot defaults");
                    foreach (string theme in new[] { "Dark", "Light" }) { ThemeService.Apply(theme); UiChangeTests.Render(dialog, $"image-save-{theme.ToLowerInvariant()}.png"); }
                    format.SelectedIndex = 1; quality.Value = 45; filename.Text = "save-dialog-" + Guid.NewGuid().ToString("N") + ".bad";
                    Find<Button>(dialog, b => (string?)b.Content == "保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask;
                });
                Ensure(dialog.SavedPath?.EndsWith(".jpg") == true && File.Exists(dialog.SavedPath), "Save dialog did not use selected format");
                Ensure(settings.ScreenshotFormat == ScreenshotFormat.WebP && settings.ScreenshotQuality == 78 && settings.RecordingFormat == RecordingFormat.Gif && settings.RecordingQuality == ExportQuality.Low, "Temporary save choice changed global / recording defaults");
            }
            finally { dialog.Close(); owner.Close(); ThemeService.Apply("Dark"); }
        });
        await check("Independent screenshot / recording defaults persist and legacy settings migrate", () =>
        {
            string? backup = File.Exists(Paths.SettingsFile) ? File.ReadAllText(Paths.SettingsFile) : null;
            try
            {
                var settings = new Settings { ScreenshotFormat = ScreenshotFormat.Jpg, ScreenshotQuality = 61, RecordingFormat = RecordingFormat.WebM, RecordingQuality = ExportQuality.High, GifFps = 20 }; settings.Save();
                var loaded = Settings.Load(); Ensure(loaded.ScreenshotFormat == ScreenshotFormat.Jpg && loaded.ScreenshotQuality == 61 && loaded.RecordingFormat == RecordingFormat.WebM && loaded.RecordingQuality == ExportQuality.High && loaded.GifFps == 20, "Independent format settings were not persisted");
                File.WriteAllText(Paths.SettingsFile, "{\"ScreenshotHotkey\":\"Ctrl+Alt+S\",\"RecordingHotkey\":\"Ctrl+Alt+R\"}"); loaded = Settings.Load();
                Ensure(loaded.ScreenshotFormat == ScreenshotFormat.Png && loaded.RecordingFormat == RecordingFormat.Mp4 && loaded.ScreenshotQuality == 90, "Legacy settings migration failed");
            }
            finally { if (backup != null) File.WriteAllText(Paths.SettingsFile, backup); else File.Delete(Paths.SettingsFile); } return Task.CompletedTask;
        });
        await check("Standalone editor crop at 50 percent zoom keeps source pixels, grip size and undo", async () =>
        {
            var window = new EditorWindow(TestPixels(700, 420), new Settings(), _ => { });
            try
            {
                window.Show(); await Task.Delay(100); var surface = Find<AnnotationSurface>(window, _ => true);
                var zoom = Find<Slider>(window, _ => true); zoom.Value = 0.5; surface.SetCrop(new(40, 40, 620, 340)); window.UpdateLayout(); await Task.Delay(40);
                var handles = Find<CropHandles>(window, _ => true); var grip = (Thumb)handles.Children[7];
                var start = grip.PointToScreen(new(grip.ActualWidth / 2, grip.ActualHeight / 2)); var scale = CropHandles.ScaleInOwner(surface, window); var dpi = VisualTreeHelper.GetDpi(window);
                Ensure(Math.Abs(grip.ActualWidth * scale.X - UiDesign.Number("HandleSize")) < 0.01, "Zoom made handles inaccessible");
                await HotkeyCaptureTests.DragAsync(window, start, start + new Vector(-50, -25));
                int dx = (int)Math.Round(-50 / scale.X / dpi.DpiScaleX), dy = (int)Math.Round(-25 / scale.Y / dpi.DpiScaleY);
                Ensure(surface.CropBounds == new Int32Rect(40, 40, 620 + dx, 340 + dy), "Standalone zoom distorted crop coordinates");
                Find<Button>(window, b => b.Name == "Tool_undo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Ensure(surface.CropBounds == new Int32Rect(40, 40, 620, 340), "Standalone crop undo failed");
                ThemeService.Apply("Dark"); UiChangeTests.Render(window, "editor-crop-dark.png"); ThemeService.Apply("Light"); UiChangeTests.Render(window, "editor-crop-light.png");
            }
            finally { window.Close(); ThemeService.Apply("Dark"); }
        });
        await check("Settings UI groups formats independently, saves defaults and preserves captured hotkeys", async () =>
        {
            string? backup = File.Exists(Paths.SettingsFile) ? File.ReadAllText(Paths.SettingsFile) : null;
            var owner = new Window(); Ui.Theme(owner); owner.Show();
            using var hotkeys = new HotkeyService(new System.Windows.Interop.WindowInteropHelper(owner).Handle);
            var settings = new Settings { ScreenshotHotkey = "Ctrl+Shift+Alt+F6", RecordingHotkey = "Ctrl+Shift+Alt+F7" }; hotkeys.Configure(settings.ScreenshotHotkey, settings.RecordingHotkey);
            var dialog = new SettingsWindow(owner, settings, hotkeys);
            try
            {
                await WithDialogAsync(dialog, () =>
                {
                    Find<ComboBox>(dialog, b => b.Name == "DefaultScreenshotFormat").SelectedIndex = 2; Find<Slider>(dialog, b => b.Name == "DefaultScreenshotQuality").Value = 73;
                    Find<ComboBox>(dialog, b => b.Name == "DefaultRecordingFormat").SelectedIndex = 2; Find<ComboBox>(dialog, b => b.Name == "DefaultRecordingQuality").SelectedIndex = 0;
                    var scroll = Find<ScrollViewer>(dialog, b => b.Content is StackPanel); scroll.ScrollToEnd(); dialog.UpdateLayout();
                    foreach (string theme in new[] { "Dark", "Light" }) { ThemeService.Apply(theme); UiChangeTests.Render(dialog, $"settings-formats-{theme.ToLowerInvariant()}.png"); }
                    Find<Button>(dialog, b => (string?)b.Content == "保存设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask;
                });
                var saved = Settings.Load(); Ensure(saved.ScreenshotFormat == ScreenshotFormat.WebP && saved.ScreenshotQuality == 73 && saved.RecordingFormat == RecordingFormat.Gif && saved.RecordingQuality == ExportQuality.Low && saved.ScreenshotHotkey == settings.ScreenshotHotkey, "Settings UI mixed defaults or changed hotkeys");
            }
            finally { dialog.Close(); owner.Close(); ThemeService.Apply("Dark"); if (backup != null) File.WriteAllText(Paths.SettingsFile, backup); else File.Delete(Paths.SettingsFile); }
        });
    }
    internal static BitmapSource TestPixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { int p = (y * width + x) * 4; pixels[p] = (byte)((x * 7 + y * 11) % 256); pixels[p + 1] = (byte)((x * 3 + y) % 256); pixels[p + 2] = (byte)((x + y * 2) % 256); pixels[p + 3] = 255; }
        var source = BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgra32, null, pixels, width * 4); source.Freeze(); return source;
    }
    internal static byte[] Bytes(BitmapSource image) { var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes; }
    internal static T Find<T>(DependencyObject root, Predicate<T> predicate) where T : DependencyObject
    { if (root is T item && predicate(item)) return item; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { try { return Find(VisualTreeHelper.GetChild(root, i), predicate); } catch (InvalidOperationException) { } } throw new InvalidOperationException("Control not found: " + typeof(T).Name); }
    internal static async Task WithDialogAsync(Window dialog, Func<Task> action)
    { var done = new TaskCompletionSource(); dialog.Loaded += async (_, _) => { try { await Task.Delay(120); await action(); done.SetResult(); } catch (Exception ex) { done.SetException(ex); } finally { if (dialog.IsVisible) dialog.Close(); } }; dialog.ShowDialog(); await done.Task; }
    internal static void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
