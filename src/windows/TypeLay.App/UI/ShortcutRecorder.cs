using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TypeLay.Core;

namespace TypeLay.App;

// Recording only changes a preference. It never intercepts source-field keys or
// grants a candidate permission; Windows registration is reported separately.
internal static class ShortcutRecorder
{
    private static readonly DependencyProperty RecorderProperty = DependencyProperty.RegisterAttached(
        "Recorder", typeof(bool), typeof(ShortcutRecorder), new PropertyMetadata(false));
    public static bool IsRecording => Keyboard.FocusedElement is DependencyObject focused && (bool)focused.GetValue(RecorderProperty);

    public static TextBox Create(string value, string label, bool allowEmpty = false)
    {
        var input = new TextBox
        {
            Text = value, Width = 178, MaxLength = 64, Padding = new(9, 6, 9, 6),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12, IsReadOnly = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            ToolTip = UiTheme.L("点击后按下快捷键，Esc 取消", "Click, then press a shortcut. Esc cancels")
        };
        input.SetValue(RecorderProperty, true);
        InputMethod.SetIsInputMethodEnabled(input, false);
        AutomationProperties.SetName(input, label);
        string previous = value;
        input.GotKeyboardFocus += (_, _) => { previous = input.Text; input.SelectAll(); input.SetResourceReference(Control.BorderBrushProperty, "Accent"); };
        input.LostKeyboardFocus += (_, _) => input.SetResourceReference(Control.BorderBrushProperty, "Line");
        input.PreviewKeyDown += (_, e) =>
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;
            e.Handled = true;
            if (key == Key.Escape) { input.Text = previous; Keyboard.ClearFocus(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            if (allowEmpty && key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                input.Text = ""; previous = ""; return;
            }
            string? chord = Chord(key, Keyboard.Modifiers);
            if (chord is not null && TranslationShortcut.TryParse(chord, out var parsed))
            {
                input.Text = parsed!.ToString(); previous = input.Text;
                input.ToolTip = UiTheme.L("已录制；自动保存", "Recorded; saves automatically");
            }
            else input.ToolTip = UiTheme.L("使用 Ctrl / Alt 与字母、数字或功能键；不支持 F12。", "Use Ctrl / Alt with a letter, number or function key. F12 is reserved.");
        };
        return input;
    }

    public static void AttachSession(ComboBox input, string defaultKey)
    {
        input.SetValue(RecorderProperty, true);
        InputMethod.SetIsInputMethodEnabled(input, false);
        input.ToolTip = UiTheme.L("选择或按下快捷键", "Choose or press a shortcut");
        input.PreviewKeyDown += (_, e) =>
        {
            if (input.IsDropDownOpen) return;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            string? value = key == Key.Tab && defaultKey == "Tab" && Keyboard.Modifiers == ModifierKeys.None ? "Tab"
                : key == Key.Escape && defaultKey == "Esc" && Keyboard.Modifiers == ModifierKeys.None ? "Esc" : Chord(key, Keyboard.Modifiers);
            if (value is not null && input.Items.Contains(value)) { e.Handled = true; input.SelectedItem = value; }
        };
    }

    private static string? Chord(Key key, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Windows)) return null;
        string? name = key >= Key.A && key <= Key.Z ? key.ToString()
            : key >= Key.D0 && key <= Key.D9 ? ((int)key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : key >= Key.F1 && key <= Key.F24 ? key.ToString() : null;
        return name is null ? null : (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "")
            + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + name;
    }
}
