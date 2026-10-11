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

public interface ISessionView
{
    Func<bool>? BeginEditing
    {
        get; set;
    }
    Action<string>? DraftChanged
    {
        get; set;
    }
    Action? ReturnRequested
    {
        get; set;
    }
    Action? EscapeRequested
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
    bool EditingComposition
    {
        get;
    }
    void Configure(UserSettings settings);
    void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null);
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
