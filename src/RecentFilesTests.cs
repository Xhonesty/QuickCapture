using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace QuickCapture;

// Check the actual XAML layout and saved-file callback without launching a
// shell preview or producing another set of documentation screenshots.
internal static class RecentFilesTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Recent-file card fits complete rows, trims long names and scrolls only its own twenty-item list", async () =>
        {
            await WithMain(async (main, settings) =>
            {
                string longName = "截图_" + new string('长', 90) + "_完整名称.png";
                for (int i = 0; i < 20; i++)
                {
                    string name = i == 0 ? longName : $"截图_示例记录_{i:00}.png";
                    string path = Path.Combine(settings.OutputDirectory, name);
                    SaveFixture(path); File.SetLastWriteTime(path, DateTime.Now.AddMinutes(-i));
                }
                Refresh(main); await Task.Delay(60); main.UpdateLayout();
                var list = Named<ListBox>(main, "RecentList");
                var card = Named<Border>(main, "RecentCard");
                var content = Named<Grid>(main, "MainContent");
                Ensure(main.Width == 480 && main.Height == 680, "The default main window is not 480 x 680 DIP");
                Ensure(list.Items.Count == 20, "The recent-file list lost entries before internal scrolling");
                Ensure(Visuals<ScrollViewer>(main).All(scroll => DescendsFrom(scroll, list)), "A ScrollViewer outside the recent-file list permits whole-window scrolling");
                foreach (string name in new[] { "ScreenshotButton", "RecordButton", "RepeatButton", "RecentCard", "StatusText" })
                    Ensure(Contains(content, Named<FrameworkElement>(main, name)), name + " is clipped by the compact main window");

                var sectionStyle = (Style)main.FindResource("MainSection");
                var sections = Visuals<Border>(main).Where(border => ReferenceEquals(border.Style, sectionStyle)).ToArray();
                Ensure(sections.Length == 3 && sections.All(border => border.CornerRadius == card.CornerRadius && border.Padding == card.Padding && border.BorderThickness == card.BorderThickness && ReferenceEquals(border.Background, card.Background) && ReferenceEquals(border.BorderBrush, card.BorderBrush)), "The three modules do not share their card appearance");
                var scrollViewer = Visuals<ScrollViewer>(list).Single();
                var viewport = Visuals<ScrollContentPresenter>(list).Single();
                int fullRows = 0;
                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem row) continue;
                    var bounds = Bounds(row, viewport);
                    var visible = Rect.Intersect(bounds, new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
                    if (visible.IsEmpty || visible.Height <= 0.5) continue;
                    Ensure(visible.Height >= row.ActualHeight - 0.5, $"The default recent list cuts off row {i}: rowHeight={row.ActualHeight:F2}, visibleHeight={visible.Height:F2}, rowY={bounds.Y:F2}, viewportHeight={viewport.ActualHeight:F2}, listHeight={list.ActualHeight:F2}, cardHeight={card.ActualHeight:F2}, slotHeight={Named<Grid>(main, "RecentSectionSlot").ActualHeight:F2}");
                    fullRows++;
                }
                Ensure(fullRows is >= 3 and <= 5, $"The default window fully displays {fullRows} records instead of three to five");
                Ensure(scrollViewer.ScrollableHeight > 0 && scrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible, "Twenty records cannot be scrolled inside the list");

                var first = (RecentItem)list.Items[0];
                var firstRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var filename = Visuals<TextBlock>(firstRow).Single(text => text.Text == first.Name);
                var fullText = new FormattedText(first.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(filename.FontFamily, filename.FontStyle, filename.FontWeight, filename.FontStretch), filename.FontSize, Brushes.Black, 1);
                Ensure(filename.TextTrimming == TextTrimming.CharacterEllipsis && filename.TextWrapping == TextWrapping.NoWrap && fullText.WidthIncludingTrailingWhitespace > filename.ActualWidth, "A long filename is not constrained to an ellipsis");
                Ensure(Visuals<FrameworkElement>(firstRow).Any(element => element.ToolTip is string tooltip && tooltip == first.Name), "The full filename is unavailable on hover");
                Ensure(firstRow.ContextMenu?.Items.Count == 4, "The row no longer exposes file, directory and recycle-bin operations");
                firstRow.Focus(); main.UpdateLayout();
                var more = Visuals<Button>(firstRow).Single(button => button.ToolTip as string == "更多操作");
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(40);
                Ensure(firstRow.ContextMenu!.IsOpen && ReferenceEquals(firstRow.ContextMenu.PlacementTarget, more), "The more action did not open the clicked row's menu");
                UiChangeTests.RenderElement(firstRow.ContextMenu, "recent-menu-light.png");
                firstRow.ContextMenu.IsOpen = false;

                var before = Named<TextBlock>(main, "StatusText").TranslatePoint(new Point(), content);
                scrollViewer.ScrollToEnd(); await Task.Delay(40); main.UpdateLayout();
                Ensure(scrollViewer.VerticalOffset > 0, "Internal scrolling did not move to older records");
                Ensure(Named<TextBlock>(main, "StatusText").TranslatePoint(new Point(), content) == before, "Scrolling recent files moved the main window status area");
            });
        });
        await check("Recent-file empty state stays compact and a new file saved to a temporary directory appears first", async () =>
        {
            await WithMain(async (main, settings) =>
            {
                var list = Named<ListBox>(main, "RecentList");
                var card = Named<Border>(main, "RecentCard");
                Ensure(!list.HasItems && list.Visibility == Visibility.Collapsed, "An empty list remains visible");
                Ensure(card.ActualHeight <= 110 && Visuals<TextBlock>(card).Any(text => text.IsVisible && text.Text.StartsWith("暂无记录", StringComparison.Ordinal)), "The empty card retains excess space or lacks its placeholder");

                string existing = Path.Combine(settings.OutputDirectory, "截图_已有记录.png");
                SaveFixture(existing); File.SetLastWriteTime(existing, DateTime.Now.AddMinutes(2));
                Refresh(main); await Task.Delay(40); main.UpdateLayout();
                Ensure(list.Items.Count == 1 && card.ActualHeight <= 120, "A one-record card is not compact");

                string generated = Path.Combine(Paths.TestRoot!, "TemporaryOutput", "截图_新保存文件.png");
                SaveFixture(generated); File.SetLastWriteTime(generated, DateTime.Now.AddMinutes(-20));
                Method("Saved").Invoke(main, new object[] { generated });
                await Task.Delay(60); main.UpdateLayout();
                Ensure(list.Items.Count == 2 && list.Items[0] is RecentItem top && top.Path == generated && ReferenceEquals(list.SelectedItem, top), "A newly saved file outside the default directory was not selected and placed first");
                Ensure(Visuals<ScrollViewer>(list).Single().VerticalOffset == 0, "The new file was left outside the visible list area");
                Ensure(Named<TextBlock>(main, "StatusText").Text.Contains(Path.GetFileName(generated), StringComparison.Ordinal), "The saved-file status was lost during refresh");
                Refresh(main); await Task.Delay(40); main.UpdateLayout();
                Ensure(list.Items.OfType<RecentItem>().Any(item => item.Path == generated), "A subsequent refresh forgot a file saved to a temporary directory in this session");
            });
        });
    }

    private static async Task WithMain(Func<MainWindow, Settings, Task> action)
    {
        string? previousRoot = Paths.TestRoot;
        string previousTheme = ThemeService.Current;
        Paths.TestRoot = Path.Combine(previousRoot ?? Path.Combine(AppContext.BaseDirectory, "InterfaceChecks"), "RecentCard-" + Guid.NewGuid().ToString("N"));
        var settings = new Settings { Theme = "Light", OutputDirectory = Paths.DefaultOutput, AutoSaveScreenshot = false, ScreenshotHotkey = "Ctrl+Alt+F8", RecordingHotkey = "Ctrl+Alt+F9" };
        settings.Save();
        MainWindow? main = null;
        try
        {
            main = new MainWindow();
            // Production close hides to the tray; the fixture must actually
            // close so Closed disposes its tray icon, hotkeys and recorder.
            main.Closing += (_, args) => args.Cancel = false;
            main.Show(); await Task.Delay(120); main.UpdateLayout();
            if (typeof(MainWindow).GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main) is Forms.NotifyIcon tray) tray.Visible = false;
            await action(main, settings);
        }
        finally { main?.Close(); Paths.TestRoot = previousRoot; ThemeService.Apply(previousTheme); }
    }
    private static void SaveFixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 238, 245, 250, 255 }, 4);
        CaptureService.Save(image, path);
    }
    private static void Refresh(MainWindow main) => Method("RefreshRecent").Invoke(main, new object?[] { null });
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Recent-file callback missing: " + name);
    private static T Named<T>(MainWindow window, string name) where T : FrameworkElement => window.FindName(name) as T ?? throw new InvalidOperationException("Main-window element missing: " + name);
    private static Rect Bounds(FrameworkElement element, Visual parent) => new(element.TranslatePoint(new Point(), (UIElement)parent), new Size(element.ActualWidth, element.ActualHeight));
    private static bool Contains(FrameworkElement parent, FrameworkElement child)
    {
        var bounds = Bounds(child, parent);
        return child.IsVisible && bounds.X >= -0.5 && bounds.Y >= -0.5 && bounds.Right <= parent.ActualWidth + 0.5 && bounds.Bottom <= parent.ActualHeight + 0.5;
    }
    private static bool DescendsFrom(DependencyObject child, DependencyObject parent)
    {
        for (DependencyObject? current = child; current != null; current = VisualTreeHelper.GetParent(current)) if (ReferenceEquals(current, parent)) return true;
        return false;
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
