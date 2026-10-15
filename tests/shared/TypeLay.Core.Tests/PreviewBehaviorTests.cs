using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;

namespace TypeLay.Core.Tests;

public sealed class PreviewBehaviorTests
{
    [Fact(DisplayName = "empty-field-shows-language-without-translating")]
    public void EmptyFieldShowsLanguageWithoutTranslating()
    {
        var s = NewSession();
        s.Observe(Sample(""), FocusMember.Source, 1000);
        Check(s.State == SessionState.IdleCapsule && Presentation.From(s.State, false).Visible);
        Check(s.TakeTranslationJob(2000) is null && s.Candidate == "" && s.Preview == "");
        Check(!s.BeginEdit());
    }

    [Fact(DisplayName = "whitespace-shows-language-without-translating")]
    public void WhitespaceShowsLanguageWithoutTranslating()
    {
        var s = NewSession();
        s.Observe(Sample(" \r\n "), FocusMember.Source, 1000);
        Check(s.State == SessionState.IdleCapsule && Presentation.From(s.State, false).Visible);
        Check(s.TakeTranslationJob(2000) is null && s.Candidate == "" && s.Preview == "");
    }

    [Fact(DisplayName = "debounce-no-early-job")]
    public void DebounceNoEarlyJob()
    {
        var s = NewSession();
        s.Observe(Sample(), FocusMember.Source, 1000);
        Check(s.TakeTranslationJob(1299) is null);
        Check(s.TakeTranslationJob(1300) is not null);
        Check(s.TakeTranslationJob(1500) is null);
    }

    [Fact(DisplayName = "continuous-unpunctuated-input-cannot-starve-model")]
    public void ContinuousUnpunctuatedInputCannotStarveModel()
    {
        var s = NewSession();
        string input = "你好";
        for (long now = 1000; now < 1500; now += 100)
        {
            input += "中";
            s.Observe(Sample(input, at: now) with
            {
                InputEpoch = now
            }, FocusMember.Source, now);
            Check(s.TakeTranslationJob(now) is null);
        }
        var job = s.TakeTranslationJob(1500)!;
        Check(job.SourceText == input);
        for (long now = 1600; now <= 2000; now += 100)
        {
            input += "中";
            var a = Sample(input, at: now) with
            {
                InputEpoch = now
            };
            s.Observe(a, FocusMember.Source, now);
            if (now == 1600)
                Check(!s.Complete(job, "Hello") && s.Preview == "Hello" && s.Candidate == "");
            if (now < 2000)
                Check(s.TakeTranslationJob(now) is null);
        }
        Check(s.TakeTranslationJob(2000)!.SourceText == input);
    }

    [Fact(DisplayName = "partial-preview-never-enables-edit-or-write")]
    public void PartialPreviewNeverEnablesEditOrWrite()
    {
        var s = NewSession();
        s.Observe(Sample(), FocusMember.Source, 1000);
        var old = s.TakeTranslationJob(1300)!;
        var a = Sample("你好朋友", at: 1400) with
        {
            InputEpoch = 2
        };
        s.Observe(a, FocusMember.Source, 1400);
        Check(!s.Complete(old, "Hello"));
        Check(s.Preview == "Hello" && s.Candidate == "" && !s.BeginEdit() && s.Prepare(a, Context(a), 1400).Plan is null);
        Assert.False(s.CanBeginEdit);
        Assert.Equal("正在翻译", Presentation.From(s.State, false, hasPreview: true, canEdit: s.CanBeginEdit).Status);
        var latest = s.TakeTranslationJob(1700)!;
        Check(s.Complete(latest, "Hello friend"));
        a = a with
        {
            ObservedAtMs = 1700
        };
        s.Observe(a, FocusMember.Source, 1700);
        Check(Plan(s, a, 1700).Replacement == "Hello friend");
    }

