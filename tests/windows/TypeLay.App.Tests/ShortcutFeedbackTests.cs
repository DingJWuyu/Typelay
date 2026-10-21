using TypeLay.Windows;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using TypeLay.Platform.Windows;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class ShortcutFeedbackTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData("两个快捷键已启用；操作不会切换工作模式。", true)]
    [InlineData("两个快捷键已启用；操作不会切换工作模式。 当前输入框的直接替换快捷键已就绪。", true)]
    [InlineData("两个快捷键已启用；操作不会切换工作模式。 直接替换快捷键尚未就绪，请保持受支持输入框的焦点并等待观察完成。", true)]
    [InlineData("两个快捷键已启用；操作不会切换工作模式。 unknown", false)]
    [InlineData("两个快捷键已启用；操作不会切换工作模式。 当前输入框的直接替换快捷键已就绪。 unknown", false)]
    [InlineData("两个快捷键已启用；操作不会切换工作模式。 直接替换快捷键尚未就绪，请保持受支持输入框的焦点并等待观察完成。 unknown", false)]
    [InlineData("快捷键输入观察未就绪，当前不会执行按需命令。", false)]
    public void RegistrationConsumersAcceptOnlyDefinedPublicStatuses(string status, bool registered) =>
        Assert.Equal(registered, KeyboardHook.IsRegisteredShortcutStatus(status));

    [Fact]
    public Task RegisteredShortcutsPresentReadinessOnlyAfterCurrentPublication() => desktop.RunAsync(async () =>
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
        string settingsDirectory = Path.Combine(Path.GetTempPath(), "typelay-shortcut-status-" + Guid.NewGuid().ToString("N"));
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
            // Registration and a capable capture do not mean Runtime has published
            // a current shortcut basis. Observe the same public status as settings
            // and the tray, without refreshing the lease or pressing a second time.
            hook.ConfigureShortcuts(true, "Ctrl+Shift+F23", "Ctrl+Shift+F24",
                SessionShortcutBindings.Default, true);
            Assert.Contains("两个快捷键已启用", hook.ShortcutStatus);
            const string ready = "当前输入框的直接替换快捷键已就绪。";
            var settings = WindowsRuntime.CreateSettings(Path.Combine(settingsDirectory, "settings.json"));
            settings.Update(settings.Value with
            {
                Mode = WorkMode.OnDemand
            });
            using var model = WindowsRuntime.CreateModel(); // Presentation only; no model starts or downloads.
            var window = new SettingsWindow(settings, model, () => { }, new WindowsApplicationSupport())
            {
                ShortcutStatus = () => hook.ShortcutStatus
            };
            window.ShowPage("shortcuts");
            window.UpdateLayout();
            Assert.True(N.PostMessage((nint)host.RootHwnd, N.FixtureFocus, (nuint)field, 0));
            await Until(() => N.Focused() == ((nint)host.RootHwnd, field), deadline.Token);
            void ExpectReady(bool expected)
            {
                string status = hook.ShortcutStatus;
                Assert.Equal(expected, status.Contains(ready, StringComparison.Ordinal));
                window.RefreshShortcutStatus();
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == status);
            }
            ExpectReady(false);
            var readySource = new InputSnapshot(new WindowsTarget(fixture.Id, fixture.StartTime.ToUniversalTime().Ticks,
                host.RootHwnd, (long)field, "controlled-hook", "field-a", Guid.NewGuid()), "private source",
                new(14, 14), new(10, 20, 300, 40), false, FieldAccess.Supported, ImeState.Clear,
                WindowsInterruptClock.NowMs, 1, 1, DirectWriteSupported: true);
            void PublishStatus(InputSnapshot snapshot, long? contentEpoch = null, long? foregroundEpoch = null) =>
                hook.PublishShortcut(new(snapshot, contentEpoch ?? hook.ContentEpoch, foregroundEpoch ?? hook.ForegroundEpoch));
            InputSnapshot FreshStatus() => readySource with
            {
                ObservedAtMs = WindowsInterruptClock.NowMs
            };
            PublishStatus(FreshStatus());
            ExpectReady(true);
            Assert.DoesNotContain(readySource.Text, hook.ShortcutStatus);
            PublishStatus(FreshStatus() with
            {
                ObservedAtMs = WindowsInterruptClock.NowMs - Limits.ObservationMaxAgeMs - 1
            });
            ExpectReady(false);
            PublishStatus(FreshStatus() with
            {
                ObservedAtMs = WindowsInterruptClock.NowMs + 10_000
            });
            ExpectReady(false);
            PublishStatus(FreshStatus() with
            {
                Ime = ImeState.Unknown
            });
            ExpectReady(false);
            PublishStatus(FreshStatus() with
            {
                DirectWriteSupported = false
            });
            ExpectReady(false);
            PublishStatus(FreshStatus() with
            {
                Access = FieldAccess.Unsupported
            });
            ExpectReady(false);
            PublishStatus(FreshStatus(), hook.ContentEpoch + 1);
            ExpectReady(false);
            PublishStatus(FreshStatus(), foregroundEpoch: hook.ForegroundEpoch + 1);
            ExpectReady(false);
            PublishStatus(FreshStatus() with
            {
                Target = new WindowsTarget(fixture.Id,
                fixture.StartTime.ToUniversalTime().Ticks, 0, (long)field, "controlled-hook", "other", Guid.NewGuid())
            });
            ExpectReady(false);
            window.Activate();
            await Until(() => Native.GetForegroundWindow() == new WindowInteropHelper(window).Handle, deadline.Token);
            PublishStatus(FreshStatus());
            ExpectReady(false);
            Assert.True(N.PostMessage((nint)host.RootHwnd, N.FixtureFocus, (nuint)field, 0));
            await Until(() => N.Focused() == ((nint)host.RootHwnd, field), deadline.Token);
            PublishStatus(FreshStatus());
            ExpectReady(true);
            hook.ConfigureShortcuts(true, "Ctrl+Shift+F22", "Ctrl+Shift+F24",
                SessionShortcutBindings.Default, true);
            ExpectReady(false);
            PublishStatus(FreshStatus());
            ExpectReady(true);
            hook.Revoke();
            ExpectReady(false);
            hook.ConfigureShortcuts(false, "Ctrl+Shift+F22", "Ctrl+Shift+F24",
                SessionShortcutBindings.Default, false);
            PublishStatus(FreshStatus());
            ExpectReady(false);
            Assert.Equal("新增快捷键仅在启用 TypeLay 的按需模式下生效。", hook.ShortcutStatus);
            window.PrepareForEmergencyShutdown();
            window.Close();
            Assert.Equal("Do not change this text", N.ReadText(field, Limits.MaxTextUnits));
        }
        finally
        {
            if (!fixture.HasExited)
            {
                fixture.Kill(true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await fixture.WaitForExitAsync(cleanup.Token);
            }
            if (Directory.Exists(settingsDirectory))
                Directory.Delete(settingsDirectory, true);
        }
    });

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
            Assert.Null(refusal.Application);
            Assert.Equal(hook.CommandGeneration, refusal.CommandGeneration);
            Assert.True(refusal.CommandSequence > 0);
            var source = new InputSnapshot(new WindowsTarget(fixture.Id, fixture.StartTime.ToUniversalTime().Ticks,
                host.RootHwnd, (long)field, "controlled-hook", "field-a", Guid.NewGuid()), "", new(0, 0),
                new(10, 20, 300, 40), false, FieldAccess.Supported, ImeState.Unknown, WindowsInterruptClock.NowMs,
                1, 1, PreviewText: "Do not change this text");
            hook.SetShortcuts([
                new("wake", 0x86, ShortcutModifiers.Control | ShortcutModifiers.Shift),
                new("direct", 0x87, ShortcutModifiers.Control | ShortcutModifiers.Shift)
            ]);
            hook.PublishShortcut(new(source, hook.ContentEpoch, hook.ForegroundEpoch, "verified-editor"));
            Keys(0x11, 0x10, 0x87);
            InputNotice? imeRefusal = null;
            await Until(() => imeRefusal is not null || hook.TryRead(out imeRefusal), deadline.Token);
            Assert.Equal(Denial.ImeUnclear, imeRefusal!.Rejection);
            Assert.Null(imeRefusal.Source);
            Assert.Null(imeRefusal.Application);
            Assert.True(imeRefusal.CommandSequence > refusal.CommandSequence);
            hook.PublishShortcut(new(source with
            {
                ObservedAtMs = WindowsInterruptClock.NowMs
            }, hook.ContentEpoch, hook.ForegroundEpoch, "verified-editor"));
            Keys(0x11, 0x10, 0x86);
            InputNotice? preview = null;
            await Until(() => preview is not null || hook.TryRead(out preview), deadline.Token);
            Assert.Equal("wake", preview!.Kind);
            Assert.Equal(Denial.None, preview.Rejection);
            Assert.Equal(ImeState.Unknown, preview.Source!.Ime);
            Assert.False(preview.Source.WriteSupported);
            Assert.Equal("verified-editor", preview.Application);
            hook.PublishShortcut(new(source with
            {
                Text = "Do not change this text",
                Ime = ImeState.Clear,
                ObservedAtMs = WindowsInterruptClock.NowMs
            }, hook.ContentEpoch, hook.ForegroundEpoch, "captured-editor"));
            Keys(0x11, 0x10, 0x87);
            InputNotice? direct = null;
            await Until(() => direct is not null || hook.TryRead(out direct), deadline.Token);
            Assert.Equal("direct", direct!.Kind);
            Assert.Equal(Denial.None, direct.Rejection);
            Assert.Equal("captured-editor", direct.Application);
            hook.PublishShortcut(new(source with
            {
                Ime = ImeState.Clear,
                ObservedAtMs = WindowsInterruptClock.NowMs
            }, hook.ContentEpoch - 1, hook.ForegroundEpoch));
            Keys(0x11, 0x10, 0x86);
            InputNotice? freshWake = null;
            await Until(() => freshWake is not null || hook.TryRead(out freshWake), deadline.Token);
            Assert.Equal("wake", freshWake!.Kind);
            Assert.Equal(Denial.None, freshWake.Rejection);
            Assert.Null(freshWake.Source);
            Assert.Null(freshWake.Permit);
            Assert.Null(freshWake.CommandCapture);
            Assert.Null(freshWake.Application);
            var intent = Assert.IsType<WakeCaptureIntent>(freshWake.WakeCapture);
            Assert.Equal(source.Target, intent.Target);
            Assert.Equal(freshWake.AcceptedAtMs, intent.TriggeredAtMs);
            Assert.Equal(hook.ContentEpoch, intent.ContentEpoch);
            Assert.Equal(hook.UserEpoch, intent.UserEpoch);
            Assert.Equal(hook.SourceUserEpoch, intent.SourceUserEpoch);
            Assert.Equal(hook.ForegroundEpoch, intent.ForegroundEpoch);
            Assert.Equal(hook.WindowEpoch, intent.WindowEpoch);
            hook.PublishShortcut(new(source with
            {
                Ime = ImeState.Clear,
                ObservedAtMs = WindowsInterruptClock.NowMs
            }, hook.ContentEpoch - 1, hook.ForegroundEpoch));
            Keys(0x11, 0x10, 0x87);
            InputNotice? changedDirect = null;
            await Until(() => changedDirect is not null || hook.TryRead(out changedDirect), deadline.Token);
            Assert.Equal(Denial.EpochChanged, changedDirect!.Rejection);
            Assert.Null(changedDirect.Source);
            Assert.Null(changedDirect.WakeCapture);
            hook.PublishShortcut(new(source with
            {
                ObservedAtMs = WindowsInterruptClock.NowMs - Limits.ObservationMaxAgeMs - 1
            }, hook.ContentEpoch - 1, hook.ForegroundEpoch));
            Keys(0x11, 0x10, 0x86);
            InputNotice? staleWake = null;
            await Until(() => staleWake is not null || hook.TryRead(out staleWake), deadline.Token);
            Assert.Equal(Denial.StaleObservation, staleWake!.Rejection);
            Assert.Null(staleWake.Source);
            Assert.Null(staleWake.WakeCapture);
            hook.PublishShortcut(new(source with
            {
                ObservedAtMs = WindowsInterruptClock.NowMs
            }, hook.ContentEpoch + 1, hook.ForegroundEpoch));
            Keys(0x11, 0x10, 0x86);
            InputNotice? futureEpoch = null;
            await Until(() => futureEpoch is not null || hook.TryRead(out futureEpoch), deadline.Token);
            Assert.Equal(Denial.EpochChanged, futureEpoch!.Rejection);
            Assert.Null(futureEpoch.Source);
            Assert.Null(futureEpoch.WakeCapture);
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

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
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
