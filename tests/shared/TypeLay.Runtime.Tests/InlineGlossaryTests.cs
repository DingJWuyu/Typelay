using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class InlineGlossaryTests
{
    private static SessionScenario Editing(GlossarySnapshot? initial = null)
    {
        var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true,
            initialGlossary: initial);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        return s;
    }
    private static SessionGlossaryEntry Open(SessionScenario s)
    {
        var entry = Assert.IsType<SessionGlossaryEntry>(s.View.BeginGlossaryRequested!(s.View.Token));
        s.View.GlossaryEntryFocus = new(entry.FormId, entry.Token, ImeState.Clear);
        return entry;
    }
    private static Task<GlossaryChangeResult> Add(SessionScenario s, SessionGlossaryEntry entry, string target = "fixed") =>
        s.View.AddGlossaryRequested!(new(entry.FormId, entry.Token, entry.SettingsRevision, new("hello", target)));

    [Fact]
    public async Task ExplicitAddNeedsNoChangedCharacterAndPreservesDraftAndPermission()
    {
        using var s = Editing();
        string draft = s.View.Draft;
        var token = s.View.Token;
        var entry = Open(s);
        Assert.NotEmpty(entry.Directions);
        Assert.False(s.View.Context!.TranslationActions!.CanOpenGlossary);
        var result = await Add(s, entry);
        Assert.True(result.Saved);
        Assert.Equal("fixed", Assert.Single(s.ReloadSettings().Glossary!.Rules).TargetTerm);
        Assert.Equal(draft, s.View.Draft);
        Assert.Equal(token, s.View.Token);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        long revision = s.Settings.Revision;
        Assert.Equal(result, await Add(s, entry));
        Assert.Equal(revision, s.Settings.Revision);
        Assert.False((await Add(s, entry, "other")).Saved);
    }

    [Theory]
    [InlineData("focus")]
    [InlineData("ime")]
    [InlineData("source")]
    [InlineData("close")]
    [InlineData("settings")]
    public async Task OriginalFormRejectsInvalidatedContextWithoutSaving(string change)
    {
        using var s = Editing();
        var entry = Open(s);
        switch (change)
        {
            case "focus":
                s.View.GlossaryEntryFocus = null;
                break;
            case "ime":
                s.View.GlossaryEntryFocus = new(entry.FormId, entry.Token, ImeState.Composing);
                break;
            case "source":
                s.Input.SourceUserEpoch++;
                break;
            case "close":
                s.View.CloseGlossaryRequested!(entry.FormId);
                break;
            case "settings":
                s.Settings.Update(s.Settings.Value with
                {
                    Theme = "dark"
                });
                break;
        }
        Assert.False((await Add(s, entry)).Saved);
        Assert.Null(s.ReloadSettings().Glossary);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public async Task ExactDuplicateIsQualifiedButDoesNotWriteAgain()
    {
        using var s = Editing(new(1, [new("hello", "fixed")]));
        var entry = Open(s);
        long revision = s.Settings.Revision;
        s.BlockSettingsWrites();
        Assert.Equal("glossary-unchanged", (await Add(s, entry)).Code);
        Assert.Equal(revision, s.Settings.Revision);
        s.AllowSettingsWrites();
        s.View.CloseGlossaryRequested!(entry.FormId);
        var next = Open(s);
        s.Input.SourceUserEpoch++;
        Assert.False((await Add(s, next)).Saved);
        Assert.Equal(revision, s.Settings.Revision);
    }

    [Fact]
    public void ReturnAndReenterRetiresTheOldFormWithoutClientClose()
    {
        using var s = Editing();
        var old = Open(s);
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        var next = Open(s);
        Assert.NotEqual(old.FormId, next.FormId);
        s.View.CloseGlossaryRequested!(old.FormId);
        Assert.Null(s.View.BeginGlossaryRequested!(next.Token));
    }

    [Fact]
    public async Task AuxiliarySaveDoesNotRequireTheRetiredEditorShortcutAcknowledgement()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        // A legitimate capture may begin while focus is still transferring
        // into the editor, then report the retired shortcut ACK afterward.
        s.Service.PendingCapture = new();
        s.Tick();
        s.Input.InEditor = true;
        var entry = Open(s);
        s.CompleteCapture(new(s.Fresh, FocusMember.Overlay, "org.typelay.ControlledApp",
            s.Input.UserEpoch, s.Input.ForegroundEpoch,
            new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, false, false)));
        Assert.False(s.View.Context!.CanEdit);
        Assert.True((await Add(s, entry)).Saved);
        Assert.Empty(s.Service.Writes);
        Assert.Null(s.Input.Permit);
    }

    [Fact]
    public async Task LeavingEditingRetiresTheFormWithoutAPeerClose()
    {
        using var s = Editing();
        var entry = Open(s);
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.False((await Add(s, entry)).Saved);
        Assert.Null(s.ReloadSettings().Glossary);
    }

    [Fact]
    public void ProductionReleaseAndMachinePreviewCannotOpenAuxiliaryForm()
    {
        using var closed = new SessionScenario();
        closed.ReadyCandidate();
        Assert.False(closed.View.Context!.TranslationActions!.GlossaryReleased);
        Assert.Null(closed.View.BeginGlossaryRequested!(closed.View.Token));
        using var preview = new SessionScenario(translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        preview.ReadyCandidate();
        Assert.Null(preview.View.BeginGlossaryRequested!(preview.View.Token));
    }
}
