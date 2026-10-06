using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Tesseract;

namespace QuickCapture;

internal enum OcrState { Ready, Recognizing, Success, NoText, MissingLanguage, Failed, Cancelled }
internal sealed record OcrResult(OcrState State, string Text = "", string Message = "");

internal static class OcrService
{
    private static readonly ConcurrentDictionary<string, Process> Active = new();
    internal static string ModelDirectory => Path.Combine(AppContext.BaseDirectory, "Tools", "ocr", "tessdata");
    internal static string MergeLines(string text)
    {
        // Preserve Latin word boundaries; CJK paragraphs do not gain spaces.
        string joined = Regex.Replace(text.Trim(), @"\s*[\r\n]+\s*", " ");
        return Regex.Replace(joined, @"(?<=[\u3400-\u9fff\uff00-\uffef]) +(?=[\u3400-\u9fff\uff00-\uffef])", "");
    }
    internal static string? MissingLanguage(string directory, string language)
    {
        if (language is not ("chi_sim+eng" or "chi_tra+eng" or "eng")) return "不支持的识别语言。";
        var missing = language.Split('+').Where(name => !File.Exists(Path.Combine(directory, name + ".traineddata"))).ToArray();
        return missing.Length == 0 ? null : "缺少本地语言组件：" + string.Join("、", missing) + "。请重新解压完整便携包；源码构建请运行 tools/setup-ocr.py。";
    }
    internal static async Task<OcrResult> RecognizeAsync(BitmapSource image, string language, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (MissingLanguage(ModelDirectory, language) is { } missing) return new(OcrState.MissingLanguage, Message: missing);
        string job = Path.Combine(Paths.Data, "OcrJobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        string input = Path.Combine(job, "input.png"), output = Path.Combine(job, "result.json");
        using var process = new Process();
        bool started = false;
        try
        {
            await Task.Run(() => ImageExportService.Save(image, input, ScreenshotFormat.Png, 100), cancellation);
            cancellation.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            foreach (string argument in new[] { "--ocr-worker", input, output, ModelDirectory, language }) start.ArgumentList.Add(argument);
            process.StartInfo = start;
            if (!process.Start()) throw new InvalidOperationException("无法启动本地文字识别。");
            started = true;
            Active[job] = process;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using var killOnCancel = timeout.Token.Register(() => Kill(process));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("文字识别超时，请裁剪较小的范围后重试。"); }
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(output)) throw new InvalidOperationException("识别进程未返回结果。请检查 Visual C++ x64 运行库及完整便携包。");
            return JsonSerializer.Deserialize<OcrResult>(await File.ReadAllTextAsync(output, cancellation)) ?? new(OcrState.Failed, Message: "识别结果无效。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ErrorLog.Write(ex); return new(OcrState.Failed, Message: ex.Message); }
        finally
        {
            Active.TryRemove(job, out _);
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                catch (Exception ex) { ErrorLog.Write(ex); }
            }
            try { if (Directory.Exists(job)) Directory.Delete(job, recursive: true); } catch (Exception ex) { ErrorLog.Write(ex); }
        }
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // The worker may have just exited.
        catch (Exception ex) { ErrorLog.Write(ex); }
    }
    internal static void CancelActive()
    {
        // On application exit the dispatcher will no longer run async finally
        // blocks. Reap workers synchronously so they cannot outlive the app.
        foreach (var (directory, process) in Active)
        {
            Kill(process);
            try { if (process.WaitForExit(3000) && Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }
    }
    // An isolated process owns the native engine. Cancellation kills only this
    // process and waits for exit before removing its private input/result files.
    internal static int RunWorker(string[] args)
    {
        if (args.Length != 5) return 2;
        OcrResult result;
        try
        {
            string input = args[1], output = args[2], models = args[3], language = args[4];
            if (MissingLanguage(models, language) is { } missing) result = new(OcrState.MissingLanguage, Message: missing);
            else
            {
                using var engine = new TesseractEngine(models, language, EngineMode.LstmOnly);
                engine.SetVariable("preserve_interword_spaces", "1");
                using var pixels = Pix.LoadFromFile(input);
                using var page = engine.Process(pixels, PageSegMode.Auto);
                string text = page.GetText().Replace("\r\n", "\n").Trim();
                result = string.IsNullOrWhiteSpace(text) ? new(OcrState.NoText, Message: "当前范围未识别到文字。请尝试更清晰或更大的文字区域。") : new(OcrState.Success, text);
            }
            File.WriteAllText(output, JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            result = new(OcrState.Failed, Message: "本地识别失败：" + ex.Message);
            try { File.WriteAllText(args[2], JsonSerializer.Serialize(result)); } catch { }
            return 1;
        }
    }
}
