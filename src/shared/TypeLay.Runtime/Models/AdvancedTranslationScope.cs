using TypeLay.Core;

namespace TypeLay.Runtime;

// Technical operation scope for one verified connection, never a quality grant.
public sealed class AdvancedTranslationScope
{
    private readonly ModelDirection[] _selected;
    private readonly HashSet<string> _advanced;

    public AdvancedTranslationScope(IEnumerable<ModelDirection> selectedDirections, IEnumerable<string> advancedDirections)
    {
        ArgumentNullException.ThrowIfNull(selectedDirections);
        ArgumentNullException.ThrowIfNull(advancedDirections);
        _selected = selectedDirections.ToArray();
        string[] advanced = advancedDirections.ToArray();
        _advanced = advanced.ToHashSet(StringComparer.Ordinal);
        if (_selected.Length == 0 || _selected.Any(direction => direction is null)
            || _selected.Select(direction => direction.Id).Distinct(StringComparer.Ordinal).Count() != _selected.Length
            || advanced.Length == 0 || advanced.Any(string.IsNullOrEmpty) || _advanced.Count != advanced.Length
            || !_advanced.IsSubsetOf(_selected.Select(direction => direction.Id)))
            throw new ArgumentException("advanced-translation-scope");
    }

    public bool Supports(LanguageDirection direction)
    {
        var possible = _selected.Where(pair => pair.Target == direction.Target
            && (direction.Source == "auto" || pair.Source == direction.Source)).ToArray();
        // Auto cannot borrow support from just one possible source language.
        return possible.Length > 0 && possible.All(pair => _advanced.Contains(pair.Id));
    }
}
