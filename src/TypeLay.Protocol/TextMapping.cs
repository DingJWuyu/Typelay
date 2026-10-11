using TypeLay.Core;
namespace TypeLay.Protocol;

// UIA character units are never interpreted as UTF-16 offsets. Prefix strings prove the mapping.
public static class TextMapping
{
    public static string Canonical(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    public static int? ToSourceOffset(string source, string canonicalPrefix)
    {
        int c = 0, i = 0;
        while (i < source.Length && c < canonicalPrefix.Length)
        {
            char actual = source[i++];
            if (actual == '\r') { if (i < source.Length && source[i] == '\n') i++; actual = '\n'; }
            if (actual != canonicalPrefix[c++]) return null;
        }
        return c == canonicalPrefix.Length && Utf16.IsBoundary(source, i) ? i : null;
    }
    public static TextRange? Selection(string source, string document, string prefix, string selected)
    {
        if (Canonical(source) != Canonical(document)) return null;
        string p = Canonical(prefix), s = Canonical(selected);
        int? start = ToSourceOffset(source, p), end = ToSourceOffset(source, p + s);
        if (start is null || end is null) return null;
        var result = new TextRange(start.Value, end.Value);
        return result.IsValid(source) && Canonical(source[result.Start..result.End]) == s ? result : null;
    }
}
