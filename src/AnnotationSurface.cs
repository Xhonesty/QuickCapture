using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal enum AnnotationTool { Arrow, Rectangle, Text, Mosaic, Freehand, MosaicBrush, Crop }
internal sealed record Annotation(AnnotationTool Tool, Point Start, Point End, Color Color, string Text = "", IReadOnlyList<Point>? Points = null, double Width = 3);

internal sealed class AnnotationSurface : FrameworkElement
{
    private readonly BitmapSource _image;
    private readonly BitmapSource _mosaic;
    private readonly List<Annotation> _items = new();
    private sealed record Edit(Annotation? Added, Int32Rect Before, Int32Rect After);
    private readonly Stack<Edit> _undo = new(), _redo = new();
    public Int32Rect CropBounds { get; private set; }
    public int SourceWidth => _image.PixelWidth;
    public int SourceHeight => _image.PixelHeight;
    internal BitmapSource Original => _image;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event Action? CropChanged;
    public Annotation? Preview { get; set; }
    public int Count => _items.Count;
    public event Action? Changed;
    public AnnotationSurface(BitmapSource image, Int32Rect? crop = null)
    {
        _image = image; CropBounds = crop ?? new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight); ValidateCrop(CropBounds); Width = CropBounds.Width; Height = CropBounds.Height;
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
    private Annotation OriginalCoordinates(Annotation item)
    {
        var offset = new Vector(CropBounds.X, CropBounds.Y);
        return item with { Start = item.Start + offset, End = item.End + offset, Points = item.Points?.Select(p => p + offset).ToArray() };
    }
    public void Add(Annotation item) { item = OriginalCoordinates(item); _items.Add(item); _undo.Push(new(item, CropBounds, CropBounds)); _redo.Clear(); Preview = null; Notify(); }
    private void ValidateCrop(Int32Rect crop)
    {
        if (crop.Width < Math.Min(4, SourceWidth) || crop.Height < Math.Min(4, SourceHeight) || crop.X < 0 || crop.Y < 0 || crop.X + crop.Width > SourceWidth || crop.Y + crop.Height > SourceHeight) throw new ArgumentException("裁剪范围超出原图或太小。");
    }
    public void SetCrop(Int32Rect crop, bool remember = true)
    {
        ValidateCrop(crop); if (crop == CropBounds) return; var before = CropBounds;
        CropBounds = crop; Width = crop.Width; Height = crop.Height; Preview = null;
        if (remember) { _undo.Push(new(null, before, crop)); _redo.Clear(); }
        CropChanged?.Invoke(); Notify();
    }
    internal void CommitCrop(Int32Rect before)
    {
        if (before != CropBounds) { _undo.Push(new(null, before, CropBounds)); _redo.Clear(); Notify(); }
    }
    public void Undo()
    {
        if (!_undo.TryPop(out var edit)) return;
        if (edit.Added != null) _items.RemoveAt(_items.Count - 1); else SetCrop(edit.Before, false);
        _redo.Push(edit); Notify();
    }
    public void Redo()
    {
        if (!_redo.TryPop(out var edit)) return;
        if (edit.Added != null) _items.Add(edit.Added); else SetCrop(edit.After, false);
        _undo.Push(edit); Notify();
    }
    private void Notify() { InvalidateVisual(); Changed?.Invoke(); }
    protected override void OnRender(DrawingContext dc) { base.OnRender(dc); Draw(dc, true); }
    private void Draw(DrawingContext dc, bool preview)
    {
        // Annotations and mosaic blocks remain anchored to original image pixels.
        // Cropping changes only this viewport transform, including after undo.
        dc.PushTransform(new TranslateTransform(-CropBounds.X, -CropBounds.Y));
        dc.DrawImage(_image, new Rect(0, 0, SourceWidth, SourceHeight));
        foreach (var item in _items) DrawItem(dc, item);
        if (preview && Preview != null) DrawItem(dc, OriginalCoordinates(Preview));
        dc.Pop();
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
    { dc.PushClip(clip); dc.DrawImage(_mosaic, new Rect(0, 0, SourceWidth, SourceHeight)); dc.Pop(); }
    public BitmapSource Export()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.PushClip(new RectangleGeometry(new Rect(0, 0, Width, Height))); Draw(dc, false); dc.Pop(); }
        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
