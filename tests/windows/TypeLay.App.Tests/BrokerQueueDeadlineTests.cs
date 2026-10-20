using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TypeLay.Runtime;
using System.Text.Json;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class BrokerQueueDeadlineTests
{
    [Theory]
    [InlineData(800)]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(0)]
    public Task ExpiredQueuedReturnDoesNotOwnTheActiveCallOrSendARequest(int? relativeMilliseconds) => WithPeer(async peer =>
    {
        var broker = peer.Client;
        await broker.StartAsync();
        var active = broker.CallAsync(new(BrokerProtocol.Version, 0, "capture", ""), TimeSpan.FromSeconds(10));
        await peer.Wait(peer.Entered);
        try
        {
            var queued = broker.CallAsync(new(BrokerProtocol.Version, 0, "return", "",
                ExpiresAtMs: WindowsInterruptClock.NowMs + 800),
                relativeMilliseconds is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(peer.Token));
            Assert.False(active.IsCompleted);
            Assert.True(broker.Available);
            Assert.Equal(new[] { "hello", "capture" }, peer.Messages().Select(message => message.Op));
        }
        finally { peer.Release.Set(); }
        Assert.Equal("observed", (await active.WaitAsync(peer.Token)).Status);
        Assert.Equal("ready", (await broker.CallAsync(new(BrokerProtocol.Version, 0, "hello", "")).WaitAsync(peer.Token)).Status);
        Assert.Equal(new[] { "hello", "capture", "hello" }, peer.Messages().Select(message => message.Op));
        Assert.Equal(new long[] { 1, 2, 3 }, peer.Messages().Select(message => message.Id));
    });

    [Fact]
    public Task ReturnAcquiringTheSlotKeepsItsOriginalDeadlineAndRejectsLateSuccess() => WithPeer(async peer =>
    {
        var broker = peer.Client;
        await broker.StartAsync();
        var active = broker.CallAsync(new(BrokerProtocol.Version, 0, "capture", ""), TimeSpan.FromSeconds(10));
        await peer.Wait(peer.Entered);
        long expiresAt = WindowsInterruptClock.NowMs + 800;
        var queued = broker.CallAsync(new(BrokerProtocol.Version, 0, "return", "", ExpiresAtMs: expiresAt),
            TimeSpan.FromMilliseconds(800));
        try
        {
            // Consume part of this exact budget in the real serial queue; no
            // fresh permit/deadline is published when A releases the slot.
            while (WindowsInterruptClock.NowMs < expiresAt - 400)
                await Task.Delay(1, peer.Token);
        }
        finally { peer.Release.Set(); }
        Assert.Equal("observed", (await active.WaitAsync(peer.Token)).Status);
        await peer.Wait(peer.Returned);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(peer.Token));
        Assert.False(broker.Available); // Acquired-call failure retains original cleanup policy.
        var request = Assert.Single(peer.Messages(), message => message.Op == "return");
        Assert.Equal(expiresAt, request.ExpiresAtMs);
        Assert.True(request.ReceivedAtMs < expiresAt, "The return must reach the wire with part of its original budget remaining.");
    });

    private static async Task WithPeer(Func<Peer, Task> test)
    {
        using var peer = new Peer();
        Exception? failure = null;
        try
        {
            await test(peer);
        }
        catch (Exception error) { failure = error; throw; }
        finally { peer.PrimaryFailure = failure; }
    }

    private sealed record Message(string Op, long Id, long ExpiresAtMs, long ReceivedAtMs);
    private sealed class Peer : IDisposable
    {
        private IChildProcess? _launched;
        private SafeProcessHandle? _rootHandle;
        private bool _startupCleanupUnconfirmed;
        private readonly string _identity = Guid.NewGuid().ToString("N");
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "TypeLay.BrokerQueue." + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        public EventWaitHandle Entered
        {
            get;
        }
        public EventWaitHandle Release
        {
            get;
        }
        public EventWaitHandle Returned
        {
            get;
        }
        public BrokerClient Client
        {
            get;
        }
        public Exception? PrimaryFailure
        {
            get; set;
        }
        public CancellationToken Token => _deadline.Token;
        public Peer()
        {
            Directory.CreateDirectory(_directory);
            _deadline.CancelAfter(TimeSpan.FromSeconds(15));
            Entered = new(false, EventResetMode.ManualReset, Name("entered"));
            Release = new(false, EventResetMode.ManualReset, Name("release"));
            Returned = new(false, EventResetMode.ManualReset, Name("return"));
            Client = new(info =>
            {
                info.FileName = AppPaths.DotNet;
                info.ArgumentList.Clear();
                DirectoryInfo? root = new(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeLay.slnx")))
                    root = root.Parent;
                if (root is null)
                    throw new DirectoryNotFoundException("Repository root not found.");
                info.ArgumentList.Add(Path.Combine(root.FullName,
                    "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"));
                info.ArgumentList.Add("--broker-queue-probe");
                info.ArgumentList.Add(_identity);
                info.ArgumentList.Add(Path.Combine(_directory, "requests.jsonl"));
                try
                {
                    var child = new ChildProcess(info);
                    _launched = child;
                    ObserveRoot(child);
                    return child;
                }
                catch (ChildProcessCleanupUnconfirmedException error)
                {
                    _startupCleanupUnconfirmed = true;
                    _launched = error.Child;
                    try
                    {
                        if (error.Child is not null)
                            ObserveRoot(error.Child);
                    }
                    catch (Exception observation) { error.Data["RootObservationFailure"] = observation; }
                    throw;
                }
            });
        }
        private void ObserveRoot(IChildProcess child)
        {
            // Duplicate the already-open exact process handle, not a later PID
            // lookup. Client.Stop may dispose its own Process before teardown.
            nint current = GetCurrentProcess();
            if (!DuplicateHandle(current, child.Process.SafeHandle, current, out var handle, 0, false, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _rootHandle = handle;
        }
        private string Name(string state) => $"Local\\TypeLay.BrokerQueue.{_identity}.{state}";
        public async Task Wait(EventWaitHandle signal) => Assert.True(
            await Task.Run(() => signal.WaitOne(TimeSpan.FromSeconds(3)), Token), "The real peer did not reach its requested boundary.");
        public Message[] Messages() => File.ReadAllLines(Path.Combine(_directory, "requests.jsonl"))
            .Select(line => JsonSerializer.Deserialize<Message>(line)!).ToArray();
        public void Dispose()
        {
            var errors = new List<Exception>();
            void Clean(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error) { errors.Add(error); }
            }
            Clean(() => Release.Set());
            Clean(Client.Dispose);
            Clean(() => _launched?.Dispose());
            bool rootExited = _launched is null || (_rootHandle is not null
                && !_rootHandle.IsInvalid && WaitForSingleObject(_rootHandle, 5_000) == 0);
            if (!rootExited || _startupCleanupUnconfirmed)
                errors.Add(new IOException("Broker queue peer exit proof is unconfirmed; retaining fixture directory."));
            Clean(() => _rootHandle?.Dispose());
            Clean(Entered.Dispose);
            Clean(Release.Dispose);
            Clean(Returned.Dispose);
            Clean(_deadline.Dispose);
            if (rootExited && !_startupCleanupUnconfirmed)
                Clean(() => Directory.Delete(_directory, true));
            if (errors.Count == 0)
                return;
            var cleanup = new AggregateException("Broker queue fixture cleanup failed.", errors);
            if (PrimaryFailure is not null)
                PrimaryFailure.Data["BrokerQueueCleanup"] = cleanup;
            else
                throw cleanup;
        }
    }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, SafeProcessHandle source, nint targetProcess,
        out SafeProcessHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

}
