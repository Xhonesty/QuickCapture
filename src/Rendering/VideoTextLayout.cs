using System;
using System.Windows;

namespace QuickCapture;

internal static class VideoTextLayout
{
    internal static double RequiredHeight(VideoObject item) => AnnotationGeometry.TextContentHeight(item.TextAnnotation());
    internal static double MinimumWidth(VideoObject item) => AnnotationGeometry.TextMinimumWidth(item.TextAnnotation());
    internal static VideoObject Fit(VideoObject item, VideoInfo info)
    {
        double width = Math.Clamp(item.Width, Math.Min(info.Width, MinimumWidth(item)), info.Width);
        // A narrower paragraph must still fit vertically; retain font size and
        // refuse a wrap width which would hide lines beyond the frame.
        if (RequiredHeight(item with { Width = width }) > info.Height)
        {
            double low = width, high = info.Width;
            for (int i = 0; i < 20; i++) { double mid = (low + high) / 2; if (RequiredHeight(item with { Width = mid }) > info.Height) low = mid; else high = mid; }
            width = high;
        }
        double height = Math.Min(info.Height, Math.Max(item.Height, RequiredHeight(item with { Width = width })));
        return item with { Width = width, Height = height, X = Math.Clamp(item.X, 0, info.Width - width), Y = Math.Clamp(item.Y, 0, info.Height - height) };
    }
    internal static VideoObject Resize(VideoObject before, (int X, int Y) handle, Vector delta, VideoInfo info, Rect? view = null)
    {
        if (view is { IsEmpty: false, Width: > 0, Height: > 0 } frame && frame != new Rect(0, 0, info.Width, info.Height))
        {
            frame.Intersect(new Rect(0, 0, info.Width, info.Height));
            if (!frame.IsEmpty && frame.Width >= 1 && frame.Height >= 1)
            {
                var localInfo = info with { Width = (int)frame.Width, Height = (int)frame.Height };
                var local = Fit(before with { X = before.X - frame.X, Y = before.Y - frame.Y }, localInfo);
                var resized = Resize(local, handle, delta, localInfo);
                return resized with { X = resized.X + frame.X, Y = resized.Y + frame.Y };
            }
        }
        double minimum = Math.Min(info.Width, MinimumWidth(before));
        double left = before.X, right = before.X + before.Width, top = before.Y, bottom = before.Y + before.Height;
        if (handle.X < 0) left = Math.Clamp(left + delta.X, 0, Math.Max(0, right - minimum));
        if (handle.X > 0) right = Math.Clamp(right + delta.X, Math.Min(info.Width, left + minimum), info.Width);
        var fitted = Fit(before with { X = left, Width = right - left }, info);
        double minHeight = Math.Min(info.Height, RequiredHeight(fitted));
        if (handle.Y < 0) top = Math.Clamp(top + delta.Y, 0, Math.Max(0, bottom - minHeight));
        if (handle.Y > 0) bottom = Math.Clamp(bottom + delta.Y, Math.Min(info.Height, top + minHeight), info.Height);
        double height = Math.Max(minHeight, bottom - top);
        if (handle.Y < 0) top = bottom - height;
        if (handle.X < 0) left = right - fitted.Width; else left = fitted.X;
        return Fit(fitted with { X = Math.Max(0, left), Y = Math.Max(0, top), Height = height }, info);
    }
}
