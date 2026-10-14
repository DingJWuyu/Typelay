using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;

namespace TypeLay.Core.Tests;

public sealed class SessionBehaviorTests
{
    [Fact(DisplayName = "injected-tab-is-not-user-acceptance")]
    public void InjectedTabIsNotUserAcceptance()
    {
        var (s, a) = Ready();
        Check(s.Prepare(a, Context(a) with
        {
            Injected = true
        }, 1300).Reason == Denial.Injected);
    }

    [Fact(DisplayName = "non-source-focus-passes-tab")]
    public void NonSourceFocusPassesTab()
    {
        var (s, a) = Ready();
        Check(s.Prepare(a, Context(a) with
        {
            Focus = FocusMember.Overlay
        }, 1300).Reason == Denial.NotSourceFocus);
    }

    [Fact(DisplayName = "stale-observation-denies-plan")]
    public void StaleObservationDeniesPlan()
    {
        var (s, a) = Ready();
        Check(s.Prepare(a, Context(a), 1451).Reason == Denial.StaleObservation);
    }

    [Fact(DisplayName = "future-observation-denies-plan")]
    public void FutureObservationDeniesPlan()
    {
        var (s, a) = Ready();
        Check(s.Prepare(a, Context(a), 1299).Reason == Denial.StaleObservation);
    }

    [Fact(DisplayName = "input-epoch-mismatch-denies-plan")]
    public void InputEpochMismatchDeniesPlan()
    {
        var (s, a) = Ready();
        Check(s.Prepare(a, Context(a) with
        {
            InputEpoch = 2
        }, 1300).Reason == Denial.EpochChanged);
    }

    [Fact(DisplayName = "focus-epoch-mismatch-denies-plan")]
    public void FocusEpochMismatchDeniesPlan()
    {
        var (s, a) = Ready();
        Check(s.Prepare(a, Context(a) with
        {
            FocusEpoch = 2
        }, 1300).Reason == Denial.EpochChanged);
    }

    [Fact(DisplayName = "ime-commit-enters-normal-translation")]
    public void ImeCommitEntersNormalTranslation()
    {
        var s = NewSession();
        s.Observe(Sample() with
        {
            Ime = ImeState.Composing
        }, FocusMember.Source, 1000);
        s.Observe(Sample(at: 1500), FocusMember.Source, 1500);
        Check(s.TakeTranslationJob(1800) is not null);
    }

    [Fact(DisplayName = "invalid-surrogate-input-blocked")]
    public void InvalidSurrogateInputBlocked()
    {
        var s = NewSession();
        s.Observe(Sample("A\ud800B"), FocusMember.Source, 1000);
        Check(s.TakeTranslationJob(2000) is null);
    }

    [Fact(DisplayName = "selection-cannot-split-surrogate-pair")]
    public void SelectionCannotSplitSurrogatePair()
    {
        Check(!new TextRange(1, 2).IsValid("🙂"));
        Check(new TextRange(0, 2).IsValid("🙂"));
    }

    [Fact(DisplayName = "emoji-adjacent-selection-is-preserved")]
    public void EmojiAdjacentSelectionIsPreserved()
    {
        var (s, a) = Ready("A🙂中文B", new(3, 5), "EN");
        var p = Plan(s, a);
        Check(p.ResultText == "A🙂ENB" && p.Caret == 5);
    }

    [Fact(DisplayName = "crlf-and-multiline-are-preserved")]
    public void CrlfAndMultilineArePreserved()
    {
        var (s, a) = Ready("你好\r\n再见", candidate: "Hello\r\nGoodbye");
        Check(Plan(s, a).ResultText == "Hello\r\nGoodbye");
    }

    [Fact(DisplayName = "single-line-rejects-multiline-output")]
    public void SingleLineRejectsMultilineOutput()
    {
        var (s, a) = Ready(candidate: "Hello\nWorld");
        var single = a with
        {
            Multiline = false
        };
        Check(s.Prepare(single, Context(single), 1300).Reason == Denial.Multiline);
    }

    [Fact(DisplayName = "invalid-model-output-cannot-become-candidate")]
    public void InvalidModelOutputCannotBecomeCandidate()
    {
        var s = NewSession();
        s.Observe(Sample(), FocusMember.Source, 1000);
        Check(!s.Complete(s.TakeTranslationJob(1300)!, "bad\0text"));
        Check(s.State == SessionState.TranslationFailed);
    }

