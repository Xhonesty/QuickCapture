using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ScreenRecorderLib;
using Forms = System.Windows.Forms;

namespace QuickCapture;

internal sealed class RecorderService : IDisposable
{
    private Recorder? _recorder;
    private TaskCompletionSource<string>? _complete;
    private TaskCompletionSource<bool>? _started;
    public bool IsBusy => _recorder != null;
    public bool IsStopping { get; private set; }
    private string? _partial, _final;
    public event Action? Started;
    public event Action<string>? Completed;
    public event Action<string>? Failed;

    internal static ScreenRect RelativeCrop(SavedRegion region, System.Drawing.Rectangle monitor)
        => new(region.X - monitor.X, region.Y - monitor.Y, region.Width, region.Height);

    public static RecordingSourceBase RegionSource(SavedRegion region)
    {
        Forms.Screen? screen = null;
        foreach (var candidate in Forms.Screen.AllScreens)
            if (candidate.DeviceName == region.DeviceName && candidate.Bounds.Contains(region.Rectangle)) { screen = candidate; break; }
        if (screen == null) throw new InvalidOperationException("上次区域已不在当前显示器内，请重新选择。区域录屏需位于同一显示器。");
        return new DisplayRecordingSource(screen.DeviceName)
        {
            // A WGC display session outlines the entire monitor even with a crop.
            // Desktop Duplication has no system monitor border; RecordingFrame
            // outlines only the selected region instead.
            RecorderApi = RecorderApi.DesktopDuplication,
            SourceRect = RelativeCrop(region, screen.Bounds)
        };
    }
    public Task StartAsync(RecordingSourceBase source, Settings settings, string finalPath)
    {
        if (IsBusy) throw new InvalidOperationException("已有录制正在进行。");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        _partial = finalPath + ".partial.mp4"; _final = finalPath; IsStopping = false;
        _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Event subscribers observe failures; avoid an unobserved task when starting fails.
        _ = _complete.Task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = RecorderOptions.Default;
        options.SourceOptions.RecordingSources = new List<RecordingSourceBase> { source };
        options.OutputOptions.RecorderMode = RecorderMode.Video;
        // H.264 requires even output dimensions; explicit dimensions also avoid
        // discrepancies between Win32 window bounds and WGC content bounds.
        int width = 0, height = 0;
        if (source is WindowRecordingSource ws && Native.GetWindowRect(ws.Handle, out var bounds))
        { width = bounds.Right - bounds.Left; height = bounds.Bottom - bounds.Top; }
        else if (source.SourceRect != null && source.SourceRect.Width > 0)
        { width = (int)source.SourceRect.Width; height = (int)source.SourceRect.Height; }
        else if (source is DisplayRecordingSource ds)
            foreach (var monitor in Forms.Screen.AllScreens)
                if (monitor.DeviceName == ds.DeviceName) { width = monitor.Bounds.Width; height = monitor.Bounds.Height; }
        if (width < 2 || height < 2) throw new InvalidOperationException("无法获取录制区域尺寸。");
        options.OutputOptions.OutputFrameSize = new ScreenSize(width - width % 2, height - height % 2);
        options.VideoEncoderOptions.Framerate = settings.FramesPerSecond;
        options.VideoEncoderOptions.Bitrate = settings.RecordingQuality switch { ExportQuality.Low => 4_000_000, ExportQuality.High => 16_000_000, _ => 8_000_000 };
        options.VideoEncoderOptions.IsHardwareEncodingEnabled = settings.HardwareEncoding;
        options.VideoEncoderOptions.IsThrottlingDisabled = false;
        options.VideoEncoderOptions.IsMp4FastStartEnabled = true;
        options.AudioOptions.IsAudioEnabled = settings.SystemAudio || settings.Microphone;
        if (settings.SystemAudio) options.AudioOptions.AudioSources.Add(LoopbackAudioSource.Default);
        if (settings.Microphone) options.AudioOptions.AudioSources.Add(CaptureAudioSource.Default);
        options.MouseOptions.IsMousePointerEnabled = settings.Cursor;
        Directory.CreateDirectory(Paths.Data);
        options.LogOptions.IsLogEnabled = true;
        options.LogOptions.LogSeverityLevel = LogLevel.Info;
        string logPath = Path.Combine(Paths.Data, "recorder.log");
        File.WriteAllText(logPath, "");
        options.LogOptions.LogFilePath = logPath;
        if (source is DisplayRecordingSource display) display.IsCursorCaptureEnabled = settings.Cursor;
        if (source is WindowRecordingSource window) window.IsCursorCaptureEnabled = settings.Cursor;
        try
        {
            _recorder = Recorder.CreateRecorder(options);
            _recorder.OnStatusChanged += (_, e) =>
            {
                if (e.Status == RecorderStatus.Recording) { _started?.TrySetResult(true); Started?.Invoke(); }
            };
            _recorder.OnRecordingComplete += (_, _) =>
            {
                try
                {
                    File.Move(_partial!, _final!, false);
                    _complete?.TrySetResult(_final!); Completed?.Invoke(_final!);
                }
                catch (Exception ex) { Fail(ex.Message); }
            };
            _recorder.OnRecordingFailed += (_, e) => Fail(e.Error);
            _recorder.Record(_partial);
            return _started.Task;
        }
        catch (Exception ex) { Fail(ex.Message); throw; }
    }
    private void Fail(string error)
    {
        var ex = new InvalidOperationException($"录屏失败：{error}\n请检查目标窗口和声音设备；编码错误可尝试在设置中关闭硬件编码。");
        _started?.TrySetException(ex); _complete?.TrySetException(ex); Failed?.Invoke(ex.Message);
    }
    public Task<string> StopAsync()
    {
        if (_recorder == null || _complete == null) throw new InvalidOperationException("没有正在进行的录制。");
        if (!IsStopping) { IsStopping = true; _recorder.Stop(); }
        return _complete.Task;
    }
    public void Dispose() { _recorder?.Dispose(); _recorder = null; IsStopping = false; }
    public static Task<System.Windows.Media.Imaging.BitmapSource> CaptureWindowAsync(IntPtr handle, string tempPath)
    {
        if (!Native.IsWindow(handle) || Native.IsIconic(handle)) throw new InvalidOperationException("窗口已关闭或最小化，请重新选择。");
        return CaptureSourceAsync(new WindowRecordingSource(handle) { IsCursorCaptureEnabled = false }, tempPath);
    }
    public static async Task<System.Windows.Media.Imaging.BitmapSource> CaptureSourceAsync(RecordingSourceBase source, string tempPath)
    {
        var options = RecorderOptions.Default;
        options.SourceOptions.RecordingSources = new List<RecordingSourceBase> { source };
        options.OutputOptions.RecorderMode = RecorderMode.Screenshot;
        options.SnapshotOptions.SnapshotFormat = ScreenRecorderLib.ImageFormat.PNG;
        var recorder = Recorder.CreateRecorder(options);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.OnRecordingComplete += (_, _) => done.TrySetResult(true);
        recorder.OnRecordingFailed += (_, e) => done.TrySetException(new InvalidOperationException(e.Error));
        try { recorder.Record(tempPath); await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); return CaptureService.Load(tempPath); }
        finally { recorder.Dispose(); if (File.Exists(tempPath)) File.Delete(tempPath); }
    }
}
