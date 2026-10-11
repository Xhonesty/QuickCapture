using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using WpfPath = System.Windows.Shapes.Path;

namespace QuickCapture;

// Preview-only SVGs remain outside the production resource glob and ToolCatalog.
internal static class MosaicCandidatePreview
{
    internal static void Render(bool refined = false)
    {
        string root = Directory.GetParent(UiChangeTests.PreviewDirectory)!.Parent!.FullName;
        var page = new StackPanel { Width = 1040, Background = new SolidColorBrush(Color.FromRgb(244, 247, 246)) };
        page.Children.Add(Text(refined ? "马赛克图标 · 方案 1 优化预览" : "马赛克图标 · 当前与 3 个候选", 25, "#1F2A28", new Thickness(24, 20, 24, 4)));
        page.Children.Add(Text("放大图 72 DIP · 实际图标 18 DIP / 含下拉箭头按钮 45×32 DIP · 纯矢量", 13, "#5C6B67", new Thickness(24, 0, 24, 18)));
        string previous = ThemeService.Current;
        foreach (string theme in new[] { "Light", "Dark" })
        {
            ThemeService.Apply(theme);
            var section = new StackPanel { Background = ThemeService.Brush("WindowBackground"), Margin = new Thickness(12, 0, 12, 12) };
            section.Children.Add(Text(theme == "Light" ? "浅色背景" : "深色背景", 16, ThemeService.Brush("TextPrimary"), new Thickness(14, 12, 0, 10)));
            var columns = new Grid { Margin = new Thickness(8, 0, 8, 12) }; section.Children.Add(columns);
            string[] titles = refined ? new[] { "当前 · 四格窗", "方案 1 · 原稿", "优化 1 · 九格像素", "参照 · 方案 2" } : new[] { "当前 · 四格窗", "方案 1 · 密集像素", "方案 2 · 四块遮挡", "方案 3 · 局部像素化" };
            for (int i = 0; i < 4; i++)
            {
                columns.ColumnDefinitions.Add(new ColumnDefinition());
                string file = i == 0 ? Path.Combine(root, "src", "Assets", "Icons", "grid-2x2.svg") : Path.Combine(root, "src", "Assets", "Icons", "MosaicCandidates", $"mosaic-0{i}.svg");
                if (refined && i >= 2) file = Path.Combine(root, "src", "Assets", "Icons", "MosaicCandidates", i == 2 ? "mosaic-01-refined.svg" : "mosaic-02.svg");
                var content = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
                var card = new Border { Child = content, Background = ThemeService.Brush("ToolbarBackground"), BorderBrush = ThemeService.Brush("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Margin = new Thickness(5, 0, 5, 0) }; Grid.SetColumn(card, i); columns.Children.Add(card);
                content.Children.Add(Text(titles[i], 13, ThemeService.Brush("TextPrimary"), new Thickness(0, 0, 0, 8)));
                var large = new Button { Content = Svg(file, 72), Width = 90, Height = 90, Foreground = ThemeService.Brush("TextPrimary"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(9), Margin = new Thickness(0, 0, 0, 8) };
                content.Children.Add(large);
                var states = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                foreach (string state in new[] { "普通", "选中", "禁用" })
                {
                    var stateColumn = new StackPanel { Margin = new Thickness(9, 0, 9, 0) };
                    var button = PreviewButton(file); button.Foreground = ThemeService.Brush(state == "选中" ? "ToolbarSelectedForeground" : state == "禁用" ? "DisabledForeground" : "TextPrimary"); button.Tag = state == "选中" ? "selected" : null; button.IsEnabled = state != "禁用";
                    if (state == "选中") button.Background = ThemeService.Brush("ToolbarSelectedBackground");
                    stateColumn.Children.Add(button); stateColumn.Children.Add(Text(state, 11, ThemeService.Brush("TextHint"), new Thickness(0, 4, 0, 0))); states.Children.Add(stateColumn);
                }
                content.Children.Add(states);
                content.Children.Add(Text("工具栏内实际尺寸", 11, ThemeService.Brush("TextHint"), new Thickness(0, 12, 0, 4)));
                var strip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                foreach (string icon in new[] { "square", "mosaic", "pencil" })
                {
                    var button = PreviewButton(icon == "mosaic" ? file : Path.Combine(root, "src", "Assets", "Icons", icon + ".svg")); button.Foreground = ThemeService.Brush("TextPrimary"); strip.Children.Add(button);
                }
                content.Children.Add(strip);
            }
            // Freeze each theme before the next ThemeService.Apply changes dynamic
            // button resources, retaining exactly the real toolbar state colors.
            section.Measure(new Size(1016, double.PositiveInfinity)); section.Arrange(new Rect(0, 0, 1016, section.DesiredSize.Height)); section.UpdateLayout();
            var snapshot = new RenderTargetBitmap(2032, (int)Math.Ceiling(section.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32); snapshot.Render(section); snapshot.Freeze();
            page.Children.Add(new Image { Source = snapshot, Width = 1016, Height = section.ActualHeight, Margin = new Thickness(12, 0, 12, 12) });
        }
        page.Children.Add(Text(refined ? "优化 1：去除密集空心描边，九个等大方块留出一致间隙；交错深浅形成像素遮挡。" : "推荐方案 2：块面更清楚，18 DIP 下留白充足；方案 1 更密集，方案 3 强调局部遮挡。", 13, "#1F2A28", new Thickness(24, 0, 24, 18)));
        page.Measure(new Size(1040, double.PositiveInfinity)); page.Arrange(new Rect(0, 0, 1040, page.DesiredSize.Height)); page.UpdateLayout();
        UiChangeTests.RenderElement(page, refined ? "mosaic-01-refined-comparison.png" : "mosaic-candidates-comparison.png"); ThemeService.Apply(previous);
    }
    private static TextBlock Text(string value, double size, string color, Thickness margin) => Text(value, size, new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), margin);
    private static Button PreviewButton(string file)
    {
        var button = ScreenshotToolbar.IconButton("grid-2x2", "预览", () => { }, dropdown: true);
        var icons = (StackPanel)button.Content; icons.Children.RemoveAt(0); icons.Children.Insert(0, Svg(file, 18)); return button;
    }
    private static TextBlock Text(string value, double size, Brush color, Thickness margin) => new() { Text = value, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = size, Foreground = color, Margin = margin, HorizontalAlignment = HorizontalAlignment.Left };
    private static FrameworkElement Svg(string file, double size)
    {
        var svg = XDocument.Load(file).Root!; var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var node in svg.Elements())
        {
            double N(string key) => double.Parse((string?)node.Attribute(key) ?? "0", CultureInfo.InvariantCulture);
            Geometry? geometry = node.Name.LocalName switch
            {
                "path" => Geometry.Parse((string)node.Attribute("d")!),
                "rect" => new RectangleGeometry(new Rect(N("x"), N("y"), N("width"), N("height")), N("rx"), N("rx")),
                _ => null
            };
            if (geometry == null) continue;
            var path = new WpfPath { Data = geometry, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            if (node.Attribute("opacity") != null) path.Opacity = N("opacity");
            Binding ColorBinding() => new("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) };
            if (((string?)node.Attribute("stroke") ?? (string?)svg.Attribute("stroke")) != "none") path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, ColorBinding());
            if (((string?)node.Attribute("fill") ?? (string?)svg.Attribute("fill")) == "currentColor") path.SetBinding(System.Windows.Shapes.Shape.FillProperty, ColorBinding());
            canvas.Children.Add(path);
        }
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }
}
