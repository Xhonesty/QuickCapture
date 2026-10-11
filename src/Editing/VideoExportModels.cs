using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuickCapture;

internal sealed record VideoInfo(double Duration, int Width, int Height, double Fps, bool HasAudio, string VideoCodec = "", string AudioCodec = "");
internal sealed record VideoExportRequest(string Source, string Destination, RecordingFormat Format, ExportQuality Quality,
    double Start, double End, double Speed = 1, bool Mute = false, int GifFps = 15, ExportQuality SourceQuality = ExportQuality.Medium, bool Overwrite = false, VideoCrop? Crop = null, int? FramesPerSecond = null, IReadOnlyList<VideoRange>? Deleted = null, IReadOnlyList<VideoObject>? Objects = null, IReadOnlyList<VideoSpeedRange>? SpeedRanges = null)
{
    internal double OutputDuration => VideoTiming.Segments(Start, End, Deleted, SpeedRanges, Speed).Sum(r => r.Duration);
    internal bool OriginalMp4(VideoInfo info) => (Objects == null || Objects.Count == 0) && (Deleted == null || Deleted.Count == 0) && (SpeedRanges == null || SpeedRanges.Count == 0) && Format == RecordingFormat.Mp4 && (FramesPerSecond == null || Math.Abs(FramesPerSecond.Value - info.Fps) < 0.001) && (Crop == null || Crop.IsFullFrame(info)) && Start <= 0.0001 && End >= info.Duration - 0.0001 && Speed == 1 && !Mute && Quality == SourceQuality && Path.GetExtension(Source).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
}