    [Fact(DisplayName = "edit-buffer-is-not-overwritten-by-observations")]
    public void EditBufferIsNotOverwrittenByObservations()
    {
        var (s, a) = Ready();
        Check(s.BeginEdit());
        s.ChangeDraft("Hi");
        s.ChangeDraft("H");
        s.Observe(a, FocusMember.Overlay, 1300);
        s.ChangeDraft("Hey");
        Check(s.Draft == "Hey" && s.State == SessionState.EditingTranslation && s.TakeTranslationJob(3000) is null);
    }

    [Fact(DisplayName = "edit-preserves-leading-trailing-whitespace")]
    public void EditPreservesLeadingTrailingWhitespace()
    {
        var (s, a) = Ready();
        s.BeginEdit();
        s.ChangeDraft("  Edited  ");
        s.ConfirmSourceReturn(a, 1300);
        Check(Plan(s, a).Replacement == "  Edited  ");
    }

    [Fact(DisplayName = "first-tab-only-requests-source-focus")]
    public void FirstTabOnlyRequestsSourceFocus()
    {
        var (s, a) = Ready();
        s.BeginEdit();
        s.ChangeDraft("Edited");
        Check(s.RequestSourceReturn());
        Check(s.State == SessionState.ReturningToSource && s.Prepare(a, Context(a), 1300).Plan is null);
    }

    [Fact(DisplayName = "acknowledged-return-enables-next-tab")]
    public void AcknowledgedReturnEnablesNextTab()
    {
        var (s, a) = Ready();
        s.BeginEdit();
        s.ChangeDraft("Edited");
        s.RequestSourceReturn();
        Check(s.ConfirmSourceReturn(a, 1300));
        Check(Plan(s, a).Replacement == "Edited");
    }

    [Fact(DisplayName = "long-editor-focus-revalidates-source-on-return")]
    public void LongEditorFocusRevalidatesSourceOnReturn()
    {
        var (s, a) = Ready("A🙂你好B", new(3, 5));
        Check(s.BeginEdit());
        s.ChangeDraft("Edited");
        Check(s.TakeTranslationJob(60000) is null && s.Draft == "Edited");
        Check(s.Prepare(a, Context(a), 60000).Plan is null);
        Check(s.RequestSourceReturn());
        var fresh = a with
        {
            ObservedAtMs = 60000,
            FocusEpoch = a.FocusEpoch + 2,
            Selection = new(6, 6)
        };
        s.Observe(fresh, FocusMember.Source, 60000);
        var plan = s.Prepare(fresh, Context(fresh), 60000).Plan;
        Check(plan?.ResultText == "A🙂EditedB" && plan.Caret == 9);
    }

    [Fact(DisplayName = "unobserved-source-change-during-editor-focus-denies-draft")]
    public void UnobservedSourceChangeDuringEditorFocusDeniesDraft()
    {
        var (s, a) = Ready();
        Check(s.BeginEdit());
        s.ChangeDraft("Edited");
        Check(s.RequestSourceReturn());
        var fresh = a with
        {
            Text = "再见",
            InputEpoch = a.InputEpoch + 1,
            ObservedAtMs = 60000
        };
        s.Observe(fresh, FocusMember.Source, 60000);
        Check(s.State == SessionState.Translating && s.Prepare(fresh, Context(fresh), 60000).Plan is null);
    }

    [Fact(DisplayName = "mouse-return-retains-safe-original-selection")]
    public void MouseReturnRetainsSafeOriginalSelection()
    {
        var (s, a) = Ready("A你好B", new(1, 3));
        s.BeginEdit();
        s.ChangeDraft("Edited");
        var collapsed = a with
        {
            Selection = new(4, 4)
        };
        s.Observe(collapsed, FocusMember.Source, 1300);
        Check(Plan(s, collapsed).ResultText == "AEditedB");
    }

    [Fact(DisplayName = "different-nonempty-selection-cancels-manual-binding")]
    public void DifferentNonemptySelectionCancelsManualBinding()
    {
        var (s, a) = Ready("A你好B", new(1, 3));
        s.BeginEdit();
        s.ChangeDraft("Edited");
        Check(!s.ConfirmSourceReturn(a with
        {
            Selection = new(0, 1)
        }, 1300));
    }

