using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using TypeLay.Core;
using TypeLay.Windows;
using N = TypeLay.Platform.Windows.NativeMethods;

// Fixture setup only. Neither a successful click nor a responsive source grants
// product readiness, an IME state, or a write permit.
internal static class NativeSourceSetup
{
    // Explicit synthetic-fixture preparation, never a fallback for a refused
    // click or production foreground policy. A request does not establish field
    // focus, model readiness or a write permit; observe before the one real click.
    internal static void RequestOwnedForeground(nint root, nint field, Process process, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Check(IsOwnedField(root, field, process), FailureReason.SourceForegroundIdentityChanged);
        Check(N.IsWindowVisible(root) && N.IsWindowEnabled(root)
            && N.IsWindowVisible(field) && N.IsWindowEnabled(field), FailureReason.SourceForegroundUnavailable);
        ct.ThrowIfCancellationRequested();
        var before = ReadForeground();
        ct.ThrowIfCancellationRequested();
        if (!N.SetForegroundWindow(root))
        {
            var after = ReadForeground();
            // Retain facts in the TRX exception, not a guessed eligibility cause.
            // SetForegroundWindow has no documented last-error contract.
            throw new Failure(FailureReason.SourceForegroundRefused, JsonSerializer.Serialize(new
            {
                Reason = nameof(FailureReason.SourceForegroundRefused),
                CallerProcessId = Environment.ProcessId,
                CallerThreadId = GetCurrentThreadId(),
                SourceProcessId = process.Id,
                SourceRoot = (long)root,
                SourceField = (long)field,
                Before = before,
                After = after
            }));
        }
    }

    // These bounded reads are non-atomic observations, not foreground permission
    // or exact-focus proofs. Never query foreign window titles or document text.
    private static object ReadForeground()
    {
        long startedAtMs = WindowsInterruptClock.NowMs;
        nint root = N.GetForegroundWindow();
        uint rootProcessId = 0;
        uint rootThreadId = root == 0 ? 0 : N.GetWindowThreadProcessId(root, out rootProcessId);
        int? rootError = root != 0 && rootThreadId == 0 ? Marshal.GetLastPInvokeError() : null;
        var info = new N.GuiThreadInfo { Size = (uint)Marshal.SizeOf<N.GuiThreadInfo>() };
        bool? guiRead = rootThreadId == 0 ? null : N.GetGUIThreadInfo(rootThreadId, ref info);
        int? guiError = guiRead == false ? Marshal.GetLastPInvokeError() : null;
        nint control = guiRead == true ? info.Focus : 0;
        uint controlProcessId = 0;
        uint controlThreadId = control == 0 ? 0 : N.GetWindowThreadProcessId(control, out controlProcessId);
        int? controlError = control != 0 && controlThreadId == 0 ? Marshal.GetLastPInvokeError() : null;
        return new
        {
            StartedAtMs = startedAtMs,
            CompletedAtMs = WindowsInterruptClock.NowMs,
            Root = (long)root,
            RootProcessId = rootProcessId,
            RootThreadId = rootThreadId,
            RootLookupError = rootError,
            Control = (long)control,
            ControlProcessId = controlProcessId,
            ControlThreadId = controlThreadId,
            ControlLookupError = controlError,
            GuiThreadInfoSucceeded = guiRead,
            GuiThreadInfoError = guiError,
            // GUITHREADINFO: GUI_INMENUMODE=4, SYSTEMMENUMODE=8, POPUPMENUMODE=16.
            GuiFlags = guiRead == true ? (uint?)info.Flags : null
        };
    }

