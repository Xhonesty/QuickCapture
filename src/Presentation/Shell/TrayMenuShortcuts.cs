using Forms = System.Windows.Forms;

namespace QuickCapture;

internal static class TrayMenuShortcuts
{
    internal static void Apply(Settings settings, Forms.ToolStripMenuItem screenshot, Forms.ToolStripMenuItem recording, Forms.ToolStripMenuItem finishEditing)
    {
        // WM_HOTKEY already handles these combinations. Display the settings
        // without registering a second shortcut on the tray menu.
        screenshot.ShortcutKeyDisplayString = settings.ScreenshotHotkey.Replace("+", " + ");
        recording.ShortcutKeyDisplayString = finishEditing.ShortcutKeyDisplayString = settings.RecordingHotkey.Replace("+", " + ");
        screenshot.ShowShortcutKeys = recording.ShowShortcutKeys = finishEditing.ShowShortcutKeys = true;
    }
}
