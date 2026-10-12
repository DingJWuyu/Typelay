using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using N = TypeLay.Platform.Windows.NativeMethods;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !Environment.UserInteractive)
            return NotRun("interactive-windows-x64-required");
        if (args.Length != 1 || !File.Exists(args[0]) || Path.GetExtension(args[0]) != ".dll")
            return NotRun("built-native-fixture-dll-required");
        return await RunWindows(Path.GetFullPath(args[0]));
    }

    private static int NotRun(string reason)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            suite = "mvp-native",
            status = "not-run",
            reason
        }));
        return 2;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindows(string fixtureDll)
    {
        var results = new List<object>();
        int failed = 0;
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(fixtureDll);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Fixture start failed.");
        try
        {
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var descriptor = JsonSerializer.Deserialize<FixtureDescriptor>(line ?? "null")
                ?? throw new InvalidOperationException("Missing fixture metadata.");
            // Windows may deny a background process the initial foreground request.
            // Wait for an actual activation before evaluating adapter behavior; an
            // unavailable desktop is a prerequisite failure, not fourteen code failures.
            long readyUntil = Environment.TickCount64 + 30000;
            while (N.Focused().Root != (nint)descriptor.RootHwnd && Environment.TickCount64 < readyUntil)
                await Task.Delay(50);
            if (N.Focused().Root != (nint)descriptor.RootHwnd)
                return NotRun("native-fixture-foreground-unavailable");
            var adapter = new NativeEditAdapter(process, descriptor);
            string phase = "start";
            WriteStatus? receiptStatus = null;
            Denial? receiptReason = null;
            bool? textMatches = null, selectionMatches = null;
            FixtureField Field(string name) => descriptor.Fields.Single(f => f.Name == name);
            nint Hwnd(string name) => (nint)Field(name).Hwnd;
            void Focus(string name)
            {
                phase = "focus";
                if (!N.PostMessage((nint)descriptor.RootHwnd, N.FixtureFocus, (nuint)Hwnd(name), 0))
                    throw new InvalidOperationException("Fixture focus request failed.");
                for (int i = 0; i < 100; i++)
                {
                    if (N.Focused() == ((nint)descriptor.RootHwnd, Hwnd(name)))
                        return;
                    Thread.Sleep(20);
                }
                throw new InvalidOperationException("Fixture did not acquire actual foreground focus.");
            }
            InputSnapshot Read(string readPhase = "after-read")
            {
                phase = readPhase;
                textMatches = selectionMatches = null;
                var observed = adapter.Capture() ?? throw new InvalidOperationException("No supported fixture observation.");
                phase = readPhase == "reset-read" ? "reset-verify" : "after-verify";
                return observed;
            }
            InputSnapshot Reset(string name, string text, TextRange? selection = null)
            {
                Focus(name);
                // TEST SETUP, not the product write path. Verify the reset before asserting behavior.
                phase = "reset-text";
                N.SendString(Hwnd(name), N.WM_SETTEXT, text);
                var range = selection ?? new TextRange(text.Length, text.Length);
                phase = "reset-selection";
                N.Send(Hwnd(name), N.EM_SETSEL, (nuint)range.Start, (nint)range.End);
                var observed = Read("reset-read");
                textMatches = observed.Text == text;
                selectionMatches = observed.Selection == range;
                Check(observed.Text == text && observed.Selection == range);
                return observed;
            }
            WritePlan Plan(InputSnapshot snapshot, string replacement)
            {
                phase = "plan";
                var result = WriteSafety.Replace(snapshot.Text, snapshot.TranslationScope, replacement);
                return new(Guid.NewGuid(), Guid.NewGuid(), 1, snapshot, snapshot.TranslationScope,
                    replacement, result.Text, result.Caret);
            }
            WriteReceipt Write(WritePlan plan)
            {
                phase = "write";
                textMatches = selectionMatches = null;
                var receipt = adapter.TryWrite(plan);
                receiptStatus = receipt.Status;
                receiptReason = receipt.Reason;
                phase = "write-verify";
                return receipt;
            }
            void Case(string name, Action action, string kind = "real-win32-directed-io")
            {
                phase = "start";
                receiptStatus = null;
                receiptReason = null;
                textMatches = selectionMatches = null;
                try
                {
                    action();
                    results.Add(new
                    {
                        name,
                        kind,
                        status = "passed"
                    });
                }
                catch (Exception ex)
                {
                    failed++;
                    // Fixed stages, enums and comparisons only; no field text,
                    // exception messages, full receipts or absolute source paths.
                    results.Add(new
                    {
                        name,
                        kind,
                        status = "failed",
                        error_type = ex.GetType().Name,
                        phase,
                        receiptStatus = receiptStatus?.ToString(),
                        receiptReason = receiptReason?.ToString(),
                        textMatches,
                        selectionMatches,
                        location = FailureLocation(ex)
                    });
                }
            }
            long Submits()
            {
                phase = "submit-count";
                return (long)N.Send((nint)descriptor.RootHwnd, N.FixtureSubmitCount);
            }

            Case("native-full-read", () => { var a = Reset("a", "你好 TypeLay"); Check(a.IsUsable && a.Text == "你好 TypeLay"); });
            Case("native-whole-replacement-and-end-caret", () =>
            {
                var a = Reset("a", "你好");
                var receipt = Write(Plan(a, "Hello"));
                Check(receipt.Status == WriteStatus.Verified);
                var after = Read();
                Check(after.Text == "Hello" && after.Selection == new TextRange(5, 5));
            });
            Case("native-selection-outside-preserved", () =>
            {
                var a = Reset("b", "Hello, 明天下午见, thanks.", new(7, 12));
                var receipt = Write(Plan(a, "see you tomorrow afternoon"));
                Check(receipt.Status == WriteStatus.Verified);
                Check(Read().Text == "Hello, see you tomorrow afternoon, thanks.");
            });
            Case("native-emoji-utf16-boundary", () =>
            {
                var a = Reset("b", "A🙂中文B", new(3, 5));
                Check(Write(Plan(a, "EN")).Status == WriteStatus.Verified);
                var after = Read();
                textMatches = after.Text == "A🙂ENB";
                selectionMatches = after.Selection == new TextRange(5, 5);
                Check(after.Text == "A🙂ENB" && after.Selection == new TextRange(5, 5));
            });
            Case("native-crlf-multiline", () =>
            {
                var a = Reset("b", "你好\r\n再见");
                Check(Write(Plan(a, "Hello\r\nGoodbye")).Status == WriteStatus.Verified);
                Check(Read().Text == "Hello\r\nGoodbye");
            });
            Case("source-changed-refuses-before-mutation", () =>
            {
                var plan = Plan(Reset("a", "你好"), "Hello");
                Reset("a", "再见");
                var receipt = Write(plan);
                Check(receipt.Status == WriteStatus.RejectedBeforeMutation && receipt.Reason == Denial.SourceChanged);
                Check(Read().Text == "再见");
            });
            Case("target-changed-does-not-write-either-field", () =>
            {
                var plan = Plan(Reset("a", "你好"), "Hello");
                Reset("b", "另一个输入框");
                var receipt = Write(plan);
                Check(receipt.Status == WriteStatus.RejectedBeforeMutation && receipt.Reason == Denial.TargetChanged);
                Check(Read().Text == "另一个输入框");
                Focus("a");
                Check(Read().Text == "你好");
            });
            Case("selection-change-refuses-before-mutation", () =>
            {
                var plan = Plan(Reset("b", "A你好B", new(1, 3)), "Hello");
                phase = "change-selection";
                N.Send(Hwnd("b"), N.EM_SETSEL, 0, 1);
                var receipt = Write(plan);
                Check(receipt.Status == WriteStatus.RejectedBeforeMutation && receipt.Reason == Denial.SelectionChanged);
                Check(Read().Text == "A你好B" && Read().Selection == new TextRange(0, 1));
            });
            Case("single-line-rejects-multiline", () =>
            {
                var a = Reset("a", "你好");
                var receipt = Write(Plan(a, "Hello\nWorld"));
                Check(receipt.Status == WriteStatus.RejectedBeforeMutation && receipt.Reason == Denial.Multiline);
                Check(Read().Text == "你好");
            });
            foreach (var (name, access) in new[] { ("password", FieldAccess.Password), ("readonly", FieldAccess.ReadOnly), ("pin", FieldAccess.Sensitive) })
                Case("no-text-read-" + name, () =>
                {
                    Focus(name);
                    phase = "read-count-before";
                    long before = (long)N.GetProp(Hwnd(name), N.ReadCountProperty);
                    var a = Read();
                    phase = "read-count-after";
                    long after = (long)N.GetProp(Hwnd(name), N.ReadCountProperty);
                    phase = "no-read-verify";
                    Check(a.Access == access && a.Text == "" && a.Selection is null && before == after);
                });
            foreach (var (value, ime) in new[] { (0, ImeState.Unknown), (2, ImeState.Composing) })
                Case("fixture-ime-contract-" + ime, () =>
                {
                    Focus("a");
                    try
                    {
                        phase = "ime-setup";
                        Check(N.SetProp(Hwnd("a"), N.ImeProperty, (nint)value));
                        phase = "read-count-before";
                        long before = (long)N.GetProp(Hwnd("a"), N.ReadCountProperty);
                        var a = Read();
                        Check(a.Ime == ime && a.Text == "" && a.Selection is null);
                        phase = "no-read-verify";
                        Check(before == (long)N.GetProp(Hwnd("a"), N.ReadCountProperty));
                    }
                    finally { N.SetProp(Hwnd("a"), N.ImeProperty, 1); }
                }, "synthetic-fixture-ime-state-NOT-real-IME");
            Case("all-directed-accepts-have-zero-submits", () => Check(Submits() == 0));
            Case("submit-counter-positive-control", () =>
            {
                // Prove the counter is live; this is NOT an acceptance action.
                phase = "submit-click";
                N.Send((nint)descriptor.SubmitButtonHwnd, 0x00F5); // BM_CLICK, own fixture only.
                Check(Submits() == 1);
            });
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                suite = "mvp-native",
                status = failed == 0 ? "passed" : "failed",
                scope = "spawned-plain-win32-edit-fixture-only",
                total = results.Count,
                failed,
                results,
                not_run = new[] { "physical-Tab", "global-hook", "real-IME", "WPF-edit-focus", "UIA-browser", "local-model" }
            },
                new JsonSerializerOptions { WriteIndented = true }));
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                suite = "mvp-native",
                status = "blocked",
                error_type = ex.GetType().Name,
                results
            }));
            return 2;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
    private static object[] FailureLocation(Exception exception) => new StackTrace(exception, true).GetFrames()
        .Where(frame => frame.GetFileLineNumber() > 0)
        .Take(8)
        .Select(frame => (object)new
        {
            file = Path.GetFileName(frame.GetFileName()),
            line = frame.GetFileLineNumber(),
            method = frame.GetMethod()?.Name
        }).ToArray();
    private static void Check(bool result)
    {
        if (!result)
            throw new InvalidOperationException("Assertion failed.");
    }
}
