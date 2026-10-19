using System.Diagnostics;
using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class ChildProcessTests
{
    [Fact]
    public async Task CancelledExitProofRetainsTheOwnerForRetryAndSuccessfulProofIsIdempotent()
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/q");
        start.ArgumentList.Add("/c");
        // Keep a real assigned root alive without timers, network or model work.
        start.ArgumentList.Add("set /p owned=");
        using var owner = new ChildProcess(start);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var cancelled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.TerminateAndWaitAsync(cancelled));
        Assert.False(owner.Process.HasExited);
        await owner.TerminateAndWaitAsync(deadline.Token);
        Assert.True(owner.Process.HasExited);
        await owner.TerminateAndWaitAsync(cancelled);
    }
}