    [Fact(DisplayName = "preview-only-provider-can-translate-but-never-write")]
    public void PreviewOnlyProviderCanTranslateButNeverWrite()
    {
        var s = NewSession();
        var a = Sample() with
        {
            WriteSupported = false
        };
        Check(a.IsUsable);
        s.Observe(a, FocusMember.Source, 1000);
        Check(s.Complete(s.TakeTranslationJob(1300)!, "Hello"));
        a = a with
        {
            ObservedAtMs = 1300
        };
        s.Observe(a, FocusMember.Source, 1300);
        Check(s.Preview == "Hello" && s.Prepare(a, Context(a), 1300).Reason == Denial.Unsupported);
    }

    [Fact(DisplayName = "preview-only-plan-cannot-be-promoted-by-a-fresh-writable-observation")]
    public void PreviewOnlyPlanCannotBePromotedByAFreshWritableObservation()
    {
        var (s, a) = Ready();
        var plan = Plan(s, a);
        Check(WriteSafety.Validate(plan with
        {
            Expected = a with
            {
                WriteSupported = false
            }
        }, a, 1300) == Denial.Unsupported);
    }

    [Fact(DisplayName = "append-retains-preview-but-source-rewrite-clears-it")]
    public void AppendRetainsPreviewButSourceRewriteClearsIt()
    {
        var (s, _) = Ready();
        var a = Sample("你好朋友", at: 1400) with
        {
            InputEpoch = 2
        };
        s.Observe(a, FocusMember.Source, 1400);
        Check(s.Preview == "Hello" && s.Candidate == "");
        var job = s.TakeTranslationJob(1700)!;
        s.Observe(Sample("再见", at: 1800) with
        {
            InputEpoch = 3
        }, FocusMember.Source, 1800);
        Check(s.Preview == "" && !s.Complete(job, "Wrong") && s.Preview == "");
    }

    [Fact(DisplayName = "preview-aba-cannot-resurrect-a-canceled-revision")]
    public void PreviewAbaCannotResurrectACanceledRevision()
    {
        var s = NewSession();
        s.Observe(Sample(), FocusMember.Source, 1000);
        var old = s.TakeTranslationJob(1300)!;
        s.Observe(Sample("再见", at: 1400) with
        {
            InputEpoch = 2
        }, FocusMember.Source, 1400);
        s.Observe(Sample("你好朋友", at: 1500) with
        {
            InputEpoch = 3
        }, FocusMember.Source, 1500);
        Check(!s.Complete(old, "Hello") && s.Preview == "");
    }

    [Fact(DisplayName = "input-version-catching-up-keeps-preview-without-granting-acceptance")]
    public void InputVersionCatchingUpKeepsPreviewWithoutGrantingAcceptance()
    {
        var (s, _) = Ready();
        var appended = Sample("你好朋友", at: 1400) with
        {
            InputEpoch = 2
        };
        s.Observe(appended, FocusMember.Source, 1400);
        var caughtUp = appended with
        {
            InputEpoch = 3,
            ObservedAtMs = 1450
        };
        s.Observe(caughtUp, FocusMember.Source, 1450);
        Check(s.Preview == "Hello" && s.Candidate == "" && !s.BeginEdit());
        Check(s.Prepare(caughtUp, Context(caughtUp), 1450).Plan is null);
        var job = s.TakeTranslationJob(1750)!;
        Check(s.Complete(job, "Hello friend"));
        caughtUp = caughtUp with
        {
            ObservedAtMs = 1750
        };
        s.Observe(caughtUp, FocusMember.Source, 1750);
        Check(Plan(s, caughtUp, 1750).Replacement == "Hello friend");
    }

    [Fact(DisplayName = "older-preview-cannot-overwrite-newer-preview")]
    public void OlderPreviewCannotOverwriteNewerPreview()
    {
        var s = NewSession();
        s.Observe(Sample(), FocusMember.Source, 1000);
        var old = s.TakeTranslationJob(1300)!;
        s.Observe(Sample("你好朋友", at: 1400) with
        {
            InputEpoch = 2
        }, FocusMember.Source, 1400);
        var newer = s.TakeTranslationJob(1700)!;
        s.Observe(Sample("你好朋友明天见", at: 1800) with
        {
            InputEpoch = 3
        }, FocusMember.Source, 1800);
        Check(!s.Complete(newer, "Hello friend") && s.Preview == "Hello friend");
        Check(!s.Complete(old, "Hello") && s.Preview == "Hello friend");
    }

