using System.Diagnostics;
using System.Text.Json;

namespace TypeLay.Runtime;

public sealed record ModelRuntimePaths(string Python, string Worker, string Models);

public enum ModelPublicationOutcome
{
    Confirmed, Unchanged, Unconfirmed
}

// Frozen installation intent. Only the worker's exact hello grants capability.
public sealed class ModelLoadPlan
{
    private ModelLoadPlan(ModelRuntimePaths paths, int schema, IReadOnlyList<ModelPackageSelection> active)
    {
        Paths = new(Path.GetFullPath(paths.Python), Path.GetFullPath(paths.Worker), Path.GetFullPath(paths.Models));
        InventorySchema = schema;
        ActivePackages = Array.AsReadOnly(active.ToArray());
        Directions = Array.AsReadOnly(active.SelectMany(item => item.Directions).Select(item => item.Id).ToArray());
    }
    public ModelRuntimePaths Paths
    {
        get;
    }
    public int InventorySchema
    {
        get;
    }
    public IReadOnlyList<ModelPackageSelection> ActivePackages
    {
        get;
    }
    public IReadOnlyList<string> Directions
    {
        get;
    }
    public bool UsesHy => ActivePackages.Any(item => item.Package.Backend == "hy-mt-llama-cpu");

    public static ModelLoadPlan Create(ModelRuntimePaths paths, JsonElement inventory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var active = ModelCatalog.ReadActiveSelection(inventory);
        int schema = inventory.GetProperty("schema").GetInt32();
        return new(paths, schema, active);
    }

    internal static ModelLoadPlan ReadLegacy(ModelRuntimePaths paths)
    {
        string marker = Path.Combine(paths.Models, "installed.json");
        if (new FileInfo(marker).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("model-inventory");
        using var document = JsonDocument.Parse(File.ReadAllText(marker));
        var inventory = document.RootElement;
        if (inventory.ValueKind != JsonValueKind.Object || !inventory.TryGetProperty("schema", out var schema)
            || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version is not (1 or 2))
            throw new InvalidDataException("model-inventory");
        return Create(paths, inventory);
    }
}

// Returned only after a child exists and its owned exit cannot be established.
// A missing child handle is an unrecoverable ownership tombstone for this client.
public sealed class ChildProcessCleanupUnconfirmedException(IChildProcess? child = null)
    : IOException("child-cleanup-unconfirmed")
{
    public IChildProcess? Child { get; } = child;
}

// A normal factory failure leaves no child. Any unconfirmed cleanup must use the
// exception above. Dispose is final cleanup, never evidence authorizing a load.
public interface IChildProcess : IDisposable
{
    Process Process
    {
        get;
    }
    // Successful completion proves the platform's owned process family exited.
    // Cancellation/failure retains the owner so the same proof can be retried.
    Task TerminateAndWaitAsync(CancellationToken cancellationToken);
}
