using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using Forms = System.Windows.Forms;

namespace QuickCapture;

internal static class ReleaseV1Tests
{
    private static void Ensure(bool condition, string message) => UpgradeTests.Ensure(condition, message);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static T Find<T>(DependencyObject root, Predicate<T> predicate) where T : DependencyObject => UpgradeTests.Find(root, predicate);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Tray displays current configured shortcuts and leaves unbound actions empty", async () =>
        {
            var main = new MainWindow(); main.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try
            {
                var menu = Field<Forms.NotifyIcon>(main, "_tray").ContextMenuStrip!;
                var settings = Field<Settings>(main, "_settings");
                void Verify()
                {
                    typeof(Forms.ToolStripDropDown).GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(menu, new object[] { new CancelEventArgs() });
                    var items = menu.Items.OfType<Forms.ToolStripMenuItem>().ToArray();
                    Ensure(items.Single(i => i.Text == "截图").ShortcutKeyDisplayString == settings.ScreenshotHotkey.Replace("+", " + "), "Screenshot shortcut differs from settings");
                    foreach (var label in new[] { "开始录屏", "结束并编辑" }) Ensure(items.Single(i => i.Text == label).ShortcutKeyDisplayString == settings.RecordingHotkey.Replace("+", " + "), "Recording shortcut missing");
                    Ensure(items.Where(i => i.Text is not ("截图" or "开始录屏" or "结束并编辑")).All(i => string.IsNullOrEmpty(i.ShortcutKeyDisplayString)), "Unbound action has a misleading shortcut");
                    Ensure(items.All(i => i.ShortcutKeys == Forms.Keys.None), "Tray created duplicate shortcut handlers");
                }
                Verify(); settings.ScreenshotHotkey = "Ctrl+Shift+7"; settings.RecordingHotkey = "Ctrl+Alt+Shift+F12"; Verify();
                menu.Show(80, 80); Forms.Application.DoEvents();
                using var bitmap = new System.Drawing.Bitmap(menu.Width, menu.Height); menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, menu.Width, menu.Height));
                bitmap.Save(Path.Combine(UiChangeTests.PreviewDirectory, "release-v1-tray.png"), System.Drawing.Imaging.ImageFormat.Png); menu.Close();
            }
            finally { main.Close(); }
        });

        string directory = Path.Combine(Paths.TestRoot!, "ReleaseV1", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "source.mp4");
        var tools = await MediaTools.DetectAsync(new Settings { MediaToolsPath = Path.Combine(AppContext.BaseDirectory, "Tools", "media") }, includeSystem: false, includeBundled: false);
        var fixture = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=320x180:r=10:d=1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source }, CancellationToken.None); Ensure(fixture.ExitCode == 0, fixture.Error);
        var info = await VideoExportService.ProbeAsync(source, tools);
        var originals = new[] { new VideoObject("arrow", VideoObjectKind.Arrow, 32, 32, 160, 48, 0, 1, 0, Color: "#00FF00", Stroke: 6), new VideoObject("line", VideoObjectKind.Line, 32, 120, 160, 1, 0, 1, 1, Color: "#00FF00", Stroke: 6) };
        VideoObject[] final = Array.Empty<VideoObject>();
        await check("Arrow and line outline controls, draft, undo redo, alpha and project reopen", async () =>
        {
            var owner = new Window { Width = 200, Height = 120, ShowInTaskbar = false }; Ui.Theme(owner); owner.Show();
            RecordingEditWindow? dialog = new(owner, source, new Settings(), new(ExportQuality.High, 10, false, 1)); dialog.Show(); await dialog.Initialization;
            try
            {
                var editor = dialog.ObjectEditor;
                foreach (var item in originals) editor.Add(item);
                editor.RestoreDefaults(originals);
                void Outline()
                {
                    dialog.UpdateLayout();
                    Find<CheckBox>(editor.Toolbar.PropertyPanel, c => Equals(c.Content, "描边")).IsChecked = true;
                    Find<ComboBox>(editor.Toolbar.PropertyPanel, c => c.ToolTip is string s && s.StartsWith("描边粗细", StringComparison.Ordinal)).Text = "6"; editor.FlushProperties();
                    Click(Find<Button>(editor.Toolbar.PropertyPanel, b => b.Name == "VideoOutlineColor"));
                    editor.Palette!.PreviewColor(Color.FromArgb(128, 255, 0, 0)); editor.FinishColor(true);
                }
                foreach (var original in originals)
                {
                    editor.Select(editor.Objects.Single(o => o.Id == original.Id), false); Outline();
                    var after = editor.Objects.Single(o => o.Id == original.Id);
                    Ensure(after.OutlineEnabled && after.OutlineWidth == 6 && after.OutlineColor == "#FF0000" && Math.Abs(after.OutlineAlpha - 128 / 255d) < .00001 && after.Color == original.Color && after.Stroke == 6, "Independent outline settings lost");
                    dialog.UndoEdit(); Ensure(editor.Objects.Single(o => o.Id == original.Id) != after, "Outline change did not undo"); dialog.RedoEdit(); Ensure(editor.Objects.Single(o => o.Id == original.Id) == after, "Outline change did not redo");
                    editor.Select(after, false); Click(Find<Button>(editor.Toolbar.PropertyPanel, b => b.Name == "VideoOutlineColor")); editor.Palette!.PreviewColor(Colors.Blue); editor.FinishColor(false);
                    Ensure(editor.Objects.Single(o => o.Id == original.Id) == after, "Cancelled outline color did not roll back");
                    editor.SetTool(original.Kind); Outline();
                    Ensure(editor.Overlay.DraftStyle is { OutlineEnabled: true, OutlineWidth: 6 }, "New arrow/line draft lost its outline");
                    Set(editor.Overlay, "_anchor", new Point(original.X, original.Y)); Set(editor.Overlay, "_end", new Point(original.X + original.Width, original.Y + original.Height)); Set(editor.Overlay, "_dragging", true);
                    editor.Overlay.InvalidateVisual(); await Dispatcher.Yield(DispatcherPriority.Render); dialog.UpdateLayout();
                    var draft = new RenderTargetBitmap((int)Math.Ceiling(editor.Overlay.ActualWidth), (int)Math.Ceiling(editor.Overlay.ActualHeight), 96, 96, PixelFormats.Pbgra32); draft.Render(editor.Overlay);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(draft)); using (var output = File.Create(Path.Combine(UiChangeTests.PreviewDirectory, "release-v1-draft-" + original.Kind.ToString().ToLowerInvariant() + ".png"))) png.Save(output);
                    Ensure(Colored(UpgradeTests.Bytes(draft), true) > 20 && Colored(UpgradeTests.Bytes(draft), false) > 20, "Draft body or outline did not render");
                    Set(editor.Overlay, "_dragging", false); editor.SetTool(null);
                }
                final = editor.Objects.ToArray(); editor.Select(final[0], false); dialog.UpdateLayout(); UiChangeTests.Render(dialog, "release-v1-arrow-outline.png");
                await dialog.ConfigurationSaved; dialog.Close();
                dialog = new(owner, source, new Settings(), new(ExportQuality.High, 10, false, 1)); dialog.Show(); await dialog.Initialization;
                Ensure(dialog.ObjectEditor.Objects.SequenceEqual(final), "Reopened project lost arrow/line outline settings");
                Ensure(dialog.ObjectEditor.ToolDefaults.Length == 2 && dialog.ObjectEditor.ToolDefaults.All(o => o.OutlineEnabled && o.OutlineWidth == 6), "Reopened project lost outline tool defaults");
            }
            finally { if (dialog != null) { await dialog.ConfigurationSaved; dialog.Close(); } owner.Close(); }
        });
        await check("Outlined arrow and line preview pixels and actual MP4 export", async () =>
        {
            Ensure(final.Length == 2, "Outline UI check did not produce a valid project");
            foreach (var item in final)
            {
                var raw = new byte[320 * 180 * 4]; for (int p = 3; p < raw.Length; p += 4) raw[p] = 255;
                VideoObjectRenderer.Render(raw, info, new[] { item }, .2, new Dictionary<string, SKBitmap>());
                Ensure(Colored(raw, true) > 30 && Colored(raw, false) > 30, "Compositor is missing the body or outline: " + item.Kind);
                var bodyOnly = (byte[])raw.Clone(); Array.Clear(bodyOnly); for (int p = 3; p < bodyOnly.Length; p += 4) bodyOnly[p] = 255;
                VideoObjectRenderer.Render(bodyOnly, info, new[] { item with { OutlineEnabled = false } }, .2, new Dictionary<string, SKBitmap>());
                Ensure(Colored(bodyOnly, true) == 0 && Colored(bodyOnly, false) > 30, "Disabling the outline changed the body: " + item.Kind);
            }
            string target = Path.Combine(directory, "outlined.mp4"); await VideoExportService.ExportAsync(new(source, target, RecordingFormat.Mp4, ExportQuality.High, 0, 1, Objects: final), info, tools);
            using var reader = new VideoFrameReader(tools.Ffmpeg!, target, 320, 180, 10); var pixels = (await reader.FrameAsync(.2, CancellationToken.None))!;
            Ensure(Colored(pixels, true) > 60 && Colored(pixels, false) > 60, "Encoded outlines or bodies are missing");
        });
    }
    private static int Colored(byte[] pixels, bool red)
    {
        int count = 0;
        for (int p = 0; p < pixels.Length; p += 4) if (red ? pixels[p + 2] > 70 && pixels[p + 1] < 45 && pixels[p] < 45 : pixels[p + 1] > 170 && pixels[p + 2] < 45 && pixels[p] < 45) count++;
        return count;
    }
}
