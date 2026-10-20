using System.Globalization;
using System.Windows.Interop;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.App;

// A short synchronous preflight only. The message-only window has no command
// handler and is destroyed before returning; this never changes saved bindings
// or grants the input hook ownership. Registration may still fail after saving.
internal static class ShortcutConflictProbe
{
    public static IReadOnlyList<string> Inspect(ShortcutSettingsDraft draft,
        IReadOnlyList<RegisteredShortcut>? owned = null)
    {
        using var window = new HwndSource(new HwndSourceParameters("TypeLay shortcut check")
        {
            ParentWindow = new nint(-3), // HWND_MESSAGE; never foreground or visible.
            WindowStyle = 0,
            Width = 0,
            Height = 0
        });
        var failures = new List<string>();
        int id = 0x53A0;
        foreach (string text in new[] { draft.Wake, draft.Direct })
        {
            if (!TranslationShortcut.TryParse(text, out var chord))
                continue; // The settings draft validates syntax before this call.
            uint key = chord!.Key.Length == 1 ? chord.Key[0]
                : 0x6Fu + uint.Parse(chord.Key.AsSpan(1), CultureInfo.InvariantCulture);
            if (owned?.Any(binding => binding.Key == key && binding.Modifiers == chord.Modifiers) == true)
                continue;
            uint modifiers = (chord.Modifiers.HasFlag(ShortcutModifiers.Alt) ? 1u : 0)
                | (chord.Modifiers.HasFlag(ShortcutModifiers.Control) ? 2u : 0)
                | (chord.Modifiers.HasFlag(ShortcutModifiers.Shift) ? 4u : 0) | 0x4000u;
            ++id;
            if (!Native.RegisterHotKey(window.Handle, id, modifiers, key))
                failures.Add(text);
            else if (!Native.UnregisterHotKey(window.Handle, id))
                failures.Add(text); // Do not represent an unconfirmed release as success.
        }
        return failures;
    }
}
