using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using TypeLay.Core;
using TypeLay.Protocol;

namespace TypeLay.Broker;

internal sealed class UiaInputProvider : IDisposable
{
    private readonly NativeUia _native = new();
    private readonly NativeEditTransactions _edits = new();
    private readonly CacheRequest _metadataCache = MetadataCache();
    private AutomationElement? _element;
    private InputSnapshot? _previous;
    private long _inputEpoch, _focusEpoch, _lastSourceUserEpoch = -1;
    private TargetId? _anchorTarget;
    private RectPx _anchorFieldBounds, _anchorBounds;
    private long _anchorAt, _anchorUserEpoch;
    private FocusMember _lastFocus = FocusMember.Other;
    private string? _application;
    private readonly HashSet<(int, long, string)> _quarantine = new();
    public string? Application => _application;
    public void ClearDirect() => _edits.Clear();

    public (InputSnapshot?, FocusMember) Capture(BrokerRequest request)
    {
        using var trace = CaptureTrace.Start();
        (InputSnapshot?, FocusMember) Reject(string reason)
        {
            trace?.Result(reason);
            return Unavailable(request);
        }
        trace?.Stage("foreground");
        nint foreground = GetForegroundWindow();
        bool overlay = request.OverlayHwnd != 0 && foreground == (nint)request.OverlayHwnd;
        // An unfocused Chromium edit need not expose a caret/selection. Keep its binding
        // for ReturnToSource, without turning the old observation into a fresh write lease.
        if (overlay)
            return Reject("overlay-focus");
        trace?.Stage("managed-focus");
        AutomationElement? focused;
        using (_metadataCache.Activate())
            focused = AutomationElement.FocusedElement;
        if (focused is null || foreground == 0)
            return Reject("no-focused-element");
        AutomationElement element = focused;
        // Raw and managed providers must refer to the same element, captured in the same MTA.
        trace?.Stage("raw-focus");
        string token = string.Join('.', element.GetRuntimeId());
        if (_native.BindFocused() != token)
            return Reject("focused-identity-changed");
        var info = element.Cached;
        if (info.ControlType != ControlType.Edit && info.ControlType != ControlType.Document)
            return Reject("not-edit-control");
        if (info.IsPassword)
            return Reject("password-field");
        if (!info.IsEnabled)
            return Reject("disabled-field");
        if (info.IsOffscreen)
            return Reject("offscreen-field");
        trace?.Stage("process-identity");
        using var process = Process.GetProcessById(info.ProcessId);
        string app = process.ProcessName.ToLowerInvariant();
        if ((request.Excluded ?? []).Contains(app, StringComparer.OrdinalIgnoreCase)
            || app is "typelay.app" or "typelay.broker")
            return Reject("excluded-application");
        long start = process.StartTime.ToUniversalTime().Ticks;
        if (_quarantine.Contains((info.ProcessId, start, token)))
            return Reject("quarantined-target");
        nint root = foreground;
        bool inDocument = false, rootSeen = false;
        var previousTarget = _previous?.Target.AsWindows();
        bool sameBinding = previousTarget is { } previous && previous.ProcessId == info.ProcessId
            && previous.ProcessStartUtcTicks == start && previous.RootHwnd == (long)root
            && previous.ElementToken == token;
        trace?.Stage("ancestor-metadata");
        var parent = sameBinding ? null : element;
        rootSeen = sameBinding;
        for (int i = 0; i < 28 && parent is not null; i++)
        {
            var p = parent.Cached;
            if (p.ControlType == ControlType.Document)
                inDocument = true;
            if (p.NativeWindowHandle != 0 && GetAncestor((nint)p.NativeWindowHandle, 2) == root)
                rootSeen = true;
            if (inDocument && rootSeen)
                break;
            parent = TreeWalker.RawViewWalker.GetParent(parent, _metadataCache);
        }
        GetWindowThreadProcessId(root, out uint foregroundProcess);
        if (!rootSeen && foregroundProcess != info.ProcessId)
            return Reject("foreground-owner");
        trace?.Stage("editable-pattern");
        ValuePattern? value = element.TryGetCurrentPattern(ValuePattern.Pattern, out object vp) ? (ValuePattern)vp : null;
        bool? textReadOnly = value is null ? _native.ReadOnly() : null;
        // Document is also used by rich editors. Require an explicit editable text
        // attribute, never read an ordinary browser page as an input field.
        if (value is null && textReadOnly != false)
            return Reject("editable-pattern-missing");
        trace?.Stage("ime");
        ImeState ime = _native.Composition(root); // No composition text is retrieved.
        trace?.Stage("field-metadata");
        var ariaProperty = AutomationProperty.LookupById(30102);
        string aria = ariaProperty is null ? "" : element.GetCachedPropertyValue(ariaProperty, true) as string ?? "";
        bool multiline = aria.Contains("multiline=true", StringComparison.OrdinalIgnoreCase);
        var metadata = new FieldMetadata(app, info.Name ?? "", info.AutomationId ?? "", info.HelpText ?? "",
            aria, info.IsPassword, info.IsEnabled, value?.Current.IsReadOnly ?? textReadOnly != false, info.IsOffscreen,
            inDocument, multiline, value is not null, ime != ImeState.Unknown);
        if (!FieldPolicy.Allows(metadata, request.Excluded ?? []))
            return Reject("field-policy");
        var rect = info.BoundingRectangle;
        var bounds = new RectPx(rect.X, rect.Y, rect.Width, rect.Height);
        if (!bounds.IsValid)
            return Reject("invalid-bounds");
        GetWindowRect(root, out var windowRect);
        if (!bounds.Intersects(new(windowRect.Left, windowRect.Top, windowRect.Right - windowRect.Left,
                windowRect.Bottom - windowRect.Top)))
            return Reject("outside-window");
        var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{info.ProcessId}/{start}/{root}/{token}"));
        var id = new WindowsTarget(info.ProcessId, start, (long)root, info.NativeWindowHandle,
            value is null ? "uia-text-preview-v2" : "uia-value-text-v2", token, new Guid(idBytes.AsSpan(0, 16)));
        trace?.Stage("native-capture");
        if (request.CaptureDirect && _edits.Capture(id, bounds, _inputEpoch, _focusEpoch) is { } direct)
        {
            trace?.Stage("focus-recheck");
            if (!StillFocused(root, token))
                return Reject("focus-changed-native");
            if (_previous?.Target != direct.Target || _lastFocus != FocusMember.Source)
                _focusEpoch++;
            if (_previous?.Target != direct.Target || _previous.Text != direct.Text
                || _lastSourceUserEpoch != request.SourceUserEpoch)
                _inputEpoch++;
            _lastSourceUserEpoch = request.SourceUserEpoch;
            _lastFocus = FocusMember.Source;
            _element = element;
            _application = app;
            _previous = direct with
            {
                InputEpoch = _inputEpoch,
                FocusEpoch = _focusEpoch
            };
            trace?.Result("observed-native");
            return (_previous, FocusMember.Source);
        }
        trace?.Stage("caret-geometry");
        // Geometry does not participate in writing. Reuse idle line geometry for
        // at most 200 ms, refreshing immediately after typing or field movement.
        // This avoids repeated remote range calls while a Tab permit is live.
        if (request.Op == "capture" && bounds.Height > 160)
        {
            long now = WindowsInterruptClock.NowMs;
            if (_anchorTarget == id && _anchorFieldBounds == bounds && _anchorUserEpoch == request.UserEpoch
                && now - _anchorAt < 200)
                bounds = _anchorBounds;
            else
            {
                _anchorTarget = id;
                _anchorFieldBounds = bounds;
                _anchorUserEpoch = request.UserEpoch;
                _anchorAt = now;
                if (_native.CaretBounds(root) is { } caret && bounds.Contains(caret))
                    bounds = new(bounds.X, caret.Y, bounds.Width, Math.Max(22, caret.Height));
                _anchorBounds = bounds;
            }
        }
        FocusMember focus = FocusMember.Source;
        if (_previous?.Target != id || focus != _lastFocus)
            _focusEpoch++;
        _lastFocus = focus;
        if (ime != ImeState.Clear)
        {
            // Known compositions expose only their committed prefix. If a desktop
            // provider cannot report IME state, its ordinary field value can still
            // be translated for display. It is not proof of committed input and
            // must never become a write candidate.
            trace?.Stage("preview-read");
            string? committedPrefix = ime == ImeState.Composing ? _native.ReadCommittedPrefix(Limits.MaxTextUnits, root) : null;
            string? preview = ime == ImeState.Unknown ? value?.Current.Value ?? _native.ReadText(Limits.MaxTextUnits)?.Document : null;
            if (preview is not null && (preview.Length > Limits.MaxTextUnits || !Utf16.IsWellFormed(preview)
                || preview.Contains('\0')))
                preview = null;
            multiline |= preview?.Contains('\n') == true || preview?.Contains('\r') == true;
            trace?.Stage("focus-recheck");
            if (!StillFocused(root, token))
                return Reject("focus-changed-preview");
            if (_previous?.Target != id || _previous.PreviewText != preview || _previous.CommittedPrefix != committedPrefix
                || _lastSourceUserEpoch != request.SourceUserEpoch)
                _inputEpoch++;
            _lastSourceUserEpoch = request.SourceUserEpoch;
            var blocked = new InputSnapshot(id, "", new TextRange(0, 0), bounds, multiline,
                FieldAccess.Supported, ime, WindowsInterruptClock.NowMs, _inputEpoch, _focusEpoch, false,
                committedPrefix, preview);
            _element = element;
            _previous = blocked;
            _application = app;
            trace?.Result("observed-" + ime.ToString().ToLowerInvariant());
            return (blocked, focus);
        }
        trace?.Stage("text-read");
        var read = _native.ReadText(Limits.MaxTextUnits);
        string? content = value?.Current.Value ?? read?.Document;
        if (content is null)
            return Reject("text-unavailable");
        if (content.Length > Limits.MaxTextUnits || !Utf16.IsWellFormed(content) || content.Contains('\0'))
            return Reject("invalid-text");
        TextRange? selection = read is { } ranges ? TextMapping.Selection(content, ranges.Document, ranges.Prefix,
            ranges.Selection) : null;
        // The provider's value and ranges are separate remote reads. During typing
        // they can describe different revisions; preserve whole-field preview while
        // refusing selection edits and acceptance until a consistent capture arrives.
        bool writeSupported = value is not null && selection is not null;
        selection ??= new TextRange(content.Length, content.Length);
        trace?.Stage("focus-recheck");
        if (!StillFocused(root, token))
            return Reject("focus-changed-text");
        if (_previous?.Target != id || _previous.Text != content || _lastSourceUserEpoch != request.SourceUserEpoch)
            _inputEpoch++;
        _lastSourceUserEpoch = request.SourceUserEpoch;
        // Known multiline provider metadata OR actual line breaks. A single-line empty input stays single-line.
        multiline |= content.Contains('\n') || content.Contains('\r');
        var snapshot = new InputSnapshot(id, content, selection, bounds, multiline, FieldAccess.Supported,
            ime, WindowsInterruptClock.NowMs, _inputEpoch, _focusEpoch, writeSupported);
        _element = element;
        _previous = snapshot;
        _application = app;
        trace?.Result("observed-clear");
        return (snapshot, focus);
    }
    private static bool StillFocused(nint root, string token) => GetForegroundWindow() == root
        && AutomationElement.FocusedElement is { } focused && string.Join('.', focused.GetRuntimeId()) == token;

