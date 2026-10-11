using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture;

internal sealed class VideoTextInput : Canvas
{
    private sealed class DraftView : FrameworkElement
    {
        internal VideoObject? Item;
        protected override void OnRender(DrawingContext dc)
        {
            if (Item is not { } o) return;
            var a = o.TextAnnotation();
            a = AnnotationGeometry.WithPlaceholder(a);
            var body = a.Color; body.A = (byte)Math.Round(body.A * o.StrokeAlpha); var outline = a.OutlineColor ?? Colors.Black; outline.A = (byte)Math.Round(outline.A * o.OutlineAlpha);
            AnnotationGeometry.DrawText(dc, a with { Color = body, OutlineColor = outline });
        }
    }
    internal TextBox Input { get; } = InPlaceTextEditor.CreateInput();
    private readonly Border _border;
    private readonly Grid _content = new();
    private readonly DraftView _view = new() { IsHitTestVisible = false };
    private VideoInfo _info = new(0, 0, 0, 30, false);
    private Rect _sourceView;
    private bool _updating;
    internal VideoObject? Draft { get; private set; }
    internal bool Active => Draft != null;
    internal bool Composing { get; private set; }
    internal event Action<VideoObject>? Confirmed;
    internal event Action? Updated, Cancelled;
    internal VideoTextInput()
    {
        Name = "VideoTextInput"; ClipToBounds = true;
        _content.Children.Add(_view); _content.Children.Add(Input);
        _border = new Border { Child = _content, Background = Brushes.Transparent, Visibility = Visibility.Collapsed };
        _border.SetResourceReference(Border.BorderBrushProperty, "Accent"); Children.Add(_border);
        Input.Name = "VideoTextBox"; Input.MaxLength = 10000; AutomationProperties.SetName(Input, "视频画布文字编辑");
        Input.TextChanged += (_, _) => { if (!_updating && Draft != null) Update(Draft with { Text = Input.Text }, false); };
        Input.AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler((_, _) => Composing = true), true);
        Input.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler((_, _) => Composing = true), true);
        Input.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler((_, _) => Composing = false), true);
        Input.PreviewKeyDown += (_, e) => { if (Composing) return; if (e.Key == Key.Escape) { Cancel(); e.Handled = true; } else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Confirm(); e.Handled = true; } };
        SizeChanged += (_, _) => Sync();
    }
    internal void Begin(VideoObject item, VideoInfo info, Rect sourceView)
    { _info = info; _sourceView = sourceView; Composing = false; Update(item); _border.Visibility = Visibility.Visible; FocusInput(); Input.CaretIndex = Input.Text.Length; }
    internal void SetView(Rect view) { _sourceView = view; Sync(); }
    internal void Update(VideoObject item, bool syncText = true)
    {
        Draft = item.FitText(_info); _view.Item = Draft;
        _updating = true;
        try { if (syncText && Input.Text != item.Text) Input.Text = item.Text; Input.FontFamily = new FontFamily(item.FontFamily); Input.FontSize = item.FontSize; Input.FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal; Input.FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal; Input.TextAlignment = item.Alignment; Input.CaretBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Color)); }
        finally { _updating = false; }
        Sync(); _view.InvalidateVisual(); Updated?.Invoke();
    }
    private void Sync()
    {
        if (Draft is not { } o || _sourceView.Width <= 0 || ActualWidth <= 0) return;
        double scale = ActualWidth / _sourceView.Width;
        double inset = AnnotationGeometry.TextInset(o.TextAnnotation());
        Input.Height = double.NaN; Input.Width = Math.Max(1, o.Width - 2 * inset);
        Input.Margin = new Thickness(inset, inset, 0, 0);
        // DesiredSize includes Margin. Measure the full outer width, then use
        // only the native text viewport height; padding belongs to the object.
        Input.Measure(new Size(Input.Width + inset, double.PositiveInfinity));
        double height = Math.Max(o.Height, Input.DesiredSize.Height - inset + 2 * inset + 2);
        if (height > o.Height + .01 && height <= _info.Height) { Draft = o = (o with { Height = height }).FitText(_info); _view.Item = o; }
        _content.Width = _view.Width = o.Width; _content.Height = _view.Height = o.Height;
        Input.Width = Math.Max(1, o.Width - 2 * inset); Input.Height = Math.Max(1, o.Height - 2 * inset); Input.Margin = new Thickness(inset, inset, 0, 0); Input.HorizontalAlignment = HorizontalAlignment.Left; Input.VerticalAlignment = VerticalAlignment.Top;
        _content.LayoutTransform = new ScaleTransform(scale, scale); _border.BorderThickness = new Thickness(1);
        SetLeft(_border, (o.X - _sourceView.X) * scale - 1); SetTop(_border, (o.Y - _sourceView.Y) * scale - 1);
    }
    internal bool Confirm()
    {
        if (Draft == null) return true; if (Composing) return false;
        var item = Draft; Clear(); Confirmed?.Invoke(item); return true;
    }
    internal void Cancel() { if (!Active) return; Clear(); Cancelled?.Invoke(); }
    private void Clear() { Draft = null; _view.Item = null; Composing = false; _border.Visibility = Visibility.Collapsed; }
    internal void FocusInput() { if (Active) { Input.Focus(); Keyboard.Focus(Input); } }
}
