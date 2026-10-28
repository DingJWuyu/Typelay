using System.Text;
using System.Text.Json;
using TypeLay.Runtime;
namespace TypeLay.App;

// Private Windows installation storage. A lease is always acquired before the
// model lifecycle/serial gates; Publish never waits for another lock.
internal sealed class ModelInstallationStore(string data, string worker,
    Action<string, string>? replaceFile = null, ModelPackageCatalog? catalog = null)
{
    private readonly Action<string, string>? _replaceFile = replaceFile;
    private readonly ModelPackageCatalog _catalog = catalog ?? ModelCatalog.Packages;
    private readonly string _data = Path.GetFullPath(data);
    private readonly string _worker = Path.GetFullPath(worker);
    // This is only a shipped-component preparation hint. Ready still requires
    // the worker's full production verification and exact hello.
    internal bool CanPrepareWindowsHy => WindowsHyPayload.CanPrepare(_worker);
    internal bool CanPrepareWindowsIndex => WindowsIndexPayload.CanPrepare(_worker);
    private bool CanPreparePackage(ModelPackage package) => package.Backend switch
    {
        "argos" => true,
        "hy-mt-llama-cpu" => CanPrepareWindowsHy,
        "index-translate-llama-cpu" => CanPrepareWindowsIndex,
        _ => false
    };
    internal const string ModelUseLockName = "model-use.lock";
    private readonly HashSet<string> _owned = new(StringComparer.Ordinal);
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownPackages = new(StringComparer.Ordinal);
    public bool CleanupPending
    {
        get; private set;
    }
    internal sealed record InstallationState(string? Generation, bool InventoryKnown,
        IReadOnlyList<ModelPackageSelection> InstalledPackages, IReadOnlyList<ModelPackageSelection> CurrentPackages);
    internal enum PublicationOperation
    {
        Unknown, VerifyCurrent, EncodeManifest, CreateTemporary, WriteTemporary, FlushTemporary,
        CloseTemporary, ReplacePointer, ConfirmPointer, RetainPrevious
    }
    internal enum PublicationError
    {
        Unknown, AccessDenied, SharingViolation, LockViolation, DiskFull, Permission, Integrity, Io
    }
    internal sealed record PublicationDiagnostic(PublicationOperation Operation, PublicationError Error)
    {
        internal static PublicationDiagnostic Capture(PublicationOperation operation, Exception error)
        {
            // A low word alone is not a Win32 code. Never retain the original
            // HRESULT, exception message, path or body in the public diagnostic.
            int? native = error is System.ComponentModel.Win32Exception windows ? windows.NativeErrorCode
                : ((uint)error.HResult & 0xffff0000) == 0x80070000 ? error.HResult & 0xffff : null;
            var code = native switch
            {
                5 => PublicationError.AccessDenied,
                32 => PublicationError.SharingViolation,
                33 => PublicationError.LockViolation,
                39 or 112 => PublicationError.DiskFull,
                _ => error switch
                {
                    UnauthorizedAccessException or System.Security.SecurityException => PublicationError.Permission,
                    InvalidDataException or JsonException => PublicationError.Integrity,
                    IOException => PublicationError.Io,
                    _ => PublicationError.Unknown
                }
            };
            // AccessDenied reports the OS result, not a claim about which reader,
            // permission or filter caused it.
            return new(operation, code);
        }
    }
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
                installation.Started(installation.ActiveId);
                var result = await model.ReloadAsync(active,
                    () => installation.ConfirmCurrent(active) ? ModelPublicationOutcome.Confirmed : ModelPublicationOutcome.Unconfirmed,
                    installation.ConfirmCurrent, ct);
                if (result is ModelPublicationOutcome.Confirmed or ModelPublicationOutcome.Unconfirmed)
                    return;
            }
            if (installation.PreviousId is { } previous && Plan(previous) is { } prior)
            {
                installation.Started(previous);
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
    public InstallationState ReadInstallationState()
    {
        string? generation = null;
        try
        {
            var manifest = Read();
            generation = manifest.Active;
            return ReadInstallationState(manifest);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return new(generation, false, [], []);
        }
    }
    private InstallationState ReadInstallationState(Manifest manifest)
    {
        var paths = Paths(manifest.Active);
        if (manifest.Active == "legacy" && !Path.Exists(paths.Models)
            && !Path.Exists(Path.Combine(_data, "runtime")) && !File.Exists(Pointer))
            return new(null, true, [], []);
        using var inventory = ReadInventory(manifest.Active);
        var root = inventory.RootElement;
        var installed = _catalog.ReadInstallationSelection(root);
        var current = _catalog.ReadActiveSelection(root);
        if (root.GetProperty("schema").GetInt32() is 1 or 2)
        {
            // Legacy directions are intent only. Preserve their exact file manifest
            // when migrating; never substitute catalog coverage or scan model files.
            if (!root.TryGetProperty("files", out var entries) || entries.ValueKind != JsonValueKind.Array
                || entries.GetArrayLength() is 0 or > 40000)
                throw new InvalidDataException("model-inventory");
            var files = new List<ModelPackageFile>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("sha256", out var hashValue) || hashValue.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("model-inventory");
                string path = pathValue.GetString()!, hash = hashValue.GetString()!;
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)
                    || !installed.Any(item => path.StartsWith(item.Directions.Single().Id + "/", StringComparison.Ordinal)
                        && path.Length > item.Directions.Single().Id.Length + 1))
                    throw new InvalidDataException("model-inventory");
                // Validate every entry before grouping so malformed/unselected paths
                // cannot disappear during migration. The shared schema3 parser below
                // also checks each relative path and duplicate file identity.
                files.Add(new(path, hash));
            }
            using var migrated = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                schema = 3,
                packages = installed.Select(item => new
                {
                    id = item.Package.Id,
                    version = item.Package.Version,
                    directions = item.Directions.Select(direction => direction.Id),
                    files = files.Where(file => file.Path.StartsWith(item.Directions.Single().Id + "/", StringComparison.Ordinal))
                        .Select(file => new
                        {
                            path = file.Path[(item.Directions.Single().Id.Length + 1)..],
                            sha256 = file.Sha256
                        })
                }),
                activePackages = current.Select(item => item.Package.Id)
            }));
            installed = _catalog.ReadInstallationSelection(migrated.RootElement);
            current = _catalog.ReadActiveSelection(migrated.RootElement);
        }
        return new(manifest.Active, true, installed, current);
    }
    private JsonDocument ReadInventory(string id)
    {
        string marker = Path.Combine(Paths(id).Models, "installed.json");
        CheckPath(marker);
        if (new FileInfo(marker).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("model-inventory");
        return JsonDocument.Parse(File.ReadAllText(marker));
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
        return _catalog.ReadActiveSelection(inventory.RootElement).SelectMany(item => item.Directions).Select(item => item.Id).ToArray();
    }

    private ModelLoadPlan ReadPlan(string id)
    {
        var paths = Paths(id);
        string marker = Path.Combine(paths.Models, "installed.json");
        CheckPath(marker);
        if (new FileInfo(marker).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("model-inventory");
        using var inventory = JsonDocument.Parse(File.ReadAllText(marker));
        // A missing production closure cannot enter startup or previous-generation
        // repair. Presence alone never bypasses worker verification or hello.
        if (_catalog.ReadActiveSelection(inventory.RootElement).Any(item => !CanPreparePackage(item.Package)))
            throw new InvalidDataException("model-platform-unavailable");
        if (!File.Exists(paths.Python) || !File.Exists(paths.Worker))
            throw new InvalidDataException("model-inventory");
        return ModelLoadPlan.Create(paths, inventory.RootElement, _catalog);
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
        internal PublicationDiagnostic? PublicationFailure
        {
            get; private set;
        }
        internal Lease(ModelInstallationStore store, FileStream handle, Manifest manifest)
        {
            _store = store;
            _handle = handle;
            _manifest = manifest;
            // Only the two exact manifest references can become known old package
            // generations. Do not discover candidates by scanning directories.
            foreach (string id in new[] { manifest.Active, manifest.Previous }.OfType<string>())
            {
                if (id == "legacy")
                    continue;
                try
                {
                    string marker = Path.Combine(store.Root(id), ModelUseLockName);
                    store.CheckPath(marker);
                    if (!File.Exists(marker) || new FileInfo(marker).Length != 0)
                        continue;
                    using var inventory = store.ReadInventory(id);
                    if (inventory.RootElement.GetProperty("schema").GetInt32() != 3)
                        continue;
                    store._catalog.ReadActiveSelection(inventory.RootElement);
                    store._knownPackages.Add(id);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                    or JsonException or KeyNotFoundException or InvalidOperationException)
                {
                }
            }
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
            _store._owned.Add(id);
            return new(this, id, root, _store.Paths(id), Array.AsReadOnly(snapshot));
        }
        public InstallationState InstallationState
        {
            get
            {
                RequireOwner();
                VerifyActive();
                return _store.ReadInstallationState(_manifest);
            }
        }
        public bool UsesPackageInventory
        {
            get
            {
                RequireOwner();
                // A cancelled first preparation can leave the components container.
                // Only the selected inventory/runtime matters here, never that container.
                if (_manifest.Active == "legacy" && _manifest.Previous is null && !File.Exists(_store.Pointer)
                    && !Path.Exists(ActivePaths.Models) && !Path.Exists(Path.Combine(_store._data, "runtime")))
                    return false;
                using var inventory = _store.ReadInventory(_manifest.Active);
                return inventory.RootElement.GetProperty("schema").GetInt32() switch
                {
                    1 or 2 => false,
                    3 => true,
                    _ => throw new InvalidDataException("model-inventory")
                };
            }
        }
        public PreparedInstallation PreparePackages(ModelPackageRequest request)
        {
            RequireOwner();
            ArgumentNullException.ThrowIfNull(request);
            var frozen = request with
            {
                PackageIds = request.PackageIds?.ToArray()!,
                ActivePackages = request.ActivePackages?.ToArray()!,
                AddDirections = request.AddDirections?.ToArray()
            };
            var state = InstallationState;
            if (frozen.ExpectedCurrent != state.Generation)
                throw new InvalidDataException("installation-changed");
            bool ValidIds(string[]? ids) => ids is not null && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length
                && ids.All(id => _store._catalog.Packages.Any(package => package.Id == id));
            bool supplement = frozen.Format == 2;
            if (frozen.Format is not (1 or 2) || !supplement && frozen.AddDirections is not null
                || !Enum.IsDefined(frozen.Operation) || !ValidIds(frozen.PackageIds)
                || !ValidIds(frozen.ActivePackages) || frozen.ActivePackages.Length == 0)
                throw new InvalidDataException("model-package-request");
            if (supplement)
            {
                if (frozen.Operation != ModelPackageOperation.Install
                    || !frozen.PackageIds.SequenceEqual(new[] { "hy-mt2-1-8b-q4-k-m" })
                    || frozen.AddDirections is null || !frozen.AddDirections.SequenceEqual(new[] { "zh_en", "en_zh" })
                    || state.Generation is null)
                    throw new InvalidDataException("model-package-request");
                var target = state.InstalledPackages.SingleOrDefault(item => item.Package.Id == frozen.PackageIds[0]);
                if (target is null || target.Package.Backend != "hy-mt-llama-cpu"
                    || frozen.AddDirections.Any(direction => target.Directions.Any(item => item.Id == direction)
                        || !target.Package.Directions.Any(item => item.Id == direction)))
                    throw new InvalidDataException("model-package-request");
                using var inventory = _store.ReadInventory(_manifest.Active);
                if (inventory.RootElement.GetProperty("schema").GetInt32() != 3
                    || !inventory.RootElement.GetProperty("activePackages").EnumerateArray().Select(item => item.GetString())
                        .SequenceEqual(frozen.ActivePackages))
                    throw new InvalidDataException("model-package-request");
                if (!WindowsHyPayload.CanPrepare(_store._worker, requireEnglish: true))
                    throw new InvalidDataException("model-platform-unavailable");
            }
            var installed = state.InstalledPackages.Select(item => item.Package.Id).ToHashSet(StringComparer.Ordinal);
            var active = state.CurrentPackages.Select(item => item.Package.Id).ToHashSet(StringComparer.Ordinal);
            if (supplement)
            {
                // Exact old selection and a disjoint two-direction increment were checked above.
            }
            else if (frozen.Operation == ModelPackageOperation.Select)
            {
                if (frozen.PackageIds.Length != 0 || frozen.ActivePackages.Any(id => !installed.Contains(id))
                    || active.SetEquals(frozen.ActivePackages))
                    throw new InvalidDataException("model-package-request");
            }
            else
            {
                if (frozen.PackageIds.Length != 1)
                    throw new InvalidDataException("model-package-request");
                string target = frozen.PackageIds[0];
                if (frozen.Operation == ModelPackageOperation.Install)
                {
                    if (installed.Contains(target) || !frozen.ActivePackages.Contains(target, StringComparer.Ordinal)
                        || frozen.ActivePackages.Any(id => id != target && !installed.Contains(id)))
                        throw new InvalidDataException("model-package-request");
                    installed.Add(target);
                }
                else if (!installed.Contains(target) || !active.SetEquals(frozen.ActivePackages)
                    || frozen.Operation == ModelPackageOperation.Remove && active.Contains(target))
                    throw new InvalidDataException("model-package-request");
            }
            var selected = _store._catalog.Packages.Where(package => frozen.ActivePackages.Contains(package.Id, StringComparer.Ordinal)).ToArray();
            if (!(selected.All(package => package.Backend == "argos")
                || selected.Length == 1 && selected[0].Backend is "hy-mt-llama-cpu" or "index-translate-llama-cpu"))
                throw new InvalidDataException("model-package-request");
            if (selected.Any(package => !_store.CanPreparePackage(package))
                || frozen.Operation is ModelPackageOperation.Install or ModelPackageOperation.Repair
                    && frozen.PackageIds.Any(id => !_store.CanPreparePackage(_store._catalog.Packages.Single(package => package.Id == id))))
                throw new InvalidDataException("model-platform-unavailable");
            string[] directions = selected.SelectMany(package =>
            {
                var current = state.InstalledPackages.FirstOrDefault(item => item.Package.Id == package.Id)?.Directions;
                var directions = current ?? ModelCatalog.DefaultInstallationDirections(package);
                var ids = directions.Select(direction => direction.Id);
                return supplement && frozen.PackageIds.Contains(package.Id, StringComparer.Ordinal)
                    ? ids.Concat(frozen.AddDirections!) : ids;
            }).ToArray();
            string id = Guid.NewGuid().ToString("N");
            string root = _store.Root(id);
            Directory.CreateDirectory(root);
            _store._owned.Add(id);
            try
            {
                using var use = new FileStream(Path.Combine(root, ModelUseLockName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                use.Flush(flushToDisk: true);
                _store._knownPackages.Add(id);
            }
            catch
            {
                DeleteGeneration(id);
                throw;
            }
            return new(this, id, root, _store.Paths(id), Array.AsReadOnly(directions), frozen, state);
        }
        internal void Started(string id) => _store._started.Add(id);
        internal void Exited(string id) => _store._started.Remove(id);
        public bool CleanupPending => _store.CleanupPending;
        internal void CleanupFailed() => _store.CleanupPending = true;
        internal void Retain(string id)
        {
            if (_store._started.Contains(id) || _uncertain)
                _store.CleanupPending = true;
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
        internal ModelPublicationOutcome PublishConfirmed(string id, bool remove = false)
        {
            try
            {
                Publish(id, remove);
                return ModelPublicationOutcome.Confirmed;
            }
            catch (ModelSetupException error) when (error.RecoveryFailureCategory is null)
            {
                return ModelPublicationOutcome.Unchanged;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _uncertain = true;
                _store.CleanupPending = true;
                return ModelPublicationOutcome.Unconfirmed;
            }
        }
        private void Publish(string id, bool remove)
        {
            RequireOwner();
            var before = _manifest;
            var after = new Manifest(1, id, remove ? null : before.Active == id ? before.Previous : before.Active);
            void RetainedOldCopies()
            {
                foreach (string old in new[] { before.Active, before.Previous }.OfType<string>())
                    if ((old != "legacy" || Path.Exists(Path.Combine(_store._data, "runtime")) || Path.Exists(Path.Combine(_store._data, "models")))
                        && old != after.Active && old != after.Previous
                        && (!_store._owned.Contains(old) && !_store._knownPackages.Contains(old) || _store._started.Contains(old)))
                        _store.CleanupPending = true;
            }
            string temp = Path.Combine(_store._data, "components-" + Guid.NewGuid().ToString("N") + ".tmp");
            var operation = PublicationOperation.VerifyCurrent;
            try
            {
                VerifyActive();
                operation = PublicationOperation.EncodeManifest;
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(after, JsonOptions));
                operation = PublicationOperation.CreateTemporary;
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    operation = PublicationOperation.WriteTemporary;
                    stream.Write(bytes);
                    operation = PublicationOperation.FlushTemporary;
                    stream.Flush(flushToDisk: true);
                    operation = PublicationOperation.CloseTemporary;
                }
                operation = PublicationOperation.ReplacePointer;
                if (_store._replaceFile is { } replace)
                    replace(temp, _store.Pointer);
                else
                    File.Move(temp, _store.Pointer, overwrite: true);
                operation = PublicationOperation.ConfirmPointer;
                if (_store.Read() != after)
                    throw new IOException("installation-publication-unconfirmed");
                _manifest = after;
                operation = PublicationOperation.RetainPrevious;
                RetainedOldCopies();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                PublicationFailure ??= PublicationDiagnostic.Capture(operation, error);
                Manifest? actual = null;
                try
                {
                    actual = _store.Read();
                }
                catch (Exception readError) when (readError is not OutOfMemoryException) { }
                if (actual == after)
                {
                    _manifest = after;
                    RetainedOldCopies();
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
            // The Local\ mutex is session-local. A different session may own
            // any old generation; never scan or prune unknown installations.
            foreach (string id in _store._owned.Concat(_store._knownPackages).Distinct(StringComparer.Ordinal).ToArray())
                if (id != _manifest.Active && id != _manifest.Previous)
                    DeleteGeneration(id);
        }
        internal void DeleteRemovedPackageCache(ModelPackage package)
        {
            RequireOwner();
            VerifyActive();
            // Cache bytes are not generation bytes: an old model's use handle
            // protects its private copy, while this lease excludes cache writers.
            if (package.Backend == "argos")
                return;
            using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ModelCatalog.ModelLockResource(package))
                ?? throw new InvalidDataException("model-lock-missing");
            using var modelLock = JsonDocument.Parse(stream);
            var fixedModel = modelLock.RootElement;
            if (fixedModel.GetProperty("package_id").GetString() != package.Id
                || fixedModel.GetProperty("version").GetString() != package.Version)
                throw new InvalidDataException("model-lock");
            foreach (string reference in new[] { _manifest.Active, _manifest.Previous }.OfType<string>())
            {
                var state = _store.ReadInstallationState(new Manifest(1, reference, null));
                if (!state.InventoryKnown || state.InstalledPackages.Any(item => item.Package.Id == package.Id))
                    throw new InvalidDataException("model-cache-still-referenced");
            }
            string downloads = Path.Combine(_store._data, "downloads");
            _store.CheckPath(downloads);
            if (!Path.Exists(downloads))
                return;
            if (!Directory.Exists(downloads))
                throw new InvalidDataException("model-cache-path");
            var paths = new List<string>();
            foreach (string kind in new[] { "model", "model_license" })
            {
                var value = fixedModel.GetProperty(kind).GetProperty("file");
                if (value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("model-lock");
                string name = value.GetString()!;
                if (name.Length == 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
                    || name.Any(character => character < 32 || "\\/:<>\"|?*".Contains(character)))
                    throw new InvalidDataException("model-lock");
                string cache = Path.GetFullPath(Path.Combine(downloads, package.Id + "-" + package.Version + "-" + name));
                if (!string.Equals(Path.GetDirectoryName(cache), downloads, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("model-cache-path");
                paths.Add(cache);
                paths.Add(cache + ".part");
            }
            // Check all exact lock-derived paths before deleting anything. Never
            // scan the cache, follow a reparse point, or erase an unrelated version.
            foreach (string path in paths)
            {
                _store.CheckPath(path);
                if (Directory.Exists(path))
                    throw new InvalidDataException("model-cache-path");
            }
            VerifyActive();
            foreach (string path in paths)
                File.Delete(path);
        }
        internal void DeleteGeneration(string id)
        {
            if (_uncertain || _store._started.Contains(id))
            {
                _store.CleanupPending = true;
                return;
            }
            if (!_store._owned.Contains(id) && !_store._knownPackages.Contains(id) || id == "legacy" || id == _manifest.Active || id == _manifest.Previous)
                return;
            try
            {
                string root = _store.Root(id);
                if (_store._knownPackages.Contains(id))
                {
                    string marker = Path.Combine(root, ModelUseLockName);
                    _store.CheckPath(marker);
                    // Keep the exact generation's exclusive handle throughout
                    // deletion of its contents. Another session's owner blocks it.
                    using (var use = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        if (use.Length != 0)
                            throw new InvalidDataException("model-use-lock");
                        ValidateTree(root);
                        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
                            if (entry != marker)
                            {
                                if (Directory.Exists(entry))
                                    Directory.Delete(entry, true);
                                else
                                    File.Delete(entry);
                            }
                    }
                    // The installation lease still excludes a new startup here.
                    File.Delete(marker);
                    Directory.Delete(root);
                }
                else
                    DeleteTree(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { _store.CleanupPending = true; }
        }
        private void ValidateTree(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out string? directory))
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    _store.CheckPath(entry);
                    if (Directory.Exists(entry))
                        pending.Push(entry);
                }
        }
        private void DeleteTree(string root)
        {
            try
            {
                _store.CheckPath(root);
                if (Directory.Exists(root))
                {
                    ValidateTree(root);
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { _store.CleanupPending = true; }
        }
        public void Dispose()
        {
            _handle?.Dispose();
            _handle = null;
        }
    }
    internal sealed class PreparedInstallation(Lease lease, string id, string root, ModelRuntimePaths paths,
        IReadOnlyList<string> directions, ModelPackageRequest? request = null, InstallationState? source = null) : IDisposable
    {
        private bool _published;
        internal ModelPackageRequest? Request => request is null ? null : request with
        {
            PackageIds = request.PackageIds.ToArray(),
            ActivePackages = request.ActivePackages.ToArray(),
            AddDirections = request.AddDirections?.ToArray()
        };
        internal InstallationState? Source { get; } = source;
        public string Root { get; } = root;
        public ModelRuntimePaths Paths { get; } = paths;
        public IReadOnlyList<string> Directions { get; } = directions;
        public bool CleanupPending => lease.CleanupPending;
        internal PublicationDiagnostic? PublicationFailure => lease.PublicationFailure;
        internal ModelReplacementFailure? ReplacementFailure
        {
            get; private set;
        }
        internal void StartingSelfCheck() => lease.Started(id);
        internal void SelfCheckExited() => lease.Exited(id);
        public ModelLoadPlan CreateLoadPlan()
        {
            var plan = lease.ReadPlan(id);
            // Callers of the original preparation API may launch this plan directly.
            // With no exact exit receipt, treat any handed-out plan conservatively.
            lease.Started(id);
            if (!plan.Directions.ToHashSet(StringComparer.Ordinal).SetEquals(Directions))
                throw new InvalidDataException("model-directions");
            return plan;
        }
        public bool ConfirmCurrent(ModelLoadPlan plan) => lease.ConfirmCurrent(plan);
        public ModelPublicationOutcome PublishConfirmed()
        {
            var outcome = lease.PublishConfirmed(id, request?.Operation == ModelPackageOperation.Remove);
            _published = outcome == ModelPublicationOutcome.Confirmed;
            return outcome;
        }
        public async Task<ModelPublicationOutcome> ActivateAsync(ModelClient model, Func<Task<bool>> prepareModelChange, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lease.VerifyActive();
            var plan = lease.ReadPlan(id);
            if (!plan.Directions.ToHashSet(StringComparer.Ordinal).SetEquals(Directions))
                throw new InvalidDataException("model-directions");
            if (!await prepareModelChange())
                throw new ModelSetupException(SetupFailureCategory.Runtime, ModelSetupStage.Activate);
            ct.ThrowIfCancellationRequested();
            lease.Started(id);
            ReplacementFailure = null;
            var outcome = await model.ReloadAsync(plan, PublishConfirmed, ConfirmCurrent, ct,
                failure => ReplacementFailure = failure);
            if (outcome == ModelPublicationOutcome.Confirmed)
                // Keep the lease until retention finishes; cancellation cannot release
                // it halfway through deletion or bring this disk work onto the UI.
                await Task.Run(Complete);
            else
                lease.Retain(id);
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
                if (request?.Operation == ModelPackageOperation.Remove)
                    lease.DeleteRemovedPackageCache(Source!.InstalledPackages.Single(item => item.Package.Id == request.PackageIds.Single()).Package);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { lease.CleanupFailed(); }
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
