using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Drawing = System.Drawing;

namespace QuickCapture;

internal sealed class RecordingFrame : Window
{
    internal Drawing.Rectangle PhysicalBounds { get; }
    internal bool CaptureExcluded { get; private set; }
    public RecordingFrame(SavedRegion region)
    {
        // The three-pixel frame sits outside the capture; it does not use a
        // full-screen overlay or intercept clicks in the recorded application.
        PhysicalBounds = Drawing.Rectangle.Inflate(region.Rectangle, 3, 3);
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        IsHitTestVisible = false; Focusable = false;
        Width = PhysicalBounds.Width; Height = PhysicalBounds.Height;
        var edge = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(255, 99, 115)) };
        Content = edge;
        SourceInitialized += (_, _) => { Native.MakeClickThrough(this); CaptureExcluded = Native.ExcludeFromCapture(this); };
        Loaded += (_, _) =>
        {
            Native.Place(this, PhysicalBounds, false);
            var dpi = VisualTreeHelper.GetDpi(this);
            edge.BorderThickness = new Thickness(3 / dpi.DpiScaleX, 3 / dpi.DpiScaleY, 3 / dpi.DpiScaleX, 3 / dpi.DpiScaleY);
        };
        DpiChanged += (_, e) => edge.BorderThickness = new Thickness(3 / e.NewDpi.DpiScaleX, 3 / e.NewDpi.DpiScaleY, 3 / e.NewDpi.DpiScaleX, 3 / e.NewDpi.DpiScaleY);
    }
}
