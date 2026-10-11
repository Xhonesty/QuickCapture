using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickCapture;

internal static class SettingsCategoryTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Three settings categories fit 480x680 in both themes with fixed tabs/footer and native keyboard focus", async () =>
        {
            await WithMain(async (main, config, keys) =>
            {
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    ThemeService.Apply(theme); main.UpdateLayout(); UiChangeTests.Render(main, $"main-simplified-{theme.ToLowerInvariant()}.png");
                    Ensure(main.FindName("EditImageButton") == null && main.FindName("EditRecordingButton") == null, "Top edit buttons remain");
                    var dialog = new SettingsWindow(main, config, keys);
                    await Dialog(dialog, async () =>
                    {
                        await dialog.DeviceInitialization;
                        Ensure(dialog.CategoryTabs.Items.Cast<TabItem>().Select(tab => tab.Header).SequenceEqual(new[] { "通用与媒体工具", "截屏", "录屏" }), "Category names/order changed");
                        double? footerY = null;
                        foreach (int category in Enumerable.Range(0, 3))
                        {
                            dialog.CategoryTabs.SelectedIndex = category; dialog.UpdateLayout();
                            var page = (StackPanel)((TabItem)dialog.CategoryTabs.SelectedItem).Content;
                            var save = dialog.Setting<Button>("SaveSettings"); var saveBounds = Bounds(save, dialog);
                            Ensure(footerY == null || Math.Abs(footerY.Value - saveBounds.Y) < .1, "Footer moved when switching category"); footerY = saveBounds.Y;
                            Ensure(!Visuals<ScrollViewer>(page).Any(view => view.Content is StackPanel), "Category contains a scroll page");
                            Ensure(page.DesiredSize.Height <= dialog.CategoryTabs.ActualHeight - 52 + 1, $"Category {category} is too tall: {page.DesiredSize.Height:F1}");
                            foreach (FrameworkElement element in Visuals<FrameworkElement>(page).Where(item => item.IsVisible && item is Control or TextBlock or Border))
                            {
                                var bounds = Bounds(element, dialog);
                                Ensure(bounds.Left >= 15 && bounds.Right <= dialog.ActualWidth - 15 + 1 && bounds.Top >= 90 && bounds.Bottom <= saveBounds.Y - 8 + 1, $"{theme} category {category} clips {element.GetType().Name} {element.Name}: {bounds}");
                            }
                            UiChangeTests.Render(dialog, $"settings-tabs-{theme.ToLowerInvariant()}-{category}.png");
                        }
                        dialog.CategoryTabs.SelectedIndex = 0; dialog.UpdateLayout();
                        var first = (TabItem)dialog.CategoryTabs.Items[0]; Ensure(first.Focus(), "Tab header cannot receive focus");
                        first.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
                        await Dispatcher.Yield(DispatcherPriority.Input);
                        Ensure(dialog.CategoryTabs.SelectedIndex == 1, "Right arrow did not select the next tab");
                        var second = (TabItem)dialog.CategoryTabs.Items[1];
                        Ensure(second.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)), "Tab navigation cannot enter selected category");
                        Ensure(Keyboard.FocusedElement is FrameworkElement focused && Bounds(focused, dialog).Top >= 90, "Focus skipped selected page");
                        await Task.CompletedTask;
                    });
                }
            });
        });
        await check("Drafts survive category changes; Save commits every category and Cancel/close restore previews without writes", async () =>
        {
            await WithMain(async (main, config, keys) =>
            {
                string before = File.ReadAllText(Paths.SettingsFile);
                var cancel = new SettingsWindow(main, config, keys);
                await Dialog(cancel, () =>
                {
                    cancel.Setting<TextBox>("OutputDirectory").Text = Path.Combine(Paths.TestRoot!, "cancelled");
                    cancel.Setting<ComboBox>("InterfaceTheme").SelectedIndex = 1;
                    cancel.CategoryTabs.SelectedIndex = 1;
                    cancel.Setting<Slider>("DefaultScreenshotQuality").Value = 64;
                    cancel.Setting<CheckBox>("ScreenshotAutoSave").IsChecked = false;
                    cancel.CategoryTabs.SelectedIndex = 2;
                    cancel.Setting<CheckBox>("RecordingCursor").IsChecked = false;
                    cancel.Setting<ComboBox>("DefaultRecordingFormat").SelectedIndex = 2;
                    cancel.CategoryTabs.SelectedIndex = 0;
                    Ensure(cancel.Setting<TextBox>("OutputDirectory").Text.EndsWith("cancelled") && cancel.Setting<Slider>("DefaultScreenshotQuality").Value == 64 && cancel.Setting<ComboBox>("DefaultRecordingFormat").SelectedIndex == 2, "Switching destroyed a draft");
                    cancel.Setting<Button>("取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask;
                });
                Ensure(File.ReadAllText(Paths.SettingsFile) == before && config.ScreenshotQuality == 90 && config.Cursor && ThemeService.Current == "Light", "Cancel committed a draft or retained theme preview");
                var close = new SettingsWindow(main, config, keys);
                await Dialog(close, () => { close.Setting<ComboBox>("InterfaceTheme").SelectedIndex = 1; close.Close(); return Task.CompletedTask; });
                Ensure(File.ReadAllText(Paths.SettingsFile) == before && ThemeService.Current == "Light", "Close did not cancel theme");
                var save = new SettingsWindow(main, config, keys);
                bool? result = await Dialog(save, () =>
                {
                    save.Setting<HotkeyCaptureBox>("ScreenshotHotkey").CaptureKey(Key.F10, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
                    save.Setting<TextBox>("OutputDirectory").Text = Path.Combine(Paths.TestRoot!, "saved");
                    save.Setting<ComboBox>("InterfaceTheme").SelectedIndex = 1;
                    save.CategoryTabs.SelectedIndex = 1;
                    save.Setting<ComboBox>("DefaultScreenshotFormat").SelectedIndex = 2;
                    save.Setting<Slider>("DefaultScreenshotQuality").Value = 78;
                    save.Setting<CheckBox>("ScreenshotAutoSave").IsChecked = false;
                    save.Setting<ComboBox>("OcrLanguage").SelectedIndex = 1;
                    save.Setting<TextBox>("StepStart").Text = "12"; save.Setting<TextBox>("StepSize").Text = "52";
                    save.Setting<Slider>("PinOpacity").Value = 65;
                    save.CategoryTabs.SelectedIndex = 2;
                    save.Setting<CheckBox>("RecordingCursor").IsChecked = false;
                    save.Setting<CheckBox>("RecordingSystemAudio").IsChecked = true;
                    save.Setting<ComboBox>("DefaultRecordingFormat").SelectedIndex = 2;
                    save.Setting<ComboBox>("DefaultRecordingFps").SelectedIndex = 3;
                    save.CategoryTabs.SelectedIndex = 0;
                    save.Setting<Button>("保存设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask;
                });
                var loaded = Settings.Load();
                Ensure(result == true && loaded.OutputDirectory.EndsWith("saved") && loaded.ScreenshotHotkey.EndsWith("F10") && loaded.Theme == "Dark", "General settings did not commit");
                Ensure(loaded.ScreenshotFormat == ScreenshotFormat.WebP && loaded.ScreenshotQuality == 78 && !loaded.AutoSaveScreenshot && loaded.OcrLanguage == "chi_tra+eng" && loaded.StepStart == 12 && loaded.StepSize == 52 && loaded.PinOpacity == .65, "Screenshot category did not commit");
                Ensure(!loaded.Cursor && loaded.SystemAudio && loaded.RecordingFormat == RecordingFormat.Gif && loaded.GifFps == 20 && loaded.MicrophoneDeviceId == "missing-mic", "Recording category lost configuration");
                Ensure(loaded.LastRegion == config.LastRegion && loaded.Mp4Fps == 30, "Unchanged/recovery preferences were lost");
            });
        });
        await check("Secondary audio device selections stay in the settings draft and missing devices remain selected", async () =>
        {
            await WithMain(async (main, config, keys) =>
            {
                string before = File.ReadAllText(Paths.SettingsFile);
                var draft = new Settings { MicrophoneDeviceId = config.MicrophoneDeviceId, Microphone = false, SystemAudio = false };
                var dialog = new AudioDeviceWindow(main, draft, persistSelection: false);
                try
                {
                    dialog.Show(); await dialog.Initialization;
                    var combo = Visuals<ComboBox>(dialog).First();
                    Ensure(((AudioDeviceChoice)combo.SelectedItem).Id == "missing-mic", "Missing device silently fell back");
                    combo.SelectedIndex = 0; await Task.Delay(50);
                    Ensure(draft.MicrophoneDeviceId == "" && config.MicrophoneDeviceId == "missing-mic" && File.ReadAllText(Paths.SettingsFile) == before, "Secondary page persisted before outer Save");
                }
                finally { dialog.Close(); }
            });
        });
        await check("Media tool detection uses the draft path without committing settings and exposes usable export formats", async () =>
        {
            await WithMain(async (main, config, keys) =>
            {
                string before = File.ReadAllText(Paths.SettingsFile); var dialog = new SettingsWindow(main, config, keys);
                await Dialog(dialog, async () =>
                {
                    dialog.Setting<TextBox>("MediaToolsPath").Text = Path.Combine(AppContext.BaseDirectory, "Tools", "media");
                    var detect = dialog.Setting<Button>("DetectMediaTools"); detect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (int i = 0; i < 600 && !detect.IsEnabled; i++) await Task.Delay(50);
                    Ensure(detect.IsEnabled && dialog.Setting<TextBlock>("MediaToolsStatus").Text == "MP4 可用 · WebM 可用 · GIF 可用", "Draft detection did not finish with export capabilities");
                    Ensure(File.ReadAllText(Paths.SettingsFile) == before && config.MediaToolsPath == "", "Detection committed the draft tools path");
                });
            });
        });
    }
    internal static async Task WithMain(Func<MainWindow, Settings, HotkeyService, Task> action)
    {
        string? previous = Paths.TestRoot; string theme = ThemeService.Current;
        Paths.TestRoot = Path.Combine(previous!, "SettingsTabs-" + Guid.NewGuid().ToString("N"));
        new Settings { Theme = "Light", ScreenshotHotkey = "Ctrl+Alt+Shift+F6", RecordingHotkey = "Ctrl+Alt+Shift+F7", MicrophoneDeviceId = "missing-mic", LastRegion = new(0, 0, 320, 240, "fixture") }.Save();
        var main = new MainWindow(); main.Closing += (_, e) => e.Cancel = false;
        try
        {
            main.Show(); await Task.Delay(120);
            await action(main, (Settings)typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!, (HotkeyService)typeof(MainWindow).GetField("_hotkeys", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!);
        }
        finally { main.Close(); Paths.TestRoot = previous; ThemeService.Apply(theme); }
    }
    private static async Task<bool?> Dialog(SettingsWindow dialog, Func<Task> action)
    {
        var done = new TaskCompletionSource();
        dialog.Loaded += async (_, _) => { try { await Task.Delay(160); await action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } finally { if (dialog.IsVisible) dialog.Close(); } };
        var result = dialog.ShowDialog(); await done.Task; return result;
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    { if (root is T match) yield return match; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var item in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return item; }
    private static Rect Bounds(FrameworkElement item, Visual root) => item.TransformToAncestor(root).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
