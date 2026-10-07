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
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
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
    private bool _busy, _exit, _stopBusy, _closed;
    private EditorWindow? _editor;
    private RecordingMetadata? _recordingMetadata;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _syncingFps;
    public MainWindow()
    {
        ThemeService.Apply(_settings.Theme);
        InitializeComponent();
        FpsBox.SelectionChanged += Fps_SelectionChanged;
        ThemeService.Changed += OnThemeChanged;
        _statusTimer.Tick += (_, _) => UpdateRecordingStatus();
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
            UpdateRecordingUi();
            if (state is RecordingState.Recording or RecordingState.Paused) UpdateRecordingStatus();
            else if (state == RecordingState.Stopping) SetStatus("正在结束录制并完成编码…");
        });
        _recorder.Failed += error => Dispatcher.BeginInvoke(() =>
        {
            ErrorLog.Write(new InvalidOperationException(error));
            _bar?.Close(); _bar = null; _frame?.Close(); _frame = null; _recorder.Dispose(); UpdateRecordingUi();
            SetStatus(error); Show(); Activate();
        });
        Closing += OnClosing;
        _recorder.Audio.Faulted += message => Dispatcher.BeginInvoke(async () =>
        {
            SetStatus(message);
            while (_recorder.State == RecordingState.Starting) await Task.Delay(100);
            if (_recorder.State is RecordingState.Recording or RecordingState.Paused) await StopAsync();
            SetStatus(message + "；录制已停止，已录原片保留。请重新选择设备。");
        });
        Closed += (_, _) => { _closed = true; PinManager.CloseAll(); ThemeService.Changed -= OnThemeChanged; _statusTimer.Stop(); _hotkeys?.Dispose(); _tray?.Dispose(); _icon?.Dispose(); _recorder.Dispose(); };
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
        RestoreRecordingPreferences();
        ShotKeyLabel.Text = _settings.ScreenshotHotkey.Replace("+", " + "); RecordKeyLabel.Text = _settings.RecordingHotkey.Replace("+", " + ");
        OnThemeChanged(ThemeService.Current); UpdateRecordingUi();
    }
    private void SaveControls()
    {
        _settings.AutoSaveScreenshot = AutoSaveBox.IsChecked == true;
        _settings.SnapToWindow = SnapWindowBox.IsChecked == true;
        _settings.SnapRecordingToWindow = SnapRecordWindowBox.IsChecked == true;
        _settings.SystemAudio = SystemAudioBox.IsChecked == true; _settings.Microphone = MicrophoneBox.IsChecked == true;
        _settings.Cursor = CursorBox.IsChecked == true;
        if (FpsBox.SelectedIndex >= 0) _settings.SetRecordingFps(_settings.RecordingFormat, RecordingFrameRates.For(_settings.RecordingFormat)[FpsBox.SelectedIndex]);
        _settings.Save();
    }
    internal void PreviewRecordingPreferences(RecordingFormat format, int fps)
    {
        _syncingFps = true;
        try
        {
            var rates = RecordingFrameRates.For(format); FpsBox.ItemsSource = null; FpsBox.Items.Clear();
            FpsBox.ItemsSource = rates.Select(value => $"{value} FPS").ToArray();
            FpsBox.SelectedIndex = Math.Max(0, Array.IndexOf(rates, fps));
            FpsBox.ToolTip = $"{RecordingFrameRates.Label(format)} 录制帧率，与设置同步";
        }
        finally { _syncingFps = false; }
    }
    internal void RestoreRecordingPreferences() => PreviewRecordingPreferences(_settings.RecordingFormat, _settings.GetRecordingFps(_settings.RecordingFormat));
    private void Fps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFps || !IsLoaded || FpsBox.SelectedIndex < 0) return;
        var old = (_settings.FramesPerSecond, _settings.Mp4Fps, _settings.WebMFps, _settings.GifFps);
        try { _settings.SetRecordingFps(_settings.RecordingFormat, RecordingFrameRates.For(_settings.RecordingFormat)[FpsBox.SelectedIndex]); _settings.Save(); }
        catch (Exception ex)
        {
            (_settings.FramesPerSecond, _settings.Mp4Fps, _settings.WebMFps, _settings.GifFps) = old;
            RestoreRecordingPreferences(); Ui.Error(this, ex);
        }
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
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("隐藏全部贴图", null, (_, _) => PinManager.HideAll());
        menu.Items.Add("恢复全部贴图", null, (_, _) => PinManager.RestoreAll());
        menu.Items.Add("解除全部贴图穿透并恢复", null, (_, _) => PinManager.DisableClickThrough());
        menu.Items.Add("关闭全部贴图", null, (_, _) => PinManager.CloseAll());
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => ShowMain();
        _tray.BalloonTipClicked += (_, _) => ShowMain();
    }
    private void ShowMain() { if (_closed || _exit || Application.Current?.Dispatcher.HasShutdownStarted == true) return; Show(); WindowState = WindowState.Normal; Activate(); }
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
                () => _recorder.Elapsed, CaptureTargetBounds, source is DisplayRecordingSource, _recorder.Audio); _bar.Show();
            if (recordingRegion != null) { _frame = new RecordingFrame(recordingRegion); _frame.Show(); }
            UpdateRecordingUi();
            _recordingMetadata = new(_settings.RecordingQuality, _settings.GetRecordingFps(_settings.RecordingFormat), _settings.SystemAudio || _settings.Microphone, 0);
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
            if (!File.Exists(path)) throw new FileNotFoundException("录屏文件不存在。", path);
            bool owned = RecordingRecovery.Owns(path);
            var metadata = owned ? RecordingRecovery.Load(path) : new RecordingMetadata((ExportQuality)_settings.RecordingQuality, _settings.GetRecordingFps(RecordingFormat.Mp4), true, 0);
            ShowMain(); var dialog = new RecordingEditWindow(this, path, _settings, metadata);
            if (dialog.ShowDialog() == true)
            {
                Saved(dialog.SavedPath!);
                // Spatial crop is reversible while the full recording remains available.
                // Keep cropped masters in the existing recent-file recovery workflow.
                if (owned && !dialog.PreserveMaster)
                    try { RecordingRecovery.Remove(path); } catch (IOException ex) { ErrorLog.Write(ex); }
                RefreshRecent();
                if (!owned) SetStatus("已保存编辑副本，原录屏保留；可从最近文件继续编辑。");
                else if (dialog.PreserveMaster) SetStatus("已导出；原片和剪辑配置保留，可继续编辑；更多菜单可清理原片。");
            }
            else { RefreshRecent(path); SetStatus(owned ? "原始录屏已保留，双击最近文件中的「待导出」项目继续编辑。" : "已结束编辑，原录屏未改动。"); }
        }
        finally { _busy = false; UpdateRecordingUi(); }
    }
    private void UpdateRecordingUi()
    {
        bool active = _recorder.State is RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping;
        RecordButtonLabel.Text = _recorder.IsBusy ? "停止录制" : "开始录屏";
        RecordingDot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, active ? "Danger" : "ReadyBrush");
        if (active) { _statusTimer.Start(); UpdateRecordingStatus(); } else _statusTimer.Stop();
        RepeatButton.IsEnabled = !_recorder.IsBusy && _settings.LastRegion != null;
        ScreenshotButton.IsEnabled = !_recorder.IsBusy;
        RecordButton.IsEnabled = !_stopBusy && _recorder.State != RecordingState.Starting;
        RecordMode.IsEnabled = !_recorder.IsBusy;
        SystemAudioBox.IsEnabled = MicrophoneBox.IsEnabled = FpsBox.IsEnabled = CursorBox.IsEnabled = !_recorder.IsBusy;
        SnapRecordWindowBox.IsEnabled = !_recorder.IsBusy;
        AudioDevicesButton.IsEnabled = !_recorder.IsBusy && !_busy;
    }
    private void UpdateRecordingStatus()
    {
        if (_recorder.State is RecordingState.Recording or RecordingState.Paused)
            SetStatus($"{(_recorder.IsPaused ? "已暂停" : "录制中")} · {_recorder.Elapsed:hh\\:mm\\:ss} · 再按录屏快捷键停止");
    }
    private void OnThemeChanged(string theme) => ThemeToggleLabel.Text = theme == "Light" ? "切换深色" : "切换浅色";
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
                .Concat(ScreenshotProjects.AssociatedImages().Select(path => new FileInfo(path)))
                .Concat(RecentList.Items.OfType<RecentItem>().Select(item => new FileInfo(item.Path)))
                .Concat(generatedPath != null ? new[] { new FileInfo(generatedPath) } : Array.Empty<FileInfo>())
                .Where(f => f.Exists && extensions.Contains(f.Extension.ToLowerInvariant()) && !f.Name.Contains(".partial.", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(f => string.Equals(f.FullName, generatedPath, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => f.LastWriteTime).Take(20)
                .Select(f => new RecentItem(f.FullName, (RecordingRecovery.Owns(f.FullName) ? "待导出 · " : "") + f.Name, $"{f.LastWriteTime:MM-dd HH:mm}  ·  {f.Length / 1024d / 1024d:F2} MB", $"{f.LastWriteTimeUtc.Ticks}:{f.Length}", _settings.MediaToolsPath)).ToArray();
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
        // Use the measured header, so complete rows also fit at fractional DPI.
        var content = (Grid)RecentCard.Child;
        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(RecentCard).DpiScaleY;
        double PixelRound(double value) => Math.Round(value * scale) / scale;
        double chrome = PixelRound(RecentCard.Padding.Top) + PixelRound(RecentCard.Padding.Bottom) + PixelRound(RecentCard.BorderThickness.Top) + PixelRound(RecentCard.BorderThickness.Bottom) + content.RowDefinitions[0].ActualHeight;
        double available = Math.Max(0, RecentSectionSlot.ActualHeight - RecentCard.Margin.Bottom);
        int rows = Math.Min(RecentList.Items.Count, Math.Clamp((int)Math.Floor((available - chrome + 0.1) / 48), 1, 5));
        RecentCard.Height = Math.Min(available, Math.Ceiling((chrome + (rows == 0 ? 30 : rows * 48)) * scale) / scale);
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
    private async Task EditScreenshotAsync(string path)
    {
        if (_busy || _recorder.IsBusy) return;
        if (_editor != null) { _editor.Activate(); return; }
        _busy = true; UpdateRecordingUi();
        try
        {
            var project = await ScreenshotProjects.LoadAsync(path);
            _editor = new EditorWindow(project.Original, _settings, Saved, project);
            _editor.Closed += (_, _) => { _editor = null; ShowMain(); }; _editor.Show();
        }
        catch (Exception ex) { Ui.Error(this, new InvalidDataException("无法继续编辑截图：" + ex.Message + " 现有图片与项目已保留。", ex)); }
        finally { _busy = false; UpdateRecordingUi(); }
    }
    private void AudioDevices_Click(object sender, RoutedEventArgs e)
    { if (_busy || _recorder.IsBusy) return; SaveControls(); new AudioDeviceWindow(this, _settings).ShowDialog(); }
    private void EditSavedRecording(string path)
    {
        if (_busy || _recorder.IsBusy) { SetStatus("请先完成当前操作。"); return; }
        try { EditRecording(path); }
        catch (Exception ex) { Ui.Error(this, ex); }
    }
    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        string previous = _settings.Theme;
        try
        {
            _settings.Theme = previous == "Light" ? "Dark" : "Light";
            _settings.Save(); ThemeService.Apply(_settings.Theme);
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
        if (e.OriginalSource is DependencyObject clicked && FindParent<Button>(clicked) != null) return;
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(RecentList, source) is ListBoxItem row && row.DataContext is RecentItem item)
            OpenRecent(item);
    }
    private void Recent_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || e.OriginalSource is DependencyObject source && FindParent<Button>(source) != null || RecentList.SelectedItem is not RecentItem item) return;
        e.Handled = true; OpenRecent(item);
    }
    private void RecentRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem row || row.ContextMenu != null) return;
        // Register menu events in code rather than inside the style resource.
        // Read the row's current item when clicked so recycled rows stay correct.
        var menu = new ContextMenu();
        menu.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("PanelStyles.xaml", UriKind.Relative) });
        menu.SetResourceReference(ContextMenu.BackgroundProperty, "PanelBackground");
        menu.SetResourceReference(ContextMenu.ForegroundProperty, "TextSecondary");
        menu.SetResourceReference(ContextMenu.BorderBrushProperty, "BorderBrush");
        var open = new MenuItem { Header = "打开文件" };
        var edit = new MenuItem { Header = "编辑录屏", Visibility = Visibility.Collapsed };
        var editImage = new MenuItem { Header = "继续编辑", Visibility = Visibility.Collapsed };
        var clean = new MenuItem { Header = "清理截图项目（保留图片）", Visibility = Visibility.Collapsed };
        var cleanVideo = new MenuItem { Header = "清理剪辑配置（保留原片）", Visibility = Visibility.Collapsed };
        var folder = new MenuItem { Header = "打开所在目录" };
        open.Click += (_, _) => { if (row.DataContext is RecentItem item) OpenRecent(item); };
        edit.Click += (_, _) => { if (row.DataContext is RecentItem { IsVideo: true } item) EditSavedRecording(item.Path); };
        editImage.Click += async (_, _) => { if (row.DataContext is RecentItem { IsVideo: false } item) await EditScreenshotAsync(item.Path); };
        clean.Click += (_, _) =>
        {
            if (row.DataContext is not RecentItem item || _busy || _recorder.IsBusy || _editor != null) return;
            if (MessageBox.Show(this, "清理关联的原图与标注项目后，将只能把导出图片作为普通底图编辑。导出图片保留。是否清理？", "清理截图项目", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { ScreenshotProjects.Clean(item.Path); RefreshRecent(); SetStatus("已清理截图项目，导出图片保留。"); } catch (Exception ex) { Ui.Error(this, ex); }
        };
        cleanVideo.Click += (_, _) =>
        {
            if (row.DataContext is not RecentItem item || _busy || _recorder.IsBusy) return;
            if (MessageBox.Show(this, "清理此原片的时间、删除片段和导出设置？原片保留。", "清理剪辑配置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { RecordingEditStore.Remove(item.Path); SetStatus("剪辑配置已清理，原片保留。"); } catch (Exception ex) { Ui.Error(this, ex); }
        };
        menu.Opened += (_, _) =>
        {
            edit.Visibility = row.DataContext is RecentItem { IsVideo: true } ? Visibility.Visible : Visibility.Collapsed;
            edit.IsEnabled = !_busy && !_recorder.IsBusy && row.DataContext is RecentItem current && File.Exists(current.Path);
            editImage.Visibility = row.DataContext is RecentItem { IsVideo: false } ? Visibility.Visible : Visibility.Collapsed;
            editImage.IsEnabled = !_busy && !_recorder.IsBusy;
            clean.Visibility = row.DataContext is RecentItem { IsVideo: false } picture && ScreenshotProjects.HasProject(picture.Path) ? Visibility.Visible : Visibility.Collapsed;
            cleanVideo.Visibility = row.DataContext is RecentItem { IsVideo: true } video && File.Exists(RecordingEditStore.FileFor(video.Path)) ? Visibility.Visible : Visibility.Collapsed;
            clean.IsEnabled = !_busy && !_recorder.IsBusy && _editor == null;
            cleanVideo.IsEnabled = !_busy && !_recorder.IsBusy;
        };
        folder.Click += (_, _) => { if (row.DataContext is RecentItem item) OpenRecentFolder(item); };
        var deleteHeader = new StackPanel { Orientation = Orientation.Horizontal };
        var deleteIcon = new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M 2,4 L 14,4 M 6,4 L 6,2 L 10,2 L 10,4 M 3.5,4 L 4.5,14 L 11.5,14 L 12.5,4 M 6.5,7 L 6.5,11 M 9.5,7 L 9.5,11"), Width = 14, Height = 14, Stretch = System.Windows.Media.Stretch.Uniform, StrokeThickness = 1.5, StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round, Margin = new Thickness(0, 0, 6, 0) };
        deleteIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Danger");
        var deleteText = new TextBlock { Text = "移到回收站" }; deleteText.SetResourceReference(TextBlock.ForegroundProperty, "DangerText");
        deleteHeader.Children.Add(deleteIcon); deleteHeader.Children.Add(deleteText);
        var delete = new MenuItem { Header = deleteHeader };
        delete.SetResourceReference(MenuItem.ForegroundProperty, "Danger");
        delete.Click += (_, _) => { if (row.DataContext is RecentItem item) DeleteRecent(item); };
        menu.Items.Add(open); menu.Items.Add(edit); menu.Items.Add(editImage); menu.Items.Add(folder); menu.Items.Add(clean); menu.Items.Add(cleanVideo); menu.Items.Add(new Separator()); menu.Items.Add(delete); row.ContextMenu = menu;
    }
    private void RecentMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || FindParent<ListBoxItem>(button) is not { } row) return;
        RecentRow_Loaded(row, e);
        row.ContextMenu!.PlacementTarget = button; row.ContextMenu.Placement = PlacementMode.Bottom; row.ContextMenu.IsOpen = true;
    }
    private void RecentFolder_Click(object sender, RoutedEventArgs e)
    { e.Handled = true; if (sender is FrameworkElement { DataContext: RecentItem item }) OpenRecentFolder(item); }
    private void DeleteRecent(RecentItem item)
    {
        if (_busy || _recorder.IsBusy || _editor != null) { SetStatus("请先完成当前操作。"); return; }
        try
        {
            bool project = !item.IsVideo && ScreenshotProjects.HasProject(item.Path);
            if ((project || RecordingRecovery.Owns(item.Path)) && MessageBox.Show(this, project ? "图片和关联原图、标注项目将一起移到回收站。是否继续？" : "此文件是可继续编辑的原片；原片和恢复、剪辑配置将一起清理。是否继续？", "删除关联文件", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            if (project) ScreenshotProjects.Clean(item.Path);
            if (RecordingRecovery.Owns(item.Path) && File.Exists(item.Path + ".json"))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path + ".json", Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            if (item.IsVideo) RecordingEditStore.Remove(item.Path);
            RefreshRecent(); SetStatus($"已移到回收站：{Path.GetFileName(item.Path)}");
        }
        catch (Exception ex) { Ui.Error(this, ex); }
    }
    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (DependencyObject? current = child; current != null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Button>((DependencyObject)e.OriginalSource) != null) return;
        if (e.ClickCount == 2) Maximize_Click(sender, e); else DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
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

internal sealed record RecentItem(string Path, string Name, string Detail, string Revision = "", string ToolsPath = "")
{
    public bool IsVideo => System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".mp4" or ".webm" or ".gif";
    public string MediaType => IsVideo ? "视频" : "图片";
}
