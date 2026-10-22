using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
        await using var source = await NotepadSource.Start(token);
        await using var broker = new NotepadBroker();
        await broker.StartAsync();
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.Ready, token);
        using var input = new WindowsInputService(broker, 0, monitor: hook, isolatedSourceAcceptance: true);
        var settings = new SettingsStore(Path.Combine(source.Directory, "settings.json"));
        var loop = new Loop();
        var view = new View();
        using var controller = new SessionController(settings, new Translation(), view, hook, input, loop);
        await controller.StartAsync();
        await Until(() => { loop.Advance(); return view.CanAccept; }, token);
        var installation = Assert.IsType<SourceAcceptanceResult>(broker.Installation);
        Assert.True(installation.Installed);
        Assert.Equal(source.ProcessId, installation.Target.AsWindows().ProcessId);
        Assert.Equal((long)source.Field, installation.Target.AsWindows().ControlHwnd);
        Assert.Equal("你好", N.ReadText(source.Field, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(0, 0), N.ReadSelection(source.Field));
        if (withdraw)
        {
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
            bool pressed = true;
            try
            {
                Tab(false);
                await Until(() => hook.StockLastActivation is not null, token);
                Assert.True(hook.StockLastActivation!.Claimed);
                hook.Revoke(); // A claimed press still owns repeat and release.
                Tab(false);
                Tab(true);
                pressed = false;
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
        Assert.NotEqual((nuint)0, N.Send(source.Field, 0x00C7)); // EM_UNDO
        await Until(() => N.ReadText(source.Field, Limits.MaxTextUnits) == "你好", token);
        Assert.Equal((source.Root, source.Field), N.Focused());
        Assert.Equal(0, broker.Writes);
    });

    private sealed class NotepadBroker : IInputBroker, IAsyncDisposable
    {
        private readonly BrokerClient _client;
        private ChildProcess? _child;
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
        internal NotepadBroker()
        {
            _client = new BrokerClient(info =>
            {
                // Keep BrokerClient's real authentication, pipe framing, timeout
                // and child owner. Only select the existing isolated test entry.
                info.FileName = "dotnet";
                info.ArgumentList.Clear();
                info.ArgumentList.Add(Path.Combine(RepositoryDirectory(),
                    "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"));
                info.ArgumentList.Add("--source-broker");
                try
                {
                    return _child = new ChildProcess(info);
                }
                catch (ChildProcessCleanupUnconfirmedException error)
                {
                    _child = error.Child as ChildProcess;
                    throw;
                }
            });
        }
        public bool Available => _client.Available;
        public int ProcessId => _client.ProcessId;
        public Task StartAsync() => _client.StartAsync();
        public async Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
        {
            if (request.Op is "write" or "direct-write")
                Writes++;
            var response = await _client.CallAsync(request, timeout);
            if (request.Op == "source-install" && response.SourceAcceptance is { Installed: true } installed)
            {
                Installation = installed;
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
        public void Dispose()
        {
        } // The test awaits exact child-family termination.
        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (_child is not null)
                    await _child.TerminateAndWaitAsync(cleanup.Token);
            }
            finally { _client.Dispose(); }
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
                Assert.Empty(existing);
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
                Assert.Equal(executable, ProcessImage.Read(source._child.Process.SafeHandle), ignoreCase: true);
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
            catch { await source.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _child.TerminateAndWaitAsync(cleanup.Token);
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
