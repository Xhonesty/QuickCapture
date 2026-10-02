using System;
using System.IO;
using System.Text.Json;
using System.Drawing;

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
    public string ScreenshotHotkey { get; set; } = "Ctrl+Alt+S";
    public string RecordingHotkey { get; set; } = "Ctrl+Alt+R";
    public bool SystemAudio { get; set; } = false;
    public bool Microphone { get; set; } = false;
    public bool Cursor { get; set; } = true;
    public bool AutoSaveScreenshot { get; set; } = true;
    public bool SnapToWindow { get; set; } = true;
    public bool SnapRecordingToWindow { get; set; } = true;
    public bool HardwareEncoding { get; set; } = true;
    public int FramesPerSecond { get; set; } = 30;
    public string Theme { get; set; } = "Dark";
    public ScreenshotFormat ScreenshotFormat { get; set; } = ScreenshotFormat.Png;
    public int ScreenshotQuality { get; set; } = 90;
    public RecordingFormat RecordingFormat { get; set; } = RecordingFormat.Mp4;
    public ExportQuality RecordingQuality { get; set; } = ExportQuality.Medium;
    public int GifFps { get; set; } = 15;
    public string MediaToolsPath { get; set; } = "";
    public SavedRegion? LastRegion { get; set; }
    public static Settings Load()
    {
        try
        {
            var s = File.Exists(Paths.SettingsFile) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile)) ?? new() : new();
            if (string.IsNullOrWhiteSpace(s.OutputDirectory)) s.OutputDirectory = Paths.DefaultOutput;
            if (s.FramesPerSecond is not (15 or 30 or 60)) s.FramesPerSecond = 30;
            if (s.Theme is not ("Light" or "Dark")) s.Theme = "Dark";
            if (!Enum.IsDefined(s.ScreenshotFormat)) s.ScreenshotFormat = ScreenshotFormat.Png;
            if (!Enum.IsDefined(s.RecordingFormat)) s.RecordingFormat = RecordingFormat.Mp4;
            if (!Enum.IsDefined(s.RecordingQuality)) s.RecordingQuality = ExportQuality.Medium;
            s.ScreenshotQuality = Math.Clamp(s.ScreenshotQuality, 1, 100);
            s.GifFps = Math.Clamp(s.GifFps, 5, 30);
            HotkeyService.Parse(s.ScreenshotHotkey); HotkeyService.Parse(s.RecordingHotkey);
            if (s.ScreenshotHotkey.Equals(s.RecordingHotkey, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("快捷键重复");
            return s;
        }
        catch (Exception ex) { ErrorLog.Write(ex); return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Paths.Data);
        string temp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, Paths.SettingsFile, true);
    }
}

internal sealed record SavedRegion(int X, int Y, int Width, int Height, string DeviceName)
{
    public Rectangle Rectangle => new(X, Y, Width, Height);
}
