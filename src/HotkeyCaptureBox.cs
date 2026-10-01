using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickCapture;

internal sealed class HotkeyCaptureBox : TextBox
{
    internal const string Instructions = "点击快捷键输入框，再按下组合键；Esc 撤销本次录入，Tab 切换输入框。";
    private string _beforeFocus;
    internal event Action<string>? HintChanged;

    internal HotkeyCaptureBox(string value, string label)
    {
        Style = (Style)Application.Current.FindResource(typeof(TextBox));
        Text = _beforeFocus = value;
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        AllowDrop = false;
        ContextMenu = null;
        Cursor = Cursors.Hand;
        ToolTip = Instructions;
        InputMethod.SetIsInputMethodEnabled(this, false);
        AutomationProperties.SetName(this, label);
        AutomationProperties.SetHelpText(this, Instructions);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _beforeFocus = Text;
        SetResourceReference(BorderBrushProperty, "Accent");
        HintChanged?.Invoke("请按下组合键，例如 Ctrl+Alt+S；按 Esc 可撤销本次录入。");
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        HintChanged?.Invoke(Instructions);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var modifiers = e.KeyboardDevice.Modifiers;
        if (e.KeyboardDevice.IsKeyDown(Key.LWin) || e.KeyboardDevice.IsKeyDown(Key.RWin)) modifiers |= ModifierKeys.Windows;
        if (key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        e.Handled = true;
        CaptureKey(key, modifiers);
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (e.Key != Key.Tab) e.Handled = true;
        base.OnPreviewKeyUp(e);
    }

    internal void CaptureKey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            Text = _beforeFocus;
            HintChanged?.Invoke("已撤销本次录入，可以继续按下组合键。");
            return;
        }
        if (HotkeyService.IsModifier(key))
        {
            HintChanged?.Invoke("请继续按一个普通按键，例如字母或 F1–F12。");
            return;
        }
        try
        {
            Text = HotkeyService.Format(key, modifiers);
            HintChanged?.Invoke($"已录入 {Text}，点击“保存设置”后生效。");
        }
        catch (ArgumentException)
        {
            HintChanged?.Invoke("请同时按下 Ctrl、Alt、Shift 或 Win，以及一个普通按键。");
        }
    }
}
