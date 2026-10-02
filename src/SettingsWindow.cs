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
        Owner = owner; Title = "轻截 · 设置"; Width = 640; Height = 840; MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40); ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var shell = new DockPanel(); Content = shell;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 8, 24, 16) }; DockPanel.SetDock(buttons, Dock.Bottom); shell.Children.Add(buttons);
        var root = new StackPanel { Margin = new Thickness(24, 24, 24, 0) }; shell.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var general = UiDesign.Section(root, "通用设置");
        var folder = Field(general, "保存目录", settings.OutputDirectory);
        var browse = Ui.Button("选择文件夹", () =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择截图和视频保存目录", InitialDirectory = Directory.Exists(folder.Text) ? folder.Text : AppContext.BaseDirectory };
            if (picker.ShowDialog(this) == true) folder.Text = picker.FolderName;
        }); browse.Margin = new Thickness(0, 8, 0, 14); browse.HorizontalAlignment = HorizontalAlignment.Left; general.Children.Add(browse);
        var shot = HotkeyField(general, "截图快捷键", settings.ScreenshotHotkey, "ScreenshotHotkey");
        var record = HotkeyField(general, "录屏开始 / 停止快捷键", settings.RecordingHotkey, "RecordingHotkey");
        var hint = new TextBlock { Text = HotkeyCaptureBox.Instructions, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), MinHeight = 34 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); general.Children.Add(hint);
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
        general.Children.Add(new TextBlock { Text = "界面风格", Margin = new Thickness(0, 12, 0, 6) });
        var theme = new ComboBox { ItemsSource = new[] { "深色", "浅色" }, SelectedIndex = settings.Theme == "Light" ? 1 : 0 }; general.Children.Add(theme);
        var screenshots = UiDesign.Section(root, "截图设置");
        var shotFormat = UiDesign.Choice(screenshots, "默认截图格式", Enum.GetValues<ScreenshotFormat>().Select(ImageExportService.Label).ToArray(), (int)settings.ScreenshotFormat); shotFormat.Name = "DefaultScreenshotFormat";
        var shotQualityLabel = UiDesign.Text($"默认 JPG / WebP 质量：{settings.ScreenshotQuality} / 100"); screenshots.Children.Add(shotQualityLabel);
        var shotQuality = new Slider { Minimum = 1, Maximum = 100, Value = settings.ScreenshotQuality, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 0, 0, 16), Name = "DefaultScreenshotQuality" }; screenshots.Children.Add(shotQuality);
        shotQuality.ValueChanged += (_, _) => shotQualityLabel.Text = $"默认 JPG / WebP 质量：{shotQuality.Value:F0} / 100";
        var snap = new CheckBox { Content = "截图时自动吸附窗口（仍可拖动自由框选）", IsChecked = settings.SnapToWindow, Margin = new Thickness(0, 0, 0, 8) }; screenshots.Children.Add(snap);
        var recordings = UiDesign.Section(root, "录屏设置");
        var recordFormat = UiDesign.Choice(recordings, "默认录屏格式", new[] { "MP4（H.264 + AAC）", "WebM（VP9 + Opus）", "GIF（无声音）" }, (int)settings.RecordingFormat); recordFormat.Name = "DefaultRecordingFormat";
        var recordQuality = UiDesign.Choice(recordings, "默认录屏质量", new[] { "低", "中", "高" }, (int)settings.RecordingQuality); recordQuality.Name = "DefaultRecordingQuality";
        var gifRates = new[] { 5, 10, 15, 20, 30 }; var gifFps = UiDesign.Choice(recordings, "默认 GIF 帧率", gifRates.Select(f => $"{f} FPS").ToArray(), Math.Max(0, Array.IndexOf(gifRates, settings.GifFps)));
        recordings.Children.Add(UiDesign.Text("GIF 不包含声音；帧率和尺寸越高，文件通常越大。", true));
        var snapRecord = new CheckBox { Content = "区域录屏时自动吸附窗口（仍可拖动自由框选）", IsChecked = settings.SnapRecordingToWindow, Margin = new Thickness(0, 0, 0, 8) }; recordings.Children.Add(snapRecord);
        var hardware = new CheckBox { Content = "录制时使用硬件编码（失败时可关闭）", IsChecked = settings.HardwareEncoding, Margin = new Thickness(0, 0, 0, 8) }; recordings.Children.Add(hardware);
        var tools = UiDesign.Section(root, "媒体工具");
        var toolsPath = UiDesign.Field(tools, "FFmpeg 工具目录（留空自动检测）", settings.MediaToolsPath);
        var browseTools = Ui.Button("选择工具目录", () => { var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择包含 ffmpeg.exe / ffprobe.exe 的目录" }; if (picker.ShowDialog(this) == true) toolsPath.Text = picker.FolderName; }); browseTools.HorizontalAlignment = HorizontalAlignment.Left; tools.Children.Add(browseTools);
        tools.Children.Add(UiDesign.Text("优先使用指定目录或系统工具；不完整时使用程序附带的 FFmpeg / ffprobe。", true));
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
    private static TextBox Field(Panel root, string label, string value)
    {
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 5) });
        var field = new TextBox { Text = value }; root.Children.Add(field); return field;
    }
    private static HotkeyCaptureBox HotkeyField(Panel root, string label, string value, string name)
    {
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 5) });
        var field = new HotkeyCaptureBox(value, label) { Name = name }; root.Children.Add(field); return field;
    }
}
