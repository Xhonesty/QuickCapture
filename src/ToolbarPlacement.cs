using System;
using System.Windows;

namespace QuickCapture;

internal static class ToolbarPlacement
{
    // All values are monitor-local DIPs. Prefer free space outside the selection.
    public static Point Place(Rect selection, Size toolbar, Rect available)
    {
        const double gap = 12;
        double ClampX(double x) => Math.Clamp(x, available.Left, Math.Max(available.Left, available.Right - toolbar.Width));
        double ClampY(double y) => Math.Clamp(y, available.Top, Math.Max(available.Top, available.Bottom - toolbar.Height));
        double x = ClampX(selection.Right - toolbar.Width);
        if (selection.Bottom + gap + toolbar.Height <= available.Bottom) return new(x, selection.Bottom + gap);
        if (selection.Top - gap - toolbar.Height >= available.Top) return new(x, selection.Top - gap - toolbar.Height);
        if (selection.Right + gap + toolbar.Width <= available.Right) return new(selection.Right + gap, ClampY(selection.Top));
        if (selection.Left - gap - toolbar.Width >= available.Left) return new(selection.Left - gap - toolbar.Width, ClampY(selection.Top));
        // A full-screen selection has no outside space; export still excludes UI.
        return new(x, ClampY(selection.Bottom - toolbar.Height - gap));
    }
}
