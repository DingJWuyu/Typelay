using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class SessionShortcutHookTests(WpfDesktopFixture desktop)
{
    [Fact]
    public async Task DefaultHookPassesInjectedTabWithoutIssuingAcceptance()
    {
        await desktop.RunAsync(async () =>
        {
            const string original = "Injected Tab stays ordinary input";
            var input = new TextBox { Text = original, AcceptsTab = true, AcceptsReturn = true };
            var source = new Window { Content = input, Width = 420, Height = 160, Title = "TypeLay injected acceptance refusal" };
            source.Show();
            source.Activate();
            input.Focus();
            input.CaretIndex = original.Length;
            nint hwnd = new WindowInteropHelper(source).Handle;
            int down = 0, up = 0;
            input.PreviewKeyDown += (_, args) => { if (args.Key == Key.Tab) down++; };
            input.PreviewKeyUp += (_, args) => { if (args.Key == Key.Tab) up++; };
            using var hook = new KeyboardHook(); // The production default, without the automation constructor opt-in.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            bool pressed = false;
            try
            {
                await Until(() => hook.Ready && Native.GetForegroundWindow() == hwnd && input.IsKeyboardFocused, deadline.Token);
                Assert.All(new[] { 9, 0x10, 0x11, 0x12, 0x5B, 0x5C }, key => Assert.False(Native.GetAsyncKeyState(key) < 0));
                using var process = Process.GetCurrentProcess();
                long now = WindowsInterruptClock.NowMs;
                var snapshot = new InputSnapshot(new WindowsTarget(process.Id, process.StartTime.ToUniversalTime().Ticks,
                    (long)hwnd, (long)hwnd, "controlled-hook", "owned-source", Guid.NewGuid()),
                    original, new(original.Length, original.Length), new(10, 20, 300, 40), false,
                    FieldAccess.Supported, ImeState.Clear, now, 1, 1, WriteSupported: true);
                var plan = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, snapshot, null, "Result", "Result", 6);
                hook.Publish(new(snapshot, hook.UserEpoch, hook.ForegroundEpoch, true, hook.CommandGeneration, 23),
                    new(plan, 1, 1, now, now + Limits.PermitMaxAgeMs));
                Assert.Equal(1u, SendInput(1, [new KeyboardInput { Type = 1, Key = 9 }], Marshal.SizeOf<KeyboardInput>()));
                pressed = true;
                Assert.Equal(1u, SendInput(1, [new KeyboardInput { Type = 1, Key = 9, Flags = 2 }], Marshal.SizeOf<KeyboardInput>()));
                pressed = false;
                await Until(() => up == 1, deadline.Token);
                Assert.Equal(1, down);
                Assert.Equal(original + "\t", input.Text);
                Assert.True(input.IsKeyboardFocused);
                Assert.False(hook.TryRead(out _));
            }
            finally
            {
                if (pressed)
                    Assert.Equal(1u, SendInput(1, [new KeyboardInput { Type = 1, Key = 9, Flags = 2 }], Marshal.SizeOf<KeyboardInput>()));
            }
        });
    }

    [Fact]
    public async Task RebindingPreservesTheConsumedReleaseAndRequiresANewPress()
    {
        await desktop.RunAsync(async () =>
        {
            var input = new TextBox { Text = "Synthetic hook source", Margin = new(20) };
            var source = new Window { Content = input, Width = 420, Height = 160, Title = "TypeLay hook binding test" };
            source.Show();
            source.Activate();
            input.Focus();
            nint hwnd = new WindowInteropHelper(source).Handle;
            var down = new List<Key>();
            var up = new List<Key>();
            input.PreviewKeyDown += (_, args) => down.Add(args.Key);
            input.PreviewKeyUp += (_, args) => up.Add(args.Key);
            using var hook = new KeyboardHook(allowInjectedInput: true);
            var support = new WindowsApplicationSupport();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var pressed = new HashSet<ushort>();
            bool cleaned = true;
            try
            {
                await Until(() => hook.Ready && Native.GetForegroundWindow() == hwnd && input.IsKeyboardFocused, deadline.Token);
                foreach (int key in new[] { 0x75, 0x76, 0x77, 0x78, 0x7B })
                    Assert.False(Native.GetAsyncKeyState(key) < 0, "The fixture must not borrow a key that is already held.");
                Configure("F8", "F7");
                Publish();
                Send(0x77, true);
                var accepted = await Notice("accept");
                Assert.NotNull(accepted.Permit);
                Assert.Equal(hook.CommandGeneration, accepted.CommandGeneration);
                Assert.Equal(23, accepted.SessionRevision);

                // F8 is still held. F9 must pass rather than steal its release
                // or consume the newly published permit under the new binding.
                Configure("F9", "F6");
                Publish();
                Send(0x78, true);
                Send(0x78, false);
                Send(0x77, true);
                Send(0x77, false);
                await SourceBarrier();
                Assert.DoesNotContain(Key.F8, down);
                Assert.DoesNotContain(Key.F8, up);
                Assert.Equal(1, down.Count(key => key == Key.F9));
                Assert.Equal(1, up.Count(key => key == Key.F9));
                Assert.False(hook.TryRead(out _));
                Publish();
                Send(0x78, true);
                Send(0x78, false);
                Assert.NotNull((await Notice("accept")).Permit);
                await SourceBarrier();
                Assert.Equal(1, down.Count(key => key == Key.F9));
                Assert.Equal(1, up.Count(key => key == Key.F9));

                Publish();
                Send(0x75, true);
                var escaped = await Notice("escape");
                Assert.NotNull(escaped.Target);
                Assert.Equal(hook.CommandGeneration, escaped.CommandGeneration);
                Configure("F8", "F7");
                Publish();
                Send(0x76, true);
                Send(0x76, false);
                Send(0x75, false);
                await SourceBarrier();
                Assert.DoesNotContain(Key.F6, down);
                Assert.DoesNotContain(Key.F6, up);
                Assert.Contains(Key.F7, down);
                Assert.Contains(Key.F7, up);
                Assert.False(hook.TryRead(out _));
                // Ctrl keeps ordinary source writing closed while unmodified exit
                // remains available, without releasing the earlier consumed press.
                Configure("F8", "F7");
                Publish();
                Send(0x77, true);
                Assert.NotNull((await Notice("accept")).Permit);
                Configure("Ctrl+F8", "Esc");
                Assert.False(hook.CanInterceptAcceptance);
                Assert.False(hook.UsesUnmodifiedSessionKeys);
                Assert.True(hook.HasSessionConfiguration(new(hook.CommandGeneration,
                    SessionShortcutBindings.Restore("Ctrl+F8", "Esc"))));
                Publish();
                // The controlled snapshot tests capability presentation only;
                // the real Hook test below still proves ordinary input passes.
                var ordinarySettings = new UserSettings(ExperimentalWriteback: true);
                Assert.True(support.Read(ordinarySettings).Rows.Single(row => row.Id == "desktop-text").Capabilities.TabAccept);
                Assert.False(support.Read(ordinarySettings with
                {
                    AcceptShortcut = "Ctrl+F8"
                })
                    .Rows.Single(row => row.Id == "desktop-text").Capabilities.TabAccept);
                int exitDown = down.Count(key => key == Key.Escape);
                int functionDown = down.Count(key => key == Key.F8);
                Send(0x77, false);
                Send(27, true);
                Send(27, false);
                Send(0x77, true);
                Send(0x77, false);
                await SourceBarrier();
                Assert.Equal(exitDown, down.Count(key => key == Key.Escape));
                Assert.Equal(functionDown + 1, down.Count(key => key == Key.F8));
                Assert.Equal(1, up.Count(key => key == Key.F8));
                Assert.Equal(hook.CommandGeneration, (await Notice("escape")).CommandGeneration);
                Assert.False(hook.TryRead(out _));
                Configure("Tab", "Esc");
                Assert.True(hook.CanInterceptAcceptance);
                Assert.Equal("Synthetic hook source", input.Text);
                Assert.True(input.IsKeyboardFocused);
            }
            finally
            {
                foreach (ushort key in pressed.ToArray())
                    cleaned &= SendInput(1, [new KeyboardInput { Type = 1, Key = key, Flags = 2 }], Marshal.SizeOf<KeyboardInput>()) == 1;
            }
            Assert.True(cleaned, "The fixture must release every key it injected.");

            void Configure(string accept, string exit)
            {
                Assert.True(SessionShortcutBindings.TryParse(accept, exit, out var bindings));
                hook.ConfigureShortcuts(false, "Ctrl+Shift+F9", "Ctrl+Shift+F10", bindings!, true);
            }
            void Publish()
            {
                Assert.Equal(hwnd, Native.GetForegroundWindow());
                using var process = Process.GetCurrentProcess();
                long now = WindowsInterruptClock.NowMs;
                // Controlled lease isolates the real hook dispatch boundary. This
                // does not certify a field provider, a model, IME or a source write.
                var snapshot = new InputSnapshot(new WindowsTarget(process.Id, process.StartTime.ToUniversalTime().Ticks,
                    (long)hwnd, (long)hwnd, "controlled-hook", "owned-source", Guid.NewGuid()),
                    input.Text, new(input.Text.Length, input.Text.Length), new(10, 20, 300, 40), false,
                    FieldAccess.Supported, ImeState.Clear, now, 1, 1, WriteSupported: true);
                support.Observe(new(snapshot, FocusMember.Source), true, true, now, support.CheckGeneration);
                var plan = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, snapshot, null, "Result", "Result", 6);
                hook.Publish(new(snapshot, hook.UserEpoch, hook.ForegroundEpoch, true, hook.CommandGeneration, 23),
                    new(plan, 1, 1, now, now + Limits.PermitMaxAgeMs));
            }
            async Task<InputNotice> Notice(string kind)
            {
                InputNotice? notice = null;
                await Until(() => notice is not null || hook.TryRead(out notice), deadline.Token);
                Assert.Equal(kind, notice!.Kind);
                return notice;
            }
            async Task SourceBarrier()
            {
                int before = up.Count(key => key == Key.F12);
                Send(0x7B, true);
                Send(0x7B, false);
                await Until(() => up.Count(key => key == Key.F12) > before, deadline.Token);
            }
            void Send(ushort key, bool isDown)
            {
                var value = new KeyboardInput { Type = 1, Key = key, Flags = isDown ? 0u : 2u };
                uint count = SendInput(1, [value], Marshal.SizeOf<KeyboardInput>());
                if (count == 1)
                {
                    if (isDown)
                        pressed.Add(key);
                    else
                        pressed.Remove(key);
                }
                Assert.Equal(1u, count);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OrdinaryControlExitUsesOneOriginalLeaseAndKeepsItsRelease(bool automation) => desktop.RunAsync(async () =>
    {
        const string original = "Ordinary exit never writes the source";
        var input = new TextBox { Text = original };
        var source = new Window { Content = input, Width = 420, Height = 160, Title = "TypeLay ordinary exit pairing" };
        source.Show();
        source.Activate();
        input.Focus();
        nint hwnd = new WindowInteropHelper(source).Handle;
        int functionDown = 0, functionUp = 0, controlUp = 0;
        input.PreviewKeyDown += (_, args) => { if (args.Key == Key.F8) functionDown++; };
        input.PreviewKeyUp += (_, args) =>
        {
            if (args.Key == Key.F8)
                functionUp++;
            if (args.Key == Key.LeftCtrl)
                controlUp++;
        };
        using var hook = new KeyboardHook(allowInjectedInput: automation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        bool held = false;
        try
        {
            await Until(() => hook.Ready && Native.GetForegroundWindow() == hwnd && input.IsKeyboardFocused, deadline.Token);
            Assert.All(new[] { 0x11, 0xA2, 0x77 }, key => Assert.False(Native.GetAsyncKeyState(key) < 0));
            Configure();
            var snapshot = Publish();
            long epoch = hook.UserEpoch, sourceEpoch = hook.SourceUserEpoch;
            // These are real SendInput events, accepted only by the explicit
            // automation Hook. The controlled lease proves dispatch, not IME.
            Send([ScanKey(0x1D, true), ScanKey(0x42, true)]);
            held = true;
            await Until(() => hook.UserEpoch == epoch + 1, deadline.Token);
            Assert.Equal(sourceEpoch + 1, hook.SourceUserEpoch);
            Assert.False(hook.CanInterceptAcceptance);
            if (automation)
            {
                InputNotice? notice = null;
                await Until(() => notice is not null || hook.TryRead(out notice), deadline.Token);
                Assert.Equal("escape", notice!.Kind);
                Assert.Null(notice.Permit);
                Assert.Equal(snapshot.Target, notice.Target);
                Assert.Equal(23, notice.SessionRevision);
                Assert.Equal(epoch + 1, notice.UserEpoch);
                Assert.InRange(notice.AcceptedAtMs, snapshot.ObservedAtMs, snapshot.ObservedAtMs + Limits.ObservationMaxAgeMs);
            }
            else
                await Until(() => functionDown == 1, deadline.Token);
            // Retiring or replacing the configuration cannot release the Fn
            // already consumed by this exact Ctrl group.
            Configure();
            Publish();
            Send([ScanKey(0x42, true)]);
            if (!automation)
                await Until(() => functionDown == 2, deadline.Token);
            Send([ScanKey(0x42, false)]);
            if (!automation)
                await Until(() => functionUp == 1, deadline.Token);
            // The original Fn has released while Ctrl remains held. A new
            // independent Fn, its repeat and its up all belong to the app.
            // Observe each passed down before the next, since Windows can
            // coalesce queued repeats into one WPF key event.
            Send([ScanKey(0x42, true)]);
            await Until(() => functionDown == (automation ? 1 : 3), deadline.Token);
            Send([ScanKey(0x42, true)]);
            await Until(() => functionDown == (automation ? 2 : 4), deadline.Token);
            Send([ScanKey(0x42, false), ScanKey(0x1D, false)]);
            held = false;
            await Until(() => controlUp == 1, deadline.Token);
            Assert.Equal(automation ? 2 : 4, functionDown);
            Assert.Equal(automation ? 1 : 2, functionUp);
            Assert.False(hook.TryRead(out _));

            if (automation)
            {
                Publish();
                Send([ScanKey(0x1D, true)]);
                held = true;
                await Until(() => hook.UserEpoch == epoch + 2, deadline.Token);
                Configure(); // A new configuration and capture cannot rescue the old Ctrl.
                Publish();
                Send([ScanKey(0x42, true), ScanKey(0x42, false), ScanKey(0x1D, false)]);
                held = false;
                await Until(() => controlUp == 2, deadline.Token);
                Assert.Equal(3, functionDown);
                Assert.Equal(2, functionUp);
                Assert.False(hook.TryRead(out _));
            }
            Assert.Equal(original, input.Text);
            Assert.True(input.IsKeyboardFocused);
        }
        finally
        {
            if (held)
                Send([ScanKey(0x42, false), ScanKey(0x1D, false)]);
        }
        void Configure()
        {
            Assert.True(SessionShortcutBindings.TryParse("Ctrl+F7", "Ctrl+F8", out var bindings));
            hook.ConfigureShortcuts(false, "Ctrl+Shift+F9", "Ctrl+Shift+F10", bindings!, true);
        }
        InputSnapshot Publish()
        {
            using var process = Process.GetCurrentProcess();
            long now = WindowsInterruptClock.NowMs;
            var snapshot = new InputSnapshot(new WindowsTarget(process.Id, process.StartTime.ToUniversalTime().Ticks,
                (long)hwnd, (long)hwnd, "controlled-hook", "ordinary-source", Guid.NewGuid()),
                original, new(original.Length, original.Length), new(10, 20, 300, 40), false,
                FieldAccess.Supported, ImeState.Clear, now, 1, 1, WriteSupported: true);
            hook.Publish(new(snapshot, hook.UserEpoch, hook.ForegroundEpoch, true, hook.CommandGeneration, 23), null);
            return snapshot;
        }
        static KeyboardInput ScanKey(ushort scan, bool down) => new()
        {
            Type = 1,
            Scan = scan,
            Flags = down ? 8u : 10u
        };
        static void Send(KeyboardInput[] keys) => Assert.Equal((uint)keys.Length, SendInput((uint)keys.Length, keys, Marshal.SizeOf<KeyboardInput>()));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RetiredEditorExitDoesNotSuppressTheNextOrdinarySourceExit(bool control) => desktop.RunAsync(async () =>
    {
        var input = new TextBox { Text = "controlled return source" };
        var source = new Window { Content = input, Width = 420, Height = 160, Title = "TypeLay return key pairing" };
        source.Show();
        source.Activate();
        input.Focus();
        nint hwnd = new WindowInteropHelper(source).Handle;
        ushort exitKey = control ? (ushort)0x77 : (ushort)27;
        Key wpfExit = control ? Key.F8 : Key.Escape;
        int escapeDown = 0, escapeUp = 0, barrierUp = 0;
        input.PreviewKeyDown += (_, args) => { if (args.Key == wpfExit) escapeDown++; };
        input.PreviewKeyUp += (_, args) =>
        {
            if (args.Key == wpfExit)
                escapeUp++;
            if (args.Key == Key.F12)
                barrierUp++;
        };
        using var hook = new KeyboardHook(allowInjectedInput: true);
        using var process = Process.GetCurrentProcess();
        using var mapping = OwnedPracticeAcceptanceProtocol.CreateWitness(process.Handle);
        var writer = new OwnedWitnessWriter(mapping);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var held = new HashSet<ushort>();
        Guid intent = Guid.NewGuid();
        try
        {
            await Until(() => hook.ShortcutsReady && Native.GetForegroundWindow() == hwnd && input.IsKeyboardFocused, deadline.Token);
            Assert.All(new[] { 27, 0x7B, 0x10, 0x11, 0x12, 0x5B, 0x5C }, key => Assert.False(Native.GetAsyncKeyState(key) < 0));
            hook.ConfigureShortcuts(false, "Ctrl+Shift+F9", "Ctrl+Shift+F10",
                control ? SessionShortcutBindings.Restore("Ctrl+F7", "Ctrl+F8") : SessionShortcutBindings.Default, true);
            hook.BeginOwnedEditorForeground(intent, hwnd, hwnd, _ => { });
            var descriptor = new OwnedCaptureDescriptor(process.Id, process.StartTime.ToUniversalTime().Ticks,
                (long)hwnd, (long)hwnd, 1, 1, 1, 1);
            if (control)
                Send(0x11, true);
            Send(exitKey, true);
            await Until(() => escapeDown == 1, deadline.Token);
            Assert.True(hook.BeginOwnedReturnInput(OwnedReturnKind.Escape, descriptor, out var returning),
                "The original scan-code exit must reach the production return-input admission.");
            Assert.NotNull(returning);
            Assert.Equal(intent, returning.EditorIntent);
            Assert.Equal(hook.EditorInputSequence, returning.Sequence);
            Assert.True(returning.Sequence > 0);
            Assert.False(returning.Released);
            Assert.Equal(new OwnedReturnKeyPress(control ? 0x42u : 1u, false, true, exitKey,
                control ? ShortcutModifiers.Control : ShortcutModifiers.None), returning.KeyPress);
            Assert.False(hook.TryRead(out _));

            // The stopped reader isolates the real Hook's owned-source early
            // return. Controlled metadata grants no native field or write authority.
            Assert.True(writer.Stop());
            hook.SetOwnedChannel(Guid.NewGuid(), writer, descriptor);
            if (control)
                Send(0x11, false);
            Send(0x7B, true); // Another actual input retires the return intent while Esc is held.
            Send(0x7B, false);
            Send(exitKey, false);
            await Until(() => barrierUp == 1 && escapeUp == 1, deadline.Token);
            hook.EndOwnedEditorForeground(intent);
            hook.ClearOwnedChannel();
            hook.ConfigureShortcuts(false, "Ctrl+Shift+F9", "Ctrl+Shift+F10",
                SessionShortcutBindings.Restore("Tab", control ? "F8" : "Esc"), true);
            long now = WindowsInterruptClock.NowMs;
            var snapshot = new InputSnapshot(new WindowsTarget(process.Id, process.StartTime.ToUniversalTime().Ticks,
                (long)hwnd, (long)hwnd, "controlled-hook", "ordinary-source", Guid.NewGuid()),
                input.Text, new(input.Text.Length, input.Text.Length), new(10, 20, 300, 40), false,
                FieldAccess.Supported, ImeState.Clear, now, 1, 1);
            hook.Publish(new(snapshot, hook.UserEpoch, hook.ForegroundEpoch, true, hook.CommandGeneration, 23), null);
            Send(exitKey, true);
            Send(exitKey, false);
            InputNotice? notice = null;
            await Until(() => notice is not null || hook.TryRead(out notice), deadline.Token);
            Assert.Equal("escape", notice!.Kind);
            Assert.Equal(snapshot.Target, notice.Target);
            Assert.Equal(23, notice.SessionRevision);
            Send(0x7B, true);
            Send(0x7B, false);
            await Until(() => barrierUp == 2, deadline.Token);
            Assert.Equal(1, escapeDown);
            Assert.Equal(1, escapeUp);
            Assert.False(hook.TryRead(out _));
            Assert.Equal("controlled return source", input.Text);
        }
        finally
        {
            hook.EndOwnedEditorForeground(intent);
            hook.ClearOwnedChannel();
            foreach (ushort key in held.ToArray())
                Send(key, false);
        }
        void Send(ushort key, bool down)
        {
            // Real scan identities cross the production return fence.
            // SendInput itself supplies the injected flag.
            ushort scan = key switch
            {
                27 => 1,
                0x11 => 0x1d,
                0x77 => 0x42,
                _ => 0
            };
            var edge = new KeyboardInput
            {
                Type = 1,
                Key = key,
                Scan = scan,
                Flags = (scan != 0 ? 8u : 0u) | (down ? 0u : 2u)
            };
            Assert.Equal(1u, SendInput(1, [edge], Marshal.SizeOf<KeyboardInput>()));
            if (down)
                held.Add(key);
            else
                held.Remove(key);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortcutDiagnosticsPreserveNoLeaseRejectionAndIdentifyLeaseRemoval(bool tracing)
    {
        string path = Path.Combine(Path.GetTempPath(), "typelay-hook-trace-" + Guid.NewGuid().ToString("N") + ".jsonl");
        string? previous = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
        Environment.SetEnvironmentVariable("TYPELAY_TRACE", tracing ? path : null);
        try
        {
            await desktop.RunAsync(async () =>
            {
                var input = new TextBox { Text = "private-hook-source-marker" };
                var source = new Window { Content = input, Width = 420, Height = 160, Title = "TypeLay hook diagnostic test" };
                source.Show();
                source.Activate();
                input.Focus();
                nint hwnd = new WindowInteropHelper(source).Handle;
                using var hook = new KeyboardHook(allowInjectedInput: true);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                bool pressed = false;
                int presses = 0, releases = 0;
                input.PreviewKeyDown += (_, args) => { if (args.Key == Key.F9) presses++; };
                input.PreviewKeyUp += (_, args) => { if (args.Key == Key.F9) releases++; };
                try
                {
                    await Until(() => hook.ShortcutsReady && Native.GetForegroundWindow() == hwnd && input.IsKeyboardFocused, deadline.Token);
                    Assert.False(Native.GetAsyncKeyState(0x78) < 0, "The fixture must not borrow an already held F9.");
                    // Controlled binding isolates Hook dispatch, not OS reservation or IME.
                    hook.SetShortcuts([new("wake", 0x78, ShortcutModifiers.None)]);
                    Guid generation = hook.CommandGeneration;
                    var rejected = await Press();
                    Assert.Equal(Denial.StaleObservation, rejected.Rejection);
                    Assert.Null(rejected.Source);
                    Assert.Equal(generation, rejected.CommandGeneration);
                    Assert.Equal(1, rejected.CommandSequence);

                    using var process = Process.GetCurrentProcess();
                    var snapshot = new InputSnapshot(new WindowsTarget(process.Id, process.StartTime.ToUniversalTime().Ticks,
                        (long)hwnd, (long)hwnd, "controlled-hook", "private-target-marker", Guid.NewGuid()),
                        input.Text, new(input.Text.Length, input.Text.Length), new(10, 20, 300, 40), false,
                        FieldAccess.Supported, ImeState.Clear, WindowsInterruptClock.NowMs, 1, 1);
                    hook.PublishShortcut(new(snapshot, hook.ContentEpoch, hook.ForegroundEpoch));
                    hook.Revoke();
                    var revoked = await Press();
                    Assert.Equal(Denial.StaleObservation, revoked.Rejection);
                    Assert.Null(revoked.Source);
                    Assert.Equal(2, revoked.CommandSequence);
                    hook.PublishShortcut(new(snapshot, hook.ContentEpoch, hook.ForegroundEpoch));
                    hook.SetShortcuts([new("wake", 0x78, ShortcutModifiers.None)]);
                    Guid nextGeneration = hook.CommandGeneration;
                    var reconfigured = await Press();
                    Assert.NotEqual(generation, nextGeneration);
                    Assert.Equal(nextGeneration, reconfigured.CommandGeneration);
                    Assert.Equal(Denial.StaleObservation, reconfigured.Rejection);
                    Assert.Null(reconfigured.Source);
                    Assert.Equal(3, reconfigured.CommandSequence);
                    Assert.Equal("private-hook-source-marker", input.Text);

                    Assert.Equal(tracing, File.Exists(path));
                    if (!tracing)
                        return;
                    string trace = File.ReadAllText(path);
                    Assert.DoesNotContain("private-", trace);
                    var entries = File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
                    var first = Assert.Single(entries, item => item.GetProperty("operation").GetString() == "shortcut-key"
                        && item.GetProperty("commandSequence").GetInt64() == 1);
                    Assert.Equal("no-lease", first.GetProperty("reason").GetString());
                    Assert.Equal(generation, first.GetProperty("commandGeneration").GetGuid());
                    var revoke = Assert.Single(entries, item => item.GetProperty("operation").GetString() == "shortcut-revoke");
                    Assert.Equal(generation, revoke.GetProperty("leaseCommandGeneration").GetGuid());
                    Assert.Equal(snapshot.ObservedAtMs, revoke.GetProperty("leaseObservedAtMs").GetInt64());
                    var configure = Assert.Single(entries, item => item.GetProperty("operation").GetString() == "shortcut-configure"
                        && item.GetProperty("commandGeneration").GetGuid() == nextGeneration);
                    Assert.Equal(generation, configure.GetProperty("leaseCommandGeneration").GetGuid());
                }
                finally
                {
                    if (pressed)
                        Assert.Equal(1u, SendInput(1, [new KeyboardInput { Type = 1, Key = 0x78, Flags = 2 }], Marshal.SizeOf<KeyboardInput>()));
                }

                async Task<InputNotice> Press()
                {
                    int pressedBefore = presses, releasedBefore = releases;
                    Assert.Equal(1u, SendInput(1, [new KeyboardInput { Type = 1, Key = 0x78 }], Marshal.SizeOf<KeyboardInput>()));
                    pressed = true;
                    Assert.Equal(1u, SendInput(1, [new KeyboardInput { Type = 1, Key = 0x78, Flags = 2 }], Marshal.SizeOf<KeyboardInput>()));
                    pressed = false;
                    InputNotice? notice = null;
                    await Until(() => (notice is not null || hook.TryRead(out notice)) && hook.CommandsReleased, deadline.Token);
                    // This registered command owns both edges, including a
                    // refusal. Drain the source dispatcher before asserting
                    // that no second consumer received either edge.
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.Equal("wake", notice!.Kind);
                    Assert.Equal(pressedBefore, presses);
                    Assert.Equal(releasedBefore, releases);
                    return notice;
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("TYPELAY_TRACE", previous);
            File.Delete(path);
        }
    }

    private static async Task Until(Func<bool> ready, CancellationToken cancellation)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(10, cancellation);
        Assert.True(ready(), "The single injected key sequence must reach its owned hook/source boundary.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] input, int size);
}
