using System.Globalization;

namespace TypeLay.Core;

// Preferences only, never proof of key ownership or permission to accept text.
// Platform consumption remains separate from parsing and persistence.
public sealed record SessionShortcutBindings
{
    public string Accept
    {
        get;
    }
    public string Exit
    {
        get;
    }
    public static SessionShortcutBindings Default { get; } = new("Tab", "Esc");
    public static IReadOnlyList<string> FunctionKeys
    {
        get;
    } = Array.AsReadOnly(
        Enumerable.Range(1, 24).Select(number => "F" + number.ToString(CultureInfo.InvariantCulture)).ToArray());

    private SessionShortcutBindings(string accept, string exit) => (Accept, Exit) = (accept, exit);

    public static bool TryParse(string? accept, string? exit, out SessionShortcutBindings? bindings)
    {
        bindings = null;
        string? parsedAccept = ParseKey(accept, "Tab"), parsedExit = ParseKey(exit, "Esc");
        if (parsedAccept is null || parsedExit is null || SameMainKey(parsedAccept, parsedExit))
            return false;
        bindings = new(parsedAccept, parsedExit);
        return true;
    }

    // Repair only the damaged preference on load. A duplicate pair preserves
    // acceptance and restores exit; neither default can collide with an F key.
    public static SessionShortcutBindings Restore(string? accept, string? exit)
    {
        string parsedAccept = ParseKey(accept, "Tab") ?? "Tab";
        string parsedExit = ParseKey(exit, "Esc") ?? "Esc";
        return new(parsedAccept, SameMainKey(parsedAccept, parsedExit) ? "Esc" : parsedExit);
    }

    // The current native route owns one action per physical function key.
    // A shared preference must not save a pair the staged route cannot admit.
    private static bool SameMainKey(string first, string second) =>
        (first.StartsWith("Ctrl+", StringComparison.Ordinal) ? first[5..] : first)
        == (second.StartsWith("Ctrl+", StringComparison.Ordinal) ? second[5..] : second);

    private static string? ParseKey(string? text, string defaultKey)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16)
            return null;
        text = text.Trim();
        if (text.Equals(defaultKey, StringComparison.OrdinalIgnoreCase))
            return defaultKey;
        bool control = text.StartsWith("Ctrl+", StringComparison.OrdinalIgnoreCase);
        string keyText = control ? text[5..] : text;
        string? key = FunctionKeys.FirstOrDefault(key => key.Equals(keyText, StringComparison.OrdinalIgnoreCase));
        return key is null ? null : control ? "Ctrl+" + key : key;
    }
}