    [Fact(DisplayName = "known-ime-can-translate-only-the-last-committed-text")]
    public void KnownImeCanTranslateOnlyTheLastCommittedText()
    {
        var s = NewSession();
        var a = Sample();
        s.Observe(a, FocusMember.Source, 1000);
        var composing = a with
        {
            Text = "",
            Selection = new(0, 0),
            Ime = ImeState.Composing,
            ObservedAtMs = 1100
        };
        s.Observe(composing, FocusMember.Source, 1100);
        var job = s.TakeTranslationJob(1300)!;
        Check(job.SourceText == "你好" && job.Basis.Ime == ImeState.Clear);
        Check(!s.Complete(job, "Hello") && s.Preview == "Hello" && s.Candidate == "");
        Check(s.TakeTranslationJob(2000) is null && s.Prepare(composing, Context(composing), 1300).Plan is null);
        Check(Presentation.From(s.State, false, true).Status == "输入中");
    }

    [Fact(DisplayName = "ime-commits-between-polls-refresh-preview-without-an-idle-gap")]
    public void ImeCommitsBetweenPollsRefreshPreviewWithoutAnIdleGap()
    {
        var (s, a) = Ready();
        var composing = a with
        {
            Text = "",
            Selection = new(0, 0),
            Ime = ImeState.Composing,
            CommittedPrefix = "你好朋友",
            WriteSupported = false,
            ObservedAtMs = 1400
        };
        s.Observe(composing, FocusMember.Source, 1400);
        var job = s.TakeTranslationJob(1700)!;
        Check(job.SourceText == "你好朋友" && !job.Basis.WriteSupported);
        Check(!s.Complete(job, "Hello friend") && s.Preview == "Hello friend" && s.Candidate == "");
        composing = composing with
        {
            CommittedPrefix = "你好朋友明天见",
            ObservedAtMs = 1800
        };
        s.Observe(composing, FocusMember.Source, 1800);
        job = s.TakeTranslationJob(2100)!;
        Check(job.SourceText == "你好朋友明天见");
        Check(!s.Complete(job, "See you tomorrow") && s.Preview == "See you tomorrow");
        Check(s.Prepare(composing, Context(composing), 2100).Plan is null);
        Check(s.TakeTranslationJob(2500) is null);
    }

    [Fact(DisplayName = "first-observation-can-preview-committed-prefix-during-ime")]
    public void FirstObservationCanPreviewCommittedPrefixDuringIme()
    {
        var s = NewSession();
        var a = Sample("") with
        {
            Ime = ImeState.Composing,
            CommittedPrefix = "你好"
        };
        s.Observe(a, FocusMember.Source, 1000);
        var job = s.TakeTranslationJob(1300)!;
        Check(job.SourceText == "你好");
        Check(!s.Complete(job, "Hello") && s.Preview == "Hello");
    }

    [Fact(DisplayName = "rewritten-ime-prefix-discards-stale-translation")]
    public void RewrittenImePrefixDiscardsStaleTranslation()
    {
        var (s, a) = Ready();
        a = a with
        {
            Text = "",
            Selection = new(0, 0),
            Ime = ImeState.Composing,
            CommittedPrefix = "再见"
        };
        s.Observe(a, FocusMember.Source, 1400);
        Check(s.Preview == "");
        Check(s.TakeTranslationJob(1700)!.SourceText == "再见");
    }

    [Fact(DisplayName = "unobservable-ime-can-preview-an-ordinary-value-but-cannot-accept")]
    public void UnobservableImeCanPreviewAnOrdinaryValueButCannotAccept()
    {
        var s = NewSession();
        var a = Sample("") with
        {
            Ime = ImeState.Unknown,
            PreviewText = "你好",
            WriteSupported = false
        };
        s.Observe(a, FocusMember.Source, 1000);
        var job = s.TakeTranslationJob(1300)!;
        Check(job.SourceText == "你好" && !job.Basis.WriteSupported);
        Check(!s.Complete(job, "Hello") && s.Preview == "Hello" && s.Candidate == "");
        Check(!s.BeginEdit() && s.Prepare(a, Context(a), 1300).Plan is null);
        var clear = Sample(at: 1400);
        s.Observe(clear, FocusMember.Source, 1400);
        Check(s.Candidate == "" && s.Prepare(clear, Context(clear), 1400).Plan is null);
    }

