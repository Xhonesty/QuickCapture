using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace QuickCapture;

internal static class RecordingReeditTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "RecordingReedit"); Directory.CreateDirectory(directory);
        var tools = await MediaTools.DetectAsync(new Settings());
        string source = Path.Combine(directory, "saved-"+Guid.NewGuid().ToString("N")+".mp4");
        var generated = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "color=c=red:s=960x540:r=30:d=1", "-f", "lavfi", "-i", "color=c=lime:s=960x540:r=30:d=1",
            "-f", "lavfi", "-i", "color=c=blue:s=960x540:r=30:d=1", "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]", "-map", "[v]", "-map", "3:a", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, CancellationToken.None);
        Ensure(generated.ExitCode == 0, "Fixture generation failed");
        var metadata = new RecordingMetadata(ExportQuality.Medium, 30, true, 3);
        var owner = new Window { Width = 300, Height = 200 }; Ui.Theme(owner); owner.Show();
        try
        {
            await check("Recording editor fits one screen at default/minimum sizes in both themes, including expanded crop controls", async () =>
            {
                var dialog = new RecordingEditWindow(owner, source, new Settings { OutputDirectory = directory }, metadata);
                try
                {
                    dialog.Show(); await dialog.Initialization; await Task.Delay(250);
                    foreach (string theme in new[] { "Light", "Dark" })
                    foreach (var size in new[] { new Size(880, 820), new Size(680, 600) })
                    {
                        ThemeService.Apply(theme); dialog.Width = size.Width; dialog.Height = size.Height; dialog.UpdateLayout();
                        foreach (bool cropping in new[] { false, true })
                        {
                            if (cropping) dialog.BeginCrop(); else dialog.CancelCrop(); dialog.UpdateLayout();
                            var content = Named<Grid>(dialog, "RecordingContent");
                            Ensure(content.Parent is DockPanel && content.ActualHeight > 0, "Editor still uses outer scrolling");
                            foreach (var element in new FrameworkElement[] { Named<TextBox>(dialog, "RecordingFolder"), Named<TextBox>(dialog, "RecordingFilename"), Named<ComboBox>(dialog, "RecordingFormat"), dialog.Timeline })
                                Ensure(Contains(content, element), element.Name + " is clipped");
                            foreach (string name in cropping ? new[] { "ConfirmVideoCrop", "CancelVideoCrop" } : new[] { "EditVideoCrop" })
                                Ensure(Contains(content, Named<Button>(dialog, name)), name + " is clipped");
                            Ensure(Contains(dialog, Named<Button>(dialog, "ExportRecording")), "Footer is clipped");
                            var viewport = Named<Grid>(dialog, "RecordingViewport");
                            Ensure(viewport.ActualHeight >= 40, $"Preview collapsed at {size}: {viewport.ActualHeight:F1}");
                            UiChangeTests.Render(dialog, $"recording-compact-{theme.ToLowerInvariant()}-{(int)size.Height}-{(cropping ? "crop" : "ready")}.png");
                        }
                    }
                }
                finally { dialog.Close(); }
            });
            await check("Trim start/end preview updates actual paused video frames and keeps the final frame visible", async () =>
            {
                var dialog = new RecordingEditWindow(owner, source, new Settings { OutputDirectory = directory }, metadata);
                try
                {
                    dialog.Show(); await dialog.Initialization;
                    var preview = Named<MediaElement>(dialog, "RecordingPreview"); await Ready(preview);
                    var start = Named<Thumb>(dialog.Timeline, "TrimStart"); var end = Named<Thumb>(dialog.Timeline, "TrimEnd");
                    foreach (var sample in new[] { (time: .5, handle: start, channel: 2), (time: 1.5, handle: end, channel: 1), (time: 2.5, handle: end, channel: 0) })
                    {
                        dialog.Timeline.SetRange(sample.handle == start ? sample.time : .5, sample.handle == end ? sample.time : 2.9);
                        sample.handle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                        sample.handle.RaiseEvent(new DragDeltaEventArgs(0, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                        sample.handle.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                        await Task.Delay(450); dialog.UpdateLayout();
                        Ensure(preview.ScrubbingEnabled && Math.Abs(preview.Position.TotalSeconds - sample.time) < .1, $"Preview seeks to the wrong boundary: expected={sample.time:F3}, actual={preview.Position.TotalSeconds:F3}, range={dialog.Timeline.Start:F3}-{dialog.Timeline.End:F3}, scrubbing={preview.ScrubbingEnabled}");
                        var viewport = Named<Grid>(dialog, "RecordingViewport");
                        // Inspect the actual WPF video visual independently of desktop z-order/input restrictions.
                        string filename = $"trim-preview-{sample.time:F1}.png"; UiChangeTests.RenderElement(viewport, filename);
                        var image = CaptureService.Load(Path.Combine(UiChangeTests.PreviewDirectory, filename));
                        int x = image.PixelWidth / 2, y = image.PixelHeight / 2;
                        byte[] pixels = UpgradeTests.Bytes(image); int pixel = (y * image.PixelWidth + x) * 4;
                        Ensure(pixels[pixel + sample.channel] > 200 && Enumerable.Range(0, 3).Where(c => c != sample.channel).All(c => pixels[pixel + c] < 50), $"Paused frame at {sample.time}s did not refresh: BGR {pixels[pixel]}/{pixels[pixel + 1]}/{pixels[pixel + 2]}");
                    }
                    dialog.Timeline.SetRange(.5, 3);
                    end.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent }); await Task.Delay(200);
                    Ensure(preview.Position.TotalSeconds < 3 && preview.Position.TotalSeconds > 2.9, "Final boundary sought past the final frame");
                }
                finally { dialog.Close(); }
            });
            await check("Saved MP4/WebM/GIF reopen from the recent menu, export edited copies and reopen again without changing originals", async () =>
            {
                var main = new MainWindow();
                var settings = (Settings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                settings.OutputDirectory = directory; main.Show();
                try
                {
                    Ensure(main.FindName("EditRecordingButton") == null, "Removed top edit button returned");
                    foreach (RecordingFormat format in Enum.GetValues<RecordingFormat>())
                    {
                        string input = source;
                        if (format != RecordingFormat.Mp4)
                        {
                            input = Path.ChangeExtension(source, VideoExportService.Extension(format)); if (File.Exists(input)) File.Delete(input);
                            await VideoExportService.ExportAsync(new(source, input, format, ExportQuality.Medium, 0, 3), await VideoExportService.ProbeAsync(source, tools), tools);
                        }
                        byte[] hash = SHA256.HashData(File.ReadAllBytes(input));
                        Invoke(main, "RefreshRecent", input); main.UpdateLayout(); await Task.Delay(80);
                        var list = (ListBox)main.FindName("RecentList"); var item = list.Items.OfType<RecentItem>().Single(i => i.Path == input); list.ScrollIntoView(item); main.UpdateLayout();
                        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item);
                        row.ContextMenu!.IsOpen = true; await Task.Delay(50);
                        var edit = row.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Header as string == "编辑录屏");
                        Ensure(edit.IsVisible && edit.IsEnabled, "Saved video has no edit menu action"); row.ContextMenu.IsOpen = false;
                        string? saved = null;
                        await OpenAndInteract(() => edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)), async dialog =>
                        {
                            Ensure(Named<TextBox>(dialog, "RecordingFolder").Text == directory && Named<TextBox>(dialog, "RecordingFilename").Text.Contains("-编辑"), "Reedit does not default to a separate copy");
                            await Ready(Named<MediaElement>(dialog, "RecordingPreview"));
                            if (format != RecordingFormat.Mp4) Ensure(Named<MediaElement>(dialog, "RecordingPreview").Source.LocalPath != input, "Unsupported media did not receive a compatible preview");
                            dialog.BeginCrop(); dialog.CropOverlay.SetSelection(new Rect(100, 50, 640, 360)); dialog.ConfirmCrop(); dialog.Timeline.SetRange(.6, 2.2);
                            Named<TextBox>(dialog, "RecordingFilename").Text = "reedit-" + format + "-" + Guid.NewGuid().ToString("N") + ".mp4";
                            await dialog.ExportAsync(); saved = dialog.SavedPath; Ensure(saved != null, "Reedit export failed");
                        });
                        var info = await VideoExportService.ProbeAsync(saved!, tools);
                        Ensure(info.Width == 640 && info.Height == 360 && Math.Abs(info.Duration - 1.6) < .15, "Reedited output lost trim/crop");
                        Ensure(SHA256.HashData(File.ReadAllBytes(input)).SequenceEqual(hash) && !File.Exists(input + ".json"), "Imported original or metadata was modified");
                        Ensure(list.Items.OfType<RecentItem>().Any(i => i.Path == saved), "Edited copy was not added to recent files");
                        await OpenAndInteract(() => Invoke(main, "EditSavedRecording", saved!), dialog => { dialog.Close(); return Task.CompletedTask; });
                        Ensure(File.Exists(saved) && SHA256.HashData(File.ReadAllBytes(input)).SequenceEqual(hash), "Cancelling a reopened export deleted a file");
                    }
                    await Task.Delay(500);
                    string previews = Path.Combine(Paths.Data, "RecordingPreviews");
                    Ensure(!Directory.Exists(previews) || !Directory.GetFiles(previews, "*.mp4").Any(), "Temporary compatible previews were not cleaned up");
                }
                finally { main.Hide(); }
            });
            await check("Reediting rejects original-file overwrite and damaged recordings without changing the source", async () =>
            {
                var dialog = new RecordingEditWindow(owner, source, new Settings { OutputDirectory = directory }, metadata);
                try
                {
                    dialog.Show(); await dialog.Initialization; byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
                    Named<TextBox>(dialog, "RecordingFilename").Text = Path.GetFileName(source); await dialog.ExportAsync();
                    Ensure(dialog.SavedPath == null && RecordingEditorThemeTestsText(dialog, "不能覆盖") && SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(hash), "Source overwrite protection failed");
                }
                finally { dialog.Close(); }
                string damaged = Path.Combine(directory, "damaged.mp4"); File.WriteAllText(damaged, "invalid media");
                var invalid = new RecordingEditWindow(owner, damaged, new Settings { OutputDirectory = directory }, new(ExportQuality.Medium, 30, true, 0));
                try { invalid.Show(); await invalid.Initialization; Ensure(!Named<Button>(invalid, "ExportRecording").IsEnabled && RecordingEditorThemeTestsText(invalid, "无法打开录屏"), "Damaged input can be exported as a recording"); }
                finally { invalid.Close(); }
            });
        }
        finally { owner.Close(); ThemeService.Apply("Dark"); }
    }
    private static async Task OpenAndInteract(Action open, Func<RecordingEditWindow, Task> interaction)
    {
        var done = new TaskCompletionSource();
        _ = Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            RecordingEditWindow? dialog = null;
            try
            {
                await Task.Delay(120); dialog = Application.Current.Windows.OfType<RecordingEditWindow>().Single(w => w.IsVisible);
                await dialog.Initialization; await interaction(dialog); done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (dialog?.IsVisible == true) dialog.Close(); }
        }));
        open(); await done.Task;
    }
    private static async Task Ready(MediaElement preview)
    { for (int i = 0; i < 50 && preview.NaturalVideoWidth == 0; i++) await Task.Delay(100); Ensure(preview.NaturalVideoWidth > 0, "Video preview did not open"); }
    private static bool Contains(FrameworkElement parent, FrameworkElement child)
    { Rect box = child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize)); return box.Width > 0 && box.Height > 0 && box.Left >= -.5 && box.Top >= -.5 && box.Right <= parent.ActualWidth + .5 && box.Bottom <= parent.ActualHeight + .5; }
    private static bool RecordingEditorThemeTestsText(DependencyObject root, string text) => Visuals<TextBlock>(root).Any(b => b.Text.Contains(text));
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Visuals<T>(root).Single(e => e.Name == name);
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    { if (root is T item) yield return item; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static void Invoke(MainWindow main, string method, string argument) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new object[] { argument });
    private static void Ensure(bool condition, string message) => UpgradeTests.Ensure(condition, message);
}
