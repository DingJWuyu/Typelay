using System.IO;
using System.Text;
using System.Text.Json;
using TypeLay.Core;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceControllerTests
{
    // Disposed after the real Broker, including on test failure. Keep only its
    // existing body-free return outcome in TRX, not the unrelated capture log.
    private sealed class BrokerReturnTraceFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "TypeLay.ControlReturn." + Guid.NewGuid().ToString("N"));
        internal string Path
        {
            get;
        }

        internal BrokerReturnTraceFile()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "return");
        }

        public void Dispose()
        {
            try
            {
                using var stream = File.OpenRead(Path + ".broker.jsonl");
                byte[] bytes = new byte[64 * 1024];
                int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                bool truncated = stream.Position < stream.Length;
                string[] lines = Encoding.UTF8.GetString(bytes, 0, count).Split('\n');
                int outcomes = 0, omitted = 0;
                for (int index = 0; index < lines.Length; index++)
                {
                    // A bounded prefix may end in the middle of a JSON row.
                    if (truncated && index == lines.Length - 1 || string.IsNullOrWhiteSpace(lines[index]))
                        continue;
                    using var entry = JsonDocument.Parse(lines[index]);
                    if (!entry.RootElement.TryGetProperty("operation", out var operation) || operation.GetString() != "return")
                        continue;
                    if (outcomes++ < 4)
                        TestContext.Current.TestOutputHelper?.WriteLine("Configured Ctrl Broker return: " + lines[index]);
                    else
                        omitted++;
                }
                TestContext.Current.TestOutputHelper?.WriteLine($"Configured Ctrl Broker return trace: observedReturns={outcomes}, truncated={truncated}, omittedReturns={omitted}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                TestContext.Current.TestOutputHelper?.WriteLine("Configured Ctrl Broker return trace unavailable: " + error.GetType().Name);
            }
            finally { Directory.Delete(_directory, true); }
        }
    }

    // Test-private body-free observations only. No foreground manipulation,
    // extra capture, extra return, new deadline, or production gate changes.
    private sealed class SourceReturnTrace(nint root, nint field, nint editor, KeyboardHook hook,
        View view, TargetId? expected)
    {
        private sealed record Entry(long AtMs, string Stage, JsonElement State);
        private readonly object _sync = new();
        private readonly List<Entry> _entries = [];
        private readonly Dictionary<string, string> _last = [];
        private readonly WindowsTarget? _original = expected is { } id && WindowsTarget.TryDecode(id, out var target)
            ? target : null;
        private Entry? _firstFailure;
        private int _omitted;
        private uint? _firstSourceThread, _firstEditorThread;

        internal void Response(BrokerRequest request, BrokerResponse response)
        {
            if (request.Op is not ("return" or "capture"))
                return;
            var snapshot = response.Snapshot;
            string? provider = null;
            bool? targetIsRoot = null, targetIsField = null, targetRootIsField = null;
            bool? sameProcess = null, sameProcessLifetime = null, sameElement = null, sameTargetLifetime = null;
            if (snapshot is not null && WindowsTarget.TryDecode(snapshot.Target, out var target))
            {
                provider = target.Provider;
                targetIsRoot = target.RootHwnd == (long)root;
                targetIsField = target.ControlHwnd == (long)field;
                targetRootIsField = target.RootHwnd == (long)field;
                if (_original is { } original)
                {
                    sameProcess = target.ProcessId == original.ProcessId;
                    sameProcessLifetime = sameProcess == true && target.ProcessStartUtcTicks == original.ProcessStartUtcTicks;
                    sameElement = target.ElementToken == original.ElementToken;
                    sameTargetLifetime = target.Lifetime == original.Lifetime;
                }
            }
            Observe("broker-" + request.Op, new
            {
                response.Status,
                focus = response.Focus.ToString(),
                hasSnapshot = snapshot is not null,
                sameBoundTarget = request.BoundTarget is not null && snapshot?.Target == request.BoundTarget,
                sameOriginalTarget = expected is not null && snapshot?.Target == expected,
                provider,
                targetIsRoot,
                targetIsField,
                targetRootIsField,
                sameProcess,
                sameProcessLifetime,
                sameElement,
                sameTargetLifetime,
                ime = snapshot?.Ime.ToString(),
                snapshot?.WriteSupported,
                snapshot?.DirectWriteSupported,
                snapshot?.InputEpoch,
                snapshot?.FocusEpoch
            }, failed: request.Op == "return" && response.Status != "focused"
                || snapshot is not null && (!snapshot.WriteSupported || snapshot.Ime != ImeState.Clear));
        }

        internal void Observe(string stage, object? detail = null, bool failed = false)
        {
            var focus = N.ReadFocus();
            string foreground = focus.Root == 0 ? "null" : focus.Root == root ? "source"
                : focus.Root == editor ? "editor" : "other";
            // Reuse the existing read, never query another HWND to fill a gap.
            // A missing source observation leaves its thread comparison unknown.
            if (focus.ThreadId != 0)
            {
                if (foreground == "source")
                    _firstSourceThread ??= focus.ThreadId;
                else if (foreground == "editor")
                    _firstEditorThread ??= focus.ThreadId;
            }
            var state = JsonSerializer.SerializeToElement(new
            {
                foreground,
                foregroundIsSourceField = focus.Root == field,
                foregroundProcessIsSource = _original is { } original ? focus.ProcessId == original.ProcessId : (bool?)null,
                foregroundThreadMatchesFirstSource = _firstSourceThread is { } sourceThread ? focus.ThreadId == sourceThread : (bool?)null,
                foregroundThreadMatchesFirstEditor = _firstEditorThread is { } editorThread ? focus.ThreadId == editorThread : (bool?)null,
                focusIsSourceField = focus.Control == field,
                focus.GuiThreadInfoSucceeded,
                hasOriginalTarget = expected is not null,
                hookFocus = expected is null ? null : hook.GetFocus(expected.Value).ToString(),
                hook.WindowEpoch,
                hook.ForegroundEpoch,
                hook.UserEpoch,
                hook.SourceUserEpoch,
                hook.ContentEpoch,
                viewState = view.State.ToString(),
                view.CanAccept,
                detail
            });
            string signature = state.GetRawText();
            lock (_sync)
            {
                if (_last.TryGetValue(stage, out string? previous) && signature == previous)
                    return;
                _last[stage] = signature;
                var entry = new Entry(WindowsInterruptClock.NowMs, stage, state);
                if (_firstFailure is null && (failed || stage != "before-return" && foreground is "null" or "other"))
                    _firstFailure = entry;
                if (_entries.Count == 64)
                {
                    // Preserve the initial observation and the first failure,
                    // while always retaining the newest/final observation.
                    _entries.RemoveAt(ReferenceEquals(_entries[1], _firstFailure) ? 2 : 1);
                    _omitted++;
                }
                _entries.Add(entry);
            }
        }

        internal string Summary()
        {
            lock (_sync)
                return JsonSerializer.Serialize(new
                {
                    omittedChanges = _omitted,
                    observations = _entries
                });
        }
    }
}
