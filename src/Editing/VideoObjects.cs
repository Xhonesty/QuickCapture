using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace QuickCapture;

internal enum VideoObjectKind { Rectangle, RoundedRectangle, Ellipse, Arrow, Brush, Mosaic, PictureInPicture, Text, Line }
internal sealed record VideoPoint(double X, double Y);
// Original-pixel coordinates and half-open, original-frame time intervals.
internal sealed record VideoObject(string Id, VideoObjectKind Kind, double X, double Y, double Width, double Height,
    double Start, double End, int Layer, string Color = "#FF0000", double Stroke = 4, string Fill = "none", double Opacity = 1,
    VideoPoint[]? Points = null, int BlockSize = 16, string? Asset = null, bool AssetVideo = false, int AssetWidth = 0, int AssetHeight = 0,
    double AssetDuration = 0, double AssetStart = 0, bool ReverseX = false, bool ReverseY = false,
    string Text = "", string FontFamily = "Microsoft YaHei UI", double FontSize = 32, bool Bold = false, TextAlignment Alignment = TextAlignment.Left,
    bool StrokeEnabled = true, double? StrokeOpacity = null, double? FillOpacity = null,
    bool Italic = false, bool OutlineEnabled = false, string OutlineColor = "#000000", double OutlineWidth = 4, double? OutlineOpacity = null, bool MosaicBrush = false, TextFrameStyle? TextFrame = null, string? Name = null)
{
    [JsonIgnore] internal Rect Bounds => new(X, Y, Width, Height);
    [JsonIgnore] internal double StrokeAlpha => StrokeOpacity ?? Opacity;
    [JsonIgnore] internal double FillAlpha => FillOpacity ?? Opacity;
    [JsonIgnore] internal double OutlineAlpha => OutlineOpacity ?? Opacity;
    [JsonIgnore] internal string Label => Name ?? (Kind switch { VideoObjectKind.Rectangle => "矩形", VideoObjectKind.RoundedRectangle => "圆角矩形", VideoObjectKind.Ellipse => "椭圆", VideoObjectKind.Arrow => "箭头", VideoObjectKind.Line => "线条", VideoObjectKind.Brush => "画笔", VideoObjectKind.Mosaic => "马赛克", VideoObjectKind.Text => "文字", _ => AssetVideo ? "视频小窗" : "图片小窗" });
    internal Annotation TextAnnotation() => new(AnnotationTool.Text, new(0, 0), new(0, 0), (Color)ColorConverter.ConvertFromString(Color), Text, FontFamily: FontFamily, FontSize: FontSize, Bold: Bold, Alignment: Alignment, TextWidth: Width, Italic: Italic, OutlineEnabled: OutlineEnabled, OutlineColor: (Color)ColorConverter.ConvertFromString(OutlineColor), OutlineWidth: OutlineWidth, TextFrame: TextFrame, TextHeight: Height);
    internal VideoObject FitText(VideoInfo info)
    {
        if (Kind != VideoObjectKind.Text) return this;
        return VideoTextLayout.Fit(this, info);
    }
    internal bool Active(double time, double fps)
    {
        long frame = (long)Math.Floor(time * fps + .000001);
        bool active = frame >= (long)Math.Round(Start * fps) && frame < (long)Math.Ceiling(End * fps - .000001);
        return active && (!AssetVideo || AssetStart + Math.Max(0, frame / fps - Start) < AssetDuration - .000001);
    }
    internal Annotation Annotation()
    {
        var tool = Kind == VideoObjectKind.Brush ? AnnotationTool.Freehand : Kind == VideoObjectKind.Arrow ? AnnotationTool.Arrow : AnnotationTool.Rectangle;
        Point start = new(ReverseX ? X + Width : X, ReverseY ? Y + Height : Y), end = new(ReverseX ? X : X + Width, ReverseY ? Y : Y + Height);
        return new(tool, start, end, (Color)ColorConverter.ConvertFromString(Color), Points: Points?.Select(p => new Point(p.X, p.Y)).ToArray(), Width: Stroke,
            Shape: Kind == VideoObjectKind.Ellipse ? AnnotationShape.Ellipse : Kind == VideoObjectKind.RoundedRectangle ? AnnotationShape.RoundedRectangle : AnnotationShape.Rectangle);
    }
    internal VideoObject Transform(Rect after)
    {
        if (Kind == VideoObjectKind.Text) return this with { X = after.X, Y = after.Y, Width = after.Width, Height = after.Height };
        var a = AnnotationGeometry.Resize(Annotation(), Bounds, after);
        return this with { X = after.X, Y = after.Y, Width = after.Width, Height = after.Height, Points = a.Points?.Select(p => new VideoPoint(p.X, p.Y)).ToArray() };
    }
    internal void Validate(VideoInfo info)
    {
        TextFrame?.Validate();
        if (Name is { Length: > 64 }) throw new InvalidDataException("对象名称过长。");
        if (!Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(Id) || Id.Length > 100 || !double.IsFinite(X + Y + Width + Height + Start + End + Stroke + Opacity + AssetStart + AssetDuration)
            || X < 0 || Y < 0 || Width < 1 || Height < 1 || X + Width > info.Width + .01 || Y + Height > info.Height + .01
            || Start < 0 || End > info.Duration + .001 || End <= Start || Opacity < 0 || Opacity > 1 || Stroke < 1 || Stroke > 200 || BlockSize < 2 || BlockSize > 128)
            throw new InvalidDataException("视频对象的坐标、时间或属性无效，项目已保留。");
        if (!double.IsFinite(StrokeAlpha + FillAlpha + OutlineAlpha + OutlineWidth) || StrokeAlpha < 0 || StrokeAlpha > 1 || FillAlpha < 0 || FillAlpha > 1 || OutlineAlpha < 0 || OutlineAlpha > 1 || OutlineWidth < 1 || OutlineWidth > 200) throw new InvalidDataException("填充或描边属性无效。");
        if (Math.Abs(VideoCuts.Snap(Start, info.Fps, info.Duration) - Start) > .00001 || Math.Abs(VideoCuts.Snap(End, info.Fps, info.Duration) - End) > .00001) throw new InvalidDataException("对象时间应按原片帧率对齐。");
        try { _ = ColorConverter.ConvertFromString(Color); _ = ColorConverter.ConvertFromString(OutlineColor); if (Fill != "none") _ = ColorConverter.ConvertFromString(Fill); } catch { throw new InvalidDataException("对象颜色无效。"); }
        if ((Kind == VideoObjectKind.Brush || Kind == VideoObjectKind.Mosaic && MosaicBrush) && (Points is not { Length: > 0 and <= 100000 } || Points.Any(p => !double.IsFinite(p.X + p.Y) || p.X < 0 || p.Y < 0 || p.X > info.Width || p.Y > info.Height))) throw new InvalidDataException("画笔路径无效。");
        if (Kind == VideoObjectKind.PictureInPicture && (Asset == null || Asset is "." or ".." || Asset != Path.GetFileName(Asset) || Asset.Contains(':') || AssetWidth < 1 || AssetHeight < 1 || (long)AssetWidth * AssetHeight > 33_177_600 || AssetStart < 0 || AssetVideo && (AssetDuration <= 0 || AssetStart >= AssetDuration))) throw new InvalidDataException("画中画素材配置无效。");
        if (Kind == VideoObjectKind.Text && (Text == null || Text.Length > 10000 || string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 200 || !double.IsFinite(FontSize) || FontSize < 4 || FontSize > 300 || !Enum.IsDefined(Alignment))) throw new InvalidDataException("文字内容、字体或样式无效。");
    }
    public override string ToString() => $"{Label} · {Start:F2}–{End:F2} 秒";
}
internal static class VideoObjectRules
{
    internal static VideoObject[] Validate(IEnumerable<VideoObject>? objects, VideoInfo info)
    {
        var items = (objects ?? Array.Empty<VideoObject>()).ToArray();
        if (items.Length > 500 || items.Select(o => o.Id).Distinct().Count() != items.Length) throw new InvalidDataException("对象数量或标识无效。");
        foreach (var item in items) item.Validate(info);
        return items.OrderBy(o => o.Layer).ToArray();
    }
    internal static double SourceTime(double outputTime, RecordingEditState state)
    {
        return VideoTiming.SourceTime(outputTime, state);
    }
}
