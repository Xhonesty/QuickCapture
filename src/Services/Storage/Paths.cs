using System;
using System.IO;
using System.Text.Json;
using System.Drawing;
using System.Linq;

namespace QuickCapture;

internal static class Paths
{
    internal static string? TestRoot { get; set; }
    internal const string InstalledMarker = "installed-mode";
    internal static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, InstalledMarker));
    internal static (string Data, string Output) ResolveStorage(string applicationDirectory, bool installed, string localAppData, string pictures, string? testRoot = null)
    {
        if (testRoot != null) return (Path.Combine(testRoot, "Data"), Path.Combine(testRoot, "Captures"));
        if (!installed) return (Path.Combine(applicationDirectory, "Data"), Path.Combine(applicationDirectory, "Captures"));
        string userRoot = Path.Combine(localAppData, "QuickCapture");
        return (Path.Combine(userRoot, "Data"), string.IsNullOrWhiteSpace(pictures) ? Path.Combine(userRoot, "Captures") : Path.Combine(pictures, "QuickCapture"));
    }
    private static (string Data, string Output) Storage => ResolveStorage(AppContext.BaseDirectory, IsInstalled,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), TestRoot);
    public static string Data => Storage.Data;
    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string DefaultOutput => Storage.Output;
    public static string NewCapture(string directory, string extension)
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{(extension is "png" or "jpg" or "webp" or "bmp" ? "截图" : "录屏")}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid().ToString("N")[..4]}.{extension}");
    }
}
