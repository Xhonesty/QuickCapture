using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace QuickCapture;

internal sealed record ThumbnailResult(BitmapSource? Image, string Status);

internal static class RecentThumbnailCache
{
    internal const int Capacity = 96;
    private readonly record struct Key(string Path, long Length, long Modified, int Side);
    private static readonly object Sync = new();
    private static readonly Dictionary<Key, (BitmapSource Image, LinkedListNode<Key> Node)> Cache = new();
    private static readonly LinkedList<Key> Lru = new();
    private static readonly SemaphoreSlim Workers = new(2);
    internal static int Count { get { lock (Sync) return Cache.Count; } }
    private static Key Stamp(string path, int side)
    {
        var file = new FileInfo(path); if (!file.Exists) throw new FileNotFoundException();
        return new(Path.GetFullPath(path).ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks, side);
    }
    private static BitmapSource? Get(Key key)
    {
        lock (Sync)
        {
            if (!Cache.TryGetValue(key, out var entry)) return null;
            Lru.Remove(entry.Node); Lru.AddFirst(entry.Node); return entry.Image;
        }
    }
    // File inspection, decoding and FFmpeg all run off the WPF dispatcher.
    internal static Task<ThumbnailResult> LoadAsync(string path, int side, string toolsPath, CancellationToken token) => Task.Run(async () =>
    {
        side = Math.Clamp(side, 32, 256);
        try
        {
            token.ThrowIfCancellationRequested(); var key = Stamp(path, side);
            if (Get(key) is { } hit) return new ThumbnailResult(hit, "预览");
            await Workers.WaitAsync(token).ConfigureAwait(false);
            BitmapSource image;
            try
            {
                token.ThrowIfCancellationRequested(); key = Stamp(path, side);
                if (Get(key) is { } cached) return new ThumbnailResult(cached, "预览");
                string extension = Path.GetExtension(path).ToLowerInvariant();
                image = extension is ".mp4" or ".webm" ? await VideoFrameAsync(path, side, toolsPath, token).ConfigureAwait(false) : Decode(path, side);
                token.ThrowIfCancellationRequested();
                // A replacement while decoding must never enter the cache under an old stamp.
                if (Stamp(path, side) != key) return new ThumbnailResult(null, "文件已变化，请刷新列表");
                lock (Sync)
                {
                    foreach (var old in new List<Key>(Cache.Keys))
                        if (old.Path == key.Path && (old.Length != key.Length || old.Modified != key.Modified)) { Lru.Remove(Cache[old].Node); Cache.Remove(old); }
                    if (Cache.TryGetValue(key, out var existing)) Lru.Remove(existing.Node);
                    Cache[key] = (image, Lru.AddFirst(key));
                    while (Cache.Count > Capacity) { var last = Lru.Last!; Cache.Remove(last.Value); Lru.RemoveLast(); }
                }
            }
            finally { Workers.Release(); }
            return new ThumbnailResult(image, "预览");
        }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { return new ThumbnailResult(null, "文件已缺失"); }
        catch (DirectoryNotFoundException) { return new ThumbnailResult(null, "文件已缺失"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new ThumbnailResult(null, "无法生成预览"); }
    }, token);
    private static BitmapSource Decode(string path, int side)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("WebP 无法解码");
            var source = codec.Info; double ratio = Math.Min(1, side / (double)Math.Max(source.Width, source.Height));
            var info = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * ratio)), Math.Max(1, (int)Math.Round(source.Height * ratio)), SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("WebP 预览无法解码");
            var image = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Pbgra32, null, bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes); image.Freeze(); return image;
        }
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0]; bool wide = frame.PixelWidth >= frame.PixelHeight;
        stream.Position = 0;
        var preview = new BitmapImage(); preview.BeginInit(); preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.StreamSource = stream;
        if (wide) preview.DecodePixelWidth = Math.Min(side, frame.PixelWidth); else preview.DecodePixelHeight = Math.Min(side, frame.PixelHeight);
        preview.EndInit(); preview.Freeze(); return preview;
    }
    private static async Task<BitmapSource> VideoFrameAsync(string path, int side, string toolsPath, CancellationToken token)
    {
        string ffmpeg = MediaTools.Find("ffmpeg.exe", toolsPath) ?? throw new NotSupportedException("FFmpeg 未安装");
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-i", path, "-an", "-vf", $"scale={side}:{side}:force_original_aspect_ratio=decrease", "-frames:v", "1", "-f", "image2pipe", "-c:v", "png", "-threads", "1", "pipe:1" }) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = new Process { StartInfo = start }; process.Start();
        using var output = new MemoryStream(); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); await copy.ConfigureAwait(false); await errors.ConfigureAwait(false);
            if (process.ExitCode != 0 || output.Length == 0) throw new InvalidDataException("视频首帧无法解码");
            output.Position = 0; var frame = BitmapFrame.Create(output, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); frame.Freeze(); return frame;
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(copy, errors).ConfigureAwait(false); } catch (OperationCanceledException) { }
            token.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new InvalidDataException("视频首帧预览超时");
            throw;
        }
    }
}

