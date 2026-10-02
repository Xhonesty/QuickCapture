using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture;

internal static class AnnotationColors
{
    private static readonly List<Color> _recent = new();
    internal static IReadOnlyList<Color> Recent => _recent;
    internal static void Remember(Color color) { _recent.Remove(color); _recent.Insert(0, color); if (_recent.Count > 10) _recent.RemoveRange(10, _recent.Count - 10); }
    internal static string Hex(Color color) => color.A == 255 ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    internal static bool TryParse(string value, out Color color)
    {
        color = default; string hex = value.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed)) return false;
        color = Color.FromArgb(hex.Length == 8 ? (byte)(parsed >> 24) : (byte)255, (byte)(parsed >> 16), (byte)(parsed >> 8), (byte)parsed); return true;
    }
    internal static Color Hsv(double hue, double saturation, double value, byte alpha = 255)
    {
        hue = (hue % 360 + 360) % 360; saturation = Math.Clamp(saturation, 0, 1); value = Math.Clamp(value, 0, 1);
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        var (r, g, b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromArgb(alpha, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
    internal static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double hue = delta < 0.000001 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return ((hue + 360) % 360, max <= 0 ? 0 : delta / max, max);
    }
}

internal sealed class SaturationValuePicker : FrameworkElement
{
    internal double Hue { get; set; }
    internal double Saturation { get; set; } = 1;
    internal double Value { get; set; } = 1;
    internal event Action? Changed;
    internal SaturationValuePicker()
    {
        Height = 105; Cursor = Cursors.Cross; Focusable = true;
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured) { Pick(e.GetPosition(this)); e.Handled = true; } };
        MouseLeftButtonUp += (_, e) => { if (IsMouseCaptured) { Pick(e.GetPosition(this)); ReleaseMouseCapture(); e.Handled = true; } };
    }
    private void Pick(Point p) { Saturation = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1); Value = 1 - Math.Clamp(p.Y / Math.Max(1, ActualHeight), 0, 1); InvalidateVisual(); Changed?.Invoke(); }
    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(new LinearGradientBrush(Colors.White, AnnotationColors.Hsv(Hue, 1, 1), 0), null, rect);
        dc.DrawRectangle(new LinearGradientBrush(Colors.Transparent, Colors.Black, 90), null, rect);
        var point = new Point(Saturation * ActualWidth, (1 - Value) * ActualHeight);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 3), point, 4, 4); dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), point, 4, 4);
    }
}

