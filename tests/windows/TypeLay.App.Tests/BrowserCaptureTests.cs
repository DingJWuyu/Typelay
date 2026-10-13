using TypeLay.Windows;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

// Actual browser + production broker, with an isolated local page/profile. This
// proves acquisition and typing updates, not translation quality or real IME.
[Collection("Desktop")]
public sealed class BrowserCaptureTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ChromePageSearchCapturesAndUpdatesWithoutSentencePunctuation()
    {
        string? chrome = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Google", "Chrome", "Application", "chrome.exe"))
            .FirstOrDefault(File.Exists);
        Assert.True(chrome is not null, "This desktop regression requires the runner's installed Google Chrome.");
        output.WriteLine("Installed Chrome version: " + FileVersionInfo.GetVersionInfo(chrome!).ProductVersion);
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
        try
        {
            Directory.CreateDirectory(directory);
            string page = Path.Combine(directory, "search.html");
            const string initial = "ordinary search without punctuation";
            await File.WriteAllTextAsync(page, $$"""
            <!doctype html><meta charset="utf-8"><title>{{title}}</title>
            <style>body{margin:0;height:100vh;display:grid;place-items:center;font:20px sans-serif}input{width:65vw;padding:14px}</style>
            <input id="query" type="search" aria-label="Search" value="ordinary search without punctuation" autofocus>
            <script>
            let inputClicked = false;
            document.addEventListener("pointerdown", event => {
              inputClicked = event.isTrusted && event.target === document.getElementById("query");
            });
            function reportFocus() {
              const pageFocused = document.hasFocus();
              const inputFocused = document.activeElement === document.getElementById("query");
              const title = "{{title}} " + (pageFocused && inputFocused ? "focused" : "unfocused") +
                " page=" + pageFocused + " input=" + inputFocused + " click=" + inputClicked;
              if (document.title !== title) document.title = title;
              requestAnimationFrame(reportFocus);
            }
            // Sample live state without changing focus or enabling UIA, rather
            // than retaining whichever focus event happened to run last.
            reportFocus();
            </script>
            """, TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo(chrome!) { UseShellExecute = false, RedirectStandardError = true };
            // Official Chromium file logging, scoped to this disposable profile.
            // ChildProcess deliberately drains stderr without retaining its text.
            start.Environment["CHROME_LOG_FILE"] = Path.Combine(diagnostics, "chrome.log");
            start.ArgumentList.Add("--enable-logging");
            start.ArgumentList.Add("--user-data-dir=" + Path.Combine(directory, "profile"));
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add("--disable-search-engine-choice-screen");
            start.ArgumentList.Add("--new-window");
            start.ArgumentList.Add(new Uri(page).AbsoluteUri);
            ownedBrowser = new ChildProcess(start);
            var browser = ownedBrowser.Process;
            windows = new(browser, title);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            nint window = 0;
            bool clicked = false;
            var startup = Stopwatch.StartNew();
            do
            {
                var pages = windows.Read().Where(candidate => candidate.Page).ToArray();
                Assert.True(pages.Length <= 1, "The isolated page must belong to exactly one owned browser window.");
                if (pages.Length == 1)
                {
                    window = pages[0].Handle;
                    if (Native.GetForegroundWindow() != window)
                        Native.SetForegroundWindow(window);
                    if (Native.GetForegroundWindow() == window && !clicked)
                    {
                        var geometry = windows.ClickPageInput(window);
                        clicked = true;
                        output.WriteLine($"Clicked owned page input: dpi={geometry.Dpi}; viewportWidth={geometry.Width}; viewportHeight={geometry.Height}");
                    }
                    if (Native.GetForegroundWindow() == window && pages[0].Focused)
                        break;
                }
                await Task.Delay(40, deadline.Token);
            } while (startup.Elapsed < TimeSpan.FromSeconds(6));
            var ready = windows.Read();
            output.WriteLine($"Browser startup atMs={WindowsInterruptClock.NowMs}: ownedProcesses={windows.ProcessCount}; "
                + $"visibleOwnedWindows={ready.Length}; ownedPages={ready.Count(candidate => candidate.Page)}; "
                + $"foregroundOwned={window != 0 && Native.GetForegroundWindow() == window}; "
                + $"domFocusConfirmed={ready.Any(candidate => candidate.Handle == window && candidate.Focused)}; "
                + $"inputClicked={clicked}; "
                + $"launcherExited={browser.HasExited}");
            bool fixtureReady = window != 0 && Native.GetForegroundWindow() == window
                && ready.Any(candidate => candidate.Handle == window && candidate.Focused);
            Assert.True(fixtureReady,
                "Browser fixture startup failed: the owned local page and DOM input focus were not established; no input capture was attempted.");
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
            var first = await Observe(initial);
            Assert.True(WindowsTarget.TryDecode(first.Target, out var target));
            Assert.Equal((long)window, target.RootHwnd);
            Assert.False(first.DirectWriteSupported);
            if (first.Ime != ImeState.Clear)
                Assert.False(first.WriteSupported);
            Keys(0x23); // End, then actual Unicode input with no punctuation or DOM value injection.
            const string appended = " and more words";
            TypeText(appended);
            var next = await Observe(initial + appended);
            Assert.Equal(first.Target, next.Target);
            Assert.True(next.InputEpoch > first.InputEpoch);
            Assert.True(next.ObservedAtMs >= first.ObservedAtMs);
            Assert.Equal((long)Native.GetForegroundWindow(), target.RootHwnd);
        }
        catch
        {
            failed = true;
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
            // The owned job closes before profile cleanup, including when the
            // browser's initial process exits before its descendants. Never mask
            // the original acquisition failure with a secondary cleanup failure.
            try
            {
                ownedBrowser?.Dispose();
                if (windows is not null)
                    await windows.StopAsync();
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

    private static void Keys(ushort key) => Send([
        new() { Type = 1, Key = key }, new() { Type = 1, Key = key, Flags = 2 }
    ]);
    private static void TypeText(string text) => Send(text.SelectMany(unit => new[]
    {
        new KeyboardInput { Type = 1, Scan = unit, Flags = 4 },
        new KeyboardInput { Type = 1, Scan = unit, Flags = 6 }
    }).ToArray());
    private static void Send(KeyboardInput[] input) =>
        Assert.Equal((uint)input.Length, SendInput((uint)input.Length, input, Marshal.SizeOf<KeyboardInput>()));
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] input, int size);
}
