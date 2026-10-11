using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture;

internal static class CropGeometry
{
    internal static Int32Rect Move(Int32Rect rect, int dx, int dy, int width, int height)
        => new(Math.Clamp(rect.X + dx, 0, width - rect.Width), Math.Clamp(rect.Y + dy, 0, height - rect.Height), rect.Width, rect.Height);
    internal static Int32Rect Resize(Int32Rect rect, int horizontal, int vertical, int dx, int dy, int width, int height)
    {
        int left = rect.X, top = rect.Y, right = left + rect.Width, bottom = top + rect.Height;
        int minimumX = Math.Min(4, width), minimumY = Math.Min(4, height);
        if (horizontal < 0) left = Math.Clamp(left + dx, 0, right - minimumX);
        if (horizontal > 0) right = Math.Clamp(right + dx, left + minimumX, width);
        if (vertical < 0) top = Math.Clamp(top + dy, 0, bottom - minimumY);
        if (vertical > 0) bottom = Math.Clamp(bottom + dy, top + minimumY, height);
        return new(left, top, right - left, bottom - top);
    }
}

internal sealed class CropHandles : Canvas, IDisposable
{
    private readonly AnnotationSurface _surface;
    private readonly Window _owner;
    private readonly Action _activate;
    private readonly (Thumb Thumb, int X, int Y)[] _handles;
    private Int32Rect _before;
    private Point _start;
    private Vector _scale;
    private Vector _displayScale;
    internal int HandleCount => _handles.Length;
    internal CropHandles(AnnotationSurface surface, Window owner, Action activate)
    {
        _surface = surface; _owner = owner; _activate = activate;
        _handles = new (Thumb, int, int)[8]; int index = 0;
        foreach (var (x, y) in new[] { (-1,-1), (0,-1), (1,-1), (-1,0), (1,0), (-1,1), (0,1), (1,1) })
        {
            var thumb = new Thumb { Width = UiDesign.Number("HandleSize"), Height = UiDesign.Number("HandleSize"), Cursor = x == 0 ? Cursors.SizeNS : y == 0 ? Cursors.SizeWE : x == y ? Cursors.SizeNWSE : Cursors.SizeNESW, ToolTip = "拖动调整选区；标注会保留" };
            thumb.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
            thumb.SetResourceReference(Control.BorderBrushProperty, "SelectionBrush"); thumb.BorderThickness = new Thickness(2);
            var border = new FrameworkElementFactory(typeof(Border)); border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); border.SetValue(Border.BorderThicknessProperty, new Thickness(2)); border.SetValue(Border.CornerRadiusProperty, Application.Current.FindResource("RadiusHandle"));
            thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
            AutomationProperties.SetAutomationId(thumb, $"CropHandle{x}_{y}");
            AutomationProperties.SetName(thumb, $"选区手柄 {x},{y}");
            thumb.DragStarted += (_, _) => { _activate(); _before = surface.CropBounds; _start = Mouse.GetPosition(owner); _scale = ScaleInOwner(surface, owner); };
            thumb.DragDelta += (_, _) =>
            {
                var delta = Mouse.GetPosition(owner) - _start;
                surface.SetCrop(CropGeometry.Resize(_before, x, y, (int)Math.Round(delta.X / _scale.X), (int)Math.Round(delta.Y / _scale.Y), surface.SourceWidth, surface.SourceHeight), false);
            };
            thumb.DragCompleted += (_, e) => { if (e.Canceled) surface.SetCrop(_before, false); else surface.CommitCrop(_before); };
            Children.Add(thumb); _handles[index++] = (thumb, x, y);
        }
        surface.CropChanged += Update; SizeChanged += (_, _) => Update(); Update();
        LayoutUpdated += ScaleChanged;
    }
    private void ScaleChanged(object? sender, EventArgs e)
    {
        if (!_surface.IsLoaded) return; var scale = ScaleInOwner(_surface, _owner);
        if ((scale - _displayScale).Length < 0.0001) return; _displayScale = scale;
        // Maintain an accessible 10 DIP hit target even when the image is zoomed.
        foreach (var (thumb, _, _) in _handles) { thumb.Width = UiDesign.Number("HandleSize") / scale.X; thumb.Height = UiDesign.Number("HandleSize") / scale.Y; } Update();
    }
    internal static Vector ScaleInOwner(FrameworkElement element, Window owner)
    {
        var transform = element.TransformToAncestor(owner); var origin = transform.Transform(new Point());
        var x = transform.Transform(new Point(1, 0)); var y = transform.Transform(new Point(0, 1));
        return new(Math.Max(0.001, (x - origin).Length), Math.Max(0.001, (y - origin).Length));
    }
    private void Update()
    {
        Width = _surface.Width; Height = _surface.Height;
        foreach (var (thumb, x, y) in _handles)
        {
            Canvas.SetLeft(thumb, (x + 1) * Width / 2 - thumb.Width / 2);
            Canvas.SetTop(thumb, (y + 1) * Height / 2 - thumb.Height / 2);
        }
    }
    public void Dispose() { _surface.CropChanged -= Update; LayoutUpdated -= ScaleChanged; }
}
