using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;

namespace QuickCapture;

// Narrow integration coverage for the new style channels and input transaction.
// Routed input and picker Press/Release exercise state transitions, not physical mouse input.
internal static class ToolStyleTests
{
    private static void Ensure(bool value, string message) => UpgradeTests.Ensure(value, message);
    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t) yield return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => UpgradeTests.Find<T>(root, c => c.Name == name);
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void MouseEvent(UIElement element, RoutedEvent type) => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = type });
    private static void Presets(DependencyObject root)
    {
        var widths = All<ComboBox>(root).Where(c => c.ToolTip?.ToString()?.Contains("粗细") == true).ToArray(); Ensure(widths.Length > 0, "No thickness controls");
        foreach (var width in widths) { Ensure(width.IsEditable && width.Items.Cast<string>().SequenceEqual(new[] { "1", "4", "8", "16", "24", "32", "48", "64", "72" }), "Preset/custom width contract differs"); Ensure(width.Template.FindName("PART_EditableTextBox", width) is TextBox input && input.Text == width.Text && input.Foreground == width.Foreground, "Editable width is hidden or unthemed"); }
    }
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "ToolStyle"); Directory.CreateDirectory(directory);
        var info = new VideoInfo(6, 640, 360, 30, true);
        var brush = new VideoObject("brush", VideoObjectKind.Brush, 30, 30, 180, 50, 0, 6, 0, Color: "#FF0000", Stroke: 16, Points: new[] { new VideoPoint(30, 30), new VideoPoint(140, 30), new VideoPoint(210, 80) }, OutlineEnabled: true, OutlineColor: "#0000FF", OutlineWidth: 8);
        var text = new VideoObject("text", VideoObjectKind.Text, 260, 24, 330, 100, 0, 6, 1, Color: "#00FF00", Text: "中文 English\n粗体与斜体", FontFamily: "Arial", FontSize: 32, Bold: true, Italic: true, OutlineEnabled: true, OutlineColor: "#000000", OutlineWidth: 4).FitText(info);
        var mosaic = new VideoObject("mosaic", VideoObjectKind.Mosaic, 30, 210, 180, 80, 0, 6, 2, Stroke: 24, Points: new[] { new VideoPoint(30, 210), new VideoPoint(130, 210), new VideoPoint(210, 290) }, BlockSize: 16, MosaicBrush: true);
        var shape = new VideoObject("shape", VideoObjectKind.Ellipse, 320, 220, 130, 90, 0, 6, 3, Color: "#FF0000", Fill: "#00FF00", Stroke: 8);
        var objects = new[] { brush, text, mosaic, shape };
        await check("Body/outline, italic CJK text and mosaic path match screenshot pixels; legacy styles and screenshot projects roundtrip", async () =>
        {
            var image = UpgradeTests.TestPixels(640, 360); byte[] original = UpgradeTests.Bytes(image), actual = original.ToArray();
            var surface = new AnnotationSurface(image);
            surface.Add(new(AnnotationTool.Freehand, new(brush.X, brush.Y), new(210, 80), Colors.Red, Points: brush.Points!.Select(p => new Point(p.X, p.Y)).ToArray(), Width: brush.Stroke, OutlineEnabled: true, OutlineColor: Colors.Blue, OutlineWidth: 8));
            surface.Add(text.TextAnnotation() with { Start = new(text.X, text.Y), End = new(text.X, text.Y) });
            surface.Add(new(AnnotationTool.MosaicBrush, new(30, 210), new(210, 290), Colors.Red, Points: mosaic.Points!.Select(p => new Point(p.X, p.Y)).ToArray(), Width: 24, BlockSize: 16));
            surface.Add(new(AnnotationTool.Rectangle, new(320, 220), new(450, 310), Colors.Red, Width: 8, Shape: AnnotationShape.Ellipse, FillColor: Colors.Lime));
            VideoObjectRenderer.Render(actual, info, objects, .5, new Dictionary<string, SKBitmap>());
            byte[] expected = UpgradeTests.Bytes(surface.Export()); int changed = 0, deviations = 0;
            for (int p = 0; p < actual.Length; p++) { if (actual[p] != original[p]) changed++; if (Math.Abs(actual[p] - expected[p]) > 12) deviations++; }
            Ensure(changed > 3000 && deviations < 2200, "Shared raster/brush/mosaic mismatch: " + deviations);
            File.WriteAllText(Path.Combine(directory, "renderer-metrics.json"), JsonSerializer.Serialize(new { changed, deviations, tolerance = 12 }));
            Ensure(surface.SelectAt(new(100, 44), 0) && surface.Selected?.Tool == AnnotationTool.Freehand, "Brush outline cannot be selected"); surface.Deselect();
            int body = (30 * 640 + 100) * 4, outline = (42 * 640 + 100) * 4, clean = (270 * 640 + 70) * 4;
            Ensure(actual[body + 2] > 240 && actual[body] < 20 && actual[outline] > 240 && actual[outline + 2] < 20, "Body/outline are not independent");
            Ensure(actual.Skip(clean).Take(4).SequenceEqual(original.Skip(clean).Take(4)), "Mosaic filled the path bounding rectangle");
            var legacy = JsonSerializer.Deserialize<VideoObject>("{\"Id\":\"old\",\"Kind\":4,\"X\":0,\"Y\":0,\"Width\":1,\"Height\":1,\"Start\":0,\"End\":6,\"Layer\":0,\"Color\":\"#FF0000\",\"Stroke\":6,\"Points\":[{\"X\":0,\"Y\":0}]} ")!;
            Ensure(!legacy.Italic && !legacy.OutlineEnabled && legacy.Stroke == 6 && legacy.Color == "#FF0000", "Legacy brush appearance changed"); legacy.Validate(info);
            var invisible = original.ToArray(); VideoObjectRenderer.Render(invisible, info, new[] { brush with { OutlineEnabled = false, StrokeEnabled = false } }, .5, new Dictionary<string, SKBitmap>()); Ensure(invisible.SequenceEqual(original), "Legacy disabled brush became visible");
            string saved = Path.Combine(directory, "styled.png"); await ScreenshotProjects.SaveAsync(ScreenshotProjects.Snapshot(surface, 1, ScreenshotFormat.Png, 90), surface.Export(), saved, ScreenshotFormat.Png, 90, true);
            var reopened = await ScreenshotProjects.LoadAsync(saved); Ensure(JsonSerializer.Serialize(reopened.Annotations) == JsonSerializer.Serialize(surface.Items), "Screenshot style channels did not persist");
        });
        await check("New and reopened screenshot editors preserve presets, separate channels and full picker click before apply/resume", async () =>
        {
            foreach (bool reopen in new[] { false, true })
            {
                var image = UpgradeTests.TestPixels(640, 360); var window = new EditorWindow(image, new Settings(), _ => { }, reopen ? new ScreenshotProject(image, Array.Empty<Annotation>(), new(0, 0, 640, 360)) : null); window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                try
                {
                    var editor = window.Editor; editor.SelectTool(AnnotationTool.Rectangle); window.UpdateLayout();
                    Presets(editor.OptionsFor(AnnotationTool.Rectangle)); Ensure(!All<Button>(editor.OptionsFor(AnnotationTool.Rectangle)).Any(b => b.ToolTip?.ToString()?.StartsWith("#") == true), "Shape quick colors remain");
                    editor.ShowColors(); editor.Palette.PreviewColor(Colors.Lime); Click(Named<Button>(editor.Palette, "PaletteApply"));
                    editor.SelectTool(AnnotationTool.Freehand); window.UpdateLayout(); Presets(editor.OptionsFor(AnnotationTool.Freehand)); Ensure(!All<Button>(editor.OptionsFor(AnnotationTool.Freehand)).Any(b => b.ToolTip?.ToString() == "#FF0000"), "Property quick colors remain");
                    var width = All<ComboBox>(editor.OptionsFor(AnnotationTool.Freehand)).First(c => c.ToolTip?.ToString()?.StartsWith("画笔粗细") == true); width.Text = "12.5";
                    editor.ShowColors(); editor.Palette.PreviewColor(Color.FromArgb(128, 0, 255, 0)); Click(Named<Button>(editor.Palette, "PaletteEyedropper")); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var picker = (PixelEyedropper)typeof(ScreenshotEditor).GetField("_eyedropper", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
                    Ensure(editor.PickingColor && editor.ColorEditing, "Picker activation failed");
                    MouseEvent(editor.Surface, Mouse.MouseDownEvent); Ensure(editor.Surface.Preview == null, "Picking initiated a brush");
                    picker.Press(new(100, 100)); Ensure(editor.PickingColor, "Mouse down confirmed too early"); picker.Release(new(100, 100));
                    Ensure(!editor.PickingColor && editor.ColorEditing && editor.Palette.Draft.A == 128, "Release committed/lost alpha");
                    MouseEvent(editor.Surface, Mouse.MouseUpEvent); Ensure(editor.Surface.Count == 0, "Picker release created annotation");
                    MouseEvent(editor.Surface, Mouse.MouseDownEvent); Ensure(editor.Surface.Preview == null, "Palette draft allows drawing");
                    Click(Named<Button>(editor.Palette, "PaletteApply")); MouseEvent(editor.Surface, Mouse.MouseUpEvent); Ensure(editor.Surface.Count == 0, "Apply release leaked into drawing");
                    MouseEvent(editor.Surface, Mouse.MouseDownEvent); Ensure(editor.Surface.Preview?.Width == 12.5, "Fresh gesture did not resume/custom width lost: width=" + editor.Surface.Preview?.Width + "; tool=" + editor.Tool + "; editing=" + editor.ColorEditing + "; picking=" + editor.PickingColor); MouseEvent(editor.Surface, Mouse.MouseUpEvent); Ensure(editor.Surface.Count == 1, "Fresh brush was not created");
                    editor.Undo(); Ensure(editor.Surface.Count == 0, "Brush undo failed"); editor.Redo(); Ensure(editor.Surface.Count == 1, "Brush redo failed");
                    editor.SelectTool(AnnotationTool.Text); window.UpdateLayout(); Presets(editor.OptionsFor(AnnotationTool.Text));
                    editor.BeginText(new(40, 50)); editor.TextInput!.Text = "中文 English\n多行文字"; Click(All<Button>(editor.OptionsFor(AnnotationTool.Text)).First(b => (string?)b.ToolTip == "粗体")); Click(All<Button>(editor.OptionsFor(AnnotationTool.Text)).First(b => (string?)b.ToolTip == "斜体"));
                    var toggle = All<CheckBox>(editor.OptionsFor(AnnotationTool.Text)).First(c => (string?)c.Content == "描边"); toggle.IsChecked = true; editor.ConfirmText(); Ensure(editor.Surface.Items.Last().Bold && editor.Surface.Items.Last().Italic && editor.Surface.Items.Last().OutlineEnabled, "Text decorations not committed");
                    foreach (string theme in new[] { "Light", "Dark" }) { ThemeService.Apply(theme); window.Width = 680; window.Height = 620; editor.SelectTool(AnnotationTool.Freehand); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); UiChangeTests.Render(window, "tool-style-screenshot-" + (reopen ? "reopened-" : "new-") + theme + ".png"); }
                }
                finally { window.Close(); Mouse.Capture(null); }
            }
        });
        var tools = await MediaTools.DetectAsync(new Settings()); string source = Path.Combine(directory, "source.mp4");
        if (!File.Exists(source)) { var generated = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=6", "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, CancellationToken.None); Ensure(generated.ExitCode == 0, generated.Error); }
        info = await VideoExportService.ProbeAsync(source, tools);
        await check("Recording panels use timeline ranges, bounded styles, five PIP icons and correct mute state; raw picker remains transactional", async () =>
        {
            string edit = RecordingEditStore.FileFor(source); if (File.Exists(edit)) File.Delete(edit);
            var owner = new Window { Width = 200, Height = 120, ShowInTaskbar = false }; owner.Show();
            var dialog = new RecordingEditWindow(owner, source, new Settings(), new(ExportQuality.High, 30, true, 6)); dialog.Show(); await dialog.Initialization; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try
            {
                var editor = dialog.ObjectEditor; editor.Add(shape); dialog.UpdateLayout(); Presets(editor.Toolbar.PropertyPanel);
                Ensure(!All<TextBlock>(editor.Toolbar.PropertyPanel).Any(t => t.Text is "起" or "止" or "填充颜色"), "Old shape/time controls remain"); Ensure(!All<Button>(editor.Toolbar.PropertyPanel).Any(b => (string?)b.ToolTip == "红 #FF0000"), "Shape quick colors remain");
                Click(Named<Button>(dialog, "VideoPrimaryColor")); Ensure(editor.Palette!.EditingFill, "Shape color is not body fill"); editor.Palette.PreviewColor(Color.FromArgb(128, 0, 255, 0));
                await editor.StartFramePicker(); Ensure(editor.PickingColor && editor.Overlay.InputBlocked!(), "Video picker did not block drawing"); editor.Eyedropper!.Press(new(editor.Overlay.ActualWidth / 2, editor.Overlay.ActualHeight / 2)); Ensure(editor.PickingColor, "Video down confirmed early"); editor.Eyedropper.Release(new(editor.Overlay.ActualWidth / 2, editor.Overlay.ActualHeight / 2));
                Ensure(editor.ColorEditing && !editor.PickingColor && editor.Palette.Draft.A == 128, "Video picked color committed early"); editor.FinishColor(false); Ensure(editor.Objects.Single() == shape, "Video cancel did not rollback");
                editor.Replace(shape with { Fill = "none" }, true); dialog.UpdateLayout(); Click(Named<Button>(dialog, "VideoPrimaryColor")); Ensure(editor.Palette!.EditingFill && editor.Palette.Draft.A == 255, "No-fill body picker starts transparent"); editor.Palette.PreviewColor(Colors.Blue); editor.FinishColor(true); Ensure(editor.Objects.Single().Fill == "#0000FF" && editor.Objects.Single().Color == shape.Color, "No-fill body color changed outline"); editor.Replace(shape, true);
                var pending = new TaskCompletionSource<BitmapSource>(); var provider = editor.FrameProvider; editor.FrameProvider = () => pending.Task; editor.OpenColor(Named<Button>(dialog, "VideoPrimaryColor"), shape, true, "形状颜色"); var picking = editor.StartFramePicker(); editor.CancelPicker(); pending.SetResult(UpgradeTests.TestPixels(640, 360)); await picking; Ensure(!editor.PickingColor && editor.Eyedropper?.Active != true, "Cancelled frame request reactivated"); editor.FrameProvider = provider; editor.FinishColor(false);
                dialog.BeginSpeed(); dialog.Timeline.MoveDraftBoundary(true, 2); dialog.Timeline.MoveDraftBoundary(false, 3); dialog.ConfirmSpeed(2); await dialog.ConfigurationSaved; var saved = (await RecordingEditStore.LoadAsync(source))!; Ensure(saved.SpeedRanges!.Single() == new VideoSpeedRange(2, 3, 2), "Timeline speed endpoints changed");
                dialog.BeginDelete(); dialog.UpdateLayout(); Ensure(!All<ComboBox>(dialog.ObjectEditor.Toolbar.PropertyPanel).Any(c => c.IsVisible) && !All<Button>(dialog.ObjectEditor.Toolbar.PropertyPanel).Any(b => b.IsVisible && b.Content is string t && t is "调整边界" or "恢复片段" or "保存配置"), "Deleted lower row remains"); UiChangeTests.Render(dialog, "tool-style-delete.png"); dialog.CancelDelete();
                var pip = new VideoObject("pip", VideoObjectKind.PictureInPicture, 0, 0, 120, 80, 0, 6, 4, Asset: "missing.png", AssetWidth: 120, AssetHeight: 80); editor.Add(pip); dialog.UpdateLayout(); Ensure(All<Button>(editor.Toolbar.PropertyPanel).Count(b => b.ToolTip?.ToString()?.StartsWith("画中画位置：") == true) == 5, "PIP positions missing");
                var centered = VideoObjectEditor.PositionPicture(pip, new(40, 30, 400, 250), info, 0, 0); Ensure(centered.X == 180 && centered.Y == 115, "Crop-aware center is wrong"); editor.Delete();
                var mute = All<Button>(dialog).First(b => b.ToolTip?.ToString() == "当前声音开启，点击静音。"); Click(mute); Ensure((string?)mute.ToolTip == "当前已静音，点击恢复声音。", "Mute state/action wrong"); Click(mute);
                foreach (var item in new[] { brush, text, mosaic }) editor.Add(item);
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    ThemeService.Apply(theme); dialog.Width = 680; dialog.Height = 650;
                    foreach (var item in new[] { brush, text, mosaic }) { editor.Select(editor.Objects.First(o => o.Id == item.Id), false); dialog.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Presets(editor.Toolbar.PropertyPanel); if (item.Kind == VideoObjectKind.Mosaic) { var block = All<ComboBox>(editor.Toolbar.PropertyPanel).First(c => c.ToolTip?.ToString()?.StartsWith("像素块：") == true); Ensure(block.Template.FindName("PART_EditableTextBox", block) is TextBox input && input.Text == "16", "Pixel block value is hidden"); } UiChangeTests.Render(dialog, "tool-style-video-" + item.Id + "-" + theme + ".png"); }
                    var body = Named<ScrollViewer>(dialog, "RecordingScroll"); Ensure(body.ExtentWidth <= body.ViewportWidth + 1, "Narrow editor overflows"); Ensure(Named<Button>(dialog, "ExportRecording").IsVisible, "Export button unreachable");
                }
                await dialog.ConfigurationSaved;
            }
            finally { typeof(RecordingEditWindow).GetField("_closeApproved", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dialog, true); dialog.Close(); owner.Close(); ThemeService.Apply("Light"); }
        });
        await check("One MP4 export and project reopen retain outlines, italic, path mosaic and speed/cut timing", async () =>
        {
            var state = new RecordingEditState(0, 6, new[] { new VideoRange(1, 1.5) }, Objects: objects, SpeedRanges: new[] { new VideoSpeedRange(2, 3, 2) }); await RecordingEditStore.SaveAsync(source, state); var restored = (await RecordingEditStore.LoadAsync(source))!;
            Ensure(JsonSerializer.Serialize(state) == JsonSerializer.Serialize(restored), "New styles did not roundtrip"); Ensure(JsonDocument.Parse(File.ReadAllText(RecordingEditStore.FileFor(source))).RootElement.GetProperty("Version").GetInt32() == 5, "New style schema unversioned");
            string target = Path.Combine(directory, "result.mp4"); var request = new VideoExportRequest(source, target, RecordingFormat.Mp4, ExportQuality.High, 0, 6, Overwrite: true, FramesPerSecond: 30, Deleted: state.Deleted, Objects: objects, SpeedRanges: state.SpeedRanges); await VideoExportService.ExportAsync(request, info, tools); var output = await VideoExportService.ProbeAsync(target, tools);
            Ensure(Math.Abs(output.Duration - request.OutputDuration) < .1 && output.HasAudio, "Export timing/audio changed"); using var reader = new VideoFrameReader(tools.Ffmpeg!, target, 640, 360, 30); var frame = (await reader.FrameAsync(.5, CancellationToken.None))!;
            int p = (30 * 640 + 100) * 4, q = (42 * 640 + 100) * 4; Ensure(frame[p + 2] > 220 && frame[q] > 210 && frame[q + 2] < 50, "Export lost brush body or outline");
            File.WriteAllText(Path.Combine(directory, "scope.txt"), "New and reopened screenshot editor; routed WPF input plus actual picker Press/Release state transitions; real WPF narrow light/dark layout at this host DPI; one MP4 export and JSON reopen. Physical native mouse, protected desktop pixel access and mixed-monitor DPI require manual verification.");
        });
    }
}
