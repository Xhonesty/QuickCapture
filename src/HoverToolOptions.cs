using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickCapture;

// Keep the flyout open while crossing the small gap or using a nested dropdown.
internal sealed class HoverToolOptions : IDisposable
{
    private readonly Button _button;
    private readonly Border _panel;
    private readonly Action _opening;
    private readonly ComboBox[] _dropdowns;
    private readonly DispatcherTimer _close = new() { Interval = TimeSpan.FromMilliseconds(180) };
    internal Popup Popup { get; }
    public HoverToolOptions(Button button, StackPanel content, Action opening)
    {
        _button = button; _opening = opening;
        _dropdowns = content.Children.OfType<ComboBox>().ToArray();
        _panel = new Border { Child = content, Padding = UiDesign.Padding("PopupPadding"), CornerRadius = UiDesign.Radius, BorderThickness = new Thickness(1), Effect = UiDesign.Shadow() };
        _panel.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        _panel.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _panel.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
        TextElement.SetFontFamily(_panel, new FontFamily("Segoe UI, Microsoft YaHei UI"));
        Popup = new Popup { Child = _panel, PlacementTarget = button, Placement = PlacementMode.Bottom,
            VerticalOffset = 2, AllowsTransparency = true, StaysOpen = true, Focusable = false };
        _button.MouseEnter += Enter; _button.MouseLeave += Leave;
        _panel.MouseEnter += Enter; _panel.MouseLeave += Leave;
        foreach (var combo in _dropdowns) combo.DropDownClosed += DropdownClosed;
        _close.Tick += (_, _) =>
        {
            _close.Stop();
            if (!_button.IsMouseOver && !_panel.IsMouseOver && !_dropdowns.Any(combo => combo.IsDropDownOpen)) Hide();
        };
    }
    private void Enter(object sender, MouseEventArgs e) => Show();
    private void Leave(object sender, MouseEventArgs e) { _close.Stop(); _close.Start(); }
    private void DropdownClosed(object? sender, EventArgs e) { _close.Stop(); _close.Start(); }
    internal void Show() { _close.Stop(); _opening(); Popup.IsOpen = true; }
    internal void Hide()
    {
        foreach (var combo in _dropdowns) combo.IsDropDownOpen = false;
        _close.Stop(); Popup.IsOpen = false;
    }
    public void Dispose()
    {
        Hide(); _button.MouseEnter -= Enter; _button.MouseLeave -= Leave;
        _panel.MouseEnter -= Enter; _panel.MouseLeave -= Leave;
        foreach (var combo in _dropdowns) combo.DropDownClosed -= DropdownClosed;
    }
}
