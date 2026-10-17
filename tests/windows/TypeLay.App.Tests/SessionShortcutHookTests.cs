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
                // Loading a shared Ctrl chord disables both Windows actions,
                // without releasing the earlier consumed physical press.
                Configure("F8", "F7");
                Publish();
                Send(0x77, true);
                Assert.NotNull((await Notice("accept")).Permit);
                Configure("Ctrl+F8", "Esc");
                Assert.False(hook.CanInterceptAcceptance);
                Assert.False(hook.HasSessionConfiguration(new(hook.CommandGeneration,
                    SessionShortcutBindings.Restore("Ctrl+F8", "Esc"))));
                Publish();
                int exitDown = down.Count(key => key == Key.Escape);
                int functionDown = down.Count(key => key == Key.F8);
                Send(0x77, false);
                Send(27, true);
                Send(27, false);
                Send(0x77, true);
                Send(0x77, false);
                await SourceBarrier();
                Assert.Equal(exitDown + 1, down.Count(key => key == Key.Escape));
                Assert.Equal(functionDown + 1, down.Count(key => key == Key.F8));
                Assert.Equal(1, up.Count(key => key == Key.F8));
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
                    await Until(() => (notice is not null || hook.TryRead(out notice)) && releases > releasedBefore, deadline.Token);
                    Assert.Equal("wake", notice!.Kind);
                    Assert.Equal(pressedBefore + 1, presses);
                    Assert.Equal(releasedBefore + 1, releases);
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
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] input, int size);
}
