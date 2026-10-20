using System.Globalization;
using TypeLay.Core;

namespace TypeLay.App;

internal sealed record RegisteredShortcut(string Action, uint Key, ShortcutModifiers Modifiers);

/// <summary>Reserves chords with Windows. Physical invocations are stamped by KeyboardHook.</summary>
internal sealed class OnDemandHotkeys(nint window,
    Func<nint, int, uint, uint, bool>? register = null, Func<nint, int, bool>? unregister = null) : IDisposable
{
    private const int WakeId = 0x5451, DirectId = 0x5452;
    private bool _wakeRegistered, _directRegistered;
    private (bool Enabled, string Wake, string Direct)? _configuration;
    public RegisteredShortcut[] Bindings { get; private set; } = [];
    public string Status { get; private set; } = "新增快捷键仅在按需模式下启用。";

    public bool Registered => _wakeRegistered && _directRegistered;
    public bool HasRegistration => _wakeRegistered || _directRegistered;
    public bool RegistrationFailed
    {
        get; private set;
    }
    public (string Wake, string Direct)? FailedConfiguration => RegistrationFailed && _configuration is { Enabled: true } configuration
        ? (configuration.Wake, configuration.Direct) : null;

    public void Configure(bool enabled, string wake, string direct, bool registerNow = true)
    {
        var configuration = (enabled, wake, direct);
        if (_configuration == configuration)
            return;
        if (!Suspend())
        {
            // The old tuple is no longer the saved configuration. Retain only
            // actual reservations for release, never restore its missing keys.
            _configuration = null;
            Bindings = [];
            Status = "快捷键注销未确认，当前不会执行按需命令。";
            return;
        }
        _configuration = configuration;
        if (!enabled)
        {
            Status = "新增快捷键仅在启用 TypeLay 的按需模式下生效。";
            return;
        }
        if (registerNow)
            Resume(restoring: false);
        else
            Status = "浏览器快捷键连接中。";
    }

    // Registration state is independent from configuration identity. A failed
    // release remains owned and must never be represented as a successful handoff.
    public bool Suspend()
    {
        RegistrationFailed = false;
        if (_wakeRegistered && (unregister ?? Native.UnregisterHotKey)(window, WakeId))
            _wakeRegistered = false;
        if (_directRegistered && (unregister ?? Native.UnregisterHotKey)(window, DirectId))
            _directRegistered = false;
        Bindings = [];
        if (HasRegistration)
            Status = "快捷键注销未确认，当前不会执行按需命令。";
        return !HasRegistration;
    }

    public bool Resume(bool restoring = true)
    {
        RegistrationFailed = false;
        if (_configuration is not { Enabled: true } configuration)
        {
            Bindings = [];
            return !HasRegistration;
        }
        var bindings = new List<RegisteredShortcut>();
        var failures = new List<string>();
        if (!_wakeRegistered)
            _wakeRegistered = Register(WakeId, "wake", "唤醒翻译 UI", configuration.Wake, bindings, failures);
        else
            AddBinding("wake", configuration.Wake, bindings);
        if (!_directRegistered)
            _directRegistered = Register(DirectId, "direct", "直接翻译并替换", configuration.Direct, bindings, failures);
        else
            AddBinding("direct", configuration.Direct, bindings);
        Bindings = bindings.ToArray();
        Status = Registered ? "两个快捷键已启用；操作不会切换工作模式。"
            : (restoring ? "快捷键当前不可用：" : "设置已保存，但") + string.Join("、", failures)
                + "未能注册，可能被系统或其他应用占用。可重试启用快捷键或修改组合键。";
        return Registered;
    }
    private static void AddBinding(string action, string text, List<RegisteredShortcut> bindings)
    {
        if (TranslationShortcut.TryParse(text, out var chord))
            bindings.Add(new(action, Key(chord!), chord!.Modifiers));
    }
    private static uint Key(TranslationShortcut chord) => chord.Key.Length == 1 ? chord.Key[0]
        : 0x6Fu + uint.Parse(chord.Key.AsSpan(1), CultureInfo.InvariantCulture);

    private bool Register(int id, string action, string label, string text,
        List<RegisteredShortcut> bindings, List<string> failures)
    {
        if (!TranslationShortcut.TryParse(text, out var chord))
        {
            failures.Add(label + "（" + text + "）");
            return false;
        }
        uint modifiers = (chord!.Modifiers.HasFlag(ShortcutModifiers.Alt) ? 1u : 0)
            | (chord.Modifiers.HasFlag(ShortcutModifiers.Control) ? 2u : 0)
            | (chord.Modifiers.HasFlag(ShortcutModifiers.Shift) ? 4u : 0);
        uint key = Key(chord);
        if (!(register ?? Native.RegisterHotKey)(window, id, modifiers | 0x4000, key))
        {
            RegistrationFailed = true;
            failures.Add(label + "（" + text + "）");
            return false;
        }
        bindings.Add(new(action, key, chord.Modifiers));
        return true;
    }

    public void Dispose() => Suspend();
}
