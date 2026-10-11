using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickCapture;

// DWM supplies live, proportional previews without scanning/capturing the desktop.
internal sealed class WindowThumbnail : Border, IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Properties
    {
        public uint Flags; public Native.RECT Destination, Source; public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out NativeSize size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref Properties properties);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    private IntPtr _thumbnail;
    private readonly TextBlock _placeholder = new() { Text = "等待预览", FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly FrameworkElement? _viewport;
    internal IntPtr Target { get; }
    internal bool HasPreview => _thumbnail != IntPtr.Zero && _placeholder.Visibility == Visibility.Hidden;
    internal string PreviewStatus => _placeholder.Text;
    internal WindowThumbnail(IntPtr target, double width, double height, FrameworkElement? viewport = null)
    {
        Target = target; Width = width; Height = height; _viewport = viewport; Child = _placeholder;
        Background = Brushes.Black; BorderThickness = new Thickness(1); SetResourceReference(BorderBrushProperty, "BorderBrush");
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Loaded += (_, _) => Refresh(); Unloaded += (_, _) => Dispose();
    }
    internal void Refresh()
    {
        if (!IsLoaded || !IsVisible || ActualWidth < 2 || ActualHeight < 2) { Dispose(); return; }
        string? unavailable = !Native.IsWindow(Target) ? "窗口已关闭" : Native.IsIconic(Target) ? "窗口已最小化\n无法预览" : Native.CaptureProtected(Target) ? "受保护窗口\n无法预览" : null;
        if (unavailable != null) { Dispose(); _placeholder.Text = unavailable; return; }
        var owner = Window.GetWindow(this); if (owner == null) return;
        var hwnd = new WindowInteropHelper(owner).Handle;
        var topLeft = PointToScreen(new Point(1, 1)); var bottomRight = PointToScreen(new Point(ActualWidth - 1, ActualHeight - 1));
        if (_viewport != null)
        {
            var start = _viewport.PointToScreen(new Point()); var end = _viewport.PointToScreen(new Point(_viewport.ActualWidth, _viewport.ActualHeight));
            if (topLeft.Y < start.Y || bottomRight.Y > end.Y) { Dispose(); return; }
        }
        if (_thumbnail == IntPtr.Zero && DwmRegisterThumbnail(hwnd, Target, out _thumbnail) != 0) { _thumbnail = IntPtr.Zero; _placeholder.Text = "暂时无法预览"; return; }
        if (DwmQueryThumbnailSourceSize(_thumbnail, out var source) != 0 || source.Width <= 0 || source.Height <= 0) { Dispose(); _placeholder.Text = "暂时无法预览"; return; }
        var origin = new NativePoint(); ClientToScreen(hwnd, ref origin);
        double scale = Math.Min((bottomRight.X - topLeft.X) / source.Width, (bottomRight.Y - topLeft.Y) / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale)), height = Math.Max(1, (int)Math.Round(source.Height * scale));
        int x = (int)Math.Round((topLeft.X + bottomRight.X - width) / 2) - origin.X, y = (int)Math.Round((topLeft.Y + bottomRight.Y - height) / 2) - origin.Y;
        var properties = new Properties { Flags = 1 | 4 | 8 | 16, Destination = new Native.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height }, Opacity = 255, Visible = true, ClientOnly = false };
        if (DwmUpdateThumbnailProperties(_thumbnail, ref properties) != 0) { Dispose(); _placeholder.Text = "暂时无法预览"; return; }
        _placeholder.Visibility = Visibility.Hidden;
    }
    public void Dispose()
    {
        if (_thumbnail != IntPtr.Zero) { DwmUnregisterThumbnail(_thumbnail); _thumbnail = IntPtr.Zero; }
        _placeholder.Visibility = Visibility.Visible;
    }
}

internal sealed class WindowPreviewHighlight : Window
{
    internal IntPtr Target { get; set; }
    internal WindowPreviewHighlight()
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; IsHitTestVisible = false;
        var frame = new Border { BorderThickness = new Thickness(3) }; frame.SetResourceReference(Border.BorderBrushProperty, "Accent"); Content = frame;
        SourceInitialized += (_, _) => Native.MakeClickThrough(this);
    }
    internal void Refresh()
    {
        if (Target == IntPtr.Zero || !Native.VisibleWindowBounds(Target, out var bounds)) { Hide(); return; }
        if (!IsVisible) Show();
        bounds.Inflate(3, 3); Native.Place(this, bounds, false);
        if (!Native.ExcludeFromCapture(this)) Hide();
    }
}