// Rendering here keeps uniform aspect ratio and rounded clipping independent of row recycling.
public sealed class RecentThumbnail : FrameworkElement
{
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(RecentThumbnail), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(nameof(Revision), typeof(string), typeof(RecentThumbnail), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty ToolsPathProperty = DependencyProperty.Register(nameof(ToolsPath), typeof(string), typeof(RecentThumbnail), new PropertyMetadata("", Changed));
    public string FilePath { get => (string)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    public string Revision { get => (string)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public string ToolsPath { get => (string)GetValue(ToolsPathProperty); set => SetValue(ToolsPathProperty, value); }
    private CancellationTokenSource? _load;
    private long _generation;
    internal BitmapSource? Preview { get; private set; }
    internal string PreviewStatus { get; private set; } = "加载中";
    internal Task Loading { get; private set; } = Task.CompletedTask;
    private bool IsVideo => Path.GetExtension(FilePath).ToLowerInvariant() is ".mp4" or ".webm" or ".gif";
    public RecentThumbnail()
    {
        Width = Height = 32; SnapsToDevicePixels = true;
        Loaded += (_, _) => { ThemeService.Changed += ThemeChanged; Reload(); };
        Unloaded += (_, _) => { ThemeService.Changed -= ThemeChanged; _generation++; _load?.Cancel(); _load?.Dispose(); _load = null; Preview = null; };
        SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "BadgeForeground");
    }
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args) => ((RecentThumbnail)element).Reload();
    private void ThemeChanged(string theme) => InvalidateVisual();
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); Reload(); }
    private void Reload()
    {
        long generation = ++_generation; _load?.Cancel(); _load?.Dispose(); _load = null;
        Preview = null; PreviewStatus = "加载中"; InvalidateVisual();
        if (!IsLoaded || string.IsNullOrWhiteSpace(FilePath)) return;
        _load = new CancellationTokenSource();
        int side = (int)Math.Ceiling(32 * Math.Max(VisualTreeHelper.GetDpi(this).DpiScaleX, VisualTreeHelper.GetDpi(this).DpiScaleY));
        Loading = LoadAsync(FilePath, side, ToolsPath, generation, _load.Token);
    }
    private async Task LoadAsync(string path, int side, string tools, long generation, CancellationToken token)
    {
        try
        {
            var result = await RecentThumbnailCache.LoadAsync(path, side, tools, token);
            if (generation != _generation || token.IsCancellationRequested || !IsLoaded) return;
            Preview = result.Image; PreviewStatus = result.Status; ToolTip = (IsVideo ? "视频 · " : "图片 · ") + result.Status; InvalidateVisual();
        }
        catch (OperationCanceledException) { }
    }
    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight); dc.PushClip(new RectangleGeometry(bounds, 6, 6));
        dc.DrawRectangle(ThemeService.Brush("BadgeBackground"), null, bounds);
        if (Preview is { } image)
        {
            // Checkerboard reveals transparent screenshots in either theme.
            for (int y = 0; y < ActualHeight; y += 4) for (int x = 0; x < ActualWidth; x += 4)
                if ((x / 4 + y / 4) % 2 == 0) dc.DrawRectangle(ThemeService.Brush("BorderBrush"), null, new Rect(x, y, 4, 4));
            double scale = Math.Min(ActualWidth / image.PixelWidth, ActualHeight / image.PixelHeight);
            double width = image.PixelWidth * scale, height = image.PixelHeight * scale;
            dc.DrawImage(image, new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height));
            if (IsVideo)
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(210, 15, 25, 23)), null, new Point(24, 24), 7, 7);
                dc.DrawGeometry(Brushes.White, null, Geometry.Parse("M22,20 L27,24 L22,28 Z"));
            }
        }
        else
        {
            var pen = new Pen(ThemeService.Brush("BadgeForeground"), 1.6) { LineJoin = PenLineJoin.Round };
            dc.DrawRoundedRectangle(null, pen, new Rect(8, 7, 16, 18), 2, 2);
            if (IsVideo) dc.DrawGeometry(null, pen, Geometry.Parse("M13,12 L20,16 L13,20 Z"));
            else { dc.DrawEllipse(null, pen, new Point(13, 12), 1.4, 1.4); dc.DrawGeometry(null, pen, Geometry.Parse("M9,23 L17,15 L23,21")); }
        }
        dc.Pop();
    }
}
