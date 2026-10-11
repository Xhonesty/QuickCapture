using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickCapture;

// The same complete color entry (including interaction states) in both editors.
internal sealed class ColorDisplay : Button
{
    private sealed class Swatch : FrameworkElement
    {
        internal Color? Color;
        protected override void OnRender(DrawingContext dc)
        {
            var rect = new Rect(RenderSize);
            dc.PushClip(new RectangleGeometry(rect, 3, 3));
            dc.DrawRectangle(ColorPalette.CheckerBrush, null, rect);
            if (Color is { } color) dc.DrawRectangle(new SolidColorBrush(color), null, rect);
            else dc.DrawLine(new Pen(Brushes.IndianRed, 2), new(2, rect.Bottom - 2), new(rect.Right - 2, 2));
            dc.Pop();
        }
    }
    private readonly Swatch _swatch = new() { IsHitTestVisible = false };
    internal Color? Value => _swatch.Color;
    internal ColorDisplay(string label, Action action)
    {
        Style = (Style)Application.Current.FindResource("ColorDisplayButton");
        Content = _swatch; ToolTip = label + " · 调色盘 / 吸管";
        AutomationProperties.SetName(this, label);
        Click += (_, _) => action();
    }
    internal void SetColor(Color? color)
    {
        _swatch.Color = color; _swatch.InvalidateVisual();
        AutomationProperties.SetHelpText(this, color is { } value ? AnnotationColors.Hex(value) : "无填充");
    }
}
