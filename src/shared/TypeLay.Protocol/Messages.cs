using System.Text.Json.Serialization;

namespace TypeLay.Protocol;

public sealed record ModelRequest(long Id, string Op, string Text = "", string Source = "auto",
    string Target = "en", string? Preserved = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousCandidate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Style = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ModelGlossary? Glossary = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoadedModelPackage(string Id, string Version, string[] Directions);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelResponse(long Id, string Status, string Text = "", string Code = "", double ElapsedMs = 0,
    string[]? Directions = null, LoadedModelPackage[]? Packages = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonNumberHandling(JsonNumberHandling.Strict)]
    int? StyledTranslationVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonNumberHandling(JsonNumberHandling.Strict)]
    int? GlossaryTranslationVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonNumberHandling(JsonNumberHandling.Strict)]
    int? StyledGlossaryTranslationVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string[]? AdvancedTranslationDirections = null);

public sealed record ModelGlossary(long Version, ModelGlossaryRule[] Rules);
public sealed record ModelGlossaryRule(string SourceTerm, string TargetTerm,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceLanguage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetLanguage = null);
