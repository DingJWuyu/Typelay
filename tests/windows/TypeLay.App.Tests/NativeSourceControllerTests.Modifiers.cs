using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceControllerTests
{
    [Fact]
    public Task APreControlGrantCannotAuthorizeTheConfiguredFunctionKey() => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(isolatedSourceAcceptance: true);
        await native.Prepare(native.A, "你好", new(0, 2));
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.ShortcutsReady, native.Token);
        var bindings = SessionShortcutBindings.Restore("Ctrl+F7", "Esc");
        hook.ConfigureSessionShortcuts(bindings);
        using var broker = new FixtureBroker(native);
        using var input = new WindowsInputService(broker, 0, monitor: hook, isolatedSourceAcceptance: true);
        var capture = await input.CaptureAsync(new(hook.UserEpoch, hook.ForegroundEpoch, hook.SourceUserEpoch,
            null, [], false, new(hook.CommandGeneration, bindings)));
        var source = Assert.IsType<InputSnapshot>(capture.Snapshot);
        var permit = Publish(hook, source, 7);
        var token = new SessionViewToken(source.Target, hook.CommandGeneration, 7);
        await Until(() => { hook.TryRead(out _); return hook.AcceptanceReady(token, permit, WindowsInterruptClock.NowMs); }, native.Token);
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceObservedKey.v1", 0x76));
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchKey.v1", 0x11));
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceBarrierKey.v1", 0x86));
        bool controlDown = false, functionDown = false, barrierDown = false;
        try
        {
            controlDown = true;
            Key(0x11, false);
            await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedDown.v1") == 1, native.Token);
            Assert.False(hook.AcceptanceReady(token, permit, WindowsInterruptClock.NowMs));
            functionDown = true;
            Key(0x76, false);
            Key(0x76, true);
            functionDown = false;
            Key(0x11, true);
            controlDown = false;
            barrierDown = true;
            Key(0x86, false);
            Key(0x86, true);
            barrierDown = false;
            await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceBarrierUp.v1") == 1, native.Token);
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceObservedDown.v1"));
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceObservedUp.v1"));
            Assert.Null(hook.StockLastActivation);
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(0, broker.Writes);
            native.AssertZeroSubmits();
        }
        finally
        {
            if (functionDown)
                Key(0x76, true);
            if (controlDown)
                Key(0x11, true);
            if (barrierDown)
                Key(0x86, true);
        }
    });

    [Theory]
    [InlineData("accept")]
    [InlineData("release")]
    [InlineData("shift")]
    [InlineData("revoke")]
    [InlineData("binding")]
    [InlineData("content")]
    public Task ConfiguredControlUsesFreshSourceAndPreservesReturnedManualWording(string action) => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(isolatedSourceAcceptance: true);
        await native.Prepare(native.A, "你好", new(0, 2));
        var editor = new Window { Content = new TextBox(), Width = 350, Height = 120, Title = "TypeLay controlled manual editor" };
        nint editorHwnd = new WindowInteropHelper(editor).EnsureHandle();
        using var hook = new KeyboardHook(allowInjectedInput: true, editorHwnd: editorHwnd);
        await Until(() => hook.ShortcutsReady, native.Token);
        using var broker = new FixtureBroker(native);
        using var input = new WindowsInputService(broker, editorHwnd, monitor: hook, isolatedSourceAcceptance: true);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.ControlSource." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const ushort function = 0x76, control = 0x11, shift = 0x10, barrier = 0x86;
        bool controlDown = false, functionDown = false, shiftDown = false, barrierDown = false;
        try
        {
            Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceObservedKey.v1", function));
            Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchKey.v1", control));
            Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceBarrierKey.v1", barrier));
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                AcceptShortcut = "Ctrl+F7"
            });
            var loop = new Loop();
            var view = new View();
            using var controller = new SessionController(settings, new Translation(), view, hook, input, loop);
            await controller.StartAsync();
            await Until(() => { loop.Advance(); return view.CanAccept; }, native.Token);
            Assert.True(view.BeginEditing!(Assert.IsType<SessionViewToken>(view.Token)));
            editor.Show();
            editor.Activate();
            await Until(() => Native.GetForegroundWindow() == editorHwnd, native.Token);
            view.DraftChanged!("Hand edited");
            view.ReturnRequested!(Assert.IsType<SessionViewToken>(view.Token));
            await Until(() => { loop.Advance(); return view.State == SessionState.EditedTranslationPending && view.CanAccept; }, native.Token);
            Assert.Equal("Hand edited", view.Candidate);
            var oldGrant = broker.Installation!.Grant;
            var oldPermit = broker.InstallationPermit!;
            long previousUser = hook.UserEpoch;
            controlDown = true;
            Key(control, false);
            await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedDown.v1") == 1, native.Token);
            Assert.Equal(previousUser + 1, hook.UserEpoch);
            Assert.NotNull(hook.AcceptanceModifierTransition);
            // The real source has handled Ctrl. Only a subsequent real capture
            // and newly installed grant may authorize the configured Fn.
            await Until(() =>
            {
                loop.Advance();
                return broker.Installation?.Grant != oldGrant && view.CanAccept;
            }, native.Token);
            Assert.Equal(SessionState.EditedTranslationPending, view.State);
            Assert.Equal("Hand edited", view.Candidate);
            Assert.Equal(oldPermit.InputEpoch + 1, broker.InstallationPermit!.InputEpoch);
            Assert.NotEqual(oldPermit.Plan.Revision, broker.InstallationPermit.Plan.Revision);
            if (action == "release")
            {
                Key(control, true);
                controlDown = false;
                await Until(() => hook.AcceptanceModifierTransition is null, native.Token);
            }
            else if (action == "shift")
            {
                shiftDown = true;
                Key(shift, false);
                await Until(() => hook.AcceptanceModifierTransition is null, native.Token);
            }
            else if (action == "revoke")
            {
                var grant = broker.Installation.Grant;
                hook.Revoke();
                Assert.Null(hook.AcceptanceModifierTransition);
                // A fresh grant does not resurrect the retired Ctrl press group.
                await Until(() => { loop.Advance(); return broker.Installation?.Grant != grant && view.CanAccept; }, native.Token);
            }
            else if (action == "binding")
            {
                hook.ConfigureSessionShortcuts(SessionShortcutBindings.Restore("Ctrl+F8", "Esc"));
                Assert.Null(hook.AcceptanceModifierTransition);
            }
            else if (action == "content")
            {
                barrierDown = true;
                Key(barrier, false);
                Key(barrier, true);
                barrierDown = false;
                await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceBarrierUp.v1") == 1, native.Token);
                Assert.Null(hook.AcceptanceModifierTransition);
            }
            functionDown = true;
            Key(function, false);
            if (action == "accept")
            {
                await Until(() => hook.StockLastActivation is not null, native.Token);
                Assert.True(hook.StockLastActivation!.Claimed);
                hook.ConfigureSessionShortcuts(SessionShortcutBindings.Restore("F8", "F9"));
                Key(function, false); // The original held group remains owned.
            }
            Key(function, true);
            functionDown = false;
            if (controlDown)
            {
                Key(control, true);
                controlDown = false;
            }
            if (shiftDown)
            {
                Key(shift, true);
                shiftDown = false;
            }
            if (action == "accept")
            {
                await Until(() => { loop.Advance(); return broker.Receipt is not null && view.State == SessionState.NoSession; }, native.Token);
                Assert.Equal(WriteStatus.Verified, broker.Receipt!.Status);
                Assert.Equal("Hand edited", N.ReadText(native.A, Limits.MaxTextUnits));
            }
            barrierDown = true;
            Key(barrier, false);
            Key(barrier, true);
            barrierDown = false;
            nint expectedBarriers = action == "content" ? 2 : 1;
            await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceBarrierUp.v1") == expectedBarriers, native.Token);
            nint expected = action == "accept" ? 0 : 1;
            Assert.Equal(expected, N.GetProp(native.Root, "TypeLay.Test.SourceObservedDown.v1"));
            Assert.Equal(expected, N.GetProp(native.Root, "TypeLay.Test.SourceObservedUp.v1"));
            if (action != "accept")
            {
                Assert.Null(hook.StockLastActivation);
                Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            }
            Assert.False(hook.StockPressOutstanding);
            Assert.Equal(0, broker.Writes);
            native.AssertZeroSubmits();
        }
        finally
        {
            if (functionDown)
                Key(function, true);
            if (controlDown)
                Key(control, true);
            if (shiftDown)
                Key(shift, true);
            if (barrierDown)
                Key(barrier, true);
            editor.Close();
            Directory.Delete(directory, true);
        }
    });
}
