using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace QuickCapture;

// Run against real WPF windows, MediaElement decoding, persisted edits and FFmpeg.
internal static class RecordingTimelineTests
{
    private static async Task<string> Fixture(string directory, int width, int height)
    {
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, $"aspect-{width}x{height}.mp4");
        if (!File.Exists(source))
        {
            var tools = await MediaTools.DetectAsync(new Settings());
            var result = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30:duration=6", "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, CancellationToken.None);
            Ensure(result.ExitCode == 0, "Fixture generation failed: " + result.Error);
        }
        return source;
    }
    internal static async Task ShowDemoAsync(string[] args)
    {
        Paths.TestRoot = Path.Combine(AppContext.BaseDirectory, "Diagnostics", "EditorDemo");
        var aspect = args.FirstOrDefault(a => a.StartsWith("--aspect=")) ?? "--aspect=landscape";
        var size = aspect.EndsWith("portrait") ? (540, 960) : aspect.EndsWith("square") ? (600, 600) : (960, 540);
        string source = await Fixture(Paths.TestRoot, size.Item1, size.Item2);
        ThemeService.Apply(args.Contains("--dark") ? "Dark" : "Light");
        var owner = new Window { Width = 1, Height = 1, Left = -10000, Top = -10000, ShowInTaskbar = false };
        Ui.Theme(owner); owner.Show();
        var dialog = new RecordingEditWindow(owner, source, new Settings { OutputDirectory = Paths.TestRoot }, new(ExportQuality.Medium, 30, true, 6));
        dialog.Closed += (_, _) => { owner.Close(); Application.Current.Shutdown(); };
        dialog.Show(); dialog.Activate(); await dialog.Initialization;
    }
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "TimelineLayout");
        var owner = new Window { Width = 320, Height = 200 }; Ui.Theme(owner); owner.Show();
        try
        {
            await check("Video aspect layout covers landscape portrait square, cropped ratios, cancel, resize and maximized in both themes", async () =>
            {
                foreach (var size in new[] { (960, 540), (540, 960), (600, 600) })
                {
                    string source = await Fixture(directory, size.Item1, size.Item2);
                    RecordingEditStore.Remove(source);
                    var dialog = new RecordingEditWindow(owner, source, new Settings { OutputDirectory = directory }, new(ExportQuality.Medium, 30, true, 6));
                    try
                    {
                        dialog.Show(); await dialog.Initialization; await Ready(dialog);
                        foreach (string theme in new[] { "Light", "Dark" })
                        {
                            ThemeService.Apply(theme); await Task.Delay(100); dialog.UpdateLayout();
                            AssertLayout(dialog, size.Item1 / (double)size.Item2);
                            var caption = Named<DockPanel>(dialog, "RecordingCaption");
                            var title = Named<TextBlock>(dialog, "RecordingCaptionTitle");
                            Ensure(title.Parent == caption && title.Margin.Left == 16, "Title is outside caption or misaligned");
                            var outline = Named<Border>(dialog, "RecordingWindowOutline");
                            Ensure(outline.BorderBrush == ThemeService.Brush("RecordingWindowBorder") && outline.BorderThickness == new Thickness(1), "Outer border missed theme");
                            foreach (var windowSize in new[] { new Size(680, 600), new Size(1000, 800) })
                            {
                                dialog.Width = windowSize.Width; dialog.Height = windowSize.Height; dialog.UpdateLayout(); AssertLayout(dialog, size.Item1 / (double)size.Item2);
                            }
                            dialog.WindowState = WindowState.Maximized; await Task.Delay(120); AssertLayout(dialog, size.Item1 / (double)size.Item2);
                            UiChangeTests.Render(dialog, $"timeline-{size.Item1}x{size.Item2}-{theme.ToLowerInvariant()}-maximized.png");
                            dialog.WindowState = WindowState.Normal;
                            dialog.BeginCrop(); dialog.CropOverlay.SetAspectRatio(1); dialog.ConfirmCrop(); await Task.Delay(120); AssertLayout(dialog, 1);
                            var crop = dialog.AppliedCrop;
                            dialog.BeginCrop(); dialog.CropOverlay.SetAspectRatio(9d / 16); dialog.CancelCrop(); await Task.Delay(120); Ensure(dialog.AppliedCrop == crop, "Cancel lost applied crop"); AssertLayout(dialog, 1);
                            dialog.BeginCrop(); dialog.ResetCrop(); dialog.ConfirmCrop(); await Task.Delay(120); AssertLayout(dialog, size.Item1 / (double)size.Item2);
                            UiChangeTests.Render(dialog, $"timeline-{size.Item1}x{size.Item2}-{theme.ToLowerInvariant()}.png");
                        }
                    }
                    finally { dialog.Close(); await dialog.ConfigurationSaved; }
                }
            });
            string landscape = await Fixture(directory, 960, 540);
            await check("Direct red handles enforce retained/frame bounds, latest paused seek, cancel, multicut confirm, adjust, restore, undo redo and persisted state", async () =>
            {
                RecordingEditStore.Remove(landscape);
                var dialog = new RecordingEditWindow(owner, landscape, new Settings { OutputDirectory = directory }, new(ExportQuality.Medium, 30, true, 6));
                try
                {
                    dialog.Show(); await dialog.Initialization; await Ready(dialog);
                    Ensure(!All<Expander>(dialog).Any(), "Deletion is still folded");
                    Click(Named<Button>(dialog, "DeleteMiddleSegment"));
                    Ensure(dialog.Timeline.Draft == new VideoRange(2, 4), "Default deletion is not centered and valid");
                    Ensure(Named<Thumb>(dialog, "DeleteStartHandle").Visibility == Visibility.Visible && !Named<Button>(dialog, "ExportRecording").IsEnabled, "Draft handles/confirmation gating missing");
                    dialog.Timeline.MoveDraftBoundary(true, -10); dialog.Timeline.MoveDraftBoundary(false, 100);
                    Ensure(dialog.Timeline.Draft!.Start >= 0 && dialog.Timeline.Draft.End <= 6 && dialog.Timeline.Draft.End - dialog.Timeline.Draft.Start <= 6 - 1d / 30 + .000001, "Draft deletes everything or exceeds trim");
                    dialog.Timeline.MoveDraftBoundary(true, 100); Ensure(dialog.Timeline.Draft.End - dialog.Timeline.Draft.Start >= 1d / 30 - .000001, "Handles crossed or collapsed");
                    for (int i = 0; i < 50; i++) dialog.Timeline.MoveDraftBoundary(true, .5 + i / 60d);
                    dialog.Timeline.MoveDraftBoundary(true, 1.5); dialog.Timeline.MoveDraftBoundary(false, 2.5);
                    await Task.Delay(250);
                    var preview = Named<MediaElement>(dialog, "RecordingPreview");
                    Ensure(Math.Abs(preview.Position.TotalSeconds - 2.5) < .08, "Fast scrubbing stayed at an old boundary: " + preview.Position);
                    double position = preview.Position.TotalSeconds; await Task.Delay(100); Ensure(Math.Abs(preview.Position.TotalSeconds - position) < .04, "Scrubbing did not pause playback");
                    foreach (string theme in new[] { "Light", "Dark" }) { ThemeService.Apply(theme); dialog.UpdateLayout(); Ensure(All<Border>(Named<Thumb>(dialog, "DeleteStartHandle")).Any(b => b.BorderBrush == ThemeService.Brush("Danger")), "Red handle missed live theme"); UiChangeTests.Render(dialog, $"timeline-draft-{theme.ToLowerInvariant()}.png"); }
                    Click(Named<Button>(dialog, "CancelDelete")); Ensure(dialog.Deleted.Count == 0 && dialog.Timeline.Draft == null, "Cancel applied a deletion");
                    dialog.BeginDelete(); dialog.Timeline.MoveDraftBoundary(true, 1); dialog.Timeline.MoveDraftBoundary(false, 2); dialog.ConfirmDelete();
                    Ensure(dialog.Deleted.SequenceEqual(new[] { new VideoRange(1, 2) }), "Confirm boundaries changed");
                    dialog.BeginDelete(); dialog.Timeline.MoveDraftBoundary(true, 3); dialog.Timeline.MoveDraftBoundary(false, 4); dialog.ConfirmDelete();
                    Ensure(dialog.Deleted.Count == 2, "Multiple deletions not retained");
                    dialog.UndoEdit(); Ensure(dialog.Deleted.Count == 1, "Undo did not revert last deletion"); dialog.RedoEdit(); Ensure(dialog.Deleted.Count == 2, "Redo failed");
                    Click(All<Button>(dialog.Timeline).First(b => b.ToolTip?.ToString() == "删除 · 1.00–2.00 秒")); dialog.Timeline.MoveDraftBoundary(true, .5); dialog.CancelDelete(); Ensure(dialog.Deleted[0].Start == 1, "Cancel changed prior confirmed range");
                    Click(All<Button>(dialog.Timeline).First(b => b.ToolTip?.ToString() == "删除 · 1.00–2.00 秒")); dialog.Timeline.MoveDraftBoundary(true, .5); dialog.ConfirmDelete(); Ensure(dialog.Deleted[0].Start == .5, "Adjust boundary not applied");
                    dialog.UndoEdit(); Ensure(dialog.Deleted[0].Start == 1, "Boundary adjustment not undoable"); dialog.UndoEdit(); Ensure(dialog.Deleted.Count == 1, "Deletion not undoable"); dialog.RedoEdit(); dialog.RedoEdit(); Ensure(dialog.Deleted.Count == 2 && dialog.Deleted[0].Start == .5, "Deletion/boundary redo failed");
                    dialog.Timeline.SetRange(.2, 5.5); dialog.BeginDelete(); dialog.Timeline.MoveDraftBoundary(true, -2); dialog.Timeline.MoveDraftBoundary(false, 99); Ensure(dialog.Timeline.Draft!.Start >= .2 && dialog.Timeline.Draft.End <= 5.5, "Draft exceeded retained trim"); dialog.CancelDelete();
                    dialog.Timeline.SetRange(1, 1.5); Ensure(dialog.Timeline.End > 1.5, "Trim allowed entirely deleted content");
                    await dialog.ConfigurationSaved;
                    var stored = await RecordingEditStore.LoadAsync(landscape); Ensure(stored != null && stored.Deleted.SequenceEqual(dialog.Deleted), "Saved edit differs from confirmed cuts");
                }
                finally { dialog.Close(); }
            });
            await check("Timeline draft export uses confirmed cuts with trim crop speed mute and source frame sequence", async () =>
            {
                RecordingEditStore.Remove(landscape);
                var tools = await MediaTools.DetectAsync(new Settings());
                var dialog = new RecordingEditWindow(owner, landscape, new Settings { OutputDirectory = directory }, new(ExportQuality.Medium, 30, true, 6));
                await UpgradeTests.WithDialogAsync(dialog, async () =>
                {
                    await dialog.Initialization; await Ready(dialog);
                    dialog.Timeline.SetRange(.5, 5.5);
                    dialog.BeginDelete(); dialog.Timeline.MoveDraftBoundary(true, 2); dialog.Timeline.MoveDraftBoundary(false, 4); dialog.ConfirmDelete();
                    dialog.BeginCrop(); dialog.CropOverlay.SetSelection(new Rect(40, 20, 480, 480)); dialog.ConfirmCrop();
                    Named<ComboBox>(dialog, "ExportSpeed").SelectedIndex = 3; All<CheckBox>(dialog).Single().IsChecked = true;
                    Named<TextBox>(dialog, "RecordingFilename").Text = "timeline-export-" + Guid.NewGuid().ToString("N") + ".mp4";
                    await dialog.ExportAsync(); Ensure(dialog.SavedPath != null, "Timeline export failed");
                });
                var output = await VideoExportService.ProbeAsync(dialog.SavedPath!, tools);
                Ensure(output.Width == 480 && output.Height == 480 && !output.HasAudio && Math.Abs(output.Duration - 1.5) < .08, "Trim/cut/crop/speed duration disagrees: " + output);
                // At output 1 s, source should be 4.5 s (0.5 trim + two retained
                // seconds at 2x, jumping the 2-4 deletion). Compare decoded crops.
                string exported = Path.Combine(directory, "export-sample.png"), original = Path.Combine(directory, "source-sample.png");
                await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-v", "error", "-y", "-ss", "1", "-i", dialog.SavedPath!, "-frames:v", "1", exported }, CancellationToken.None);
                await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-v", "error", "-y", "-ss", "4.5", "-i", landscape, "-vf", "crop=480:480:40:20", "-frames:v", "1", original }, CancellationToken.None);
                byte[] a = UpgradeTests.Bytes(CaptureService.Load(exported)), b = UpgradeTests.Bytes(CaptureService.Load(original));
                Ensure(a.Length == b.Length && a.Zip(b, (x, y) => Math.Abs(x - y)).Average() < 8, "Export retained wrong source frame");
            });
        }
        finally { owner.Close(); ThemeService.Apply("Dark"); }
    }
    private static void AssertLayout(RecordingEditWindow dialog, double ratio)
    {
        dialog.UpdateLayout(); var viewport = Named<Grid>(dialog, "RecordingViewport"); var slot = Named<Grid>(dialog, "RecordingPreviewSlot");
        Ensure(viewport.ActualHeight >= 40 && Math.Abs(viewport.ActualWidth / viewport.ActualHeight - ratio) < .025, $"Preview aspect mismatch: {viewport.RenderSize}, expected {ratio}");
        Ensure(viewport.ActualWidth <= slot.ActualWidth + 1 && viewport.ActualHeight <= slot.ActualHeight + 1, $"Video viewport exceeds available slot: viewport={viewport.RenderSize}, slot={slot.RenderSize}, ratio={ratio}, window={dialog.RenderSize}");
        foreach (string name in new[] { "ExportRecording", "DeleteMiddleSegment", "RecordingFolder", "RecordingFilename", "RecordingFormat" })
        {
            var element = All<FrameworkElement>(dialog).Single(e => e.Name == name); Rect rect = element.TransformToAncestor(dialog).TransformBounds(new Rect(element.RenderSize));
            Ensure(rect.Height > 0 && rect.Bottom <= dialog.ActualHeight + 1 && rect.Right <= dialog.ActualWidth + 1, "Control clipped: " + name);
        }
    }
    private static async Task Ready(RecordingEditWindow dialog)
    { for (int i = 0; i < 60 && Named<MediaElement>(dialog, "RecordingPreview").NaturalVideoWidth == 0; i++) await Task.Delay(50); Ensure(Named<MediaElement>(dialog, "RecordingPreview").NaturalVideoWidth > 0, "Preview not opened"); await Task.Delay(150); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Ensure(bool condition, string message) => UpgradeTests.Ensure(condition, message);
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => All<T>(root).Single(e => e.Name == name);
    private static System.Collections.Generic.IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    { if (root is T item) yield return item; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
}