    [Fact(DisplayName = "source-change-during-edit-keeps-editor-but-denies-accept")]
    public void SourceChangeDuringEditKeepsEditorButDeniesAccept()
    {
        var (s, a) = Ready();
        s.BeginEdit();
        s.ChangeDraft("Edited");
        var changed = a with
        {
            Text = "再见"
        };
        s.Observe(changed, FocusMember.Overlay, 1300);
        Check(s.Draft == "Edited" && s.State == SessionState.EditingTranslation);
        Check(!s.ConfirmSourceReturn(changed, 1300));
        Check(s.State == SessionState.Translating);
    }

    [Fact(DisplayName = "manual-append-job-preserves-user-prefix")]
    public void ManualAppendJobPreservesUserPrefix()
    {
        var (s, a) = Ready();
        s.BeginEdit();
        s.ChangeDraft("My greeting.");
        s.ConfirmSourceReturn(a, 1300);
        var appended = Sample("你好再见", at: 1400);
        s.Observe(appended, FocusMember.Source, 1400);
        var job = s.TakeTranslationJob(1700)!;
        Check(job.Kind == TranslationKind.AppendToManual && job.SourceText == "再见" && job.PreservedManualText == "My greeting.");
        Check(s.Complete(job, "My greeting. Goodbye."));
    }

    [Fact(DisplayName = "manual-append-result-cannot-drop-user-edits")]
    public void ManualAppendResultCannotDropUserEdits()
    {
        var (s, a) = Ready();
        s.BeginEdit();
        s.ChangeDraft("My greeting.");
        s.ConfirmSourceReturn(a, 1300);
        s.Observe(Sample("你好再见", at: 1400), FocusMember.Source, 1400);
        Check(!s.Complete(s.TakeTranslationJob(1700)!, "Hello. Goodbye."));
    }

    [Fact(DisplayName = "manual-existing-source-edit-retranslates-full-source")]
    public void ManualExistingSourceEditRetranslatesFullSource()
    {
        var (s, a) = Ready();
        s.BeginEdit();
        s.ChangeDraft("Edited");
        s.ConfirmSourceReturn(a, 1300);
        s.Observe(Sample("您好", at: 1400), FocusMember.Source, 1400);
        var job = s.TakeTranslationJob(1700)!;
        Check(job.Kind == TranslationKind.FullOrSelection && job.SourceText == "您好");
    }

    [Fact(DisplayName = "esc-is-silent-and-does-not-reopen-on-poll")]
    public void EscIsSilentAndDoesNotReopenOnPoll()
    {
        var (s, a) = Ready();
        s.Escape();
        s.Observe(a, FocusMember.Source, 2000);
        Check(s.State == SessionState.NoSession && s.Current!.Text == a.Text && s.Candidate == "");
    }

    [Fact(DisplayName = "new-input-after-esc-can-start-new-session")]
    public void NewInputAfterEscCanStartNewSession()
    {
        var (s, a) = Ready();
        s.Escape();
        s.Observe(a with
        {
            Text = "再见",
            ObservedAtMs = 1500
        }, FocusMember.Source, 1500);
        Check(s.State == SessionState.Translating);
    }

    [Fact(DisplayName = "pause-clears-content-without-creating-a-text-session")]
    public void PauseClearsContentWithoutCreatingATextSession()
    {
        var (s, a) = Ready();
        Check(s.BeginEdit());
        s.ChangeDraft("private draft");
        s.SetPaused(true);
        s.Observe(a, FocusMember.Source, 2000);
        Check(s.Current is null && s.Candidate == "" && s.Preview == "" && s.Draft == "");
        Check(s.State == SessionState.Paused && s.TakeTranslationJob(3000) is null);
        Check(!Presentation.From(s.State, false).Visible);
        var paused = Presentation.From(s.State, true, hasPreview: true, hasPauseAnchor: true);
        Check(paused.Visible && paused.Kind == OverlayKind.Paused);
        Check(paused.Status == "已暂停" && paused.TabHint == "点击恢复");
        Check(!paused.ShowEscape && !paused.EditorOwnsText);
    }

