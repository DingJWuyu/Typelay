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
            hook.BeginLocalDraftAnchor(opening, hwnd, hwnd, _ => { });
            ushort tab = Scan(9), otherScan = Scan(0x77); // F8 never inserts editor text.
            Input[] pair = [Edge(tab, false), Edge(tab, true)];
            if (change == "unarmed-prefix")
            {
                Send(pair);
                await Until(() => upTimes.Count == 1);
                Assert.Null(captures.Single());
                Assert.True(hook.LocalDraftAnchorCurrent(opening));
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
            var tap = press is null ? null : hook.ClaimLocalDraftTap(press, tab, false,
                change == "wrong-up-message" ? unchecked(upTimes[0] + 1) : upTimes[0]);
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
