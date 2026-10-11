using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using SkiaSharp;

namespace QuickCapture;

// Only the layout, color handoff and text resize paths changed in this release.
internal static class EditorLayoutTests
{
    private static void Ensure(bool ok, string message) => UpgradeTests.Ensure(ok, message);
    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Contained(FrameworkElement parent)
    {
        foreach (var child in All<Control>(parent).Where(c => c.IsVisible && c is Button or CheckBox or ComboBox))
        {
            var p = child.TranslatePoint(new(0, 0), parent);
            Ensure(p.X >= -.1 && p.Y >= -.1 && p.X + child.ActualWidth <= parent.ActualWidth + 1 && p.Y + child.ActualHeight <= parent.ActualHeight + 1, "Clipped property: " + child.ToolTip);
        }
        foreach (var color in All<ColorDisplay>(parent).Where(c => c.IsVisible))
            Ensure(color.ActualWidth == 52 && color.ActualHeight == 36 && color.Padding == new Thickness(6) && color.Content is FrameworkElement swatch && swatch.ActualWidth >= 37 && swatch.ActualHeight >= 21, "Color display differs or collapses");
    }
    private static void InputVisible(TextBox input)
    {
        input.UpdateLayout();
        Ensure(input.Style.Setters.Count == 0 && input.VerticalContentAlignment == VerticalAlignment.Top && input.MinHeight == 0, "Input inherited form sizing");
        Ensure(input.ActualHeight >= input.FontSize && input.ActualWidth >= input.FontSize, $"Input/hint has no glyph-sized viewport: actual={input.ActualWidth}x{input.ActualHeight}; requested={input.Width}x{input.Height}; visible={input.IsVisible}");
        if (input.Text.Length == 0) return;
        var first = input.GetRectFromCharacterIndex(0); var last = input.GetRectFromCharacterIndex(input.Text.Length, true);
        Ensure(!first.IsEmpty && first.Top >= -.5 && !last.IsEmpty && last.Bottom <= input.ActualHeight + 1, $"Caret/lines clipped: first={first}; last={last}; height={input.ActualHeight}");
    }
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "EditorLayout", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        await check("Screenshot grouped wrapping, unified entries and monitor placement at narrow widths and DPI", async () =>
        {
            var window = new EditorWindow(UpgradeTests.TestPixels(960, 540), new Settings(), _ => { }); window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try
            {
                var toolbar = All<ScreenshotToolbar>(window).Single();
                foreach (double width in new[] { 320d, 560d, 800d })
                    foreach (var tool in new[] { AnnotationTool.Text, AnnotationTool.Rectangle, AnnotationTool.Freehand, AnnotationTool.Arrow, AnnotationTool.Step, AnnotationTool.Mosaic })
                    {
                        window.Editor.SelectTool(tool); toolbar.SetAvailableWidth(width); window.UpdateLayout(); Contained(toolbar.PropertyPanel);
                        Ensure(toolbar.ActualWidth <= width + 1, "Toolbar exceeded available width");
                        if (tool == AnnotationTool.Text) Ensure(toolbar.PropertyPanel.ActualHeight > 80, "Text properties did not wrap by group");
                        int count = tool switch { AnnotationTool.Text => 4, AnnotationTool.Rectangle => 2, AnnotationTool.Freehand => 2, AnnotationTool.Arrow or AnnotationTool.Step => 1, _ => 0 };
                        Ensure(All<ColorDisplay>(toolbar.PropertyPanel).Count(c => c.IsVisible) == count, "Missing or duplicate color entry");
                        Ensure(!All<Button>(toolbar.PropertyPanel).Any(b => b.IsVisible && b is not ColorDisplay && b.ToolTip?.ToString()?.Contains("吸管") == true), "Standalone picker remains");
                    }
                window.Editor.SelectTool(AnnotationTool.Text); toolbar.SetAvailableWidth(560); window.UpdateLayout();
                UiChangeTests.RenderElement(toolbar, "layout-v017-screenshot-toolbar.png");
                var screen = Forms.Screen.PrimaryScreen!;
                var selection = new SelectionWindow(screen, UiChangeTests.SyntheticDesktop(screen.Bounds.Width, screen.Bounds.Height), false, _ => { }, new Settings());
                try
                {
                    selection.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var work = screen.WorkingArea; selection.BeginEditing(new(work.Right - 220, work.Bottom - 140, 180, 100, screen.DeviceName));
                    foreach (var tool in new[] { AnnotationTool.Text, AnnotationTool.Freehand, AnnotationTool.Mosaic })
                    {
                        selection.Editor!.SelectTool(tool); selection.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(selection);
                        var area = new Rect((work.X - screen.Bounds.X) / dpi.DpiScaleX + 8, (work.Y - screen.Bounds.Y) / dpi.DpiScaleY + 8, work.Width / dpi.DpiScaleX - 16, work.Height / dpi.DpiScaleY - 16);
                        Ensure(area.Contains(selection.ToolbarBounds), "Expanded panels escaped work area");
                        Contained(All<ScreenshotToolbar>(selection).Single());
                    }
                }
                finally { selection.Close(); }
                foreach (double dpi in new[] { 1d, 1.25, 1.5, 2d })
                {
                    toolbar.SetAvailableWidth(720 / dpi - 16); toolbar.Measure(new(720 / dpi - 16, double.PositiveInfinity));
                    var area = new Rect(8, 8, 720 / dpi - 16, 900 / dpi - 16); var size = toolbar.DesiredSize;
                    Ensure(area.Contains(new Rect(ToolbarPlacement.Place(new(area.Right - 40, area.Bottom - 30, 24, 16), size, area), size)), "DPI layout escaped area");
                }
            }
            finally { window.Close(); }
        });
        await check("Screenshot placeholder, glyphs and caret use full font metrics; reopen and four image formats", async () =>
        {
            var window = new EditorWindow(UpgradeTests.TestPixels(960, 540), new Settings(), _ => { }); window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try
            {
                var editor = window.Editor; editor.Surface.ShowSelection = false;
                foreach (var style in new[] { ("Microsoft YaHei UI", 24d), ("Arial", 64d), ("SimSun", 96d) })
                {
                    editor.SetTextStyle(style.Item1, style.Item2, true, TextAlignment.Left); editor.BeginText(new(850, 490)); window.UpdateLayout(); InputVisible(editor.TextInput!);
                    var frame = new TextFrameStyle(Enabled: true, Shape: AnnotationShape.RoundedRectangle, Width: 4, Color: "#00FF00", Fill: "#40808080");
                    var draft = editor.Surface.TextDraft! with { Text = "中文 Ágj English\n完整显示", Italic = true, OutlineEnabled = true, OutlineWidth = 4, TextFrame = frame };
                    editor.CancelText(); editor.BeginText(draft.Start, draft); editor.TextInput!.Text = draft.Text; window.UpdateLayout(); InputVisible(editor.TextInput);
                    // The existing-object branch requires a selected object for confirmation.
                    editor.CancelText(); editor.Surface.Add(draft); editor.Surface.SelectAt(draft.Start + new Vector(20, 20));
                    var item = editor.Surface.Selected!; editor.BeginText(item.Start, item); editor.TextInput!.Text = item.Text + "!"; window.UpdateLayout(); InputVisible(editor.TextInput); editor.ConfirmText();
                    Ensure(editor.Surface.Selected!.FontSize == style.Item2 && editor.Surface.Selected.Italic && editor.Surface.Selected.TextFrame == frame, "Re-edit lost style");
                    editor.Surface.DeleteSelected();
                }
                editor.SetTextStyle("Arial", 48, true, TextAlignment.Left); editor.BeginText(new(40, 40)); editor.TextInput!.Text = "中文 English\n输入与确认一致"; window.UpdateLayout();
                UiChangeTests.Render(window, "layout-v017-screenshot-input.png");
                var preview = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32); preview.Render(editor.Surface);
                editor.ConfirmText(); Ensure(UpgradeTests.Bytes(preview).SequenceEqual(UpgradeTests.Bytes(editor.Surface.Export())), "Draft and confirmed layout differ");
                foreach (var format in new[] { ScreenshotFormat.Png, ScreenshotFormat.Jpg, ScreenshotFormat.WebP, ScreenshotFormat.Bmp })
                {
                    var bytes = ImageExportService.Encode(editor.Surface.Export(), format, 90);
                    var image = ImageExportService.Decode(bytes, format); Ensure(image.PixelWidth == 960 && image.PixelHeight == 540, "Screenshot output format lost dimensions: " + format);
                }
            }
            finally { window.Close(); }
        });
        // Reuse the existing handoff check without running its unrelated export checks.
        await ToolStyleTests.RunAsync((name, action) => name.StartsWith("New and reopened screenshot", StringComparison.Ordinal) ? check(name, action) : Task.CompletedTask);
        await ColorTransactionTests.RunAsync(check);
        await check("Video eight text handles preserve style, reflow, undo redo, project reopen, preview and one MP4 export", async () =>
        {
            var tools = await MediaTools.DetectAsync(new Settings());
            string source = Path.Combine(directory, "source.mp4"), target = Path.Combine(directory, "resized.mp4");
            var fixture = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=gray:s=640x360:r=10:d=1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source }, CancellationToken.None); Ensure(fixture.ExitCode == 0, fixture.Error);
            var info = await VideoExportService.ProbeAsync(source, tools);
            var owner = new Window { Width = 200, Height = 120, ShowInTaskbar = false }; Ui.Theme(owner); owner.Show();
            RecordingEditWindow? dialog = new(owner, source, new Settings(), new(ExportQuality.High, 10, false, 1)); dialog.Show(); await dialog.Initialization;
            VideoObject final;
            try
            {
                var editor = dialog.ObjectEditor; editor.Restore(Array.Empty<VideoObject>());
                foreach (var kind in new[] { VideoObjectKind.Rectangle, VideoObjectKind.RoundedRectangle, VideoObjectKind.Ellipse, VideoObjectKind.Arrow, VideoObjectKind.Line, VideoObjectKind.Brush, VideoObjectKind.Text })
                {
                    editor.SetTool(kind); dialog.UpdateLayout(); Contained(editor.Toolbar.PropertyPanel);
                    int count = kind == VideoObjectKind.Text ? 4 : 2;
                    Ensure(All<ColorDisplay>(editor.Toolbar.PropertyPanel).Count(c => c.IsVisible) == count, "Video color entry missing or duplicated: " + kind);
                }
                editor.BeginText(new(600, 330), null);
                editor.TextEditor.Update(editor.TextEditor.Draft! with { FontFamily = "Arial", FontSize = 64, Bold = true, Italic = true, OutlineEnabled = true, OutlineWidth = 4, TextFrame = new(Enabled: true, Width: 4) });
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); dialog.UpdateLayout(); InputVisible(editor.TextEditor.Input);
                var hint = editor.TextEditor.Draft!; Ensure(hint.Height >= AnnotationGeometry.TextContentHeight(hint.TextAnnotation()), "Empty video hint clipped");
                UiChangeTests.Render(dialog, "layout-v017-video-hint.png"); editor.TextEditor.Cancel();
                var original = new VideoObject("resize", VideoObjectKind.Text, 100, 30, 420, 230, 0, 1, 0, Text: "中文 English resize\nÁgj 自动换行 保留字号", FontFamily: "Arial", FontSize: 28, Bold: true, Italic: true, Alignment: TextAlignment.Center, OutlineEnabled: true, OutlineWidth: 2, TextFrame: new(Enabled: true, Width: 2, Color: "#00FF00"));
                editor.Add(original); original = editor.Objects.Single();
                foreach (var handle in VideoObjectOverlay.Handles(original))
                {
                    editor.Select(original, false); dialog.UpdateLayout(); double scale = editor.Overlay.ActualWidth / 640;
                    var point = new Point((original.X + (handle.X + 1) * original.Width / 2) * scale, (original.Y + (handle.Y + 1) * original.Height / 2) * scale);
                    Ensure(editor.Overlay.HandleAt(point) == handle, "Text handle missing: " + handle);
                    Ensure(VideoObjectOverlay.HandleCursor(handle) == (handle.X == 0 ? Cursors.SizeNS : handle.Y == 0 ? Cursors.SizeWE : handle.X == handle.Y ? Cursors.SizeNWSE : Cursors.SizeNESW), "Handle cursor differs");
                    var delta = new Vector(handle.X * 18, handle.Y * 18); editor.Overlay.ResizeText(original, handle, delta, false); editor.Overlay.ResizeText(original, handle, delta, true);
                    var after = editor.Objects.Single(); after.Validate(info);
                    Ensure(after.Bounds != original.Bounds && after.FontSize == original.FontSize && after.Italic && after.Bold && after.Alignment == original.Alignment && after.TextFrame == original.TextFrame && after.OutlineWidth == original.OutlineWidth, "Resize distorted style or didn't adjust bounds");
                    Ensure(after.Height >= VideoTextLayout.RequiredHeight(after), "Text content clipped after resize");
                    dialog.UndoEdit(); Ensure(editor.Objects.Single() == original, "Resize undo failed"); dialog.RedoEdit(); Ensure(editor.Objects.Single() == after, "Resize redo failed"); dialog.UndoEdit();
                }
                editor.Select(original, false); editor.Overlay.ResizeText(original, (1, 0), new(-200, 0), true);
                final = editor.Objects.Single(); Ensure(final.Width < original.Width && VideoTextLayout.RequiredHeight(final) > VideoTextLayout.RequiredHeight(original), "Horizontal drag did not reflow");
                foreach (var handle in VideoObjectOverlay.Handles(final))
                {
                    var clamped = VideoTextLayout.Resize(final, handle, new(handle.X * -10000, handle.Y * -10000), info); clamped.Validate(info);
                    Ensure(clamped.Width >= Math.Min(info.Width, VideoTextLayout.MinimumWidth(clamped)) - 1 && clamped.Height >= VideoTextLayout.RequiredHeight(clamped) - 1, "Minimum/boundary clips glyphs");
                    var view = new Rect(50, 20, 560, 320); var cropped = VideoTextLayout.Resize(final, handle, new(handle.X * 10000, handle.Y * 10000), info, view);
                    Ensure(view.Contains(cropped.Bounds), "Text resize escaped the cropped frame");
                }
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    ThemeService.Apply(theme); dialog.Width = 720; dialog.UpdateLayout(); editor.Select(final, false); dialog.UpdateLayout(); Contained(editor.Toolbar.PropertyPanel);
                    Ensure(!All<TextBlock>(editor.Toolbar.PropertyPanel).Any(t => t.Text == "文本框宽度"), "Width field remains");
                    editor.BeginText(new(final.X, final.Y), final); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); dialog.UpdateLayout(); InputVisible(editor.TextEditor.Input);
                    UiChangeTests.Render(dialog, "layout-v017-video-" + theme.ToLowerInvariant() + ".png"); editor.TextEditor.Cancel();
                }
                await dialog.ConfigurationSaved; dialog.Close(); dialog = new(owner, source, new Settings(), new(ExportQuality.High, 10, false, 1)); dialog.Show(); await dialog.Initialization;
                Ensure(dialog.ObjectEditor.Objects.Single() == final, "Reopen lost resized bounds/style");
            }
            finally { if (dialog != null) { dialog.ObjectEditor.TextEditor.Cancel(); await dialog.ConfigurationSaved; dialog.Close(); } owner.Close(); ThemeService.Apply("Light"); }
            using var originalReader = new VideoFrameReader(tools.Ffmpeg!, source, 640, 360, 10); byte[] raw = (await originalReader.FrameAsync(.2, CancellationToken.None))!;
            var expected = raw.ToArray(); VideoObjectRenderer.Render(expected, info, new[] { final }, .2, new Dictionary<string, SKBitmap>());
            var surface = new AnnotationSurface(BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, raw, 640 * 4)); surface.Add(final.TextAnnotation() with { Start = new(final.X, final.Y), End = new(final.X, final.Y) }); var screenshot = UpgradeTests.Bytes(surface.Export());
            Ensure(expected.Zip(screenshot).Count(p => Math.Abs(p.First - p.Second) > 3) < 300, "Resized text preview/render differs from screenshot");
            await VideoExportService.ExportAsync(new(source, target, RecordingFormat.Mp4, ExportQuality.High, 0, 1, Overwrite: true, FramesPerSecond: 30, Objects: new[] { final }), info, tools);
            using var exportedReader = new VideoFrameReader(tools.Ffmpeg!, target, 640, 360, 30); byte[] actual = (await exportedReader.FrameAsync(.2, CancellationToken.None))!;
            int changed = 0, matching = 0;
            for (int p = 0; p < raw.Length; p += 4)
            {
                if (Math.Abs(expected[p] - raw[p]) + Math.Abs(expected[p + 1] - raw[p + 1]) + Math.Abs(expected[p + 2] - raw[p + 2]) < 60) continue;
                changed++; if (Math.Abs(expected[p] - actual[p]) + Math.Abs(expected[p + 1] - actual[p + 1]) + Math.Abs(expected[p + 2] - actual[p + 2]) < 120) matching++;
            }
            Ensure(changed > 1000 && matching > changed * .90, "Export lost or scaled resized glyphs/frame");
            File.WriteAllText(Path.Combine(directory, "scope.txt"), "Grouped toolbar at 320/560/800 DIPs; installed monitor work area; 100/125/150/200% simulated layout; shared chip sizes/states by common template; screenshot/font caret layout; routed picker Press/Release; all eight handle hit areas and production resize callbacks; undo/redo; saved project reopening; shared preview pixels; one MP4 export and four screenshot encoders. Physical mouse, native IME candidate window and live mixed-monitor DPI switching were not manually exercised.");
        });
    }
}
