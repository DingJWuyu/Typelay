using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class LocalDraftTapInputTests(WpfDesktopFixture desktop)
{
    // Real controlled LL/WPF input, not physical-key evidence. The original
    // full PracticeHost fast-pair cases still prove the source/Core/Bind route.
    [Theory]
    [InlineData("current")]
    [InlineData("unarmed-prefix")]
    [InlineData("wrong-up")]
    [InlineData("intervening-key")]
    [InlineData("repeat")]
    [InlineData("extra-up")]
    [InlineData("queued-pairs")]
    [InlineData("same-time-pairs")]
    [InlineData("old-down-message")]
    [InlineData("wrong-up-message")]
    [InlineData("expired")]
    [InlineData("new-opening")]
    [InlineData("retired-before-up")]
    [InlineData("retired-after-up")]
    [InlineData("external-focus")]
    [InlineData("editor-focus-cycle")]
    public Task OnlyTheOriginalReleasedTabCanBeClaimedOnce(string change) => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var editor = new TextBox { Text = "Synthetic local draft", AcceptsTab = true };
        var window = new Window { Content = editor, Width = 420, Height = 160 };
        nint hwnd = new WindowInteropHelper(window).EnsureHandle();
        using var hook = new KeyboardHook(allowInjectedInput: true, editorHwnd: hwnd, observeEditorForegroundDelivery: true);
        hook.ConfigureShortcuts(false, "Ctrl+Shift+F9", "Ctrl+Shift+F10", SessionShortcutBindings.Default, true);
        Guid opening = Guid.NewGuid();
        var held = new HashSet<ushort>();
        var captures = new List<KeyboardHook.LocalDraftPress?>();
        var downTimes = new List<uint>();
        var upTimes = new List<uint>();
        int cancellations = 0;
        object? firstCancellation = null;
        // DEBUG-t12: independent observation of original event HWND identities.
        // This callback does not repair focus or participate in Hook permission.
        var foregroundEvents = new List<object>();
        int foregroundDropped = 0, foregroundErrors = 0;
        string foregroundStage = "before-activation";
        Native.WinEventProc observeForeground = (_, _, original, _, _, eventThread, eventTime) =>
        {
            try
            {
                if (foregroundEvents.Count == 32)
                {
                    foregroundDropped++;
                    return;
                }
                long atTimestamp = Stopwatch.GetTimestamp();
                uint windowThread = Native.GetWindowThreadProcessId(original, out uint process);
                var className = new StringBuilder(256);
                int classLength = LocalDraftWindowClass(original, className, className.Capacity);
                string membership = original == hwnd ? "test-editor" : process != Environment.ProcessId ? "external"
                    : Application.Current.Windows.Cast<Window>().Any(candidate => new WindowInteropHelper(candidate).Handle == original)
                        ? "another-current-wpf-window" : "same-process-unlisted";
                foregroundEvents.Add(new
                {
                    stage = foregroundStage, atTimestamp, atMs = WindowsInterruptClock.NowMs, eventTime,
                    originalHwnd = (long)original, eventThread, windowThread, process, membership,
                    className = classLength > 0 ? className.ToString() : null
                });
            }
            catch (Exception error) when (error is not OutOfMemoryException) { foregroundErrors++; }
        };
        nint foregroundObservation = 0;
        KeyEventHandler down = (_, e) =>
        {
            e.Handled = true;
            if (e.Key != Key.Tab)
                return;
            var message = ComponentDispatcher.CurrentKeyboardMessage;
            uint time = unchecked((uint)message.time);
            downTimes.Add(time);
            uint scan = (uint)(message.lParam.ToInt64() >> 16) & 255;
            captures.Add(hook.CaptureLocalDraftPress(opening, scan, (message.lParam.ToInt64() & 0x01000000) != 0,
                change == "old-down-message" ? unchecked(time - 1) : time));
        };
        KeyEventHandler up = (_, e) =>
        {
            e.Handled = true;
            if (e.Key == Key.Tab)
                upTimes.Add(unchecked((uint)ComponentDispatcher.CurrentKeyboardMessage.time));
        };
        window.AddHandler(Keyboard.PreviewKeyDownEvent, down, true);
        window.AddHandler(Keyboard.PreviewKeyUpEvent, up, true);
        try
        {
            if (change is "current" or "unarmed-prefix" or "expired")
                foregroundObservation = Native.SetWinEventHook(3, 3, 0, observeForeground, 0, 0, 0);
            // Install before the original activation, then consume that exact
            // event only after the product callback stack has fully processed it.
            // No extra activation, queue drain, timestamp inference or retry.
            await Until(() => hook.ShortcutsReady);
            window.Show();
            Assert.True(window.Activate());
            Assert.True(editor.Focus());
            await Until(() => hook.ShortcutsReady && hook.EditorForegroundDelivered is { IsCompletedSuccessfully: true }
                && Native.GetForegroundWindow() == hwnd && editor.IsKeyboardFocused);
            Assert.All(new[] { 9, 0x77, 0x10, 0x11, 0x12, 0x5b, 0x5c }, key => Assert.False(Native.GetAsyncKeyState(key) < 0));
            foregroundStage = "anchor-open";
            hook.BeginLocalDraftAnchor(opening, hwnd, hwnd, id =>
            {
                if (Interlocked.Increment(ref cancellations) != 1 || change != "unarmed-prefix")
                    return;
                try
                {
                    long observedAtTimestamp = Stopwatch.GetTimestamp();
                    nint foreground = Native.GetForegroundWindow();
                    Volatile.Write(ref firstCancellation, new
                    {
                        observedAtTimestamp,
                        atMs = WindowsInterruptClock.NowMs,
                        openingMatches = id == opening,
                        foreground = foreground == hwnd ? "overlay" : foreground == 0 ? "none" : "other"
                    });
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            });
            ushort tab = Scan(9), otherScan = Scan(0x77); // F8 never inserts editor text.
            Input[] pair = [Edge(tab, false), Edge(tab, true)];
            if (change == "unarmed-prefix")
            {
                foregroundStage = "unarmed-prefix";
                Send(pair);
                await Until(() => upTimes.Count == 1);
                Assert.Null(captures.Single());
                long anchorCallTimestamp = Stopwatch.GetTimestamp();
                bool anchorCurrent = hook.LocalDraftAnchorCurrent(opening);
                long anchorObservedTimestamp = Stopwatch.GetTimestamp();
                if (!anchorCurrent)
                {
                    try
                    {
                        // This is a public cancellation callback, not the original
                        // WinEvent. Its foreground and these reads are not atomic
                        // observations or proof of the cancellation cause.
                        TestContext.Current.TestOutputHelper?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                        {
                            operation = "local-draft-unarmed-prefix-anchor-retired",
                            anchorCurrent,
                            anchorCallTimestamp,
                            anchorObservedTimestamp,
                            timestampFrequency = Stopwatch.Frequency,
                            cancelCallbacksAtDiagnostic = Volatile.Read(ref cancellations),
                            firstCancellation = Volatile.Read(ref firstCancellation),
                            captures = captures.Count,
                            downs = downTimes.Count,
                            ups = upTimes.Count
                        }));
                    }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                }
                Assert.True(anchorCurrent);
                captures.Clear();
                downTimes.Clear();
                upTimes.Clear();
            }
            hook.SetLocalDraftEditorFocus(opening, true);
            foregroundStage = "armed-pair";
            int expectedUps = 1;
            if (change == "retired-before-up")
            {
                Send([pair[0]]);
                await Until(() => downTimes.Count == 1);
                Assert.NotNull(captures[0]);
                hook.EndLocalDraftAnchor(opening);
                Send([pair[1]]);
            }
            else
            {
                Input[] sequence = change switch
                {
                    "wrong-up" => [pair[0], Edge(otherScan, true), pair[1]],
                    "intervening-key" => [pair[0], Edge(otherScan, false), Edge(otherScan, true), pair[1]],
                    "repeat" => [pair[0], pair[0], pair[1]],
                    "extra-up" => [pair[0], pair[1], pair[1]],
                    "queued-pairs" or "same-time-pairs" => [pair[0], pair[1], pair[0], pair[1]],
                    _ => pair
                };
                if (change is "extra-up" or "queued-pairs" or "same-time-pairs")
                    expectedUps = 2;
                if (change == "same-time-pairs")
                {
                    uint time = unchecked((uint)Environment.TickCount) | 1u;
                    for (int i = 0; i < sequence.Length; i++)
                        sequence[i].Time = time;
                }
                Send(sequence);
            }
            await Until(() => upTimes.Count == expectedUps);
            foregroundStage = "before-claim";
            if (change == "same-time-pairs")
            {
                Assert.Equal(2, downTimes.Count);
                Assert.Equal(downTimes[0], downTimes[1]);
            }
            var press = captures.FirstOrDefault();
            Guid newer = Guid.NewGuid();
            if (change is "new-opening")
                hook.BeginLocalDraftAnchor(newer, hwnd, hwnd, _ => { });
            else if (change is "retired-after-up")
                hook.EndLocalDraftAnchor(opening);
            else if (change == "expired")
            {
                Assert.NotNull(press);
                while (WindowsInterruptClock.NowMs < press.ExpiresAtMs)
                    await Task.Delay(5, deadline.Token);
            }
            else if (change == "editor-focus-cycle")
            {
                hook.SetLocalDraftEditorFocus(opening, false);
                hook.SetLocalDraftEditorFocus(opening, true);
                Assert.False(hook.LocalDraftAnchorCurrent(opening));
            }
            else if (change == "external-focus")
            {
                var other = new Window { Content = new TextBox(), Width = 250, Height = 120 };
                other.Show();
                Assert.True(other.Activate());
                await Until(() => !hook.LocalDraftAnchorCurrent(opening));
                Assert.True(window.Activate());
                Assert.True(editor.Focus());
            }
            Assert.Equal(hwnd, Native.GetForegroundWindow());
            Assert.Same(editor, Keyboard.FocusedElement);
            deadline.Token.ThrowIfCancellationRequested();
            KeyboardHook.LocalDraftTap? tap = null;
            KeyboardHook.LocalDraftTapClaimObservation? claim = null;
            if (press is not null)
            {
                tap = hook.ClaimLocalDraftTap(press, tab, false,
                    change == "wrong-up-message" ? unchecked(upTimes[0] + 1) : upTimes[0], out var observed);
                claim = observed;
            }
            if ((change is "current" or "unarmed-prefix") && (press is null || tap is null))
            {
                try
                {
                    TestContext.Current.TestOutputHelper?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operation = "local-draft-tap-claim-refused",
                        change,
                        reason = claim?.Result.ToString(),
                        evaluatedAtMs = claim?.EvaluatedAtMs,
                        upSequence = claim?.UpSequence,
                        releasedAtMs = claim?.ReleasedAtMs,
                        nativeUpTime = claim?.NativeUpTime,
                        pressSequence = press?.Sequence,
                        pressReceivedAtMs = press?.ReceivedAtMs,
                        pressNativeTime = press?.NativeTime,
                        pressScan = press?.Key?.ScanCode,
                        pressExtended = press?.Key?.Extended,
                        messageScan = tab,
                        downTimes = downTimes.Take(2).ToArray(),
                        upTimes = upTimes.Take(2).ToArray(),
                        downDropped = Math.Max(0, downTimes.Count - 2),
                        upDropped = Math.Max(0, upTimes.Count - 2)
                    }));
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
            if (change is "current" or "unarmed-prefix")
            {
                Assert.NotNull(press);
                Assert.NotNull(tap);
                Assert.Same(press, tap.Press);
                Assert.Equal(press.Sequence + 1, tap.UpSequence);
                Assert.Equal(hook.EditorInputSequence, tap.UpSequence);
                Assert.Equal(upTimes[0], tap.NativeUpTime);
                Assert.InRange(tap.ReleasedAtMs, press.ReceivedAtMs, press.ExpiresAtMs - 1);
                Assert.True(hook.LocalDraftTapCurrent(tap));
                Assert.Null(hook.ClaimLocalDraftTap(press, tab, false, upTimes[0]));
                hook.EndLocalDraftAnchor(opening);
                Assert.False(hook.LocalDraftTapCurrent(tap));
            }
            else
                Assert.Null(tap);
            if (change == "new-opening")
                Assert.True(hook.LocalDraftAnchorCurrent(newer));
            Assert.Equal("Synthetic local draft", editor.Text);
            Assert.False(hook.TryRead(out _)); // No source/action notice or acceptance permit.
        }
        finally
        {
            if (foregroundObservation != 0)
            {
                Native.UnhookWinEvent(foregroundObservation);
                GC.KeepAlive(observeForeground);
                try
                {
                    TestContext.Current.TestOutputHelper?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operation = "DEBUG-t12-local-foreground-identity", change,
                        testProcess = Environment.ProcessId, testHwnd = (long)hwnd, timestampFrequency = Stopwatch.Frequency,
                        events = foregroundEvents, dropped = foregroundDropped, errors = foregroundErrors,
                        cancelCallbacks = Volatile.Read(ref cancellations), firstCancellation = Volatile.Read(ref firstCancellation)
                    }));
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
            window.RemoveHandler(Keyboard.PreviewKeyDownEvent, down);
            window.RemoveHandler(Keyboard.PreviewKeyUpEvent, up);
            hook.EndLocalDraftAnchor(opening);
            foreach (ushort scan in held.ToArray())
                Assert.Equal(1u, SendInput(1, [Edge(scan, true)], Marshal.SizeOf<Input>()));
        }
        async Task Until(Func<bool> condition)
        {
            while (!condition())
                await Task.Delay(5, deadline.Token);
        }
        void Send(Input[] inputs)
        {
            Assert.Equal(hwnd, Native.GetForegroundWindow());
            Assert.Same(editor, Keyboard.FocusedElement);
            deadline.Token.ThrowIfCancellationRequested();
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            foreach (var edge in inputs.Take((int)sent))
                if ((edge.Flags & 2) == 0)
                    held.Add(edge.Scan);
                else
                    held.Remove(edge.Scan);
            Assert.Equal((uint)inputs.Length, sent); // A partial send is never retried.
        }
    });

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int LocalDraftWindowClass(nint hwnd, StringBuilder value, int capacity);

    [Fact]
    public Task AReleasedReplaceThatExpiresBeforeClaimDoesNotConsumeTheFirstFreshRetry() => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var replace = new Button { Content = "Replace" };
        var window = new Window { Content = replace, Width = 420, Height = 160 };
        window.Show();
        Assert.True(window.Activate());
        Assert.True(replace.Focus());
        nint hwnd = new WindowInteropHelper(window).Handle;
        using var hook = new KeyboardHook(allowInjectedInput: true, editorHwnd: hwnd);
        hook.ConfigureShortcuts(false, "Ctrl+Shift+F9", "Ctrl+Shift+F10", SessionShortcutBindings.Default, true);
        Guid opening = Guid.NewGuid(), confirmation = Guid.NewGuid();
        ushort enter = Scan(13);
        int cancellations = 0;
        long observationSequence = 0;
        object? firstCancellation = null, beforeExpiredClaim = null, afterExpiredClaim = null;
        bool? expiredClaimReturned = null, anchorAtAssertion = null;
        string stage = "ready";
        var captures = new List<KeyboardHook.LocalDraftPress?>();
        var ups = new List<uint>();
        var inputObservations = new List<object>(4);
        int inputObservationsDropped = 0;
        KeyEventHandler down = (_, e) =>
        {
            e.Handled = true;
            if (e.Key != Key.Enter)
                return;
            var message = ComponentDispatcher.CurrentKeyboardMessage;
            uint scan = (uint)(message.lParam.ToInt64() >> 16) & 255;
            uint time = unchecked((uint)message.time);
            var press = hook.CaptureLocalDraftReplacePress(opening, confirmation,
                time, 13, scan, (message.lParam.ToInt64() & 0x01000000) != 0);
            captures.Add(press);
            ObserveInput("down", time, press);
        };
        KeyEventHandler up = (_, e) =>
        {
            e.Handled = true;
            if (e.Key == Key.Enter)
            {
                uint time = unchecked((uint)ComponentDispatcher.CurrentKeyboardMessage.time);
                ups.Add(time);
                ObserveInput("up", time, null);
            }
        };
        window.AddHandler(Keyboard.PreviewKeyDownEvent, down, true);
        window.AddHandler(Keyboard.PreviewKeyUpEvent, up, true);
        try
        {
            await Until(() => hook.ShortcutsReady && Native.GetForegroundWindow() == hwnd && replace.IsKeyboardFocused);
            hook.BeginLocalDraftAnchor(opening, hwnd, hwnd, id =>
            {
                if (Interlocked.Increment(ref cancellations) != 1)
                    return;
                // This observes the existing cancellation callback, not the
                // native foreground event that may have preceded it.
                Volatile.Write(ref firstCancellation, new
                {
                    openingMatches = id == opening,
                    state = ObserveState()
                });
            });
            hook.SetLocalDraftPrompt(opening, confirmation, replaceFocused: true);
            stage = "original-pair";
            SendPair();
            await Until(() => ups.Count == 1);
            var expired = Assert.IsType<KeyboardHook.LocalDraftPress>(captures.Single());
            Assert.True(WindowsInterruptClock.NowMs < expired.ExpiresAtMs); // Original LL down/up was timely.
            stage = "await-expiry";
            await Until(() => WindowsInterruptClock.NowMs >= expired.ExpiresAtMs);
            stage = "expired-claim";
            beforeExpiredClaim = ObserveState();
            var rejected = hook.ClaimLocalDraftReplaceTap(expired, ups[0]);
            expiredClaimReturned = rejected is not null;
            afterExpiredClaim = ObserveState();
            Assert.Null(rejected); // Delayed consumer, not a held key.
            stage = "anchor-after-expired-claim";
            bool anchorCurrent = hook.LocalDraftAnchorCurrent(opening);
            anchorAtAssertion = anchorCurrent;
            Assert.True(anchorCurrent);
            stage = "first-fresh-pair";
            SendPair(); // The very first new real action must work, not only a second retry.
            await Until(() => ups.Count == 2);
            var fresh = Assert.IsType<KeyboardHook.LocalDraftPress>(captures[1]);
            Assert.NotSame(expired, fresh);
            Assert.Equal(fresh.ReceivedAtMs + 500, fresh.ExpiresAtMs);
            stage = "fresh-claim";
            Assert.Null(hook.ClaimLocalDraftReplaceTap(expired, ups[0]));
            var claimed = Assert.IsType<KeyboardHook.LocalDraftTap>(hook.ClaimLocalDraftReplaceTap(fresh, ups[1]));
            Assert.Same(fresh, claimed.Press);
            Assert.Null(hook.ClaimLocalDraftReplaceTap(fresh, ups[1]));
            Assert.Equal(0, Volatile.Read(ref cancellations));
            Assert.False(hook.TryRead(out _));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    operation = "local-draft-replace-expiry-failed",
                    failure = error.GetType().Name,
                    stage,
                    timestampFrequency = Stopwatch.Frequency,
                    beforeExpiredClaim,
                    afterExpiredClaim,
                    expiredClaimReturned,
                    anchorAtAssertion,
                    cancelCallbacksAtFailure = Volatile.Read(ref cancellations),
                    firstCancellation = Volatile.Read(ref firstCancellation),
                    inputObservations,
                    inputObservationsDropped,
                    captures = captures.Take(2).Select(press => press is null ? null : new
                    {
                        press.Sequence,
                        press.ReceivedAtMs,
                        press.ExpiresAtMs,
                        press.NativeTime
                    }).ToArray(),
                    capturesDropped = Math.Max(0, captures.Count - 2),
                    ups = ups.Take(2).ToArray(),
                    upsDropped = Math.Max(0, ups.Count - 2),
                    stateAtFailure = ObserveState(),
                    replaceFocusedAtFailure = replace.IsKeyboardFocused
                }));
            }
            catch (Exception diagnosticError) when (diagnosticError is not OutOfMemoryException) { }
            throw;
        }
        finally
        {
            window.RemoveHandler(Keyboard.PreviewKeyDownEvent, down);
            window.RemoveHandler(Keyboard.PreviewKeyUpEvent, up);
            hook.EndLocalDraftAnchor(opening);
        }
        object? ObserveState()
        {
            try
            {
                // These separate reads are not an atomic snapshot or an
                // original WinEvent. Never use them to admit or retry input.
                long sequence = Interlocked.Increment(ref observationSequence);
                long observedAtTimestamp = Stopwatch.GetTimestamp();
                long observedAtMs = WindowsInterruptClock.NowMs;
                bool current = hook.LocalDraftAnchorCurrent(opening);
                long inputSequence = hook.EditorInputSequence;
                nint foreground = Native.GetForegroundWindow();
                return new
                {
                    observationSequence = sequence,
                    observedAtTimestamp,
                    observedAtMs,
                    anchorCurrent = current,
                    inputSequence,
                    foreground = foreground == hwnd ? "overlay" : foreground == 0 ? "none" : "other"
                };
            }
            catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        }
        void ObserveInput(string phase, uint nativeTime, KeyboardHook.LocalDraftPress? press)
        {
            if (inputObservations.Count == 4)
            {
                inputObservationsDropped++;
                return;
            }
            inputObservations.Add(new
            {
                phase,
                nativeTime,
                pressSequence = press?.Sequence,
                state = ObserveState()
            });
        }
        async Task Until(Func<bool> condition)
        {
            while (!condition())
                await Task.Delay(5, deadline.Token);
        }
        void SendPair()
        {
            Assert.Equal(hwnd, Native.GetForegroundWindow());
            Assert.Same(replace, Keyboard.FocusedElement);
            Assert.False(Native.GetAsyncKeyState(13) < 0);
            deadline.Token.ThrowIfCancellationRequested();
            uint sent = SendInput(2, [Edge(enter, false), Edge(enter, true)], Marshal.SizeOf<Input>());
            if (sent == 1)
                Assert.Equal(1u, SendInput(1, [Edge(enter, true)], Marshal.SizeOf<Input>())); // Own partial-send cleanup only.
            Assert.Equal(2u, sent);
        }
    });

    private static ushort Scan(uint key)
    {
        uint scan = MapVirtualKey(key, 4);
        Assert.InRange(scan, 1u, 255u);
        return checked((ushort)scan);
    }
    private static Input Edge(ushort scan, bool up) => new() { Type = 1, Scan = scan, Flags = up ? 10u : 8u };
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
    }
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW", ExactSpelling = true)]
    private static extern uint MapVirtualKey(uint key, uint mapType);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
