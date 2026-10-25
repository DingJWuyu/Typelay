using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task APreControlGrantCannotAuthorizeTheConfiguredFunctionKey(bool withScanIdentity) => desktop.RunAsync(async () =>
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
            Control(false);
            await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedDown.v1") == 1, native.Token);
            // Distinguish an admitted Ctrl prefix from a missing scan identity.
            // Neither may carry the pre-Ctrl write authorization forward.
            if (withScanIdentity)
                Assert.NotNull(hook.AcceptanceModifierTransition);
            else
                Assert.Null(hook.AcceptanceModifierTransition);
            Assert.False(hook.AcceptanceReady(token, permit, WindowsInterruptClock.NowMs));
            functionDown = true;
            SourceScanKey(0x41, false);
            SourceScanKey(0x41, true);
            functionDown = false;
            Control(true);
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
                SourceScanKey(0x41, true);
            if (controlDown)
                Control(true);
            if (barrierDown)
                Key(0x86, true);
        }
        void Control(bool up)
        {
            if (withScanIdentity)
                SourceScanKey(0x1d, up);
            else
                Key(0x11, up);
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
        using var brokerTrace = new BrokerReturnTraceFile();
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(isolatedSourceAcceptance: true,
            returnTrace: brokerTrace.Path);
        await native.Prepare(native.A, "你好", new(0, 2));
        var editor = new Window { Content = new TextBox(), Width = 350, Height = 120, Title = "TypeLay controlled manual editor" };
        nint editorHwnd = new WindowInteropHelper(editor).EnsureHandle();
        using var hook = new KeyboardHook(allowInjectedInput: true, editorHwnd: editorHwnd);
        await Until(() => hook.ShortcutsReady, native.Token);
        using var broker = new FixtureBroker(native);
        using var input = new WindowsInputService(broker, editorHwnd, monitor: hook, isolatedSourceAcceptance: true);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.ControlSource." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const ushort function = 0x76, control = 0x11, barrier = 0x86;
        bool controlDown = false, functionDown = false, shiftDown = false, barrierDown = false;
        try
        {
            Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceObservedKey.v1", function));
            Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchKey.v1", control));
            Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceBarrierKey.v1", barrier));
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                AcceptShortcut = "Ctrl+F7",
                ExperimentalWriteback = true
            });
            var loop = new Loop();
            var view = new View();
            using var controller = new SessionController(settings, new Translation(), view, hook, input, loop);
            await controller.StartAsync();
            await Until(() => { loop.Advance(); return view.CanAccept; }, native.Token);
            var originalSource = Assert.IsType<SessionViewToken>(view.Token).Target;
            Assert.True(view.BeginEditing!(Assert.IsType<SessionViewToken>(view.Token)));
            editor.Show();
            editor.Activate();
            await Until(() => Native.GetForegroundWindow() == editorHwnd, native.Token);
            view.DraftChanged!("Hand edited");
            var returnTrace = new SourceReturnTrace(native.Root, native.A, editorHwnd, hook, view,
                originalSource);
            broker.ObserveResponse = returnTrace.Response;
            try
            {
                returnTrace.Observe("before-return");
                view.ReturnRequested!(Assert.IsType<SessionViewToken>(view.Token));
                await Until(() =>
                {
                    loop.Advance();
                    returnTrace.Observe("waiting");
                    return view.State == SessionState.EditedTranslationPending && view.CanAccept;
                }, native.Token);
            }
            catch (Exception error)
            {
                returnTrace.Observe("failed", new
                {
                    exceptionType = error.GetType().Name
                }, failed: true);
                Assert.Fail($"Configured Ctrl source return failed before Ctrl/revoke, action={action}: {returnTrace.Summary()}");
            }
            finally { broker.ObserveResponse = null; }
            Assert.Equal("Hand edited", view.Candidate);
            Assert.Equal(originalSource, Assert.IsType<SessionViewToken>(view.Token).Target);
            Assert.Equal(originalSource, broker.InstallationPermit!.Plan.Expected.Target);
            var oldGrant = broker.Installation!.Grant;
            var oldPermit = broker.InstallationPermit!;
            long previousUser = hook.UserEpoch;
            controlDown = true;
            SourceScanKey(0x1d, false);
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
                SourceScanKey(0x1d, true);
                controlDown = false;
                await Until(() => hook.AcceptanceModifierTransition is null, native.Token);
            }
            else if (action == "shift")
            {
                shiftDown = true;
                SourceScanKey(0x2a, false);
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
            SourceScanKey(0x41, false);
            if (action == "accept")
            {
                await Until(() => hook.StockLastActivation is not null, native.Token);
                Assert.True(hook.StockLastActivation!.Claimed);
                hook.ConfigureSessionShortcuts(SessionShortcutBindings.Restore("F8", "F9"));
                SourceScanKey(0x41, false); // The original held group remains owned.
            }
            SourceScanKey(0x41, true);
            functionDown = false;
            if (controlDown)
            {
                SourceScanKey(0x1d, true);
                controlDown = false;
            }
            if (shiftDown)
            {
                SourceScanKey(0x2a, true);
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
                SourceScanKey(0x41, true);
            if (controlDown)
                SourceScanKey(0x1d, true);
            if (shiftDown)
                SourceScanKey(0x2a, true);
            if (barrierDown)
                Key(barrier, true);
            editor.Close();
            Directory.Delete(directory, true);
        }
    });

    [Theory]
    [InlineData("fresh")]
    [InlineData("old-read")]
    [InlineData("text")]
    [InlineData("selection")]
    [InlineData("configuration")]
    [InlineData("early-function")]
    public Task OrderedControlPreservesOnlyAnUnchangedManualDraftWithoutTrustingSendInput(string change) =>
        ExerciseOrderedControlDraft(change, physical: false);

    [Fact(Explicit = true)]
    public Task ManualPhysicalControlF7UsesFreshManualDraftAndOnlyOwnsItsFunctionPress() =>
        ExerciseOrderedControlDraft("fresh", physical: true);

    private Task ExerciseOrderedControlDraft(string change, bool physical) => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        await native.Prepare(native.A, "你好", new(0, 2));
        var editor = new Window { Content = new TextBox(), Width = 350, Height = 120, Title = "TypeLay ordered manual editor" };
        nint editorHwnd = new WindowInteropHelper(editor).EnsureHandle();
        using var hook = new KeyboardHook(editorHwnd: editorHwnd); // Never opt SendInput into source trust.
        await Until(() => hook.ShortcutsReady, native.Token);
        using var broker = new FixtureBroker(native);
        using var input = new WindowsInputService(broker, editorHwnd, monitor: hook,
            sourceMessageAcceptance: true, orderedSourceMessageWitness: true);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.OrderedControl." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool controlDown = false, functionDown = false, barrierDown = false;
        SourceAcceptanceRequest? observer = null, attempted = null;
        SourceAcceptanceResult? attemptedResult = null, attemptedRetirement = null;
        broker.ObserveResponse = (request, response) =>
        {
            if (request.SourceAcceptance is not { } install)
                return;
            if (request.Op == "source-retire" && attempted?.Grant == install.Grant)
                attemptedRetirement = response.SourceAcceptance;
            if (request.Op != "source-install")
                return;
            if (install.Chord?.Mode == SourceAcceptanceMode.ObserveControlPrefix && response.SourceAcceptance?.Installed == true)
                observer = install;
            if (install.Chord is { Mode: SourceAcceptanceMode.Accept, Modifiers: ShortcutModifiers.Control })
            {
                attempted ??= install;
                if (attempted.Grant == install.Grant)
                    attemptedResult = response.SourceAcceptance;
            }
        };
        try
        {
            Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchKey.v1", 0x11));
            Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceObservedKey.v1", 0x76));
            Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceBarrierKey.v1", 0x86));
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                AcceptShortcut = "Ctrl+F7"
            });
            var loop = new Loop();
            var view = new View();
            using var controller = new SessionController(settings, new Translation(), view, hook, input, loop);
            await controller.StartAsync();
            await Until(() => { loop.Advance(); return view.Candidate == "Hello" && observer is not null; }, native.Token);
            Assert.False(view.CanAccept); // Bootstrap is observation, not a write-ready cue.
            Assert.True(view.BeginEditing!(Assert.IsType<SessionViewToken>(view.Token)));
            editor.Show();
            editor.Activate();
            await Until(() => Native.GetForegroundWindow() == editorHwnd, native.Token);
            view.DraftChanged!("Hand edited");
            Guid initialObserver = observer!.Grant;
            view.ReturnRequested!(Assert.IsType<SessionViewToken>(view.Token));
            await Until(() =>
            {
                loop.Advance();
                return view.State == SessionState.EditedTranslationPending && observer?.Grant != initialObserver;
            }, native.Token);
            Assert.False(view.CanAccept);
            var before = observer!;
            string witnessName = "Local\\TypeLay.SourceWitness."
                + Convert.ToHexString(before.Target.Lifetime.ToByteArray()).ToLowerInvariant();
            using var mapping = MemoryMappedFile.OpenExisting(witnessName, MemoryMappedFileRights.Read);
            using var witness = mapping.CreateViewAccessor(0, SourceInputWitness.Bytes, MemoryMappedFileAccess.Read);
            await Until(() => { loop.Advance(); return witness.ReadUInt32(132) == 1; }, native.Token);
            Assert.NotEqual(0UL, witness.ReadUInt64(112));
            Assert.Null(attempted);
            if (change == "old-read")
            {
                broker.HoldNextCapture = true;
                await Until(() => { loop.Advance(); return broker.CaptureReceived.Task.IsCompleted; }, native.Token);
            }
            if (physical)
            {
                Console.WriteLine("请在原生输入框按住物理 Ctrl；看到下一条就绪提示后，再按下并释放一次 F7，最后松开 Ctrl。");
                await Until(() => { loop.Advance(); return N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedDown.v1") == 1; }, native.Token);
            }
            else
            {
                controlDown = true;
                SourceScanKey(0x1d, false);
                await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedDown.v1") == 1, native.Token);
            }
            Assert.NotNull(hook.AcceptanceModifierTransition);
            Assert.False(hook.AcceptanceReady(Assert.IsType<SessionViewToken>(view.Token), before.Permit!, WindowsInterruptClock.NowMs));
            if (change == "text")
                N.SendString(native.A, N.WM_SETTEXT, "changed source");
            if (change == "selection")
                N.Send(native.A, N.EM_SETSEL, 0, 0);
            if (change == "configuration")
                settings.Update(settings.Value with
                {
                    AcceptShortcut = "Ctrl+F8"
                });
            if (change == "early-function")
            {
                functionDown = true;
                SourceScanKey(0x41, false);
                SourceScanKey(0x41, true);
                functionDown = false;
            }
            if (change == "old-read")
            {
                int renders = view.RenderCount, captures = broker.Captures;
                broker.ReleaseCapture.TrySetResult();
                await Until(() =>
                {
                    // Pump only the existing monitor retirement, not another
                    // controller Tick/capture. Quiet releases the delivery fence;
                    // the next Render proves Runtime processed this held response.
                    Assert.False(hook.TryRead(out var notice));
                    Assert.Null(notice);
                    return view.RenderCount > renders;
                }, native.Token);
                Assert.Equal(captures, broker.Captures);
                Assert.Null(attempted); // The processed old response cannot install a Ctrl grant.
                Assert.Equal("Hand edited", view.Candidate);
                Assert.False(view.CanAccept);
            }
            if (change is "fresh" or "old-read")
            {
                await Until(() => { loop.Advance(); return attempted is not null; }, native.Token);
                Assert.Equal(SessionState.EditedTranslationPending, view.State);
                Assert.Equal("Hand edited", view.Candidate);
                Assert.Equal("Hand edited", attempted!.Permit!.Plan.Replacement);
                Assert.NotEqual(before.Grant, attempted.Grant);
                Assert.NotEqual(before.Permit!.Plan.Revision, attempted.Permit.Plan.Revision);
                Assert.Equal(before.Permit.InputEpoch + 1, attempted.Permit.InputEpoch);
                Assert.Equal(attempted.Permit.ObservedAtMs + Limits.PermitMaxAgeMs, attempted.Permit.ExpiresAtMs);
                Assert.Equal(attempted.Permit.InputEpoch - 1, attempted.Chord!.Control!.BeforeInputEpoch);
                if (physical)
                {
                    await Until(() => { loop.Advance(); return view.CanAccept; }, native.Token);
                    Console.WriteLine("接受已就绪。继续按住 Ctrl，按下并释放一次物理 F7，然后松开 Ctrl。");
                    await Until(() => { loop.Advance(); return broker.Receipt is not null && broker.Retired; }, native.Token);
                    Assert.Equal(WriteStatus.Verified, broker.Receipt!.Status);
                    Assert.Equal(1, broker.CompletionTransfers);
                    Assert.Equal("Hand edited", N.ReadText(native.A, Limits.MaxTextUnits));
                }
                else
                {
                    // The real source rejects the injected original Ctrl. LL
                    // attribution may preserve text, but it grants no source authority.
                    Assert.False(attemptedResult!.Installed);
                    Assert.False(attemptedResult.Claimed);
                    Assert.Equal(WriteStatus.RejectedBeforeMutation, attemptedResult.Receipt?.Status);
                    Assert.Equal(Denial.Unsupported, attemptedResult.Receipt?.Reason);
                    await Until(() => { loop.Advance(); return attemptedRetirement?.Quiet == true; }, native.Token);
                    Assert.True(attemptedRetirement!.Acknowledged);
                    Assert.False(attemptedRetirement.Claimed);
                    Assert.False(view.CanAccept);
                }
            }
            else if (change != "early-function")
            {
                await Until(() => { loop.Advance(); return view.State != SessionState.EditedTranslationPending; }, native.Token);
                Assert.NotEqual("Hand edited", view.Candidate);
                Assert.Null(attempted);
            }
            if (!physical)
            {
                if (change != "early-function")
                {
                    functionDown = true;
                    SourceScanKey(0x41, false);
                    SourceScanKey(0x41, true);
                    functionDown = false;
                }
                SourceScanKey(0x1d, true);
                controlDown = false;
                barrierDown = true;
                SourceScanKey(0x6e, false);
                SourceScanKey(0x6e, true);
                barrierDown = false;
                await Until(() => { loop.Advance(); return N.GetProp(native.Root, "TypeLay.Test.SourceBarrierUp.v1") == 1; }, native.Token);
                Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceObservedDown.v1"));
                Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceObservedUp.v1"));
                Assert.Equal(change == "text" ? "changed source" : "你好", N.ReadText(native.A, Limits.MaxTextUnits));
                Assert.Equal(0, broker.CompletionTransfers);
                Assert.Null(broker.Receipt);
            }
            Assert.Null(hook.StockLastActivation);
            Assert.Equal(0, broker.Writes);
            Assert.False(settings.Value.ExperimentalWriteback);
            native.AssertZeroSubmits();
        }
        finally
        {
            broker.ReleaseCapture.TrySetResult();
            if (functionDown)
                SourceScanKey(0x41, true);
            if (controlDown)
                SourceScanKey(0x1d, true);
            if (barrierDown)
                SourceScanKey(0x6e, true);
            editor.Close();
            Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task AReceivedForeignForegroundCannotBeErasedByReturningBeforeTheAcknowledgement() => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(isolatedSourceAcceptance: true);
        await native.Prepare(native.A, "你好", new(0, 2));
        var editor = new Window { Content = new TextBox(), Width = 350, Height = 120, Title = "TypeLay controlled return editor" };
        var foreign = new Window { Content = new TextBox(), Width = 350, Height = 120, Title = "TypeLay controlled other window" };
        nint editorHwnd = new WindowInteropHelper(editor).EnsureHandle();
        nint foreignHwnd = new WindowInteropHelper(foreign).EnsureHandle();
        var hook = new KeyboardHook(allowInjectedInput: true, editorHwnd: editorHwnd);
        using var broker = new FixtureBroker(native) { HoldReturnAcknowledgement = true };
        var input = new WindowsInputService(broker, editorHwnd, monitor: hook, isolatedSourceAcceptance: true);
        var browser = new BrowserInput(new BrowserServer(editor.Dispatcher), hook);
        using var router = new WindowsInputRouter(input, hook, browser);
        Task<InputReturn>? pending = null;
        try
        {
            await Until(() => hook.ShortcutsReady, native.Token);
            var configuration = new SessionShortcutConfiguration(hook.CommandGeneration, SessionShortcutBindings.Default);
            var context = new InputContext(hook.UserEpoch, hook.ForegroundEpoch, hook.SourceUserEpoch, null, [], false, configuration);
            var source = Assert.IsType<InputSnapshot>((await router.CaptureAsync(context)).Snapshot);
            editor.Show();
            editor.Activate();
            await Until(() => Native.GetForegroundWindow() == editorHwnd, native.Token);
            pending = router.ReturnToSourceAsync(context with
            {
                BoundTarget = source.Target,
                UserEpoch = hook.UserEpoch,
                SourceUserEpoch = hook.SourceUserEpoch,
                ForegroundEpoch = hook.ForegroundEpoch
            });
            Assert.Equal("focused", (await broker.ReturnReceived.Task.WaitAsync(native.Token)).Status);
            // No keys, field writes, retries or new focus budget. The actual
            // return has completed; only its existing acknowledgement is held.
            foreign.Show();
            foreign.Activate();
            await Until(() => Native.GetForegroundWindow() == foreignHwnd, native.Token);
            editor.Activate();
            await Until(() => Native.GetForegroundWindow() == editorHwnd, native.Token);
            await Until(() => !router.CanRetainDraftDuringSourceReturn(source.Target), native.Token);
            Assert.Equal(FocusMember.Overlay, hook.GetFocus(source.Target));
            broker.ReleaseReturn.TrySetResult();
            var result = await pending;
            Assert.False(result.Returned);
            Assert.True(result.ContextLost);
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(0, broker.Writes);
            native.AssertZeroSubmits();
        }
        finally
        {
            broker.ReleaseReturn.TrySetResult();
            if (pending is not null)
            {
                try
                {
                    await pending;
                }
                catch { /* Own the continuation without replacing the scenario failure. */ }
            }
            foreign.Close();
            editor.Close();
        }
    });

    // Preserve physical press identity through the production hook. VK-only
    // injection supplies no scan identity; Ctrl provenance requires one.
    private static void SourceScanKey(ushort scan, bool up) => Assert.Equal(1u,
        SendInput(1, [new KeyInput { Type = 1, Scan = scan, Flags = up ? 10u : 8u }], Marshal.SizeOf<KeyInput>()));
}
