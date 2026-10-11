using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace QuickCapture;

// The same compositor is used by frame preview and the export pipe. Effects are
// applied before cropping, cutting and retiming, on the original source frame.
internal static class VideoObjectRenderer
{
    internal static void Render(byte[] pixels, VideoInfo info, IEnumerable<VideoObject> objects, double time, IReadOnlyDictionary<string, SKBitmap> assets)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var canvas = new SKCanvas(bitmap);
        byte[] sourcePixels = objects.Any(o => o.Kind == VideoObjectKind.Mosaic) ? (byte[])pixels.Clone() : pixels;
        foreach (var o in objects.OrderBy(o => o.Layer).Where(o => o.Active(time, info.Fps)))
        {
            var rect = SKRect.Create((float)o.X, (float)o.Y, (float)o.Width, (float)o.Height);
            using var paint = new SKPaint { IsAntialias = true, Color = Parse(o.Color, o.StrokeAlpha), StrokeWidth = (float)o.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Style = SKPaintStyle.Stroke };
            if (o.Kind == VideoObjectKind.Mosaic)
            {
                canvas.Save();
                using var mask = MosaicMask(o);
                if (o.MosaicBrush) canvas.ClipPath(mask, SKClipOperation.Intersect, true); else canvas.ClipRect(rect);
                double radius = o.MosaicBrush ? o.Stroke / 2 : 0;
                int left = Math.Max(0, (int)Math.Floor((o.X - radius) / o.BlockSize) * o.BlockSize), top = Math.Max(0, (int)Math.Floor((o.Y - radius) / o.BlockSize) * o.BlockSize);
                int right = Math.Min(info.Width, (int)Math.Ceiling(o.X + o.Width + radius)), bottom = Math.Min(info.Height, (int)Math.Ceiling(o.Y + o.Height + radius));
                paint.Style = SKPaintStyle.Fill; paint.IsAntialias = false;
                for (int y = top; y < bottom; y += o.BlockSize) for (int x = left; x < right; x += o.BlockSize)
                {
                    int r = Math.Min(info.Width, x + o.BlockSize), b = Math.Min(info.Height, y + o.BlockSize), count = (r - x) * (b - y);
                    long blue = 0, green = 0, red = 0;
                    for (int yy = y; yy < b; yy++) for (int xx = x; xx < r; xx++) { int p = (yy * info.Width + xx) * 4; blue += sourcePixels[p]; green += sourcePixels[p + 1]; red += sourcePixels[p + 2]; }
                    paint.Color = new SKColor((byte)(red / count), (byte)(green / count), (byte)(blue / count));
                    canvas.DrawRect(SKRect.Create(x, y, r - x, b - y), paint);
                }
                canvas.Restore();
            }
            else if (o.Kind == VideoObjectKind.PictureInPicture)
            {
                if (o.Asset != null && (assets.TryGetValue(o.Id, out var asset) || assets.TryGetValue(o.Asset, out asset)))
                { paint.Color = new SKColor(255, 255, 255, (byte)Math.Round(o.Opacity * 255)); canvas.DrawBitmap(asset, rect, new SKSamplingOptions(SKFilterMode.Linear), paint); }
            }
            else if (o.Kind == VideoObjectKind.Text) VideoTextRenderer.Draw(canvas, o, rect, paint);
            else if (o.Kind == VideoObjectKind.Brush)
            {
                using var path = new SKPathBuilder(); var points = o.Points!; path.MoveTo((float)points[0].X, (float)points[0].Y);
                foreach (var p in points.Skip(1)) path.LineTo((float)p.X, (float)p.Y);
                using var geometry = path.Detach();
                void DrawBrush() { if (points.Length == 1) { paint.Style = SKPaintStyle.Fill; canvas.DrawCircle((float)points[0].X, (float)points[0].Y, paint.StrokeWidth / 2, paint); } else { paint.Style = SKPaintStyle.Stroke; canvas.DrawPath(geometry, paint); } }
                if (o.OutlineEnabled) { paint.Color = Parse(o.OutlineColor, o.OutlineAlpha); paint.StrokeWidth = (float)(o.Stroke + 2 * o.OutlineWidth); DrawBrush(); }
                if (o.StrokeEnabled) { paint.Color = Parse(o.Color, o.StrokeAlpha); paint.StrokeWidth = (float)o.Stroke; DrawBrush(); }
            }
            else if (o.Kind is VideoObjectKind.Arrow or VideoObjectKind.Line)
            {
                float x1 = (float)(o.ReverseX ? o.X + o.Width : o.X), x2 = (float)(o.ReverseX ? o.X : o.X + o.Width);
                float y1 = (float)(o.ReverseY ? o.Y + o.Height : o.Y), y2 = (float)(o.ReverseY ? o.Y : o.Y + o.Height);
                double angle = Math.Atan2(y2 - y1, x2 - x1), head = Math.Max(12, o.Stroke * 4);
                using var path = new SKPathBuilder(); path.MoveTo(x1, y1); path.LineTo(x2, y2);
                if (o.Kind == VideoObjectKind.Arrow)
                {
                    path.MoveTo(x2, y2); path.LineTo(x2 - (float)(head * Math.Cos(angle - .45)), y2 - (float)(head * Math.Sin(angle - .45)));
                    path.MoveTo(x2, y2); path.LineTo(x2 - (float)(head * Math.Cos(angle + .45)), y2 - (float)(head * Math.Sin(angle + .45)));
                }
                using var geometry = path.Detach();
                if (o.OutlineEnabled) { paint.Color = Parse(o.OutlineColor, o.OutlineAlpha); paint.StrokeWidth = (float)(o.Stroke + 2 * o.OutlineWidth); canvas.DrawPath(geometry, paint); }
                if (o.StrokeEnabled) { paint.Color = Parse(o.Color, o.StrokeAlpha); paint.StrokeWidth = (float)o.Stroke; canvas.DrawPath(geometry, paint); }
            }
            else
            {
                void Draw() { if (o.Kind == VideoObjectKind.Ellipse) canvas.DrawOval(rect, paint); else if (o.Kind == VideoObjectKind.RoundedRectangle) { float radius = (float)Math.Min(14, Math.Min(o.Width, o.Height) / 4); canvas.DrawRoundRect(rect, radius, radius, paint); } else canvas.DrawRect(rect, paint); }
                if (o.Fill != "none" && o.Kind != VideoObjectKind.Brush) { paint.Style = SKPaintStyle.Fill; paint.Color = Parse(o.Fill, o.FillAlpha); Draw(); }
                if (o.StrokeEnabled && o.Kind != VideoObjectKind.Brush) { paint.Style = SKPaintStyle.Stroke; paint.Color = Parse(o.Color, o.StrokeAlpha); Draw(); }
            }
        }
        canvas.Flush(); Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
    }
    private static SKPath MosaicMask(VideoObject o)
    {
        using var path = new SKPathBuilder();
        if (!o.MosaicBrush || o.Points is not { Length: > 0 } points) return path.Detach();
        float radius = (float)o.Stroke / 2;
        foreach (var p in points) path.AddCircle((float)p.X, (float)p.Y, radius);
        for (int i = 1; i < points.Length; i++)
        {
            var a = points[i - 1]; var b = points[i]; double length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)); if (length < .0001) continue;
            float dx = (float)((b.Y - a.Y) / length * radius), dy = (float)((a.X - b.X) / length * radius);
            path.MoveTo((float)a.X + dx, (float)a.Y + dy); path.LineTo((float)b.X + dx, (float)b.Y + dy); path.LineTo((float)b.X - dx, (float)b.Y - dy); path.LineTo((float)a.X - dx, (float)a.Y - dy); path.Close();
        }
        return path.Detach();
    }
    private static SKColor Parse(string color, double opacity)
    { if (!SKColor.TryParse(color, out var value)) value = SKColors.Red; return value.WithAlpha((byte)Math.Round(value.Alpha * opacity)); }
}

