using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.Runtime;
using TypeLay.Protocol;

namespace TypeLay.Linux;

// One current symlink, immutable generations and the same flock used by Python
// preparation. No directory scan, second pointer, or inferred newest generation.
internal sealed class LinuxModelInstallationStore(string home, string data)
{
    private readonly string _components = Path.Combine(data, "components");
    public bool CleanupPending
    {
        get; private set;
    }
    internal sealed class PublicationUnconfirmedException : IOException
    {
    }
    private static readonly string[] FullDirections = ModelCatalog.Directions.Select(direction => direction.Id).ToArray();
    public static bool ValidId(string? id) => id is { Length: 32 }
        && id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void CheckDirectory(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null || File.Exists(path))
            throw new IOException("model-directory");
    }
    private string Root(string id)
    {
        if (!ValidId(id))
            throw new IOException("model-generation");
        CheckDirectory(_components);
        string root = Path.Combine(_components, id);
        CheckDirectory(root);
        return root;
    }
    private string? Current()
    {
        CheckDirectory(_components);
        string path = Path.Combine(_components, "current");
        string? id = new FileInfo(path).LinkTarget;
        if (id is null && (Directory.Exists(path) || File.Exists(path)) || id is not null && !ValidId(id))
            throw new IOException("model-current");
        return id;
    }
    // Marker-only UI observations before startup. These moving paths are never
    // launched; StartModelAsync and activation always resolve a single generation.
    public ModelRuntimePaths InitialPaths() => new(
        Path.Combine(_components, "current", "runtime", "venv", "bin", "python"),
        Path.Combine(home, "worker", "run_worker.py"), Path.Combine(_components, "current", "models"));
    private static readonly ModelRuntimePaths UnavailablePaths = new("", "", "");
    private ModelRuntimePaths Paths(string id)
    {
        string root = Root(id);
        string runtime = Path.Combine(root, "runtime"), models = Path.Combine(root, "models");
        foreach (string path in new[] { runtime, models, Path.Combine(runtime, "venv"), Path.Combine(runtime, "venv", "bin") })
            CheckDirectory(path);
        string python = Path.Combine(runtime, "venv", "bin", "python");
        if (new FileInfo(python).LinkTarget is not null)
        {
            string resolved = new FileInfo(python).ResolveLinkTarget(true)?.FullName
                ?? throw new IOException("model-python");
            if (!resolved.StartsWith(runtime + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException("model-python");
            for (string? parent = Path.GetDirectoryName(resolved); parent != runtime; parent = Path.GetDirectoryName(parent))
            {
                if (parent is null)
                    throw new IOException("model-python");
                CheckDirectory(parent);
            }
            // Keep the venv entry point so Python uses its own pyvenv.cfg.
        }
        return new(python, Path.Combine(home, "worker", "run_worker.py"), models);
    }
    private sealed record Activation(int Schema, string? Previous, IReadOnlyList<string> Directions,
        LoadedModelPackage[]? Packages = null, string? Operation = null,
        LoadedModelPackage[]? RemovedPackages = null, string? RecoveryPrevious = null);
    internal sealed record InstallationState(string? Generation, bool InventoryKnown,
        IReadOnlyList<ModelPackageSelection> InstalledPackages, IReadOnlyList<ModelPackageSelection> CurrentPackages);

    public InstallationState ReadInstallationState()
    {
        string? generation = null;
        try
        {
            generation = Current();
            if (generation is null)
                return new(null, true, [], []);
            using var inventory = ReadInventory(generation);
            var installed = ModelCatalog.ReadInstallationSelection(inventory.RootElement);
            var active = ModelCatalog.ReadActiveSelection(inventory.RootElement);
            var activation = ReadActivation(generation, required: false);
            ValidateActivation(inventory.RootElement.GetProperty("schema").GetInt32(), active, activation);
            if (activation.RemovedPackages?.Any(removed => installed.Any(item => item.Package.Id == removed.Id)) == true)
                throw new InvalidDataException("model-activation");
            if (Current() != generation)
                return new(generation, false, [], []);
            return new(generation, true, installed, active);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            return new(generation, false, [], []);
        }
    }

    private bool HasLegacyEvidence(string id)
    {
        bool known = false;
        try
        {
            using var inventory = ReadInventory(id);
            var root = inventory.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("packages", out _) || root.TryGetProperty("activePackages", out _)
                    || root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Number
                        && schema.TryGetInt32(out int version) && version == 3))
                return false;
            ModelCatalog.ReadInstallationDirections(root);
            known = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException) { }
        try
        {
            var activation = ReadActivation(id, required: true);
            if (activation.Schema == 3)
                return false;
            known = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException) { }
        return known;
    }

    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                    throw new InvalidDataException("model-metadata");
                UniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                UniqueProperties(item);
    }

    private static LoadedModelPackage[] ReadIdentities(JsonElement value, bool allowEmpty)
    {
        if (value.ValueKind != JsonValueKind.Array || !allowEmpty && value.GetArrayLength() == 0)
            throw new InvalidDataException("model-activation");
        List<LoadedModelPackage> result = [];
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 3
                || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("directions", out var directions) || directions.ValueKind != JsonValueKind.Array
                || directions.GetArrayLength() == 0 || directions.EnumerateArray().Any(direction => direction.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("model-activation");
            var package = ModelCatalog.Packages.Packages.FirstOrDefault(package => package.Id == id.GetString());
            string[] ids = directions.EnumerateArray().Select(direction => direction.GetString()!).ToArray();
            if (package is null || package.Version != version.GetString() || result.Any(previous => previous.Id == package.Id)
                || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
                || ids.Any(identity => !package.Directions.Any(direction => direction.Id == identity)))
                throw new InvalidDataException("model-activation");
            result.Add(new(package.Id, package.Version, ids));
        }
        return result.ToArray();
    }
    private Activation ReadActivation(string id, bool required)
    {
        string path = Path.Combine(Root(id), "activation.json");
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException) when (!required) { return new(1, null, Array.AsReadOnly(FullDirections)); }
        catch (DirectoryNotFoundException) when (!required) { return new(1, null, Array.AsReadOnly(FullDirections)); }
        // Legacy metadata may be absent, but existing invalid entries must
        // not acquire legacy semantics.
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("model-activation");
        if (new FileInfo(path).Length > 16384)
            throw new InvalidDataException("model-activation");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        UniqueProperties(root);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out int version) || version is not (1 or 2 or 3)
            || root.EnumerateObject().Count() != (version == 1 ? 2 : version == 2 ? 3 : 6)
            || !root.TryGetProperty("previous", out var previous)
            || previous.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new InvalidDataException("model-activation");
        string? value = previous.GetString();
        if (value is not null && (!ValidId(value) || value == id))
            throw new InvalidDataException("model-activation");
        if (version != 3)
            return new(version, value, ModelCatalog.ReadInstallationDirections(root), RecoveryPrevious: value);
        if (!root.TryGetProperty("packages", out var packages)
            || !root.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.String
            || operation.GetString() is not ("Install" or "Repair" or "Select" or "Remove")
            || !root.TryGetProperty("removedPackages", out var removed)
            || !root.TryGetProperty("recoveryPrevious", out var recovery)
            || recovery.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new InvalidDataException("model-activation");
        var selected = ReadIdentities(packages, allowEmpty: false);
        var removedPackages = ReadIdentities(removed, allowEmpty: true);
        string? recoveryPrevious = recovery.GetString();
        if (recoveryPrevious is not null && (!ValidId(recoveryPrevious) || recoveryPrevious == id)
            || operation.GetString() == "Remove" && (removedPackages.Length != 1 || recoveryPrevious is not null
                || selected.Any(item => item.Id == removedPackages[0].Id))
            || operation.GetString() != "Remove" && (removedPackages.Length != 0 || recoveryPrevious != value))
            throw new InvalidDataException("model-activation");
        return new(version, value, Array.AsReadOnly(selected.SelectMany(item => item.Directions).ToArray()),
            selected, operation.GetString(), removedPackages, recoveryPrevious);
    }
    public IReadOnlyList<string>? ReadCommittedDirections()
    {
        try
        {
            return Current() is { } id ? CommittedDirections(id) : Array.Empty<string>();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            return null;
        }
    }
    private IReadOnlyList<string> CommittedDirections(string id)
    {
        using var inventory = ReadInventory(id);
        var active = ModelCatalog.ReadActiveSelection(inventory.RootElement);
        ValidateActivation(inventory.RootElement.GetProperty("schema").GetInt32(), active, ReadActivation(id, required: false));
        return active.SelectMany(item => item.Directions).Select(item => item.Id).ToArray();
    }
    private JsonDocument ReadInventory(string id)
    {
        string models = Path.Combine(Root(id), "models");
        CheckDirectory(models);
        string marker = Path.Combine(models, "installed.json");
        if ((File.GetAttributes(marker) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || new FileInfo(marker).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("model-inventory");
        var document = JsonDocument.Parse(File.ReadAllText(marker));
        try
        {
            UniqueProperties(document.RootElement);
            return document;
        }
        catch { document.Dispose(); throw; }
    }
    private ModelLoadPlan ReadPlan(string id)
    {
        using var document = ReadInventory(id);
        var paths = Paths(id);
        if (!File.Exists(paths.Python) || !File.Exists(paths.Worker))
            throw new InvalidDataException("model-inventory");
        var plan = ModelLoadPlan.Create(paths, document.RootElement);
        if (plan.InventorySchema == 3)
        {
            var activation = ReadActivation(id, required: true);
            var installed = ModelCatalog.ReadInstallationSelection(document.RootElement);
            if (activation.RemovedPackages!.Any(removed => installed.Any(item => item.Package.Id == removed.Id)))
                throw new InvalidDataException("model-activation");
        }
        return plan;
    }
    private static void ValidateActivation(int inventorySchema, IReadOnlyList<ModelPackageSelection> active, Activation activation)
    {
        if ((inventorySchema == 3) != (activation.Schema == 3)
            || activation.Directions.Count != activation.Directions.Distinct(StringComparer.Ordinal).Count()
            || !active.SelectMany(item => item.Directions).Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal).SetEquals(activation.Directions))
            throw new InvalidDataException("model-activation");
        if (inventorySchema != 3)
            return;
        var selected = activation.Packages!;
        if (selected.Length != active.Count
            || selected.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new InvalidDataException("model-activation");
        foreach (var item in selected)
        {
            var expected = active.FirstOrDefault(candidate => candidate.Package.Id == item.Id);
            if (expected is null || item.Version != expected.Package.Version || item.Directions.Length == 0
                || item.Directions.Any(string.IsNullOrEmpty)
                || item.Directions.Distinct(StringComparer.Ordinal).Count() != item.Directions.Length
                || !item.Directions.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Directions.Select(direction => direction.Id)))
                throw new InvalidDataException("model-activation");
        }
    }
    public Lease Acquire()
    {
        CheckDirectory(_components);
        Directory.CreateDirectory(_components);
        // Linux x86_64 O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC. A fcntl byte
        // lock is not interchangeable with the preparer's flock.
        int fd = Open(Path.Combine(_components, "install.lock"), 2 | 64 | 131072 | 524288, 384);
        if (fd < 0)
            throw new IOException("model-lock");
        try
        {
            if (Flock(fd, 2 | 4) != 0)
                throw new IOException("model-installation-busy");
            return new(this, fd, Current());
        }
        catch { Close(fd); throw; }
    }
    public async Task StartModelAsync(ModelClient model, Lease? existingLease = null)
    {
        ModelLoadPlan? StartupPlan(string id, Activation activation)
        {
            try
            {
                var plan = ReadPlan(id);
                ValidateActivation(plan.InventorySchema, plan.ActivePackages, activation);
                return plan;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException)
            {
                return null;
            }
        }
        try
        {
            using var ownedLease = existingLease is null ? Acquire() : null;
            var lease = existingLease ?? ownedLease!;
            if (lease.Active is null || model.Ready)
                return;
            var active = ReadActivation(lease.Active, required: false);
            if (StartupPlan(lease.Active, active) is { } plan)
            {
                var result = await model.ReloadAsync(plan,
                    () => lease.ConfirmCurrent(plan) ? ModelPublicationOutcome.Confirmed : ModelPublicationOutcome.Unconfirmed,
                    lease.ConfirmCurrent);
                if (result is ModelPublicationOutcome.Confirmed or ModelPublicationOutcome.Unconfirmed)
                    return;
            }
            if (active.RecoveryPrevious is { } previous)
            {
                var recovery = ReadActivation(previous, required: false);
                if (StartupPlan(previous, recovery) is { } prior)
                {
                    var result = await model.ReloadAsync(prior, () => lease.PublishConfirmed(previous), lease.ConfirmCurrent);
                    if (result is ModelPublicationOutcome.Confirmed or ModelPublicationOutcome.Unconfirmed)
                        return;
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or ArgumentException)
        {
        }
        try
        {
            await model.ReloadAsync(UnavailablePaths, () => { });
        }
        catch (OperationCanceledException) { }
    }
    public sealed class Lease : IDisposable
    {
        private readonly LinuxModelInstallationStore _store;
        private int _fd;
        public string? Active
        {
            get;
        }
        internal Lease(LinuxModelInstallationStore store, int fd, string? active)
        {
            _store = store;
            _fd = fd;
            Active = active;
        }
        public Task<ModelPublicationOutcome> ActivateAsync(ModelClient model, string id, CancellationToken ct,
            Func<Task<bool>>? prepareModelChange = null)
        {
            ObjectDisposedException.ThrowIf(_fd < 0, this);
            ct.ThrowIfCancellationRequested();
            var activation = _store.ReadActivation(id, required: true);
            if (activation.Previous != Active || _store.Current() != Active || id == Active)
                throw new IOException("model-preparation-stale");
            var plan = _store.ReadPlan(id);
            ValidateActivation(plan.InventorySchema, plan.ActivePackages, activation);
            if (Active is { } active)
            {
                if (plan.InventorySchema == 3)
                {
                    using var oldInventory = _store.ReadInventory(active);
                    using var newInventory = _store.ReadInventory(id);
                    var installed = ModelCatalog.ReadInstallationSelection(newInventory.RootElement);
                    var prior = ModelCatalog.ReadInstallationSelection(oldInventory.RootElement);
                    var priorActive = ModelCatalog.ReadActiveSelection(oldInventory.RootElement);
                    ValidateActivation(oldInventory.RootElement.GetProperty("schema").GetInt32(), priorActive,
                        _store.ReadActivation(active, required: false));
                    var removed = activation.RemovedPackages!;
                    foreach (var old in prior)
                    {
                        var retained = installed.FirstOrDefault(item => item.Package.Id == old.Package.Id
                            && item.Package.Version == old.Package.Version);
                        if (retained is null)
                        {
                            var removal = removed.FirstOrDefault(item => item.Id == old.Package.Id);
                            if (activation.Operation != "Remove" || removal is null || removal.Version != old.Package.Version
                                || !removal.Directions.ToHashSet(StringComparer.Ordinal).SetEquals(old.Directions.Select(item => item.Id))
                                || priorActive.Any(item => item.Package.Id == old.Package.Id))
                                throw new InvalidDataException("model-package-loss");
                        }
                        else if (!old.Directions.All(direction => retained.Directions.Any(item => item.Id == direction.Id))
                            || activation.Operation != "Install" && old.Directions.Count != retained.Directions.Count
                            || oldInventory.RootElement.GetProperty("schema").GetInt32() == 3
                                && activation.Operation is "Install" or "Select" or "Remove"
                                && !old.Files.OrderBy(item => item.Path, StringComparer.Ordinal)
                                    .SequenceEqual(retained.Files.OrderBy(item => item.Path, StringComparer.Ordinal)))
                            throw new InvalidDataException("model-package-loss");
                    }
                    if (activation.Operation != "Install" && installed.Any(item => !prior.Any(old => old.Package.Id == item.Package.Id))
                        || activation.Operation == "Remove" && (prior.Count - installed.Count != 1
                            || removed.Any(item => installed.Any(retained => retained.Package.Id == item.Id)))
                        || activation.Operation is "Repair" or "Remove"
                            && !plan.ActivePackages.Select(item => item.Package.Id).ToHashSet(StringComparer.Ordinal)
                                .SetEquals(priorActive.Select(item => item.Package.Id)))
                        throw new InvalidDataException("model-package-loss");
                }
                else
                {
                    var selected = plan.Directions.ToHashSet(StringComparer.Ordinal);
                    if (!_store.HasLegacyEvidence(active))
                        throw new IOException("model-installation-unknown");
                    try
                    {
                        using var oldInventory = _store.ReadInventory(active);
                        var root = oldInventory.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("schema", out var schema)
                            && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out int version) && version == 3
                            || _store.ReadActivation(active, required: false).Schema == 3)
                            throw new IOException("model-package-loss");
                        if (!_store.CommittedDirections(active).All(selected.Contains))
                            throw new InvalidDataException("model-direction-loss");
                    }
                    catch (Exception error) when (ModelCatalog.IsCompleteDirectionSet(selected)
                        && error is InvalidDataException or JsonException or FileNotFoundException or DirectoryNotFoundException)
                    { /* Full legacy repair cannot discard a legacy catalog direction. */
                    }
                }
            }
            if (Active is null && plan.InventorySchema == 3 && activation.Operation != "Install")
                throw new InvalidDataException("model-package-operation");
            return ActivatePreparedAsync(model, id, plan, activation, ct, prepareModelChange);
        }
        private async Task<ModelPublicationOutcome> ActivatePreparedAsync(ModelClient model, string id, ModelLoadPlan plan,
            Activation activation, CancellationToken ct, Func<Task<bool>>? prepareModelChange)
        {
            if (prepareModelChange is not null && !await prepareModelChange())
                return ModelPublicationOutcome.Unchanged;
            ct.ThrowIfCancellationRequested();
            if (_store.Current() != Active)
                return ModelPublicationOutcome.Unchanged;
            var outcome = await model.ReloadAsync(plan, () => PublishConfirmed(id), ConfirmCurrent, ct);
            if (outcome == ModelPublicationOutcome.Confirmed && activation.Operation == "Remove")
            {
                _store.CleanupPending = true;
                // Reload can confirm publication yet retain an unconfirmed old
                // owner. Never equate that result with safe physical deletion.
                if (model.FailureCode is null)
                {
                    try
                    {
                        CleanupRemovedHistory(id, activation);
                        _store.CleanupPending = false;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException
                        or InvalidDataException or JsonException or ArgumentException)
                    {
                    }
                }
            }
            return outcome;
        }
        private void CleanupRemovedHistory(string current, Activation activation)
        {
            if (_store.Current() != current || activation.RecoveryPrevious is not null)
                throw new IOException("model-cleanup-unconfirmed");
            HashSet<string> retired = new(StringComparer.Ordinal);
            string? previous = activation.Previous;
            while (previous is not null)
            {
                if (previous == current || !retired.Add(previous))
                    throw new InvalidDataException("model-recovery-cycle");
                using var inventory = _store.ReadInventory(previous);
                var record = _store.ReadActivation(previous, required: false);
                ValidateActivation(inventory.RootElement.GetProperty("schema").GetInt32(),
                    ModelCatalog.ReadActiveSelection(inventory.RootElement), record);
                previous = record.RecoveryPrevious;
            }
            // All references are validated before deleting any directory. A
            // failed deletion changes only the cleanup notice, never publication.
            List<int> uses = [];
            try
            {
                foreach (string generation in retired)
                {
                    int fd = Open(Path.Combine(_store.Root(generation), "use.lock"), 2 | 64 | 131072 | 524288, 384);
                    if (fd < 0)
                        throw new IOException("model-use-lock");
                    uses.Add(fd);
                    if (Fstat(fd, out var status) != 0 || status.Links != 1 || (status.Mode & 61440) != 32768
                        || status.User != Geteuid() || (status.Mode & 63) != 0 || Flock(fd, 2 | 4) != 0)
                        throw new IOException("model-generation-in-use");
                }
                foreach (string generation in retired)
                {
                    if (_store.Current() != current)
                        throw new IOException("model-cleanup-unconfirmed");
                    Directory.Delete(_store.Root(generation), recursive: true);
                }
            }
            finally { foreach (int fd in uses) Close(fd); }
        }
        internal bool ConfirmCurrent(ModelLoadPlan plan)
        {
            ObjectDisposedException.ThrowIf(_fd < 0, this);
            return Active is { } active && _store.Current() == active && _store.Paths(active) == plan.Paths;
        }
        internal ModelPublicationOutcome PublishConfirmed(string id)
        {
            try
            {
                Publish(id);
                return ModelPublicationOutcome.Confirmed;
            }
            catch (PublicationUnconfirmedException) { return ModelPublicationOutcome.Unconfirmed; }
            catch (IOException) { return ModelPublicationOutcome.Unchanged; }
        }
        internal void Publish(string id)
        {
            ObjectDisposedException.ThrowIf(_fd < 0, this);
            if (_store.Current() != Active)
                throw new IOException("model-current-changed");
            string pending = Path.Combine(_store._components, ".current-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.CreateSymbolicLink(pending, id);
                if (Rename(pending, Path.Combine(_store._components, "current")) != 0)
                    throw new IOException("model-publication");
                int directory = Open(_store._components, 65536 | 524288, 0);
                if (directory < 0)
                    throw new IOException("model-publication");
                try
                {
                    if (Fsync(directory) != 0)
                        throw new IOException("model-publication");
                }
                finally { Close(directory); }
                if (_store.Current() != id)
                    throw new IOException("model-publication");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A failed durability/readback step is ambiguous even when the
                // new link is currently visible. Never delete either possibility.
                bool unchanged = false;
                try
                {
                    unchanged = _store.Current() == Active;
                }
                catch (Exception read) when (read is IOException or UnauthorizedAccessException) { }
                if (unchanged)
                    throw new IOException("model-publication-unchanged", error);
                throw new PublicationUnconfirmedException();
            }
            finally
            {
                try
                {
                    File.Delete(pending);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        public void Dispose()
        {
            int fd = Interlocked.Exchange(ref _fd, -1);
            if (fd >= 0)
                Close(fd);
        }
    }
    // Linux x86_64 stat layout, matching this distribution's native ABI.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct FileStatus
    {
        [FieldOffset(16)] public ulong Links;
        [FieldOffset(24)] public uint Mode;
        [FieldOffset(28)] public uint User;
    }
    [DllImport("libc", EntryPoint = "fstat")]
    private static extern int Fstat(int fd, out FileStatus status);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();
    [DllImport("libc", EntryPoint = "rename")]
    private static extern int Rename(string source, string target);
    [DllImport("libc", EntryPoint = "open")]
    private static extern int Open(string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "flock")]
    private static extern int Flock(int fd, int operation);
    [DllImport("libc", EntryPoint = "fsync")]
    private static extern int Fsync(int fd);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);
}
