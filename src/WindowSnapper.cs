using System;
using System.Collections.Generic;
using System.Drawing;

namespace QuickCapture;

internal sealed record WindowTarget(IntPtr Handle, string Title, Rectangle Bounds);

internal static class WindowSnapper
{
    // Snapshot order preserves overlapping windows' Z order. Clip spanning windows
    // to the selected monitor, matching the single-monitor region editor.
    internal static SavedRegion? Find(Point point, Rectangle screen, string deviceName, IReadOnlyList<WindowTarget> targets)
    {
        if (!screen.Contains(point)) return null;
        foreach (var target in targets)
        {
            if (!target.Bounds.Contains(point)) continue;
            var clipped = Rectangle.Intersect(target.Bounds, screen);
            if (clipped.Width >= 4 && clipped.Height >= 4)
                return new(clipped.X, clipped.Y, clipped.Width, clipped.Height, deviceName);
        }
        return null;
    }
}