// Draft HSV / HEX changes commit once on Apply, keeping undo history useful.
internal sealed class ColorPalette : StackPanel
{
    private readonly Action<Color> _apply;
    private readonly Action _targetChanged;
    private readonly ComboBox _target;
    private readonly Border _current = new() { Width = 28, Height = 22, CornerRadius = new(4) };
    private readonly TextBlock _currentHex = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status;
    private readonly WrapPanel _recent = new();
    private readonly SaturationValuePicker _sv = new();
    private readonly Slider _hue = new() { Minimum = 0, Maximum = 359.9, Height = 24, ToolTip = "色相" };
    private readonly Slider _alpha = new() { Minimum = 0, Maximum = 100, Height = 24, ToolTip = "不透明度" };
    private readonly TextBox _hex = new() { ToolTip = "HEX：#RRGGBB 或 #AARRGGBB", Height = 30 };
    private readonly Button _noFill;
    private readonly Border _draftSample = new() { Width = 24, Height = 24, CornerRadius = new(4), Margin = new(4, 0, 0, 0) };
    private bool _updating;
    private Color _draft;
    internal bool EditingFill => _target.SelectedIndex == 1;
    internal Color Draft => _draft;
    internal ColorPalette(Action<Color> apply, Action targetChanged, Action clearFill, Action<bool> eyedropper)
    {
        _apply = apply; _targetChanged = targetChanged; Width = 240;
        Children.Add(UiDesign.Text("标注颜色 · K", true));
        _target = new ComboBox { ItemsSource = new[] { "描边 / 文字 / 画笔", "形状填充" }, SelectedIndex = 0, Margin = new(0, 0, 0, 7), ToolTip = "正在编辑的颜色属性" }; Children.Add(_target);
        _target.SelectionChanged += (_, _) => { if (!_updating) _targetChanged(); };
        var current = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 7) }; current.Children.Add(_current); current.Children.Add(new TextBlock { Text = "当前 ", Margin = new(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }); current.Children.Add(_currentHex); Children.Add(current);
        var presets = new WrapPanel(); Children.Add(presets);
        foreach (var color in new[] { Color.FromRgb(255, 99, 115), Color.FromRgb(130, 226, 187), Colors.Gold, Colors.DodgerBlue, Colors.Violet, Colors.Orange, Colors.White, Colors.Black }) presets.Children.Add(Swatch(color));
        _noFill = Ui.Button("无填充", clearFill); _noFill.Height = 28; _noFill.Margin = new(0, 3, 0, 6); Children.Add(_noFill);
        Children.Add(UiDesign.Text("最近使用", true)); Children.Add(_recent);
        Children.Add(UiDesign.Text("自定义 · 调整后点击应用", true)); Children.Add(_sv);
        Children.Add(_hue); Children.Add(UiDesign.Text("不透明度", true)); Children.Add(_alpha);
        var edit = new DockPanel { Margin = new(0, 3, 0, 5) }; DockPanel.SetDock(_draftSample, Dock.Right); edit.Children.Add(_draftSample); edit.Children.Add(_hex); Children.Add(edit);
        var actions = new UniformGrid { Columns = 2 }; var applyButton = Ui.Button("应用颜色", () => { if (AnnotationColors.TryParse(_hex.Text, out var color)) Commit(color); else SetStatus("请输入 6 或 8 位 HEX 色值。"); }); actions.Children.Add(applyButton); actions.Children.Add(Ui.Button("画布吸管", () => eyedropper(false))); Children.Add(actions);
        var screen = Ui.Button("屏幕吸管", () => eyedropper(true)); screen.Height = 28; screen.Margin = new(0, 5, 0, 0); Children.Add(screen);
        _status = UiDesign.Text("吸管：单击确认 · Esc 取消", true); _status.Margin = new(0, 6, 0, 0); Children.Add(_status);
        _hue.ValueChanged += (_, _) => { if (!_updating) { _sv.Hue = _hue.Value; UpdateDraftFromSliders(); } };
        _alpha.ValueChanged += (_, _) => { if (!_updating) UpdateDraftFromSliders(); };
        _sv.Changed += UpdateDraftFromSliders;
        _hex.TextChanged += (_, _) => { if (!_updating && AnnotationColors.TryParse(_hex.Text, out var color)) SetDraft(color, false); };
        // Editor disables IME tool composition; HEX remains a normal text field.
        InputMethod.SetIsInputMethodEnabled(_hex, true);
    }
    private Button Swatch(Color color)
    {
        var sample = new Border { Background = new SolidColorBrush(color), Width = 21, Height = 21, CornerRadius = new(3), BorderBrush = Brushes.Gray, BorderThickness = new(0.5) };
        var button = Ui.Button("", () => Commit(color)); button.Content = sample; button.Width = button.Height = 28; button.Padding = new(0); button.Margin = new(0, 0, 2, 4); button.ToolTip = AnnotationColors.Hex(color); return button;
    }
    private void Commit(Color color) { AnnotationColors.Remember(color); _apply(color); RefreshRecent(); }
    internal void RefreshRecent() { _recent.Children.Clear(); foreach (var color in AnnotationColors.Recent) _recent.Children.Add(Swatch(color)); }
    internal void SetStatus(string message) => _status.Text = message;
    internal void SetCurrent(Color color, bool canFill, bool filled)
    {
        _updating = true; if (!canFill) _target.SelectedIndex = 0; _target.IsEnabled = canFill; _noFill.Visibility = canFill && EditingFill ? Visibility.Visible : Visibility.Collapsed; _updating = false;
        _current.Background = new SolidColorBrush(color); _currentHex.Text = EditingFill && !filled ? "无填充" : AnnotationColors.Hex(color); SetDraft(color); RefreshRecent();
    }
    private void SetDraft(Color color, bool writeHex = true)
    {
        _updating = true; _draft = color; var hsv = AnnotationColors.ToHsv(color); _hue.Value = hsv.Hue; _sv.Hue = hsv.Hue; _sv.Saturation = hsv.Saturation; _sv.Value = hsv.Value; _alpha.Value = color.A * 100d / 255; if (writeHex) _hex.Text = AnnotationColors.Hex(color); _draftSample.Background = new SolidColorBrush(color); _sv.InvalidateVisual(); _updating = false;
    }
    private void UpdateDraftFromSliders()
    {
        if (_updating) return; _draft = AnnotationColors.Hsv(_hue.Value, _sv.Saturation, _sv.Value, (byte)Math.Round(_alpha.Value * 255 / 100)); _updating = true; _hex.Text = AnnotationColors.Hex(_draft); _updating = false; _draftSample.Background = new SolidColorBrush(_draft); _sv.InvalidateVisual();
    }
}
