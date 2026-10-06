using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal sealed class OcrWindow : Window
{
    private readonly Func<BitmapSource> _image;
    private readonly Settings _settings;
    private readonly Func<BitmapSource, string, CancellationToken, Task<OcrResult>> _recognize;
    private readonly TextBlock _status;
    private readonly TextBox _text;
    private readonly ComboBox _language;
    private readonly CheckBox _merge;
    private readonly Button _run, _cancel, _copy;
    private CancellationTokenSource? _task;
    private bool _closed;
    internal OcrState State { get; private set; } = OcrState.Ready;
    internal Task Recognition { get; private set; } = Task.CompletedTask;
    internal TextBox ResultInput => _text;
    internal OcrWindow(Window owner, Func<BitmapSource> image, Settings settings,
        Func<BitmapSource, string, CancellationToken, Task<OcrResult>>? recognize = null)
    {
        Ui.Theme(this); Owner = owner; _image = image; _settings = settings; _recognize = recognize ?? OcrService.RecognizeAsync;
        Title = "轻截 · 提取文字"; Width = 540; Height = 540; MinWidth = 390; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Topmost = owner.Topmost;
        InputMethod.SetIsInputMethodEnabled(this, true);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "提取文字", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        header.Children.Add(UiDesign.Text("本地识别当前裁剪范围的原图 · 结果可直接编辑", true));
        _language = new ComboBox { Name = "OcrLanguage", ItemsSource = new[] { "简体中文 + English", "繁體中文 + English", "English" }, SelectedIndex = settings.OcrLanguage switch { "chi_tra+eng" => 1, "eng" => 2, _ => 0 }, Margin = new Thickness(0, 10, 0, 12) }; header.Children.Add(_language);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), MinHeight = 36 }; _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); header.Children.Add(_status);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _merge = new CheckBox { Content = "复制时合并换行（未勾选则保留换行）", IsChecked = settings.OcrMergeLines, Margin = new Thickness(0, 0, 0, 12) }; footer.Children.Add(_merge);
        var buttons = new WrapPanel(); footer.Children.Add(buttons);
        _run = Ui.Button("重新识别当前范围", () => Recognition = RunAsync()); _cancel = Ui.Button("取消识别", CancelRecognition);
        _copy = Ui.Button("复制文字", async () => await CopyAsync());
        buttons.Children.Add(_run); buttons.Children.Add(_cancel); buttons.Children.Add(_copy);
        _text = new TextBox { Name = "OcrResult", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), FontSize = 15 }; root.Children.Add(_text);
        _text.TextChanged += (_, _) => _copy.IsEnabled = !string.IsNullOrWhiteSpace(_text.Text);
        _language.SelectionChanged += (_, _) => { _settings.OcrLanguage = SelectedLanguage; SavePreferences(); };
        _merge.Checked += (_, _) => { _settings.OcrMergeLines = true; SavePreferences(); };
        _merge.Unchecked += (_, _) => { _settings.OcrMergeLines = false; SavePreferences(); };
        Loaded += (_, _) => Recognition = RunAsync();
        Closed += (_, _) => { _closed = true; _task?.Cancel(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        SetState(OcrState.Ready, "准备识别…");
    }
    private string SelectedLanguage => _language.SelectedIndex switch { 1 => "chi_tra+eng", 2 => "eng", _ => "chi_sim+eng" };
    private void SavePreferences() { try { _settings.Save(); } catch (Exception ex) { ErrorLog.Write(ex); _status.Text = "设置保存失败：" + ex.Message; } }
    internal void CancelRecognition() => _task?.Cancel();
    private void SetState(OcrState state, string message)
    {
        State = state; _status.Text = message;
        _run.IsEnabled = _language.IsEnabled = state != OcrState.Recognizing;
        _cancel.Visibility = state == OcrState.Recognizing ? Visibility.Visible : Visibility.Collapsed;
        _copy.IsEnabled = !string.IsNullOrWhiteSpace(_text.Text);
    }
    internal async Task RunAsync()
    {
        if (_closed || _task != null) return;
        using var task = new CancellationTokenSource(); _task = task;
        SetState(OcrState.Recognizing, "正在本地识别… 可继续编辑截图，或取消识别。");
        try
        {
            var result = await _recognize(_image(), SelectedLanguage, task.Token);
            if (_closed) return;
            task.Token.ThrowIfCancellationRequested();
            if (result.State == OcrState.Success) _text.Text = result.Text;
            else _text.Clear();
            SetState(result.State, result.State == OcrState.Success ? $"识别完成 · {result.Text.Length} 字符 · 可编辑后复制" : result.Message);
        }
        catch (OperationCanceledException) { if (!_closed) SetState(OcrState.Cancelled, "已取消识别，已有编辑内容保留。"); }
        catch (Exception ex) { ErrorLog.Write(ex); if (!_closed) SetState(OcrState.Failed, "识别失败：" + ex.Message); }
        finally { _task = null; }
    }
    internal async Task CopyAsync()
    {
        string text = _merge.IsChecked == true ? OcrService.MergeLines(_text.Text) : _text.Text;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try { Clipboard.SetText(text); break; }
                catch (COMException) when (attempt < 4) { await Task.Delay(60 * (attempt + 1)); if (_closed) return; }
            }
            if (!_closed) _status.Text = "已复制文字";
        }
        catch (Exception ex) { ErrorLog.Write(ex); if (!_closed) _status.Text = "复制失败，请重试：" + ex.Message; }
    }
}
