using System.Text;
using System.Text.Json;
using TypeLay.Runtime;
namespace TypeLay.App;

// Private Windows installation storage. A lease is always acquired before the
// model lifecycle/serial gates; Publish never waits for another lock.
internal sealed class ModelInstallationStore(string data, string worker,
    Action<string, string>? replaceFile = null)
{
    private readonly Action<string, string>? _replaceFile = replaceFile;
    private readonly string _data = Path.GetFullPath(data);
    private readonly string _worker = Path.GetFullPath(worker);
    private string Pointer => Path.Combine(_data, "components.json");
    private string Components => Path.Combine(_data, "components");
    internal sealed record Manifest(int Format, string Active, string? Previous);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task StartModelAsync(ModelClient model, CancellationToken ct)
    {
        ModelLoadPlan? Plan(string id)
        {
            try
            {
                return ReadPlan(id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                return null;
            }
        }
        try
        {
            using var installation = await AcquireAsync(ct);
            if (model.Ready || installation.IsEmptyLegacy)
                return;
            if (Plan(installation.ActiveId) is { } active)
            {
                var result = await model.ReloadAsync(active,
                    () => installation.ConfirmCurrent(active) ? ModelPublicationOutcome.Confirmed : ModelPublicationOutcome.Unconfirmed,
                    installation.ConfirmCurrent, ct);
                if (result is ModelPublicationOutcome.Confirmed or ModelPublicationOutcome.Unconfirmed)
                    return;
            }
            if (installation.PreviousId is { } previous && Plan(previous) is { } prior)
            {
                var result = await model.ReloadAsync(prior, () => installation.PublishConfirmed(previous), installation.ConfirmCurrent, ct);
                if (result == ModelPublicationOutcome.Confirmed)
                    return;
                if (result == ModelPublicationOutcome.Unconfirmed)
                    throw new ModelSetupException(SetupFailureCategory.Unknown, ModelSetupStage.Activate, SetupFailureCategory.Unknown);
                throw new ModelSetupException(SetupFailureCategory.Runtime, ModelSetupStage.Activate);
            }
            await model.ReloadAsync(new ModelRuntimePaths("", "", ""), () => { }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw ModelSetupException.From(error, ModelSetupStage.Activate);
        }
    }

    public ModelRuntimePaths InitialPaths()
    {
        try
        {
            return Paths(Read().Active);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // An invalid pointer never permits a guessed legacy/newest-directory startup.
            return new(Path.Combine(_data, "unconfirmed", "python.exe"), _worker, Path.Combine(_data, "unconfirmed", "models"));
        }
    }
    public IReadOnlyList<string>? ReadCommittedDirections()
    {
        try
        {
            return ReadDirections(Read());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            return null;
        }
    }
    private IReadOnlyList<string> ReadDirections(Manifest manifest)
    {
        var paths = Paths(manifest.Active);
        if (manifest.Active == "legacy" && !Path.Exists(paths.Models)
            && !Path.Exists(Path.Combine(_data, "runtime")))
            return Array.Empty<string>();
        string marker = Path.Combine(paths.Models, "installed.json");
        CheckPath(marker);
        if (new FileInfo(marker).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("model-inventory");
        using var inventory = JsonDocument.Parse(File.ReadAllText(marker));
        return ModelCatalog.ReadActiveSelection(inventory.RootElement).SelectMany(item => item.Directions).Select(item => item.Id).ToArray();
    }

    private ModelLoadPlan ReadPlan(string id)
    {
        var paths = Paths(id);
        string marker = Path.Combine(paths.Models, "installed.json");
        CheckPath(marker);
        if (new FileInfo(marker).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("model-inventory");
        using var inventory = JsonDocument.Parse(File.ReadAllText(marker));
        // Windows Hy has not passed its real backend gate. Reject before a load
        // plan or child can exist, including startup and previous-generation repair.
        if (ModelCatalog.ReadActiveSelection(inventory.RootElement).Any(item => item.Package.Backend == "hy-mt-llama-cpu"))
            throw new InvalidDataException("model-platform-unavailable");
        if (!File.Exists(paths.Python) || !File.Exists(paths.Worker))
            throw new InvalidDataException("model-inventory");
        return ModelLoadPlan.Create(paths, inventory.RootElement);
    }

    public async Task<Lease> AcquireAsync(CancellationToken ct)
    {
        CheckPath(_data);
        Directory.CreateDirectory(_data);
        string path = Path.Combine(_data, "components.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            CheckPath(path);
            FileStream handle;
            try
            {
                handle = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
            {
                await Task.Delay(100, ct);
                continue;
            }
            try
            {
                return new(this, handle, Read());
            }
            catch { handle.Dispose(); throw; }
        }
    }
    private Manifest Read()
    {
        CheckPath(Pointer);
        if (Directory.Exists(Pointer))
            throw new InvalidDataException("installation-pointer-kind");
        if (!File.Exists(Pointer))
            return new(1, "legacy", null);
        if (new FileInfo(Pointer).Length > 4096)
            throw new InvalidDataException("installation-pointer-size");
        var value = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Pointer), JsonOptions);
        if (value is null || value.Format != 1 || !ValidId(value.Active)
            || value.Previous is not null && (!ValidId(value.Previous) || value.Previous == value.Active))
            throw new InvalidDataException("installation-pointer");
        return value;
    }
    private static bool ValidId(string? id) => id == "legacy" || id is { Length: 32 }
        && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private string Root(string id)
    {
        if (!ValidId(id))
            throw new InvalidDataException("installation-id");
        string root = id == "legacy" ? _data : Path.Combine(Components, id);
        CheckPath(root);
        return root;
    }
    private ModelRuntimePaths Paths(string id)
    {
        string root = Root(id);
        string python = Path.Combine(root, "runtime", "python", "tools", "python.exe");
        string models = Path.Combine(root, "models");
        CheckPath(python);
        CheckPath(models);
        CheckPath(Path.Combine(models, "installed.json"));
        return new(python, _worker, models);
    }
    private void CheckPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full != _data && !full.StartsWith(_data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("installation-path");
        for (string? part = full; part is not null; part = part == _data ? null : Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("installation-link");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    internal sealed class Lease : IDisposable
    {
        private readonly ModelInstallationStore _store;
        private FileStream? _handle;
        private Manifest _manifest;
        private bool _uncertain;
        internal Lease(ModelInstallationStore store, FileStream handle, Manifest manifest)
        {
            _store = store;
            _handle = handle;
            _manifest = manifest;
        }
        public bool IsEmptyLegacy
        {
            get
            {
                RequireOwner();
                if (_manifest.Active != "legacy" || _manifest.Previous is not null || File.Exists(_store.Pointer))
                    return false;
                // Only a pristine first launch is uninstalled. Leftover or partial
                // components still need the normal load-failure/recovery path.
                foreach (string name in new[] { "runtime", "models", "components" })
                {
                    string path = Path.Combine(_store._data, name);
                    _store.CheckPath(path);
                    if (Path.Exists(path))
                        return false;
                }
                return true;
            }
        }
        internal string ActiveId => _manifest.Active;
        internal string? PreviousId => _manifest.Previous;
        public ModelRuntimePaths ActivePaths => _store.Paths(_manifest.Active);
        public IReadOnlyList<string> CommittedDirections
        {
            get
            {
                RequireOwner();
                return _store.ReadDirections(_manifest);
            }
        }
        public PreparedInstallation Prepare(IReadOnlyList<string>? directions = null)
        {
            RequireOwner();
            string[] snapshot = directions?.ToArray() ?? ModelCatalog.Directions.Select(direction => direction.Id).ToArray();
            if (!ModelCatalog.IsValidDirectionSet(snapshot))
                throw new InvalidDataException("model-directions");
            string id = Guid.NewGuid().ToString("N");
            string root = _store.Root(id);
            Directory.CreateDirectory(root);
            return new(this, id, root, _store.Paths(id), Array.AsReadOnly(snapshot));
        }
        private void RequireOwner()
        {
            if (_handle is null)
                throw new ObjectDisposedException(nameof(Lease));
            if (_uncertain)
                throw new ModelSetupException(SetupFailureCategory.Unknown, ModelSetupStage.Activate, SetupFailureCategory.Unknown);
        }
        public void VerifyActive()
        {
            RequireOwner();
            if (_store.Read() != _manifest)
                throw new InvalidDataException("installation-changed");
        }
        internal bool ConfirmCurrent(ModelLoadPlan plan)
        {
            RequireOwner();
            return _store.Read() == _manifest && _store.Paths(_manifest.Active) == plan.Paths;
        }
        internal ModelLoadPlan ReadPlan(string id)
        {
            RequireOwner();
            return _store.ReadPlan(id);
        }
        internal ModelPublicationOutcome PublishConfirmed(string id)
        {
            try
            {
                Publish(id);
                return ModelPublicationOutcome.Confirmed;
            }
            catch (ModelSetupException error) when (error.RecoveryFailureCategory is null)
            {
                return ModelPublicationOutcome.Unchanged;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _uncertain = true;
                return ModelPublicationOutcome.Unconfirmed;
            }
        }
        private void Publish(string id)
        {
            RequireOwner();
            var before = _manifest;
            var after = new Manifest(1, id, before.Active == id ? before.Previous : before.Active);
            string temp = Path.Combine(_store._data, "components-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                VerifyActive();
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(after, JsonOptions));
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (_store._replaceFile is { } replace)
                    replace(temp, _store.Pointer);
                else
                    File.Move(temp, _store.Pointer, overwrite: true);
                if (_store.Read() != after)
                    throw new IOException("installation-publication-unconfirmed");
                _manifest = after;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Manifest? actual = null;
                try
                {
                    actual = _store.Read();
                }
                catch (Exception readError) when (readError is not OutOfMemoryException) { }
                if (actual == after)
                {
                    _manifest = after;
                    return;
                }
                var failure = ModelSetupException.From(error, ModelSetupStage.Activate);
                if (actual == before)
                    throw failure;
                // Do not prune any member of either plausible manifest (A/Z or C/A).
                _uncertain = true;
                throw new ModelSetupException(failure.Category, ModelSetupStage.Activate, SetupFailureCategory.Unknown);
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        public void Prune()
        {
            RequireOwner();
            VerifyActive();
            if (Directory.Exists(_store.Components))
                foreach (string root in Directory.GetDirectories(_store.Components))
                {
                    string id = Path.GetFileName(root);
                    if (ValidId(id) && id != "legacy" && id != _manifest.Active && id != _manifest.Previous)
                        DeleteGeneration(id);
                }
            // Legacy is a real previous generation until two confirmed upgrades.
            // Only private component paths are retired, never settings/downloads or Argos packages.
            if (_manifest.Active != "legacy" && _manifest.Previous != "legacy")
            {
                DeleteTree(Path.Combine(_store._data, "runtime"));
                DeleteTree(Path.Combine(_store._data, "models"));
            }
        }
        internal void DeleteGeneration(string id)
        {
            if (_uncertain || id == "legacy" || id == _manifest.Active || id == _manifest.Previous)
                return;
            try
            {
                DeleteTree(_store.Root(id));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        private void DeleteTree(string root)
        {
            try
            {
                _store.CheckPath(root);
                if (Directory.Exists(root))
                {
                    // Inspect without following links before recursive deletion.
                    var pending = new Stack<string>();
                    pending.Push(root);
                    while (pending.TryPop(out string? directory))
                        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                        {
                            _store.CheckPath(entry);
                            if (Directory.Exists(entry))
                                pending.Push(entry);
                        }
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        public void Dispose()
        {
            _handle?.Dispose();
            _handle = null;
        }
    }
    internal sealed class PreparedInstallation(Lease lease, string id, string root, ModelRuntimePaths paths, IReadOnlyList<string> directions) : IDisposable
    {
        private bool _published;
        public string Root { get; } = root;
        public ModelRuntimePaths Paths { get; } = paths;
        public IReadOnlyList<string> Directions { get; } = directions;
        public ModelLoadPlan CreateLoadPlan()
        {
            var plan = lease.ReadPlan(id);
            if (!plan.Directions.ToHashSet(StringComparer.Ordinal).SetEquals(Directions))
                throw new InvalidDataException("model-directions");
            return plan;
        }
        public bool ConfirmCurrent(ModelLoadPlan plan) => lease.ConfirmCurrent(plan);
        public ModelPublicationOutcome PublishConfirmed()
        {
            var outcome = lease.PublishConfirmed(id);
            _published = outcome == ModelPublicationOutcome.Confirmed;
            return outcome;
        }
        public void Complete()
        {
            if (!_published)
                throw new InvalidOperationException("installation-not-published");
            // Retention is best effort after actual takeover, never a rollback.
            try
            {
                lease.Prune();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        public void Dispose()
        {
            try
            {
                if (!_published)
                    lease.DeleteGeneration(id);
            }
            finally { lease.Dispose(); }
        }
    }
}
