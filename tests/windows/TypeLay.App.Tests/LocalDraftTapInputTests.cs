using System.Diagnostics;
using System.Runtime.InteropServices;
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
        window.Show();
        Assert.True(window.Activate());
        Assert.True(editor.Focus());
        nint hwnd = new WindowInteropHelper(window).Handle;
        using var hook = new KeyboardHook(allowInjectedInput: true, editorHwnd: hwnd);
        hook.ConfigureShortcuts(false, "Ctrl+Shift+F9", "Ctrl+Shift+F10", SessionShortcutBindings.Default, true);
        Guid opening = Guid.NewGuid();
        var held = new HashSet<ushort>();
        var captures = new List<KeyboardHook.LocalDraftPress?>();
        var downTimes = new List<uint>();
        var upTimes = new List<uint>();
        int cancellations = 0;
        object? firstCancellation = null;
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
            await Until(() => hook.ShortcutsReady && Native.GetForegroundWindow() == hwnd && editor.IsKeyboardFocused);
            Assert.All(new[] { 9, 0x77, 0x10, 0x11, 0x12, 0x5b, 0x5c }, key => Assert.False(Native.GetAsyncKeyState(key) < 0));
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
        var captures = new List<KeyboardHook.LocalDraftPress?>();
        var ups = new List<uint>();
        KeyEventHandler down = (_, e) =>
        {
            e.Handled = true;
            if (e.Key != Key.Enter)
                return;
            var message = ComponentDispatcher.CurrentKeyboardMessage;
            uint scan = (uint)(message.lParam.ToInt64() >> 16) & 255;
            captures.Add(hook.CaptureLocalDraftReplacePress(opening, confirmation,
                unchecked((uint)message.time), 13, scan, (message.lParam.ToInt64() & 0x01000000) != 0));
        };
        KeyEventHandler up = (_, e) =>
        {
            e.Handled = true;
            if (e.Key == Key.Enter)
                ups.Add(unchecked((uint)ComponentDispatcher.CurrentKeyboardMessage.time));
        };
        window.AddHandler(Keyboard.PreviewKeyDownEvent, down, true);
        window.AddHandler(Keyboard.PreviewKeyUpEvent, up, true);
        try
        {
            await Until(() => hook.ShortcutsReady && Native.GetForegroundWindow() == hwnd && replace.IsKeyboardFocused);
            hook.BeginLocalDraftAnchor(opening, hwnd, hwnd, _ => Interlocked.Increment(ref cancellations));
            hook.SetLocalDraftPrompt(opening, confirmation, replaceFocused: true);
            SendPair();
            await Until(() => ups.Count == 1);
            var expired = Assert.IsType<KeyboardHook.LocalDraftPress>(captures.Single());
            Assert.True(WindowsInterruptClock.NowMs < expired.ExpiresAtMs); // Original LL down/up was timely.
            await Until(() => WindowsInterruptClock.NowMs >= expired.ExpiresAtMs);
            Assert.Null(hook.ClaimLocalDraftReplaceTap(expired, ups[0])); // Delayed consumer, not a held key.
            Assert.True(hook.LocalDraftAnchorCurrent(opening));
            SendPair(); // The very first new real action must work, not only a second retry.
            await Until(() => ups.Count == 2);
            var fresh = Assert.IsType<KeyboardHook.LocalDraftPress>(captures[1]);
            Assert.NotSame(expired, fresh);
            Assert.Equal(fresh.ReceivedAtMs + 500, fresh.ExpiresAtMs);
            Assert.Null(hook.ClaimLocalDraftReplaceTap(expired, ups[0]));
            var claimed = Assert.IsType<KeyboardHook.LocalDraftTap>(hook.ClaimLocalDraftReplaceTap(fresh, ups[1]));
            Assert.Same(fresh, claimed.Press);
            Assert.Null(hook.ClaimLocalDraftReplaceTap(fresh, ups[1]));
            Assert.Equal(0, Volatile.Read(ref cancellations));
            Assert.False(hook.TryRead(out _));
        }
        finally
        {
            window.RemoveHandler(Keyboard.PreviewKeyDownEvent, down);
            window.RemoveHandler(Keyboard.PreviewKeyUpEvent, up);
            hook.EndLocalDraftAnchor(opening);
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
