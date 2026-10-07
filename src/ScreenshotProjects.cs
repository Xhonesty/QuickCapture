using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;
internal sealed record ScreenshotProject(BitmapSource Original, Annotation[] Annotations, Int32Rect Crop, int NextStep = 1, ScreenshotFormat Format = ScreenshotFormat.Png, int Quality = 90, string? ExportPath = null);
internal static class ScreenshotProjects
{
    private sealed record CropData(int X, int Y, int Width, int Height);
    private sealed record ProjectData(string Owner, int Version, Annotation[] Annotations, CropData Crop, int NextStep, ScreenshotFormat Format, int Quality);
    private sealed record LinkData(string Owner, int Version, string ExportPath, string ProjectId);
    private const string Owner = "QuickCapture.ScreenshotProject";
    internal static string DirectoryPath => Path.Combine(Paths.Data, "ScreenshotProjects");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = CreateJson();
    internal static string LinkFor(string image) => Path.Combine(DirectoryPath, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(image).ToUpperInvariant()))) + ".link.json");
    private static string OwnedProject(string id) => Guid.TryParseExact(id, "N", out var value) ? Path.Combine(DirectoryPath, value.ToString("N") + ".qcap") : throw new InvalidDataException("项目关联无效，已有文件已保留。");
    internal static bool HasProject(string image) => File.Exists(LinkFor(image));
    internal static ScreenshotProject Snapshot(AnnotationSurface surface, int nextStep, ScreenshotFormat format, int quality, string? path = null) => new(surface.Original, surface.Items.Select(a => a with { Points = a.Points?.ToArray() }).ToArray(), surface.CropBounds, nextStep, format, quality, path);
    private static LinkData ReadLink(string path, string? image = null)
    {
        var link = JsonSerializer.Deserialize<LinkData>(File.ReadAllText(path)) ?? throw new InvalidDataException("截图项目关联损坏。");
        if (link.Owner != Owner || link.Version != 1 || image != null && !link.ExportPath.Equals(Path.GetFullPath(image), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("截图项目关联格式不支持，已有文件已保留。");
        _ = OwnedProject(link.ProjectId); return link;
    }
    internal static Task<ScreenshotProject> LoadAsync(string image) => Task.Run(() =>
    {
        if (!File.Exists(image)) throw new FileNotFoundException("导出图片缺失，项目已保留。请恢复图片到原位置再继续编辑。", image);
        if (!HasProject(image))
        {
            var format = FormatFor(image); var original = ImageExportService.Decode(File.ReadAllBytes(image), format);
            return new ScreenshotProject(original, Array.Empty<Annotation>(), new(0, 0, original.PixelWidth, original.PixelHeight), Format: format, ExportPath: null);
        }
        var link = ReadLink(LinkFor(image), image); string path = OwnedProject(link.ProjectId);
        if (!File.Exists(path)) throw new FileNotFoundException("关联截图项目缺失，导出图片已保留；可清理失效关联后作为普通图片打开。", path);
        using var zip = ZipFile.OpenRead(path); var entry = zip.GetEntry("project.json") ?? throw new InvalidDataException("截图项目损坏：缺少项目数据。");
        if (entry.Length > 16*1024*1024) throw new InvalidDataException("截图项目数据过大，已有文件已保留。");
        using var reader = new StreamReader(entry.Open()); var doc = JsonSerializer.Deserialize<ProjectData>(reader.ReadToEnd(), Json) ?? throw new InvalidDataException("截图项目数据损坏。");
        if (doc.Owner != Owner || doc.Version != 1) throw new InvalidDataException("截图项目版本不支持，图片和项目已保留。");
        if (!Enum.IsDefined(doc.Format) || doc.Crop == null) throw new InvalidDataException("截图项目属性损坏，已有文件已保留。");
        var originalEntry = zip.GetEntry("original.png") ?? throw new InvalidDataException("截图项目损坏：缺少原图。");
        if (originalEntry.Length > 256*1024*1024 || doc.Annotations == null || doc.Annotations.Length > 100000) throw new InvalidDataException("截图项目过大或数据无效。");
        using var stream = new MemoryStream(); using (var input = originalEntry.Open()) input.CopyTo(stream);
        var bitmap = ImageExportService.Decode(stream.ToArray(), ScreenshotFormat.Png); var crop = new Int32Rect(doc.Crop.X, doc.Crop.Y, doc.Crop.Width, doc.Crop.Height);
        Validate(doc.Annotations, crop, bitmap.PixelWidth, bitmap.PixelHeight);
        return new ScreenshotProject(bitmap, doc.Annotations, crop, Math.Clamp(doc.NextStep,1,9999), doc.Format, Math.Clamp(doc.Quality,1,100), Path.GetFullPath(image));
    });
    internal static ScreenshotFormat FormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch { ".jpg" or ".jpeg" => ScreenshotFormat.Jpg, ".webp" => ScreenshotFormat.WebP, ".bmp" => ScreenshotFormat.Bmp, _ => ScreenshotFormat.Png };
    private static void Validate(Annotation[] annotations, Int32Rect crop, int width, int height)
    {
        if (crop.X < 0 || crop.Y < 0 || crop.Width < Math.Min(4,width) || crop.Height < Math.Min(4,height) || (long)crop.X+crop.Width>width || (long)crop.Y+crop.Height>height) throw new InvalidDataException("截图项目裁剪范围损坏。");
        bool PointValid(Point p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && Math.Abs(p.X)<=100000 && Math.Abs(p.Y)<=100000;
        foreach (var a in annotations)
            if (!Enum.IsDefined(a.Tool) || a.Tool is AnnotationTool.Crop or AnnotationTool.Select || !Enum.IsDefined(a.Shape) || !Enum.IsDefined(a.Alignment) || !PointValid(a.Start) || !PointValid(a.End) || !double.IsFinite(a.Width) || a.Width<=0 || a.Width>1000 || !double.IsFinite(a.FontSize) || a.FontSize<=0 || a.FontSize>1000 || !double.IsFinite(a.TextWidth) || a.TextWidth<0 || a.TextWidth>100000 || a.Points?.Any(p=>!PointValid(p)) == true || a.Text==null || a.Text.Length>100000 || string.IsNullOrWhiteSpace(a.FontFamily)) throw new InvalidDataException("截图项目标注数据损坏。");
    }
    internal static async Task SaveAsync(ScreenshotProject project, BitmapSource rendered, string image, ScreenshotFormat format, int quality, bool overwrite = false)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(() => SaveCore(project, rendered, image, format, quality, overwrite)).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }
    private static void SaveCore(ScreenshotProject project, BitmapSource rendered, string image, ScreenshotFormat format, int quality, bool overwrite)
    {
        image = Path.GetFullPath(image); Validate(project.Annotations, project.Crop, project.Original.PixelWidth, project.Original.PixelHeight);
        string linkPath = LinkFor(image); LinkData? oldLink = File.Exists(linkPath) ? ReadLink(linkPath, image) : null;
        // Inspect supported versions before replacing any existing valid project.
        if (oldLink != null && File.Exists(OwnedProject(oldLink.ProjectId))) { using var previousZip = ZipFile.OpenRead(OwnedProject(oldLink.ProjectId)); using var previousReader = new StreamReader((previousZip.GetEntry("project.json") ?? throw new InvalidDataException("旧项目损坏，文件已保留。")).Open()); var previousDoc = JsonSerializer.Deserialize<ProjectData>(previousReader.ReadToEnd(), Json); if (previousDoc?.Version != 1 || previousDoc.Owner != Owner) throw new InvalidDataException("旧项目版本不支持，文件已保留。"); }
        if (File.Exists(image) && !overwrite) throw new IOException("目标图片已存在。");
        Directory.CreateDirectory(DirectoryPath); Directory.CreateDirectory(Path.GetDirectoryName(image)!);
        string id = Guid.NewGuid().ToString("N"), projectPath = OwnedProject(id), temporaryProject = projectPath + ".partial", temporaryImage = image + "." + id + ".partial", backupImage = image + "." + id + ".backup";
        bool imageCommitted = false, linked = false, backedUp = false, rollbackFailed = false;
        try
        {
            var crop = project.Crop; var data = new ProjectData(Owner, 1, project.Annotations, new(crop.X,crop.Y,crop.Width,crop.Height), project.NextStep, format, quality);
            using (var zip = ZipFile.Open(temporaryProject, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("project.json").Open())) writer.Write(JsonSerializer.Serialize(data,Json));
                using var original = zip.CreateEntry("original.png",CompressionLevel.NoCompression).Open(); original.Write(ImageExportService.Encode(project.Original,ScreenshotFormat.Png,100));
            }
            File.WriteAllBytes(temporaryImage,ImageExportService.Encode(rendered,format,quality));
            File.Move(temporaryProject,projectPath);
            if (File.Exists(image)) { File.Copy(image,backupImage,false); backedUp=true; }
            File.Move(temporaryImage,image,overwrite); imageCommitted=true;
            ImageExportService.AtomicWrite(linkPath,JsonSerializer.SerializeToUtf8Bytes(new LinkData(Owner,1,image,id)),true); linked=true;
        }
        catch
        {
            if (imageCommitted && !linked)
            {
                try { if (backedUp) File.Move(backupImage,image,true); else File.Delete(image); }
                catch (Exception ex) { rollbackFailed = true; throw new IOException("保存失败，旧图片备份已保留：" + backupImage, ex); }
            }
            throw;
        }
        finally
        {
            foreach (string file in new[]{temporaryImage,temporaryProject}) if(File.Exists(file)) File.Delete(file);
            if (!linked) { if(File.Exists(projectPath)) File.Delete(projectPath); }
            if (!rollbackFailed && File.Exists(backupImage)) File.Delete(backupImage);
        }
        if (oldLink != null) { try { File.Delete(OwnedProject(oldLink.ProjectId)); } catch(IOException ex) { ErrorLog.Write(ex); } }
    }
    internal static IEnumerable<string> AssociatedImages()
    {
        if (!Directory.Exists(DirectoryPath)) yield break;
        foreach (string file in Directory.EnumerateFiles(DirectoryPath,"*.link.json"))
        { LinkData? link = null; try { link = ReadLink(file); if (LinkFor(link.ExportPath) != file) link=null; } catch { } if(link != null && File.Exists(link.ExportPath)) yield return link.ExportPath; }
    }
    internal static void Clean(string image)
    {
        string path=LinkFor(image); if(!File.Exists(path)) return; var link=ReadLink(path,image); string project=OwnedProject(link.ProjectId);
        // The association and GUID path are both validated; imported images are never removed here.
        if (File.Exists(project)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(project,Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }
    private static JsonSerializerOptions CreateJson()
    { var options = new JsonSerializerOptions(); options.Converters.Add(new PointJson()); options.Converters.Add(new ColorJson()); return options; }
    private sealed class PointJson : JsonConverter<Point>
    {
        public override Point Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) { using var doc=JsonDocument.ParseValue(ref reader); return new(doc.RootElement.GetProperty("x").GetDouble(),doc.RootElement.GetProperty("y").GetDouble()); }
        public override void Write(Utf8JsonWriter writer,Point value,JsonSerializerOptions options) { writer.WriteStartObject();writer.WriteNumber("x",value.X);writer.WriteNumber("y",value.Y);writer.WriteEndObject(); }
    }
    private sealed class ColorJson : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options) => (Color)ColorConverter.ConvertFromString(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer,Color value,JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
