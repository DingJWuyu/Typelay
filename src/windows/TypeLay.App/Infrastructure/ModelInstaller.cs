using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using TypeLay.Runtime;
namespace TypeLay.App;

internal sealed record SetupProgress(string Label, long Downloaded = 0, long? Total = null);
internal sealed record DownloadLock(string Name, string Version, string Url, string Sha256);

/// <summary>Only this explicit setup path has network access. It never receives source or candidate text.</summary>
internal sealed class ModelInstaller
{
    private readonly string _data, _worker, _cache, _localPackages;
    private readonly ModelPackageCatalog _catalog;
    private readonly Func<string, long> _availableDiskBytes;
    private string? _sourceModels;
    public bool CleanupPending
    {
        get; private set;
    }

    public ModelInstaller() : this(AppPaths.Data, AppPaths.Worker) { }
    internal ModelInstaller(string data, string worker, HttpMessageHandler? handler = null,
        TimeSpan? downloadTimeout = null, Func<ProcessStartInfo, IChildProcess>? startProcess = null,
        TimeSpan? selfCheckTimeout = null, string? localPackages = null, ModelPackageCatalog? catalog = null,
        Func<string, long>? availableDiskBytes = null)
    {
        _data = Path.GetFullPath(data);
        _worker = Path.GetFullPath(worker);
        _cache = Path.Combine(_data, "downloads");
        _localPackages = localPackages ?? DefaultLocalPackages;
        _catalog = catalog ?? ModelCatalog.Packages;
        _availableDiskBytes = availableDiskBytes ?? AvailableDiskBytes;
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = downloadTimeout ?? TimeSpan.FromMinutes(15)
        };
        _startProcess = startProcess ?? (start => new ChildProcess(start));
        _selfCheckTimeout = selfCheckTimeout ?? TimeSpan.FromMinutes(3);
    }

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    { "api.nuget.org", "globalcdn.nuget.org", "pypi.org", "files.pythonhosted.org", "argos-net.com", "huggingface.co", "cdn-lfs.huggingface.co", "cdn-lfs-us-1.hf.co", "cas-bridge.xethub.hf.co" };
    private readonly HttpClient _http;
    private readonly Func<ProcessStartInfo, IChildProcess> _startProcess;
    private readonly TimeSpan _selfCheckTimeout;
    private readonly List<DownloadLock> _locks = [];
    private static readonly (string Name, string Version)[] Wheels =
    [("numpy", "2.3.5"), ("PyYAML", "6.0.3"), ("setuptools", "80.9.0"),
     ("ctranslate2", "4.8.2"), ("sentencepiece", "0.2.2"), ("lingua-language-detector", "2.1.1")];
    private static readonly string[] ModelFiles =
    ["metadata.json", "sentencepiece.model", "model/config.json", "model/model.bin", "model/shared_vocabulary.json"];

    private static string DefaultLocalPackages => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "argos-translate", "packages");
    public static bool HasReusableLocalModels
    {
        get
        {
            var paths = AppPaths.ModelPaths;
            return ModelCatalog.Directions.All(direction => FindLocalPackage(direction, paths.Models, DefaultLocalPackages) is not null);
        }
    }

    public async Task<ModelInstallationStore.PreparedInstallation> PrepareAsync(IProgress<SetupProgress> progress, CancellationToken ct,
        IEnumerable<string>? requestedDirections = null, bool repair = false)
    {
        ModelInstallationStore.Lease? lease = null;
        ModelInstallationStore.PreparedInstallation? prepared = null;
        bool complete = false;
        var stage = ModelSetupStage.Prepare;
        try
        {
            string[] requested = requestedDirections?.ToArray() ?? _catalog.LegacyDirections.Select(direction => direction.Id).ToArray();
            if (!ModelCatalog.IsValidDirectionSet(requested))
                throw new InvalidDataException("model-directions");
            ct.ThrowIfCancellationRequested();
            lease = await new ModelInstallationStore(_data, _worker, catalog: _catalog).AcquireAsync(ct);
            // The legacy direction workflow must never overwrite a schema3 inventory.
            if (lease.UsesPackageInventory)
                throw new InvalidDataException("model-package-request-required");
            IReadOnlyList<string> committed;
            try
            {
                committed = lease.CommittedDirections;
            }
            catch (Exception error) when (repair && ModelCatalog.IsCompleteDirectionSet(requested)
                && error is InvalidDataException or JsonException or FileNotFoundException or DirectoryNotFoundException)
            {
                committed = [];
            }
            var directions = _catalog.LegacyDirections.Where(direction => requested.Contains(direction.Id, StringComparer.Ordinal)
                || committed.Contains(direction.Id, StringComparer.Ordinal)).ToArray();
            var selected = Array.AsReadOnly(directions.Select(direction => direction.Id).ToArray());
            _sourceModels = lease.ActivePaths.Models;
            prepared = lease.Prepare(selected);
            string staging = prepared.Root;
            Directory.CreateDirectory(_cache);
            stage = ModelSetupStage.Runtime;
            await PrepareRuntime(staging, progress, ct);
            string modelStage = Path.Combine(staging, "models");
            Directory.CreateDirectory(modelStage);
            foreach (var direction in directions)
            {
                string pair = direction.Id;
                string dest = Path.Combine(modelStage, pair);
                stage = ModelSetupStage.ModelVerify;
                if (await TryStageLocalModel(direction, dest, progress, ct))
                    continue;
                stage = ModelSetupStage.ModelDownload;
                string modelUrl = direction.Url;
                string archive = await Download(modelUrl, pair + "-" + direction.Version + ".argosmodel", direction.ArchivePin?.Sha256,
                    $"下载{direction.SourceName} → {direction.TargetName} 模型", progress, ct, direction.ArchivePin?.Bytes);
                // The catalog pin identifies a project-reviewed archive, not an upstream signature.
                string extraction = Path.Combine(staging, pair);
                stage = ModelSetupStage.ModelExtract;
                await Task.Run(() => Extract(archive, extraction, stage, ct, direction.ArchivePin), ct);
                stage = ModelSetupStage.ModelVerify;
                string package;
                try
                {
                    var metadataPaths = Directory.GetFiles(extraction, "metadata.json", SearchOption.AllDirectories);
                    if (metadataPaths.Length != 1)
                        throw new InvalidDataException("model-layout");
                    using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPaths[0], ct));
                    if (metadata.RootElement.GetProperty("from_code").GetString() != direction.Source
                        || metadata.RootElement.GetProperty("to_code").GetString() != direction.Target
                        || metadata.RootElement.GetProperty("package_version").GetString() != direction.Version)
                        throw new InvalidDataException("model-direction");
                    package = Path.GetDirectoryName(metadataPaths[0])!;
                    foreach (string relative in ModelFiles)
                        RequireModelFile(Path.Combine(package, relative));
                }
                catch (Exception error) when (error is InvalidDataException or JsonException
                    or KeyNotFoundException or InvalidOperationException)
                {
                    // A complete but unusable Argos archive must not poison every explicit retry.
                    // Storage failures and cancellation do not establish that its bytes are bad.
                    throw new ModelSetupException(SetupFailureCategory.Integrity, stage,
                        DeleteDamagedArchive(archive));
                }
                Directory.CreateDirectory(dest);
                Directory.Move(Path.Combine(package, "model"), Path.Combine(dest, "model"));
                foreach (string file in Directory.GetFiles(package))
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
                _locks.Add(new("argos-" + pair, direction.Version, modelUrl, direction.ArchivePin?.Sha256 ?? await Hash(archive, ct)));
                Directory.Delete(extraction, recursive: true);
            }
            stage = ModelSetupStage.ModelVerify;
            progress.Report(new("校验模型并执行真实 CPU 翻译自检"));
            var files = new List<object>();
            foreach (string file in Directory.GetFiles(modelStage, "*", SearchOption.AllDirectories))
                files.Add(new
                {
                    path = Path.GetRelativePath(modelStage, file).Replace('\\', '/'),
                    sha256 = await Hash(file, ct)
                });
            await File.WriteAllTextAsync(Path.Combine(modelStage, "installed.json"), JsonSerializer.Serialize(new
            {
                schema = ModelCatalog.IsCompleteDirectionSet(selected) ? 1 : 2,
                directions = selected,
                installedAt = DateTimeOffset.UtcNow,
                files,
                dependencies = _locks
            }, new JsonSerializerOptions { WriteIndented = true }), ct);
            stage = ModelSetupStage.SelfCheck;
            var start = new ProcessStartInfo(prepared.Paths.Python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            start.ArgumentList.Add("-I");
            start.ArgumentList.Add("-B");
            start.ArgumentList.Add(_worker);
            start.ArgumentList.Add("--models");
            start.ArgumentList.Add(modelStage);
            start.ArgumentList.Add("--self-check");
            prepared.StartingSelfCheck();
            using (var child = _startProcess(start))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_selfCheckTimeout);
                string result = await child.Process.StandardOutput.ReadToEndAsync(timeout.Token);
                await child.Process.WaitForExitAsync(timeout.Token);
                await child.TerminateAndWaitAsync(timeout.Token);
                prepared.SelfCheckExited();
                if (child.Process.ExitCode != 0 || result.Length > 16384)
                    throw new InvalidDataException("cpu-self-check-failed");
                using var check = JsonDocument.Parse(result);
                if (!PassedSelfCheck(check.RootElement, selected))
                    throw new InvalidDataException("cpu-self-check-failed");
                await File.WriteAllTextAsync(Path.Combine(staging, "setup-self-check.json"), result, ct);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "dependency-lock.json"),
                JsonSerializer.Serialize(_locks, new JsonSerializerOptions { WriteIndented = true }), ct);
            progress.Report(new("模型已通过本机 CPU 自检，正在等待启动"));
            complete = true;
            return prepared;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw ModelSetupException.From(error, stage);
        }
        finally
        {
            _http.Dispose();
            if (!complete)
            {
                prepared?.Dispose();
                lease?.Dispose();
                CleanupPending |= lease?.CleanupPending == true;
            }
        }
    }
    private async Task PrepareRuntime(string staging, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        var stage = ModelSetupStage.Runtime;
        try
        {
            const string pythonVersion = "3.13.16";
            progress.Report(new("读取 CPython 官方包元数据"));
            using var index = await Json(new Uri("https://api.nuget.org/v3/index.json"), ct);
            string registration = index.RootElement.GetProperty("resources").EnumerateArray()
                .First(e => e.GetProperty("@type").GetString() == "RegistrationsBaseUrl/3.6.0")
                .GetProperty("@id").GetString()!;
            using var leaf = await Json(new Uri(registration + $"python/{pythonVersion}.json"), ct);
            string pythonUrl = leaf.RootElement.GetProperty("packageContent").GetString()!;
            var entry = leaf.RootElement.GetProperty("catalogEntry");
            string catalogUrl = entry.ValueKind == JsonValueKind.String ? entry.GetString()! : entry.GetProperty("@id").GetString()!;
            using var catalog = await Json(new Uri(catalogUrl), ct);
            if (catalog.RootElement.GetProperty("id").GetString() != "python"
                || catalog.RootElement.GetProperty("version").GetString() != pythonVersion
                || catalog.RootElement.GetProperty("packageHashAlgorithm").GetString() != "SHA512")
                throw new InvalidDataException("python-metadata");
            byte[] expectedPython = Convert.FromBase64String(catalog.RootElement.GetProperty("packageHash").GetString()!);
            string pythonZip = await Download(pythonUrl, "python-" + pythonVersion + ".zip", null, "下载私有 Python 运行组件", progress, ct);
            byte[] actualPython;
            await using (var file = File.OpenRead(pythonZip))
                actualPython = await SHA512.HashDataAsync(file, ct);
            if (!CryptographicOperations.FixedTimeEquals(actualPython, expectedPython))
            {
                File.Delete(pythonZip);
                throw new InvalidDataException("python-integrity");
            }
            string pythonStage = Path.Combine(staging, "runtime", "python");
            progress.Report(new("准备私有运行组件"));
            await Task.Run(() => Extract(pythonZip, pythonStage, stage, ct), ct);
            if (!File.Exists(Path.Combine(pythonStage, "tools", "python.exe")))
                throw new InvalidDataException("python-layout");
            string pythonTarget = pythonStage;
            _locks.Add(new("python", pythonVersion, pythonUrl, await Hash(pythonZip, ct)));
            string site = Path.Combine(pythonTarget, "tools", "Lib", "site-packages");
            Directory.CreateDirectory(site);
            stage = ModelSetupStage.Dependencies;
            foreach (var (name, version) in Wheels)
            {
                ct.ThrowIfCancellationRequested();
                using var response = await Get(new Uri($"https://pypi.org/pypi/{name}/{version}/json"), null, ct);
                response.EnsureSuccessStatusCode();
                using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var wheel = metadata.RootElement.GetProperty("urls").EnumerateArray().FirstOrDefault(e =>
                    !e.GetProperty("yanked").GetBoolean() && Compatible(e.GetProperty("filename").GetString()!));
                if (wheel.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("wheel-unavailable");
                string filename = wheel.GetProperty("filename").GetString()!, url = wheel.GetProperty("url").GetString()!;
                string checksum = wheel.GetProperty("digests").GetProperty("sha256").GetString()!;
                string archive = await Download(url, filename, checksum, "下载本地组件 · " + name, progress, ct);
                await Task.Run(() => Extract(archive, site, stage, ct), ct);
                _locks.Add(new(name, version, url, checksum));
            }
            await WriteRuntimeInventory(staging, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw ModelSetupException.From(error, stage);
        }
    }
    private sealed record FileEntry(string Path, string Sha256);
    private sealed record PackageEntry(string Id, string Version, string[] Directions, FileEntry[] Files);
    private static readonly JsonSerializerOptions InventoryJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string[] SafeFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("component-link");
            if (Directory.Exists(path))
                foreach (string entry in Directory.EnumerateFileSystemEntries(path))
                    pending.Push(entry);
            else
                files.Add(path);
        }
        return files.ToArray();
    }
    private static string FilePath(string root, string relative)
    {
        if (relative.Length == 0 || relative.Split('/').Any(part => part.Length == 0 || part is "." or ".."
            || part.EndsWith('.') || part.EndsWith(' ') || part.Any(character => character < 32 || "\\:\0<>\"|?*".Contains(character))))
            throw new InvalidDataException("component-path");
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }
    private static async Task<FileEntry[]> InventoryFiles(string root, CancellationToken ct)
    {
        var result = new List<FileEntry>();
        foreach (string file in SafeFiles(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new(Path.GetRelativePath(root, file).Replace('\\', '/'), await Hash(file, ct)));
        }
        return result.ToArray();
    }
    private static async Task WriteRuntimeInventory(string root, CancellationToken ct)
    {
        var files = await InventoryFiles(Path.Combine(root, "runtime"), ct);
        await File.WriteAllTextAsync(Path.Combine(root, "runtime-inventory.json"),
            JsonSerializer.Serialize(new
            {
                schema = 1,
                files
            }, InventoryJson), ct);
    }
    private static async Task<long> CopyVerified(string source, string target, IReadOnlyList<FileEntry> expected,
        CancellationToken ct, long maxBytes)
    {
        if (expected.Count == 0 || expected.Count > 30000
            || expected.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Count)
            throw new InvalidDataException("component-inventory");
        var actual = SafeFiles(source).Select(file => Path.GetRelativePath(source, file).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected.Select(file => file.Path)))
            throw new InvalidDataException("component-inventory");
        long copied = 0;
        foreach (var file in expected)
        {
            ct.ThrowIfCancellationRequested();
            string input = FilePath(source, file.Path), output = FilePath(target, file.Path);
            long size = new FileInfo(input).Length;
            if (size > maxBytes - copied)
                throw new InvalidDataException("component-size");
            if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)
                || !string.Equals(await Hash(input, ct), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("component-integrity");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using (var from = File.OpenRead(input))
            await using (var to = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await CopyExact(from, to, size, ct);
            if (!string.Equals(await Hash(output, ct), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("component-integrity");
            copied += size;
        }
        return copied;
    }
    private static async Task<long> CopyRuntime(string source, string target, CancellationToken ct, long maxBytes)
    {
        string marker = Path.Combine(source, "runtime-inventory.json");
        if (!File.Exists(marker) || new FileInfo(marker).Length > 8 * 1024 * 1024
            || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("runtime-repair-required");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(marker, ct));
        if (document.RootElement.GetProperty("schema").GetInt32() != 1)
            throw new InvalidDataException("runtime-repair-required");
        var files = document.RootElement.GetProperty("files").Deserialize<FileEntry[]>(InventoryJson)
            ?? throw new InvalidDataException("runtime-repair-required");
        if (!files.Any(file => file.Path == "python/tools/python.exe"))
            throw new InvalidDataException("runtime-repair-required");
        long copied = await CopyVerified(Path.Combine(source, "runtime"), Path.Combine(target, "runtime"), files, ct, maxBytes);
        File.Copy(marker, Path.Combine(target, "runtime-inventory.json"));
        return copied;
    }
    private long Preflight(string sourceRoot, ModelInstallationStore.PreparedInstallation prepared)
    {
        long sourceCopyBytes = 0;
        foreach (string name in new[] { "runtime", "models" })
            if (Directory.Exists(Path.Combine(sourceRoot, name)))
                foreach (string file in SafeFiles(Path.Combine(sourceRoot, name)))
                    sourceCopyBytes = checked(sourceCopyBytes + new FileInfo(file).Length);
        long bytes = checked(64L * 1024 * 1024 + sourceCopyBytes);
        var request = prepared.Request!;
        if (request.Format == 1 && request.Operation is ModelPackageOperation.Install or ModelPackageOperation.Repair)
        {
            // Runtime extraction is bounded at 2 GiB; allow its archive/cache and copy,
            // plus each selected model's bounded extraction or exact locked assets.
            bytes = checked(bytes + 4L * 1024 * 1024 * 1024);
            foreach (string id in request.PackageIds)
            {
                var package = _catalog.Packages.Single(item => item.Id == id);
                // Argos keeps its archive, extraction and verified staged copy on this
                // volume simultaneously. Display bytes alone are not a disk budget.
                long packageBytes = package.Backend == "argos"
                    ? package.ArchivePin is { } pin ? Math.Max(3L * 1024 * 1024 * 1024, checked(pin.Bytes + 2 * pin.UnpackedBytes))
                        : 5L * 1024 * 1024 * 1024
                    : ModelCatalog.PackageDownloadBytes(package) is { } size ? checked(size * 2) : 3L * 1024 * 1024 * 1024;
                bytes = checked(bytes + packageBytes);
            }
        }
        if (_availableDiskBytes(_data) < bytes)
            throw new IOException("component-disk-space", unchecked((int)0x80070070));
        return sourceCopyBytes;
    }
    private static long AvailableDiskBytes(string data)
    {
        var volume = DriveInfo.GetDrives().Where(drive => data == drive.Name.TrimEnd(Path.DirectorySeparatorChar)
            || data.StartsWith(drive.Name.EndsWith(Path.DirectorySeparatorChar) ? drive.Name : drive.Name + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)).OrderByDescending(drive => drive.Name.Length).First();
        return volume.AvailableFreeSpace;
    }
    public Task<ModelInstallationStore.PreparedInstallation> PreparePackagesAsync(ModelPackageRequest request,
        IProgress<SetupProgress> progress, CancellationToken ct) =>
        // Preparation owns only bytes and an installation lease. Keep extraction,
        // inventory traversal and failed-candidate cleanup off the caller's UI thread.
        Task.Run(() => PreparePackagesCoreAsync(request, progress, ct), ct);
    private async Task<ModelInstallationStore.PreparedInstallation> PreparePackagesCoreAsync(ModelPackageRequest request,
        IProgress<SetupProgress> progress, CancellationToken ct)
    {
        ModelInstallationStore.Lease? lease = null;
        ModelInstallationStore.PreparedInstallation? prepared = null;
        bool complete = false;
        var stage = ModelSetupStage.Prepare;
        try
        {
            lease = await new ModelInstallationStore(_data, _worker, catalog: _catalog).AcquireAsync(ct);
            prepared = lease.PreparePackages(request);
            request = prepared.Request!;
            var state = prepared.Source!;
            string sourceRoot = Path.GetDirectoryName(lease.ActivePaths.Models)!;
            long remainingSourceCopyBytes = Preflight(sourceRoot, prepared);
            stage = ModelSetupStage.Runtime;
            if (request.Format == 2 || request.Operation is ModelPackageOperation.Select or ModelPackageOperation.Remove)
                remainingSourceCopyBytes -= await CopyRuntime(sourceRoot, prepared.Root, ct, remainingSourceCopyBytes);
            else
            {
                Directory.CreateDirectory(_cache);
                // Repair is explicit network consent. A trustworthy runtime may be
                // reused; missing or damaged runtime bytes are repaired here only.
                try
                {
                    remainingSourceCopyBytes -= await CopyRuntime(sourceRoot, prepared.Root, ct, remainingSourceCopyBytes);
                }
                catch (Exception error) when (error is not InvalidDataException { Message: "component-size" }
                    && (error is IOException or UnauthorizedAccessException or InvalidDataException
                        or JsonException or KeyNotFoundException or InvalidOperationException))
                {
                    string runtime = Path.Combine(prepared.Root, "runtime");
                    if (Directory.Exists(runtime))
                        Directory.Delete(runtime, true);
                    await PrepareRuntime(prepared.Root, progress, ct);
                }
            }
            stage = ModelSetupStage.ModelVerify;
            using var original = state.InstalledPackages.Count > 0
                ? JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(lease.ActivePaths.Models, "installed.json"), ct)) : null;
            bool legacy = original is not null && original.RootElement.GetProperty("schema").GetInt32() is 1 or 2;
            var packages = new List<PackageEntry>();
            foreach (var item in state.InstalledPackages)
            {
                if (request.PackageIds.Contains(item.Package.Id, StringComparer.Ordinal)
                    && request.Operation is ModelPackageOperation.Remove or ModelPackageOperation.Repair)
                    continue;
                string source = legacy ? Path.Combine(lease.ActivePaths.Models, item.Directions.Single().Id)
                    : PackageRoot(lease.ActivePaths.Models, item.Package);
                var files = item.Files.Select(file => new FileEntry(file.Path, file.Sha256)).ToArray();
                remainingSourceCopyBytes -= await CopyVerified(source, PackageRoot(prepared.Paths.Models, item.Package),
                    files, ct, remainingSourceCopyBytes);
                var directions = item.Directions.Select(direction => direction.Id);
                if (request.Format == 2 && request.PackageIds.Contains(item.Package.Id, StringComparer.Ordinal))
                    directions = directions.Concat(request.AddDirections!);
                packages.Add(new(item.Package.Id, item.Package.Version, directions.ToArray(), files));
            }
            if (request.Format == 1 && request.Operation is ModelPackageOperation.Install or ModelPackageOperation.Repair)
                foreach (string id in request.PackageIds)
                {
                    var package = _catalog.Packages.Single(item => item.Id == id);
                    string destination = PackageRoot(prepared.Paths.Models, package);
                    stage = ModelSetupStage.ModelDownload;
                    if (package.Backend == "hy-mt-llama-cpu")
                        await StageHy(package, destination, progress, ct);
                    else
                        await StageArgos(package, destination, prepared.Root, progress, ct);
                    stage = ModelSetupStage.ModelVerify;
                    var directions = state.InstalledPackages.FirstOrDefault(item => item.Package.Id == id)?.Directions
                        ?? ModelCatalog.DefaultInstallationDirections(package);
                    packages.Add(new(id, package.Version, directions.Select(direction => direction.Id).ToArray(), await InventoryFiles(destination, ct)));
                }
            Directory.CreateDirectory(prepared.Paths.Models);
            string inventory = JsonSerializer.Serialize(new
            {
                schema = 3,
                packages,
                activePackages = request.ActivePackages
            }, InventoryJson);
            using (var validated = JsonDocument.Parse(inventory))
            {
                _catalog.ReadInstallationSelection(validated.RootElement);
                _catalog.ReadActiveSelection(validated.RootElement);
            }
            await File.WriteAllTextAsync(Path.Combine(prepared.Paths.Models, "installed.json"), inventory, ct);
            await File.WriteAllTextAsync(Path.Combine(prepared.Root, "dependency-lock.json"), JsonSerializer.Serialize(_locks, InventoryJson), ct);
            // Byte preparation never starts another Hy worker while the old one serves.
            // Only ActivateAsync owns the single candidate and its exact hello.
            progress.Report(new("本地组件已校验，正在等待切换"));
            complete = true;
            return prepared;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidDataException error) when (error.Message == "installation-changed") { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { throw ModelSetupException.From(error, stage); }
        finally
        {
            _http.Dispose();
            if (!complete)
            {
                prepared?.Dispose();
                lease?.Dispose();
                CleanupPending |= lease?.CleanupPending == true;
            }
        }
    }
    private static string PackageRoot(string models, ModelPackage package) => Path.Combine(models, "packages", package.Id, package.Version);
    private async Task StageArgos(ModelPackage package, string destination, string staging,
        IProgress<SetupProgress> progress, CancellationToken ct)
    {
        var direction = package.Directions.Single();
        string archive = await Download(package.Url, direction.Id + "-" + package.Version + ".argosmodel", package.ArchivePin?.Sha256,
            "下载 " + direction.SourceName + " → " + direction.TargetName, progress, ct, package.ArchivePin?.Bytes);
        string extraction = Path.Combine(staging, "extract-" + package.Id);
        Extract(archive, extraction, ModelSetupStage.ModelExtract, ct, package.ArchivePin);
        try
        {
            var metadataPaths = Directory.GetFiles(extraction, "metadata.json", SearchOption.AllDirectories);
            if (metadataPaths.Length != 1)
                throw new InvalidDataException("model-layout");
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPaths[0], ct));
            if (metadata.RootElement.GetProperty("from_code").GetString() != direction.Source
                || metadata.RootElement.GetProperty("to_code").GetString() != direction.Target
                || metadata.RootElement.GetProperty("package_version").GetString() != package.Version)
                throw new InvalidDataException("model-direction");
            string root = Path.GetDirectoryName(metadataPaths[0])!;
            foreach (string relative in ModelFiles)
                RequireModelFile(Path.Combine(root, relative));
            await CopyVerified(root, destination, await InventoryFiles(root, ct), ct,
                package.ArchivePin?.UnpackedBytes ?? 2L * 1024 * 1024 * 1024);
            _locks.Add(new(package.Id, package.Version, package.Url, package.ArchivePin?.Sha256 ?? await Hash(archive, ct)));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ModelSetupException(SetupFailureCategory.Integrity, ModelSetupStage.ModelVerify, DeleteDamagedArchive(archive));
        }
        Directory.Delete(extraction, true);
    }
    private async Task StageHy(ModelPackage package, string destination, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("TypeLay.HyModelLock.json")
            ?? throw new InvalidDataException("model-lock-missing");
        using var document = JsonDocument.Parse(stream);
        var fixedModel = document.RootElement;
        if (fixedModel.GetProperty("package_id").GetString() != package.Id || fixedModel.GetProperty("version").GetString() != package.Version)
            throw new InvalidDataException("model-lock");
        Directory.CreateDirectory(destination);
        foreach (string kind in new[] { "model", "model_license" })
        {
            var asset = fixedModel.GetProperty(kind);
            string name = asset.GetProperty("file").GetString()!, sha = asset.GetProperty("sha256").GetString()!;
            long size = asset.GetProperty("bytes").GetInt64();
            string url = kind == "model" ? package.Url : new Uri(new Uri(package.Url), "LICENSE.txt").AbsoluteUri;
            string archive = await Download(url, package.Id + "-" + package.Version + "-" + name, sha,
                "下载 Hy 本地模型", progress, ct, size);
            string target = FilePath(destination, name);
            await using (var input = File.OpenRead(archive))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, ct);
            if (new FileInfo(target).Length != size || await Hash(target, ct) != sha)
                throw new InvalidDataException("model-integrity");
            _locks.Add(new(package.Id + "-" + kind, package.Version, url, sha));
        }
    }
    private static string? FindLocalPackage(ModelDirection direction, string models, string localPackages)
    {
        string[] candidates =
        [Path.Combine(models, direction.Id),
         Path.Combine(localPackages, $"translate-{direction.Id}-{direction.Version.Replace('.', '_')}")];
        foreach (string source in candidates)
        {
            try
            {
                if (!Directory.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0
                    || (File.GetAttributes(Path.Combine(source, "model")) & FileAttributes.ReparsePoint) != 0)
                    continue;
                long total = 0;
                foreach (string relative in ModelFiles)
                {
                    string file = Path.Combine(source, relative);
                    var info = new FileInfo(file);
                    if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length == 0)
                        throw new InvalidDataException("model-layout");
                    total = checked(total + info.Length);
                }
                if (total > 1024L * 1024 * 1024)
                    continue;
                using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "metadata.json")));
                var root = metadata.RootElement;
                if (!root.TryGetProperty("package_version", out var version) || version.GetString() != direction.Version
                    || !root.TryGetProperty("from_code", out var from) || from.GetString() != direction.Source
                    || !root.TryGetProperty("to_code", out var to) || to.GetString() != direction.Target)
                    continue;
                return source;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OverflowException)
            { /* An incomplete local package is not a usable source. */
            }
        }
        return null;
    }
    private async Task<bool> TryStageLocalModel(ModelDirection direction, string destination, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        string? source = FindLocalPackage(direction, _sourceModels!, _localPackages);
        if (source is null)
            return false;
        progress.Report(new($"复用本机{direction.SourceName} → {direction.TargetName} 模型"));
        try
        {
            Dictionary<string, string>? expected = null;
            if (source == Path.Combine(_sourceModels!, direction.Id))
            {
                using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_sourceModels!, "installed.json"), ct));
                expected = new(StringComparer.Ordinal);
                foreach (var entry in inventory.RootElement.GetProperty("files").EnumerateArray())
                {
                    var pathValue = entry.GetProperty("path");
                    if (pathValue.ValueKind != JsonValueKind.String)
                        return false;
                    string relative = pathValue.GetString()!;
                    if (!relative.StartsWith(direction.Id + "/", StringComparison.Ordinal))
                        continue;
                    var hashValue = entry.GetProperty("sha256");
                    if (hashValue.ValueKind != JsonValueKind.String)
                        return false;
                    string hash = hashValue.GetString()!;
                    if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)
                        || !expected.TryAdd(relative[(direction.Id.Length + 1)..], hash))
                        return false;
                }
                foreach (string relative in ModelFiles)
                    if (!expected.TryGetValue(relative, out string? hash)
                        || !string.Equals(await Hash(Path.Combine(source, relative), ct), hash, StringComparison.OrdinalIgnoreCase))
                        return false;
            }
            long copied = 0;
            foreach (string relative in ModelFiles)
            {
                ct.ThrowIfCancellationRequested();
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = File.OpenRead(Path.Combine(source, relative));
                long size = input.Length;
                if (size > 1024L * 1024 * 1024 - copied)
                    throw new InvalidDataException("model-copy-size");
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                await CopyExact(input, output, size, ct);
                copied += size;
                await output.DisposeAsync();
                if (expected is not null && !string.Equals(await Hash(target, ct), expected[relative], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("model-copy-integrity");
            }
            // The lock identifies the copied model bytes; the full file inventory is written below.
            _locks.Add(new("argos-" + direction.Id, direction.Version, "local-installed-model:model/model.bin",
                await Hash(Path.Combine(destination, "model", "model.bin"), ct)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException)
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, true);
            return false;
        }
    }
    private static bool PassedSelfCheck(JsonElement check, IReadOnlyList<string> expected)
    {
        if (check.GetProperty("status").GetString() != "passed" || check.GetProperty("device").GetString() != "cpu")
            return false;
        var results = check.GetProperty("results").EnumerateArray().ToArray();
        var actual = results.Select(result => result.GetProperty("direction").GetString()!).ToArray();
        return ModelCatalog.IsValidDirectionSet(actual) && new HashSet<string>(expected, StringComparer.Ordinal).SetEquals(actual)
            && results.All(result => result.GetProperty("status").GetString() == "translated"
                && result.GetProperty("nonempty").GetBoolean() && result.GetProperty("differs").GetBoolean());
    }
    private async Task<JsonDocument> Json(Uri uri, CancellationToken ct)
    {
        using var response = await Get(uri, null, ct);
        response.EnsureSuccessStatusCode();
        string value = await response.Content.ReadAsStringAsync(ct);
        if (value.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("metadata-size");
        return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
    }
    private static bool Compatible(string filename) => filename.EndsWith("-py3-none-any.whl", StringComparison.Ordinal)
        || filename.EndsWith("-py2.py3-none-any.whl", StringComparison.Ordinal)
        || (filename.EndsWith("-win_amd64.whl", StringComparison.Ordinal)
            && (filename.Contains("-cp313-cp313-", StringComparison.Ordinal)
                || (filename.Contains("-abi3-", StringComparison.Ordinal)
                    && Enumerable.Range(8, 6).Any(v => filename.Contains("-cp3" + v + "-", StringComparison.Ordinal)))));
    private async Task<HttpResponseMessage> Get(Uri uri, long? range, CancellationToken ct)
    {
        for (int i = 0; i < 6; i++)
        {
            if (uri.Scheme != "https" || !AllowedHosts.Contains(uri.IdnHost) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("download-origin");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (range is > 0)
                request.Headers.Range = new RangeHeaderValue(range, null);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                response.Dispose();
                continue;
            }
            return response;
        }
        throw new HttpRequestException("redirect-limit");
    }
    private async Task<string> Download(string url, string name, string? expectedHash, string label,
        IProgress<SetupProgress> progress, CancellationToken ct, long? exactBytes = null)
    {
        long limit = exactBytes ?? 1024L * 1024 * 1024;
        string destination = Path.Combine(_cache, Path.GetFileName(name));
        if (File.Exists(destination))
        {
            if (new FileInfo(destination).Length <= limit
                && (exactBytes is null || new FileInfo(destination).Length == exactBytes)
                && (expectedHash is null || (await Hash(destination, ct)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)))
                return destination;
            File.Delete(destination);
        }
        string partial = destination + ".part";
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > limit)
        {
            File.Delete(partial);
            offset = 0;
        }
        using var response = await Get(new Uri(url), offset, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
        {
            File.Delete(partial);
            return await Download(url, name, expectedHash, label, progress, ct, exactBytes);
        }
        response.EnsureSuccessStatusCode();
        bool append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange?.From == offset;
        if (response.StatusCode == HttpStatusCode.PartialContent && !append)
            throw new InvalidDataException("download-range");
        if (!append)
            offset = 0;
        if (response.Content.Headers.ContentLength is { } declared && declared > limit - offset)
            throw new InvalidDataException("download-size");
        long? total = response.Content.Headers.ContentLength is { } length ? length + offset : null;
        if (total > limit || exactBytes is not null && total is not null && total != exactBytes)
            throw new InvalidDataException("download-size");
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write,
                         FileShare.None, 65536, true))
        {
            byte[] buffer = new byte[65536];
            int read;
            long lastProgress = 0;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                if (read > limit - offset)
                    throw new InvalidDataException("download-size");
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                offset += read;
                if (Environment.TickCount64 - lastProgress >= 80)
                {
                    lastProgress = Environment.TickCount64;
                    progress.Report(new(label, offset, total));
                }
            }
            await file.FlushAsync(ct);
            progress.Report(new(label, offset, total));
        }
        if (exactBytes is { } pinnedLength && offset != pinnedLength)
            throw new InvalidDataException("download-size");
        if (total is { } expectedLength && offset != expectedLength)
            throw new HttpRequestException("download-incomplete");
        if (expectedHash is not null && !(await Hash(partial, ct)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException("download-integrity");
        }
        File.Move(partial, destination, true);
        return destination;
    }
    private static void RequireModelFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0 || new FileInfo(path).Length == 0)
                throw new InvalidDataException("model-layout");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException("model-layout");
        }
    }
    private static SetupFailureCategory? DeleteDamagedArchive(string archive)
    {
        try
        {
            File.Delete(archive);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ModelSetupException.From(error, ModelSetupStage.Prepare).Category;
        }
    }
    private static async Task<string> Hash(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
    }
    private static void Extract(string archive, string destination, ModelSetupStage stage, CancellationToken ct,
        ModelArchivePin? pin = null)
    {
        try
        {
            ExtractChecked(archive, destination, ct, pin);
        }
        catch (InvalidDataException)
        {
            throw new ModelSetupException(SetupFailureCategory.Integrity, stage, DeleteDamagedArchive(archive));
        }
    }
    private static void ExtractChecked(string archive, string destination, CancellationToken ct, ModelArchivePin? pin)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archiveBytes = File.OpenRead(archive);
        if (pin is not null)
        {
            // Validate the same opened bytes that ZipArchive will consume, not only
            // an earlier download/cache observation of this path.
            if (archiveBytes.Length != pin.Bytes || Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant() != pin.Sha256)
                throw new InvalidDataException("model-integrity");
            archiveBytes.Position = 0;
        }
        ct.ThrowIfCancellationRequested();
        using var zip = new ZipArchive(archiveBytes, ZipArchiveMode.Read);
        if (zip.Entries.Count > 30000)
            throw new InvalidDataException("zip-count");
        long total = 0;
        long limit = pin?.UnpackedBytes ?? 2L * 1024 * 1024 * 1024;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Length > limit - total || entry.FullName.Length > 1024
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || entry.FullName.Contains(':'))
                throw new InvalidDataException("zip-entry");
            total += entry.Length;
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("zip-path");
            if (entry.FullName.EndsWith('/') && entry.Length != 0)
                throw new InvalidDataException("zip-entry");
        }
        if (pin is not null && total != pin.UnpackedBytes)
            throw new InvalidDataException("zip-size");
        long written = 0;
        byte[] buffer = new byte[65536];
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            long entryWritten = 0;
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (read > entry.Length - entryWritten || read > limit - written)
                    throw new InvalidDataException("zip-size");
                output.Write(buffer, 0, read);
                entryWritten += read;
                written += read;
            }
            if (entryWritten != entry.Length)
                throw new InvalidDataException("zip-size");
        }
        if (written != total)
            throw new InvalidDataException("zip-size");
    }

    private static async Task CopyExact(Stream input, Stream output, long size, CancellationToken ct)
    {
        byte[] buffer = new byte[65536];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (read > size - copied)
                throw new InvalidDataException("component-size");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
        }
        if (copied != size)
            throw new InvalidDataException("component-size");
    }
}
