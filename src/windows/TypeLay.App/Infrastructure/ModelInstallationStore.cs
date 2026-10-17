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
        try
        {
            using var installation = await AcquireAsync(ct);
            // Settings can finish an explicit installation while startup waits for the store.
            if (model.Ready || installation.IsEmptyLegacy)
                return;
            if (await model.ReloadAsync(installation.ActivePaths, installation.VerifyActive, ct))
                return;
            if (installation.PreviousPaths is { } previous)
            {
                // The same verified candidate is activated only after the pointer is restored.
                if (!await model.ReloadAsync(previous, installation.RestorePrevious, ct))
                    throw new ModelSetupException(SetupFailureCategory.Runtime, ModelSetupStage.Activate);
            }
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
        public ModelRuntimePaths ActivePaths => _store.Paths(_manifest.Active);
        public ModelRuntimePaths? PreviousPaths => _manifest.Previous is { } previous ? _store.Paths(previous) : null;
        public PreparedInstallation Prepare()
        {
            RequireOwner();
            string id = Guid.NewGuid().ToString("N");
            string root = _store.Root(id);
            Directory.CreateDirectory(root);
            return new(this, id, root, _store.Paths(id));
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
        public void RestorePrevious()
        {
            if (_manifest.Previous is not { } previous)
                throw new InvalidOperationException("installation-no-previous");
            Publish(previous);
        }
        internal void Publish(string id)
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
    internal sealed class PreparedInstallation(Lease lease, string id, string root, ModelRuntimePaths paths) : IDisposable
    {
        private bool _published;
        public string Root { get; } = root;
        public ModelRuntimePaths Paths { get; } = paths;
        public void Publish()
        {
            lease.Publish(id);
            _published = true;
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
