using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using TypeLay.Core;
using TypeLay.Protocol;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using TypeLay.Windows.Testing;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceControllerTests
{
    // Real installed Notepad and its message loop, not the stock-control fixture.
    // Only the isolated Broker constructor is enabled. Controlled translation and
    // SendInput are not evidence about model quality or physical keyboard/IME.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RealNotepadAcceptsOnceOrPassesTabAfterWithdrawal(bool withdraw) => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(28));
        var token = deadline.Token;
        var source = await NotepadSource.Start(token);
        var broker = new NotepadBroker();
        var failures = new List<Exception>();
        string stage = "broker-start";
        try
        {
            await broker.StartAsync();
            using var hook = new KeyboardHook(allowInjectedInput: true);
            await Until(() => hook.Ready, token);
            using var input = new WindowsInputService(broker, 0, monitor: hook, isolatedSourceAcceptance: true);
            var settings = new SettingsStore(Path.Combine(source.Directory, "settings.json"));
            var loop = new Loop();
            var view = new View();
            using var controller = new SessionController(settings, new Translation(), view, hook, input, loop);
            stage = "controller-start";
            await controller.StartAsync();
            stage = "installation-and-acceptance";
            await Until(() => { loop.Advance(); return view.CanAccept; }, token);
            long acceptanceReadyAt = WindowsInterruptClock.NowMs;
            var installation = Assert.IsType<SourceAcceptanceResult>(broker.Installation);
            var installedPermit = Assert.IsType<AcceptancePermit>(broker.InstallationPermit);
            Assert.True(installation.Installed);
            Assert.Equal(source.ProcessId, installation.Target.AsWindows().ProcessId);
            Assert.Equal((long)source.Field, installation.Target.AsWindows().ControlHwnd);
            Assert.Equal("你好", N.ReadText(source.Field, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(0, 0), N.ReadSelection(source.Field));
            if (withdraw)
            {
                stage = "withdrawal";
                var installed = installation;
                settings.Update(settings.Value with
                {
                    Enabled = false
                });
                await Until(() =>
                {
                    loop.Advance();
                    return broker.Terminal is { Quiet: true } terminal && terminal.Grant == installed.Grant
                        && terminal.Generation == installed.Generation && terminal.SessionRevision == installed.SessionRevision
                        && terminal.Target == installed.Target && !view.CanAccept;
                }, token);
                Assert.False(broker.Claimed);
                Assert.Null(hook.StockLastActivation);
                Assert.Equal("你好", N.ReadText(source.Field, Limits.MaxTextUnits));
                Assert.Equal(new TextRange(0, 0), N.ReadSelection(source.Field));
                stage = "withdrawn-tab";
                try
                {
                    Tab(false);
                }
                finally { Tab(true); }
                await Until(() => N.ReadText(source.Field, Limits.MaxTextUnits) == "\t你好", token);
                Assert.Null(hook.StockLastActivation);
                Assert.Equal(new TextRange(1, 1), N.ReadSelection(source.Field));
            }
            else
            {
                stage = "single-acceptance";
                bool pressed = true;
                try
                {
                    long tabStartedAt = WindowsInterruptClock.NowMs;
                    Tab(false);
                    long tabSentAt = WindowsInterruptClock.NowMs;
                    await Until(() => hook.StockLastActivation is not null, token);
                    var activation = hook.StockLastActivation!;
                    Assert.True(activation.Claimed,
                        $"Notepad source activation did not claim: status={activation.Receipt?.Status}, reason={activation.Receipt?.Reason}, "
                        + $"nativeReason=0x{activation.NativeReason:x}, budgetExceeded={activation.ExceededBudget}, elapsedMs={activation.ElapsedMs:F3}, "
                        + $"observedAtMs={installedPermit.ObservedAtMs}, expiresAtMs={installedPermit.ExpiresAtMs}, "
                        + $"acceptanceReadyAtMs={acceptanceReadyAt}, tabStartedAtMs={tabStartedAt}, tabSentAtMs={tabSentAt}, "
                        + $"resultObservedAtMs={WindowsInterruptClock.NowMs}.");
                    hook.Revoke(); // A claimed press still owns repeat and release.
                    Tab(false);
                    Tab(true);
                    pressed = false;
                    stage = "verified-receipt";
                    await Until(() => { loop.Advance(); return broker.Receipt is not null && view.State == SessionState.NoSession; }, token);
                    Assert.Equal(1, broker.Receipts);
                    Assert.Equal(WriteStatus.Verified, broker.Receipt!.Status);
                    Assert.NotNull(broker.Receipt.VerifiedSnapshot);
                    Assert.Equal("Hello", broker.Receipt.VerifiedSnapshot!.Text);
                    Assert.Equal(new TextRange(5, 5), broker.Receipt.VerifiedSnapshot.Selection);
                    Assert.Equal(broker.Installation!.Target, broker.Receipt.VerifiedSnapshot.Target);
                    Assert.True(broker.Quiet);
                    Assert.True(broker.Claimed);
                    Assert.Equal("Hello", N.ReadText(source.Field, Limits.MaxTextUnits));
                    Assert.Equal(new TextRange(5, 5), N.ReadSelection(source.Field));
                    Assert.False(view.CanAccept);
                }
                finally { if (pressed) Tab(true); }
            }
            Assert.Equal((source.Root, source.Field), N.Focused());
            Assert.Equal(0, broker.Writes); // Source receipt, never a second Broker write.
                                            // Exercise the actual application's native undo buffer. An unsupported
                                            // undo is a failure, not a replacement WM_SETTEXT or a silently skipped case.
            stage = "native-undo";
            Assert.NotEqual((nuint)0, N.Send(source.Field, 0x00C7)); // EM_UNDO
            await Until(() => N.ReadText(source.Field, Limits.MaxTextUnits) == "你好", token);
            Assert.Equal((source.Root, source.Field), N.Focused());
            Assert.Equal(0, broker.Writes);
            stage = "scene-complete";
        }
        catch (Exception error) { failures.Add(new InvalidOperationException("Notepad scene failed at " + stage, error)); }
        finally
        {
            try
            {
                await broker.DisposeAsync();
            }
            catch (Exception error) { failures.Add(new InvalidOperationException("Notepad Broker cleanup failed after " + stage, error)); }
            try
            {
                await source.DisposeAsync();
            }
            catch (Exception error) { failures.Add(new InvalidOperationException("Notepad source cleanup failed after " + stage, error)); }
        }
        if (failures.Count != 0)
            throw new AggregateException(failures);
    });

    [Fact]
    public async Task NotepadTransportCleanupRetainsItsOwnerUntilAnInFlightChildHasExited()
    {
        string identity = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.NotepadCleanup." + identity);
        Directory.CreateDirectory(directory);
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, $"Local\\TypeLay.BrokerQueue.{identity}.entered");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, $"Local\\TypeLay.BrokerQueue.{identity}.release");
        using var returned = new EventWaitHandle(false, EventResetMode.ManualReset, $"Local\\TypeLay.BrokerQueue.{identity}.return");
        // This peer only controls a real blocked pipe request. It is not
        // evidence for production Broker behavior or real Notepad acceptance.
        var broker = new NotepadBroker(identity, Path.Combine(directory, "requests.jsonl"));
        var failures = new List<Exception>();
        try
        {
            await broker.StartAsync();
            var pending = broker.CallAsync(new(BrokerProtocol.Version, 0, "capture", ""), TimeSpan.FromSeconds(10));
            try
            {
                Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(3))));
                Assert.False(pending.IsCompleted);
                await broker.DisposeAsync();
                Assert.True(broker.CleanupConfirmed);
                await Assert.ThrowsAnyAsync<IOException>(() => pending);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => broker.CallAsync(new(BrokerProtocol.Version, 0, "hello", "")));
            }
            finally
            {
                release.Set();
                try
                {
                    await pending;
                }
                catch (IOException) { }
            }
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try
            {
                await broker.DisposeAsync();
            }
            catch (Exception error) { failures.Add(new InvalidOperationException("In-flight pipe cleanup failed.", error)); }
            if (broker.CleanupConfirmed)
                Directory.Delete(directory, true);
        }
        if (failures.Count != 0)
            throw new AggregateException(failures);
    }

    [Fact]
    public Task NotepadIsolationRejectsALiveOwnerButAcceptsItsProvenExit() => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(28));
        var source = await NotepadSource.Start(deadline.Token);
        var failures = new List<Exception>();
        // Keep an independent real handle through exit. An object and its PID
        // are not evidence that the process can still receive desktop input.
        Process? retained = null;
        try
        {
            retained = Process.GetProcessById(source.ProcessId);
            _ = retained.SafeHandle;
            var error = await Assert.ThrowsAsync<IOException>(async () =>
            {
                await using var unexpected = await NotepadSource.Start(deadline.Token);
            });
            Assert.Contains("notepad-already-running", error.Message);
            Assert.False(retained.HasExited);
            Assert.Equal("你好", N.ReadText(source.Field, Limits.MaxTextUnits));
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try
            {
                await source.DisposeAsync();
            }
            catch (Exception error) { failures.Add(new InvalidOperationException("Notepad isolation regression cleanup failed.", error)); }
            if (failures.Count != 0)
                retained?.Dispose();
        }
        if (failures.Count != 0)
            throw new AggregateException(failures);
        using (retained)
        {
            Assert.NotNull(retained);
            Assert.True(retained.HasExited);
            NotepadSource.RejectLiveProcesses([retained]);
        }
    });

    private sealed class NotepadBroker : IInputBroker, IAsyncDisposable
    {
        private readonly SemaphoreSlim _serial = new(1, 1);
        private readonly string _pipeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        private readonly string? _queueIdentity, _queueLog;
        private ChildProcess? _child;
        private volatile bool _closing;
        private long _sequence;
        private Task? _cleanup;
        internal bool CleanupConfirmed
        {
            get; private set;
        }
        internal AcceptancePermit? InstallationPermit
        {
            get; private set;
        }
        internal SourceAcceptanceResult? Installation
        {
            get; private set;
        }
        internal WriteReceipt? Receipt
        {
            get; private set;
        }
        internal SourceAcceptanceResult? Terminal
        {
            get; private set;
        }
        internal int Receipts
        {
            get; private set;
        }
        internal bool Quiet
        {
            get; private set;
        }
        internal bool Claimed
        {
            get; private set;
        }
        internal int Writes
        {
            get; private set;
        }
        internal NotepadBroker(string? queueIdentity = null, string? queueLog = null)
        {
            _queueIdentity = queueIdentity;
            _queueLog = queueLog;
        }
        public bool Available => _child is not null && !_closing;
        public int ProcessId => _child?.Process.Id ?? 0;
        public async Task StartAsync()
        {
            if (_child is not null)
                return;
            // Like ProductionForegroundWriteTests.Desktop, this test transport
            // owns its child exactly once. BrokerClient owns and disposes its
            // own child on pipe failure, so layering a second terminating owner
            // around it races cleanup against its in-flight error path.
            var info = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add(Path.Combine(RepositoryDirectory(),
                "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"));
            info.ArgumentList.Add(_queueIdentity is null ? "--source-broker" : "--broker-queue-probe");
            if (_queueIdentity is not null)
            {
                info.ArgumentList.Add(_queueIdentity);
                info.ArgumentList.Add(_queueLog!);
            }
            info.Environment["TYPELAY_PIPE_TOKEN"] = _pipeToken;
            try
            {
                _child = new ChildProcess(info);
            }
            catch (ChildProcessCleanupUnconfirmedException error)
            {
                _child = error.Child as ChildProcess;
                throw;
            }
            Assert.Equal("ready", (await CallAsync(new(BrokerProtocol.Version, 0, "hello", ""), TimeSpan.FromSeconds(10))).Status);
        }
        public async Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            await _serial.WaitAsync();
            try
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                var child = _child ?? throw new IOException("notepad-broker-not-started");
                using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromMilliseconds(1500));
                long id = ++_sequence;
                if (request.Op is "write" or "direct-write")
                    Writes++;
                await Wire.WriteAsync(child.Process.StandardInput.BaseStream, request with
                {
                    Id = id,
                    Token = _pipeToken
                }, cancellation.Token);
                var response = await Wire.ReadAsync<BrokerResponse>(child.Process.StandardOutput.BaseStream, cancellation.Token);
                Assert.Equal(BrokerProtocol.Version, response.Version);
                Assert.Equal(id, response.Id);
                if (request.Op == "source-install" && response.SourceAcceptance is { Installed: true } installed)
                {
                    Installation = installed;
                    InstallationPermit = request.SourceAcceptance?.Permit;
                    Quiet = false;
                }
                if (request.Op is "source-retire" or "source-receipt" && response.SourceAcceptance is { } result)
                {
                    Terminal = result;
                    Quiet = result.Quiet;
                    Claimed = result.Claimed;
                    if (request.Op == "source-receipt")
                    {
                        Receipts++;
                        Receipt = result.Receipt;
                    }
                }
                return response;
            }
            catch
            {
                // Match the production client's fail-closed admission without
                // giving the RPC error path ownership of child disposal.
                _closing = true;
                throw;
            }
            finally { _serial.Release(); }
        }
        public void Dispose() => _closing = true;
        public ValueTask DisposeAsync() => new(_cleanup ??= CleanupAsync());
        private async Task CleanupAsync()
        {
            Dispose(); // No new RPC can acquire ownership during termination.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (_child is not null)
                    await _child.TerminateAndWaitAsync(cleanup.Token);
                // The pipe reader may still be unwinding EOF; keep streams alive
                // until that exact caller has left the real transport boundary.
                await _serial.WaitAsync(cleanup.Token);
                _serial.Release();
                CleanupConfirmed = true;
            }
            finally { _child?.Dispose(); }
        }
    }

    private sealed class NotepadSource : IAsyncDisposable
    {
        private readonly ChildProcess _child;
        public string Directory
        {
            get;
        }
        public nint Root
        {
            get; private set;
        }
        public nint Field
        {
            get; private set;
        }
        public int ProcessId => _child.Process.Id;
        private NotepadSource(ChildProcess child, string directory)
        {
            _child = child;
            Directory = directory;
        }
        public static async Task<NotepadSource> Start(CancellationToken token)
        {
            var existing = Process.GetProcessesByName("notepad");
            try
            {
                RejectLiveProcesses(existing);
            }
            finally { foreach (var process in existing) process.Dispose(); }
            string directory = Path.Combine(Path.GetTempPath(), "TypeLay.NotepadSource." + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            string document = Path.Combine(directory, "synthetic.txt");
            await File.WriteAllTextAsync(document, "你好", new UTF8Encoding(true), token);
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            var info = new ProcessStartInfo(executable) { UseShellExecute = false };
            info.ArgumentList.Add(document);
            // A startup/ownership failure deliberately preserves the temporary
            // document; it does not authorize following a handoff to another app.
            var source = new NotepadSource(new ChildProcess(info), directory);
            try
            {
                var image = ProcessImage.Read(source._child.Process.SafeHandle);
                TestContext.Current.TestOutputHelper?.WriteLine($"notepad-start pid={source.ProcessId}; image={image}; started={source._child.Process.StartTime.ToUniversalTime():O}");
                Assert.Equal(executable, image, ignoreCase: true);
                Assert.True(IsWow64Process2(source._child.Process.Handle, out ushort machine, out ushort native));
                Assert.Equal((ushort)0, machine);
                Assert.Equal((ushort)0x8664, native);
                AutomationElement? window = null;
                await Until(() =>
                {
                    Assert.False(source._child.Process.HasExited, "Notepad handed off or exited.");
                    var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ProcessIdProperty, source.ProcessId)).Cast<AutomationElement>()
                        .Where(item => item.Current.ControlType == ControlType.Window && !item.Current.IsOffscreen).ToArray();
                    Assert.True(windows.Length <= 1);
                    window = windows.SingleOrDefault();
                    return window is not null;
                }, token);
                source.Root = (nint)window!.Current.NativeWindowHandle;
                var fields = window.FindAll(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)))
                    .Cast<AutomationElement>().Where(item =>
                    {
                        var name = new StringBuilder(64);
                        return item.Current.NativeWindowHandle != 0
                            && GetClassNameW((nint)item.Current.NativeWindowHandle, name, name.Capacity) > 0 && name.ToString() == "Edit";
                    }).ToArray();
                var field = Assert.Single(fields);
                source.Field = (nint)field.Current.NativeWindowHandle;
                Assert.Equal(source.Root, N.GetAncestor(source.Field, 2));
                Assert.Equal(source.ProcessId, field.Current.ProcessId);
                Assert.True(N.IsWindowEnabled(source.Field));
                Assert.Equal((nint)0, N.GetStyle(source.Field, -16) & (0x20 | 0x800));
                Assert.True(N.SetForegroundWindow(source.Root));
                field.SetFocus(); // Explicit test-user navigation, not write permission.
                await Until(() => N.Focused() == (source.Root, source.Field), token);
                Assert.Equal("你好", N.ReadText(source.Field, Limits.MaxTextUnits));
                N.Send(source.Field, N.EM_SETSEL, 0, 0);
                Assert.Equal(new TextRange(0, 0), N.ReadSelection(source.Field));
                return source;
            }
            catch (Exception failure)
            {
                try
                {
                    await source.DisposeAsync();
                }
                catch (Exception cleanup) { throw new AggregateException("Notepad startup and cleanup failed.", failure, cleanup); }
                throw;
            }
        }
        internal static void RejectLiveProcesses(IEnumerable<Process> existing)
        {
            var rejected = new List<string>();
            foreach (var process in existing)
            {
                int pid = process.Id;
                bool? exited = null;
                string state;
                try
                {
                    exited = process.HasExited;
                    state = "exited=" + exited;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    state = "exit-state-unavailable:" + error.GetType().Name;
                }
                string identity;
                try
                {
                    string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
                    var image = ProcessImage.Read(process.SafeHandle);
                    string label = image is null ? "exited" : string.Equals(image, expected, StringComparison.OrdinalIgnoreCase)
                        ? expected : "other-image";
                    identity = $"image={label}; started={process.StartTime.ToUniversalTime():O}";
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    identity = "identity-unavailable:" + error.GetType().Name;
                }
                string observation = $"pid={pid}; {state}; {identity}";
                TestContext.Current.TestOutputHelper?.WriteLine("notepad-preflight " + observation);
                // This does not authorize terminating or attaching to an existing
                // process. Live and unqueryable objects still block the scenario.
                if (exited != true)
                    rejected.Add(observation);
            }
            if (rejected.Count != 0)
                throw new IOException("notepad-already-running: " + string.Join(" | ", rejected));
        }
        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _child.TerminateAndWaitAsync(cleanup.Token);
                TestContext.Current.TestOutputHelper?.WriteLine($"notepad-cleanup pid={ProcessId}; job-empty=true; root-exited={_child.Process.HasExited}; exit-code={_child.Process.ExitCode}");
            }
            finally { _child.Dispose(); }
            System.IO.Directory.Delete(Directory, true); // Only after whole-family proof.
        }
    }
    private static string RepositoryDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TypeLay.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder name, int capacity);
}
