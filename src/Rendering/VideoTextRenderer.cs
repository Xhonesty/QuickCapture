using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace QuickCapture;

// WPF's screenshot text formatter supplies wrapping, CJK fallback and alignment.
// Rasterize on a private STA worker, then reuse the same pixels in preview/export.
internal static class VideoTextRenderer
{
    private sealed record Key(string Text, string Family, double Size, bool Bold, bool Italic, TextAlignment Alignment, string Color, double Alpha, bool Outline, string OutlineColor, double OutlineWidth, double OutlineAlpha, double Width, double Height, TextFrameStyle? Frame);
    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<Key, SKBitmap> Cache = new();
    private static readonly Queue<Key> Order = new();
    private static long _bytes;
    static VideoTextRenderer()
    {
        var thread = new Thread(() => { foreach (var action in Queue.GetConsumingEnumerable()) action(); }) { IsBackground = true, Name = "Video text layout" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    internal static void Draw(SKCanvas canvas, VideoObject item, SKRect rect, SKPaint paint)
    {
        if (string.IsNullOrEmpty(item.Text)) return;
        var key = new Key(item.Text, item.FontFamily, item.FontSize, item.Bold, item.Italic, item.Alignment, item.Color, item.StrokeAlpha, item.OutlineEnabled, item.OutlineColor, item.OutlineWidth, item.OutlineAlpha, item.Width, item.Height, item.TextFrame);
        lock (Gate)
        {
            if (!Cache.TryGetValue(key, out var image))
            {
                var ready = new TaskCompletionSource<SKBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
                Queue.Add(() => { try { ready.SetResult(Rasterize(item)); } catch (Exception ex) { ready.SetException(ex); } });
                image = ready.Task.GetAwaiter().GetResult();
                while (Order.Count > 0 && (_bytes + image.ByteCount > 64 * 1024 * 1024 || Cache.Count >= 16))
                { var old = Order.Dequeue(); var removed = Cache[old]; _bytes -= removed.ByteCount; Cache.Remove(old); removed.Dispose(); }
                Cache[key] = image; Order.Enqueue(key); _bytes += image.ByteCount;
            }
            paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White;
            // Integer raster dimensions do not change the logical wrap width.
            canvas.Save(); canvas.ClipRect(rect);
            canvas.DrawBitmap(image, rect.Left, rect.Top, new SKSamplingOptions(SKFilterMode.Nearest), paint); canvas.Restore();
        }
    }
    private static SKBitmap Rasterize(VideoObject item)
    {
        int width = Math.Max(1, (int)Math.Ceiling(item.Width)), height = Math.Max(1, (int)Math.Ceiling(item.Height));
        if ((long)width * height > 33_177_600) throw new InvalidOperationException("文字框像素数过大，请缩小文字框。");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            Color WithAlpha(string hex, double alpha) { var color = (Color)ColorConverter.ConvertFromString(hex); color.A = (byte)Math.Round(color.A * alpha); return color; }
            AnnotationGeometry.DrawText(dc, item.TextAnnotation() with { Color = WithAlpha(item.Color, item.StrokeAlpha), OutlineColor = WithAlpha(item.OutlineColor, item.OutlineAlpha) });
        }
        var frame = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); frame.Render(visual);
        var bytes = new byte[checked(width * height * 4)]; frame.CopyPixels(bytes, width * 4, 0);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)); Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length); return bitmap;
    }
}
