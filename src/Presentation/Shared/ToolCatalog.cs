using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickCapture;

// One definition drives buttons, tooltips, selected/enabled state and gestures.
internal sealed record ToolDefinition(string Id, string Name, string Icon, Key Shortcut, Action Execute,
    string Group = "draw", AnnotationTool? Tool = null, Func<bool>? CanExecute = null,
    string AlternativeKeys = "", Func<WrapPanel>? Options = null);

internal static class ToolCatalog
{
    internal static IReadOnlyList<ToolDefinition> Create(ScreenshotEditor editor, Action? reselect) => new ToolDefinition[]
    {
        new("rectangle", "形状", "square", Key.R, editor.ShowShapes, Tool: AnnotationTool.Rectangle, Options: editor.CreateShapeOptions),
        new("freehand", "涂鸦", "pencil", Key.B, () => editor.SelectTool(AnnotationTool.Freehand), Tool: AnnotationTool.Freehand, Options: editor.CreateFreehandOptions),
        new("arrow", "箭头", "move-up-right", Key.A, () => editor.SelectTool(AnnotationTool.Arrow), Tool: AnnotationTool.Arrow, Options: editor.CreateArrowOptions),
        new("text", "文字", "type", Key.T, editor.ShowTextOptions, Tool: AnnotationTool.Text, Options: editor.CreateTextOptions),
        new("step", "步骤编号", "list-ordered", Key.D, editor.ShowStepOptions, Tool: AnnotationTool.Step, Options: editor.CreateStepOptions),
        new("mosaic", "马赛克", "mosaic-pixels", Key.M, () => editor.SelectTool(AnnotationTool.Mosaic), Tool: AnnotationTool.Mosaic, Options: editor.CreateMosaicOptions),
        new("color", "标注颜色", "palette", Key.K, editor.ShowColors, "draw"),
        new("undo", "撤销", "undo-2", Key.Z, editor.Undo, "history", CanExecute: () => editor.Surface.CanUndo, AlternativeKeys: "Ctrl+Z"),
        new("redo", "重做", "redo-2", Key.Y, editor.Redo, "history", CanExecute: () => editor.Surface.CanRedo, AlternativeKeys: "Ctrl+Y"),
        new("crop", "选择 / 裁剪", "crop", Key.C, () => editor.SelectTool(AnnotationTool.Crop), "history", Tool: AnnotationTool.Crop),
        new("select", "编辑标注", "scan", Key.E, () => editor.SelectTool(AnnotationTool.Select), "history", Tool: AnnotationTool.Select, AlternativeKeys: "拖动移动 / 手柄缩放 / Delete"),
        new("ocr", "提取文字", "text-search", Key.O, editor.ExtractText, "output"),
        new("pin", "贴图", "pin", Key.P, editor.Pin, "output"),
        new("reselect", "重新框选", "scan", Key.N, () => reselect?.Invoke(), "output", CanExecute: () => reselect != null),
        new("cancel", "取消", "x", Key.Escape, editor.Complete, "output"),
        new("save", "保存", "save", Key.S, editor.SaveAndComplete, "output", AlternativeKeys: "Ctrl+S"),
        new("copy", "复制", "copy", Key.V, async () => await editor.CopyAndCompleteAsync(), "output", AlternativeKeys: "Enter / Ctrl+C")
    };
}
