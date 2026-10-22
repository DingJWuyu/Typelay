using TypeLay.Windows;
using TypeLay.Windows.Testing;
using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

// Actual browser + production broker, with isolated test pages/profiles. This
// proves acquisition and typing updates, not translation quality or real IME.
[Collection("Desktop")]
public sealed class BrowserCaptureTests(ITestOutputHelper output)
{
    // Cooperative budget: 120s browser preparation + 20s capture, with room
    // for diagnostics/cleanup. Synchronous UIA calls need the outer executor's
    // process deadline; xUnit cancellation cannot terminate them. No launch retry.
    [Fact(Timeout = 160000)]
    public async Task ChromePageSearchCapturesAndUpdatesWithoutSentencePunctuation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-browser-" + Guid.NewGuid().ToString("N"));
        string title = "TypeLay browser input regression " + Guid.NewGuid().ToString("N");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string diagnostics = Path.Combine(repository.FullName, "artifacts", "test-results", "browser");
        Directory.CreateDirectory(diagnostics);
        BrowserWindows? windows = null;
        ChildProcess? ownedBrowser = null;
        bool failed = false;
        string stage = "external-browser-setup", captureOutcome = "not-run";
        try
        {
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            setup.CancelAfter(TimeSpan.FromSeconds(120));
            Directory.CreateDirectory(directory);
            const string initial = "ordinary search without punctuation";
            var start = await BrowserWindows.PageStartInfoAsync(directory, title, initial,
                Path.Combine(diagnostics, "chrome.log"), output.WriteLine, setup.Token);
            setup.Token.ThrowIfCancellationRequested();
            ownedBrowser = new ChildProcess(start);
            windows = new(ownedBrowser.Process, title);
            stage = "external-page-ready";
            nint window = await windows.PreparePageAsync(output.WriteLine, setup.Token);
            setup.Token.ThrowIfCancellationRequested();
            // The external application must be ready before measuring product
            // acquisition. This does not renew any capture/authority deadline.
            captureOutcome = "running";
            stage = "broker-start";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var broker = new BrokerClient();
            using var input = new WindowsInputService(broker, 0);
            await input.StartAsync().WaitAsync(deadline.Token);
            var context = new InputContext(0, 0, 0, null, [], false);
            async Task<InputSnapshot> Observe(string expected)
            {
                var elapsed = Stopwatch.StartNew();
                do
                {
                    Assert.Contains(windows.Read(), candidate => candidate.Handle == window && candidate.Page && candidate.Focused);
                    Assert.Equal(window, Native.GetForegroundWindow());
                    var observed = await input.CaptureAsync(context).WaitAsync(deadline.Token);
                    if (observed.Snapshot is { } source && observed.Focus == FocusMember.Source
                        && (source.Ime == ImeState.Clear ? source.Text : source.PreviewText) == expected)
                        return source;
                    await Task.Delay(40, deadline.Token);
                } while (elapsed.Elapsed < TimeSpan.FromSeconds(6));
                ReportFocus(window);
                throw new Xunit.Sdk.XunitException("The ordinary page search field was not captured; inspect the broker's fixed stage/rejection trace.");
            }
            stage = "capture-initial";
            var first = await Observe(initial);
            Assert.True(WindowsTarget.TryDecode(first.Target, out var target));
            Assert.Equal((long)window, target.RootHwnd);
            Assert.False(first.DirectWriteSupported);
            if (first.Ime != ImeState.Clear)
                Assert.False(first.WriteSupported);
            stage = "type-update";
            BrowserWindows.Keys(0x23); // End, then actual Unicode input with no punctuation or DOM value injection.
            const string appended = " and more words";
            BrowserWindows.TypeText(appended);
            stage = "capture-update";
            var next = await Observe(initial + appended);
            Assert.Equal(first.Target, next.Target);
            Assert.True(next.InputEpoch > first.InputEpoch);
            Assert.True(next.ObservedAtMs >= first.ObservedAtMs);
            Assert.Equal((long)Native.GetForegroundWindow(), target.RootHwnd);
            captureOutcome = "passed";
            stage = "complete";
            output.WriteLine($"Browser acquisition: stage={stage}; captureOutcome={captureOutcome}");
        }
        catch
        {
            failed = true;
            if (captureOutcome == "running")
                captureOutcome = "failed";
            output.WriteLine($"Browser acquisition failed: stage={stage}; captureOutcome={captureOutcome}");
            try
            {
                if (windows is not null)
                    await windows.SaveDiagnosticsAsync(diagnostics, output.WriteLine);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                output.WriteLine("Owned browser diagnostics unavailable: " + error.GetType().Name);
            }
            throw;
        }
        finally
        {
            // Retain the final owned process identities before closing the job,
            // then confirm their exit before deleting the disposable profile.
            // Never mask acquisition failure with a secondary cleanup failure.
            try
            {
                if (windows is not null && ownedBrowser is not null)
                    await windows.StopAsync(ownedBrowser, output.WriteLine);
                else
                    ownedBrowser?.Dispose();
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception error) when (failed && error is IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
            {
                output.WriteLine($"Isolated browser profile cleanup failed: {error.GetType().Name}; hresult=0x{error.HResult:x8}");
                if (error is IOException)
                    output.WriteLine("Isolated cleanup detail: " + System.Text.Json.JsonSerializer.Serialize(error.Message.Replace(directory, "<isolated-browser>", StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    [Fact]
    public async Task ChromeNewTabCentralSearchCapturesAndUpdatesWithoutSentencePunctuation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-newtab-" + Guid.NewGuid().ToString("N"));
        BrowserWindows? windows = null;
        ChildProcess? ownedBrowser = null;
        bool failed = false;
        string stage = "startup", focus = "unobserved";
        try
        {
            Directory.CreateDirectory(directory);
            ownedBrowser = new ChildProcess(BrowserWindows.NewTabStartInfo(directory, output.WriteLine));
            windows = new(ownedBrowser.Process, Guid.NewGuid().ToString("N"));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var (window, field) = await windows.PrepareNewTabAsync(output.WriteLine, deadline.Token);
            using var broker = new BrokerClient();
            using var input = new WindowsInputService(broker, 0);
            await input.StartAsync().WaitAsync(deadline.Token);
            var context = new InputContext(0, 0, 0, null, [], false);
            void RequireSource()
            {
                focus = windows.NewTabFocus(window, field);
                Assert.True(focus == "source", "new-tab-" + focus);
            }
            async Task<InputSnapshot> Observe(string expected)
            {
                var elapsed = Stopwatch.StartNew();
                do
                {
                    RequireSource();
                    var observed = await input.CaptureAsync(context).WaitAsync(deadline.Token);
                    RequireSource();
                    if (observed.Snapshot is { } source && observed.Focus == FocusMember.Source
                        && (source.Ime == ImeState.Clear ? source.Text : source.PreviewText) == expected)
                    {
                        Assert.True(WindowsTarget.TryDecode(source.Target, out var target), "new-tab-target-unavailable");
                        Assert.True(target.RootHwnd == (long)window
                            && target.ElementToken == string.Join('.', field.RuntimeId), "new-tab-target-mismatch");
                        Assert.False(source.DirectWriteSupported);
                        if (source.Ime != ImeState.Clear)
                            Assert.False(source.WriteSupported);
                        return source;
                    }
                    await Task.Delay(40, deadline.Token);
                } while (elapsed.Elapsed < TimeSpan.FromSeconds(6));
                throw new Xunit.Sdk.XunitException("new-tab-capture-unavailable; inspect the broker's fixed stage/rejection trace");
            }
            const string initial = "ordinary search without punctuation", appended = " and more words";
            stage = "first-input";
            RequireSource();
            BrowserWindows.TypeText(initial);
            var first = await Observe(initial);
            output.WriteLine("New tab capture: phase=first-input; source=True; exactSyntheticText=True");
            stage = "appended-input";
            RequireSource();
            BrowserWindows.Keys(0x23);
            RequireSource();
            BrowserWindows.TypeText(appended);
            var next = await Observe(initial + appended);
            Assert.True(first.Target == next.Target, "new-tab-target-changed");
            Assert.True(next.InputEpoch > first.InputEpoch);
            Assert.True(next.ObservedAtMs >= first.ObservedAtMs);
            output.WriteLine("New tab capture: phase=appended-input; source=True; exactSyntheticText=True; epochAdvanced=True");
        }
        catch
        {
            failed = true;
            // No page screenshots, labels, URLs, input values or suggestion text.
            output.WriteLine($"New tab failed: phase={stage}; focus={focus}");
            throw;
        }
        finally
        {
            try
            {
                if (windows is not null && ownedBrowser is not null)
                    await windows.StopAsync(ownedBrowser, output.WriteLine);
                else
                    ownedBrowser?.Dispose();
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception error) when (failed && error is IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
            {
                output.WriteLine($"Isolated new tab cleanup failed: {error.GetType().Name}; hresult=0x{error.HResult:x8}");
            }
        }
    }

    private void ReportFocus(nint window)
    {
        // Test-owned browser only. Log fixed booleans/control type, not window
        // titles, field text, paths, runtime IDs or user metadata. This separates
        // browser/page readiness from the production broker's rejection reason.
        try
        {
            if (window == 0 || Native.GetForegroundWindow() != window)
            {
                output.WriteLine($"Browser focus atMs={WindowsInterruptClock.NowMs}: foregroundOwned=False");
                return;
            }
            var focus = AutomationElement.FocusedElement?.Current;
            output.WriteLine($"Browser focus atMs={WindowsInterruptClock.NowMs}: windowReady={window != 0}; "
                + $"foregroundOwned={window != 0 && Native.GetForegroundWindow() == window}; "
                + $"controlType={focus?.ControlType.Id}; keyboardFocus={focus?.HasKeyboardFocus}; "
                + $"enabled={focus?.IsEnabled}; offscreen={focus?.IsOffscreen}; password={focus?.IsPassword}");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            output.WriteLine("Browser focus diagnostic unavailable: " + error.GetType().Name);
        }
    }
}
