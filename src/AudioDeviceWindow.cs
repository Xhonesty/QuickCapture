using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QuickCapture;
internal sealed class AudioDeviceWindow : Window
{
    private readonly AudioMonitor _monitor = new();
    private readonly Settings _settings;
    private readonly bool _persistSelection;
    private readonly ComboBox _mic = new(), _system = new();
    private readonly TextBlock _status;
    private readonly AudioLevels _levels = new();
    private readonly Button _test, _refresh, _detect;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly string _testDirectory = Path.Combine(Paths.Data, "AudioTest");
    private bool _closed, _testing, _filling;
    internal Task Initialization { get; private set; } = Task.CompletedTask;
    internal AudioDeviceWindow(Window owner, Settings settings, bool persistSelection = true)
    {
        Ui.Theme(this); Owner = owner; _settings = settings; _persistSelection = persistSelection; Title = "声音设备与试录"; Width = 540; Height = 510; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(24) }; Content = root;
        var heading = UiDesign.Text("声音设备与电平"); heading.FontSize = UiDesign.Number("FontPageTitle"); heading.FontWeight = FontWeights.SemiBold; root.Children.Add(heading);
        root.Children.Add(UiDesign.Text("麦克风输入设备")); root.Children.Add(_mic);
        root.Children.Add(UiDesign.Text("系统声音 · 播放设备")); root.Children.Add(_system);
        root.Children.Add(UiDesign.Text("检测使用当前启用的音源。默认设备变化时需重新检测；录制期间会停止并保留已录内容。", true));
        root.Children.Add(_levels); _status = UiDesign.Text("正在读取设备…", true); root.Children.Add(_status);
        var actions = new WrapPanel(); root.Children.Add(actions);
        _refresh = Ui.Button("刷新设备", async () => await RefreshAsync()); actions.Children.Add(_refresh);
        _detect = Ui.Button("检测声音", async () => await DetectAsync()); actions.Children.Add(_detect);
        _test = Ui.Button("试录 3 秒", async () => await TestAsync()); actions.Children.Add(_test);
        var playback = new WrapPanel(); root.Children.Add(playback);
        foreach (var entry in new[] { ("回放麦克风", "microphone.wav"), ("回放系统声音", "system.wav") })
            playback.Children.Add(Ui.Button(entry.Item1, () => { string path = Path.Combine(_testDirectory, entry.Item2); if (File.Exists(path)) Native.Open(path); else _status.Text = "请先启用对应音源并试录。"; }));
        root.Children.Add(UiDesign.Text("试录保存在 Data/AudioTest，下一次试录覆盖。", true));
        root.Children.Add(Ui.Button("清理试录", () => { if (!_testing) foreach (string name in new[] { "microphone.wav", "system.wav" }) File.Delete(Path.Combine(_testDirectory, name)); }));
        _timer.Tick += (_, _) => _levels.Update(_monitor);
        _monitor.Faulted += message => Dispatcher.BeginInvoke(() => _status.Text = message);
        async void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling || _testing || _closed) return;
            try { SaveSelection(); if (_monitor.Active) await DetectAsync(); }
            catch (Exception ex) { _status.Text = ex.Message; }
        }
        _mic.SelectionChanged += SelectionChanged; _system.SelectionChanged += SelectionChanged;
        Loaded += (_, _) => { Initialization = RefreshAsync(); _timer.Start(); };
        Closed += async (_, _) => { _closed = true; _timer.Stop(); await _monitor.StopAsync(); };
    }
    private async Task RefreshAsync()
    {
        try
        {
            var mic = await Task.Run(() => AudioMonitor.Devices(true)); var system = await Task.Run(() => AudioMonitor.Devices(false));
            if (_closed) return; _filling = true;
            void Fill(ComboBox box, System.Collections.Generic.IReadOnlyList<AudioDeviceChoice> choices, string id)
            { var items = choices.ToList(); if (!items.Any(x => x.Id == id)) items.Add(new(id, "设备缺失 · 请重新选择")); box.ItemsSource = items; box.SelectedItem = items.First(x => x.Id == id); }
            Fill(_mic, mic, _settings.MicrophoneDeviceId); Fill(_system, system, _settings.SystemAudioDeviceId);
            _status.Text = mic.Any(d => d.Id == _settings.MicrophoneDeviceId) && system.Any(d => d.Id == _settings.SystemAudioDeviceId) ? (_persistSelection ? "选择设备后点击检测或试录，选择将自动保存。" : "选择设备后点击检测或试录；关闭后返回设置，统一保存。") : "设备缺失，请重新选择。当前选择保留，不会自动替换。";
        }
        catch (Exception ex) { _status.Text = "读取设备失败：" + ex.Message; }
        finally { _filling = false; }
    }
    private void SaveSelection()
    { _settings.MicrophoneDeviceId = (_mic.SelectedItem as AudioDeviceChoice)?.Id ?? _settings.MicrophoneDeviceId; _settings.SystemAudioDeviceId = (_system.SelectedItem as AudioDeviceChoice)?.Id ?? _settings.SystemAudioDeviceId; if (_persistSelection) _settings.Save(); }
    private async Task DetectAsync()
    { try { SaveSelection(); await _monitor.StartAsync(_settings); if (_closed) await _monitor.StopAsync(); else _status.Text = _monitor.Active ? "正在检测真实声音；持续过高时显示提示。" : "两个音源均已关闭，请先启用。"; } catch (Exception ex) { _status.Text = ex.Message; } }
    private async Task TestAsync()
    {
        _testing = true; _test.IsEnabled = _refresh.IsEnabled = _detect.IsEnabled = _mic.IsEnabled = _system.IsEnabled = false;
        try
        {
            SaveSelection(); await _monitor.StopAsync(); Directory.CreateDirectory(_testDirectory);
            foreach (string name in new[] { "microphone.wav", "system.wav" }) File.Delete(Path.Combine(_testDirectory, name));
            await _monitor.StartAsync(_settings, _testDirectory); _status.Text = _monitor.Active ? "试录中，请讲话或播放声音…" : "两个音源均已关闭，请在主界面启用。";
            await Task.Delay(3000); await _monitor.StopAsync(); if (!_closed && (_settings.Microphone || _settings.SystemAudio)) _status.Text = "试录完成，点击对应回放检查声音；无电平时请讲话或播放音频重试。";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _testing = false; _test.IsEnabled = _refresh.IsEnabled = _detect.IsEnabled = _mic.IsEnabled = _system.IsEnabled = true; }
    }
}
internal sealed class AudioLevels : StackPanel
{
    private readonly bool _compact;
    private readonly ProgressBar[] _bars = { new(), new() };
    private readonly TextBlock[] _labels = { new(), new() };
    internal AudioLevels(bool compact = false)
    {
        _compact = compact;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("RecordingEditorStyles.xaml", UriKind.Relative) });
        for (int i = 0; i < 2; i++)
        {
            var row = new DockPanel { Margin = new Thickness(0, compact ? 1 : 4, 0, 0) }; Children.Add(row);
            _labels[i].Text = i == 0 ? (compact ? "麦" : "麦克风") : (compact ? "系" : "系统"); _labels[i].Width = compact ? 45 : 78; _labels[i].FontSize = compact ? 10 : 12; _labels[i].SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); DockPanel.SetDock(_labels[i], Dock.Left); row.Children.Add(_labels[i]);
            _bars[i].Maximum = 1; _bars[i].Height = compact ? 5 : 8; _bars[i].VerticalAlignment = VerticalAlignment.Center; _bars[i].SetResourceReference(Control.ForegroundProperty, "Accent"); row.Children.Add(_bars[i]);
        }
    }
    internal void Update(AudioMonitor monitor)
    {
        for (int i = 0; i < 2; i++)
        {
            var reading = monitor.Read(i == 0); bool disabled = reading.Status == "已关闭";
            _bars[i].Value = Math.Sqrt(reading.Peak); _bars[i].Opacity = disabled ? .4 : 1;
            _bars[i].SetResourceReference(Control.ForegroundProperty, reading.High || !reading.Ready && !disabled ? "Danger" : "Accent");
            _labels[i].Text = (i == 0 ? (_compact ? "麦" : "麦克风") : (_compact ? "系" : "系统")) + (disabled ? "·关" : reading.High || !reading.Ready ? " !" : "");
            _bars[i].ToolTip = _labels[i].ToolTip = reading.High ? "电平持续过高，请降低输入或播放音量。" : reading.Status;
        }
    }
}
