using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Input;
using System.Threading;
using Shape = System.Windows.Shapes.Shape;

namespace QuickCapture;

internal sealed class SettingsWindow : Window
{
    internal TabControl CategoryTabs { get; } = new() { Name = "SettingsCategories" };
    internal System.Threading.Tasks.Task DeviceInitialization { get; private set; } = System.Threading.Tasks.Task.CompletedTask;
    public SettingsWindow(Window owner, Settings settings, HotkeyService hotkeys)
    {
        Ui.Theme(this);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("PanelStyles.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("SettingsStyles.xaml", UriKind.Relative) });
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        Owner = owner; Title = "设置";
        Width = owner is MainWindow ? (owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width) : 480;
        Height = owner is MainWindow ? (owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height) : 680;
        ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(14) });
        var shell = new DockPanel { Margin = new Thickness(16, 12, 16, 14) };
        var surface = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0.5), Child = shell };
        surface.SetResourceReference(Border.BackgroundProperty, "WindowBackground"); surface.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); Content = surface;
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(title, Dock.Top); shell.Children.Add(title);
        var close = StyledButton("", Close, "IconButton"); close.ToolTip = "关闭设置";
        close.SetResourceReference(Button.BackgroundProperty, "WindowBackground");
        close.Content = LineIcon("M 2,2 L 10,10 M 10,2 L 2,10", 12, "TextSecondary"); DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        title.Children.Add(new TextBlock { Text = "设置", Name = "SettingsTitle", FontSize = 16, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
        title.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); shell.Children.Add(buttons);
        // Each category owns its controls for the lifetime of the dialog. Switching
        // changes visibility only; saving submits all categories in one transaction.
        CategoryTabs.Style = (Style)FindResource("SettingsCategories"); shell.Children.Add(CategoryTabs);
        var generalPage = Category("通用与媒体工具");
        var screenshotPage = Category("截屏");
        var recordingPage = Category("录屏");
        CategoryTabs.SelectedIndex = 0;
        CategoryTabs.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (!CategoryTabs.Items.Cast<TabItem>().Any(tab => tab.IsKeyboardFocused)) return;
            int index = e.Key switch
            {
                Key.Left => (CategoryTabs.SelectedIndex + 2) % 3,
                Key.Right => (CategoryTabs.SelectedIndex + 1) % 3,
                Key.Home => 0,
                Key.End => 2,
                _ => -1
            };
            if (index < 0) return;
            CategoryTabs.SelectedIndex = index; ((TabItem)CategoryTabs.SelectedItem).Focus(); e.Handled = true;
        }), true);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                int direction = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
                CategoryTabs.SelectedIndex = (CategoryTabs.SelectedIndex + direction + CategoryTabs.Items.Count) % CategoryTabs.Items.Count;
                ((TabItem)CategoryTabs.SelectedItem).Focus(); e.Handled = true;
            }
        };

        var general = Section(generalPage, "保存与快捷键");
        var folder = new TextBox { Text = settings.OutputDirectory, Name = "OutputDirectory" };
        var browse = StyledButton("选择文件夹", () =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择截图和视频保存目录", InitialDirectory = Directory.Exists(folder.Text) ? folder.Text : AppContext.BaseDirectory };
            if (picker.ShowDialog(this) == true) folder.Text = picker.FolderName;
        }, "PillButton");
        PathRow(general, "保存目录", folder, browse);
        var shortcutRow = Columns(general, 1, 1); ((Grid)shortcutRow[0].Parent).Margin = new Thickness(0, 12, 0, 0);
        var shot = HotkeyField(shortcutRow[0], "截图快捷键", settings.ScreenshotHotkey, "ScreenshotHotkey");
        var record = HotkeyField(shortcutRow[1], "录屏开始 / 停止", settings.RecordingHotkey, "RecordingHotkey");
        var hint = Hint(general, HotkeyCaptureBox.Instructions);
        shot.HintChanged += text => hint.Text = text; record.HintChanged += text => hint.Text = text;
        var appearance = Section(generalPage, "界面主题");
        var theme = Choice(appearance, "即时预览 · 取消后恢复", new[] { "浅色", "深色" }, ThemeService.Current == "Light" ? 0 : 1); theme.Name = "InterfaceTheme";
        string originalTheme = ThemeService.Current; bool committed = false, syncingTheme = false;
        theme.SelectionChanged += (_, _) => { if (!syncingTheme) ThemeService.Apply(theme.SelectedIndex == 0 ? "Light" : "Dark"); };
        void SyncTheme(string value)
        {
            syncingTheme = true; theme.SelectedIndex = value == "Light" ? 0 : 1; syncingTheme = false;
        }
        ThemeService.Changed += SyncTheme;
        IDisposable? capture = null;
        Loaded += (_, _) => capture = hotkeys.BeginCapture((key, modifiers) =>
        {
            if (!IsActive) return;
            if (shot.IsKeyboardFocused) shot.CaptureKey(key, modifiers);
            else if (record.IsKeyboardFocused) record.CaptureKey(key, modifiers);
        });
        Closed += (_, _) =>
        {
            capture?.Dispose(); ThemeService.Changed -= SyncTheme;
            if (!committed) ThemeService.Apply(originalTheme);
        };

        var screenshots = Section(screenshotPage, "图片输出");
        var shotRow = new Grid(); shotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); shotRow.ColumnDefinitions.Add(new ColumnDefinition());
        screenshots.Children.Add(shotRow);
        var formatCell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) }; shotRow.Children.Add(formatCell);
        formatCell.Children.Add(Label("默认格式", false, new Thickness(0, 0, 10, 0)));
        var shotFormat = new ComboBox { ItemsSource = Enum.GetValues<ScreenshotFormat>().Select(ImageExportService.Label).ToArray(), SelectedIndex = (int)settings.ScreenshotFormat, Width = 110, Height = 34, FontSize = 12, Name = "DefaultScreenshotFormat" }; formatCell.Children.Add(shotFormat);
        var qualityCell = new Grid(); Grid.SetColumn(qualityCell, 1); shotRow.Children.Add(qualityCell);
        qualityCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); qualityCell.ColumnDefinitions.Add(new ColumnDefinition()); qualityCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        qualityCell.Children.Add(Label("质量", false, new Thickness(0, 0, 10, 0)));
        var shotQuality = new Slider { Minimum = 1, Maximum = 100, Value = settings.ScreenshotQuality, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center, Name = "DefaultScreenshotQuality", ToolTip = "默认 JPG / WebP 质量" }; Grid.SetColumn(shotQuality, 1); qualityCell.Children.Add(shotQuality);
        var qualityValue = new TextBlock { Text = settings.ScreenshotQuality.ToString(), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Name = "ScreenshotQualityValue" };
        var valueBox = new Border { Child = qualityValue, MinWidth = 40, Height = 26, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0.5), Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(3) };
        valueBox.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); valueBox.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); Grid.SetColumn(valueBox, 2); qualityCell.Children.Add(valueBox);
        shotQuality.ValueChanged += (_, _) => qualityValue.Text = $"{shotQuality.Value:F0}";
        Hint(screenshots, "质量用于 JPG / WebP；PNG / BMP 保留无损输出。");
        var captureOptions = Section(screenshotPage, "截取与保存");
        var snap = new CheckBox { Name = "ScreenshotSnap", Content = "截图时自动吸附窗口", IsChecked = settings.SnapToWindow }; captureOptions.Children.Add(snap);
        Hint(captureOptions, "开启吸附后仍可拖动鼠标自由框选。", new Thickness(20, 4, 0, 0));
        var autoSave = new CheckBox { Name = "ScreenshotAutoSave", Content = "复制时自动保存图片与可编辑项目", IsChecked = settings.AutoSaveScreenshot, Margin = new Thickness(0, 8, 0, 0) }; captureOptions.Children.Add(autoSave);
        var office = Section(screenshotPage, "文字识别");
        var ocrLanguage = Choice(office, "默认识别语言", new[] { "简体中文 + 英文", "繁体中文 + 英文", "英文" }, Array.IndexOf(new[] { "chi_sim+eng", "chi_tra+eng", "eng" }, settings.OcrLanguage)); ocrLanguage.Name = "OcrLanguage";
        var ocrMerge = new CheckBox { Name = "OcrMergeLines", Content = "识别结果合并换行", IsChecked = settings.OcrMergeLines, Margin = new Thickness(0, 8, 0, 0) }; office.Children.Add(ocrMerge);
        var annotation = Section(screenshotPage, "编号与贴图");
        var annotationColumns = Columns(annotation, 1, 1, 1.2);
        annotationColumns[0].Children.Add(Label("起始编号", true, new Thickness(0, 0, 0, 4)));
        var stepStart = new TextBox { Name = "StepStart", Text = settings.StepStart.ToString(), MaxLength = 4 }; annotationColumns[0].Children.Add(stepStart);
        annotationColumns[1].Children.Add(Label("编号大小（像素）", true, new Thickness(0, 0, 0, 4)));
        var stepSize = new TextBox { Name = "StepSize", Text = settings.StepSize.ToString(), MaxLength = 3 }; annotationColumns[1].Children.Add(stepSize);
        annotationColumns[2].Children.Add(Label("贴图透明度", true, new Thickness(0, 0, 0, 4)));
        var pinOpacity = new Slider { Name = "PinOpacity", Minimum = 20, Maximum = 100, Value = settings.PinOpacity * 100, TickFrequency = 1, IsSnapToTickEnabled = true };
        annotationColumns[2].Children.Add(pinOpacity);
        pinOpacity.ValueChanged += (_, _) => pinOpacity.ToolTip = $"{pinOpacity.Value:F0}%"; pinOpacity.ToolTip = $"{pinOpacity.Value:F0}%";
        Hint(annotation, "编号：1–9999；大小：16–160 像素；透明度：20–100%。");

        var recordings = Section(recordingPage, "格式与编码");
        var recordingRow = Columns(recordings, 1.65, 1, 1);
        var recordFormat = Choice(recordingRow[0], "录制格式", new[] { "MP4（H.264 + AAC）", "WebM（VP9 + Opus）", "GIF（无声音）" }, (int)settings.RecordingFormat); recordFormat.Name = "DefaultRecordingFormat";
        var recordQuality = Choice(recordingRow[1], "录制质量", new[] { "低", "中", "高" }, (int)settings.RecordingQuality); recordQuality.Name = "DefaultRecordingQuality";
        var formatFps = Enum.GetValues<RecordingFormat>().ToDictionary(format => format, settings.GetRecordingFps);
        var activeFormat = settings.RecordingFormat;
        var fpsRates = RecordingFrameRates.For(activeFormat);
        var recordingFps = Choice(recordingRow[2], "录制帧率", fpsRates.Select(f => $"{f} FPS").ToArray(), Math.Max(0, Array.IndexOf(fpsRates, formatFps[activeFormat]))); recordingFps.Name = "DefaultRecordingFps";
        bool changingFps = false;
        void PreviewFps() { if (owner is MainWindow main) main.PreviewRecordingPreferences(activeFormat, formatFps[activeFormat]); }
        recordingFps.SelectionChanged += (_, _) =>
        {
            if (changingFps || recordingFps.SelectedIndex < 0) return;
            formatFps[activeFormat] = fpsRates[recordingFps.SelectedIndex]; PreviewFps();
        };
        recordFormat.SelectionChanged += (_, _) =>
        {
            if (recordFormat.SelectedIndex < 0) return;
            activeFormat = (RecordingFormat)recordFormat.SelectedIndex; fpsRates = RecordingFrameRates.For(activeFormat);
            changingFps = true;
            recordingFps.ItemsSource = fpsRates.Select(f => $"{f} FPS").ToArray();
            recordingFps.SelectedIndex = Math.Max(0, Array.IndexOf(fpsRates, formatFps[activeFormat]));
            changingFps = false; PreviewFps();
        };
        Closed += (_, _) => { if (owner is MainWindow main) main.RestoreRecordingPreferences(); };
        var recordingChecks = Columns(recordings, 1.6, 1);
        foreach (var cell in recordingChecks) cell.Margin = new Thickness(cell.Margin.Left, 10, 0, 0);
        var snapRecord = new CheckBox { Name = "RecordingSnap", Content = "区域录屏自动吸附窗口", ToolTip = "仍可拖动自由框选", IsChecked = settings.SnapRecordingToWindow }; recordingChecks[0].Children.Add(snapRecord);
        var hardware = new CheckBox { Name = "HardwareEncoding", Content = "使用硬件编码", ToolTip = "编码失败时可关闭", IsChecked = settings.HardwareEncoding }; recordingChecks[1].Children.Add(hardware);
        Hint(recordings, "帧率按录制格式分别保存，并同步主面板。GIF 不包含声音。");
        var pointer = Section(recordingPage, "画面");
        var cursor = new CheckBox { Name = "RecordingCursor", Content = "录制时显示鼠标", IsChecked = settings.Cursor }; pointer.Children.Add(cursor);
        var audio = Section(recordingPage, "声音与设备");
        var sources = Columns(audio, 1, 1);
        var systemAudio = new CheckBox { Name = "RecordingSystemAudio", Content = "录制系统声音", IsChecked = settings.SystemAudio }; sources[0].Children.Add(systemAudio);
        var microphone = new CheckBox { Name = "RecordingMicrophone", Content = "录制麦克风", IsChecked = settings.Microphone }; sources[1].Children.Add(microphone);
        var devices = Columns(audio, 1, 1); ((Grid)devices[0].Parent).Margin = new Thickness(0, 10, 0, 0);
        var micDevice = Choice(devices[0], "麦克风输入设备", new[] { new AudioDeviceChoice(settings.MicrophoneDeviceId, settings.MicrophoneDeviceId == "" ? "跟随系统默认" : "正在读取设备…") }, 0); micDevice.Name = "MicrophoneDevice";
        var systemDevice = Choice(devices[1], "系统声音播放设备", new[] { new AudioDeviceChoice(settings.SystemAudioDeviceId, settings.SystemAudioDeviceId == "" ? "跟随系统默认" : "正在读取设备…") }, 0); systemDevice.Name = "SystemAudioDevice";
        var audioHint = Hint(audio, "设备选择在保存设置后生效；可进入二级页面检测电平与试录。");
        async System.Threading.Tasks.Task RefreshDevices()
        {
            try
            {
                var choices = await System.Threading.Tasks.Task.Run(() => (AudioMonitor.Devices(true), AudioMonitor.Devices(false)));
                void Fill(ComboBox box, System.Collections.Generic.IReadOnlyList<AudioDeviceChoice> list)
                {
                    string id = ((AudioDeviceChoice)box.SelectedItem).Id; var items = list.ToList();
                    if (!items.Any(item => item.Id == id)) items.Add(new(id, "设备缺失 · 保留选择"));
                    box.ItemsSource = items; box.SelectedItem = items.First(item => item.Id == id);
                }
                Fill(micDevice, choices.Item1); Fill(systemDevice, choices.Item2);
            }
            catch (Exception ex) { audioHint.Text = "读取设备失败：" + ex.Message; }
        }
        Loaded += (_, _) => DeviceInitialization = RefreshDevices();
        var audioActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) }; audio.Children.Add(audioActions);
        var refreshDevices = StyledButton("刷新设备", () => { _ = RefreshDevices(); }, "PanelButton"); audioActions.Children.Add(refreshDevices);
        var testDevices = StyledButton("声音检测与试录…", () =>
        {
            // The shared audio window receives a draft and cannot persist the dialog.
            var draft = new Settings { Microphone = microphone.IsChecked == true, SystemAudio = systemAudio.IsChecked == true, MicrophoneDeviceId = ((AudioDeviceChoice)micDevice.SelectedItem).Id, SystemAudioDeviceId = ((AudioDeviceChoice)systemDevice.SelectedItem).Id };
            new AudioDeviceWindow(this, draft, persistSelection: false).ShowDialog();
            void Select(ComboBox box, string id) { var items = box.Items.Cast<AudioDeviceChoice>().ToList(); if (!items.Any(item => item.Id == id)) items.Add(new(id, "设备缺失 · 保留选择")); box.ItemsSource = items; box.SelectedItem = items.First(item => item.Id == id); }
            Select(micDevice, draft.MicrophoneDeviceId); Select(systemDevice, draft.SystemAudioDeviceId);
        }, "PanelButton"); testDevices.Name = "TestAudioDevices"; testDevices.Margin = new Thickness(8, 0, 0, 0); audioActions.Children.Add(testDevices);

        var tools = Section(generalPage, "媒体工具");
        var toolsPath = new TextBox { Text = settings.MediaToolsPath, Name = "MediaToolsPath", ToolTip = "FFmpeg 工具目录，留空自动检测" };
        var browseTools = StyledButton("选择目录", () => { var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择包含 ffmpeg.exe / ffprobe.exe 的目录" }; if (picker.ShowDialog(this) == true) toolsPath.Text = picker.FolderName; }, "PillButton");
        PathRow(tools, "FFmpeg", toolsPath, browseTools, "自动检测");
        Hint(tools, "工具目录留空自动检测；指定目录或系统工具不完整时使用程序附带的 FFmpeg / ffprobe。");
        var detection = Hint(tools, "检测按当前输入的目录执行，无需先保存。"); detection.Name = "MediaToolsStatus";
        var detectionCancellation = new CancellationTokenSource(); Closed += (_, _) => { detectionCancellation.Cancel(); detectionCancellation.Dispose(); };
        Button? detect = null;
        detect = StyledButton("检测媒体工具", async () =>
        {
            detect!.IsEnabled = false; detection.Text = "正在检测 FFmpeg / ffprobe…";
            try
            {
                var found = await MediaTools.DetectAsync(new Settings { MediaToolsPath = toolsPath.Text.Trim() }, detectionCancellation.Token);
                detection.Text = found.HasVideoTools ? $"MP4 {(found.Supports(RecordingFormat.Mp4) ? "可用" : "不可用")} · WebM {(found.Supports(RecordingFormat.WebM) ? "可用" : "不可用")} · GIF {(found.Supports(RecordingFormat.Gif) ? "可用" : "不可用")}" : "未找到完整媒体工具；仍可保存原生 MP4。";
                detection.ToolTip = $"FFmpeg：{found.Ffmpeg ?? "未找到"}\nffprobe：{found.Ffprobe ?? "未找到"}";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { detection.Text = "检测失败：" + ex.Message; }
            finally { detect.IsEnabled = true; }
        }, "PanelButton"); detect.Name = "DetectMediaTools"; detect.Margin = new Thickness(0, 10, 0, 0); tools.Children.Add(detect);

        var cancel = StyledButton("取消", () => DialogResult = false, "PanelButton"); cancel.Margin = new Thickness(0, 0, 8, 0); cancel.Padding = new Thickness(16, 7, 16, 7); cancel.IsCancel = true; buttons.Children.Add(cancel);
        var save = StyledButton("保存设置", () =>
        {
            string oldShot = settings.ScreenshotHotkey, oldRecord = settings.RecordingHotkey, oldFolder = settings.OutputDirectory, oldTheme = settings.Theme;
            bool oldHardware = settings.HardwareEncoding, oldSnap = settings.SnapToWindow, oldSnapRecord = settings.SnapRecordingToWindow, configured = false;
            var oldFormats = (settings.ScreenshotFormat, settings.ScreenshotQuality, settings.RecordingFormat, settings.RecordingQuality, settings.GifFps, settings.MediaToolsPath, settings.FramesPerSecond, settings.Mp4Fps, settings.WebMFps);
            var oldAdditional = (settings.AutoSaveScreenshot, settings.Cursor, settings.SystemAudio, settings.Microphone, settings.MicrophoneDeviceId, settings.SystemAudioDeviceId, settings.OcrLanguage, settings.OcrMergeLines, settings.StepStart, settings.StepSize, settings.PinOpacity);
            try
            {
                if (!int.TryParse(stepStart.Text, out int start) || start < 1 || start > 9999 || !int.TryParse(stepSize.Text, out int size) || size < 16 || size > 160)
                {
                    CategoryTabs.SelectedIndex = 1; stepStart.Focus(); throw new ArgumentException("起始编号需为 1–9999，编号大小需为 16–160 像素。");
                }
                if (toolsPath.Text.Trim() != "" && !Directory.Exists(toolsPath.Text.Trim())) throw new ArgumentException("媒体工具目录不存在。");
                string fullPath = Path.GetFullPath(folder.Text.Trim()); Directory.CreateDirectory(fullPath);
                string probe = Path.Combine(fullPath, $".quickcapture-write-{Guid.NewGuid():N}");
                File.WriteAllText(probe, ""); File.Delete(probe);
                hotkeys.Configure(shot.Text.Trim(), record.Text.Trim()); configured = true;
                settings.OutputDirectory = fullPath; settings.ScreenshotHotkey = shot.Text.Trim(); settings.RecordingHotkey = record.Text.Trim(); settings.HardwareEncoding = hardware.IsChecked == true;
                settings.Theme = theme.SelectedIndex == 0 ? "Light" : "Dark";
                settings.SnapToWindow = snap.IsChecked == true;
                settings.SnapRecordingToWindow = snapRecord.IsChecked == true;
                settings.ScreenshotFormat = (ScreenshotFormat)shotFormat.SelectedIndex; settings.ScreenshotQuality = (int)shotQuality.Value;
                settings.RecordingFormat = (RecordingFormat)recordFormat.SelectedIndex; settings.RecordingQuality = (ExportQuality)recordQuality.SelectedIndex;
                foreach (var (format, fps) in formatFps) settings.SetRecordingFps(format, fps);
                settings.MediaToolsPath = toolsPath.Text.Trim();
                settings.AutoSaveScreenshot = autoSave.IsChecked == true; settings.Cursor = cursor.IsChecked == true;
                settings.SystemAudio = systemAudio.IsChecked == true; settings.Microphone = microphone.IsChecked == true;
                settings.MicrophoneDeviceId = ((AudioDeviceChoice)micDevice.SelectedItem).Id; settings.SystemAudioDeviceId = ((AudioDeviceChoice)systemDevice.SelectedItem).Id;
                settings.OcrLanguage = new[] { "chi_sim+eng", "chi_tra+eng", "eng" }[ocrLanguage.SelectedIndex]; settings.OcrMergeLines = ocrMerge.IsChecked == true;
                settings.StepStart = start; settings.StepSize = size; settings.PinOpacity = pinOpacity.Value / 100;
                settings.Save(); ThemeService.Apply(settings.Theme); committed = true; DialogResult = true;
            }
            catch (Exception ex)
            {
                (settings.ScreenshotFormat, settings.ScreenshotQuality, settings.RecordingFormat, settings.RecordingQuality, settings.GifFps, settings.MediaToolsPath, settings.FramesPerSecond, settings.Mp4Fps, settings.WebMFps) = oldFormats;
                settings.ScreenshotHotkey = oldShot; settings.RecordingHotkey = oldRecord; settings.OutputDirectory = oldFolder; settings.HardwareEncoding = oldHardware;
                settings.Theme = oldTheme;
                settings.SnapToWindow = oldSnap;
                settings.SnapRecordingToWindow = oldSnapRecord;
                (settings.AutoSaveScreenshot, settings.Cursor, settings.SystemAudio, settings.Microphone, settings.MicrophoneDeviceId, settings.SystemAudioDeviceId, settings.OcrLanguage, settings.OcrMergeLines, settings.StepStart, settings.StepSize, settings.PinOpacity) = oldAdditional;
                if (configured) try { hotkeys.Configure(oldShot, oldRecord); } catch (Exception rollback) { ErrorLog.Write(rollback); }
                Ui.Error(this, ex);
            }

        }, "PrimaryButton"); save.Name = "SaveSettings"; buttons.Children.Add(save);
    }
    private StackPanel Category(string label)
    {
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var tab = new TabItem { Header = label, Content = content, Style = (Style)FindResource("SettingsCategory") };
        System.Windows.Automation.AutomationProperties.SetName(tab, label);
        CategoryTabs.Items.Add(tab); return content;
    }
    // Search the logical tree so secondary/hidden category controls remain
    // addressable without changing the selected page or rebuilding a draft.
    internal T Setting<T>(string name) where T : FrameworkElement
    {
        T? Find(DependencyObject item)
        {
            if (item is T match && (match.Name == name || match is Button button && button.Content as string == name)) return match;
            foreach (object child in LogicalTreeHelper.GetChildren(item))
                if (child is DependencyObject dependency && Find(dependency) is { } found) return found;
            return null;
        }
        return Find(this) ?? throw new InvalidOperationException("Missing setting: " + name);
    }
    private Button StyledButton(string label, Action clicked, string style)
    {
        var button = Ui.Button(label, clicked); button.Style = (Style)FindResource(style); return button;
    }
    private StackPanel Section(Panel parent, string title)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 8) });
        parent.Children.Add(new Border { Child = content, Style = (Style)FindResource("PanelCard"), Padding = new Thickness(14, 10, 14, 10) }); return content;
    }
    private TextBlock Hint(Panel parent, string text, Thickness? margin = null)
    {
        var hint = new TextBlock { Text = text, Style = (Style)FindResource("PanelHint"), Margin = margin ?? new Thickness(0, 8, 0, 0) }; parent.Children.Add(hint); return hint;
    }
    private static TextBlock Label(string text, bool above, Thickness margin)
    {
        var label = new TextBlock { Text = text, FontSize = above ? 11 : 12, Margin = margin, VerticalAlignment = above ? VerticalAlignment.Top : VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, above ? "TextHint" : "TextSecondary"); return label;
    }
    private static StackPanel[] Columns(Panel parent, params double[] widths)
    {
        var row = new Grid(); parent.Children.Add(row); var cells = new StackPanel[widths.Length];
        for (int i = 0; i < widths.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i], GridUnitType.Star) });
            cells[i] = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0) }; Grid.SetColumn(cells[i], i); row.Children.Add(cells[i]);
        }
        return cells;
    }
    private static ComboBox Choice(Panel parent, string label, object items, int selected)
    {
        parent.Children.Add(Label(label, true, new Thickness(0, 0, 0, 4)));
        var box = new ComboBox { ItemsSource = (System.Collections.IEnumerable)items, SelectedIndex = selected, MinWidth = 0, Height = 34, FontSize = 12, ToolTip = label }; parent.Children.Add(box); return box;
    }
    private void PathRow(Panel parent, string label, TextBox field, Button browse, string? placeholder = null)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Label(label, false, new Thickness(0, 0, 10, 0)));
        browse.Margin = new Thickness(10, 0, 0, 0); browse.Height = 34; Grid.SetColumn(browse, 2); row.Children.Add(browse);
        var path = new Grid(); Grid.SetColumn(path, 1); row.Children.Add(path); path.Children.Add(field);
        field.SetBinding(ToolTipProperty, new Binding("Text") { Source = field });
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        text.Text = string.IsNullOrEmpty(field.Text) ? placeholder ?? "" : field.Text;
        field.TextChanged += (_, _) => text.Text = string.IsNullOrEmpty(field.Text) ? placeholder ?? "" : field.Text;
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var display = new Border { Child = text, CornerRadius = new CornerRadius(8), Margin = new Thickness(0.5), Padding = new Thickness(10, 0, 10, 0), IsHitTestVisible = false };
        display.SetResourceReference(Border.BackgroundProperty, "InputBackground");
        var displayStyle = new Style(typeof(Border));
        var focused = new DataTrigger { Binding = new Binding("IsKeyboardFocusWithin") { Source = field }, Value = true };
        focused.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed)); displayStyle.Triggers.Add(focused); display.Style = displayStyle; path.Children.Add(display);
        parent.Children.Add(row);
    }
    private HotkeyCaptureBox HotkeyField(Panel parent, string label, string value, string name)
    {
        parent.Children.Add(Label(label, true, new Thickness(0, 0, 0, 4)));
        var field = new HotkeyCaptureBox(value, label) { Name = name, Style = (Style)FindResource("PanelTextBox"), Padding = new Thickness(8, 7, 8, 7) }; parent.Children.Add(field); return field;
    }
    private static FrameworkElement LineIcon(string geometry, double size, string color)
    {
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        icon.SetResourceReference(Shape.StrokeProperty, color); return icon;
    }
}
