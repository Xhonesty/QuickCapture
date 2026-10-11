using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

// Documentation images are rendered from the actual updated application UI.
// Only the screenshot's subject (a clean note) is generated here.
internal static class AcceptanceArtifacts
{
    internal static async Task RunDemoAsync(bool keepEditorOpen = false, bool panelOnly = false)
    {
        string? previousRoot = Paths.TestRoot;
        string previousTheme = ThemeService.Current;
        string demoRoot = Path.Combine(previousRoot ?? AppContext.BaseDirectory, "InterfaceDemo", Guid.NewGuid().ToString("N"));
        Paths.TestRoot = demoRoot;
        var settings = new Settings { Theme = "Light", AutoSaveScreenshot = false, OutputDirectory = Path.Combine(demoRoot, "Captures"), ScreenshotHotkey = "Ctrl+Alt+F8", RecordingHotkey = "Ctrl+Alt+F9" };
        settings.Save(); ThemeService.Apply("Light");
        Directory.CreateDirectory(UiChangeTests.PreviewDirectory);
        Directory.CreateDirectory(settings.OutputDirectory);
        foreach (var (name, minutes) in new[] { ("截图_产品讨论纪要.png", 12), ("截图_使用说明.png", 5), ("截图_透明文字标注.png", 0) })
        {
            string path = Path.Combine(settings.OutputDirectory, name); CaptureService.Save(NoteImage(900, 540), path); File.SetLastWriteTime(path, DateTime.Now.AddMinutes(-minutes));
        }
        MainWindow? main = null; EditorWindow? window = null; bool preserveDemoState = false;
        try
        {
            main = new MainWindow(); main.Closing += (_, e) => e.Cancel = false;
            main.Show(); await Task.Delay(180); main.UpdateLayout();
            UiChangeTests.Render(main, "main-recent-files-light.png");
            if (panelOnly)
            {
                var list = (ListBox)main.FindName("RecentList");
                if (list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem row)
                {
                    row.Focus(); main.UpdateLayout(); UiChangeTests.Render(main, "main-file-actions-light.png");
                }
                ThemeService.Apply("Dark"); main.UpdateLayout(); UiChangeTests.Render(main, "main-recent-files-dark.png");
                using var hotkeys = new HotkeyService(new System.Windows.Interop.WindowInteropHelper(main).Handle);
                var dialog = new SettingsWindow(main, settings, hotkeys);
                try
                {
                    dialog.Show(); await Task.Delay(100); dialog.UpdateLayout(); UiChangeTests.Render(dialog, "settings-panel-dark.png");
                    ThemeService.Apply("Light"); dialog.UpdateLayout(); UiChangeTests.Render(dialog, "settings-panel-light.png");
                }
                finally { dialog.Close(); }
                return;
            }
            main.Close(); main = null;

            window = new EditorWindow(NoteImage(900, 540), settings, _ => { }) { Width = 1050, Height = 710 };
            window.Show(); await HotkeyCaptureTests.ActivateAsync(window); window.UpdateLayout();
            var editor = window.Editor; editor.SelectTool(AnnotationTool.Text); editor.BeginText(new(80, 340));
            editor.TextInput!.Text = "直接在截图上输入文字\n透明预览 · 中文与 English";
            editor.SetTextStyle("Microsoft YaHei UI", 28, true, TextAlignment.Left);
            editor.ApplyColor(Color.FromRgb(21, 114, 95));
            editor.TextInput.CaretIndex = editor.TextInput.Text.Length;
            await Task.Delay(100); window.UpdateLayout();
            UiChangeTests.Render(window, "text-in-place-light.png");
            if (keepEditorOpen)
            {
                window.Closed += (_, _) => { Paths.TestRoot = previousRoot; ThemeService.Apply(previousTheme); Application.Current.Shutdown(); };
                preserveDemoState = true; window = null; return;
            }
        }
        finally
        {
            main?.Close(); window?.Close();
            if (!preserveDemoState) { Paths.TestRoot = previousRoot; ThemeService.Apply(previousTheme); }
        }
    }

    internal static BitmapSource NoteImage(int width, int height, double dpi = 96)
    {
        Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            void Text(string value, double x, double y, double size, string color, bool bold = false)
                => dc.DrawText(new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, Brush(color), 1), new(x, y));
            dc.DrawRectangle(Brush("#EDF3F4"), null, new Rect(0, 0, width, height));
            dc.DrawRoundedRectangle(Brush("#FFFFFF"), new Pen(Brush("#D2E0E0"), 1), new Rect(28, 24, width - 56, height - 48), 14, 14);
            Text("轻截使用笔记", 65, 48, 30, "#183C42", true);
            Text("QUICKCAPTURE  /  SCREENSHOT NOTES", 67, 96, 13, "#648487");
            dc.DrawLine(new Pen(Brush("#D8E5E5"), 1), new(65, 133), new(width - 65, 133));
            Text("把屏幕内容保存成清楚的说明", 65, 160, 23, "#244C53", true);
            Text("01   框选区域、选择窗口，或截取整个桌面。", 65, 207, 20, "#46666C");
            Text("02   添加箭头、形状和文字，再复制或保存。", 65, 246, 20, "#46666C");
            Text("03   最近文件集中查看，录屏支持裁剪与导出。", 65, 285, 20, "#46666C");
            Text("一张截图，让沟通更清楚。", 65, height - 84, 16, "#6D8589");
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        var result = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Pbgra32, null, pixels, width * 4); result.Freeze(); return result;
    }
}
