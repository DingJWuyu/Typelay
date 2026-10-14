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
public sealed record SessionViewContext(SessionViewToken Token, SessionShortcutBindings Bindings, bool CanEdit);

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
    Action? RegenerateRequested
    {
        get; set;
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
    void RenderPaused(PauseAnchor anchor, long revision);
    void HideQuietly();
    void ResumeEditor();
}

public interface ITranslationService
{
    bool Ready
    {
        get;
    }
    bool Busy
    {
        get;
    }
    Task<ModelResponse> TranslateAsync(TranslationJob job);
}
