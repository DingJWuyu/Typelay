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
    [Fact]
    public async Task TranslationActionsReachOnlyTheActiveSurfaceWithTheirOriginalContext()
    {
        var native = new View();
        var browser = new View();
        var router = new WindowsSessionView(native, browser);
        var settings = new UserSettings();
        var session = new Session(settings.Direction);
        var styles = new List<SessionStyleRequest>();
        var alternatives = new List<SessionViewToken>();
        router.StyleRequested = request =>
        {
            styles.Add(request);
            return Task.FromResult(new StyleChangeResult(true, "saved"));
        };
        router.AlternativeRequested = alternatives.Add;
        var source = Source("native");
        session.Observe(source, FocusMember.Source, 1);
        router.Render(session, settings, false);
        var original = new SessionViewToken(source.Target, Guid.NewGuid(), 7);
        var request = new SessionStyleRequest(original, 12, TranslationStyle.Formal);
        Assert.True((await native.StyleRequested!(request)).Saved);
        native.AlternativeRequested!(original);
        Assert.Equal(request, Assert.Single(styles));
        Assert.Equal(original, Assert.Single(alternatives));
        source = Source("windows-browser");
        session.Observe(source, FocusMember.Source, 2);
        router.Render(session, settings, false);
        Assert.False((await native.StyleRequested!(request)).Saved);
        native.AlternativeRequested!(original);
        Assert.Single(styles);
        Assert.Single(alternatives);
        var browserToken = new SessionViewToken(source.Target, Guid.NewGuid(), 8);
        var inherit = new SessionStyleRequest(browserToken, 13, null);
        Assert.True((await browser.StyleRequested!(inherit)).Saved);
        browser.AlternativeRequested!(browserToken);
        Assert.Equal(inherit, styles[1]);
        Assert.Equal(browserToken, alternatives[1]);
        router.HideQuietly();
        Assert.False((await browser.StyleRequested!(inherit)).Saved);
        browser.AlternativeRequested!(browserToken);
        Assert.Equal(2, styles.Count);
        Assert.Equal(2, alternatives.Count);
    }

    [Fact]
    public async Task AuxiliaryEntryAndFocusBelongOnlyToTheActiveSurface()
    {
        var native = new View();
        var browser = new View();
        var router = new WindowsSessionView(native, browser);
        var session = new Session(new("auto", "en"));
        var source = Source("native");
        session.Observe(source, FocusMember.Source, 1);
        var token = new SessionViewToken(source.Target, Guid.NewGuid(), 7);
        var entry = new SessionGlossaryEntry(Guid.NewGuid(), token, 4, []);
        var request = new SessionGlossaryRequest(entry.FormId, token, 4, new("PR", "PR"));
        int saves = 0, closes = 0;
        router.BeginGlossaryRequested = _ => entry;
        router.AddGlossaryRequested = _ => { saves++; return Task.FromResult(new GlossaryChangeResult(true, "glossary-saved")); };
        router.CloseGlossaryRequested = _ => closes++;
        router.Render(session, new(), false);
        native.GlossaryEntryFocus = new(entry.FormId, token, ImeState.Composing);
        Assert.Equal(native.GlossaryEntryFocus, router.GlossaryEntryFocus);
        Assert.Same(entry, native.BeginGlossaryRequested!(token));
        Assert.True((await native.AddGlossaryRequested!(request)).Saved);
        session.Observe(Source("windows-browser"), FocusMember.Source, 2);
        router.Render(session, new(), false);
        Assert.Null(router.GlossaryEntryFocus);
        Assert.Null(native.BeginGlossaryRequested!(token));
        Assert.False((await native.AddGlossaryRequested!(request)).Saved);
        native.CloseGlossaryRequested!(entry.FormId);
        Assert.Equal(1, saves);
        Assert.Equal(0, closes);
        browser.CloseGlossaryRequested!(entry.FormId);
        Assert.Equal(1, closes);
        router.HideQuietly();
        Assert.Null(browser.BeginGlossaryRequested!(token));
        Assert.False((await browser.AddGlossaryRequested!(request)).Saved);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task SourceGlossaryCapabilityAndReturnBelongOnlyToTheActiveBrowserSurface()
    {
        var native = new View();
        var browser = new View { SourceGlossarySupported = true };
        ISessionView router = new WindowsSessionView(native, browser);
        var session = new Session(new("auto", "en"));
        var source = Source("windows-browser");
        session.Observe(source, FocusMember.Source, 1);
        var token = new SessionViewToken(source.Target, Guid.NewGuid(), 7);
        var entry = new SessionGlossaryEntry(Guid.NewGuid(), token, 4, [], SourceOrigin: true);
        Assert.False(router.SupportsSourceGlossaryEntry(token));
        Assert.False(await router.ReturnGlossaryToSourceAsync(entry));
        router.Render(session, new(), false);
        Assert.True(router.SupportsSourceGlossaryEntry(token));
        Assert.True(await router.ReturnGlossaryToSourceAsync(entry));
        Assert.Same(entry, browser.ReturnedGlossary);
        session.Observe(Source("native"), FocusMember.Source, 2);
        router.Render(session, new(), false);
        Assert.False(router.SupportsSourceGlossaryEntry(token));
        Assert.False(await router.ReturnGlossaryToSourceAsync(entry));
        router.HideQuietly();
        Assert.False(router.SupportsSourceGlossaryEntry(token));
        Assert.False(await router.ReturnGlossaryToSourceAsync(entry));
    }

    private static InputSnapshot Source(string backend) => new(new(backend, "field", Guid.NewGuid()), "source", new(0, 0),
        new(0, 0, 100, 20), false, FieldAccess.Supported, ImeState.Clear, 1, 1, 1);
    private sealed class View : ISessionView
    {
        public int Hidden, Rendered;
        public bool SourceGlossarySupported;
        public SessionGlossaryEntry? ReturnedGlossary;
        public bool SupportsSourceGlossaryEntry(SessionViewToken token) => SourceGlossarySupported;
        public Task<bool> ReturnGlossaryToSourceAsync(SessionGlossaryEntry entry)
        {
            ReturnedGlossary = entry;
            return Task.FromResult(SourceGlossarySupported);
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
        public SessionGlossaryFocus? GlossaryEntryFocus
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