    [Fact(DisplayName = "unobservable-ime-append-refreshes-without-clearing-preview")]
    public void UnobservableImeAppendRefreshesWithoutClearingPreview()
    {
        var s = NewSession();
        var a = Sample("") with
        {
            Ime = ImeState.Unknown,
            PreviewText = "你好",
            WriteSupported = false
        };
        s.Observe(a, FocusMember.Source, 1000);
        s.Complete(s.TakeTranslationJob(1300)!, "Hello");
        a = a with
        {
            PreviewText = "你好朋友",
            InputEpoch = 2,
            ObservedAtMs = 1400
        };
        s.Observe(a, FocusMember.Source, 1400);
        Check(s.Preview == "Hello");
        var job = s.TakeTranslationJob(1700)!;
        Check(job.SourceText == "你好朋友");
        Check(!s.Complete(job, "Hello friend") && s.Preview == "Hello friend" && s.Candidate == "");
        s.Observe(a with
        {
            Target = B,
            PreviewText = "再见"
        }, FocusMember.Source, 1800);
        Check(s.Preview == "" && s.Candidate == "");
    }

    [Fact(DisplayName = "preview-only-escape-suppresses-until-field-value-changes")]
    public void PreviewOnlyEscapeSuppressesUntilFieldValueChanges()
    {
        var s = NewSession();
        var a = Sample("") with
        {
            Ime = ImeState.Unknown,
            PreviewText = "你好",
            WriteSupported = false
        };
        s.Observe(a, FocusMember.Source, 1000);
        s.Complete(s.TakeTranslationJob(1300)!, "Hello");
        s.Escape();
        s.Observe(a, FocusMember.Source, 1400);
        Check(s.Preview == "" && s.TakeTranslationJob(1700) is null);
        s.Observe(a with
        {
            PreviewText = "你好朋友",
            InputEpoch = 2
        }, FocusMember.Source, 1800);
        Check(s.TakeTranslationJob(2100)!.SourceText == "你好朋友");
    }

    [Fact(DisplayName = "preview-only-no-translation-does-not-block-later-input")]
    public void PreviewOnlyNoTranslationDoesNotBlockLaterInput()
    {
        var s = NewSession();
        var a = Sample("") with
        {
            Ime = ImeState.Unknown,
            PreviewText = "Hello",
            WriteSupported = false
        };
        s.Observe(a, FocusMember.Source, 1000);
        Check(s.CompleteNoTranslation(s.TakeTranslationJob(1300)!));
        s.Observe(a, FocusMember.Source, 1400);
        Check(s.TakeTranslationJob(1700) is null);
        s.Observe(a with
        {
            PreviewText = "Hello 你好",
            InputEpoch = 2
        }, FocusMember.Source, 1800);
        Check(s.TakeTranslationJob(2100)!.SourceText == "Hello 你好");
    }

    [Fact(DisplayName = "protected-preview-text-is-never-translated")]
    public void ProtectedPreviewTextIsNeverTranslated()
    {
        var s = NewSession();
        var a = Sample("") with
        {
            Ime = ImeState.Unknown,
            PreviewText = "synthetic protected text",
            Access = FieldAccess.Password,
            WriteSupported = false
        };
        s.Observe(a, FocusMember.Source, 1000);
        Check(s.TakeTranslationJob(2000) is null && s.Preview == "");
    }

    [Fact(DisplayName = "unconfirmed-preview-can-regenerate-after-an-inference-failure")]
    public void UnconfirmedPreviewCanRegenerateAfterAnInferenceFailure()
    {
        var s = NewSession();
        var a = Sample("") with
        {
            Ime = ImeState.Unknown,
            PreviewText = "你好",
            WriteSupported = false
        };
        s.Observe(a, FocusMember.Source, 1000);
        Check(s.Fail(s.TakeTranslationJob(1300)!));
        Check(s.TakeTranslationJob(2000) is null && s.Retranslate(2100));
        var job = s.TakeTranslationJob(2400)!;
        Check(job.SourceText == "你好");
        Check(!s.Complete(job, "Hello") && s.Preview == "Hello" && s.Candidate == "");
    }

