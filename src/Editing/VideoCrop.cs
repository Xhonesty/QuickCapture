using System;
using System.Windows;

namespace QuickCapture;

// Source-pixel coordinates. Align to the chroma grid so the preview and yuv420 export agree.
internal sealed record VideoCrop(int X, int Y, int Width, int Height)
{
    internal Rect Bounds => new(X, Y, Width, Height);
    internal bool IsFullFrame(VideoInfo info) => X == 0 && Y == 0 && Width == info.Width && Height == info.Height;
    internal static VideoCrop FromSelection(Rect selection, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth < 2 || sourceHeight < 2 || selection.IsEmpty || !double.IsFinite(selection.X + selection.Y + selection.Width + selection.Height))
            throw new ArgumentException("视频画面过小或裁剪范围无效。");
        int maxWidth = sourceWidth / 2 * 2, maxHeight = sourceHeight / 2 * 2;
        int width = Math.Clamp((int)Math.Round(selection.Width / 2) * 2, 2, maxWidth);
        int height = Math.Clamp((int)Math.Round(selection.Height / 2) * 2, 2, maxHeight);
        int x = Math.Clamp((int)Math.Round(selection.X / 2) * 2, 0, sourceWidth - width) / 2 * 2;
        int y = Math.Clamp((int)Math.Round(selection.Y / 2) * 2, 0, sourceHeight - height) / 2 * 2;
        return new(x, y, width, height);
    }
    internal void Validate(VideoInfo info)
    {
        if (X < 0 || Y < 0 || Width < 2 || Height < 2 || (long)X + Width > info.Width || (long)Y + Height > info.Height || (X | Y | Width | Height) % 2 != 0)
            throw new ArgumentException("裁剪范围必须位于原视频内，宽高及坐标应为偶数像素。");
    }
}