    public WriteReceipt Write(BrokerRequest request)
    {
        long writeAt = WindowsInterruptClock.NowMs;
        TraceWrite("start", request, writeAt);
        if (request.Plan is not { } plan || WindowsInterruptClock.NowMs > request.ExpiresAtMs)
            return new(WriteStatus.RejectedBeforeMutation, Denial.StaleObservation);
        if (!WindowsTarget.TryDecode(plan.Expected.Target, out var target))
            return new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged);
        bool mutation = false;
        try
        {
            var (fresh, focus) = Capture(request with
            {
                OverlayHwnd = 0
            });
            TraceWrite("captured", request, writeAt);
            if (fresh is null || focus != FocusMember.Source)
                return new(WriteStatus.RejectedBeforeMutation, Denial.NotSourceFocus);
            Denial reason = WriteSafety.Validate(plan, fresh, WindowsInterruptClock.NowMs);
            if (reason != Denial.None)
                return new(WriteStatus.RejectedBeforeMutation, reason);
            if (_element is null || GetForegroundWindow() != (nint)target.RootHwnd)
                return new(WriteStatus.RejectedBeforeMutation, Denial.EpochChanged);
            if (target.Provider != NativeEditTransactions.Provider
                && _native.Composition((nint)target.RootHwnd) != ImeState.Clear)
                return new(WriteStatus.RejectedBeforeMutation, Denial.ImeUnclear);
            TraceWrite("ime-checked", request, writeAt);
            if (WindowsInterruptClock.NowMs > request.ExpiresAtMs)
                return new(WriteStatus.RejectedBeforeMutation, Denial.StaleObservation);
            if (string.Join('.', AutomationElement.FocusedElement.GetRuntimeId()) != target.ElementToken)
                return new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged);
            if (target.Provider == NativeEditTransactions.Provider)
            {
                var nativeReceipt = _edits.Write(plan);
                return nativeReceipt.Status == WriteStatus.OutcomeUnknown ? Unknown(plan) : nativeReceipt;
            }
            // Text-only editors can preview but SetValue must never flatten their
            // formatting. Acceptance is granted only to a value-backed target.
            if (!string.Equals(target.Provider, "uia-value-text-v2", StringComparison.Ordinal))
                return new(WriteStatus.RejectedBeforeMutation, Denial.Unsupported);
            // An exact-target UIA operation; no clipboard, SendInput, Enter or select-all keystrokes.
            var value = (ValuePattern)_element.GetCurrentPattern(ValuePattern.Pattern);
            if (value.Current.Value != plan.Expected.Text || value.Current.IsReadOnly)
                return new(WriteStatus.RejectedBeforeMutation, Denial.SourceChanged);
            // The final remote value read also consumes time.
            if (WindowsInterruptClock.NowMs > request.ExpiresAtMs)
                return new(WriteStatus.RejectedBeforeMutation, Denial.StaleObservation);
            TraceWrite("before-mutation", request, writeAt);
            mutation = true;
            value.SetValue(plan.ResultText);
            if (value.Current.Value != plan.ResultText || !_native.PlaceCaret(plan.ResultText, plan.Caret, Limits.MaxTextUnits))
                return Unknown(plan);
            var (after, afterFocus) = Capture(request with
            {
                OverlayHwnd = 0
            });
            if (afterFocus != FocusMember.Source || after is null || after.Target != plan.Expected.Target
                || after.Text != plan.ResultText || after.Selection != new TextRange(plan.Caret, plan.Caret))
                return Unknown(plan);
            return new(WriteStatus.Verified, Denial.None, after);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return mutation ? Unknown(plan) : new(WriteStatus.RejectedBeforeMutation, Denial.PlatformFailure);
        }
    }
    public WriteReceipt WriteDirect(BrokerRequest request)
    {
        if (request.Plan is not { } plan || request.BoundTarget != plan.Expected.Target
            || !WindowsTarget.TryDecode(plan.Expected.Target, out var target)
            || target.Provider != NativeEditTransactions.Provider || !plan.Expected.DirectWriteSupported)
            return new(WriteStatus.RejectedBeforeMutation, Denial.Unsupported);
        long now = WindowsInterruptClock.NowMs;
        var basis = plan.Expected with
        {
            ObservedAtMs = now
        };
        Denial validity = WriteSafety.Validate(plan, basis, now);
        if (validity != Denial.None || plan.Scope != basis.TranslationScope)
            return new(WriteStatus.RejectedBeforeMutation, validity == Denial.None ? Denial.BadPlan : validity);
        bool dispatched = false;
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != target.ProcessStartUtcTicks
                || _quarantine.Contains((target.ProcessId, target.ProcessStartUtcTicks, target.ElementToken)))
                return new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged);
            // Metadata is reacquired for the original HWND, never FocusedElement.
            // The native transaction subsequently checks its lifetime and text in
            // the target's GUI thread. There is no UIA SetValue fallback.
            using (_metadataCache.Activate())
            {
                var element = AutomationElement.FromHandle((nint)target.ControlHwnd);
                var info = element.Cached;
                if (info.ProcessId != target.ProcessId || info.NativeWindowHandle != target.ControlHwnd
                    || string.Join('.', element.GetRuntimeId()) != target.ElementToken)
                    return new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged);
                var ariaProperty = AutomationProperty.LookupById(30102);
                string aria = ariaProperty is null ? "" : element.GetCachedPropertyValue(ariaProperty, true) as string ?? "";
                var metadata = new FieldMetadata(process.ProcessName.ToLowerInvariant(), info.Name ?? "",
                    info.AutomationId ?? "", info.HelpText ?? "", aria, info.IsPassword, info.IsEnabled,
                    !element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern) || ((ValuePattern)pattern).Current.IsReadOnly,
                    info.IsOffscreen, false, basis.Multiline, true, true);
                if (!FieldPolicy.Allows(metadata, request.Excluded ?? []))
                    return new(WriteStatus.RejectedBeforeMutation, Denial.Sensitive);
            }
            dispatched = true;
            var receipt = _edits.Write(plan);
            return receipt.Status == WriteStatus.OutcomeUnknown ? Unknown(plan) : receipt;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return dispatched ? Unknown(plan) : new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged);
        }
    }

    private static void TraceWrite(string stage, BrokerRequest request, long started)
    {
        string? path = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
        if (string.IsNullOrWhiteSpace(path))
            return;
        long now = WindowsInterruptClock.NowMs;
        try
        {
            System.IO.File.AppendAllText(path + ".write.jsonl", "{\"atMs\":" + now + ",\"stage\":\"" + stage
            + "\",\"elapsedMs\":" + (now - started) + ",\"remainingMs\":" + (request.ExpiresAtMs - now) + "}" + Environment.NewLine);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }
    private WriteReceipt Unknown(WritePlan plan)
    {
        var t = plan.Expected.Target.AsWindows();
        _quarantine.Add((t.ProcessId, t.ProcessStartUtcTicks, t.ElementToken));
        return new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
    }
    public bool ReturnToSource(TargetId? target)
    {
        if (target is null || _previous?.Target != target || _element is null
            || !WindowsTarget.TryDecode(target.Value, out var binding))
            return false;
        if (string.Join('.', _element.GetRuntimeId()) != binding.ElementToken)
            return false;
        if (GetForegroundWindow() != (nint)binding.RootHwnd && !SetForegroundWindow((nint)binding.RootHwnd))
            return false;
        _element.SetFocus();
        return string.Join('.', AutomationElement.FocusedElement.GetRuntimeId()) == binding.ElementToken;
    }
    public void Clear(TargetId? expected = null)
    {
        if (expected is { } id && _previous?.Target != id)
            return;
        _previous = null;
        _element = null;
        _application = null;
        _lastFocus = FocusMember.Other;
        _anchorTarget = null;
        _focusEpoch++;
        _native.ClearElement();
    }
    private (InputSnapshot?, FocusMember) Unavailable(BrokerRequest request)
    {
        // Also handles a capture that started on the source and lost focus mid-call.
        if (request.OverlayHwnd != 0 && GetForegroundWindow() == (nint)request.OverlayHwnd
            && request.BoundTarget is { } target && _previous?.Target == target && _element is not null)
        {
            if (_lastFocus != FocusMember.Overlay)
                _focusEpoch++;
            _lastFocus = FocusMember.Overlay;
            return (_previous, FocusMember.Overlay); // Preserve the original ObservedAtMs.
        }
        Clear();
        return (null, FocusMember.Other);
    }
    private static CacheRequest MetadataCache()
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        foreach (var property in new[] { AutomationElement.ControlTypeProperty, AutomationElement.IsPasswordProperty,
            AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, AutomationElement.ProcessIdProperty,
            AutomationElement.NameProperty, AutomationElement.AutomationIdProperty, AutomationElement.HelpTextProperty,
            AutomationElement.NativeWindowHandleProperty, AutomationElement.BoundingRectangleProperty,
            AutomationProperty.LookupById(30102) })
        {
            if (property is not null)
            {
                cache.Add(property);
            }
        }
        return cache;
    }
    public void Dispose()
    {
        _edits.Dispose();
        _native.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rectangle rect);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
}
