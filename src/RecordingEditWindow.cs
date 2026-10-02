using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickCapture;

internal sealed class RecordingEditWindow : Window
{
    private readonly string _source;
    private readonly RecordingMetadata _metadata;
    private readonly Settings _settings;
    private readonly MediaElement _preview = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Height = 260, Name = "RecordingPreview" };
    internal TrimTimeline Timeline { get; } = new();
    private readonly ComboBox _format, _quality, _speed, _gifFps;
    private readonly CheckBox _mute = new() { Content = "静音", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBox _folder, _filename;
    private readonly TextBlock _rangeLabel, _notice, _toolsNotice, _status;
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _export, _cancel, _play;
    private readonly StackPanel _options;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _exportCancellation;
    private MediaCapabilities _tools = new(null, null, "", "");
    private VideoInfo _info;
    private bool _ready, _exporting, _playing, _opened;
    internal string? SavedPath { get; private set; }
    internal Task Initialization { get; private set; } = Task.CompletedTask;
    internal RecordingEditWindow(Window owner, string source, Settings settings, RecordingMetadata metadata)
    {
        Ui.Theme(this); Owner = owner; _source = source; _settings = settings; _metadata = metadata;
        _info = new(metadata.Duration, 0, 0, metadata.Fps, metadata.HasAudio);
        Title = "轻截 · 录屏编辑与导出"; Width = 880; Height = 930; MinWidth = 680; MinHeight = 500;
        MaxHeight = Math.Max(500, SystemParameters.WorkArea.Height - 32); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var shell = new DockPanel(); Content = shell;
        var footer = new StackPanel { Margin = new Thickness(24, 8, 24, 16) }; DockPanel.SetDock(footer, Dock.Bottom); shell.Children.Add(footer);
        var root = new StackPanel { Margin = new Thickness(24, 24, 24, 0) }; shell.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var title = UiDesign.Text("录屏编辑与导出"); title.FontSize = UiDesign.Number("FontPageTitle"); title.FontWeight = FontWeights.SemiBold; root.Children.Add(title);
        var previewPanel = UiDesign.Panel(_preview); previewPanel.Padding = new Thickness(0); previewPanel.ClipToBounds = true; root.Children.Add(previewPanel);
        root.Children.Add(Timeline); _rangeLabel = UiDesign.Text("正在读取录屏…", true); root.Children.Add(_rangeLabel);
        var playback = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 16) }; root.Children.Add(playback);
        _play = Ui.Button("播放选段", TogglePlayback); _play.Name = "PlaySelection"; playback.Children.Add(_play);
        playback.Children.Add(Ui.Button("重置选段", () => { Timeline.SetRange(0, _info.Duration); Seek(0); }));
        _speed = new ComboBox { ItemsSource = new[] { "0.5×", "1×", "1.5×", "2×" }, SelectedIndex = 1, Width = 96, Margin = new Thickness(8, 0, 0, 0), Name = "ExportSpeed", ToolTip = "导出倍率（保留声音时维持音调）" }; playback.Children.Add(_speed); playback.Children.Add(_mute);
        _toolsNotice = UiDesign.Text("正在检测媒体工具…", true); root.Children.Add(_toolsNotice);
        _options = UiDesign.Section(root, "导出选项");
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new()); _options.Children.Add(row);
        var formatPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; row.Children.Add(formatPanel);
        var qualityPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; Grid.SetColumn(qualityPanel, 1); row.Children.Add(qualityPanel);
        var fpsPanel = new StackPanel(); Grid.SetColumn(fpsPanel, 2); row.Children.Add(fpsPanel);
        _format = UiDesign.Choice(formatPanel, "格式", new[] { "MP4 · H.264 / AAC", "WebM · VP9 / Opus", "GIF · 无声音" }, (int)settings.RecordingFormat); _format.Name = "RecordingFormat";
        _quality = UiDesign.Choice(qualityPanel, "质量", new[] { "低", "中", "高" }, (int)settings.RecordingQuality); _quality.Name = "RecordingQuality";
        int[] rates = { 5, 10, 15, 20, 30 }; _gifFps = UiDesign.Choice(fpsPanel, "GIF 帧率", new[] { "5 FPS", "10 FPS", "15 FPS", "20 FPS", "30 FPS" }, Math.Max(0, Array.IndexOf(rates, settings.GifFps))); _gifFps.Name = "GifFps";
        _notice = UiDesign.Text("", true); _options.Children.Add(_notice);
        var output = UiDesign.Section(root, "保存位置");
        _folder = UiDesign.Field(output, "目录", settings.OutputDirectory); _folder.Name = "RecordingFolder";
        var browse = Ui.Button("选择文件夹", () => { var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择录屏保存目录", InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : AppContext.BaseDirectory }; if (picker.ShowDialog(this) == true) _folder.Text = picker.FolderName; }); browse.HorizontalAlignment = HorizontalAlignment.Left; output.Children.Add(browse);
        _filename = UiDesign.Field(output, "文件名", Path.GetFileName(Paths.NewCapture(settings.OutputDirectory, VideoExportService.Extension(settings.RecordingFormat)))); _filename.Name = "RecordingFilename";
        _status = UiDesign.Text("取消或关闭后保留原始录屏，可在主界面的最近文件中继续编辑。", true); footer.Children.Add(_status); footer.Children.Add(_progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(buttons);
        _cancel = Ui.Button("稍后编辑", () => { if (_exporting) _exportCancellation?.Cancel(); else Close(); }); _cancel.Name = "CancelExport"; buttons.Children.Add(_cancel);
        _export = Ui.Button("导出并保存", async () => await ExportAsync()); _export.Name = "ExportRecording"; _export.IsEnabled = false; buttons.Children.Add(_export);
        Timeline.RangeChanged += () => { UpdateRange(); if (_ready) Seek(Timeline.Start); };
        Timeline.SeekRequested += Seek;
        _format.SelectionChanged += (_, _) => { _filename.Text = Path.ChangeExtension(_filename.Text, VideoExportService.Extension(Format)); UpdateOptions(); };
        _quality.SelectionChanged += (_, _) => UpdateOptions(); _speed.SelectionChanged += (_, _) => { UpdateRange(); UpdateOptions(); }; _mute.Checked += (_, _) => UpdateOptions(); _mute.Unchecked += (_, _) => UpdateOptions();
        _preview.MediaOpened += (_, _) => { _opened = true; if (_info.Duration <= 0 && _preview.NaturalDuration.HasTimeSpan) { _info = _info with { Duration = _preview.NaturalDuration.TimeSpan.TotalSeconds, Width = _preview.NaturalVideoWidth, Height = _preview.NaturalVideoHeight }; Timeline.Initialize(_info.Duration, _info.Fps); } _preview.Pause(); };
        _preview.MediaFailed += (_, e) => { _status.Text = "预览不可用：" + e.ErrorException.Message + "。仍可保存原片或使用媒体工具导出。"; _play.IsEnabled = false; };
        _preview.MediaEnded += (_, _) => Pause();
        _timer.Tick += (_, _) => { Timeline.SetPosition(_preview.Position.TotalSeconds); if (_playing && _preview.Position.TotalSeconds >= Timeline.End) { Pause(); Seek(Timeline.Start); } };
        Loaded += (_, _) => Initialization = InitializeAsync(); Closing += OnClosing;
        Closed += (_, _) => { _lifetime.Cancel(); _timer.Stop(); _preview.Close(); _lifetime.Dispose(); };
    }
    private RecordingFormat Format => (RecordingFormat)Math.Max(0, _format.SelectedIndex);
    private double Speed => new[] { 0.5, 1d, 1.5, 2d }[Math.Max(0, _speed.SelectedIndex)];
    private async Task InitializeAsync()
    {
        try
        {
            _tools = await MediaTools.DetectAsync(_settings, cancellationToken: _lifetime.Token);
            if (_tools.Ffprobe != null) _info = await VideoExportService.ProbeAsync(_source, _tools, _lifetime.Token);
            Timeline.Initialize(_info.Duration, _info.Fps);
            _preview.Source = new Uri(Path.GetFullPath(_source)); _preview.Play(); _timer.Start();
            _toolsNotice.Text = _tools.CanEdit ? "可裁切、变速与导出 MP4 / WebM / GIF" : "媒体工具缺失或编码器不完整：可保存完整 MP4 原片。补齐 FFmpeg / ffprobe 后可裁切、变速及导出其他格式。";
            Timeline.IsEnabled = _tools.CanEdit; _speed.IsEnabled = _mute.IsEnabled = _tools.CanEdit;
            // A missing tool never blocks saving the native MP4 master.
            if (!_tools.Supports(Format)) { _format.SelectedIndex = 0; _quality.SelectedIndex = (int)_metadata.Quality; }
            _ready = true; UpdateRange(); UpdateOptions();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLog.Write(ex); _tools = new(null, null, "", ""); _ready = true;
            Timeline.Initialize(_info.Duration, _info.Fps); Timeline.IsEnabled = false; _speed.IsEnabled = _mute.IsEnabled = false;
            _format.SelectedIndex = 0; _quality.SelectedIndex = (int)_metadata.Quality;
            _toolsNotice.Text = "读取媒体工具失败：" + ex.Message + "。可保存完整 MP4 原片。"; UpdateOptions();
        }
    }
    private void UpdateRange()
    { if (_rangeLabel != null) _rangeLabel.Text = $"起点 {Timeline.Start:F2}s  ·  终点 {Timeline.End:F2}s  ·  选段 {Timeline.End - Timeline.Start:F2}s  ·  导出 {(Timeline.End - Timeline.Start) / (_speed == null ? 1 : Speed):F2}s"; }
    private void UpdateOptions()
    {
        if (_notice == null || _filename == null) return;
        bool gif = Format == RecordingFormat.Gif; _gifFps.IsEnabled = gif && !_exporting; _mute.IsEnabled = !gif && _tools.CanEdit && !_exporting;
        _preview.IsMuted = gif || _mute.IsChecked == true; _preview.SpeedRatio = Speed;
        _notice.Text = Format switch { RecordingFormat.Gif => "GIF 不支持声音：导出会丢弃音频。动画体积通常较大；低 / 中质量分别限制宽度为 480 / 720 像素，高质量保留原尺寸。", RecordingFormat.WebM => "WebM 使用 VP9 / Opus，播放器兼容性取决于编码支持；导出会重新编码。", _ => "MP4 使用 H.264 / AAC。完整原片、1×、保留声音且质量不变时直接保存；其他操作会重新编码。" };
        bool available = _tools.Supports(Format) || Format == RecordingFormat.Mp4;
        _quality.IsEnabled = !_exporting && _tools.Supports(Format);
        if (!_tools.Supports(Format) && Format != RecordingFormat.Mp4) _notice.Text += "\n当前缺少对应编码器，请选择 MP4 原片或补齐媒体工具。";
        _export.IsEnabled = _ready && !_exporting && available;
    }
    private void Seek(double seconds) { if (_opened) _preview.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _info.Duration)); Timeline.SetPosition(seconds); }
    private void Pause() { _preview.Pause(); _playing = false; _play.Content = "播放选段"; }
    private void TogglePlayback()
    { if (!_opened) return; if (_playing) Pause(); else { if (_preview.Position.TotalSeconds < Timeline.Start || _preview.Position.TotalSeconds >= Timeline.End) Seek(Timeline.Start); _preview.SpeedRatio = Speed; _preview.Play(); _playing = true; _play.Content = "暂停预览"; } }
    internal async Task ExportAsync()
    {
        if (!_ready || _exporting) return;
        try
        {
            string name = _filename.Text.Trim(); if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("请输入有效的文件名。");
            string target = Path.Combine(Path.GetFullPath(_folder.Text.Trim()), Path.ChangeExtension(name, VideoExportService.Extension(Format)));
            bool overwrite = File.Exists(target); if (overwrite && MessageBox.Show(this, "文件已存在，是否替换？", "保存录屏", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var request = new VideoExportRequest(_source, target, Format, (ExportQuality)_quality.SelectedIndex, Timeline.Start, Timeline.End, Speed, _mute.IsChecked == true, new[] { 5, 10, 15, 20, 30 }[_gifFps.SelectedIndex], _metadata.Quality, overwrite);
            VideoExportService.Validate(request, _info, _tools); Pause(); _exporting = true; _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _options.IsEnabled = false; Timeline.IsEnabled = _speed.IsEnabled = _folder.IsEnabled = _filename.IsEnabled = _play.IsEnabled = false;
            _cancel.Content = "取消导出"; _status.Text = "正在导出…"; _progress.Value = 0; UpdateOptions();
            await VideoExportService.ExportAsync(request, _info, _tools, new Progress<double>(value => _progress.Value = value), _exportCancellation.Token);
            SavedPath = target; _exporting = false; _preview.Close(); DialogResult = true;
        }
        catch (OperationCanceledException) { _status.Text = "已取消导出，原始录屏保留；可调整后重试或稍后编辑。"; }
        catch (Exception ex) { _status.Text = "导出失败，原始录屏保留。"; Ui.Error(this, ex); }
        finally
        {
            _exporting = false; _exportCancellation?.Dispose(); _exportCancellation = null;
            _options.IsEnabled = _folder.IsEnabled = _filename.IsEnabled = _play.IsEnabled = true; Timeline.IsEnabled = _speed.IsEnabled = _tools.CanEdit; _cancel.Content = "稍后编辑"; UpdateOptions();
        }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (_exporting) { e.Cancel = true; _exportCancellation?.Cancel(); _status.Text = "正在取消导出…"; } }
}
