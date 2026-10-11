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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;

namespace QuickCapture;

internal static class EditorObjectTests
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    private static void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    { if (root is T item) yield return item; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => All<T>(root).First(e => e.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void NoQuickColors(DependencyObject root) => Ensure(!All<Button>(root).Any(b => b.ToolTip is string tip && (tip.StartsWith("#") || AnnotationColors.Presets.Any(p => tip == p.Name + " " + AnnotationColors.Hex(p.Color)))), "Property quick colors remain");
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "EditorObjects"); Directory.CreateDirectory(directory);
        await check("Screenshot text frames, repeated text, transactional frame colors and native window icons", async () =>
        {
            var window = new EditorWindow(UpgradeTests.TestPixels(640, 360), new Settings(), _ => { }); window.Show();
            try
            {
                var editor = window.Editor;
                foreach (var tool in new[] { AnnotationTool.Arrow, AnnotationTool.Freehand, AnnotationTool.Text, AnnotationTool.Step }) { editor.SelectTool(tool); window.UpdateLayout(); NoQuickColors(editor.OptionsFor(tool)); }
                editor.SelectTool(AnnotationTool.Text); editor.BeginText(new(30, 30)); editor.TextInput!.Text = "中文 English 第一处";
                var options = editor.OptionsFor(AnnotationTool.Text); Named<CheckBox>(options, "ScreenshotTextFrameEnabled").IsChecked = true;
                var borderColor = All<Button>(options).First(b => (string?)b.ToolTip == "边框颜色 · 调色盘 / 吸管"); Click(borderColor); editor.Palette.PreviewColor(Colors.Blue); editor.FinishColor(false);
                Ensure(editor.Surface.TextDraft!.TextFrame!.Color == "#000000", "Frame color cancellation leaked");
                Click(All<Button>(options).First(b => (string?)b.ToolTip == "填充颜色 · 调色盘 / 吸管")); Ensure(editor.Palette.Draft.A == 255, "No-fill frame picker starts invisible"); editor.Palette.PreviewColor(Colors.White); editor.FinishColor(false); Ensure(editor.Surface.TextDraft!.TextFrame!.Fill == "none", "Frame fill cancellation leaked");
                Click(borderColor); editor.Palette.PreviewColor(Colors.Lime); editor.FinishColor(true); editor.ConfirmText();
                var first = editor.Surface.Items.First(); var bounds = AnnotationGeometry.Bounds(first); var resized = AnnotationGeometry.Resize(first, bounds, new(bounds.TopLeft, new Size(bounds.Width + 50, bounds.Height)));
                Ensure(resized.TextWidth == bounds.Width + 50 && resized.TextFrame == first.TextFrame && resized.FontSize == first.FontSize, "Resizing a framed paragraph lost width or style"); editor.Surface.SelectAt(new(40, 40), 3); editor.Surface.SetSelected(resized);
                editor.BeginText(new(320, 190)); editor.TextInput!.Text = "第二处文字"; editor.ConfirmText();
                Ensure(editor.Surface.Count == 2 && editor.Surface.Items.All(a => a.TextFrame?.Enabled == true) && editor.Surface.Items.First().Color == Colors.Red, "Multiple text/frame/body styles interfered");
                editor.Undo(); Ensure(editor.Surface.Count == 1, "Second text undo failed"); editor.Redo(); Ensure(editor.Surface.Count == 2, "Second text redo failed");
                string image = Path.Combine(directory, "texts.png"); var project = ScreenshotProjects.Snapshot(editor.Surface, 1, ScreenshotFormat.Png, 100);
                await ScreenshotProjects.SaveAsync(project, editor.Surface.Export(), image, ScreenshotFormat.Png, 100, true); var restored = await ScreenshotProjects.LoadAsync(image);
                Ensure(JsonSerializer.Serialize(project.Annotations) == JsonSerializer.Serialize(restored.Annotations), "Screenshot frame/project roundtrip failed");
                var icon = window.Icon as BitmapFrame; Ensure(icon?.Decoder?.Frames.Count == 7, "Window did not receive the multi-size ICO");
                IntPtr nativeIcon = SendMessage(new WindowInteropHelper(window).Handle, 0x7f, new IntPtr(1), IntPtr.Zero); Ensure(nativeIcon != IntPtr.Zero, "Taskbar large icon missing");
                using var drawingIcon = System.Drawing.Icon.FromHandle(nativeIcon); using var bitmap = drawingIcon.ToBitmap(); int green = 0;
                for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) { var pixel = bitmap.GetPixel(x, y); if (pixel.A > 100 && pixel.G > pixel.R + 25 && pixel.G > 90) green++; }
                Ensure(green > 20, "Taskbar icon is not the application logo"); bitmap.Save(Path.Combine(UiChangeTests.PreviewDirectory, "editor-objects-taskbar-icon.png"));
                foreach (string theme in new[] { "Light", "Dark" }) { ThemeService.Apply(theme); window.Width = 680; window.Height = 640; editor.SelectTool(AnnotationTool.Select); editor.Surface.SelectAt(new(40, 40), 3); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); UiChangeTests.Render(window, "editor-objects-screenshot-" + theme + ".png"); }
            }
            finally { window.Close(); ThemeService.Apply("Light"); }
        });
        await check("Multiple video text/PIP tracks, same-asset frame isolation, schema reopen and one composed MP4 export", async () =>
        {
            var tools = await MediaTools.DetectAsync(new Settings()); Ensure(tools.CanEffects, "Media tools unavailable");
            string source = Path.Combine(directory, "source.mp4"), material = Path.Combine(directory, "pip.mp4");
            if (!File.Exists(source)) { var r = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=gray:s=640x360:r=10:d=2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source }, CancellationToken.None); Ensure(r.ExitCode == 0, r.Error); }
            if (!File.Exists(material)) { var r = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=80x80:r=10:d=1", "-f", "lavfi", "-i", "color=c=blue:s=80x80:r=10:d=1", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", material }, CancellationToken.None); Ensure(r.ExitCode == 0, r.Error); }
            var info = await VideoExportService.ProbeAsync(source, tools); string asset = await RecordingEditStore.ImportAssetAsync(source, material, CancellationToken.None);
            var owner = new Window { Width = 200, Height = 120, ShowInTaskbar = false }; Ui.Theme(owner); owner.Show();
            var dialog = new RecordingEditWindow(owner, source, new Settings(), new(ExportQuality.High, 10, false, 2)); dialog.Show(); await dialog.Initialization;
            VideoObject[] objects;
            try
            {
                var editor = dialog.ObjectEditor; editor.Restore(Array.Empty<VideoObject>()); editor.SetTool(VideoObjectKind.Text);
                editor.BeginText(new(15, 20), null); editor.TextEditor.Input.Text = "中文 第一段"; dialog.UpdateLayout(); Named<CheckBox>(editor.Toolbar.PropertyPanel, "VideoTextFrameEnabled").IsChecked = true;
                editor.OpenColor(Named<Button>(dialog, "VideoTextFrameColor"), editor.TextEditor.Draft!, false, "边框颜色", frameFill: false); editor.Palette!.PreviewColor(Colors.Lime); editor.FinishColor(true); editor.TextEditor.Confirm();
                Ensure(editor.Overlay.Tool == VideoObjectKind.Text && editor.Objects.Length == 1, "Text tool did not continue after confirmation");
                editor.BeginText(new(345, 20), null); editor.TextEditor.Input.Text = "第二段 English"; editor.TextEditor.Confirm();
                var second = editor.Objects.Last(); editor.Replace(second with { TextFrame = second.TextFrame! with { Color = "#0000FF" }, Width = 270 }, true);
                var pip = new VideoObject("pip-a", VideoObjectKind.PictureInPicture, 40, 230, 80, 80, 0, 2, 2, Asset: asset, AssetVideo: true, AssetWidth: 80, AssetHeight: 80, AssetDuration: 2);
                editor.Add(pip); editor.Add(pip with { Id = "pip-b", X = 150, Layer = 3, AssetStart = 1 });
                Ensure(editor.Objects.Length == 4 && editor.Objects.Select(o => o.Name).Distinct().Count() == 4 && editor.Tracks.RowCount == 4, "Multiple text/PIP objects lack distinct tracks/names");
                foreach (var item in editor.Objects) { editor.Select(item, false); dialog.UpdateLayout(); NoQuickColors(editor.Toolbar.PropertyPanel); }
                editor.Select(editor.Objects.First(), false);
                var before = editor.Objects.First(); editor.OpenColor(Named<Button>(dialog, "VideoTextFrameFill"), before, false, "框内填充", frameFill: true); Ensure(editor.Palette!.Draft.A == 255, "Video no-fill frame picker starts invisible"); editor.Palette.PreviewColor(Color.FromArgb(120, 255, 255, 255)); editor.FinishColor(false); Ensure(editor.Objects.First() == before, "Video frame fill cancel changed text");
                foreach (string theme in new[] { "Light", "Dark" }) { ThemeService.Apply(theme); dialog.Width = 680; dialog.Height = 700; dialog.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); UiChangeTests.Render(dialog, "editor-objects-video-" + theme + ".png"); }
                objects = editor.Objects;
            }
            finally { await dialog.ConfigurationSaved; typeof(RecordingEditWindow).GetField("_closeApproved", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dialog, true); dialog.Close(); owner.Close(); ThemeService.Apply("Light"); }
            var state = new RecordingEditState(0, 2, Array.Empty<VideoRange>(), Objects: objects); await RecordingEditStore.SaveAsync(source, state); var reopened = (await RecordingEditStore.LoadAsync(source))!;
            Ensure(JsonSerializer.Serialize(state) == JsonSerializer.Serialize(reopened), "Multiple object/frame schema roundtrip failed"); Ensure(JsonDocument.Parse(File.ReadAllText(RecordingEditStore.FileFor(source))).RootElement.GetProperty("Version").GetInt32() == 6, "New project features need schema 6");
            using (var frames = new VideoObjectFrames(source, tools.Ffmpeg!, 10))
            {
                var fetched = await frames.AtAsync(objects, .5, CancellationToken.None); Ensure(fetched.Count == 2 && fetched["pip-a"].GetPixel(20, 20).Red > 220 && fetched["pip-b"].GetPixel(20, 20).Blue > 220, "Same video asset objects share playback frames");
            }
            string target = Path.Combine(directory, "result.mp4"); await VideoExportService.ExportAsync(new(source, target, RecordingFormat.Mp4, ExportQuality.High, 0, 2, Overwrite: true, FramesPerSecond: 30, Objects: objects), info, tools);
            using var reader = new VideoFrameReader(tools.Ffmpeg!, target, 640, 360, 10); var frameBytes = (await reader.FrameAsync(.5, CancellationToken.None))!;
            int red = (250 * 640 + 60) * 4, blue = (250 * 640 + 170) * 4, border = (21 * 640 + 80) * 4, secondBorder = (21 * 640 + 380) * 4;
            Ensure(frameBytes[red + 2] > 215 && frameBytes[red] < 50 && frameBytes[blue] > 215 && frameBytes[blue + 2] < 50 && frameBytes[border + 1] > 180 && frameBytes[border + 2] < 90 && frameBytes[secondBorder] > 180 && frameBytes[secondBorder + 2] < 90, "Composed export lost independent PIP frames or text frames");
            File.WriteAllText(Path.Combine(directory, "scope.txt"), "Targeted screenshot/video frame editing, multiple text/PIP objects, per-object timeline rows, screenshot/recording project reopen, one composed MP4 export, current-host-DPI light/dark narrow layout and real WM_GETICON logo. Windows Explorer pinned shortcut cache and physical mouse/mixed-monitor DPI are not manually exercised.");
        });
    }
}
