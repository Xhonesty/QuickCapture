using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QuickCapture;

internal sealed class RecordingSaveWindow : Window
{
    private readonly string _source;
    private readonly Settings _settings;
    private readonly RecordingMetadata _metadata;
    private readonly TextBlock _status = UiDesign.Text("正在保存完整录像…", true);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 8, Margin = new Thickness(0, 16, 0, 16) };
    private readonly Button _retry, _edit, _cancel;
    private CancellationTokenSource? _cancellation;
    private bool _saving;
    internal string? SavedPath { get; private set; }
    internal bool ContinueEditing { get; private set; }
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal event Action<string>? Saved;
    internal event Action<bool>? SavingChanged;
    internal RecordingSaveWindow(Window owner, string source, Settings settings, RecordingMetadata metadata)
    {
        Ui.Theme(this); Owner = owner; _source = source; _settings = settings; _metadata = metadata;
        Title = "保存录像"; Width = 470; Height = 220; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(24) }; Content = root; root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _status.Name = "DirectSaveStatus"; _status.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("Text") { Source = _status });
        var message = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _status }; root.Children.Add(message); Grid.SetRow(_progress, 1); root.Children.Add(_progress);
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetRow(row, 2); root.Children.Add(row);
        _retry = Ui.Button("重试保存", () => Completion = SaveAsync()); _retry.Name = "RetryDirectSave"; row.Children.Add(_retry);
        _edit = Ui.Button("继续编辑", () => { ContinueEditing = true; Close(); }); _edit.Name = "EditAfterSave"; row.Children.Add(_edit);
        _cancel = Ui.Button("取消保存", () => { if (_saving) _cancellation?.Cancel(); else Close(); }); _cancel.Name = "CancelDirectSave"; row.Children.Add(_cancel);
        Loaded += (_, _) => Completion = SaveAsync(); Closing += OnClosing;
    }
    private async Task SaveAsync()
    {
        if (_saving) return; _saving = true; SavingChanged?.Invoke(true); _retry.IsEnabled = _edit.IsEnabled = false; _cancel.Content = "取消保存";
        _cancellation = new(); _progress.Value = 0; _progress.IsIndeterminate = true; _status.Text = "正在读取录像…";
        try
        {
            var tools = await MediaTools.DetectAsync(_settings, _cancellation.Token);
            var info = tools.Ffprobe != null ? await VideoExportService.ProbeAsync(_source, tools, _cancellation.Token) : new VideoInfo(_metadata.Duration, 0, 0, _metadata.Fps, _metadata.HasAudio);
            string target = Paths.NewCapture(_settings.OutputDirectory, VideoExportService.Extension(_settings.RecordingFormat));
            var request = new VideoExportRequest(_source, target, _settings.RecordingFormat, _settings.RecordingQuality, 0, info.Duration,
                SourceQuality: _metadata.Quality, FramesPerSecond: _settings.GetRecordingFps(_settings.RecordingFormat));
            _progress.IsIndeterminate = false; _status.Text = "正在保存 · " + RecordingFrameRates.Label(_settings.RecordingFormat);
            await VideoExportService.ExportAsync(request, info, tools, new Progress<double>(v => _progress.Value = v), _cancellation.Token);
            await RecordingEditStore.AssociateAsync(target, _source);
            RecordingRecovery.Remember(_source, RecordingRecovery.Load(_source) with { ExportedPath = target, SaveStatus = "Saved" });
            SavedPath = target; _status.Text = "已保存完整录像，原片保留，可继续编辑。"; _retry.Visibility = Visibility.Collapsed;
            Saved?.Invoke(target);
        }
        catch (OperationCanceledException) { _status.Text = "已取消保存，原片保留。可重试或继续编辑。"; }
        catch (Exception ex) { ErrorLog.Write(ex); _status.Text = "保存失败，原片保留：" + ex.Message; }
        finally { _saving = false; SavingChanged?.Invoke(false); _progress.IsIndeterminate = false; _retry.IsEnabled = _edit.IsEnabled = true; _cancel.Content = "关闭"; _cancellation?.Dispose(); _cancellation = null; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    { if (_saving) { e.Cancel = true; _cancellation?.Cancel(); _status.Text = "正在取消保存…"; } }
}
