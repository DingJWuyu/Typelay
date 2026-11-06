using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class UnchangedSettingsSessionTests
{
    [Theory]
    [InlineData("same-record")]
    [InlineData("rebuilt-exclusions")]
    [InlineData("normalized-shortcuts")]
    public async Task SavingUnchangedSettingsPreservesTheManualDraftAndActiveWake(string submission)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        s.Settings.Update(s.Settings.Value with
        {
            ExcludedApplications = ["private-app"]
        });
        s.Tick();
        s.Command("wake", 1);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("manual draft");
        s.Tick();
        var token = s.View.Token;
        Guid generation = s.Input.CommandGeneration;

        Assert.True((await s.Settings.CommitAsync(s.Settings.Revision, Unchanged(s.Settings.Value, submission),
            s.Controller.PrepareSettingsChangeAsync, cancellationToken: TestContext.Current.CancellationToken)).Saved);
        s.Tick();

        Assert.Equal(token, s.View.Token);
        Assert.Equal(generation, s.Input.CommandGeneration);
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.Equal("manual draft", s.View.Preview);
        Assert.NotNull(s.Input.Permit);
        Assert.Single(s.Model.Jobs);

        // Saving does not exempt the session from actual focus loss.
        s.FocusAnotherField();
        s.Tick();
        Assert.False(s.View.Visible);
        Assert.Equal("", s.View.Draft);
        Assert.Null(s.Input.Permit);
        s.Tick(Limits.DebounceMs);
        Assert.Single(s.Model.Jobs);
    }

    [Theory]
    [InlineData("same-record")]
    [InlineData("rebuilt-exclusions")]
    [InlineData("normalized-shortcuts")]
    public async Task SavingUnchangedSettingsKeepsPracticeAndItsPendingDirectReplacement(string submission)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        s.Settings.Update(s.Settings.Value with
        {
            ExcludedApplications = ["private-app"]
        });
        Guid run = BindPractice(s);
        s.Command("wake", 1);
        s.ReadyCandidate();
        var progress = Assert.IsType<OnDemandPracticeProgress>(s.Controller.PracticeProgress);
        Assert.True(progress.PreviewCompleted);
        Assert.True((await s.Settings.CommitAsync(s.Settings.Revision, Unchanged(s.Settings.Value, submission),
            s.Controller.PrepareSettingsChangeAsync, cancellationToken: TestContext.Current.CancellationToken)).Saved);
        Assert.Equal(progress, s.Controller.PracticeProgress);
        Assert.Equal(run, s.Controller.PracticeProgress!.RunId);

        s.Service.PendingDirectWrite = new();
        s.Command("direct", 2);
        s.Tick();
        Assert.True((await s.Settings.CommitAsync(s.Settings.Revision, Unchanged(s.Settings.Value, submission),
            s.Controller.PrepareSettingsChangeAsync, cancellationToken: TestContext.Current.CancellationToken)).Saved);
        s.CompleteTranslation("replacement");
        var plan = Assert.Single(s.Service.DirectWrites);
        Assert.True((await s.Settings.CommitAsync(s.Settings.Revision, Unchanged(s.Settings.Value, submission),
            s.Controller.PrepareSettingsChangeAsync, cancellationToken: TestContext.Current.CancellationToken)).Saved);
        s.Service.PendingDirectWrite.SetResult(new(WriteStatus.Verified, Denial.None,
            plan.Expected with
            {
                Text = plan.ResultText,
                Selection = new(plan.Caret, plan.Caret)
            }));
        s.Tick();
        Assert.True(s.Controller.PracticeProgress!.Completed);
        Assert.Equal(run, s.Controller.PracticeProgress.RunId);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public async Task FailedUnchangedSaveLeavesTheDraftAndWakeUsableForRetry()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        s.Tick();
        s.Command("wake", 1);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("manual draft");
        s.Tick();
        var token = s.View.Token;
        s.BlockSettingsWrites();
        var result = await s.Settings.CommitAsync(s.Settings.Revision, s.Settings.Value,
            s.Controller.PrepareSettingsChangeAsync, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Saved);
        Assert.IsType<UnauthorizedAccessException>(Assert.IsType<SettingsPersistenceException>(result.Error).InnerException);
        s.Tick();
        Assert.Equal(token, s.View.Token);
        s.AllowSettingsWrites();
        Assert.True((await s.Settings.CommitAsync(s.Settings.Revision, s.Settings.Value,
            s.Controller.PrepareSettingsChangeAsync, cancellationToken: TestContext.Current.CancellationToken)).Saved);
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.Equal("manual draft", s.View.Preview);
        Assert.NotNull(s.Input.Permit);
    }

    public static TheoryData<string> ChangedFields => new()
    {
        "schema", "enabled", "source", "target", "exclusions", "startup", "theme", "onboarding",
        "experimental-writeback", "mode", "wake", "direct", "accept", "exit"
    };

    [Theory]
    [MemberData(nameof(ChangedFields))]
    public async Task EveryRealSettingsChangeStillRetiresTheDraftWakeAndPractice(string field)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true, setStartup: _ => { });
        Guid run = BindPractice(s);
        s.Command("wake", 1);
        s.ReadyCandidate();
        Assert.True(s.Controller.PracticeProgress!.PreviewCompleted);
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("manual draft");
        s.Tick();
        var token = s.View.Token;
        Guid generation = s.Input.CommandGeneration;
        int changes = 0;
        s.Settings.Changed += () => changes++;

        Assert.True((await s.Settings.CommitAsync(s.Settings.Revision, Changed(s.Settings.Value, field),
            s.Controller.PrepareSettingsChangeAsync, cancellationToken: TestContext.Current.CancellationToken)).Saved);

        Assert.Equal(1, changes);
        Assert.Equal("", s.View.Draft);
        Assert.Null(s.Input.Permit);
        Assert.NotEqual(generation, s.Input.CommandGeneration);
        Assert.Null(s.Controller.PracticeProgress);
        Assert.False(s.Controller.BindOnDemandPractice(run, s.Source.Target));
        s.View.ReturnRequested!(token);
        Assert.Equal(0, s.Service.Returns);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [MemberData(nameof(ChangedFields))]
    public async Task EveryRealSettingsChangeStillCancelsAnUnsubmittedDirectReplacement(string field)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true, setStartup: _ => { });
        s.Command("direct", 1);
        s.Tick();
        Assert.Single(s.Model.Jobs);

        Assert.True((await s.Settings.CommitAsync(s.Settings.Revision, Changed(s.Settings.Value, field),
            s.Controller.PrepareSettingsChangeAsync, cancellationToken: TestContext.Current.CancellationToken)).Saved);
        s.CompleteTranslation("obsolete replacement");
        s.Tick();

        Assert.Empty(s.Service.DirectWrites);
        Assert.Empty(s.Service.Writes);
        Assert.Contains("设置已改变", s.Controller.OperationStatus);
    }

    private static Guid BindPractice(SessionScenario s)
    {
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginOnDemandPractice(run));
        Assert.True(s.Controller.BindOnDemandPractice(run, s.Source.Target));
        s.Tick();
        return run;
    }

    private static UserSettings Unchanged(UserSettings settings, string submission) => submission switch
    {
        "same-record" => settings,
        "rebuilt-exclusions" => settings with { ExcludedApplications = settings.Excluded.ToArray() },
        "normalized-shortcuts" => settings with
        {
            WakeShortcut = "shift+ctrl+f9",
            DirectShortcut = "shift+ctrl+f10",
            AcceptShortcut = "tab",
            ExitShortcut = "esc"
        },
        _ => throw new ArgumentException("Unknown submission.")
    };

    private static UserSettings Changed(UserSettings settings, string field) => field switch
    {
        // Update historically accepts Schema; it must not be omitted from equality.
        "schema" => settings with { Schema = 2 },
        "enabled" => settings with { Enabled = false },
        "source" => settings with { Source = "zh" },
        "target" => settings with { Target = "zh" },
        "exclusions" => settings with { ExcludedApplications = ["private-app"] },
        "startup" => settings with { Startup = true },
        "theme" => settings with { Theme = "dark" },
        "onboarding" => settings with { OnboardingComplete = true },
        "experimental-writeback" => settings with { ExperimentalWriteback = false },
        "mode" => settings with { Mode = WorkMode.Automatic },
        "wake" => settings with { WakeShortcut = "Alt+F8" },
        "direct" => settings with { DirectShortcut = "Alt+F8" },
        "accept" => settings with { AcceptShortcut = "F8" },
        "exit" => settings with { ExitShortcut = "F7" },
        _ => throw new ArgumentException("Unknown setting.")
    };
}
