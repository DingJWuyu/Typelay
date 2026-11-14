using System.Text.Json;

namespace TypeLay.App;

internal sealed record BrowserCommandSource(Guid ConnectionId, BrowserRoute Route, nint Root,
    long WindowEpoch, Guid Generation, JsonElement Authority);

// A rejected preparation is only a comparison basis. A later verified source
// change can consume it once; neither this record nor its old ACK grants a key.
internal sealed class BrowserCommandRecovery
{
    private BrowserCommandSource? _rejected;
    public void Remember(BrowserCommandSource source) => _rejected = source;
    public void Clear() => _rejected = null;
    public bool TryTakeChangedSource(BrowserCommandSource current, out BrowserCommandSource? rejected)
    {
        rejected = null;
        var previous = _rejected;
        if (previous is null || previous.ConnectionId != current.ConnectionId || previous.Route != current.Route
            || previous.Root != current.Root || previous.WindowEpoch != current.WindowEpoch || previous.Generation != current.Generation
            || !BrowserInput.ValidAuthority(previous.Authority) || !BrowserInput.ValidAuthority(current.Authority)
            || BrowserInput.String(previous.Authority, "target") != BrowserInput.String(current.Authority, "target")
            || BrowserInput.String(previous.Authority, "token") == BrowserInput.String(current.Authority, "token")
            || new[] { "contentRevision", "inputRevision", "focusRevision", "observedAtMs" }
                .Any(name => BrowserInput.Number(current.Authority, name) < BrowserInput.Number(previous.Authority, name))
            || BrowserInput.SameAuthority(previous.Authority, current.Authority))
            return false;
        rejected = previous;
        _rejected = null;
        return true;
    }
}
