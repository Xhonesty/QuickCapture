using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuickCapture;

// Focused regression checks use routed WPF input and generated media, never user files.
internal static class EditorColorFixTests
{
    private static void Ensure(bool value, string message) => UpgradeTests.Ensure(value, message);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
    { try { return UpgradeTests.Find<T>(root, e => e.Name == name); } catch (InvalidOperationException ex) { throw new InvalidOperationException("Missing " + name, ex); } }
    private static void Escape(Window window) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static FrameworkElement PaletteHost(ColorPalette palette) => (FrameworkElement)((FrameworkElement)palette.Parent).Parent;
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "EditorColorFix"); Directory.CreateDirectory(directory);
        await check("Speed bands retain source coordinates through narrow ranges, cuts and trim; ruler labels are truthful", () =>
        {
            var track = new TrimTimeline { Width = 620 }; track.Initialize(9.57, 30);
            track.Measure(new(620, 70)); track.Arrange(new(0, 0, 620, 70));
            track.SetSpeeds(new[] { new VideoSpeedRange(.2, .3, 2), new VideoSpeedRange(.5, 3, .5) }); track.SetDeleted(new[] { new VideoRange(1, 1.5) }); track.UpdateLayout();
            var bands = track.Children.OfType<Button>().Where(b => b.Height == 20).OrderBy(Canvas.GetLeft).ToArray();
            var expected = new[] { new VideoRange(.2, .3), new VideoRange(.5, 1), new VideoRange(1.5, 3) };
            Ensure(bands.Length == expected.Length, "Confirmed speed includes deleted content or loses a band");
            for (int i = 0; i < bands.Length; i++)
            {
                Ensure(Math.Abs(Canvas.GetLeft(bands[i]) - (8 + expected[i].Start / 9.57 * 604)) < .01, "Speed start shifted");
                Ensure(Math.Abs(bands[i].Width - (expected[i].End - expected[i].Start) / 9.57 * 604) < .01, "Caption widened a short band");
            }
            var ticks = track.RulerTicks; Ensure(ticks.Select(t => t.Label).Distinct().Count() == ticks.Count && ticks.All(t => TimeSpan.Parse("00:" + t.Label).TotalSeconds == t.Time), "Ruler label is a truncated fractional timestamp");
            Ensure(track.BeginDraft(new(0, 9.57), new(.2, .3), 9.57, true), "Speed draft refused"); track.UpdateLayout();
            var draft = Named<System.Windows.Shapes.Rectangle>(track, "DraftIntervalBand");
            Ensure(draft.StrokeDashArray.Count > 0 && Math.Abs(draft.Width - .1 / 9.57 * 604) < .01, "Draft styling or geometry wrong");
            track.EndDraft(); track.SetRange(.6, 2.4); track.UpdateLayout();
            Ensure(track.Children.OfType<Button>().Where(b => b.Height == 20).All(b => Canvas.GetLeft(b) >= 8 + .6 / 9.57 * 604 - .01 && Canvas.GetLeft(b) + b.Width <= 8 + 2.4 / 9.57 * 604 + .01), "Speed display extends beyond retained trim");
            return Task.CompletedTask;
        });
        await check("Screenshot capture and reopened project share pure presets, preview/cancel/apply/undo and picker input handoff", async () =>
        {
            var image = UpgradeTests.TestPixels(640, 360);
            var original = new Annotation(AnnotationTool.Rectangle, new(30, 30), new(150, 120), Colors.Red, FillColor: Colors.Blue);
            foreach (bool reopen in new[] { false, true })
            {
                var project = reopen ? new ScreenshotProject(image, new[] { original }, new(0, 0, 640, 360)) : null;
                var window = new EditorWindow(image, new Settings { OutputDirectory = directory }, _ => { }, project); window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                try
                {
                    var editor = window.Editor; if (!reopen) editor.Surface.Add(original);
                    editor.SelectTool(AnnotationTool.Select); Ensure(editor.Surface.SelectAt(new(70, 70)), "Shape selection failed"); editor.ShowColors(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Ensure(editor.Palette.EditingFill && AnnotationColors.Presets.Select(p => AnnotationColors.Hex(p.Color)).SequenceEqual(new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#00FFFF", "#FF00FF", "#FFA500", "#808080", "#FFFFFF", "#000000" }), "Primary fill or pure colors differ");
                    Named<TextBox>(editor.Palette, "PaletteHex").Text = "#8000FF00";
                    Ensure(editor.Surface.Selected!.FillColor == Color.FromArgb(128, 0, 255, 0) && editor.Surface.Selected.Color == Colors.Red, "Fill preview modified stroke");
                    Click(Named<Button>(editor.Palette, "PaletteCancel")); Ensure(editor.Surface.Items.Single() == original, "Screenshot cancel failed");
                    editor.ShowColors(); Named<TextBox>(editor.Palette, "PaletteHex").Text = "#00FF00"; Click(Named<Button>(editor.Palette, "PaletteApply"));
                    Ensure(editor.Surface.Items.Single().FillColor == Colors.Lime, "Screenshot apply failed"); editor.Undo(); Ensure(editor.Surface.Items.Single() == original, "Color apply did not undo in one step"); editor.Redo(); Ensure(editor.Surface.Items.Single().FillColor == Colors.Lime, "Screenshot redo lost color");
                    editor.Surface.SelectAt(new(70, 70)); editor.ShowColors(); editor.Palette.PreviewColor(Color.FromArgb(128, 0, 255, 0));
                    Click(Named<Button>(editor.Palette, "PaletteEyedropper")); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var picker = (PixelEyedropper)typeof(ScreenshotEditor).GetField("_eyedropper", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                    Ensure(editor.PickingColor && !editor.Surface.ShowSelection, "Screenshot picker never received input: " + editor.Palette.Status + "; editing=" + editor.ColorEditing + "; active=" + picker.Active + "; owner=" + window.IsActive + "; capture=" + Mouse.Captured);
                    var expected = UpgradeTests.Bytes(editor.Surface.Export()); int p = (240 * 640 + 400) * 4;
                    Ensure(picker.SampleCanvas(new(400, 240)) == Color.FromRgb(expected[p + 2], expected[p + 1], expected[p]), "Screenshot source sampling wrong");
                    picker.PreviewCanvas(new(400, 240)); picker.Confirm(); Ensure(!editor.PickingColor && editor.ColorEditing && editor.Palette.Draft.A == 128 && editor.Surface.ShowSelection, "Picker confirmation commits early or loses alpha");
                    Click(Named<Button>(editor.Palette, "PaletteEyedropper")); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Escape(window);
                    Ensure(!editor.PickingColor && editor.ColorEditing && window.IsVisible, "First Esc closed the editor/palette instead of cancelling picking");
                    editor.StartEyedropper(true); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Ensure(editor.PickingColor && picker.HasScreenInput, "Screen picker could not receive desktop input: " + editor.Palette.Status); Escape(window);
                    Ensure(!editor.PickingColor && editor.ColorEditing && !picker.HasScreenInput, "Screen picker did not release input on Esc");
                    editor.StartEyedropper(true); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var screenPoint = window.PointToScreen(new(20, 20)); IntPtr hookPoint = Marshal.AllocHGlobal(8);
                    try
                    {
                        Marshal.WriteInt32(hookPoint, 0, (int)screenPoint.X); Marshal.WriteInt32(hookPoint, 4, (int)screenPoint.Y);
                        var result = (IntPtr)typeof(PixelEyedropper).GetMethod("ScreenMouse", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, new object[] { 0, new IntPtr(0x0202), hookPoint })!;
                        Ensure(result == new IntPtr(1), "Screen pixel selection click was not consumed");
                    }
                    finally { Marshal.FreeHGlobal(hookPoint); }
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Ensure(!editor.PickingColor && !picker.HasScreenInput && editor.ColorEditing && editor.Palette.Draft.A == 128, "Screen click handler failed to return to the draft palette or release input");
                    bool screenPixelRead = editor.Palette.Status.StartsWith("已取色", StringComparison.Ordinal);
                    Ensure(screenPixelRead || editor.Palette.Status is "该屏幕画面无法读取，可能受到保护。请使用画布吸管。" or "系统或权限不支持屏幕取色。画布吸管仍可使用。", "Unexpected screen picker result: " + editor.Palette.Status);
                    File.WriteAllText(Path.Combine(directory, "screen-picker-" + (reopen ? "reopened" : "new") + ".json"), JsonSerializer.Serialize(new { desktopPixelSamplePassed = screenPixelRead, message = editor.Palette.Status, input = "Simulated native callback; real desktop mouse input not exercised" }, new JsonSerializerOptions { WriteIndented = true }));
                    editor.Palette.SetStatus("单击预览 · 双击色块应用 · Esc 取消");
                    foreach (string theme in reopen ? new[] { "Light", "Dark" } : new[] { "Light" })
                    {
                        ThemeService.Apply(theme); window.UpdateLayout();
                        UiChangeTests.RenderElement(PaletteHost(editor.Palette), "ui-fix-screenshot-palette-" + theme.ToLowerInvariant() + ".png");
                    }
                    Escape(window); Ensure(!editor.ColorEditing && editor.Surface.Items.Single().FillColor == Colors.Lime, "Second Esc failed to restore color before picking");
                }
                finally { window.Close(); Mouse.Capture(null); }
            }
        });
        var tools = await MediaTools.DetectAsync(new Settings()); string source = Path.Combine(directory, "source.mp4");
        if (!File.Exists(source))
        {
            var generated = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=6", "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, CancellationToken.None); Ensure(generated.ExitCode == 0, generated.Error);
        }
        var info = await VideoExportService.ProbeAsync(source, tools);
        await check("Recording primary shape color, alpha-aware raw-frame picker, pending cancellation and timeline ranges work in bounded layouts", async () =>
        {
            string editFile = RecordingEditStore.FileFor(source); if (File.Exists(editFile)) File.Delete(editFile);
            var owner = new Window { Width = 200, Height = 120, ShowInTaskbar = false }; owner.Show();
            var dialog = new RecordingEditWindow(owner, source, new Settings { OutputDirectory = directory }, new(ExportQuality.High, 30, true, 6)); dialog.Show(); await dialog.Initialization; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try
            {
                var editor = dialog.ObjectEditor; var shape = new VideoObject("shape", VideoObjectKind.Rectangle, 30, 30, 140, 100, 0, 6, 0, Color: "#FF0000", Fill: "#0000FF"); editor.Add(shape); dialog.UpdateLayout();
                Click(Named<Button>(dialog, "VideoPrimaryColor")); Ensure(editor.Palette!.EditingFill, "Solid shape main color did not select fill");
                Named<TextBox>(editor.Palette, "PaletteHex").Text = "#8000FF00"; Ensure(editor.Objects.Single().Fill == "#00FF00" && editor.Objects.Single().Color == "#FF0000", "Primary fill changed stroke");
                Click(Named<Button>(editor.Palette, "PaletteApply")); dialog.UndoEdit(); Ensure(editor.Objects.Single() == shape, "Recording color was not one undo operation"); dialog.RedoEdit(); editor.Select(editor.Objects.Single(), false); dialog.UpdateLayout();
                var rawProvider = editor.FrameProvider; BitmapSource? pickedFrame = null; editor.FrameProvider = async () => pickedFrame = await rawProvider!();
                Click(Named<Button>(dialog, "VideoPrimaryColor")); await editor.StartFramePicker(); Ensure(editor.PickingColor && editor.Eyedropper?.Active == true && !editor.Overlay.ShowSelection, "Raw frame picker did not activate"); editor.FrameProvider = rawProvider;
                var local = new Point(editor.Overlay.ActualWidth * .75, editor.Overlay.ActualHeight * .75); var point = VideoObjectEditor.FramePoint(local, editor.Overlay.View, editor.Overlay.ActualWidth, editor.Overlay.ActualHeight);
                var raw = UpgradeTests.Bytes(pickedFrame!); int rawPixel = ((int)point.Y * info.Width + (int)point.X) * 4;
                Ensure(editor.Eyedropper!.SampleCanvas(point) == Color.FromRgb(raw[rawPixel + 2], raw[rawPixel + 1], raw[rawPixel]), "Eyedropper sampled the composed shape instead of the exact raw-frame snapshot");
                editor.Eyedropper!.PreviewCanvas(local); editor.Eyedropper.Confirm(); Ensure(editor.ColorEditing && !editor.PickingColor && editor.Palette!.Draft.A == 128, "Recording picker lost color transaction/alpha"); Escape(dialog); Ensure(!editor.ColorEditing && dialog.IsVisible, "Recording palette Esc closed editor");
                var provider = editor.FrameProvider; var pending = new TaskCompletionSource<BitmapSource>(); editor.FrameProvider = () => pending.Task;
                Click(Named<Button>(dialog, "VideoPrimaryColor")); var picking = editor.StartFramePicker(); Escape(dialog); Ensure(!editor.PickingColor && editor.ColorEditing, "Esc while reading source frame lost the palette"); pending.SetResult(UpgradeTests.TestPixels(640, 360)); await picking; Ensure(!editor.PickingColor && editor.Eyedropper?.Active != true, "Cancelled asynchronous picker reactivated"); editor.FrameProvider = provider; editor.FinishColor(false);
                editor.Replace(editor.Objects.Single() with { Fill = "none" }, true); Click(Named<Button>(dialog, "VideoPrimaryColor")); Ensure(editor.Palette!.EditingFill && editor.Palette.Draft.A > 0, "No-fill body color starts transparent"); editor.Palette.PreviewColor(Colors.Blue); editor.FinishColor(true); Ensure(editor.Objects.Single().Fill == "#0000FF" && editor.Objects.Single().Color == "#FF0000", "Shape body color modified outline");
                dialog.UpdateLayout(); Ensure(!editor.ColorEditing, "Color transaction retained after apply"); editor.Select(editor.Objects.Single(), false);
                dialog.BeginSpeed(); Ensure(!Named<Button>(dialog, "RestoreNormalSpeed").IsEnabled, "New draft offers restoring a nonexistent interval");
                dialog.Timeline.MoveDraftBoundary(true, 4.10); dialog.Timeline.MoveDraftBoundary(false, 5.20); dialog.ConfirmSpeed(2); await dialog.ConfigurationSaved;
                var saved = (await RecordingEditStore.LoadAsync(source))!; Ensure(saved.SpeedRanges!.Single() == new VideoSpeedRange(4.1, 5.2, 2), "Confirm changed timeline endpoints"); dialog.UndoEdit(); dialog.RedoEdit();
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    ThemeService.Apply(theme); editor.Select(editor.Objects.Single(), false); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); dialog.Width = 1040; dialog.Height = 820; dialog.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    UiChangeTests.Render(dialog, "ui-fix-recording-" + theme.ToLowerInvariant() + ".png");
                    Click(Named<Button>(dialog, "VideoPrimaryColor")); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); UiChangeTests.RenderElement(PaletteHost(editor.Palette!), "ui-fix-recording-palette-" + theme.ToLowerInvariant() + ".png"); editor.FinishColor(false);
                    dialog.BeginSpeed(0); Ensure(Named<Button>(dialog, "RestoreNormalSpeed").IsEnabled, "Existing interval cannot be restored"); dialog.UpdateLayout(); UiChangeTests.Render(dialog, "ui-fix-speed-" + theme.ToLowerInvariant() + ".png"); dialog.CancelSpeed();
                }
                dialog.Width = 680; dialog.Height = 600; editor.Select(editor.Objects.Single(), false); dialog.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var body = UpgradeTests.Find<ScrollViewer>(dialog, s => s.Content is Grid && s.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled);
                Ensure(body.ExtentWidth <= body.ViewportWidth + 1, "Narrow editor overflows horizontally"); body.ScrollToEnd(); dialog.UpdateLayout(); UiChangeTests.Render(dialog, "ui-fix-recording-narrow.png");
                Ensure(Named<Button>(dialog, "ExportRecording").IsVisible, "Export action unreachable in narrow editor");
                dialog.BeginSpeed(0); dialog.ConfirmSpeed(1); await dialog.ConfigurationSaved; Ensure((await RecordingEditStore.LoadAsync(source))!.SpeedRanges!.Length == 0, "Restore normal left a speed band");
                File.WriteAllText(Path.Combine(directory, "validation-scope.txt"), "Routed WPF buttons/keys; real WPF layout; actual monitor DPI only. Native mouse input and mixed-DPI monitors require manual validation. One MP4 export; unrelated codecs/install lifecycle not rerun.");
            }
            finally { dialog.Close(); owner.Close(); ThemeService.Apply("Light"); Mouse.Capture(null); }
        });
        await check("One short MP4 export retains speed/cut duration, pure shape color and audio", async () =>
        {
            string target = Path.Combine(directory, "result.mp4"); var cuts = new[] { new VideoRange(1, 1.5) }; var speeds = new[] { new VideoSpeedRange(2, 3, 2) }; var shape = new VideoObject("shape", VideoObjectKind.Rectangle, 30, 30, 140, 100, 0, 6, 0, Color: "#FF0000", Fill: "#00FF00");
            var request = new VideoExportRequest(source, target, RecordingFormat.Mp4, ExportQuality.High, 0, 6, Overwrite: true, FramesPerSecond: 30, Deleted: cuts, Objects: new[] { shape }, SpeedRanges: speeds);
            await VideoExportService.ExportAsync(request, info, tools); var output = await VideoExportService.ProbeAsync(target, tools);
            Ensure(Math.Abs(output.Duration - request.OutputDuration) < .1 && output.HasAudio, "Export speed/cut duration or audio wrong");
            using var reader = new VideoFrameReader(tools.Ffmpeg!, target, output.Width, output.Height, 30); var frame = (await reader.FrameAsync(.5, CancellationToken.None))!; int p = (70 * output.Width + 80) * 4;
            Ensure(frame[p + 1] > 230 && frame[p] < 25 && frame[p + 2] < 25, "Pure shape fill differs in export");
        });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
