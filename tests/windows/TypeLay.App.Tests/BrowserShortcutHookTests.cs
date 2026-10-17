using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class BrowserShortcutHookTests(WpfDesktopFixture desktop)
{
    [Fact]
    public async Task NativeBrowserShortcutFreezesAfterItsOwnHookEpochsAndRefusesLaterInput()
    {
        await desktop.RunAsync(async () =>
        {
            var input = new TextBox { Text = "Original", AcceptsReturn = true };
            var window = new Window { Content = input, Width = 400, Height = 150 };
            window.Show();
            window.Activate();
            input.Focus();
            nint hwnd = new WindowInteropHelper(window).Handle;
            using var hook = new KeyboardHook(allowInjectedInput: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (!hook.ShortcutsReady || Native.GetForegroundWindow() != hwnd || !input.IsKeyboardFocused)
                await Task.Delay(10, timeout.Token);
            Assert.False(Native.GetAsyncKeyState(0x87) < 0);
            hook.SetShortcuts([new("direct", 0x87, ShortcutModifiers.None)]);
            var before = hook.BrowserEpochs;
            long originalTime = WindowsInterruptClock.NowMs;
            var source = new InputSnapshot(new("windows-browser", "document:1", Guid.NewGuid()), input.Text,
                new(0, 0), new(0, 0, 100, 20), false, FieldAccess.Supported, ImeState.Clear, originalTime, 1, 2,
                WriteSupported: true, DirectWriteSupported: true);
            object authority = new();
            hook.PublishBrowserShortcut(new(new(source, hook.ContentEpoch, hook.ForegroundEpoch), hwnd, hook.CommandGeneration, authority));
            BrowserShortcutPress? press = null;
            try
            {
                Send(0x87, false);
                Send(0x87, true);
                while (!hook.TryReadBrowserPress(out press))
                    await Task.Delay(1, timeout.Token);
                Assert.NotNull(press);
                Assert.Equal(press.Sequence, press.CommandId);
                Assert.InRange(press.CommandId, 1, 9007199254740991);
                Assert.Same(source, press.Binding.Lease.Source);
                Assert.Same(authority, press.Binding.Basis);
                Assert.Equal(before.User + 1, press.CompletionEpochs.User);
                Assert.Equal(before.Source + 1, press.CompletionEpochs.Source);
                Assert.Equal(before.Content + 1, press.CompletionEpochs.Content);
                Assert.Equal(before.Foreground, press.CompletionEpochs.Foreground);
                Assert.True(press.Current(hook.BrowserEpochs, press.TriggeredAtMs));
                Assert.False(press.Current(before, press.TriggeredAtMs));
                Assert.False(press.Current(press.CompletionEpochs, press.TriggeredAtMs + 151));
                Assert.False(press.Current(press.CompletionEpochs, press.TriggeredAtMs - 1));
                Assert.False(hook.TryRead(out _)); // No second native/legacy terminal notice.
                hook.PublishBrowserShortcut(new(new(source, hook.ContentEpoch, hook.ForegroundEpoch), hwnd, hook.CommandGeneration, new object()));
                Assert.Same(authority, press.Binding.Basis); // Normal renewal does not replace the frozen basis.
                Send(0x41, false);
                Send(0x41, true);
                while (hook.ContentEpoch == press.CompletionEpochs.Content)
                    await Task.Delay(1, timeout.Token);
                Assert.False(press.Current(hook.BrowserEpochs, press.TriggeredAtMs));
            }
            finally { Send(0x87, true); Send(0x41, true); }
        });
    }
    private static void Send(ushort key, bool up) => Assert.Equal(1u,
        SendInput(1, [new KeyInput { Type = 1, Key = key, Flags = up ? 2u : 0u }], Marshal.SizeOf<KeyInput>()));
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, KeyInput[] input, int size);
}
