using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal enum AnnotationTool { Arrow, Rectangle, Text, Mosaic, Freehand, MosaicBrush }
internal sealed record Annotation(AnnotationTool Tool, Point Start, Point End, Color Color, string Text = "", IReadOnlyList<Point>? Points = null, double Width = 3);

internal sealed class AnnotationSurface : FrameworkElement
{
    private readonly BitmapSource _image;
    private readonly BitmapSource _mosaic;
    private readonly List<Annotation> _items = new();
    private readonly Stack<Annotation> _redo = new();
    public Annotation? Preview { get; set; }
    public int Count => _items.Count;
    public event Action? Changed;
    public AnnotationSurface(BitmapSource image)
    {
        _image = image; Width = image.PixelWidth; Height = image.PixelHeight;
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(pixels, image.PixelWidth * 4, 0);
        // Cache a pixelated copy once; long brush strokes can then clip it cheaply.
        var mosaicPixels = new byte[pixels.Length]; int stride = image.PixelWidth * 4;
        for (int y = 0; y < image.PixelHeight; y += 14)
            for (int x = 0; x < image.PixelWidth; x += 14)
            {
                int right = Math.Min(x + 14, image.PixelWidth), bottom = Math.Min(y + 14, image.PixelHeight), count = (right - x) * (bottom - y);
                int blue = 0, green = 0, red = 0;
                for (int py = y; py < bottom; py++) for (int px = x; px < right; px++)
                { int p = py * stride + px * 4; blue += pixels[p]; green += pixels[p + 1]; red += pixels[p + 2]; }
                for (int py = y; py < bottom; py++) for (int px = x; px < right; px++)
                { int p = py * stride + px * 4; mosaicPixels[p] = (byte)(blue / count); mosaicPixels[p + 1] = (byte)(green / count); mosaicPixels[p + 2] = (byte)(red / count); mosaicPixels[p + 3] = 255; }
            }
        _mosaic = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Bgra32, null, mosaicPixels, stride); _mosaic.Freeze();
        ClipToBounds = true; Focusable = true;
    }
    public void Add(Annotation item) { _items.Add(item); _redo.Clear(); Preview = null; InvalidateVisual(); Changed?.Invoke(); }
    public void Undo() { if (_items.Count > 0) { _redo.Push(_items[^1]); _items.RemoveAt(_items.Count - 1); InvalidateVisual(); Changed?.Invoke(); } }
    public void Redo() { if (_redo.TryPop(out var item)) { _items.Add(item); InvalidateVisual(); Changed?.Invoke(); } }
    protected override void OnRender(DrawingContext dc) { base.OnRender(dc); Draw(dc, true); }
    private void Draw(DrawingContext dc, bool preview)
    {
        dc.DrawImage(_image, new Rect(0, 0, Width, Height));
        foreach (var item in _items) DrawItem(dc, item);
        if (preview && Preview != null) DrawItem(dc, Preview);
    }
    private void DrawItem(DrawingContext dc, Annotation a)
    {
        var brush = new SolidColorBrush(a.Color); var pen = new Pen(brush, a.Width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var bounds = new Rect(a.Start, a.End);
        switch (a.Tool)
        {
            case AnnotationTool.Rectangle: dc.DrawRectangle(null, pen, bounds); break;
            case AnnotationTool.Arrow:
                var vector = a.End - a.Start;
                if (vector.Length < 1) return;
                vector.Normalize(); var normal = new Vector(-vector.Y, vector.X);
                var back = a.End - vector * 15;
                dc.DrawLine(pen, a.Start, back);
                var triangle = new StreamGeometry();
                using (var c = triangle.Open()) { c.BeginFigure(a.End, true, true); c.LineTo(back + normal * 7, true, false); c.LineTo(back - normal * 7, true, false); }
                dc.DrawGeometry(brush, null, triangle); break;
            case AnnotationTool.Text:
                dc.DrawText(new FormattedText(a.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 24, brush, 1), a.Start); break;
            case AnnotationTool.Mosaic:
                DrawMosaic(dc, new RectangleGeometry(bounds)); break;
            case AnnotationTool.Freehand:
            case AnnotationTool.MosaicBrush:
                var points = a.Points;
                if (points == null || points.Count == 0) return;
                if (points.Count == 1)
                {
                    var dot = new EllipseGeometry(points[0], a.Width / 2, a.Width / 2);
                    if (a.Tool == AnnotationTool.Freehand) dc.DrawGeometry(brush, null, dot); else DrawMosaic(dc, dot);
                    break;
                }
                var path = new StreamGeometry();
                using (var c = path.Open()) { c.BeginFigure(points[0], false, false); for (int i = 1; i < points.Count; i++) c.LineTo(points[i], true, false); }
                path.Freeze();
                if (a.Tool == AnnotationTool.Freehand) dc.DrawGeometry(null, pen, path);
                else DrawMosaic(dc, path.GetWidenedPathGeometry(pen));
                break;
        }
    }
    private void DrawMosaic(DrawingContext dc, Geometry clip)
    { dc.PushClip(clip); dc.DrawImage(_mosaic, new Rect(0, 0, Width, Height)); dc.Pop(); }
    public BitmapSource Export()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.PushClip(new RectangleGeometry(new Rect(0, 0, Width, Height))); Draw(dc, false); dc.Pop(); }
        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
