using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QuickCapture;

internal static class VideoComposedExport
{
    internal static Task RunAsync(VideoExportRequest request, VideoInfo info, MediaCapabilities tools, string temp, IProgress<double>? progress, CancellationToken token) => Task.Run(async () =>
    {
        var start = new ProcessStartInfo(tools.Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in VideoExportService.Arguments(request, info, temp, true)) start.ArgumentList.Add(argument);
        using var encoder = Process.Start(start)!;
        using var cancel = token.Register(() => { try { if (!encoder.HasExited) encoder.Kill(true); } catch (InvalidOperationException) { } });
        var errors = encoder.StandardError.ReadToEndAsync();
        async Task ReadProgress()
        {
            while (await encoder.StandardOutput.ReadLineAsync() is { } line)
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && double.TryParse(line[12..], CultureInfo.InvariantCulture, out double us) && request.OutputDuration > 0) progress?.Report(Math.Clamp(us / 1_000_000 / request.OutputDuration, 0, .95));
        }
        var reading = ReadProgress();
        using var frames = new VideoFrameReader(tools.Ffmpeg!, request.Source, info.Width, info.Height, info.Fps);
        using var assets = new VideoObjectFrames(request.Source, tools.Ffmpeg!, info.Fps);
        try
        {
            for (long index = 0; index / info.Fps < request.End - .000001; index++)
            {
                token.ThrowIfCancellationRequested(); double time = index / info.Fps;
                var frame = await frames.FrameAsync(time, token); if (frame == null) break;
                var material = await assets.AtAsync(request.Objects!, time, token);
                VideoObjectRenderer.Render(frame, info, request.Objects!, time, material);
                try { await encoder.StandardInput.BaseStream.WriteAsync(frame, token); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 109 or 232)
                {
                    // Trim/fps filters can finish the output before consuming every
                    // source frame. Check the encoder's exit status below; a closed
                    // input pipe alone does not mean that encoding failed.
                    break;
                }
            }
            encoder.StandardInput.Close(); await encoder.WaitForExitAsync(token); await reading;
            if (encoder.ExitCode != 0) throw new InvalidOperationException("效果导出失败，原片和项目已保留：" + await errors);
        }
        catch
        {
            if (!encoder.HasExited) encoder.Kill(true);
            await encoder.WaitForExitAsync(CancellationToken.None); await reading;
            token.ThrowIfCancellationRequested();
            string error = await errors;
            if (error.Length > 0) throw new InvalidOperationException("效果导出失败，原片和项目已保留：" + error);
            throw;
        }
    }, token);
}
