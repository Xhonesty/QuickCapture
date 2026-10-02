using System;
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

internal static class AnnotationUpgradeTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Palette HEX/HSV alpha, bounded deduplicated recent colors, five shapes and unified object history", () =>
        {
            Ensure(AnnotationColors.TryParse("#6495ED", out var custom) && custom == Colors.CornflowerBlue, "HEX was not parsed accurately");
            Ensure(AnnotationColors.TryParse("#806495ED", out var translucent) && translucent.A == 128, "HEX transparency was lost");
            Ensure(!AnnotationColors.TryParse("#zz22aa", out _), "Invalid HEX was accepted");
            var hsv = AnnotationColors.ToHsv(custom); Ensure(AnnotationColors.Hsv(hsv.Hue, hsv.Saturation, hsv.Value) == custom, "HSV roundtrip changed the chosen color");
            for (int i = 0; i < 16; i++) AnnotationColors.Remember(Color.FromRgb((byte)i, 12, 23)); AnnotationColors.Remember(custom); AnnotationColors.Remember(custom);
            Ensure(AnnotationColors.Recent.Count == 10 && AnnotationColors.Recent.Distinct().Count() == 10 && AnnotationColors.Recent[0] == custom, "Recent colors are duplicated or unbounded");
            var background = Solid(180, 150, Color.FromRgb(10, 20, 30));
            foreach (var shape in Enum.GetValues<AnnotationShape>())
            {
                var surface = new AnnotationSurface(background); surface.Add(new(AnnotationTool.Rectangle, new(25, 25), new(145, 115), custom, Shape: shape, FillColor: Colors.Gold));
                Ensure(Pixel(surface.Export(), 85, 75) == Colors.Gold, $"{shape} export does not contain its fill");
                Ensure(Pixel(surface.Export(), 5, 5) == Color.FromRgb(10, 20, 30), $"{shape} altered unrelated source pixels");
                Ensure(surface.SelectAt(new(85, 75)), $"{shape} cannot be selected");
                var before = surface.Selected!; surface.SetSelected(before with { Color = Colors.Lime, FillColor = translucent });
                surface.Undo(); Ensure(surface.Items[0] == before, "Selected color change cannot be undone"); surface.Redo();
                surface.SelectAt(new(85, 75)); var colored = surface.Selected!; surface.SetSelected(AnnotationGeometry.Move(colored, new(10, 8)), false); surface.CommitSelected(colored);
                surface.Undo(); Ensure(surface.Items[0] == colored, "Drag was not recorded as one edit"); surface.Redo();
                surface.SelectAt(new(95, 83)); var moved = surface.Selected!; var bounds = AnnotationGeometry.Bounds(moved); surface.SetSelected(AnnotationGeometry.Resize(moved, bounds, new Rect(bounds.Left, bounds.Top, 100, 80)));
                surface.DeleteSelected(); Ensure(surface.Count == 0, "Delete left the object on the canvas"); surface.Undo(); Ensure(surface.Count == 1, "Undo delete lost the object");
                surface.SetCrop(new(12, 10, 150, 125)); surface.Undo(); Ensure(surface.CropBounds == new Int32Rect(0, 0, 180, 150), "Object history broke crop undo");
            }
            var constrained = AnnotationGeometry.Constrain(new(160, 130), new(20, 140), new(180, 150), true);
            Ensure(Math.Abs(constrained.X - 160) == Math.Abs(constrained.Y - 130) && constrained.Y <= 150, "Shift constraint exceeded the canvas");
            var legacy = new AnnotationSurface(background); legacy.Add(new(AnnotationTool.Rectangle, new(20, 20), new(130, 100), Colors.Red)); Ensure(legacy.Items[0].Shape == AnnotationShape.Rectangle && legacy.Items[0].FillColor == null, "Legacy rectangle defaults changed");
            return Task.CompletedTask;
        });
        await check("Real shape drawing, selection move/resize/delete, custom stroke/fill, zoom-scroll canvas eyedropper confirm and Esc", async () =>
        {
            var owner = new Window { Width = 730, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterScreen }; Ui.Theme(owner);
            using var editor = new ScreenshotEditor(owner, TestGrid(640, 480), new Settings { AutoSaveScreenshot = false }, _ => { }, () => { });
            var root = new DockPanel(); var toolbar = editor.CreateToolbar(); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
            editor.Surface.LayoutTransform = new ScaleTransform(0.75, 0.75);
            var scroll = new ScrollViewer { Content = editor.Surface, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 425, Height = 255, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(12) }; root.Children.Add(scroll); owner.Content = root;
            try
            {
                owner.Show(); await HotkeyCaptureTests.ActivateAsync(owner); editor.SelectShape(AnnotationShape.RoundedRectangle);
                await HotkeyCaptureTests.DragAsync(owner, editor.Surface.PointToScreen(new(50, 70)), editor.Surface.PointToScreen(new(185, 175)));
                Ensure(editor.Surface.Count == 1 && editor.Surface.Items[0].Shape == AnnotationShape.RoundedRectangle, "Shape drawing did not create the configured object");
                editor.SelectTool(AnnotationTool.Select);
                await HotkeyCaptureTests.DragAsync(owner, editor.Surface.PointToScreen(new(120, 120)), editor.Surface.PointToScreen(new(140, 132)));
                Ensure(editor.Surface.Selected != null && Math.Abs(editor.Surface.Selected.Start.X - 70) < 2, "Actual pointer did not move the selected shape through zoom");
                var beforeResize = AnnotationGeometry.Bounds(editor.Surface.Selected!);
                await HotkeyCaptureTests.DragAsync(owner, editor.Surface.PointToScreen(new(beforeResize.Right, beforeResize.Bottom)), editor.Surface.PointToScreen(new(beforeResize.Right + 20, beforeResize.Bottom + 10)));
                Ensure(AnnotationGeometry.Bounds(editor.Surface.Selected!).Width > beforeResize.Width + 18, "Actual selection handle did not resize");
                var beforeKeys = editor.Surface.Selected!; var keyBounds = AnnotationGeometry.Bounds(beforeKeys);
                editor.Surface.SetSelected(AnnotationGeometry.Move(beforeKeys, new Vector(2 - keyBounds.Left, 0)));
                await HotkeyCaptureTests.PressAsync(owner, Key.Left, ModifierKeys.Shift);
                Ensure(Math.Abs(AnnotationGeometry.Bounds(editor.Surface.Selected!).Left) < 0.001, "Keyboard move crossed the left source boundary");
                var atLeft = editor.Surface.Selected!; var leftBounds = AnnotationGeometry.Bounds(atLeft);
                editor.Surface.SetSelected(AnnotationGeometry.Move(atLeft, new Vector(editor.Surface.SourceWidth - 2 - leftBounds.Right, 0)));
                await HotkeyCaptureTests.PressAsync(owner, Key.Right, ModifierKeys.Shift);
                Ensure(Math.Abs(AnnotationGeometry.Bounds(editor.Surface.Selected!).Right - editor.Surface.SourceWidth) < 0.001, "Keyboard move crossed the right source boundary");
                editor.Surface.SetSelected(beforeKeys);
                Ensure(AnnotationColors.TryParse("#4267B2", out var custom), "Custom color parse failed"); editor.ApplyColor(custom); Ensure(editor.Surface.Selected?.Color == custom, "Selected object ignored custom stroke");
                editor.Palette.Children.OfType<ComboBox>().First().SelectedIndex = 1; editor.ApplyColor(Colors.Gold); Ensure(editor.Surface.Selected?.FillColor == Colors.Gold, "Fill target overwrote stroke or ignored color");
                editor.Undo(); Ensure(editor.Surface.Items[0].FillColor == null, "Fill color is not reversible"); editor.Redo();
                editor.Surface.SelectAt(new(140, 132)); await HotkeyCaptureTests.PressAsync(owner, Key.Delete); Ensure(editor.Surface.Count == 0, "Actual Delete key failed"); editor.Undo(); Ensure(editor.Surface.Count == 1, "Undo Delete failed");
                editor.SelectTool(AnnotationTool.Freehand); editor.Surface.SetCrop(new(24, 16, 580, 420)); scroll.ScrollToHorizontalOffset(40); scroll.ScrollToVerticalOffset(34); owner.UpdateLayout();
                var samplePoint = new Point(286, 242); var physical = editor.Surface.PointToScreen(samplePoint);
                Color expected = Pixel(editor.Surface.Export(), (int)samplePoint.X, (int)samplePoint.Y); Color prior = editor.CurrentColor; int count = editor.Surface.Count;
                editor.StartEyedropper(false); Ensure(editor.PickingColor && !editor.Handles.IsHitTestVisible, "Picker did not isolate canvas controls");
                await HotkeyCaptureTests.PressAsync(owner, Key.Escape); Ensure(!editor.PickingColor && editor.CurrentColor == prior && editor.Surface.Count == count && owner.IsVisible, "Esc changed color, drew a stroke or closed the editor");
                editor.StartEyedropper(false); await HotkeyCaptureTests.DragAsync(owner, physical, physical);
                Ensure(!editor.PickingColor && editor.CurrentColor == expected && editor.Surface.Count == count, $"Picker sampled the wrong source pixel after zoom/crop/scroll ({AnnotationColors.Hex(editor.CurrentColor)} vs {AnnotationColors.Hex(expected)})");
            }
            finally { owner.Close(); }
        });
        await check("Screen eyedropper near bottom-right edge avoids its preview and keeps the physical pixel color", async () =>
        {
            var screen = Forms.Screen.PrimaryScreen!; Color expected = Color.FromRgb(37, 128, 91);
            var owner = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Width = 260, Height = 230, Background = new SolidColorBrush(expected) };
            var surface = new AnnotationSurface(Solid(260, 230, expected)); owner.Content = surface;
            Color? chosen = null; string? failure = null; var oldPointer = Forms.Cursor.Position;
            using var picker = new PixelEyedropper(owner, surface, color => chosen = color, message => failure = message);
            try
            {
                owner.Show(); Native.Place(owner, new System.Drawing.Rectangle(screen.Bounds.Right - 260, screen.Bounds.Bottom - 230, 260, 230));
                await HotkeyCaptureTests.ActivateAsync(owner); owner.UpdateLayout();
                var pointer = new System.Drawing.Point(screen.Bounds.Right - 8, screen.Bounds.Bottom - 8); Forms.Cursor.Position = pointer; await Task.Delay(60);
                picker.Begin(true); await Task.Delay(140); // Observe more than two 45 ms updates.
                Ensure(failure == null && picker.Active, "Screen eyedropper did not start: " + failure);
                Ensure(picker.SampledColor == expected, $"Screen preview fed itself into sampling ({picker.SampledColor} vs {expected})");
                var sample = new Rect(pointer.X - 4, pointer.Y - 4, 9, 9);
                Ensure(picker.PreviewPhysicalBounds is { } bounds && !bounds.IntersectsWith(sample), "Screen magnifier overlaps its physical 9 x 9 sample block");
                picker.Confirm(); Ensure(chosen == expected && !picker.Active, "Confirm changed the accurate screen pixel color");
            }
            finally { picker.Cancel(); owner.Close(); Forms.Cursor.Position = oldPointer; }
        });
    }
    private static BitmapSource Solid(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4]; for (int p = 0; p < pixels.Length; p += 4) { pixels[p] = color.B; pixels[p + 1] = color.G; pixels[p + 2] = color.R; pixels[p + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
    }
    private static BitmapSource TestGrid(int width, int height)
    {
        var pixels = new byte[width * height * 4]; for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { int p = (y * width + x) * 4; pixels[p] = (byte)((x / 12 * 11 + y / 12 * 3) % 256); pixels[p + 1] = (byte)((x / 12 * 5 + y / 12 * 9) % 256); pixels[p + 2] = (byte)((x / 12 * 13 + y / 12 * 7) % 256); pixels[p + 3] = 255; }
        var image = BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
    }
    private static Color Pixel(BitmapSource image, int x, int y) { var bytes = new byte[4]; new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0); return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]); }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
