using TypeLay.Core;

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
    TranslationMenuState? TranslationActions = null, PresentationIdentity? Identity = null,
    SessionFormPresentation? GlossaryForm = null);

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
    // Optional browser-editing path. Legacy/native Regenerate keeps its existing contract.
    Func<EditorRegenerateRequest, Task<bool>>? EditorRegenerateRequested
    {
        get => null;
        set
        {
        }
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
    void RenderPaused(PauseAnchor anchor, long revision, PresentationIdentity? identity = null);
    void HideQuietly();
    void ResumeEditor();
}

// Native interaction evidence is supplied separately from presentation. None
// of these observations grant capture or write authority to the renderer.
public interface ISourceInteraction
{
    bool SupportsSourceGlossaryEntry(SessionViewToken token) => false;
    // Only this exact form's native source/auxiliary foreground transfer may
    // preserve its window identity. Global input epochs are never rewritten.
    bool IsGlossarySourceWindowCurrent(SessionGlossaryEntry entry, long original, long current) => original == current;
    // A pending no-write return keeps its original bounded platform authority.
    // It is not source focus and ends on completion, expiry or identity change.
    bool IsGlossarySourceReturnPending(SessionGlossaryEntry entry) => false;
    Task<bool> ReturnGlossaryToSourceAsync(SessionGlossaryEntry entry) => Task.FromResult(false);
    // True only during the exact editor-exit callback after the adapter has
    // pre-armed its owned return. It grants no capture or write authority.
    bool IsEditorExitPrearmed(SessionViewToken token) => false;
}

internal sealed class UnavailableSourceInteraction : ISourceInteraction
{
    internal static ISourceInteraction Instance { get; } = new UnavailableSourceInteraction();
}

public sealed record AlternativeState(SessionViewToken? Session, bool CanRequest, string? UnavailableReason, bool IsReleased);
