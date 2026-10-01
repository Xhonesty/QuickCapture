using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal enum AnnotationTool { Arrow, Rectangle, Text, Mosaic }
internal sealed record Annotation(AnnotationTool Tool, Point Start, Point End, Color Color, string Text = "");

internal sealed class AnnotationSurface : FrameworkElement
{
    private readonly BitmapSource _image;
    private readonly byte[] _pixels;
    private readonly List<Annotation> _items = new();
    private readonly Stack<Annotation> _redo = new();
    public Annotation? Preview { get; set; }
    public int Count => _items.Count;
    public AnnotationSurface(BitmapSource image)
    {
        _image = image; Width = image.PixelWidth; Height = image.PixelHeight;
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        _pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(_pixels, image.PixelWidth * 4, 0);
        ClipToBounds = true; Focusable = true;
    }
    public void Add(Annotation item) { _items.Add(item); _redo.Clear(); Preview = null; InvalidateVisual(); }
    public void Undo() { if (_items.Count > 0) { _redo.Push(_items[^1]); _items.RemoveAt(_items.Count - 1); InvalidateVisual(); } }
    public void Redo() { if (_redo.TryPop(out var item)) { _items.Add(item); InvalidateVisual(); } }
    protected override void OnRender(DrawingContext dc) { base.OnRender(dc); Draw(dc, true); }
    private void Draw(DrawingContext dc, bool preview)
    {
        dc.DrawImage(_image, new Rect(0, 0, Width, Height));
        foreach (var item in _items) DrawItem(dc, item);
        if (preview && Preview != null) DrawItem(dc, Preview);
    }
    private void DrawItem(DrawingContext dc, Annotation a)
    {
        var brush = new SolidColorBrush(a.Color); var pen = new Pen(brush, 3);
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
                int x0 = Math.Max(0, (int)bounds.Left), y0 = Math.Max(0, (int)bounds.Top);
                int x1 = Math.Min((int)Width, (int)Math.Ceiling(bounds.Right)), y1 = Math.Min((int)Height, (int)Math.Ceiling(bounds.Bottom));
                for (int y = y0; y < y1; y += 14)
                    for (int x = x0; x < x1; x += 14)
                    {
                        int right = Math.Min(x + 14, x1), bottom = Math.Min(y + 14, y1), count = (right - x) * (bottom - y);
                        int blue = 0, green = 0, red = 0;
                        for (int py = y; py < bottom; py++) for (int px = x; px < right; px++)
                        { int p = (py * (int)Width + px) * 4; blue += _pixels[p]; green += _pixels[p + 1]; red += _pixels[p + 2]; }
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(red / count), (byte)(green / count), (byte)(blue / count))), null, new Rect(x, y, right - x, bottom - y));
                    }
                break;
        }
    }
    public BitmapSource Export()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.PushClip(new RectangleGeometry(new Rect(0, 0, Width, Height))); Draw(dc, false); dc.Pop(); }
        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
