using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class WindowsSessionViewTests
{
    [Fact]
    public void ViewSwitchKeepsOneSurfaceAndRejectsOldSurfaceCallbacks()
    {
        var native = new View();
        var browser = new View();
        var router = new WindowsSessionView(native, browser);
        var settings = new UserSettings();
        var session = new Session(settings.Direction);
        int drafts = 0;
        SessionViewToken? returned = null;
        router.DraftChanged = _ => drafts++;
        router.ReturnRequested = token => returned = token;
        var source = Source("native");
        session.Observe(source, FocusMember.Source, 1);
        router.Render(session, settings, false);
        native.DraftChanged!("native draft");
        Assert.Equal(1, drafts);
        source = Source("windows-browser");
        session.Observe(source, FocusMember.Source, 2);
        var token = new SessionViewToken(source.Target, Guid.NewGuid(), 7);
        router.Render(session, settings, false, context: new(token, SessionShortcutBindings.Default, true));
        Assert.Equal(1, native.Hidden);
        Assert.Equal(1, browser.Rendered);
        native.DraftChanged!("stale draft");
        native.ReturnRequested!(token);
        Assert.Equal(1, drafts);
        Assert.Null(returned);
        browser.ReturnRequested!(token);
        Assert.Same(token, returned);
        router.HideQuietly();
        browser.DraftChanged!("hidden draft");
        Assert.Equal(1, drafts);
    }
    private static InputSnapshot Source(string backend) => new(new(backend, "field", Guid.NewGuid()), "source", new(0, 0),
        new(0, 0, 100, 20), false, FieldAccess.Supported, ImeState.Clear, 1, 1, 1);
    private sealed class View : ISessionView
    {
        public int Hidden, Rendered;
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
        public bool EditingComposition => false;
        public void Configure(UserSettings settings)
        {
        }
        public void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null, SessionViewContext? context = null) => Rendered++;
        public void RenderPaused(PauseAnchor anchor, long revision)
        {
        }
        public void HideQuietly() => Hidden++;
        public void ResumeEditor()
        {
        }
    }
}
