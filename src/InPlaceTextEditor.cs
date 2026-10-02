using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;

namespace QuickCapture;

// Input chrome stays outside AnnotationSurface.Export. The surface draws the
// draft with exactly the same text formatter as the confirmed annotation.
internal sealed class InPlaceTextEditor
{
    private readonly AnnotationSurface _surface;
    private readonly Border _border;
    private readonly TextBlock _placeholder;
    private Annotation? _before, _draft;
    private Point _anchor;
    private bool _updating;
    internal Canvas Overlay { get; } = new() { ClipToBounds = false };
    internal TextBox Input { get; }
    internal bool Active => _draft != null;
    internal bool Composing { get; private set; }
    internal Annotation? Draft => _draft;
    internal event Action? Changed;

    internal InPlaceTextEditor(AnnotationSurface surface)
    {
        _surface = surface;
        Input = new TextBox
        {
            Background = Brushes.Transparent, Foreground = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            SelectionOpacity = 0.28, IsUndoEnabled = true,
            Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        // Remove theme-dependent input padding and backgrounds.
        var host = new FrameworkElementFactory(typeof(ScrollViewer));
        host.Name = "PART_ContentHost"; host.SetValue(Control.PaddingProperty, new Thickness(0));
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        Input.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = host };
        InputMethod.SetIsInputMethodEnabled(Input, true);
        AutomationProperties.SetName(Input, "画布文字编辑");
        _placeholder = new TextBlock { Text = "输入文字…", IsHitTestVisible = false, Opacity = 0.75 };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "SelectionBrush");
        var contents = new Grid(); contents.Children.Add(_placeholder); contents.Children.Add(Input);
        _border = new Border { Child = contents, Background = Brushes.Transparent, BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
        _border.SetResourceReference(Border.BorderBrushProperty, "SelectionBrush");
        Overlay.Children.Add(_border); SyncBounds();
        surface.CropChanged += SyncBounds;
        Input.TextChanged += (_, _) => { if (!_updating && _draft != null) Update(_draft with { Text = Input.Text }); };
        Input.AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler((_, _) => Composing = true), true);
        Input.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler((_, _) => Composing = true), true);
        Input.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler((_, _) => Composing = false), true);
    }

    private void SyncBounds() { Overlay.Width = _surface.Width; Overlay.Height = _surface.Height; }
    internal void Begin(Point viewportPoint, Annotation defaults, Annotation? existing)
    {
        Cancel(); _before = existing;
        var original = _surface.ToOriginal(viewportPoint);
        _draft = existing ?? defaults with { Start = original, End = original, Text = "" };
        _anchor = _draft.Start;
        _updating = true; Input.Text = _draft.Text; _updating = false;
        _surface.EditingTextOriginal = existing;
        Update(_draft); _border.Visibility = Visibility.Visible;
        Focus(); Input.CaretIndex = Input.Text.Length; Changed?.Invoke();
    }

    internal void UpdateStyle(string family, double size, bool bold, TextAlignment alignment, Color color)
    {
        if (_draft != null) Update(_draft with { FontFamily = family, FontSize = size, Bold = bold, Alignment = alignment, Color = color });
    }

    private void Update(Annotation draft)
    {
        double size = draft.FontSize;
        var crop = _surface.CropBounds;
        // Keep at least one glyph and a caret reachable even at the last pixel.
        double x = _before != null ? _anchor.X - crop.X : Math.Clamp(_anchor.X - crop.X, 0, Math.Max(0, crop.Width - Math.Min(crop.Width, size + 4)));
        double y = _before != null ? _anchor.Y - crop.Y : Math.Clamp(_anchor.Y - crop.Y, 0, Math.Max(0, crop.Height - Math.Min(crop.Height, size * 1.5)));
        var natural = AnnotationGeometry.Text(draft with { Alignment = TextAlignment.Left, TextWidth = 0 }, Brushes.Black);
        double width = _before != null
            ? (_before.TextWidth > 0 ? _before.TextWidth : Math.Max(160, Math.Ceiling(natural.WidthIncludingTrailingWhitespace) + 4))
            : Math.Min(crop.Width, Math.Max(160, Math.Ceiling(natural.WidthIncludingTrailingWhitespace) + 4));
        // Existing paragraph width/anchor survive subsequent crops unchanged.
        if (_before == null) x = Math.Min(x, crop.Width - width);
        Input.FontFamily = new FontFamily(draft.FontFamily); Input.FontSize = size;
        Input.FontWeight = draft.Bold ? FontWeights.Bold : FontWeights.Normal;
        Input.TextAlignment = draft.Alignment; Input.CaretBrush = new SolidColorBrush(draft.Color);
        // Avoid a scrollbar feeding back into the paragraph width. A line that
        // fits its measured paragraph must not rewrap in the native input view.
        Input.TextWrapping = natural.WidthIncludingTrailingWhitespace <= width ? TextWrapping.NoWrap : TextWrapping.Wrap;
        Input.Width = width; Input.Height = double.NaN;
        Input.Measure(new Size(width, double.PositiveInfinity));
        draft = draft with { Start = new(crop.X + x, crop.Y + y), End = new(crop.X + x, crop.Y + y), TextWidth = width };
        var formatted = AnnotationGeometry.Text(draft, Brushes.Black);
        double height = Math.Max(Math.Max(size * 1.5, Math.Ceiling(formatted.Height) + 2), Input.DesiredSize.Height + 2);
        if (_before == null) y = Math.Max(0, Math.Min(y, crop.Height - Math.Min(crop.Height, height)));
        draft = draft with { Start = new(crop.X + x, crop.Y + y), End = new(crop.X + x, crop.Y + y) };
        _draft = draft; _surface.TextDraft = draft;
        // Text remains visible on the surface while the transparent input owns IME,
        // selection and caret. Matching width preserves wrapping and alignment.
        Input.Height = Math.Min(height, Math.Max(1, crop.Height - y));
        _placeholder.FontFamily = Input.FontFamily; _placeholder.FontSize = size;
        _placeholder.Visibility = string.IsNullOrEmpty(Input.Text) && !Composing ? Visibility.Visible : Visibility.Collapsed;
        var scale = _surface.DisplayScale;
        double edgeX = 1 / Math.Max(0.05, scale.X), edgeY = 1 / Math.Max(0.05, scale.Y);
        _border.BorderThickness = new Thickness(edgeX, edgeY, edgeX, edgeY);
        Canvas.SetLeft(_border, x - edgeX); Canvas.SetTop(_border, y - edgeY);
        _surface.InvalidateVisual(); Changed?.Invoke();
    }

    internal bool Confirm()
    {
        if (!Active || Composing) return false;
        var draft = _draft!; var before = _before;
        Clear();
        if (string.IsNullOrWhiteSpace(draft.Text)) return true;
        if (before != null && before.Text == draft.Text && before.Color == draft.Color && before.FontFamily == draft.FontFamily
            && before.FontSize == draft.FontSize && before.Bold == draft.Bold && before.Alignment == draft.Alignment) return true;
        if (before != null) _surface.SetSelected(draft);
        else _surface.Add(draft with { Start = draft.Start - new Vector(_surface.CropBounds.X, _surface.CropBounds.Y), End = draft.End - new Vector(_surface.CropBounds.X, _surface.CropBounds.Y) });
        return true;
    }
    internal void Cancel() { if (!Active) return; Clear(); }
    private void Clear()
    {
        _draft = _before = null; Composing = false; _border.Visibility = Visibility.Collapsed;
        _surface.TextDraft = _surface.EditingTextOriginal = null; _surface.InvalidateVisual(); Changed?.Invoke();
    }
    internal void Focus() { if (Active) { Input.Focus(); Keyboard.Focus(Input); } }
}