internal sealed class WindowPicker : Window
{
    private readonly ListBox _list = new() { Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly List<WindowThumbnail> _thumbnails = new();
    private readonly WindowPreviewHighlight _highlight = new();
    private readonly Border _preview = new();
    private readonly TextBlock _caption = new() { Text = "悬停或选择窗口查看预览", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private WindowItem? _hover;
    private WindowThumbnail? _largePreview;
    private readonly Button _confirm;
    internal WindowItem? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as WindowItem;
    internal IntPtr PreviewTarget => _largePreview?.Target ?? IntPtr.Zero;
    internal bool PreviewAvailable => _largePreview?.HasPreview == true;
    internal WindowPicker(Window owner, IReadOnlyList<WindowItem>? items = null)
    {
        Ui.Theme(this); Owner = owner; Title = "轻截 · 选择窗口"; Width = 760; Height = 510; MinWidth = 680; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var hint = new TextBlock { Text = "悬停预览，选择后确认 · Esc 取消", Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(Ui.Button("取消", () => DialogResult = false)); _confirm = Ui.Button("选择", Confirm); _confirm.IsEnabled = false; buttons.Children.Add(_confirm); root.Children.Add(buttons);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(280) }); root.Children.Add(grid);
        grid.Children.Add(_list); var side = new StackPanel { Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(side, 1); grid.Children.Add(side); side.Children.Add(_preview); side.Children.Add(_caption);
        foreach (var item in items ?? Native.Windows())
        {
            var row = new Grid { Margin = new Thickness(2, 4, 2, 4) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(112) }); row.ColumnDefinitions.Add(new());
            var thumbnail = new WindowThumbnail(item.Handle, 104, 64, _list); row.Children.Add(thumbnail); _thumbnails.Add(thumbnail);
            var text = new StackPanel { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(text, 1); row.Children.Add(text);
            text.Children.Add(new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.Title });
            var app = new TextBlock { Text = $"{item.ApplicationName} · {item.ProcessId}\n窗口 {item.Handle.ToInt64():X}", FontSize = 12, Margin = new Thickness(0, 4, 0, 0) }; app.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); text.Children.Add(app);
            var entry = new ListBoxItem { Content = row, Tag = item }; _list.Items.Add(entry);
            entry.MouseEnter += (_, _) => { _hover = item; ShowPreview(item); };
            entry.MouseLeave += (_, _) => { if (_hover == item) { _hover = null; ShowPreview(Selected); } };
        }
        _list.SelectionChanged += (_, _) => { _hover = null; _confirm.IsEnabled = Selected != null && Native.IsWindow(Selected.Handle) && !Native.IsIconic(Selected.Handle); ShowPreview(Selected); };
        _list.MouseDoubleClick += (_, _) => Confirm();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                // Enter confirms the window currently being previewed. Pointer
                // confirmation restores the selected preview when leaving a row.
                if (_hover is { } target) _list.SelectedItem = _list.Items.Cast<ListBoxItem>().FirstOrDefault(row => ((WindowItem)row.Tag).Handle == target.Handle);
                Confirm(); e.Handled = true;
            }
        };
        _timer.Tick += (_, _) => RefreshPreviews();
        Loaded += (_, _) => { Native.ExcludeFromCapture(this); _timer.Start(); RefreshPreviews(); };
        LocationChanged += (_, _) => RefreshPreviews(); SizeChanged += (_, _) => RefreshPreviews();
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => RefreshPreviews()));
        Closed += (_, _) => { _timer.Stop(); _largePreview?.Dispose(); foreach (var preview in _thumbnails) preview.Dispose(); _highlight.Close(); };
    }
    internal void SelectForTest(int index) { _list.SelectedIndex = index; ShowPreview(Selected); }
    private void Confirm() { if (Selected is { } item && Native.IsWindow(item.Handle) && !Native.IsIconic(item.Handle)) DialogResult = true; }
    private void ShowPreview(WindowItem? item)
    {
        if (_largePreview?.Target == item?.Handle) return;
        _largePreview?.Dispose(); _preview.Child = null; _largePreview = null; _highlight.Target = item?.Handle ?? IntPtr.Zero;
        if (item != null)
        {
            _largePreview = new WindowThumbnail(item.Handle, 268, 200); _preview.Child = _largePreview;
            _caption.Text = $"{item.Title}\n{item.ApplicationName} · 进程 {item.ProcessId}\n窗口 {item.Handle.ToInt64():X}";
        }
        else _caption.Text = "悬停或选择窗口查看预览";
        _highlight.Refresh(); Activate();
    }
    private void RefreshPreviews()
    {
        if (!IsLoaded || !IsVisible) return;
        foreach (var thumbnail in _thumbnails) thumbnail.Refresh(); _largePreview?.Refresh(); _highlight.Refresh();
        _confirm.IsEnabled = Selected is { } item && Native.IsWindow(item.Handle) && !Native.IsIconic(item.Handle);
    }
    public static WindowItem? Pick(Window owner) { var window = new WindowPicker(owner); return window.ShowDialog() == true ? window.Selected : null; }
}
