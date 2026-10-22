using System.Text.Json;
using TypeLay.Core;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceControllerTests
{
    // Test-private body-free observations only. No foreground manipulation,
    // extra capture, extra return, new deadline, or production gate changes.
    private sealed class SourceReturnTrace(nint root, nint field, nint editor, KeyboardHook hook,
        View view, TargetId? expected)
    {
        private sealed record Entry(long AtMs, string Stage, JsonElement State);
        private readonly object _sync = new();
        private readonly List<Entry> _entries = [];
        private readonly Dictionary<string, string> _last = [];
        private Entry? _firstFailure;
        private int _omitted;

        internal void Response(BrokerRequest request, BrokerResponse response)
        {
            if (request.Op is not ("return" or "capture"))
                return;
            var snapshot = response.Snapshot;
            string? provider = null;
            bool? targetIsRoot = null, targetIsField = null;
            if (snapshot is not null && WindowsTarget.TryDecode(snapshot.Target, out var target))
            {
                provider = target.Provider;
                targetIsRoot = target.RootHwnd == (long)root;
                targetIsField = target.ControlHwnd == (long)field;
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
            var state = JsonSerializer.SerializeToElement(new
            {
                foreground,
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
