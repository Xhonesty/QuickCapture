using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace QuickCapture;

// Both screenshot hosts measure this same DIP-based, two-panel control.
internal sealed class ScreenshotToolbar : Grid
{
    internal WrapPanel MainTools { get; } = new();
    internal Grid Properties { get; } = new();
    internal Border MainPanel { get; }
    internal Border PropertyPanel { get; }
    private readonly Dictionary<AnnotationTool, WrapPanel> _rows = new();

    internal ScreenshotToolbar()
    {
        Width = 800; HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top; UseLayoutRounding = true;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        MainPanel = Panel(MainTools); PropertyPanel = Panel(Properties);
        MainPanel.HorizontalAlignment = PropertyPanel.HorizontalAlignment = HorizontalAlignment.Left;
        PropertyPanel.Margin = new Thickness(0, 8, 0, 0);
        SetRow(PropertyPanel, 1); Children.Add(MainPanel); Children.Add(PropertyPanel);
    }
    private static Border Panel(UIElement child)
    {
        var border = new Border { Child = child, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1), Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = .16, Color = Colors.Black } };
        border.SetResourceReference(Border.BackgroundProperty, "ToolbarBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }
    internal void SetAvailableWidth(double width) => Width = Math.Max(100, Math.Min(800, width));
    internal void AddProperties(AnnotationTool tool, WrapPanel row)
    {
        // Hidden rows still measure: changing tools/values does not move the panels.
        row.Visibility = Visibility.Hidden; row.MinHeight = 32;
        _rows.Add(tool, row); Properties.Children.Add(row);
    }
    internal WrapPanel OptionsFor(AnnotationTool tool) => _rows[tool];
    internal void ShowProperties(AnnotationTool tool)
    {
        foreach (var (key, row) in _rows) row.Visibility = key == tool ? Visibility.Visible : Visibility.Hidden;
    }
    internal static void Selected(Button button, bool selected) => button.Tag = selected ? "selected" : null;
    internal static Button IconButton(string icon, string tooltip, Action action, bool dropdown = false)
    {
        var button = Ui.Button("", action); button.Style = (Style)Application.Current.FindResource("ScreenshotToolButton");
        var icons = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        icons.Children.Add(IconSet.Create(icon));
        if (dropdown)
        {
            var arrow = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L3,3 L6,0"), StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            arrow.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
            icons.Children.Add(arrow); button.Width = 45;
        }
        button.Content = icons; button.ToolTip = tooltip;
        return button;
    }
    internal static Border Separator()
    {
        var line = new Border { Width = 1, Height = 22, Margin = new Thickness(7, 5, 7, 5) };
        line.SetResourceReference(Border.BackgroundProperty, "BorderBrush"); return line;
    }
}
