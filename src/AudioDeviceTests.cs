using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace QuickCapture;
internal static class AudioDeviceTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Audio device persistence, missing-device rejection, PCM peak and disabled resource release", async () =>
        {
            var settings = new Settings { MicrophoneDeviceId = "missing-test-device", SystemAudioDeviceId = "missing-render" }; settings.Save();
            var restored = Settings.Load(); UpgradeTests.Ensure(restored.MicrophoneDeviceId == settings.MicrophoneDeviceId && restored.SystemAudioDeviceId == settings.SystemAudioDeviceId, "Device selection lost");
            using var monitor = new AudioMonitor(); await monitor.StartAsync(settings); UpgradeTests.Ensure(!monitor.Active, "Disabled sources acquire audio");
            settings.Microphone = true; bool rejected = false;
            try { await monitor.StartAsync(settings); } catch (InvalidOperationException) { rejected = true; }
            UpgradeTests.Ensure(rejected && !monitor.Active, "Missing device appears healthy");
            UpgradeTests.Ensure(Math.Abs(AudioMonitor.Peak(BitConverter.GetBytes(.75f), 4) - .75) < .0001, "Real float PCM peak wrong"); await monitor.StopAsync();
        });
        await check("Actual default and explicit WASAPI endpoints, loopback tone, short audio file and release", async () =>
        {
            var mic = await Task.Run(() => AudioMonitor.Devices(true)); var render = await Task.Run(() => AudioMonitor.Devices(false));
            UpgradeTests.Ensure(mic.First().Id == "" && render.First().Id == "" && render.Count > 1 && mic.Count > 1, "No available audio endpoint");
            var settings = new Settings { Microphone = true, SystemAudio = true };
            using var monitor = new AudioMonitor();
            string directory = Path.Combine(Paths.TestRoot!, "AudioDevices");
            await monitor.StartAsync(settings, directory); await Task.Delay(250);
            string micId = monitor.MicrophoneId, renderId = monitor.SystemId;
            using var device = AudioMonitor.Resolve(false, renderId);
            using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
            // Produce a real endpoint signal; loopback must capture it, not a simulated UI value.
            var signal = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Frequency = 440, Gain = .35 };
            output.Init(signal); output.Play();
            await Task.Delay(1000);
            UpgradeTests.Ensure(monitor.Read(false).Ready && monitor.Read(false).Peak > .001 && monitor.Read(true).Ready, $"No real loopback level or microphone unavailable: system={monitor.Read(false)}, mic={monitor.Read(true)}");
            await monitor.StopAsync(); output.Stop();
            using (var file = new WaveFileReader(Path.Combine(directory, "system.wav"))) UpgradeTests.Ensure(file.TotalTime.TotalSeconds > .7 && file.Length > 10000, "Short recording has no PCM audio");
            settings.MicrophoneDeviceId = micId; settings.SystemAudioDeviceId = renderId;
            await monitor.StartAsync(settings); UpgradeTests.Ensure(monitor.MicrophoneId == micId && monitor.SystemId == renderId, "Explicit device selection ignored"); await monitor.StopAsync();
            UpgradeTests.Ensure(!monitor.Active, "Audio resources remain active");
            File.WriteAllText(Path.Combine(directory, "endpoints.txt"), string.Join(Environment.NewLine, mic.Concat(render).Select(d => d.Name + " | " + d.Id)));
        });
        await check("Audio selection and compact excluded recording bar in both themes", async () =>
        {
            var owner = new Window(); owner.Show();
            try
            {
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    ThemeService.Apply(theme); var window = new AudioDeviceWindow(owner, new Settings());
                    using var monitor = new AudioMonitor(); var bar = new RecordingBar("Ctrl+Alt+F9", () => { }, () => { }, audio: monitor);
                    try { window.Show(); await window.Initialization; bar.Show(); bar.SetRecording(); await Task.Delay(100); UiChangeTests.Render(window, "audio-devices-" + theme.ToLowerInvariant() + ".png"); UiChangeTests.Render(bar, "audio-bar-" + theme.ToLowerInvariant() + ".png"); }
                    finally { window.Close(); bar.Close(); }
                }
            }
            finally { owner.Close(); }
        });
    }
}
