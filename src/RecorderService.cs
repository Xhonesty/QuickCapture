using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ScreenRecorderLib;
using Forms = System.Windows.Forms;

namespace QuickCapture;

internal enum RecordingState { Idle, Starting, Recording, Paused, Stopping, Completed, Failed }

internal sealed class RecorderService : IDisposable
{
    private readonly object _gate = new();
    private readonly object _commandGate = new();
    private readonly Stopwatch _elapsed = new();
    private Recorder? _recorder;
    private TaskCompletionSource<string>? _complete;
    private TaskCompletionSource<bool>? _started;
    private TaskCompletionSource<bool>? _pauseRequest;
    private RecordingSourceBase? _source;
    private ScreenSize? _sourcePreviewSize;
    private bool _sourcePreviewEnabled, _refreshWhilePaused;
    private RecordingState _state;
    private long _session;
    private bool _hasStarted;
    public bool IsBusy { get { lock (_gate) return _recorder != null || _state == RecordingState.Starting; } }
    public bool IsStopping { get { lock (_gate) return _state == RecordingState.Stopping; } }
    public bool IsPaused { get { lock (_gate) return _state == RecordingState.Paused; } }
    public RecordingState State { get { lock (_gate) return _state; } }
    public TimeSpan Elapsed { get { lock (_gate) return _elapsed.Elapsed; } }
    private string? _partial, _final;
    public event Action? Started;
    public event Action<RecordingState>? StateChanged;
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
        _partial = finalPath + ".partial.mp4"; _final = finalPath;
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
            long session;
            lock (_gate) { session = ++_session; _hasStarted = false; _elapsed.Reset(); _state = RecordingState.Starting; }
            StateChanged?.Invoke(RecordingState.Starting);
            _source = source; _sourcePreviewEnabled = source.IsVideoFramePreviewEnabled; _sourcePreviewSize = source.VideoFramePreviewSize;
            _refreshWhilePaused = false; source.OnFrameRecorded += DiscardSourcePreview;
            _recorder = Recorder.CreateRecorder(options);
            var recorder = _recorder;
            recorder.OnFrameRecorded += (_, _) =>
            {
                TaskCompletionSource<bool>? request;
                lock (_gate)
                {
                    if (session != _session || _state != RecordingState.Recording || _pauseRequest == null) return;
                    request = _pauseRequest; _pauseRequest = null;
                }
                try
                {
                    // This callback runs after RenderFrame on the recording
                    // worker. Pause here, before its next capture iteration;
                    // the native Paused notification alone is not a barrier.
                    EnablePausedRefresh(recorder);
                    recorder.Pause(); request.TrySetResult(IsPaused);
                }
                catch (Exception ex)
                {
                    try { DisablePausedRefresh(recorder); } catch (Exception restore) { ErrorLog.Write(restore); }
                    request.TrySetException(ex);
                }
            };
            _recorder.OnStatusChanged += (_, e) =>
            {
                bool firstStart = false; RecordingState? changed = null;
                lock (_gate)
                {
                    if (session != _session || _state is RecordingState.Failed or RecordingState.Completed or RecordingState.Idle) return;
                    if (e.Status == RecorderStatus.Recording && _state != RecordingState.Stopping)
                    {
                        firstStart = !_hasStarted; _hasStarted = true; _elapsed.Start();
                        if (_state != RecordingState.Recording) { _state = RecordingState.Recording; changed = _state; }
                    }
                    else if (e.Status == RecorderStatus.Paused && _state != RecordingState.Stopping)
                    { _elapsed.Stop(); _pauseRequest?.TrySetResult(true); _pauseRequest = null; if (_state != RecordingState.Paused) { _state = RecordingState.Paused; changed = _state; } }
                    else if (e.Status == RecorderStatus.Finishing)
                    { _elapsed.Stop(); if (_state != RecordingState.Stopping) { _state = RecordingState.Stopping; changed = _state; } }
                }
                if (firstStart) { _started?.TrySetResult(true); Started?.Invoke(); }
                if (changed is { } state) StateChanged?.Invoke(state);
            };
            _recorder.OnRecordingComplete += (_, _) =>
            {
                lock (_gate) if (session != _session || _state == RecordingState.Failed) return;
                try
                {
                    File.Move(_partial!, _final!, false);
                    lock (_gate) { _elapsed.Stop(); _state = RecordingState.Completed; _pauseRequest?.TrySetResult(false); _pauseRequest = null; }
                    StateChanged?.Invoke(RecordingState.Completed);
                    _complete?.TrySetResult(_final!); Completed?.Invoke(_final!);
                }
                catch (Exception ex) { Fail(ex.Message); }
            };
            _recorder.OnRecordingFailed += (_, e) => { lock (_gate) if (session != _session) return; Fail(e.Error); };
            _recorder.Record(_partial);
            return _started.Task;
        }
        catch (Exception ex) { Fail(ex.Message); throw; }
    }
    private void Fail(string error)
    {
        var ex = new InvalidOperationException($"录屏失败：{error}\n请检查目标窗口和声音设备；编码错误可尝试在设置中关闭硬件编码。");
        lock (_gate) { if (_state == RecordingState.Failed) return; _elapsed.Stop(); _state = RecordingState.Failed; _pauseRequest?.TrySetException(ex); _pauseRequest = null; }
        StateChanged?.Invoke(RecordingState.Failed);
        _started?.TrySetException(ex); _complete?.TrySetException(ex); Failed?.Invoke(ex.Message);
    }
    public bool Pause()
    {
        lock (_commandGate)
        {
            var request = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) { if (_recorder == null || _state != RecordingState.Recording) return false; _pauseRequest = request; }
            // Only wait for a recording-worker acknowledgement. It does not
            // depend on the UI dispatcher and never takes _commandGate.
            _ = request.Task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            try { return request.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            {
                lock (_gate) { if (ReferenceEquals(_pauseRequest, request)) { _pauseRequest = null; request.TrySetResult(false); } }
                throw new InvalidOperationException("录制线程暂未响应暂停，请稍候重试或停止并保存。");
            }
        }
    }
    public bool Resume()
    {
        lock (_commandGate)
        {
            Recorder recorder;
            lock (_gate) { if (_recorder == null || _state != RecordingState.Paused) return false; recorder = _recorder; }
            recorder.Resume(); DisablePausedRefresh(recorder); return true;
        }
    }
    private static void DiscardSourcePreview(object? sender, FrameDataRecordedEventArgs e) { }
    private void EnablePausedRefresh(Recorder recorder)
    {
        if (_source == null || _sourcePreviewEnabled || _refreshWhilePaused) return;
        // Consume producer frames while paused so a shared capture texture
        // cannot retain a stale paused frame at Resume. The native recorder
        // discards these preview frames and audio before writing to the file.
        _source.VideoFramePreviewSize = new ScreenSize(1, 1); _source.IsVideoFramePreviewEnabled = true;
        if (!recorder.GetDynamicOptionsBuilder().SetUpdatedRecordingSource(_source).Apply())
        { _source.IsVideoFramePreviewEnabled = _sourcePreviewEnabled; _source.VideoFramePreviewSize = _sourcePreviewSize; throw new InvalidOperationException("无法准备暂停录制，请停止保存后重试。"); }
        _refreshWhilePaused = true;
    }
    private void DisablePausedRefresh(Recorder recorder)
    {
        if (_source == null || !_refreshWhilePaused) return;
        _source.IsVideoFramePreviewEnabled = _sourcePreviewEnabled; _source.VideoFramePreviewSize = _sourcePreviewSize;
        if (!recorder.GetDynamicOptionsBuilder().SetUpdatedRecordingSource(_source).Apply()) throw new InvalidOperationException("恢复录制后的预览设置未能更新。");
        _refreshWhilePaused = false;
    }
    public bool TogglePause() => IsPaused ? Resume() : Pause();
    public Task<string> StopAsync()
    {
        lock (_commandGate)
        {
            Task<string> task; Recorder? recorder = null;
            lock (_gate)
            {
                if (_recorder == null || _complete == null) throw new InvalidOperationException("没有正在进行的录制。");
                task = _complete.Task;
                if (_state is RecordingState.Completed or RecordingState.Failed) return task;
                if (_state != RecordingState.Stopping) { _state = RecordingState.Stopping; _elapsed.Stop(); recorder = _recorder; }
            }
            if (recorder != null) { StateChanged?.Invoke(RecordingState.Stopping); recorder.Stop(); }
            return task;
        }
    }
    public void Dispose()
    {
        lock (_commandGate)
        {
            Recorder? recorder;
            lock (_gate) { ++_session; recorder = _recorder; _recorder = null; _elapsed.Stop(); _state = RecordingState.Idle; }
            // Native disposal waits for its worker. Do not hold the status lock
            // while waiting for callbacks to unwind.
            recorder?.Dispose();
            if (_source != null)
            {
                _source.IsVideoFramePreviewEnabled = _sourcePreviewEnabled; _source.VideoFramePreviewSize = _sourcePreviewSize;
                _source.OnFrameRecorded -= DiscardSourcePreview; _source = null;
            }
            _refreshWhilePaused = false;
        }
    }
    public static Task<System.Windows.Media.Imaging.BitmapSource> CaptureWindowAsync(IntPtr handle, string tempPath)
    {
        if (!Native.IsWindow(handle) || Native.IsIconic(handle)) throw new InvalidOperationException("窗口已关闭或最小化，请重新选择。");
        if (Native.CaptureProtected(handle)) throw new InvalidOperationException("窗口受捕获保护，无法截图，请选择其他窗口。");
        return CaptureSourceAsync(new WindowRecordingSource(handle) { IsCursorCaptureEnabled = false }, tempPath);
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr handle, IntPtr updateRect, IntPtr updateRegion, uint flags);
    public static async Task<System.Windows.Media.Imaging.BitmapSource> CaptureSourceAsync(RecordingSourceBase source, string tempPath)
    {
        if (source is WindowRecordingSource || source is DisplayRecordingSource { RecorderApi: RecorderApi.WindowsGraphicsCapture })
            return await CaptureGraphicsSourceAsync(source, tempPath);
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
    private static async Task<System.Windows.Media.Imaging.BitmapSource> CaptureGraphicsSourceAsync(RecordingSourceBase source, string tempPath)
    {
        // Screenshot mode can finish from the initial shared surface before WGC
        // produces a window frame. Wait for the producer's actual bitmap event;
        // black pixels are valid content and are never used as a readiness test.
        RecordingSourceBase capture = source is WindowRecordingSource window ? new WindowRecordingSource(window)
            : new DisplayRecordingSource((DisplayRecordingSource)source);
        capture.SourceRect = null; capture.OutputSize = null;
        capture.IsVideoFramePreviewEnabled = true; capture.VideoFramePreviewSize = null;
        var ready = new TaskCompletionSource<System.Windows.Media.Imaging.BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = done.Task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        EventHandler<FrameDataRecordedEventArgs> arrived = (_, e) =>
        {
            if (ready.Task.IsCompleted) return;
            try
            {
                var frame = e.BitmapData;
                if (frame == null || frame.Data == IntPtr.Zero || frame.Width <= 0 || frame.Height <= 0 || frame.Stride < checked(frame.Width * 4)) return;
                int left = 0, top = 0, width = frame.Width, height = frame.Height;
                if (source.SourceRect is { } crop)
                { left = (int)Math.Round(crop.Left); top = (int)Math.Round(crop.Top); width = (int)Math.Round(crop.Width); height = (int)Math.Round(crop.Height); }
                if (left < 0 || top < 0 || width <= 0 || height <= 0 || left + width > frame.Width || top + height > frame.Height)
                    throw new InvalidOperationException("截图区域已超出当前画面，请重新选择。");
                int stride = checked(width * 4); var pixels = new byte[checked(stride * height)];
                // The native bitmap is unmapped as soon as this handler returns.
                // Copy the requested physical pixels now, respecting row pitch.
                for (int row = 0; row < height; row++)
                    Marshal.Copy(IntPtr.Add(frame.Data, checked((top + row) * frame.Stride + left * 4)), pixels, row * stride, stride);
                var image = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
                image.Freeze(); ready.TrySetResult(image);
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        };
        capture.OnFrameRecorded += arrived;
        // Slideshow keeps the capture session alive without starting an H264
        // encoder or audio. Its disposable output is local to the caller's temp.
        string parent = Path.GetDirectoryName(Path.GetFullPath(tempPath))!;
        string scratch = Path.Combine(parent, "capture-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var options = RecorderOptions.Default; options.SourceOptions.RecordingSources = new List<RecordingSourceBase> { capture };
        options.OutputOptions.RecorderMode = RecorderMode.Slideshow; options.AudioOptions.IsAudioEnabled = false;
        options.SnapshotOptions.SnapshotFormat = ScreenRecorderLib.ImageFormat.PNG; options.SnapshotOptions.SnapshotsIntervalMillis = 30_000;
        Recorder? recorder = null;
        try
        {
            recorder = Recorder.CreateRecorder(options);
            recorder.OnRecordingComplete += (_, _) => { done.TrySetResult(true); ready.TrySetException(new InvalidOperationException("目标窗口未产生可用画面，请重新选择。")); };
            recorder.OnRecordingFailed += (_, e) => { var error = new InvalidOperationException(e.Error); ready.TrySetException(error); done.TrySetException(error); };
            recorder.Record(scratch);
            var frameTask = ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (capture is WindowRecordingSource target)
            {
                // ScreenRecorderLib discards the first WGC window frame when
                // recreating its pool. A static window may never supply a
                // second frame, so request paint only while awaiting pixels.
                // Avoid UPDATENOW: an unresponsive foreign window must not
                // synchronously block this capture's caller.
                for (int attempt = 0; attempt < 8 && !frameTask.IsCompleted; attempt++)
                {
                    if (await Task.WhenAny(frameTask, Task.Delay(200)) == frameTask) break;
                    if (!frameTask.IsCompleted && Native.IsWindow(target.Handle) && !Native.IsIconic(target.Handle))
                        RedrawWindow(target.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001u | 0x0080u | 0x0400u); // INVALIDATE | ALLCHILDREN | FRAME
                }
            }
            var image = await frameTask;
            recorder.Stop(); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); return image;
        }
        finally
        {
            recorder?.Dispose(); capture.OnFrameRecorded -= arrived;
            // This GUID directory was created immediately above under parent;
            // delete only its own flat PNG outputs, without recursive deletion.
            foreach (string file in Directory.EnumerateFiles(scratch, "*.png", SearchOption.TopDirectoryOnly)) File.Delete(file);
            Directory.Delete(scratch);
        }
    }
}
