using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace QuickCapture;

internal static class OfficeToolsTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check, bool pointer)
    {
        string directory = Path.Combine(Paths.TestRoot!, "Office"); Directory.CreateDirectory(directory);
        var settings = new Settings { AutoSaveScreenshot = false, OutputDirectory = Path.Combine(directory, "Captures") };
        var fixture = TextImage();
        var samples = new List<object>();
        await check("OCR real local simplified Chinese, traditional Chinese, English, crop and no-text", async () =>
        {
            foreach (var (language, image, expected) in new[] {
                ("chi_sim+eng", fixture, "日常办公文字提取\n操作说明与资料对照\nQuickCapture office notes\nProject review 12345"),
                ("chi_tra+eng", TextImage(traditional: true), "日常辦公文字提取\n操作說明與資料對照\nQuickCapture office notes\nProject review 12345"),
                ("eng", TextImage(englishOnly: true), "QuickCapture office notes\nProject review 12345") })
            {
                var timer = Stopwatch.StartNew(); var result = await OcrService.RecognizeAsync(image, language, CancellationToken.None);
                string normalized = Normalize(result.Text), wanted = Normalize(expected);
                double errorRate = Distance(normalized, wanted) / (double)wanted.Length;
                samples.Add(new { language, expected, result.Text, state = result.State.ToString(), milliseconds = timer.ElapsedMilliseconds, characterErrorRate = errorRate });
                File.WriteAllText(Path.Combine(directory, "ocr-samples.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                Ensure(result.State == OcrState.Success && errorRate < 0.08, $"{language} OCR failed or character error exceeds 8%: {result.State} {result.Message} {result.Text}");
            }
            var owner = new Window(); using var editor = new ScreenshotEditor(owner, fixture, settings, _ => { }, () => { }, new Int32Rect(0, 142, 960, 160));
            editor.Surface.Add(new(AnnotationTool.Rectangle, new(0, 0), new(960, 160), Colors.Red, FillColor: Colors.Red));
            var crop = await OcrService.RecognizeAsync(editor.OcrImage(), "eng", CancellationToken.None);
            Ensure(crop.Text.Contains("QuickCapture") && !crop.Text.Contains("INCLUDE"), "OCR ignored crop or used annotation overlay");
            var empty = await OcrService.RecognizeAsync(Blank(300, 200), "eng", CancellationToken.None);
            Ensure(empty.State == OcrState.NoText, "Blank input did not return NoText: " + empty.Message);
            Ensure(!Directory.EnumerateDirectories(Path.Combine(Paths.Data, "OcrJobs")).Any(), "OCR temporary jobs leaked"); owner.Close();
        });
        await check("OCR editable result, actual clipboard, line modes and failure/missing/cancelled states", async () =>
        {
            Ensure(OcrService.MergeLines("中文\n资料\nOffice\nnotes") == "中文资料 Office notes", "Line merge damaged Chinese or Latin word spacing");
            Ensure(OcrService.MissingLanguage(directory, "chi_sim+eng")?.Contains("chi_sim") == true, "Missing models were not reported");
            string failureResult = Path.Combine(directory, "worker-failure.json");
            OcrService.RunWorker(new[] { "--ocr-worker", Path.Combine(directory, "missing.png"), failureResult, OcrService.ModelDirectory, "eng" });
            Ensure(JsonSerializer.Deserialize<OcrResult>(File.ReadAllText(failureResult))?.State == OcrState.Failed, "Native worker failure did not return a structured state");
            var owner = new Window { Width = 700, Height = 500 }; owner.Show();
            try
            {
                foreach (var state in new[] { OcrState.MissingLanguage, OcrState.Failed, OcrState.NoText })
                {
                    var dialog = new OcrWindow(owner, () => fixture, settings, (_, _, _) => Task.FromResult(new OcrResult(state, Message: "验证状态：" + state)));
                    dialog.Show(); await dialog.Recognition; Ensure(dialog.State == state, "Panel lost failure state"); dialog.Close();
                }
                var window = new OcrWindow(owner, () => fixture, settings, (_, _, _) => Task.FromResult(new OcrResult(OcrState.Success, "日常办公\nOffice notes")));
                window.Show(); await window.Recognition;
                window.ResultInput.Text = "编辑后的文字\nOffice notes"; await window.CopyAsync();
                Ensure(Clipboard.GetText() == window.ResultInput.Text, "Editable multiline text did not reach the real clipboard");
                UpgradeTests.Find<CheckBox>(window, _ => true).IsChecked = true; await window.CopyAsync();
                Ensure(Clipboard.GetText() == "编辑后的文字 Office notes", "Merged clipboard lost words"); window.Close();
                var delayed = new OcrWindow(owner, () => fixture, settings, async (_, _, token) => { await Task.Delay(10000, token); return new(OcrState.Success); });
                delayed.Show(); delayed.CancelRecognition(); await delayed.Recognition; Ensure(delayed.State == OcrState.Cancelled, "Cancelled state missing"); delayed.Close();
            }
            finally { owner.Close(); }
        });
        await check("OCR real worker cancellation, editor remains usable, close releases process and files", async () =>
        {
            var owner = new EditorWindow(TextImage(large: true), settings, _ => { }); owner.Show();
            int baseline = Process.GetProcessesByName("QuickCapture").Length;
            var window = new OcrWindow(owner, owner.Editor.OcrImage, settings); window.Show();
            try
            {
                for (int i = 0; i < 40 && Process.GetProcessesByName("QuickCapture").Length <= baseline; i++) await Task.Delay(25);
                Ensure(window.State == OcrState.Recognizing && Process.GetProcessesByName("QuickCapture").Length > baseline, "Real worker never started for cancellation test");
                owner.Editor.AddStep(new(60, 60)); Ensure(owner.Editor.Surface.Count == 1, "Editor blocked during recognition");
                window.Close(); await window.Recognition.WaitAsync(TimeSpan.FromSeconds(10));
                Ensure(Process.GetProcessesByName("QuickCapture").Length == baseline, "Closed OCR left worker alive");
                Ensure(!Directory.EnumerateDirectories(Path.Combine(Paths.Data, "OcrJobs")).Any(), "Cancelled OCR left input/result files");
                owner.Editor.ExtractText(); var owned = Application.Current.Windows.OfType<OcrWindow>().Single();
                for (int i = 0; i < 40 && Process.GetProcessesByName("QuickCapture").Length <= baseline; i++) await Task.Delay(25);
                Ensure(Process.GetProcessesByName("QuickCapture").Length > baseline, "Owned OCR worker did not start");
                owner.Close(); await owned.Recognition.WaitAsync(TimeSpan.FromSeconds(10));
                Ensure(Process.GetProcessesByName("QuickCapture").Length == baseline && !Directory.EnumerateDirectories(Path.Combine(Paths.Data, "OcrJobs")).Any(), "Closing editor leaked its OCR worker or files");
            }
            finally { window.Close(); owner.Close(); }
        });
        await check("Steps sequential numbering, stable deletion, digits/color/size, undo/redo and exact crop export", () =>
        {
            var owner = new Window(); using var editor = new ScreenshotEditor(owner, Blank(640, 400), settings, _ => { }, () => { }, new Int32Rect(30, 40, 560, 320));
            editor.CreateToolbar(); editor.AddStep(new(65, 70)); editor.AddStep(new(170, 130)); editor.AddStep(new(400, 240));
            var surface = editor.Surface;
            Ensure(surface.Items.Select(a => a.StepNumber).SequenceEqual(new[] { 1, 2, 3 }), "Steps not sequential");
            Ensure(surface.Items[0].Start == new Point(75, 90), "Step creation forgot original crop offset");
            surface.SelectAt(new(170, 130)); surface.DeleteSelected(); editor.AddStep(new(220, 170));
            Ensure(surface.Items.Select(a => a.StepNumber).SequenceEqual(new[] { 1, 3, 4 }), "Deleting renumbered other steps or reused a number");
            editor.ResetStepStart(8); editor.AddStep(new(280, 210)); Ensure(surface.Items[^1].StepNumber == 8 && editor.NextStep == 9, "Reset changed existing numbers");
            surface.SelectAt(new(65, 70)); var before = surface.Selected!;
            editor.SetStepNumber(1234); surface.Undo(); Ensure(surface.Items[0].StepNumber == 1, "Digit edit not reversible"); surface.Redo();
            surface.SelectAt(new(65, 70)); editor.ApplyColor(Colors.Gold); var colored = surface.Selected!;
            surface.SetSelected(AnnotationGeometry.Move(colored, new Vector(20, 15))); var moved = surface.Selected!;
            var bounds = AnnotationGeometry.Bounds(moved); surface.SetSelected(AnnotationGeometry.Resize(moved, bounds, new Rect(bounds.TopLeft, new Size(90, 75))));
            Ensure(AnnotationGeometry.Bounds(surface.Selected!).Width == 75 && AnnotationGeometry.Bounds(surface.Selected!).Height == 75, "Resized step lost circle proportions");
            surface.Undo(); Ensure(surface.Items[0] == moved, "Resize undo failed"); surface.Undo(); Ensure(surface.Items[0] == colored, "Move undo failed"); surface.Redo(); surface.Redo();
            var original = surface.Items[0]; surface.SelectAt(new(95, 90)); surface.DeleteSelected(); surface.Undo(); Ensure(surface.Items[0] == original, "Delete undo failed");
            var full = surface.Export(); surface.SetCrop(new Int32Rect(43, 57, 520, 280)); var cropped = surface.Export();
            Ensure(UpgradeTests.Bytes(cropped).SequenceEqual(UpgradeTests.Bytes(new CroppedBitmap(full, new(13, 17, 520, 280)))), "Step pixels drifted during crop export");
            ImageExportService.Save(cropped, Path.Combine(directory, "steps-cropped.png"), ScreenshotFormat.Png, 100, overwrite: true); owner.Close(); return Task.CompletedTask;
        });
        await check("New preferences persist, legacy defaults and malformed values normalize", () =>
        {
            settings.OcrLanguage = "chi_tra+eng"; settings.OcrMergeLines = true; settings.StepSize = 64; settings.StepStart = 5; settings.PinOpacity = 0.6; settings.Save();
            var loaded = Settings.Load(); Ensure(loaded.OcrLanguage == "chi_tra+eng" && loaded.OcrMergeLines && loaded.StepSize == 64 && loaded.StepStart == 5 && loaded.PinOpacity == 0.6, "New settings not persisted");
            File.WriteAllText(Paths.SettingsFile, "{\"ScreenshotHotkey\":\"Ctrl+Alt+F8\",\"RecordingHotkey\":\"Ctrl+Alt+F9\"}");
            loaded = Settings.Load(); Ensure(loaded.StepStart == 1 && loaded.StepSize == 40 && loaded.OcrLanguage == "chi_sim+eng" && loaded.PinOpacity == 1, "Legacy configuration did not get safe defaults");
            File.WriteAllText(Paths.SettingsFile, "{\"StepStart\":-3,\"StepSize\":900,\"PinOpacity\":0,\"OcrLanguage\":\"bad\"}"); loaded = Settings.Load();
            Ensure(loaded.StepStart == 1 && loaded.StepSize == 160 && loaded.PinOpacity == 0.2 && loaded.OcrLanguage == "chi_sim+eng", "Invalid office settings escaped normalization");
            settings.StepStart = 1; settings.StepSize = 40; settings.PinOpacity = 1; settings.OcrLanguage = "chi_sim+eng"; settings.OcrMergeLines = false; settings.Save(); return Task.CompletedTask;
        });
        await check("Pins physical-size zoom/reset, alpha bounds, native click-through recovery and all-pin visibility", async () =>
        {
            var work = Forms.Screen.PrimaryScreen!.WorkingArea;
            foreach (var area in new[] { work, new System.Drawing.Rectangle(-1920, -200, 1920, 1040), new System.Drawing.Rectangle(1920, 0, 2560, 1440) })
            foreach (double scale in new[] { 0.05, 0.5, 1d, 3d, 8d })
            {
                var rect = PinGeometry.Place(640, 400, scale, area, new System.Drawing.Point(area.Right - 5, area.Bottom - 5));
                Ensure(area.Contains(rect) && Math.Abs(rect.Width / (double)rect.Height - 1.6) < 0.06, "Pin left screen or changed aspect ratio");
            }
            var pin = PinManager.Create(fixture, settings, _ => { }); var second = PinManager.Create(Blank(320, 200), settings, _ => { });
            try
            {
                await Task.Delay(150); Ensure(PinManager.Count == 2, "Pins not tracked");
                Native.GetWindowRect(new WindowInteropHelper(second).Handle, out var original); Ensure(original.Right - original.Left == 320 && original.Bottom - original.Top == 200, "100% pin is not source pixels at actual DPI");
                second.ApplyWheel(120, false); await Task.Delay(80); Native.GetWindowRect(new WindowInteropHelper(second).Handle, out var zoomed);
                Ensure(zoomed.Right - zoomed.Left == 352 && zoomed.Bottom - zoomed.Top == 220, "Wheel did not scale proportionally");
                second.ResetSize(); Native.GetWindowRect(new WindowInteropHelper(second).Handle, out var reset); Ensure(reset.Right - reset.Left == 320 && reset.Bottom - reset.Top == 200, "Reset did not restore original physical dimensions");
                second.ApplyWheel(-120, true); Ensure(Math.Abs(second.Opacity - 0.95) < 0.001, "Ctrl wheel did not change alpha"); second.SetOpacity(-1); Ensure(second.Opacity == 0.2, "Pin can become irretrievably transparent"); second.SetOpacity(1);
                second.SetClickThrough(true); Ensure(Native.IsClickThrough(second), "WS_EX_TRANSPARENT absent"); PinManager.HideAll(); Ensure(!pin.IsVisible && !second.IsVisible && PinManager.Hidden, "Hide all lost collection");
                PinManager.RestoreAll(); Ensure(pin.IsVisible && second.IsVisible && second.ClickThrough, "Restore forgot windows or through state");
                PinManager.DisableClickThrough(); Ensure(!second.ClickThrough && !Native.IsClickThrough(second), "Recovery did not clear native input flag");
                Ensure(pin.Menu.Items.OfType<MenuItem>().Any(item => item.Header.ToString()!.Contains("保存")), "Pin menu missing save");
            }
            finally { PinManager.CloseAll(); }
            Ensure(PinManager.Count == 0, "Closing pins leaked registry");
        });
        await check("Actual light/dark OCR panels, step toolbar/options and pin menu screenshots", async () =>
        {
            foreach (string theme in new[] { "Light", "Dark" })
            {
                ThemeService.Apply(theme); var owner = new EditorWindow(fixture, settings, _ => { }) { Width = 1050, Height = 690 }; owner.Show();
                try
                {
                    owner.Editor.AddStep(new(64, 58)); owner.Editor.AddStep(new(64, 179)); owner.Editor.AddStep(new(64, 258)); owner.UpdateLayout();
                    UiChangeTests.Render(owner, "office-steps-" + theme.ToLowerInvariant() + ".png");
                    owner.Editor.ShowStepOptions(); await Task.Delay(80); UiChangeTests.RenderElement((FrameworkElement)owner.Editor.OptionsFor(AnnotationTool.Step), "office-step-options-" + theme.ToLowerInvariant() + ".png");
                    var ocr = new OcrWindow(owner, owner.Editor.OcrImage, settings); ocr.Show(); await ocr.Recognition; ocr.UpdateLayout();
                    Ensure(ocr.State == OcrState.Success, "OCR preview is not a real recognized result"); UiChangeTests.Render(ocr, "office-ocr-" + theme.ToLowerInvariant() + ".png"); ocr.Close();
                    var pin = PinManager.Create(fixture, settings, _ => { }); await Task.Delay(80); pin.Menu.IsOpen = true; await Task.Delay(120); pin.Menu.UpdateLayout(); UiChangeTests.RenderElement(pin.Menu, "office-pin-menu-" + theme.ToLowerInvariant() + ".png");
                    var opacity = pin.Menu.Items.OfType<MenuItem>().Single(item => item.Header.ToString() == "透明度"); opacity.IsSubmenuOpen = true; await Task.Delay(80);
                    var popup = (Popup)opacity.Template.FindName("PART_Popup", opacity); Ensure(popup.IsOpen, "Opacity submenu did not open");
                    UiChangeTests.RenderElement((FrameworkElement)popup.Child, "office-pin-opacity-" + theme.ToLowerInvariant() + ".png");
                    ((MenuItem)opacity.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Ensure(pin.Opacity == 0.6, "Opacity submenu did not apply choice"); PinManager.CloseAll();
                }
                finally { owner.Close(); }
            }
        });
        if (pointer) await check("Real pointer step creation/move/resize/digits/delete and shortcuts at 50% preview", async () =>
        {
            settings.StepStart = 1; settings.StepSize = 40;
            var owner = new EditorWindow(fixture, settings, _ => { }); owner.Show();
            try
            {
                await HotkeyCaptureTests.ActivateAsync(owner); var editor = owner.Editor;
                var inputLog = new List<string>();
                owner.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) => inputLog.Add($"key={e.Key} ime={e.ImeProcessedKey} handled={e.Handled} focus={Keyboard.FocusedElement?.GetType().Name} tool={editor.Tool}")), handledEventsToo: true);
                owner.PreviewMouseLeftButtonDown += (_, e) => inputLog.Add($"owner point={e.GetPosition(editor.Surface)} source={e.OriginalSource.GetType().Name} clicks={e.ClickCount} tool={editor.Tool}");
                UpgradeTests.Find<Slider>(owner, _ => true).Value = 0.5; editor.Surface.SetCrop(new Int32Rect(20, 20, 900, 340)); owner.UpdateLayout(); await Task.Delay(160);
                await HotkeyCaptureTests.PressAsync(owner, Key.D); Ensure(editor.Tool == AnnotationTool.Step, "Step shortcut failed");
                // The options popup is a real input surface. Place the first
                // marker on the visible canvas to its right, not underneath it.
                await HotkeyCaptureTests.DragAsync(owner, editor.Surface.PointToScreen(new(460, 80)), editor.Surface.PointToScreen(new(460, 80)));
                await HotkeyCaptureTests.DragAsync(owner, editor.Surface.PointToScreen(new(650, 140)), editor.Surface.PointToScreen(new(650, 140)));
                UiChangeTests.Render(owner, "office-pointer-check.png");
                File.WriteAllLines(Path.Combine(directory, "pointer-log.txt"), inputLog);
                Ensure(editor.Surface.Count == 2 && editor.Surface.Items[1].StepNumber == 2, $"Actual clicks did not create steps: count={editor.Surface.Count}, tool={editor.Tool}, next={editor.NextStep}, numbers={string.Join(',', editor.Surface.Items.Select(a => a.StepNumber))}");
                await HotkeyCaptureTests.PressAsync(owner, Key.E);
                var beforeMove = editor.Surface.Items[0];
                await HotkeyCaptureTests.DragAsync(owner, editor.Surface.PointToScreen(new(460, 80)), editor.Surface.PointToScreen(new(500, 110)));
                File.WriteAllLines(Path.Combine(directory, "pointer-log.txt"), inputLog);
                Ensure(editor.Surface.Selected is { } moved && Math.Abs(moved.Start.X - beforeMove.Start.X - 40) < 3, $"Zoomed pointer move ignored original coordinates: before={beforeMove.Start}, selected={editor.Surface.Selected?.Start}, tool={editor.Tool}");
                var bounds = AnnotationGeometry.Bounds(editor.Surface.Selected!); var crop = editor.Surface.CropBounds;
                var edge = editor.Surface.PointToScreen(new(bounds.Right - crop.X, bounds.Bottom - crop.Y));
                await HotkeyCaptureTests.DragAsync(owner, edge, edge + new Vector(24, 24)); Ensure(AnnotationGeometry.Bounds(editor.Surface.Selected!).Width > 60, "Actual resize failed");
                editor.ShowStepOptions(); var options = editor.OptionsFor(AnnotationTool.Step);
                UpgradeTests.Find<TextBox>(options, box => box.Name == "StepNumber").Text = "12";
                UpgradeTests.Find<Button>(options, button => button.Content?.ToString() == "修改选中数字").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                 editor.Surface.Focus();
                await HotkeyCaptureTests.PressAsync(owner, Key.Z, ModifierKeys.Control); Ensure(editor.Surface.Items[0].StepNumber == 1, "Ctrl+Z digit edit failed");
                await HotkeyCaptureTests.PressAsync(owner, Key.Y, ModifierKeys.Control); var item = editor.Surface.Items[0]; var itemBounds = AnnotationGeometry.Bounds(item);
                editor.Surface.SelectAt(new(itemBounds.Left + itemBounds.Width / 2 - crop.X, itemBounds.Top + itemBounds.Height / 2 - crop.Y));
                await HotkeyCaptureTests.PressAsync(owner, Key.Delete); Ensure(editor.Surface.Count == 1 && editor.Surface.Items[0].StepNumber == 2, "Delete shifted other digits");
                await HotkeyCaptureTests.PressAsync(owner, Key.Z, ModifierKeys.Control); Ensure(editor.Surface.Count == 2, "Delete undo failed");
            }
            finally { owner.Close(); }
        });
        if (pointer) await check("Actual native wheel/Ctrl-wheel and pointer delivery through pin with tray recovery", async () =>
        {
            settings.PinOpacity = 1;
            var backdrop = new Window { Width = 450, Height = 320, Background = Brushes.Gold, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            int backgroundClicks = 0, pinClicks = 0;
            backdrop.MouseLeftButtonDown += (_, e) => { backgroundClicks++; e.Handled = true; };
            backdrop.Show(); var pin = PinManager.Create(Blank(320, 200), settings, _ => { });
            pin.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => pinClicks++), handledEventsToo: true);
            try
            {
                await Task.Delay(100); var center = backdrop.PointToScreen(new(200, 130));
                Native.SetWindowPos(new WindowInteropHelper(pin).Handle, new IntPtr(-1), (int)center.X - 160, (int)center.Y - 100, 320, 200, 0x0010);
                await HotkeyCaptureTests.WheelAsync(pin, center, 120, false); Ensure(Math.Abs(pin.Zoom - 1.1) < 0.005, "Native wheel did not reach pin");
                await HotkeyCaptureTests.WheelAsync(pin, center, -120, true); Ensure(Math.Abs(pin.Opacity - 0.95) < 0.005, "Native Ctrl wheel did not adjust opacity");
                pin.SetClickThrough(true);
                await HotkeyCaptureTests.DragAsync(backdrop, center, center); Ensure(backgroundClicks == 1 && pinClicks == 0, "Click-through did not deliver pointer to underlying owned window");
                PinManager.DisableClickThrough();
                await HotkeyCaptureTests.DragAsync(backdrop, center, center); Ensure(backgroundClicks == 1 && pinClicks == 1, "Recovery did not restore pin hit testing");
            }
            finally { PinManager.CloseAll(); backdrop.Close(); }
        });
    }
    private static string Normalize(string value) => Regex.Replace(value, @"\s", "");
    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++) { int diagonal = row[0]; row[0] = i; for (int j = 1; j <= b.Length; j++) { int old = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1)); diagonal = old; } }
        return row[^1];
    }
    private static BitmapSource Blank(int width, int height)
    {
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray(); var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
    }
    private static BitmapSource TextImage(bool traditional = false, bool englishOnly = false, bool large = false)
    {
        int width = large ? 3000 : 960, height = large ? 3000 : 380;
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var lines = englishOnly ? new[] { "QuickCapture office notes", "Project review 12345" } : new[] { traditional ? "日常辦公文字提取" : "日常办公文字提取", traditional ? "操作說明與資料對照" : "操作说明与资料对照", "QuickCapture office notes", "Project review 12345" };
            int repetitions = large ? 9 : 1;
            for (int n = 0; n < repetitions; n++) for (int i = 0; i < lines.Length; i++) dc.DrawText(new FormattedText(lines[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 32, Brushes.Black, 1), new Point(120, 35 + i * 65 + n * 310));
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