internal sealed class VideoFrameReader : IDisposable
{
    private readonly string _ffmpeg, _path;
    private readonly int _width, _height;
    private readonly double _fps;
    internal double Origin { get; }
    private Process? _process;
    private Task<string>? _error;
    private long _nextFrame, _currentFrame = -1;
    private byte[]? _current;
    internal VideoFrameReader(string ffmpeg, string path, int width, int height, double fps, double origin = 0)
    { _ffmpeg = ffmpeg; _path = path; _width = width; _height = height; _fps = fps; Origin = origin; }
    private void Open(long frame)
    {
        DisposeProcess();
        var start = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-nostdin", "-v", "error", "-ss", (Origin + frame / _fps).ToString("0.########", CultureInfo.InvariantCulture), "-noautorotate", "-i", _path, "-an", "-vf", $"fps={_fps.ToString("0.########", CultureInfo.InvariantCulture)},scale={_width}:{_height}", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }) start.ArgumentList.Add(a);
        _process = Process.Start(start)!; _error = _process.StandardError.ReadToEndAsync(); _nextFrame = frame; _currentFrame = -1;
    }
    internal async Task<byte[]?> FrameAsync(double time, CancellationToken token)
    {
        long target = Math.Max(0, (long)Math.Floor((time - Origin) * _fps + .000001));
        if (_process == null || target < _currentFrame || target - _nextFrame > _fps / 2) Open(target);
        using var cancel = token.Register(() => { try { if (_process is { HasExited: false }) _process.Kill(true); } catch (InvalidOperationException) { } });
        while (_currentFrame < target)
        {
            var data = new byte[checked(_width * _height * 4)]; int offset = 0;
            while (offset < data.Length) { int count = await _process!.StandardOutput.BaseStream.ReadAsync(data.AsMemory(offset), token); if (count == 0) break; offset += count; }
            if (offset == 0) { await _process!.WaitForExitAsync(token); if (_process.ExitCode != 0) throw new InvalidDataException("视频解码失败：" + await _error!); return null; }
            if (offset != data.Length) throw new InvalidDataException("解码帧不完整，原片已保留。");
            _current = data; _currentFrame = _nextFrame++;
        }
        return _current;
    }
    private void DisposeProcess()
    {
        if (_process == null) return;
        try { if (!_process.HasExited) _process.Kill(true); } catch (InvalidOperationException) { }
        _process.Dispose(); _process = null;
    }
    public void Dispose() => DisposeProcess();
}

