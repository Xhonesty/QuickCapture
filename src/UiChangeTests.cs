using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace QuickCapture;

internal static class UiChangeTests
{
    internal static string PreviewDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory.Parent != null && !File.Exists(Path.Combine(directory.FullName, "src", "QuickCapture.csproj"))) directory = directory.Parent;
            string root = File.Exists(Path.Combine(directory.FullName, "src", "QuickCapture.csproj")) ? directory.FullName : AppContext.BaseDirectory;
            return Path.Combine(root, "artifacts", "screenshots");
        }
    }
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        Directory.CreateDirectory(PreviewDirectory);
        await check("Live light/dark switching, control colors and settings persistence", async () =>
        {
            ThemeService.Apply("Dark");
            var main = new MainWindow();
            try
            {
                main.Show(); await Task.Delay(150);
                Render(main, "main-dark.png");
                var button = (Button)main.FindName("ThemeToggle");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Ensure(ThemeService.Current == "Light", "Theme switch did not apply");
                Ensure(Settings.Load().Theme == "Light", "Theme choice was not saved");
                Ensure(main.Background == ThemeService.Brush("WindowBackground"), "Existing window did not update");
                Ensure(((SolidColorBrush)main.Background).Color.R > 200, "Light background remains dark");
                Ensure(((SolidColorBrush)main.Foreground).Color.R < 100, "Light text has insufficient contrast");
                var modes = (ComboBox)main.FindName("ShotMode"); modes.IsDropDownOpen = true; await Task.Delay(80); modes.IsDropDownOpen = false;
                Render(main, "main-light.png");
                using var hotkeys = new HotkeyService(new System.Windows.Interop.WindowInteropHelper(main).Handle);
                var settings = new SettingsWindow(main, Settings.Load(), hotkeys);
                settings.Show(); await Task.Delay(100); Render(settings, "settings-light.png"); settings.Close();
                var bar = new RecordingBar("Ctrl+Alt+R", () => { }); bar.Show(); bar.SetRecording(); await Task.Delay(300); Render(bar, "recording-light.png"); bar.Close();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Ensure(ThemeService.Current == "Dark" && Settings.Load().Theme == "Dark", "Reverse theme switch failed");
            }
            finally { main.Hide(); ThemeService.Apply("Dark"); }
        });
        await check("Floating toolbar avoids selection and stays inside screen at every edge", () =>
        {
            var available = new Rect(8, 8, 1500, 840); var size = new Size(880, 50);
            foreach (var selection in new[] { new Rect(400, 200, 500, 260), new Rect(1050, 700, 350, 100), new Rect(8, 8, 40, 40), new Rect(1440, 150, 60, 130) })
            {
                var placement = new Rect(ToolbarPlacement.Place(selection, size, available), size);
                Ensure(available.Contains(placement), "Toolbar left visible work area");
                Ensure(!selection.IntersectsWith(placement), "Toolbar obscures selection when outside space is available");
            }
            var full = new Rect(ToolbarPlacement.Place(new(0, 0, 1536, 864), size, available), size);
            Ensure(available.Contains(full), "Full-screen toolbar is inaccessible");
            return Task.CompletedTask;
        });
        await check("In-place editing retains original pixels, exports clean PNG and reselects", async () =>
        {
            var screen = Forms.Screen.PrimaryScreen!; var image = SyntheticDesktop(screen.Bounds.Width, screen.Bounds.Height);
            var done = new TaskCompletionSource<bool>(); string? saved = null;
            var settings = new Settings { OutputDirectory = Paths.DefaultOutput };
            var selection = new SelectionWindow(screen, image, false, _ => done.TrySetResult(true), settings, path => saved = path);
            var region = new SavedRegion(screen.Bounds.X + 260, screen.Bounds.Y + 230, Math.Min(850, screen.Bounds.Width - 280), Math.Min(430, screen.Bounds.Height - 250), screen.DeviceName);
            try
            {
                selection.Show(); await Task.Delay(120); selection.BeginEditing(region); selection.UpdateLayout();
                Ensure(!done.Task.IsCompleted && selection.IsVisible, "Selection closed before user finished editing");
                var editor = selection.Editor!;
                var physical = editor.Surface.PointToScreen(new Point(0, 0));
                Ensure(Math.Abs(physical.X - region.X) < 1 && Math.Abs(physical.Y - region.Y) < 1, "Editor moved away from original selection");
                var end = editor.Surface.PointToScreen(new Point(region.Width, region.Height));
                Ensure(Math.Abs(end.X - physical.X - region.Width) < 1 && Math.Abs(end.Y - physical.Y - region.Height) < 1, "DPI altered original display size");
                editor.Surface.Add(new(AnnotationTool.Arrow, new(80, 150), new(330, 90), Colors.OrangeRed));
                editor.Surface.Add(new(AnnotationTool.Rectangle, new(380, 190), new(720, 340), Colors.OrangeRed));
                editor.Surface.Add(new(AnnotationTool.Freehand, new(50, 360), new(220, 365), Colors.OrangeRed,
                    Points: new[] { new Point(50, 360), new Point(90, 345), new Point(130, 365), new Point(170, 345), new Point(220, 365) }, Width: 6));
                editor.Surface.Add(new(AnnotationTool.MosaicBrush, new(100, 238), new(345, 238), Colors.White,
                    Points: new[] { new Point(100, 238), new Point(220, 238), new Point(345, 238) }, Width: 26));
                editor.Surface.Undo(); editor.Surface.Redo();
                Render(selection, "selection-dark.png");
                ThemeService.Apply("Light"); selection.UpdateLayout(); Render(selection, "selection-light.png");
                string first = editor.SaveImage();
                var exported = CaptureService.Load(first);
                Ensure(exported.PixelWidth == region.Width && exported.PixelHeight == region.Height, "Selection export includes surrounding UI or was resized");
                var original = new CroppedBitmap(image, new Int32Rect(region.X - screen.Bounds.X, region.Y - screen.Bounds.Y, region.Width, region.Height));
                var expected = new byte[4]; var actual = new byte[4];
                original.CopyPixels(new Int32Rect(2, 2, 1, 1), expected, 4, 0); exported.CopyPixels(new Int32Rect(2, 2, 1, 1), actual, 4, 0);
                Ensure(expected[0] == actual[0] && expected[1] == actual[1] && expected[2] == actual[2], "Selection mask/frame leaked into exported image");
                editor.Surface.Add(new(AnnotationTool.Text, new(20, 20), new(20, 20), Colors.OrangeRed, "原位标注"));
                string second = editor.SaveImage(); Ensure(second != first && saved == second, "Saved image cache did not invalidate after editing");
                selection.Reselect(); Ensure(selection.Editor == null && !done.Task.IsCompleted, "Reselect ended capture instead of resetting selection");
                selection.BeginEditing(region); Ensure(selection.Editor!.Surface.Count == 0, "Reselect retained old annotations");
                var border = FindVisual<Border>(selection, b => b.Child is WrapPanel);
                var panel = (WrapPanel)border!.Child;
                var copy = FindVisual<Button>(panel, b => b.Name == "Tool_copy");
                copy!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150);
                Ensure(done.Task.IsCompleted && saved != null && File.Exists(saved), "Copy / auto-save button did not finish the screenshot");
            }
            finally { selection.Close(); ThemeService.Apply("Dark"); }
        });
        await check("Actual bottom-edge toolbar placement and light editor rendering", async () =>
        {
            var screen = Forms.Screen.PrimaryScreen!;
            var selection = new SelectionWindow(screen, SyntheticDesktop(screen.Bounds.Width, screen.Bounds.Height), false, _ => { }, new Settings());
            try
            {
                ThemeService.Apply("Light"); selection.Show(); await Task.Delay(100);
                int width = Math.Min(500, screen.Bounds.Width - 40), height = Math.Min(180, screen.Bounds.Height - 40);
                selection.BeginEditing(new(screen.Bounds.Right - width - 15, screen.Bounds.Bottom - height - 12, width, height, screen.DeviceName)); selection.UpdateLayout();
                Ensure(selection.ToolbarBounds.Bottom < selection.SelectionBounds.Top, "Bottom-edge toolbar did not move above selection");
                Ensure(selection.ToolbarBounds.Left >= 0 && selection.ToolbarBounds.Right <= selection.ActualWidth, "Toolbar was clipped horizontally");
                Render(selection, "selection-bottom-light.png");
                var editor = new EditorWindow(selection.Editor!.Surface.Export(), new Settings(), _ => { }); editor.Show(); await Task.Delay(100); Render(editor, "editor-light.png"); editor.Close();
            }
            finally { selection.Close(); ThemeService.Apply("Dark"); }
        });
        await FeatureTests.RunUiAsync(check);
        await HotkeyCaptureTests.RunUiAsync(check);
    }
    internal static void Render(Window window, string name)
        => RenderElement(window.Content as FrameworkElement ?? window, name);
    internal static void RenderElement(FrameworkElement window, string name)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var offset = VisualTreeHelper.GetOffset(window);
        // A root content element's margin is outside ActualWidth/ActualHeight,
        // but remains in the visual's offset when rendered from a live window.
        double width = window.ActualWidth + Math.Max(0, offset.X) + Math.Max(0, window.Margin.Right);
        double height = window.ActualHeight + Math.Max(0, offset.Y) + Math.Max(0, window.Margin.Bottom);
        var rendered = new RenderTargetBitmap((int)Math.Round(width * dpi.DpiScaleX), (int)Math.Round(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bounds = new Rect(0, 0, width, height);
        var brush = new VisualBrush(window)
        {
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds,
            ViewportUnits = BrushMappingMode.Absolute, Viewport = bounds,
            Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top
        };
        // Capture the element's local bounds rather than its visual offset in
        // the parent, so a root margin cannot shift and clip the screenshot.
        var capture = new DrawingVisual();
        using (var dc = capture.RenderOpen())
        {
            dc.DrawRectangle(Window.GetWindow(window)?.Background ?? Brushes.Transparent, null, bounds);
            dc.DrawRectangle(brush, null, bounds);
        }
        rendered.Render(capture);
        string path = Path.Combine(PreviewDirectory, name); if (File.Exists(path)) File.Delete(path); CaptureService.Save(rendered, path);
    }
    internal static BitmapSource SyntheticDesktop(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Text(string value, double x, double y, int size, string color) => dc.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), size, B(color), 1), new(x, y));
            dc.DrawRectangle(B("#E5EBF2"), null, new(0, 0, width, height));
            dc.DrawRoundedRectangle(B("#FFFFFF"), null, new(80, 100, width - 160, height - 180), 16, 16);
            dc.DrawRoundedRectangle(B("#EEF3F7"), null, new(80, 100, width - 160, 70), 16, 16);
            Text("轻截 · 工作笔记", 110, 118, 24, "#26384C"); Text("在原始位置完成截图和标注", 250, 225, 34, "#182B3E");
            Text("框选之后，直接在选区中画箭头、写文字或遮挡信息。", 250, 285, 22, "#5B6F83");
            for (int i = 0; i < 4; i++)
            {
                dc.DrawRoundedRectangle(B(i % 2 == 0 ? "#E3F3EC" : "#EBF0F6"), null, new(250, 355 + i * 95, Math.Max(100, width - 520), 70), 10, 10);
                Text(new[] { "01   保留原始位置与像素", "02   工具栏自动避让屏幕边缘", "03   复制 / 保存 / 贴图", "04   选择习惯的浅色或深色界面" }[i], 275, 373 + i * 95, 23, "#35556B");
            }
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    private static T? FindVisual<T>(DependencyObject root, Predicate<T> predicate) where T : DependencyObject
    {
        if (root is T item && predicate(item)) return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = FindVisual(VisualTreeHelper.GetChild(root, i), predicate); if (child != null) return child;
        }
        return null;
    }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