    // Observe the requested foreground root and its current desktop hit test.
    // Never repeat activation, move windows or synthesize input while waiting.
    internal static async Task<N.Point> WaitVisibleAsync(nint root, nint field, Process process,
        Observation observation, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            nint previousDpi = SetThreadDpiAwarenessContext(-4);
            Check(previousDpi != 0, FailureReason.SourceClickObservationDpiContext);
            try
            {
                Check(IsOwnedField(root, field, process), FailureReason.SourceForegroundIdentityChanged);
                observation.VisibilityAttempts++;
                observation.VisibilityForegroundMatched = N.GetForegroundWindow() == root;
                bool boundsAvailable = N.GetWindowRect(field, out var bounds);
                observation.VisibilityGeometryAvailable = N.IsWindowVisible(root) && N.IsWindowEnabled(root)
                    && N.IsWindowVisible(field) && N.IsWindowEnabled(field)
                    && boundsAvailable
                    && (long)bounds.Right - bounds.Left > 8 && (long)bounds.Bottom - bounds.Top > 8;
                string hit = "none";
                if (observation.VisibilityGeometryAvailable)
                {
                    int y = (int)((long)bounds.Top + ((long)bounds.Bottom - bounds.Top) / 2);
                    int[] xs = [(int)((long)bounds.Left + ((long)bounds.Right - bounds.Left) / 2), bounds.Left + 4, bounds.Right - 4];
                    foreach (int x in xs)
                    {
                        var point = new N.Point { X = x, Y = y };
                        nint target = WindowFromPoint(point);
                        string current = target == field ? "field" : target == root ? "source-root" : target == 0 ? "none"
                            : N.GetWindowThreadProcessId(target, out uint pid) != 0 && pid == process.Id ? "other-owned" : "foreign";
                        if (x == xs[0])
                            hit = current;
                        if (target == field && observation.VisibilityForegroundMatched)
                        {
                            RecordHit("field");
                            Check(IsOwnedField(root, field, process), FailureReason.SourceForegroundIdentityChanged);
                            ct.ThrowIfCancellationRequested();
                            return point;
                        }
                    }
                }
                RecordHit(hit);
            }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
            await Task.Delay(15, ct);
        }

