using System.IO;
using System.Net;
using System.Net.Http;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class ModelInstallerTests
{
    [Theory]
    [InlineData(false, 200)]
    [InlineData(false, 500)]
    [InlineData(false, 502)]
    [InlineData(false, 503)]
    [InlineData(false, 504)]
    [InlineData(true, 200)]
    [InlineData(true, 500)]
    [InlineData(true, 502)]
    [InlineData(true, 503)]
    [InlineData(true, 504)]
    public async Task BothInstallPathsFinishAfterASingleTransientResponse(bool packages, int firstStatus)
    {
        using var files = new SetupFiles();
        string retried = packages ? "zh_en" : "en_zh";
        string other = packages ? "en_zh" : "zh_en";
        string target = $"/v1/translate-{retried}-1_9.argosmodel";
        var firstErrors = new Dictionary<string, HttpStatusCode> { ["/v3/index.json"] = HttpStatusCode.ServiceUnavailable };
        if (firstStatus != 200)
            firstErrors.Add(target, (HttpStatusCode)firstStatus);
        var network = files.Responses(ModelArchive, intercept: uri =>
            firstErrors.Remove(uri.AbsolutePath, out var status) ? new HttpResponseMessage(status) : null);
        var installer = files.Installer(network, startProcess: files.Child(PassedCheck(other, retried)),
            availableDiskBytes: _ => long.MaxValue);
        string[] ids = ["argos-zh-en"];
        using var prepared = packages
            ? await installer.PreparePackagesAsync(new(1, ModelPackageOperation.Install, ids, ids, null),
                NoProgress, TestContext.Current.CancellationToken)
            : await installer.PrepareAsync(NoProgress, TestContext.Current.CancellationToken, [other, retried]);
        Assert.Empty(firstErrors);
        var counts = network.Requests.CountBy(path => path).ToDictionary(item => item.Key, item => item.Value);
        Assert.Equal(2, counts["/v3/index.json"]);
        Assert.Equal(firstStatus == 200 ? 1 : 2, counts[target]);
        Assert.All(counts.Where(item => item.Key != target && item.Key != "/v3/index.json"),
            item => Assert.Equal(1, item.Value));
        if (!packages)
            Assert.Equal(ModelArchive(other), File.ReadAllBytes(files.CachedModel(other)));
        Assert.Equal(ModelArchive(retried), File.ReadAllBytes(files.CachedModel(retried)));
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    public static TheoryData<int, int> TransientPairs
    {
        get
        {
            int[] statuses = [500, 502, 503, 504];
            var pairs = new TheoryData<int, int>();
            foreach (int first in statuses)
                foreach (int final in statuses)
                    pairs.Add(first, final);
            return pairs;
        }
    }

    [Theory]
    [MemberData(nameof(TransientPairs))]
    [InlineData(503, 404)]
    [InlineData(404, 0)]
    [InlineData(501, 0)]
    public async Task ArchiveHttpErrorsStopAtTheFinalStatus(int firstStatus, int secondStatus)
    {
        using var files = new SetupFiles();
        var statuses = new Queue<int>(secondStatus == 0 ? [firstStatus] : [firstStatus, secondStatus]);
        var network = files.Responses(ModelArchive, intercept: uri => uri.AbsolutePath.EndsWith(".argosmodel", StringComparison.Ordinal)
            ? new HttpResponseMessage((HttpStatusCode)statuses.Dequeue()) : null);
        var failure = await Assert.ThrowsAsync<ModelSetupException>(() => PrepareZh(files, network, packages: true));
        Assert.Equal(SetupFailureCategory.Network, failure.Category);
        Assert.Equal(ModelSetupStage.ModelDownload, failure.Stage);
        Assert.Equal($"resource=argos-zh-en attempts={(secondStatus == 0 ? 1 : 2)} httpStatus={(secondStatus == 0 ? firstStatus : secondStatus)}", failure.HttpDiagnostic);
        Assert.Empty(statuses);
        Assert.Equal(secondStatus == 0 ? 1 : 2, network.Requests.Count(path => path.EndsWith(".argosmodel", StringComparison.Ordinal)));
        Assert.False(File.Exists(files.CachedModel("zh_en")));
    }

    [Fact]
    public async Task CancellationOnTransientHeadersStopsBeforeAnotherGet()
    {
        using var files = new SetupFiles();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var network = files.Responses(ModelArchive, intercept: uri =>
        {
            if (!uri.AbsolutePath.EndsWith(".argosmodel", StringComparison.Ordinal))
                return null;
            cancel.Cancel();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            files.Installer(network).PrepareAsync(NoProgress, cancel.Token, ["en_zh"]));
        Assert.Single(network.Requests, path => path.EndsWith(".argosmodel", StringComparison.Ordinal));
        Assert.False(File.Exists(files.CachedModel("en_zh")));
    }

    [Fact]
    public async Task HttpRetryAndRangeRestartConsumeTheOriginalArchiveBodyDeadline()
    {
        using var files = new SetupFiles();
        byte[] bytes = ModelArchive("en_zh");
        Directory.CreateDirectory(Path.Combine(files.Root, "downloads"));
        File.WriteAllBytes(files.CachedModel("en_zh") + ".part", bytes[..64]);
        CancellationTokenSource? activeDeadline = null;
        CancellationTokenSource? firstDeadline = null;
        int attempts = 0;
        using var body = new ExpiringBody(bytes, () => firstDeadline!.Cancel());
        var responses = files.Responses(ModelArchive, intercept: uri =>
        {
            if (!uri.AbsolutePath.EndsWith(".argosmodel", StringComparison.Ordinal))
                return null;
            attempts++;
            if (attempts == 1)
            {
                firstDeadline = activeDeadline;
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }
            return attempts == 2 ? new(HttpStatusCode.RequestedRangeNotSatisfiable)
                : new(HttpStatusCode.OK)
                {
                    Content = new StreamContent(body)
                };
        });
        var progress = new List<SetupProgress>();
        var installer = files.Installer(responses, scheduleDeadline: (source, budget) =>
        {
            Assert.Equal(TimeSpan.FromMinutes(15), budget);
            activeDeadline = source;
        }, availableDiskBytes: _ => long.MaxValue);
        var failure = await Assert.ThrowsAsync<ModelSetupException>(() => installer.PreparePackagesAsync(
            new(1, ModelPackageOperation.Install, ["argos-en-zh"], ["argos-en-zh"], null), new SetupProgressSink(progress.Add), TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Network, failure.Category);
        Assert.Equal(ModelSetupStage.ModelDownload, failure.Stage);
        Assert.Equal(3, attempts);
        Assert.Equal(new long[] { 64, 0 }, progress.Where(item => item.Label == "下载 English → 中文" &&
            item.DownloadPhase == SetupDownloadPhase.Headers).Select(item => item.Downloaded));
        Assert.True(body.CanceledDuringRead);
        Assert.False(File.Exists(files.CachedModel("en_zh")));
    }

    private sealed class ExpiringBody(byte[] bytes, Action expire) : MemoryStream(bytes)
    {
        public bool CanceledDuringRead
        {
            get; private set;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            expire();
            CanceledDuringRead = ct.IsCancellationRequested;
            ct.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer, ct);
        }
    }
}
