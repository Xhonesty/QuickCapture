using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickCapture;

internal sealed class RecordingEditWindow : Window
{
    private readonly string _source;
    private readonly RecordingMetadata _metadata;
    private readonly Settings _settings;
    private readonly MediaElement _preview = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, ScrubbingEnabled = true, Stretch = Stretch.Fill, Name = "RecordingPreview" };
    private readonly Grid _videoViewport = new() { Background = Brushes.Black, ClipToBounds = true, Name = "RecordingViewport" };
    private readonly Canvas _videoCanvas = new() { IsHitTestVisible = false };
    internal VideoCropOverlay CropOverlay { get; } = new();
    private readonly StackPanel _cropPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8), ToolTip = "拖动框内移动，拖动边框或手柄缩放；Esc 取消本次调整。" };
    private readonly Button _cropButton;
    private readonly ComboBox _cropRatio;
    private readonly TextBlock _cropLabel;
    private VideoCrop? _crop;
    private bool _cropEditing;
    internal VideoCrop? AppliedCrop => _crop;
    internal TrimTimeline Timeline { get; } = new();
    private readonly ComboBox _format, _quality, _speed, _exportFps;
    private readonly CheckBox _mute = new() { Content = "静音", Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBox _folder, _filename;
    private readonly TextBlock _rangeLabel, _notice, _toolsNotice, _status;
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 4, 0, 4) };
    private readonly Button _export, _cancel, _play;
    private readonly StackPanel _options;
    private readonly WrapPanel _playback;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _exportCancellation;
    private MediaCapabilities _tools = new(null, null, "", "");
    private readonly System.Collections.Generic.Dictionary<RecordingFormat, int> _formatFps;
    private bool _changingFps;
    private VideoInfo _info;
    private bool _ready, _exporting, _playing, _opened;
    private string? _previewCopy;
    private double? _pendingSeek;
    private VideoRange[] _deleted = Array.Empty<VideoRange>();
    private readonly ListBox _cutList = new() { Height = 62, Name = "DeletedSegments" };
    private readonly TextBox _cutStart = new() { Width = 75, Text = "0", Name = "DeleteStart" }, _cutEnd = new() { Width = 75, Text = "0", Name = "DeleteEnd" };
    private readonly Expander _cutExpander;
    private readonly Grid _exportDetails;
    private readonly Stack<RecordingEditState> _undo = new(), _redo = new();
    private RecordingEditState? _lastEdit;
    private bool _restoring, _dragging, _configBlocked, _closeApproved, _saveFailed;
    internal Task ConfigurationSaved { get; private set; } = Task.CompletedTask;
    internal IReadOnlyList<VideoRange> Deleted => _deleted;
    internal bool PreserveMaster => _crop != null || _deleted.Length > 0;
    internal string? SavedPath { get; private set; }
    internal Task Initialization { get; private set; } = Task.CompletedTask;
    internal RecordingEditWindow(Window owner, string source, Settings settings, RecordingMetadata metadata)
    {
        Ui.Theme(this); Owner = owner; _source = source; _settings = settings; _metadata = metadata;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("RecordingEditorStyles.xaml", UriKind.Relative) });
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        _info = new(metadata.Duration, 0, 0, metadata.Fps, metadata.HasAudio);
        Title = "录屏编辑与导出"; Width = 880; Height = Math.Min(820, SystemParameters.WorkArea.Height - 32); MinWidth = 680; MinHeight = Math.Min(600, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var shell = new DockPanel(); Content = shell;
        RecordingWindowChrome.Install(this, shell);
        var footer = new StackPanel { Margin = new Thickness(24, 4, 24, 12) }; DockPanel.SetDock(footer, Dock.Bottom); shell.Children.Add(footer);
        footer.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
        var root = new Grid { Margin = new Thickness(24, 12, 24, 0), Name = "RecordingContent" }; shell.Children.Add(root);
        void AddRow(UIElement element, bool stretch = false)
        {
            int index = root.RowDefinitions.Count;
            root.RowDefinitions.Add(new() { Height = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetRow(element, index); root.Children.Add(element);
        }
        var title = UiDesign.Text("录屏编辑与导出"); title.FontSize = UiDesign.Number("FontPageTitle"); title.FontWeight = FontWeights.SemiBold; AddRow(title);
        _videoCanvas.Children.Add(_preview); _videoViewport.Children.Add(_videoCanvas); _videoViewport.Children.Add(CropOverlay);
        _videoViewport.SizeChanged += (_, _) => LayoutPreview();
        var previewPanel = UiDesign.Panel(_videoViewport); previewPanel.Padding = new Thickness(0); previewPanel.ClipToBounds = true; previewPanel.Margin = new Thickness(0, 0, 0, 8); AddRow(previewPanel, true);
        AddRow(Timeline); Timeline.ToolTip = "青绿为保留范围，红色为删除片段；所有边界按原片帧率对齐。"; _rangeLabel = UiDesign.Text("正在读取录屏…", true); AddRow(_rangeLabel);
        var playback = _playback = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) }; AddRow(playback);
        _play = Ui.Button("播放选段", TogglePlayback); _play.Name = "PlaySelection"; playback.Children.Add(_play);
        playback.Children.Add(Ui.Button("重置选段", () => { Timeline.SetRange(0, _info.Duration); Seek(0); }));
        _speed = new ComboBox { ItemsSource = new[] { "0.5×", "1×", "1.5×", "2×" }, SelectedIndex = 1, Width = 96, Margin = new Thickness(8, 0, 0, 0), Name = "ExportSpeed", ToolTip = "导出倍率（保留声音时维持音调）" }; playback.Children.Add(_speed); playback.Children.Add(_mute);
        _cropButton = Ui.Button("画面裁剪", BeginCrop); _cropButton.Name = "EditVideoCrop"; _cropButton.IsEnabled = false; playback.Children.Add(_cropButton);
        var cutPanel = new StackPanel(); _cutExpander = new Expander { Header = "删除中间片段 · 红色区间 / 撤销重做", Content = cutPanel, Margin = new Thickness(0, 0, 0, 6), ToolTip = "小窗口展开时暂时收起导出设置；收起此面板即可恢复。" }; AddRow(_cutExpander);
        _cutExpander.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        _cutStart.ToolTip = "删除起点（原片秒数）"; _cutEnd.ToolTip = "删除终点（原片秒数）";
        var cutActions = new WrapPanel(); cutPanel.Children.Add(cutActions);
        cutActions.Children.Add(_cutStart); cutActions.Children.Add(Ui.Button("标记起点", () => _cutStart.Text = _preview.Position.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)));
        cutActions.Children.Add(_cutEnd); cutActions.Children.Add(Ui.Button("标记终点", () => _cutEnd.Text = _preview.Position.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)));
        cutActions.Children.Add(Ui.Button("删除片段", () => ChangeCut(false))); cutActions.Children.Add(Ui.Button("调整边界", () => ChangeCut(true)));
        cutPanel.Children.Add(_cutList); var historyActions = new WrapPanel(); cutPanel.Children.Add(historyActions);
        historyActions.Children.Add(Ui.Button("恢复所选片段", () => { if (_cutList.SelectedIndex >= 0) SetCuts(_deleted.Where((_, i) => i != _cutList.SelectedIndex)); }));
        historyActions.Children.Add(Ui.Button("撤销", UndoEdit)); historyActions.Children.Add(Ui.Button("重做", RedoEdit));
        historyActions.Children.Add(Ui.Button("保存配置", () => SaveEdit(CurrentEdit())));
        _cutList.SelectionChanged += (_, _) => { if (_cutList.SelectedItem is VideoRange r) { _cutStart.Text = r.Start.ToString("F3", CultureInfo.InvariantCulture); _cutEnd.Text = r.End.ToString("F3", CultureInfo.InvariantCulture); PreviewRange(r.Start); } };
        cutPanel.Children.Add(UiDesign.Text("输入原片秒数或用播放位置标记。删除后自动合并重叠/相邻片段。Ctrl+Z / Ctrl+Y 撤销重做。", true));
        AddRow(_cropPanel);
        var cropActions = new WrapPanel(); _cropPanel.Children.Add(cropActions);
        _cropRatio = new ComboBox { ItemsSource = new[] { "自由比例", "原始比例", "16:9", "9:16", "1:1" }, SelectedIndex = 0, Width = 120, Name = "VideoCropRatio", ToolTip = "裁剪框比例" }; cropActions.Children.Add(_cropRatio);
        var resetCrop = Ui.Button("重置裁剪", ResetCrop); resetCrop.Name = "ResetVideoCrop"; cropActions.Children.Add(resetCrop);
        var confirmCrop = Ui.Button("确认裁剪", ConfirmCrop); confirmCrop.Name = "ConfirmVideoCrop"; cropActions.Children.Add(confirmCrop);
        confirmCrop.Style = (Style)FindResource("RecordingPrimaryButton");
        var cancelCrop = Ui.Button("取消裁剪", CancelCrop); cancelCrop.Name = "CancelVideoCrop"; cropActions.Children.Add(cancelCrop);
        _cropLabel = UiDesign.Text("保留完整画面", true); AddRow(_cropLabel);
        _cropRatio.SelectionChanged += (_, _) => { if (_cropEditing) CropOverlay.SetAspectRatio(_cropRatio.SelectedIndex switch { 1 => _info.Width / (double)_info.Height, 2 => 16d / 9, 3 => 9d / 16, 4 => 1, _ => 0 }); };
        CropOverlay.SelectionChanged += UpdateCropLabel;
        PreviewKeyDown += (_, e) => { if (_cropEditing && e.Key == Key.Escape) { CancelCrop(); e.Handled = true; } };
        _toolsNotice = UiDesign.Text("正在检测媒体工具…", true); AddRow(_toolsNotice);
        var details = _exportDetails = new Grid(); details.ColumnDefinitions.Add(new()); details.ColumnDefinitions.Add(new() { Width = new GridLength(1.35, GridUnitType.Star) }); AddRow(details);
        _cutExpander.Expanded += (_, _) => CompactPanels(); _cutExpander.Collapsed += (_, _) => CompactPanels(); SizeChanged += (_, _) => CompactPanels();
        _options = UiDesign.Section(details, "导出选项"); ((Border)details.Children[0]).Margin = new Thickness(0, 0, 12, 0);
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new()); row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto }); _options.Children.Add(row);
        var formatPanel = new StackPanel(); Grid.SetColumnSpan(formatPanel, 2); row.Children.Add(formatPanel);
        var qualityPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; Grid.SetRow(qualityPanel, 1); row.Children.Add(qualityPanel);
        var fpsPanel = new StackPanel(); Grid.SetRow(fpsPanel, 1); Grid.SetColumn(fpsPanel, 1); row.Children.Add(fpsPanel);
        _format = UiDesign.Choice(formatPanel, "格式", new[] { "MP4 · H.264 / AAC", "WebM · VP9 / Opus", "GIF · 无声音" }, (int)settings.RecordingFormat); _format.Name = "RecordingFormat";
        _quality = UiDesign.Choice(qualityPanel, "质量", new[] { "低", "中", "高" }, (int)settings.RecordingQuality); _quality.Name = "RecordingQuality";
        _formatFps = Enum.GetValues<RecordingFormat>().ToDictionary(format => format, settings.GetRecordingFps);
        var rates = RecordingFrameRates.For(settings.RecordingFormat);
        _exportFps = UiDesign.Choice(fpsPanel, "导出帧率", rates.Select(fps => $"{fps} FPS").ToArray(), Math.Max(0, Array.IndexOf(rates, settings.GetRecordingFps(settings.RecordingFormat)))); _exportFps.Name = "RecordingFps";
        _notice = UiDesign.Text("", true); _options.Children.Add(_notice);
        _options.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("Text") { Source = _notice });
        SizeChanged += (_, _) => _notice.Visibility = ActualHeight < 700 ? Visibility.Collapsed : Visibility.Visible;
        var output = UiDesign.Section(details, "保存位置"); var outputCard = (Border)details.Children[1]; Grid.SetColumn(outputCard, 1); outputCard.Margin = new Thickness(0);
        bool savedRecording = !RecordingRecovery.Owns(source);
        string outputDirectory = savedRecording ? Path.GetDirectoryName(Path.GetFullPath(source))! : settings.OutputDirectory;
        output.Children.Add(UiDesign.Text("目录"));
        var folderRow = new Grid { Margin = new Thickness(0, 0, 0, UiDesign.Number("SpaceM")) };
        folderRow.ColumnDefinitions.Add(new()); folderRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); output.Children.Add(folderRow);
        _folder = new TextBox { Text = outputDirectory, Name = "RecordingFolder" }; folderRow.Children.Add(_folder);
        var browse = Ui.Button("选择文件夹", () => { var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择录屏保存目录", InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : AppContext.BaseDirectory }; if (picker.ShowDialog(this) == true) _folder.Text = picker.FolderName; });
        browse.Margin = new Thickness(UiDesign.Number("SpaceM"), 0, 0, 0); browse.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(browse, 1); folderRow.Children.Add(browse);
        string outputName = savedRecording ? EditedName(source, VideoExportService.Extension(settings.RecordingFormat)) : Path.GetFileName(Paths.NewCapture(outputDirectory, VideoExportService.Extension(settings.RecordingFormat)));
        _filename = UiDesign.Field(output, "文件名", outputName); _filename.Name = "RecordingFilename";
        output.Children.Add(UiDesign.Text(savedRecording ? "编辑后另存为副本，保留原录屏。" : "保存后可从最近文件再次编辑。", true));
        _status = UiDesign.Text(savedRecording ? "正在编辑已保存的录屏；关闭或取消不会修改原文件。" : "取消或关闭后保留原始录屏，可在最近文件中继续编辑。", true); _status.MaxHeight = 36;
        _status.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("Text") { Source = _status }); footer.Children.Add(_status); footer.Children.Add(_progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(buttons);
        _cancel = Ui.Button("稍后编辑", () => { if (_exporting) _exportCancellation?.Cancel(); else Close(); }); _cancel.Name = "CancelExport"; buttons.Children.Add(_cancel);
        _export = Ui.Button("导出并保存", async () => await ExportAsync()); _export.Name = "ExportRecording"; _export.IsEnabled = false; buttons.Children.Add(_export);
        _export.Style = (Style)FindResource("RecordingPrimaryButton");
        Timeline.RangeChanged += () => { UpdateRange(); EditChanged(); };
        Timeline.EditStarted += () => _dragging = true;
        Timeline.EditCompleted += () => { _dragging = false; EditChanged(); };
        PreviewKeyDown += (_, e) => { if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Z or Key.Y && e.OriginalSource is not TextBox) { if (e.Key == Key.Z) UndoEdit(); else RedoEdit(); e.Handled = true; } };
        Timeline.RangePreviewRequested += PreviewRange;
        Timeline.SeekRequested += Seek;
        _format.SelectionChanged += (_, _) => { _filename.Text = Path.ChangeExtension(_filename.Text, VideoExportService.Extension(Format)); UpdateFpsOptions(); UpdateOptions(); EditChanged(); };
        _exportFps.SelectionChanged += (_, _) => { if (!_changingFps && _exportFps.SelectedIndex >= 0) { _formatFps[Format] = RecordingFrameRates.For(Format)[_exportFps.SelectedIndex]; EditChanged(); } };
        _quality.SelectionChanged += (_, _) => { UpdateOptions(); EditChanged(); }; _speed.SelectionChanged += (_, _) => { UpdateRange(); UpdateOptions(); EditChanged(); }; _mute.Checked += (_, _) => { UpdateOptions(); EditChanged(); }; _mute.Unchecked += (_, _) => { UpdateOptions(); EditChanged(); };
        _preview.MediaOpened += (_, _) => { _opened = true; if (_info.Width <= 0 || _info.Height <= 0) _info = _info with { Width = _preview.NaturalVideoWidth, Height = _preview.NaturalVideoHeight }; if (_info.Duration <= 0 && _preview.NaturalDuration.HasTimeSpan) { _info = _info with { Duration = _preview.NaturalDuration.TimeSpan.TotalSeconds }; Timeline.Initialize(_info.Duration, _info.Fps); } _preview.Pause(); Seek(_pendingSeek ?? Timeline.Start); LayoutPreview(); UpdateOptions(); };
        _preview.MediaFailed += (_, e) => { _status.Text = "预览不可用：" + e.ErrorException.Message + "。仍可保存原片或使用媒体工具导出。"; _play.IsEnabled = false; };
        _preview.MediaEnded += (_, _) => Pause();
        _timer.Tick += (_, _) => { Timeline.SetPosition(_preview.Position.TotalSeconds); if (_playing) { double next = VideoCuts.Next(_preview.Position.TotalSeconds, Timeline.Start, Timeline.End, _deleted); if (next < Timeline.End && next > _preview.Position.TotalSeconds + .001) Seek(next); } if (_playing && _preview.Position.TotalSeconds >= Timeline.End) { Pause(); Seek(Timeline.Start); } };
        Loaded += (_, _) => Initialization = InitializeAsync(); Closing += OnClosing;
        Closed += async (_, _) => { await ConfigurationSaved; _lifetime.Cancel(); _timer.Stop(); _preview.Close(); await CleanupPreviewAsync(); _lifetime.Dispose(); };
    }
    private RecordingFormat Format => (RecordingFormat)Math.Max(0, _format.SelectedIndex);
    private double Speed => new[] { 0.5, 1d, 1.5, 2d }[Math.Max(0, _speed.SelectedIndex)];
    private static string EditedName(string source, string extension)
    {
        string stem = Path.GetFileNameWithoutExtension(source) + "-编辑", directory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        string name = stem + "." + extension;
        for (int index = 2; File.Exists(Path.Combine(directory, name)); index++) name = $"{stem}-{index}.{extension}";
        return name;
    }
    private async Task InitializeAsync()
    {
        CancellationToken token = _lifetime.Token;
        try
        {
            _tools = await MediaTools.DetectAsync(_settings, cancellationToken: token);
            if (_tools.Ffprobe != null)
            {
                _info = await VideoExportService.ProbeAsync(_source, _tools, token);
                if (_metadata.Crop is { } stored)
                {
                    try { stored.Validate(_info); _crop = stored; _cropButton.Content = "调整裁剪"; }
                    catch (ArgumentException ex) { ErrorLog.Write(ex); _status.Text = "已保存的裁剪范围不适用于当前原片，已恢复完整画面，可重新调整。"; }
                }
            }
            Timeline.Initialize(_info.Duration, _info.Fps);
            try { var storedEdit = await RecordingEditStore.LoadAsync(_source); if (storedEdit != null) ApplyEdit(storedEdit); }
            catch (Exception ex) { _configBlocked = true; _status.Text = ex.Message + " 请在最近文件中清理剪辑配置后重新打开。"; }
            string previewSource = await PreparePreviewAsync(token); token.ThrowIfCancellationRequested();
            _preview.Source = new Uri(Path.GetFullPath(previewSource)); _preview.Play(); _timer.Start();
            _toolsNotice.Text = _tools.CanEdit ? "可截取时间、裁剪画面、变速与导出 MP4 / WebM / GIF" : "媒体工具缺失或编码器不完整：可保存完整 MP4 原片。补齐 FFmpeg / ffprobe 后可截取时间、裁剪画面、变速及导出其他格式。";
            _toolsNotice.Visibility = _tools.CanEdit ? Visibility.Collapsed : Visibility.Visible;
            Timeline.IsEnabled = _tools.CanEdit && !_configBlocked; _speed.IsEnabled = _mute.IsEnabled = _tools.CanEdit && !_configBlocked;
            // A missing tool never blocks saving the native MP4 master.
            if (!_tools.Supports(Format)) { _format.SelectedIndex = 0; _quality.SelectedIndex = (int)_metadata.Quality; }
            _ready = true; _lastEdit = CurrentEdit(); LayoutPreview(); UpdateRange(); UpdateCropLabel(); UpdateOptions();
        }
        catch (OperationCanceledException) { await CleanupPreviewAsync(); }
        catch (Exception ex)
        {
            if (!RecordingRecovery.Owns(_source))
            {
                ErrorLog.Write(ex); _ready = false; Timeline.IsEnabled = _play.IsEnabled = false;
                _toolsNotice.Text = "无法读取该录屏，请检查文件是否完整。";
                _status.Text = "无法打开录屏：" + ex.Message; _status.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); UpdateOptions(); return;
            }
            ErrorLog.Write(ex); _tools = new(null, null, "", ""); _ready = true;
            Timeline.Initialize(_info.Duration, _info.Fps); Timeline.IsEnabled = false; _speed.IsEnabled = _mute.IsEnabled = false;
            _format.SelectedIndex = 0; _quality.SelectedIndex = (int)_metadata.Quality;
            _toolsNotice.Text = "读取媒体工具失败：" + ex.Message + "。可保存完整 MP4 原片。"; UpdateOptions();
        }
    }
    private async Task<string> PreparePreviewAsync(CancellationToken token)
    {
        if (!_tools.CanEdit || (Path.GetExtension(_source).Equals(".mp4", StringComparison.OrdinalIgnoreCase) && _info.VideoCodec == "h264" && (!_info.HasAudio || _info.AudioCodec == "aac"))) return _source;
        // Windows MediaElement cannot reliably decode WebM or animated GIF.
        // Only the preview uses this compatible copy; edits/export always read the original.
        string directory = Path.Combine(Paths.Data, "RecordingPreviews"); Directory.CreateDirectory(directory);
        _previewCopy = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp4");
        string previousStatus = _status.Text; _status.Text = "正在准备视频预览，可关闭窗口取消…";
        var result = await MediaTools.RunAsync(_tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-i", _source, "-map", "0:v:0", "-map", "0:a?", "-vf", "scale='trunc(min(1280,iw)/2)*2':-2", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "25", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", _previewCopy }, token);
        if (result.ExitCode != 0) { await CleanupPreviewAsync(); _status.Text = "兼容预览准备失败；仍可使用媒体工具导出。"; return _source; }
        _status.Text = previousStatus; return _previewCopy;
    }
    private async Task CleanupPreviewAsync()
    {
        if (_previewCopy == null) return;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try { File.Delete(_previewCopy); return; }
            catch (IOException) when (attempt < 3) { await Task.Delay(100 * (attempt + 1)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write(ex); return; }
        }
    }
    private void UpdateRange()
    { if (_rangeLabel != null) _rangeLabel.Text = $"原片 {_info.Duration:F2} 秒  ·  保留 {VideoCuts.Kept(Timeline.Start, Timeline.End, _deleted).Sum(r => r.End-r.Start):F2} 秒  ·  导出 {VideoCuts.Kept(Timeline.Start, Timeline.End, _deleted).Sum(r => r.End-r.Start)/Speed:F2} 秒  ·  {Timeline.Start:F3}–{Timeline.End:F3}"; }
    private RecordingEditState CurrentEdit() => new(Timeline.Start, Timeline.End, _deleted.ToArray(), Speed, _mute.IsChecked == true, _crop, Format, (ExportQuality)Math.Max(0, _quality.SelectedIndex), _formatFps[Format]);
    private void EditChanged()
    {
        if (!_ready || _restoring || _dragging) return;
        var state = CurrentEdit();
        if (_lastEdit != null && JsonSerializer.Serialize(_lastEdit) == JsonSerializer.Serialize(state)) return;
        if (_lastEdit != null) _undo.Push(_lastEdit); _redo.Clear(); _lastEdit = state; SaveEdit(state);
    }
    private void SaveEdit(RecordingEditState state)
    {
        if (_configBlocked) return;
        ConfigurationSaved = PersistAsync(ConfigurationSaved, state);
    }
    private async Task PersistAsync(Task previous, RecordingEditState state)
    {
        await previous;
        try { await RecordingEditStore.SaveAsync(_source, state); _saveFailed = false; }
        catch (Exception ex) { _saveFailed = true; ErrorLog.Write(ex); _status.Text = "配置保存失败，原片仍保留：" + ex.Message + "。请修复目录权限后点击保存配置重试。"; }
    }
    private void ApplyEdit(RecordingEditState state)
    {
        if (!double.IsFinite(state.Start) || !double.IsFinite(state.End) || state.Start < 0 || state.End > _info.Duration + .001 || state.End <= state.Start || state.Speed is not (.5 or 1 or 1.5 or 2) || !Enum.IsDefined(state.Format) || !Enum.IsDefined(state.Quality)) throw new InvalidDataException("剪辑配置数据损坏，原片与配置已保留。");
        state.Crop?.Validate(_info);
        _restoring = true;
        try
        {
            _deleted = VideoCuts.Normalize(state.Deleted, _info.Duration, _info.Fps); Timeline.SetRange(state.Start, state.End); Timeline.SetDeleted(_deleted); _cutList.ItemsSource = _deleted;
            _crop = state.Crop; _crop?.Validate(_info); _speed.SelectedIndex = Array.IndexOf(new[] { .5, 1d, 1.5, 2d }, state.Speed); _mute.IsChecked = state.Mute;
            _format.SelectedIndex = (int)state.Format; _quality.SelectedIndex = (int)state.Quality;
            if (RecordingFrameRates.For(state.Format).Contains(state.Fps)) { _formatFps[state.Format] = state.Fps; UpdateFpsOptions(); }
            LayoutPreview(); UpdateRange(); UpdateCropLabel(); UpdateOptions();
        }
        finally { _restoring = false; }
    }
    internal void SetCuts(IEnumerable<VideoRange> ranges)
    {
        if (_exporting) return;
        var cuts = VideoCuts.Normalize(ranges, _info.Duration, _info.Fps);
        if (VideoCuts.Kept(Timeline.Start, Timeline.End, cuts).Sum(r => r.End-r.Start) < 1/_info.Fps - .000001) throw new ArgumentException("至少保留一帧，不能删除全部内容。");
        Pause(); _deleted = cuts; Timeline.SetDeleted(cuts); _cutList.ItemsSource = cuts; UpdateRange(); UpdateOptions(); EditChanged();
    }
    private void ChangeCut(bool replace)
    {
        try
        {
            if (!double.TryParse(_cutStart.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double start) || !double.TryParse(_cutEnd.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double end) || start < 0 || end > _info.Duration || end-start < .5/_info.Fps) throw new ArgumentException("请输入有效的原片秒数，至少标记一帧。");
            if (replace && _cutList.SelectedIndex < 0) throw new ArgumentException("请先选择要调整的删除片段。");
            SetCuts(_deleted.Where((_, i) => !replace || i != _cutList.SelectedIndex).Append(new(start, end)));
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    internal void UndoEdit() { if (!_exporting && _undo.TryPop(out var state)) { _redo.Push(CurrentEdit()); ApplyEdit(state); _lastEdit = state; SaveEdit(state); } }
    internal void RedoEdit() { if (!_exporting && _redo.TryPop(out var state)) { _undo.Push(CurrentEdit()); ApplyEdit(state); _lastEdit = state; SaveEdit(state); } }
    private void CompactPanels()
    {
        if (_exportDetails == null || _cropLabel == null) return;
        _exportDetails.Visibility = _cutExpander.IsExpanded && ActualHeight < 760 ? Visibility.Collapsed : Visibility.Visible;
        _cropLabel.Visibility = ActualHeight < 700 && !_cropEditing && _crop == null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UpdateFpsOptions()
    {
        _changingFps = true;
        try
        {
            var rates = RecordingFrameRates.For(Format);
            _exportFps.ItemsSource = rates.Select(fps => $"{fps} FPS").ToArray();
            _exportFps.SelectedIndex = Math.Max(0, Array.IndexOf(rates, _formatFps[Format]));
        }
        finally { _changingFps = false; }
    }
    private void UpdateOptions()
    {
        if (_notice == null || _filename == null) return;
        bool gif = Format == RecordingFormat.Gif; _exportFps.IsEnabled = _tools.Supports(Format) && !_exporting; _mute.IsEnabled = !gif && _tools.CanEdit && !_exporting;
        _preview.IsMuted = gif || _mute.IsChecked == true; _preview.SpeedRatio = Speed;
        _notice.Text = Format switch { RecordingFormat.Gif => "GIF 无声音。低 / 中质量限制宽度为 480 / 720 像素，高质量保留画面尺寸。", RecordingFormat.WebM => "WebM 使用 VP9 / Opus，导出时重新编码。", _ => "MP4 使用 H.264 / AAC；未修改的原片直接保存，其余操作重新编码。" };
        bool available = _tools.Supports(Format) || Format == RecordingFormat.Mp4;
        _quality.IsEnabled = !_exporting && _tools.Supports(Format);
        if (!_tools.Supports(Format) && Format != RecordingFormat.Mp4) _notice.Text += "\n当前缺少对应编码器，请选择 MP4 原片或补齐媒体工具。";
        _cropButton.IsEnabled = _ready && !_exporting && !_cropEditing && !_configBlocked && _tools.CanEdit && _info.Width >= 2 && _info.Height >= 2;
        _cropButton.Tag = _cropEditing || _crop != null ? "selected" : null;
        _cropButton.ToolTip = _tools.CanEdit ? "裁剪视频画面；保留原片，可重新调整" : "画面裁剪需要 FFmpeg / ffprobe 媒体工具";
        _cropPanel.IsEnabled = !_exporting;
        _cutExpander.IsEnabled = _ready && !_exporting && _tools.CanEdit && !_configBlocked;
        CompactPanels();
        _export.IsEnabled = _ready && _info.Duration > 0 && !_exporting && !_cropEditing && available && !_configBlocked && VideoCuts.Kept(Timeline.Start, Timeline.End, _deleted).Sum(r => r.End-r.Start) >= 1/_info.Fps - .000001;
    }
    internal void BeginCrop()
    {
        if (!_ready || _exporting || _configBlocked || !_tools.CanEdit || _info.Width < 2 || _info.Height < 2) return;
        _cropEditing = true; _cropRatio.SelectedIndex = 0; CropOverlay.Initialize(_info.Width, _info.Height, _crop);
        _cutExpander.IsExpanded = false; _cutExpander.Visibility = Visibility.Collapsed;
        CropOverlay.Visibility = _cropPanel.Visibility = Visibility.Visible; LayoutPreview(); UpdateCropLabel(); UpdateOptions(); CropOverlay.Focus();
    }
    internal void ResetCrop()
    { if (_cropEditing) { _cropRatio.SelectedIndex = 0; CropOverlay.Initialize(_info.Width, _info.Height, null); } }
    internal void ConfirmCrop()
    {
        if (!_cropEditing) return;
        // Returning to the full frame keeps the original copy path, including odd-sized imported videos.
        Rect full = new(0, 0, _info.Width, _info.Height);
        _crop = CropOverlay.Selection == full ? null : VideoCrop.FromSelection(CropOverlay.Selection, _info.Width, _info.Height);
        if (RecordingRecovery.Owns(_source))
        {
            try { RecordingRecovery.Remember(_source, RecordingRecovery.Load(_source) with { Crop = _crop }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write(ex); _status.Text = "本次裁剪已应用，但配置保存失败：" + ex.Message + "。关闭后需要重新调整，原片仍保留。"; }
        }
        FinishCrop(); EditChanged();
    }
    internal void CancelCrop() { if (_cropEditing) FinishCrop(); }
    private void FinishCrop()
    {
        _cropEditing = false; CropOverlay.ReleaseMouseCapture(); CropOverlay.Visibility = _cropPanel.Visibility = Visibility.Collapsed;
        _cutExpander.Visibility = Visibility.Visible;
        _cropButton.Content = _crop == null ? "画面裁剪" : "调整裁剪"; LayoutPreview(); UpdateCropLabel(); UpdateOptions();
    }
    private void UpdateCropLabel()
    {
        if (_cropLabel == null) return;
        VideoCrop? crop = _cropEditing ? VideoCrop.FromSelection(CropOverlay.Selection, _info.Width, _info.Height) : _crop;
        _cropLabel.Text = crop == null ? "保留完整画面" : $"{(_cropEditing ? "待确认" : "裁剪预览")}：{crop.Width} × {crop.Height} 像素  ·  左上角 ({crop.X}, {crop.Y})  ·  对齐到偶数像素用于编码";
    }
    private void LayoutPreview()
    {
        if (_info.Width <= 0 || _info.Height <= 0) return;
        Rect pixels = !_cropEditing && _crop != null ? _crop.Bounds : new(0, 0, _info.Width, _info.Height);
        Rect view = VideoCropOverlay.Fit(pixels.Width, pixels.Height, _videoViewport.ActualWidth, _videoViewport.ActualHeight);
        if (view.IsEmpty) return;
        double scale = view.Width / pixels.Width;
        _preview.Width = _info.Width * scale; _preview.Height = _info.Height * scale;
        Canvas.SetLeft(_preview, view.X - pixels.X * scale); Canvas.SetTop(_preview, view.Y - pixels.Y * scale);
        _videoCanvas.Clip = new RectangleGeometry(view);
    }
    private void PreviewRange(double seconds)
    {
        if (!_ready) return;
        Pause();
        // The end boundary can equal the duration; seek to the last frame instead of beyond the video.
        double lastFrame = Math.Max(0, _info.Duration - 1 / (_info.Fps > 0 ? _info.Fps : 30));
        Seek(Math.Min(seconds, lastFrame));
    }
    private void Seek(double seconds) { _pendingSeek = seconds; if (_opened) _preview.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _info.Duration)); Timeline.SetPosition(seconds); }
    private void Pause() { _preview.Pause(); _playing = false; _play.Content = "播放选段"; _play.Tag = null; }
    private void TogglePlayback()
    { if (!_opened) return; if (_playing) Pause(); else { if (_preview.Position.TotalSeconds < Timeline.Start || _preview.Position.TotalSeconds >= Timeline.End) Seek(Timeline.Start); Seek(VideoCuts.Next(_preview.Position.TotalSeconds, Timeline.Start, Timeline.End, _deleted)); _preview.SpeedRatio = Speed; _preview.Play(); _playing = true; _play.Content = "暂停预览"; _play.Tag = "selected"; } }
    internal async Task ExportAsync()
    {
        if (!_ready || _exporting || _cropEditing || _configBlocked) return;
        try
        {
            string name = _filename.Text.Trim(); if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("请输入有效的文件名。");
            string target = Path.Combine(Path.GetFullPath(_folder.Text.Trim()), Path.ChangeExtension(name, VideoExportService.Extension(Format)));
            if (Path.GetFullPath(_source).Equals(target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请使用其他文件名另存副本，不能覆盖正在编辑的原录屏。");
            bool overwrite = File.Exists(target); if (overwrite && MessageBox.Show(this, "文件已存在，是否替换？", "保存录屏", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var request = new VideoExportRequest(_source, target, Format, (ExportQuality)_quality.SelectedIndex, Timeline.Start, Timeline.End, Speed, _mute.IsChecked == true, _formatFps[RecordingFormat.Gif], _metadata.Quality, overwrite, _crop, FramesPerSecond: _tools.Supports(Format) ? _formatFps[Format] : null, Deleted: _deleted);
            VideoExportService.Validate(request, _info, _tools); await ConfigurationSaved; if (_saveFailed) throw new IOException("剪辑配置未能保存；请先重试保存配置，原片仍保留。"); Pause(); _exporting = true; _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _options.IsEnabled = _playback.IsEnabled = false; Timeline.IsEnabled = _speed.IsEnabled = _folder.IsEnabled = _filename.IsEnabled = _play.IsEnabled = false;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            _cancel.Content = "取消导出"; _status.Text = "正在导出…"; _progress.Value = 0; UpdateOptions();
            await VideoExportService.ExportAsync(request, _info, _tools, new Progress<double>(value => _progress.Value = value), _exportCancellation.Token);
            SavedPath = target; _exporting = false; _preview.Close(); DialogResult = true;
        }
        catch (OperationCanceledException) { _status.Text = "已取消导出，原始录屏保留；可调整后重试或稍后编辑。"; }
        catch (Exception ex) { ErrorLog.Write(ex); _status.Text = "导出失败：" + ex.Message + " 原始录屏保留，可调整后重试。"; _status.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); }
        finally
        {
            _exporting = false; _exportCancellation?.Dispose(); _exportCancellation = null;
            _options.IsEnabled = _playback.IsEnabled = _folder.IsEnabled = _filename.IsEnabled = _play.IsEnabled = true; Timeline.IsEnabled = _speed.IsEnabled = _tools.CanEdit; _cancel.Content = "稍后编辑"; UpdateOptions();
        }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exporting) { e.Cancel = true; _exportCancellation?.Cancel(); _status.Text = "正在取消导出…"; return; }
        if (!_closeApproved && !ConfigurationSaved.IsCompleted)
        { e.Cancel=true; IsEnabled=false; _status.Text="正在保存剪辑配置…"; await ConfigurationSaved; _closeApproved=true; Close(); }
    }
}
