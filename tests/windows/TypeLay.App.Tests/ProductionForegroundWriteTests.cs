using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using TypeLay.Protocol;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using TypeLay.Windows.Testing;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Two public process boundaries: production Broker IPC and its platform-native
// transaction wire. Both run against separate-process, unmodified system Edit
// controls. The wire cases isolate GUI-thread guards from Broker preflight.
[Collection("Desktop")]
public sealed class ProductionForegroundWriteTests
{
    [Fact]
    public async Task NativeReturnRejectsTheOriginalBindingAfterCookieLossBeforeMovingFocus()
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var source = await desktop.Capture();
        await desktop.Focus(desktop.B);
        Assert.NotEqual(nint.Zero, N.RemoveProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));

        var returned = await desktop.Call(new(BrokerProtocol.Version, 0, "return", "", BoundTarget: source.Target,
            ExpiresAtMs: WindowsInterruptClock.NowMs + 800));

        Assert.Equal("denied", returned.Status);
        Assert.Equal((desktop.Root, desktop.B), N.Focused());
        Assert.Equal(source.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(source.Selection, N.ReadSelection(desktop.A));
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("wrong-target")]
    public async Task NativeReturnRejectsInvalidRequestBeforeMovingFocus(string change)
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var source = await desktop.Capture();
        await desktop.Focus(desktop.B);
        string otherText = N.ReadText(desktop.B, Limits.MaxTextUnits);
        var target = change == "wrong-target"
            ? (source.Target.AsWindows() with
            {
                ControlHwnd = (long)desktop.B
            }).Id : source.Target;
        var returned = await desktop.Call(new(BrokerProtocol.Version, 0, "return", "", BoundTarget: target,
            ExpiresAtMs: WindowsInterruptClock.NowMs + (change == "expired" ? -1 : 800)));
        Assert.Equal("denied", returned.Status);
        Assert.Equal((desktop.Root, desktop.B), N.Focused());
        Assert.Equal(source.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(otherText, N.ReadText(desktop.B, Limits.MaxTextUnits));
        Assert.Equal(source.Selection, N.ReadSelection(desktop.A));
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData("success", false)]
    [InlineData("timeout", false)]
    [InlineData("cookie-lost", false)]
    [InlineData("success", true)]
    [InlineData("timeout", true)]
    [InlineData("cookie-lost", true)]
    public async Task NativeReturnCompletesRootActivationBeforeCorrectingAnotherField(string change, bool activateFromElsewhere)
    {
        await using var desktop = await Desktop.Start(returnFocus: activateFromElsewhere ? "root" : null);
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var source = await desktop.Capture();
        nint originalCookie = N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2");
        Assert.NotEqual(nint.Zero, originalCookie);
        uint sourceThread = N.GetWindowThreadProcessId(desktop.A, out uint sourceProcess);
        Assert.NotEqual(0u, sourceThread);
        await desktop.Focus(desktop.B);
        string otherText = N.ReadText(desktop.B, Limits.MaxTextUnits);
        await using var elsewhere = activateFromElsewhere ? await StockFixture.Start(desktop.Token) : null;
        if (elsewhere is not null)
            await elsewhere.Focus(elsewhere.A, desktop.Token);
        string? elsewhereText = elsewhere is null ? null : N.ReadText(elsewhere.A, Limits.MaxTextUnits);
        using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "entered"));
        using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "release"));
        using var drained = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "drained"));
        Assert.Equal((nuint)1, N.Send(desktop.Root, N.FixtureArmNullBarrier));
        long expiresAt = WindowsInterruptClock.NowMs + 800;
        var pending = desktop.Call(new(BrokerProtocol.Version, 0, "return", "", BoundTarget: source.Target,
            OverlayHwnd: (long)(elsewhere?.Root ?? 0),
            ExpiresAtMs: expiresAt));
        BrokerResponse returned;
        try
        {
            Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(2)), desktop.Token),
                "The original root must receive the real no-op completion message.");
            // Unlike the same-root B-to-A case, this host deliberately leaves
            // keyboard focus on its root after cross-process activation. The
            // original return must correct it once without changing the bound
            // top-level root/field identity or renewing its deadline.
            Assert.Equal((desktop.Root, activateFromElsewhere ? desktop.Root : desktop.B), N.Focused());
            if (change == "timeout")
            {
                // Keep the real receiver blocked until the original request's
                // budget ends. No delay, renewed request or extra activation.
                returned = await pending;
            }
            else
            {
                Assert.False(pending.IsCompleted);
                if (change == "cookie-lost")
                    Assert.NotEqual(nint.Zero, N.RemoveProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
                release.Set();
                returned = await pending;
            }
        }
        finally { release.Set(); }
        // A successful explicit release, not the fixture's two-second escape,
        // must have completed the receiver even in the request-timeout case.
        Assert.Equal((nuint)1, N.Send(desktop.Root, N.FixtureBarrierStatus));
        Assert.True(N.PostMessage(desktop.Root, N.FixtureGuiDrained, 0, 0));
        Assert.True(await Task.Run(() => drained.WaitOne(TimeSpan.FromSeconds(2)), desktop.Token));
        var afterReturn = N.ReadFocus();
        // One bounded, body-free failure record distinguishes raw foreground
        // identity from ancestry and actual keyboard focus. These observations
        // are not substitute acceptance predicates or a second return attempt.
        string facts = JsonSerializer.Serialize(new
        {
            atMs = WindowsInterruptClock.NowMs,
            returned.Status,
            foregroundIsRoot = afterReturn.Root == desktop.Root,
            foregroundIsField = afterReturn.Root == desktop.A,
            foregroundRootIsSource = N.GetAncestor(afterReturn.Root, 2) == desktop.Root,
            foregroundRootOwnerIsSource = N.GetAncestor(afterReturn.Root, 3) == desktop.Root,
            foregroundProcessIsSource = afterReturn.ProcessId == sourceProcess,
            foregroundThreadIsSource = afterReturn.ThreadId == sourceThread,
            afterReturn.GuiThreadInfoSucceeded,
            focusIsSourceField = afterReturn.Control == desktop.A,
            cookieIsOriginal = N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2") == originalCookie
        });
        Assert.True(returned.Status == (change == "success" ? "focused" : "denied"), facts);
        // Denial retains completed root activation, but expiration or cookie
        // loss before correction must leave field focus unchanged. No branch
        // may modify the body or selection.
        if (change == "success")
        {
            Assert.True((afterReturn.Root == desktop.Root || afterReturn.Root == desktop.A)
                && N.GetAncestor(afterReturn.Root, 2) == desktop.Root
                && afterReturn.ProcessId == sourceProcess && afterReturn.ThreadId == sourceThread
                && afterReturn.GuiThreadInfoSucceeded == true && afterReturn.Control == desktop.A
                && N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2") == originalCookie, facts);
            var observed = await desktop.Capture();
            Assert.Equal(source.Target, observed.Target);
            Assert.Equal(ImeState.Clear, observed.Ime);
        }
        else
            Assert.True(afterReturn.Root == desktop.Root
                && afterReturn.Control == (activateFromElsewhere ? desktop.Root : desktop.B), facts);
        Assert.Equal(source.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(otherText, N.ReadText(desktop.B, Limits.MaxTextUnits));
        Assert.Equal(source.Selection, N.ReadSelection(desktop.A));
        desktop.AssertZeroSubmits();
        if (elsewhere is not null)
        {
            Assert.Equal(elsewhereText, N.ReadText(elsewhere.A, Limits.MaxTextUnits));
            elsewhere.AssertZeroSubmits();
        }
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("source", true)]
    [InlineData("sibling", false)]
    [InlineData("other-root", false)]
    public async Task NativeForegroundChildKeepsOnlyItsExactSourceBinding(string foreground, bool direct)
    {
        await using var desktop = await Desktop.Start();
        // This field may already be focused. Complete each preparation command,
        // rather than letting a posted same-field focus outlive the later raw
        // foreground change merely because the requested focus already matches.
        N.Send(desktop.Root, N.FixtureFocus, (nuint)desktop.A);
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        N.SendString(desktop.A, N.WM_SETTEXT, "A🙂你好Z");
        N.Send(desktop.A, N.EM_SETSEL, 3, 5);
        var source = await desktop.Capture();
        nint cookie = N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2");
        string otherText = N.ReadText(desktop.B, Limits.MaxTextUnits);
        await using var elsewhere = foreground == "other-root" ? await StockFixture.Start(desktop.Token) : null;
        nint field = foreground == "source" ? desktop.A : elsewhere?.A ?? desktop.B;
        nint root = elsewhere?.Root ?? desktop.Root;
        using var hook = new KeyboardHook();
        // Observe startup before disposal can race the hook thread's creation.
        var startup = Stopwatch.StartNew();
        while (!hook.Ready && startup.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(10, desktop.Token);
        Assert.True(hook.Ready);
        N.Send(root, N.FixtureFocus, (nuint)field);
        Assert.Equal((root, field), N.Focused());
        // Exercise the Win32 state seen after CI460's UIA correction, without
        // depending on whether a particular provider happens to produce it.
        Assert.True(N.SetForegroundWindow(field));
        N.Send(root, 0);
        Assert.Equal((field, field), N.Focused());
        Assert.Equal(root, N.GetAncestor(field, 2));
        Assert.Equal(foreground == "source" ? FocusMember.Source : FocusMember.Other, hook.GetFocus(source.Target));

        var reply = await desktop.Call(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: direct));
        if (foreground == "source")
        {
            Assert.Equal(FocusMember.Source, reply.Focus);
            Assert.NotNull(reply.Snapshot);
            Assert.Equal(source.Target, reply.Snapshot.Target);
            Assert.Equal((long)desktop.Root, reply.Snapshot.Target.AsWindows().RootHwnd);
            Assert.Equal(ImeState.Clear, reply.Snapshot.Ime);
            Assert.Equal(source.Text, reply.Snapshot.Text);
            Assert.Equal(source.Selection, reply.Snapshot.Selection);
            Assert.Equal(direct, reply.Snapshot.WriteSupported);
            Assert.Equal(direct, reply.Snapshot.DirectWriteSupported);
        }
        else
            Assert.NotEqual(source.Target, reply.Snapshot?.Target);
        Assert.Equal(cookie, N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
        Assert.Equal(source.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(source.Selection, N.ReadSelection(desktop.A));
        Assert.Equal(otherText, N.ReadText(desktop.B, Limits.MaxTextUnits));
        Assert.Equal((field, field), N.Focused());
        desktop.AssertZeroSubmits();
        elsewhere?.AssertZeroSubmits();
    }

    [Theory]
    [InlineData("success")]
    [InlineData("timeout")]
    [InlineData("cookie-lost")]
    public async Task NativeReturnPreservesTheExactFieldRestoredByRootActivation(string change)
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var source = await desktop.Capture();
        string otherFieldText = N.ReadText(desktop.B, Limits.MaxTextUnits);
        await using var elsewhere = await StockFixture.Start(desktop.Token);
        await elsewhere.Focus(elsewhere.A, desktop.Token);
        string elsewhereText = N.ReadText(elsewhere.A, Limits.MaxTextUnits);
        using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "entered"));
        using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "release"));
        Assert.Equal((nuint)1, N.Send(desktop.Root, N.FixtureArmNullBarrier));
        var pending = desktop.Call(new(BrokerProtocol.Version, 0, "return", "", BoundTarget: source.Target,
            OverlayHwnd: (long)elsewhere.Root, ExpiresAtMs: WindowsInterruptClock.NowMs + 800));
        try
        {
            Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(2)), desktop.Token));
            // The actual host restored A during its one root activation. The
            // completion message must not first refocus A through UIA.
            Assert.Equal((desktop.Root, desktop.A), N.Focused());
            if (change == "timeout")
                Assert.Equal("denied", (await pending).Status);
            else
            {
                Assert.False(pending.IsCompleted);
                if (change == "cookie-lost")
                    Assert.NotEqual(nint.Zero, N.RemoveProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
                release.Set();
                Assert.Equal(change == "success" ? "focused" : "denied", (await pending).Status);
            }
        }
        finally { release.Set(); }
        Assert.Equal((nuint)1, N.Send(desktop.Root, N.FixtureBarrierStatus));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        Assert.Equal(source.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(source.Selection, N.ReadSelection(desktop.A));
        Assert.Equal(otherFieldText, N.ReadText(desktop.B, Limits.MaxTextUnits));
        Assert.Equal(elsewhereText, N.ReadText(elsewhere.A, Limits.MaxTextUnits));
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForegroundReplacementUsesOriginalScopeAfterTheExpectedSelectionCollapses(bool multiline)
    {
        await using var desktop = await Desktop.Start();
        nint field = desktop.Field(multiline ? "b" : "a");
        nint other = desktop.Field(multiline ? "a" : "b");
        string suffix = multiline ? "Z\r\nOutside" : "Z";
        N.SendString(other, N.WM_SETTEXT, "Other field stays unchanged");
        await desktop.Prepare(field, "A🙂你好" + suffix, new(3, 5));
        var original = await desktop.Capture();
        Assert.Equal(new TextRange(3, 5), original.TranslationScope);
        N.Send(field, N.EM_SETSEL, 5, 5);
        var expected = await desktop.Capture();
        Assert.Equal(original.Target, expected.Target);
        Assert.Equal(new TextRange(5, 5), expected.Selection);
        var plan = Plan(expected, original.TranslationScope);

        var receipt = await desktop.Write(plan);

        Assert.Equal(WriteStatus.Verified, receipt.Status);
        Assert.Equal(Denial.None, receipt.Reason);
        Assert.NotNull(receipt.VerifiedSnapshot);
        Assert.Equal(expected.Target, receipt.VerifiedSnapshot.Target);
        Assert.Equal("A🙂Hello" + suffix, N.ReadText(field, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(8, 8), N.ReadSelection(field));
        Assert.Equal("Other field stays unchanged", N.ReadText(other, Limits.MaxTextUnits));
        Assert.Equal((desktop.Root, field), N.Focused());
        desktop.AssertZeroSubmits();
        N.Send(field, 0x00c7); // EM_UNDO is a real system Edit positive control.
        Assert.Equal("A🙂你好" + suffix, N.ReadText(field, Limits.MaxTextUnits));
        desktop.AssertSubmitCounterWorks();
    }

    [Theory]
    [InlineData("same-root")]
    [InlineData("other-root")]
    [InlineData("selection")]
    [InlineData("expired")]
    public async Task BrokerRejectsAStaleForegroundAuthorizationWithoutChangingEitherField(string change)
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        N.SendString(desktop.B, N.WM_SETTEXT, "Other field stays unchanged");
        var plan = Plan(await desktop.Capture(), new(3, 5));
        await using var other = change == "other-root" ? await StockFixture.Start(desktop.Token) : null;
        (nint Root, nint Control) focus = (desktop.Root, desktop.A);
        if (change == "same-root")
        {
            await desktop.Focus(desktop.B);
            focus = (desktop.Root, desktop.B);
        }
        if (other is not null)
        {
            N.SendString(other.A, N.WM_SETTEXT, "Other root stays unchanged");
            await other.Focus(other.A, desktop.Token);
            focus = (other.Root, other.A);
        }
        if (change == "selection")
            N.Send(desktop.A, N.EM_SETSEL, 0, 0);
        TextRange selection = N.ReadSelection(desktop.A);
        long expiresAt = change == "expired" ? WindowsInterruptClock.NowMs - 1
            : WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs;

        var receipt = await desktop.Write(plan, expiresAt);

        Assert.Equal(WriteStatus.RejectedBeforeMutation, receipt.Status);
        if (change == "selection")
            Assert.Equal(Denial.SelectionChanged, receipt.Reason);
        if (change == "expired")
            Assert.Equal(Denial.StaleObservation, receipt.Reason);
        if (change is "same-root" or "other-root")
            Assert.Equal(Denial.TargetChanged, receipt.Reason);
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(selection, N.ReadSelection(desktop.A));
        Assert.Equal("Other field stays unchanged", N.ReadText(desktop.B, Limits.MaxTextUnits));
        Assert.Equal(focus, N.Focused());
        if (other is not null)
        {
            Assert.Equal("Other root stays unchanged", N.ReadText(other.A, Limits.MaxTextUnits));
            other.AssertZeroSubmits();
        }
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task ForegroundNativeRequestCannotFallBackWhenNativeCaptureIsDisabled()
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        var reply = await desktop.Call(new(BrokerProtocol.Version, 0, "write", "",
            BoundTarget: plan.Expected.Target, Plan: plan, CaptureDirect: false,
            ExpiresAtMs: WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs));

        Assert.NotNull(reply.Receipt);
        Assert.Equal(WriteStatus.RejectedBeforeMutation, reply.Receipt.Status);
        Assert.Equal(Denial.TargetChanged, reply.Receipt.Reason);
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task BrokerWithoutTheNativeDllDoesNotAdvertiseNativeWriteCapability()
    {
        await using var desktop = await Desktop.Start(withoutNativeDll: true);
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));

        var reply = await desktop.Call(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: true));

        Assert.Contains(reply.Status, new[] { "observed", "silent" });
        if (reply.Snapshot is { } snapshot)
        {
            Assert.NotEqual("win32-edit-transaction-v2", snapshot.Target.AsWindows().Provider);
            Assert.False(snapshot.DirectWriteSupported);
        }
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task CurrentNativeWireAndDllReplaceTheOriginalScopeWithACollapsedExpectedSelection()
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(5, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        using var transaction = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs);

        transaction.Send(desktop.A);

        transaction.AssertResult(1, Denial.None);
        Assert.Equal("A🙂HelloZ", transaction.ObservedText);
        Assert.Equal("A🙂HelloZ", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(8, 8), N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData("same-root", Denial.NotSourceFocus)]
    [InlineData("other-root", Denial.NotSourceFocus)]
    [InlineData("selection", Denial.SelectionChanged)]
    [InlineData("expired", Denial.StaleObservation)]
    public async Task NativeThreadRejectsChangedForegroundGuardsWhileItsPrivateBudgetIsFresh(string change, Denial reason)
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        N.SendString(desktop.B, N.WM_SETTEXT, "Other field stays unchanged");
        var plan = Plan(await desktop.Capture(), new(3, 5));
        await using var other = change == "other-root" ? await StockFixture.Start(desktop.Token) : null;
        (nint Root, nint Control) focus = (desktop.Root, desktop.A);
        if (change == "same-root")
        {
            await desktop.Focus(desktop.B);
            focus = (desktop.Root, desktop.B);
        }
        if (other is not null)
        {
            N.SendString(other.A, N.WM_SETTEXT, "Other root stays unchanged");
            await other.Focus(other.A, desktop.Token);
            focus = (other.Root, other.A);
        }
        if (change == "selection")
            N.Send(desktop.A, N.EM_SETSEL, 0, 0);
        TextRange selection = N.ReadSelection(desktop.A);
        long expiresAt = change == "expired" ? WindowsInterruptClock.NowMs - 1
            : WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs;
        using var transaction = new NativeTransaction(plan, expiresAt);

        transaction.Send(desktop.A);

        Assert.True(GetTickCount64() <= transaction.PrivateDeadline,
            "This case must exercise the original authorization, not an expired transport budget.");
        transaction.AssertResult(2, reason);
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(selection, N.ReadSelection(desktop.A));
        Assert.Equal("Other field stays unchanged", N.ReadText(desktop.B, Limits.MaxTextUnits));
        Assert.Equal(focus, N.Focused());
        if (other is not null)
        {
            Assert.Equal("Other root stays unchanged", N.ReadText(other.A, Limits.MaxTextUnits));
            other.AssertZeroSubmits();
        }
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData("cookie", Denial.TargetChanged)]
    [InlineData("readonly", Denial.Unsupported)]
    [InlineData("password", Denial.Unsupported)]
    public async Task NativeForegroundWriteRechecksLifetimeAndFieldProtection(string change, Denial reason)
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        using var transaction = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs);
        if (change == "cookie")
            Assert.NotEqual(nint.Zero, N.RemoveProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
        if (change == "readonly")
            N.Send(desktop.A, 0x00cf, 1); // EM_SETREADONLY.
        if (change == "password")
        {
            N.Send(desktop.A, 0x00cc, '*'); // EM_SETPASSWORDCHAR, synthetic source only.
            Assert.Equal((nuint)'*', N.Send(desktop.A, 0x00d2));
        }

        transaction.Send(desktop.A);

        transaction.AssertResult(2, reason);
        if (change == "password")
        {
            Assert.Equal((nuint)'*', N.Send(desktop.A, 0x00d2));
            Assert.Equal((nuint)1, N.Send(desktop.Root, N.FixtureClearPassword, (nuint)desktop.A));
            Assert.Equal((nuint)0, N.Send(desktop.A, 0x00d2));
        }
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task NativeTransactionQueuedBeforeExpiryIsRejectedWhenTheGuiThreadDispatchesItAfterExpiry()
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "entered"));
        using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "release"));
        using var drained = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "drained"));
        Assert.True(N.PostMessage(desktop.Root, N.FixtureBlockGui, 0, 0));
        try
        {
            Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(2)), desktop.Token),
                "The fixture must own the GUI barrier before the transaction is sent.");
            long expiresAt = WindowsInterruptClock.NowMs + Limits.PermitMaxAgeMs;
            using var transaction = new NativeTransaction(plan, expiresAt);
            Assert.True(WindowsInterruptClock.NowMs <= expiresAt);
            transaction.Queue(desktop.A);
            Assert.True(WindowsInterruptClock.NowMs <= expiresAt,
                "The actual native message must have been queued while authorization was live.");
            Assert.Equal(0u, transaction.Status);
            // Wait for the real authorization clock, not an arbitrary sleep.
            while (WindowsInterruptClock.NowMs <= expiresAt)
                await Task.Delay((int)Math.Clamp(expiresAt - WindowsInterruptClock.NowMs + 1, 1, 100), desktop.Token);
            Assert.Equal(0u, transaction.Status);
            Assert.True(GetTickCount64() < transaction.PrivateDeadline);
            // Sent messages run when the blocked thread resumes its message pump,
            // before this posted marker is removed from the fixture's queue.
            Assert.True(N.PostMessage(desktop.Root, N.FixtureGuiDrained, 0, 0));
            release.Set();
            Assert.True(await Task.Run(() => drained.WaitOne(TimeSpan.FromSeconds(2)), desktop.Token));
            Assert.Equal((nuint)1, N.Send(desktop.Root, N.FixtureBarrierStatus));
            Assert.True(GetTickCount64() <= transaction.PrivateDeadline,
                "The target must process the transaction while the private 500ms budget is still fresh.");
            transaction.AssertResult(2, Denial.StaleObservation);
            Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
            Assert.Equal((desktop.Root, desktop.A), N.Focused());
            desktop.AssertZeroSubmits();
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task OwnedRetirementRejectsOldCaptureReleaseAndWritesEvenAfterANewRun()
    {
        await using var desktop = await Desktop.Start(owned: true);
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        ulong oldCookie = unchecked((ulong)(long)N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));

        await desktop.OwnedControl("retire"); // A real GUI-thread ACK, not a successful pipe write.
        Assert.Equal(nint.Zero, N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
        foreach (uint operation in new uint[] { 1, 3, 4, 2, 5 })
        {
            using var stale = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs,
                operation: operation, lifetimeCookie: oldCookie);
            stale.Send(desktop.A);
            stale.AssertResult(2, Denial.TargetChanged);
        }
        await desktop.OwnedControl("begin");
        nint currentCookie = N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2");
        Assert.NotEqual(unchecked((nint)oldCookie), currentCookie);
        foreach (uint operation in new uint[] { 3, 1, 5 })
        {
            using var stale = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs,
                operation: operation, lifetimeCookie: oldCookie);
            stale.Send(desktop.A);
            stale.AssertResult(2, Denial.TargetChanged);
            Assert.Equal(currentCookie, N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
        }
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
        // The Broker must retire its old binding before obtaining a fresh one.
        await desktop.Call(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: true));
        var current = await desktop.Capture();
        using var positive = new NativeTransaction(Plan(current, new(3, 5)),
            WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs);
        positive.Send(desktop.A);
        positive.AssertResult(1, Denial.None);
        Assert.Equal("A🙂HelloZ", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        N.Send(desktop.A, 0x00c7);
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task QueuedOldWriteSettlesBeforeTheOuterLoopRetirementAcknowledgement()
    {
        await using var desktop = await Desktop.Start(owned: true);
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "entered"));
        using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "release"));
        Assert.True(N.PostMessage(desktop.Root, N.FixtureBlockGui, 0, 0));
        try
        {
            Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(2)), desktop.Token));
            using var old = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs);
            old.Queue(desktop.A);
            Assert.Equal(0u, old.Status);
            Task retired = desktop.OwnedControl("retire");
            Assert.False(retired.IsCompleted);
            release.Set();
            await retired;
            old.AssertResult(1, Denial.None); // Sent transactions finish before GetMessage returns to the outer loop.
            Assert.Equal("A🙂HelloZ", N.ReadText(desktop.A, Limits.MaxTextUnits));
            Assert.Equal(nint.Zero, N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
            desktop.AssertZeroSubmits();
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task OwnedRetirementInsideSelectionCallbackRejectsThePendingWriteBeforeTextMutation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.Retirement." + Guid.NewGuid().ToString("N"));
        string stage = "build-test-dll";
        bool completed = false, textUnchanged = false, focusUnchanged = false, lifetimeCleared = false;
        uint? wireStatus = null, wireReason = null;
        TextRange? finalSelection = null;
        JsonElement? observation = null;
        try
        {
            await BuildWinEventProbe(directory);
            stage = "start-owned-fixture";
            await using (var desktop = await Desktop.Start(owned: true,
                retirementProbe: Path.Combine(directory, "TypeLay.WinEventProbe.dll")))
            {
                await desktop.Prepare(desktop.A, "pre old post", new(0, 0));
                stage = "production-capture";
                // Capture installs the production transaction hook first. No
                // capture is allowed after the fixture puts its hook at the head.
                var plan = Plan(await desktop.Capture(), new(4, 7));
                using var transaction = new NativeTransaction(plan,
                    WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs);
                try
                {
                    stage = "arm-retirement";
                    transaction.ArmRetirement(desktop.Root);
                    stage = "send-real-wire";
                    transaction.Send(desktop.A);
                    wireStatus = transaction.Status;
                    wireReason = transaction.Reason;
                }
                finally
                {
                    // Explicit cleanup is required even when arming or sending
                    // failed. Killing the fixture alone is not a passing unhook.
                    observation = await desktop.FinishRetirement();
                }
                stage = "observe-real-control";
                textUnchanged = N.ReadText(desktop.A, Limits.MaxTextUnits) == "pre old post";
                finalSelection = N.ReadSelection(desktop.A);
                focusUnchanged = N.Focused() == (desktop.Root, desktop.A);
                lifetimeCleared = N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2") == 0;
                stage = "assert-retirement";
                JsonElement result = observation.Value;
                Assert.Equal(0, result.GetProperty("armStatus").GetInt32());
                Assert.Equal(0, result.GetProperty("cleanupStatus").GetInt32());
                Assert.Equal(2, result.GetProperty("hooksInstalled").GetInt32());
                Assert.Equal(2, result.GetProperty("hooksRemoved").GetInt32());
                Assert.Equal(1, result.GetProperty("transactionCalls").GetInt32());
                Assert.Equal(1, result.GetProperty("matchingCallbacks").GetInt32());
                Assert.Equal(1, result.GetProperty("retireCalls").GetInt32());
                Assert.Equal(1, result.GetProperty("retireResult").GetInt32());
                Assert.Equal(1, result.GetProperty("mappingUnmapped").GetInt32());
                Assert.Equal(1, result.GetProperty("mappingClosed").GetInt32());
                Assert.Equal(new[] { 0, 0 }, result.GetProperty("initialSelection").EnumerateArray().Select(value => value.GetInt32()));
                Assert.Equal(new[] { 4, 7 }, result.GetProperty("callbackSelection").EnumerateArray().Select(value => value.GetInt32()));
                transaction.AssertResult(2, Denial.SourceChanged);
                Assert.True(textUnchanged);
                // Owned retirement short-circuits production's post-SETSEL read;
                // the wire's observed 0..0 is not the final control selection.
                Assert.Equal(new TextRange(4, 7), finalSelection);
                Assert.True(focusUnchanged);
                Assert.True(lifetimeCleared);
                desktop.AssertZeroSubmits();
                stage = "stop-fixture";
            }
            stage = "delete-test-dll";
            Directory.Delete(directory, true);
            completed = true;
        }
        finally
        {
            string evidence = Path.Combine(Repository(), "artifacts/test-results/owned-retirement-reentry.json");
            Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
            File.WriteAllText(evidence, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                result = completed ? "passed" : "error",
                stage = completed ? "completed" : stage,
                observation,
                wireStatus,
                wireReason,
                textUnchanged,
                finalSelection = finalSelection is { } selection ? new[] { selection.Start, selection.End } : null,
                focusUnchanged,
                lifetimeCleared
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    internal static async Task BuildWinEventProbe(string directory)
    {
        var info = new ProcessStartInfo("python")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(Path.Combine(Repository(), "tests/windows/test_native_foreground_guards.py"));
        info.ArgumentList.Add("--build-winevent-probe");
        info.ArgumentList.Add(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var process = Process.Start(info) ?? throw new InvalidOperationException("Probe build did not start.");
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.True(File.Exists(Path.Combine(directory, "TypeLay.WinEventProbe.dll")));
        }
        finally { await Stop(process); }
    }

    [Theory]
    [InlineData("TypeLay.NativeInput.Transaction.v1", 1u, 88, 49248)]
    [InlineData("TypeLay.NativeInput.Transaction.v2", 1u, 88, 49248)]
    [InlineData("TypeLay.NativeInput.Transaction.v1", 2u, 104, 49264)]
    public async Task LegacyWireCannotGrantCurrentNativeWriteCapability(string messageName, uint version, int header, int size)
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        using var transaction = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs,
            messageName, version, header, size);

        transaction.Send(desktop.A);

        Assert.NotEqual(1u, transaction.Status);
        Assert.NotEqual(3u, transaction.Status);
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        desktop.AssertZeroSubmits();
        // This is legacy wire against the current DLL, not an old binary test.
    }

    private static WritePlan Plan(InputSnapshot expected, TextRange? scope)
    {
        var result = WriteSafety.Replace(expected.Text, scope, "Hello");
        return new(Guid.NewGuid(), Guid.NewGuid(), 1, expected, scope, "Hello", result.Text, result.Caret);
    }

    private sealed class NativeTransaction : IDisposable
    {
        private const int BufferBytes = 8193 * 2;
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        private readonly ulong _first, _second;
        private readonly uint _message;
        private readonly int _header;
        public ulong PrivateDeadline
        {
            get;
        }
        public uint Status => _view.ReadUInt32(4);
        public uint Reason => _view.ReadUInt32(8);
        public string ObservedText
        {
            get
            {
                int length = _view.ReadInt32(56);
                Assert.InRange(length, 0, Limits.MaxTextUnits);
                byte[] bytes = new byte[length * 2];
                _view.ReadArray(_header + 2 * BufferBytes, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
        }

        public NativeTransaction(WritePlan plan, long expiresAt, string messageName = "TypeLay.NativeInput.Transaction.v2",
            uint version = 2, int header = 104, int size = 49264, uint operation = 5, ulong? lifetimeCookie = null)
        {
            _header = header;
            var target = plan.Expected.Target.AsWindows();
            uint thread = N.GetWindowThreadProcessId((nint)target.ControlHwnd, out uint pid);
            Assert.Equal((uint)target.ProcessId, pid);
            Assert.NotEqual(0u, thread);
            ulong cookie = lifetimeCookie ?? unchecked((ulong)(long)N.GetProp((nint)target.ControlHwnd, "TypeLay.NativeInput.Lifetime.v2"));
            Assert.NotEqual(0UL, cookie);
            byte[] nonce = RandomNumberGenerator.GetBytes(16);
            _first = BitConverter.ToUInt64(nonce, 0);
            _second = BitConverter.ToUInt64(nonce, 8);
            _mapping = MemoryMappedFile.CreateNew($"Local\\TypeLay.Input.{_first:x16}{_second:x16}", size,
                MemoryMappedFileAccess.ReadWrite);
            _view = _mapping.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            _message = RegisterWindowMessageW(messageName);
            Assert.NotEqual(0u, _message);
            PrivateDeadline = GetTickCount64() + 500;
            _view.Write(0, version);
            _view.Write(12, version == 1 ? 2u : operation);
            _view.Write(16, pid);
            _view.Write(20, thread);
            _view.Write(24, target.ControlHwnd);
            _view.Write(32, target.RootHwnd);
            _view.Write(40, cookie);
            _view.Write(48, PrivateDeadline);
            _view.Write(56, plan.Expected.Text.Length);
            _view.Write(60, plan.Scope?.Start ?? 0);
            _view.Write(64, plan.Scope?.End ?? plan.Expected.Text.Length);
            _view.Write(68, plan.Replacement.Length);
            if (version == 2)
            {
                _view.Write(88, plan.Expected.Selection!.Value.Start);
                _view.Write(92, plan.Expected.Selection.Value.End);
                _view.Write(96, expiresAt);
            }
            WriteText(header, plan.Expected.Text);
            WriteText(header + BufferBytes, plan.Replacement);
        }

        public void Send(nint field) => N.Send(field, _message, (nuint)_first, unchecked((nint)_second));
        public void ArmRetirement(nint root) =>
            Assert.Equal((nuint)0, N.Send(root, 0x8052, (nuint)_first, unchecked((nint)_second)));
        public void Queue(nint field) => Assert.True(SendNotifyMessageW(field, _message, (nuint)_first, unchecked((nint)_second)));
        public void AssertResult(uint status, Denial reason)
        {
            Assert.Equal(status, Status);
            Assert.Equal(reason, (Denial)_view.ReadUInt32(8));
        }
        private void WriteText(int offset, string text)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text);
            _view.WriteArray(offset, bytes, 0, bytes.Length);
        }
        public void Dispose()
        {
            _view.Dispose();
            _mapping.Dispose();
        }
    }

    internal sealed class Desktop : IAsyncDisposable
    {
        private readonly StockFixture _fixture;
        private readonly Process _broker;
        private readonly CancellationTokenSource _deadline;
        private readonly string _pipeToken;
        private readonly string? _isolatedDirectory;
        private long _id;
        public CancellationToken Token => _deadline.Token;
        public nint Root => _fixture.Root;
        public nint A => _fixture.A;
        public nint B => _fixture.Field("b");
        public int FixtureProcessId => _fixture.ProcessId;
        internal int BrokerProcessId => _broker.Id;
        private Desktop(StockFixture fixture, Process broker, CancellationTokenSource deadline, string? isolatedDirectory)
        {
            _fixture = fixture;
            _broker = broker;
            _deadline = deadline;
            _pipeToken = broker.StartInfo.Environment["TYPELAY_PIPE_TOKEN"]!;
            _isolatedDirectory = isolatedDirectory;
        }
        public static async Task<Desktop> Start(bool withoutNativeDll = false, bool owned = false, string? retirementProbe = null, string? returnFocus = null, string? returnTrace = null, string? sourceClaimProbe = null, bool isolatedSourceAcceptance = false, bool sourceKeyboardProbe = false)
        {
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            StockFixture? fixture = null;
            Process? broker = null;
            string? isolatedDirectory = null;
            try
            {
                fixture = await StockFixture.Start(deadline.Token, owned, retirementProbe, returnFocus, sourceClaimProbe, sourceKeyboardProbe);
                string directory = Path.Combine(Repository(), "src/windows/TypeLay.Broker/bin/Release/net10.0-windows");
                if (withoutNativeDll)
                {
                    isolatedDirectory = Path.Combine(Path.GetTempPath(), "TypeLay.Foreground." + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(isolatedDirectory);
                    foreach (string name in new[] { "TypeLay.Broker.dll", "TypeLay.Broker.deps.json", "TypeLay.Broker.runtimeconfig.json",
                        "TypeLay.Core.dll", "TypeLay.Protocol.dll", "TypeLay.Windows.Contracts.dll" })
                        File.Copy(Path.Combine(directory, name), Path.Combine(isolatedDirectory, name));
                    directory = isolatedDirectory;
                    Assert.False(File.Exists(Path.Combine(directory, "TypeLay.NativeInput.dll")));
                }
                else
                {
                    nint library = NativeLibrary.Load(Path.Combine(directory, "TypeLay.NativeInput.dll"));
                    try
                    {
                        var version = Marshal.GetDelegateForFunctionPointer<ProtocolVersion>(
                            NativeLibrary.GetExport(library, "TypeLayNativeProtocolVersion"));
                        Assert.Equal(2u, version());
                    }
                    finally { NativeLibrary.Free(library); }
                }
                broker = isolatedSourceAcceptance
                    ? StartProcessWithTrace(returnTrace, Path.Combine(Repository(),
                        "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"), "--source-broker")
                    : StartProcessWithTrace(returnTrace, Path.Combine(directory, "TypeLay.Broker.dll"));
                var desktop = new Desktop(fixture, broker, deadline, isolatedDirectory);
                Assert.Equal("ready", (await desktop.Call(new(BrokerProtocol.Version, 0, "hello", ""))).Status);
                return desktop;
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                await Cleanup(broker, fixture, deadline, isolatedDirectory, errors);
                if (errors.Count > 1)
                    throw new AggregateException("Foreground fixture startup and cleanup failed.", errors);
                throw;
            }
        }
        public Task OwnedControl(string kind) => _fixture.OwnedControl(kind, Token);
        public Task<JsonElement> FinishRetirement() => _fixture.FinishRetirement(Token);
        public nint Field(string name) => _fixture.Field(name);
        public Task Focus(nint field) => _fixture.Focus(field, Token);
        public async Task Prepare(nint field, string text, TextRange selection)
        {
            await Focus(field);
            N.SendString(field, N.WM_SETTEXT, text);
            N.Send(field, N.EM_SETSEL, (nuint)selection.Start, (nint)selection.End);
        }
        internal Task<JsonElement> KeyboardProbeSnapshot() => _fixture.KeyboardProbeSnapshot(Token);
        public async Task<InputSnapshot> Capture()
        {
            var reply = await Call(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: true));
            Assert.NotNull(reply.Snapshot);
            Assert.Equal("win32-edit-transaction-v2", reply.Snapshot.Target.AsWindows().Provider);
            Assert.Equal(FixtureProcessId, reply.Snapshot.Target.AsWindows().ProcessId);
            Assert.Equal((long)N.Focused().Control, reply.Snapshot.Target.AsWindows().ControlHwnd);
            Assert.True(reply.Snapshot.WriteSupported);
            Assert.True(reply.Snapshot.DirectWriteSupported);
            Assert.Equal(ImeState.Clear, reply.Snapshot.Ime);
            return reply.Snapshot;
        }
        public async Task<WriteReceipt> Write(WritePlan plan, long? expiresAt = null)
        {
            var reply = await Call(new(BrokerProtocol.Version, 0, "write", "", BoundTarget: plan.Expected.Target,
                Plan: plan, CaptureDirect: true, ExpiresAtMs: expiresAt ?? WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs));
            Assert.NotNull(reply.Receipt);
            return reply.Receipt;
        }
        public async Task<BrokerResponse> Call(BrokerRequest request, Action? dispatched = null)
        {
            await Wire.WriteAsync(_broker.StandardInput.BaseStream, request with
            {
                Id = ++_id,
                Token = _pipeToken
            }, Token);
            dispatched?.Invoke();
            return await Wire.ReadAsync<BrokerResponse>(_broker.StandardOutput.BaseStream, Token);
        }
        public void AssertZeroSubmits() => _fixture.AssertZeroSubmits();
        public void AssertSubmitCounterWorks() => _fixture.AssertSubmitCounterWorks();
        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>();
            await Cleanup(_broker, _fixture, _deadline, _isolatedDirectory, errors);
            if (errors.Count != 0)
                throw new AggregateException("Foreground fixture cleanup failed.", errors);
        }
        private static async Task Cleanup(Process? broker, StockFixture? fixture, CancellationTokenSource deadline,
            string? isolatedDirectory, List<Exception> errors)
        {
            // Each owned resource gets a cleanup attempt even if an earlier one
            // fails; startup retains its original failure as the first error.
            try
            {
                if (broker is not null)
                    await Stop(broker);
            }
            catch (Exception error) { errors.Add(error); }
            try
            {
                if (fixture is not null)
                    await fixture.DisposeAsync();
            }
            catch (Exception error) { errors.Add(error); }
            try
            {
                deadline.Dispose();
            }
            catch (Exception error) { errors.Add(error); }
            try
            {
                if (isolatedDirectory is not null && Directory.Exists(isolatedDirectory))
                    Directory.Delete(isolatedDirectory, true);
            }
            catch (Exception error) { errors.Add(error); }
        }
    }

    private sealed class StockFixture(Process process, FixtureDescriptor host) : IAsyncDisposable
    {
        public nint Root => (nint)host.RootHwnd;
        public nint A => Field("a");
        public int ProcessId => process.Id;
        public nint Field(string name) => (nint)host.Fields.Single(value => value.Name == name).Hwnd;
        private Guid _connection, _run;
        private long _sequence = 1;
        public static async Task<StockFixture> Start(CancellationToken token, bool owned = false, string? retirementProbe = null, string? returnFocus = null, string? sourceClaimProbe = null, bool sourceKeyboardProbe = false)
        {
            Guid connection = Guid.NewGuid(), run = Guid.NewGuid();
            string assembly = Path.Combine(Repository(),
                "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll");
            string[] arguments = owned ? new[] { assembly, "--owned-controls", Path.Combine(Repository(),
                "src/windows/TypeLay.Broker/bin/Release/net10.0-windows/TypeLay.NativeInput.dll"),
                connection.ToString("N"), run.ToString("N") } : new[] { assembly, "--stock-controls" };
            if (retirementProbe is not null)
            {
                Assert.True(owned);
                arguments = [.. arguments, retirementProbe];
            }
            if (returnFocus is not null)
            {
                Assert.False(owned);
                Assert.Contains(returnFocus, new[] { "root", "second" });
                arguments = [.. arguments, "--return-focus", returnFocus];
            }
            if (sourceClaimProbe is not null)
            {
                Assert.False(owned);
                Assert.Null(returnFocus);
                arguments = [.. arguments, "--source-claim-probe", sourceClaimProbe];
            }
            if (sourceKeyboardProbe)
            {
                Assert.False(owned);
                Assert.Null(returnFocus);
                Assert.Null(sourceClaimProbe);
                arguments = [.. arguments, "--source-keyboard-probe"];
            }
            var process = StartProcess(arguments);
            try
            {
                string? line = await process.StandardOutput.ReadLineAsync(token);
                var host = JsonSerializer.Deserialize<FixtureDescriptor>(line ?? "null");
                Assert.NotNull(host);
                Assert.Equal(process.Id, host.ProcessId);
                return new(process, host)
                {
                    _connection = connection,
                    _run = run
                };
            }
            catch
            {
                await Stop(process);
                throw;
            }
        }
        internal async Task<JsonElement> KeyboardProbeSnapshot(CancellationToken token)
        {
            Assert.True(N.PostMessage(Root, 0x8081, 0, 0));
            string? line = await process.StandardOutput.ReadLineAsync(token);
            return JsonSerializer.Deserialize<JsonElement>(line ?? "null");
        }
        public async Task OwnedControl(string kind, CancellationToken token)
        {
            if (kind == "begin")
                _run = Guid.NewGuid();
            var command = new PracticeMessage(kind, _connection, _run, ++_sequence);
            await PracticeProgressPipe.WriteAsync(process.StandardInput.BaseStream, command, token);
            var ack = await PracticeProgressPipe.ReadAsync(process.StandardOutput.BaseStream, token);
            Assert.Equal(command with
            {
                Kind = kind == "begin" ? "started" : "retired"
            }, ack);
        }
        public async Task<JsonElement> FinishRetirement(CancellationToken token)
        {
            N.Send(Root, 0x8053);
            string? line = await process.StandardOutput.ReadLineAsync(token);
            return JsonSerializer.Deserialize<JsonElement>(line ?? "null");
        }
        public async Task Focus(nint field, CancellationToken token)
        {
            Assert.True(N.PostMessage(Root, N.FixtureFocus, (nuint)field, 0));
            var until = Stopwatch.StartNew();
            while (N.Focused() != (Root, field) && until.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(10, token);
            Assert.Equal((Root, field), N.Focused());
        }
        public void AssertZeroSubmits() => Assert.Equal((nuint)0, N.Send(Root, N.FixtureSubmitCount));
        public void AssertSubmitCounterWorks()
        {
            AssertZeroSubmits();
            N.Send((nint)host.SubmitButtonHwnd, 0x00f5); // BM_CLICK on our own positive control.
            Assert.Equal((nuint)1, N.Send(Root, N.FixtureSubmitCount));
        }
        public ValueTask DisposeAsync() => new(Stop(process));
    }

    // Reuse this fixture owner's exact process handle and bounded kill/exit proof.
    // No Broker, production lease or additional input hook is started for this diagnostic.
    internal static async Task RunConsumptionOrderProbe(string experiment, Action<JsonElement> observed, Action exited)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var process = StartProcess(Path.Combine(Repository(),
            "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"),
            "--source-consumption-order", experiment);
        try
        {
            string? line = await process.StandardOutput.ReadLineAsync(deadline.Token);
            using var document = JsonDocument.Parse(line ?? "null");
            var result = document.RootElement.Clone();
            observed(result); // Retain raw bounded observations even if exit/assertion fails.
            Assert.Equal(process.Id, result.GetProperty("process").GetInt32());
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            await Stop(process);
            exited();
        }
    }

    private static Process StartProcess(params string[] arguments) => StartProcessWithTrace(null, arguments);
    private static Process StartProcessWithTrace(string? trace, params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        info.Environment["TYPELAY_PIPE_TOKEN"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        info.Environment.Remove("TYPELAY_TRACE");
        if (trace is not null)
            info.Environment["TYPELAY_TRACE"] = trace;
        return Process.Start(info) ?? throw new InvalidOperationException("Test process did not start.");
    }
    private static async Task Stop(Process process)
    {
        using (process)
        using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var handle = process.SafeHandle;
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(cleanup.Token);
            }
            // HasExited/WaitForExitAsync can observe the exit code before the
            // kernel signals completion. The retirement probe stays loaded for
            // the fixture lifetime, so prove that exact process has terminated
            // before its caller deletes the DLL. Keep the same cleanup budget.
            while (!ProcessImage.Exited(handle))
                await Task.Delay(10, cleanup.Token);
        }
    }
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TypeLay.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ProtocolVersion();
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
}
