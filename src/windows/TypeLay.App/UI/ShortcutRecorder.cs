using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TypeLay.Core;

namespace TypeLay.App;

// A native preference recorder, not a source-field key interceptor or proof of
// registration. Text remains the draft; the recording prompt never changes it.
internal sealed class ShortcutRecorder : TextBox
{
    public static readonly DependencyProperty RecordingProperty = DependencyProperty.Register(
        nameof(Recording), typeof(bool), typeof(ShortcutRecorder), new PropertyMetadata(false));
    public static readonly DependencyProperty RecordingPromptProperty = DependencyProperty.Register(
        nameof(RecordingPrompt), typeof(string), typeof(ShortcutRecorder), new PropertyMetadata(""));
    public static readonly DependencyProperty EmptyPromptProperty = DependencyProperty.Register(
        nameof(EmptyPrompt), typeof(string), typeof(ShortcutRecorder), new PropertyMetadata(""));
    public bool Recording
    {
        get => (bool)GetValue(RecordingProperty); private set => SetValue(RecordingProperty, value);
    }
    public string RecordingPrompt
    {
        get => (string)GetValue(RecordingPromptProperty); private set => SetValue(RecordingPromptProperty, value);
    }
    public string EmptyPrompt
    {
        get => (string)GetValue(EmptyPromptProperty); private set => SetValue(EmptyPromptProperty, value);
    }
    // WM_HOTKEY may be queued behind the final recording key or cancellation.
    // Keep the existing suppression for the entire recorder focus ownership,
    // not just the prompt's shorter visual lifetime.
    public static bool IsRecording => Keyboard.FocusedElement is ShortcutRecorder;
    public bool IsCommittingRecording
    {
        get; private set;
    }
    private string? _sessionDefault;
    private bool _allowEmpty;
    private Func<bool>? _current;
    private object _lifetime = new();
    private readonly TranslateTransform _shake = new();
    public event Action? RecordingStarted;
    public event Action? UnchangedRecording;

    public static ShortcutRecorder Create(string value, string label, bool allowEmpty = false)
    {
        var input = new ShortcutRecorder
        {
            Text = value,
            MaxLength = 64,
            IsReadOnly = true,
            MinWidth = 92,
            MaxWidth = 178,
            Height = 26,
            Padding = new(10, 0, 10, 0),
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            TextAlignment = TextAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            _allowEmpty = allowEmpty
        };
        input.RenderTransform = input._shake;
        input.SetResourceReference(StyleProperty, "SettingsShortcutRecorder");
        AutomationProperties.SetName(input, UiStrings.Translate(label));
        InputMethod.SetIsInputMethodEnabled(input, false);
        input.IsVisibleChanged += (_, _) => { if (!input.IsVisible) input.Retire(); };
        input.Unloaded += (_, _) => input.Retire();
        input.TextChanged += (_, _) => input.RefreshPresentation();
        input.RefreshPresentation();
        return input;
    }
    public static void AttachSession(ShortcutRecorder input, string defaultKey) => input._sessionDefault = defaultKey;
    public void AttachPage(Func<bool> current) => _current = current;
    private bool Current => IsVisible && IsLoaded && IsEnabled && _current?.Invoke() != false;
    public void CancelRecording()
    {
        Recording = false;
        RefreshPresentation();
    }
    private void Retire()
    {
        _lifetime = new();
        _shake.BeginAnimation(TranslateTransform.XProperty, null);
        CancelRecording();
    }
    public void RefreshPresentation()
    {
        if (UiTheme.ReduceMotion)
            _shake.BeginAnimation(TranslateTransform.XProperty, null);
        RecordingPrompt = UiTheme.L("按下按键…", "Press keys…");
        EmptyPrompt = UiTheme.L("未设置", "Not set");
        ToolTip = UiTheme.L("点击后按下快捷键，Esc 取消", "Click, then press a shortcut. Esc cancels");
        AutomationProperties.SetItemStatus(this, Recording ? RecordingPrompt : Text.Length == 0 ? EmptyPrompt : Text);
        if (Template?.FindName("pulse", this) is Border pulse)
        {
            pulse.BeginAnimation(OpacityProperty, null);
            pulse.Opacity = 0;
            if (Recording && IsVisible && !UiTheme.ReduceMotion)
                pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
        }
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        RefreshPresentation();
    }
    private void ToggleRecording()
    {
        if (!Current || !Focus())
            return;
        _shake.BeginAnimation(TranslateTransform.XProperty, null);
        Recording = !Recording;
        if (Recording)
            RecordingStarted?.Invoke();
        RefreshPresentation();
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            e.Handled = true;
            ToggleRecording();
            return;
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        CancelRecording();
        base.OnLostKeyboardFocus(e);
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!Current || !IsKeyboardFocused)
            return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (!Recording)
        {
            if (modifiers == ModifierKeys.None && key is Key.Space or Key.Enter)
            {
                e.Handled = true;
                ToggleRecording();
            }
            else
                base.OnPreviewKeyDown(e);
            return;
        }
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        if (key == Key.Escape && modifiers == ModifierKeys.None && _sessionDefault != "Esc")
        {
            CancelRecording();
            return;
        }
        string chord = _allowEmpty && modifiers == ModifierKeys.None && key is Key.Back or Key.Delete ? "" : Chord(key, modifiers);
        // The existing group parser is the authority for permitted keys and
        // duplicates. Invalid recordings remain an unsaved, editable draft.
        if (_sessionDefault is null && TranslationShortcut.TryParse(chord, out var parsed))
            chord = parsed!.ToString();
        CancelRecording();
        IsCommittingRecording = true;
        try
        {
            if (Text == chord)
                UnchangedRecording?.Invoke();
            else
                Text = chord;
        }
        finally { IsCommittingRecording = false; }
    }
    private static string Chord(Key key, ModifierKeys modifiers)
    {
        string name = key is Key.Escape ? "Esc" : key >= Key.D0 && key <= Key.D9
            ? ((int)key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture) : key.ToString();
        return (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "")
            + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "")
            + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "")
            + (modifiers.HasFlag(ModifierKeys.Windows) ? "Win+" : "") + name;
    }
    public void ShowInvalidRecording()
    {
        if (!Current || UiTheme.ReduceMotion)
            return;
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360), FillBehavior = FillBehavior.Stop };
        foreach (var (time, value) in new[] { (0, 0d), (72, -4d), (144, 4d), (216, -4d), (288, 4d), (360, 0d) })
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(time))));
        _shake.BeginAnimation(TranslateTransform.XProperty, animation);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new RecorderPeer(this);
    private sealed class RecorderPeer(ShortcutRecorder owner) : TextBoxAutomationPeer(owner), IInvokeProvider
    {
        private readonly object _lifetime = owner._lifetime;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);
        public void Invoke()
        {
            if (!owner.IsEnabled)
                throw new ElementNotEnabledException();
            if (ReferenceEquals(_lifetime, owner._lifetime))
                owner.ToggleRecording();
        }
    }
}
