using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;

namespace QuickCapture;

// Lucide SVG primitives become WPF vector geometry, never rasterized fonts.
internal static class IconSet
{
    internal static FrameworkElement Create(string name)
    {
        using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Icons/{name}.svg")).Stream;
        var group = new GeometryGroup();
        foreach (var node in XDocument.Load(stream).Root!.Elements())
        {
            double N(string attribute) => double.Parse((string?)node.Attribute(attribute) ?? "0", CultureInfo.InvariantCulture);
            string D(string attribute) => (string?)node.Attribute(attribute) ?? "";
            Geometry? geometry = node.Name.LocalName switch
            {
                "path" => Geometry.Parse(D("d")),
                "circle" => new EllipseGeometry(new Point(N("cx"), N("cy")), N("r"), N("r")),
                "ellipse" => new EllipseGeometry(new Point(N("cx"), N("cy")), N("rx"), N("ry")),
                "rect" => new RectangleGeometry(new Rect(N("x"), N("y"), N("width"), N("height")), N("rx"), N("rx")),
                "line" => new LineGeometry(new Point(N("x1"), N("y1")), new Point(N("x2"), N("y2"))),
                "polyline" => Geometry.Parse("M " + D("points")),
                "polygon" => Geometry.Parse("M " + D("points") + " Z"),
                _ => null
            };
            if (geometry != null) group.Children.Add(geometry);
        }
        group.Freeze();
        var path = new Path { Data = group, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var canvas = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false }; canvas.Children.Add(path);
        return new Viewbox { Width = UiDesign.Number("IconSize"), Height = UiDesign.Number("IconSize"), Child = canvas, IsHitTestVisible = false };
    }
}
