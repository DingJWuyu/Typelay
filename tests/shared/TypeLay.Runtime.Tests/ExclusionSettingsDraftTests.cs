using TypeLay.Runtime;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class ExclusionSettingsDraftTests
{
    [Theory]
    [InlineData("Keep,Remove", "KEEP,Added", "External,keep,REMOVE", "External,keep,Added")]
    [InlineData("RemovedElsewhere,Keep", "RemovedElsewhere,Keep,Added", "Keep,External", "Keep,External,Added")]
    [InlineData("Keep", "keep,ADDED,added", "Keep,Added,External,external", "Keep,Added,External")]
    [InlineData("Keep,Other", "other,KEEP,KEEP", "Other,Keep,External", "Other,Keep,External")]
    [InlineData("Keep,Remove", "Keep", "External,remove,Keep", "External,Keep")]
    [InlineData("", "tool.exe,/opt/tool,tool", "External", "External,tool.exe,/opt/tool,tool")]
    public void AppliesOnlyThisDraftsChangesAndPreservesLatestOrderAndSpelling(
        string baseline, string values, string latest, string expected)
    {
        var settings = new UserSettings(Enabled: false, Target: "zh", Theme: "dark",
            ExcludedApplications: Split(latest));
        var draft = new ExclusionSettingsDraft(Split(baseline), Split(values));

        Assert.True(draft.TryApply(settings, out var updated));
        Assert.Equal(Split(expected), updated!.Excluded);
        Assert.Equal(settings with
        {
            ExcludedApplications = null
        }, updated with
        {
            ExcludedApplications = null
        });
        Assert.Equal(Split(latest), settings.Excluded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-long")]
    public void RejectsInvalidDesiredValuesEvenWhenAlreadyInTheBaseline(string? invalid)
    {
        invalid = invalid == "too-long" ? new string('x', 129) : invalid;
        var values = new[] { invalid! };
        var draft = new ExclusionSettingsDraft(values, values);

        Assert.False(draft.TryApply(new UserSettings(), out var updated));
        Assert.Null(updated);
        Assert.Equal(values, draft.Values);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ValidatesTheMergedLimitWithoutDiscardingExternalAdditions(bool removeOne, bool valid)
    {
        var latest = new UserSettings(ExcludedApplications: Enumerable.Range(0, 256).Select(n => $"app-{n}").ToArray());
        var draft = new ExclusionSettingsDraft(removeOne ? ["app-0"] : [], ["new-app"]);

        Assert.Equal(valid, draft.TryApply(latest, out var updated));
        Assert.Equal(256, latest.Excluded.Length);
        if (valid)
        {
            Assert.Equal(256, updated!.Excluded.Length);
            Assert.Equal("app-1", updated.Excluded[0]);
            Assert.Equal("new-app", updated.Excluded[^1]);
        }
        else
            Assert.Null(updated);
    }

    [Fact]
    public void SnapshotsInputsAndDoesNotExposeMutableDraftOrResultArrays()
    {
        string[] baseline = ["keep", "remove"], values = ["keep", "add"];
        var draft = new ExclusionSettingsDraft(baseline, values);
        baseline[1] = "add";
        values[1] = "other";
        Assert.Throws<NotSupportedException>(() => ((IList<string>)draft.Values)[0] = "mutated");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)draft.Baseline)[0] = "mutated");
        var settings = new UserSettings(ExcludedApplications: ["keep", "remove", "external"]);

        Assert.True(draft.TryApply(settings, out var updated));
        Assert.Equal(["keep", "external", "add"], updated!.Excluded);
        updated.Excluded[0] = "changed-result";
        Assert.True(draft.TryApply(settings, out var retried));
        Assert.Equal(["keep", "external", "add"], retried!.Excluded);
        Assert.Equal(["keep", "add"], draft.Values);
    }

    [Fact]
    public void EmptyDraftLeavesLatestExclusionsAloneAndRejectsInvalidRetainedValues()
    {
        var draft = new ExclusionSettingsDraft(null, null);
        Assert.True(draft.TryApply(new UserSettings(ExcludedApplications: ["external"]), out var updated));
        Assert.Equal(["external"], updated!.Excluded);
        Assert.False(draft.TryApply(new UserSettings(ExcludedApplications: [" "]), out updated));
        Assert.Null(updated);
    }

    private static string[] Split(string value) => value.Length == 0 ? [] : value.Split(',');
}
