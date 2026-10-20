using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using TypeLay.App;

namespace TypeLay.Windows.Testing;

// Tracks only the process family launched for this test. MainWindowHandle is
// not a browser identity: a launcher or another member may own the page window.
internal sealed class BrowserWindows
{
    private readonly Dictionary<int, Process> _processes = new();
    private readonly DateTime _startedAt;
    private readonly string _title;
    public int TotalWindowCount
    {
        get; private set;
    }

    public BrowserWindows(Process root, string title)
    {
        _startedAt = root.StartTime.ToUniversalTime();
        _title = title;
        var owned = Process.GetProcessById(root.Id);
        _ = owned.Handle; // Retain this process identity through cleanup.
        _processes.Add(owned.Id, owned);
    }

    public (nint Handle, bool Page, bool InputReady, bool Focused, string Title)[] Read()
    {
        Discover();
        TotalWindowCount = 0;
        var windows = new List<(nint, bool, bool, bool, string)>();
        EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out uint processId);
            if (!_processes.TryGetValue((int)processId, out var owner) || owner.HasExited)
                return true;
            TotalWindowCount++;
            if (!IsWindowVisible(window))
                return true;
            var title = new StringBuilder(512);
            GetWindowTextW(window, title, title.Capacity);
            windows.Add((window, title.ToString().Contains(_title, StringComparison.Ordinal),
                title.ToString().Contains(" ready=true ", StringComparison.Ordinal),
                title.ToString().Contains(_title + " focused", StringComparison.Ordinal), title.ToString()));
            return true;
        }, 0);
        return windows.ToArray();
    }

    public int ProcessCount => _processes.Count;

    public string DescribeProcesses()
    {
        int alive = 0, exited = 0, unknown = 0, index = 0;
        var states = new List<string>();
        // Ordinals refer only to retained fixture identities, never current PIDs
        // or command lines. Limit detail while still counting every held process.
        foreach (var process in _processes.Values)
        {
            string state;
            try
            {
                if (process.HasExited)
                {
                    state = $"exit:0x{process.ExitCode:x8}";
                    exited++;
                }
                else
                {
                    state = "alive";
                    alive++;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                state = "unknown";
                unknown++;
            }
            if (index < 16)
                states.Add($"{index}:{state}");
            index++;
        }
        return $"aliveProcesses={alive}; exitedProcesses={exited}; unknownProcesses={unknown}; "
            + $"processStates=[{string.Join(',', states)}]; omittedProcessStates={Math.Max(0, index - states.Count)}";
    }

    public async Task SaveDiagnosticsAsync(string folder, Action<string> log)
    {
        // Only this disposable profile's held process identities are eligible.
        // PrintWindow requests the owned HWND, never another window's screen pixels.
        var windows = Read().Take(4).ToArray();
        for (int index = 0; index < windows.Length; index++)
        {
            var window = windows[index];
            var name = new StringBuilder(128);
            GetClassNameW(window.Handle, name, name.Capacity);
            log($"Owned browser window {index}: title={JsonSerializer.Serialize(window.Title)}; class={name}; foreground={Native.GetForegroundWindow() == window.Handle}");
            Native.GetWindowThreadProcessId(window.Handle, out uint processId);
            if (!_processes.TryGetValue((int)processId, out var owner) || owner.HasExited)
                continue;
            string path = Path.Combine(folder, $"owned-window-{index}.png");
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                byte[]? png = await Task.Run(() =>
                {
                    nint previousDpi = SetThreadDpiAwarenessContext(-4);
                    if (previousDpi == 0)
                        return null;
                    try
                    {
                        if (!GetWindowRect(window.Handle, out var bounds))
                            return null;
                        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
                        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > 16_000_000)
                            return null;
                        using var bitmap = new Bitmap(width, height);
                        using var graphics = Graphics.FromImage(bitmap);
                        Native.GetWindowThreadProcessId(window.Handle, out uint beforePrintProcess);
                        if (beforePrintProcess != processId || owner.HasExited || deadline.IsCancellationRequested)
                            return null;
                        nint dc = graphics.GetHdc();
                        bool printed;
                        try
                        {
                            printed = PrintWindow(window.Handle, dc, 2);
                        }
                        finally { graphics.ReleaseHdc(dc); }
                        Native.GetWindowThreadProcessId(window.Handle, out uint currentProcess);
                        if (!printed || currentProcess != processId || owner.HasExited || deadline.IsCancellationRequested)
                            return null;
                        using var buffer = new MemoryStream();
                        bitmap.Save(buffer, ImageFormat.Png);
                        return buffer.ToArray();
                    }
                    finally { SetThreadDpiAwarenessContext(previousDpi); }
                }).WaitAsync(deadline.Token);
                // Timed-out work can only discard its bytes. The awaiting test
                // alone publishes an image after successful bounded completion.
                if (png is not null)
                    await File.WriteAllBytesAsync(path, png);
                log($"Owned browser window {index}: screenshotSaved={png is not null}");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log("Owned browser screenshot unavailable: " + error.GetType().Name);
            }
        }
    }

    public static async Task<ProcessStartInfo> PageStartInfoAsync(string directory, string title, string initial,
        string logPath, Action<string> log, CancellationToken cancellation)
    {
        string page = Path.Combine(directory, "search.html");
        await File.WriteAllTextAsync(page, $$"""
        <!doctype html><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>body{margin:0;height:100vh;display:grid;place-items:center;font:20px sans-serif}input{width:65vw;padding:14px}</style>
        <input id="query" type="search" aria-label="Search" value="{{WebUtility.HtmlEncode(initial)}}" autofocus>
        <script>
        let inputClicked = false;
        document.addEventListener("pointerdown", event => {
          inputClicked = event.isTrusted && event.target === document.getElementById("query");
        });
        function reportFocus() {
          const input = document.getElementById("query");
          const bounds = input.getBoundingClientRect();
          const inputReady = bounds.width > 0 && bounds.height > 0 &&
            document.elementFromPoint(window.innerWidth / 2, window.innerHeight / 2) === input;
          const pageFocused = document.hasFocus();
          const inputFocused = document.activeElement === input;
          const title = {{JsonSerializer.Serialize(title)}} + " " + (pageFocused && inputFocused ? "focused" : "unfocused") +
            " ready=" + inputReady + " page=" + pageFocused + " input=" + inputFocused + " click=" + inputClicked;
          if (document.title !== title) document.title = title;
          requestAnimationFrame(reportFocus);
        }
        // Sample live DOM state without changing focus or enabling UIA.
        requestAnimationFrame(reportFocus);
        </script>
        """, cancellation);
        return ChromeStartInfo(directory, new Uri(page).AbsoluteUri, logPath, log);
    }

    public async Task<nint> PreparePageAsync(Action<string> log, CancellationToken cancellation)
    {
        nint window = 0;
        bool clicked = false;
        var startup = Stopwatch.StartNew();
        string? previousStartupState = null;
        int startupChanges = 0;
        void RecordStartupState(int visibleWindows, int pageWindows, bool final = false)
        {
            string state = $"totalOwnedWindows={TotalWindowCount}; visibleOwnedWindows={visibleWindows}; "
                + $"ownedPages={pageWindows}; {DescribeProcesses()}";
            if (!final && (state == previousStartupState || startupChanges >= 12))
                return;
            previousStartupState = state;
            if (!final)
                startupChanges++;
            log($"Browser startup {(final ? "final" : "change")} elapsedMs={startup.ElapsedMilliseconds}: {state}");
        }
        // Cold preparation shares the caller's total cancellation budget.
        do
        {
            var observed = Read();
            var pages = observed.Where(candidate => candidate.Page).ToArray();
            RecordStartupState(observed.Length, pages.Length);
            Require(pages.Length <= 1, "page-window-ambiguous");
            if (pages.Length == 1)
            {
                window = pages[0].Handle;
                if (Native.GetForegroundWindow() != window)
                    Native.SetForegroundWindow(window);
                if (pages[0].InputReady && Native.GetForegroundWindow() == window && !clicked)
                {
                    var geometry = ClickPageInput(window);
                    clicked = true;
                    log($"Clicked owned page input: dpi={geometry.Dpi}; viewportWidth={geometry.Width}; viewportHeight={geometry.Height}");
                }
                if (clicked && Native.GetForegroundWindow() == window && pages[0].Focused)
                    break;
            }
            await Task.Delay(40, cancellation);
        } while (true);
        var ready = Read();
        RecordStartupState(ready.Length, ready.Count(candidate => candidate.Page), final: true);
        log($"Browser startup atMs={WindowsInterruptClock.NowMs}: ownedProcesses={ProcessCount}; "
            + $"visibleOwnedWindows={ready.Length}; ownedPages={ready.Count(candidate => candidate.Page)}; "
            + $"foregroundOwned={window != 0 && Native.GetForegroundWindow() == window}; "
            + $"domFocusConfirmed={ready.Any(candidate => candidate.Handle == window && candidate.Focused)}; "
            + $"inputClicked={clicked}");
        Require(clicked && window != 0 && Native.GetForegroundWindow() == window
            && ready.Any(candidate => candidate.Handle == window && candidate.Focused), "page-dom-focus-unconfirmed");
        return window;
    }

    // Called only after the cold-page model preview is already visible. This
    // independent UIA check must never become a discovery/activation prerequisite.
    public void ConfirmPageSource(nint window, string elementToken, string pageUri, string expected)
    {
        Require(Read().Any(candidate => candidate.Handle == window && candidate.Page && candidate.Focused)
            && Native.GetForegroundWindow() == window, "page-focus-changed");
        var focused = AutomationElement.FocusedElement;
        Require(focused is not null && string.Join('.', focused.GetRuntimeId()) == elementToken
            && focused.Current.HasKeyboardFocus, "page-field-changed");
        var document = OwnedDocument(focused!, window, out bool ownedRoot);
        Require(ownedRoot && document is not null
            && document.TryGetCurrentPattern(ValuePattern.Pattern, out var documentPattern)
            && ((ValuePattern)documentPattern).Current.Value == pageUri, "page-document-changed");
        Require(focused!.TryGetCurrentPattern(ValuePattern.Pattern, out var inputPattern)
            && ((ValuePattern)inputPattern).Current.Value == expected, "page-source-changed");
    }

    public (uint Dpi, int Width, int Height) ClickPageInput(nint window)
    {
        Require(Read().Any(candidate => candidate.Handle == window && candidate.Page && candidate.InputReady), "page-input-not-ready");
        Require(window == Native.GetForegroundWindow(), "browser-foreground-changed");
        // The fixture centers its input in the renderer viewport. Read its HWND
        // bounds in physical pixels, not CSS pixels or a guessed toolbar offset.
        // This does not query UIA or pre-enable browser accessibility.
        nint previousDpi = SetThreadDpiAwarenessContext(-4); // Per-monitor V2, this test thread only.
        Require(previousDpi != 0, "browser-dpi-context");
        try
        {
            var renderers = ReadRenderers(window);
            Require(renderers.Length == 1, "page-renderer-ambiguous");
            nint renderer = renderers[0];
            Require(window == Native.GetAncestor(renderer, 2), "browser-renderer-root");
            Require(GetWindowRect(renderer, out var bounds), "browser-renderer-bounds");
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            Require(width >= 100 && height >= 80, "browser-renderer-viewport");
            uint dpi = Native.GetDpiForWindow(renderer);
            Require(dpi is >= 48u and <= 960u, "browser-dpi-range");
            int x = bounds.Left + width / 2, y = bounds.Top + height / 2;
            ClickContentPoint(window, renderer, x, y);
            return (dpi, width, height);
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    // UIA is intentionally primed for this internal-page fixture only. Document
    // ValuePattern is Chromium's URL metadata, never the input or suggestions.
    internal sealed record NewTabInput(AutomationElement Element, int[] RuntimeId, int[] DocumentId, nint Renderer);

    public NewTabInput? FindNewTabInput(nint window, out string stage)
    {
        stage = "renderer-unavailable";
        var renderers = ReadRenderers(window);
        Require(renderers.Length <= 1, "new-tab-renderer-ambiguous");
        if (renderers.Length == 0)
            return null;
        stage = "document-unavailable";
        var root = AutomationElement.FromHandle(renderers[0]);
        var documents = root.FindAll(TreeScope.Subtree,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document))
            .Cast<AutomationElement>().Where(IsNewTabDocument).ToArray();
        Require(documents.Length <= 1, "new-tab-document-ambiguous");
        if (documents.Length == 0)
            return null;
        stage = "input-unavailable";
        var documentId = documents[0].GetRuntimeId();
        var candidates = documents[0].FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)))
            .Cast<AutomationElement>().Where(element =>
            {
                var current = element.Current;
                return current.IsEnabled && current.IsKeyboardFocusable && !current.IsOffscreen && !current.IsPassword
                    && !current.BoundingRectangle.IsEmpty && current.BoundingRectangle.Width > 0 && current.BoundingRectangle.Height > 0
                    && OwnedDocument(element, window, out _)?.GetRuntimeId().SequenceEqual(documentId) == true;
            }).ToArray();
        Require(candidates.Length <= 1, "new-tab-input-ambiguous");
        if (candidates.Length == 1)
            stage = "ready";
        return candidates.Length == 1
            ? new(candidates[0], candidates[0].GetRuntimeId(), documentId, renderers[0]) : null;
    }

    public void ClickNewTabInput(nint window, NewTabInput input)
    {
        AssertNewTabIdentity(window, input);
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        Require(previousDpi != 0, "browser-dpi-context");
        try
        {
            var bounds = input.Element.Current.BoundingRectangle;
            Require(!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0, "new-tab-input-bounds");
            Require(GetWindowRect(input.Renderer, out var viewport), "new-tab-renderer-bounds");
            Require(bounds.Left >= viewport.Left && bounds.Right <= viewport.Right
                && bounds.Top >= viewport.Top && bounds.Bottom <= viewport.Bottom, "new-tab-input-outside-content");
            var point = new System.Windows.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Require(AutomationElement.FromPoint(point).GetRuntimeId().SequenceEqual(input.RuntimeId), "new-tab-input-hit-test");
            AssertNewTabIdentity(window, input);
            ClickContentPoint(window, input.Renderer, (int)point.X, (int)point.Y);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    public string NewTabFocus(nint window, NewTabInput input)
    {
        Require(Read().Any(candidate => candidate.Handle == window), "new-tab-window-unavailable");
        Require(Native.GetForegroundWindow() == window, "new-tab-foreground-changed");
        var focused = AutomationElement.FocusedElement;
        if (focused is null)
            return "focus-unavailable";
        var document = OwnedDocument(focused, window, out bool ownedRoot);
        if (!ownedRoot)
            return "focus-unavailable";
        if (document is null)
            return "focus-transfer";
        AssertNewTabIdentity(window, input);
        if (document.GetRuntimeId().SequenceEqual(input.DocumentId)
            && focused.GetRuntimeId().SequenceEqual(input.RuntimeId) && focused.Current.HasKeyboardFocus)
            return "source";
        return "field-changed";
    }

    private void AssertNewTabIdentity(nint window, NewTabInput input)
    {
        Require(Read().Any(candidate => candidate.Handle == window), "new-tab-window-unavailable");
        Require(Native.GetForegroundWindow() == window, "new-tab-foreground-changed");
        Require(ReadRenderers(window).Contains(input.Renderer), "new-tab-renderer-changed");
        Require(input.Element.GetRuntimeId().SequenceEqual(input.RuntimeId), "new-tab-field-changed");
        var document = OwnedDocument(input.Element, window, out _);
        Require(document is not null && document.GetRuntimeId().SequenceEqual(input.DocumentId)
            && IsNewTabDocument(document), "new-tab-document-changed");
        var current = input.Element.Current;
        Require(current.IsEnabled && current.IsKeyboardFocusable && !current.IsOffscreen && !current.IsPassword,
            "new-tab-input-unavailable");
    }

    private AutomationElement? OwnedDocument(AutomationElement element, nint window, out bool ownedRoot)
    {
        ownedRoot = false;
        AutomationElement? document = null;
        for (int depth = 0; element is not null && depth < 28; depth++, element = TreeWalker.RawViewWalker.GetParent(element))
        {
            var current = element.Current;
            if (!_processes.TryGetValue(current.ProcessId, out var owner) || owner.HasExited)
                return null;
            if (document is null && current.ControlType == ControlType.Document)
                document = element;
            if ((nint)current.NativeWindowHandle == window)
            {
                ownedRoot = true;
                return document;
            }
        }
        return null;
    }

    private static bool IsNewTabDocument(AutomationElement element) =>
        element.Current.ControlType == ControlType.Document
        && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
        && ((ValuePattern)pattern).Current.Value is "chrome://new-tab-page/" or "chrome://newtab/";

    private nint[] ReadRenderers(nint window)
    {
        var renderers = new List<nint>();
        EnumChildWindows(window, (child, _) =>
        {
            Native.GetWindowThreadProcessId(child, out uint processId);
            var name = new StringBuilder(128);
            GetClassNameW(child, name, name.Capacity);
            if (IsWindowVisible(child) && _processes.TryGetValue((int)processId, out var owner) && !owner.HasExited
                && name.ToString() == "Chrome_RenderWidgetHostHWND")
                renderers.Add(child);
            return true;
        }, 0);
        return renderers.ToArray();
    }

    private static void ClickContentPoint(nint window, nint renderer, int x, int y)
    {
        nint hit = WindowFromPoint(new()
        {
            X = x,
            Y = y
        });
        Require(window == Native.GetAncestor(hit, 2), "browser-click-root");
        Require(hit == renderer || IsChild(renderer, hit), "browser-click-outside-content");
        int desktopLeft = GetSystemMetrics(76), desktopTop = GetSystemMetrics(77);
        int desktopWidth = GetSystemMetrics(78), desktopHeight = GetSystemMetrics(79);
        Require(desktopWidth > 1 && desktopHeight > 1, "browser-desktop-bounds");
        Require(x >= desktopLeft && x <= desktopLeft + desktopWidth - 1, "browser-click-x");
        Require(y >= desktopTop && y <= desktopTop + desktopHeight - 1, "browser-click-y");
        int absoluteX = (int)(((long)x - desktopLeft) * 65535 / (desktopWidth - 1));
        int absoluteY = (int)(((long)y - desktopTop) * 65535 / (desktopHeight - 1));
        Require(window == Native.GetForegroundWindow(), "browser-foreground-changed");
        Require((Native.GetAsyncKeyState(1) & 0x8000) == 0, "browser-mouse-already-down");
        MouseInput[] input = [new() { X = absoluteX, Y = absoluteY, Flags = 0xc001 },
            new() { Flags = 2 }, new() { Flags = 4 }];
        uint sent = SendInput((uint)input.Length, input, Marshal.SizeOf<MouseInput>());
        if (sent == 2)
            SendInput(1, [new MouseInput { Flags = 4 }], Marshal.SizeOf<MouseInput>());
        Require((uint)input.Length == sent, "browser-click-injection-incomplete");
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
            throw new Failure(code);
    }

    internal sealed class Failure(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }

    public static ProcessStartInfo NewTabStartInfo(string directory, Action<string> log)
    {
        log("New tab fixture uses UIA metadata to locate its input; browser accessibility is primed before capture.");
        return ChromeStartInfo(directory, "chrome://newtab", Path.Combine(directory, "chrome.log"), log);
    }

    private static ProcessStartInfo ChromeStartInfo(string directory, string destination, string logPath, Action<string> log)
    {
        string? chrome = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Google", "Chrome", "Application", "chrome.exe"))
            .FirstOrDefault(File.Exists);
        Require(chrome is not null, "installed-chrome-required");
        log("Installed Chrome version: " + FileVersionInfo.GetVersionInfo(chrome!).ProductVersion);
        var start = new ProcessStartInfo(chrome!) { UseShellExecute = false, RedirectStandardError = true };
        start.Environment["CHROME_LOG_FILE"] = logPath;
        start.ArgumentList.Add("--enable-logging");
        start.ArgumentList.Add("--user-data-dir=" + Path.Combine(directory, "profile"));
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add("--disable-search-engine-choice-screen");
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add(destination);
        return start;
    }

    public async Task<(nint Window, NewTabInput Input)> PrepareNewTabAsync(Action<string> log, CancellationToken cancellation)
    {
        string stage = "startup", focus = "unobserved";
        try
        {
            nint window = 0;
            NewTabInput? field = null;
            string? previousStage = null;
            int startupChanges = 0;
            do
            {
                stage = "window-unavailable";
                var visible = Read();
                Require(visible.Length <= 1, "new-tab-window-ambiguous");
                if (visible.Length == 1)
                {
                    window = visible[0].Handle;
                    if (Native.GetForegroundWindow() != window)
                        Native.SetForegroundWindow(window);
                    stage = "foreground-unavailable";
                    if (Native.GetForegroundWindow() == window)
                        field = FindNewTabInput(window, out stage);
                }
                if (stage != previousStage && startupChanges < 12)
                {
                    previousStage = stage;
                    startupChanges++;
                    log("New tab prerequisite: phase=" + stage);
                }
                if (field is null)
                    await Task.Delay(40, cancellation);
            } while (field is null);
            log("New tab prerequisite: internalDocumentConfirmed=True; uniqueInput=True");
            stage = "click";
            ClickNewTabInput(window, field);
            // SendInput insertion is not an observed DOM click. Wait for the
            // exact UIA input focus, never adopt a toolbar field after transfer.
            while ((focus = NewTabFocus(window, field)) != "source")
                await Task.Delay(40, cancellation);
            log("New tab focus: phase=after-click; source=True; inputClickInserted=True");
            return (window, field);
        }
        catch
        {
            log($"New tab prerequisite failed: phase={stage}; focus={focus}");
            throw;
        }
    }

    private void Discover()
    {
        DateTime capturedBefore = DateTime.UtcNow;
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "browser-process-snapshot");
        try
        {
            var entries = new List<ProcessEntry>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32FirstW(snapshot, ref entry))
                do
                {
                    entries.Add(entry);
                } while (Process32NextW(snapshot, ref entry));
            bool added;
            do
            {
                added = false;
                foreach (var child in entries)
                {
                    if (_processes.ContainsKey((int)child.ProcessId) || !_processes.ContainsKey((int)child.ParentProcessId))
                        continue;
                    Process? process = null;
                    try
                    {
                        process = Process.GetProcessById((int)child.ProcessId);
                        _ = process.Handle;
                        DateTime createdAt = process.StartTime.ToUniversalTime();
                        // Reject a PID reused after this snapshot. A freshly
                        // started owned child can be discovered on the next read.
                        if (createdAt < _startedAt || createdAt >= capturedBefore)
                            continue;
                        _processes.Add(process.Id, process);
                        process = null;
                        added = true;
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
                    finally { process?.Dispose(); }
                }
            } while (added);
        }
        finally { Native.CloseHandle(snapshot); }
    }

    public async Task StopAsync(ChildProcess owner, Action<string> log)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            // Retain exact process identities before terminating the owned job.
            // Also await held identities outside that job, without killing them.
            // A discovery failure must still terminate and close the owned job.
            try
            {
                Discover();
            }
            finally
            {
                await owner.TerminateAndWaitAsync(deadline.Token);
            }
            await Task.WhenAll(_processes.Values.Select(process => process.WaitForExitAsync(deadline.Token)));
            // WaitForExitAsync may return on HasExited's early exit-code check.
            // WaitForExit(0) instead tests the kernel signal on our retained
            // handle, without reopening a PID or waiting for redirected output.
            // https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessManager.Windows.cs#L259-L291
            int pending = _processes.Values.Count(process => !process.WaitForExit(0));
            log($"Owned browser exit proof: retainedProcesses={_processes.Count}; pendingKernelSignals={pending}");
            while (pending != 0)
            {
                await Task.Delay(10, deadline.Token);
                pending = _processes.Values.Count(process => !process.WaitForExit(0));
            }
            log($"Owned browser exit proof: kernelSignaledProcesses={_processes.Count}");
        }
        finally
        {
            foreach (var process in _processes.Values)
                process.Dispose();
            _processes.Clear();
            owner.Dispose();
        }
    }

    public static void Keys(ushort key) => Send([
        new() { Type = 1, Key = key }, new() { Type = 1, Key = key, Flags = 2 }
    ]);
    public static void TypeText(string text) => Send(text.SelectMany(unit => new[]
    {
        new KeyboardInput { Type = 1, Scan = unit, Flags = 4 },
        new KeyboardInput { Type = 1, Scan = unit, Flags = 6 }
    }).ToArray());
    private static void Send(KeyboardInput[] input) =>
        Require((uint)input.Length == SendInput((uint)input.Length, input, Marshal.SizeOf<KeyboardInput>()), "browser-key-injection-incomplete");
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    private delegate bool EnumWindow(nint window, nint data);
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int X;
        [FieldOffset(12)] public int Y;
        [FieldOffset(20)] public uint Flags;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Native.Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint deviceContext, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Native.Point point);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, MouseInput[] input, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int maximum);
}
