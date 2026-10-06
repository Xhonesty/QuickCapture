using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace QuickCapture;

internal static class PinManager
{
    private static readonly List<PinWindow> Pins = new();
    internal static bool Hidden { get; private set; }
    internal static int Count => Pins.Count;
    internal static PinWindow Create(BitmapSource image, Settings settings, Action<string> saved)
    {
        // Creating a new pin restores visibility for the whole collection.
        if (Hidden) RestoreAll();
        var pin = new PinWindow(image, settings, saved); Pins.Add(pin);
        pin.Closed += (_, _) => { Pins.Remove(pin); if (Pins.Count == 0) Hidden = false; };
        pin.Show(); return pin;
    }
    internal static void HideAll() { foreach (var pin in Pins) pin.Hide(); Hidden = Pins.Count > 0; }
    internal static void RestoreAll() { foreach (var pin in Pins) { pin.Show(); pin.RecoverPlacement(); } Hidden = false; }
    internal static void DisableClickThrough() { foreach (var pin in Pins) pin.SetClickThrough(false); RestoreAll(); }
    internal static void CloseAll() { foreach (var pin in Pins.ToArray()) pin.Close(); Hidden = false; }
}

internal static class PinGeometry
{
    internal static Drawing.Rectangle Place(int sourceWidth, int sourceHeight, double scale, Drawing.Rectangle work, Drawing.Point origin)
    {
        scale = Math.Clamp(scale, 0.05, 8);
        double fit = Math.Min(scale, Math.Min((double)work.Width / sourceWidth, (double)work.Height / sourceHeight));
        int width = Math.Max(1, (int)Math.Round(sourceWidth * fit)), height = Math.Max(1, (int)Math.Round(sourceHeight * fit));
        return new(Math.Clamp(origin.X, work.Left, work.Right - width), Math.Clamp(origin.Y, work.Top, work.Bottom - height), width, height);
    }
}

