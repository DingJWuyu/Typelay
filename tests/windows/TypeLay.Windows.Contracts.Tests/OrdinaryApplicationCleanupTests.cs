using System.Diagnostics;
using Xunit;

namespace TypeLay.Windows.Contracts.Tests;

public sealed class OrdinaryApplicationCleanupTests
{
    [Fact]
    public async Task NormalRootExitDoesNotAuthorizeDeletionWhenFamilyProofFails()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay-ordinary-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, "synthetic-model-and-profile-owner");
        await File.WriteAllTextAsync(marker, "owned data", TestContext.Current.CancellationToken);
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh") { UseShellExecute = false };
        foreach (string argument in OperatingSystem.IsWindows() ? new[] { "/d", "/c", "exit", "0" } : new[] { "-c", "exit 0" })
            start.ArgumentList.Add(argument);
        using var root = Process.Start(start);
        Assert.NotNull(root);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await root.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, root.ExitCode);
            var failures = new List<string>();
            bool result = await OrdinaryApplicationCleanup.RunAsync(
                [_ =>
                {
                    Assert.True(root.HasExited); // A real successful root observation already exists.
                    return Task.FromException(new InvalidOperationException("remaining owned family unknown"));
                }], false, () => { Directory.Delete(directory, true); return Task.CompletedTask; }, failures.Add);
            Assert.False(result);
            Assert.Equal("owned data", await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken));
            Assert.Contains("ordinary-owned-family-exit-unconfirmed", failures);
            Assert.Contains("ordinary-owned-data-preserved", failures);
        }
        finally
        {
            if (!root.HasExited)
            {
                root.Kill(entireProcessTree: true);
                root.WaitForExit(5000);
            }
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task BothSourceAndAppFamiliesMustConfirmEvenWhenTheOtherOneExited(int failedIndex)
    {
        var observed = new List<int>();
        bool deleted = false;
        var proofs = Enumerable.Range(0, 2).Select(index => (Func<CancellationToken, Task>)(_ =>
        {
            observed.Add(index);
            return index == failedIndex ? Task.FromException(new TimeoutException()) : Task.CompletedTask;
        }));
        Assert.False(await OrdinaryApplicationCleanup.RunAsync(proofs, false,
            () => { deleted = true; return Task.CompletedTask; }, _ => { }));
        Assert.Equal([0, 1], observed);
        Assert.False(deleted);
    }

    [Fact]
    public async Task UnconfirmedConstructionPreservesDataEvenWhenAllReturnedOwnersConfirmExit()
    {
        bool deleted = false;
        Assert.False(await OrdinaryApplicationCleanup.RunAsync([_ => Task.CompletedTask], true,
            () => { deleted = true; return Task.CompletedTask; }, _ => { }));
        Assert.False(deleted);
    }

    [Fact]
    public async Task ConfirmedFamiliesPermitExactlyOneOwnedDataCleanupAfterTheirProofs()
    {
        var order = new List<string>();
        var failures = new List<string>();
        Assert.True(await OrdinaryApplicationCleanup.RunAsync([
            _ => { order.Add("source-family"); return Task.CompletedTask; },
            _ => { order.Add("app-family"); return Task.CompletedTask; }
        ], false, () => { order.Add("delete"); return Task.CompletedTask; }, failures.Add));
        Assert.Equal(["source-family", "app-family", "delete"], order);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task CanceledFamilyProofRetainsDataAndReportsTheUnknownExit()
    {
        bool deleted = false;
        Assert.False(await OrdinaryApplicationCleanup.RunAsync(
            [_ => Task.FromCanceled(new CancellationToken(true))], false,
            () => { deleted = true; return Task.CompletedTask; }, _ => { }));
        Assert.False(deleted);
    }
}
