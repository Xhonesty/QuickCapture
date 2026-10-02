using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace QuickCapture;

internal sealed class RecordingBar : Window
{
    private readonly TextBlock _time = new() { Text = "00:00:00", FontSize = 14 };
    private readonly TextBlock _status = new() { Text = "准备录制…", FontSize = 11 };
    private readonly TextBlock _marker = new() { Text = "●", FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Stopwatch _watch = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Button _stop, _pause;
    private readonly Func<TimeSpan>? _elapsed;
    private readonly Func<Drawing.Rectangle?>? _targetBounds;
    private readonly bool _displayCapture;
    private Drawing.Rectangle? _lastTarget;
    private string? _fallbackMonitor;
    private DpiScale _lastDpi;
    private bool _userPositioned, _placing;
    private RecordingState _state = RecordingState.Starting;
    internal bool CaptureExcluded { get; private set; }
    internal bool HiddenForCaptureSafety { get; private set; }
    internal bool UserPositioned => _userPositioned;
    internal TimeSpan Elapsed => _elapsed?.Invoke() ?? _watch.Elapsed;

    // Kept for existing callers and UI verification fixtures.
    public RecordingBar(string hotkey, Action stop) : this(hotkey, stop, null) { }

    public RecordingBar(string hotkey, Action stop, Action? togglePause, Func<TimeSpan>? elapsed = null,
        Func<Drawing.Rectangle?>? targetBounds = null, bool displayCapture = false)
    {
        _elapsed = elapsed; _targetBounds = targetBounds; _displayCapture = displayCapture;
        Ui.Theme(this);
        Title = "轻截 · 准备录制"; Width = 332; Height = 60; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Left = SystemParameters.WorkArea.Right - Width - 24; Top = SystemParameters.WorkArea.Bottom - Height - 24;
        var panel = new Grid(); panel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var drag = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.SizeAll, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        var info = new StackPanel(); info.Children.Add(_time); info.Children.Add(_status); drag.Children.Add(_marker); drag.Children.Add(info);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); panel.Children.Add(drag);
        _pause = Ui.Button("暂停", () => togglePause?.Invoke()); _pause.Name = "RecordingPause"; _pause.Width = 66; _pause.Margin = new Thickness(4, 0, 6, 0);
        _pause.ToolTip = "暂停／继续当前录像；暂停时不录入声音或画面"; _pause.IsEnabled = false; Grid.SetColumn(_pause, 1); panel.Children.Add(_pause);
        _stop = Ui.Button("停止并保存", stop); _stop.Name = "RecordingStop"; _stop.Width = 96; _stop.Margin = new Thickness(0); _stop.ToolTip = hotkey; _stop.IsEnabled = false;
        Grid.SetColumn(_stop, 2); panel.Children.Add(_stop);
        var frame = UiDesign.Panel(panel); frame.Padding = new Thickness(10); frame.Margin = new Thickness(0); Content = frame;
        drag.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            _userPositioned = true;
            try { DragMove(); } catch (InvalidOperationException) { }
            ClampManualPosition(); EnsureSafeVisibility(); e.Handled = true;
        };
        SourceInitialized += (_, _) => CaptureExcluded = Native.ExcludeFromCapture(this);
        Loaded += (_, _) => { CaptureExcluded = Native.ExcludeFromCapture(this); UpdatePlacement(true); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => { UpdatePlacement(true); if (_userPositioned) ClampManualPosition(); });
        _timer.Tick += (_, _) => { RefreshTime(); UpdatePlacement(); };
        Closed += (_, _) => _timer.Stop();
        SetMarker(false);
    }

    public void SetState(RecordingState state)
    {
        if (state == RecordingState.Recording) SetRecording();
        else if (state == RecordingState.Paused) SetPaused();
        else if (state == RecordingState.Stopping) SetSaving();
    }
    public void SetRecording()
    {
        if (_state == RecordingState.Stopping) return;
        if (_elapsed == null && _state != RecordingState.Recording) _watch.Start();
        _state = RecordingState.Recording; _pause.Content = "暂停"; _pause.IsEnabled = true; _stop.IsEnabled = true; _status.Text = "录制中 · 拖动移动";
        Title = "轻截 · 录制中"; SetMarker(false); RefreshTime(); _timer.Start();
    }
    public void SetPaused()
    {
        if (_state == RecordingState.Stopping) return;
        _watch.Stop(); _state = RecordingState.Paused; _pause.Content = "继续"; _pause.IsEnabled = true;
        _status.Text = "已暂停"; Title = "轻截 · 已暂停"; SetMarker(true); RefreshTime();
        // Keep placement responsive while a recorded window moves during pause.
        _timer.Start();
    }
    public void SetSaving()
    {
        _watch.Stop(); _state = RecordingState.Stopping; _timer.Stop(); _status.Text = "正在结束录制…";
        Title = "轻截 · 保存中"; _stop.IsEnabled = false; _pause.IsEnabled = false; RefreshTime();
    }
    private void SetMarker(bool paused) => _marker.Foreground = new SolidColorBrush(paused ? Color.FromRgb(245, 190, 85) : Color.FromRgb(255, 99, 115));
    private void RefreshTime() => _time.Text = Elapsed.ToString(@"hh\:mm\:ss");

    internal static Drawing.Rectangle PlacementBounds(Drawing.Rectangle selection, Drawing.Size toolbar, Drawing.Rectangle available, double dpiScale = 1)
    {
        // Translate origin and sizes into monitor-local DIPs. SetWindowPos uses
        // absolute physical pixels, including negative monitor origins.
        dpiScale = Math.Max(0.1, dpiScale);
        var localSelection = new Rect((selection.X - available.X) / dpiScale, (selection.Y - available.Y) / dpiScale, selection.Width / dpiScale, selection.Height / dpiScale);
        var localAvailable = new Rect(0, 0, available.Width / dpiScale, available.Height / dpiScale);
        var size = new Size(toolbar.Width / dpiScale, toolbar.Height / dpiScale);
        var point = ToolbarPlacement.Place(localSelection, size, localAvailable);
        return new(available.X + (int)Math.Round(point.X * dpiScale), available.Y + (int)Math.Round(point.Y * dpiScale), toolbar.Width, toolbar.Height);
    }
    internal void UpdatePlacement(bool force = false)
    {
        if (_placing || !IsLoaded) return;
        var target = _targetBounds?.Invoke(); var dpi = VisualTreeHelper.GetDpi(this);
        if (!force && target == _lastTarget && dpi.Equals(_lastDpi)) return;
        _lastTarget = target; _lastDpi = dpi;
        if (_userPositioned) { ClampManualPosition(); EnsureSafeVisibility(); return; }
        var anchor = target ?? Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        var monitor = Forms.Screen.FromRectangle(anchor); var available = monitor.WorkingArea;
        var size = new Drawing.Size((int)Math.Ceiling(Width * dpi.DpiScaleX), (int)Math.Ceiling(Height * dpi.DpiScaleY));
        if (_fallbackMonitor != null)
            foreach (var alternate in Forms.Screen.AllScreens)
                if (alternate.DeviceName == _fallbackMonitor && (target == null || !alternate.Bounds.IntersectsWith(target.Value)))
                { PlacePhysical(new(alternate.WorkingArea.Right - size.Width - 12, alternate.WorkingArea.Bottom - size.Height - 12, size.Width, size.Height)); return; }
        _fallbackMonitor = null;
        var bounds = target is { } region ? PlacementBounds(region, size, available, dpi.DpiScaleX)
            : new Drawing.Rectangle(available.Right - size.Width - 16, available.Bottom - size.Height - 16, size.Width, size.Height);
        PlacePhysical(bounds); EnsureSafeVisibility();
    }
    private void PlacePhysical(Drawing.Rectangle bounds)
    {
        _placing = true;
        try { Native.Place(this, bounds, false); }
        finally { _placing = false; }
    }
    private void ClampManualPosition()
    {
        if (!Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect)) return;
        var current = Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom); var available = Forms.Screen.FromRectangle(current).WorkingArea;
        int x = Math.Clamp(current.X, available.Left, Math.Max(available.Left, available.Right - current.Width));
        int y = Math.Clamp(current.Y, available.Top, Math.Max(available.Top, available.Bottom - current.Height));
        if (x != current.X || y != current.Y) PlacePhysical(new(x, y, current.Width, current.Height));
    }
    private void EnsureSafeVisibility()
    {
        if (CaptureExcluded || !_displayCapture || _lastTarget is not { } target) return;
        if (!Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect)) return;
        var bounds = Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (!bounds.IntersectsWith(target)) return;
        // Without capture exclusion, another monitor is a reliable fallback;
        // otherwise hide the bar and keep tray / stop hotkey controls available.
        foreach (var screen in Forms.Screen.AllScreens)
        {
            if (screen.Bounds.IntersectsWith(target) || screen.WorkingArea.Width < bounds.Width || screen.WorkingArea.Height < bounds.Height) continue;
            _fallbackMonitor = screen.DeviceName;
            PlacePhysical(new(screen.WorkingArea.Right - bounds.Width - 12, screen.WorkingArea.Bottom - bounds.Height - 12, bounds.Width, bounds.Height)); return;
        }
        HiddenForCaptureSafety = true; Hide();
    }
}
