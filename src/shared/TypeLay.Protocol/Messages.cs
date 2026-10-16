namespace TypeLay.Protocol;

public sealed record ModelRequest(long Id, string Op, string Text = "", string Source = "auto",
    string Target = "en", string? Preserved = null);
public sealed record ModelResponse(long Id, string Status, string Text = "", string Code = "", double ElapsedMs = 0,
    string[]? Directions = null);
