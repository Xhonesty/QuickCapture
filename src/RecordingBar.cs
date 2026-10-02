using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickCapture;

internal sealed class RecordingBar : Window
{
    private readonly TextBlock _time = new() { Text = "准备录制…", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 18, 0) };
    private readonly Stopwatch _watch = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Button _stop;
    public RecordingBar(string hotkey, Action stop)
    {
        Ui.Theme(this);
        Title = "轻截 · 录制中"; Width = 360; Height = 70; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; ShowInTaskbar = false; Topmost = true;
        Left = SystemParameters.WorkArea.Right - Width - 24; Top = SystemParameters.WorkArea.Bottom - Height - 24;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "●", Foreground = new SolidColorBrush(Color.FromRgb(255, 99, 115)), FontSize = 20, VerticalAlignment = VerticalAlignment.Center }); panel.Children.Add(_time);
        _stop = Ui.Button("停止并保存", stop); _stop.ToolTip = hotkey; panel.Children.Add(_stop); var frame = UiDesign.Panel(panel); frame.Padding = UiDesign.Padding("PopupPadding"); frame.Margin = new Thickness(0); Content = frame;
        MouseLeftButtonDown += (_, _) => DragMove();
        Loaded += (_, _) => Native.ExcludeFromCapture(this);
        _timer.Tick += (_, _) => _time.Text = _watch.Elapsed.ToString(@"hh\:mm\:ss");
        Closed += (_, _) => _timer.Stop();
    }
    public void SetRecording() { _watch.Restart(); _timer.Start(); }
    public void SetSaving() { _timer.Stop(); _time.Text = "保存中…"; _stop.IsEnabled = false; }
}
