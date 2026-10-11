using System.Text.Json;
using TypeLay.Core;

internal static class Program
{
    private static readonly List<(string Name, Action Body)> Tests = new();
    private static readonly TargetId A = new(101, 10000, 10, 11, "test", "a", Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly TargetId B = A with { ControlHwnd = 12, ElementToken = "b" };

    private static int Main()
    {
        Register();
        var results = new List<object>();
        int failed = 0;
        foreach (var (name, body) in Tests)
        {
            try { body(); results.Add(new { name, status = "passed" }); }
            catch (Exception ex) { failed++; results.Add(new { name, status = "failed", error_type = ex.GetType().Name }); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { suite = "mvp-core", kind = "deterministic-logic-not-native-input",
            runtime = Environment.Version.ToString(), total = Tests.Count, passed = Tests.Count - failed, failed, results },
            new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }

    private static void Case(string name, Action test) => Tests.Add((name, test));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
    private static InputSnapshot Sample(string text = "你好", TextRange? selected = null, long at = 1000)
        => new(A, text, selected ?? new(text.Length, text.Length), new(100, 400, 450, 70), true,
            FieldAccess.Supported, ImeState.Clear, at, 1, 1);
    private static Session NewSession() => new(new("auto", "en"));
    private static AcceptanceContext Context(InputSnapshot s) => new(FocusMember.Source, false, false, ImeState.Clear, s.InputEpoch, s.FocusEpoch);
    private static (Session Session, InputSnapshot Snapshot) Ready(string text = "你好", TextRange? range = null, string candidate = "Hello")
    {
        var s = NewSession(); var snapshot = Sample(text, range);
        s.Observe(snapshot, FocusMember.Source, 1000);
        var job = s.TakeTranslationJob(1300) ?? throw new InvalidOperationException();
        Check(s.Complete(job, candidate));
        snapshot = snapshot with { ObservedAtMs = 1300 };
        s.Observe(snapshot, FocusMember.Source, 1300);
        return (s, snapshot);
    }
    private static WritePlan Plan(Session s, InputSnapshot snapshot, long now = 1300)
        => s.Prepare(snapshot, Context(snapshot), now).Plan ?? throw new InvalidOperationException();

    private static void Register()
    {
        Case("empty-field-is-silent", () => { var s = NewSession(); s.Observe(Sample(""), FocusMember.Source, 1000); Check(s.State == SessionState.NoSession && s.TakeTranslationJob(2000) is null); });
        Case("whitespace-only-is-silent", () => { var s = NewSession(); s.Observe(Sample(" \r\n "), FocusMember.Source, 1000); Check(s.State == SessionState.NoSession); });
        Case("debounce-no-early-job", () => { var s = NewSession(); s.Observe(Sample(), FocusMember.Source, 1000); Check(s.TakeTranslationJob(1299) is null); Check(s.TakeTranslationJob(1300) is not null); Check(s.TakeTranslationJob(1500) is null); });
        Case("continuous-unpunctuated-input-cannot-starve-model", () =>
        {
            var s = NewSession(); string input = "你好";
            for (long now = 1000; now < 1500; now += 100)
            {
                input += "中"; s.Observe(Sample(input, at: now) with { InputEpoch = now }, FocusMember.Source, now);
                Check(s.TakeTranslationJob(now) is null);
            }
            var job = s.TakeTranslationJob(1500)!;
            Check(job.SourceText == input);
            for (long now = 1600; now <= 2000; now += 100)
            {
                input += "中"; var a = Sample(input, at: now) with { InputEpoch = now };
                s.Observe(a, FocusMember.Source, now);
                if (now == 1600) Check(!s.Complete(job, "Hello") && s.Preview == "Hello" && s.Candidate == "");
                if (now < 2000) Check(s.TakeTranslationJob(now) is null);
            }
            Check(s.TakeTranslationJob(2000)!.SourceText == input);
        });
        Case("partial-preview-never-enables-edit-or-write", () =>
        {
            var s = NewSession(); s.Observe(Sample(), FocusMember.Source, 1000);
            var old = s.TakeTranslationJob(1300)!; var a = Sample("你好朋友", at: 1400) with { InputEpoch = 2 };
            s.Observe(a, FocusMember.Source, 1400); Check(!s.Complete(old, "Hello"));
            Check(s.Preview == "Hello" && s.Candidate == "" && !s.BeginEdit() && s.Prepare(a, Context(a), 1400).Plan is null);
            var latest = s.TakeTranslationJob(1700)!; Check(s.Complete(latest, "Hello friend"));
            a = a with { ObservedAtMs = 1700 }; s.Observe(a, FocusMember.Source, 1700);
            Check(Plan(s, a, 1700).Replacement == "Hello friend");
        });
        Case("preview-only-provider-can-translate-but-never-write", () =>
        {
            var s = NewSession(); var a = Sample() with { WriteSupported = false };
            Check(a.IsUsable); s.Observe(a, FocusMember.Source, 1000);
            Check(s.Complete(s.TakeTranslationJob(1300)!, "Hello"));
            a = a with { ObservedAtMs = 1300 }; s.Observe(a, FocusMember.Source, 1300);
            Check(s.Preview == "Hello" && s.Prepare(a, Context(a), 1300).Reason == Denial.Unsupported);
        });
        Case("preview-only-plan-cannot-be-promoted-by-a-fresh-writable-observation", () =>
        {
            var (s, a) = Ready(); var plan = Plan(s, a);
            Check(WriteSafety.Validate(plan with { Expected = a with { WriteSupported = false } }, a, 1300) == Denial.Unsupported);
        });
        Case("append-retains-preview-but-source-rewrite-clears-it", () =>
        {
            var (s, _) = Ready(); var a = Sample("你好朋友", at: 1400) with { InputEpoch = 2 };
            s.Observe(a, FocusMember.Source, 1400); Check(s.Preview == "Hello" && s.Candidate == "");
            var job = s.TakeTranslationJob(1700)!;
            s.Observe(Sample("再见", at: 1800) with { InputEpoch = 3 }, FocusMember.Source, 1800);
            Check(s.Preview == "" && !s.Complete(job, "Wrong") && s.Preview == "");
        });
        Case("preview-aba-cannot-resurrect-a-canceled-revision", () =>
        {
            var s = NewSession(); s.Observe(Sample(), FocusMember.Source, 1000); var old = s.TakeTranslationJob(1300)!;
            s.Observe(Sample("再见", at: 1400) with { InputEpoch = 2 }, FocusMember.Source, 1400);
            s.Observe(Sample("你好朋友", at: 1500) with { InputEpoch = 3 }, FocusMember.Source, 1500);
            Check(!s.Complete(old, "Hello") && s.Preview == "");
        });
        Case("input-version-catching-up-keeps-preview-without-granting-acceptance", () =>
        {
            var (s, _) = Ready(); var appended = Sample("你好朋友", at: 1400) with { InputEpoch = 2 };
            s.Observe(appended, FocusMember.Source, 1400);
            var caughtUp = appended with { InputEpoch = 3, ObservedAtMs = 1450 };
            s.Observe(caughtUp, FocusMember.Source, 1450);
            Check(s.Preview == "Hello" && s.Candidate == "" && !s.BeginEdit());
            Check(s.Prepare(caughtUp, Context(caughtUp), 1450).Plan is null);
            var job = s.TakeTranslationJob(1750)!; Check(s.Complete(job, "Hello friend"));
            caughtUp = caughtUp with { ObservedAtMs = 1750 }; s.Observe(caughtUp, FocusMember.Source, 1750);
            Check(Plan(s, caughtUp, 1750).Replacement == "Hello friend");
        });
        Case("older-preview-cannot-overwrite-newer-preview", () =>
        {
            var s = NewSession(); s.Observe(Sample(), FocusMember.Source, 1000); var old = s.TakeTranslationJob(1300)!;
            s.Observe(Sample("你好朋友", at: 1400) with { InputEpoch = 2 }, FocusMember.Source, 1400);
            var newer = s.TakeTranslationJob(1700)!;
            s.Observe(Sample("你好朋友明天见", at: 1800) with { InputEpoch = 3 }, FocusMember.Source, 1800);
            Check(!s.Complete(newer, "Hello friend") && s.Preview == "Hello friend");
            Check(!s.Complete(old, "Hello") && s.Preview == "Hello friend");
        });
        Case("known-ime-can-translate-only-the-last-committed-text", () =>
        {
            var s = NewSession(); var a = Sample(); s.Observe(a, FocusMember.Source, 1000);
            var composing = a with { Text = "", Selection = new(0, 0), Ime = ImeState.Composing, ObservedAtMs = 1100 };
            s.Observe(composing, FocusMember.Source, 1100);
            var job = s.TakeTranslationJob(1300)!; Check(job.SourceText == "你好" && job.Basis.Ime == ImeState.Clear);
            Check(!s.Complete(job, "Hello") && s.Preview == "Hello" && s.Candidate == "");
            Check(s.TakeTranslationJob(2000) is null && s.Prepare(composing, Context(composing), 1300).Plan is null);
            Check(Presentation.From(s.State, false, true).Status == "输入中");
        });
        Case("ime-commits-between-polls-refresh-preview-without-an-idle-gap", () =>
        {
            var (s, a) = Ready();
            var composing = a with { Text = "", Selection = new(0, 0), Ime = ImeState.Composing,
                CommittedPrefix = "你好朋友", WriteSupported = false, ObservedAtMs = 1400 };
            s.Observe(composing, FocusMember.Source, 1400);
            var job = s.TakeTranslationJob(1700)!;
            Check(job.SourceText == "你好朋友" && !job.Basis.WriteSupported);
            Check(!s.Complete(job, "Hello friend") && s.Preview == "Hello friend" && s.Candidate == "");
            composing = composing with { CommittedPrefix = "你好朋友明天见", ObservedAtMs = 1800 };
            s.Observe(composing, FocusMember.Source, 1800);
            job = s.TakeTranslationJob(2100)!; Check(job.SourceText == "你好朋友明天见");
            Check(!s.Complete(job, "See you tomorrow") && s.Preview == "See you tomorrow");
            Check(s.Prepare(composing, Context(composing), 2100).Plan is null);
            Check(s.TakeTranslationJob(2500) is null);
        });
        Case("first-observation-can-preview-committed-prefix-during-ime", () =>
        {
            var s = NewSession(); var a = Sample("") with { Ime = ImeState.Composing, CommittedPrefix = "你好" };
            s.Observe(a, FocusMember.Source, 1000); var job = s.TakeTranslationJob(1300)!;
            Check(job.SourceText == "你好"); Check(!s.Complete(job, "Hello") && s.Preview == "Hello");
        });
        Case("rewritten-ime-prefix-discards-stale-translation", () =>
        {
            var (s, a) = Ready();
            a = a with { Text = "", Selection = new(0, 0), Ime = ImeState.Composing, CommittedPrefix = "再见" };
            s.Observe(a, FocusMember.Source, 1400); Check(s.Preview == "");
            Check(s.TakeTranslationJob(1700)!.SourceText == "再见");
        });
        Case("unobservable-ime-can-preview-an-ordinary-value-but-cannot-accept", () =>
        {
            var s = NewSession(); var a = Sample("") with { Ime = ImeState.Unknown,
                PreviewText = "你好", WriteSupported = false };
            s.Observe(a, FocusMember.Source, 1000);
            var job = s.TakeTranslationJob(1300)!;
            Check(job.SourceText == "你好" && !job.Basis.WriteSupported);
            Check(!s.Complete(job, "Hello") && s.Preview == "Hello" && s.Candidate == "");
            Check(!s.BeginEdit() && s.Prepare(a, Context(a), 1300).Plan is null);
            var clear = Sample(at: 1400); s.Observe(clear, FocusMember.Source, 1400);
            Check(s.Candidate == "" && s.Prepare(clear, Context(clear), 1400).Plan is null);
        });
        Case("unobservable-ime-append-refreshes-without-clearing-preview", () =>
        {
            var s = NewSession(); var a = Sample("") with { Ime = ImeState.Unknown,
                PreviewText = "你好", WriteSupported = false };
            s.Observe(a, FocusMember.Source, 1000); s.Complete(s.TakeTranslationJob(1300)!, "Hello");
            a = a with { PreviewText = "你好朋友", InputEpoch = 2, ObservedAtMs = 1400 };
            s.Observe(a, FocusMember.Source, 1400); Check(s.Preview == "Hello");
            var job = s.TakeTranslationJob(1700)!; Check(job.SourceText == "你好朋友");
            Check(!s.Complete(job, "Hello friend") && s.Preview == "Hello friend" && s.Candidate == "");
            s.Observe(a with { Target = B, PreviewText = "再见" }, FocusMember.Source, 1800);
            Check(s.Preview == "" && s.Candidate == "");
        });
        Case("preview-only-escape-suppresses-until-field-value-changes", () =>
        {
            var s = NewSession(); var a = Sample("") with { Ime = ImeState.Unknown,
                PreviewText = "你好", WriteSupported = false };
            s.Observe(a, FocusMember.Source, 1000); s.Complete(s.TakeTranslationJob(1300)!, "Hello");
            s.Escape(); s.Observe(a, FocusMember.Source, 1400);
            Check(s.Preview == "" && s.TakeTranslationJob(1700) is null);
            s.Observe(a with { PreviewText = "你好朋友", InputEpoch = 2 }, FocusMember.Source, 1800);
            Check(s.TakeTranslationJob(2100)!.SourceText == "你好朋友");
        });
        Case("preview-only-no-translation-does-not-block-later-input", () =>
        {
            var s = NewSession(); var a = Sample("") with { Ime = ImeState.Unknown,
                PreviewText = "Hello", WriteSupported = false };
            s.Observe(a, FocusMember.Source, 1000); Check(s.CompleteNoTranslation(s.TakeTranslationJob(1300)!));
            s.Observe(a, FocusMember.Source, 1400); Check(s.TakeTranslationJob(1700) is null);
            s.Observe(a with { PreviewText = "Hello 你好", InputEpoch = 2 }, FocusMember.Source, 1800);
            Check(s.TakeTranslationJob(2100)!.SourceText == "Hello 你好");
        });
        Case("protected-preview-text-is-never-translated", () =>
        {
            var s = NewSession(); var a = Sample("") with { Ime = ImeState.Unknown,
                PreviewText = "synthetic protected text", Access = FieldAccess.Password, WriteSupported = false };
            s.Observe(a, FocusMember.Source, 1000); Check(s.TakeTranslationJob(2000) is null && s.Preview == "");
        });
        Case("unconfirmed-preview-can-regenerate-after-an-inference-failure", () =>
        {
            var s = NewSession(); var a = Sample("") with { Ime = ImeState.Unknown,
                PreviewText = "你好", WriteSupported = false };
            s.Observe(a, FocusMember.Source, 1000); Check(s.Fail(s.TakeTranslationJob(1300)!));
            Check(s.TakeTranslationJob(2000) is null && s.Retranslate(2100));
            var job = s.TakeTranslationJob(2400)!; Check(job.SourceText == "你好");
            Check(!s.Complete(job, "Hello") && s.Preview == "Hello" && s.Candidate == "");
        });
        Case("known-ime-keeps-preview-unknown-ime-without-a-value-clears-it", () =>
        {
            var (s, a) = Ready();
            s.Observe(a with { Text = "", Selection = new(0, 0), Ime = ImeState.Composing }, FocusMember.Source, 1400);
            Check(s.Preview == "Hello" && s.Candidate == "" && !s.BeginEdit());
            s.Observe(a with { Text = "", Selection = new(0, 0), Ime = ImeState.Unknown }, FocusMember.Source, 1500);
            Check(s.Preview == "" && s.TakeTranslationJob(2000) is null);
        });
        Case("new-ime-commit-refreshes-exact-acceptance-candidate", () =>
        {
            var (s, a) = Ready();
            s.Observe(a with { Text = "", Selection = new(0, 0), Ime = ImeState.Composing }, FocusMember.Source, 1400);
            a = Sample("你好朋友", at: 1500) with { InputEpoch = 2 };
            s.Observe(a, FocusMember.Source, 1500);
            Check(s.Candidate == "" && s.Preview == "Hello");
            Check(s.Complete(s.TakeTranslationJob(1800)!, "Hello friend"));
            a = a with { ObservedAtMs = 1800 }; s.Observe(a, FocusMember.Source, 1800);
            Check(Plan(s, a, 1800).Replacement == "Hello friend");
        });
        Case("selection-change-discards-preview-and-inflight-result", () =>
        {
            var (s, a) = Ready();
            a = Sample("你好朋友", at: 1400) with { InputEpoch = 2 }; s.Observe(a, FocusMember.Source, 1400);
            var job = s.TakeTranslationJob(1700)!;
            s.Observe(a with { Selection = new(2, 4), ObservedAtMs = 1800 }, FocusMember.Source, 1800);
            Check(s.Preview == "" && !s.Complete(job, "Wrong") && s.Preview == "");
        });
        Case("pending-candidate-cannot-accept", () => { var s = NewSession(); s.Observe(Sample(), FocusMember.Source, 1000); Check(s.Prepare(Sample(), Context(Sample()), 1000).Plan is null); });
        Case("full-text-plan-and-end-caret", () => { var (s, a) = Ready(); var p = Plan(s, a); Check(p.ResultText == "Hello" && p.Caret == 5 && p.Scope is null); });
        Case("selection-replaces-only-selected-text", () => { var (s, a) = Ready("A你好B", new(1, 3)); var p = Plan(s, a); Check(p.ResultText == "AHelloB" && p.Caret == 6); });
        Case("selection-is-the-model-input", () => { var s = NewSession(); s.Observe(Sample("A你好B", new(1, 3)), FocusMember.Source, 1000); Check(s.TakeTranslationJob(1300)!.SourceText == "你好"); });
        Case("source-change-rejects-before-observer-refresh", () => { var (s, a) = Ready(); var changed = a with { Text = "您好" }; Check(s.Prepare(changed, Context(changed), 1300).Reason == Denial.SourceChanged); });
        Case("target-change-rejects-before-observer-refresh", () => { var (s, a) = Ready(); var changed = a with { Target = B }; Check(s.Prepare(changed, Context(changed), 1300).Reason == Denial.TargetChanged); });
        Case("lifetime-token-prevents-handle-reuse", () => { var (s, a) = Ready(); var changed = a with { Target = a.Target with { Lifetime = Guid.NewGuid() } }; Check(s.Prepare(changed, Context(changed), 1300).Reason == Denial.TargetChanged); });
        Case("changed-selection-rejects-candidate", () => { var (s, a) = Ready("A你好B", new(1, 3)); var changed = a with { Selection = new(0, 3) }; Check(s.Prepare(changed, Context(changed), 1300).Reason == Denial.SelectionChanged); });
        Case("collapsed-caret-move-does-not-retranslate-full-field", () => { var (s, a) = Ready(); var moved = a with { Selection = new(0, 0) }; s.Observe(moved, FocusMember.Source, 1300); Check(s.State == SessionState.ValidTranslation && Plan(s, moved).ResultText == "Hello"); });
        Case("late-model-completion-is-discarded", () => { var s = NewSession(); s.Observe(Sample(), FocusMember.Source, 1000); var old = s.TakeTranslationJob(1300)!; s.Observe(Sample("再见", at: 1301), FocusMember.Source, 1301); Check(!s.Complete(old, "Wrong")); Check(s.State == SessionState.Translating); });
        Case("language-change-rejects-old-job", () => { var s = NewSession(); s.Observe(Sample(), FocusMember.Source, 1000); var old = s.TakeTranslationJob(1300)!; s.SetDirection(new("zh", "ja"), 1300); Check(!s.Complete(old, "Hello")); });
        Case("old-session-response-cannot-enter-new-target", () => { var s = NewSession(); s.Observe(Sample(), FocusMember.Source, 1000); var old = s.TakeTranslationJob(1300)!; s.Observe(Sample() with { Target = B }, FocusMember.Source, 1300); Check(!s.Complete(old, "Wrong")); });
        Case("failed-translation-passes-tab", () => { var s = NewSession(); s.Observe(Sample(), FocusMember.Source, 1000); var job = s.TakeTranslationJob(1300)!; Check(s.Fail(job)); Check(s.Prepare(Sample(), Context(Sample()), 1300).Plan is null); Check(Presentation.From(s.State, false).Status == "暂时无法翻译"); });
        Case("already-target-language-is-not-rewritten", () => { var s = NewSession(); var a = Sample("Hello"); s.Observe(a, FocusMember.Source, 1000); Check(s.CompleteNoTranslation(s.TakeTranslationJob(1300)!)); s.Observe(a, FocusMember.Source, 2000); Check(s.State == SessionState.NoSession && s.TakeTranslationJob(2300) is null); });
        Case("modifier-passes-tab", () => { var (s, a) = Ready(); Check(s.Prepare(a, Context(a) with { Modified = true }, 1300).Reason == Denial.Modifier); });
        Case("injected-tab-is-not-user-acceptance", () => { var (s, a) = Ready(); Check(s.Prepare(a, Context(a) with { Injected = true }, 1300).Reason == Denial.Injected); });
        Case("non-source-focus-passes-tab", () => { var (s, a) = Ready(); Check(s.Prepare(a, Context(a) with { Focus = FocusMember.Overlay }, 1300).Reason == Denial.NotSourceFocus); });
        Case("stale-observation-denies-plan", () => { var (s, a) = Ready(); Check(s.Prepare(a, Context(a), 1451).Reason == Denial.StaleObservation); });
        Case("future-observation-denies-plan", () => { var (s, a) = Ready(); Check(s.Prepare(a, Context(a), 1299).Reason == Denial.StaleObservation); });
        Case("input-epoch-mismatch-denies-plan", () => { var (s, a) = Ready(); Check(s.Prepare(a, Context(a) with { InputEpoch = 2 }, 1300).Reason == Denial.EpochChanged); });
        Case("focus-epoch-mismatch-denies-plan", () => { var (s, a) = Ready(); Check(s.Prepare(a, Context(a) with { FocusEpoch = 2 }, 1300).Reason == Denial.EpochChanged); });
        foreach (var access in new[] { FieldAccess.Password, FieldAccess.Sensitive, FieldAccess.ReadOnly, FieldAccess.Unsupported, FieldAccess.Unknown })
            Case("blocked-access-" + access, () => { var s = NewSession(); var a = Sample() with { Access = access }; s.Observe(a, FocusMember.Source, 1000); Check(s.TakeTranslationJob(2000) is null && s.Prepare(a, Context(a), 1000).Plan is null); });
        foreach (var ime in new[] { ImeState.Unknown, ImeState.Composing })
            Case("no-translation-or-accept-during-ime-" + ime, () => { var s = NewSession(); var a = Sample() with { Ime = ime }; s.Observe(a, FocusMember.Source, 1000); Check(s.TakeTranslationJob(2000) is null); var (ready, fresh) = Ready(); Check(ready.Prepare(fresh, Context(fresh) with { Ime = ime }, 1300).Plan is null); });
        Case("ime-commit-enters-normal-translation", () => { var s = NewSession(); s.Observe(Sample() with { Ime = ImeState.Composing }, FocusMember.Source, 1000); s.Observe(Sample(at: 1500), FocusMember.Source, 1500); Check(s.TakeTranslationJob(1800) is not null); });
        Case("invalid-surrogate-input-blocked", () => { var s = NewSession(); s.Observe(Sample("A\ud800B"), FocusMember.Source, 1000); Check(s.TakeTranslationJob(2000) is null); });
        Case("selection-cannot-split-surrogate-pair", () => { Check(!new TextRange(1, 2).IsValid("🙂")); Check(new TextRange(0, 2).IsValid("🙂")); });
        Case("emoji-adjacent-selection-is-preserved", () => { var (s, a) = Ready("A🙂中文B", new(3, 5), "EN"); var p = Plan(s, a); Check(p.ResultText == "A🙂ENB" && p.Caret == 5); });
        Case("crlf-and-multiline-are-preserved", () => { var (s, a) = Ready("你好\r\n再见", candidate: "Hello\r\nGoodbye"); Check(Plan(s, a).ResultText == "Hello\r\nGoodbye"); });
        Case("single-line-rejects-multiline-output", () => { var (s, a) = Ready(candidate: "Hello\nWorld"); var single = a with { Multiline = false }; Check(s.Prepare(single, Context(single), 1300).Reason == Denial.Multiline); });
        Case("invalid-model-output-cannot-become-candidate", () => { var s = NewSession(); s.Observe(Sample(), FocusMember.Source, 1000); Check(!s.Complete(s.TakeTranslationJob(1300)!, "bad\0text")); Check(s.State == SessionState.TranslationFailed); });
        Case("edit-buffer-is-not-overwritten-by-observations", () => { var (s, a) = Ready(); Check(s.BeginEdit()); s.ChangeDraft("Hi"); s.ChangeDraft("H"); s.Observe(a, FocusMember.Overlay, 1300); s.ChangeDraft("Hey"); Check(s.Draft == "Hey" && s.State == SessionState.EditingTranslation && s.TakeTranslationJob(3000) is null); });
        Case("edit-preserves-leading-trailing-whitespace", () => { var (s, a) = Ready(); s.BeginEdit(); s.ChangeDraft("  Edited  "); s.ConfirmSourceReturn(a, 1300); Check(Plan(s, a).Replacement == "  Edited  "); });
        Case("first-tab-only-requests-source-focus", () => { var (s, a) = Ready(); s.BeginEdit(); s.ChangeDraft("Edited"); Check(s.RequestSourceReturn()); Check(s.State == SessionState.ReturningToSource && s.Prepare(a, Context(a), 1300).Plan is null); });
        Case("acknowledged-return-enables-next-tab", () => { var (s, a) = Ready(); s.BeginEdit(); s.ChangeDraft("Edited"); s.RequestSourceReturn(); Check(s.ConfirmSourceReturn(a, 1300)); Check(Plan(s, a).Replacement == "Edited"); });
        Case("long-editor-focus-revalidates-source-on-return", () =>
        {
            var (s, a) = Ready("A🙂你好B", new(3, 5)); Check(s.BeginEdit()); s.ChangeDraft("Edited");
            Check(s.TakeTranslationJob(60000) is null && s.Draft == "Edited");
            Check(s.Prepare(a, Context(a), 60000).Plan is null); Check(s.RequestSourceReturn());
            var fresh = a with { ObservedAtMs = 60000, FocusEpoch = a.FocusEpoch + 2, Selection = new(6, 6) };
            s.Observe(fresh, FocusMember.Source, 60000);
            var plan = s.Prepare(fresh, Context(fresh), 60000).Plan;
            Check(plan?.ResultText == "A🙂EditedB" && plan.Caret == 9);
        });
        Case("unobserved-source-change-during-editor-focus-denies-draft", () =>
        {
            var (s, a) = Ready(); Check(s.BeginEdit()); s.ChangeDraft("Edited"); Check(s.RequestSourceReturn());
            var fresh = a with { Text = "再见", InputEpoch = a.InputEpoch + 1, ObservedAtMs = 60000 };
            s.Observe(fresh, FocusMember.Source, 60000);
            Check(s.State == SessionState.Translating && s.Prepare(fresh, Context(fresh), 60000).Plan is null);
        });
        Case("mouse-return-retains-safe-original-selection", () => { var (s, a) = Ready("A你好B", new(1, 3)); s.BeginEdit(); s.ChangeDraft("Edited"); var collapsed = a with { Selection = new(4, 4) }; s.Observe(collapsed, FocusMember.Source, 1300); Check(Plan(s, collapsed).ResultText == "AEditedB"); });
        Case("different-nonempty-selection-cancels-manual-binding", () => { var (s, a) = Ready("A你好B", new(1, 3)); s.BeginEdit(); s.ChangeDraft("Edited"); Check(!s.ConfirmSourceReturn(a with { Selection = new(0, 1) }, 1300)); });
        Case("source-change-during-edit-keeps-editor-but-denies-accept", () => { var (s, a) = Ready(); s.BeginEdit(); s.ChangeDraft("Edited"); var changed = a with { Text = "再见" }; s.Observe(changed, FocusMember.Overlay, 1300); Check(s.Draft == "Edited" && s.State == SessionState.EditingTranslation); Check(!s.ConfirmSourceReturn(changed, 1300)); Check(s.State == SessionState.Translating); });
        Case("manual-append-job-preserves-user-prefix", () => { var (s, a) = Ready(); s.BeginEdit(); s.ChangeDraft("My greeting."); s.ConfirmSourceReturn(a, 1300); var appended = Sample("你好再见", at: 1400); s.Observe(appended, FocusMember.Source, 1400); var job = s.TakeTranslationJob(1700)!; Check(job.Kind == TranslationKind.AppendToManual && job.SourceText == "再见" && job.PreservedManualText == "My greeting."); Check(s.Complete(job, "My greeting. Goodbye.")); });
        Case("manual-append-result-cannot-drop-user-edits", () => { var (s, a) = Ready(); s.BeginEdit(); s.ChangeDraft("My greeting."); s.ConfirmSourceReturn(a, 1300); s.Observe(Sample("你好再见", at: 1400), FocusMember.Source, 1400); Check(!s.Complete(s.TakeTranslationJob(1700)!, "Hello. Goodbye.")); });
        Case("manual-existing-source-edit-retranslates-full-source", () => { var (s, a) = Ready(); s.BeginEdit(); s.ChangeDraft("Edited"); s.ConfirmSourceReturn(a, 1300); s.Observe(Sample("您好", at: 1400), FocusMember.Source, 1400); var job = s.TakeTranslationJob(1700)!; Check(job.Kind == TranslationKind.FullOrSelection && job.SourceText == "您好"); });
        Case("esc-is-silent-and-does-not-reopen-on-poll", () => { var (s, a) = Ready(); s.Escape(); s.Observe(a, FocusMember.Source, 2000); Check(s.State == SessionState.NoSession && s.Current!.Text == a.Text && s.Candidate == ""); });
        Case("new-input-after-esc-can-start-new-session", () => { var (s, a) = Ready(); s.Escape(); s.Observe(a with { Text = "再见", ObservedAtMs = 1500 }, FocusMember.Source, 1500); Check(s.State == SessionState.Translating); });
        Case("pause-clears-content-and-hides-ui", () => { var (s, a) = Ready(); s.SetPaused(true); s.Observe(a, FocusMember.Source, 2000); Check(s.Current is null && s.Candidate == "" && !Presentation.From(s.State, false).Visible); });
        Case("own-write-does-not-retranslate-on-caret-change", () => { var (s, a) = Ready(); var p = Plan(s, a); Check(s.BeginWrite(p)); var after = a with { Text = p.ResultText, Selection = new(p.Caret, p.Caret) }; s.FinishWrite(p, new(WriteStatus.Verified, Denial.None, after)); s.Observe(after with { Selection = new(0, 0) }, FocusMember.Source, 2000); Check(s.State == SessionState.NoSession && s.TakeTranslationJob(2400) is null); });
        Case("unknown-write-outcome-quarantines-target", () => { var (s, a) = Ready(); var p = Plan(s, a); s.BeginWrite(p); s.FinishWrite(p, new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)); s.Observe(a, FocusMember.Source, 1500); Check(s.State == SessionState.OutcomeUnknown && s.Prepare(a, Context(a), 1500).Plan is null); });
        Case("unverified-success-receipt-is-not-success", () => { var (s, a) = Ready(); var p = Plan(s, a); s.BeginWrite(p); s.FinishWrite(p, new(WriteStatus.Verified, Denial.None, a)); Check(s.State == SessionState.OutcomeUnknown); });
        Case("write-plan-result-cannot-be-tampered", () => { var (s, a) = Ready(); var p = Plan(s, a) with { ResultText = "wrong" }; Check(WriteSafety.Validate(p, a, 1300) == Denial.BadPlan); });
        Case("write-revalidation-rejects-new-input-epoch", () => { var (s, a) = Ready(); Check(WriteSafety.Validate(Plan(s, a), a with { InputEpoch = 2 }, 1300) == Denial.EpochChanged); });
        Case("family-exit-destroys-source-and-draft", () => { var (s, _) = Ready(); s.BeginEdit(); s.ChangeDraft("private"); s.LeaveFamily(); Check(s.Current is null && s.Candidate == "" && s.Draft == ""); });
        Case("quarantine-survives-esc-and-pause", () => { var (s, a) = Ready(); var p = Plan(s, a); s.BeginWrite(p); s.FinishWrite(p, new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)); s.Escape(); s.SetPaused(true); s.SetPaused(false); s.Observe(a, FocusMember.Source, 1500); Check(s.State == SessionState.OutcomeUnknown); });
        Case("abandoned-inflight-write-quarantines-original-target", () => { var (s, a) = Ready(); s.BeginWrite(Plan(s, a)); s.LeaveFamily(); s.Observe(a, FocusMember.Source, 1500); Check(s.State == SessionState.OutcomeUnknown); });
        Case("content-records-do-not-format-private-text", () => { var (s, a) = Ready(); Check(!a.ToString().Contains(a.Text) && !Plan(s, a).ToString().Contains("Hello")); });
        Case("target-auto-is-rejected-case-insensitively", () => { bool rejected = false; try { new LanguageDirection("zh", "Auto").Validate(); } catch (ArgumentException) { rejected = true; } Check(rejected); });
        Case("source-aba-with-new-epoch-invalidates-candidate", () => { var (s, a) = Ready(); var changed = a with { InputEpoch = 2 }; Check(s.Prepare(changed, Context(changed), 1300).Reason == Denial.EpochChanged); s.Observe(changed, FocusMember.Source, 1300); Check(s.State == SessionState.Translating); });
        Case("source-aba-with-new-epoch-invalidates-edit-binding", () => { var (s, a) = Ready(); s.BeginEdit(); s.ChangeDraft("Edited"); Check(!s.ConfirmSourceReturn(a with { InputEpoch = 2 }, 1300)); });
        RegisterGateTests();
        Case("placement-prefers-above-with-gap", () => { var input = new RectPx(100, 400, 400, 70); var p = OverlayPlacement.Place(input, new(0, 0, 1920, 1080), 390, 150)!.Value; Check(p.Bottom == input.Y - 8 && !p.Intersects(input)); });
        Case("placement-falls-below-without-covering-input", () => { var input = new RectPx(20, 20, 200, 60); var p = OverlayPlacement.Place(input, new(0, 0, 400, 600), 300, 150)!.Value; Check(p.Y == input.Bottom + 8 && !p.Intersects(input)); });
        Case("placement-supports-negative-monitor-coordinates", () => { var work = new RectPx(-1920, 0, 1920, 1080); var p = OverlayPlacement.Place(new(-1800, 400, 500, 60), work, 390, 150)!.Value; Check(work.Contains(p)); });
        Case("no-space-hides-instead-of-covering-input", () => { Check(OverlayPlacement.Place(new(0, 0, 800, 600), new(0, 0, 800, 600), 390, 150) is null); });
        Case("translation-ui-copy-is-quiet", () => { var p = Presentation.From(SessionState.Translating, false); Check(p.Status == "正在翻译" && p.Dot == StatusDot.Yellow && p.TabHint == "Tab 交还应用"); });
        Case("ready-ui-copy-and-green-dot", () => { var p = Presentation.From(SessionState.ValidTranslation, true); Check(p.Status == "译文就绪" && p.Dot == StatusDot.Green && p.TabHint == "Tab 接受译文"); });
        Case("editor-is-authority-during-edit-and-return", () => { Check(Presentation.From(SessionState.EditingTranslation, false).EditorOwnsText && Presentation.From(SessionState.ReturningToSource, false).EditorOwnsText); });
        Case("diagnostics-has-no-free-text-field", () => { Check(typeof(DiagnosticEvent).GetProperties().All(p => p.PropertyType != typeof(string))); });
        Case("utf16-range-fuzz-preserves-outside-text", () =>
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
        });
    }

