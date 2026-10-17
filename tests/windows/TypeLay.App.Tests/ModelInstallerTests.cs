using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class ModelInstallerTests
{
    [Fact]
    public async Task FailedCpuCheckLeavesThePreviousRuntimeAndInventoryUntouched()
    {
        using var files = new SetupFiles();
        string python = Path.Combine(files.Root, "runtime", "python", "tools", "python.exe");
        string inventory = Path.Combine(files.Root, "models", "installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        Directory.CreateDirectory(Path.GetDirectoryName(inventory)!);
        File.WriteAllText(python, "previous-runtime");
        File.WriteAllText(inventory, "{\"schema\":1}");
        await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(files.Responses(ModelArchive),
            startProcess: files.Child("exit /b 7")).PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal("previous-runtime", File.ReadAllText(python));
        Assert.Equal("{\"schema\":1}", File.ReadAllText(inventory));
        files.AssertChildrenExited();
    }

    [Fact]
    public async Task HttpTimeoutIsNetworkFailureRatherThanUserCancellation()
    {
        using var files = new SetupFiles();
        var installer = files.Installer(new WaitingHandler(), downloadTimeout: TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => installer.PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Network, error.Category);
        Assert.Equal(ModelSetupStage.Runtime, error.Stage);
    }

    [Fact]
    public async Task UserCancellationStillCancelsTheDownload()
    {
        using var files = new SetupFiles();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = new WaitingHandler();
        var install = files.Installer(waiting).PrepareAsync(NoProgress, cancel.Token);
        await waiting.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
    }

    [Fact]
    public async Task ConnectionFailureDoesNotExposeTheServerError()
    {
        using var files = new SetupFiles();
        var installer = files.Installer(new ResponseHandler(_ => throw new HttpRequestException("private server response")));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => installer.PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Network, error.Category);
        Assert.DoesNotContain("private server response", error.ToString());
    }

    [Fact]
    public async Task RedirectLoopIsNetworkFailure()
    {
        using var files = new SetupFiles();
        var handler = new ResponseHandler(uri => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = uri }
        });
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(handler).PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Network, error.Category);
    }

    [Fact]
    public async Task EnvironmentPreparationFailureHasAFixedCategoryAndStage()
    {
        using var files = new SetupFiles();
        File.WriteAllText(Path.Combine(files.Root, "downloads"), "not a directory");
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(new ResponseHandler(_ =>
            throw new InvalidOperationException("HTTP must not be reached"))).PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Unknown, error.Category);
        Assert.Equal(ModelSetupStage.Prepare, error.Stage);
        Assert.DoesNotContain(files.Root, error.ToString());
    }

    [Theory]
    [InlineData("missing-metadata")]
    [InlineData("duplicate-metadata")]
    [InlineData("direction")]
    [InlineData("invalid-json")]
    [InlineData("missing-direction")]
    [InlineData("wrong-type")]
    [InlineData("missing-model")]
    [InlineData("missing-config")]
    [InlineData("missing-vocabulary")]
    [InlineData("wrong-version")]
    [InlineData("empty-model")]
    [InlineData("empty-tokenizer")]
    [InlineData("empty-config")]
    [InlineData("empty-vocabulary")]
    public async Task InvalidCompletedModelIsReDownloadedOnExplicitRetry(string defect)
    {
        using var files = new SetupFiles();
        Directory.CreateDirectory(Path.Combine(files.Root, "downloads"));
        byte[] untouched = ModelArchive("en_zh");
        File.WriteAllBytes(files.CachedModel("en_zh"), untouched);
        byte[] broken = ModelArchive("zh_en", defect);
        var first = files.Responses(pair => pair == "zh_en" ? broken : ModelArchive(pair));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(first).PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Integrity, error.Category);
        Assert.Equal(ModelSetupStage.ModelVerify, error.Stage);
        Assert.False(File.Exists(files.CachedModel("zh_en")));
        Assert.Equal(untouched, File.ReadAllBytes(files.CachedModel("en_zh")));
        Assert.True(File.Exists(Path.Combine(files.Root, "downloads", "python-3.13.16.zip")));

        // The retry must consume a fresh archive and reach the real child-process boundary.
        // The controlled child deliberately fails: these tiny packages never prove model inference.
        var retry = files.Responses(ModelArchive);
        var retryError = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(retry,
            startProcess: files.Child("exit /b 7")).PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Runtime, retryError.Category);
        Assert.Equal(ModelSetupStage.SelfCheck, retryError.Stage);
        Assert.Contains("/v1/translate-zh_en-1_9.argosmodel", retry.Requests);
        Assert.Equal(ModelArchive("zh_en"), File.ReadAllBytes(files.CachedModel("zh_en")));
        files.AssertChildrenExited();
    }

    [Theory]
    [InlineData("exit /b 7")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"status\":false}")]
    [InlineData("{\"status\":\"failed\"}")]
    [InlineData("{\"status\":\"passed\"}")]
    [InlineData("set /p wait=")]
    public async Task FailedCpuCheckIsRuntimeFailureAndKeepsCompletedArchives(string command)
    {
        using var files = new SetupFiles();
        var installer = files.Installer(files.Responses(ModelArchive), startProcess: files.Child(command),
            selfCheckTimeout: command == "set /p wait=" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => installer.PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Runtime, error.Category);
        Assert.Equal(ModelSetupStage.SelfCheck, error.Stage);
        Assert.True(File.Exists(files.CachedModel("zh_en")));
        Assert.True(File.Exists(files.CachedModel("en_zh")));
        Assert.False(File.Exists(Path.Combine(files.Root, "models", "installed.json")));
        files.AssertChildrenExited();
    }

    [Fact]
    public async Task SuccessfulCheckPreparesBothDirectionsWithoutPublishing()
    {
        using var files = new SetupFiles();
        // The controlled external check exercises activation, not real model inference.
        var installer = files.Installer(files.Responses(ModelArchive),
            startProcess: files.Child("{\"status\":\"passed\",\"device\":\"cpu\",\"results\":[{\"direction\":\"zh_en\",\"status\":\"translated\",\"nonempty\":true,\"differs\":true},{\"direction\":\"en_zh\",\"status\":\"translated\",\"nonempty\":true,\"differs\":true}]}"));
        using var prepared = await installer.PrepareAsync(NoProgress, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(prepared.Paths.Models, "installed.json")));
        Assert.Equal(1, inventory.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(new[] { "zh_en", "en_zh" }, inventory.RootElement.GetProperty("directions")
            .EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.All(inventory.RootElement.GetProperty("files").EnumerateArray(), entry =>
        {
            string relative = entry.GetProperty("path").GetString()!;
            string expected = entry.GetProperty("sha256").GetString()!;
            string actual = Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(prepared.Paths.Models, relative)))).ToLowerInvariant();
            Assert.Equal(expected, actual);
        });
        files.AssertChildrenExited();
    }

    [Fact]
    public async Task SingleDirectionPreparationAndSupplementUseExactCommittedUnion()
    {
        using var files = new SetupFiles();
        var firstResponses = files.Responses(ModelArchive);
        using (var first = await files.Installer(firstResponses, startProcess: files.Child(PassedCheck("zh_en")))
            .PrepareAsync(NoProgress, TestContext.Current.CancellationToken, ["zh_en"]))
        {
            Assert.Equal(new[] { "zh_en" }, first.Directions);
            using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(first.Paths.Models, "installed.json")));
            Assert.Equal(2, inventory.RootElement.GetProperty("schema").GetInt32());
            Assert.All(inventory.RootElement.GetProperty("files").EnumerateArray(), entry =>
                Assert.StartsWith("zh_en/", entry.GetProperty("path").GetString()));
            Assert.DoesNotContain("/v1/translate-en_zh-1_9.argosmodel", firstResponses.Requests);
            Assert.False(Directory.Exists(Path.Combine(first.Paths.Models, "en_zh")));
            // Commit only the installation intent for the next installer call;
            // the controlled check is not evidence of model/service readiness.
            first.Publish();
        }
        string pointer = File.ReadAllText(Path.Combine(files.Root, "components.json"));
        var mismatch = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(files.Responses(ModelArchive),
            startProcess: files.Child(PassedCheck("en_zh")))
            .PrepareAsync(NoProgress, TestContext.Current.CancellationToken, ["en_zh"]));
        Assert.Equal(ModelSetupStage.SelfCheck, mismatch.Stage);
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(files.Root, "components.json")));
        using var supplement = await files.Installer(files.Responses(ModelArchive),
            startProcess: files.Child(PassedCheck("zh_en", "en_zh")))
            .PrepareAsync(NoProgress, TestContext.Current.CancellationToken, ["en_zh"]);
        Assert.Equal(new[] { "zh_en", "en_zh" }, supplement.Directions);
        using var complete = JsonDocument.Parse(File.ReadAllText(Path.Combine(supplement.Paths.Models, "installed.json")));
        Assert.Equal(1, complete.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(files.Root, "components.json")));
        files.AssertChildrenExited();
    }

    [Theory]
    [InlineData("path")]
    [InlineData("sha256")]
    public async Task InvalidLocalReuseInventoryFallsBackToDownload(string nullField)
    {
        using var files = new SetupFiles();
        string extraction = Path.Combine(files.Root, "source");
        using (var archive = new ZipArchive(new MemoryStream(ModelArchive("zh_en"))))
            archive.ExtractToDirectory(extraction);
        string models = Path.Combine(files.Root, "models");
        Directory.CreateDirectory(models);
        Directory.Move(Path.Combine(extraction, "package"), Path.Combine(models, "zh_en"));
        var entry = new Dictionary<string, string?>
        {
            ["path"] = "zh_en/metadata.json",
            ["sha256"] = new string('a', 64)
        };
        entry[nullField] = null;
        File.WriteAllText(Path.Combine(models, "installed.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            files = new[] { entry }
        }));
        var responses = files.Responses(ModelArchive);
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(responses,
            startProcess: _ => throw new Win32Exception(5)).PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal(ModelSetupStage.SelfCheck, error.Stage);
        Assert.Contains("/v1/translate-zh_en-1_9.argosmodel", responses.Requests);
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Fact]
    public async Task LockedCompletedArchiveIsPermissionFailureAndIsRetained()
    {
        using var files = new SetupFiles();
        Directory.CreateDirectory(Path.Combine(files.Root, "downloads"));
        byte[] archive = ModelArchive("zh_en");
        File.WriteAllBytes(files.CachedModel("zh_en"), archive);
        using (var locked = new FileStream(files.CachedModel("zh_en"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(files.Responses(ModelArchive))
                .PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
            Assert.Equal(SetupFailureCategory.Permission, error.Category);
            Assert.Equal(ModelSetupStage.ModelExtract, error.Stage);
        }
        Assert.Equal(archive, File.ReadAllBytes(files.CachedModel("zh_en")));
    }

    [Fact]
    public async Task BadArchiveThatCannotBeDeletedReportsTheRecoveryBlockerOnRetry()
    {
        using var files = new SetupFiles();
        Directory.CreateDirectory(Path.Combine(files.Root, "downloads"));
        byte[] archive = ModelArchive("zh_en", "invalid-json");
        File.WriteAllBytes(files.CachedModel("zh_en"), archive);
        // Permit verification but deny deletion of this test-owned completed archive.
        using (var locked = new FileStream(files.CachedModel("zh_en"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(files.Responses(ModelArchive))
                    .PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
                Assert.Equal(SetupFailureCategory.Integrity, error.Category);
                Assert.Equal(ModelSetupStage.ModelVerify, error.Stage);
                Assert.Equal(SetupFailureCategory.Permission, error.RecoveryFailureCategory);
                Assert.False(File.Exists(Path.Combine(files.Root, "models", "installed.json")));
            }
        }
        Assert.Equal(archive, File.ReadAllBytes(files.CachedModel("zh_en")));
    }

    [Theory]
    [InlineData(112, SetupFailureCategory.DiskSpace)]
    [InlineData(39, SetupFailureCategory.DiskSpace)]
    [InlineData(5, SetupFailureCategory.Permission)]
    public async Task OperatingSystemStartupFailurePreservesPreviousInstallationAndGoodArchives(int errorCode, SetupFailureCategory category)
    {
        using var files = new SetupFiles();
        string python = Path.Combine(files.Root, "runtime", "python", "tools", "python.exe");
        string inventory = Path.Combine(files.Root, "models", "installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        Directory.CreateDirectory(Path.GetDirectoryName(inventory)!);
        File.WriteAllText(python, "previous-runtime");
        File.WriteAllText(inventory, "{\"schema\":1}");
        var installer = files.Installer(files.Responses(ModelArchive),
            startProcess: _ => throw new Win32Exception(errorCode, "private system error"));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => installer.PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(category, error.Category);
        Assert.Equal(ModelSetupStage.SelfCheck, error.Stage);
        Assert.DoesNotContain("private system error", error.ToString());
        Assert.Equal("previous-runtime", File.ReadAllText(python));
        Assert.Equal("{\"schema\":1}", File.ReadAllText(inventory));
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
        Assert.True(File.Exists(files.CachedModel("zh_en")));
        Assert.True(File.Exists(files.CachedModel("en_zh")));
    }

    [Fact]
    public async Task UserCancellationDuringCpuCheckClosesTheChildAndKeepsGoodArchives()
    {
        using var files = new SetupFiles();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var start = files.Child("set /p wait=");
        var installer = files.Installer(files.Responses(ModelArchive), startProcess: info =>
        {
            var child = start(info);
            cancel.Cancel();
            return child;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.PrepareAsync(NoProgress, cancel.Token));
        Assert.True(File.Exists(files.CachedModel("zh_en")));
        Assert.True(File.Exists(files.CachedModel("en_zh")));
        files.AssertChildrenExited();
    }

    [Fact]
    public async Task CancelAfterDownloadsKeepsGoodArchivesForRetry()
    {
        using var files = new SetupFiles();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new SetupProgressSink(value =>
        {
            if (value.Label == "校验模型并执行真实 CPU 翻译自检")
                cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.Installer(files.Responses(ModelArchive))
            .PrepareAsync(progress, cancel.Token));
        Assert.True(File.Exists(files.CachedModel("zh_en")));
        Assert.True(File.Exists(files.CachedModel("en_zh")));
    }

    private static readonly IProgress<SetupProgress> NoProgress = new SetupProgressSink(_ => { });
    private sealed class SetupProgressSink(Action<SetupProgress> report) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => report(value);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class ResponseHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request.RequestUri));
        }
    }

    private sealed class SetupFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "TypeLay setup " + Guid.NewGuid().ToString("N"));
        private readonly List<ObservedChild> _children = [];
        public SetupFiles() => Directory.CreateDirectory(Root);
        public ModelInstaller Installer(HttpMessageHandler handler, TimeSpan? downloadTimeout = null,
            Func<ProcessStartInfo, IChildProcess>? startProcess = null, TimeSpan? selfCheckTimeout = null) =>
            new(Root, Path.Combine(Root, "worker.py"), handler, downloadTimeout, startProcess,
                selfCheckTimeout ?? TimeSpan.FromSeconds(5),
                localPackages: Path.Combine(Root, "local-packages"));
        public string CachedModel(string pair) => Path.Combine(Root, "downloads", pair + "-1.9.argosmodel");
        public Func<ProcessStartInfo, IChildProcess> Child(string command) => _ =>
        {
            if (command is not "exit /b 7" and not "set /p wait=")
            {
                File.WriteAllText(Path.Combine(Root, "self-check-output.txt"), command);
                command = "type self-check-output.txt";
            }
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = Root,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
            // The input handshake keeps the process alive until its real job owner is attached.
            start.ArgumentList.Add("set /p ready= & " + command);
            var child = new ObservedChild(new ChildProcess(start));
            _children.Add(child);
            child.Process.StandardInput.WriteLine("continue");
            child.Process.StandardInput.Flush();
            return child;
        };
        public void AssertChildrenExited()
        {
            Assert.NotEmpty(_children);
            Assert.All(_children, child => Assert.True(child.Exited));
        }
        public ResponseHandler Responses(Func<string, byte[]> model)
        {
            byte[] python = Archive(new()
            {
                ["tools/python.exe"] = "controlled runtime package"
            });
            byte[] wheel = Archive(new()
            {
                ["fixture.py"] = "controlled dependency package"
            });
            return new ResponseHandler(uri => uri.AbsolutePath switch
            {
                "/v3/index.json" => Json(new
                {
                    resources = new[] { new Dictionary<string, string>
                { ["@type"] = "RegistrationsBaseUrl/3.6.0", ["@id"] = "https://api.nuget.org/registration/" } }
                }),
                "/registration/python/3.13.16.json" => Json(new
                {
                    packageContent = "https://api.nuget.org/python.zip",
                    catalogEntry = "https://api.nuget.org/catalog/python.json"
                }),
                "/catalog/python.json" => Json(new
                {
                    id = "python",
                    version = "3.13.16",
                    packageHashAlgorithm = "SHA512",
                    packageHash = Convert.ToBase64String(SHA512.HashData(python))
                }),
                "/python.zip" => Bytes(python),
                var path when path.StartsWith("/pypi/", StringComparison.Ordinal) => Json(new
                {
                    urls = new[] { new
                {
                    filename = "fixture-1-py3-none-any.whl", url = "https://files.pythonhosted.org/fixture.whl", yanked = false,
                    digests = new { sha256 = Convert.ToHexString(SHA256.HashData(wheel)).ToLowerInvariant() }
                } }
                }),
                "/fixture.whl" => Bytes(wheel),
                "/v1/translate-zh_en-1_9.argosmodel" => Bytes(model("zh_en")),
                "/v1/translate-en_zh-1_9.argosmodel" => Bytes(model("en_zh")),
                _ => throw new InvalidOperationException("Unexpected setup request.")
            });
        }
        public void Dispose()
        {
            foreach (var child in _children)
                child.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
    }

    private sealed class ObservedChild(ChildProcess child) : IChildProcess
    {
        private bool _disposed;
        public bool Exited
        {
            get; private set;
        }
        public Process Process => child.Process;
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (!Process.HasExited)
                    Process.Kill(true);
                Exited = Process.WaitForExit(5000);
            }
            finally { child.Dispose(); }
        }
    }

    private static string PassedCheck(params string[] directions) => JsonSerializer.Serialize(new
    {
        status = "passed",
        device = "cpu",
        results = directions.Select(direction => new
        {
            direction,
            status = "translated",
            nonempty = true,
            differs = true
        })
    });

    private static HttpResponseMessage Json(object value) => Bytes(JsonSerializer.SerializeToUtf8Bytes(value));
    private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    private static byte[] ModelArchive(string pair) => ModelArchive(pair, "");
    private static byte[] ModelArchive(string pair, string defect)
    {
        string[] direction = pair.Split('_');
        var files = new Dictionary<string, string>
        {
            ["package/metadata.json"] = JsonSerializer.Serialize(new { from_code = direction[0], to_code = direction[1], package_version = "1.9" }),
            ["package/sentencepiece.model"] = "controlled tokenizer",
            ["package/model/model.bin"] = "controlled model",
            ["package/model/config.json"] = "{}",
            ["package/model/shared_vocabulary.json"] = "[]"
        };
        switch (defect)
        {
            case "missing-metadata":
                files.Remove("package/metadata.json");
                break;
            case "duplicate-metadata":
                files["other/metadata.json"] = "{}";
                break;
            case "direction":
                files["package/metadata.json"] = "{\"from_code\":\"en\",\"to_code\":\"zh\",\"package_version\":\"1.9\"}";
                break;
            case "invalid-json":
                files["package/metadata.json"] = "{";
                break;
            case "missing-direction":
                files["package/metadata.json"] = "{}";
                break;
            case "wrong-type":
                files["package/metadata.json"] = "{\"from_code\":7}";
                break;
            case "missing-model":
                files.Remove("package/model/model.bin");
                break;
            case "missing-config":
                files.Remove("package/model/config.json");
                break;
            case "missing-vocabulary":
                files.Remove("package/model/shared_vocabulary.json");
                break;
            case "wrong-version":
                files["package/metadata.json"] = "{\"from_code\":\"zh\",\"to_code\":\"en\",\"package_version\":\"1.8\"}";
                break;
            case "empty-model":
                files["package/model/model.bin"] = "";
                break;
            case "empty-tokenizer":
                files["package/sentencepiece.model"] = "";
                break;
            case "empty-config":
                files["package/model/config.json"] = "";
                break;
            case "empty-vocabulary":
                files["package/model/shared_vocabulary.json"] = "";
                break;
        }
        return Archive(files);
    }
    private static byte[] Archive(Dictionary<string, string> files)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            foreach (var (name, value) in files)
            {
                var entry = zip.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(value);
            }
        }
        return bytes.ToArray();
    }
}
