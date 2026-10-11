using System;
using System.IO;
using System.Text.Json;
using System.Drawing;
using System.Linq;

namespace QuickCapture;

internal enum ScreenshotFormat { Png, Jpg, WebP, Bmp }
internal enum RecordingFormat { Mp4, WebM, Gif }
internal enum ExportQuality { Low, Medium, High }
internal enum RecordingStartMode { Automatic, Manual }
