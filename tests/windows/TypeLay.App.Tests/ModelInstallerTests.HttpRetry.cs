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
        int attempts = 0;
        var network = files.Responses(ModelArchive, intercept: uri =>
            uri.AbsolutePath == target && ++attempts == 1 && firstStatus != 200
                ? new HttpResponseMessage((HttpStatusCode)firstStatus) : null);
        var installer = files.Installer(network, startProcess: files.Child(PassedCheck(other, retried)),
            availableDiskBytes: _ => long.MaxValue);
        string[] ids = ["argos-zh-en"];
        using var prepared = packages
            ? await installer.PreparePackagesAsync(new(1, ModelPackageOperation.Install, ids, ids, null),
                NoProgress, TestContext.Current.CancellationToken)
            : await installer.PrepareAsync(NoProgress, TestContext.Current.CancellationToken, [other, retried]);
        Assert.Equal(firstStatus == 200 ? 1 : 2, attempts);
        Assert.Single(network.Requests, path => path == "/python.zip");
        if (!packages)
        {
            Assert.Single(network.Requests, path => path == $"/v1/translate-{other}-1_9.argosmodel");
            Assert.Equal(ModelArchive(other), File.ReadAllBytes(files.CachedModel(other)));
        }
        Assert.Equal(ModelArchive(retried), File.ReadAllBytes(files.CachedModel(retried)));
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(502, 504)]
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
        using var body = new DelayedBody(bytes);
        var network = new SlowRetryHandler(files.Responses(ModelArchive), body);
        var installer = files.Installer(network, downloadTimeout: TimeSpan.FromSeconds(2), availableDiskBytes: _ => long.MaxValue);
        var failure = await Assert.ThrowsAsync<ModelSetupException>(() => installer.PreparePackagesAsync(
            new(1, ModelPackageOperation.Install, ["argos-en-zh"], ["argos-en-zh"], null), NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Network, failure.Category);
        Assert.Equal(ModelSetupStage.ModelDownload, failure.Stage);
        Assert.Equal(new long?[] { 64, 64, null }, network.RangeStarts);
        Assert.True(body.CanceledDuringRead);
        Assert.False(File.Exists(files.CachedModel("en_zh")));
    }

    private sealed class SlowRetryHandler(HttpMessageHandler inner, DelayedBody body) : DelegatingHandler(inner)
    {
        public List<long?> RangeStarts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith(".argosmodel", StringComparison.Ordinal))
                return await base.SendAsync(request, ct);
            RangeStarts.Add(request.Headers.Range?.Ranges.Single().From);
            if (RangeStarts.Count == 1)
            {
                await Task.Delay(1200, ct);
                return new(HttpStatusCode.BadGateway);
            }
            return RangeStarts.Count == 2 ? new(HttpStatusCode.RequestedRangeNotSatisfiable)
                : new(HttpStatusCode.OK)
                {
                    Content = new StreamContent(body)
                };
        }
    }

    private sealed class DelayedBody(byte[] bytes) : MemoryStream(bytes)
    {
        public bool CanceledDuringRead
        {
            get; private set;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position == 0)
            {
                try
                {
                    await Task.Delay(1200, ct);
                }
                catch (OperationCanceledException) { CanceledDuringRead = true; throw; }
            }
            return await base.ReadAsync(buffer, ct);
        }
    }
}
