using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace QuickCapture;

internal sealed record AudioDeviceChoice(string Id, string Name) { public override string ToString() => Name; }
internal sealed record AudioReading(double Peak, bool High, string Status, bool Ready);
internal sealed class AudioMonitor : IDisposable
{
    private sealed class Channel : IDisposable
    {
        internal MMDevice Device = null!;
        internal WasapiCapture Capture = null!;
        internal WaveFileWriter? Writer;
        internal string Selection = "", Id = "", Status = "正在连接…";
        internal double Peak;
        internal long LastData, HighSince;
        internal bool Ready, High, Stopping;
        internal readonly object Gate = new();
        public void Dispose() { Stopping = true; Capture?.Dispose(); lock (Gate) Writer?.Dispose(); Device?.Dispose(); }
    }
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Channel? _mic, _system;
    private CancellationTokenSource? _health;
    internal event Action<string>? Faulted;
    internal string MicrophoneId => _mic?.Id ?? "";
    internal string SystemId => _system?.Id ?? "";
    internal bool Active => _mic != null || _system != null;
    internal static IReadOnlyList<AudioDeviceChoice> Devices(bool microphone)
    {
        using var e = new MMDeviceEnumerator();
        var list = new List<AudioDeviceChoice> { new("", "跟随系统默认设备") };
        foreach (var d in e.EnumerateAudioEndPoints(microphone ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
        { using (d) list.Add(new(d.ID, d.FriendlyName)); }
        return list;
    }
    internal static MMDevice Resolve(bool microphone, string id)
    {
        using var e = new MMDeviceEnumerator();
        var d = string.IsNullOrEmpty(id) ? e.GetDefaultAudioEndpoint(microphone ? DataFlow.Capture : DataFlow.Render, Role.Console) : e.GetDevice(id);
        if (d.State != DeviceState.Active || d.DataFlow != (microphone ? DataFlow.Capture : DataFlow.Render)) { d.Dispose(); throw new InvalidOperationException("选择的声音设备已断开或不可用，请刷新并重新选择。"); }
        return d;
    }
    internal async Task StartAsync(Settings settings, string? testDirectory = null)
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
            await Task.Run(() =>
            {
                try
                {
                    if (settings.Microphone) _mic = Open(true, settings.MicrophoneDeviceId, testDirectory);
                    if (settings.SystemAudio) _system = Open(false, settings.SystemAudioDeviceId, testDirectory);
                }
                catch { _mic?.Dispose(); _system?.Dispose(); _mic = _system = null; throw; }
            });
            _health = new(); var token = _health.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(700, token); Check(_mic, true); Check(_system, false); }
                    catch (OperationCanceledException) { break; }
                }
            });
        }
        finally { _lifecycle.Release(); }
    }
    private Channel Open(bool mic, string id, string? directory)
    {
        var c = new Channel { Selection = id };
        try
        {
            c.Device = Resolve(mic, id); c.Id = c.Device.ID; c.Status = c.Device.FriendlyName;
            c.Capture = mic ? new WasapiCapture(c.Device) : new WasapiLoopbackCapture(c.Device);
            // Standard float format gives the same sample conversion on all endpoints.
            c.Capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(c.Capture.WaveFormat.SampleRate, c.Capture.WaveFormat.Channels);
            if (directory != null) { Directory.CreateDirectory(directory); c.Writer = new WaveFileWriter(Path.Combine(directory, mic ? "microphone.wav" : "system.wav"), c.Capture.WaveFormat); }
            c.Capture.DataAvailable += (_, e) =>
            {
                double peak = Peak(e.Buffer, e.BytesRecorded);
                lock (c.Gate)
                {
                    c.Writer?.Write(e.Buffer, 0, e.BytesRecorded);
                    c.Peak = Math.Max(peak, c.Peak * .65); c.LastData = Environment.TickCount64;
                    if (peak >= .95) { if (c.HighSince == 0) c.HighSince = c.LastData; c.High = c.LastData - c.HighSince >= 1500; }
                    else { c.HighSince = 0; c.High = false; }
                }
            };
            c.Capture.RecordingStopped += (_, e) => { if (!c.Stopping) Fail(c, mic, e.Exception?.Message ?? "声音采集已意外停止"); };
            c.Ready = true; c.Capture.StartRecording(); return c;
        }
        catch (Exception ex) { c.Dispose(); throw new InvalidOperationException($"{(mic ? "麦克风" : "系统声音")}不可用：{ex.Message}。请刷新设备；麦克风请检查 Windows 隐私权限。", ex); }
    }
    internal static double Peak(byte[] bytes, int count)
    { double result = 0; for (int i = 0; i + 4 <= count; i += 4) { float value = BitConverter.ToSingle(bytes, i); if (float.IsFinite(value)) result = Math.Max(result, Math.Abs(value)); } return Math.Min(1, result); }
    private void Check(Channel? c, bool mic)
    {
        if (c == null || c.Stopping || !c.Ready) return;
        try
        {
            using var current = Resolve(mic, c.Selection);
            if (current.ID != c.Id) throw new InvalidOperationException("系统默认设备已更改，请重新检测或开始录制");
        }
        catch (Exception ex) { Fail(c, mic, ex.Message); }
    }
    private void Fail(Channel c, bool mic, string error)
    { lock (c.Gate) { if (!c.Ready || c.Stopping) return; c.Ready = false; c.Peak = 0; c.Status = "不可用：" + error; } Faulted?.Invoke($"{(mic ? "麦克风" : "系统声音")} {c.Status}"); }
    internal AudioReading Read(bool microphone)
    {
        var c = microphone ? _mic : _system;
        if (c == null) return new(0, false, "已关闭", false);
        lock (c.Gate) return new(c.Ready ? c.Peak * Math.Exp(-Math.Max(0, Environment.TickCount64 - c.LastData - 100) / 350d) : 0, c.High && Environment.TickCount64 - c.LastData < 300, c.Status, c.Ready);
    }
    internal async Task StopAsync() { await _lifecycle.WaitAsync(); try { await StopCoreAsync(); } finally { _lifecycle.Release(); } }
    private async Task StopCoreAsync()
    { _health?.Cancel(); _health?.Dispose(); _health = null; var mic = _mic; var sys = _system; _mic = _system = null; await Task.Run(() => { mic?.Dispose(); sys?.Dispose(); }); }
    public void Dispose() => _ = StopAsync();
}
