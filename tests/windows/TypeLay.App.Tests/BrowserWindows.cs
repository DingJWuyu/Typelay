using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

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

    public (uint Dpi, int Width, int Height) ClickPageInput(nint window)
    {
        Assert.Contains(Read(), candidate => candidate.Handle == window && candidate.Page && candidate.InputReady);
        Assert.Equal(window, Native.GetForegroundWindow());
        // The fixture centers its input in the renderer viewport. Read its HWND
        // bounds in physical pixels, not CSS pixels or a guessed toolbar offset.
        // This does not query UIA or pre-enable browser accessibility.
        nint previousDpi = SetThreadDpiAwarenessContext(-4); // Per-monitor V2, this test thread only.
        Assert.NotEqual(nint.Zero, previousDpi);
        try
        {
            nint renderer = Assert.Single(ReadRenderers(window));
            Assert.Equal(window, Native.GetAncestor(renderer, 2));
            Assert.True(GetWindowRect(renderer, out var bounds));
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            Assert.True(width >= 100 && height >= 80, "The owned renderer must have a visible content viewport.");
            uint dpi = Native.GetDpiForWindow(renderer);
            Assert.InRange(dpi, 48u, 960u);
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
        Assert.True(renderers.Length <= 1, "new-tab-renderer-ambiguous");
        if (renderers.Length == 0)
            return null;
        stage = "document-unavailable";
        var root = AutomationElement.FromHandle(renderers[0]);
        var documents = root.FindAll(TreeScope.Subtree,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document))
            .Cast<AutomationElement>().Where(IsNewTabDocument).ToArray();
        Assert.True(documents.Length <= 1, "new-tab-document-ambiguous");
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
        Assert.True(candidates.Length <= 1, "new-tab-input-ambiguous");
        if (candidates.Length == 1)
            stage = "ready";
        return candidates.Length == 1
            ? new(candidates[0], candidates[0].GetRuntimeId(), documentId, renderers[0]) : null;
    }

    public void ClickNewTabInput(nint window, NewTabInput input)
    {
        AssertNewTabIdentity(window, input);
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        Assert.NotEqual(nint.Zero, previousDpi);
        try
        {
            var bounds = input.Element.Current.BoundingRectangle;
            Assert.True(!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0, "new-tab-input-bounds");
            Assert.True(GetWindowRect(input.Renderer, out var viewport), "new-tab-renderer-bounds");
            Assert.True(bounds.Left >= viewport.Left && bounds.Right <= viewport.Right
                && bounds.Top >= viewport.Top && bounds.Bottom <= viewport.Bottom, "new-tab-input-outside-content");
            var point = new System.Windows.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Assert.True(AutomationElement.FromPoint(point).GetRuntimeId().SequenceEqual(input.RuntimeId), "new-tab-input-hit-test");
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
        Assert.True(Read().Any(candidate => candidate.Handle == window), "new-tab-window-unavailable");
        Assert.True(Native.GetForegroundWindow() == window, "new-tab-foreground-changed");
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
        Assert.True(Read().Any(candidate => candidate.Handle == window), "new-tab-window-unavailable");
        Assert.True(Native.GetForegroundWindow() == window, "new-tab-foreground-changed");
        Assert.True(ReadRenderers(window).Contains(input.Renderer), "new-tab-renderer-changed");
        Assert.True(input.Element.GetRuntimeId().SequenceEqual(input.RuntimeId), "new-tab-field-changed");
        var document = OwnedDocument(input.Element, window, out _);
        Assert.True(document is not null && document.GetRuntimeId().SequenceEqual(input.DocumentId)
            && IsNewTabDocument(document), "new-tab-document-changed");
        var current = input.Element.Current;
        Assert.True(current.IsEnabled && current.IsKeyboardFocusable && !current.IsOffscreen && !current.IsPassword,
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
        Assert.Equal(window, Native.GetAncestor(hit, 2));
        Assert.True(hit == renderer || IsChild(renderer, hit), "The click must reach the owned content viewport.");
        int desktopLeft = GetSystemMetrics(76), desktopTop = GetSystemMetrics(77);
        int desktopWidth = GetSystemMetrics(78), desktopHeight = GetSystemMetrics(79);
        Assert.True(desktopWidth > 1 && desktopHeight > 1);
        Assert.InRange(x, desktopLeft, desktopLeft + desktopWidth - 1);
        Assert.InRange(y, desktopTop, desktopTop + desktopHeight - 1);
        int absoluteX = (int)(((long)x - desktopLeft) * 65535 / (desktopWidth - 1));
        int absoluteY = (int)(((long)y - desktopTop) * 65535 / (desktopHeight - 1));
        Assert.Equal(window, Native.GetForegroundWindow());
        Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
        MouseInput[] input = [new() { X = absoluteX, Y = absoluteY, Flags = 0xc001 },
            new() { Flags = 2 }, new() { Flags = 4 }];
        uint sent = SendInput((uint)input.Length, input, Marshal.SizeOf<MouseInput>());
        if (sent == 2)
            SendInput(1, [new() { Flags = 4 }], Marshal.SizeOf<MouseInput>());
        Assert.Equal((uint)input.Length, sent);
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

    public async Task StopAsync(ChildProcess owner)
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
        }
        finally
        {
            foreach (var process in _processes.Values)
                process.Dispose();
            _processes.Clear();
        }
    }

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
