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
    private static async Task SetOnDemandAsync(Process app, string data, CancellationToken ct)
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
            if (saved?.Mode == WorkMode.OnDemand && saved.DirectShortcut == "Ctrl+Shift+F10" && saved.ExperimentalWriteback)
                break;
            await Task.Delay(40, ct);
        }
        await Task.Run(() =>
        {
            var close = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "关闭设置"));
            Require(close is not null, "settings-close-unavailable");
            ct.ThrowIfCancellationRequested();
            ((InvokePattern)close!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }, ct).WaitAsync(ct);
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
        var inputs = keys.Select(key => new KeyInput { Type = 1, Key = key })
            .Concat(keys.Reverse().Select(key => new KeyInput { Type = 1, Key = key, Flags = 2 })).ToArray();
        uint inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyInput>());
        if (inserted == inputs.Length)
            return;
        // Clean up only the successfully inserted prefix; never repeat a keydown.
        var held = new List<ushort>();
        foreach (var input in inputs.Take((int)Math.Min(inserted, (uint)inputs.Length)))
            if (input.Flags == 0)
                held.Add(input.Key);
            else
                held.Remove(input.Key);
        var release = held.AsEnumerable().Reverse().Select(key => new KeyInput { Type = 1, Key = key, Flags = 2 }).ToArray();
        if (release.Length > 0 && SendInput((uint)release.Length, release, Marshal.SizeOf<KeyInput>()) != release.Length)
            cleanupErrors.Add(new("injected-keys", "key-release-incomplete"));
        throw new Failure("single-shortcut-injection-incomplete");
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyInput[] input, int size);
}
