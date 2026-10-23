using System.IO;
using System.Text.Json;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class TsfKeyRoutingProbe
{
    [Fact(Explicit = true, Timeout = 90000)]
    public Task ObservePassiveTabAndEndRoutingAndSynchronousSelectionRead() => Observe(decision: false);

    [Fact(Explicit = true, Timeout = 90000)]
    public Task ObserveNaturalTabRejectionAndConsumption() => Observe(decision: true);

    private static async Task Observe(bool decision)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TypeLay.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string output = Path.Combine(directory.FullName, "artifacts", decision ? "tsf-key-decision" : "tsf-key-routing",
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        var cases = new List<ProductionForegroundWriteTests.TsfKeyRun>();
        string status = "incomplete";
        try
        {
            foreach (string scenario in decision ? new[] { "Reject", "Consume" } : new[] { "Tab", "End" })
            {
                var run = new ProductionForegroundWriteTests.TsfKeyRun
                {
                    Key = decision ? "Tab" : scenario,
                    DecisionMode = decision ? scenario : "Observe"
                };
                cases.Add(run);
                await ProductionForegroundWriteTests.RunTsfKeyRoutingProbe(run);
                if (decision && run.Status is "mechanism-not-admitted" or "prerequisite-not-met")
                    break; // A negative result stops this mechanism, not a retry or a new matrix.
            }
            status = "research-observation-complete-not-product-acceptance";
        }
        catch { status = "probe-failed"; throw; }
        finally
        {
            using var file = new FileStream(Path.Combine(output, "result.json"), FileMode.CreateNew);
            JsonSerializer.Serialize(file, new
            {
                diagnosticOnly = true,
                productionReadiness = false,
                status,
                input = "SendInput-automation-not-physical",
                consumption = decision ? "owned-Tab-KeyDown-only-after-synchronous-read" : "always-false",
                completeDecisionPair = decision ? cases.Count == 2
                    && cases[0].Status == "rejection-preserved-navigation"
                    && cases[1].Status == "consumption-suppressed-navigation" : (bool?)null,
                repeatAndUpCycle = "not-proven",
                messagePump = "unchanged-IsDialogMessage",
                composition = "not-tested",
                queuedKeyIdentity = "not-proven",
                cases
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
