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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace QuickCapture;

internal static class ToolbarPreviewTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Two shared toolbar panels: themes, tool selection, contextual properties and stable layout", async () =>
        {
            var owner = new EditorWindow(AcceptanceArtifacts.NoteImage(900, 540), new Settings(), _ => { }) { Width = 1000, Height = 660 };
            try
            {
                owner.Show(); await Task.Delay(100); var editor = owner.Editor; var toolbar = Find<ScreenshotToolbar>(owner).Single();
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    ThemeService.Apply(theme); owner.UpdateLayout(); double height = toolbar.ActualHeight;
                    Ensure(toolbar.MainPanel.CornerRadius.TopLeft == 11 && toolbar.PropertyPanel.Margin.Top == 8, "Panels lost their rounded separation");
                    foreach (var tool in new[] { AnnotationTool.Crop, AnnotationTool.Select, AnnotationTool.Arrow, AnnotationTool.Rectangle, AnnotationTool.Text, AnnotationTool.Step, AnnotationTool.Freehand, AnnotationTool.Mosaic })
                    {
                        editor.SelectTool(tool); owner.UpdateLayout();
                        var buttons = toolbar.MainTools.Children.OfType<Button>().Where(b => b.Tag as string == "selected").ToArray();
                        Ensure(buttons.Length == 1 && editor.Tool == tool && editor.OptionsFor(tool).IsVisible, "Selected tool or relevant property row missing");
                        Ensure(toolbar.Properties.Children.OfType<WrapPanel>().Count(p => p.IsVisible) == 1, "More than one property row is visible");
                        Ensure(Math.Abs(toolbar.ActualHeight - height) < .5, "Switching tools shifted the floating panels");
                        Ensure(buttons[0].Foreground == ThemeService.Brush("ToolbarSelectedForeground"), "Selected icon did not follow the theme");
                    }
                    editor.SelectShape(AnnotationShape.Ellipse); var row = editor.OptionsFor(AnnotationTool.Rectangle);
                    row.Children.OfType<ComboBox>().Single().SelectedIndex = 2; row.Children.OfType<CheckBox>().Single().IsChecked = true;
                    editor.ApplyColor(Colors.Red); owner.UpdateLayout();
                    UiChangeTests.RenderElement(toolbar, "toolbar-shape-" + theme.ToLowerInvariant() + ".png");
                    editor.SelectTool(AnnotationTool.Text); editor.BeginText(new Point(60, 90)); editor.TextInput!.Text = "中文与 English\n属性实时预览";
                    editor.SetTextStyle("Microsoft YaHei UI", 32, true, TextAlignment.Center); owner.UpdateLayout();
                    Ensure(editor.TextInput.FontSize == 32 && editor.TextInput.FontWeight == FontWeights.Bold, "Properties lost the text draft");
                    UiChangeTests.Render(owner, "toolbar-text-" + theme.ToLowerInvariant() + ".png"); editor.CancelText();
                    Ensure(!editor.ButtonFor("undo").IsEnabled && !editor.ButtonFor("redo").IsEnabled, "Empty history actions are enabled");
                }
                owner.Width = 640; owner.UpdateLayout(); editor.SelectTool(AnnotationTool.Text); owner.UpdateLayout();
                Ensure(toolbar.ActualWidth <= 616 && toolbar.ActualHeight > 90, "Narrow editor did not wrap its controls");
                foreach (var button in Find<Button>(toolbar).Where(b => b.IsVisible)) Ensure(Contains(toolbar, button), "Narrow toolbar clips " + button.ToolTip);
                UiChangeTests.Render(owner, "toolbar-narrow-editor.png");
                var brush = new Annotation(AnnotationTool.Freehand, new Point(50, 50), new Point(100, 100), Colors.Blue, Points: new[] { new Point(50, 50), new Point(100, 100) }, Width: 24);
                editor.Surface.Add(brush); editor.SelectTool(AnnotationTool.Select); editor.Surface.SelectAt(new Point(75, 75));
                var brushSize = editor.OptionsFor(AnnotationTool.Freehand).Children.OfType<ComboBox>().Single();
                Ensure(brushSize.SelectedIndex == 3, "Selected brush did not display its own width");
                brushSize.SelectedIndex = 2; Ensure(editor.Surface.Selected?.Width == 12, "Brush property did not edit the selected annotation");
                editor.Undo(); Ensure(editor.Surface.Selected?.Width == 24 || editor.Surface.Items.Single().Width == 24, "Brush style edit lost undo");
            }
            finally { owner.Close(); }
        });
        await check("Actual tiny selections and screen edges preserve every toolbar control at available monitor DPI", async () =>
        {
            var display = new List<object>();
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var selection = new SelectionWindow(screen, UiChangeTests.SyntheticDesktop(screen.Bounds.Width, screen.Bounds.Height), false, _ => { }, new Settings { AutoSaveScreenshot = false });
                try
                {
                    selection.Show(); await Task.Delay(100);
                    var dpi = VisualTreeHelper.GetDpi(selection); var work = screen.WorkingArea;
                    display.Add(new { screen.DeviceName, screen.Bounds, screen.WorkingArea, dpi.DpiScaleX, dpi.DpiScaleY });
                    foreach (var region in new[] { new SavedRegion(work.Left + 12, work.Top + 12, 16, 16, screen.DeviceName), new SavedRegion(work.Right - 32, work.Top + 12, 16, 16, screen.DeviceName), new SavedRegion(work.Right - 220, work.Bottom - 140, 200, 120, screen.DeviceName), new SavedRegion(work.Left + 12, work.Bottom - 32, 16, 16, screen.DeviceName) })
                    {
                        selection.BeginEditing(region); selection.Editor!.SelectTool(AnnotationTool.Rectangle); selection.UpdateLayout();
                        var toolbar = Find<ScreenshotToolbar>(selection).Single();
                        var available = new Rect((work.X - screen.Bounds.X) / dpi.DpiScaleX + 8, (work.Y - screen.Bounds.Y) / dpi.DpiScaleY + 8, work.Width / dpi.DpiScaleX - 16, work.Height / dpi.DpiScaleY - 16);
                        Ensure(available.Contains(selection.ToolbarBounds), "Two-panel toolbar leaves the monitor work area");
                        Ensure(!selection.ToolbarBounds.IntersectsWith(selection.SelectionBounds), "Toolbar obscures a tiny/edge selection despite free space");
                        foreach (var button in Find<Button>(toolbar).Where(b => b.IsVisible)) Ensure(Contains(toolbar, button), "Edge toolbar clipped a button");
                    }
                    UiChangeTests.Render(selection, "toolbar-edge-" + display.Count + ".png");
                }
                finally { selection.Close(); }
            }
            // Coordinate-only checks supplement the actual installed displays.
            foreach (double scale in new[] { 1d, 1.25, 1.5, 2d })
            {
                var area = new Rect(8, 8, 1920 / scale - 16, 1080 / scale - 16); var size = new Size(800, 98);
                foreach (var bounds in new[] { new Rect(8, 8, 8, 8), new Rect(area.Right - 20, area.Bottom - 20, 8, 8) })
                    Ensure(area.Contains(new Rect(ToolbarPlacement.Place(bounds, size, area), size)), "Simulated DPI placement escaped the work area");
            }
            File.WriteAllText(Path.Combine(Paths.TestRoot!, "toolbar-displays.json"), JsonSerializer.Serialize(display, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        });
        await check("Image and video thumbnails: aspect ratios, transparency, first frames, missing/corrupt fallback and cache replacement", async () =>
        {
            string directory = Path.Combine(Paths.TestRoot!, "Thumbnails"); Directory.CreateDirectory(directory);
            foreach (var format in new[] { ScreenshotFormat.Png, ScreenshotFormat.Jpg, ScreenshotFormat.WebP, ScreenshotFormat.Bmp })
            {
                foreach (bool portrait in new[] { false, true })
                {
                    var image = Fixture(portrait ? 180 : 480, portrait ? 480 : 180, Colors.Red, transparent: format == ScreenshotFormat.Png);
                    string path = Path.Combine(directory, format + (portrait ? "-portrait" : "-landscape") + "." + ImageExportService.Extension(format));
                    ImageExportService.Save(image, path, format, 90, true);
                    var result = await RecentThumbnailCache.LoadAsync(path, 48, "", CancellationToken.None);
                    Ensure(result.Image is { IsFrozen: true } preview && Math.Max(preview.PixelWidth, preview.PixelHeight) <= 48 && (portrait ? preview.PixelHeight > preview.PixelWidth : preview.PixelWidth > preview.PixelHeight), "Incorrect decoded preview: " + path);
                    if (format == ScreenshotFormat.Png) { var pixel = new byte[4]; var alpha = new FormatConvertedBitmap(result.Image!, PixelFormats.Bgra32, null, 0); alpha.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0); Ensure(pixel[3] == 0, "Thumbnail lost alpha"); }
                }
            }
            string video = Path.Combine(directory, "preview.mp4"); string ffmpeg = MediaTools.Find("ffmpeg.exe", "")!;
            Ensure(ffmpeg != null, "Bundled media tools unavailable");
            var generated = await MediaTools.RunAsync(ffmpeg!, new[] { "-y", "-f", "lavfi", "-i", "color=c=blue:s=320x180:d=0.2", "-c:v", "libx264", "-pix_fmt", "yuv420p", video }, CancellationToken.None);
            Ensure(generated.ExitCode == 0, "Video fixture generation failed");
            var frame = await RecentThumbnailCache.LoadAsync(video, 48, "", CancellationToken.None); Ensure(frame.Image?.PixelWidth == 48 && frame.Image.PixelHeight == 27, "Video first frame missing");
            string gif = Path.Combine(directory, "preview.gif"); await MediaTools.RunAsync(ffmpeg!, new[] { "-y", "-i", video, "-frames:v", "1", gif }, CancellationToken.None);
            Ensure((await RecentThumbnailCache.LoadAsync(gif, 48, "", CancellationToken.None)).Image != null, "GIF first frame missing");
            string broken = Path.Combine(directory, "broken.png"); File.WriteAllText(broken, "invalid image");
            Ensure((await RecentThumbnailCache.LoadAsync(broken, 48, "", CancellationToken.None)).Image == null, "Corrupt image lacks fallback");
            Ensure((await RecentThumbnailCache.LoadAsync(Path.Combine(directory, "missing.mp4"), 48, "", CancellationToken.None)).Status == "文件已缺失", "Missing-file fallback lost its state");
            string replace = Path.Combine(directory, "replace.bmp"); ImageExportService.Save(Fixture(100, 100, Colors.Red), replace, ScreenshotFormat.Bmp, 100, true);
            var old = (await RecentThumbnailCache.LoadAsync(replace, 32, "", CancellationToken.None)).Image!;
            Ensure(ReferenceEquals(old, (await RecentThumbnailCache.LoadAsync(replace, 32, "", CancellationToken.None)).Image), "Repeated load bypassed cache");
            ImageExportService.Save(Fixture(100, 100, Colors.Blue), replace, ScreenshotFormat.Bmp, 100, true); File.SetLastWriteTimeUtc(replace, DateTime.UtcNow.AddSeconds(2));
            var updated = (await RecentThumbnailCache.LoadAsync(replace, 32, "", CancellationToken.None)).Image!;
            Ensure(!ReferenceEquals(old, updated) && Pixel(updated)[0] > 240 && Pixel(updated)[2] < 10, "Changed same-size file retained its old thumbnail");
            for (int i = 0; i <= RecentThumbnailCache.Capacity; i++) { string path = Path.Combine(directory, "cache-" + i + ".bmp"); File.Copy(replace, path, true); await RecentThumbnailCache.LoadAsync(path, 32, "", CancellationToken.None); }
            Ensure(RecentThumbnailCache.Count == RecentThumbnailCache.Capacity, "LRU exceeded its capacity");
        });
        await check("Recent rows: async binding, both themes, refresh, recycled scrolling, deletion and pending-export state", async () =>
        {
            var previousRoot = Paths.TestRoot; string previousTheme = ThemeService.Current;
            Paths.TestRoot = Path.Combine(previousRoot!, "PreviewList");
            var settings = new Settings { OutputDirectory = Paths.DefaultOutput, Theme = "Light", AutoSaveScreenshot = false }; settings.Save();
            Directory.CreateDirectory(settings.OutputDirectory);
            var sources = Directory.GetFiles(Path.Combine(previousRoot!, "Thumbnails")).Where(p => !Path.GetFileName(p).StartsWith("cache-", StringComparison.Ordinal)).ToArray();
            foreach (var source in sources) File.Copy(source, Path.Combine(settings.OutputDirectory, Path.GetFileName(source)), true);
            for (int i = 0; i < 8; i++) File.Copy(Path.Combine(previousRoot!, "Thumbnails", "Png-landscape.png"), Path.Combine(settings.OutputDirectory, "row-" + i + ".png"), true);
            var main = new MainWindow(); main.Closing += (_, e) => e.Cancel = false;
            try
            {
                main.Show(); await Task.Delay(100); main.UpdateLayout(); var list = (ListBox)main.FindName("RecentList");
                foreach (var theme in new[] { "Light", "Dark" })
                {
                    ThemeService.Apply(theme); await WaitPreviews(main); UiChangeTests.Render(main, "recent-previews-" + theme.ToLowerInvariant() + ".png");
                    var scroller = Find<ScrollViewer>(list).Single();
                    for (int i = 0; i < 8; i++) { scroller.ScrollToVerticalOffset(i % 2 == 0 ? scroller.ScrollableHeight : 0); main.UpdateLayout(); }
                    await WaitPreviews(main);
                    foreach (var thumbnail in Find<RecentThumbnail>(list))
                    {
                        Ensure(thumbnail.DataContext is RecentItem item && item.Path == thumbnail.FilePath, "Recycled row displayed another file");
                        if (thumbnail.Preview != null) Ensure(Math.Max(thumbnail.Preview.PixelWidth, thumbnail.Preview.PixelHeight) <= Math.Ceiling(32 * VisualTreeHelper.GetDpi(thumbnail).DpiScaleX), "Row decoded excessive pixels");
                    }
                }
                var thumb = new RecentThumbnail { FilePath = sources[0] }; var window = new Window { Content = thumb, Width = 100, Height = 100 };
                try { window.Show(); await Task.Delay(30); thumb.FilePath = sources[1]; thumb.FilePath = sources[2]; thumb.FilePath = Path.Combine(previousRoot!, "Thumbnails", "Jpg-portrait.jpg"); await thumb.Loading; Ensure(thumb.Preview is { } final && final.PixelHeight > final.PixelWidth, "Cancelled row load overwrote the latest source: " + thumb.PreviewStatus); }
                finally { window.Close(); }
                var current = (RecentItem)list.Items[0]; list.SelectedItem = current;
                Invoke(main, "DeleteRecent", current); await WaitPreviews(main);
                Ensure(!File.Exists(current.Path) && !list.Items.OfType<RecentItem>().Any(i => i.Path == current.Path), "Delete did not recycle the file and refresh rows");
                string newPath = Path.Combine(settings.OutputDirectory, "new-preview.png"); ImageExportService.Save(Fixture(180, 100, Colors.Lime), newPath, ScreenshotFormat.Png, 100, true);
                Invoke(main, "Saved", newPath); await WaitPreviews(main);
                Ensure(list.Items[0] is RecentItem top && top.Path == newPath && Find<RecentThumbnail>(list).Any(t => t.FilePath == newPath && t.Preview != null), "New saved file has no matching preview");
                var newItem = (RecentItem)list.Items[0]; Invoke(main, "OpenRecent", newItem); Invoke(main, "OpenRecentFolder", newItem);
                string master = Path.Combine(RecordingRecovery.DirectoryPath, "master-preview.mp4"); Directory.CreateDirectory(RecordingRecovery.DirectoryPath);
                File.Copy(sources.Single(p => p.EndsWith(".mp4", StringComparison.Ordinal)), master, true); Invoke(main, "RefreshRecent", null); await WaitPreviews(main);
                Ensure(list.Items.OfType<RecentItem>().Any(i => i.Path == master && i.Name.StartsWith("待导出 · ", StringComparison.Ordinal)), "Pending-export entry lost its status");
                var pending = list.Items.OfType<RecentItem>().Single(i => i.Path == master);
                var resumed = main.Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(150); var dialog = Application.Current.Windows.OfType<RecordingEditWindow>().Single();
                    try { await dialog.Initialization; Ensure(dialog.IsVisible && dialog.Timeline.End > 0, "Pending-export entry did not resume editing"); UiChangeTests.Render(dialog, "recent-pending-editor.png"); }
                    finally { dialog.Close(); }
                });
                Invoke(main, "OpenRecent", pending); await (await resumed);
                Ensure(File.Exists(master) && list.Items.OfType<RecentItem>().Any(i => i.Path == master), "Closing resumed editing discarded the pending recording");
            }
            finally { main.Close(); Paths.TestRoot = previousRoot; ThemeService.Apply(previousTheme); }
        });
    }
    private static async Task WaitPreviews(Window window)
    {
        window.UpdateLayout(); await Task.Delay(30); await Task.WhenAll(Find<RecentThumbnail>(window).Select(t => t.Loading)); window.UpdateLayout();
    }
    private static void Invoke(MainWindow main, string name, object? arg) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new[] { arg });
    private static byte[] Pixel(BitmapSource image) { var result = new byte[4]; new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), result, 4, 0); return result; }
    private static BitmapSource Fixture(int width, int height, Color color, bool transparent = false)
    {
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = color.B; bytes[i + 1] = color.G; bytes[i + 2] = color.R; bytes[i + 3] = transparent && i < width * 4 * Math.Max(4, height / 5) ? (byte)0 : (byte)255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4); image.Freeze(); return image;
    }
    private static IEnumerable<T> Find<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(element, i))) yield return child;
    }
    private static bool Contains(FrameworkElement parent, FrameworkElement child) { var p = child.TranslatePoint(new Point(), parent); return p.X >= -.5 && p.Y >= -.5 && p.X + child.ActualWidth <= parent.ActualWidth + .5 && p.Y + child.ActualHeight <= parent.ActualHeight + .5; }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
