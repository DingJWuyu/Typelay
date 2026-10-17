using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.Runtime;

namespace TypeLay.Linux;

// One current symlink, immutable generations and the same flock used by Python
// preparation. No directory scan, second pointer, or inferred newest generation.
internal sealed class LinuxModelInstallationStore(string home, string data)
{
    private readonly string _components = Path.Combine(data, "components");
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
    public async Task StartModelAsync(ModelClient model)
    {
        ModelRuntimePaths StartupPaths(string id)
        {
            try
            {
                return Paths(id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return UnavailablePaths;
            }
        }
        try
        {
            using var lease = Acquire();
            if (lease.Active is null)
                return;
            string? previous = lease.Previous(lease.Active, required: false);
            if (await model.ReloadAsync(StartupPaths(lease.Active), () => { }))
                return;
            if (previous is not null)
            {
                lease.Previous(previous, required: false);
                await model.ReloadAsync(StartupPaths(previous), () => lease.Publish(previous));
            }
            return;
        }
        catch (OperationCanceledException) { return; } // A newer lifecycle owner won.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException)
        {
        }
        // Empty paths cannot contain markers or start a process. Reuse the
        // client's existing failure/old-health semantics without a status setter.
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
        internal string? Previous(string id, bool required)
        {
            string path = Path.Combine(_store.Root(id), "activation.json");
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException) when (!required) { return null; }
            catch (DirectoryNotFoundException) when (!required) { return null; }
            // Legacy metadata may be absent, but existing invalid entries must
            // not acquire legacy semantics.
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("model-activation");
            if (new FileInfo(path).Length > 4096)
                throw new IOException("model-activation");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out int version) || version != 1
                || !root.TryGetProperty("previous", out var previous)
                || previous.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new IOException("model-activation");
            string? value = previous.GetString();
            if (value is not null && (!ValidId(value) || value == id))
                throw new IOException("model-activation");
            return value;
        }
        public Task<bool> ActivateAsync(ModelClient model, string id, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_fd < 0, this);
            ct.ThrowIfCancellationRequested();
            if (Previous(id, required: true) != Active || _store.Current() != Active || id == Active)
                throw new IOException("model-preparation-stale");
            return model.ReloadAsync(_store.Paths(id), () => Publish(id), ct, FullDirections);
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