    [Fact(DisplayName = "own-write-does-not-retranslate-on-caret-change")]
    public void OwnWriteDoesNotRetranslateOnCaretChange()
    {
        var (s, a) = Ready();
        var p = Plan(s, a);
        Check(s.BeginWrite(p));
        var after = a with
        {
            Text = p.ResultText,
            Selection = new(p.Caret, p.Caret)
        };
        s.FinishWrite(p, new(WriteStatus.Verified, Denial.None, after));
        s.Observe(after with
        {
            Selection = new(0, 0)
        }, FocusMember.Source, 2000);
        Check(s.State == SessionState.NoSession && s.TakeTranslationJob(2400) is null);
    }

    [Fact(DisplayName = "unknown-write-outcome-quarantines-target")]
    public void UnknownWriteOutcomeQuarantinesTarget()
    {
        var (s, a) = Ready();
        var p = Plan(s, a);
        s.BeginWrite(p);
        s.FinishWrite(p, new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome));
        s.Observe(a, FocusMember.Source, 1500);
        Check(s.State == SessionState.OutcomeUnknown && s.Prepare(a, Context(a), 1500).Plan is null);
    }

    [Fact(DisplayName = "unverified-success-receipt-is-not-success")]
    public void UnverifiedSuccessReceiptIsNotSuccess()
    {
        var (s, a) = Ready();
        var p = Plan(s, a);
        s.BeginWrite(p);
        s.FinishWrite(p, new(WriteStatus.Verified, Denial.None, a));
        Check(s.State == SessionState.OutcomeUnknown);
    }

    [Fact(DisplayName = "write-plan-result-cannot-be-tampered")]
    public void WritePlanResultCannotBeTampered()
    {
        var (s, a) = Ready();
        var p = Plan(s, a) with
        {
            ResultText = "wrong"
        };
        Check(WriteSafety.Validate(p, a, 1300) == Denial.BadPlan);
    }

    [Fact(DisplayName = "write-revalidation-rejects-new-input-epoch")]
    public void WriteRevalidationRejectsNewInputEpoch()
    {
        var (s, a) = Ready();
        Check(WriteSafety.Validate(Plan(s, a), a with
        {
            InputEpoch = 2
        }, 1300) == Denial.EpochChanged);
    }

    [Fact(DisplayName = "family-exit-destroys-source-and-draft")]
    public void FamilyExitDestroysSourceAndDraft()
    {
        var (s, _) = Ready();
        s.BeginEdit();
        s.ChangeDraft("private");
        s.LeaveFamily();
        Check(s.Current is null && s.Candidate == "" && s.Draft == "");
    }

    [Fact(DisplayName = "quarantine-survives-esc-and-pause")]
    public void QuarantineSurvivesEscAndPause()
    {
        var (s, a) = Ready();
        var p = Plan(s, a);
        s.BeginWrite(p);
        s.FinishWrite(p, new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome));
        s.Escape();
        s.SetPaused(true);
        s.SetPaused(false);
        s.Observe(a, FocusMember.Source, 1500);
        Check(s.State == SessionState.OutcomeUnknown);
    }

    [Fact(DisplayName = "abandoned-inflight-write-quarantines-original-target")]
    public void AbandonedInflightWriteQuarantinesOriginalTarget()
    {
        var (s, a) = Ready();
        s.BeginWrite(Plan(s, a));
        s.LeaveFamily();
        s.Observe(a, FocusMember.Source, 1500);
        Check(s.State == SessionState.OutcomeUnknown);
    }

    [Fact(DisplayName = "content-records-do-not-format-private-text")]
    public void ContentRecordsDoNotFormatPrivateText()
    {
        var (s, a) = Ready();
        Check(!a.ToString().Contains(a.Text) && !Plan(s, a).ToString().Contains("Hello"));
    }

    [Fact(DisplayName = "target-auto-is-rejected-case-insensitively")]
    public void TargetAutoIsRejectedCaseInsensitively()
    {
        bool rejected = false;
        try
        {
            new LanguageDirection("zh", "Auto").Validate();
        }
        catch (ArgumentException) { rejected = true; }
        Check(rejected);
    }

    [Fact(DisplayName = "source-aba-with-new-epoch-invalidates-candidate")]
    public void SourceAbaWithNewEpochInvalidatesCandidate()
    {
        var (s, a) = Ready();
        var changed = a with
        {
            InputEpoch = 2
        };
        Check(s.Prepare(changed, Context(changed), 1300).Reason == Denial.EpochChanged);
        s.Observe(changed, FocusMember.Source, 1300);
        Check(s.State == SessionState.Translating);
    }

    [Fact(DisplayName = "source-aba-with-new-epoch-invalidates-edit-binding")]
    public void SourceAbaWithNewEpochInvalidatesEditBinding()
    {
        var (s, a) = Ready();
        s.BeginEdit();
        s.ChangeDraft("Edited");
        Check(!s.ConfirmSourceReturn(a with
        {
            InputEpoch = 2
        }, 1300));
    }

    [Fact(DisplayName = "placement-prefers-above-with-gap")]
    public void PlacementPrefersAboveWithGap()
    {
        var input = new RectPx(100, 400, 400, 70);
        var p = OverlayPlacement.Place(input, new(0, 0, 1920, 1080), 390, 150)!.Value;
        Check(p.Bottom == input.Y - 8 && !p.Intersects(input));
    }

    [Fact(DisplayName = "placement-falls-below-without-covering-input")]
    public void PlacementFallsBelowWithoutCoveringInput()
    {
        var input = new RectPx(20, 20, 200, 60);
        var p = OverlayPlacement.Place(input, new(0, 0, 400, 600), 300, 150)!.Value;
        Check(p.Y == input.Bottom + 8 && !p.Intersects(input));
    }

    [Fact(DisplayName = "placement-supports-negative-monitor-coordinates")]
    public void PlacementSupportsNegativeMonitorCoordinates()
    {
        var work = new RectPx(-1920, 0, 1920, 1080);
        var p = OverlayPlacement.Place(new(-1800, 400, 500, 60), work, 390, 150)!.Value;
        Check(work.Contains(p));
    }

    [Fact(DisplayName = "no-space-hides-instead-of-covering-input")]
    public void NoSpaceHidesInsteadOfCoveringInput()
    {
        Check(OverlayPlacement.Place(new(0, 0, 800, 600), new(0, 0, 800, 600), 390, 150) is null);
    }

    [Fact(DisplayName = "translation-ui-copy-is-quiet")]
    public void TranslationUiCopyIsQuiet()
    {
        var p = Presentation.From(SessionState.Translating, false);
        Check(p.Status == "正在翻译" && p.Dot == StatusDot.Yellow && p.TabHint == "Tab 交还应用");
    }

    [Fact(DisplayName = "ready-ui-copy-and-green-dot")]
    public void ReadyUiCopyAndGreenDot()
    {
        var p = Presentation.From(SessionState.ValidTranslation, true);
        Check(p.Status == "译文就绪" && p.Dot == StatusDot.Green && p.TabHint == "Tab 接受译文");
    }

    [Fact(DisplayName = "editor-is-authority-during-edit-and-return")]
    public void EditorIsAuthorityDuringEditAndReturn()
    {
        Check(Presentation.From(SessionState.EditingTranslation, false).EditorOwnsText && Presentation.From(SessionState.ReturningToSource, false).EditorOwnsText);
    }

    [Fact(DisplayName = "utf16-range-fuzz-preserves-outside-text")]
    public void Utf16RangeFuzzPreservesOutsideText()
    {
        var random = new Random(4107);
        string[] pieces = ["中", "A", "🙂", "\r\n", "é", "e\u0301", "👩‍💻"];
        for (int i = 0; i < 1000; i++)
        {
            string text = string.Concat(Enumerable.Range(0, 8).Select(_ => pieces[random.Next(pieces.Length)]));
            var boundaries = Enumerable.Range(0, text.Length + 1).Where(n => Utf16.IsBoundary(text, n)).ToArray();
            int left = random.Next(boundaries.Length - 1), right = random.Next(left + 1, boundaries.Length);
            var range = new TextRange(boundaries[left], boundaries[right]);
            var result = WriteSafety.Replace(text, range, "译文🙂");
            Check(result.Text == text[..range.Start] + "译文🙂" + text[range.End..]);
            Check(Utf16.IsWellFormed(result.Text) && Utf16.IsBoundary(result.Text, result.Caret));
        }
    }

}
