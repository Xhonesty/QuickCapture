using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ScreenRecorderLib;
using Forms = System.Windows.Forms;

namespace QuickCapture;

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly RecorderService _recorder = new();
    private HotkeyService? _hotkeys;
    private Forms.NotifyIcon? _tray;
    private Icon? _icon;
    private RecordingBar? _bar;
    private RecordingFrame? _frame;
    private bool _busy, _exit, _stopBusy;
    private EditorWindow? _editor;
    private RecordingMetadata? _recordingMetadata;
    public MainWindow()
    {
        ThemeService.Apply(_settings.Theme);
        InitializeComponent();
        RecentSectionSlot.SizeChanged += (_, _) => UpdateRecentHeight();
        SourceInitialized += (_, _) =>
        {
            _hotkeys = new(new WindowInteropHelper(this).Handle);
            _hotkeys.Pressed += async id => { if (id == 1) await ScreenshotAsync(); else await ToggleRecordingAsync(); };
            ConfigureHotkeys();
        };
        Loaded += (_, _) => { LoadControls(); CreateTray(); RefreshRecent(); };
        _recorder.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            _bar?.SetState(state);
            if (state == RecordingState.Recording) SetStatus("录制中 · 可暂停／继续 · 再按录屏快捷键停止");
            else if (state == RecordingState.Paused) SetStatus("已暂停 · 画面、声音和计时均已暂停");
            else if (state == RecordingState.Stopping) SetStatus("正在结束录制并完成编码…");
        });
        _recorder.Failed += error => Dispatcher.BeginInvoke(() =>
        {
            ErrorLog.Write(new InvalidOperationException(error));
            _bar?.Close(); _bar = null; _frame?.Close(); _frame = null; _recorder.Dispose(); UpdateRecordingUi();
            SetStatus(error); Show(); Activate();
        });
        Closing += OnClosing;
        Closed += (_, _) => { _hotkeys?.Dispose(); _tray?.Dispose(); _icon?.Dispose(); _recorder.Dispose(); };
    }
    private void ConfigureHotkeys()
    {
        try { _hotkeys!.Configure(_settings.ScreenshotHotkey, _settings.RecordingHotkey); }
        catch (Exception ex)
        {
            if (!File.Exists(Paths.SettingsFile))
            {
                foreach (string prefix in new[] { "Ctrl+Alt+", "Ctrl+Shift+", "Ctrl+Alt+Shift+" })
                {
                    try
                    {
                        string shot = prefix + "F8", record = prefix + "F9";
                        _hotkeys!.Configure(shot, record);
                        _settings.ScreenshotHotkey = shot; _settings.RecordingHotkey = record;
                        _settings.Save();
                        StatusText.Text = $"默认快捷键已被占用，已使用 {shot} 截图 / {record} 录屏。";
                        return;
                    }
                    catch (InvalidOperationException) { }
                }
            }
            StatusText.Text = ex.Message;
        }
    }
    private void LoadControls()
    {
        AutoSaveBox.IsChecked = _settings.AutoSaveScreenshot; SystemAudioBox.IsChecked = _settings.SystemAudio;
        SnapWindowBox.IsChecked = _settings.SnapToWindow;
        SnapRecordWindowBox.IsChecked = _settings.SnapRecordingToWindow;
        MicrophoneBox.IsChecked = _settings.Microphone; CursorBox.IsChecked = _settings.Cursor;
        FpsBox.SelectedIndex = _settings.FramesPerSecond == 15 ? 0 : _settings.FramesPerSecond == 60 ? 2 : 1;
        ShotKeyLabel.Text = _settings.ScreenshotHotkey; RecordKeyLabel.Text = _settings.RecordingHotkey;
        RepeatButton.IsEnabled = _settings.LastRegion != null;
        ThemeToggle.Content = _settings.Theme == "Light" ? "切换深色" : "切换浅色";
    }
    private void SaveControls()
    {
        _settings.AutoSaveScreenshot = AutoSaveBox.IsChecked == true;
        _settings.SnapToWindow = SnapWindowBox.IsChecked == true;
        _settings.SnapRecordingToWindow = SnapRecordWindowBox.IsChecked == true;
        _settings.SystemAudio = SystemAudioBox.IsChecked == true; _settings.Microphone = MicrophoneBox.IsChecked == true;
        _settings.Cursor = CursorBox.IsChecked == true; _settings.FramesPerSecond = new[] { 15, 30, 60 }[Math.Max(0, FpsBox.SelectedIndex)];
        _settings.Save();
    }
    private void CreateTray()
    {
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        using var icon = new Icon(resource, 32, 32); _icon = (Icon)icon.Clone();
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "轻截 · QuickCapture", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开轻截", null, (_, _) => ShowMain());
        menu.Items.Add("截图", null, async (_, _) => await ScreenshotAsync());
        menu.Items.Add("开始 / 停止录屏", null, async (_, _) => await ToggleRecordingAsync());
        menu.Items.Add("暂停 / 继续录屏", null, (_, _) => TogglePause());
        menu.Items.Add("打开保存目录", null, (_, _) => OpenFolder());
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => ShowMain();
        _tray.BalloonTipClicked += (_, _) => ShowMain();
    }
    private void ShowMain() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void SetStatus(string message) => StatusText.Text = message;
    private void Saved(string path)
    {
        RefreshRecent(path); SetStatus($"已保存：{Path.GetFileName(path)}");
        _tray?.ShowBalloonTip(2000, "轻截 · 已保存", Path.GetFileName(path), Forms.ToolTipIcon.Info);
    }
    private async Task ScreenshotAsync()
    {
        if (_busy || _recorder.IsBusy) { SetStatus("请先完成当前操作。"); return; }
        if (_editor != null) { _editor.Activate(); return; }
        _busy = true;
        try
        {
            SaveControls(); BitmapSource? image = null;
            if (ShotMode.SelectedIndex == 1)
            {
                ShowMain(); var item = WindowPicker.Pick(this); if (item == null) return;
                Hide(); await Task.Delay(180); Directory.CreateDirectory(Paths.Data);
                image = await RecorderService.CaptureWindowAsync(item.Handle, Path.Combine(Paths.Data, $"window-{Guid.NewGuid():N}.png"));
            }
            else
            {
                Hide(); await Task.Delay(180);
                if (ShotMode.SelectedIndex == 2) image = await CaptureService.CaptureAsync(Forms.SystemInformation.VirtualScreen);
                else
                {
                    await SelectionWindow.EditScreenshotAsync(_settings, Saved);
                    return;
                }
            }
            _editor = new EditorWindow(image, _settings, Saved);
            _editor.Closed += (_, _) => { _editor = null; ShowMain(); };
            _editor.Show();
        }
        catch (Exception ex) { ShowMain(); Ui.Error(this, ex); }
        finally { _busy = false; if (_editor == null) ShowMain(); }
    }
    private async Task ToggleRecordingAsync(bool repeat = false)
    {
        if (_recorder.IsBusy) { await StopAsync(); return; }
        if (_busy || _editor != null) { SetStatus("请先完成当前截图操作。"); return; }
        _busy = true;
        try
        {
            SaveControls(); RecordingSourceBase? source = null; SavedRegion? recordingRegion = null;
            if (repeat)
            {
                if (_settings.LastRegion == null) return;
                source = RecorderService.RegionSource(_settings.LastRegion);
                recordingRegion = _settings.LastRegion;
            }
            else if (RecordMode.SelectedIndex == 1)
            {
                ShowMain(); var item = WindowPicker.Pick(this); if (item == null) return;
                if (!Native.IsWindow(item.Handle) || Native.IsIconic(item.Handle)) throw new InvalidOperationException("窗口已关闭或最小化。");
                source = new WindowRecordingSource(item.Handle);
            }
            else if (RecordMode.SelectedIndex == 2)
            {
                var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
                source = new DisplayRecordingSource(screen.DeviceName) { RecorderApi = RecorderApi.WindowsGraphicsCapture };
            }
            Hide(); await Task.Delay(180);
            if (source == null)
            {
                var region = await SelectionWindow.SelectAsync(true, _settings); if (region == null) return;
                source = RecorderService.RegionSource(region.Region); _settings.LastRegion = region.Region; _settings.Save();
                recordingRegion = region.Region;
                await Task.Delay(120);
            }
            Rectangle? CaptureTargetBounds()
            {
                if (recordingRegion != null) return recordingRegion.Rectangle;
                if (source is WindowRecordingSource target && Native.VisibleWindowBounds(target.Handle, out var bounds)) return bounds;
                if (source is DisplayRecordingSource display) return Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == display.DeviceName)?.Bounds;
                return null;
            }
            _bar = new RecordingBar(_settings.RecordingHotkey, async () => await StopAsync(), TogglePause,
                () => _recorder.Elapsed, CaptureTargetBounds, source is DisplayRecordingSource); _bar.Show();
            if (recordingRegion != null) { _frame = new RecordingFrame(recordingRegion); _frame.Show(); }
            UpdateRecordingUi();
            _recordingMetadata = new(_settings.RecordingQuality, _settings.FramesPerSecond, _settings.SystemAudio || _settings.Microphone, 0);
            await _recorder.StartAsync(source, _settings, RecordingRecovery.NewMaster()).WaitAsync(TimeSpan.FromSeconds(20));
            if (_bar.HiddenForCaptureSafety) _tray?.ShowBalloonTip(3500, "轻截 · 录屏控制", "系统无法排除控制条，已隐藏以避免入镜。使用托盘暂停／继续，录屏快捷键停止。", Forms.ToolTipIcon.Info);
            UpdateRecordingUi();
        }
        catch (Exception ex) { _bar?.Close(); _bar = null; _frame?.Close(); _frame = null; _recorder.Dispose(); ShowMain(); Ui.Error(this, ex); }
        finally { _busy = false; UpdateRecordingUi(); if (!_recorder.IsBusy) ShowMain(); }
    }
    private async Task StopAsync()
    {
        if (_stopBusy || !_recorder.IsBusy || _recorder.State == RecordingState.Starting) return;
        _stopBusy = true; _bar?.SetSaving(); SetStatus("正在完成编码…");
        try
        {
            // Keep the application alive until the encoder has finalized the MP4.
            string path = await _recorder.StopAsync();
            RecordingRecovery.Remember(path, (_recordingMetadata ?? new(ExportQuality.Medium, 30, false, 0)) with { Duration = _recorder.Elapsed.TotalSeconds });
            _recorder.Dispose(); _bar?.Close(); _bar = null; _frame?.Close(); _frame = null; UpdateRecordingUi();
            if (!_exit) EditRecording(path); else RefreshRecent();
        }
        catch (Exception ex) { Ui.Error(this, ex); }
        finally
        {
            _recorder.Dispose(); _bar?.Close(); _bar = null; _frame?.Close(); _frame = null; _stopBusy = false;
            UpdateRecordingUi(); if (!_exit) ShowMain();
        }
    }
    private void TogglePause()
    {
        if (_stopBusy || !_recorder.IsBusy) return;
        try { _recorder.TogglePause(); }
        catch (Exception ex) { ErrorLog.Write(ex); SetStatus(ex.Message); }
    }
    private void EditRecording(string path)
    {
        _busy = true;
        try
        {
            ShowMain(); var dialog = new RecordingEditWindow(this, path, _settings, RecordingRecovery.Load(path));
            if (dialog.ShowDialog() == true)
            {
                Saved(dialog.SavedPath!);
                // Spatial crop is reversible while the full recording remains available.
                // Keep cropped masters in the existing recent-file recovery workflow.
                if (dialog.AppliedCrop == null)
                    try { RecordingRecovery.Remove(path); } catch (IOException ex) { ErrorLog.Write(ex); }
                RefreshRecent();
                if (dialog.AppliedCrop != null) SetStatus("已导出；原片保留在最近文件，可再次调整裁剪。");
            }
            else { RefreshRecent(); SetStatus("原始录屏已保留，双击最近文件中的「待导出」项目继续编辑。"); }
        }
        finally { _busy = false; }
    }
    private void UpdateRecordingUi()
    {
        RecordButton.Content = _recorder.IsBusy ? "停止并保存" : "开始录屏";
        RepeatButton.IsEnabled = !_recorder.IsBusy && _settings.LastRegion != null;
        SystemAudioBox.IsEnabled = MicrophoneBox.IsEnabled = FpsBox.IsEnabled = CursorBox.IsEnabled = !_recorder.IsBusy;
        SnapRecordWindowBox.IsEnabled = !_recorder.IsBusy;
    }
    private void RefreshRecent(string? generatedPath = null)
    {
        try
        {
            string? selectedPath = (RecentList.SelectedItem as RecentItem)?.Path;
            Directory.CreateDirectory(_settings.OutputDirectory);
            Directory.CreateDirectory(RecordingRecovery.DirectoryPath);
            var extensions = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".mp4", ".webm", ".gif" };
            var items = new DirectoryInfo(_settings.OutputDirectory).EnumerateFiles()
                .Concat(new DirectoryInfo(RecordingRecovery.DirectoryPath).EnumerateFiles("master-*.mp4"))
                .Concat(RecentList.Items.OfType<RecentItem>().Select(item => new FileInfo(item.Path)))
                .Concat(generatedPath != null ? new[] { new FileInfo(generatedPath) } : Array.Empty<FileInfo>())
                .Where(f => f.Exists && extensions.Contains(f.Extension.ToLowerInvariant()) && !f.Name.Contains(".partial.", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(f => string.Equals(f.FullName, generatedPath, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => f.LastWriteTime).Take(20)
                .Select(f => new RecentItem(f.FullName, (RecordingRecovery.Owns(f.FullName) ? "待导出 · " : "") + f.Name, $"{f.LastWriteTime:MM-dd HH:mm}  ·  {f.Length / 1024d / 1024d:F2} MB")).ToArray();
            RecentList.ItemsSource = items;
            RecentList.SelectedItem = items.FirstOrDefault(item => string.Equals(item.Path, generatedPath ?? selectedPath, StringComparison.OrdinalIgnoreCase));
            UpdateRecentHeight();
            if (generatedPath != null && items.Length > 0) RecentList.ScrollIntoView(items[0]);
        }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void UpdateRecentHeight()
    {
        if (!IsLoaded || RecentSectionSlot.ActualHeight <= 0) return;
        // The header, border and padding occupy 56 DIP. Fit complete 44-DIP rows,
        // cap at five, and use a compact placeholder when there are no files.
        double available = Math.Max(0, RecentSectionSlot.ActualHeight - 6);
        int rows = Math.Min(RecentList.Items.Count, Math.Clamp((int)((available - 56) / 44), 1, 5));
        RecentCard.Height = Math.Min(available, 56 + (rows == 0 ? 34 : rows * 44));
    }
    private void OpenFolder() { try { Directory.CreateDirectory(_settings.OutputDirectory); Native.Open(_settings.OutputDirectory); } catch (Exception ex) { Ui.Error(this, ex); } }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exit) return; e.Cancel = true;
        try { SaveControls(); } catch (Exception ex) { ErrorLog.Write(ex); }
        Hide();
    }
    private async Task ExitAsync()
    {
        if (_busy || _stopBusy) { SetStatus("当前操作完成后再退出。"); return; }
        _exit = true; await StopAsync();
        try { SaveControls(); } catch (Exception ex) { ErrorLog.Write(ex); }
        Application.Current.Shutdown();
    }
    private async void Screenshot_Click(object sender, RoutedEventArgs e) => await ScreenshotAsync();
    private async void Recording_Click(object sender, RoutedEventArgs e) => await ToggleRecordingAsync();
    private async void Repeat_Click(object sender, RoutedEventArgs e) => await ToggleRecordingAsync(true);
    private void Folder_Click(object sender, RoutedEventArgs e) => OpenFolder();
    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        string previous = _settings.Theme;
        try
        {
            _settings.Theme = previous == "Light" ? "Dark" : "Light";
            _settings.Save(); ThemeService.Apply(_settings.Theme);
            ThemeToggle.Content = _settings.Theme == "Light" ? "切换深色" : "切换浅色";
        }
        catch (Exception ex) { _settings.Theme = previous; Ui.Error(this, ex); }
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsBusy || _busy) { SetStatus("请先停止录屏。"); return; }
        try
        {
            SaveControls(); _busy = true; var dialog = new SettingsWindow(this, _settings, _hotkeys!);
            dialog.ShowDialog(); LoadControls(); RefreshRecent();
        }
        catch (Exception ex) { Ui.Error(this, ex); }
        finally { _busy = false; }
    }
    private void Recent_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(RecentList, source) is ListBoxItem row && row.DataContext is RecentItem item)
            OpenRecent(item);
    }
    private void Recent_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || RecentList.SelectedItem is not RecentItem item) return;
        e.Handled = true; OpenRecent(item);
    }
    private void RecentRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem row || row.ContextMenu != null) return;
        // Register menu events in code rather than inside the style resource.
        // Read the row's current item when clicked so recycled rows stay correct.
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "打开文件" };
        var folder = new MenuItem { Header = "打开所在目录" };
        open.Click += (_, _) => { if (row.DataContext is RecentItem item) OpenRecent(item); };
        folder.Click += (_, _) => { if (row.DataContext is RecentItem item) OpenRecentFolder(item); };
        menu.Items.Add(open); menu.Items.Add(folder); row.ContextMenu = menu;
    }
    private void OpenRecentFolder(RecentItem item)
    {
        try { Native.Open(Path.GetDirectoryName(item.Path)!); }
        catch (Exception ex) { Ui.Error(this, ex); }
    }
    private void OpenRecent(RecentItem item)
    {
        try
        {
            if (RecordingRecovery.Owns(item.Path)) { if (!_busy && !_recorder.IsBusy) EditRecording(item.Path); }
            else Native.Open(item.Path);
        }
        catch (Exception ex) { Ui.Error(this, ex); }
    }
}

internal sealed record RecentItem(string Path, string Name, string Detail)
{
    public bool IsVideo => System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".mp4" or ".webm" or ".gif";
    public string MediaType => IsVideo ? "视频" : "图片";
}
