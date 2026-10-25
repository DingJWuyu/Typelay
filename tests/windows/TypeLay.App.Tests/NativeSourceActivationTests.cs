extern alias BrokerProduction;

using System.Runtime.InteropServices;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;
using NativeTransactions = BrokerProduction::TypeLay.Broker.NativeEditTransactions;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Actual stock Edit, production DLL and LL callback. SendInput uses only the
// existing explicit automation constructor opt-in; it is NOT physical-key proof.
[Collection("Desktop")]
public sealed class NativeSourceActivationTests(WpfDesktopFixture ui)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedTabPassesRepeatAndReleaseToAnotherApplication(bool nativeActivation)
    {
        await using var receiver = await ProductionForegroundWriteTests.Desktop.Start();
        await NavigateAndPrepare(receiver, receiver.A, "Other application", new(0, 0));
        // Navigate and prepare each fixture before the next same-position window
        // can cover its field. Later cross-application navigation stays explicit.
        await using var source = await ProductionForegroundWriteTests.Desktop.Start();
        await NavigateAndPrepare(source, source.A, "pre old post", new(0, 0));
        Assert.NotEqual(source.FixtureProcessId, receiver.FixtureProcessId);
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.Ready, source.Token);
        Assert.All(new[] { 9, 0x10, 0x11, 0x12, 0x5B, 0x5C }, key => Assert.False(Native.GetAsyncKeyState(key) < 0));
        var observed = await source.Capture();
        using var native = new NativeTransactions();
        var permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
        Guid generation = Guid.NewGuid();
        using var activation = nativeActivation ? Install(native, generation, permit) : null;
        if (activation is not null)
            Assert.True(hook.InstallStockEditActivation(Lease(hook, permit), activation));
        else
            hook.Publish(Lease(hook, permit), permit);
        bool pressed = false;
        try
        {
            SendTab(false);
            pressed = true;
            InputNotice? accepted = null;
            await Until(() => accepted is not null || hook.TryRead(out accepted), source.Token);
            Assert.Equal("accept", accepted!.Kind);
            Assert.Equal(permit, accepted.Permit);
            if (nativeActivation)
                Assert.Equal(WriteStatus.Verified, accepted.CompletedReceipt!.Status);

            // The user changes apps after acceptance while the same Tab is held.
            // B has no TypeLay source grant and receives ordinary Windows input.
            receiver.RequestForeground(receiver.A);
            await receiver.Focus(receiver.A);
            SendTab(false);
            SendTab(true);
            pressed = false;
            await Until(() => N.GetProp(receiver.Root, "TypeLay.Test.SourceTabUp.v1") == 1, receiver.Token);
            Assert.Equal((nint)1, N.GetProp(receiver.Root, "TypeLay.Test.SourceTabDownUnits.v1"));
            Assert.Equal(nint.Zero, N.GetProp(source.Root, "TypeLay.Test.SourceTabDown.v1"));
            Assert.Equal(nint.Zero, N.GetProp(source.Root, "TypeLay.Test.SourceTabUp.v1"));
            Assert.False(hook.TryRead(out _));
            Assert.Equal("Other application", N.ReadText(receiver.A, Limits.MaxTextUnits));
            Assert.Equal(nativeActivation ? "pre Hello post" : "pre old post", N.ReadText(source.A, Limits.MaxTextUnits));
            if (nativeActivation)
            {
                var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
                Assert.True(retired.Quiet);
                Assert.True(retired.Claimed);
                Assert.Equal(WriteStatus.Verified, retired.Receipt!.Status);
                source.RequestForeground(source.A);
                await source.Focus(source.A);
                N.Send(source.A, 0x00c7); // Explicit native Undo, never automatic recovery.
                Assert.Equal("pre old post", N.ReadText(source.A, Limits.MaxTextUnits));
            }
            source.AssertZeroSubmits();
            receiver.AssertZeroSubmits();
        }
        finally { if (pressed) SendTab(true); }
    }

    [Fact]
    public async Task ProductionInjectionPolicyNeverActivatesEvenAnInstalledNativeGrant()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await NavigateAndPrepare(desktop, desktop.A, "pre old post", new(0, 0));
        using var hook = new KeyboardHook(); // No automation opt-in.
        await Until(() => hook.Ready, desktop.Token);
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        var permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
        Guid generation = Guid.NewGuid();
        using var activation = Install(native, generation, permit);
        Assert.True(hook.InstallStockEditActivation(Lease(hook, permit), activation));
        bool pressed = true;
        try
        {
            SendTab(false);
            SendTab(true);
            pressed = false;
            await Until(() => (long)N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUp.v1") == 1, desktop.Token);
        }
        finally { if (pressed) SendTab(true); }
        Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDown.v1"));
        Assert.Null(hook.StockLastActivation);
        Assert.False(hook.TryRead(out _));
        var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
        Assert.True(retired.Quiet);
        Assert.False(retired.Claimed);
        Assert.Equal("pre old post", N.ReadText(desktop.A, Limits.MaxTextUnits));
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task WithdrawnActivationPassesTheWholeTabAndCannotChangeSelectionAfterGuiResumes()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await NavigateAndPrepare(desktop, desktop.A, "pre old post", new(0, 0));
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.Ready, desktop.Token);
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "entered"));
        using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "release"));
        var permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
        Guid generation = Guid.NewGuid();
        using var activation = Install(native, generation, permit);
        Assert.True(hook.InstallStockEditActivation(Lease(hook, permit), activation));
        // Upstream fixture hook holds an already-dispatched activation before
        // production enters. After timeout it MUST continue down that same chain.
        N.Send(desktop.Root, 0x8068, (nuint)desktop.A);
        Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourcePreclaimArmed.v1"));
        bool pressed = false;
        try
        {
            pressed = true;
            SendTab(false);
            Assert.True(entered.WaitOne(TimeSpan.FromSeconds(1)));
            await Until(() => hook.StockLastActivation is not null, desktop.Token);
            Assert.False(hook.StockLastActivation!.Claimed);
            SendTab(false); // The same press's repeat is also ordinary input.
            SendTab(true);
            pressed = false;
            await Until(() => !hook.StockPressOutstanding, desktop.Token);
        }
        finally { release.Set(); if (pressed) SendTab(true); }
        N.Send(desktop.Root, 0);
        Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourcePreclaimContinued.v1"));
        var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
        Assert.True(retired.Quiet);
        Assert.False(retired.Claimed);
        Assert.Equal("pre old post", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(0, 0), N.ReadSelection(desktop.A));
        try
        {
            await Until(() => (long)N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUp.v1") >= 1, desktop.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Withdrawn Tab did not reach source: rawDowns={N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDown.v1")}, "
                + $"downUnits={N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDownUnits.v1")}, "
                + $"ups={N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUp.v1")}, "
                + $"continued={N.GetProp(desktop.Root, "TypeLay.Test.SourcePreclaimContinued.v1")}, "
                + $"delay={N.GetProp(desktop.Root, "TypeLay.Test.SourceDelay.v1")}, "
                + $"text={N.ReadText(desktop.A, Limits.MaxTextUnits)}, selection={N.ReadSelection(desktop.A)}");
        }
        Assert.True((long)N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDownUnits.v1") == 2,
            $"Expected exactly two down units: rawDowns={N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDown.v1")}, "
            + $"downUnits={N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDownUnits.v1")}");
        Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUp.v1"));
        Assert.Equal("pre old post", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(0, 0), N.ReadSelection(desktop.A));
        Assert.False(hook.TryRead(out _));
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task ClaimBeforeDelayedReceiptConsumesOneWholePressAndCannotRetireTheRunningCallback()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await NavigateAndPrepare(desktop, desktop.A, "pre old post", new(0, 0));
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.Ready, desktop.Token);
        using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "entered"));
        using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "release"));
        N.Send(desktop.Root, 0x8061); // Downstream delay, after real source execution.
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        var permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
        Guid generation = Guid.NewGuid();
        using var activation = Install(native, generation, permit);
        Assert.True(hook.InstallStockEditActivation(Lease(hook, permit), activation));
        bool pressed = false;
        try
        {
            pressed = true;
            SendTab(false);
            Assert.True(entered.WaitOne(TimeSpan.FromSeconds(1)));
            await Until(() => hook.StockLastActivation is not null, desktop.Token);
            Assert.True(hook.StockLastActivation!.Claimed);
            Assert.Null(hook.StockLastActivation.Receipt);
            SendTab(false);
            SendTab(true);
            pressed = false;
            await Until(() => !hook.StockPressOutstanding, desktop.Token);
            Assert.False(native.RetireSourceAcceptance(generation, permit.Plan.Id).Quiet);
        }
        finally { release.Set(); if (pressed) SendTab(true); }
        var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
        Assert.True(retired.Quiet);
        Assert.True(retired.Claimed);
        Assert.Equal(WriteStatus.Verified, retired.Receipt!.Status);
        Assert.Equal("pre Hello post", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(9, 9), N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        Assert.Equal(nint.Zero, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDown.v1"));
        Assert.Equal(nint.Zero, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUp.v1"));
        Assert.True(hook.TryRead(out var notice));
        Assert.Equal("accept", notice!.Kind);
        Assert.Equal(WriteStatus.OutcomeUnknown, notice.CompletedReceipt!.Status);
        Assert.False(hook.TryRead(out _));
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData("text")]
    [InlineData("focus")]
    [InlineData("no-context")]
    [InlineData("restored-context")]
    public async Task SourceChangesBeforeClaimRejectActivationWithoutMutation(string change)
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await NavigateAndPrepare(desktop, desktop.A, "pre old post", new(0, 0));
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        var permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
        Guid generation = Guid.NewGuid();
        using var activation = Install(native, generation, permit);
        if (change == "text")
            N.SendString(desktop.A, N.WM_SETTEXT, "changed");
        else if (change == "focus")
            await desktop.Focus(desktop.B);
        else
        {
            N.Send(desktop.Root, 0x8062, (nuint)desktop.A);
            Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceContextDetached.v1"));
            if (change == "restored-context")
            {
                var revoked = native.PollSourceAcceptance(generation, permit.Plan.Id);
                Assert.False(revoked.Installed);
                Assert.Equal(Denial.ImeUnclear, revoked.Receipt!.Reason);
                N.Send(desktop.Root, 0x8063);
                Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceContextRestored.v1"));
            }
        }
        string text = N.ReadText(desktop.A, Limits.MaxTextUnits);
        var selection = N.ReadSelection(desktop.A);
        try
        {
            var result = activation.Activate();
            Assert.False(result.Claimed);
            if (change == "no-context")
                Assert.Equal(Denial.ImeUnclear, result.Receipt!.Reason);
            Assert.True(native.RetireSourceAcceptance(generation, permit.Plan.Id).Quiet);
        }
        finally
        {
            if (change == "no-context")
            {
                N.Send(desktop.Root, 0x8063);
                Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceContextRestored.v1"));
            }
        }
        Assert.Equal(text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(selection, N.ReadSelection(desktop.A));
        if (change == "restored-context")
        {
            // Restoration is not revival. Only a fresh snapshot and separately
            // installed grant may execute after exact retirement of the old one.
            var fresh = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
            Guid next = Guid.NewGuid();
            using var replacement = Install(native, next, fresh);
            var accepted = replacement.Activate();
            Assert.True(accepted.Claimed);
            Assert.Equal(WriteStatus.Verified, accepted.Receipt!.Status);
            Assert.True(native.RetireSourceAcceptance(next, fresh.Plan.Id).Quiet);
            Assert.Equal("pre Hello post", N.ReadText(desktop.A, Limits.MaxTextUnits));
        }
        desktop.AssertZeroSubmits();
    }

    private static InputLease Lease(KeyboardHook hook, AcceptancePermit permit) =>
        new(permit.Plan.Expected, hook.UserEpoch, hook.ForegroundEpoch, true, hook.CommandGeneration, 1);

    [Fact]
    public async Task SourceQuietDoesNotEraseClaimBeforeAnAlreadyOwningObserverReadsIt()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await NavigateAndPrepare(desktop, desktop.A, "pre old post", new(0, 0));
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        var permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
        Guid generation = Guid.NewGuid();
        Assert.True(native.InstallSourceAcceptance(generation, permit).Installed);
        var endpoint = Assert.IsType<NativeTransactions.SourceActivationEndpoint>(native.ReserveSourceActivation(generation, permit.Plan.Id));
        using var first = new StockEditActivation(endpoint.Nonce, endpoint.Command, generation, permit);
        using var duplicate = new StockEditActivation(endpoint.Nonce, endpoint.Command, generation, permit);
        using var observer = MemoryMappedFile.OpenExisting($"Local\\TypeLay.Source.{endpoint.Nonce:x16}");
        using var view = observer.CreateViewAccessor();
        Assert.True(first.Activate().Claimed);
        var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
        Assert.True(retired.Quiet);
        Assert.True(retired.Claimed);
        // Deliberately read the actual claim only AFTER Broker quiet/disposal.
        // This invariant catches the former page wipe, without claiming to
        // control the scheduler between SendMessage's return and a CPU load.
        Assert.Equal(1u, view.ReadUInt32(64));
        byte[] body = new byte[49160];
        view.ReadArray(176, body, 0, body.Length);
        Assert.All(body, value => Assert.Equal((byte)0, value));
        Assert.False(duplicate.Activate().Claimed);
        Assert.Equal(1u, view.ReadUInt32(64));
        Assert.Equal("pre Hello post", N.ReadText(desktop.A, Limits.MaxTextUnits));
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task ExactSourceProcessExitRetiresResourcesWithoutErasingTheClaimFact()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await NavigateAndPrepare(desktop, desktop.A, "pre old post", new(0, 0));
        using var process = System.Diagnostics.Process.GetProcessById(desktop.FixtureProcessId);
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        var permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
        Guid generation = Guid.NewGuid();
        using var activation = Install(native, generation, permit);
        Assert.True(activation.Activate().Claimed);
        Assert.Equal("pre Hello post", N.ReadText(desktop.A, Limits.MaxTextUnits));
        desktop.AssertZeroSubmits();
        N.Send(desktop.Root, 0x10); // Close only this test's owned stock fixture.
        await process.WaitForExitAsync(desktop.Token);
        var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
        Assert.True(retired.Quiet);
        Assert.True(retired.Claimed);
        Assert.Null(retired.Receipt); // Process exit is not an execution receipt.
    }

    [Theory]
    [InlineData(1, WriteStatus.RejectedBeforeMutation)]
    [InlineData(2, WriteStatus.OutcomeUnknown)]
    public async Task RealStockNotificationAfterClaimNeverReplaysTabEvenWhenExecutionCannotVerify(int mode, WriteStatus expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.SourceClaim." + Guid.NewGuid().ToString("N"));
        try
        {
            await ProductionForegroundWriteTests.BuildWinEventProbe(directory);
            await using var desktop = await ProductionForegroundWriteTests.Desktop.Start(
                sourceClaimProbe: Path.Combine(directory, "TypeLay.WinEventProbe.dll"));
            await NavigateAndPrepare(desktop, desktop.A, "pre old post", new(0, 0));
            using var hook = new KeyboardHook(allowInjectedInput: true);
            await Until(() => hook.Ready, desktop.Token);
            var observed = await desktop.Capture();
            using var native = new NativeTransactions();
            var permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
            Guid generation = Guid.NewGuid();
            Assert.True(native.InstallSourceAcceptance(generation, permit).Installed);
            var endpoint = Assert.IsType<NativeTransactions.SourceActivationEndpoint>(native.ReserveSourceActivation(generation, permit.Plan.Id));
            using var activation = new StockEditActivation(endpoint.Nonce, endpoint.Command, generation, permit);
            bool pressed = false;
            try
            {
                Assert.Equal((nuint)0, N.Send(desktop.Root, 0x8065, (nuint)endpoint.Nonce, (nint)mode));
                Assert.True(hook.InstallStockEditActivation(Lease(hook, permit), activation));
                pressed = true;
                SendTab(false);
                await Until(() => hook.StockLastActivation is not null, desktop.Token);
                Assert.True(hook.StockLastActivation!.Claimed);
                SendTab(false);
                SendTab(true);
                pressed = false;
                await Until(() => !hook.StockPressOutstanding, desktop.Token);
            }
            finally
            {
                if (pressed)
                    SendTab(true);
                Assert.Equal((nuint)0, N.Send(desktop.Root, 0x8066));
            }
            var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
            Assert.True(retired.Quiet);
            Assert.True(retired.Claimed);
            Assert.Equal(expected, retired.Receipt!.Status);
            Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceClaimInstalled.v1"));
            Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceClaimRemoved.v1"));
            Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceClaimCallbacks.v1"));
            Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceClaimAction.v1"));
            Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceClaimObserved.v1"));
            Assert.Equal(nint.Zero, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDown.v1"));
            Assert.Equal(nint.Zero, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUp.v1"));
            Assert.Equal(mode == 1 ? "pre old post" : "pre Hello post", N.ReadText(desktop.A, Limits.MaxTextUnits));
            desktop.AssertZeroSubmits();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LostQuietReceiptCannotTurnAnAlreadyPerformedWriteIntoRejectedBeforeMutation()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await NavigateAndPrepare(desktop, desktop.A, "pre old post", new(0, 0));
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "entered"));
        using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "release"));
        var permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
        Guid generation = Guid.NewGuid();
        using var activation = Install(native, generation, permit);
        Assert.True(activation.Activate().Claimed);
        N.Send(desktop.Root, 0x8067); // First revoke runs, but its caller times out.
        try
        {
            var uncertain = native.RetireSourceAcceptance(generation, permit.Plan.Id);
            Assert.False(uncertain.Quiet);
            Assert.True(entered.WaitOne(0));
        }
        finally { release.Set(); }
        N.Send(desktop.Root, 0); // GUI has now returned from the first revoke.
        var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
        Assert.True(retired.Quiet);
        Assert.True(retired.Claimed);
        Assert.Equal(WriteStatus.OutcomeUnknown, retired.Receipt!.Status);
        Assert.Equal("pre Hello post", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(9, 9), N.ReadSelection(desktop.A));
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData(0x102, "Qpre old post")]
    [InlineData(0x100, "pre old post")]
    [InlineData(0xb1, "pre old post")]
    public async Task PendingInputOrPostedEditRefusesClaimWithoutExecutingItInPreflight(int message, string finalText)
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await NavigateAndPrepare(desktop, desktop.A, "pre old post", new(0, 0));
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        var permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
        Guid generation = Guid.NewGuid();
        using var activation = Install(native, generation, permit);
        N.Send(desktop.Root, 0x8064, (nuint)desktop.A, (nint)message);
        Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourcePendingArmed.v1"));
        var result = activation.Activate();
        Assert.False(result.Claimed);
        Assert.Equal(Denial.EpochChanged, result.Receipt!.Reason);
        Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourcePendingPosted.v1"));
        Assert.True(native.RetireSourceAcceptance(generation, permit.Plan.Id).Quiet);
        await Until(() => N.ReadText(desktop.A, Limits.MaxTextUnits) == finalText
            && N.ReadSelection(desktop.A) == new TextRange(1, 1), desktop.Token);
        Assert.Equal(new TextRange(1, 1), N.ReadSelection(desktop.A));
        desktop.AssertZeroSubmits();
    }
    private Task NavigateAndPrepare(ProductionForegroundWriteTests.Desktop source, nint field, string text, TextRange selection)
        => ui.RunAsync(async () =>
        {
            source.Token.ThrowIfCancellationRequested();
            Assert.NotEqual(nint.Zero, source.Root);
            if (N.GetForegroundWindow() == source.Root)
            {
                // No caller foreground is needed when the source is already there.
                // Prepare still revalidates the retained process/field identity,
                // hit target, this click's dispatch receipt and exact source focus.
                await source.Prepare(field, text, selection);
                Assert.Equal((source.Root, field), N.Focused());
                return;
            }
            // This group can be the first desktop consumer. Establish the caller's
            // own foreground window instead of relying on a previous WPF test.
            var navigationContent = new TextBlock { Text = "Preparing the test source." };
            var navigation = new Window
            {
                Title = "TypeLay source setup navigation",
                Width = 160,
                Height = 100,
                // An empty Window does not raise ContentRendered.
                Content = navigationContent
            };
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mouseUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            navigation.ContentRendered += (_, _) => rendered.TrySetResult();
            nint navigationRoot = 0;
            nint? hit = null;
            bool? clickComplete = null, releaseComplete = null;
            bool callerPrepared = false;
            string stage = "show";
            try
            {
                source.Token.ThrowIfCancellationRequested();
                navigation.Show();
                stage = "render";
                await rendered.Task.WaitAsync(source.Token);
                source.Token.ThrowIfCancellationRequested();
                navigationRoot = new WindowInteropHelper(navigation).Handle;
                Assert.NotEqual(nint.Zero, navigationRoot);
                // A visible caller need not have permission to activate itself.
                // Establish the unmet foreground condition with one verified click.
                if (N.GetForegroundWindow() != navigationRoot)
                    await ClickNavigation();
                stage = "foreground";
                Assert.Equal(navigationRoot, N.GetForegroundWindow());
                callerPrepared = true;
                await source.NavigateAndPrepare(field, text, selection);
            }
            catch when (!callerPrepared)
            {
                try
                {
                    nint foregroundAfterFailure = N.GetForegroundWindow();
                    bool? hitIsSourceRoot = hit is { } observedHit ? observedHit == source.Root : null;
                    TestContext.Current.TestOutputHelper?.WriteLine($"Caller navigation failed: stage={stage}; "
                        + $"own={navigationRoot}; sourceRoot={source.Root}; sourceField={field}; sourceProcessId={source.FixtureProcessId}; "
                        + $"lastPreflightHit={hit?.ToString() ?? "not-sampled"}; hitIsSourceRoot={hitIsSourceRoot?.ToString() ?? "not-sampled"}; "
                        + $"foregroundAfterFailure={foregroundAfterFailure}; foregroundIsSourceRoot={foregroundAfterFailure == source.Root}; "
                        + $"clickComplete={clickComplete?.ToString() ?? "not-sampled"}; releaseComplete={releaseComplete?.ToString() ?? "not-sampled"}; "
                        + $"mouseUpReceived={mouseUp.Task.IsCompletedSuccessfully}.");
                }
                catch { /* Diagnostic output must not replace the preparation failure. */ }
                throw;
            }
            finally { navigation.Close(); }
            // Closing setup UI must not take focus back. Observe only; do not
            // reactivate or reclick the source before its fresh capture/permit.
            Assert.Equal((source.Root, field), N.Focused());

            async Task ClickNavigation()
            {
                stage = "click-preflight";
                Assert.True(GetCursorPos(out var previous));
                bool pointerMoved = false;
                navigation.PreviewMouseLeftButtonUp += ReceivedMouseUp;
                try
                {
                    source.Token.ThrowIfCancellationRequested();
                    nint previousDpi = SetThreadDpiAwarenessContext(-4);
                    Assert.NotEqual(nint.Zero, previousDpi);
                    try
                    {
                        Assert.True(navigationContent.ActualWidth > 0 && navigationContent.ActualHeight > 0);
                        var point = navigationContent.PointToScreen(new Point(navigationContent.ActualWidth / 2, navigationContent.ActualHeight / 2));
                        Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)
                            && point.X >= int.MinValue && point.X <= int.MaxValue
                            && point.Y >= int.MinValue && point.Y <= int.MaxValue);
                        var pixel = new N.Point { X = (int)point.X, Y = (int)point.Y };
                        RequireTarget();
                        Assert.All(new[] { 0x01, 0x02, 0x04, 0x10, 0x11, 0x12, 0x5B, 0x5C },
                            key => Assert.True(GetAsyncKeyState(key) >= 0));
                        source.Token.ThrowIfCancellationRequested();
                        Assert.True(SetCursorPos(pixel.X, pixel.Y));
                        pointerMoved = true;
                        RequireTarget();
                        source.Token.ThrowIfCancellationRequested();
                        stage = "click-input";
                        var click = HyPackageCheck.SendLeftClick();
                        clickComplete = click.Complete;
                        releaseComplete = click.ReleaseComplete;
                        Assert.True(click.ReleaseComplete);
                        Assert.True(click.Complete);

                        void RequireTarget()
                        {
                            Assert.Equal(navigationRoot, new WindowInteropHelper(navigation).Handle);
                            Assert.NotEqual(0u, N.GetWindowThreadProcessId(navigationRoot, out uint owner));
                            Assert.Equal((uint)Environment.ProcessId, owner);
                            Assert.True(N.IsWindowVisible(navigationRoot) && N.IsWindowEnabled(navigationRoot));
                            Assert.True(new Rect(0, 0, navigationContent.ActualWidth, navigationContent.ActualHeight)
                                .Contains(navigationContent.PointFromScreen(new Point(pixel.X, pixel.Y))));
                            nint currentHit = WindowFromPoint(pixel);
                            hit = currentHit;
                            Assert.Equal(navigationRoot, currentHit);
                        }
                    }
                    finally { SetThreadDpiAwarenessContext(previousDpi); }
                    stage = "mouse-up";
                    await mouseUp.Task.WaitAsync(source.Token);
                }
                finally
                {
                    navigation.PreviewMouseLeftButtonUp -= ReceivedMouseUp;
                    if (pointerMoved)
                        SetCursorPos(previous.X, previous.Y);
                }
            }
            void ReceivedMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs args)
            {
                if (clickComplete == true)
                    mouseUp.TrySetResult();
            }
        });
    private static StockEditActivation Install(NativeTransactions native, Guid generation, AcceptancePermit permit)
    {
        Assert.True(native.InstallSourceAcceptance(generation, permit).Installed);
        var endpoint = Assert.IsType<NativeTransactions.SourceActivationEndpoint>(
            native.ReserveSourceActivation(generation, permit.Plan.Id));
        return new(endpoint.Nonce, endpoint.Command, generation, permit);
    }
    private static async Task Until(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
            await Task.Delay(5, token);
    }
    private static void SendTab(bool up) => Assert.Equal(1u,
        SendInput(1, [new KeyboardInput { Type = 1, Key = 9, Flags = up ? 2u : 0u }], Marshal.SizeOf<KeyboardInput>()));
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] input, int size);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(N.Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out N.Point point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
}
