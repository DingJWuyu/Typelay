using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed class WindowsSessionView : ISessionView
{
    private readonly ISessionView _native, _browser;
    private ISessionView? _active;
    public WindowsSessionView(ISessionView native, ISessionView browser)
    {
        _native = native;
        _browser = browser;
        foreach (var view in new[] { native, browser })
        {
            view.BeginEditing = token => ReferenceEquals(_active, view) && BeginEditing?.Invoke(token) == true;
            view.DraftChanged = text => { if (ReferenceEquals(_active, view)) DraftChanged?.Invoke(text); };
            view.ReturnRequested = token => { if (ReferenceEquals(_active, view)) ReturnRequested?.Invoke(token); };
            view.EscapeRequested = token => { if (ReferenceEquals(_active, view)) EscapeRequested?.Invoke(token); };
            view.RegenerateRequested = () => { if (ReferenceEquals(_active, view)) RegenerateRequested?.Invoke(); };
            view.AlternativeRequested = token => { if (ReferenceEquals(_active, view)) AlternativeRequested?.Invoke(token); };
            view.StyleRequested = request => ReferenceEquals(_active, view) && StyleRequested is { } change
                ? change(request) : Task.FromResult(new StyleChangeResult(false, "style-stale"));
            view.BeginGlossaryRequested = token => ReferenceEquals(_active, view) ? BeginGlossaryRequested?.Invoke(token) : null;
            view.AddGlossaryRequested = request => ReferenceEquals(_active, view) && AddGlossaryRequested is { } add
                ? add(request) : Task.FromResult(new GlossaryChangeResult(false, "glossary-stale"));
            view.CloseGlossaryRequested = formId => { if (ReferenceEquals(_active, view)) CloseGlossaryRequested?.Invoke(formId); };
            view.ExcludeRequested = () => { if (ReferenceEquals(_active, view)) ExcludeRequested?.Invoke(); };
            view.ResumeRequested = revision => { if (ReferenceEquals(_active, view)) ResumeRequested?.Invoke(revision); };
        }
    }
    public Func<SessionViewToken, bool>? BeginEditing
    {
        get; set;
    }
    public Action<string>? DraftChanged
    {
        get; set;
    }
    public Action<SessionViewToken>? ReturnRequested
    {
        get; set;
    }
    public Action<SessionViewToken>? EscapeRequested
    {
        get; set;
    }
    public Action? RegenerateRequested
    {
        get; set;
    }
    public Action<SessionViewToken>? AlternativeRequested
    {
        get; set;
    }
    public Func<SessionStyleRequest, Task<StyleChangeResult>>? StyleRequested
    {
        get; set;
    }
    public Func<SessionViewToken, SessionGlossaryEntry?>? BeginGlossaryRequested
    {
        get; set;
    }
    public Func<SessionGlossaryRequest, Task<GlossaryChangeResult>>? AddGlossaryRequested
    {
        get; set;
    }
    public Action<Guid>? CloseGlossaryRequested
    {
        get; set;
    }
    public SessionGlossaryFocus? GlossaryEntryFocus => _active?.GlossaryEntryFocus;
    public bool SupportsSourceGlossaryEntry(SessionViewToken token) =>
        _active?.SupportsSourceGlossaryEntry(token) == true;
    public bool IsGlossarySourceWindowCurrent(SessionGlossaryEntry entry, long original, long current) =>
        _active?.IsGlossarySourceWindowCurrent(entry, original, current) == true;
    public Task<bool> ReturnGlossaryToSourceAsync(SessionGlossaryEntry entry) =>
        _active?.ReturnGlossaryToSourceAsync(entry) ?? Task.FromResult(false);
    public Action? ExcludeRequested
    {
        get; set;
    }
    public Action<long>? ResumeRequested
    {
        get; set;
    }
    public bool EditingComposition => _active?.EditingComposition == true;
    public void Configure(UserSettings settings)
    {
        _native.Configure(settings);
        _browser.Configure(settings);
    }
    private ISessionView Select(TargetId target)
    {
        var next = BrowserInput.Owns(target) ? _browser : _native;
        if (!ReferenceEquals(_active, next))
        {
            _active?.HideQuietly();
            _active = next;
        }
        return next;
    }
    public void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null, SessionViewContext? context = null)
    {
        if (session.Current is not { } source)
        {
            HideQuietly();
            return;
        }
        Select(source.Target).Render(session, settings, canAccept, statusOverride, context);
    }
    public void RenderPaused(PauseAnchor anchor, long revision) => Select(anchor.Target).RenderPaused(anchor, revision);
    public void HideQuietly()
    {
        _native.HideQuietly();
        _browser.HideQuietly();
        _active = null;
    }
    public void ResumeEditor() => _active?.ResumeEditor();
    public bool IsEditorExitPrearmed(SessionViewToken token) => _active?.IsEditorExitPrearmed(token) == true;
}