        void RecordHit(string hit)
        {
            observation.VisibilityFirstHit ??= hit;
            observation.VisibilityLastHit = hit;
        }
    }

    private static bool IsOwnedField(nint root, nint field, Process process) => !process.HasExited
        && N.GetWindowThreadProcessId(root, out uint rootPid) != 0 && rootPid == process.Id
        && N.GetWindowThreadProcessId(field, out uint fieldPid) != 0 && fieldPid == process.Id
        && N.GetAncestor(field, 2) == root;

    internal static async Task ClickAsync(nint root, nint field, Process process, CancellationToken ct, nuint marker = 0)
    {
        ct.ThrowIfCancellationRequested();
        int processId = process.Id;
        // Only observation runs in the worker. A cancelled UIA wait cannot leave
        // a background action that later moves the pointer or clicks.
        var observed = await Task.Run(() =>
        {
            nint previousDpi = SetThreadDpiAwarenessContext(-4);
            Check(previousDpi != 0, FailureReason.SourceClickObservationDpiContext);
            try
            {
                Check(IsOwnedField(root, field, process), FailureReason.SourceClickIdentityChanged);
                var element = AutomationElement.FromHandle(field);
                var current = element.Current;
                Check(current.NativeWindowHandle == field, FailureReason.SourceClickControlHandleMismatch);
                Check(current.ProcessId == processId, FailureReason.SourceClickControlProcessMismatch);
                var controlType = current.ControlType;
                Check(controlType == ControlType.Edit, controlType == ControlType.Document
                    ? FailureReason.SourceClickControlDocument : FailureReason.SourceClickControlTypeMismatch);
                Check(current.IsEnabled, FailureReason.SourceClickControlDisabled);
                Check(!current.IsOffscreen, FailureReason.SourceClickControlOffscreen);
                var bounds = current.BoundingRectangle;
                Check(!bounds.IsEmpty && bounds.Width > 8 && bounds.Height > 8
                    && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
                    && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom), FailureReason.SourceClickGeometryUnavailable);
                foreach (double x in new[] { bounds.Left + bounds.Width / 2, bounds.Left + 4, bounds.Right - 4 })
                {
                    double y = bounds.Top + bounds.Height / 2;
                    Check(x >= int.MinValue && x <= int.MaxValue && y >= int.MinValue && y <= int.MaxValue,
                        FailureReason.SourceClickGeometryUnavailable);
                    var point = new N.Point { X = (int)x, Y = (int)y };
                    if (bounds.Contains(point.X, point.Y) && WindowFromPoint(point) == field)
                        return point;
                }
                throw new Failure(FailureReason.SourceClickObscured);
            }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
        }, ct).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        nint previousClickDpi = SetThreadDpiAwarenessContext(-4);
        Check(previousClickDpi != 0, FailureReason.SourceClickDpiContext);
        try
        {
            CheckTarget();
            Check(new[] { 0x01, 0x02, 0x04, 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(key => GetAsyncKeyState(key) >= 0),
                FailureReason.SourceClickInputAlreadyHeld);
            Check(SetCursorPos(observed.X, observed.Y), FailureReason.SourceClickCursorUnavailable);
            CheckTarget();
            ct.ThrowIfCancellationRequested();
            var click = HyPackageCheck.SendLeftClick(marker);
            Check(click.ReleaseComplete, FailureReason.SourceClickReleaseIncomplete);
            Check(click.Complete, FailureReason.SourceClickIncomplete);

            void CheckTarget() => Check(IsOwnedField(root, field, process) && N.IsWindowVisible(field) && N.IsWindowEnabled(field)
                && N.GetWindowRect(field, out var bounds) && observed.X >= bounds.Left && observed.X < bounds.Right
                && observed.Y >= bounds.Top && observed.Y < bounds.Bottom && WindowFromPoint(observed) == field,
                FailureReason.SourceClickTargetChanged);
        }
        finally { SetThreadDpiAwarenessContext(previousClickDpi); }
    }

    // A synchronization hypothesis for the observed post-click transport timeout,
    // not a claim that one read makes the next message infallible. Only read 1460
    // is re-observed; the caller sends its selection mutation exactly once.
    internal static async Task WaitResponsiveAsync(nint root, nint field, Process process, string expectedText,
        Observation observation, CancellationToken ct)
    {
        observation.ReadStartedAtMs = WindowsInterruptClock.NowMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            observation.Observe(root, field, process);
            Check(observation.IdentityMatched && observation.FocusMatched, FailureReason.SourceResponseIdentityOrFocusChanged);
            observation.ReadAttempts++;
            observation.LastReadStartedAtMs = WindowsInterruptClock.NowMs;
            bool responsive = false;
            try
            {
                Check(N.ReadText(field, Limits.MaxTextUnits) == expectedText, FailureReason.SourceResponseTextChanged);
                _ = N.ReadSelection(field); // The click may legitimately have moved the caret.
                responsive = true;
            }
            catch (N.NativeMessageException error) when (error.Win32Error == 1460
                && ((error.Operation == nameof(N.Send) && error.MessageId is N.WM_GETTEXTLENGTH or N.EM_GETSEL)
                    || (error.Operation == nameof(N.ReadText) && error.MessageId == N.WM_GETTEXT)))
            {
                observation.ReadTimeouts++;
            }
            finally { observation.LastReadEndedAtMs = WindowsInterruptClock.NowMs; }
            observation.Observe(root, field, process);
            Check(observation.IdentityMatched && observation.FocusMatched, FailureReason.SourceResponseIdentityOrFocusChanged);
            if (responsive)
            {
                ct.ThrowIfCancellationRequested();
                observation.ReadCompletedAtMs = WindowsInterruptClock.NowMs;
                return;
            }
            await Task.Delay(40, ct);
        }
    }

    internal sealed class Observation
    {
        public int VisibilityAttempts
        {
            get; internal set;
        }
        public bool VisibilityForegroundMatched
        {
            get; internal set;
        }
        public bool VisibilityGeometryAvailable
        {
            get; internal set;
        }
        public string? VisibilityFirstHit
        {
            get; internal set;
        }
        public string? VisibilityLastHit
        {
            get; internal set;
        }
        public long SetupStartedAtMs { get; } = WindowsInterruptClock.NowMs;
        public long? SetupEndedAtMs
        {
            get; set;
        }
        public long? TextStartedAtMs
        {
            get; set;
        }
        public long? TextEndedAtMs
        {
            get; set;
        }
        public long? ClickStartedAtMs
        {
            get; set;
        }
        public long? ClickEndedAtMs
        {
            get; set;
        }
        public long? ReadStartedAtMs
        {
            get; set;
        }
        public long? LastReadStartedAtMs
        {
            get; set;
        }
        public long? LastReadEndedAtMs
        {
            get; set;
        }
        public long? ReadCompletedAtMs
        {
            get; set;
        }
        public int ReadAttempts
        {
            get; set;
        }
        public int ReadTimeouts
        {
            get; set;
        }
        public long? SelectionStartedAtMs
        {
            get; set;
        }
        public long? SelectionEndedAtMs
        {
            get; set;
        }
        public bool SelectionCompleted
        {
            get; set;
        }
        public bool ProcessAlive
        {
            get; private set;
        }
        public bool RootMatched
        {
            get; private set;
        }
        public bool FieldMatched
        {
            get; private set;
        }
        public bool FieldRootMatched
        {
            get; private set;
        }
        public bool FocusMatched
        {
            get; private set;
        }
        public bool IdentityMatched => ProcessAlive && RootMatched && FieldMatched && FieldRootMatched;

        // Fixed booleans only: no source text, handles, or raw process identities.
        internal void Observe(nint root, nint field, Process process)
        {
            ProcessAlive = !process.HasExited;
            RootMatched = N.GetWindowThreadProcessId(root, out uint rootPid) != 0 && rootPid == process.Id;
            FieldMatched = N.GetWindowThreadProcessId(field, out uint fieldPid) != 0 && fieldPid == process.Id;
            FieldRootMatched = N.GetAncestor(field, 2) == root;
            FocusMatched = N.Focused() == (root, field);
        }
    }

    internal enum FailureReason
    {
        SourceForegroundIdentityChanged,
        SourceForegroundUnavailable,
        SourceForegroundRefused,
        SourceClickObservationDpiContext,
        SourceClickIdentityChanged,
        SourceClickControlHandleMismatch,
        SourceClickControlProcessMismatch,
        SourceClickControlDocument,
        SourceClickControlTypeMismatch,
        SourceClickControlDisabled,
        SourceClickControlOffscreen,
        SourceClickGeometryUnavailable,
        SourceClickObscured,
        SourceClickDpiContext,
        SourceClickInputAlreadyHeld,
        SourceClickCursorUnavailable,
        SourceClickReleaseIncomplete,
        SourceClickIncomplete,
        SourceClickTargetChanged,
        SourceResponseIdentityOrFocusChanged,
        SourceResponseTextChanged,
    }

    internal sealed class Failure(FailureReason reason, string? detail = null) : InvalidOperationException(detail ?? reason.ToString())
    {
        internal FailureReason Reason { get; } = reason;
        internal string Code => Reason switch
        {
            FailureReason.SourceForegroundIdentityChanged => "source-foreground-identity-changed",
            FailureReason.SourceForegroundUnavailable => "source-foreground-unavailable",
            FailureReason.SourceForegroundRefused => "source-foreground-refused",
            FailureReason.SourceClickObservationDpiContext => "source-click-observation-dpi-context",
            FailureReason.SourceClickIdentityChanged => "source-click-identity-changed",
            FailureReason.SourceClickControlHandleMismatch => "source-click-control-handle-mismatch",
            FailureReason.SourceClickControlProcessMismatch => "source-click-control-process-mismatch",
            FailureReason.SourceClickControlDocument => "source-click-control-document",
            FailureReason.SourceClickControlTypeMismatch => "source-click-control-type-mismatch",
            FailureReason.SourceClickControlDisabled => "source-click-control-disabled",
            FailureReason.SourceClickControlOffscreen => "source-click-control-offscreen",
            FailureReason.SourceClickGeometryUnavailable => "source-click-geometry-unavailable",
            FailureReason.SourceClickObscured => "source-click-obscured",
            FailureReason.SourceClickDpiContext => "source-click-dpi-context",
            FailureReason.SourceClickInputAlreadyHeld => "source-click-input-already-held",
            FailureReason.SourceClickCursorUnavailable => "source-click-cursor-unavailable",
            FailureReason.SourceClickReleaseIncomplete => "source-click-release-incomplete",
            FailureReason.SourceClickIncomplete => "source-click-incomplete",
            FailureReason.SourceClickTargetChanged => "source-click-target-changed",
            FailureReason.SourceResponseIdentityOrFocusChanged => "source-response-identity-or-focus-changed",
            FailureReason.SourceResponseTextChanged => "source-response-text-changed",
            _ => "source-setup-failed"
        };
    }

    private static void Check(bool condition, FailureReason reason)
    {
        if (!condition)
            throw new Failure(reason);
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(N.Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
}
