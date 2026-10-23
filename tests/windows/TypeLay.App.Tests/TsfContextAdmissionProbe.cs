using System.IO;
using System.Text.Json;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class TsfContextAdmissionProbe
{
    [Fact(Explicit = true, Timeout = 60000)]
    public async Task ObserveExistingContextOfSelfOwnedStockEdit()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TypeLay.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string output = Path.Combine(directory.FullName, "artifacts", "tsf-context-admission",
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        var observations = new List<JsonElement>();
        string status = "admission_not_established";
        string? error = null;
        bool processExited = false, normalShutdown = false;
        try
        {
            await ProductionForegroundWriteTests.RunTsfContextAdmissionProbe(
                observations.Add, normal => { processExited = true; normalShutdown = normal; });
            status = "existing_context_observed_not_keyboard_or_write_support";
        }
        catch (Exception failure)
        {
            error = failure.GetType().Name;
            status = normalShutdown && observations.Count > 1
                && observations[^1].GetProperty("Denial").ValueKind == JsonValueKind.String
                && observations[^1].GetProperty("Denial").GetString() != "observation_exception"
                ? "existing_context_not_admitted" : "probe_failed";
            throw;
        }
        finally
        {
            using var file = new FileStream(Path.Combine(output, "result.json"), FileMode.CreateNew);
            JsonSerializer.Serialize(file, new
            {
                diagnosticOnly = true,
                productionReadiness = false,
                status,
                error,
                processExited,
                normalShutdown,
                keyRouting = "not-tested",
                synchronousReadLock = "not-tested",
                composition = "not-tested",
                observations
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
