using System;
using System.Windows;
using System.Windows.Media;

namespace QuickCapture;

internal static class ThemeService
{
    public static string Current { get; private set; } = "Dark";
    public static event Action<string>? Changed;
    public static void Apply(string theme)
    {
        Current = theme == "Light" ? "Light" : "Dark";
        bool light = Current == "Light";
        var colors = new (string Key, string Dark, string Light)[]
        {
            ("WindowBackground", "#141B19", "#F4F7F6"),
            ("TextPrimary", "#E8F0ED", "#1F2A28"),
            ("TextSecondary", "#AEBBB7", "#5C6B67"),
            ("TextMetadata", "#8FA39D", "#6B7A77"),
            // The supplied light metadata token falls below AA on both surfaces.
            ("TextHint", "#8FA39D", "#5C6B67"),
            ("PanelBackground", "#1E2724", "#FFFFFF"),
            ("ButtonBackground", "#242F2C", "#F1F5F4"),
            ("HoverBackground", "#242F2C", "#F1F5F4"),
            ("InputBackground", "#1E2724", "#FFFFFF"),
            ("BorderBrush", "#2C3835", "#E3E8E6"),
            ("Accent", "#5DCAA5", "#0F6E56"),
            ("AccentHover", "#9FE1CB", "#1D9E75"),
            ("AccentActive", "#9FE1CB", "#085041"),
            ("AccentForeground", "#04342C", "#FFFFFF"),
            ("AccentHoverForeground", "#04342C", "#141B19"),
            ("BadgeBackground", "#1C4A3C", "#E1F5EE"),
            ("BadgeForeground", "#9FE1CB", "#0F6E56"),
            ("DisabledForeground", "#5C6B67", "#8A9694"),
            ("Danger", "#E24B4A", "#A32D2D"),
            ("DangerText", "#AEBBB7", "#A32D2D"),
            ("ReadyBrush", "#5DCAA5", "#0F6E56"),
            ("ListBackground", "#1E2724", "#FFFFFF"),
            ("EditorBackground", "#141B19", "#F4F7F6"),
            ("SelectionBrush", "#5DCAA5", "#0F6E56"),
            ("ToolbarBackground", "#222A28", "#FFFFFF"),
            ("ToolbarSelectedBackground", "#203F5C", "#EAF3FF"),
            ("ToolbarSelectedForeground", "#80BBFF", "#267CF5"),
            ("ToolbarPressedBackground", "#33516A", "#D5E7FD"),
            ("RecordingButtonBackground", "#303F39", "#DFE8E3"),
            ("RecordingButtonBorder", "#60736A", "#A7BAB0"),
            ("RecordingButtonHover", "#40564B", "#C8D9CF"),
            ("RecordingButtonPressed", "#203E31", "#AFC8BA"),
            ("RecordingButtonSelected", "#244F3D", "#C2E3D3"),
            ("RecordingDisabledBackground", "#26312C", "#E5EAE7"),
            ("RecordingDisabledForeground", "#9AAEA3", "#697A70"),
            ("RecordingInputBorder", "#60736A", "#A7BAB0"),
            ("TimelineTrack", "#36463F", "#D4DFD8"),
            ("ScrollThumb", "#657D71", "#9AAFA2"),
            ("DangerForeground", "#FFFFFF", "#FFFFFF"),
            ("PreviewCropAccent", "#5DCAA5", "#5DCAA5"),
        };
        foreach (var color in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? color.Light : color.Dark));
            brush.Freeze(); Application.Current.Resources[color.Key] = brush;
        }
        Changed?.Invoke(Current);
    }
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
