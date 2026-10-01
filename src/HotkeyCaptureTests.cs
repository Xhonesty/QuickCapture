using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace QuickCapture;

internal static class HotkeyCaptureTests
{
    internal static async Task RunUiAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Actual pressed-key input, Alt/Win modifiers, digits, validation, Esc and Tab", async () =>
        {
            var first = new HotkeyCaptureBox("Ctrl+Alt+S", "截图快捷键");
            var second = new HotkeyCaptureBox("Ctrl+Alt+R", "录屏快捷键");
            string hint = ""; first.HintChanged += value => hint = value;
            bool altSystemKey = false;
            first.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) => altSystemKey |= e.Key == Key.System && e.SystemKey == Key.F6), true);
            var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(first); panel.Children.Add(second);
            var window = new Window { Title = "轻截 · 按键录入验证", Width = 500, Height = 240, Content = panel }; Ui.Theme(window);
            try
            {
                window.Show(); await FocusAsync(window, first);
                Ensure(first.IsReadOnly && !InputMethod.GetIsInputMethodEnabled(first), "Shortcut input permits text/IME entry");
                await PressAsync(window, Key.F12, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
                Ensure(first.Text == "Ctrl+Alt+Shift+F12", "Physical keys did not produce the normalized shortcut");
                await PressAsync(window, Key.D5, ModifierKeys.Control);
                Ensure(first.Text == "Ctrl+5" && HotkeyService.Parse(first.Text) == (0x4002u, 0x35u), "Top-row digit maps to the wrong virtual key");
                await PressAsync(window, Key.F6, ModifierKeys.Alt);
                Ensure(first.Text == "Alt+F6" && altSystemKey, "Alt SystemKey was not captured");
                await PressAsync(window, Key.LeftCtrl);
                Ensure(first.Text == "Alt+F6", "A modifier alone replaced the shortcut");
                await PressAsync(window, Key.A);
                Ensure(first.Text == "Alt+F6" && hint.Contains("同时按下"), "Unmodified input was accepted or lacks a hint");
                await PressAsync(window, Key.Return, ModifierKeys.Control);
                Ensure(first.Text == "Ctrl+Enter" && HotkeyService.Parse(first.Text).Key == 0x0D, "Enter was not captured");
                Ensure(HotkeyService.Format(Key.F9, ModifierKeys.Control | ModifierKeys.Windows) == "Ctrl+Win+F9", "Windows modifier formatting failed");
                await PressAsync(window, Key.F9, ModifierKeys.Control | ModifierKeys.Windows);
                Ensure(first.Text == "Ctrl+Win+F9", "Physical Windows modifier was not captured");
                await PressAsync(window, Key.Escape);
                Ensure(first.Text == "Ctrl+Alt+S", "Esc did not restore the value from before focus");
                await PressAsync(window, Key.Tab);
                Ensure(second.IsKeyboardFocused, "Tab no longer moves to the next field");
                await PressAsync(window, Key.Tab, ModifierKeys.Shift);
                Ensure(first.IsKeyboardFocused, "Shift+Tab no longer moves back");
            }
            finally { window.Close(); }
        });

        await check("Registered-key capture, reservation, duplicate rejection and settings save/cancel", async () =>
        {
            const string oldShot = "Ctrl+Alt+Shift+F10", oldRecord = "Ctrl+Alt+Shift+F11";
            const string newShot = "Ctrl+Alt+Shift+F8", newRecord = "Ctrl+Alt+Shift+F9";
            string? savedSettings = File.Exists(Paths.SettingsFile) ? File.ReadAllText(Paths.SettingsFile) : null;
            string previousTheme = ThemeService.Current;
            var owner = new Window { Title = "轻截 · 全局快捷键验证", Width = 500, Height = 200 }; Ui.Theme(owner);
            var competitorWindow = new Window();
            try
            {
                owner.Show();
                using var hotkeys = new HotkeyService(new WindowInteropHelper(owner).Handle);
                using var competitor = new HotkeyService(new WindowInteropHelper(competitorWindow).EnsureHandle());
                hotkeys.Configure(oldShot, oldRecord);
                var triggered = new List<int>(); hotkeys.Pressed += id => triggered.Add(id);
                var config = new Settings { ScreenshotHotkey = oldShot, RecordingHotkey = oldRecord, Theme = "Light" };
                var cancel = new SettingsWindow(owner, config, hotkeys);
                bool? canceled = await WithDialogAsync(cancel, async () =>
                {
                    var record = Field(cancel, "RecordingHotkey"); await FocusAsync(cancel, record);
                    await PressAsync(cancel, Key.F10, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
                    Ensure(record.Text == oldShot && triggered.Count == 0, "Existing shortcut triggered an action instead of being captured");
                    bool duplicate = false;
                    try { hotkeys.Configure(Field(cancel, "ScreenshotHotkey").Text, record.Text); } catch (ArgumentException) { duplicate = true; }
                    Ensure(duplicate, "Duplicate screenshot and recording shortcuts were accepted");
                    AssertOccupied(competitor, oldShot, oldRecord);
                    Find<Button>(cancel, b => (string?)b.Content == "取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                Ensure(canceled == false && config.ScreenshotHotkey == oldShot && config.RecordingHotkey == oldRecord, "Cancel changed the active settings");
                Ensure((File.Exists(Paths.SettingsFile) ? File.ReadAllText(Paths.SettingsFile) : null) == savedSettings, "Cancel wrote settings to disk");
                await ActivateAsync(owner);
                await PressAsync(owner, Key.F10, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
                Ensure(triggered.SequenceEqual(new[] { 1 }), "Closing the dialog did not restore command delivery"); triggered.Clear();

                ThemeService.Apply("Light");
                var save = new SettingsWindow(owner, config, hotkeys);
                bool? saved = await WithDialogAsync(save, async () =>
                {
                    var shot = Field(save, "ScreenshotHotkey"); var record = Field(save, "RecordingHotkey");
                    await FocusAsync(save, shot); await PressAsync(save, Key.F8, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
                    await FocusAsync(save, record); await PressAsync(save, Key.F9, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
                    Ensure(shot.Text == newShot && record.Text == newRecord && triggered.Count == 0, "New combinations were not recorded");
                    Ensure(config.ScreenshotHotkey == oldShot, "Input changed active settings before Save");
                    UiChangeTests.Render(save, "settings-hotkeys-light.png");
                    ThemeService.Apply("Dark"); UiChangeTests.Render(save, "settings-hotkeys-dark.png"); ThemeService.Apply("Light");
                    Find<Button>(save, b => (string?)b.Content == "保存设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                var loaded = Settings.Load();
                Ensure(saved == true && config.ScreenshotHotkey == newShot && config.RecordingHotkey == newRecord, "Save did not apply the captured values");
                Ensure(loaded.ScreenshotHotkey == newShot && loaded.RecordingHotkey == newRecord, "Captured shortcuts were not persisted");
                competitor.Configure(oldShot, oldRecord); // Previous combinations were released only after Save.
                AssertOccupied(competitor, newShot, newRecord);
                await ActivateAsync(owner);
                await PressAsync(owner, Key.F8, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
                await PressAsync(owner, Key.F9, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
                Ensure(triggered.SequenceEqual(new[] { 1, 2 }), "Saved shortcuts did not dispatch their original actions");
            }
            finally
            {
                owner.Close(); competitorWindow.Close(); ThemeService.Apply(previousTheme);
                if (savedSettings != null) File.WriteAllText(Paths.SettingsFile, savedSettings);
                else if (File.Exists(Paths.SettingsFile)) File.Delete(Paths.SettingsFile);
            }
        });
    }

    private static void AssertOccupied(HotkeyService competitor, string shot, string record)
    {
        bool rejected = false;
        try { competitor.Configure(shot, record); } catch (InvalidOperationException) { rejected = true; }
        Ensure(rejected, "Shortcut reservations were lost while editing");
    }

    private static async Task<bool?> WithDialogAsync(SettingsWindow dialog, Func<Task> interaction)
    {
        var done = new TaskCompletionSource<bool>();
        dialog.Loaded += async (_, _) =>
        {
            try { await Task.Delay(120); await interaction(); done.TrySetResult(true); }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (dialog.IsVisible) dialog.Close(); }
        };
        bool? result = dialog.ShowDialog(); await done.Task; return result;
    }

    private static HotkeyCaptureBox Field(Window window, string name) => Find<HotkeyCaptureBox>(window, b => b.Name == name);
    private static T Find<T>(DependencyObject root, Predicate<T> predicate) where T : DependencyObject
        => FindOrNull(root, predicate) ?? throw new InvalidOperationException($"Control {typeof(T).Name} not found");
    private static T? FindOrNull<T>(DependencyObject root, Predicate<T> predicate) where T : DependencyObject
    {
        if (root is T element && predicate(element)) return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindOrNull(VisualTreeHelper.GetChild(root, i), predicate) is { } found) return found;
        return null;
    }

    private static async Task FocusAsync(Window window, HotkeyCaptureBox field)
    {
        await ActivateAsync(window); field.Focus(); await Task.Delay(100);
        Ensure(field.IsKeyboardFocused, "Shortcut field could not receive keyboard focus");
    }

    private static async Task ActivateAsync(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        window.Activate();
        if (!SetForegroundWindow(hwnd))
        {
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint ownThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);
            try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); }
            finally { if (attached) AttachThreadInput(ownThread, foregroundThread, false); }
        }
        await Task.Delay(100);
    }

    private static async Task PressAsync(Window window, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        // Synthetic input is sent only while our own verification window is foreground.
        Ensure(GetForegroundWindow() == new WindowInteropHelper(window).Handle && window.IsActive, $"Verification window is not foreground (foreground={GetForegroundWindow()}, own={new WindowInteropHelper(window).Handle}, active={window.IsActive}); no keys were sent");
        Ensure(Keyboard.Modifiers == ModifierKeys.None, "A physical modifier is held; no keys were sent");
        var keys = new List<Key>();
        if (modifiers.HasFlag(ModifierKeys.Control)) keys.Add(Key.LeftCtrl);
        if (modifiers.HasFlag(ModifierKeys.Alt)) keys.Add(Key.LeftAlt);
        if (modifiers.HasFlag(ModifierKeys.Shift)) keys.Add(Key.LeftShift);
        if (modifiers.HasFlag(ModifierKeys.Windows)) keys.Add(Key.LWin);
        keys.Add(key);
        var inputs = keys.Select(k => InputFor(k, false)).Concat(keys.AsEnumerable().Reverse().Select(k => InputFor(k, true))).ToArray();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            var release = keys.Select(k => InputFor(k, true)).ToArray(); SendInput((uint)release.Length, release, Marshal.SizeOf<INPUT>());
            throw new InvalidOperationException("Windows did not accept verification keystrokes");
        }
        await Task.Delay(100);
    }

    private static INPUT InputFor(Key key, bool release) => new() { Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { VirtualKey = (ushort)KeyInterop.VirtualKeyFromKey(key), Flags = release ? 2u : 0u } } };
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { internal uint Type; internal INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] internal KEYBDINPUT Keyboard; [FieldOffset(0)] internal MOUSEINPUT Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { internal ushort VirtualKey, ScanCode; internal uint Flags, Time; internal IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { internal int X, Y; internal uint Data, Flags, Time; internal IntPtr Extra; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
