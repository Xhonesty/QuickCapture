using System;
using System.Windows;
using System.Windows.Media;

namespace QuickCapture;

internal static class ThemeService
{
    public static string Current { get; private set; } = "Dark";
    public static void Apply(string theme)
    {
        Current = theme == "Light" ? "Light" : "Dark";
        bool light = Current == "Light";
        var colors = new (string Key, string Dark, string Light)[]
        {
            ("WindowBackground", "#12171E", "#F3F6FA"),
            ("TextPrimary", "#E7EDF4", "#19283A"),
            ("TextSecondary", "#9CADBF", "#576B81"),
            ("PanelBackground", "#1B2430", "#FFFFFF"),
            ("ButtonBackground", "#26313D", "#E6EDF4"),
            ("InputBackground", "#202935", "#FFFFFF"),
            ("BorderBrush", "#384859", "#CBD7E3"),
            ("Accent", "#82E2BB", "#176E57"),
            ("AccentForeground", "#11251C", "#FFFFFF"),
            ("BadgeBackground", "#20372E", "#DEEEE7"),
            ("ListBackground", "#171F29", "#FFFFFF"),
            ("EditorBackground", "#090D12", "#E4EAF1"),
            ("SelectionBrush", "#82E2BB", "#46E5B2"),
        };
        foreach (var color in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? color.Light : color.Dark));
            brush.Freeze(); Application.Current.Resources[color.Key] = brush;
        }
    }
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