internal sealed class VideoObjectFrames : IDisposable
{
    private readonly string _source, _ffmpeg;
    private readonly double _fps;
    private readonly Dictionary<string, SKBitmap> _images = new();
    private readonly Dictionary<string, VideoFrameReader> _videos = new();
    internal VideoObjectFrames(string source, string ffmpeg, double fps) { _source = source; _ffmpeg = ffmpeg; _fps = fps; }
    internal async Task<IReadOnlyDictionary<string, SKBitmap>> AtAsync(IEnumerable<VideoObject> objects, double time, CancellationToken token)
    {
        var active = new Dictionary<string, SKBitmap>();
        var items = objects.Where(o => o.Kind == VideoObjectKind.PictureInPicture).ToArray();
        var videoKeys = items.Where(o => o.AssetVideo).Select(o => "video:" + o.Id + "/" + o.Asset).ToHashSet();
        foreach (var key in _videos.Keys.Where(key => !videoKeys.Contains(key)).ToArray())
        { _videos[key].Dispose(); _videos.Remove(key); if (_images.Remove(key, out var obsolete)) obsolete.Dispose(); }
        foreach (var o in items.Where(o => o.Active(time, _fps)))
        {
            string path = RecordingEditStore.AssetPath(_source, o.Asset!);
            if (!File.Exists(path)) throw new FileNotFoundException("画中画素材缺失，请选择对象后重新定位素材。", path);
            if (!o.AssetVideo)
            { if (!_images.TryGetValue(o.Asset!, out var image)) _images[o.Asset!] = image = SKBitmap.Decode(path) ?? throw new InvalidDataException("无法读取图片素材。"); active[o.Id] = image; }
            else
            {
                string key = "video:" + o.Id + "/" + o.Asset;
                if (!_videos.TryGetValue(key, out var reader) || Math.Abs(reader.Origin - o.AssetStart) > .000001)
                { reader?.Dispose(); _videos[key] = reader = new(_ffmpeg, path, o.AssetWidth, o.AssetHeight, _fps, o.AssetStart); }
                var pixels = await reader.FrameAsync(o.AssetStart + Math.Max(0, time - o.Start), token);
                if (pixels == null) continue;
                if (!_images.TryGetValue(key, out var image)) _images[key] = image = new SKBitmap(new SKImageInfo(o.AssetWidth, o.AssetHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                Marshal.Copy(pixels, 0, image.GetPixels(), pixels.Length); active[o.Id] = image;
            }
        }
        return active;
    }
    public void Dispose() { foreach (var image in _images.Values) image.Dispose(); foreach (var video in _videos.Values) video.Dispose(); }
}
