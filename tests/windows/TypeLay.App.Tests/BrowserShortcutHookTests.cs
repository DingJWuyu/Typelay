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
    public async Task PageOwnershipPassesNewPressButNativeOwnedPressKeepsItsRepeatAndRelease()
    {
        await desktop.RunAsync(async () =>
        {
            var input = new TextBox { Text = "Original" };
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
            int downs = 0, ups = 0;
            input.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.F24) downs++; };
            input.PreviewKeyUp += (_, e) => { if (e.Key == System.Windows.Input.Key.F24) ups++; };
            hook.SetShortcuts([new("direct", 0x87, ShortcutModifiers.None)]);
            Guid generation = hook.CommandGeneration;
            try
            {
                Send(0x87, false);
                InputNotice? original = null;
                while (!hook.TryRead(out original))
                    await Task.Delay(1, timeout.Token);
                Assert.Equal("direct", original!.Kind);
                Assert.True(original.CommandSequence > 0);
                Assert.Null(original.Source); // A matched native command still cannot invent a source.
                hook.DeferCommandRegistration();
                Assert.False(hook.CommandsReleased);
                Send(0x87, false);
                Send(0x87, true);
                while (!hook.CommandsReleased)
                    await Task.Delay(1, timeout.Token);
                Assert.Equal(0, downs);
                Assert.Equal(0, ups);
                Assert.False(hook.TryRead(out _));
                Assert.Equal(generation, hook.CommandGeneration);
                Assert.True(hook.NextCommandSequence() > original.CommandSequence);
                Send(0x87, false);
                Send(0x87, true);
                while (ups == 0)
                    await Task.Delay(1, timeout.Token);
                Assert.Equal(1, downs);
                Assert.Equal(1, ups);
                Assert.False(hook.TryRead(out _));
            }
            finally { Send(0x87, true); window.Close(); }
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
