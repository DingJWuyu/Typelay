using System.IO;
using System.Text.Json;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class TsfKeyRoutingProbe
{
    [Fact(Explicit = true, Timeout = 90000)]
    public Task ObservePassiveTabAndEndRoutingAndSynchronousSelectionRead() => Observe(sourceKeyboard: false);

    [Fact(Explicit = true, Timeout = 60000)]
    public Task ObserveSourceKeyboardSynchronousSelectionRead() => Observe(sourceKeyboard: true);

    private static async Task Observe(bool sourceKeyboard)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TypeLay.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string output = Path.Combine(directory.FullName, "artifacts", sourceKeyboard ? "tsf-source-key-read" : "tsf-key-routing",
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        var cases = new List<ProductionForegroundWriteTests.TsfKeyRun>();
        string status = "incomplete";
        try
        {
            foreach (string key in sourceKeyboard ? new[] { "Tab" } : new[] { "Tab", "End" })
            {
                var run = new ProductionForegroundWriteTests.TsfKeyRun { Key = key, SourceKeyboardRead = sourceKeyboard };
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
                consumption = sourceKeyboard ? "unchanged-CallNextHookEx-result" : "always-false",
                sourceKeyboardRead = sourceKeyboard,
                contextKeySink = sourceKeyboard ? "not-subscribed" : "passive-observer",
                messagePump = "unchanged-IsDialogMessage",
                composition = "not-tested",
                queuedKeyIdentity = "not-proven",
                cases
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
