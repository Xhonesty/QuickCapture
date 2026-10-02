using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace QuickCapture;

internal static class ImageExportService
{
    internal static string Extension(ScreenshotFormat format) => format.ToString().ToLowerInvariant();
    internal static string Label(ScreenshotFormat format) => format == ScreenshotFormat.WebP ? "WebP" : format.ToString().ToUpperInvariant();
    internal static byte[] Encode(BitmapSource source, ScreenshotFormat format, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        if (format == ScreenshotFormat.WebP)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[source.PixelWidth * source.PixelHeight * 4]; converted.CopyPixels(pixels, source.PixelWidth * 4, 0);
            using var bitmap = new SKBitmap(new SKImageInfo(source.PixelWidth, source.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, quality) ?? throw new InvalidOperationException("WebP 编码失败。");
            return encoded.ToArray();
        }
        BitmapEncoder encoder = format switch { ScreenshotFormat.Jpg => new JpegBitmapEncoder { QualityLevel = quality }, ScreenshotFormat.Bmp => new BmpBitmapEncoder(), _ => new PngBitmapEncoder() };
        if (format is ScreenshotFormat.Jpg or ScreenshotFormat.Bmp)
        {
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight)); dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight)); }
            var opaque = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32); opaque.Render(visual);
            source = new FormatConvertedBitmap(opaque, PixelFormats.Bgr24, null, 0);
        }
        encoder.Frames.Add(BitmapFrame.Create(source)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    internal static BitmapSource Decode(byte[] bytes, ScreenshotFormat format)
    {
        if (format == ScreenshotFormat.WebP)
        {
            using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("无法读取 WebP。");
            using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100); bytes = png.ToArray();
        }
        using var stream = new MemoryStream(bytes); var result = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); result.Freeze(); return result;
    }
    internal static string CorrectExtension(string path, ScreenshotFormat format) => Path.ChangeExtension(path, Extension(format));
    internal static void Save(BitmapSource source, string path, ScreenshotFormat format, int quality, bool overwrite = false)
        => AtomicWrite(path, Encode(source, format, quality), overwrite);
    internal static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + $".{Guid.NewGuid():N}.partial";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, overwrite); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static async Task CopyAsync(BitmapSource source, ScreenshotFormat format, int quality)
    {
        byte[] bytes = Encode(source, format, quality), bmp = Encode(Decode(bytes, format), ScreenshotFormat.Bmp, 100);
        string mime = format switch { ScreenshotFormat.Jpg => "image/jpeg", ScreenshotFormat.WebP => "image/webp", ScreenshotFormat.Bmp => "image/bmp", _ => "image/png" };
        string registered = format switch { ScreenshotFormat.Jpg => "JFIF", ScreenshotFormat.WebP => "WebP", ScreenshotFormat.Bmp => "BMP", _ => "PNG" };
        await ImageClipboard.SetAsync(bytes, bmp, registered, mime);
    }
}