    [Fact(DisplayName = "known-ime-keeps-preview-unknown-ime-without-a-value-clears-it")]
    public void KnownImeKeepsPreviewUnknownImeWithoutAValueClearsIt()
    {
        var (s, a) = Ready();
        s.Observe(a with
        {
            Text = "",
            Selection = new(0, 0),
            Ime = ImeState.Composing
        }, FocusMember.Source, 1400);
        Check(s.Preview == "Hello" && s.Candidate == "" && !s.BeginEdit());
        s.Observe(a with
        {
            Text = "",
            Selection = new(0, 0),
            Ime = ImeState.Unknown
        }, FocusMember.Source, 1500);
        Check(s.Preview == "" && s.TakeTranslationJob(2000) is null);
    }

    [Fact(DisplayName = "new-ime-commit-refreshes-exact-acceptance-candidate")]
    public void NewImeCommitRefreshesExactAcceptanceCandidate()
    {
        var (s, a) = Ready();
        s.Observe(a with
        {
            Text = "",
            Selection = new(0, 0),
            Ime = ImeState.Composing
        }, FocusMember.Source, 1400);
        a = Sample("你好朋友", at: 1500) with
        {
            InputEpoch = 2
        };
        s.Observe(a, FocusMember.Source, 1500);
        Check(s.Candidate == "" && s.Preview == "Hello");
        Check(s.Complete(s.TakeTranslationJob(1800)!, "Hello friend"));
        a = a with
        {
            ObservedAtMs = 1800
        };
        s.Observe(a, FocusMember.Source, 1800);
        Check(Plan(s, a, 1800).Replacement == "Hello friend");
    }

    [Fact(DisplayName = "selection-change-discards-preview-and-inflight-result")]
    public void SelectionChangeDiscardsPreviewAndInflightResult()
    {
        var (s, a) = Ready();
        a = Sample("你好朋友", at: 1400) with
        {
            InputEpoch = 2
        };
        s.Observe(a, FocusMember.Source, 1400);
        var job = s.TakeTranslationJob(1700)!;
        s.Observe(a with
        {
            Selection = new(2, 4),
            ObservedAtMs = 1800
        }, FocusMember.Source, 1800);
        Check(s.Preview == "" && !s.Complete(job, "Wrong") && s.Preview == "");
    }

    [Fact(DisplayName = "pending-candidate-cannot-accept")]
    public void PendingCandidateCannotAccept()
    {
        var s = NewSession();
        s.Observe(Sample(), FocusMember.Source, 1000);
        Check(s.Prepare(Sample(), Context(Sample()), 1000).Plan is null);
    }

    [Fact(DisplayName = "full-text-plan-and-end-caret")]
    public void FullTextPlanAndEndCaret()
    {
        var (s, a) = Ready();
        var p = Plan(s, a);
        Check(p.ResultText == "Hello" && p.Caret == 5 && p.Scope is null);
    }

    [Fact(DisplayName = "selection-replaces-only-selected-text")]
    public void SelectionReplacesOnlySelectedText()
    {
        var (s, a) = Ready("A你好B", new(1, 3));
        var p = Plan(s, a);
        Check(p.ResultText == "AHelloB" && p.Caret == 6);
    }

    [Fact(DisplayName = "selection-is-the-model-input")]
    public void SelectionIsTheModelInput()
    {
        var s = NewSession();
        s.Observe(Sample("A你好B", new(1, 3)), FocusMember.Source, 1000);
        Check(s.TakeTranslationJob(1300)!.SourceText == "你好");
    }

    [Fact(DisplayName = "source-change-rejects-before-observer-refresh")]
    public void SourceChangeRejectsBeforeObserverRefresh()
    {
        var (s, a) = Ready();
        var changed = a with
        {
            Text = "您好"
        };
        Check(s.Prepare(changed, Context(changed), 1300).Reason == Denial.SourceChanged);
    }

