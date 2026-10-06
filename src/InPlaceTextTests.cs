using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

// These checks operate the real editor window, textbox, routed composition
// events and native pointer/key input; they do not maintain a second text model.
internal static class InPlaceTextTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("In-place text creation, empty confirmation, Esc, newline and single-step undo/redo", async () =>
        {
            await WithEditor(async (window, editor) =>
            {
                await ClickTextAsync(window, editor, new(70, 90));
                var input = RequireInput(editor);
                Ensure(input.IsKeyboardFocused && input.AcceptsReturn && InputMethod.GetIsInputMethodEnabled(input), "The in-place textbox is not focused, multiline or IME-enabled");
                Ensure(input.Background is SolidColorBrush background && background.Color.A == 0, "The edit textbox obscures the screenshot");
                input.Text = "取消这次输入";
                await HotkeyCaptureTests.PressAsync(window, Key.Escape);
                Ensure(!editor.EditingText && editor.Surface.Count == 0 && !editor.Surface.CanUndo && window.IsVisible, "Esc left an empty object/history step or closed the editor");

                await ClickTextAsync(window, editor, new(70, 90));
                RequireInput(editor).Text = " \r\n \t";
                editor.ConfirmText();
                Ensure(editor.Surface.Count == 0 && !editor.Surface.CanUndo, "Whitespace confirmation created an annotation");

                await ClickTextAsync(window, editor, new(70, 90));
                input = RequireInput(editor); input.Text = "轻截 QuickCapture"; input.CaretIndex = input.Text.Length;
                await HotkeyCaptureTests.PressAsync(window, Key.Enter);
                Ensure(editor.EditingText && input.Text.Contains('\n') && editor.Surface.Count == 0, "Enter submitted instead of adding a newline");
                input.SelectedText = "中文与 English 多行文字";
                await HotkeyCaptureTests.PressAsync(window, Key.Enter, ModifierKeys.Control);
                Ensure(!editor.EditingText && editor.Surface.Count == 1 && editor.Surface.Items[0].Text.Contains("中文与 English"), "Ctrl+Enter did not confirm multiline Chinese/English text");
                editor.Undo(); Ensure(editor.Surface.Count == 0 && !editor.Surface.CanUndo && editor.Surface.CanRedo, "One confirmation produced more than one undo step");
                editor.Redo(); Ensure(editor.Surface.Count == 1, "Redo did not restore the confirmed text");
            });
        });

        await check("Text font, size, bold, alignment, palette and matching clean preview/export", async () =>
        {
            await WithEditor(async (window, editor) =>
            {
                await ClickTextAsync(window, editor, new(90, 150));
                var input = RequireInput(editor); input.Text = "透明文字预览\nQuickCapture";
                editor.ShowTextOptions();
                var options = editor.OptionsFor(AnnotationTool.Text);
                Find<ComboBox>(options, c => c.ToolTip as string == "字体").SelectedItem = "Microsoft YaHei UI";
                Find<ComboBox>(options, c => c.ToolTip as string == "字号（原始像素）").SelectedItem = 32d;
                Find<CheckBox>(options, c => c.Content as string == "粗体").IsChecked = true;
                Find<ComboBox>(options, c => c.ToolTip as string == "文字对齐").SelectedIndex = 1;
                editor.ShowColors();
                Find<TextBox>(editor.Palette, _ => true).Text = "#14725E";
                Find<Button>(editor.Palette, b => b.Content as string == "应用颜色").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editor.StartEyedropper(false);
                await HotkeyCaptureTests.PressAsync(window, Key.Escape);
                Ensure(editor.EditingText && !editor.PickingColor && input.Text == "透明文字预览\nQuickCapture", "Cancelling the eyedropper lost the active text edit");
                editor.StartEyedropper(false);
                var sample = editor.Surface.PointToScreen(new(450, 440));
                await HotkeyCaptureTests.DragAsync(window, sample, sample);
                Ensure(editor.EditingText && !editor.PickingColor && editor.CurrentColor == Colors.White, "The canvas eyedropper did not update the active text draft");
                editor.ShowColors(); Find<TextBox>(editor.Palette, _ => true).Text = "#14725E";
                Find<Button>(editor.Palette, b => b.Content as string == "应用颜色").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editor.TextInput!.Focus(); window.UpdateLayout();
                Ensure(editor.EditingText && input.FontSize == 32 && input.FontWeight == FontWeights.Bold && input.TextAlignment == TextAlignment.Center, "Text style changes did not update the active textbox");
                Ensure(editor.Surface.Count == 0 && !editor.Surface.CanUndo, "Draft properties created annotation history before confirmation");
                Ensure(input.ActualHeight >= 32 * 2 && input.ActualWidth > 50, "Multiline input is clipped or has no usable width");
                byte[] draftPreview = Bytes(RenderSurface(editor.Surface));
                var done = Find<Button>(window, b => b.IsVisible && ((b.Content as string)?.Contains("完成") == true || (b.Content as string)?.Contains("确认文字") == true));
                done.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                Ensure(!editor.EditingText && editor.Surface.Count == 1, "The visible completion action did not confirm text");
                var text = editor.Surface.Items[0];
                Ensure(text.FontFamily == "Microsoft YaHei UI" && text.FontSize == 32 && text.Bold && text.Alignment == TextAlignment.Center && text.Color == Color.FromRgb(20, 114, 94), "Confirmed annotation lost text properties");
                Ensure(draftPreview.SequenceEqual(Bytes(editor.Surface.Export())), "The rendered draft glyphs differ from the confirmed export");
                editor.Surface.SelectAt(new(100, 160));
                byte[] selectedExport = Bytes(editor.Surface.Export()); editor.Surface.Deselect();
                Ensure(selectedExport.SequenceEqual(Bytes(editor.Surface.Export())), "Selection borders/control points leaked into export");
                editor.Undo(); Ensure(editor.Surface.Count == 0 && !editor.Surface.CanUndo, "Style edits split a confirmation into multiple history steps");
                editor.Redo(); Ensure(editor.Surface.Items[0] == text, "Redo lost the edited text style");
            });
        });

        await check("Existing text double-click edit, cancel restoration, move/delete and reversible confirmation", async () =>
        {
            await WithEditor(async (window, editor) =>
            {
                await ClickTextAsync(window, editor, new(80, 110));
                RequireInput(editor).Text = "原有文字\nOriginal"; editor.ConfirmText();
                var before = editor.Surface.Items[0]; editor.SelectTool(AnnotationTool.Select);
                await DoubleClickAsync(window, editor.Surface.PointToScreen(new(95, 120)));
                Ensure(editor.EditingText, "Double-click did not reopen the text object");
                RequireInput(editor).Text = "修改后取消"; editor.SetTextStyle("Segoe UI", 42, true, TextAlignment.Right); editor.ApplyColor(Colors.OrangeRed);
                await HotkeyCaptureTests.PressAsync(window, Key.Escape);
                Ensure(!editor.EditingText && editor.Surface.Items[0] == before, "Cancel did not restore the original text/style");
                editor.Undo(); Ensure(editor.Surface.Count == 0, "Cancel created a history step"); editor.Redo();

                editor.Surface.SelectAt(new(95, 120)); editor.BeginText(new(80, 110), editor.Surface.Selected);
                RequireInput(editor).Text = "已更新\nUpdated"; editor.SetTextStyle("Segoe UI", 30, true, TextAlignment.Right); editor.ConfirmText();
                var after = editor.Surface.Items[0]; Ensure(after.Text.Contains("Updated") && after.Bold, "Existing text confirmation ignored changes");
                editor.Undo(); Ensure(editor.Surface.Items[0] == before, "Undo did not restore the previous text in one step");
                editor.Redo(); Ensure(editor.Surface.Items[0] == after, "Redo did not restore the edited text");
                editor.Surface.Add(new(AnnotationTool.Rectangle, new(140, 108), new(235, 152), Colors.LightSkyBlue, FillColor: Colors.LightSkyBlue));
                editor.Surface.SelectAt(new(85, 120)); editor.BeginText(new(80, 110), editor.Surface.Selected); window.UpdateLayout();
                byte[] layeredPreview = Bytes(RenderSurface(editor.Surface)); editor.ConfirmText();
                Ensure(layeredPreview.SequenceEqual(Bytes(editor.Surface.Export())), "Re-editing text changed its preview stacking order relative to a later shape");
                editor.Surface.SelectAt(new(180, 130)); editor.Surface.DeleteSelected();
                var uncropped = editor.Surface.CropBounds; editor.Surface.SetCrop(new(100, 90, 760, 440));
                editor.Surface.SelectAt(new(0, 30)); editor.BeginText(new(0, 30), editor.Surface.Selected); editor.ConfirmText();
                Ensure(editor.Surface.Items[0] == after, "Opening cropped text without changes moved or restyled the existing annotation");
                editor.Undo(); Ensure(editor.Surface.CropBounds == uncropped && editor.Surface.Items[0] == after, "No-op text confirmation inserted a new undo step ahead of the crop");
                editor.SelectTool(AnnotationTool.Select);
                await HotkeyCaptureTests.DragAsync(window, editor.Surface.PointToScreen(new(95, 120)), editor.Surface.PointToScreen(new(118, 134)));
                Ensure(editor.Surface.Selected is { } moved && (moved.Start - after.Start).Length > 15, "Confirmed text cannot be moved with the pointer");
                await HotkeyCaptureTests.PressAsync(window, Key.Delete);
                Ensure(editor.Surface.Count == 0, "Delete did not remove selected text"); editor.Undo(); Ensure(editor.Surface.Count == 1, "Undo did not restore deleted text");
            });
        });

        await check("Text composition events suppress confirmation/cancel/tool shortcuts until composition completes", async () =>
        {
            await WithEditor(async (window, editor) =>
            {
                await ClickTextAsync(window, editor, new(80, 110));
                var input = RequireInput(editor); input.Text = "中文";
                var composition = new TextComposition(InputManager.Current, input, "中文");
                Composition(input, TextCompositionManager.PreviewTextInputStartEvent, composition);
                Composition(input, TextCompositionManager.PreviewTextInputUpdateEvent, composition);
                // A native Enter sent without an actual installed IME session
                // would produce an ordinary TextInput and complete this synthetic
                // composition. Route the candidate-stage keys through WPF while
                // keeping the real Ctrl modifier pressed instead.
                await CompositionKeyAsync(window, input, Key.Enter, control: true);
                Ensure(editor.EditingText && editor.Surface.Count == 0, "Ctrl+Enter confirmed during IME composition");
                await CompositionKeyAsync(window, input, Key.Escape);
                Ensure(editor.EditingText && editor.Surface.Count == 0 && window.IsVisible, "Esc cancelled/closed the editor during IME composition");
                await CompositionKeyAsync(window, input, Key.R);
                Ensure(editor.Tool == AnnotationTool.Text && editor.EditingText, "A letter triggered a drawing shortcut during composition");
                Composition(input, TextCompositionManager.TextInputEvent, composition); input.Text = "中文组合输入完成";
                await HotkeyCaptureTests.PressAsync(window, Key.Enter, ModifierKeys.Control);
                Ensure(!editor.EditingText && editor.Surface.Items.Single().Text == "中文组合输入完成", "Completed composition could not be confirmed normally");
            });
        });

        await check("Text positioning across zoom, scroll, crop and DPI; clean PNG and clipboard output", async () =>
        {
            await WithEditor(async (window, editor) =>
            {
                editor.Surface.SetCrop(new(42, 32, 760, 440));
                var zoom = Find<Slider>(window, _ => true); zoom.Value = 1.5;
                var scroll = Find<ScrollViewer>(window, s => s.Content is Canvas); window.UpdateLayout();
                scroll.ScrollToHorizontalOffset(85); scroll.ScrollToVerticalOffset(62); window.UpdateLayout();
                var point = new Point(150, 165); await ClickTextAsync(window, editor, point);
                var input = RequireInput(editor); input.Text = "位置与导出一致\nZoom + scroll";
                editor.SetTextStyle("Microsoft YaHei UI", 26, false, TextAlignment.Left); window.UpdateLayout();
                var screenInput = input.PointToScreen(new(0, 0)); var screenAnchor = editor.Surface.PointToScreen(point);
                var dpi = VisualTreeHelper.GetDpi(window);
                Ensure(Math.Abs(screenInput.X - screenAnchor.X) < 15 * dpi.DpiScaleX && Math.Abs(screenInput.Y - screenAnchor.Y) < 15 * dpi.DpiScaleY, "Transparent editor drifted away from the scaled/scrolled click position");
                editor.ConfirmText(); var annotation = editor.Surface.Items.Single();
                Ensure(Math.Abs(annotation.Start.X - point.X - 42) < 2 && Math.Abs(annotation.Start.Y - point.Y - 32) < 2, "Text annotation did not retain original-image coordinates");
                var output = editor.Surface.Export(); Ensure(output.PixelWidth == 760 && output.PixelHeight == 440, "Zoom or system DPI changed export dimensions");
                string path = editor.SaveImage();
                VerifySavedPng(output, path, editor.Surface);
                editor.BeginText(new(759, 439)); RequireInput(editor).Text = "靠近边缘仍可输入"; window.UpdateLayout();
                var edge = editor.Surface.TextDraft!; var edgeInput = RequireInput(editor);
                Ensure(edge.Start.X >= 42 && edge.Start.Y >= 32 && edge.Start.X + edge.TextWidth <= 802 && edgeInput.ActualWidth >= 120 && edgeInput.ActualHeight >= 26, "An edge insertion did not expand into a usable input range inside the crop");
                var inputScroll = Find<ScrollViewer>(edgeInput, _ => true);
                Ensure(inputScroll.ExtentHeight <= inputScroll.ViewportHeight + 1, "Edge text unexpectedly needs internal scrolling and separates the caret from its preview");
                editor.CancelText(); Ensure(editor.Surface.Items.Single() == annotation, "Cancelling edge text changed confirmed annotations");
                await editor.CopyAndCompleteAsync();
                var clipboard = Clipboard.GetDataObject();
                Ensure(clipboard?.GetData("PNG", false) is MemoryStream png && png.ToArray().SequenceEqual(File.ReadAllBytes(path)), "Copied PNG differs from saved text export");
                var copied = Clipboard.GetImage();
                Ensure(copied != null && copied.PixelWidth == output.PixelWidth && copied.PixelHeight == output.PixelHeight, "Clipboard is missing the compatible bitmap representation");
                var savedPng = ImageExportService.Decode(File.ReadAllBytes(path), ScreenshotFormat.Png);
                var opaqueReference = ImageExportService.Decode(ImageExportService.Encode(savedPng, ScreenshotFormat.Bmp, 100), ScreenshotFormat.Bmp);
                Ensure(StraightBytes(copied!).SequenceEqual(StraightBytes(opaqueReference)), "Compatible clipboard bitmap differs from the saved PNG composited onto white");
            }, sourceDpi: 144);
        });
    }

    private static async Task WithEditor(Func<EditorWindow, ScreenshotEditor, Task> action, double sourceDpi = 96)
    {
        var image = AcceptanceArtifacts.NoteImage(900, 540, sourceDpi);
        var settings = new Settings { AutoSaveScreenshot = false, ScreenshotFormat = ScreenshotFormat.Png, OutputDirectory = Path.Combine(Paths.TestRoot ?? AppContext.BaseDirectory, "TextCaptures") };
        var window = new EditorWindow(image, settings, _ => { }) { Width = 1000, Height = 650, Topmost = true };
        try { window.Show(); await HotkeyCaptureTests.ActivateAsync(window); window.UpdateLayout(); await action(window, window.Editor); }
        finally { window.Close(); }
    }
    private static async Task ClickTextAsync(Window window, ScreenshotEditor editor, Point point)
    {
        editor.SelectTool(AnnotationTool.Text); window.UpdateLayout();
        var physical = editor.Surface.PointToScreen(point); await HotkeyCaptureTests.DragAsync(window, physical, physical);
        window.UpdateLayout(); Ensure(editor.EditingText, "Click did not open an in-place text editor");
    }
    private static async Task DoubleClickAsync(Window window, Point physical)
    {
        await HotkeyCaptureTests.ActivateAsync(window);
        Ensure(GetForegroundWindow() == new WindowInteropHelper(window).Handle && window.IsActive && Keyboard.Modifiers == ModifierKeys.None, "Verification editor is not foreground; no double-click was sent");
        var prior = System.Windows.Forms.Cursor.Position;
        try
        {
            SetCursorPos((int)Math.Round(physical.X), (int)Math.Round(physical.Y)); await Task.Delay(35);
            MouseEvent(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(25); MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(25); MouseEvent(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(25); MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(90);
        }
        finally { MouseEvent(4, 0, 0, 0, UIntPtr.Zero); System.Windows.Forms.Cursor.Position = prior; }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void KeyboardEvent(byte key, byte scan, uint flags, UIntPtr extra);
    private static async Task CompositionKeyAsync(Window window, TextBox input, Key key, bool control = false)
    {
        await HotkeyCaptureTests.ActivateAsync(window);
        Ensure(GetForegroundWindow() == new WindowInteropHelper(window).Handle && window.IsActive && Keyboard.Modifiers == ModifierKeys.None, "Verification editor is not foreground; no composition-stage key was sent");
        try
        {
            if (control) { KeyboardEvent(0x11, 0, 0, UIntPtr.Zero); await Task.Delay(40); Ensure(Keyboard.Modifiers.HasFlag(ModifierKeys.Control), "The real Ctrl modifier was not delivered"); }
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        finally { if (control) { KeyboardEvent(0x11, 0, 2, UIntPtr.Zero); await Task.Delay(40); } }
    }
    private static void Composition(TextBox input, RoutedEvent routedEvent, TextComposition composition)
    {
        input.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, composition) { RoutedEvent = routedEvent });
    }
    private static void VerifySavedPng(BitmapSource expected, string path, AnnotationSurface surface)
    {
        // PNG stores straight alpha. Compare that representation directly:
        // re-premultiplying decoded alpha=253/254 can introduce one-byte rounding.
        var loaded = CaptureService.Load(path); byte[] reference = StraightBytes(expected), actual = StraightBytes(loaded);
        if (reference.SequenceEqual(actual)) return;
        var after = surface.Export(); string directory = Path.GetDirectoryName(path)!;
        byte[] encodedReference = ImageExportService.Encode(expected, ScreenshotFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(directory, "png-diagnostic-expected.png"), encodedReference);
        File.WriteAllBytes(Path.Combine(directory, "png-diagnostic-after-save.png"), ImageExportService.Encode(after, ScreenshotFormat.Png, 90));
        int differences = 0, maxDelta = 0, first = -1;
        for (int i = 0; i < Math.Min(reference.Length, actual.Length); i++) if (reference[i] != actual[i]) { differences++; maxDelta = Math.Max(maxDelta, Math.Abs(reference[i] - actual[i])); if (first < 0) first = i; }
        string Alpha(byte[] bytes) => string.Join(",", Enumerable.Range(0, bytes.Length / 4).Select(i => bytes[i * 4 + 3]).Distinct().Order());
        string report = $"expectedFormat={expected.Format}; actualFormat={loaded.Format}; expectedSize={expected.PixelWidth}x{expected.PixelHeight}; actualSize={loaded.PixelWidth}x{loaded.PixelHeight}\n" +
            $"differences={differences}; maxDelta={maxDelta}; firstByte={first}; firstPixel=({(first < 0 ? -1 : first / 4 % expected.PixelWidth)},{(first < 0 ? -1 : first / 4 / expected.PixelWidth)})\n" +
            $"expectedAlpha={Alpha(reference)}; actualAlpha={Alpha(actual)}\n" +
            $"saveBeforeAfterPbgraEqual={Bytes(expected).SequenceEqual(Bytes(after))}; encodedExpectedFileEqual={encodedReference.SequenceEqual(File.ReadAllBytes(path))}\n";
        File.WriteAllText(Path.Combine(directory, "png-diagnostic.txt"), report);
        throw new InvalidOperationException("Saved PNG differs from the clean annotation export: " + report);
    }
    private static byte[] StraightBytes(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }
    private static TextBox RequireInput(ScreenshotEditor editor) => editor.TextInput ?? throw new InvalidOperationException("Text editor input was not created");
    private static BitmapSource RenderSurface(AnnotationSurface surface)
    {
        bool before = surface.ShowSelection; surface.ShowSelection = false; surface.InvalidateVisual(); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)surface.Width, (int)surface.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        surface.ShowSelection = before; surface.InvalidateVisual(); return bitmap;
    }
    private static byte[] Bytes(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0); var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }
    private static T Find<T>(DependencyObject root, Predicate<T> predicate) where T : DependencyObject
    {
        if (FindOrNull(root, predicate) is { } item) return item; throw new InvalidOperationException($"Real editor control {typeof(T).Name} not found");
    }
    private static T? FindOrNull<T>(DependencyObject root, Predicate<T> predicate) where T : DependencyObject
    {
        if (root is T item && predicate(item)) return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (FindOrNull(VisualTreeHelper.GetChild(root, i), predicate) is { } found) return found;
        return null;
    }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
