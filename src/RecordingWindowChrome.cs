using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace QuickCapture;

internal static class RecordingWindowChrome
{
    internal static void Install(Window window, DockPanel shell)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 32, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = false
        });
        var caption = new DockPanel { Height = 32, LastChildFill = true, Name = "RecordingCaption" };
        caption.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
        DockPanel.SetDock(caption, Dock.Top); shell.Children.Add(caption);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right); caption.Children.Add(actions);
        Button Action(string name, string tooltip, string geometry, Action clicked, string style = "RecordingCaptionButton")
        {
            var button = Ui.Button("", clicked); button.Name = name; button.ToolTip = tooltip;
            button.Style = (Style)window.FindResource(style);
            var glyph = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 12, Height = 12, Stretch = Stretch.None, StrokeThickness = 1 };
            glyph.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
            button.Content = glyph; WindowChrome.SetIsHitTestVisibleInChrome(button, true); actions.Children.Add(button); return button;
        }
        Action("RecordingMinimize", "最小化", "M1,7 L11,7", () => SystemCommands.MinimizeWindow(window));
        var maximize = Action("RecordingMaximize", "最大化", "M1,1 L11,1 L11,11 L1,11 Z", () =>
        { if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window); else SystemCommands.MaximizeWindow(window); });
        window.StateChanged += (_, _) =>
        {
            bool maximized = window.WindowState == WindowState.Maximized;
            maximize.ToolTip = maximized ? "还原" : "最大化";
            ((System.Windows.Shapes.Path)maximize.Content).Data = Geometry.Parse(maximized ? "M1,3 L9,3 L9,11 L1,11 Z M3,3 L3,1 L11,1 L11,9 L9,9" : "M1,1 L11,1 L11,11 L1,11 Z");
        };
        Action("RecordingClose", "关闭", "M1,1 L11,11 M11,1 L1,11", () => SystemCommands.CloseWindow(window), "RecordingCloseButton");
        caption.Children.Add(new Border()); // Native caption hit testing supplies drag, double-click and system menu.
    }
}
