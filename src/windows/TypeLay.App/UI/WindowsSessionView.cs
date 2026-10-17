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
