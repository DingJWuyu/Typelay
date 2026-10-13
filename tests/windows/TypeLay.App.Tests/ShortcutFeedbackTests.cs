using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Platform.Windows;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class ShortcutFeedbackTests
{
    [Fact]
    public async Task ExplicitShortcutWithoutSafeObservationReportsRefusalAndLeavesSourceUntouched()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeLay.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root.FullName,
            "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"));
        start.ArgumentList.Add("--stock-controls");
        using var fixture = Process.Start(start)!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var line = await fixture.StandardOutput.ReadLineAsync(deadline.Token);
            var host = JsonSerializer.Deserialize<FixtureDescriptor>(line ?? "null");
            Assert.NotNull(host);
            nint field = (nint)host.Fields.Single(value => value.Name == "a").Hwnd;
            N.SendString(field, N.WM_SETTEXT, "Do not change this text");
            Assert.True(N.PostMessage((nint)host.RootHwnd, N.FixtureFocus, (nuint)field, 0));
            await Until(() => N.Focused() == ((nint)host.RootHwnd, field), deadline.Token);
            using var hook = new KeyboardHook(allowInjectedInput: true);
            await Until(() => hook.ShortcutsReady, deadline.Token);
            // This test enters the real low-level hook with explicit injected-input
            // consent. Registration conflicts have their own OS-level regression.
            hook.SetShortcuts([new("direct", 0x87, ShortcutModifiers.Control | ShortcutModifiers.Shift)]);
            Keys(0x11, 0x10, 0x87);
            InputNotice? refusal = null;
            await Until(() => refusal is not null || hook.TryRead(out refusal), deadline.Token);
            Assert.NotNull(refusal);
            Assert.Equal("direct", refusal.Kind);
            Assert.NotEqual(Denial.None, refusal.Rejection);
            Assert.Null(refusal.Source);
            Assert.Equal(hook.CommandGeneration, refusal.CommandGeneration);
            Assert.True(refusal.CommandSequence > 0);
            Assert.Equal("Do not change this text", N.ReadText(field, Limits.MaxTextUnits));
            Assert.Equal(((nint)host.RootHwnd, field), N.Focused());
            Assert.Equal(0, (long)N.Send((nint)host.RootHwnd, N.FixtureSubmitCount));
        }
        finally
        {
            if (!fixture.HasExited)
            {
                fixture.Kill(true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await fixture.WaitForExitAsync(cleanup.Token);
            }
        }
    }

    private static async Task Until(Func<bool> ready, CancellationToken cancellation)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(10, cancellation);
        Assert.True(ready(), "The configured shortcut must produce an explicit refusal instead of disappearing.");
    }

    private static void Keys(params ushort[] keys)
    {
        var input = keys.Select(key => new KeyboardInput { Type = 1, Key = key })
            .Concat(keys.Reverse().Select(key => new KeyboardInput { Type = 1, Key = key, Flags = 2 })).ToArray();
        Assert.Equal((uint)input.Length, SendInput((uint)input.Length, input, Marshal.SizeOf<KeyboardInput>()));
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
