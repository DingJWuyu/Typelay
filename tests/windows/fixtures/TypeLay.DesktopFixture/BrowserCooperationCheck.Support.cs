using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Testing;

internal static partial class BrowserCooperationCheck
{
    private static async Task CopyModelAsync(string directory, string destination, CancellationToken ct)
    {
        string previous = Path.Combine(Path.GetDirectoryName(directory)!, "continuous-translation");
        string source = Path.Combine(previous, "data");
        Require(File.Exists(Path.Combine(previous, "result.json")) && Directory.Exists(source), "same-run-model-artifacts-missing");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(previous, "result.json"), ct));
        Require(result.RootElement.GetProperty("modelReady").GetBoolean()
            && result.RootElement.GetProperty("modelSetup").GetProperty("outcome").GetString() == "ready", "same-run-model-not-verified");
        string pointer = Path.Combine(source, "components.json");
        Require(File.Exists(pointer), "model-generation-pointer-missing");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(pointer, ct));
        Require(manifest.RootElement.GetProperty("format").GetInt32() == 1, "model-pointer-format");
        string active = manifest.RootElement.GetProperty("active").GetString() ?? "";
        string? previousId = manifest.RootElement.GetProperty("previous").GetString();
        static bool Valid(string value) => value == "legacy" || value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        Require(Valid(active) && (previousId is null || Valid(previousId) && previousId != active), "model-pointer-generation");
        foreach (string id in new[] { active, previousId }.OfType<string>())
        {
            string root = id == "legacy" ? source : Path.Combine(source, "components", id);
            string target = id == "legacy" ? destination : Path.Combine(destination, "components", id);
            if (id != active && !Directory.Exists(Path.Combine(root, "runtime")) && !Directory.Exists(Path.Combine(root, "models")))
                continue;
            Require(File.Exists(Path.Combine(root, "runtime", "python", "tools", "python.exe"))
                && File.Exists(Path.Combine(root, "models", "installed.json")), "verified-model-generation-incomplete");
            foreach (string component in new[] { "runtime", "models" })
                await CopyTreeAsync(Path.Combine(root, component), Path.Combine(target, component), ct);
            byte[] original = await File.ReadAllBytesAsync(Path.Combine(root, "models", "installed.json"), ct);
            byte[] copied = await File.ReadAllBytesAsync(Path.Combine(target, "models", "installed.json"), ct);
            Require(SHA256.HashData(original).SequenceEqual(SHA256.HashData(copied)), "model-inventory-copy-changed");
        }
        // Preserve the actual store's generation pointer and untouched model inventory.
        // The shipped loader must still validate and load it before any preview succeeds.
        await File.WriteAllBytesAsync(Path.Combine(destination, "components.json"), await File.ReadAllBytesAsync(pointer, ct), ct);
    }
    private static void CheckTree(string root)
    {
        Require((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "owned-path-is-link");
        foreach (string item in Directory.EnumerateFileSystemEntries(root))
        {
            Require((File.GetAttributes(item) & FileAttributes.ReparsePoint) == 0, "owned-path-is-link");
            if (Directory.Exists(item))
                CheckTree(item);
        }
    }
    private static async Task CopyTreeAsync(string source, string destination, CancellationToken ct)
    {
        Require((File.GetAttributes(source) & FileAttributes.ReparsePoint) == 0, "model-source-is-link");
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            Require((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0, "model-source-is-link");
            string target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
                await CopyTreeAsync(entry, target, ct);
            else
            {
                await using var input = File.OpenRead(entry);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, ct);
            }
        }
    }
    private static async Task FocusPageAsync(Cdp cdp, BrowserWindows windows, CancellationToken ct)
    {
        await cdp.PageCallAsync("Page.bringToFront", new
        {
        }, ct);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var matches = windows.Read().Where(window => window.Page).ToArray();
            Require(matches.Length <= 1, "owned-page-window-ambiguous");
            if (matches.Length == 1)
            {
                nint window = matches[0].Handle;
                if (Native.GetForegroundWindow() != window)
                    Native.SetForegroundWindow(window);
                if (Native.GetForegroundWindow() == window && (await cdp.StateAsync("", ct)).PageFocused)
                    return;
            }
            await Task.Delay(40, ct);
        }
    }
    private static async Task<System.Windows.Rect> FocusCommandFieldAsync(Cdp cdp, BrowserWindows windows, AutomationElement settings, CancellationToken ct)
    {
        await FocusPageAsync(cdp, windows, ct);
        var pages = windows.Read().Where(window => window.Page).ToArray();
        Require(pages.Length == 1, "owned-page-window-unavailable");
        nint page = pages[0].Handle;
        var browserBounds = WindowBounds(page);
        var settingsBounds = WindowBounds(settings.Current.NativeWindowHandle);
        var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        Require(Native.GetMonitorInfoW(Native.MonitorFromWindow(page, 2), ref monitor), "source-monitor-unavailable");
        var metrics = await cdp.EvaluateAsync("({width:innerWidth,height:innerHeight,scale:devicePixelRatio,left:screen.availLeft,top:screen.availTop,screenWidth:screen.availWidth,screenHeight:screen.availHeight})", ct);
        double scale = Native.GetDpiForWindow(page) / 96.0;
        Require(scale > 0 && Math.Abs(scale - metrics.GetProperty("scale").GetDouble()) < 0.001
            && Math.Abs(monitor.Work.Right - monitor.Work.Left - metrics.GetProperty("screenWidth").GetDouble() * scale) <= 2
            && Math.Abs(monitor.Work.Bottom - monitor.Work.Top - metrics.GetProperty("screenHeight").GetDouble() * scale) <= 2,
            "source-screen-scale-mismatch");
        double width = Math.Min(metrics.GetProperty("width").GetDouble(), (settingsBounds.Left - browserBounds.Left - 32) / scale);
        Require(width >= 100, "source-command-layout-unavailable");
        // Only this controlled page's layout changes. Chrome keeps its normal
        // window; both real fields fit in the exposed space left of Settings.
        await cdp.EvaluateAsync($"document.body.style.cssText='margin:0;padding:8px;box-sizing:border-box;font:20px sans-serif;width:'+{JsonSerializer.Serialize(width)}+'px';for(const f of [a,b]){{f.style.boxSizing='border-box';f.style.width='100%';f.style.margin='8px 0';f.style.padding='8px';}}true", ct);
        var point = await cdp.EvaluateAsync("(()=>{const r=a.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2;return {x,y,left:r.left,top:r.top,width:r.width,height:r.height,visible:a.isConnected&&r.width>0&&r.height>0&&r.left>=0&&r.top>=0&&r.right<=innerWidth&&r.bottom<=innerHeight&&document.elementFromPoint(x,y)===a};})()", ct);
        Require(point.GetProperty("visible").GetBoolean(), "source-field-not-visible");
        double x = point.GetProperty("x").GetDouble(), y = point.GetProperty("y").GetDouble();
        // Observe the original click's browser-computed screen/client pair.
        // No Chrome-internal child HWND or guessed toolbar height is needed.
        await cdp.EvaluateAsync("globalThis.__cooperationPointer=null;a.addEventListener('mousedown',e=>{globalThis.__cooperationPointer={trusted:e.isTrusted,target:e.target===a,clientX:e.clientX,clientY:e.clientY,screenX:e.screenX,screenY:e.screenY};},{once:true});true", ct);
        foreach (string type in new[] { "mousePressed", "mouseReleased" })
            await cdp.PageCallAsync("Input.dispatchMouseEvent", new
            {
                type,
                x,
                y,
                button = "left",
                clickCount = 1
            }, ct);
        var click = await cdp.EvaluateAsync("(()=>{const p=globalThis.__cooperationPointer;delete globalThis.__cooperationPointer;return p;})()", ct);
        Require(click.ValueKind == JsonValueKind.Object && click.GetProperty("trusted").GetBoolean()
            && click.GetProperty("target").GetBoolean()
            && Math.Abs(click.GetProperty("clientX").GetDouble() - x) <= 1
            && Math.Abs(click.GetProperty("clientY").GetDouble() - y) <= 1, "source-click-unobserved");
        double screenX = monitor.Work.Left + (click.GetProperty("screenX").GetDouble() - metrics.GetProperty("left").GetDouble()) * scale;
        double screenY = monitor.Work.Top + (click.GetProperty("screenY").GetDouble() - metrics.GetProperty("top").GetDouble()) * scale;
        var viewport = new System.Windows.Rect(screenX - click.GetProperty("clientX").GetDouble() * scale,
            screenY - click.GetProperty("clientY").GetDouble() * scale,
            metrics.GetProperty("width").GetDouble() * scale, metrics.GetProperty("height").GetDouble() * scale);
        var field = new System.Windows.Rect(viewport.Left + point.GetProperty("left").GetDouble() * scale,
            viewport.Top + point.GetProperty("top").GetDouble() * scale,
            point.GetProperty("width").GetDouble() * scale, point.GetProperty("height").GetDouble() * scale);
        Require(viewport.Contains(field) && browserBounds.Contains(field) && !settingsBounds.IntersectsWith(field)
            && Native.GetForegroundWindow() == page
            && Native.GetAncestor(WindowFromPoint(new Native.Point { X = (int)screenX, Y = (int)screenY }), 2) == page,
            "source-field-obscured");
        var state = await cdp.StateAsync("", ct);
        Require(state.PageFocused && state.Focus == "a", "source-focus-precondition");
        return field;
    }
    private static async Task WaitPublicCommandReadyAsync(AutomationElement settings, BrowserWindows windows, System.Windows.Rect source, CancellationToken ct)
    {
        // The existing settings page remains visible beside the source. Read its
        // actual status text before typing/selection, never an internal ACK.
        const string ready = "浏览器快捷键已就绪。";
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                bool visible = await Task.Run(() =>
                {
                    var pages = windows.Read().Where(window => window.Page).ToArray();
                    Require(pages.Length == 1, "owned-page-window-unavailable");
                    Require(Native.GetForegroundWindow() == pages[0].Handle, "public-command-source-not-foreground");
                    nint settingsHandle = settings.Current.NativeWindowHandle;
                    var settingsBounds = WindowBounds(settingsHandle);
                    var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
                    Require(Native.GetMonitorInfoW(Native.MonitorFromWindow(settingsHandle, 2), ref monitor), "settings-monitor-unavailable");
                    var work = new System.Windows.Rect(monitor.Work.Left, monitor.Work.Top,
                        monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
                    Require(!settings.Current.IsOffscreen && work.Contains(settingsBounds) && work.Contains(source)
                        && WindowBounds(pages[0].Handle).Contains(source) && !settingsBounds.IntersectsWith(source)
                        && Native.GetAncestor(WindowFromPoint(new Native.Point
                        {
                            X = (int)(source.Left + source.Width / 2),
                            Y = (int)(source.Top + source.Height / 2)
                        }), 2) == pages[0].Handle,
                        "settings-command-layout-unavailable");
                    var status = settings.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                        new PropertyCondition(AutomationElement.NameProperty, ready),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
                    if (status is null)
                        return false;
                    var viewport = settings.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ClassNameProperty, "ScrollViewer"));
                    var bounds = status.Current.BoundingRectangle;
                    return viewport is not null && !viewport.Current.IsOffscreen && !bounds.IsEmpty
                        && bounds.Width > 0 && bounds.Height > 0 && settingsBounds.Contains(bounds)
                        && viewport.Current.BoundingRectangle.Contains(bounds);
                }, ct).WaitAsync(ct);
                if (visible)
                    return;
                await Task.Delay(40, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new Failure("public-command-ready-unavailable");
        }
    }
    private static System.Windows.Rect WindowBounds(nint window)
    {
        Require(GetWindowRect(window, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top,
            "owned-window-bounds-unavailable");
        return new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }
    private static async Task PrepareInputAsync(Cdp cdp, BrowserWindows windows, string prefix, string suffix,
        bool selectSource, CancellationToken ct)
    {
        await FocusPageAsync(cdp, windows, ct);
        await cdp.EvaluateAsync($"a.value={JsonSerializer.Serialize(prefix + suffix)};b.value={JsonSerializer.Serialize(Other)};a.focus();a.setSelectionRange({prefix.Length},{prefix.Length});resetCounts();true", ct);
        BrowserWindows.TypeText(Source);
        var state = await WaitStateAsync(cdp, s => s.A == prefix + Source + suffix && s.Focus == "a" && s.PageFocused, ct);
        Require(state.Replacements == 0 && state.Submits == 0 && state.B == Other, "source-input-precondition");
        if (selectSource)
            await cdp.EvaluateAsync($"a.setSelectionRange({prefix.Length},{prefix.Length + Source.Length});true", ct);
        state = await cdp.StateAsync("", ct);
        Require(state.AStart == prefix.Length + (selectSource ? 0 : Source.Length)
            && state.AEnd == prefix.Length + Source.Length, "source-selection-precondition");
    }
    private static async Task<PageState> WaitStateAsync(Cdp cdp, Func<PageState, bool> ready, CancellationToken ct)
    {
        while (true)
        {
            var state = await cdp.StateAsync("", ct);
            if (ready(state))
                return state;
            await Task.Delay(20, ct);
        }
    }
    private static async Task<Preview> WaitPreviewAsync(Cdp cdp, CancellationToken ct)
    {
        while (true)
        {
            var preview = await cdp.PreviewAsync(ct);
            if (preview.Visible && preview.Text.Length > 0 && preview.Text != Source
                && preview.Hint.Contains("Tab", StringComparison.Ordinal) && preview.Hint.Contains("接受", StringComparison.Ordinal))
                return preview;
            await Task.Delay(40, ct);
        }
    }
    private static void CheckReplacement(PageState state, Scene scene, string expected, bool sourceFocus, int? caret = null)
    {
        scene.ReplacementEvents = state.Replacements;
        scene.Submits = state.Submits;
        scene.ExactText = state.A == expected;
        scene.SourceFocus = state.Focus == "a" && state.PageFocused;
        Require(scene.ExactText, "replacement-text-mismatch");
        Require(state.Replacements == 1, "replacement-count-not-one");
        Require(state.Submits == 0, "unexpected-submit");
        Require(state.B == Other, "other-field-text-changed");
        Require(state.AStart == (caret ?? expected.Length) && state.AEnd == state.AStart, "replacement-caret-mismatch");
        if (sourceFocus)
            Require(scene.SourceFocus, "replacement-source-focus-lost");
    }
    private static long TraceLength(string trace) => File.Exists(trace) ? new FileInfo(trace).Length : 0;
    private static async Task<long> WaitTraceAsync(string path, string operation, long offset, CancellationToken ct, bool directOnly = false)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Require(stream.Length <= 32 * 1024 * 1024, "trace-size-limit");
                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(line);
                    }
                    catch (JsonException) { continue; } // Writer may still own the final partial line.
                    using (document)
                    {
                        var root = document.RootElement;
                        if (root.TryGetProperty("operation", out var op) && op.GetString() == operation
                            && (!directOnly || root.TryGetProperty("detail", out var detail)
                                && detail.TryGetProperty("kind", out var kind) && kind.GetString() == "direct"))
                            return stream.Position;
                    }
                }
            }
            await Task.Delay(10, ct);
        }
    }
    private static async Task ReplaceConnectionConfigurationAsync(string path, byte[] expected, byte[] replacement, CancellationToken ct)
    {
        string temporary = path + ".fixture-" + Guid.NewGuid().ToString("N");
        bool ownTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownTemporary = true;
                await stream.WriteAsync(replacement, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            Require((await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(expected), "release-configuration-ownership-changed");
            ct.ThrowIfCancellationRequested();
            // Same-directory replacement publishes only a complete file. A
            // cancelled temporary write leaves the original release untouched.
            File.Move(temporary, path, overwrite: true);
            ownTemporary = false;
        }
        finally
        {
            if (ownTemporary)
                File.Delete(temporary);
        }
    }
    private static async Task<AutomationElement> FindSettingsAsync(Process app, CancellationToken ct)
    {
        // UIA only drives the shipped settings window. It neither reads browser
        // input nor supplies a replacement Runtime/controller to the test.
        AutomationElement? window = null;
        while (window is null)
        {
            ct.ThrowIfCancellationRequested();
            Require(!app.HasExited, "shipped-app-exited");
            window = await Task.Run(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
                    new PropertyCondition(AutomationElement.NameProperty, "TypeLay 设置"))), ct).WaitAsync(ct);
            if (window is null)
                await Task.Delay(40, ct);
        }
        return window;
    }
    private static async Task BrowserConnectionActionAsync(Process app, string action, Action<string> setStage, CancellationToken ct, bool accept = true)
    {
        setStage("find-settings");
        var window = await FindSettingsAsync(app, ct);
        var presentation = ApplicationSupportPresentation.Create([]);
        var applications = new PropertyCondition(AutomationElement.NameProperty, "▦  " + presentation.Title);
        setStage("navigate-applications");
        int[] previousNavigation = await Task.Run(() =>
        {
            var apps = window.FindFirst(TreeScope.Descendants, applications);
            Require(apps is not null, "settings-applications-unavailable");
            int[] identity = apps!.GetRuntimeId();
            ((InvokePattern)apps.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            return identity;
        }, ct).WaitAsync(ct);
        // WPF queues Invoke. Even when applications was already selected, wait
        // for its new visible page before expanding controls from that page.
        setStage("await-applications-page");
        while (true)
        {
            bool ready = await Task.Run(() =>
            {
                var apps = window.FindFirst(TreeScope.Descendants, applications);
                return apps is not null && !apps.GetRuntimeId().SequenceEqual(previousNavigation)
                    && window.FindFirst(TreeScope.Descendants,
                        new AndCondition(new PropertyCondition(AutomationElement.NameProperty, presentation.Title),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))) is not null;
            }, ct).WaitAsync(ct);
            if (ready)
                break;
            await Task.Delay(40, ct);
        }
        setStage("expand-other-applications");
        await Task.Run(() =>
        {
            var more = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, presentation.Labels.MoreApplications));
            if (more is not null && more.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expansion))
                ((ExpandCollapsePattern)expansion).Expand();
        }, ct).WaitAsync(ct);
        setStage("find-primary");
        AutomationElement? primary = null;
        while (primary is null)
        {
            primary = await Task.Run(() => window.FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "ChromeConnectionPrimary"),
                    new PropertyCondition(AutomationElement.NameProperty, action),
                    new PropertyCondition(AutomationElement.IsEnabledProperty, true))), ct).WaitAsync(ct);
            if (primary is null)
                await Task.Delay(40, ct);
        }
        setStage("invoke-primary");
        Task invocation = Task.Run(() => ((InvokePattern)primary.GetCurrentPattern(InvokePattern.Pattern)).Invoke(), ct);
        AutomationElement? FindConfirmation() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ChromeConnectionConfirmation")));
        setStage("find-confirmation");
        AutomationElement? confirm = null;
        while (confirm is null)
        {
            ct.ThrowIfCancellationRequested();
            confirm = await Task.Run(() =>
            {
                var dialog = FindConfirmation();
                return dialog?.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, accept ? "ChromeConnectionConfirm" : "ChromeConnectionCancel"));
            }, ct).WaitAsync(ct);
            if (confirm is null)
                await Task.Delay(40, ct);
        }
        setStage(accept ? "confirm" : "cancel");
        await Task.Run(() => ((InvokePattern)confirm.GetCurrentPattern(InvokePattern.Pattern)).Invoke(), ct).WaitAsync(ct);
        await invocation.WaitAsync(ct);
        setStage("await-dialog-close");
        while (await Task.Run(() => FindConfirmation() is not null, ct).WaitAsync(ct))
            await Task.Delay(40, ct);
        if (!accept)
            return;
        // Wait for the actual serialized Host operation, not merely the dialog.
        setStage("await-host-completion");
        while (true)
        {
            bool complete = await Task.Run(() => primary.Current.IsEnabled && primary.Current.Name != action, ct).WaitAsync(ct);
            if (complete)
                break;
            await Task.Delay(40, ct);
        }
    }
    private static async Task WaitBrowserConnectionStatusAsync(Process app, string expected, CancellationToken ct)
    {
        var window = await FindSettingsAsync(app, ct);
        while (true)
        {
            string? value = await Task.Run(() => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ChromeConnectionStatus"))?.Current.Name, ct).WaitAsync(ct);
            if (value == expected)
                return;
            await Task.Delay(40, ct);
        }
    }
    private static async Task<AutomationElement> SetOnDemandAsync(Process app, string data, CancellationToken ct)
    {
        var window = await FindSettingsAsync(app, ct);
        await Task.Run(() =>
        {
            var general = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "⌂  常规"));
            Require(general is not null && general.TryGetCurrentPattern(InvokePattern.Pattern, out _), "settings-general-unavailable");
            ct.ThrowIfCancellationRequested();
            ((InvokePattern)general!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }, ct).WaitAsync(ct);
        AutomationElement? mode = null;
        while (mode is null)
        {
            mode = await Task.Run(() => window.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, "工作模式"),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox))), ct).WaitAsync(ct);
            if (mode is null)
                await Task.Delay(40, ct);
        }
        await Task.Run(() =>
        {
            ((ExpandCollapsePattern)mode.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            var choice = mode.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "按需模式"));
            Require(choice is not null, "on-demand-choice-unavailable");
            ct.ThrowIfCancellationRequested();
            ((SelectionItemPattern)choice!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        }, ct).WaitAsync(ct);
        while (true)
        {
            var saved = JsonSerializer.Deserialize<UserSettings>(await ReadSharedTextAsync(Path.Combine(data, "settings.json"), ct));
            if (saved?.Mode == WorkMode.OnDemand && saved.DirectShortcut == "Ctrl+Shift+F10" && !saved.ExperimentalWriteback)
                break;
            await Task.Delay(40, ct);
        }
        await Task.Run(() =>
        {
            var shortcuts = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "⌨  快捷键"));
            Require(shortcuts is not null && shortcuts.TryGetCurrentPattern(InvokePattern.Pattern, out _), "settings-shortcuts-unavailable");
            ct.ThrowIfCancellationRequested();
            ((InvokePattern)shortcuts!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            nint settingsHandle = window.Current.NativeWindowHandle;
            Native.GetWindowThreadProcessId(settingsHandle, out uint settingsProcess);
            Require(settingsProcess == app.Id, "settings-window-ownership-changed");
            var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            Require(Native.GetMonitorInfoW(Native.MonitorFromWindow(settingsHandle, 2), ref monitor), "settings-monitor-unavailable");
            int width = (int)Math.Ceiling(WindowBounds(settingsHandle).Width);
            int height = monitor.Work.Bottom - monitor.Work.Top;
            Require(width <= monitor.Work.Right - monitor.Work.Left && height > 0, "settings-command-layout-unavailable");
            ct.ThrowIfCancellationRequested();
            // Only this fixture-owned settings window is kept above Chrome,
            // without activating it. The real source receives focus next.
            Require(Native.SetWindowPos(settingsHandle, -1, monitor.Work.Right - width, monitor.Work.Top, width, height,
                0x0010), "settings-command-layout-failed"); // TOPMOST | NOACTIVATE
        }, ct).WaitAsync(ct);
        return window;
    }
    private static async Task<string> ReadSharedTextAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
    private static void SendChord(ushort[] keys, List<CleanupError> cleanupErrors)
    {
        Require(new ushort[] { 0x11, 0x10, 0x12, 0x5b, 0x5c }.All(key => (Native.GetAsyncKeyState(key) & 0x8000) == 0), "modifier-already-held");
        // The browser's physical press identity comes from the scan code.
        // Resolve every key before inserting any edge, then reuse the same
        // identity for key-up and partial-insertion cleanup.
        var presses = keys.Select(key =>
        {
            uint scan = MapVirtualKeyW(key, 0); // MAPVK_VK_TO_VSC; generic modifiers map to the left key.
            Require(scan is > 0 and <= 0xff, "shortcut-scan-unavailable");
            return new KeyInput { Type = 1, Scan = (ushort)scan, Flags = 8 }; // KEYEVENTF_SCANCODE
        }).ToArray();
        var inputs = presses.Concat(presses.Reverse().Select(input => input with { Flags = input.Flags | 2 })).ToArray();
        uint inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyInput>());
        if (inserted == inputs.Length)
            return;
        // Clean up only the successfully inserted prefix; never repeat a keydown.
        var held = new List<KeyInput>();
        foreach (var input in inputs.Take((int)Math.Min(inserted, (uint)inputs.Length)))
            if ((input.Flags & 2) == 0)
                held.Add(input);
            else
                held.RemoveAll(press => press.Scan == input.Scan);
        var release = held.AsEnumerable().Reverse().Select(input => input with { Flags = input.Flags | 2 }).ToArray();
        if (release.Length > 0 && SendInput((uint)release.Length, release, Marshal.SizeOf<KeyInput>()) != release.Length)
            cleanupErrors.Add(new("injected-keys", "key-release-incomplete"));
        throw new Failure("single-shortcut-injection-incomplete");
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Native.Point point);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Native.Rect bounds);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyInput[] input, int size);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);
}
