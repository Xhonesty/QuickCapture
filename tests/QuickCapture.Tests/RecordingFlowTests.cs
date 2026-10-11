using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ScreenRecorderLib;
using Forms = System.Windows.Forms;

namespace QuickCapture;

internal static class RecordingFlowTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Start mode defaults, persistence, settings cancellation and save", async () =>
        {
            Directory.CreateDirectory(Paths.Data); File.WriteAllText(Paths.SettingsFile, "{}");
            UpgradeTests.Ensure(Settings.Load().RecordingStartMode == RecordingStartMode.Automatic, "Legacy automatic default lost");
            var settings = new Settings { RecordingStartMode = RecordingStartMode.Manual }; settings.Save();
            UpgradeTests.Ensure(Settings.Load().RecordingStartMode == RecordingStartMode.Manual, "Manual mode not persisted");
            var owner = new Window { Width = 480, Height = 680 }; owner.Show();
            using var keys = new HotkeyService(new System.Windows.Interop.WindowInteropHelper(owner).Handle);
            try
            {
                var dialog = new SettingsWindow(owner, settings, keys); dialog.Show(); await dialog.DeviceInitialization;
                dialog.CategoryTabs.SelectedIndex = 2; dialog.Setting<ComboBox>("RecordingStartMode").SelectedIndex = 0;
                UiChangeTests.Render(dialog, "recording-start-mode-dark.png"); dialog.Close();
                UpgradeTests.Ensure(settings.RecordingStartMode == RecordingStartMode.Manual && Settings.Load().RecordingStartMode == RecordingStartMode.Manual, "Cancel changed startup setting");
                dialog = new SettingsWindow(owner, settings, keys);
                _ = owner.Dispatcher.BeginInvoke(new Action(async () => { await Task.Delay(120); await dialog.DeviceInitialization; dialog.CategoryTabs.SelectedIndex = 2; dialog.Setting<ComboBox>("RecordingStartMode").SelectedIndex = 0; dialog.Setting<Button>("SaveSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }));
                dialog.ShowDialog(); UpgradeTests.Ensure(Settings.Load().RecordingStartMode == RecordingStartMode.Automatic, "Save didn't persist auto start");
            }
            finally { owner.Close(); }
        });
        await check("Preparing all target types is silent, zero time, cancellable and revalidates stale targets", async () =>
        {
            var scene = new Window { Width = 360, Height = 240, Title = "QuickCapture flow scene" }; scene.Show(); await Task.Delay(150);
            var handle = new System.Windows.Interop.WindowInteropHelper(scene).Handle; var screen = Forms.Screen.PrimaryScreen!;
            var bounds = screen.Bounds; var region = new SavedRegion(bounds.X + 30, bounds.Y + 30, 320, 180, screen.DeviceName);
            using var recorder = new RecorderService(); string directory = RecordingRecovery.DirectoryPath;
            int Before() => Directory.Exists(directory) ? Directory.GetFiles(directory).Length : 0;
            int files = Before();
            try
            {
                UpgradeTests.Ensure(Native.VisibleWindowBounds(handle, out var targetBounds), "Visible preparation target has no bounds");
                var frame = new RecordingFrame(new(targetBounds.X, targetBounds.Y, targetBounds.Width, targetBounds.Height, screen.DeviceName), () => Native.VisibleWindowBounds(handle, out var current) ? current : null);
                try { frame.Show(); Native.Place(scene, new(bounds.X + 100, bounds.Y + 100, 420, 300), false); await Task.Delay(300); UpgradeTests.Ensure(Native.VisibleWindowBounds(handle, out var movedBounds) && frame.PhysicalBounds == System.Drawing.Rectangle.Inflate(movedBounds, 3, 3), "Prepared window border didn't follow target movement"); }
                finally { frame.Close(); }
                foreach (var source in new RecordingSourceBase[] { RecorderService.RegionSource(region), new WindowRecordingSource(handle), new DisplayRecordingSource(screen.DeviceName) })
                {
                    recorder.Prepare(source, new Settings { SystemAudio = true, Microphone = true, RecordingStartMode = RecordingStartMode.Manual });
                    await Task.Delay(150); UpgradeTests.Ensure(recorder.State == RecordingState.Preparing && recorder.Elapsed == TimeSpan.Zero && Before() == files && !recorder.Audio.Read(true).Ready && !recorder.Audio.Read(false).Ready, "Preparation sampled media or time");
                    recorder.CancelPreparation(); UpgradeTests.Ensure(recorder.State == RecordingState.Idle && !recorder.IsBusy, "Preparation leaked state");
                }
                recorder.Prepare(new WindowRecordingSource(handle), new Settings()); scene.Close();
                bool rejected = false; try { await recorder.StartPreparedAsync(); } catch (InvalidOperationException) { rejected = true; }
                UpgradeTests.Ensure(rejected && Before() == files, "Closed target started recording"); recorder.Dispose();
                rejected = false; try { recorder.Prepare(new DisplayRecordingSource("removed-monitor"), new Settings()); } catch (InvalidOperationException) { rejected = true; }
                UpgradeTests.Ensure(rejected, "Disconnected display accepted");
            }
            finally { scene.Close(); }
        });
        await check("Three recording controls follow preparation, starting, recording, pause and stop states in both themes", async () =>
        {
            foreach (string theme in new[] { "Light", "Dark" })
            {
                ThemeService.Apply(theme); var bar = new RecordingBar("Ctrl+Alt+F9", () => { }, () => { }, save: () => { }); bar.Show();
                var state = UpgradeTests.Find<Button>(bar, b => b.Name == "RecordingPause"); var edit = UpgradeTests.Find<Button>(bar, b => b.Name == "RecordingStop"); var save = UpgradeTests.Find<Button>(bar, b => b.Name == "RecordingSave");
                bar.SetState(RecordingState.Preparing); UpgradeTests.Ensure((string)state.Content == "开始" && state.IsEnabled && edit.IsEnabled && !save.IsEnabled, "Preparing controls wrong");
                await Task.Delay(120); UiChangeTests.Render(bar, "recording-ready-" + theme.ToLowerInvariant() + ".png");
                bar.SetState(RecordingState.Starting); UpgradeTests.Ensure(!state.IsEnabled && !edit.IsEnabled && !save.IsEnabled, "Starting permits conflicting operations");
                bar.SetRecording(); UpgradeTests.Ensure((string)state.Content == "暂停" && edit.IsEnabled && save.IsEnabled, "Recording controls wrong");
                bar.SetPaused(); UpgradeTests.Ensure((string)state.Content == "继续" && edit.IsEnabled && save.IsEnabled, "Paused completion disabled");
                UiChangeTests.Render(bar, "recording-paused-" + theme.ToLowerInvariant() + ".png"); bar.SetSaving(); UpgradeTests.Ensure(!state.IsEnabled && !edit.IsEnabled && !save.IsEnabled, "Stopping permits conflicting operations"); bar.Close();
            }
        });
        await check("Manual start, duplicate starts, pause excluded from duration and paused finalization on actual device", async () =>
        {
            var scene = new Window { Width = 400, Height = 280, Background = System.Windows.Media.Brushes.Teal }; scene.Show(); await Task.Delay(150);
            using var recorder = new RecorderService(); var states = new List<RecordingState>(); recorder.StateChanged += s => { lock (states) states.Add(s); };
            try
            {
                var screen = Forms.Screen.PrimaryScreen!; recorder.Prepare(RecorderService.RegionSource(new SavedRegion(screen.Bounds.X + 30, screen.Bounds.Y + 30, 320, 180, screen.DeviceName)), new Settings { HardwareEncoding = false });
                var first = recorder.StartPreparedAsync(); var duplicate = recorder.StartPreparedAsync(); await Task.WhenAll(first, duplicate);
                await Task.Delay(900); recorder.Pause(); var elapsed = recorder.Elapsed; await Task.Delay(400);
                UpgradeTests.Ensure(Math.Abs((recorder.Elapsed - elapsed).TotalMilliseconds) < 5, "Pause contributes to duration");
                var stop = recorder.StopAsync(); var again = recorder.StopAsync(); UpgradeTests.Ensure(ReferenceEquals(stop, again), "Duplicated encoder finalization");
                string master = await stop.WaitAsync(TimeSpan.FromSeconds(30)); var tools = await MediaTools.DetectAsync(new Settings()); var info = await VideoExportService.ProbeAsync(master, tools);
                UpgradeTests.Ensure(info.Duration > .2 && File.Exists(master) && recorder.State == RecordingState.Completed && states.Count(s => s == RecordingState.Recording) == 1, "Manual recording did not finalize once");
                recorder.BeginSaving(); UpgradeTests.Ensure(recorder.State == RecordingState.Saving && recorder.IsBusy, "Saving isn't busy"); recorder.FinishSaving(); UpgradeTests.Ensure(!recorder.IsBusy, "Save state stuck");
            }
            finally { scene.Close(); }
        });
        await check("Main and tray command paths share state guards; recording shortcut command starts prepared target and ends paused capture into editor", async () =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var main = new MainWindow(); var recorder = (RecorderService)typeof(MainWindow).GetField("_recorder", flags)!.GetValue(main)!;
            var settings = (Settings)typeof(MainWindow).GetField("_settings", flags)!.GetValue(main)!;
            settings.ScreenshotHotkey = "Ctrl+Alt+F10"; settings.RecordingHotkey = "Ctrl+Alt+F11"; settings.HardwareEncoding = false; settings.SystemAudio = settings.Microphone = false; settings.OutputDirectory = Path.Combine(Paths.TestRoot!, "FlowSave");
            Task Command(string method, params object[] args) => (Task)typeof(MainWindow).GetMethod(method, flags)!.Invoke(main, args)!;
            var closer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; int editors = 0, saves = 0;
            closer.Tick += (_, _) => { var editor = Application.Current.Windows.OfType<RecordingEditWindow>().FirstOrDefault(); if (editor?.Initialization.IsCompleted == true) { editors++; editor.Close(); } var save = Application.Current.Windows.OfType<RecordingSaveWindow>().FirstOrDefault(); if (save?.SavedPath != null && save.Completion.IsCompleted) { saves++; save.Close(); } };
            try
            {
                main.Show(); await Task.Delay(120); var tray = (Forms.NotifyIcon)typeof(MainWindow).GetField("_tray", flags)!.GetValue(main)!; tray.Visible = false; var menu = tray.ContextMenuStrip!;
                void UpdateMenu() => typeof(Forms.ToolStripDropDown).GetMethod("OnOpening", flags)!.Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
                Forms.ToolStripItem Item(string text) => menu.Items.Cast<Forms.ToolStripItem>().Last(i => i.Text == text);
                foreach (var state in new[] { RecordingState.Starting, RecordingState.Stopping, RecordingState.Saving })
                {
                    typeof(RecorderService).GetField("_state", flags)!.SetValue(recorder, state); UpdateMenu();
                    UpgradeTests.Ensure(!Item("结束并编辑").Enabled && !Item("结束并保存").Enabled && !menu.Items[2].Enabled, "Tray permits a conflicting command in " + state);
                    await Command("ToggleRecordingAsync", false); UpgradeTests.Ensure(recorder.State == state, "Recording hotkey command changed a busy state");
                }
                recorder.Dispose(); var screen = Forms.Screen.PrimaryScreen!;
                recorder.Prepare(RecorderService.RegionSource(new SavedRegion(screen.Bounds.X + 40, screen.Bounds.Y + 40, 320, 180, screen.DeviceName)), settings); UpdateMenu();
                UpgradeTests.Ensure(Item("开始当前录制").Enabled && !Item("结束并保存").Enabled && Item("取消准备").Available, "Tray preparation actions wrong");
                await Command("ToggleRecordingAsync", false); await Task.Delay(700);
                typeof(MainWindow).GetMethod("TogglePause", flags)!.Invoke(main, null); UpdateMenu();
                UpgradeTests.Ensure(recorder.State == RecordingState.Paused && Item("结束并保存").Enabled && Item("结束并编辑").Enabled, "Paused tray completion actions missing");
                closer.Start(); await Command("ToggleRecordingAsync", false); closer.Stop();
                UpgradeTests.Ensure(editors == 1 && recorder.State == RecordingState.Idle && Directory.GetFiles(RecordingRecovery.DirectoryPath, "master-*.mp4").Any(p => RecordingRecovery.Load(p).Duration > .2), "Shortcut command didn't finalize before editing");
                settings.RecordingStartMode = RecordingStartMode.Automatic; settings.LastRegion = new(screen.Bounds.X + 40, screen.Bounds.Y + 40, 320, 180, screen.DeviceName);
                await Command("ToggleRecordingAsync", true); UpgradeTests.Ensure(recorder.State == RecordingState.Recording, "Automatic target selection didn't start"); await Task.Delay(650);
                closer.Start(); var saving = Command("StopAsync", true); await Command("StopAsync", true); await saving; closer.Stop();
                UpgradeTests.Ensure(saves == 1 && editors == 1 && recorder.State == RecordingState.Idle && Directory.GetFiles(settings.OutputDirectory, "*.mp4").Any(p => RecordingEditStore.ResolveSource(p) != p), "Direct end/save entered editor, duplicated save, or lost original association");
                await Command("ToggleRecordingAsync", true); await Task.Delay(500); string exitMaster = (string)typeof(RecorderService).GetField("_final", flags)!.GetValue(recorder)!; typeof(MainWindow).GetField("_exit", flags)!.SetValue(main, true); await Command("StopAsync", false);
                UpgradeTests.Ensure(recorder.State == RecordingState.Idle && File.Exists(exitMaster) && !File.Exists(exitMaster + ".partial.mp4") && RecordingRecovery.Load(exitMaster).Duration > .2 && editors == 1, "Exit path didn't finish its own recording before closing");
            }
            finally { closer.Stop(); typeof(MainWindow).GetField("_exit", flags)!.SetValue(main, true); recorder.Dispose(); main.Close(); }
        });
    }
}
