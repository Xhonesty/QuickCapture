using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace QuickCapture;

internal sealed class SettingsWindow : Window
{
    public SettingsWindow(Window owner, Settings settings, HotkeyService hotkeys)
    {
        Ui.Theme(this);
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        Owner = owner; Title = "轻截 · 设置"; Width = 560; Height = 640; MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40); ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var shell = new DockPanel(); Content = shell;
        var page = UiDesign.Padding("PagePadding");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(page.Left, UiDesign.Number("SpaceS"), page.Right, UiDesign.Number("SpaceL")) }; DockPanel.SetDock(buttons, Dock.Bottom); shell.Children.Add(buttons);
        var root = new StackPanel { Margin = new Thickness(page.Left, UiDesign.Number("SpaceL"), page.Right, 0) }; shell.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var general = UiDesign.Section(root, "通用设置");
        var folder = new TextBox { Text = settings.OutputDirectory };
        var browse = Ui.Button("选择文件夹", () =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择截图和视频保存目录", InitialDirectory = Directory.Exists(folder.Text) ? folder.Text : AppContext.BaseDirectory };
            if (picker.ShowDialog(this) == true) folder.Text = picker.FolderName;
        }); PathRow(general, "保存目录", folder, browse);
        var shortcutRow = Columns(general, 2, 2, 1.2);
        var shot = HotkeyField(shortcutRow[0], "截图快捷键", settings.ScreenshotHotkey, "ScreenshotHotkey");
        var record = HotkeyField(shortcutRow[1], "录屏开始 / 停止", settings.RecordingHotkey, "RecordingHotkey");
        var theme = Choice(shortcutRow[2], "界面风格", new[] { "深色", "浅色" }, settings.Theme == "Light" ? 1 : 0);
        var hint = UiDesign.Text(HotkeyCaptureBox.Instructions, true); hint.Margin = new Thickness(0, UiDesign.Number("SpaceS"), 0, 0); general.Children.Add(hint);
        shot.HintChanged += text => hint.Text = text;
        record.HintChanged += text => hint.Text = text;
        IDisposable? capture = null;
        Loaded += (_, _) => capture = hotkeys.BeginCapture((key, modifiers) =>
        {
            if (!IsActive) return;
            if (shot.IsKeyboardFocused) shot.CaptureKey(key, modifiers);
            else if (record.IsKeyboardFocused) record.CaptureKey(key, modifiers);
        });
        Closed += (_, _) => capture?.Dispose();
        var screenshots = UiDesign.Section(root, "截图设置");
        var shotRow = Columns(screenshots, 1, 1.2);
        var shotFormat = InlineChoice(shotRow[0], "默认格式", Enum.GetValues<ScreenshotFormat>().Select(ImageExportService.Label).ToArray(), (int)settings.ScreenshotFormat); shotFormat.Name = "DefaultScreenshotFormat";
        var shotQuality = new Slider { Minimum = 1, Maximum = 100, Value = settings.ScreenshotQuality, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center, Name = "DefaultScreenshotQuality", ToolTip = "默认 JPG / WebP 质量" };
        var shotQualityLabel = InlineField(shotRow[1], $"质量 {settings.ScreenshotQuality} / 100", shotQuality);
        shotQuality.ValueChanged += (_, _) => shotQualityLabel.Text = $"质量 {shotQuality.Value:F0} / 100";
        var snap = new CheckBox { Content = "截图时自动吸附窗口（仍可拖动自由框选）", IsChecked = settings.SnapToWindow, Margin = new Thickness(0, UiDesign.Number("SpaceS"), 0, 0) }; screenshots.Children.Add(snap);
        var recordings = UiDesign.Section(root, "录屏设置");
        var recordingRow = Columns(recordings, 2.2, 1, 1.35);
        var recordFormat = InlineChoice(recordingRow[0], "格式", new[] { "MP4（H.264 + AAC）", "WebM（VP9 + Opus）", "GIF（无声音）" }, (int)settings.RecordingFormat); recordFormat.Name = "DefaultRecordingFormat";
        var recordQuality = InlineChoice(recordingRow[1], "质量", new[] { "低", "中", "高" }, (int)settings.RecordingQuality); recordQuality.Name = "DefaultRecordingQuality";
        var gifRates = new[] { 5, 10, 15, 20, 30 }; var gifFps = InlineChoice(recordingRow[2], "GIF 帧率", gifRates.Select(f => $"{f} FPS").ToArray(), Math.Max(0, Array.IndexOf(gifRates, settings.GifFps)));
        var recordingChecks = Columns(recordings, 1, 1);
        foreach (var cell in recordingChecks) cell.Margin = new Thickness(cell.Margin.Left, UiDesign.Number("SpaceS"), 0, 0);
        var snapRecord = new CheckBox { Content = "区域录屏自动吸附窗口", ToolTip = "仍可拖动自由框选", IsChecked = settings.SnapRecordingToWindow }; recordingChecks[0].Children.Add(snapRecord);
        var hardware = new CheckBox { Content = "使用硬件编码", ToolTip = "编码失败时可关闭", IsChecked = settings.HardwareEncoding }; recordingChecks[1].Children.Add(hardware);
        var gifHint = UiDesign.Text("GIF 不包含声音；帧率和尺寸越高，文件通常越大。", true); gifHint.Margin = new Thickness(0, UiDesign.Number("SpaceS"), 0, 0); recordings.Children.Add(gifHint);
        var tools = UiDesign.Section(root, "媒体工具");
        var toolsPath = new TextBox { Text = settings.MediaToolsPath, ToolTip = "FFmpeg 工具目录，留空自动检测" };
        var browseTools = Ui.Button("选择目录", () => { var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择包含 ffmpeg.exe / ffprobe.exe 的目录" }; if (picker.ShowDialog(this) == true) toolsPath.Text = picker.FolderName; }); PathRow(tools, "FFmpeg", toolsPath, browseTools);
        var toolsHint = UiDesign.Text("工具目录留空自动检测；指定目录或系统工具不完整时使用程序附带的 FFmpeg / ffprobe。", true); toolsHint.Margin = new Thickness(0, UiDesign.Number("SpaceS"), 0, 0); tools.Children.Add(toolsHint);
        if (root.Children[root.Children.Count - 1] is Border toolsPanel) toolsPanel.Margin = new Thickness(toolsPanel.Margin.Left, toolsPanel.Margin.Top, toolsPanel.Margin.Right, 0);
        buttons.Children.Add(Ui.Button("取消", () => DialogResult = false));
        buttons.Children.Add(Ui.Button("保存设置", () =>
        {
            string oldShot = settings.ScreenshotHotkey, oldRecord = settings.RecordingHotkey, oldFolder = settings.OutputDirectory, oldTheme = settings.Theme;
            bool oldHardware = settings.HardwareEncoding, oldSnap = settings.SnapToWindow, oldSnapRecord = settings.SnapRecordingToWindow, configured = false;
            var oldFormats = (settings.ScreenshotFormat, settings.ScreenshotQuality, settings.RecordingFormat, settings.RecordingQuality, settings.GifFps, settings.MediaToolsPath);
            try
            {
                if (toolsPath.Text.Trim() != "" && !Directory.Exists(toolsPath.Text.Trim())) throw new ArgumentException("媒体工具目录不存在。");
                string fullPath = Path.GetFullPath(folder.Text.Trim()); Directory.CreateDirectory(fullPath);
                string probe = Path.Combine(fullPath, $".quickcapture-write-{Guid.NewGuid():N}");
                File.WriteAllText(probe, ""); File.Delete(probe);
                hotkeys.Configure(shot.Text.Trim(), record.Text.Trim()); configured = true;
                settings.OutputDirectory = fullPath; settings.ScreenshotHotkey = shot.Text.Trim(); settings.RecordingHotkey = record.Text.Trim(); settings.HardwareEncoding = hardware.IsChecked == true;
                settings.Theme = theme.SelectedIndex == 1 ? "Light" : "Dark";
                settings.SnapToWindow = snap.IsChecked == true;
                settings.SnapRecordingToWindow = snapRecord.IsChecked == true;
                settings.ScreenshotFormat = (ScreenshotFormat)shotFormat.SelectedIndex; settings.ScreenshotQuality = (int)shotQuality.Value;
                settings.RecordingFormat = (RecordingFormat)recordFormat.SelectedIndex; settings.RecordingQuality = (ExportQuality)recordQuality.SelectedIndex;
                settings.GifFps = gifRates[Math.Max(0, gifFps.SelectedIndex)]; settings.MediaToolsPath = toolsPath.Text.Trim();
                settings.Save(); ThemeService.Apply(settings.Theme); DialogResult = true;
            }
            catch (Exception ex)
            {
                (settings.ScreenshotFormat, settings.ScreenshotQuality, settings.RecordingFormat, settings.RecordingQuality, settings.GifFps, settings.MediaToolsPath) = oldFormats;
                settings.ScreenshotHotkey = oldShot; settings.RecordingHotkey = oldRecord; settings.OutputDirectory = oldFolder; settings.HardwareEncoding = oldHardware;
                settings.Theme = oldTheme;
                settings.SnapToWindow = oldSnap;
                settings.SnapRecordingToWindow = oldSnapRecord;
                if (configured) try { hotkeys.Configure(oldShot, oldRecord); } catch (Exception rollback) { ErrorLog.Write(rollback); }
                Ui.Error(this, ex);
            }
        }));
    }
    private static StackPanel[] Columns(Panel parent, params double[] widths)
    {
        var row = new Grid(); parent.Children.Add(row);
        var cells = new StackPanel[widths.Length];
        for (int i = 0; i < widths.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i], GridUnitType.Star) });
            cells[i] = new StackPanel { Margin = new Thickness(i == 0 ? 0 : UiDesign.Number("SpaceS"), 0, 0, 0) };
            Grid.SetColumn(cells[i], i); row.Children.Add(cells[i]);
        }
        return cells;
    }
    private static TextBlock InlineField(Panel parent, string label, FrameworkElement field)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition());
        var text = UiDesign.Text(label); text.TextWrapping = TextWrapping.NoWrap; text.VerticalAlignment = VerticalAlignment.Center; text.Margin = new Thickness(0, 0, UiDesign.Number("SpaceS"), 0); row.Children.Add(text);
        Grid.SetColumn(field, 1); row.Children.Add(field); parent.Children.Add(row); return text;
    }
    private static ComboBox InlineChoice(Panel parent, string label, object items, int selected)
    {
        var box = new ComboBox { ItemsSource = (System.Collections.IEnumerable)items, SelectedIndex = selected, MinWidth = 0 }; InlineField(parent, label, box); return box;
    }
    private static ComboBox Choice(Panel parent, string label, object items, int selected)
    {
        parent.Children.Add(UiDesign.Text(label)); var box = new ComboBox { ItemsSource = (System.Collections.IEnumerable)items, SelectedIndex = selected, MinWidth = 0 }; parent.Children.Add(box); return box;
    }
    private static void PathRow(Panel parent, string label, TextBox field, Button browse)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, UiDesign.Number("SpaceS")) };
        browse.Margin = new Thickness(UiDesign.Number("SpaceS"), 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse);
        var text = UiDesign.Text(label); text.Margin = new Thickness(0, 0, UiDesign.Number("SpaceS"), 0); text.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(text, Dock.Left); row.Children.Add(text);
        row.Children.Add(field); parent.Children.Add(row);
    }
    private static HotkeyCaptureBox HotkeyField(Panel root, string label, string value, string name)
    {
        root.Children.Add(UiDesign.Text(label));
        var field = new HotkeyCaptureBox(value, label) { Name = name }; root.Children.Add(field); return field;
    }
}
