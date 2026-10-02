using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickCapture;

// One definition drives buttons, tooltips, selected/enabled state and gestures.
internal sealed record ToolDefinition(string Id, string Name, string Icon, Key Shortcut, Action Execute,
    string Group = "draw", AnnotationTool? Tool = null, Func<bool>? CanExecute = null,
    string AlternativeKeys = "", Func<StackPanel>? Options = null, IReadOnlyList<ToolDefinition>? Children = null);

internal static class ToolCatalog
{
    internal static IReadOnlyList<ToolDefinition> Create(ScreenshotEditor editor, Action? reselect) => new ToolDefinition[]
    {
        new("crop", "选择 / 裁剪", "crop", Key.C, () => editor.SelectTool(AnnotationTool.Crop), Tool: AnnotationTool.Crop),
        new("select", "编辑标注", "scan", Key.E, () => editor.SelectTool(AnnotationTool.Select), Tool: AnnotationTool.Select, AlternativeKeys: "拖动移动 / 手柄缩放 / Delete"),
        new("arrow", "箭头", "move-up-right", Key.A, () => editor.SelectTool(AnnotationTool.Arrow), Tool: AnnotationTool.Arrow),
        new("rectangle", "形状", "square", Key.R, editor.ShowShapes, Tool: AnnotationTool.Rectangle, Options: editor.CreateShapeOptions),
        new("text", "文字", "type", Key.T, editor.ShowTextOptions, Tool: AnnotationTool.Text, Options: editor.CreateTextOptions),
        new("freehand", "涂鸦", "pencil", Key.B, () => editor.SelectTool(AnnotationTool.Freehand), Tool: AnnotationTool.Freehand, Options: editor.CreateFreehandOptions),
        new("mosaic", "马赛克", "grid-2x2", Key.M, () => editor.SelectTool(AnnotationTool.Mosaic), Tool: AnnotationTool.Mosaic, Options: editor.CreateMosaicOptions),
        new("color", "标注颜色", "palette", Key.K, editor.ShowColors, "options", Options: editor.CreateColorOptions),
        new("undo", "撤销", "undo-2", Key.Z, editor.Undo, "history", CanExecute: () => editor.Surface.CanUndo, AlternativeKeys: "Ctrl+Z"),
        new("redo", "重做", "redo-2", Key.Y, editor.Redo, "history", CanExecute: () => editor.Surface.CanRedo, AlternativeKeys: "Ctrl+Y"),
        new("pin", "贴图", "pin", Key.P, editor.Pin, "output"),
        new("reselect", "重新框选", "scan", Key.N, () => reselect?.Invoke(), "output", CanExecute: () => reselect != null),
        new("cancel", "取消", "x", Key.Escape, editor.Complete, "output"),
        new("save", "保存", "save", Key.S, editor.SaveAndComplete, "output", AlternativeKeys: "Ctrl+S"),
        new("copy", "复制", "copy", Key.V, async () => await editor.CopyAndCompleteAsync(), "output", AlternativeKeys: "Enter / Ctrl+C")
    };
}
