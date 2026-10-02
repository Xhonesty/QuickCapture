using System;
using System.Windows;
using System.Windows.Controls;

namespace QuickCapture;

internal static class Ui
{
    public static void Theme(Window window) => window.Style = (Style)Application.Current.FindResource(typeof(Window));
    public static Button Button(string label, Action clicked)
    {
        var b = new Button { Content = label }; b.Click += (_, _) => clicked(); return b;
    }
    public static void Error(Window owner, Exception ex) { ErrorLog.Write(ex); MessageBox.Show(owner, ex.Message, "轻截 · 操作失败", MessageBoxButton.OK, MessageBoxImage.Error); }
}

internal sealed class PromptWindow : Window
{
    private readonly TextBox _input;
    private PromptWindow(Window owner, string title, string hint, string value)
    {
        Ui.Theme(this);
        Owner = owner; Title = title; Width = 440; Height = 260; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = UiDesign.Padding("PagePadding") }; Content = root;
        root.Children.Add(new TextBlock { Text = hint, Margin = new Thickness(0, 0, 0, 12) });
        _input = new TextBox { Text = value, Height = 100, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(12) }; root.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Ui.Button("取消", () => DialogResult = false)); buttons.Children.Add(Ui.Button("确定", () => DialogResult = true)); root.Children.Add(buttons);
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }
    public static string? Ask(Window owner, string title, string hint, string value)
    { var window = new PromptWindow(owner, title, hint, value); return window.ShowDialog() == true ? window._input.Text : null; }
}
