using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfPath = System.Windows.Shapes.Path;

namespace QuickCapture;

internal static class MosaicIconTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Approved mosaic SVG renders filled pixels at 18 DIP in the actual toolbar in both themes and all states", async () =>
        {
            var owner = new EditorWindow(AcceptanceArtifacts.NoteImage(900, 540), new Settings(), _ => { }) { Width = 1000, Height = 660 };
            string previousTheme = ThemeService.Current;
            try
            {
                owner.Show(); await Task.Delay(100);
                var editor = owner.Editor; var toolbar = Visuals<ScreenshotToolbar>(owner).Single(); var button = editor.ButtonFor("mosaic");
                var viewbox = Visuals<Viewbox>(button).Single();
                Ensure(viewbox.Width == 18 && button.Width == 45 && button.Height == 32 && button.Width == editor.ButtonFor("rectangle").Width, "Production icon changed toolbar dimensions");
                var paths = ((Canvas)viewbox.Child).Children.OfType<WpfPath>().ToArray();
                Ensure(paths.Length == 2 && paths.All(path => path.Stroke == null) && paths[1].Opacity == .42 && paths[0].Data.Bounds == new Rect(3, 3, 18, 18), "Approved block geometry/opacity changed");
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    ThemeService.Apply(theme);
                    foreach (string state in new[] { "normal", "selected", "disabled" })
                    {
                        button.IsEnabled = true; editor.SelectTool(state == "selected" ? AnnotationTool.Mosaic : AnnotationTool.Select);
                        if (state == "disabled") button.IsEnabled = false;
                        owner.UpdateLayout();
                        string foreground = state == "selected" ? "ToolbarSelectedForeground" : state == "disabled" ? "DisabledForeground" : "TextPrimary";
                        Ensure(button.Foreground == ThemeService.Brush(foreground) && paths.All(path => ReferenceEquals(path.Fill, button.Foreground)), "Filled icon did not follow " + theme + " " + state);
                        UiChangeTests.RenderElement(toolbar, $"mosaic-toolbar-{theme.ToLowerInvariant()}-{state}.png");
                    }
                    button.IsEnabled = true;
                }
                // Keep both host layouts and outlined sibling SVGs covered.
                owner.Width = 640; editor.SelectTool(AnnotationTool.Mosaic); owner.UpdateLayout();
                Ensure(toolbar.ActualWidth <= 616 && Visuals<Button>(toolbar).Where(b => b.IsVisible).All(b => Bounds(b, toolbar).Right <= toolbar.ActualWidth + 1), "New vector clips the narrow toolbar");
                Ensure(Visuals<WpfPath>(editor.ButtonFor("rectangle")).Any(path => path.Stroke != null && path.Fill == null), "Existing outline icons changed to filled shapes");
            }
            finally { owner.Close(); ThemeService.Apply(previousTheme); }
        });
        await check("Recent screenshot Continue Editing restores the shared editor, original image and annotations after removing top buttons", async () =>
        {
            await SettingsCategoryTests.WithMain(async (main, config, keys) =>
            {
                Directory.CreateDirectory(config.OutputDirectory);
                var original = AcceptanceArtifacts.NoteImage(480, 300); var surface = new AnnotationSurface(original);
                surface.Add(new(AnnotationTool.Arrow, new(20, 30), new(180, 80), Colors.Red));
                string image = Path.Combine(config.OutputDirectory, "editable.png");
                await ScreenshotProjects.SaveAsync(ScreenshotProjects.Snapshot(surface, 1, ScreenshotFormat.Png, 90), surface.Export(), image, ScreenshotFormat.Png, 90, true);
                var edit = await RecentAction(main, image, "继续编辑"); edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                EditorWindow? editor = null;
                for (int i = 0; i < 100 && editor == null; i++) { await Task.Delay(30); editor = Application.Current.Windows.OfType<EditorWindow>().FirstOrDefault(window => window.IsVisible); }
                Ensure(editor != null, "Recent edit entry no longer opens the editor");
                try { Ensure(editor!.Editor.Surface.Items.Count == 1 && editor.Editor.Surface.Original.PixelWidth == original.PixelWidth, "Recent edit lost original/project annotations"); }
                finally { editor!.Close(); }
            });
        });
        await check("Recent video edit and the shared post-recording edit pipeline open the same editor while retaining the master", async () =>
        {
            await SettingsCategoryTests.WithMain(async (main, config, keys) =>
            {
                Directory.CreateDirectory(config.OutputDirectory); string video = Path.Combine(config.OutputDirectory, "saved.mp4");
                var tools = await MediaTools.DetectAsync(config);
                var generated = await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=teal:s=320x240:r=30:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", video }, CancellationToken.None);
                Ensure(generated.ExitCode == 0, generated.Error);
                var edit = await RecentAction(main, video, "编辑录屏");
                await OpenRecording(() => edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                await OpenRecording(() => typeof(MainWindow).GetMethod("EditRecording", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new object[] { video }));
                Ensure(File.Exists(video), "Closing the shared edit pipeline removed the video");
            });
        });
    }
    private static async Task<MenuItem> RecentAction(MainWindow main, string path, string header)
    {
        typeof(MainWindow).GetMethod("RefreshRecent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new object[] { path }); main.UpdateLayout();
        await Task.Delay(80);
        var list = (ListBox)main.FindName("RecentList"); var item = list.Items.OfType<RecentItem>().Single(entry => entry.Path == path); list.ScrollIntoView(item); main.UpdateLayout();
        await Task.Delay(40);
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item); row.ContextMenu!.IsOpen = true;
        await Task.Delay(50);
        var edit = row.ContextMenu.Items.OfType<MenuItem>().Single(entry => entry.Header as string == header);
        Ensure(edit.IsEnabled && edit.Visibility == Visibility.Visible, "Recent edit menu action unavailable"); row.ContextMenu.IsOpen = false; return edit;
    }
    private static async Task OpenRecording(Action open)
    {
        var done = new TaskCompletionSource();
        _ = Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            RecordingEditWindow? dialog = null;
            try
            {
                for (int i = 0; i < 100 && dialog == null; i++) { await Task.Delay(30); dialog = Application.Current.Windows.OfType<RecordingEditWindow>().FirstOrDefault(window => window.IsVisible); }
                Ensure(dialog != null, "Video menu failed to open the shared editor"); await dialog!.Initialization;
                Ensure(Visuals<Button>(dialog).Single(button => button.Name == "ExportRecording").IsEnabled, "Reopened video failed to initialize"); done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { dialog?.Close(); }
        }));
        open(); await done.Task;
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    { if (root is T match) yield return match; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var item in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return item; }
    private static Rect Bounds(FrameworkElement item, Visual root) => item.TransformToAncestor(root).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
