using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using TypeLay.Windows;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class NativeSourceSetupTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task PreparingAnAlreadyFocusedSourceWaitsForItsClickBeforeInitializingSelection()
        => desktop.RunAsync(async () =>
        {
            await using var source = await ProductionForegroundWriteTests.Desktop.Start();
            await source.Prepare(source.A, "Initial source", new(1, 3));
            Assert.Equal((source.Root, source.A), N.Focused());
            nint previousReceipt = N.GetProp(source.Root, N.FixtureClickReceipt);
            Assert.NotEqual(nint.Zero, previousReceipt);
            using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(source.FixtureProcessId, "entered"));
            using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(source.FixtureProcessId, "release"));
            Task? preparing = null;
            bool failed = false;
            try
            {
                Assert.Equal((nuint)1, N.Send(source.Root, N.FixtureArmClickBarrier, (nuint)source.A));
                preparing = source.Prepare(source.A, "Prepared selection", new(1, 5));
                Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(2)), source.Token));
                // The real mouse-up has been retrieved, but not dispatched. These
                // observations do not send messages to the blocked source thread.
                Assert.Equal((source.Root, source.A), N.Focused());
                Assert.Equal(previousReceipt, N.GetProp(source.Root, N.FixtureClickReceipt));
                Assert.False(preparing.IsCompleted);
                release.Set();
                await preparing;
                Assert.Equal((nuint)1, N.Send(source.Root, N.FixtureBarrierStatus));
                Assert.NotEqual(previousReceipt, N.GetProp(source.Root, N.FixtureClickReceipt));
                Assert.Equal(source.A, N.GetProp(source.Root, N.FixtureClickField));
                Assert.Equal("Prepared selection", N.ReadText(source.A, Limits.MaxTextUnits));
                Assert.Equal(new TextRange(1, 5), N.ReadSelection(source.A));
                source.AssertZeroSubmits();
            }
            catch { failed = true; throw; }
            finally
            {
                release.Set();
                // Join the bounded preparation before disposing its source, even
                // if an earlier assertion failed while the mouse-up was held.
                if (preparing is not null)
                {
                    try
                    {
                        await preparing;
                    }
                    catch (Exception error) when (failed)
                    {
                        TestContext.Current.TestOutputHelper?.WriteLine("Preparation cleanup also failed: " + error);
                    }
                }
            }
        });

    [Theory]
    [InlineData("visible")]
    [InlineData("covered")]
    [InlineData("cancelled")]
    [InlineData("wrong-root")]
    public Task ClickUsesTheVisibleOwnedStockEditOrRefusesWithoutClickingAnotherWindow(string scenario)
        => desktop.RunAsync(async () =>
        {
            await using var source = await Source.Start();
            var cover = new Window
            {
                Title = "TypeLay source setup cover",
                Width = 1600,
                Height = 1000,
                Left = 0,
                Top = 0,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Topmost = true,
                Content = new Button { Content = "not the source" }
            };
            int coverClicks = 0;
            cover.PreviewMouseLeftButtonDown += (_, _) => coverClicks++;
            Assert.True(GetCursorPos(out var previous));
            try
            {
                if (scenario == "covered")
                {
                    Assert.True(N.GetWindowRect(source.Root, out var bounds));
                    cover.Left = bounds.Left;
                    cover.Top = bounds.Top;
                    cover.Width = Math.Max(200, bounds.Right - bounds.Left);
                    cover.Height = Math.Max(200, bounds.Bottom - bounds.Top);
                }
                if (scenario == "visible")
                {
                    // Keep the source exposed but explicitly in the background,
                    // as it can be between successive desktop scenes.
                    cover.Width = 160;
                    cover.Height = 100;
                }
                if (scenario is "covered" or "visible")
                {
                    var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    cover.ContentRendered += (_, _) => rendered.TrySetResult();
                    cover.Show();
                    await rendered.Task.WaitAsync(source.Token);
                    Assert.True(cover.Activate());
                    Assert.Equal(new WindowInteropHelper(cover).Handle, N.GetForegroundWindow());
                }
                using var action = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
                if (scenario == "cancelled")
                    action.Cancel();
                nint root = scenario == "wrong-root" ? source.Field : source.Root;
                if (scenario == "cancelled")
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeSourceSetup.ClickAsync(root, source.Field, source.Process, action.Token));
                else if (scenario != "visible")
                {
                    var failure = await Assert.ThrowsAsync<NativeSourceSetup.Failure>(() => NativeSourceSetup.ClickAsync(root, source.Field, source.Process, action.Token));
                    Assert.Equal(scenario == "covered" ? "source-click-obscured" : "source-click-identity-changed", failure.Code);
                    // TRX retains Exception.Message, not the custom Reason/Code.
                    Assert.Equal(scenario == "covered" ? "SourceClickObscured" : "SourceClickIdentityChanged", failure.Message);
                }
                else
                {
                    await NativeSourceSetup.ClickAsync(root, source.Field, source.Process, action.Token);
                    while (N.Focused() != (source.Root, source.Field))
                        await Task.Delay(10, source.Token);
                }
                Assert.Equal(0, coverClicks);
                Assert.Equal(Source.Text, N.ReadText(source.Field, Limits.MaxTextUnits));
            }
            finally
            {
                cover.Close();
                SetCursorPos(previous.X, previous.Y);
            }
        });

    [Theory]
    [InlineData("foreground")]
    [InlineData("topmost")]
    [InlineData("cancelled")]
    [InlineData("wrong-root")]
    public Task ExplicitOwnedForegroundPreparationPreservesGeometryBeforeTheSingleSourceClick(string scenario)
        => desktop.RunAsync(async () =>
        {
            await using var source = await Source.Start();
            Assert.True(N.GetWindowRect(source.Root, out var originalBounds));
            Assert.True(N.GetWindowRect(source.Field, out var fieldBounds));
            var point = new N.Point
            {
                X = fieldBounds.Left + (fieldBounds.Right - fieldBounds.Left) / 2,
                Y = fieldBounds.Top + (fieldBounds.Bottom - fieldBounds.Top) / 2
            };
            var cover = new Window
            {
                Title = "TypeLay ordinary source cover",
                Left = originalBounds.Left,
                Top = originalBounds.Top,
                Width = originalBounds.Right - originalBounds.Left,
                Height = originalBounds.Bottom - originalBounds.Top,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Topmost = scenario == "topmost",
                Content = new Button { Content = "not the source" }
            };
            int coverClicks = 0;
            cover.PreviewMouseLeftButtonDown += (_, _) => coverClicks++;
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cover.ContentRendered += (_, _) => rendered.TrySetResult();
            Assert.True(GetCursorPos(out var previous));
            var observation = new NativeSourceSetup.Observation();
            long? coverRenderedAtMs = null, visibleAtMs = null, focusWaitStartedAtMs = null, focusMatchedAtMs = null;
            int focusObservations = 0;
            (N.FocusObservation Focus, long AtMs)? firstFocus = null, lastFocus = null;
            nint coverRoot = 0;
            try
            {
                cover.Show();
                await rendered.Task.WaitAsync(source.Token);
                coverRenderedAtMs = WindowsInterruptClock.NowMs;
                Assert.True(cover.Activate());
                coverRoot = new WindowInteropHelper(cover).Handle;
                Assert.Equal(coverRoot, N.GetForegroundWindow());
                Assert.Equal(coverRoot, N.GetAncestor(WindowFromPoint(point), 2));
                Assert.Equal(0, N.GetStyle(source.Root, -20) & 0x8); // WS_EX_TOPMOST.
                Assert.Equal(scenario == "topmost" ? 0x8 : 0, N.GetStyle(coverRoot, -20) & 0x8);
                var beforeFocus = N.Focused();
                using var action = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
                if (scenario == "cancelled")
                    action.Cancel();
                if (scenario == "cancelled")
                    Assert.ThrowsAny<OperationCanceledException>(() => NativeSourceSetup.RequestOwnedForeground(source.Root, source.Field, source.Process, action.Token));
                else if (scenario == "wrong-root")
                {
                    var failure = Assert.Throws<NativeSourceSetup.Failure>(() => NativeSourceSetup.RequestOwnedForeground(source.Field, source.Field, source.Process, action.Token));
                    Assert.Equal("source-foreground-identity-changed", failure.Code);
                }
                else
                    NativeSourceSetup.RequestOwnedForeground(source.Root, source.Field, source.Process, action.Token);
                if (scenario == "foreground")
                {
                    point = await NativeSourceSetup.WaitVisibleAsync(source.Root, source.Field, source.Process, observation, source.Token);
                    visibleAtMs = WindowsInterruptClock.NowMs;
                }
                else if (scenario == "cancelled")
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeSourceSetup.WaitVisibleAsync(source.Root, source.Field, source.Process, observation, action.Token));
                else if (scenario == "wrong-root")
                {
                    var failure = await Assert.ThrowsAsync<NativeSourceSetup.Failure>(() => NativeSourceSetup.WaitVisibleAsync(source.Field, source.Field, source.Process, observation, action.Token));
                    Assert.Equal("source-foreground-identity-changed", failure.Code);
                }
                else
                {
                    // Activation and the target thread's dispatch are distinct;
                    // observe the one request before testing persistent occlusion.
                    while (N.GetForegroundWindow() != source.Root)
                        await Task.Delay(10, source.Token);
                    using var waitStop = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
                    var waiting = NativeSourceSetup.WaitVisibleAsync(source.Root, source.Field, source.Process, observation, waitStop.Token);
                    Assert.False(waiting.IsCompleted);
                    Assert.Equal("foreign", observation.VisibilityLastHit);
                    waitStop.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
                }
                if (scenario is "cancelled" or "wrong-root")
                {
                    Assert.Equal(beforeFocus, N.Focused());
                    Assert.Equal(coverRoot, N.GetForegroundWindow());
                }
                else
                {
                    Assert.True(observation.VisibilityForegroundMatched);
                    Assert.Equal(source.Root, N.GetForegroundWindow());
                }
                Assert.True(N.GetWindowRect(source.Root, out var afterBounds));
                Assert.Equal(originalBounds, afterBounds);
                Assert.Equal(0, N.GetStyle(source.Root, -20) & 0x8);
                if (scenario == "foreground")
                {
                    nint previousDpi = SetThreadDpiAwarenessContext(-4);
                    Assert.NotEqual(0, previousDpi);
                    try
                    {
                        Assert.Equal(source.Field, WindowFromPoint(point));
                    }
                    finally { SetThreadDpiAwarenessContext(previousDpi); }
                    observation.ClickStartedAtMs = WindowsInterruptClock.NowMs;
                    await NativeSourceSetup.ClickAsync(source.Root, source.Field, source.Process, source.Token);
                    observation.ClickEndedAtMs = WindowsInterruptClock.NowMs;
                    focusWaitStartedAtMs = WindowsInterruptClock.NowMs;
                    while (true)
                    {
                        // Focused() already performs this one ReadFocus call. Keep
                        // its exact comparison, but retain the discarded error facts.
                        var focus = N.ReadFocus();
                        lastFocus = (focus, WindowsInterruptClock.NowMs);
                        firstFocus ??= lastFocus;
                        focusObservations++;
                        if ((focus.Root, focus.Control) == (source.Root, source.Field))
                            break;
                        await Task.Delay(10, source.Token);
                    }
                    focusMatchedAtMs = WindowsInterruptClock.NowMs;
                }
                else
                {
                    Assert.Equal(coverRoot, N.GetAncestor(WindowFromPoint(point), 2));
                    if (scenario == "topmost")
                    {
                        var failure = await Assert.ThrowsAsync<NativeSourceSetup.Failure>(() => NativeSourceSetup.ClickAsync(source.Root, source.Field, source.Process, source.Token));
                        Assert.Equal("source-click-obscured", failure.Code);
                    }
                }
                Assert.Equal(0, coverClicks);
                Assert.Equal(Source.Text, N.ReadText(source.Field, Limits.MaxTextUnits));
            }
            finally
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"Source visibility: attempts={observation.VisibilityAttempts}; "
                    + $"foregroundMatched={observation.VisibilityForegroundMatched}; geometry={observation.VisibilityGeometryAvailable}; firstHit={observation.VisibilityFirstHit}; lastHit={observation.VisibilityLastHit}.");
                TestContext.Current.TestOutputHelper?.WriteLine("Source focus wait: " + JsonSerializer.Serialize(new
                {
                    budgetMs = 10000,
                    sourceReadyMs = Since(source.ReadyAtMs),
                    coverRenderedMs = Since(coverRenderedAtMs),
                    visibleMs = Since(visibleAtMs),
                    clickStartedMs = Since(observation.ClickStartedAtMs),
                    clickReturnedMs = Since(observation.ClickEndedAtMs),
                    focusWaitStartedMs = Since(focusWaitStartedAtMs),
                    focusMatchedMs = Since(focusMatchedAtMs),
                    focusObservations,
                    first = Project(firstFocus),
                    last = Project(lastFocus)
                }));
                cover.Close();
                SetCursorPos(previous.X, previous.Y);
            }
            long? Since(long? atMs) => atMs - source.BudgetStartedAtMs;
            object? Project((N.FocusObservation Focus, long AtMs)? item)
            {
                if (item is not { } sample)
                    return null;
                var focus = sample.Focus;
                return new
                {
                    atMs = Since(sample.AtMs),
                    root = focus.Root == source.Root ? "source" : focus.Root == coverRoot && coverRoot != 0 ? "cover"
                        : focus.Root == 0 ? "none" : "other",
                    control = focus.Control == source.Field ? "source-field" : focus.Control == source.OtherField ? "other-field"
                        : focus.Control == 0 ? "none" : "other",
                    focus.ThreadLookupError,
                    focus.GuiThreadInfoSucceeded,
                    focus.GuiThreadInfoError
                };
            }
        });

    [Theory]
    [InlineData("responsive")]
    [InlineData("busy")]
    [InlineData("changed-text")]
    [InlineData("wrong-field")]
    [InlineData("cancelled")]
    public Task ResponseGateOnlyWaitsForReadTimeoutAndNeverMutatesTheSelection(string scenario)
        => desktop.RunAsync(async () =>
        {
            await using var source = await Source.Start();
            using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(source.Process.Id, "entered"));
            using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(source.Process.Id, "release"));
            try
            {
                // This contract starts from a prepared selection, not an input
                // queue. Focus can already match before a newly injected click
                // is dispatched; that click would then overwrite EM_SETSEL.
                // ClickAsync has its own real-click contracts above.
                N.Send(source.Root, N.FixtureFocus, (nuint)source.Field, 0);
                while (N.Focused() != (source.Root, source.Field))
                    await Task.Delay(10, source.Token);
                N.Send(source.Field, N.EM_SETSEL, 1, 5);
                Assert.Equal(new TextRange(1, 5), N.ReadSelection(source.Field));
                var observation = new NativeSourceSetup.Observation();
                using var action = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
                if (scenario == "cancelled")
                    action.Cancel();
                if (scenario == "busy")
                {
                    Assert.True(N.PostMessage(source.Root, N.FixtureBlockGui, 0, 0));
                    Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(2)), source.Token));
                }
                string expected = scenario == "changed-text" ? "different synthetic source" : Source.Text;
                nint field = scenario == "wrong-field" ? source.OtherField : source.Field;
                var waiting = NativeSourceSetup.WaitResponsiveAsync(source.Root, field, source.Process, expected, observation, action.Token);
                if (scenario == "busy")
                {
                    // The first genuine 100ms native read has timed out. Release
                    // the existing bounded fixture barrier, not a timing sleep.
                    Assert.False(waiting.IsCompleted);
                    Assert.True(observation.ReadTimeouts > 0);
                    release.Set();
                }
                if (scenario == "cancelled")
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
                else if (scenario is "changed-text" or "wrong-field")
                {
                    var failure = await Assert.ThrowsAsync<NativeSourceSetup.Failure>(() => waiting);
                    Assert.Equal(scenario == "changed-text" ? "source-response-text-changed" : "source-response-identity-or-focus-changed", failure.Code);
                }
                else
                {
                    await waiting;
                    Assert.NotNull(observation.ReadCompletedAtMs);
                    Assert.True(observation.ReadCompletedAtMs >= observation.ReadStartedAtMs);
                    Assert.True(observation.IdentityMatched && observation.FocusMatched);
                }
                Assert.Equal(Source.Text, N.ReadText(source.Field, Limits.MaxTextUnits));
                Assert.Equal(new TextRange(1, 5), N.ReadSelection(source.Field));
                Assert.False(observation.SelectionCompleted);
                Assert.Null(observation.SelectionStartedAtMs);
            }
            finally
            {
                release.Set();
            }
        });

    private sealed class Source(Process process, FixtureDescriptor descriptor, CancellationTokenSource deadline, long budgetStartedAtMs) : IAsyncDisposable
    {
        internal const string Text = "Synthetic stock source 🙂";
        internal Process Process => process;
        internal long BudgetStartedAtMs => budgetStartedAtMs;
        internal long ReadyAtMs
        {
            get; private set;
        }
        internal CancellationToken Token => deadline.Token;
        internal nint Root => (nint)descriptor.RootHwnd;
        internal nint Field => (nint)descriptor.Fields.Single(item => item.Name == "a").Hwnd;
        internal nint OtherField => (nint)descriptor.Fields.Single(item => item.Name == "b").Hwnd;
        internal static async Task<Source> Start()
        {
            string root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "TypeLay.slnx")))
                root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("TypeLay.slnx");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(root, "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"));
            start.ArgumentList.Add("--stock-controls");
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            long budgetStartedAtMs = WindowsInterruptClock.NowMs;
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var process = Process.Start(start)!;
            try
            {
                var descriptor = JsonSerializer.Deserialize<FixtureDescriptor>(await process.StandardOutput.ReadLineAsync(deadline.Token) ?? "null");
                Assert.NotNull(descriptor);
                Assert.Equal(process.Id, descriptor.ProcessId);
                var source = new Source(process, descriptor, deadline, budgetStartedAtMs);
                N.SendString(source.Field, N.WM_SETTEXT, Text);
                source.ReadyAtMs = WindowsInterruptClock.NowMs;
                return source;
            }
            catch
            {
                try
                {
                    await Stop(process);
                }
                finally { process.Dispose(); deadline.Dispose(); }
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Stop(process);
            }
            finally { process.Dispose(); deadline.Dispose(); }
        }
        private static async Task Stop(Process process)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token);
        }
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(N.Point point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out N.Point point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
}