    [Fact(DisplayName = "target-change-rejects-before-observer-refresh")]
    public void TargetChangeRejectsBeforeObserverRefresh()
    {
        var (s, a) = Ready();
        var changed = a with
        {
            Target = B
        };
        Check(s.Prepare(changed, Context(changed), 1300).Reason == Denial.TargetChanged);
    }

    [Fact(DisplayName = "lifetime-token-prevents-handle-reuse")]
    public void LifetimeTokenPreventsHandleReuse()
    {
        var (s, a) = Ready();
        var changed = a with
        {
            Target = a.Target with
            {
                Lifetime = Guid.NewGuid()
            }
        };
        Check(s.Prepare(changed, Context(changed), 1300).Reason == Denial.TargetChanged);
    }

    [Fact(DisplayName = "changed-selection-rejects-candidate")]
    public void ChangedSelectionRejectsCandidate()
    {
        var (s, a) = Ready("A你好B", new(1, 3));
        var changed = a with
        {
            Selection = new(0, 3)
        };
        Check(s.Prepare(changed, Context(changed), 1300).Reason == Denial.SelectionChanged);
    }

    [Fact(DisplayName = "collapsed-caret-move-does-not-retranslate-full-field")]
    public void CollapsedCaretMoveDoesNotRetranslateFullField()
    {
        var (s, a) = Ready();
        var moved = a with
        {
            Selection = new(0, 0)
        };
        s.Observe(moved, FocusMember.Source, 1300);
        Check(s.State == SessionState.ValidTranslation && Plan(s, moved).ResultText == "Hello");
    }

    [Fact(DisplayName = "late-model-completion-is-discarded")]
    public void LateModelCompletionIsDiscarded()
    {
        var s = NewSession();
        s.Observe(Sample(), FocusMember.Source, 1000);
        var old = s.TakeTranslationJob(1300)!;
        s.Observe(Sample("再见", at: 1301), FocusMember.Source, 1301);
        Check(!s.Complete(old, "Wrong"));
        Check(s.State == SessionState.Translating);
    }

    [Fact(DisplayName = "language-change-rejects-old-job")]
    public void LanguageChangeRejectsOldJob()
    {
        var s = NewSession();
        s.Observe(Sample(), FocusMember.Source, 1000);
        var old = s.TakeTranslationJob(1300)!;
        s.SetDirection(new("zh", "ja"), 1300);
        Check(!s.Complete(old, "Hello"));
    }

    [Fact(DisplayName = "old-session-response-cannot-enter-new-target")]
    public void OldSessionResponseCannotEnterNewTarget()
    {
        var s = NewSession();
        s.Observe(Sample(), FocusMember.Source, 1000);
        var old = s.TakeTranslationJob(1300)!;
        s.Observe(Sample() with
        {
            Target = B
        }, FocusMember.Source, 1300);
        Check(!s.Complete(old, "Wrong"));
    }

    [Fact(DisplayName = "failed-translation-passes-tab")]
    public void FailedTranslationPassesTab()
    {
        var s = NewSession();
        s.Observe(Sample(), FocusMember.Source, 1000);
        var job = s.TakeTranslationJob(1300)!;
        Check(s.Fail(job));
        Check(s.Prepare(Sample(), Context(Sample()), 1300).Plan is null);
        Check(Presentation.From(s.State, false).Status == "暂时无法翻译");
    }

    [Fact(DisplayName = "already-target-language-is-not-rewritten")]
    public void AlreadyTargetLanguageIsNotRewritten()
    {
        var s = NewSession();
        var a = Sample("Hello");
        s.Observe(a, FocusMember.Source, 1000);
        Check(s.CompleteNoTranslation(s.TakeTranslationJob(1300)!));
        s.Observe(a, FocusMember.Source, 2000);
        Check(s.State == SessionState.NoSession && s.TakeTranslationJob(2300) is null);
    }

    [Fact(DisplayName = "modifier-passes-tab")]
    public void ModifierPassesTab()
    {
        var (s, a) = Ready();
        Check(s.Prepare(a, Context(a) with
        {
            Modified = true
        }, 1300).Reason == Denial.Modifier);
    }

}