internal sealed class PinWindow : Window
{
    private readonly BitmapSource _image;
    private readonly Settings _settings;
    private readonly Action<string> _saved;
    private double _scale = 1;
    private bool _placing;
    private bool _initialized;
    private bool _initialPlaced;
    internal bool ClickThrough { get; private set; }
    internal double Zoom => _scale;
    internal ContextMenu Menu { get; }
    private MenuItem _through = null!;
    internal PinWindow(BitmapSource image, Settings? settings = null, Action<string>? saved = null)
    {
        Ui.Theme(this); _image = image; _settings = settings ?? new Settings(); _saved = saved ?? (_ => { });
        Title = "轻截 · 贴图"; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        Width = Math.Min(image.PixelWidth, 650); Height = Math.Min(image.PixelHeight, 500);
        var view = new Image { Source = image, Stretch = Stretch.Fill, ToolTip = "拖动移动 · 滚轮缩放 · Ctrl+滚轮透明度 · 右键菜单\n穿透恢复：托盘 → 解除全部贴图穿透" };
        RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality); Content = view;
        Menu = CreateMenu(); view.ContextMenu = Menu;
        SourceInitialized += (_, _) =>
        {
            _initialized = true; Opacity = _settings.PinOpacity;
        };
        Loaded += (_, _) =>
        {
            if (_initialPlaced) return; _initialPlaced = true;
            var pointer = Forms.Cursor.Position; Place(_scale, pointer, Forms.Screen.FromPoint(pointer).WorkingArea);
        };
        MouseLeftButtonDown += (_, e) => { if (ClickThrough) return; e.Handled = true; Activate(); try { DragMove(); RecoverPlacement(); } catch (InvalidOperationException) { } };
        MouseWheel += (_, e) => { ApplyWheel(e.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); e.Handled = true; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } else if (e.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control) { ResetSize(); e.Handled = true; } };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Closed += (_, _) => { Menu.IsOpen = false; Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged; };
    }
    private ContextMenu CreateMenu()
    {
        var menu = new ContextMenu();
        menu.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("PinMenuStyles.xaml", UriKind.Relative) });
        menu.SetResourceReference(Control.BackgroundProperty, "PanelBackground"); menu.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        MenuItem Item(string title, Action action)
        {
            var item = new MenuItem { Header = title }; item.SetResourceReference(Control.BackgroundProperty, "PanelBackground"); item.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
            item.Click += (_, _) => action(); menu.Items.Add(item); return item;
        }
        Item("复制图片", async () => { try { await ImageExportService.CopyAsync(_image, _settings.ScreenshotFormat, _settings.ScreenshotQuality); } catch (Exception ex) { Ui.Error(this, ex); } });
        Item("保存图片…", () => { try { var dialog = new ScreenshotSaveWindow(this, _image, _settings); if (dialog.ShowDialog() == true) _saved(dialog.SavedPath!); } catch (Exception ex) { Ui.Error(this, ex); } });
        menu.Items.Add(new Separator());
        Item("恢复原尺寸 · Ctrl+0", ResetSize);
        var opacity = Item("透明度", () => { });
        foreach (double value in new[] { 1d, 0.8, 0.6, 0.4, 0.2 })
        {
            var choice = new MenuItem { Header = $"{value:P0}" }; choice.SetResourceReference(Control.ForegroundProperty, "TextPrimary"); choice.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
            choice.Click += (_, _) => SetOpacity(value); opacity.Items.Add(choice);
        }
        _through = Item("鼠标穿透（托盘可解除）", () => SetClickThrough(!ClickThrough)); _through.IsCheckable = true;
        Item("隐藏全部贴图（托盘可恢复）", PinManager.HideAll);
        menu.Items.Add(new Separator()); Item("关闭 · Esc", Close);
        menu.Opened += (_, _) => { _through.IsChecked = ClickThrough; foreach (MenuItem item in opacity.Items) item.IsChecked = item.Header?.ToString() == $"{Opacity:P0}"; };
        return menu;
    }
    internal void SetClickThrough(bool enabled) { Native.SetClickThrough(this, enabled); ClickThrough = enabled; _through.IsChecked = enabled; }
    internal void SetOpacity(double opacity)
    {
        Opacity = Math.Clamp(opacity, 0.2, 1); _settings.PinOpacity = Opacity;
        try { _settings.Save(); } catch (Exception ex) { Ui.Error(this, ex); }
    }
    internal void ApplyWheel(int delta, bool control)
    {
        if (delta == 0) return;
        if (control) { SetOpacity(Opacity + Math.Sign(delta) * 0.05); return; }
        var pointer = Forms.Cursor.Position; var bounds = PhysicalBounds();
        double scale = Math.Clamp(_scale * Math.Pow(1.1, delta / 120d), 0.05, 8);
        var work = Forms.Screen.FromPoint(pointer).WorkingArea;
        var size = PinGeometry.Place(_image.PixelWidth, _image.PixelHeight, scale, work, pointer).Size;
        double x = Math.Clamp((pointer.X - bounds.X) / (double)Math.Max(1, bounds.Width), 0, 1), y = Math.Clamp((pointer.Y - bounds.Y) / (double)Math.Max(1, bounds.Height), 0, 1);
        Place(scale, new Drawing.Point((int)Math.Round(pointer.X - x * size.Width), (int)Math.Round(pointer.Y - y * size.Height)), work);
    }
    internal void ResetSize() { var bounds = PhysicalBounds(); Place(1, bounds.Location, Forms.Screen.FromRectangle(bounds).WorkingArea); }
    private Drawing.Rectangle PhysicalBounds()
    {
        Native.GetWindowRect(new WindowInteropHelper(this).EnsureHandle(), out var bounds);
        return Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }
    private void Place(double scale, Drawing.Point origin, Drawing.Rectangle work)
    {
        if (_placing) return; _placing = true;
        try
        {
            var bounds = PinGeometry.Place(_image.PixelWidth, _image.PixelHeight, scale, work, origin);
            _scale = bounds.Width / (double)_image.PixelWidth;
            Native.SetWindowPos(new WindowInteropHelper(this).EnsureHandle(), new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010);
        }
        finally { _placing = false; }
    }
    internal void RecoverPlacement() { if (!_initialized || _placing) return; var bounds = PhysicalBounds(); Place(_scale, bounds.Location, Forms.Screen.FromRectangle(bounds).WorkingArea); }
    private void DisplayChanged(object? sender, EventArgs e) { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RecoverPlacement); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_initialized) Dispatcher.BeginInvoke(RecoverPlacement);
    }
}
