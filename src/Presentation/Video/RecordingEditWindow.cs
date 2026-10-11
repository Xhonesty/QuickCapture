using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly Grid _previewSlot = new() { Name = "RecordingPreviewSlot", Margin = new Thickness(0, 0, 0, 8) };
    private readonly Image _effectImage = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _effectCover = new() { Background = Brushes.Black, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    internal VideoObjectEditor ObjectEditor { get; }
    private VideoObjectPreview? _objectPreview;
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
    private readonly TextBlock _currentTime = new(), _totalTime = new();
    private readonly Button _speedButton, _muteButton, _resetSelection;
    private readonly Button _snapButton;
    private readonly WrapPanel _speedProperties = new();
    private readonly ComboBox _speedList = new() { Name = "SpeedIntervals", Width = 180, ToolTip = "选择已确认变速区间或新增区间" };
    private readonly TextBlock _speedRangeHint = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
    private readonly Button _confirmSpeed, _normalSpeed;
    private readonly TextBlock _speedTitle = new();
    private readonly Grid _contentGrid;
    private VideoSpeedRange[] _speedRanges = Array.Empty<VideoSpeedRange>();
    private int _replaceSpeed = -1;
    private bool _refreshingSpeed;
    private readonly StackPanel _deleteProperties = new();
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
    private int _selectedCut = -1;
    private readonly Button _deleteButton, _undoButton, _redoButton;
    private readonly WrapPanel _draftActions = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 6) };
    private readonly TextBlock _draftLabel = UiDesign.Text("", true);
    private int _replaceCut = -1;
    private double _draftPreviousPosition;
    private readonly DispatcherTimer _seekTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private double? _latestPreview;
    private int _seekGeneration;
    private readonly Grid _exportDetails;
    private readonly Stack<RecordingEditState> _undo = new(), _redo = new();
    private RecordingEditState? _lastEdit;
    private bool _restoring, _dragging, _configBlocked, _closeApproved, _saveFailed;
    internal Task ConfigurationSaved { get; private set; } = Task.CompletedTask;
    internal IReadOnlyList<VideoRange> Deleted => _deleted;
    internal bool PreserveMaster => _crop != null || _deleted.Length > 0 || _speedRanges.Length > 0 || _mute.IsChecked == true || ObjectEditor.Objects.Length > 0;
    internal double PreviewPosition => _pendingSeek ?? _preview.Position.TotalSeconds;
    internal string? SavedPath { get; private set; }
    internal Task Initialization { get; private set; } = Task.CompletedTask;
    internal RecordingEditWindow(Window owner, string source, Settings settings, RecordingMetadata metadata)
    {
        Ui.Theme(this); Owner = owner; _source = source; _settings = settings; _metadata = metadata;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/QuickCapture;component/RecordingEditorStyles.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/QuickCapture;component/VideoEditorStyles.xaml", UriKind.Relative) });
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        _info = new(metadata.Duration, 0, 0, metadata.Fps, metadata.HasAudio);
        Title = "录屏编辑与导出"; Width = 880; Height = Math.Min(820, SystemParameters.WorkArea.Height - 32); MinWidth = 680; MinHeight = Math.Min(600, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ObjectEditor = new(this, source, () => _pendingSeek ?? _preview.Position.TotalSeconds, () => Timeline.End, Pause, _lifetime.Token);
        var shell = new DockPanel();
        RecordingWindowChrome.Install(this, shell);
        var footer = new StackPanel { Margin = new Thickness(24, 4, 24, 12) }; DockPanel.SetDock(footer, Dock.Bottom); shell.Children.Add(footer);
        footer.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
        var root = _contentGrid = new Grid { Margin = new Thickness(24, 12, 24, 0), Name = "RecordingContent" };
        var body = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Name = "RecordingScroll" }; shell.Children.Add(body);
        _previewSlot.Height = 260;
        SizeChanged += (_, _) => UpdatePreviewHeight();
        void AddRow(UIElement element, bool stretch = false)
        {
            int index = root.RowDefinitions.Count;
            root.RowDefinitions.Add(new() { Height = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetRow(element, index); root.Children.Add(element);
        }
        _videoCanvas.Children.Add(_preview); _videoCanvas.Children.Add(_effectCover); _videoCanvas.Children.Add(_effectImage); _videoViewport.Children.Add(_videoCanvas); _videoViewport.Children.Add(ObjectEditor.Overlay); _videoViewport.Children.Add(ObjectEditor.TextEditor); _videoViewport.Children.Add(CropOverlay);
        _videoViewport.HorizontalAlignment = HorizontalAlignment.Center; _videoViewport.VerticalAlignment = VerticalAlignment.Center;
        _previewSlot.Children.Add(_videoViewport); _previewSlot.SizeChanged += (_, _) => LayoutPreview(); AddRow(_previewSlot, true);
        var playback = _playback = new WrapPanel { Margin = new Thickness(0, 2, 0, 6), HorizontalAlignment = HorizontalAlignment.Center }; AddRow(playback);
        _currentTime.Margin = new Thickness(0, 0, 16, 0); _totalTime.Margin = new Thickness(16, 0, 0, 0);
        foreach (var text in new[] { _currentTime, _totalTime }) { text.FontSize = 11; text.VerticalAlignment = VerticalAlignment.Center; text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); }
        playback.Children.Add(_currentTime);
        playback.Children.Add(VideoObjectEditor.Button("step-back", "上一帧 (←)", () => NavigatePreview(-1 / _info.Fps, true)));
        playback.Children.Add(VideoObjectEditor.Button("rewind", "后退 5 秒 (Shift+←)", () => NavigatePreview(-5)));
        _play = VideoObjectEditor.Button("play", "播放 / 暂停 (Space)", TogglePlayback); _play.Name = "PlaySelection"; _play.Style = (Style)FindResource("VideoPlayButton"); playback.Children.Add(_play);
        playback.Children.Add(VideoObjectEditor.Button("forward", "前进 5 秒 (Shift+→)", () => NavigatePreview(5)));
        playback.Children.Add(VideoObjectEditor.Button("step-forward", "下一帧 (→)", () => NavigatePreview(1 / _info.Fps, true))); playback.Children.Add(_totalTime);
        AddRow(ObjectEditor);
        var legend = new WrapPanel { Margin = new Thickness(8, 2, 8, 4) };
        foreach (var (label, resource) in new[] { ("保留区间", "Accent"), ("已确认变速", "TimelineSpeedBackground"), ("待确认 · 虚线", "TimelinePending"), ("删除区间", "Danger") })
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            var chip = new System.Windows.Shapes.Rectangle { Width = 14, Height = 8, RadiusX = 2, RadiusY = 2, StrokeThickness = 1, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            chip.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, resource);
            chip.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, resource == "Danger" ? "Danger" : "Accent");
            if (resource == "TimelinePending") chip.StrokeDashArray = new DoubleCollection { 2, 1 };
            item.Children.Add(chip); var text = UiDesign.Text(label, true); text.Margin = new Thickness(0); item.Children.Add(text); legend.Children.Add(item);
        }
        AddRow(legend); AddRow(Timeline); AddRow(ObjectEditor.Tracks);
        Timeline.ToolTip = "青绿为保留范围与变速，虚线为待确认，红色为删除；边界按原片帧率对齐。Alt 临时关闭吸附。"; _rangeLabel = UiDesign.Text("正在读取录屏…", true); AddRow(_rangeLabel);
        _deleteProperties.Children.Add(_draftActions); _draftLabel.Margin = new Thickness(0, 0, 12, 0); _draftLabel.VerticalAlignment = VerticalAlignment.Center; _draftLabel.TextWrapping = TextWrapping.Wrap; _draftActions.Children.Add(_draftLabel);
        var confirmDelete = Ui.Button("确认删除", ConfirmDelete); confirmDelete.Name = "ConfirmDelete"; confirmDelete.Style = (Style)FindResource("RecordingPrimaryButton"); _draftActions.Children.Add(confirmDelete);
        var cancelDelete = Ui.Button("取消", CancelDelete); cancelDelete.Name = "CancelDelete"; _draftActions.Children.Add(cancelDelete);
        var tools = ObjectEditor.Toolbar.MainTools; tools.Children.Add(ScreenshotToolbar.Separator());
        _speed = new ComboBox { ItemsSource = new[] { "0.5×", "1×", "1.5×", "2×" }, SelectedIndex = 2, Name = "ExportSpeed", Width = 72, MinWidth = 0, MinHeight = 30, ToolTip = "当前变速区间倍率" };
        var models = new Grid { Visibility = Visibility.Collapsed }; models.Children.Add(_mute); AddRow(models);
        _cropButton = VideoObjectEditor.Button("crop", "画面裁剪 (C)", BeginCrop); _cropButton.Name = "EditVideoCrop"; _cropButton.IsEnabled = false; tools.Children.Add(_cropButton);
        _resetSelection = VideoObjectEditor.Button("reset-range", "重置首尾选段 · 保留编辑对象", () => { if (!ObjectEditor.CommitText()) return; Timeline.SetRange(0, _info.Duration); Seek(0); }); tools.Children.Add(_resetSelection);
        _speedButton = VideoObjectEditor.Button("speed", "区间变速 · 拖动起终点", () => BeginSpeed()); tools.Children.Add(_speedButton);
        _muteButton = VideoObjectEditor.Button("volume", "当前声音开启，点击静音。", () => { if (ObjectEditor.CommitText()) _mute.IsChecked = _mute.IsChecked != true; }); tools.Children.Add(_muteButton);
        _deleteButton = VideoObjectEditor.Button("delete-segment", "删除中间片段", BeginDelete, true); _deleteButton.Name = "DeleteMiddleSegment"; tools.Children.Add(_deleteButton);
        _deleteButton.PreviewMouseRightButtonDown += (_, e) => { ObjectEditor.ShowExternalProperties(_deleteProperties); e.Handled = true; };
        _snapButton = VideoObjectEditor.Button("magnet", "自动吸附已开启，点击关闭；按住 Alt 临时关闭。", () => { Timeline.SnapEnabled = !Timeline.SnapEnabled; UpdateOptions(); EditChanged(); }); tools.Children.Add(_snapButton);
        tools.Children.Add(ScreenshotToolbar.Separator());
        ObjectEditor.AddLayerButtons(tools);
        tools.Children.Add(ScreenshotToolbar.Separator());
        ObjectEditor.Changed += commit => { RequestObjectPreview(true); if (commit) EditChanged(); UpdateOptions(); };
        ObjectEditor.SeekRequested += seconds => PreviewRange(seconds); ObjectEditor.AvailabilityChanged += UpdateOptions;
        _undoButton = VideoObjectEditor.Button("undo-2", "撤销 (Ctrl+Z)", UndoEdit); _undoButton.Name = "UndoRecordingEdit"; tools.Children.Add(_undoButton);
        _redoButton = VideoObjectEditor.Button("redo-2", "重做 (Ctrl+Y)", RedoEdit); _redoButton.Name = "RedoRecordingEdit"; tools.Children.Add(_redoButton);
        _speedTitle.Text = "新增变速区间"; _speedTitle.FontSize = 12; _speedTitle.VerticalAlignment = VerticalAlignment.Center; _speedTitle.Margin = new Thickness(0, 0, 8, 0);
        _speedTitle.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); _speedProperties.Children.Add(_speedTitle);
        _speedProperties.Children.Add(_speedList);
        _speedProperties.Children.Add(_speedRangeHint); _speedProperties.Children.Add(ThicknessOptions.Group("倍率", _speed));
        _confirmSpeed = Ui.Button("确认变速", () => ConfirmSpeed()); _confirmSpeed.Name = "ConfirmSpeed"; _confirmSpeed.Style = (Style)FindResource("RecordingPrimaryButton"); _speedProperties.Children.Add(_confirmSpeed);
        _speedProperties.Children.Add(Ui.Button("取消", CancelSpeed)); _normalSpeed = Ui.Button("恢复正常速度", () => ConfirmSpeed(1)); _normalSpeed.Name = "RestoreNormalSpeed"; ToolTipService.SetShowOnDisabled(_normalSpeed, true); _speedProperties.Children.Add(_normalSpeed);
        _speedList.SelectionChanged += (_, _) => { if (!_refreshingSpeed && _speedList.SelectedIndex >= 0) { int index = _speedList.SelectedIndex - 1; CancelSpeed(false); BeginSpeed(index); } };
        Timeline.SpeedSelected += index => BeginSpeed(index); Timeline.DeleteSelected += index => { _selectedCut = index; BeginDelete(true); };
        Timeline.ObjectBoundaries = () => ObjectEditor.Objects.SelectMany(o => new[] { o.Start, o.End });
        ObjectEditor.FrameProvider = ReadSourceFrame;
        var cropActions = new WrapPanel(); _cropPanel.Children.Add(cropActions);
        _cropRatio = new ComboBox { ItemsSource = new[] { "自由比例", "原始比例", "16:9", "9:16", "1:1" }, SelectedIndex = 0, Width = 120, Name = "VideoCropRatio", ToolTip = "裁剪框比例" }; cropActions.Children.Add(_cropRatio);
        var resetCrop = Ui.Button("重置裁剪", ResetCrop); resetCrop.Name = "ResetVideoCrop"; cropActions.Children.Add(resetCrop);
        var confirmCrop = Ui.Button("确认裁剪", ConfirmCrop); confirmCrop.Name = "ConfirmVideoCrop"; cropActions.Children.Add(confirmCrop);
        confirmCrop.Style = (Style)FindResource("RecordingPrimaryButton");
        var cancelCrop = Ui.Button("取消裁剪", CancelCrop); cancelCrop.Name = "CancelVideoCrop"; cropActions.Children.Add(cancelCrop);
        _cropLabel = UiDesign.Text("保留完整画面", true); _cropPanel.Children.Add(_cropLabel);
        _cropRatio.SelectionChanged += (_, _) => { if (_cropEditing) CropOverlay.SetAspectRatio(_cropRatio.SelectedIndex switch { 1 => _info.Width / (double)_info.Height, 2 => 16d / 9, 3 => 9d / 16, 4 => 1, _ => 0 }); };
        CropOverlay.SelectionChanged += UpdateCropLabel;
        PreviewKeyDown += EditorKeyDown;
        _toolsNotice = UiDesign.Text("正在检测媒体工具…", true); AddRow(_toolsNotice);
        var details = _exportDetails = new Grid(); details.ColumnDefinitions.Add(new()); details.ColumnDefinitions.Add(new() { Width = new GridLength(1.35, GridUnitType.Star) });
        details.RowDefinitions.Add(new() { Height = GridLength.Auto }); details.RowDefinitions.Add(new() { Height = GridLength.Auto }); AddRow(details);
        SizeChanged += (_, _) => CompactPanels();
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
        _status = UiDesign.Text(savedRecording ? "正在编辑已保存的录屏；关闭或取消不会修改原文件。" : "取消或关闭后保留原始录屏，可在最近文件中继续编辑。", true);
        _status.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("Text") { Source = _status }); footer.Children.Add(_status); footer.Children.Add(_progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(buttons);
        _cancel = Ui.Button("稍后编辑", () => { if (_exporting) _exportCancellation?.Cancel(); else Close(); }); _cancel.Name = "CancelExport"; buttons.Children.Add(_cancel);
        _export = Ui.Button("导出并保存", async () => await ExportAsync()); _export.Name = "ExportRecording"; _export.IsEnabled = false; buttons.Children.Add(_export);
        _export.Style = (Style)FindResource("RecordingPrimaryButton");
        Timeline.RangeChanged += () => { UpdateRange(); UpdateOptions(); EditChanged(); };
        Timeline.EditStarted += () => _dragging = true;
        Timeline.EditCompleted += () => { _dragging = false; FlushPreview(); EditChanged(); };
        Timeline.DraftChanged += () =>
        {
            if (Timeline.Draft is { } draft)
            {
                if (Timeline.DraftIsSpeed) _speedRangeHint.Text = $"{draft.Start:F2}–{draft.End:F2} 秒 · 在时间轴调整";
                else _draftLabel.Text = $"删除起点 {draft.Start:F2} 秒  ·  终点 {draft.End:F2} 秒  ·  拟删除 {draft.End - draft.Start:F2} 秒";
            }
            UpdateRange();
        };
        Timeline.RangePreviewRequested += QueuePreview;
        Timeline.SeekRequested += seconds => { PreviewRange(seconds); };
        _seekTimer.Tick += (_, _) => { if (_latestPreview is double seconds) { _latestPreview = null; PreviewRange(seconds); } _seekTimer.Stop(); };
        _format.SelectionChanged += (_, _) => { _filename.Text = Path.ChangeExtension(_filename.Text, VideoExportService.Extension(Format)); UpdateFpsOptions(); UpdateOptions(); EditChanged(); };
        _exportFps.SelectionChanged += (_, _) => { if (!_changingFps && _exportFps.SelectedIndex >= 0) { _formatFps[Format] = RecordingFrameRates.For(Format)[_exportFps.SelectedIndex]; EditChanged(); } };
        _quality.SelectionChanged += (_, _) => { UpdateOptions(); EditChanged(); }; _speed.SelectionChanged += (_, _) => { Timeline.SetDraftRate(DraftSpeed); UpdateRange(); UpdateOptions(); }; _mute.Checked += (_, _) => { UpdateOptions(); EditChanged(); }; _mute.Unchecked += (_, _) => { UpdateOptions(); EditChanged(); };
        _preview.MediaOpened += (_, _) => { _opened = true; if (_info.Width <= 0 || _info.Height <= 0) { _info = _info with { Width = _preview.NaturalVideoWidth, Height = _preview.NaturalVideoHeight }; ResizeForVideo(); } if (_info.Duration <= 0 && _preview.NaturalDuration.HasTimeSpan) { _info = _info with { Duration = _preview.NaturalDuration.TimeSpan.TotalSeconds }; Timeline.Initialize(_info.Duration, _info.Fps); } _preview.Pause(); Seek(_pendingSeek ?? Timeline.Start); LayoutPreview(); UpdateOptions(); };
        _preview.MediaFailed += (_, e) => { _status.Text = "预览不可用：" + e.ErrorException.Message + "。仍可保存原片或使用媒体工具导出。"; _play.IsEnabled = false; };
        _preview.MediaEnded += (_, _) => Pause();
        _timer.Tick += (_, _) => { if (!_playing) return; double now = _preview.Position.TotalSeconds; _preview.SpeedRatio = VideoTiming.RateAt(now, _speedRanges); RequestObjectPreview(); UpdateTransport(); Timeline.SetPosition(now); double next = VideoCuts.Next(now, Timeline.Start, Timeline.End, _deleted); if (next < Timeline.End && next > now + .001) { Seek(next); _preview.SpeedRatio = VideoTiming.RateAt(next, _speedRanges); _pendingSeek = null; } if (_preview.Position.TotalSeconds >= Timeline.End) { Pause(); Seek(Timeline.Start); } };
        Loaded += (_, _) => Initialization = InitializeAsync(); Closing += OnClosing;
        Closed += async (_, _) => { _seekGeneration++; _seekTimer.Stop(); await ConfigurationSaved; _lifetime.Cancel(); _objectPreview?.Dispose(); _timer.Stop(); _preview.Close(); await CleanupPreviewAsync(); _lifetime.Dispose(); };
    }
    private RecordingFormat Format => (RecordingFormat)Math.Max(0, _format.SelectedIndex);
    private double Speed => VideoTiming.RateAt(PreviewPosition, _speedRanges);
    private double DraftSpeed => new[] { .5, 1d, 1.5, 2d }[Math.Max(0, _speed.SelectedIndex)];
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
                    try { stored.Validate(_info); _crop = stored; }
                    catch (ArgumentException ex) { ErrorLog.Write(ex); _status.Text = "已保存的裁剪范围不适用于当前原片，已恢复完整画面，可重新调整。"; }
                }
            }
            Timeline.Initialize(_info.Duration, _info.Fps);
            _timer.Interval = TimeSpan.FromSeconds(1 / Math.Clamp(_info.Fps, 10, 60));
            ObjectEditor.Initialize(_info, _tools);
            if (_tools.CanEffects) { _objectPreview = new(_source, _tools.Ffmpeg!, _info); _objectPreview.FrameReady += frame => Dispatcher.BeginInvoke(() => { if (!_lifetime.IsCancellationRequested && (ObjectEditor.Objects.Length > 0 || ObjectEditor.TextEditor.Active)) { _effectImage.Source = frame; if (_status.Text.StartsWith("效果预览暂不可用：", StringComparison.Ordinal)) _status.Text = ""; } }); _objectPreview.Failed += error => Dispatcher.BeginInvoke(() => { if (!_lifetime.IsCancellationRequested) _status.Text = "效果预览暂不可用：" + error; }); }
            try { var storedEdit = await RecordingEditStore.LoadAsync(_source); if (storedEdit != null) ApplyEdit(storedEdit); }
            catch (Exception ex) { _configBlocked = true; _status.Text = ex.Message + " 请在最近文件中清理剪辑配置后重新打开。"; }
            string previewSource = await PreparePreviewAsync(token); token.ThrowIfCancellationRequested();
            _preview.Source = new Uri(Path.GetFullPath(previewSource)); _preview.Play(); _timer.Start();
            _toolsNotice.Text = _tools.CanEdit ? "可截取时间、裁剪画面、变速与导出 MP4 / WebM / GIF" : "媒体工具缺失或编码器不完整：可保存完整 MP4 原片。补齐 FFmpeg / ffprobe 后可截取时间、裁剪画面、变速及导出其他格式。";
            _toolsNotice.Visibility = _tools.CanEdit ? Visibility.Collapsed : Visibility.Visible;
            Timeline.IsEnabled = _tools.CanEdit && !_configBlocked; _speed.IsEnabled = _mute.IsEnabled = _tools.CanEdit && !_configBlocked;
            // A missing tool never blocks saving the native MP4 master.
            if (!_tools.Supports(Format)) { _format.SelectedIndex = 0; _quality.SelectedIndex = (int)_metadata.Quality; }
            _ready = true; _lastEdit = CurrentEdit(); LayoutPreview(); UpdateRange(); UpdateCropLabel(); UpdateOptions(); ResizeForVideo();
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
    {
        if (_rangeLabel == null) return;
        var speeds = _speedRanges;
        if (Timeline.DraftIsSpeed && Timeline.Draft is { } draft) speeds = VideoTiming.Apply(_speedRanges.Where((_, i) => i != _replaceSpeed), new(draft.Start, draft.End, DraftSpeed));
        double duration = VideoTiming.Segments(Timeline.Start, Timeline.End, _deleted, speeds).Sum(r => r.Duration);
        _rangeLabel.Text = $"原片 {_info.Duration:F2} 秒  ·  保留 {VideoCuts.Kept(Timeline.Start, Timeline.End, _deleted).Sum(r => r.End-r.Start):F2} 秒  ·  {(Timeline.DraftIsSpeed ? "预计导出" : "导出")} {duration:F2} 秒  ·  {Timeline.Start:F3}–{Timeline.End:F3}";
    }
    private RecordingEditState CurrentEdit() => new(Timeline.Start, Timeline.End, _deleted.ToArray(), 1, _mute.IsChecked == true, _crop, Format, (ExportQuality)Math.Max(0, _quality.SelectedIndex), _formatFps[Format], ObjectEditor.Objects.ToArray(), _speedRanges.ToArray(), Timeline.SnapEnabled, ObjectEditor.ToolDefaults);
    private void EditChanged()
    {
        if (!_ready || _restoring || _dragging) return;
        var state = CurrentEdit();
        if (_lastEdit != null && JsonSerializer.Serialize(_lastEdit) == JsonSerializer.Serialize(state)) return;
        if (_lastEdit != null) _undo.Push(_lastEdit); _redo.Clear(); _lastEdit = state; SaveEdit(state); UpdateOptions();
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
        var restoredObjects = VideoObjectRules.Validate(state.Objects, _info);
        var restoredSpeeds = VideoTiming.Normalize(state.SpeedRanges ?? (state.Speed == 1 ? Array.Empty<VideoSpeedRange>() : new[] { new VideoSpeedRange(0, _info.Duration, state.Speed) }), _info.Duration, _info.Fps);
        if (VideoCuts.Kept(state.Start, state.End, VideoCuts.Normalize(state.Deleted, _info.Duration, _info.Fps)).Sum(r => r.End - r.Start) < 1 / _info.Fps - .000001) throw new InvalidDataException("剪辑配置未保留有效的视频帧。");
        _restoring = true;
        try
        {
            Timeline.EndDraft(); _draftActions.Visibility = Visibility.Collapsed;
            _deleted = VideoCuts.Normalize(state.Deleted, _info.Duration, _info.Fps); Timeline.SetDeleted(_deleted); Timeline.SetRange(state.Start, state.End); RefreshCuts();
            ObjectEditor.Restore(restoredObjects);
            ObjectEditor.RestoreDefaults(state.ToolDefaults); _speedRanges = restoredSpeeds; Timeline.SetSpeeds(_speedRanges); Timeline.SnapEnabled = state.SnapEnabled; RefreshSpeeds();
            _crop = state.Crop; _crop?.Validate(_info); _mute.IsChecked = state.Mute;
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
        Pause(); _deleted = cuts; Timeline.SetDeleted(cuts); RefreshCuts(); UpdateRange(); UpdateOptions(); EditChanged();
    }
    private void RefreshCuts()
    { _selectedCut = _deleted.Length > 0 ? Math.Clamp(_selectedCut, 0, _deleted.Length - 1) : -1; }
    internal void BeginDelete() => BeginDelete(false);
    private void BeginDelete(bool replace)
    {
        if (!_ready || _exporting || _cropEditing || Timeline.Draft != null || _configBlocked || !_tools.CanEdit || !ObjectEditor.CommitText()) return;
        _replaceCut = replace ? _selectedCut : -1;
        if (replace && _replaceCut < 0) return;
        var baseCuts = _deleted.Where((_, i) => i != _replaceCut).ToArray();
        var kept = VideoCuts.Kept(Timeline.Start, Timeline.End, baseCuts);
        double middle = (Timeline.Start + Timeline.End) / 2;
        var bounds = replace ? kept.FirstOrDefault(r => r.Start <= _deleted[_replaceCut].Start && r.End >= _deleted[_replaceCut].End)
            : kept.Where(r => r.End - r.Start >= 1 / _info.Fps - .000001).OrderBy(r => middle < r.Start ? r.Start - middle : middle > r.End ? middle - r.End : 0).ThenByDescending(r => r.End - r.Start).FirstOrDefault();
        if (bounds == null) { _status.Text = "当前选段没有可调整的删除范围。"; return; }
        var range = replace ? _deleted[_replaceCut] : new VideoRange(bounds.Start + (bounds.End - bounds.Start) / 3, bounds.Start + (bounds.End - bounds.Start) * 2 / 3);
        if (!Timeline.BeginDraft(bounds, range, kept.Sum(r => r.End - r.Start))) { _status.Text = "至少保留一帧；当前选段太短，无法再删除。"; return; }
        _draftPreviousPosition = _preview.Position.TotalSeconds; Pause(); Timeline.SetDeleted(baseCuts);
        _draftActions.Visibility = Visibility.Visible; ObjectEditor.ShowExternalProperties(_deleteProperties); UpdateOptions(); PreviewRange(Timeline.Draft!.Start);
    }
    internal void ConfirmDelete()
    {
        if (Timeline.Draft is not { } draft || Timeline.DraftIsSpeed || _exporting) return;
        var ranges = _deleted.Where((_, i) => i != _replaceCut).Append(draft).ToArray();
        Timeline.EndDraft(); _draftActions.Visibility = Visibility.Collapsed;
        try { SetCuts(ranges); PreviewRange(VideoCuts.Next(draft.Start, Timeline.Start, Timeline.End, _deleted)); }
        catch (ArgumentException ex) { Timeline.SetDeleted(_deleted); _status.Text = ex.Message; }
        ObjectEditor.ShowExternalProperties(null); UpdateOptions();
    }
    internal void CancelDelete()
    {
        if (Timeline.Draft == null || Timeline.DraftIsSpeed) return;
        Timeline.EndDraft(); Timeline.SetDeleted(_deleted); _draftActions.Visibility = Visibility.Collapsed;
        PreviewRange(_draftPreviousPosition); UpdateOptions();
        ObjectEditor.ShowExternalProperties(null);
    }
    internal void UndoEdit() { if (!_exporting && !_cropEditing && Timeline.Draft == null && ObjectEditor.CommitText() && _undo.TryPop(out var state)) { Pause(); _redo.Push(CurrentEdit()); ObjectEditor.ShowExternalProperties(null); ApplyEdit(state); _lastEdit = state; SaveEdit(state); UpdateOptions(); ResizeForVideo(); } }
    internal void RedoEdit() { if (!_exporting && !_cropEditing && Timeline.Draft == null && ObjectEditor.CommitText() && _redo.TryPop(out var state)) { Pause(); _undo.Push(CurrentEdit()); ObjectEditor.ShowExternalProperties(null); ApplyEdit(state); _lastEdit = state; SaveEdit(state); UpdateOptions(); ResizeForVideo(); } }
    private void CompactPanels()
    {
        if (_exportDetails == null || _cropLabel == null) return;
        _exportDetails.Visibility = Visibility.Visible;
        _cropLabel.Visibility = _cropEditing ? Visibility.Visible : Visibility.Collapsed;
        bool narrow = ActualWidth < 800;
        if (_exportDetails.Children.Count >= 2)
        {
            var first = (Border)_exportDetails.Children[0]; var second = (Border)_exportDetails.Children[1];
            Grid.SetColumnSpan(first, narrow ? 2 : 1); Grid.SetColumnSpan(second, narrow ? 2 : 1);
            Grid.SetColumn(second, narrow ? 0 : 1); Grid.SetRow(second, narrow ? 1 : 0);
            first.Margin = narrow ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 12, 0);
            foreach (var card in new[] { first, second })
            {
                card.Padding = new Thickness(12); card.CornerRadius = new CornerRadius(10);
                if (card.Child is StackPanel panel && panel.Children[0] is TextBlock title) title.FontSize = 14;
            }
        }
        UpdatePreviewHeight();
    }
    private void UpdatePreviewHeight()
    {
        if (_contentGrid == null || _info.Width <= 0 || _info.Height <= 0) return;
        double controls = _contentGrid.Children.OfType<FrameworkElement>().Where(e => e != _previewSlot).Sum(e => e.DesiredSize.Height);
        Rect view = !_cropEditing && _crop != null ? _crop.Bounds : new(0, 0, _info.Width, _info.Height);
        double fit = Math.Max(120, (Math.Max(120, ActualWidth - 64)) * view.Height / view.Width);
        _previewSlot.Height = Math.Min(fit, Math.Clamp(ActualHeight - controls - 130, 120, 500));
    }
    private void ResizeForVideo()
    {
        // Wait for controls to measure before reserving space for them. Resizing by
        // the user only fits the preview slot; it never triggers this auto sizing.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsVisible || WindowState != WindowState.Normal || _info.Width <= 0 || _info.Height <= 0) return;
            UpdateLayout();
            var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            Rect work = new(screen.X / dpi.DpiScaleX, screen.Y / dpi.DpiScaleY, screen.Width / dpi.DpiScaleX, screen.Height / dpi.DpiScaleY);
            MinWidth = Math.Min(680, work.Width - 24); MinHeight = Math.Min(600, work.Height - 24);
            Rect pixels = !_cropEditing && _crop != null ? _crop.Bounds : new(0, 0, _info.Width, _info.Height);
            double ratio = pixels.Width / pixels.Height, maxWidth = Math.Min(1120, work.Width - 24);
            double width = Math.Clamp(ratio >= 1 ? 1040 : 800, MinWidth, Math.Max(MinWidth, maxWidth));
            Width = width; UpdateLayout();
            double controls = Math.Max(340, _contentGrid.Children.OfType<FrameworkElement>().Where(e => e != _previewSlot).Sum(e => e.DesiredSize.Height) + 118);
            double videoHeight = Math.Max(120, Math.Min(500, work.Height - 24 - controls));
            double height = Math.Clamp(Math.Min(videoHeight, (width - 50) / ratio) + controls, MinHeight, Math.Max(MinHeight, work.Height - 24));
            double centerX = Left + ActualWidth / 2, centerY = Top + ActualHeight / 2;
            Width = width; Height = height;
            Left = Math.Clamp(centerX - width / 2, work.Left + 12, Math.Max(work.Left + 12, work.Right - width - 12));
            Top = Math.Clamp(centerY - height / 2, work.Top + 12, Math.Max(work.Top + 12, work.Bottom - height - 12));
            UpdateLayout(); LayoutPreview();
        }));
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
        bool drafting = Timeline.Draft != null, coloring = ObjectEditor.ColorEditing;
        ObjectEditor.IsEnabled = _ready && !_exporting && !_configBlocked && !ObjectEditor.Importing;
        ObjectEditor.SetObjectAvailability(!_cropEditing && !drafting && !coloring);
        ObjectEditor.Overlay.IsEnabled = ObjectEditor.IsEnabled && !_cropEditing && !drafting && !coloring && !ObjectEditor.TextEditor.Active;
        ObjectEditor.TextEditor.IsEnabled = ObjectEditor.IsEnabled && !coloring;
        ObjectEditor.Tracks.IsEnabled = ObjectEditor.Overlay.IsEnabled;
        ObjectEditor.Overlay.Visibility = _cropEditing ? Visibility.Collapsed : Visibility.Visible;
        bool editable = _ready && !_exporting && !_cropEditing && !drafting && !coloring && _tools.CanEdit && !_configBlocked;
        ObjectEditor.SetLayerAvailability(editable);
        _cropButton.IsEnabled = editable && _info.Width >= 2 && _info.Height >= 2;
        _cropButton.Tag = _cropEditing || _crop != null ? "selected" : null;
        _cropButton.ToolTip = _tools.CanEdit ? "裁剪视频画面；保留原片，可重新调整" : "画面裁剪需要 FFmpeg / ffprobe 媒体工具";
        _cropPanel.IsEnabled = !_exporting;
        _deleteButton.IsEnabled = editable && VideoCuts.Kept(Timeline.Start, Timeline.End, _deleted).Sum(r => r.End - r.Start) >= 2 / _info.Fps - .000001;
        _deleteButton.Tag = drafting && !Timeline.DraftIsSpeed ? "selected" : null;
        _undoButton.IsEnabled = editable && _undo.Count > 0; _redoButton.IsEnabled = editable && _redo.Count > 0;
        _playback.IsEnabled = !drafting && !coloring && !_cropEditing && !_exporting && _opened && !ObjectEditor.TextEditor.Composing;
        _play.IsEnabled = _playback.IsEnabled;
        _speed.IsEnabled = Timeline.DraftIsSpeed && !_exporting && _tools.CanEdit && !_configBlocked;
        _mute.IsEnabled = !drafting && !gif && _tools.CanEdit && !_exporting && !_configBlocked;
        _speedButton.IsEnabled = editable; _speedButton.Tag = Timeline.DraftIsSpeed ? "selected" : null;
        _speedButton.ToolTip = "区间变速 · 拖动起终点；虚线为待确认，声音保留音调";
        _confirmSpeed.IsEnabled = Timeline.DraftIsSpeed && !_exporting;
        _normalSpeed.IsEnabled = _confirmSpeed.IsEnabled && _replaceSpeed >= 0;
        _normalSpeed.ToolTip = _replaceSpeed >= 0 ? "恢复选中区间为 1×" : "选中已有变速区间后可恢复正常速度";
        _speedList.IsEnabled = !_exporting && !_cropEditing;
        _muteButton.IsEnabled = _mute.IsEnabled && !_cropEditing; _muteButton.Tag = _mute.IsChecked == true ? "selected" : null;
        _muteButton.IsEnabled &= !coloring; _muteButton.Content = IconSet.Create(_mute.IsChecked == true ? "volume-x" : "volume");
        _muteButton.ToolTip = gif ? "GIF 无声音；主视频静音设置已保留。" : _mute.IsChecked == true ? "当前已静音，点击恢复声音。" : "当前声音开启，点击静音。";
        System.Windows.Automation.AutomationProperties.SetName(_muteButton, (string)_muteButton.ToolTip);
        _snapButton.IsEnabled = _ready && !_exporting && !coloring && !_cropEditing && !_configBlocked;
        _snapButton.Tag = Timeline.SnapEnabled ? "selected" : null; _snapButton.ToolTip = Timeline.SnapEnabled ? "自动吸附已开启，点击关闭；按住 Alt 临时关闭。" : "自动吸附已关闭，点击开启；自由拖动仍按原片帧率对齐。";
        System.Windows.Automation.AutomationProperties.SetName(_snapButton, (string)_snapButton.ToolTip);
        _resetSelection.IsEnabled = editable;
        Timeline.IsEnabled = _ready && !_exporting && !coloring && !_configBlocked && _tools.CanEdit && !ObjectEditor.TextEditor.Active;
        _cropButton.IsEnabled = editable && _info.Width >= 2 && _info.Height >= 2;
        _options.IsEnabled = !drafting && !_exporting;
        CompactPanels();
        _export.IsEnabled = _ready && _info.Duration > 0 && !_exporting && !_cropEditing && !drafting && !coloring && !ObjectEditor.Importing && available && !_configBlocked && VideoCuts.Kept(Timeline.Start, Timeline.End, _deleted).Sum(r => r.End-r.Start) >= 1/_info.Fps - .000001;
    }
    internal void BeginCrop()
    {
        if (!_ready || _exporting || Timeline.Draft != null || _configBlocked || !_tools.CanEdit || _info.Width < 2 || _info.Height < 2 || !ObjectEditor.CommitText()) return;
        Pause(); ObjectEditor.Overlay.Cancel(); _cropEditing = true; _cropRatio.SelectedIndex = 0; CropOverlay.Initialize(_info.Width, _info.Height, _crop);
        CropOverlay.Visibility = _cropPanel.Visibility = Visibility.Visible; ObjectEditor.ShowExternalProperties(_cropPanel); LayoutPreview(); UpdateCropLabel(); UpdateOptions(); ResizeForVideo(); CropOverlay.Focus();
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
        ObjectEditor.ShowExternalProperties(null); LayoutPreview(); UpdateCropLabel(); UpdateOptions(); ResizeForVideo();
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
        Rect slot = VideoCropOverlay.Fit(pixels.Width, pixels.Height, _previewSlot.ActualWidth, _previewSlot.ActualHeight);
        if (slot.IsEmpty) return;
        _videoViewport.Width = slot.Width; _videoViewport.Height = slot.Height;
        Rect view = new(0, 0, slot.Width, slot.Height);
        if (view.IsEmpty) return;
        double scale = view.Width / pixels.Width;
        _preview.Width = _info.Width * scale; _preview.Height = _info.Height * scale;
        Canvas.SetLeft(_preview, view.X - pixels.X * scale); Canvas.SetTop(_preview, view.Y - pixels.Y * scale);
        _videoCanvas.Clip = new RectangleGeometry(view);
        _effectImage.Width = _effectCover.Width = _preview.Width; _effectImage.Height = _effectCover.Height = _preview.Height;
        Canvas.SetLeft(_effectImage, -pixels.X * scale); Canvas.SetTop(_effectImage, -pixels.Y * scale);
        Canvas.SetLeft(_effectCover, -pixels.X * scale); Canvas.SetTop(_effectCover, -pixels.Y * scale);
        ObjectEditor.SetView(pixels); RequestObjectPreview(true);
    }
    private void RequestObjectPreview(bool invalidate = false)
    {
        if (ObjectEditor == null) return;
        bool effects = ObjectEditor.PreviewObjects.Length > 0 || ObjectEditor.TextEditor.Active;
        _effectImage.Visibility = _effectCover.Visibility = effects ? Visibility.Visible : Visibility.Collapsed;
        double time = _playing ? _preview.Position.TotalSeconds : _pendingSeek ?? _preview.Position.TotalSeconds;
        ObjectEditor.UpdateTime(time);
        if (effects) _objectPreview?.Request(time, ObjectEditor.PreviewObjects, invalidate);
    }
    private void PreviewRange(double seconds)
    {
        if (!_ready) return;
        _seekGeneration++; _latestPreview = null; _seekTimer.Stop();
        Pause();
        // The end boundary can equal the duration; seek to the last frame instead of beyond the video.
        double lastFrame = Math.Max(0, _info.Duration - 1 / (_info.Fps > 0 ? _info.Fps : 30));
        Seek(Math.Min(seconds, lastFrame));
    }
    private void QueuePreview(double seconds)
    { Pause(); _seekGeneration++; _latestPreview = seconds; if (!_seekTimer.IsEnabled) _seekTimer.Start(); }
    private async void FlushPreview()
    {
        double seconds = _latestPreview ?? _pendingSeek ?? Timeline.Start;
        PreviewRange(seconds); int generation = _seekGeneration;
        // Reassert the final request after MediaElement processes pending decoder
        // work. A later seek/play/close invalidates this completion.
        await Task.Delay(80);
        if (IsVisible && !_playing && generation == _seekGeneration) Seek(_pendingSeek ?? seconds);
    }
    private void Seek(double seconds) { _seekGeneration++; _pendingSeek = Math.Clamp(seconds, 0, _info.Duration); if (_opened) _preview.Position = TimeSpan.FromSeconds(_pendingSeek.Value); Timeline.SetPosition(_pendingSeek.Value); RequestObjectPreview(true); UpdateTransport(); }
    private void Pause() { if (_opened && (_playing || !_pendingSeek.HasValue)) _pendingSeek = _preview.Position.TotalSeconds; _preview.Pause(); _playing = false; _play.Content = IconSet.Create("play"); _play.Tag = null; UpdateTransport(); }
    private void TogglePlayback()
    { if (!_opened || _exporting || _cropEditing || Timeline.Draft != null || !ObjectEditor.CommitText()) return; _latestPreview = null; _seekTimer.Stop(); _seekGeneration++; if (_playing) Pause(); else { double time = _pendingSeek ?? _preview.Position.TotalSeconds; if (time < Timeline.Start || time >= Timeline.End) time = Timeline.Start; Seek(VideoCuts.Next(time, Timeline.Start, Timeline.End, _deleted)); _preview.SpeedRatio = Speed; _pendingSeek = null; ObjectEditor.Overlay.Cancel(); _preview.Play(); _playing = true; _play.Content = IconSet.Create("pause"); _play.Tag = "selected"; } }
    private void UpdateTransport()
    { double time = _pendingSeek ?? _preview.Position.TotalSeconds; _currentTime.Text = TimeSpan.FromSeconds(Math.Max(0, time)).ToString(@"mm\:ss\.ff"); _totalTime.Text = "/ " + TimeSpan.FromSeconds(Math.Max(0, _info.Duration)).ToString(@"mm\:ss\.ff"); }
    private void RefreshSpeeds(int selected = 0)
    { _refreshingSpeed = true; _speedList.ItemsSource = new[] { "新增区间" }.Concat(_speedRanges.Select(r => r.ToString())).ToArray(); _speedList.SelectedIndex = Math.Clamp(selected, 0, _speedRanges.Length); _refreshingSpeed = false; }
    internal void BeginSpeed(int index = -1)
    {
        if (!_ready || _exporting || _cropEditing || Timeline.Draft != null || _configBlocked || !_tools.CanEdit || !ObjectEditor.CommitText()) return;
        _replaceSpeed = index >= 0 && index < _speedRanges.Length ? index : -1; _draftPreviousPosition = PreviewPosition; Pause();
        _speedTitle.Text = _replaceSpeed >= 0 ? "编辑变速区间" : "新增变速区间";
        var old = _replaceSpeed >= 0 ? _speedRanges[_replaceSpeed] : null;
        var range = old == null ? new VideoRange(Timeline.Start + (Timeline.End - Timeline.Start) / 3, Timeline.Start + (Timeline.End - Timeline.Start) * 2 / 3) : new VideoRange(old.Start, old.End);
        _speed.SelectedIndex = Array.IndexOf(new[] { .5, 1d, 1.5, 2d }, old?.Rate ?? 1.5); Timeline.SetDraftRate(DraftSpeed);
        if (!Timeline.BeginDraft(new(0, _info.Duration), range, _info.Duration, true)) return;
        Timeline.SetSpeeds(_speedRanges.Where((_, i) => i != _replaceSpeed).ToArray()); RefreshSpeeds(_replaceSpeed + 1);
        ObjectEditor.ShowExternalProperties(_speedProperties); _status.Text = "新变速覆盖重叠部分；同倍率相邻合并。红色删除优先，仅对保留内容变速。"; UpdateOptions(); PreviewRange(Timeline.Draft!.Start);
    }
    internal void ConfirmSpeed(double? rate = null)
    {
        if (!Timeline.DraftIsSpeed || Timeline.Draft is not { } draft || _exporting) return;
        var next = new VideoSpeedRange(draft.Start, draft.End, rate ?? DraftSpeed);
        _speedRanges = VideoTiming.Apply(_speedRanges.Where((_, i) => i != _replaceSpeed), next); Timeline.EndDraft(); Timeline.SetSpeeds(_speedRanges); RefreshSpeeds();
        ObjectEditor.ShowExternalProperties(null); UpdateRange(); UpdateOptions(); EditChanged(); PreviewRange(draft.Start);
    }
    internal void CancelSpeed() => CancelSpeed(true);
    private void CancelSpeed(bool closeProperties)
    {
        if (!Timeline.DraftIsSpeed) return; Timeline.EndDraft(); Timeline.SetSpeeds(_speedRanges); PreviewRange(_draftPreviousPosition); UpdateRange();
        if (closeProperties) ObjectEditor.ShowExternalProperties(null); UpdateOptions();
    }
    private async Task<BitmapSource> ReadSourceFrame()
    {
        Pause(); double time = VideoCuts.Snap(Math.Min(PreviewPosition, Math.Max(0, _info.Duration - 1 / _info.Fps)), _info.Fps, _info.Duration);
        return await Task.Run(async () =>
        {
            using var reader = new VideoFrameReader(_tools.Ffmpeg!, _source, _info.Width, _info.Height, _info.Fps);
            var pixels = await reader.FrameAsync(time, _lifetime.Token) ?? throw new InvalidDataException("当前原片帧无法读取。");
            var frame = BitmapSource.Create(_info.Width, _info.Height, 96, 96, PixelFormats.Bgra32, null, pixels, _info.Width * 4); frame.Freeze(); return frame;
        }, _lifetime.Token);
    }
    internal void NavigatePreview(double amount, bool frameStep = false)
    {
        if (!_ready || _exporting || _cropEditing || Timeline.Draft != null || !ObjectEditor.CommitText()) return;
        var kept = VideoCuts.Kept(Timeline.Start, Timeline.End, _deleted); if (kept.Length == 0) return;
        double time = _pendingSeek ?? _preview.Position.TotalSeconds, target;
        if (frameStep)
        {
            target = VideoCuts.Snap(time + amount, _info.Fps, _info.Duration);
            foreach (var cut in _deleted) if (target >= cut.Start && target < cut.End) target = amount < 0 ? cut.Start - 1 / _info.Fps : cut.End;
        }
        else
        {
            target = VideoTiming.SourceTime(Math.Max(0, VideoTiming.OutputTime(time, CurrentEdit()) + amount), CurrentEdit());
        }
        bool resume = _playing && !frameStep;
        PreviewRange(Math.Clamp(target, kept[0].Start, Math.Max(kept[^1].Start, kept[^1].End - 1 / _info.Fps)));
        if (resume) TogglePlayback();
    }
    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (ObjectEditor.PickingColor)
        { if (e.Key == Key.Escape) ObjectEditor.CancelPicker(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.Thumb && e.Key is Key.Left or Key.Right) return;
        if (e.Key == Key.Escape && !ObjectEditor.TextEditor.Active && !ObjectEditor.TextEditor.Composing)
        { if (ObjectEditor.ColorEditing) ObjectEditor.FinishColor(false); else if (_cropEditing) CancelCrop(); else if (Timeline.DraftIsSpeed) CancelSpeed(); else if (Timeline.Draft != null) CancelDelete(); else ObjectEditor.SetTool(null); e.Handled = true; return; }
        if (ObjectEditor.ColorEditing) return;
        if (e.OriginalSource is TextBox || Keyboard.FocusedElement is TextBox || ObjectEditor.TextEditor.Active || ObjectEditor.TextEditor.Composing) return;
        if (Keyboard.Modifiers == ModifierKeys.Control) { if (e.Key == Key.Z) UndoEdit(); else if (e.Key == Key.Y) RedoEdit(); else if (e.Key == Key.S) { ObjectEditor.FlushProperties(); SaveEdit(CurrentEdit()); } else return; e.Handled = true; return; }
        if (!_ready || _exporting || _cropEditing || Timeline.Draft != null) return;
        if (e.Key is Key.Left or Key.Right) { int direction = e.Key == Key.Left ? -1 : 1; bool jump = Keyboard.Modifiers == ModifierKeys.Shift; NavigatePreview(direction * (jump ? 5 : 1 / _info.Fps), !jump); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Space: TogglePlayback(); break;
            case Key.E: ObjectEditor.SetTool(null); break;
            case Key.R: ObjectEditor.SetTool(VideoObjectKind.Rectangle); break;
            case Key.B: ObjectEditor.SetTool(VideoObjectKind.Brush); break;
            case Key.T: ObjectEditor.SetTool(VideoObjectKind.Text); break;
            case Key.M: ObjectEditor.SetTool(VideoObjectKind.Mosaic); break;
            case Key.P: ObjectEditor.OpenImportMenu(); break;
            case Key.C: BeginCrop(); break;
            default: return;
        }
        e.Handled = true;
    }
    internal async Task ExportAsync()
    {
        if (!_ready || _exporting || _cropEditing || Timeline.Draft != null || _configBlocked || ObjectEditor.Importing || !ObjectEditor.CommitText()) return;
        try
        {
            string name = _filename.Text.Trim(); if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("请输入有效的文件名。");
            string target = Path.Combine(Path.GetFullPath(_folder.Text.Trim()), Path.ChangeExtension(name, VideoExportService.Extension(Format)));
            if (Path.GetFullPath(_source).Equals(target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请使用其他文件名另存副本，不能覆盖正在编辑的原录屏。");
            bool overwrite = File.Exists(target); if (overwrite && MessageBox.Show(this, "文件已存在，是否替换？", "保存录屏", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var request = new VideoExportRequest(_source, target, Format, (ExportQuality)_quality.SelectedIndex, Timeline.Start, Timeline.End, 1, _mute.IsChecked == true, _formatFps[RecordingFormat.Gif], _metadata.Quality, overwrite, _crop, FramesPerSecond: _tools.Supports(Format) ? _formatFps[Format] : null, Deleted: _deleted, Objects: ObjectEditor.Objects, SpeedRanges: _speedRanges);
            VideoExportService.Validate(request, _info, _tools); await ConfigurationSaved; if (_saveFailed) throw new IOException("剪辑配置未能保存；请先重试保存配置，原片仍保留。"); Pause(); _exporting = true; _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _options.IsEnabled = _playback.IsEnabled = false; Timeline.IsEnabled = _speed.IsEnabled = _folder.IsEnabled = _filename.IsEnabled = _play.IsEnabled = false;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            _cancel.Content = "取消导出"; _status.Text = "正在导出…"; _progress.Value = 0; UpdateOptions();
            await VideoExportService.ExportAsync(request, _info, _tools, new Progress<double>(value => _progress.Value = value), _exportCancellation.Token);
            await RecordingEditStore.AssociateAsync(target, _source); SavedPath = target; _exporting = false; _preview.Close(); DialogResult = true;
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
        if (!ObjectEditor.CommitText()) { e.Cancel = true; _status.Text = "请先完成当前输入法选词，再关闭编辑器。"; return; }
        if (_exporting) { e.Cancel = true; _exportCancellation?.Cancel(); _status.Text = "正在取消导出…"; return; }
        ObjectEditor.Overlay.Cancel(); ObjectEditor.Tracks.ReleaseMouseCapture();
        if (!_closeApproved && !ConfigurationSaved.IsCompleted)
        { e.Cancel=true; IsEnabled=false; _status.Text="正在保存剪辑配置…"; await ConfigurationSaved; _closeApproved=true; Close(); }
    }
}
