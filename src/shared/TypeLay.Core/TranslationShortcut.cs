using System.Globalization;

namespace TypeLay.Core;

[Flags]
public enum ShortcutModifiers
{
    None = 0, Control = 1, Alt = 2, Shift = 4
}

/// <summary>A configurable command chord; never a bare typing, Tab, Escape or Windows key.</summary>
public sealed record TranslationShortcut(ShortcutModifiers Modifiers, string Key)
{
    public static bool TryParse(string? text, out TranslationShortcut? shortcut)
    {
        shortcut = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
            return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 4)
            return false;
        ShortcutModifiers modifiers = ShortcutModifiers.None;
        foreach (string part in parts[..^1])
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" => ShortcutModifiers.Control,
                "ALT" => ShortcutModifiers.Alt,
                "SHIFT" => ShortcutModifiers.Shift,
                _ => ShortcutModifiers.None
            };
            if (modifier == ShortcutModifiers.None || modifiers.HasFlag(modifier))
                return false;
            modifiers |= modifier;
        }
        if ((modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt)) == 0)
            return false;
        string key = parts[^1].ToUpperInvariant();
        bool character = key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9');
        bool function = key.StartsWith('F') && int.TryParse(key.AsSpan(1), NumberStyles.None,
            CultureInfo.InvariantCulture, out int number) && number is >= 1 and <= 24 and not 12
            && key == "F" + number.ToString(CultureInfo.InvariantCulture);
        if (!character && !function)
            return false;
        shortcut = new(modifiers, key);
        return true;
    }

    public override string ToString() => (Modifiers.HasFlag(ShortcutModifiers.Control) ? "Ctrl+" : "")
        + (Modifiers.HasFlag(ShortcutModifiers.Alt) ? "Alt+" : "")
        + (Modifiers.HasFlag(ShortcutModifiers.Shift) ? "Shift+" : "") + Key;
}
