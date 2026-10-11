using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal sealed class AnnotationSurface : FrameworkElement
{
    private readonly BitmapSource _image;
    private readonly Dictionary<int, BitmapSource> _mosaics = new();
    private readonly List<Annotation> _items = new();
    private sealed record Edit(int Index, Annotation? Before, Annotation? After, Int32Rect CropBefore, Int32Rect CropAfter, Action<bool>? Defaults = null);
    private readonly Stack<Edit> _undo = new(), _redo = new();
    private int _selected = -1;
    internal IReadOnlyList<Annotation> Items => _items;
    internal void Restore(IEnumerable<Annotation> items)
    { _items.Clear(); _items.AddRange(items); _undo.Clear(); _redo.Clear(); _selected = -1; Notify(); }
    internal Annotation? Selected => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;
    public Int32Rect CropBounds { get; private set; }
    public int SourceWidth => _image.PixelWidth;
    public int SourceHeight => _image.PixelHeight;
    internal BitmapSource Original => _image;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event Action? CropChanged;
    internal event Action? SelectionChanged;
    public Annotation? Preview { get; set; }
    // Drafts are original-image coordinates and only belong to the screen preview.
    internal Annotation? TextDraft { get; set; }
    internal Annotation? EditingTextOriginal { get; set; }
    public int Count => _items.Count;
    public event Action? Changed;
    internal bool ShowSelection { get; set; } = true;
    public AnnotationSurface(BitmapSource image, Int32Rect? crop = null)
    {
        _image = image; CropBounds = crop ?? new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight); ValidateCrop(CropBounds); Width = CropBounds.Width; Height = CropBounds.Height;
        ClipToBounds = true; Focusable = true;
    }
    private BitmapSource Mosaic(int block)
    {
        if (_mosaics.TryGetValue(block, out var cached)) return cached;
        var image = _image;
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(pixels, image.PixelWidth * 4, 0);
        var mosaicPixels = new byte[pixels.Length]; int stride = image.PixelWidth * 4;
        for (int y = 0; y < image.PixelHeight; y += block)
            for (int x = 0; x < image.PixelWidth; x += block)
            {
                int right = Math.Min(x + block, image.PixelWidth), bottom = Math.Min(y + block, image.PixelHeight), count = (right - x) * (bottom - y);
                int blue = 0, green = 0, red = 0;
                for (int py = y; py < bottom; py++) for (int px = x; px < right; px++)
                { int p = py * stride + px * 4; blue += pixels[p]; green += pixels[p + 1]; red += pixels[p + 2]; }
                for (int py = y; py < bottom; py++) for (int px = x; px < right; px++)
                { int p = py * stride + px * 4; mosaicPixels[p] = (byte)(blue / count); mosaicPixels[p + 1] = (byte)(green / count); mosaicPixels[p + 2] = (byte)(red / count); mosaicPixels[p + 3] = 255; }
            }
        var mosaic = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Bgra32, null, mosaicPixels, stride); mosaic.Freeze();
        if (_mosaics.Count >= 4) _mosaics.Clear(); _mosaics[block] = mosaic; return mosaic;
    }
    internal Point ToOriginal(Point point) => point + new Vector(CropBounds.X, CropBounds.Y);
    private Annotation OriginalCoordinates(Annotation item) => AnnotationGeometry.Move(item, new(CropBounds.X, CropBounds.Y));
    public void Add(Annotation item)
    {
        item = OriginalCoordinates(item); int index = _items.Count; _items.Add(item); Remember(new(index, null, item, CropBounds, CropBounds)); Preview = null; Deselect(); Notify();
    }
    private void Remember(Edit edit) { _undo.Push(edit); _redo.Clear(); }
    internal void Deselect() { if (_selected < 0) return; _selected = -1; InvalidateVisual(); SelectionChanged?.Invoke(); }
    internal bool SelectAt(Point viewportPoint, double tolerance = 6)
    {
        Point point = ToOriginal(viewportPoint); _selected = -1;
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i]; var bounds = AnnotationGeometry.Bounds(item);
            double reach = tolerance + (item.Tool is AnnotationTool.Freehand or AnnotationTool.MosaicBrush ? item.Width / 2 + (item.Tool == AnnotationTool.Freehand && item.OutlineEnabled ? item.OutlineWidth : 0) : item.Tool == AnnotationTool.Rectangle && item.StrokeEnabled ? item.Width / 2 : 0);
            bounds.Inflate(reach, reach);
            if (!bounds.Contains(point)) continue;
            bool hit = item.Tool is AnnotationTool.Text or AnnotationTool.Mosaic;
            if (item.Tool == AnnotationTool.Step) hit = new EllipseGeometry(AnnotationGeometry.Bounds(item)).FillContains(point);
            if (item.Tool == AnnotationTool.Rectangle) { var geometry = AnnotationGeometry.Shape(item.Shape, AnnotationGeometry.Bounds(item)); hit = geometry.FillContains(point) || geometry.StrokeContains(new Pen(Brushes.Black, item.Width + tolerance * 2), point); }
            if (item.Tool is AnnotationTool.Freehand or AnnotationTool.MosaicBrush)
            {
                if (item.Points is { Count: > 0 } points) hit = points.Count == 1 ? (point - points[0]).Length <= reach : Enumerable.Range(1, points.Count - 1).Any(n => NearSegment(point, points[n - 1], points[n], reach));
            }
            if (item.Tool == AnnotationTool.Arrow) hit = NearSegment(point, item.Start, item.End, item.Width / 2 + tolerance + 3);
            if (hit) { _selected = i; break; }
        }
        InvalidateVisual(); SelectionChanged?.Invoke(); return Selected != null;
    }
    private static bool NearSegment(Point p, Point a, Point b, double tolerance)
    {
        var d = b - a; double t = d.LengthSquared < 0.001 ? 0 : Math.Clamp(Vector.Multiply(p - a, d) / d.LengthSquared, 0, 1); return (p - (a + d * t)).Length <= tolerance;
    }
    internal void SetSelected(Annotation value, bool remember = true)
    {
        if (Selected is not { } before || before == value) return;
        _items[_selected] = value; if (remember) Remember(new(_selected, before, value, CropBounds, CropBounds)); Notify(); SelectionChanged?.Invoke();
    }
    internal void CommitSelected(Annotation before, Action<bool>? defaults = null)
    {
        if (Selected is not { } after) return;
        if (after == before) { if (defaults != null) RememberDefaults(defaults); return; }
        Remember(new(_selected, before, after, CropBounds, CropBounds, defaults)); Notify();
    }
    internal void RememberDefaults(Action<bool> apply)
    { Remember(new(-2, null, null, CropBounds, CropBounds, apply)); Notify(); }
    internal void DeleteSelected()
    {
        if (Selected is not { } before) return; int index = _selected; _items.RemoveAt(index); Remember(new(index, before, null, CropBounds, CropBounds)); Deselect(); Notify();
    }
    private void ValidateCrop(Int32Rect crop)
    {
        if (crop.Width < Math.Min(4, SourceWidth) || crop.Height < Math.Min(4, SourceHeight) || crop.X < 0 || crop.Y < 0 || crop.X + crop.Width > SourceWidth || crop.Y + crop.Height > SourceHeight) throw new ArgumentException("裁剪范围超出原图或太小。");
    }
    public void SetCrop(Int32Rect crop, bool remember = true)
    {
        ValidateCrop(crop); if (crop == CropBounds) return; var before = CropBounds;
        CropBounds = crop; Width = crop.Width; Height = crop.Height; Preview = null;
        if (remember) Remember(new(-1, null, null, before, crop));
        CropChanged?.Invoke(); Notify();
    }
    internal void CommitCrop(Int32Rect before) { if (before != CropBounds) { Remember(new(-1, null, null, before, CropBounds)); Notify(); } }
    private void ApplyEdit(Edit edit, bool forward)
    {
        edit.Defaults?.Invoke(forward);
        if (edit.Index == -2) return;
        if (edit.Index < 0) SetCrop(forward ? edit.CropAfter : edit.CropBefore, false);
        else
        {
            var from = forward ? edit.Before : edit.After; var to = forward ? edit.After : edit.Before;
            if (from == null && to != null) _items.Insert(edit.Index, to);
            else if (to == null) _items.RemoveAt(edit.Index);
            else _items[edit.Index] = to;
            Deselect();
        }
    }
    public void Undo() { if (!_undo.TryPop(out var edit)) return; ApplyEdit(edit, false); _redo.Push(edit); Notify(); }
    public void Redo() { if (!_redo.TryPop(out var edit)) return; ApplyEdit(edit, true); _undo.Push(edit); Notify(); }
    private void Notify() { InvalidateVisual(); Changed?.Invoke(); }
    protected override void OnRender(DrawingContext dc) { base.OnRender(dc); Draw(dc, true); }
    private void Draw(DrawingContext dc, bool preview)
    {
        dc.PushTransform(new TranslateTransform(-CropBounds.X, -CropBounds.Y));
        dc.DrawImage(_image, new Rect(0, 0, SourceWidth, SourceHeight));
        foreach (var item in _items)
        {
            if (preview && ReferenceEquals(item, EditingTextOriginal)) { if (TextDraft != null) DrawItem(dc, TextDraft); }
            else DrawItem(dc, item);
        }
        if (preview && Preview != null) DrawItem(dc, OriginalCoordinates(Preview));
        if (preview && EditingTextOriginal == null && TextDraft != null) DrawItem(dc, TextDraft);
        if (preview && TextDraft == null && ShowSelection && Selected is { } selected) DrawSelection(dc, AnnotationGeometry.Bounds(selected));
        dc.Pop();
    }
    internal Vector DisplayScale
    {
        get { var window = Window.GetWindow(this); return window != null && IsLoaded ? CropHandles.ScaleInOwner(this, window) : new(1, 1); }
    }
    internal static readonly (int X, int Y)[] HandleDirections = { (-1,-1), (0,-1), (1,-1), (-1,0), (1,0), (-1,1), (0,1), (1,1) };
    internal (int X, int Y)? SelectedHandle(Point viewportPoint)
    {
        if (Selected is not { } selected || selected.Tool == AnnotationTool.Text) return null;
        var bounds = AnnotationGeometry.Bounds(selected); var p = ToOriginal(viewportPoint); var scale = DisplayScale;
        foreach (var (x, y) in HandleDirections)
        {
            var center = new Point(bounds.Left + (x + 1) * bounds.Width / 2, bounds.Top + (y + 1) * bounds.Height / 2);
            if (Math.Abs(p.X - center.X) <= 7 / scale.X && Math.Abs(p.Y - center.Y) <= 7 / scale.Y) return (x, y);
        }
        return null;
    }
    private void DrawSelection(DrawingContext dc, Rect bounds)
    {
        var scale = DisplayScale; var brush = TryFindResource("SelectionBrush") as Brush ?? Brushes.DodgerBlue;
        var pen = new Pen(brush, 1.5 / Math.Max(scale.X, scale.Y)) { DashStyle = DashStyles.Dash };
        dc.DrawRectangle(null, pen, bounds);
        if (Selected?.Tool == AnnotationTool.Text) return;
        foreach (var (x, y) in HandleDirections) dc.DrawRectangle(Brushes.White, new Pen(brush, 1 / scale.X), new Rect(bounds.Left + (x + 1) * bounds.Width / 2 - 4 / scale.X, bounds.Top + (y + 1) * bounds.Height / 2 - 4 / scale.Y, 8 / scale.X, 8 / scale.Y));
    }
    private void DrawItem(DrawingContext dc, Annotation a)
    {
        var brush = new SolidColorBrush(a.Color); var pen = new Pen(brush, a.Width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var bounds = new Rect(a.Start, a.End);
        switch (a.Tool)
        {
            case AnnotationTool.Rectangle: dc.DrawGeometry(a.FillColor is { } fill ? new SolidColorBrush(fill) : null, a.StrokeEnabled ? pen : null, AnnotationGeometry.Shape(a.Shape, bounds)); break;
            case AnnotationTool.Arrow:
                var vector = a.End - a.Start; if (vector.Length < 1) return;
                vector.Normalize(); var normal = new Vector(-vector.Y, vector.X); var back = a.End - vector * 15;
                dc.DrawLine(pen, a.Start, back); var triangle = new StreamGeometry();
                using (var c = triangle.Open()) { c.BeginFigure(a.End, true, true); c.LineTo(back + normal * 7, true, false); c.LineTo(back - normal * 7, true, false); }
                dc.DrawGeometry(brush, null, triangle); break;
            case AnnotationTool.Text: AnnotationGeometry.DrawText(dc, a); break;
            case AnnotationTool.Step:
                // Opaque fill, contrasting number and two rings remain legible on
                // both bright and dark backgrounds, regardless of palette alpha.
                double diameter = Math.Min(bounds.Width, bounds.Height);
                var center = new Point(bounds.Left + diameter / 2, bounds.Top + diameter / 2);
                var fillBrush = new SolidColorBrush(Color.FromRgb(a.Color.R, a.Color.G, a.Color.B));
                dc.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1), center, diameter / 2, diameter / 2);
                dc.DrawEllipse(fillBrush, null, center, Math.Max(1, diameter / 2 - 2.5), Math.Max(1, diameter / 2 - 2.5));
                var foreground = (a.Color.R * 0.299 + a.Color.G * 0.587 + a.Color.B * 0.114) > 155 ? Brushes.Black : Brushes.White;
                var number = AnnotationGeometry.Text(a with { Text = a.StepNumber.ToString(CultureInfo.InvariantCulture), FontFamily = "Segoe UI", FontSize = diameter * 0.53, Bold = true, TextWidth = 0, Alignment = TextAlignment.Left }, foreground);
                if (number.Width > diameter * 0.72) number.SetFontSize(diameter * 0.53 * diameter * 0.72 / number.Width);
                dc.DrawText(number, new Point(center.X - number.Width / 2, center.Y - number.Height / 2));
                break;
            case AnnotationTool.Mosaic: DrawMosaic(dc, new RectangleGeometry(bounds), a.BlockSize); break;
            case AnnotationTool.Freehand:
            case AnnotationTool.MosaicBrush:
                var points = a.Points; if (points == null || points.Count == 0) return;
                var outline = new Pen(new SolidColorBrush(a.OutlineColor ?? Colors.Black), a.Width + 2 * a.OutlineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                if (points.Count == 1) { var dot = new EllipseGeometry(points[0], a.Width / 2, a.Width / 2); if (a.Tool == AnnotationTool.Freehand) { if (a.OutlineEnabled) dc.DrawEllipse(outline.Brush, null, points[0], a.Width / 2 + a.OutlineWidth, a.Width / 2 + a.OutlineWidth); dc.DrawGeometry(brush, null, dot); } else DrawMosaic(dc, dot, a.BlockSize); break; }
                var path = new StreamGeometry();
                using (var c = path.Open()) { c.BeginFigure(points[0], false, false); for (int i = 1; i < points.Count; i++) c.LineTo(points[i], true, false); }
                path.Freeze(); if (a.Tool == AnnotationTool.Freehand) { if (a.OutlineEnabled) dc.DrawGeometry(null, outline, path); dc.DrawGeometry(null, pen, path); } else DrawMosaic(dc, path.GetWidenedPathGeometry(pen), a.BlockSize); break;
        }
    }
    private void DrawMosaic(DrawingContext dc, Geometry clip, int block) { dc.PushClip(clip); dc.DrawImage(Mosaic(block), new Rect(0, 0, SourceWidth, SourceHeight)); dc.Pop(); }
    public BitmapSource Export()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.PushClip(new RectangleGeometry(new Rect(0, 0, Width, Height))); Draw(dc, false); dc.Pop(); }
        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
