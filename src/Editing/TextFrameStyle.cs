using System;
using System.IO;
using System.Windows.Media;

namespace QuickCapture;

// The paragraph frame is independent of the outline around each glyph.
internal sealed record TextFrameStyle(bool Enabled = false, string Color = "#000000", double Width = 4, string Fill = "none", AnnotationShape Shape = AnnotationShape.Rectangle)
{
    internal Color StrokeColor => (Color)ColorConverter.ConvertFromString(Color);
    internal Color? FillColor => Fill == "none" ? null : (Color)ColorConverter.ConvertFromString(Fill);
    internal void Validate()
    {
        if (!double.IsFinite(Width) || Width < 1 || Width > 200 || Shape is not (AnnotationShape.Rectangle or AnnotationShape.RoundedRectangle)) throw new InvalidDataException("文字边框属性无效。");
        try { _ = StrokeColor; _ = FillColor; } catch { throw new InvalidDataException("文字边框颜色无效。"); }
    }
    internal static string Hex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
