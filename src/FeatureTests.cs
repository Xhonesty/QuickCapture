using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace QuickCapture;

internal static class FeatureTests
{
    internal static async Task RunUiAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Application and tray use the supplied multi-size icon", () =>
        {
            using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
            using var reader = new BinaryReader(resource);
            Ensure(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 7, "Multi-size icon was not embedded");
            resource.Position = 0;
            using var expected = new Drawing.Icon(resource, 32, 32);
            using var actual = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!;
            using var expectedImage = expected.ToBitmap(); using var extracted = actual.ToBitmap();
            // The shell can return an icon sized for the current monitor DPI.
            using var actualImage = new Drawing.Bitmap(extracted, new Drawing.Size(32, 32));
            long difference = 0;
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
            {
                var expectedColor = expectedImage.GetPixel(x, y); var actualColor = actualImage.GetPixel(x, y);
                difference += Math.Abs(expectedColor.R - actualColor.R) + Math.Abs(expectedColor.G - actualColor.G) + Math.Abs(expectedColor.B - actualColor.B);
            }
            Ensure(difference / (32d * 32 * 3) < 12, $"Executable icon differs from supplied asset ({extracted.Width}px shell icon, average color difference {difference / (32d * 32 * 3):F1})");
            extracted.Save(Path.Combine(UiChangeTests.PreviewDirectory, "icon-executable-dpi.png"));
            Ensure(Application.Current.FindResource("ApplicationIcon") is BitmapSource image && image.PixelWidth > 1000, "Window icon resource missing");
            return Task.CompletedTask;
        });
        await check("Freehand curves, brush mosaic clipping, dots, undo and tool modes", () =>
        {
            var pixels = new byte[200 * 160 * 4];
            for (int y = 0; y < 160; y++) for (int x = 0; x < 200; x++)
            { int p = (y * 200 + x) * 4; byte value = (byte)((x + y) % 2 == 0 ? 0 : 255); pixels[p] = value; pixels[p + 1] = value; pixels[p + 2] = value; pixels[p + 3] = 255; }
            var original = BitmapSource.Create(200, 160, 96, 96, PixelFormats.Bgra32, null, pixels, 800);
            var surface = new AnnotationSurface(original);
            var path = new[] { new Point(10, 20), new Point(60, 90), new Point(110, 20) };
            surface.Add(new(AnnotationTool.Freehand, path[0], path[^1], Colors.Lime, Points: path, Width: 6));
            var middle = Pixel(surface.Export(), 60, 89);
            Ensure(middle[1] > 240 && middle[2] < 10, "Curve ignores its middle point");
            Ensure(SamePixel(surface.Export(), original, 60, 40), "Freehand drew the bounding rectangle instead of the path");
            surface.Undo(); Ensure(SamePixel(surface.Export(), original, 60, 89), "Undo did not remove the entire stroke"); surface.Redo();
            surface.Add(new(AnnotationTool.Freehand, new(140, 40), new(140, 40), Colors.Lime, Points: new[] { new Point(140, 40) }, Width: 12));
            Ensure(Pixel(surface.Export(), 140, 40)[1] > 240, "Single click did not produce a brush dot");
            var mosaic = new[] { new Point(20, 120), new Point(60, 100), new Point(100, 120) };
            surface.Add(new(AnnotationTool.MosaicBrush, mosaic[0], mosaic[^1], Colors.White, Points: mosaic, Width: 16));
            Ensure(!SamePixel(surface.Export(), original, 60, 100), "Brush mosaic did not replace pixels along the stroke");
            Ensure(SamePixel(surface.Export(), original, 60, 119), "Brush mosaic obscured pixels outside the painted path");
            Ensure(!SamePixel(surface.Export(), original, 16, 120), "Brush mosaic lacks rounded end caps");
            Ensure(SamePixel(surface.Export(), original, 4, 4), "Painting changed unrelated pixels");
            surface.Undo(); Ensure(SamePixel(surface.Export(), original, 60, 100), "Brush mosaic undo failed"); surface.Redo();
            string preview = Path.Combine(UiChangeTests.PreviewDirectory, "annotations-brush.png");
            if (File.Exists(preview)) File.Delete(preview); CaptureService.Save(surface.Export(), preview);
            var owner = new Window();
            using var editor = new ScreenshotEditor(owner, original, new Settings(), _ => { }, () => { });
            var toolbar = editor.CreateToolbar();
            var button = toolbar.Children.OfType<Button>().Single(b => (string?)b.Content == "涂鸦");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Ensure(editor.Tool == AnnotationTool.Freehand, "Doodle button not wired");
            toolbar.Children.OfType<Button>().Single(b => (string?)b.Content == "马赛克").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var mode = toolbar.Children.OfType<ComboBox>().Single(c => (string?)c.ToolTip == "马赛克模式");
            Ensure(mode.IsEnabled && !editor.MosaicFreehand, "Rectangle mosaic mode unavailable");
            mode.SelectedIndex = 1; Ensure(editor.MosaicFreehand, "Brush mosaic mode unavailable");
            var size = toolbar.Children.OfType<ComboBox>().Single(c => (string?)c.ToolTip == "画笔粗细（原始像素）");
            Ensure(size.IsEnabled && size.SelectedIndex == 3, "Mosaic brush size not restored");
            owner.Close(); return Task.CompletedTask;
        });
        await check("Window snap Z order, monitor clipping, click confirmation and free drag", async () =>
        {
            var negative = new Drawing.Rectangle(-1920, -120, 1920, 1080);
            var targets = new[] { new WindowTarget(new(1), "top", new(-1900, -100, 400, 300)), new WindowTarget(new(2), "behind", new(-2000, -200, 1400, 800)) };
            var hit = WindowSnapper.Find(new(-1800, 0), negative, "test", targets);
            Ensure(hit == new SavedRegion(-1900, -100, 400, 300, "test"), "Snap ignored window Z order or negative origin");
            var clipped = WindowSnapper.Find(new(-1910, -110), negative, "test", targets);
            Ensure(clipped == new SavedRegion(-1920, -120, 1320, 720, "test"), "Spanning window was not clipped to monitor");
            var screen = Forms.Screen.PrimaryScreen!;
            var bounds = new Drawing.Rectangle(screen.Bounds.X + 200, screen.Bounds.Y + 200, 500, 320);
            var settings = new Settings { SnapToWindow = true };
            var selection = new SelectionWindow(screen, UiChangeTests.SyntheticDesktop(screen.Bounds.Width, screen.Bounds.Height), false, _ => { }, settings,
                snapTargets: new[] { new WindowTarget(new(1), "test", bounds) });
            try
            {
                selection.Show(); await Task.Delay(100);
                var point = selection.PointFromScreen(new Point(bounds.X + 100, bounds.Y + 100));
                selection.UpdateWindowHover(point);
                Ensure(selection.HoveredRegion?.Rectangle == bounds, "Hover did not select a window");
                UiChangeTests.Render(selection, "window-snap.png");
                selection.BeginSelection(point); selection.FinishSelection(point);
                Ensure(selection.Editor?.Surface.Width == bounds.Width && selection.Editor.Surface.Height == bounds.Height, "Click did not confirm window region");
                selection.Reselect(); settings.SnapToWindow = false; selection.UpdateWindowHover(point);
                Ensure(selection.HoveredRegion == null, "Disabled snapping still highlights windows");
                selection.BeginSelection(point); selection.FinishSelection(point);
                Ensure(selection.Editor == null, "Disabled snapping still confirms a window on click");
                settings.SnapToWindow = true; selection.BeginSelection(point);
                var end = selection.PointFromScreen(new Point(bounds.X + 200, bounds.Y + 180));
                selection.MoveSelection(end); selection.FinishSelection(end);
                Ensure(selection.Editor?.Surface.Width == 100 && selection.Editor.Surface.Height == 80, "Window snapping overrode manual dragging");
                settings.SnapToWindow = false; settings.Save(); Ensure(!Settings.Load().SnapToWindow, "Snap preference did not persist");
            }
            finally { selection.Close(); }
        });
    }

    internal static async Task CheckRecordingFrameAsync(Window scene, string directory)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(scene).Handle;
        Ensure(Native.VisibleWindowBounds(handle, out var bounds), "Visible window bounds unavailable for snapping");
        Ensure(!Native.SnapTargets().Any(target => target.Handle == handle), "Own windows enter snap candidates");
        var screen = Forms.Screen.FromHandle(handle);
        var region = new SavedRegion(bounds.X + 40, bounds.Y + 170, 300, 220, screen.DeviceName);
        var source = (ScreenRecorderLib.DisplayRecordingSource)RecorderService.RegionSource(region);
        Ensure(source.RecorderApi == ScreenRecorderLib.RecorderApi.DesktopDuplication, "Region capture still requests a full-screen WGC border");
        var frame = new RecordingFrame(region);
        try
        {
            scene.Activate(); frame.Show(); await Task.Delay(150);
            Ensure(frame.CaptureExcluded && !frame.IsActive && !frame.IsHitTestVisible, "Recording frame captures content or steals focus/clicks");
            Ensure(Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(frame).Handle, out var actual), "Recording frame bounds unavailable");
            Ensure(actual.Left == region.X - 3 && actual.Top == region.Y - 3 && actual.Right == region.X + region.Width + 3 && actual.Bottom == region.Y + region.Height + 3, "Recording frame surrounds the full screen instead of the selected region");
            var image = await RecorderService.CaptureSourceAsync(source, Path.Combine(directory, "region-frame-temp.png"));
            Ensure(image.PixelWidth == region.Width && image.PixelHeight == region.Height, "Desktop Duplication crop dimensions incorrect");
            foreach (var point in new[] { new Drawing.Point(2, 2), new Drawing.Point(297, 2) })
            {
                var pixel = Pixel(image, point.X, point.Y);
                Ensure(Math.Abs(pixel[0] - 50) <= 2 && Math.Abs(pixel[1] - 33) <= 2 && Math.Abs(pixel[2] - 22) <= 2, "Region crop contains a frame or content from the wrong screen coordinates");
            }
            string path = Path.Combine(directory, "region-frame-crop.png"); if (File.Exists(path)) File.Delete(path); CaptureService.Save(image, path);
        }
        finally { frame.Close(); }
    }

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4]; converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel;
    }
    private static bool SamePixel(BitmapSource a, BitmapSource b, int x, int y) => Pixel(a, x, y).SequenceEqual(Pixel(b, x, y));
    private static void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
