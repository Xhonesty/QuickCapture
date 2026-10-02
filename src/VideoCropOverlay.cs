using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture;

internal sealed class VideoCropOverlay : FrameworkElement
{
    private Rect _selection, _start;
    private int _width, _height, _horizontal, _vertical;
    private Point _anchor;
    private bool _dragging, _moving;
    internal event Action? SelectionChanged;
    internal double AspectRatio { get; private set; }
    internal Rect Selection => _selection;
    internal Rect PictureBounds => Fit(_width, _height, ActualWidth, ActualHeight);
    internal VideoCropOverlay()
    {
        Name = "VideoCropOverlay"; Focusable = true; Visibility = Visibility.Collapsed;
        ToolTip = "拖动框内移动；拖动边框或八个手柄调整裁剪范围";
        SizeChanged += (_, _) => InvalidateVisual();
    }
    internal static Rect Fit(double width, double height, double viewWidth, double viewHeight)
    {
        if (width <= 0 || height <= 0 || viewWidth <= 0 || viewHeight <= 0) return Rect.Empty;
        double scale = Math.Min(viewWidth / width, viewHeight / height);
        return new((viewWidth - width * scale) / 2, (viewHeight - height * scale) / 2, width * scale, height * scale);
    }
    internal void Initialize(int width, int height, VideoCrop? crop)
    { _width = width; _height = height; AspectRatio = 0; SetSelection(crop?.Bounds ?? new Rect(0, 0, width, height)); }
    internal void SetSelection(Rect value)
    {
        if (_width <= 0 || _height <= 0) return;
        double width = Math.Clamp(value.Width, Math.Min(24, _width), _width), height = Math.Clamp(value.Height, Math.Min(24, _height), _height);
        _selection = new(Math.Clamp(value.X, 0, _width - width), Math.Clamp(value.Y, 0, _height - height), width, height);
        InvalidateVisual(); SelectionChanged?.Invoke();
    }
    internal void SetAspectRatio(double ratio)
    {
        AspectRatio = ratio;
        if (ratio <= 0 || _selection.IsEmpty) return;
        double width = _selection.Width, height = width / ratio;
        if (height > _selection.Height) { height = _selection.Height; width = height * ratio; }
        double minScale = Math.Max(Math.Min(24, _width) / width, Math.Min(24, _height) / height);
        if (minScale > 1) { width *= minScale; height *= minScale; }
        double maxScale = Math.Min(1, Math.Min(_width / width, _height / height)); width *= maxScale; height *= maxScale;
        SetSelection(new(_selection.X + (_selection.Width - width) / 2, _selection.Y + (_selection.Height - height) / 2, width, height));
    }
    internal Point SourceToPreview(Point point)
    { Rect picture = PictureBounds; return picture.IsEmpty ? new() : new(picture.X + point.X * picture.Width / _width, picture.Y + point.Y * picture.Height / _height); }
    private Point PreviewToSource(Point point)
    { Rect picture = PictureBounds; return new((point.X - picture.X) * _width / picture.Width, (point.Y - picture.Y) * _height / picture.Height); }
    private Rect PreviewSelection => new(SourceToPreview(_selection.TopLeft), SourceToPreview(_selection.BottomRight));
    protected override void OnRender(DrawingContext dc)
    {
        if (_width <= 0 || _height <= 0 || PictureBounds.IsEmpty) return;
        Rect picture = PictureBounds, crop = PreviewSelection;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd }; shade.Children.Add(new RectangleGeometry(picture)); shade.Children.Add(new RectangleGeometry(crop));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(165, 0, 0, 0)), null, shade);
        var accent = TryFindResource("Accent") as Brush ?? Brushes.DodgerBlue;
        dc.DrawRectangle(null, new Pen(Brushes.Black, 3), crop); dc.DrawRectangle(null, new Pen(accent, 1.5), crop);
        foreach (var (x, y) in Handles)
        {
            Point p = new(crop.Left + (x + 1) * crop.Width / 2, crop.Top + (y + 1) * crop.Height / 2);
            dc.DrawRectangle(Brushes.White, new Pen(accent, 1.5), new Rect(p.X - 4, p.Y - 4, 8, 8));
        }
        var pixels = VideoCrop.FromSelection(_selection, _width, _height);
        var label = new FormattedText($"{pixels.Width} × {pixels.Height}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double left = Math.Clamp(crop.Left + 8, 0, Math.Max(0, ActualWidth - label.Width - 12)), top = Math.Clamp(crop.Top + 8, 0, Math.Max(0, ActualHeight - label.Height - 8));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), null, new Rect(left - 4, top - 2, label.Width + 8, label.Height + 4), 3, 3); dc.DrawText(label, new(left, top));
    }
    private static readonly (int x, int y)[] Handles = { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) };
    private (int x, int y, bool move) Hit(Point point)
    {
        Rect crop = PreviewSelection;
        foreach (var (x, y) in Handles)
        {
            Point p = new(crop.Left + (x + 1) * crop.Width / 2, crop.Top + (y + 1) * crop.Height / 2);
            if (Math.Abs(point.X - p.X) <= 9 && Math.Abs(point.Y - p.Y) <= 9) return (x, y, false);
        }
        if (crop.Contains(point))
        {
            int x = Math.Abs(point.X - crop.Left) <= 6 ? -1 : Math.Abs(point.X - crop.Right) <= 6 ? 1 : 0;
            int y = Math.Abs(point.Y - crop.Top) <= 6 ? -1 : Math.Abs(point.Y - crop.Bottom) <= 6 ? 1 : 0;
            return (x, y, x == 0 && y == 0);
        }
        return (0, 0, false);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_width < 2 || _height < 2 || PictureBounds.IsEmpty) return;
        var hit = Hit(e.GetPosition(this)); if (!hit.move && hit.x == 0 && hit.y == 0) { e.Handled = true; return; }
        _horizontal = hit.x; _vertical = hit.y; _moving = hit.move; _anchor = PreviewToSource(e.GetPosition(this)); _start = _selection; _dragging = CaptureMouse(); Focus(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_width <= 0 || _height <= 0 || PictureBounds.IsEmpty) return;
        if (!_dragging)
        {
            var hit = Hit(e.GetPosition(this)); Cursor = hit.move ? Cursors.SizeAll : hit.x == 0 && hit.y != 0 ? Cursors.SizeNS : hit.y == 0 && hit.x != 0 ? Cursors.SizeWE : hit.x * hit.y > 0 ? Cursors.SizeNWSE : hit.x * hit.y < 0 ? Cursors.SizeNESW : Cursors.Arrow; return;
        }
        Point point = PreviewToSource(e.GetPosition(this)); Vector delta = point - _anchor;
        if (_moving) SetSelection(new(_start.X + delta.X, _start.Y + delta.Y, _start.Width, _start.Height));
        else Resize(point);
        e.Handled = true;
    }
    private void Resize(Point point)
    {
        double minWidth = Math.Min(24, _width), minHeight = Math.Min(24, _height);
        double left = _horizontal < 0 ? Math.Clamp(point.X, 0, _start.Right - minWidth) : _start.Left;
        double right = _horizontal > 0 ? Math.Clamp(point.X, _start.Left + minWidth, _width) : _start.Right;
        double top = _vertical < 0 ? Math.Clamp(point.Y, 0, _start.Bottom - minHeight) : _start.Top;
        double bottom = _vertical > 0 ? Math.Clamp(point.Y, _start.Top + minHeight, _height) : _start.Bottom;
        if (AspectRatio <= 0) { SetSelection(new Rect(new Point(left, top), new Point(right, bottom))); return; }
        double width = right - left, height = bottom - top;
        if (_horizontal == 0) width = height * AspectRatio;
        else if (_vertical == 0) height = width / AspectRatio;
        else if (width / height > AspectRatio) height = width / AspectRatio;
        else width = height * AspectRatio;
        width = Math.Max(width, Math.Max(minWidth, minHeight * AspectRatio)); height = width / AspectRatio;
        double anchorX = _horizontal < 0 ? _start.Right : _horizontal > 0 ? _start.Left : (_start.Left + _start.Right) / 2;
        double anchorY = _vertical < 0 ? _start.Bottom : _vertical > 0 ? _start.Top : (_start.Top + _start.Bottom) / 2;
        double maxWidth = _horizontal < 0 ? anchorX : _horizontal > 0 ? _width - anchorX : 2 * Math.Min(anchorX, _width - anchorX);
        double maxHeight = _vertical < 0 ? anchorY : _vertical > 0 ? _height - anchorY : 2 * Math.Min(anchorY, _height - anchorY);
        double scale = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height)); width *= scale; height *= scale;
        left = _horizontal < 0 ? anchorX - width : _horizontal > 0 ? anchorX : anchorX - width / 2;
        top = _vertical < 0 ? anchorY - height : _vertical > 0 ? anchorY : anchorY - height / 2;
        SetSelection(new(left, top, width, height));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { if (_dragging) { _dragging = false; ReleaseMouseCapture(); e.Handled = true; } }
    protected override void OnLostMouseCapture(MouseEventArgs e) { _dragging = false; base.OnLostMouseCapture(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        Vector movement = e.Key switch { Key.Left => new(-1, 0), Key.Right => new(1, 0), Key.Up => new(0, -1), Key.Down => new(0, 1), _ => new() };
        if (movement.LengthSquared == 0) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) movement *= 10;
        SetSelection(new(_selection.X + movement.X, _selection.Y + movement.Y, _selection.Width, _selection.Height)); e.Handled = true;
    }
}
