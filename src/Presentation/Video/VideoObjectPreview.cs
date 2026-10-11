using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

// One decoder worker coalesces requests. Dragging never starts a full encode.
internal sealed class VideoObjectPreview : IDisposable
{
    private readonly object _gate = new();
    private readonly string _source, _ffmpeg;
    private readonly VideoInfo _info;
    private readonly CancellationTokenSource _lifetime = new();
    private (double Time, VideoObject[] Objects, int Revision)? _pending;
    private readonly SemaphoreSlim _requests = new(0, 1);
    private readonly Task _worker;
    private int _revision;
    private bool _disposed;
    internal event Action<BitmapSource>? FrameReady;
    internal event Action<string>? Failed;
    internal VideoObjectPreview(string source, string ffmpeg, VideoInfo info) { _source = source; _ffmpeg = ffmpeg; _info = info; _worker = Task.Run(WorkAsync); }
    internal void Request(double time, VideoObject[] objects, bool invalidate = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (invalidate) _revision++;
            bool wake = _pending == null;
            _pending = (Math.Max(0, Math.Min(time, Math.Max(0, _info.Duration - 1 / _info.Fps))), objects.ToArray(), _revision);
            if (wake) _requests.Release();
        }
    }
    private async Task WorkAsync()
    {
        using var reader = new VideoFrameReader(_ffmpeg, _source, _info.Width, _info.Height, _info.Fps);
        using var assets = new VideoObjectFrames(_source, _ffmpeg, _info.Fps);
        while (true)
        {
            (double Time, VideoObject[] Objects, int Revision) request;
            try { await _requests.WaitAsync(_lifetime.Token); } catch (OperationCanceledException) { return; }
            lock (_gate) { if (_disposed) return; if (_pending == null) continue; request = _pending.Value; _pending = null; }
            try
            {
                var raw = await reader.FrameAsync(request.Time, _lifetime.Token); if (raw == null) continue;
                var pixels = (byte[])raw.Clone(); var material = await assets.AtAsync(request.Objects, request.Time, _lifetime.Token);
                VideoObjectRenderer.Render(pixels, _info, request.Objects, request.Time, material);
                var frame = BitmapSource.Create(_info.Width, _info.Height, 96, 96, PixelFormats.Bgra32, null, pixels, _info.Width * 4); frame.Freeze();
                lock (_gate) { if (!_disposed && request.Revision == _revision) FrameReady?.Invoke(frame); }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { lock (_gate) { if (!_disposed && request.Revision == _revision) Failed?.Invoke(ex.Message); } }
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _pending = null; }
        _lifetime.Cancel();
        _ = _worker.ContinueWith(_ => { _requests.Dispose(); _lifetime.Dispose(); }, TaskScheduler.Default);
    }
}
