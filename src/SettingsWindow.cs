using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace QuickCapture;

internal sealed class SettingsWindow : Window
{
    public SettingsWindow(Window owner, Settings settings, HotkeyService hotkeys)
    {
        Ui.Theme(this);
        Owner = owner; Title = "轻截 · 设置"; Width = 580; Height = 565; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(24) }; Content = root;
        var folder = Field(root, "保存目录", settings.OutputDirectory);
        var browse = Ui.Button("选择文件夹", () =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择截图和视频保存目录", InitialDirectory = Directory.Exists(folder.Text) ? folder.Text : AppContext.BaseDirectory };
            if (picker.ShowDialog(this) == true) folder.Text = picker.FolderName;
        }); browse.Margin = new Thickness(0, 8, 0, 14); browse.HorizontalAlignment = HorizontalAlignment.Left; root.Children.Add(browse);
        var shot = Field(root, "截图快捷键", settings.ScreenshotHotkey);
        var record = Field(root, "录屏开始 / 停止快捷键", settings.RecordingHotkey);
        root.Children.Add(new TextBlock { Text = "界面风格", Margin = new Thickness(0, 12, 0, 6) });
        var theme = new ComboBox { ItemsSource = new[] { "深色", "浅色" }, SelectedIndex = settings.Theme == "Light" ? 1 : 0 }; root.Children.Add(theme);
        var snap = new CheckBox { Content = "截图时自动吸附窗口（仍可拖动自由框选）", IsChecked = settings.SnapToWindow, Margin = new Thickness(0, 14, 0, 0) }; root.Children.Add(snap);
        var hardware = new CheckBox { Content = "使用硬件编码（失败时可关闭）", IsChecked = settings.HardwareEncoding, Margin = new Thickness(0, 14, 0, 14) }; root.Children.Add(hardware);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Ui.Button("取消", () => DialogResult = false));
        buttons.Children.Add(Ui.Button("保存设置", () =>
        {
            string oldShot = settings.ScreenshotHotkey, oldRecord = settings.RecordingHotkey, oldFolder = settings.OutputDirectory, oldTheme = settings.Theme;
            bool oldHardware = settings.HardwareEncoding, oldSnap = settings.SnapToWindow, configured = false;
            try
            {
                string fullPath = Path.GetFullPath(folder.Text.Trim()); Directory.CreateDirectory(fullPath);
                string probe = Path.Combine(fullPath, $".quickcapture-write-{Guid.NewGuid():N}");
                File.WriteAllText(probe, ""); File.Delete(probe);
                hotkeys.Configure(shot.Text.Trim(), record.Text.Trim()); configured = true;
                settings.OutputDirectory = fullPath; settings.ScreenshotHotkey = shot.Text.Trim(); settings.RecordingHotkey = record.Text.Trim(); settings.HardwareEncoding = hardware.IsChecked == true;
                settings.Theme = theme.SelectedIndex == 1 ? "Light" : "Dark";
                settings.SnapToWindow = snap.IsChecked == true;
                settings.Save(); ThemeService.Apply(settings.Theme); DialogResult = true;
            }
            catch (Exception ex)
            {
                settings.ScreenshotHotkey = oldShot; settings.RecordingHotkey = oldRecord; settings.OutputDirectory = oldFolder; settings.HardwareEncoding = oldHardware;
                settings.Theme = oldTheme;
                settings.SnapToWindow = oldSnap;
                if (configured) try { hotkeys.Configure(oldShot, oldRecord); } catch (Exception rollback) { ErrorLog.Write(rollback); }
                Ui.Error(this, ex);
            }
        })); root.Children.Add(buttons);
    }
    private static TextBox Field(Panel root, string label, string value)
    {
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 5) });
        var field = new TextBox { Text = value }; root.Children.Add(field); return field;
    }
}
