using TypeLay.Core;
using TypeLay.Protocol;

namespace TypeLay.Runtime;

// All callbacks and continuations execute on the host's serial UI/event loop.
// Start/Stop control the heartbeat only; no thread or dispatcher is created by the runtime.
public interface ISessionLoop
{
    long NowMs
    {
        get;
    }
    event Action? Tick;
    void Start();
    void Stop();
}

// Commands return the identity actually displayed, never a freshly substituted
// token. Revision belongs to the controller's session lifecycle, not repainting.
public sealed record SessionViewToken(TargetId Target, Guid CommandGeneration, long Revision);
public sealed record SessionViewContext(SessionViewToken Token, SessionShortcutBindings Bindings, bool CanEdit,
    TranslationMenuState? TranslationActions = null);

public interface ISessionView
{
    Func<SessionViewToken, bool>? BeginEditing
    {
        get; set;
    }
    Action<string>? DraftChanged
    {
        get; set;
    }
    Action<SessionViewToken>? ReturnRequested
    {
        get; set;
    }
    Action<SessionViewToken>? EscapeRequested
    {
        get; set;
    }
    Action<SessionViewToken>? AlternativeRequested
    {
        get; set;
    }
    Func<SessionStyleRequest, Task<StyleChangeResult>>? StyleRequested
    {
        get; set;
    }
    Action? RegenerateRequested
    {
        get; set;
    }
    Func<SessionViewToken, SessionGlossaryEntry?>? BeginGlossaryRequested
    {
        get; set;
    }
    Func<SessionGlossaryRequest, Task<GlossaryChangeResult>>? AddGlossaryRequested
    {
        get; set;
    }
    Action<Guid>? CloseGlossaryRequested
    {
        get; set;
    }
    SessionGlossaryFocus? GlossaryEntryFocus
    {
        get;
    }
    // Capability is presentation only. Opening and returning still require the
    // original source/form identity and actual focus at the platform seam.
    bool SupportsSourceGlossaryEntry(SessionViewToken token) => false;
    Task<bool> ReturnGlossaryToSourceAsync(SessionGlossaryEntry entry) => Task.FromResult(false);
    Action? ExcludeRequested
    {
        get; set;
    }
    // Return the exact revision from RenderPaused. The controller owns resume
    // validation and setting changes; the view must never save cached settings.
    Action<long>? ResumeRequested
    {
        get; set;
    }
    bool EditingComposition
    {
        get;
    }
    void Configure(UserSettings settings);
    void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null, SessionViewContext? context = null);
    void RenderPaused(PauseAnchor anchor, long revision);
    void HideQuietly();
    void ResumeEditor();
    // True only during the exact editor-exit callback after the adapter has
    // pre-armed its owned return. It grants no capture or write authority.
    bool IsEditorExitPrearmed(SessionViewToken token) => false;
}

// A negotiated protocol feature for one worker connection, not a quality claim.
public sealed record StyledTranslationCapability(long Revision);
public sealed record GlossaryTranslationCapability(long Revision);

public interface ITranslationService
{
    VerifiedModelIdentity? VerifiedIdentity => null;
    StyledTranslationCapability? StyledTranslationCapability => null;
    GlossaryTranslationCapability? GlossaryTranslationCapability => null;
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
    internal VerifiedModelIdentity(ModelLoadPlan plan)
    {
        Packages = Array.AsReadOnly(plan.ActivePackages.Select(item => new VerifiedModelPackage(item)).ToArray());
        // Legacy alternative operation has no versioned hello feature. Its
        // compatibility rule belongs to the verified model projection, not UI
        // or session orchestration. This is technical support, not a quality grant.
        AlternativeTranslationAvailable = Packages.Count > 0 && Packages.All(package =>
            package.Version == "1.9" && package.Id is "argos-zh-en" or "argos-en-zh");
    }
    public Guid ConnectionId { get; } = Guid.NewGuid();
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
    Style, Glossary, Alternative
}
public sealed record TranslationRelease(TranslationOperation Operation, string PackageId, string Version, string Direction);

// A compiled product decision, not hello negotiation or a configurable flag system.
// This release has no accepted quality rows. Constructor injection is for controlled tests.
public sealed class TranslationReleasePolicy(IEnumerable<TranslationRelease> releases)
{
    private readonly TranslationRelease[] _releases = releases.ToArray();
    public static TranslationReleasePolicy Current { get; } = new([]);
    public bool IsReleased(TranslationOperation operation) => _releases.Any(row => row.Operation == operation);
    public bool Allows(TranslationOperation operation, VerifiedModelIdentity identity, LanguageDirection direction)
    {
        var possible = identity.Packages.SelectMany(package => package.Directions
            .Where(pair => pair.Target == direction.Target && (direction.Source == "auto" || pair.Source == direction.Source))
            .Select(pair => new TranslationRelease(operation, package.Id, package.Version, pair.Id))).ToArray();
        // Auto cannot borrow acceptance from a single direction. No new detection RPC.
        return possible.Length > 0 && possible.All(row => _releases.Contains(row));
    }
}
public sealed record AlternativeState(SessionViewToken? Session, bool CanRequest, string? UnavailableReason, bool IsReleased);
