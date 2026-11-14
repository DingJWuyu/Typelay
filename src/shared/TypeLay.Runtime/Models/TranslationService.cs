using TypeLay.Core;
using TypeLay.Protocol;

namespace TypeLay.Runtime;

// A negotiated protocol feature for one worker connection, not a quality claim.
public sealed record StyledTranslationCapability(long Revision, AdvancedTranslationScope? Scope = null)
{
    public bool Supports(LanguageDirection direction) => Scope?.Supports(direction) ?? true;
}
public sealed record GlossaryTranslationCapability(long Revision, AdvancedTranslationScope? Scope = null)
{
    public bool Supports(LanguageDirection direction) => Scope?.Supports(direction) ?? true;
}
public sealed record StyledGlossaryTranslationCapability(long Revision, AdvancedTranslationScope? Scope = null)
{
    public bool Supports(LanguageDirection direction) => Scope?.Supports(direction) ?? true;
}

public interface ITranslationService
{
    VerifiedModelIdentity? VerifiedIdentity => null;
    StyledTranslationCapability? StyledTranslationCapability => null;
    GlossaryTranslationCapability? GlossaryTranslationCapability => null;
    StyledGlossaryTranslationCapability? StyledGlossaryTranslationCapability => null;
    bool Ready
    {
        get;
    }
    bool Busy
    {
        get;
    }
    Task<ModelResponse> TranslateAsync(TranslationJob job);
    // Special operations must bind dispatch to the exact qualified live connection.
    // An adapter that has not implemented this atomic binding fails closed.
    Task<ModelResponse> TranslateAsync(TranslationJob job, VerifiedModelIdentity expectedIdentity) =>
        Task.FromResult(new ModelResponse(0, "error", Code: "not-ready"));
}

// Created only after this connection's strict hello matched its immutable load plan.
// A legacy hello without verified package versions grants no such projection.
public sealed class VerifiedModelIdentity
{
    internal VerifiedModelIdentity(ModelLoadPlan plan, bool indexAlternative = false, string? inferenceProfileId = null)
    {
        InferenceProfileId = inferenceProfileId;
        Packages = Array.AsReadOnly(plan.ActivePackages.Select(item => new VerifiedModelPackage(item)).ToArray());
        // Legacy alternative operation has no versioned hello feature. Its
        // compatibility rule belongs to the verified model projection, not UI
        // or session orchestration. This is technical support, not a quality grant.
        AlternativeTranslationAvailable = plan.UsesIndex && indexAlternative
            || Packages.Count > 0 && Packages.All(package =>
                package.Version == "1.9" && package.Id is "argos-zh-en" or "argos-en-zh");
    }
    public Guid ConnectionId { get; } = Guid.NewGuid();
    // Independently checked launch inputs, then this owned connection's strict
    // hello. Never copied from a worker-reported string or a previous connection.
    public string? InferenceProfileId
    {
        get;
    }
    public IReadOnlyList<VerifiedModelPackage> Packages
    {
        get;
    }
    public bool AlternativeTranslationAvailable
    {
        get;
    }
}
public sealed class VerifiedModelPackage
{
    internal VerifiedModelPackage(ModelPackageSelection selected)
    {
        Id = selected.Package.Id;
        Version = selected.Package.Version;
        Directions = Array.AsReadOnly(selected.Directions.ToArray());
    }
    public string Id
    {
        get;
    }
    public string Version
    {
        get;
    }
    public IReadOnlyList<ModelDirection> Directions
    {
        get;
    }
}
public enum TranslationOperation
{
    Style, Glossary, Alternative, StyledGlossary
}
public sealed record TranslationRelease(TranslationOperation Operation, string PackageId, string Version, string Direction,
    string? InferenceProfileId = null);

// A compiled product decision, not hello negotiation or a configurable flag system.
// Only these operation/direction/profile rows have accepted feature-delta evidence.
// Constructor injection is for controlled tests.
public sealed class TranslationReleasePolicy(IEnumerable<TranslationRelease> releases)
{
    private readonly TranslationRelease[] _releases = releases.ToArray();
    public static TranslationReleasePolicy Current
    {
        get;
    } = new([
        new(TranslationOperation.Glossary, "index-translate-2b-q4-k-m", "3a0eca6ce931d88d00614d427ab3ed8ff0743902", "zh_en",
            "index-v1-d1ba1b7b9a3e78434e49767a43586a097ce40ef7607d170f1097cfae67c95180"),
        new(TranslationOperation.Glossary, "index-translate-2b-q4-k-m", "3a0eca6ce931d88d00614d427ab3ed8ff0743902", "en_zh",
            "index-v1-d1ba1b7b9a3e78434e49767a43586a097ce40ef7607d170f1097cfae67c95180")
    ]);
    public bool IsReleased(TranslationOperation operation) => _releases.Any(row => row.Operation == operation);
    public bool Allows(TranslationOperation operation, VerifiedModelIdentity identity, LanguageDirection direction)
    {
        var possible = identity.Packages.SelectMany(package => package.Directions
            .Where(pair => pair.Target == direction.Target && (direction.Source == "auto" || pair.Source == direction.Source))
            .Select(pair => new TranslationRelease(operation, package.Id, package.Version, pair.Id,
                package.Id == "index-translate-2b-q4-k-m" ? identity.InferenceProfileId : null))).ToArray();
        // Auto cannot borrow acceptance from a single direction. No new detection RPC.
        return possible.Length > 0 && possible.All(row =>
            (row.PackageId != "index-translate-2b-q4-k-m" || row.InferenceProfileId is not null) && _releases.Contains(row));
    }
}
