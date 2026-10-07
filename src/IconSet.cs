using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;

namespace QuickCapture;

// SVG primitives become WPF vector geometry, with per-primitive currentColor
// fill/stroke and opacity. Both outlined Lucide and solid pixel icons share the
// toolbar's Foreground binding for theme, selection and disabled states.
internal static class IconSet
{
    internal static FrameworkElement Create(string name)
    {
        using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Icons/{name}.svg")).Stream;
        var svg = XDocument.Load(stream).Root!;
        var canvas = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false };
        foreach (var node in svg.Elements())
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
            if (geometry == null) continue;
            geometry.Freeze();
            var path = new Path { Data = geometry, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            Binding Foreground() => new("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) };
            string Attribute(string key, string fallback) => (string?)node.Attribute(key) ?? (string?)svg.Attribute(key) ?? fallback;
            if (Attribute("stroke", "currentColor") != "none") path.SetBinding(Shape.StrokeProperty, Foreground());
            if (Attribute("fill", "none") == "currentColor") path.SetBinding(Shape.FillProperty, Foreground());
            path.StrokeThickness = double.Parse(Attribute("stroke-width", "2"), CultureInfo.InvariantCulture);
            path.Opacity = double.Parse(Attribute("opacity", "1"), CultureInfo.InvariantCulture);
            canvas.Children.Add(path);
        }
        return new Viewbox { Width = UiDesign.Number("IconSize"), Height = UiDesign.Number("IconSize"), Child = canvas, IsHitTestVisible = false };
    }
}
