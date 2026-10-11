using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture;

internal static class ContextToolbarTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Context panel resizes during CJK text/composition, restores selected-object options and preserves undo/redo", async () =>
        {
            var window = new EditorWindow(AcceptanceArtifacts.NoteImage(900, 540), new Settings(), _ => { });
            try
            {
                window.Show(); await Task.Delay(80); var editor = window.Editor;
                var toolbar = Toolbar(window);
                editor.SelectTool(AnnotationTool.Text); window.UpdateLayout(); double before = toolbar.PropertyPanel.ActualWidth;
                editor.BeginText(new Point(80, 120)); var input = editor.TextInput!; input.Text = "中文输入与 English\n保留原位编辑"; window.UpdateLayout();
                UpgradeTests.Ensure(InputMethod.GetIsInputMethodEnabled(input) && toolbar.PropertyPanel.ActualWidth > before && input.IsKeyboardFocused, "Text entry lost IME/focus or failed to resize");
                var composition = new TextComposition(InputManager.Current, input, "中");
                input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
                input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                UpgradeTests.Ensure(editor.EditingText && editor.Tool == AnnotationTool.Text, "Composition Escape cancelled text or changed tool");
                input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent });
                editor.SetTextStyle("Microsoft YaHei UI", 32, true, TextAlignment.Center); editor.ApplyColor(Colors.Blue);
                UpgradeTests.Ensure(editor.EditingText && input.FontSize == 32 && input.FontWeight == FontWeights.Bold && input.Text.Contains("中文"), "Property adjustment recreated the input or lost the draft");
                editor.ConfirmText(); window.UpdateLayout(); UpgradeTests.Ensure(editor.Surface.Count == 1 && toolbar.PropertyPanel.ActualWidth < before + 10, "Confirm did not shrink text actions or added extra history");
                editor.Undo(); UpgradeTests.Ensure(editor.Surface.Count == 0, "Text confirmation is not one undo step"); editor.Redo();
                var original = editor.Surface.Items.Single(); editor.SelectTool(AnnotationTool.Select); editor.Surface.SelectAt(original.Start + new Vector(original.TextWidth / 2, original.FontSize / 2)); window.UpdateLayout();
                UpgradeTests.Ensure(toolbar.PropertyPanel.IsVisible && editor.OptionsFor(AnnotationTool.Text).IsVisible, "Selected object did not restore text properties");
                editor.BeginText(original.Start, original); editor.TextInput!.Text = "取消这次改动"; editor.CancelText();
                UpgradeTests.Ensure(editor.Surface.Items.Single().Text == original.Text, "Cancelled object edit lost the original");
                editor.Surface.Deselect(); window.UpdateLayout(); UpgradeTests.Ensure(toolbar.PropertyPanel.Visibility == Visibility.Collapsed, "Deselect retained stale properties");
                editor.SelectTool(AnnotationTool.Crop); window.UpdateLayout(); UiChangeTests.Render(window, "toolbar-context-hidden.png");
            }
            finally { window.Close(); }
        });
        await check("Save dialog cancellation, copying and screenshot cancellation leave no property panel or reserved height", async () =>
        {
            var window = new EditorWindow(AcceptanceArtifacts.NoteImage(500, 300), new Settings { OutputDirectory = Paths.DefaultOutput }, _ => { });
            try
            {
                window.Show(); await Task.Delay(80); var editor = window.Editor; var toolbar = Toolbar(window);
                editor.SelectTool(AnnotationTool.Rectangle); window.UpdateLayout(); double expanded = toolbar.ActualHeight;
                var closed = window.Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(120); var save = Application.Current.Windows.OfType<ScreenshotSaveWindow>().Single();
                    UpgradeTests.Ensure(toolbar.PropertyPanel.Visibility == Visibility.Collapsed, "Save left properties behind the dialog"); save.Close();
                });
                editor.SaveAndComplete(); await (await closed); window.UpdateLayout();
                UpgradeTests.Ensure(window.IsVisible && toolbar.PropertyPanel.Visibility == Visibility.Collapsed && toolbar.ActualHeight < expanded - 8, "Cancel save retained property placeholder");
                editor.SelectTool(AnnotationTool.Text); window.UpdateLayout(); UpgradeTests.Ensure(toolbar.PropertyPanel.IsVisible, "Tool switch failed to restore properties after save");
                editor.Complete(); UpgradeTests.Ensure(toolbar.PropertyPanel.Visibility == Visibility.Collapsed, "Cancel screenshot retained properties");
                var copy = new EditorWindow(AcceptanceArtifacts.NoteImage(500, 300), new Settings { OutputDirectory = Paths.DefaultOutput, AutoSaveScreenshot = false }, _ => { });
                try
                {
                    copy.Show(); await Task.Delay(80); copy.Editor.SelectTool(AnnotationTool.Text);
                    copy.Editor.BeginText(new Point(30, 70)); copy.Editor.TextInput!.Text = "复制前完成文字";
                    var copyToolbar = Toolbar(copy); Task copying = copy.Editor.CopyAndCompleteAsync();
                    UpgradeTests.Ensure(copyToolbar.PropertyPanel.Visibility == Visibility.Collapsed, "Copy retained properties while preparing output");
                    await copying; UpgradeTests.Ensure(!copy.IsVisible, "Copy did not complete the screenshot session");
                }
                finally { copy.Close(); }
            }
            finally { window.Close(); }
        });
    }
    private static ScreenshotToolbar Toolbar(EditorWindow window) => ((DockPanel)window.Content).Children.OfType<ScreenshotToolbar>().Single();
}
