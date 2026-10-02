using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal sealed class ScreenshotSaveWindow : Window
{
    internal string? SavedPath { get; private set; }
    internal ScreenshotSaveWindow(Window owner, BitmapSource image, Settings settings)
    {
        Ui.Theme(this); Owner = owner; Title = "轻截 · 保存截图"; Width = 560; Height = 540; MaxHeight = SystemParameters.WorkArea.Height - 40; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var shell = new DockPanel(); Content = shell;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 8, 24, 16) }; DockPanel.SetDock(buttons, Dock.Bottom); shell.Children.Add(buttons);
        var root = new StackPanel { Margin = new Thickness(24, 24, 24, 0) }; shell.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(UiDesign.Text($"{image.PixelWidth} × {image.PixelHeight} px", true));
        var folder = UiDesign.Field(root, "保存目录", settings.OutputDirectory);
        var browse = Ui.Button("选择文件夹", () => { var picker = new Microsoft.Win32.OpenFolderDialog(); if (picker.ShowDialog(this) == true) folder.Text = picker.FolderName; }); browse.HorizontalAlignment = HorizontalAlignment.Left; browse.Margin = new Thickness(0, 0, 0, 12); root.Children.Add(browse);
        var filename = UiDesign.Field(root, "文件名", Path.GetFileName(Paths.NewCapture(settings.OutputDirectory, ImageExportService.Extension(settings.ScreenshotFormat)))); filename.Name = "ImageFilename";
        var format = UiDesign.Choice(root, "截图格式", Enum.GetValues<ScreenshotFormat>().Select(ImageExportService.Label).ToArray(), (int)settings.ScreenshotFormat); format.Name = "ImageFormat";
        var qualityPanel = new StackPanel(); var qualityLabel = UiDesign.Text("质量"); qualityPanel.Children.Add(qualityLabel);
        var quality = new Slider { Minimum = 1, Maximum = 100, Value = settings.ScreenshotQuality, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 0, 16), Name = "ImageQuality" }; qualityPanel.Children.Add(quality); root.Children.Add(qualityPanel);
        void Update()
        {
            var selected = (ScreenshotFormat)format.SelectedIndex;
            qualityPanel.Visibility = selected is ScreenshotFormat.Jpg or ScreenshotFormat.WebP ? Visibility.Visible : Visibility.Collapsed;
            qualityLabel.Text = $"质量：{quality.Value:F0} / 100";
            filename.Text = ImageExportService.CorrectExtension(filename.Text, selected);
        }
        format.SelectionChanged += (_, _) => Update(); quality.ValueChanged += (_, _) => Update(); Update();
        root.Children.Add(UiDesign.Text("本次格式与质量仅用于这次保存，不修改默认设置。", true));
        buttons.Children.Add(Ui.Button("取消", () => DialogResult = false)); buttons.Children.Add(Ui.Button("保存", () =>
        {
            try
            {
                string name = filename.Text.Trim();
                if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("请输入有效的文件名，不要包含目录。");
                string path = ImageExportService.CorrectExtension(Path.Combine(Path.GetFullPath(folder.Text.Trim()), name), (ScreenshotFormat)format.SelectedIndex);
                bool overwrite = File.Exists(path);
                if (overwrite && MessageBox.Show(this, "目标文件已存在，是否替换？", "轻截", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                ImageExportService.Save(image, path, (ScreenshotFormat)format.SelectedIndex, (int)quality.Value, overwrite); SavedPath = path; DialogResult = true;
            }
            catch (Exception ex) { Ui.Error(this, ex); }
        }));
    }
}
