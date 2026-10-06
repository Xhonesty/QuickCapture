using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace QuickCapture;

internal static class RecordingEditorThemeTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check, bool pointer = false)
    {
        string directory = Path.Combine(Paths.TestRoot!, "EditorUI"); Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "source.mp4");
        var tools = await MediaTools.DetectAsync(new Settings());
        var fixture = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=960x540:rate=30:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, CancellationToken.None);
        UpgradeTests.Ensure(fixture.ExitCode == 0, "Editor fixture generation failed");
        var metadata = new RecordingMetadata(ExportQuality.Medium, 30, true, 3);
        var owner = new Window { Width = 400, Height = 300 }; Ui.Theme(owner); owner.Show();
        try
        {
            await check("Recording editor live themes cover surfaces, dropdown popup, input, checkbox, timeline and footer", async () =>
            {
                var dialog = new RecordingEditWindow(owner, source, new Settings { OutputDirectory = directory }, metadata);
                try
                {
                    dialog.Show(); await dialog.Initialization; await Task.Delay(250);
                    foreach (string theme in new[] { "Light", "Dark", "Light" })
                    {
                        ThemeService.Apply(theme); dialog.UpdateLayout();
                        UpgradeTests.Ensure(dialog.Background == ThemeService.Brush("WindowBackground") && dialog.Foreground == ThemeService.Brush("TextPrimary"), "Open editor missed the theme change");
                        UpgradeTests.Ensure(Find<TextBlock>(dialog).Count(b => b.Text == "录屏编辑与导出") == 1 && !Find<Image>(Find<DockPanel>(dialog).Single(p => p.Name == "RecordingCaption")).Any(), "Caption still displays branding");
                        var folder = Named<TextBox>(dialog, "RecordingFolder");
                        UpgradeTests.Ensure(folder.Background == ThemeService.Brush("InputBackground") && folder.Foreground == ThemeService.Brush("TextPrimary"), "Input stayed in previous theme");
                        var checkbox = Find<CheckBox>(dialog).Single(); checkbox.IsChecked = true; dialog.UpdateLayout();
                        UpgradeTests.Ensure(Find<Border>(checkbox).Any(b => b.Background == ThemeService.Brush("Accent")), "Checked checkbox is not themed"); checkbox.IsChecked = false;
                        var combo = Named<ComboBox>(dialog, "RecordingFormat"); combo.IsDropDownOpen = true; await Task.Delay(80);
                        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                        var popupBorder = (Border)popup.Child;
                        UpgradeTests.Ensure(popupBorder.Background == ThemeService.Brush("PanelBackground") && Find<ComboBoxItem>(popupBorder).All(b => b.Foreground == ThemeService.Brush("TextPrimary") || b.Foreground == ThemeService.Brush("Accent")), "Popup or list items missed the theme");
                        UiChangeTests.RenderElement(popupBorder, "editor-dropdown-" + theme.ToLowerInvariant() + ".png"); combo.IsDropDownOpen = false;
                        UpgradeTests.Ensure(Find<Border>(dialog.Timeline).Any(b => b.Background == ThemeService.Brush("TimelineTrack")), "Timeline track missed the theme");
                        UiChangeTests.Render(dialog, "editor-ready-" + theme.ToLowerInvariant() + ".png");
                        dialog.BeginCrop(); dialog.CropOverlay.SetSelection(new Rect(100, 50, 640, 360)); dialog.UpdateLayout();
                        UpgradeTests.Ensure(!Named<Button>(dialog, "ExportRecording").IsEnabled && Named<Button>(dialog, "ConfirmVideoCrop").Background == ThemeService.Brush("Accent"), "Crop state or primary confirmation changed");
                        UiChangeTests.Render(dialog, "editor-crop-" + theme.ToLowerInvariant() + ".png"); dialog.CancelCrop();
                        UpgradeTests.Ensure(dialog.AppliedCrop == null && Named<Button>(dialog, "ExportRecording").IsEnabled, "Cancel changed the applied crop or export state");
                        dialog.BeginCrop(); dialog.CropOverlay.SetSelection(new Rect(100, 50, 640, 360)); dialog.ConfirmCrop();
                        UpgradeTests.Ensure(dialog.AppliedCrop == new VideoCrop(100, 50, 640, 360), "Confirm lost source coordinates");
                        dialog.BeginCrop(); dialog.ResetCrop(); dialog.ConfirmCrop();
                    }
                }
                finally { dialog.Close(); }
            });
            await check("Recording button state rendering distinguishes normal, hover, pressed, selection, keyboard focus and disabled in both themes", async () =>
            {
                var dialog = new RecordingEditWindow(owner, source, new Settings { OutputDirectory = directory }, metadata);
                try
                {
                    dialog.Show(); await dialog.Initialization; await Task.Delay(150);
                    foreach (string theme in new[] { "Light", "Dark" })
                    {
                        ThemeService.Apply(theme);
                        var button = Find<Button>(dialog).Single(b => b.Content as string == "重置选段");
                        var old = System.Windows.Forms.Cursor.Position;
                        try
                        {
                            // Exercise the actual WPF template independently of OS input
                            // permissions. --pointer additionally verifies native delivery.
                            SetState(button, typeof(UIElement), "IsMouseOverPropertyKey", false);
                            Named<TextBox>(dialog, "RecordingFolder").Focus(); dialog.UpdateLayout(); Color normal = ColorOf(button.Background);
                            UpgradeTests.Ensure(normal != ColorOf(dialog.Background) && normal != ColorOf(ThemeService.Brush("PanelBackground")), "Neutral button merges into the page/card");
                            UiChangeTests.RenderElement(button, "editor-button-normal-" + theme.ToLowerInvariant() + ".png");
                            button.Tag = "selected"; dialog.UpdateLayout(); Color selected = ColorOf(button.Background);
                            UiChangeTests.RenderElement(button, "editor-button-selected-" + theme.ToLowerInvariant() + ".png"); button.Tag = null;
                            button.Focus(); dialog.UpdateLayout();
                            UpgradeTests.Ensure(button.IsKeyboardFocused && ((Border)button.Template.FindName("FocusRing", button)).Visibility == Visibility.Visible, "Keyboard focus is invisible");
                            UiChangeTests.RenderElement(button, "editor-button-focus-" + theme.ToLowerInvariant() + ".png");
                            var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                            SetState(button, typeof(UIElement), "IsMouseOverPropertyKey", true); dialog.UpdateLayout(); Color hover = ColorOf(button.Background);
                            UiChangeTests.RenderElement(button, "editor-button-hover-" + theme.ToLowerInvariant() + ".png"); Color pressed = normal;
                            SetState(button, typeof(ButtonBase), "IsPressedPropertyKey", true); dialog.UpdateLayout(); pressed = ColorOf(button.Background);
                            UiChangeTests.RenderElement(button, "editor-button-pressed-" + theme.ToLowerInvariant() + ".png");
                            SetState(button, typeof(ButtonBase), "IsPressedPropertyKey", false);
                            SetState(button, typeof(UIElement), "IsMouseOverPropertyKey", false);
                            if (pointer) await HotkeyCaptureTests.HoldPointerAsync(dialog, center, () =>
                            { UpgradeTests.Ensure(button.IsPressed, "Real pointer did not press the button"); return Task.CompletedTask; });
                            button.IsEnabled = false; dialog.UpdateLayout(); Color disabled = ColorOf(button.Background);
                            UiChangeTests.RenderElement(button, "editor-button-disabled-" + theme.ToLowerInvariant() + ".png");
                            UpgradeTests.Ensure(new[] { normal, selected, hover, pressed, disabled }.Distinct().Count() == 5 && button.Opacity == 1, "Button states merge or disabled surface disappears"); button.IsEnabled = true;
                            var export = Named<Button>(dialog, "ExportRecording");
                            UpgradeTests.Ensure(export.Background == ThemeService.Brush("Accent") && export.Foreground == ThemeService.Brush("AccentForeground"), "Primary export is not accented");
                        }
                        finally { System.Windows.Forms.Cursor.Position = old; }
                    }
                }
                finally { dialog.Close(); }
            });
            await check("Unbranded recording caption retains native caption and resize hit targets, minimize, maximize, restore and close", async () =>
            {
                var dialog = new RecordingEditWindow(owner, source, new Settings { OutputDirectory = directory }, metadata);
                try
                {
                    dialog.Show(); await dialog.Initialization;
                    UpgradeTests.Ensure(WindowChrome.GetWindowChrome(dialog) is { CaptionHeight: 32 } && dialog.ResizeMode == ResizeMode.CanResize, "Caption resize hit testing missing");
                    double left = dialog.Left, top = dialog.Top;
                    Point caption = dialog.PointToScreen(new Point(150, 18));
                    UpgradeTests.Ensure(HitTest(dialog, caption) == 2, "Native caption drag target is missing");
                    double width = dialog.ActualWidth;
                    Point edge = dialog.PointToScreen(new Point(width - 2, 150));
                    UpgradeTests.Ensure(HitTest(dialog, edge) == 11, "Native right resize target is missing");
                    if (pointer)
                    {
                        await HotkeyCaptureTests.DragAsync(dialog, caption, caption + new Vector(40, 25));
                        UpgradeTests.Ensure(Math.Abs(dialog.Left - left) > 10 && Math.Abs(dialog.Top - top) > 10, "Native caption does not drag");
                        edge = dialog.PointToScreen(new Point(width - 2, 150)); await HotkeyCaptureTests.DragAsync(dialog, edge, edge + new Vector(40, 0));
                        UpgradeTests.Ensure(dialog.ActualWidth > width + 10, "Native resize border does not resize");
                    }
                    Click(Named<Button>(dialog, "RecordingMaximize")); await Task.Delay(100); UpgradeTests.Ensure(dialog.WindowState == WindowState.Maximized, "Maximize failed");
                    Click(Named<Button>(dialog, "RecordingMaximize")); await Task.Delay(100); UpgradeTests.Ensure(dialog.WindowState == WindowState.Normal, "Restore failed");
                    Click(Named<Button>(dialog, "RecordingMinimize")); await Task.Delay(100); UpgradeTests.Ensure(dialog.WindowState == WindowState.Minimized, "Minimize failed");
                    SystemCommands.RestoreWindow(dialog); await Task.Delay(100); Click(Named<Button>(dialog, "RecordingClose")); await Task.Delay(100);
                    UpgradeTests.Ensure(!dialog.IsVisible && File.Exists(source), "Close failed or removed the original");
                }
                finally { dialog.Close(); }
            });
            await check("Themed editor export reports errors, cancels cleanly, progresses and saves actual MP4 / WebM / GIF", async () =>
            {
                foreach (var format in Enum.GetValues<RecordingFormat>())
                {
                    ThemeService.Apply(format == RecordingFormat.WebM ? "Dark" : "Light");
                    var dialog = new RecordingEditWindow(owner, source, new Settings { OutputDirectory = directory, RecordingFormat = format }, metadata);
                    await UpgradeTests.WithDialogAsync(dialog, async () =>
                    {
                        await dialog.Initialization;
                        var name = Named<TextBox>(dialog, "RecordingFilename"); name.Text = "invalid/name"; await dialog.ExportAsync();
                        UpgradeTests.Ensure(dialog.IsVisible && Find<TextBlock>(dialog).Any(b => b.Text.Contains("导出失败") && b.Foreground == ThemeService.Brush("Danger")), "Export validation error is not themed");
                        UiChangeTests.Render(dialog, "editor-error-" + format + ".png");
                        name.Text = "ui-export-" + format + "-" + Guid.NewGuid().ToString("N");
                        dialog.BeginCrop(); dialog.CropOverlay.SetSelection(new Rect(100, 50, 640, 360)); dialog.ConfirmCrop(); dialog.Timeline.SetRange(.4, 2.4);
                        if (format == RecordingFormat.WebM)
                        {
                            Task cancelled = dialog.ExportAsync(); await Task.Delay(30);
                            UpgradeTests.Ensure(!Named<Button>(dialog, "ExportRecording").IsEnabled && Find<ProgressBar>(dialog).Single().Foreground == ThemeService.Brush("Accent"), "Export running controls/progress missed theme");
                            UiChangeTests.Render(dialog, "editor-exporting-dark.png"); Click(Named<Button>(dialog, "CancelExport")); await cancelled;
                            UpgradeTests.Ensure(dialog.SavedPath == null && !Directory.GetFiles(directory, "*.partial*").Any() && File.Exists(source) && Named<Button>(dialog, "ExportRecording").IsEnabled, "Cancellation lost source or left export disabled");
                        }
                        await dialog.ExportAsync();
                        UpgradeTests.Ensure(dialog.SavedPath != null && File.Exists(dialog.SavedPath) && Find<ProgressBar>(dialog).Single().Value == 1, "UI export did not save or finish progress");
                        var info = await VideoExportService.ProbeAsync(dialog.SavedPath!, tools);
                        UpgradeTests.Ensure(info.Width == 640 && info.Height == 360 && Math.Abs(info.Duration - 2) < .15 && info.HasAudio == (format != RecordingFormat.Gif), $"UI export {format}: {info}");
                    });
                }
            });
        }
        finally { owner.Close(); ThemeService.Apply("Dark"); }
    }
    private static System.Drawing.Point ToPixel(Point point) => new((int)Math.Round(point.X), (int)Math.Round(point.Y));
    private static void SetState(DependencyObject element, Type owner, string field, bool value)
    {
        var key = (DependencyPropertyKey)owner.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        element.SetValue(key, value);
    }
    private static int HitTest(Window window, Point point)
    {
        int packed = unchecked(((int)Math.Round(point.Y) << 16) | ((int)Math.Round(point.X) & 0xFFFF));
        return (int)SendMessage(new System.Windows.Interop.WindowInteropHelper(window).Handle, 0x84, IntPtr.Zero, new IntPtr(packed));
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    private static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Find<T>(root).Single(b => b.Name == name);
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    { if (root is T item) yield return item; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
}
