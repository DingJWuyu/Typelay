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
    public async Task SourceCandidateReturnsAndRevalidatesBeforeSavingWithoutEditing()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        var token = s.View.Token;
        string candidate = s.View.Preview;
        var entry = Open(s);
        Assert.Equal(SessionState.ValidTranslation, s.View.State);
        Assert.Equal(candidate, s.View.Preview);
        Assert.Null(s.Input.Permit);
        s.Input.InEditor = true; // This host role is the exact auxiliary form, not a Core editor transition.
        s.View.ReturnGlossary = returned =>
        {
            Assert.Same(entry, returned);
            Assert.Null(s.Input.Permit);
            s.Input.InEditor = false;
            s.View.GlossaryEntryFocus = null;
            s.Source = s.Source with
            {
                FocusEpoch = s.Source.FocusEpoch + 2
            };
            return Task.FromResult(true);
        };
        int captures = s.Service.Captures;
        var result = await Add(s, entry);
        Assert.True(result.Saved, result.Code);
        Assert.Equal(captures + 1, s.Service.Captures);
        Assert.Equal("fixed", Assert.Single(s.ReloadSettings().Glossary!.Rules).TargetTerm);
        Assert.NotEqual(token, s.View.Token);
        Assert.Equal(SessionState.Translating, s.View.State);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        Assert.Equal(0, s.Service.Returns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvenPendingSourceReturnPreservesTheFormWithoutCapturingOrGrantingFocus(bool editedPending)
    {
        const string manualDraft = "keep my manual draft";
        using var s = editedPending ? Editing() : new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        if (editedPending)
        {
            s.View.DraftChanged!(manualDraft);
            s.View.ReturnRequested!(s.View.Token);
            s.Tick();
            Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
            Assert.Equal(manualDraft, s.View.Preview);
        }
        else
            s.ReadyCandidate();
        s.View.SourceGlossarySupported = true;
        var entry = Open(s);
        string preview = s.View.Preview;
        s.Input.InEditor = true;
        var pending = new TaskCompletionSource<bool>();
        bool platformPending = true;
        s.View.ReturnGlossary = _ => pending.Task;
        // This explicit platform proof is not supplied by equal window epochs.
        // Windows owns the real receipt, native events and original deadline.
        s.View.GlossarySourceReturnPending = form => platformPending && ReferenceEquals(form, entry);
        var saving = Add(s, entry);
        int captures = s.Service.Captures;
        s.Input.InEditor = false;
        s.Input.FocusedTarget = new("controlled", "unavailable", Guid.NewGuid());
        s.View.GlossaryEntryFocus = null;
        s.Tick();
        Assert.Equal(FocusMember.Other, s.Input.GetFocus(entry.Token.Target));
        Assert.True(s.View.Visible);
        Assert.Equal(entry.Token, s.View.Token);
        Assert.Equal(editedPending ? manualDraft : preview, s.View.Preview);
        Assert.Equal(captures, s.Service.Captures);
        Assert.Null(s.Input.Permit);
        Assert.Null(s.ReloadSettings().Glossary);
        Assert.Empty(s.Service.Writes);

        platformPending = false;
        s.Input.FocusedTarget = s.Source.Target;
        s.Service.PendingCapture = new();
        pending.SetResult(true);
        s.Tick(0);
        Assert.False(saving.IsCompleted);
        Assert.Equal(captures + 1, s.Service.Captures);
        Assert.Null(s.ReloadSettings().Glossary);
        s.CompleteCapture(new(s.Fresh, FocusMember.Source, "org.typelay.ControlledApp",
            s.Input.UserEpoch, s.Input.ForegroundEpoch,
            new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, false, false)));
        Assert.True((await saving).Saved);
        Assert.Equal("fixed", Assert.Single(s.ReloadSettings().Glossary!.Rules).TargetTerm);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        if (editedPending)
        {
            Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
            Assert.Equal(manualDraft, s.View.Preview);
            // Source return promotes the draft to the pending candidate. The
            // editor exposes it as Draft again only after a new explicit edit.
            s.View.CloseGlossaryRequested!(entry.FormId);
            s.Tick(); // Ordinary fresh capture restores the editor shortcut ACK.
            Assert.True(s.View.BeginEditing!(s.View.Token));
            Assert.Equal(SessionState.EditingTranslation, s.View.State);
            Assert.Equal(manualDraft, s.View.Draft);
        }
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("unproven")]
    [InlineData("revoked")]
    [InlineData("window")]
    [InlineData("configuration")]
    [InlineData("input-unavailable")]
    [InlineData("closed")]
    public async Task PendingSourceReturnCannotPreserveAnInvalidOrUnprovenForm(string change)
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        var entry = Open(s);
        s.Input.InEditor = true;
        var pending = new TaskCompletionSource<bool>();
        bool platformPending = true;
        s.View.ReturnGlossary = _ => pending.Task;
        if (change != "unproven")
            s.View.GlossarySourceReturnPending = form => platformPending && ReferenceEquals(form, entry);
        var saving = Add(s, entry);
        Assert.False(saving.IsCompleted);
        s.Input.InEditor = false;
        s.Input.FocusedTarget = new("controlled", "unavailable", Guid.NewGuid());
        s.View.GlossaryEntryFocus = null;
        if (change == "revoked")
            platformPending = false;
        if (change == "window")
            s.Input.WindowEpoch++;
        if (change == "configuration")
        {
            var generation = s.Input.CommandGeneration;
            s.Settings.Update(s.Settings.Value with
            {
                Mode = WorkMode.OnDemand
            });
            Assert.Equal(WorkMode.OnDemand, s.Settings.Value.Mode);
            Assert.NotEqual(generation, s.Input.CommandGeneration);
        }
        if (change == "input-unavailable")
            s.Input.Ready = false;
        if (change == "closed")
            s.View.CloseGlossaryRequested!(entry.FormId);
        s.Tick();
        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        s.Input.FocusedTarget = s.Source.Target;
        pending.SetResult(true);
        s.Tick(0);
        var result = await saving;
        Assert.False(result.Saved);
        Assert.Equal("glossary-canceled", result.Code);
        Assert.Null(s.ReloadSettings().Glossary);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactFormWindowReceiptDoesNotGrantWritingOrAuthorizeAnotherWindow(bool foreignWindow)
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        long original = s.Input.WindowEpoch;
        var entry = Open(s);
        s.Input.InEditor = true;
        s.Input.WindowEpoch++;
        // Trusted platform evidence at the view seam. These controlled ports do
        // not prove a real foreground event or IME; Windows tests own that part.
        s.View.GlossarySourceWindow = (form, before, current) => ReferenceEquals(form, entry)
            && before == original && (current == original + 1 || current == original + 2);
        int returns = 0;
        s.View.ReturnGlossary = form =>
        {
            returns++;
            Assert.Same(entry, form);
            Assert.Null(s.Input.Permit);
            s.Input.InEditor = false;
            s.View.GlossaryEntryFocus = null;
            s.Input.WindowEpoch += foreignWindow ? 2 : 1;
            return Task.FromResult(true);
        };
        var result = await Add(s, entry);
        Assert.Equal(!foreignWindow, result.Saved);
        Assert.Equal(1, returns);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        if (foreignWindow)
            Assert.Null(s.ReloadSettings().Glossary);
        else
            Assert.Equal("fixed", Assert.Single(s.ReloadSettings().Glossary!.Rules).TargetTerm);
    }

    [Fact]
    public async Task RevokedFormWindowReceiptCannotFallBackToEqualEpochs()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        var entry = Open(s);
        s.Input.InEditor = true;
        s.View.GlossarySourceWindow = (_, original, current) =>
        {
            Assert.Equal(original, current);
            return false;
        };
        s.View.ReturnGlossary = _ => throw new InvalidOperationException("Revoked form cannot return.");
        Assert.False((await Add(s, entry)).Saved);
        Assert.Null(s.ReloadSettings().Glossary);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public async Task SourceFormRejectsWindowChangesBeforeReturn()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        var entry = Open(s);
        s.Input.InEditor = true;
        int returns = 0;
        s.View.ReturnGlossary = _ =>
        {
            returns++;
            s.Input.InEditor = false;
            s.View.GlossaryEntryFocus = null;
            return Task.FromResult(true);
        };
        s.Input.WindowEpoch++;
        Assert.False((await Add(s, entry)).Saved);
        Assert.Equal(0, returns);
        Assert.Null(s.ReloadSettings().Glossary);
    }

    [Fact]
    public async Task SourceFormCannotHoldSessionAfterExternalFocusLoss()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        var entry = Open(s);
        s.View.GlossarySourceReturnPending = form => ReferenceEquals(form, entry);
        s.Input.InEditor = true;
        s.Tick(1000);
        Assert.Equal(SessionState.ValidTranslation, s.View.State);
        s.Input.InEditor = false;
        s.Input.FocusedTarget = new("controlled", "external", Guid.NewGuid());
        s.View.GlossaryEntryFocus = null;
        s.Tick();
        Assert.False(s.View.Visible);
        Assert.False((await Add(s, entry)).Saved);
        Assert.Null(s.Input.Permit);
    }

    [Fact]
    public async Task FailedSourceSaveRetainsFormWithoutPermitsAndCanRetryFromAuxiliaryFocus()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        var entry = Open(s);
        s.Input.InEditor = true;
        s.View.ReturnGlossary = _ =>
        {
            s.Input.InEditor = false;
            s.View.GlossaryEntryFocus = null;
            return Task.FromResult(true);
        };
        s.BlockSettingsWrites();
        Assert.Equal("glossary-save-failed", (await Add(s, entry)).Code);
        s.Tick(1000);
        Assert.True(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.False(s.View.Context!.TranslationActions!.CanOpenGlossary);
        s.AllowSettingsWrites();
        s.Input.InEditor = true;
        s.View.GlossaryEntryFocus = new(entry.FormId, entry.Token, ImeState.Clear);
        Assert.True((await Add(s, entry)).Saved);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("input")]
    [InlineData("selection")]
    [InlineData("target")]
    [InlineData("ime")]
    [InlineData("composing")]
    [InlineData("model")]
    [InlineData("combined-capability")]
    [InlineData("settings")]
    [InlineData("close")]
    public async Task SourceReturnRechecksOriginalBasisBeforeAnyPersistence(string change)
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        var entry = Open(s);
        s.Input.InEditor = true;
        s.View.ReturnGlossary = _ =>
        {
            s.Input.InEditor = false;
            s.View.GlossaryEntryFocus = null;
            switch (change)
            {
                case "text":
                    s.Source = s.Source with
                    {
                        Text = "other"
                    };
                    break;
                case "input":
                    s.Source = s.Source with
                    {
                        InputEpoch = 2
                    };
                    break;
                case "selection":
                    s.Source = s.Source with
                    {
                        Selection = new(1, 2)
                    };
                    break;
                case "target":
                    s.Source = s.Source with
                    {
                        Target = s.Source.Target with
                        {
                            Lifetime = Guid.NewGuid()
                        }
                    };
                    break;
                case "ime":
                case "composing":
                    s.Source = s.Source with
                    {
                        Ime = change == "composing" ? ImeState.Composing : ImeState.Unknown
                    };
                    break;
                case "combined-capability":
                    s.Model.StyledGlossaryTranslationCapability = new(2);
                    break;
                case "model":
                    s.Model.VerifiedIdentity = SessionScenario.ArgosIdentity;
                    break;
                case "settings":
                    s.Settings.Update(s.Settings.Value with
                    {
                        Source = "fr"
                    });
                    break;
                case "close":
                    s.View.CloseGlossaryRequested!(entry.FormId);
                    break;
            }
            return Task.FromResult(true);
        };
        Assert.False((await Add(s, entry)).Saved);
        Assert.Null(s.ReloadSettings().Glossary);
        Assert.Empty(s.Service.Writes);
        Assert.Null(s.Input.Permit);
    }

    [Fact]
    public async Task SourcePendingDraftKeepsItsCandidateAndAppliesSavedRuleOnlyToNextMachineJob()
    {
        using var s = Editing();
        s.View.DraftChanged!("keep my manual draft");
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        s.View.SourceGlossarySupported = true;
        var token = s.View.Token;
        string draft = s.View.Draft, preview = s.View.Preview;
        int jobs = s.Model.Jobs.Count;
        var entry = Open(s);
        s.Input.InEditor = true;
        s.View.ReturnGlossary = _ =>
        {
            s.Input.InEditor = false;
            s.View.GlossaryEntryFocus = null;
            s.Source = s.Source with
            {
                FocusEpoch = s.Source.FocusEpoch + 2
            };
            return Task.FromResult(true);
        };
        Assert.True((await Add(s, entry)).Saved);
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal(token, s.View.Token);
        Assert.Equal(draft, s.View.Draft);
        Assert.Equal(preview, s.View.Preview);
        Assert.Equal(jobs, s.Model.Jobs.Count);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        s.View.CloseGlossaryRequested!(entry.FormId);
        s.View.RegenerateRequested!();
        s.Tick(Limits.DebounceMs);
        Assert.Equal("fixed", Assert.Single(s.Model.Jobs[^1].Glossary!.Rules).TargetTerm);
    }

    [Fact]
    public async Task SourceCommitReturnsSavedEvenWhenRenderRetiresFormAndNotificationFails()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.Source = s.Source with
        {
            Selection = new(1, 4)
        };
        s.ReadyCandidate();
        var entry = Open(s);
        s.Input.InEditor = true;
        s.View.ReturnGlossary = _ =>
        {
            s.Input.InEditor = false;
            s.View.GlossaryEntryFocus = null;
            return Task.FromResult(true);
        };
        s.Settings.GlossarySaved += () => throw new IOException("controlled notification failure");
        var result = await Add(s, entry);
        Assert.True(result.Saved);
        Assert.Equal("glossary-saved-notification-failed", result.Code);
        Assert.Equal("fixed", Assert.Single(s.ReloadSettings().Glossary!.Rules).TargetTerm);
        Assert.NotEqual(entry.Token, s.View.Token);
        Assert.False((await Add(s, entry)).Saved);
        s.Tick(Limits.DebounceMs);
        Assert.Equal("ell", s.Model.Jobs[^1].SourceText);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public async Task ClosingSourceFormWhileFreshCaptureIsPendingCannotCommitLater()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        var entry = Open(s);
        s.Input.InEditor = true;
        s.View.ReturnGlossary = _ =>
        {
            s.Input.InEditor = false;
            s.View.GlossaryEntryFocus = null;
            return Task.FromResult(true);
        };
        s.Service.PendingCapture = new();
        var saving = Add(s, entry);
        Assert.False(saving.IsCompleted);
        s.View.CloseGlossaryRequested!(entry.FormId);
        s.CompleteCapture(new(s.Fresh, FocusMember.Source, "org.typelay.ControlledApp",
            s.Input.UserEpoch, s.Input.ForegroundEpoch,
            new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, false, false)));
        Assert.Equal("glossary-canceled", (await saving).Code);
        Assert.Null(s.ReloadSettings().Glossary);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void FailedCaptureStartedBeforeSourceFormCannotCloseItsAuxiliarySession()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        s.Service.PendingCapture = new();
        s.Tick();
        var entry = Open(s);
        string preview = s.View.Preview;
        s.Input.InEditor = true;
        s.FailCapture();
        Assert.True(s.View.Visible);
        Assert.Equal(entry.Token, s.View.Token);
        Assert.Equal(preview, s.View.Preview);
        Assert.Equal(SessionState.ValidTranslation, s.View.State);
        Assert.Null(s.Input.Permit);
        Assert.False(s.View.CanAccept);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void CaptureStartedBeforeSourceFormCannotReplaceItsBasisOrIssueAnotherPermit()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        s.Service.PendingCapture = new();
        s.Tick();
        var entry = Open(s);
        string preview = s.View.Preview;
        s.Input.InEditor = true;
        s.CompleteCapture(new(s.Fresh with
        {
            Text = "late source observation",
            InputEpoch = 9
        },
            FocusMember.Source, "org.typelay.ControlledApp", s.Input.UserEpoch, s.Input.ForegroundEpoch,
            new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, true, true)));
        s.Tick(1000);
        Assert.Equal(entry.Token, s.View.Token);
        Assert.Equal(preview, s.View.Preview);
        Assert.Equal(SessionState.ValidTranslation, s.View.State);
        Assert.Null(s.Input.Permit);
        Assert.False(s.View.CanAccept);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureDisplacedByOpenedAndClosedSourceFormCannotChangeTheCandidate(bool failed)
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        s.Service.PendingCapture = new();
        s.Tick();
        var entry = Open(s);
        string preview = s.View.Preview;
        s.Input.InEditor = true;
        // Cancel restores actual source focus and retires the form before the
        // original ordinary read completes. Both owners now appear absent.
        s.Input.InEditor = false;
        s.View.GlossaryEntryFocus = null;
        s.View.CloseGlossaryRequested!(entry.FormId);
        if (failed)
            s.FailCapture();
        else
            s.CompleteCapture(new(s.Fresh with
            {
                Text = "late source observation",
                InputEpoch = 9
            }, FocusMember.Source, "org.typelay.ControlledApp", s.Input.UserEpoch, s.Input.ForegroundEpoch,
                new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, true, true)));
        Assert.True(s.View.Visible);
        Assert.Equal(entry.Token, s.View.Token);
        Assert.Equal(preview, s.View.Preview);
        Assert.Equal(SessionState.ValidTranslation, s.View.State);
        Assert.Equal(InputConnectionState.Connected, s.Controller.ConnectionState);
        Assert.Null(s.Input.Permit);
        Assert.True(s.View.Context!.TranslationActions!.CanOpenGlossary);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureDisplacedBySourceFormCannotAffectItAfterReturningToSource(bool failed)
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        var earlierRead = new TaskCompletionSource<InputCapture>();
        s.Service.PendingCapture = earlierRead;
        s.Tick();
        var entry = Open(s);
        string preview = s.View.Preview;
        s.Input.InEditor = true;
        s.View.ReturnGlossary = _ =>
        {
            s.Input.InEditor = false;
            s.View.GlossaryEntryFocus = null;
            return Task.FromResult(true);
        };
        // The explicit return gets its own fresh read while the earlier
        // ordinary read remains pending at the input-service boundary.
        s.Service.PendingCapture = null;
        s.BlockSettingsWrites();
        Assert.Equal("glossary-save-failed", (await Add(s, entry)).Code);
        s.Service.PendingCapture = earlierRead;
        if (failed)
            s.FailCapture();
        else
            s.CompleteCapture(new(s.Fresh with
            {
                Text = "late source observation",
                InputEpoch = 9
            }, FocusMember.Source, "org.typelay.ControlledApp", s.Input.UserEpoch, s.Input.ForegroundEpoch,
                new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, true, true)));
        Assert.True(s.View.Visible);
        Assert.Equal(entry.Token, s.View.Token);
        Assert.Equal(preview, s.View.Preview);
        Assert.Equal(SessionState.ValidTranslation, s.View.State);
        Assert.Equal(InputConnectionState.Connected, s.Controller.ConnectionState);
        Assert.Null(s.Input.Permit);
        s.AllowSettingsWrites();
        s.Input.InEditor = true;
        s.View.GlossaryEntryFocus = new(entry.FormId, entry.Token, ImeState.Clear);
        Assert.True((await Add(s, entry)).Saved);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public async Task CurrentCaptureFailureAfterSourceReturnStillClosesTheSession()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.View.SourceGlossarySupported = true;
        s.ReadyCandidate();
        var entry = Open(s);
        s.Input.InEditor = true;
        s.View.ReturnGlossary = _ =>
        {
            s.Input.InEditor = false;
            s.View.GlossaryEntryFocus = null;
            return Task.FromResult(true);
        };
        s.BlockSettingsWrites();
        Assert.Equal("glossary-save-failed", (await Add(s, entry)).Code);
        s.Service.PendingCapture = new();
        int captures = s.Service.Captures;
        s.Tick();
        Assert.Equal(captures + 1, s.Service.Captures);
        s.FailCapture();
        Assert.False(s.View.Visible);
        Assert.Equal(InputConnectionState.Unavailable, s.Controller.ConnectionState);
        Assert.False((await Add(s, entry)).Saved);
        Assert.Null(s.Input.Permit);
        Assert.Null(s.ReloadSettings().Glossary);
        Assert.Empty(s.Service.Writes);
    }

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
