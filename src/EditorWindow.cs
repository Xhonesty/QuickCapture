using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;

// Window/full-desktop screenshots can still use a resizable standalone editor.
internal sealed class EditorWindow : Window
{
    internal ScreenshotEditor Editor { get; }
    public EditorWindow(BitmapSource image, Settings settings, Action<string> saved)
    {
        Ui.Theme(this);
        Title = $"轻截 · 标注截图 · {image.PixelWidth} × {image.PixelHeight}";
        Width = Math.Min(1180, SystemParameters.WorkArea.Width - 60); Height = Math.Min(850, SystemParameters.WorkArea.Height - 60);
        MinWidth = 640; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var editor = Editor = new ScreenshotEditor(this, image, settings, saved, Close);
        Closed += (_, _) => editor.Dispose();
        var root = new DockPanel(); Content = root;
        var toolbar = editor.CreateToolbar(); toolbar.Margin = new Thickness(12); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var bottom = new DockPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var hint = new TextBlock { Margin = new Thickness(16, 8, 16, 8), FontSize = UiDesign.Number("FontHelp"), Text = "T 原位文字 · D 步骤编号 · O 提取文字 · E 编辑标注 · K 调色 / 吸管 · Ctrl+Z 撤销", TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var zoom = new Slider { Minimum = 0.25, Maximum = 2, Value = Math.Min(1, Math.Min((Width - 50) / image.PixelWidth, (Height - 160) / image.PixelHeight)), Width = 140, Margin = new Thickness(12), ToolTip = "缩放预览（导出保持原始像素）" };
        DockPanel.SetDock(zoom, Dock.Right); bottom.Children.Add(zoom); bottom.Children.Add(hint);
        var viewport = new Canvas { Width = image.PixelWidth, Height = image.PixelHeight };
        viewport.Children.Add(new Image { Source = image, Width = image.PixelWidth, Height = image.PixelHeight, Stretch = Stretch.Fill });
        var mask = new SelectionMask { Width = image.PixelWidth, Height = image.PixelHeight }; viewport.Children.Add(mask);
        viewport.Children.Add(editor.Surface); viewport.Children.Add(editor.Handles); viewport.Children.Add(editor.TextOverlay);
        void Position()
        {
            var crop = editor.Surface.CropBounds; mask.Selection = new Rect(crop.X, crop.Y, crop.Width, crop.Height);
            Canvas.SetLeft(editor.Surface, crop.X); Canvas.SetTop(editor.Surface, crop.Y);
            Canvas.SetLeft(editor.Handles, crop.X); Canvas.SetTop(editor.Handles, crop.Y);
            Canvas.SetLeft(editor.TextOverlay, crop.X); Canvas.SetTop(editor.TextOverlay, crop.Y);
        }
        editor.Surface.CropChanged += Position; Position();
        var scale = new ScaleTransform(zoom.Value, zoom.Value); viewport.LayoutTransform = scale;
        zoom.ValueChanged += (_, _) => { scale.ScaleX = zoom.Value; scale.ScaleY = zoom.Value; };
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = viewport, Padding = new Thickness(12) };
        scroll.SetResourceReference(Control.BackgroundProperty, "EditorBackground"); root.Children.Add(scroll);
        PreviewKeyDown += (_, e) => { if (!e.Handled && !editor.EditingText && e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }
}
