using System.Diagnostics;
using System.IO;
using TypeLay.Runtime;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class BrowserFixtureProcessTests
{
    [Fact]
    public async Task ConstructorFailureRetainsChildAndCannotAuthorizeProfileDeletionWithoutExitProof()
    {
        var child = new ControlledChild();
        var failure = new ChildProcessCleanupUnconfirmedException(child);
        var lifetime = new BrowserFixtureProcess();
        Assert.Same(failure, Assert.Throws<ChildProcessCleanupUnconfirmedException>(
            () => lifetime.Start<IChildProcess>(() => throw failure)));
        Assert.Same(child, lifetime.Child);
        Assert.False(lifetime.ExitConfirmed);
        await Assert.ThrowsAsync<IOException>(() => lifetime.StopAsync(owner => owner.TerminateAndWaitAsync(default)));
        Assert.False(lifetime.ExitConfirmed);
        Assert.Equal(1, child.Attempts);
        Assert.Equal(0, child.Disposals);
        child.Proven = true;
        await lifetime.StopAsync(owner => owner.TerminateAndWaitAsync(default));
        Assert.True(lifetime.ExitConfirmed);
        Assert.Equal(2, child.Attempts);
        Assert.Equal(1, child.Disposals);
        await lifetime.StopAsync(_ => throw new InvalidOperationException());
        Assert.Equal(1, child.Disposals);
    }

    [Fact]
    public async Task MissingChildAfterUnconfirmedConstructionNeverAuthorizesCleanup()
    {
        var lifetime = new BrowserFixtureProcess();
        Assert.Throws<ChildProcessCleanupUnconfirmedException>(
            () => lifetime.Start<IChildProcess>(() => throw new ChildProcessCleanupUnconfirmedException()));
        await Assert.ThrowsAsync<IOException>(() => lifetime.StopAsync(_ => Task.CompletedTask));
        Assert.False(lifetime.ExitConfirmed);
    }

    [Fact]
    public async Task SuccessfulConstructionStillRequiresSuccessfulExitProof()
    {
        var child = new ControlledChild();
        var lifetime = new BrowserFixtureProcess();
        Assert.Same(child, lifetime.Start(() => child));
        Assert.False(lifetime.ExitConfirmed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifetime.StopAsync(_ => Task.FromCanceled(new CancellationToken(true))));
        Assert.False(lifetime.ExitConfirmed);
        Assert.Equal(0, child.Disposals);
        child.Proven = true;
        await lifetime.StopAsync(owner => owner.TerminateAndWaitAsync(default));
        Assert.True(lifetime.ExitConfirmed);
        Assert.Equal(1, child.Disposals);
    }

    [Fact]
    public async Task DisposalFailureIsReportedWithoutRevokingCompletedExitProof()
    {
        var child = new ControlledChild { Proven = true, FailDispose = true };
        var lifetime = new BrowserFixtureProcess();
        lifetime.Start(() => child);
        await Assert.ThrowsAsync<IOException>(() => lifetime.StopAsync(owner => owner.TerminateAndWaitAsync(default)));
        Assert.True(lifetime.ExitConfirmed);
        Assert.Equal(1, child.Disposals);
        await lifetime.StopAsync(_ => throw new InvalidOperationException());
        Assert.Equal(1, child.Disposals);
    }

    private sealed class ControlledChild : IChildProcess
    {
        public bool Proven
        {
            get; set;
        }
        public int Attempts
        {
            get; private set;
        }
        public int Disposals
        {
            get; private set;
        }
        public bool FailDispose
        {
            get; set;
        }
        public Process Process => throw new NotSupportedException();
        public Task TerminateAndWaitAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            return Proven ? Task.CompletedTask : Task.FromException(new IOException("controlled-exit-unconfirmed"));
        }
        public void Dispose()
        {
            Disposals++;
            if (FailDispose)
                throw new IOException("controlled-dispose-failure");
        }
    }
}
