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
    // Disposed after the real Broker, including on test failure. Scan its now
    // finite body-free log within fixed byte/line limits, and bound TRX output.
    // Report an incomplete scan rather than implying no capture was rejected.
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
                byte[] bytes = new byte[1024 * 1024];
                int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                bool truncated = stream.Position < stream.Length;
                string[] lines = Encoding.UTF8.GetString(bytes, 0, count).Split('\n');
                int outcomes = 0, omitted = 0, captures = 0, omittedCaptures = 0;
                int scannedLines = 0;
                bool capturedNonNative = false, capturedNonNativeAfterReturn = false, returnObserved = false;
                for (int index = 0; index < lines.Length; index++)
                {
                    // A bounded prefix may end in the middle of a JSON row.
                    if (truncated && index == lines.Length - 1)
                        break;
                    string line = lines[index];
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    if (scannedLines == 4096)
                    {
                        truncated = true;
                        break;
                    }
                    scannedLines++;
                    using var entry = JsonDocument.Parse(line);
                    if (!entry.RootElement.TryGetProperty("operation", out var operation))
                        continue;
                    if (operation.GetString() == "return")
                    {
                        returnObserved = true;
                        if (outcomes++ < 4)
                            TestContext.Current.TestOutputHelper?.WriteLine("Configured Ctrl Broker return: " + line);
                        else
                            omitted++;
                    }
                    else if (operation.GetString() == "capture")
                    {
                        bool nonNative = !entry.RootElement.TryGetProperty("result", out var result)
                            || result.GetString() != "observed-native";
                        bool firstNonNative = !capturedNonNative && nonNative;
                        bool firstNonNativeAfterReturn = returnObserved && !capturedNonNativeAfterReturn && nonNative;
                        capturedNonNative |= firstNonNative;
                        capturedNonNativeAfterReturn |= firstNonNativeAfterReturn;
                        if (captures++ < 8 || firstNonNative || firstNonNativeAfterReturn)
                        {
                            string scope = firstNonNative && firstNonNativeAfterReturn ? "global and after return"
                                : firstNonNative ? "global" : "after return";
                            TestContext.Current.TestOutputHelper?.WriteLine(firstNonNative || firstNonNativeAfterReturn
                                ? $"Configured Ctrl Broker first capture not observed-native ({scope}): " + line
                                : "Configured Ctrl Broker capture: " + line);
                        }
                        else
                            omittedCaptures++;
                    }
                }
                string captureResult = capturedNonNative ? "observed" : truncated ? "unknown-truncated" : "not-observed";
                string afterReturnResult = capturedNonNativeAfterReturn ? "observed" : truncated ? "unknown-truncated"
                    : returnObserved ? "not-observed" : "return-not-observed";
                TestContext.Current.TestOutputHelper?.WriteLine($"Configured Ctrl Broker return trace: observedReturns={outcomes}, omittedReturns={omitted}, observedCaptures={captures}, omittedCaptures={omittedCaptures}, firstCaptureNotObservedNative={captureResult}, firstCaptureNotObservedNativeAfterReturn={afterReturnResult}, readBytes={count}, scannedLines={scannedLines}, truncated={truncated}");
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
        [Flags]
        private enum Signal
        {
            None = 0,
            EmptyCapture = 1,
            NonSourceCapture = 2,
            OriginalTargetChanged = 4,
            OriginalLifetimeChanged = 8,
            ClearResponse = 16,
            ReturnResponse = 32,
            UnexpectedForeground = 64,
            Failure = 128
        }
        private const int PrefixCount = 8, MaximumEntries = 64;
        private sealed record Entry(long AtMs, string Stage, JsonElement State, string? FirstSignals,
            string? FirstSignalsAfterReturn);
        private readonly object _sync = new();
        private readonly List<Entry> _entries = [];
        private readonly Dictionary<string, string> _last = [];
        private readonly WindowsTarget? _original = expected is { } id && WindowsTarget.TryDecode(id, out var target)
            ? target : null;
        private Signal _observedSignals, _observedSignalsAfterReturn;
        private bool _returnResponseObserved;
        private int _omitted;
        private uint? _firstSourceThread, _firstEditorThread;

        internal void Response(BrokerRequest request, BrokerResponse response)
        {
            if (request.Op is not ("return" or "capture" or "clear"))
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
            Signal signals = request.Op == "return" ? Signal.ReturnResponse
                : request.Op == "clear" ? Signal.ClearResponse : Signal.None;
            if (request.Op == "capture")
            {
                if (snapshot is null)
                    signals |= Signal.EmptyCapture;
                if (response.Focus != FocusMember.Source)
                    signals |= Signal.NonSourceCapture;
                if (expected is not null && snapshot is not null && snapshot.Target != expected)
                    signals |= Signal.OriginalTargetChanged;
                if (sameTargetLifetime == false)
                    signals |= Signal.OriginalLifetimeChanged;
            }
            if (request.Op == "return" && response.Status != "focused"
                || snapshot is not null && (!snapshot.WriteSupported || snapshot.Ime != ImeState.Clear))
                signals |= Signal.Failure;
            Observe("broker-" + request.Op, new
            {
                response.Status,
                focus = response.Focus.ToString(),
                hasSnapshot = snapshot is not null,
                boundToOriginalTarget = expected is not null && request.BoundTarget == expected,
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
            }, signals);
        }

        internal void Observe(string stage, object? detail = null, bool failed = false) =>
            Observe(stage, detail, failed ? Signal.Failure : Signal.None);

        private void Observe(string stage, object? detail, Signal signals)
        {
            var focus = N.ReadFocus();
            string foreground = focus.Root == 0 ? "null" : focus.Root == root ? "source"
                : focus.Root == field ? "source-field" : focus.Root == editor ? "editor" : "other";
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
                if (stage != "before-return" && foreground is "null" or "other")
                    signals |= Signal.UnexpectedForeground;
                Signal firstSignals = signals & ~_observedSignals;
                Signal firstSignalsAfterReturn = _returnResponseObserved ? signals & ~_observedSignalsAfterReturn : Signal.None;
                if (_last.TryGetValue(stage, out string? previous) && signature == previous
                    && firstSignals == Signal.None && firstSignalsAfterReturn == Signal.None)
                    return;
                _last[stage] = signature;
                _observedSignals |= signals;
                if (_returnResponseObserved)
                    _observedSignalsAfterReturn |= signals;
                _returnResponseObserved |= (signals & Signal.ReturnResponse) != 0;
                var entry = new Entry(WindowsInterruptClock.NowMs, stage, state,
                    firstSignals == Signal.None ? null : firstSignals.ToString(),
                    firstSignalsAfterReturn == Signal.None ? null : firstSignalsAfterReturn.ToString());
                if (_entries.Count == MaximumEntries)
                {
                    // These are observations, not root-cause verdicts. Preserve
                    // the prefix and first occurrence of each fixed signal,
                    // both globally and after the first return response. An
                    // in-flight earlier capture must not consume the latter.
                    // Eight prefix entries plus at most sixteen signal entries
                    // always leave an evictable entry in the 64-entry buffer.
                    _entries.RemoveAt(_entries.FindIndex(PrefixCount, retained => retained.FirstSignals is null
                        && retained.FirstSignalsAfterReturn is null));
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
