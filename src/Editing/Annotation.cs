using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

// Keep the original enum values and Rectangle record representation compatible.
internal enum AnnotationTool { Arrow, Rectangle, Text, Mosaic, Freehand, MosaicBrush, Crop, Select, Step }
internal enum AnnotationShape { Rectangle, RoundedRectangle, Ellipse, Triangle, Diamond }
internal sealed record Annotation(AnnotationTool Tool, Point Start, Point End, Color Color, string Text = "", IReadOnlyList<Point>? Points = null, double Width = 3, AnnotationShape Shape = AnnotationShape.Rectangle, Color? FillColor = null,
    string FontFamily = "Microsoft YaHei UI", double FontSize = 24, bool Bold = false, TextAlignment Alignment = TextAlignment.Left, double TextWidth = 0, int StepNumber = 1,
    bool Italic = false, bool OutlineEnabled = false, Color? OutlineColor = null, double OutlineWidth = 4, bool StrokeEnabled = true, int BlockSize = 14, TextFrameStyle? TextFrame = null, double TextHeight = 0);