    private static void RegisterGateTests()
    {
        (AcceptanceGate Gate, AcceptancePermit Permit, GateContext Context) NewGate(bool allowInjectedInput = false)
        {
            var (s, a) = Ready(); var permit = new AcceptancePermit(Plan(s, a), 1, 1, 1300, 1400);
            return (new(allowInjectedInput), permit, new(A, true, ImeState.Clear, false, false, true, 1, 1, 1350));
        }
        Case("gate-consumes-down-up-once", () => { var (g, p, c) = NewGate(); g.Publish(p); Check(g.ProcessTab(true, c).Permit == p); Check(g.ProcessTab(true, c).Permit is null); Check(g.ProcessTab(false, c).Consume); Check(!g.ProcessTab(false, c).Consume); });
        Case("passed-held-tab-cannot-start-accepting-on-repeat", () => { var (g, p, c) = NewGate(); Check(!g.ProcessTab(true, c).Consume); g.Publish(p); Check(!g.ProcessTab(true, c).Consume); Check(!g.ProcessTab(false, c).Consume); Check(g.ProcessTab(true, c).Consume); });
        Case("expired-permit-passes-tab", () => { var (g, p, c) = NewGate(); g.Publish(p); Check(!g.ProcessTab(true, c with { NowMs = 1401 }).Consume); });
        Case("uncertified-provider-lease-passes-tab", () => { var (g, p, c) = NewGate(); g.Publish(p); Check(!g.ProcessTab(true, c with { ProviderLeaseFresh = false }).Consume); });
        Case("gate-unknown-ime-passes-tab", () => { var (g, p, c) = NewGate(); g.Publish(p); Check(!g.ProcessTab(true, c with { Ime = ImeState.Unknown }).Consume); });
        Case("gate-focus-change-passes-tab", () => { var (g, p, c) = NewGate(); g.Publish(p); Check(!g.ProcessTab(true, c with { Target = B }).Consume); });
        Case("gate-injected-input-revokes-permit", () => { var (g, p, c) = NewGate(); g.Publish(p); Check(!g.ProcessTab(true, c with { Injected = true }).Consume); Check(!g.ProcessTab(true, c).Consume); });
        Case("automation-injected-tab-consumes-one-down-up-pair", () =>
        {
            var (g, p, c) = NewGate(true); c = c with { Injected = true }; g.Publish(p);
            Check(g.ProcessTab(true, c).Permit == p);
            g.Publish(p); Check(g.ProcessTab(true, c).Permit is null);
            Check(g.ProcessTab(false, c).Consume && !g.ProcessTab(false, c).Consume);
        });
        foreach (var (name, change) in new (string, Func<GateContext, GateContext>)[]
        {
            ("other-target", c => c with { Target = B }),
            ("no-source-focus", c => c with { SourceFocused = false }),
            ("composing", c => c with { Ime = ImeState.Composing }),
            ("unknown-ime", c => c with { Ime = ImeState.Unknown }),
            ("modifier", c => c with { Modified = true }),
            ("expired-permit", c => c with { NowMs = 1401 }),
            ("stale-provider", c => c with { ProviderLeaseFresh = false }),
            ("new-input", c => c with { InputEpoch = 2 }),
            ("new-focus", c => c with { FocusEpoch = 2 })
        })
            Case("automation-still-rejects-" + name, () =>
            {
                var (g, p, c) = NewGate(true); g.Publish(p);
                var rejected = g.ProcessTab(true, change(c with { Injected = true }));
                Check(!rejected.Consume && rejected.Permit is null);
                Check(!g.ProcessTab(false, c with { Injected = true }).Consume);
            });
        Case("gate-revoke-prevents-accept", () => { var (g, p, c) = NewGate(); g.Publish(p); g.Revoke(); Check(!g.ProcessTab(true, c).Consume); });
        Case("broker-timeout-cannot-extend-tab-permit", () => { var (g, p, _) = NewGate(); bool thrown = false; try { g.Publish(p with { ExpiresAtMs = 6300 }); } catch (ArgumentException) { thrown = true; } Check(thrown); });
    }
}
