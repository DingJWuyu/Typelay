using System.Globalization;
using TypeLay.Core;

namespace TypeLay.App;

internal sealed record RegisteredShortcut(string Action, uint Key, ShortcutModifiers Modifiers);

/// <summary>Reserves chords with Windows. Physical invocations are stamped by KeyboardHook.</summary>
internal sealed class OnDemandHotkeys(nint window) : IDisposable
{
    private const int WakeId = 0x5451, DirectId = 0x5452;
    private bool _wakeRegistered, _directRegistered;
    private (bool Enabled, string Wake, string Direct)? _configuration;
    public RegisteredShortcut[] Bindings { get; private set; } = [];
    public string Status { get; private set; } = "新增快捷键仅在按需模式下启用。";

    public void Configure(bool enabled, string wake, string direct)
    {
        var configuration = (enabled, wake, direct);
        if (_configuration == configuration)
            return;
        Release();
        _configuration = configuration;
        if (!enabled)
        {
            Status = "新增快捷键仅在启用 TypeLay 的按需模式下生效。";
            return;
        }
        var bindings = new List<RegisteredShortcut>();
        var failures = new List<string>();
        _wakeRegistered = Register(WakeId, "wake", "唤醒翻译 UI", wake, bindings, failures);
        _directRegistered = Register(DirectId, "direct", "直接翻译并替换", direct, bindings, failures);
        Bindings = bindings.ToArray();
        Status = failures.Count == 0 ? "两个快捷键已启用；操作不会切换工作模式。"
            : "设置已保存，但" + string.Join("、", failures) + "未能注册，可能已被系统或其他应用占用。请修改组合键。";
    }

    private bool Register(int id, string action, string label, string text,
        List<RegisteredShortcut> bindings, List<string> failures)
    {
        if (!TranslationShortcut.TryParse(text, out var chord))
        {
            failures.Add(label);
            return false;
        }
        uint modifiers = (chord!.Modifiers.HasFlag(ShortcutModifiers.Alt) ? 1u : 0)
            | (chord.Modifiers.HasFlag(ShortcutModifiers.Control) ? 2u : 0)
            | (chord.Modifiers.HasFlag(ShortcutModifiers.Shift) ? 4u : 0);
        uint key = chord.Key.Length == 1 ? chord.Key[0]
            : 0x6Fu + uint.Parse(chord.Key.AsSpan(1), CultureInfo.InvariantCulture);
        if (!Native.RegisterHotKey(window, id, modifiers | 0x4000, key))
        {
            failures.Add(label);
            return false;
        }
        bindings.Add(new(action, key, chord.Modifiers));
        return true;
    }

    private void Release()
    {
        if (_wakeRegistered)
            Native.UnregisterHotKey(window, WakeId);
        if (_directRegistered)
            Native.UnregisterHotKey(window, DirectId);
        _wakeRegistered = _directRegistered = false;
        Bindings = [];
    }

    public void Dispose() => Release();
}
