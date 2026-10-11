using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal static class VideoTextTests
{
    private static void Ensure(bool value, string message) => UpgradeTests.Ensure(value, message);
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "VideoText", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var tools = await MediaTools.DetectAsync(new Settings());
        string png = Path.Combine(directory, "notes.png"), source = Path.Combine(directory, "notes.mp4");
        Notes(png);
        var fixture = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-loop", "1", "-i", png, "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-t", "4", "-r", "30", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, CancellationToken.None);
        Ensure(fixture.ExitCode == 0, fixture.Error);
        var info = await VideoExportService.ProbeAsync(source, tools);
        var text = new VideoObject("text", VideoObjectKind.Text, 180, 338, 700, 130, .6, 3, 0, Color: "#FF5268", Text: "中文与 English\n让每一帧更清楚", FontSize: 48, Bold: true).FitText(info);
        await check("Text shares screenshot CJK wrapping and alignment; original-frame boundaries and clean render", () =>
        {
            var rawInfo = new VideoInfo(4, 960, 540, 30, false); var original = UpgradeTests.Bytes(UpgradeTests.TestPixels(960, 540));
            var item = text with { X = 70, Y = 70, Width = 260, Color = "#FFFF00", FontFamily = "Arial", FontSize = 32, Alignment = TextAlignment.Center, Text = "中文 English 自动换行\n多行文字" }; item = item.FitText(rawInfo);
            var surface = new AnnotationSurface(UpgradeTests.TestPixels(960, 540)); surface.Add(item.TextAnnotation() with { Start = new(item.X, item.Y), End = new(item.X, item.Y) });
            byte[] expected = UpgradeTests.Bytes(surface.Export()), actual = original.ToArray();
            VideoObjectRenderer.Render(actual, rawInfo, new[] { item }, .6, new System.Collections.Generic.Dictionary<string, SkiaSharp.SKBitmap>());
            int changed = 0, deviations = 0;
            for (int i = 0; i < actual.Length; i++) { if (actual[i] != original[i]) changed++; if (Math.Abs(actual[i] - expected[i]) > 2) deviations++; }
            Ensure(changed > 500 && deviations < 50, "Text render differs from screenshot fallback/wrapping: " + deviations);
            foreach (double time in new[] { .6 - 1 / 30d, 3d }) { var clean = original.ToArray(); VideoObjectRenderer.Render(clean, rawInfo, new[] { item }, time, new System.Collections.Generic.Dictionary<string, SkiaSharp.SKBitmap>()); Ensure(clean.SequenceEqual(original), "Text leaked beyond its frame range"); }
            Ensure(item.Active(3 - 1 / 30d, 30) && !item.Active(3, 30), "Last effective text frame omitted");
            var input = InPlaceTextEditor.CreateInput(); Ensure(input.AcceptsReturn && InputMethod.GetIsInputMethodEnabled(input) && input.Padding == new Thickness(0), "Shared screenshot text input contract changed");
            return Task.CompletedTask;
        });
        var state = new RecordingEditState(0, 4, new[] { new VideoRange(1.2, 1.8) }, Speed: 1.5, Crop: new(100, 80, 720, 400), Objects: new[] { text });
        await check("Text project v3 saves full editable style and ranges; v2 compatibility and original time mapping", async () =>
        {
            await RecordingEditStore.SaveAsync(source, state); var stored = (await RecordingEditStore.LoadAsync(source))!;
            Ensure(JsonSerializer.Serialize(stored.Objects) == JsonSerializer.Serialize(state.Objects), "Text fields lost during restore");
            using (var doc = JsonDocument.Parse(File.ReadAllText(RecordingEditStore.FileFor(source)))) Ensure(doc.RootElement.GetProperty("Version").GetInt32() == 3, "Text project wasn't versioned safely");
            Ensure(Math.Abs(VideoObjectRules.SourceTime(1, state) - 2.1) < .00001, "Cut/speed source mapping wrong");
            await RecordingEditStore.SaveAsync(source, state with { Objects = null });
            using (var doc = JsonDocument.Parse(File.ReadAllText(RecordingEditStore.FileFor(source)))) Ensure(doc.RootElement.GetProperty("Version").GetInt32() == 2, "Legacy project version changed unnecessarily");
            Ensure((await RecordingEditStore.LoadAsync(source))!.Objects == null, "Legacy no-object project rejected");
            bool bad = false; try { (text with { FontSize = double.PositiveInfinity }).Validate(info); } catch (InvalidDataException) { bad = true; } Ensure(bad, "Unsafe text size accepted");
            await RecordingEditStore.SaveAsync(source, state);
        });
        await check("MP4 WebM GIF write text with crop, speed, middle deletion, mute and first/last frame boundaries", async () =>
        {
            foreach (var format in Enum.GetValues<RecordingFormat>())
            {
                int fps = format == RecordingFormat.Gif ? 15 : 30;
                string target = Path.Combine(directory, "text." + VideoExportService.Extension(format));
                await VideoExportService.ExportAsync(new(source, target, format, ExportQuality.High, 0, 4, 1.5, format == RecordingFormat.WebM, 15, Crop: state.Crop, FramesPerSecond: fps, Deleted: state.Deleted, Objects: new[] { text }), info, tools);
                var output = await VideoExportService.ProbeAsync(target, tools);
                Ensure(output.Width == 720 && output.Height == 400 && Math.Abs(output.Duration - 3.4 / 1.5) < .16, "Export crop/time dimensions wrong for " + format);
                Ensure(format == RecordingFormat.Mp4 ? output.HasAudio : !output.HasAudio, "Export audio mode wrong for " + format);
                using var reader = new VideoFrameReader(tools.Ffmpeg!, target, 720, 400, fps);
                foreach (var (time, visible) in new[] { (0d, false), (.4, true), (1.53, true), (1.67, false) })
                {
                    var pixels = await reader.FrameAsync(time, CancellationToken.None); Ensure(pixels != null, "Export frame missing");
                    int red = 0;
                    for (int y = 252; y < 385; y++) for (int x = 65; x < 700; x++) { int p = (y * 720 + x) * 4; if (pixels![p + 2] > 180 && pixels[p + 1] < 145 && pixels[p] < 170) red++; }
                    Ensure(visible ? red > 500 : red < 30, $"{format}: text position/time wrong at {time}, red pixels={red}");
                }
            }
        });
        await check("Embedded toolbar alignment, hidden/wrapping properties, in-place CJK composition guards and text history/reopen", async () =>
        {
            await RecordingEditStore.SaveAsync(source, state with { Crop = null, Speed = 1, Deleted = Array.Empty<VideoRange>(), Objects = Array.Empty<VideoObject>() });
            var owner = new Window(); owner.Show(); RecordingEditWindow? dialog = null;
            try
            {
                dialog = new(owner, source, new Settings { OutputDirectory = directory }, new(ExportQuality.High, 30, true, 4)); dialog.Show(); await dialog.Initialization; await Task.Delay(400);
                Ensure(dialog.ObjectEditor.Toolbar.PropertyPanel.Visibility == Visibility.Collapsed, "No-tool property row consumes height");
                dialog.ObjectEditor.SetTool(VideoObjectKind.Text); Ensure(dialog.ObjectEditor.Tracks.VisibleCategories.SequenceEqual(new[] { 4 }), "Choosing text didn't expose its track");
                dialog.NavigatePreview(.6); dialog.ObjectEditor.BeginText(new(180, 338), null);
                var input = dialog.ObjectEditor.TextEditor.Input; input.Text = text.Text;
                dialog.ObjectEditor.TextEditor.Update(dialog.ObjectEditor.TextEditor.Draft! with { FontSize = 48, Width = 700, Bold = true });
                var composition = new TextComposition(InputManager.Current, input, "中");
                input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
                Ensure(dialog.ObjectEditor.TextEditor.Composing && !dialog.ObjectEditor.CommitText(), "IME candidate selection prematurely confirmed text");
                input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input)!, Environment.TickCount, Key.B) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Ensure(dialog.ObjectEditor.TextEditor.Active && dialog.ObjectEditor.Overlay.Tool == VideoObjectKind.Text, "Text input activated a tool shortcut");
                input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent });
                // WPF inserts the committed candidate through its real TextBox handler.
                input.Text = text.Text;
                Ensure(dialog.ObjectEditor.CommitText() && dialog.ObjectEditor.Objects.Length == 1, "CJK/multiline confirmation didn't add exactly one object");
                Ensure(dialog.ObjectEditor.Objects[0].Active(dialog.PreviewPosition, info.Fps), "Confirmed text disappeared before its first effective frame");
                var item = dialog.ObjectEditor.Objects[0]; dialog.UndoEdit(); Ensure(dialog.ObjectEditor.Objects.Length == 0 && !dialog.ObjectEditor.Tracks.VisibleCategories.Contains(4), "Single-step text undo failed"); dialog.RedoEdit(); Ensure(dialog.ObjectEditor.Objects[0].Text == text.Text, "Text redo lost lines");
                item = dialog.ObjectEditor.Objects[0]; dialog.ObjectEditor.Replace(item with { X = 160, Width = 500, Alignment = TextAlignment.Right }, true); dialog.UndoEdit(); Ensure(dialog.ObjectEditor.Objects[0].X == item.X, "Text move/style undo failed"); dialog.RedoEdit();
                dialog.ObjectEditor.Select(dialog.ObjectEditor.Objects[0], false); dialog.UpdateLayout();
                var alignment = Find<ComboBox>(dialog.ObjectEditor.Toolbar.PropertyPanel).Single(c => (string?)c.ToolTip == "文字对齐");
                Ensure((string?)alignment.SelectedItem == "右对齐", "Alignment label differs from the object's actual style");
                alignment.SelectedIndex = 1; Ensure(dialog.ObjectEditor.Objects[0].Alignment == TextAlignment.Center, "Center property doesn't apply center alignment"); alignment.SelectedIndex = 2; dialog.ObjectEditor.FlushProperties();
                item = dialog.ObjectEditor.Objects[0]; dialog.ObjectEditor.BeginText(new(item.X, item.Y), item); input.Text = "取消修改"; dialog.ObjectEditor.TextEditor.Cancel(); Ensure(dialog.ObjectEditor.Objects[0].Text == text.Text, "Cancel modified existing text");
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    ThemeService.Apply(theme); dialog.Width = 1120; dialog.Height = Math.Min(1040, SystemParameters.WorkArea.Height - 24);
                    item = dialog.ObjectEditor.Objects[0]; dialog.ObjectEditor.BeginText(new(item.X, item.Y), item); dialog.UpdateLayout(); await Task.Delay(600); dialog.UpdateLayout();
                    var toolbar = dialog.ObjectEditor.Toolbar;
                    Ensure(Math.Abs(toolbar.MainPanel.ActualWidth - (dialog.Timeline.ActualWidth - 16)) < 1 && toolbar.PropertyPanel.ActualWidth <= toolbar.MainPanel.ActualWidth + 1, "Toolbar/property/timeline widths differ");
                    Ensure(Math.Abs(toolbar.MainPanel.TranslatePoint(new(0, 0), dialog).X - dialog.Timeline.TranslatePoint(new(8, 0), dialog).X) < 1, "Toolbar isn't aligned with the timeline rail");
                    Ensure(toolbar.MainPanel.Effect == null && toolbar.PropertyPanel.Effect == null && dialog.ObjectEditor.TranslatePoint(new(0, 0), dialog).Y < dialog.Timeline.TranslatePoint(new(0, 0), dialog).Y, "Toolbar floats or overlaps timeline");
                    UiChangeTests.Render(dialog, "video-text-" + theme.ToLowerInvariant() + ".png"); dialog.ObjectEditor.TextEditor.Cancel();
                }
                ThemeService.Apply("Light"); dialog.Width = 720; dialog.Height = Math.Min(980, SystemParameters.WorkArea.Height - 24); dialog.ObjectEditor.Select(dialog.ObjectEditor.Objects[0], false); await Task.Delay(350); dialog.UpdateLayout();
                Ensure(dialog.ObjectEditor.Objects[0].Active(dialog.PreviewPosition, info.Fps), "Repeated pause/re-edit changed the requested source frame");
                Ensure(dialog.ObjectEditor.Toolbar.PropertyPanel.ActualHeight > 64, "Narrow properties didn't wrap");
                foreach (var control in Find<Control>(dialog.ObjectEditor.Toolbar.PropertyPanel).Where(c => c.IsVisible && c is TextBox or ComboBox or Button))
                { var point = control.TranslatePoint(new(control.ActualWidth, control.ActualHeight), dialog.ObjectEditor.Toolbar.PropertyPanel); Ensure(point.X <= dialog.ObjectEditor.Toolbar.PropertyPanel.ActualWidth + 1 && point.Y <= dialog.ObjectEditor.Toolbar.PropertyPanel.ActualHeight + 1, "Narrow property control clipped"); }
                UiChangeTests.Render(dialog, "video-text-narrow.png");
                dialog.ObjectEditor.Select(dialog.ObjectEditor.Objects[0], false); dialog.ObjectEditor.Delete(); Ensure(!dialog.ObjectEditor.Tracks.VisibleCategories.Contains(4) && dialog.ObjectEditor.Toolbar.PropertyPanel.Visibility == Visibility.Collapsed, "Deleted last text left track/properties"); dialog.UndoEdit();
                dialog.ObjectEditor.Select(null, false); dialog.UpdateLayout(); UiChangeTests.Render(dialog, "video-text-properties-hidden.png");
                await dialog.ConfigurationSaved; dialog.Close(); dialog = new(owner, source, new Settings { OutputDirectory = directory }, new(ExportQuality.High, 30, true, 4)); dialog.Show(); await dialog.Initialization;
                Ensure(dialog.ObjectEditor.Objects.Single().Text == text.Text && dialog.ObjectEditor.Objects.Single().Alignment == TextAlignment.Right, "Reopen lost text/width/style");
                dialog.BeginCrop(); dialog.CropOverlay.SetSelection(new(100, 80, 720, 400)); dialog.ConfirmCrop(); dialog.SetCuts(new[] { new VideoRange(1.2, 1.8) });
                var speed = UpgradeTests.Find<ComboBox>(dialog, b => b.Name == "ExportSpeed"); speed.SelectedIndex = 2;
                dialog.NavigatePreview(-5); Ensure(Math.Abs(dialog.PreviewPosition) < .001, "Fast rewind didn't clamp to kept start");
                dialog.NavigatePreview(1.1666666667, true); dialog.NavigatePreview(1 / 30d, true); Ensure(Math.Abs(dialog.PreviewPosition - 1.8) < .001, "Frame forward didn't skip deleted segment");
                dialog.NavigatePreview(-1 / 30d, true); Ensure(Math.Abs(dialog.PreviewPosition - 1.1666666667) < .001, "Frame backward didn't skip deleted segment");
                dialog.NavigatePreview(5); Ensure(Math.Abs(dialog.PreviewPosition - (4 - 1 / 30d)) < .001, "Fast forward didn't clamp to kept last frame");
                Ensure(dialog.ObjectEditor.Overlay.View == new Rect(100, 80, 720, 400), "Crop source view lost text mapping");
                dialog.ObjectEditor.SetTool(VideoObjectKind.Mosaic); Ensure(dialog.ObjectEditor.Toolbar.PropertyPanel.Visibility == Visibility.Visible, "Mosaic properties disappeared"); dialog.ObjectEditor.SetTool(null);
                string asset = await RecordingEditStore.ImportAssetAsync(source, png, CancellationToken.None);
                dialog.ObjectEditor.Add(new("pip-feedback", VideoObjectKind.PictureInPicture, 100, 100, 192, 108, 0, 4, 1, Asset: asset, AssetWidth: 960, AssetHeight: 540));
                dialog.ObjectEditor.Add(new("pip-second", VideoObjectKind.PictureInPicture, 350, 100, 192, 108, 0, 4, 2, Asset: asset, AssetWidth: 960, AssetHeight: 540));
                Ensure(dialog.ObjectEditor.Objects.Count(o => o.Kind == VideoObjectKind.PictureInPicture) == 2 && dialog.ObjectEditor.Tracks.RowCount >= 3, "Multiple PIP objects lack independent tracks");
                dialog.ObjectEditor.SetTool(VideoObjectKind.Mosaic); Ensure(!Find<TextBlock>(dialog.ObjectEditor).Any(t => t.IsVisible && t.Text.Contains("支持一个画中画")), "Previous tool feedback remains after switch");
            }
            finally { dialog?.ObjectEditor.TextEditor.Cancel(); dialog?.Close(); owner.Close(); }
        });
        File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { source, formats = new[] { "mp4", "webm", "gif" }, NativeImeInputTested = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static void Notes(string path)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            Brush B(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            void T(string value, double x, double y, double size, bool bold = false) => dc.DrawText(AnnotationGeometry.Text(new(AnnotationTool.Text, new(), new(), (Color)ColorConverter.ConvertFromString("#284E55"), value, FontSize: size, Bold: bold), B("#284E55")), new(x, y));
            dc.DrawRectangle(B("#EBF3F4"), null, new(0, 0, 960, 540)); dc.DrawRoundedRectangle(Brushes.White, new Pen(B("#DCE8E9"), 1), new(43, 31, 874, 478), 18, 18);
            T("轻截使用笔记", 78, 63, 34, true); T("QUICKCAPTURE / SCREEN NOTES", 80, 115, 12); dc.DrawLine(new Pen(B("#EAF1F2"), 2), new(80, 146), new(880, 146));
            T("把屏幕内容保存成清楚的说明", 80, 169, 23, true); T("01   框选区域、选择窗口，或截取整个桌面。", 80, 225, 20); T("02   添加形状、画笔、马赛克和文字。", 80, 275, 20); T("03   录屏剪辑后导出，还能继续编辑。", 80, 325, 20);
        }
        var bitmap = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static System.Collections.Generic.IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    { if (root is T value) yield return value; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
}
