using System.Globalization;
using System.Windows.Interop;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.App;

// Pausing is independent of source-field acceptance. This message-only window
// cannot become foreground, and the existing settings retirement path owns save.
internal sealed class GlobalPauseShortcut : IDisposable
{
    private const int Id = 0x5453;
    private readonly SettingsStore _settings;
    private readonly Func<bool, Task<bool>> _setEnabled;
    private readonly HwndSource _window;
    private readonly Func<nint, int, uint, uint, bool> _register;
    private readonly Func<nint, int, bool> _unregister;
    internal nint WindowHandle => _window.Handle;
    private bool _registered, _changing;
    private uint _virtualKey, _modifiers;
    private string? _configured;
    private string _status = "unset";
    public string Status => _status switch
    {
        "release-failed" => UiTheme.L("全局暂停快捷键注销未确认，当前不执行。", "Pause shortcut release was not confirmed; disabled."),
        "active" => UiTheme.L("全局暂停快捷键已启用", "Global pause shortcut is active"),
        "registration-failed" => UiTheme.L("已保存，但全局暂停快捷键被系统或其他应用占用。", "Saved, but the global pause shortcut is in use by another app."),
        "save-failed" => UiTheme.L("暂停状态未保存，请打开设置重试。", "Pause state was not saved. Open Settings to retry."),
        _ => UiTheme.L("尚未设置全局暂停快捷键", "No global pause shortcut")
    };
    public bool CanRetry => _registered && _configured != _settings.Value.PauseShortcut
        || !_registered && _settings.Value.PauseShortcut.Length > 0;
    public void RetryRegistration() => Configure();
    public event Action? Changed;

    public GlobalPauseShortcut(SettingsStore settings, Func<bool, Task<bool>> setEnabled,
        Func<nint, int, uint, uint, bool>? register = null, Func<nint, int, bool>? unregister = null)
    {
        _settings = settings; _setEnabled = setEnabled;
        _register = register ?? Native.RegisterHotKey; _unregister = unregister ?? Native.UnregisterHotKey;
        _window = new HwndSource(new HwndSourceParameters("TypeLay pause shortcut") { ParentWindow = new nint(-3), WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(Message);
        settings.Changed += Configure;
        Configure();
    }
    private void Configure()
    {
        string requested = _settings.Value.PauseShortcut;
        if (requested == _configured && (requested.Length == 0 || _registered)) return;
        if (_registered)
        {
            if (!_unregister(_window.Handle, Id))
            {
                _configured = null; _status = "release-failed"; Changed?.Invoke(); return;
            }
            _registered = false;
        }
        _configured = requested;
        if (!TranslationShortcut.TryParse(requested, out var chord))
        {
            _status = "unset"; Changed?.Invoke(); return;
        }
        uint key = chord!.Key.Length == 1 ? chord.Key[0] : 0x6Fu + uint.Parse(chord.Key.AsSpan(1), CultureInfo.InvariantCulture);
        uint modifiers = (chord.Modifiers.HasFlag(ShortcutModifiers.Alt) ? 1u : 0) | (chord.Modifiers.HasFlag(ShortcutModifiers.Control) ? 2u : 0)
            | (chord.Modifiers.HasFlag(ShortcutModifiers.Shift) ? 4u : 0) | 0x4000u;
        _virtualKey = key; _modifiers = modifiers & 0x0Fu;
        _registered = _register(_window.Handle, Id, modifiers, key);
        _status = _registered ? "active" : "registration-failed";
        Changed?.Invoke();
    }
    public IReadOnlyList<string> InspectConflicts(string candidate)
    {
        if (candidate.Length == 0 || _registered && _configured == candidate) return [];
        if (!TranslationShortcut.TryParse(candidate, out var chord)) return [];
        using var probe = new HwndSource(new HwndSourceParameters("TypeLay pause shortcut check") { ParentWindow = new nint(-3), WindowStyle = 0, Width = 0, Height = 0 });
        uint key = chord!.Key.Length == 1 ? chord.Key[0] : 0x6Fu + uint.Parse(chord.Key.AsSpan(1), CultureInfo.InvariantCulture);
        uint modifiers = (chord.Modifiers.HasFlag(ShortcutModifiers.Alt) ? 1u : 0) | (chord.Modifiers.HasFlag(ShortcutModifiers.Control) ? 2u : 0)
            | (chord.Modifiers.HasFlag(ShortcutModifiers.Shift) ? 4u : 0) | 0x4000u;
        if (!Native.RegisterHotKey(probe.Handle, Id, modifiers, key)) return [candidate];
        return Native.UnregisterHotKey(probe.Handle, Id) ? [] : [candidate];
    }
    private nint Message(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == Id && _registered && _configured == _settings.Value.PauseShortcut && !_changing && !ShortcutRecorder.IsRecording
            && ((ulong)lParam >> 16 & 0xFFFF) == _virtualKey && ((ulong)lParam & 0x0F) == _modifiers)
        {
            handled = true; Toggle();
        }
        return 0;
    }
    private async void Toggle()
    {
        _changing = true;
        try
        {
            if (!await _setEnabled(!_settings.Value.Enabled))
            {
                _status = "save-failed"; Changed?.Invoke();
            }
        }
        finally { _changing = false; }
    }
    public void Dispose()
    {
        _settings.Changed -= Configure;
        if (_registered) _unregister(_window.Handle, Id);
        _registered = false; _window.RemoveHook(Message); _window.Dispose();
    }
}
