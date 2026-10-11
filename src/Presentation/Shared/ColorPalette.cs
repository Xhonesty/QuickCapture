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
    internal static readonly (string Name, Color Color)[] Presets =
    {
        ("红", Colors.Red), ("绿", Colors.Lime), ("蓝", Colors.Blue), ("黄", Colors.Yellow),
        ("青", Colors.Cyan), ("品红", Colors.Magenta), ("橙", Colors.Orange),
        ("灰", Colors.Gray), ("白", Colors.White), ("黑", Colors.Black)
    };
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
        var point = new Point(Math.Clamp(Saturation * ActualWidth, 6, Math.Max(6, ActualWidth - 6)), Math.Clamp((1 - Value) * ActualHeight, 6, Math.Max(6, ActualHeight - 6)));
        dc.DrawEllipse(null, new Pen(Brushes.Black, 3), point, 4, 4); dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), point, 4, 4);
    }
}

// Draft HSV / HEX changes commit once on Apply, keeping undo history useful.
internal sealed class ColorPalette : StackPanel
{
    private readonly Action<Color> _apply;
    private readonly Action _targetChanged;
    private readonly ComboBox _target;
    private readonly TextBlock _title;
    private readonly TextBlock _status;
    private readonly WrapPanel _recent = new();
    private readonly SaturationValuePicker _sv = new();
    private readonly Slider _hue = new() { Minimum = 0, Maximum = 359.9, Height = 26, ToolTip = "色相", Name = "PaletteHue" };
    private readonly Slider _alpha = new() { Minimum = 0, Maximum = 100, Height = 26, ToolTip = "不透明度", Name = "PaletteAlpha" };
    private readonly TextBlock _alphaValue = new() { TextAlignment = TextAlignment.Right };
    private readonly TextBox _hex = new() { ToolTip = "HEX：#RRGGBB 或 #AARRGGBB（透明度在前）", Height = 30, MinHeight = 0, MinWidth = 0, Name = "PaletteHex" };
    private readonly Button _noFill;
    private readonly Border _draftSample = new() { Width = 30, Height = 30, CornerRadius = new(5) };
    private readonly List<(Color Color, Button Button)> _swatches = new();
    private readonly Button _applyButton, _canvasPicker;
    private readonly string? _fixedTarget;
    private bool _updating;
    private Color _draft;
    private readonly bool? _fixedFill;
    private readonly Action<Color>? _previewColor;
    internal bool EditingFill => _fixedFill ?? (_target.SelectedIndex == 1);
    internal Color Draft => _draft;
    internal string Status => _status.Text;
    internal ColorPalette(Action<Color> apply, Action targetChanged, Action clearFill, Action<bool> eyedropper, string? fixedTarget = null, bool fillTarget = false, Action<Color>? previewColor = null, Action? cancel = null)
    {
        _apply = apply; _targetChanged = targetChanged; _fixedFill = fixedTarget == null ? null : fillTarget;
        _fixedTarget = fixedTarget; _previewColor = previewColor; Width = 264; UseLayoutRounding = true;
        var heading = new DockPanel { Margin = new(0, 0, 0, 8) };
        if (cancel != null)
        {
            var close = IconButton("x", "关闭调色盘并取消修改", cancel); close.Name = "PaletteClose";
            DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        }
        _title = UiDesign.Text(fixedTarget ?? "标注颜色"); _title.FontWeight = FontWeights.SemiBold;
        _title.VerticalAlignment = VerticalAlignment.Center; _title.Margin = new(0);
        heading.Children.Add(_title); Children.Add(heading);
        _target = new ComboBox { ItemsSource = new[] { "描边颜色", "形状颜色" }, SelectedIndex = 0,
            MinWidth = 0, Margin = new(0, 0, 0, 8), ToolTip = "正在编辑的颜色属性" }; Children.Add(_target);
        _target.SelectionChanged += (_, _) => { if (!_updating) _targetChanged(); };
        if (fixedTarget != null) _target.Visibility = Visibility.Collapsed;
        var current = new Grid { Margin = new(0, 0, 0, 10) };
        current.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); current.ColumnDefinitions.Add(new());
        current.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var sample = new Border { Child = _draftSample, Background = CheckerBrush, CornerRadius = new(5),
            BorderThickness = new(1), Margin = new(0, 0, 8, 0), ClipToBounds = true };
        sample.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); current.Children.Add(sample);
        Grid.SetColumn(_hex, 1); current.Children.Add(_hex);
        _canvasPicker = IconButton("pipette", fixedTarget == null ? "画布吸管" : "原片吸管", () => eyedropper(false));
        _canvasPicker.Name = "PaletteEyedropper"; _canvasPicker.Margin = new(6, 0, 0, 0);
        Grid.SetColumn(_canvasPicker, 2); current.Children.Add(_canvasPicker); Children.Add(current);
        Children.Add(UiDesign.Text("预设颜色", true));
        var presets = new UniformGrid { Columns = 5, Margin = new(-2, 0, -2, 6) }; Children.Add(presets);
        foreach (var (name, color) in AnnotationColors.Presets) presets.Children.Add(Swatch(color, name, true));
        _noFill = Ui.Button("无填充", clearFill); _noFill.Height = 28; _noFill.Margin = new(0, 3, 0, 6); Children.Add(_noFill);
        Children.Add(UiDesign.Text("最近使用", true)); Children.Add(_recent);
        Children.Add(UiDesign.Text("自定义", true));
        var svFrame = new Border { Child = _sv, CornerRadius = new(6), ClipToBounds = true, BorderThickness = new(1) };
        svFrame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); Children.Add(svFrame);
        _hue.Template = GradientSliderTemplate(); _alpha.Template = GradientSliderTemplate();
        var rainbow = new LinearGradientBrush();
        foreach (double hue in new[] { 0d, 60, 120, 180, 240, 300, 360 })
            rainbow.GradientStops.Add(new(AnnotationColors.Hsv(hue, 1, 1), hue / 360));
        _hue.Background = rainbow; Children.Add(_hue);
        var alphaLabel = new DockPanel(); DockPanel.SetDock(_alphaValue, Dock.Right); alphaLabel.Children.Add(_alphaValue);
        alphaLabel.Children.Add(UiDesign.Text("不透明度", true)); Children.Add(alphaLabel); Children.Add(_alpha);
        var actions = new UniformGrid { Columns = 2, Margin = new(0, 6, 0, 0) };
        if (cancel != null) { var cancelButton = Ui.Button("取消", cancel); cancelButton.Name = "PaletteCancel"; actions.Children.Add(cancelButton); }
        _applyButton = Ui.Button("应用颜色", () => { if (AnnotationColors.TryParse(_hex.Text, out var color)) Commit(color); else SetStatus("请输入 6 或 8 位 HEX 色值。"); });
        _applyButton.Name = "PaletteApply"; _applyButton.SetResourceReference(Control.BackgroundProperty, "Accent");
        _applyButton.SetResourceReference(Control.ForegroundProperty, "AccentForeground"); _applyButton.Margin = new(3, 0, 0, 0);
        actions.Children.Add(_applyButton); Children.Add(actions);
        var screen = Ui.Button("屏幕吸管", () => eyedropper(true)); screen.Height = 28; screen.Margin = new(0, 5, 0, 0); screen.Visibility = fixedTarget == null ? Visibility.Visible : Visibility.Collapsed; Children.Add(screen);
        if (cancel != null) PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { cancel(); e.Handled = true; } };
        _status = UiDesign.Text("单击预览 · 双击色块应用 · Esc 取消", true); _status.Margin = new(0, 7, 0, 0); Children.Add(_status);
        _hue.ValueChanged += (_, _) => { if (!_updating) { _sv.Hue = _hue.Value; UpdateDraftFromSliders(); } };
        _alpha.ValueChanged += (_, _) => { if (!_updating) UpdateDraftFromSliders(); };
        _sv.Changed += UpdateDraftFromSliders;
        _hex.TextChanged += (_, _) =>
        {
            if (_updating) return;
            bool valid = AnnotationColors.TryParse(_hex.Text, out var color); _applyButton.IsEnabled = valid;
            if (valid) { SetDraft(color, false); _previewColor?.Invoke(color); SetStatus("单击预览 · 双击色块应用 · Esc 取消"); }
            else SetStatus("HEX 需为 6 或 8 位；8 位格式为 #AARRGGBB。");
        };
        // Editor disables IME tool composition; HEX remains a normal text field.
        InputMethod.SetIsInputMethodEnabled(_hex, true);
    }
    internal static Brush CheckerBrush
    {
        get
        {
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(238, 241, 240)), null, new RectangleGeometry(new Rect(0, 0, 12, 12))));
            var gray = new SolidColorBrush(Color.FromRgb(185, 193, 190));
            group.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(0, 0, 6, 6))));
            group.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(6, 6, 6, 6))));
            var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new(0, 0, 12, 12) };
            brush.Freeze(); return brush;
        }
    }
    private static Button IconButton(string icon, string title, Action action)
    {
        var button = Ui.Button("", action); var image = IconSet.Create(icon); image.Width = image.Height = 16; button.Content = image;
        button.Width = button.Height = 30; button.MinWidth = 0; button.Margin = new(0); button.Padding = new(5);
        button.ToolTip = title; System.Windows.Automation.AutomationProperties.SetName(button, title); return button;
    }
    private static ControlTemplate GradientSliderTemplate()
    {
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Slider'>
 <Grid>
  <Border Height='10' CornerRadius='5'><Border.Background><DrawingBrush TileMode='Tile' Viewport='0,0,12,12' ViewportUnits='Absolute'><DrawingBrush.Drawing><DrawingGroup>
   <GeometryDrawing Brush='#EEF1F0' Geometry='M0,0 L12,0 12,12 0,12Z'/>
   <GeometryDrawing Brush='#B9C1BE' Geometry='M0,0 L6,0 6,6 0,6Z M6,6 L12,6 12,12 6,12Z'/>
  </DrawingGroup></DrawingBrush.Drawing></DrawingBrush></Border.Background></Border>
  <Border Height='10' CornerRadius='5' Background='{TemplateBinding Background}'/>
  <Track Name='PART_Track' Height='18' VerticalAlignment='Center' Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' Value='{TemplateBinding Value}'>
   <Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
   <Track.Thumb><Thumb Width='16' Height='16'><Thumb.Template><ControlTemplate TargetType='Thumb'><Border Background='White' BorderBrush='{DynamicResource Accent}' BorderThickness='2' CornerRadius='8'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
   <Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
  </Track>
 </Grid>
</ControlTemplate>");
    }
    internal Border CreateHost(FrameworkElement anchor)
    {
        var dpi = VisualTreeHelper.GetDpi(anchor);
        var window = Window.GetWindow(anchor);
        var handle = window == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var area = handle != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(handle).WorkingArea : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        Width = Math.Min(264, Math.Max(160, area.Width / dpi.DpiScaleX - 32));
        var scroll = new ScrollViewer { Content = this, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(100, area.Height / dpi.DpiScaleY - 48) };
        var host = UiDesign.Panel(scroll, true); host.Padding = new(12); host.Margin = new(0);
        host.Width = Width + 26; return host;
    }
    private Button Swatch(Color color, string? name = null, bool stretch = false)
    {
        var sample = new Border { Background = new SolidColorBrush(color), Height = 25, CornerRadius = new(4), BorderBrush = Brushes.Gray, BorderThickness = new(.5) };
        var button = Ui.Button("", () => PreviewColor(color)); button.Content = sample;
        var frame = new FrameworkElementFactory(typeof(Border)); frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        frame.AppendChild(content); button.Template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
        button.Height = 33; if (!stretch) button.Width = 33; button.MinWidth = 0; button.Padding = new(2); button.Margin = new(2, 0, 2, 5);
        button.BorderThickness = new(2); button.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        button.ToolTip = (name == null ? "" : name + " ") + AnnotationColors.Hex(color);
        System.Windows.Automation.AutomationProperties.SetName(button, "颜色 " + AnnotationColors.Hex(color));
        button.MouseDoubleClick += (_, e) => { Commit(color); e.Handled = true; };
        _swatches.Add((color, button)); return button;
    }
    private void Commit(Color color) { AnnotationColors.Remember(color); _apply(color); RefreshRecent(); }
    internal void RefreshRecent()
    {
        foreach (var child in _recent.Children.OfType<Button>().ToArray()) _swatches.RemoveAll(s => s.Button == child);
        _recent.Children.Clear();
        foreach (var color in AnnotationColors.Recent) _recent.Children.Add(Swatch(color));
        if (_recent.Children.Count == 0) _recent.Children.Add(UiDesign.Text("尚无最近颜色", true));
    }
    internal void SetStatus(string message) => _status.Text = message;
    internal void PreviewColor(Color color) { SetDraft(color); _previewColor?.Invoke(color); }
    internal void SelectTarget(bool fill) { if (_fixedFill == null) _target.SelectedIndex = fill ? 1 : 0; }
    internal void SetPickerAvailable(bool available, string? reason = null)
    { _canvasPicker.IsEnabled = available; _canvasPicker.ToolTip = available ? (_fixedTarget == null ? "画布吸管 · 单击取色，Esc 取消" : "原片吸管 · 单击取色，Esc 取消") : reason ?? "当前画面不可取色"; }
    internal void SetCurrent(Color color, bool canFill, bool filled)
    {
        _updating = true; if (!canFill && _fixedFill == null) _target.SelectedIndex = 0; _target.IsEnabled = canFill;
        _target.Visibility = _fixedTarget == null && canFill ? Visibility.Visible : Visibility.Collapsed;
        _noFill.Visibility = canFill && EditingFill ? Visibility.Visible : Visibility.Collapsed; _updating = false;
        _title.Text = _fixedTarget ?? (EditingFill ? "形状颜色" : canFill ? "描边颜色" : "标注颜色");
        _draftSample.ToolTip = EditingFill && !filled ? "原状态：无填充" : "原颜色 " + AnnotationColors.Hex(color);
        SetDraft(color); RefreshRecent(); UpdateSwatches();
    }
    internal void SetLabel(string label) => _title.Text = label;
    private void SetDraft(Color color, bool writeHex = true)
    {
        _updating = true; _draft = color; var hsv = AnnotationColors.ToHsv(color); _hue.Value = hsv.Hue; _sv.Hue = hsv.Hue; _sv.Saturation = hsv.Saturation; _sv.Value = hsv.Value; _alpha.Value = color.A * 100d / 255; if (writeHex) _hex.Text = AnnotationColors.Hex(color); _draftSample.Background = new SolidColorBrush(color); _sv.InvalidateVisual(); _applyButton.IsEnabled = true; UpdateSwatches(); _updating = false;
    }
    private void UpdateDraftFromSliders()
    {
        if (_updating) return; _draft = AnnotationColors.Hsv(_hue.Value, _sv.Saturation, _sv.Value, (byte)Math.Round(_alpha.Value * 255 / 100)); _updating = true; _hex.Text = AnnotationColors.Hex(_draft); _updating = false; _draftSample.Background = new SolidColorBrush(_draft); _sv.InvalidateVisual(); _applyButton.IsEnabled = true; UpdateSwatches(); _previewColor?.Invoke(_draft);
    }
    private void UpdateSwatches()
    {
        _alphaValue.Text = $"{_draft.A * 100d / 255:0}%";
        _alpha.Background = new LinearGradientBrush(Color.FromArgb(0, _draft.R, _draft.G, _draft.B), Color.FromRgb(_draft.R, _draft.G, _draft.B), 0);
        foreach (var (color, button) in _swatches)
            button.SetResourceReference(Control.BorderBrushProperty, color == _draft ? "Accent" : "BorderBrush");
    }
}
