using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuickCapture;

internal sealed record VideoRange(double Start, double End)
{ public override string ToString() => $"删除 {Start:F3} – {End:F3} 秒"; }
internal sealed record RecordingEditState(double Start, double End, VideoRange[] Deleted, double Speed = 1, bool Mute = false, VideoCrop? Crop = null, RecordingFormat Format = RecordingFormat.Mp4, ExportQuality Quality = ExportQuality.Medium, int Fps = 30, VideoObject[]? Objects = null, VideoSpeedRange[]? SpeedRanges = null, bool SnapEnabled = true, VideoObject[]? ToolDefaults = null);
