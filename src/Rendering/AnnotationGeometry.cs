using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal static class AnnotationGeometry
{
    internal static Rect Bounds(Annotation a)
    {
        if (a.Tool == AnnotationTool.Text)
        {
            var text = Text(WithPlaceholder(a), Brushes.Black); double inset = TextInset(a); return new(a.Start, new Size(Math.Max(4, a.TextWidth > 0 ? a.TextWidth : text.WidthIncludingTrailingWhitespace + 2 * inset), Math.Max(a.TextHeight, Math.Max(4, Math.Ceiling(text.Height + 2 * inset + 2)))));
        }
        if (a.Points is { Count: > 0 } points)
        {
            double left = points.Min(p => p.X), top = points.Min(p => p.Y);
            return new(left, top, Math.Max(1, points.Max(p => p.X) - left), Math.Max(1, points.Max(p => p.Y) - top));
        }
        return new Rect(a.Start, a.End);
    }
    internal static FormattedText Text(Annotation a, Brush brush)
    {
        var text = Format(a, brush);
        if (a.TextWidth > 0) text.MaxTextWidth = Math.Max(1, a.TextWidth - 2 * TextInset(a));
        text.TextAlignment = a.Alignment; return text;
    }
    private static FormattedText Format(Annotation a, Brush brush)
        => new FormattedText(a.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(a.FontFamily), a.Italic ? FontStyles.Italic : FontStyles.Normal, a.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), a.FontSize, brush, 1);
    internal const string TextPlaceholder = "输入文字…";
    internal static Annotation WithPlaceholder(Annotation a) => string.IsNullOrEmpty(a.Text) ? a with { Text = TextPlaceholder } : a;
    internal static double TextInset(Annotation a)
    {
        var text = Format(WithPlaceholder(a), Brushes.Black);
        var ink = text.BuildGeometry(new Point()).Bounds;
        double overhang = ink.IsEmpty ? 0 : Math.Max(Math.Max(0, -ink.Left), Math.Max(Math.Max(0, -ink.Top), Math.Max(ink.Right - text.WidthIncludingTrailingWhitespace, ink.Bottom - text.Height)));
        return Math.Ceiling(Math.Max(0, overhang)) + 2 + (a.OutlineEnabled ? a.OutlineWidth : 0) + (a.TextFrame is { Enabled: true } frame ? frame.Width + 8 : 0);
    }
    internal static double TextContentHeight(Annotation a) => Bounds(WithPlaceholder(a) with { TextHeight = 0 }).Height;
    internal static double TextMinimumWidth(Annotation a)
    {
        var text = Format(WithPlaceholder(a), Brushes.Black); text.MaxTextWidth = 1;
        return Math.Ceiling(Math.Max(a.FontSize, text.WidthIncludingTrailingWhitespace) + 2 * TextInset(a) + 4);
    }
    internal static void DrawText(DrawingContext dc, Annotation a)
    {
        var brush = new SolidColorBrush(a.Color); var text = Text(a, brush); double inset = TextInset(a); var origin = a.Start + new Vector(inset, inset);
        if (a.TextFrame is { Enabled: true } frame)
        {
            var bounds = Bounds(a); double stroke = Math.Min(frame.Width, Math.Min(bounds.Width, bounds.Height) / 2);
            bounds.Inflate(-stroke / 2, -stroke / 2);
            dc.DrawGeometry(frame.FillColor is { } fill ? new SolidColorBrush(fill) : null, new Pen(new SolidColorBrush(frame.StrokeColor), stroke) { LineJoin = PenLineJoin.Round }, Shape(frame.Shape, bounds));
        }
        if (a.OutlineEnabled) dc.DrawGeometry(null, new Pen(new SolidColorBrush(a.OutlineColor ?? Colors.Black), 2 * a.OutlineWidth) { LineJoin = PenLineJoin.Round }, text.BuildGeometry(origin));
        dc.DrawText(text, origin);
    }
    internal static Geometry Shape(AnnotationShape shape, Rect bounds)
    {
        if (shape == AnnotationShape.Rectangle) return new RectangleGeometry(bounds);
        if (shape == AnnotationShape.RoundedRectangle) { double radius = Math.Min(14, Math.Min(bounds.Width, bounds.Height) / 4); return new RectangleGeometry(bounds, radius, radius); }
        if (shape == AnnotationShape.Ellipse) return new EllipseGeometry(bounds);
        var path = new StreamGeometry();
        using (var c = path.Open())
        {
            c.BeginFigure(new(bounds.Left + bounds.Width / 2, bounds.Top), true, true);
            if (shape == AnnotationShape.Triangle) { c.LineTo(bounds.BottomRight, true, false); c.LineTo(bounds.BottomLeft, true, false); }
            else { c.LineTo(new(bounds.Right, bounds.Top + bounds.Height / 2), true, false); c.LineTo(new(bounds.Left + bounds.Width / 2, bounds.Bottom), true, false); c.LineTo(new(bounds.Left, bounds.Top + bounds.Height / 2), true, false); }
        }
        path.Freeze(); return path;
    }
    internal static Point Constrain(Point start, Point end, Size canvas, bool square)
    {
        end = new(Math.Clamp(end.X, 0, canvas.Width), Math.Clamp(end.Y, 0, canvas.Height));
        if (!square) return end;
        double signX = end.X >= start.X ? 1 : -1, signY = end.Y >= start.Y ? 1 : -1;
        double side = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        side = Math.Min(side, Math.Min(signX > 0 ? canvas.Width - start.X : start.X, signY > 0 ? canvas.Height - start.Y : start.Y));
        return start + new Vector(side * signX, side * signY);
    }
    internal static Annotation Move(Annotation a, Vector delta) => a with { Start = a.Start + delta, End = a.End + delta, Points = a.Points?.Select(p => p + delta).ToArray() };
    internal static Annotation Resize(Annotation a, Rect before, Rect after)
    {
        if (a.Tool == AnnotationTool.Text) return a with { Start = after.TopLeft, End = after.TopLeft, TextWidth = after.Width };
        if (a.Tool == AnnotationTool.Step)
        {
            double side = Math.Clamp(Math.Min(after.Width, after.Height), 16, 240);
            var start = new Point(after.Left, after.Top);
            return a with { Start = start, End = start + new Vector(side, side) };
        }
        Point Map(Point p) => new(after.Left + (p.X - before.Left) * after.Width / Math.Max(1, before.Width), after.Top + (p.Y - before.Top) * after.Height / Math.Max(1, before.Height));
        return a with { Start = Map(a.Start), End = Map(a.End), Points = a.Points?.Select(Map).ToArray() };
    }
}
