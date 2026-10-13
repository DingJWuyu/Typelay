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
        bool failed = false;
        try
        {
            Directory.CreateDirectory(directory);
            string page = Path.Combine(directory, "search.html");
            const string initial = "ordinary search without punctuation";
            await File.WriteAllTextAsync(page, """
            <!doctype html><meta charset="utf-8"><title>TypeLay browser input regression</title>
            <style>body{padding:20vh 15vw;font:20px sans-serif}input{width:65vw;padding:14px}</style>
            <label for="query">Search</label>
            <input id="query" type="search" value="ordinary search without punctuation" autofocus>
            <script>
            function reportFocus() {
              document.title = "TypeLay browser input regression " +
                (document.hasFocus() && document.activeElement === document.getElementById("query") ? "focused" : "unfocused");
            }
            window.addEventListener("focus", reportFocus);
            window.addEventListener("blur", reportFocus);
            document.addEventListener("focusin", reportFocus);
            document.addEventListener("focusout", reportFocus);
            reportFocus();
            </script>
            """, TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo(chrome!) { UseShellExecute = false, RedirectStandardError = true };
            start.ArgumentList.Add("--user-data-dir=" + Path.Combine(directory, "profile"));
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add("--disable-search-engine-choice-screen");
            start.ArgumentList.Add("--new-window");
            start.ArgumentList.Add(new Uri(page).AbsoluteUri);
            using var ownedBrowser = new ChildProcess(start);
            var browser = ownedBrowser.Process;
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
                    Assert.False(browser.HasExited, "The isolated browser must remain running.");
                    browser.Refresh();
                    if (browser.MainWindowHandle != 0 && Native.GetForegroundWindow() != browser.MainWindowHandle)
                        Native.SetForegroundWindow(browser.MainWindowHandle);
                    var observed = await input.CaptureAsync(context).WaitAsync(deadline.Token);
                    if (observed.Snapshot is { } source && observed.Focus == FocusMember.Source
                        && (source.Ime == ImeState.Clear ? source.Text : source.PreviewText) == expected)
                        return source;
                    await Task.Delay(40, deadline.Token);
                } while (elapsed.Elapsed < TimeSpan.FromSeconds(6));
                ReportFocus(browser);
                throw new Xunit.Sdk.XunitException("The ordinary page search field was not captured; inspect the broker's fixed stage/rejection trace.");
            }
            var first = await Observe(initial);
            Assert.True(WindowsTarget.TryDecode(first.Target, out var target));
            Assert.Equal((long)browser.MainWindowHandle, target.RootHwnd);
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
            throw;
        }
        finally
        {
            // The owned job closes before profile cleanup, including when the
            // browser's initial process exits before its descendants. Never mask
            // the original acquisition failure with a secondary cleanup failure.
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception error) when (failed && error is IOException or UnauthorizedAccessException)
            {
                output.WriteLine("Isolated browser profile cleanup failed: " + error.GetType().Name);
            }
        }
    }

    private void ReportFocus(Process browser)
    {
        // Test-owned browser only. Log fixed booleans/control type, not window
        // titles, field text, paths, runtime IDs or user metadata. This separates
        // browser/page readiness from the production broker's rejection reason.
        try
        {
            browser.Refresh();
            nint window = browser.MainWindowHandle;
            var focus = AutomationElement.FocusedElement?.Current;
            output.WriteLine($"Browser focus atMs={Environment.TickCount64}: windowReady={window != 0}; "
                + $"foregroundOwned={window != 0 && Native.GetForegroundWindow() == window}; "
                + $"expectedPageTitle={browser.MainWindowTitle.Contains("TypeLay browser input regression", StringComparison.Ordinal)}; "
                + $"domFocusConfirmed={browser.MainWindowTitle.Contains("TypeLay browser input regression focused", StringComparison.Ordinal)}; "
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
