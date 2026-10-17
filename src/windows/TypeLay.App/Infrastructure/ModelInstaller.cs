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
    private string? _sourceModels;

    public ModelInstaller() : this(AppPaths.Data, AppPaths.Worker) { }
    internal ModelInstaller(string data, string worker, HttpMessageHandler? handler = null,
        TimeSpan? downloadTimeout = null, Func<ProcessStartInfo, IChildProcess>? startProcess = null,
        TimeSpan? selfCheckTimeout = null, string? localPackages = null)
    {
        _data = Path.GetFullPath(data);
        _worker = Path.GetFullPath(worker);
        _cache = Path.Combine(_data, "downloads");
        _localPackages = localPackages ?? DefaultLocalPackages;
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
    { "api.nuget.org", "globalcdn.nuget.org", "pypi.org", "files.pythonhosted.org", "argos-net.com" };
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

    public async Task<ModelInstallationStore.PreparedInstallation> PrepareAsync(IProgress<SetupProgress> progress, CancellationToken ct)
    {
        ModelInstallationStore.Lease? lease = null;
        ModelInstallationStore.PreparedInstallation? prepared = null;
        bool complete = false;
        var stage = ModelSetupStage.Prepare;
        try
        {
            ct.ThrowIfCancellationRequested();
            lease = await new ModelInstallationStore(_data, _worker).AcquireAsync(ct);
            _sourceModels = lease.ActivePaths.Models;
            prepared = lease.Prepare();
            string staging = prepared.Root;
            Directory.CreateDirectory(_cache);
            stage = ModelSetupStage.Runtime;
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
            string modelStage = Path.Combine(staging, "models");
            Directory.CreateDirectory(modelStage);
            foreach (var direction in ModelCatalog.Directions)
            {
                string pair = direction.Id;
                string dest = Path.Combine(modelStage, pair);
                stage = ModelSetupStage.ModelVerify;
                if (await TryStageLocalModel(direction, dest, progress, ct))
                    continue;
                stage = ModelSetupStage.ModelDownload;
                string modelUrl = direction.Url;
                string archive = await Download(modelUrl, pair + "-" + direction.Version + ".argosmodel", null,
                    $"下载{direction.SourceName} → {direction.TargetName} 模型", progress, ct);
                // Argos publishes no archive checksum for these entries. HTTPS is the first-install
                // authenticity boundary; the observed SHA is recorded, not misrepresented as upstream-pinned.
                string extraction = Path.Combine(staging, pair);
                stage = ModelSetupStage.ModelExtract;
                await Task.Run(() => Extract(archive, extraction, stage, ct), ct);
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
                _locks.Add(new("argos-" + pair, direction.Version, modelUrl, await Hash(archive, ct)));
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
                schema = 1,
                directions = ModelCatalog.Directions.Select(direction => direction.Id).ToArray(),
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
            using (var child = _startProcess(start))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_selfCheckTimeout);
                string result = await child.Process.StandardOutput.ReadToEndAsync(timeout.Token);
                await child.Process.WaitForExitAsync(timeout.Token);
                if (child.Process.ExitCode != 0 || result.Length > 16384)
                    throw new InvalidDataException("cpu-self-check-failed");
                using var check = JsonDocument.Parse(result);
                if (!PassedSelfCheck(check.RootElement))
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
            }
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
            foreach (string relative in ModelFiles)
            {
                ct.ThrowIfCancellationRequested();
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = File.OpenRead(Path.Combine(source, relative));
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                await input.CopyToAsync(output, ct);
            }
            // The lock identifies the copied model bytes; the full file inventory is written below.
            _locks.Add(new("argos-" + direction.Id, direction.Version, "local-installed-model:model/model.bin",
                await Hash(Path.Combine(destination, "model", "model.bin"), ct)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, true);
            return false;
        }
    }
    private static bool PassedSelfCheck(JsonElement check)
    {
        if (check.GetProperty("status").GetString() != "passed" || check.GetProperty("device").GetString() != "cpu")
            return false;
        var results = check.GetProperty("results").EnumerateArray().ToArray();
        return ModelCatalog.IsCompleteDirectionSet(results.Select(result => result.GetProperty("direction").GetString()!).ToArray())
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
        IProgress<SetupProgress> progress, CancellationToken ct)
    {
        string destination = Path.Combine(_cache, Path.GetFileName(name));
        if (File.Exists(destination))
        {
            if (expectedHash is null || (await Hash(destination, ct)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                return destination;
            File.Delete(destination);
        }
        string partial = destination + ".part";
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var response = await Get(new Uri(url), offset, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
        {
            File.Delete(partial);
            return await Download(url, name, expectedHash, label, progress, ct);
        }
        response.EnsureSuccessStatusCode();
        bool append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange?.From == offset;
        if (response.StatusCode == HttpStatusCode.PartialContent && !append)
            throw new InvalidDataException("download-range");
        if (!append)
            offset = 0;
        long? total = response.Content.Headers.ContentLength is { } length ? length + offset : null;
        if (total is > 1024L * 1024 * 1024)
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
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                offset += read;
                if (offset > 1024L * 1024 * 1024)
                    throw new InvalidDataException("download-size");
                if (Environment.TickCount64 - lastProgress >= 80)
                {
                    lastProgress = Environment.TickCount64;
                    progress.Report(new(label, offset, total));
                }
            }
            await file.FlushAsync(ct);
            progress.Report(new(label, offset, total));
        }
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
    private static void Extract(string archive, string destination, ModelSetupStage stage, CancellationToken ct)
    {
        try
        {
            ExtractChecked(archive, destination, ct);
        }
        catch (InvalidDataException)
        {
            throw new ModelSetupException(SetupFailureCategory.Integrity, stage, DeleteDamagedArchive(archive));
        }
    }
    private static void ExtractChecked(string archive, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 30000)
            throw new InvalidDataException("zip-count");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            total = checked(total + entry.Length);
            if (total > 2L * 1024 * 1024 * 1024 || entry.FullName.Length > 1024
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || entry.FullName.Contains(':'))
                throw new InvalidDataException("zip-entry");
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("zip-path");
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }
}
