using System.Diagnostics;
using System.IO;
using TypeLay.App;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class BrowserCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedKernelExitIsProvenEvenWhenDiagnosticOutputFails(bool failOutput)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/q");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("set /p owned=");
        using var owner = new ChildProcess(start);
        using var retained = Process.GetProcessById(owner.Process.Id);
        _ = retained.Handle;
        using var windows = new BrowserWindows(owner.Process, "unused-owned-cleanup-test");
        var records = new List<string>();
        await windows.StopAsync(owner, value =>
        {
            records.Add(value);
            if (failOutput)
                throw new IOException("synthetic-diagnostic-failure");
        });
        Assert.True(retained.WaitForExit(0));
        Assert.Equal(5, records.Count);
        Assert.Contains("stage=discover; outcome=started", records[0]);
        Assert.Contains("stage=terminate-owned-job; outcome=started", records[1]);
        Assert.Contains("stage=wait-managed-exit; outcome=started", records[2]);
        Assert.Contains("stage=wait-kernel-signals; outcome=completed", records[^1]);
        Assert.Contains("pendingKernelSignals=0; finalPendingKernelSignals=unobserved", records[^1]);
    }
}
