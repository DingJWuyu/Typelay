using System.IO;
using System.Text.Json;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class TsfKeyRoutingProbe
{
    [Fact(Explicit = true, Timeout = 90000)]
    public async Task ObservePassiveTabAndEndRoutingAndSynchronousSelectionRead()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TypeLay.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string output = Path.Combine(directory.FullName, "artifacts", "tsf-key-routing",
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        var cases = new List<ProductionForegroundWriteTests.TsfKeyRun>();
        string status = "incomplete";
        try
        {
            foreach (string key in new[] { "Tab", "End" })
            {
                var run = new ProductionForegroundWriteTests.TsfKeyRun { Key = key };
                cases.Add(run);
                await ProductionForegroundWriteTests.RunTsfKeyRoutingProbe(run);
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
                consumption = "always-false",
                messagePump = "unchanged-IsDialogMessage",
                composition = "not-tested",
                queuedKeyIdentity = "not-proven",
                cases
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
