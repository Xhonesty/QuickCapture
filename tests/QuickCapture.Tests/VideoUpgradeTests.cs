using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;

namespace QuickCapture;
internal static class VideoUpgradeTests
{
    private static void Ensure(bool value, string message) => UpgradeTests.Ensure(value, message);
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string directory = Path.Combine(Paths.TestRoot!, "VideoUpgrade"); Directory.CreateDirectory(directory);
        string png = Path.Combine(directory, "notes.png"), source = Path.Combine(directory, "notes.mp4"); VideoTextTests.Notes(png);
        var tools = await MediaTools.DetectAsync(new Settings());
        var generated = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-loop", "1", "-i", png, "-f", "lavfi", "-i", "aevalsrc=0.1*sin(2*PI*(400+100*floor(t))*t):s=48000:d=6", "-t", "6", "-r", "30", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, CancellationToken.None); Ensure(generated.ExitCode == 0, generated.Error);
        var info = await VideoExportService.ProbeAsync(source, tools);
        var speeds = new[] { new VideoSpeedRange(.8, 2, 1.5), new VideoSpeedRange(3.4, 4.6, .5) };
        var cuts = new[] { new VideoRange(1.8, 2.4) };
        var state = new RecordingEditState(.2, 5.8, cuts, SpeedRanges: speeds, Crop: new(40, 40, 880, 460), SnapEnabled: false);
        double expectedDuration = 5.866666666666667;
        await check("Interval timing covers overlap, normal restoration, adjacent merge, deleted priority and legacy speed", () =>
        {
            var replaced = VideoTiming.Apply(new[] { new VideoSpeedRange(4, 11, 1.5) }, new(6, 10, 2)); Ensure(replaced.SequenceEqual(new[] { new VideoSpeedRange(4, 6, 1.5), new VideoSpeedRange(6, 10, 2), new VideoSpeedRange(10, 11, 1.5) }), "Overlap did not preserve non-overlapping sides");
            replaced = VideoTiming.Apply(replaced, new(6, 10, 1)); Ensure(replaced.Length == 2 && VideoTiming.RateAt(8, replaced) == 1, "Normal speed not restored");
            Ensure(VideoTiming.Apply(new[] { new VideoSpeedRange(0, 1, 2) }, new(1, 2, 2)).Single() == new VideoSpeedRange(0, 2, 2), "Adjacent equal speeds not merged");
            Ensure(Math.Abs(VideoTiming.Segments(state).Sum(r => r.Duration) - expectedDuration) < .000001, "Final duration not computed from kept intersections");
            foreach (double original in new[] { .2, .7, .8, 1.4, 2.4, 3.4, 4.2, 4.6, 5.7 }) Ensure(Math.Abs(VideoTiming.SourceTime(VideoTiming.OutputTime(original, state), state) - original) < .000001, "Source/output mapping roundtrip failed at " + original);
            var legacy = JsonSerializer.Deserialize<RecordingEditState>("{\"Start\":0,\"End\":6,\"Deleted\":[],\"Speed\":2}")!; Ensure(legacy.SnapEnabled && legacy.SpeedRanges == null && VideoTiming.SourceTime(1, legacy) == 2, "Legacy project speed or default snap changed");
            return Task.CompletedTask;
        });
        await check("Snap uses screen distance with hysteresis, valid targets and free-drag reset for short and long videos", () =>
        {
            foreach (double duration in new[] { 6d, 3600d })
            {
                double target = duration / 2; var snap = new TimelineSnapper(); var targets = new[] { new TimelineSnapTarget(target, "对象") };
                Ensure(snap.Resolve(target - 7 * duration / 800, duration, 800, targets, 0, duration, true) == target, "8-DIP snap failed");
                Ensure(snap.Resolve(target - 10 * duration / 800, duration, 800, targets, 0, duration, true) == target, "12-DIP hysteresis failed");
                Ensure(snap.Resolve(target - 13 * duration / 800, duration, 800, targets, 0, duration, true) != target, "Snap did not release");
                Ensure(snap.Resolve(target - duration / 800, duration, 800, targets, target + .01, duration, true) != target, "Invalid boundary snapped");
                Ensure(snap.Resolve(target - duration / 800, duration, 800, targets, 0, duration, false) != target && snap.Target == null, "Off/Alt did not restore free drag");
            }
            return Task.CompletedTask;
        });
        string asset = await RecordingEditStore.ImportAssetAsync(source, png, CancellationToken.None);
        var objects = new[] {
            new VideoObject("shape", VideoObjectKind.RoundedRectangle, 560, 315, 280, 130, .6, 5.6, 0, Color:"#FF5268", Fill:"#82E2BB", Stroke:4, StrokeOpacity:1, FillOpacity:.3),
            new VideoObject("brush", VideoObjectKind.Brush, 80, 445, 370, 12, .6, 5.6, 1, Color:"#528CF0", Stroke:7, Points:new[] { new VideoPoint(80,445), new VideoPoint(250,450), new VideoPoint(450,445) }, StrokeOpacity:1),
            new VideoObject("text", VideoObjectKind.Text, 585, 328, 250, 90, .6, 5.6, 2, Color:"#FF5268", Text:"中文与 English\n让每一帧更清楚", FontSize:37, Bold:true).FitText(info),
            new VideoObject("mosaic", VideoObjectKind.Mosaic, 70, 210, 95, 55, 4.8, 5.6, 3, BlockSize:12),
            new VideoObject("pip", VideoObjectKind.PictureInPicture, 715, 70, 192, 108, 3, 5.6, 4, Asset:asset, AssetWidth:960, AssetHeight:540)
        };
        state = state with { Objects = objects };
        await check("Independent fill and stroke alpha, disabled outlines and lines share source-pixel rendering", async () =>
        {
            using var reader = new VideoFrameReader(tools.Ffmpeg!, source, info.Width, info.Height, info.Fps); var original = (byte[])(await reader.FrameAsync(1, CancellationToken.None))!.Clone();
            var shape = new VideoObject("sample", VideoObjectKind.Rectangle, 400, 400, 80, 50, 0, 6, 0, Color:"#FF0000", Fill:"#00FF00", Stroke:4, StrokeOpacity:1, FillOpacity:.5);
            var rendered = original.ToArray(); VideoObjectRenderer.Render(rendered, info, new[] { shape }, 1, new Dictionary<string, SKBitmap>());
            int inside = (425 * info.Width + 430) * 4, edge = (425 * info.Width + 400) * 4;
            Ensure(rendered[inside + 1] > 240 && rendered[inside + 2] > 100 && rendered[inside + 2] < 150 && rendered[edge + 2] > 245 && rendered[edge + 1] < 10, "Fill alpha overrode stroke alpha");
            var off = original.ToArray(); VideoObjectRenderer.Render(off, info, new[] { shape with { StrokeEnabled = false } }, 1, new Dictionary<string, SKBitmap>()); Ensure(off.AsSpan((425 * info.Width + 399) * 4, 3).SequenceEqual(original.AsSpan((425 * info.Width + 399) * 4, 3)), "Disabled stroke still rendered");
            var line = shape with { Kind = VideoObjectKind.Line, Color = "#0000FF", Fill = "none" }; var path = original.ToArray(); VideoObjectRenderer.Render(path, info, new[] { line }, 1, new Dictionary<string, SKBitmap>()); Ensure(path[(425 * info.Width + 440) * 4] > 230, "Line missing its independent stroke");
        });
        await RecordingEditStore.SaveAsync(source, state);
        await check("WPF palette transaction, original-frame picker, editable sizes, CJK composition, mute, layer history and reopen", async () =>
        {
            var owner = new Window { Width = 1, Height = 1, ShowInTaskbar = false }; owner.Show(); RecordingEditWindow? dialog = null;
            try
            {
                dialog = new(owner, source, new Settings { OutputDirectory = directory }, new(ExportQuality.High, 30, true, 6)); dialog.Show(); await dialog.Initialization; await Task.Delay(350);
                var editor = dialog.ObjectEditor; editor.Select(editor.Objects.Single(o => o.Id == "shape"), true); dialog.UpdateLayout();
                var speedBand = dialog.Timeline.Children.OfType<Button>().First(b => ((string?)b.ToolTip)?.StartsWith("1.5×") == true);
                var deleteBand = dialog.Timeline.Children.OfType<Button>().First(b => ((string?)b.ToolTip)?.StartsWith("删除") == true);
                Ensure(((Border)speedBand.Content).ActualWidth > 100 && ((Border)deleteBand.Content).ActualWidth > 50, "Confirmed interval color bands collapsed in the button template");
                var before = editor.Objects.Single(o => o.Id == "shape"); var colorButton = UpgradeTests.Find<Button>(dialog, b => b.Name == "VideoPrimaryColor"); editor.OpenColor(colorButton, before, true, "形状颜色"); editor.PreviewColor(Color.FromArgb(128, 10, 40, 80)); Ensure(editor.Objects.Single(o => o.Id == "shape").StrokeAlpha == 1, "Fill preview changed stroke"); editor.FinishColor(false); Ensure(editor.Objects.Single(o => o.Id == "shape") == before, "Color cancel did not restore all fields");
                colorButton = UpgradeTests.Find<Button>(dialog, b => b.Name == "VideoStrokeColor"); editor.OpenColor(colorButton, before, false, "描边颜色"); editor.PreviewColor(Color.FromArgb(190, 25, 90, 180)); editor.FinishColor(true); dialog.UndoEdit(); Ensure(editor.Objects.Single(o => o.Id == "shape") == before, "Color undo not one transaction"); dialog.RedoEdit(); Ensure(editor.Objects.Single(o => o.Id == "shape").Color == "#195AB4", "Color redo lost RGB"); dialog.UndoEdit();
                editor.Select(editor.Objects.Single(o => o.Id == "shape"), false); colorButton = UpgradeTests.Find<Button>(dialog, b => b.Name == "VideoPrimaryColor"); editor.OpenColor(colorButton, editor.Objects.Single(o => o.Id == "shape"), true, "形状颜色"); await editor.StartFramePicker();
                Ensure(editor.Eyedropper?.Active == true && !editor.Overlay.ShowSelection, "Picker did not start: " + editor.Palette?.Status + " Active=" + editor.Eyedropper?.Active + " Owner=" + dialog.IsActive + " Enabled=" + dialog.IsEnabled); var local = new Point(editor.Overlay.ActualWidth * .7, editor.Overlay.ActualHeight * .75); var point = VideoObjectEditor.FramePoint(local, editor.Overlay.View, editor.Overlay.ActualWidth, editor.Overlay.ActualHeight);
                using (var reader = new VideoFrameReader(tools.Ffmpeg!, source, info.Width, info.Height, info.Fps)) { var raw = (await reader.FrameAsync(dialog.PreviewPosition, CancellationToken.None))!; int p = ((int)point.Y * info.Width + (int)point.X) * 4; var expected = Color.FromRgb(raw[p+2], raw[p+1], raw[p]); Ensure(editor.Eyedropper!.SampleCanvas(point) == expected, "Picker sampled composed overlay or wrong crop coordinate"); editor.Eyedropper.PreviewCanvas(local); editor.Eyedropper.Confirm(); Ensure(editor.Palette!.Draft == expected, "Picker confirmation didn't update palette"); }
                editor.FinishColor(false); Ensure(editor.Objects.Single(o => o.Id == "shape") == before && editor.Overlay.ShowSelection, "Picker/palette cancel didn't restore original");
                editor.SetTool(VideoObjectKind.Brush); var brushColor = UpgradeTests.Find<Button>(dialog, b => b.Name == "VideoStrokeColor"); brushColor.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); editor.PreviewColor(Color.FromArgb(128, 45, 80, 160)); editor.FinishColor(true);
                Ensure(editor.ToolDefaults.Single(o => o.Kind == VideoObjectKind.Brush).Color == "#2D50A0" && editor.Objects.Length == objects.Length, "Default brush color changed existing objects");
                var snapButton = UpgradeTests.Find<Button>(dialog, b => b.Name == "VideoToolmagnet"); snapButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Ensure(dialog.Timeline.SnapEnabled && (string?)snapButton.Tag == "selected", "Snap-on button state missing"); snapButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Ensure(!dialog.Timeline.SnapEnabled, "Snap-off button state missing");
                foreach (var size in new[] { new Size(440, 230), new Size(660, 345), new Size(880, 460) }) Ensure(VideoObjectEditor.FramePoint(new(size.Width * .25, size.Height * .75), new(40,40,880,460), size.Width, size.Height) == new Point(260,385), "Scaled crop mapping changed source pixel");
                editor.Select(editor.Objects.Single(o => o.Id == "text"), false); dialog.UpdateLayout(); var font = UpgradeTests.Find<ComboBox>(dialog, c => c.Name == "VideoFontSize"); Ensure(font.Text == "37" && font.Items.Count == 13, "Custom size forced to preset or preset list incomplete"); font.SelectedItem = "72"; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Ensure(editor.Objects.Single(o => o.Id == "text").FontSize == 72, "Preset font size not applied"); var editableSize = (TextBox)font.Template.FindName("PART_EditableTextBox", font); Ensure(editableSize.IsVisible, "Editable size input missing"); editableSize.Text = "37"; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); editor.FlushProperties(); Ensure(editor.Objects.Single(o => o.Id == "text").FontSize == 37, "Manual font size not retained");
                var item = editor.Objects.Single(o => o.Id == "text"); editor.BeginText(new(item.X,item.Y), item); var input = editor.TextEditor.Input; var composition = new TextComposition(InputManager.Current, input, "中"); input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent }); Ensure(!editor.CommitText() && editor.TextEditor.Active, "IME composition committed early"); input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent }); input.Text = "中文与 English\n让每一帧更清楚"; Ensure(editor.CommitText() && editor.Objects.Single(o => o.Id == "text").Text.Contains('\n'), "Multiline CJK lost during confirm");
                editor.Replace(editor.Objects.Single(o => o.Id == "text") with { X = 585, Y = 328, Width = 280 }, true); editor.Select(editor.Objects.Single(o => o.Id == "shape"), false); editor.MoveToEdge(true); Ensure(editor.Objects.Last().Id == "shape", "Shape not above PIP/mosaic/text"); dialog.UndoEdit(); Ensure(editor.Objects.First().Id == "shape", "Layer undo failed"); dialog.RedoEdit(); editor.Select(editor.Objects.Single(o => o.Id == "shape"), false); editor.MoveToEdge(false); Ensure(editor.Objects.First().Id == "shape", "Send to bottom failed");
                var mute = UpgradeTests.Find<Button>(dialog, b => b.Name == "VideoToolvolumex"); Ensure((string)mute.ToolTip == "当前声音开启，点击静音。", "Sound-on tooltip wrong"); mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Ensure((string)mute.ToolTip == "当前已静音，点击恢复声音。" && (string?)mute.Tag == "selected", "Muted icon/state not updated"); mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dialog.BeginSpeed(); dialog.Timeline.MoveDraftBoundary(true, 2.6); dialog.Timeline.MoveDraftBoundary(false, 3.2); UpgradeTests.Find<ComboBox>(dialog, c => c.Name == "ExportSpeed").SelectedIndex = 3; dialog.ConfirmSpeed(); var saved = (await RecordingEditStore.LoadAsync(source))!; await dialog.ConfigurationSaved; saved = (await RecordingEditStore.LoadAsync(source))!; Ensure(saved.SpeedRanges!.Any(r => r.Start == 2.6 && r.End == 3.2 && r.Rate == 2), "Speed draft did not save"); dialog.UndoEdit(); dialog.RedoEdit(); dialog.BeginSpeed(Array.FindIndex(saved.SpeedRanges!, r => r.Start == 2.6)); dialog.ConfirmSpeed(1); await dialog.ConfigurationSaved;
                foreach (string theme in new[] { "Light", "Dark" }) { ThemeService.Apply(theme); editor.Select(editor.Objects.Single(o => o.Id == "shape"), true); await Task.Delay(200); dialog.UpdateLayout(); UiChangeTests.Render(dialog, "video-upgrade-implemented-" + theme.ToLowerInvariant() + ".png"); dialog.BeginSpeed(0); await Task.Delay(100); dialog.UpdateLayout(); UiChangeTests.Render(dialog, "video-upgrade-speed-" + theme.ToLowerInvariant() + ".png"); var speedHandle = UpgradeTests.Find<System.Windows.Controls.Primitives.Thumb>(dialog, h => h.Name == "SpeedStartHandle"); Ensure(speedHandle.Focus(), "Speed handle focus failed"); dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Ensure(dialog.Timeline.Draft == null, "Escape did not cancel the focused interval handle"); editor.Select(editor.Objects.Single(o => o.Id == "shape"), false); colorButton = UpgradeTests.Find<Button>(dialog, b => b.Name == "VideoPrimaryColor"); editor.OpenColor(colorButton, editor.Objects.Single(o => o.Id == "shape"), true, "形状颜色"); await Task.Delay(100); UiChangeTests.RenderElement((FrameworkElement)editor.Palette!.Parent, "video-upgrade-palette-" + theme.ToLowerInvariant() + ".png"); editor.FinishColor(false); mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); dialog.UpdateLayout(); UiChangeTests.RenderElement(editor.Toolbar.MainPanel, "video-upgrade-muted-" + theme.ToLowerInvariant() + ".png"); mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                await dialog.ConfigurationSaved; var expectedState = (await RecordingEditStore.LoadAsync(source))!; dialog.Close(); dialog = new(owner, source, new Settings { OutputDirectory = directory }, new(ExportQuality.High,30,true,6)); dialog.Show(); await dialog.Initialization; Ensure(!dialog.Timeline.SnapEnabled && JsonSerializer.Serialize(dialog.ObjectEditor.Objects) == JsonSerializer.Serialize(expectedState.Objects) && JsonSerializer.Serialize(dialog.ObjectEditor.ToolDefaults) == JsonSerializer.Serialize(expectedState.ToolDefaults), "Reopen lost snap/colors/defaults/size/layers");
                Ensure(VideoTiming.Segments(expectedState).Sum(r=>r.Duration) == VideoTiming.Segments(state).Sum(r=>r.Duration), "Restore-normal or reopen changed duration");
                File.WriteAllText(Path.Combine(directory, "dpi.json"), JsonSerializer.Serialize(new { dpi = VisualTreeHelper.GetDpi(dialog), testedScales = new[] { .5, .75, 1d }, actualMonitorDpiOnly = true }));
            }
            finally { dialog?.Close(); owner.Close(); ThemeService.Apply("Light"); }
        });
        await check("MP4 WebM GIF use the same multi-speed cut crop and object plan; audio mute and retimed source markers", async () =>
        {
            foreach (var format in Enum.GetValues<RecordingFormat>())
            {
                int fps = format == RecordingFormat.Gif ? 15 : 30; string target = Path.Combine(directory, "composed." + VideoExportService.Extension(format));
                await VideoExportService.ExportAsync(new(source, target, format, ExportQuality.High, state.Start, state.End, Mute:format == RecordingFormat.WebM, Overwrite:true, Crop:state.Crop, FramesPerSecond:fps, Deleted:cuts, Objects:objects, SpeedRanges:speeds), info, tools);
                var output = await VideoExportService.ProbeAsync(target, tools); Ensure(output.Width == 880 && output.Height == 460 && Math.Abs(output.Duration - expectedDuration) < .16, "Retimed dimensions/duration wrong: " + format); Ensure(output.HasAudio == (format == RecordingFormat.Mp4), "Export mute/audio wrong: " + format);
                using var reader = new VideoFrameReader(tools.Ffmpeg!, target, output.Width, output.Height, fps);
                foreach (double original in new[] { .4, 1.2, 4d, 5.7 })
                {
                    double at = VideoTiming.OutputTime(original, state); var frame = await reader.FrameAsync(at, CancellationToken.None); Ensure(frame != null, "Missing mapped frame"); int p = (360 * output.Width + 530) * 4;
                    bool active = original >= .6 && original < 5.6; Ensure(active ? frame![p+1] > frame[p+2] + 3 : Math.Abs(frame![p+1]-frame[p+2]) < 8, "Shape alpha/color/time mapping mismatch: " + format + " at " + original);
                }
                if (format == RecordingFormat.Mp4)
                {
                    foreach (double original in new[] { .5, 1.3, 3.8, 5.2 })
                    {
                        string sample = Path.Combine(directory, "audio-" + original.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".pcm");
                        var extracted = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-ss", VideoTiming.OutputTime(original, state).ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", target, "-t", "0.1", "-vn", "-ac", "1", "-ar", "8000", "-f", "s16le", sample }, CancellationToken.None); Ensure(extracted.ExitCode == 0, extracted.Error);
                        var bytes = File.ReadAllBytes(sample); int crossings = 0; for (int i=2;i<bytes.Length;i+=2) if (BitConverter.ToInt16(bytes,i-2) <= 0 && BitConverter.ToInt16(bytes,i) > 0) crossings++; double frequency = crossings / (bytes.Length / 2d / 8000); Ensure(Math.Abs(frequency - (400 + 100 * Math.Floor(original))) < 35, "Audio source timing or pitch changed at " + original + ": " + frequency);
                    }
                }
            }
        });
        File.WriteAllText(Path.Combine(Paths.TestRoot!, "video-upgrade-fixtures.json"), JsonSerializer.Serialize(new { source, directory, expectedDuration, state }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
