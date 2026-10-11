using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace QuickCapture;

internal static class ThicknessOptions
{
    private sealed class ThicknessCombo : ComboBox
    {
        internal Action<double>? Changed;
        internal double Minimum = 1, Maximum = 200;
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == TextProperty && double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) && n >= Minimum && n <= Maximum) Changed?.Invoke(n);
        }
    }
    internal static readonly int[] Presets = { 1, 4, 8, 16, 24, 32, 48, 64, 72 };
    internal static ComboBox Create(double value, string label, Action<double> changed)
        => CreateValue(value, label + "（1–200 原始像素，可输入自定义值）", Presets.Select(n => n.ToString(CultureInfo.InvariantCulture)).ToArray(), 1, 200, changed);
    internal static ComboBox CreateValue(double value, string label, string[] presets, double minimum, double maximum, Action<double> changed)
    {
        var box = new ThicknessCombo { IsEditable = true, IsTextSearchEnabled = false, ItemsSource = presets, Text = value.ToString("0.###", CultureInfo.InvariantCulture), Width = 76, MinWidth = 0, MinHeight = 30, Margin = new Thickness(2, 2, 6, 2), ToolTip = label, Minimum = minimum, Maximum = maximum, Changed = changed, Style = (Style)Application.Current.FindResource("EditableValueCombo") };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        InputMethod.SetIsInputMethodEnabled(box, false);
        return box;
    }
    internal static StackPanel Group(string label, UIElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 5, 0) }); row.Children.Add(control); return row;
    }
}
