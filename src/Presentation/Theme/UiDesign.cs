using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;

namespace QuickCapture;

internal static class UiDesign
{
    internal static double Number(string name) => (double)Application.Current.FindResource(name);
    internal static CornerRadius Radius => (CornerRadius)Application.Current.FindResource("RadiusControl");
    internal static Thickness Padding(string name) => (Thickness)Application.Current.FindResource(name);
    internal static DropShadowEffect Shadow() => new() { BlurRadius = Number("ShadowBlur"), ShadowDepth = Number("ShadowDepth"), Opacity = Number("ShadowOpacity") };
    internal static Border Panel(UIElement content, bool floating = false)
    {
        var panel = new Border { Child = content, CornerRadius = Radius, Padding = Padding(floating ? "PopupPadding" : "PanelPadding"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, Number("SpaceL")) };
        panel.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        panel.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        if (floating) panel.Effect = Shadow();
        return panel;
    }
    internal static TextBlock Text(string text, bool help = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = Number(help ? "FontHelp" : "FontBody"), Margin = new Thickness(0, 0, 0, Number("SpaceS")) };
        block.SetResourceReference(TextBlock.ForegroundProperty, help ? "TextSecondary" : "TextPrimary"); return block;
    }
    internal static StackPanel Section(Panel parent, string name)
    {
        var content = new StackPanel(); var title = Text(name); title.FontSize = Number("FontSection"); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 0, Number("SpaceM")); content.Children.Add(title);
        parent.Children.Add(Panel(content)); return content;
    }
    internal static TextBox Field(Panel parent, string name, string value)
    {
        parent.Children.Add(Text(name)); var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, Number("SpaceM")) }; parent.Children.Add(box); return box;
    }
    internal static ComboBox Choice(Panel parent, string name, object items, int selected)
    {
        parent.Children.Add(Text(name)); var choice = new ComboBox { ItemsSource = (System.Collections.IEnumerable)items, SelectedIndex = selected, Margin = new Thickness(0, 0, 0, Number("SpaceM")) }; parent.Children.Add(choice); return choice;
    }
}
