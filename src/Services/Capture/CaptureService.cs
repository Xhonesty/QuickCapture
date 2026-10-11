using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using ScreenRecorderLib;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal static class CaptureService
{
    public static async Task<BitmapSource> CaptureAsync(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) throw new ArgumentException("截取区域为空。");
        Directory.CreateDirectory(Paths.Data);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new System.Windows.Rect(0, 0, rect.Width, rect.Height));
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var intersection = Rectangle.Intersect(rect, screen.Bounds);
                if (intersection.Width <= 0 || intersection.Height <= 0) continue;
                var source = new DisplayRecordingSource(screen.DeviceName)
                {
                    RecorderApi = RecorderApi.WindowsGraphicsCapture, IsCursorCaptureEnabled = false,
                    SourceRect = RecorderService.RelativeCrop(new(intersection.X, intersection.Y, intersection.Width, intersection.Height, screen.DeviceName), screen.Bounds)
                };
                var bitmap = await RecorderService.CaptureSourceAsync(source, Path.Combine(Paths.Data, $"screen-{Guid.NewGuid():N}.png"));
                dc.DrawImage(bitmap, new System.Windows.Rect(intersection.X - rect.X, intersection.Y - rect.Y, intersection.Width, intersection.Height));
            }
        }
        var result = new RenderTargetBitmap(rect.Width, rect.Height, 96, 96, PixelFormats.Pbgra32); result.Render(visual); result.Freeze(); return result;
    }
    public static BitmapSource Load(string path)
    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
    public static void Save(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        string temp = path + ".partial";
        try { using (var stream = File.Create(temp)) encoder.Save(stream); File.Move(temp, path); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
