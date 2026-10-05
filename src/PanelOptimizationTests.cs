using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace QuickCapture;

internal static class PanelOptimizationTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Shortcut fields retain captured combinations, Esc rollback and active key reservations", () =>
        {
            var field = new HotkeyCaptureBox("Ctrl+Alt+F8", "截图快捷键");
            field.CaptureKey(System.Windows.Input.Key.F10, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift);
            Ensure(field.Text == "Ctrl+Shift+F10", "Captured shortcut changed during panel styling");
            field.CaptureKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
            Ensure(field.Text == "Ctrl+Alt+F8", "Esc did not restore the original shortcut");
            var owner = new Window(); var competitor = new Window();
            try
            {
                using var first = new HotkeyService(new WindowInteropHelper(owner).EnsureHandle());
                using var second = new HotkeyService(new WindowInteropHelper(competitor).EnsureHandle());
                first.Configure("Ctrl+Alt+Shift+F10", "Ctrl+Alt+Shift+F11");
                bool rejected = false;
                try { second.Configure("Ctrl+Alt+Shift+F10", "Ctrl+Alt+Shift+F12"); } catch (InvalidOperationException) { rejected = true; }
                Ensure(rejected, "An occupied shortcut was accepted");
            }
            finally { owner.Close(); competitor.Close(); }
            return Task.CompletedTask;
        });
        await check("Settings theme preview synchronizes both windows, cancels cleanly and saves chosen theme", async () =>
        {
            string? previousRoot = Paths.TestRoot; string previousTheme = ThemeService.Current;
            Paths.TestRoot = Path.Combine(previousRoot!, "PanelTheme-" + Guid.NewGuid().ToString("N"));
            var config = new Settings { Theme = "Light", ScreenshotHotkey = "Ctrl+Alt+Shift+F8", RecordingHotkey = "Ctrl+Alt+Shift+F9" }; config.Save();
            var main = new MainWindow(); main.Closing += (_, e) => e.Cancel = false;
            try
            {
                main.Show(); await Task.Delay(100);
                var keys = (HotkeyService)typeof(MainWindow).GetField("_hotkeys", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                var before = File.ReadAllText(Paths.SettingsFile);
                var cancel = new SettingsWindow(main, config, keys);
                await Dialog(cancel, () =>
                {
                    Named<ComboBox>(cancel, "InterfaceTheme").SelectedIndex = 1; cancel.UpdateLayout();
                    Ensure(ThemeService.Current == "Dark" && ReferenceEquals(main.Background, cancel.Background), "Preview did not synchronize existing windows");
                    Ensure(((TextBlock)main.FindName("ThemeToggleLabel")).Text == "切换浅色", "Main theme action did not synchronize");
                    Ensure(File.ReadAllText(Paths.SettingsFile) == before && config.Theme == "Light", "Theme preview persisted settings before Save");
                    Named<Button>(cancel, "取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return Task.CompletedTask;
                });
                Ensure(ThemeService.Current == "Light" && File.ReadAllText(Paths.SettingsFile) == before, "Cancel did not restore the original theme without saving");
                var save = new SettingsWindow(main, config, keys);
                bool? result = await Dialog(save, () =>
                {
                    Ensure(save.Width == main.Width && save.Height == main.Height, "Panel and settings sizes differ");
                    Named<ComboBox>(save, "InterfaceTheme").SelectedIndex = 1;
                    var quality = Named<Slider>(save, "DefaultScreenshotQuality"); quality.Value = 73;
                    Ensure(Named<TextBlock>(save, "ScreenshotQualityValue").Text == "73", "Quality value does not follow the slider");
                    Named<Button>(save, "保存设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return Task.CompletedTask;
                });
                Ensure(result == true && config.Theme == "Dark" && Settings.Load().Theme == "Dark" && Settings.Load().ScreenshotQuality == 73, "Save lost the previewed theme or quality");
                Ensure(ThemeService.Current == "Dark", "Successful save reverted the selected theme");
            }
            finally { main.Close(); Paths.TestRoot = previousRoot; ThemeService.Apply(previousTheme); }
        });
        await check("Recording controls show elapsed time only while active and reset on completion", async () =>
        {
            var main = new MainWindow(); main.Closing += (_, e) => e.Cancel = false;
            try
            {
                main.Show(); await Task.Delay(80);
                var recorder = (RecorderService)typeof(MainWindow).GetField("_recorder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                var state = typeof(RecorderService).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var update = typeof(MainWindow).GetMethod("UpdateRecordingUi", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Ensure(((System.Windows.Shapes.Ellipse)main.FindName("RecordingDot")).Visibility == Visibility.Collapsed, "Idle recording button has a recording dot");
                state.SetValue(recorder, RecordingState.Recording); update.Invoke(main, null);
                Ensure(((System.Windows.Shapes.Ellipse)main.FindName("RecordingDot")).IsVisible, "Active recording has no dot");
                Ensure(((TextBlock)main.FindName("StatusText")).Text.Contains("00:00:00"), "Active status has no duration");
                state.SetValue(recorder, RecordingState.Paused); update.Invoke(main, null);
                Ensure(((TextBlock)main.FindName("StatusText")).Text.StartsWith("已暂停"), "Pause status was lost");
                state.SetValue(recorder, RecordingState.Completed); update.Invoke(main, null);
                Ensure(((System.Windows.Shapes.Ellipse)main.FindName("RecordingDot")).Visibility == Visibility.Collapsed, "Completion retained the active dot");
            }
            finally { main.Close(); }
        });
        await check("Small text, shortcut chips and primary button states meet AA in both themes", () =>
        {
            string original = ThemeService.Current;
            try
            {
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    ThemeService.Apply(theme);
                    foreach (var pair in new[] { ("TextHint", "WindowBackground"), ("TextHint", "PanelBackground"), ("BadgeForeground", "BadgeBackground"), ("AccentForeground", "Accent"), ("AccentHoverForeground", "AccentHover"), ("AccentForeground", "AccentActive"), ("DangerText", "PanelBackground"), ("DangerText", "HoverBackground") })
                        Ensure(Contrast(pair.Item1, pair.Item2) >= 4.5, $"{theme} {pair} has insufficient contrast");
                }
            }
            finally { ThemeService.Apply(original); }
            return Task.CompletedTask;
        });
    }
    private static async Task<bool?> Dialog(SettingsWindow dialog, Func<Task> action)
    {
        var done = new TaskCompletionSource();
        dialog.Loaded += async (_, _) =>
        {
            try { await Task.Delay(80); await action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (dialog.IsVisible) dialog.Close(); }
        };
        var result = dialog.ShowDialog(); await done.Task; return result;
    }
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
        => Visuals<T>(root).FirstOrDefault(item => item.Name == name || item is Button button && button.Content as string == name) ?? throw new InvalidOperationException("Missing control: " + name);
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static double Contrast(string foreground, string background)
    {
        double L(string key)
        {
            var color = ((SolidColorBrush)ThemeService.Brush(key)).Color;
            double Linear(byte value) { double s = value / 255d; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return Linear(color.R) * 0.2126 + Linear(color.G) * 0.7152 + Linear(color.B) * 0.0722;
        }
        double a = L(foreground), b = L(background); return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
