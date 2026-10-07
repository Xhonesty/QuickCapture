using System;
using System.IO;
using System.Text.Json;
using System.Drawing;
using System.Linq;

namespace QuickCapture;

internal enum ScreenshotFormat { Png, Jpg, WebP, Bmp }
internal enum RecordingFormat { Mp4, WebM, Gif }
internal enum ExportQuality { Low, Medium, High }

internal static class Paths
{
    internal static string? TestRoot { get; set; }
    public static string Data => Path.Combine(TestRoot ?? AppContext.BaseDirectory, "Data");
    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string DefaultOutput => Path.Combine(TestRoot ?? AppContext.BaseDirectory, "Captures");
    public static string NewCapture(string directory, string extension)
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{(extension is "png" or "jpg" or "webp" or "bmp" ? "截图" : "录屏")}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid().ToString("N")[..4]}.{extension}");
    }
}

internal sealed class Settings
{
    public string OutputDirectory { get; set; } = Paths.DefaultOutput;
    public string ScreenshotHotkey { get; set; } = "Ctrl+Alt+F8";
    public string RecordingHotkey { get; set; } = "Ctrl+Alt+F9";
    public bool SystemAudio { get; set; } = false;
    public bool Microphone { get; set; } = false;
    public string MicrophoneDeviceId { get; set; } = "";
    public string SystemAudioDeviceId { get; set; } = "";
    public bool Cursor { get; set; } = true;
    public bool AutoSaveScreenshot { get; set; } = true;
    public bool SnapToWindow { get; set; } = true;
    public bool SnapRecordingToWindow { get; set; } = true;
    public bool HardwareEncoding { get; set; } = true;
    public int FramesPerSecond { get; set; } = 30;
    public int? Mp4Fps { get; set; }
    public int? WebMFps { get; set; }
    public string Theme { get; set; } = "Dark";
    public ScreenshotFormat ScreenshotFormat { get; set; } = ScreenshotFormat.Png;
    public int ScreenshotQuality { get; set; } = 90;
    public RecordingFormat RecordingFormat { get; set; } = RecordingFormat.Mp4;
    public ExportQuality RecordingQuality { get; set; } = ExportQuality.Medium;
    public int GifFps { get; set; } = 15;
    public string MediaToolsPath { get; set; } = "";
    public string OcrLanguage { get; set; } = "chi_sim+eng";
    public bool OcrMergeLines { get; set; }
    public int StepStart { get; set; } = 1;
    public int StepSize { get; set; } = 40;
    public double PinOpacity { get; set; } = 1;
    public SavedRegion? LastRegion { get; set; }
    public int GetRecordingFps(RecordingFormat format) => format switch
    {
        RecordingFormat.Gif => GifFps,
        RecordingFormat.WebM => WebMFps ?? FramesPerSecond,
        _ => Mp4Fps ?? FramesPerSecond,
    };
    public void SetRecordingFps(RecordingFormat format, int fps)
    {
        if (!RecordingFrameRates.For(format).Contains(fps)) throw new ArgumentException("不支持该格式的帧率。");
        int legacy = FramesPerSecond is 15 or 30 or 60 ? FramesPerSecond : 30;
        Mp4Fps ??= legacy; WebMFps ??= legacy;
        if (format == RecordingFormat.Gif) GifFps = fps;
        else if (format == RecordingFormat.WebM) WebMFps = fps;
        else Mp4Fps = fps;
        FramesPerSecond = GetRecordingFps(RecordingFormat);
    }
    private void NormalizeRecordingFps()
    {
        int legacy = FramesPerSecond is 15 or 30 or 60 ? FramesPerSecond : 30;
        Mp4Fps = Mp4Fps is 15 or 30 or 60 ? Mp4Fps : legacy;
        WebMFps = WebMFps is 15 or 30 or 60 ? WebMFps : legacy;
        int gif = Math.Clamp(GifFps, 5, 30);
        GifFps = RecordingFrameRates.For(RecordingFormat.Gif).OrderBy(fps => Math.Abs(fps - gif)).First();
        FramesPerSecond = GetRecordingFps(RecordingFormat);
    }
    public static Settings Load()
    {
        try
        {
            var s = File.Exists(Paths.SettingsFile) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile)) ?? new() : new();
            if (string.IsNullOrWhiteSpace(s.OutputDirectory)) s.OutputDirectory = Paths.DefaultOutput;
            if (s.Theme is not ("Light" or "Dark")) s.Theme = "Dark";
            if (!Enum.IsDefined(s.ScreenshotFormat)) s.ScreenshotFormat = ScreenshotFormat.Png;
            if (!Enum.IsDefined(s.RecordingFormat)) s.RecordingFormat = RecordingFormat.Mp4;
            if (!Enum.IsDefined(s.RecordingQuality)) s.RecordingQuality = ExportQuality.Medium;
            s.ScreenshotQuality = Math.Clamp(s.ScreenshotQuality, 1, 100);
            s.NormalizeRecordingFps();
            s.NormalizeOffice();
            HotkeyService.Parse(s.ScreenshotHotkey); HotkeyService.Parse(s.RecordingHotkey);
            if (s.ScreenshotHotkey.Equals(s.RecordingHotkey, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("快捷键重复");
            return s;
        }
        catch (Exception ex) { ErrorLog.Write(ex); return new(); }
    }
    public void Save()
    {
        NormalizeRecordingFps();
        NormalizeOffice();
        Directory.CreateDirectory(Paths.Data);
        string temp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, Paths.SettingsFile, true);
    }
    private void NormalizeOffice()
    {
        if (OcrLanguage is not ("chi_sim+eng" or "chi_tra+eng" or "eng")) OcrLanguage = "chi_sim+eng";
        StepStart = Math.Clamp(StepStart, 1, 9999); StepSize = Math.Clamp(StepSize, 16, 160);
        PinOpacity = double.IsFinite(PinOpacity) ? Math.Clamp(PinOpacity, 0.2, 1) : 1;
    }
}

internal static class RecordingFrameRates
{
    internal static int[] For(RecordingFormat format) => format == RecordingFormat.Gif ? new[] { 5, 10, 15, 20, 30 } : new[] { 15, 30, 60 };
    internal static string Label(RecordingFormat format) => format switch { RecordingFormat.WebM => "WebM", RecordingFormat.Gif => "GIF", _ => "MP4" };
}

internal sealed record SavedRegion(int X, int Y, int Width, int Height, string DeviceName)
{
    public Rectangle Rectangle => new(X, Y, Width, Height);
}
