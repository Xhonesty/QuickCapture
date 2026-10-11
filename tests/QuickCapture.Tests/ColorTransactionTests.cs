using System;
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

namespace QuickCapture;

internal static class ColorTransactionTests
{
    private static void Ensure(bool condition, string message) => UpgradeTests.Ensure(condition, message);
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => UpgradeTests.Find<T>(root, e => e.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    // Simulate an asynchronous model change without the normal UI cancellation,
    // to verify a late picker callback and window teardown cannot restore a lost object.
    private static void RemoveTarget(VideoObjectEditor editor) => typeof(VideoObjectEditor).GetProperty(nameof(VideoObjectEditor.Objects), BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, Array.Empty<VideoObject>());

    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Recording color defaults, picker apply/cancel and missing-target window teardown", async () =>
        {
            var tools = await MediaTools.DetectAsync(new Settings()); Ensure(tools.CanEffects, "Media tools unavailable");
            var info = new VideoInfo(6, 640, 360, 30, false);
            var window = new Window { Title = "Color transaction regression", Width = 1000, Height = 760, ShowInTaskbar = false };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/QuickCapture;component/RecordingEditorStyles.xaml", UriKind.Relative) });
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/QuickCapture;component/VideoEditorStyles.xaml", UriKind.Relative) });
            Ui.Theme(window);
            var editor = new VideoObjectEditor(window, Path.Combine(Paths.TestRoot!, "color-transaction-source.mp4"), () => 0, () => 6, () => { }, CancellationToken.None);
            var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var preview = new Grid(); preview.Children.Add(editor.Overlay); preview.Children.Add(editor.TextEditor); layout.Children.Add(preview); Grid.SetRow(editor, 1); layout.Children.Add(editor); window.Content = layout;
            editor.Initialize(info, tools); editor.SetView(new(0, 0, 640, 360));
            var pixels = new byte[640 * 360 * 4]; for (int p = 0; p < pixels.Length; p += 4) { pixels[p] = 255; pixels[p + 3] = 255; }
            var frame = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4); frame.Freeze();
            editor.FrameProvider = () => Task.FromResult(frame);
            window.Show(); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            async Task Pick(string name)
            {
                window.UpdateLayout(); Click(Named<Button>(editor.Toolbar.PropertyPanel, name));
                Ensure(editor.ColorEditing, "Palette did not open for " + name);
                await editor.StartFramePicker(); Ensure(editor.PickingColor && editor.Eyedropper?.Active == true, "Picker did not start for " + name);
                var point = new Point(editor.Overlay.ActualWidth / 2, editor.Overlay.ActualHeight / 2);
                editor.Eyedropper!.Press(point); editor.Eyedropper.Release(point);
                Ensure(editor.ColorEditing && !editor.PickingColor && editor.Eyedropper.Active == false && editor.Overlay.ShowSelection, "Picker did not return to the palette for " + name);
                Ensure(editor.Palette!.Draft == Colors.Blue, "Picked color or alpha wrong for " + name);
            }
            try
            {
                // Reproduce the path that previously stored a temporary text ID
                // in the tool defaults, then tried to find it in the object list.
                editor.BeginText(new(20, 20), null); editor.TextEditor.Input.Text = "未提交文字"; window.UpdateLayout();
                Named<CheckBox>(editor.Toolbar.PropertyPanel, "VideoTextFrameEnabled").IsChecked = true;
                Ensure(editor.ToolDefaults.Single().Id == "new", "Draft style retained the temporary object ID");
                var draftBefore = editor.TextEditor.Draft; await Pick("VideoTextFrameColor"); editor.FinishColor(false);
                Ensure(editor.TextEditor.Draft == draftBefore && editor.Objects.Length == 0, "Draft frame color cancellation changed or committed the text: before=" + System.Text.Json.JsonSerializer.Serialize(draftBefore) + "; after=" + System.Text.Json.JsonSerializer.Serialize(editor.TextEditor.Draft));
                editor.TextEditor.Cancel(); editor.SetTool(VideoObjectKind.Text);
                foreach (string name in new[] { "VideoPrimaryColor", "VideoOutlineColor", "VideoTextFrameColor", "VideoTextFrameFill" })
                {
                    var before = editor.ToolDefaults.Single(); await Pick(name); Click(Named<Button>(editor.Palette!, "PaletteCancel"));
                    Ensure(editor.ToolDefaults.Single() == before && editor.Objects.Length == 0 && !editor.ColorEditing, "Default color cancellation leaked for " + name);
                    await Pick(name); Click(Named<Button>(editor.Palette!, "PaletteApply"));
                    Ensure(editor.ToolDefaults.Single().Id == "new" && editor.Objects.Length == 0 && !editor.ColorEditing, "Applying a default created or selected an object for " + name);
                }
                editor.BeginText(new(20, 20), null); editor.TextEditor.Input.Text = "第一处文字";
                Ensure(editor.TextEditor.Draft!.Color == "#0000FF" && editor.TextEditor.Draft.OutlineColor == "#0000FF" && editor.TextEditor.Draft.TextFrame!.Color == "#FF0000FF" && editor.TextEditor.Draft.TextFrame.Fill == "#FF0000FF", "New text did not inherit the confirmed defaults");
                editor.TextEditor.Confirm(); Ensure(editor.Objects.Length == 1, "Text confirmation failed"); var original = editor.Objects.Single();
                editor.RestoreDefaults(new[] { original with { Id = "legacy-draft-id", Color = "#FF0000" } });
                Ensure(editor.ToolDefaults.Single().Id == "new" && editor.ToolDefaults.Single().Name == null, "Legacy defaults retained an object identity");
                editor.SetTool(VideoObjectKind.Text); await Pick("VideoPrimaryColor"); editor.FinishColor(true);
                Ensure(editor.Objects.Single() == original && editor.ToolDefaults.Single().Color == "#0000FF", "Legacy tool defaults changed the existing text");

                editor.Select(original, false); Click(Named<Button>(editor.Toolbar.PropertyPanel, "VideoPrimaryColor")); editor.Palette!.PreviewColor(Color.FromArgb(128, 255, 0, 0));
                await editor.StartFramePicker(); var center = new Point(editor.Overlay.ActualWidth / 2, editor.Overlay.ActualHeight / 2); editor.Eyedropper!.Press(center); editor.Eyedropper.Release(center);
                Ensure(editor.ColorEditing && editor.Palette.Draft.A == 128, "Existing text picker lost its transaction or alpha"); editor.FinishColor(false);
                Ensure(editor.Objects.Single() == original, "Existing text cancellation did not restore the original");
                await Pick("VideoTextFrameColor"); editor.FinishColor(true); Ensure(editor.Objects.Single().Id == original.Id, "Frame color apply changed the text identity");

                // A lost target cancels the transaction; it is never resurrected.
                editor.OpenColor(Named<Button>(editor.Toolbar.PropertyPanel, "VideoPrimaryColor"), editor.Objects.Single(), false, "文字颜色");
                var pending = new TaskCompletionSource<BitmapSource>(); editor.FrameProvider = () => pending.Task; var picking = editor.StartFramePicker();
                RemoveTarget(editor); pending.SetResult(frame); await picking;
                Ensure(editor.Eyedropper?.Active == true, "Late picker regression did not reach input"); editor.Eyedropper!.Press(center); editor.Eyedropper.Release(center);
                Ensure(!editor.ColorEditing && !editor.PickingColor && editor.Objects.Length == 0 && editor.Overlay.Selected == null, "Lost target was restored or picker remained active");
                editor.FinishColor(false); editor.FinishColor(true);

                editor.Add(original); window.UpdateLayout(); editor.OpenColor(Named<Button>(editor.Toolbar.PropertyPanel, "VideoPrimaryColor"), original, false, "文字颜色"); RemoveTarget(editor);
                window.Close(); Ensure(!editor.ColorEditing && !editor.PickingColor && editor.Objects.Length == 0, "Closing the window retained a failed color transaction");
            }
            finally { if (window.IsVisible) window.Close(); System.Windows.Input.Mouse.Capture(null); }
        });
    }
}
