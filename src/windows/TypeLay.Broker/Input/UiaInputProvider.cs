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
    private readonly NativeEditTransactions _edits;
    private readonly CacheRequest _metadataCache = MetadataCache();
    private AutomationElement? _element;
    private InputSnapshot? _previous;
    private OwnedCaptureDescriptor? _ownedCapture;
    private long _inputEpoch, _focusEpoch, _lastSourceUserEpoch = -1;
    private TargetId? _anchorTarget;
    private RectPx _anchorFieldBounds, _anchorBounds;
    private long _anchorAt, _anchorUserEpoch;
    private FocusMember _lastFocus = FocusMember.Other;
    private string? _application;
    private readonly HashSet<(int, long, string)> _quarantine = new();
    private readonly bool _sourcePrototype;
    private SourceAcceptanceRequest? _sourceOwner;
    private readonly bool _sourceMessageRoute;
    private bool _sourceCompletionTransferred;
    public UiaInputProvider(bool sourcePrototype = false, bool sourceMessageRoute = false, bool orderedSourceMessageWitness = false)
    {
        _sourcePrototype = sourcePrototype;
        _sourceMessageRoute = sourcePrototype && sourceMessageRoute;
        _edits = new(sourceMessageRoute: _sourceMessageRoute, orderedSourceMessageWitness: orderedSourceMessageWitness);
    }
    public string? Application => _application;

    public SourceAcceptanceResult SourceAcceptance(BrokerRequest request)
    {
        var identity = request.SourceAcceptance;
        if (identity is null)
            throw new ArgumentException("Missing source owner.");
        SourceAcceptanceResult Result(bool installed = false, bool quiet = false, bool claimed = false,
            SourceActivationEndpoint? endpoint = null, WriteReceipt? receipt = null, bool acknowledged = false) =>
            new(identity.Generation, identity.SessionRevision, identity.Target, identity.Grant,
                installed, quiet, claimed, endpoint, receipt, acknowledged);
        if (!_sourcePrototype || identity.Generation == Guid.Empty || identity.Grant == Guid.Empty
            || identity.SessionRevision <= 0 || identity.Chord is not { } chord || !chord.IsValid(identity.AcceptKey))
            return Result(receipt: new(WriteStatus.RejectedBeforeMutation, Denial.Unsupported));
        if (request.Op == "source-install")
        {
            // Reusing the last observation must not bypass an earlier uncertain
            // native write, even if the original Runtime session no longer exists.
            if (WindowsTarget.TryDecode(identity.Target, out var target)
                && _quarantine.Contains((target.ProcessId, target.ProcessStartUtcTicks, target.ElementToken)))
                return Result(receipt: new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged));
            if (_sourceOwner is { Permit: not null } || _sourceOwner?.Grant == identity.Grant
                || identity.Permit is not { } permit
                || permit.Plan.Id != identity.Grant || permit.Plan.Expected.Target != identity.Target
                || _previous is not { } current
                || permit.Plan.Expected with
                {
                    ObservedAtMs = current.ObservedAtMs,
                    Bounds = current.Bounds
                } != current
                || chord.Control is { } control && (current.InputEpoch != control.BeforeInputEpoch + 1
                    || current.ObservedAtMs < control.ObservedAtMs)
                || _ownedCapture is not null || _lastFocus != FocusMember.Source)
                return Result(receipt: new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged));
            _sourceCompletionTransferred = false;
            var installed = _edits.InstallSourceAcceptance(identity.Generation, permit, identity.AcceptKey, chord);
            if (!installed.Quiet)
                _sourceOwner = identity;
            var endpoint = installed.Installed ? _edits.ReserveSourceActivation(identity.Generation, identity.Grant) : null;
            return Result(installed.Installed && endpoint is not null, installed.Quiet, installed.Claimed,
                endpoint is null ? null : new(endpoint.Nonce, endpoint.Command), installed.Receipt, installed.Acknowledged);
        }
        if (request.Op is not ("source-retire" or "source-receipt" or "source-poll")
            || request.Op == "source-poll" && !_sourceMessageRoute || identity.Permit is not null
            || _sourceOwner is not { } owner || owner.Generation != identity.Generation
            || owner.SessionRevision != identity.SessionRevision || owner.Target != identity.Target || owner.Grant != identity.Grant
            || owner.AcceptKey != identity.AcceptKey || owner.Chord != identity.Chord)
            return Result(receipt: new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged));
        var terminal = request.Op == "source-poll"
            ? _edits.PollSourceAcceptance(identity.Generation, identity.Grant)
            : _edits.RetireSourceAcceptance(identity.Generation, identity.Grant);
        // Claim plus an uncertain response is already unsafe for every writer,
        // including Direct. Quiet governs lifetime retirement, not quarantine.
        // A source-message transfer may precede Quiet. Its explicit transfer
        // flag prevents a later metadata-only Unknown from quarantining Verified;
        // the old sent route still retains Permit through its terminal transfer.
        if (terminal.Claimed && !_sourceCompletionTransferred && owner.Permit is { } original
            && terminal.Receipt is null or { Status: WriteStatus.OutcomeUnknown })
            terminal = terminal with
            {
                Receipt = Unknown(original.Plan)
            };
        if (_sourceMessageRoute && terminal.Claimed && terminal.Receipt is not null)
        {
            // Completion transfer and native tail/resource retirement are
            // independent. A later metadata-only response cannot undo Verified.
            if (_sourceCompletionTransferred)
                terminal = terminal with
                {
                    Receipt = null
                };
            else
                _sourceCompletionTransferred = true;
        }
        // Keep this exact owner for duplicate terminal reads. A new installation
        // may replace it only once resource quiet has actually been confirmed.
        if (terminal.Quiet)
        {
            _sourceOwner = owner with
            {
                Permit = null
            };
            _edits.ReleaseSourceCompletionBody(identity.Generation, identity.Grant);
        }
        return Result(installed: terminal.Installed, quiet: terminal.Quiet, claimed: terminal.Claimed, receipt: terminal.Receipt,
            acknowledged: terminal.Acknowledged);
    }

    public void ClearDirect() => _edits.Clear();

    public (PauseAnchor?, FocusMember) ObservePause(BrokerRequest request)
    {
        // Pausing must not retain a previous body or bind an executable source.
        // This path never reads Value/Text, selection, caret, or composition text.
        Clear();
        static (PauseAnchor?, FocusMember) Reject() => (null, FocusMember.Other);
        nint root = GetForegroundWindow();
        if (root == 0 || root == (nint)request.OverlayHwnd || GetAncestor(root, 2) != root)
            return Reject();
        AutomationElement? element;
        using (_metadataCache.Activate())
            element = AutomationElement.FocusedElement;
        if (element is null)
            return Reject();
        var info = element.Cached;
        bool combo = info.ControlType == ControlType.ComboBox;
        if (info.ControlType != ControlType.Edit && info.ControlType != ControlType.Document && !combo)
            return Reject();
        // Missing password/enabled/visibility metadata is not a safe field.
        if (element.GetCachedPropertyValue(AutomationElement.IsPasswordProperty, true) is not false
            || element.GetCachedPropertyValue(AutomationElement.IsEnabledProperty, true) is not true
            || element.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty, true) is not false)
            return Reject();
        using var process = Process.GetProcessById(info.ProcessId);
        string app = process.ProcessName.ToLowerInvariant();
        if ((request.Excluded ?? []).Contains(app, StringComparer.OrdinalIgnoreCase)
            || app is "typelay.app" or "typelay.broker")
            return Reject();
        long start = process.StartTime.ToUniversalTime().Ticks;
        string token = string.Join('.', element.GetRuntimeId());
        if (_quarantine.Contains((info.ProcessId, start, token)))
            return Reject();
        bool inDocument = false, rootSeen = false;
        var parent = element;
        for (int i = 0; i < 28 && parent is not null; i++)
        {
            var metadata = parent.Cached;
            inDocument |= metadata.ControlType == ControlType.Document;
            rootSeen |= metadata.NativeWindowHandle != 0 && GetAncestor((nint)metadata.NativeWindowHandle, 2) == root;
            if (inDocument && rootSeen)
                break;
            parent = TreeWalker.RawViewWalker.GetParent(parent, _metadataCache);
        }
        GetWindowThreadProcessId(root, out uint foregroundProcess);
        if (!rootSeen && foregroundProcess != info.ProcessId)
            return Reject();
        ValuePattern? value = element.TryGetCurrentPattern(ValuePattern.Pattern, out object vp) ? (ValuePattern)vp : null;
        bool textEditable = false;
        if ((value is null || combo) && element.TryGetCurrentPattern(TextPattern.Pattern, out object tp))
            textEditable = ((TextPattern)tp).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
        bool editable = value is not null ? !value.Current.IsReadOnly : textEditable;
        if (!editable || (combo && (value is null || !textEditable)))
            return Reject();
        var ariaProperty = AutomationProperty.LookupById(30102);
        string aria = ariaProperty is null ? "" : element.GetCachedPropertyValue(ariaProperty, true) as string ?? "";
        var field = new FieldMetadata(app, info.Name ?? "", info.AutomationId ?? "", info.HelpText ?? "", aria,
            false, true, false, false, inDocument, false, value is not null, false);
        if (!FieldPolicy.Allows(field, request.Excluded ?? []))
            return Reject();
        var rect = info.BoundingRectangle;
        var bounds = new RectPx(rect.X, rect.Y, rect.Width, rect.Height);
        if (!bounds.IsValid || !GetWindowRect(root, out var windowRect)
            || !bounds.Intersects(new(windowRect.Left, windowRect.Top, windowRect.Right - windowRect.Left,
                windowRect.Bottom - windowRect.Top)) || !StillFocused(root, token))
            return Reject();
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes($"{info.ProcessId}/{start}/{root}/{token}"));
        TargetId target = new WindowsTarget(info.ProcessId, start, (long)root, info.NativeWindowHandle,
            "uia-pause-anchor-v1", token, new Guid(identity.AsSpan(0, 16)));
        _application = app;
        return (new(target, bounds, FieldAccess.Supported, WindowsInterruptClock.NowMs, request.ForegroundEpoch), FocusMember.Source);
    }

    public (InputSnapshot?, FocusMember) Capture(BrokerRequest request)
    {
        // Owned observations cannot cross into the ordinary capture/write path.
        if (request.Op is not ("capture" or "write") || request.OwnedCapture is not null || request.WakeOrigin is not null)
        {
            Clear();
            return (null, FocusMember.Other);
        }
        if (_ownedCapture is not null)
            Clear();
        return CaptureCore(request);
    }

    public (InputSnapshot?, FocusMember) CaptureWake(BrokerRequest request)
    {
        // A read cannot steal retirement from an installed source grant.
        // Direct observations remain reusable only through the read-only path.
        if (_edits.HasSourceAcceptanceOwner)
            return (null, FocusMember.Other);
        // A no-lease Wake never recovers an old body, bound target or owned
        // descriptor. The supplied identity can authorize only this fresh read.
        Clear();
        if (request.Op != "capture-wake" || request.CaptureDirect || request.BoundTarget is not null
            || request.Plan is not null || request.OwnedCapture is not null
            || request.SourceGlossaryReturn is not null || request.SourceAcceptance is not null
            || request.WakeOrigin is not { IsValid: true } origin
            || origin.RootHwnd == request.OverlayHwnd
            || !WakeOriginCurrent(request))
            return (null, FocusMember.Other);
        try
        {
            var (snapshot, focus) = CaptureCore(request);
            if (snapshot is null || focus != FocusMember.Source || !WakeOriginCurrent(request))
            {
                Clear();
                return (null, FocusMember.Other);
            }
            _previous = snapshot with
            {
                WriteSupported = false,
                DirectWriteSupported = false
            };
            return (_previous, FocusMember.Source);
        }
        catch
        {
            Clear();
            throw;
        }
    }

    private static bool WakeOriginCurrent(BrokerRequest request)
    {
        if (request.WakeOrigin is not { IsValid: true } origin
            || origin.TriggeredAtMs > long.MaxValue - Limits.ObservationMaxAgeMs
            || request.ExpiresAtMs != origin.TriggeredAtMs + Limits.ObservationMaxAgeMs)
            return false;
        long now = WindowsInterruptClock.NowMs;
        if (now < origin.TriggeredAtMs || now >= request.ExpiresAtMs)
            return false;
        try
        {
            using var process = Process.GetProcessById(origin.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != origin.ProcessStartUtcTicks)
                return false;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        nint root = (nint)origin.RootHwnd, field = (nint)origin.ControlHwnd;
        nint foreground = GetForegroundWindow();
        if ((foreground != root && foreground != field) || GetAncestor(root, 2) != root
            || GetAncestor(field, 2) != root
            || GetWindowThreadProcessId(root, out uint rootProcess) != origin.ThreadId || rootProcess != origin.ProcessId
            || GetWindowThreadProcessId(field, out uint fieldProcess) != origin.ThreadId || fieldProcess != origin.ProcessId)
            return false;
        var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(origin.ThreadId, ref gui) && gui.Focus == field
            && GetForegroundWindow() == foreground && WindowsInterruptClock.NowMs < request.ExpiresAtMs;
    }

    private static bool WakeElementCurrent(BrokerRequest request, AutomationElement element)
    {
        if (request.WakeOrigin is not { } origin)
            return true;
        // A virtual child under the right root is not the field captured by the
        // key. Never substitute an ancestor HWND or root-only UIA identity.
        return WakeOriginCurrent(request)
            && element.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty, true) is int hwnd
            && hwnd != 0 && hwnd == origin.ControlHwnd
            && element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty, true) is int process
            && process == origin.ProcessId && WakeOriginCurrent(request);
    }

    public (InputSnapshot?, FocusMember) CaptureOwned(BrokerRequest request)
    {
        using var trace = CaptureTrace.Start();
        (InputSnapshot?, FocusMember) Reject(string reason)
        {
            trace?.Result(reason);
            Clear();
            return (null, FocusMember.Other);
        }
        trace?.Stage("owned-identity");
        if (request.Op != "capture-owned" || request.CaptureDirect || request.WakeOrigin is not null
            || request.OwnedCapture is not { } owned
            || owned.RootHwnd == request.OverlayHwnd || !NativeEditTransactions.IsCurrentOwned(owned))
            return Reject("owned-identity-changed");

        // Reuse only the reference for this exact native lifetime. Every field
        // policy value is refreshed; neither the old cache nor the UIA object
        // proves native focus. A changed descriptor starts a new lookup.
        trace?.Stage("owned-field-metadata");
        bool refresh = _ownedCapture == owned && _element is not null;
        WindowsTarget bound = default;
        if (refresh && (_previous is null || !WindowsTarget.TryDecode(_previous.Target, out bound)
            || bound.ProcessId != owned.ProcessId || bound.ProcessStartUtcTicks != owned.ProcessStartUtcTicks
            || bound.RootHwnd != owned.RootHwnd || bound.ControlHwnd != owned.ControlHwnd
            || bound.Provider != NativeEditTransactions.Provider))
            return Reject("owned-bound-identity-changed");
        AutomationElement element;
        if (refresh)
        {
            try
            {
                element = _element!.GetUpdatedCache(_metadataCache);
            }
            catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException
                or COMException or ArgumentException)
            {
                return Reject("owned-metadata-refresh-failed"); // No rediscovery fallback.
            }
        }
        else
            using (_metadataCache.Activate())
                element = AutomationElement.FromHandle((nint)owned.ControlHwnd);
        var info = element.Cached;
        if (info.ProcessId != owned.ProcessId || info.NativeWindowHandle != owned.ControlHwnd
            || (info.ControlType != ControlType.Edit && info.ControlType != ControlType.Document)
            || !NativeEditTransactions.IsCurrentOwned(owned))
            return Reject("owned-identity-changed");
        if (element.GetCachedPropertyValue(AutomationElement.IsPasswordProperty, true) is not false
            || element.GetCachedPropertyValue(AutomationElement.IsEnabledProperty, true) is not true
            || element.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty, true) is not false)
            return Reject("owned-field-metadata-missing");

        trace?.Stage("process-identity");
        using var process = Process.GetProcessById(owned.ProcessId);
        long start = process.StartTime.ToUniversalTime().Ticks;
        if (start != owned.ProcessStartUtcTicks)
            return Reject("owned-process-changed");
        string app = process.ProcessName.ToLowerInvariant();
        string token = string.Join('.', element.GetRuntimeId());
        if (refresh && token != bound.ElementToken)
            return Reject("owned-provider-identity-changed");
        if (_quarantine.Contains((owned.ProcessId, start, token)))
            return Reject("quarantined-target");

        // ES_READONLY is live stock-Edit metadata, not a promise made by the
        // practice configuration. Native op6 qualifies it again before reading.
        long style = (long)GetWindowLongPtrW((nint)owned.ControlHwnd, -16);
        if (Marshal.GetLastPInvokeError() != 0 || (style & 0x40000000) == 0)
            return Reject("owned-style-unavailable");
        var ariaProperty = AutomationProperty.LookupById(30102);
        string aria = ariaProperty is null ? "" : element.GetCachedPropertyValue(ariaProperty, true) as string ?? "";
        var metadata = new FieldMetadata(app, info.Name ?? "", info.AutomationId ?? "", info.HelpText ?? "",
            aria, info.IsPassword, info.IsEnabled, (style & 0x800) != 0, info.IsOffscreen,
            false, (style & 4) != 0, false, false);
        if (!FieldPolicy.Allows(metadata, request.Excluded ?? []))
            return Reject("field-policy");
        var rect = info.BoundingRectangle;
        var bounds = new RectPx(rect.X, rect.Y, rect.Width, rect.Height);
        if (!bounds.IsValid || !GetWindowRect((nint)owned.RootHwnd, out var windowRect)
            || !bounds.Intersects(new(windowRect.Left, windowRect.Top, windowRect.Right - windowRect.Left,
                windowRect.Bottom - windowRect.Top)))
            return Reject("invalid-bounds");

        var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{owned.ProcessId}/{start}/{owned.RootHwnd}/{token}"));
        var id = new WindowsTarget(owned.ProcessId, start, owned.RootHwnd, owned.ControlHwnd,
            NativeEditTransactions.Provider, token, new Guid(idBytes.AsSpan(0, 16)));
        trace?.Stage("native-capture");
        // Only native op6 reads the body/selection and establishes IME Clear.
        // Failure cannot fall through to the ordinary UIA capture/write route.
        if (!NativeEditTransactions.IsCurrentOwned(owned)
            || _edits.CaptureOwned(id, owned, bounds, _inputEpoch) is not { } captured)
            return Reject("owned-native-unavailable");
        // Discovery identity and issued native binding have distinct lifetimes.
        // The fresh metadata matched the original identity fields above; only
        // the native read can confirm the same issued binding, including lifetime.
        if (refresh && captured.Target != _previous!.Target)
            return Reject("owned-bound-identity-changed");
        if (!NativeEditTransactions.IsCurrentOwned(owned))
            return Reject("owned-focus-changed");
        if (_previous?.Target != captured.Target || _previous.Text != captured.Text
            || _lastSourceUserEpoch != request.SourceUserEpoch)
            _inputEpoch++;
        _lastSourceUserEpoch = request.SourceUserEpoch;
        _lastFocus = FocusMember.Source;
        _element = element;
        _application = app;
        _ownedCapture = owned;
        _previous = captured with
        {
            InputEpoch = _inputEpoch,
            FocusEpoch = owned.NativeFocusRevision
        };
        trace?.Result("observed-owned-native");
        return (_previous, FocusMember.Source);
    }

    private (InputSnapshot?, FocusMember) CaptureCore(BrokerRequest request)
    {
        using var trace = CaptureTrace.Start();
        (InputSnapshot?, FocusMember) Reject(string reason)
        {
            trace?.Result(reason);
            return Unavailable(request);
        }
        if (request.WakeOrigin is not null && !WakeOriginCurrent(request))
            return Reject("wake-origin-changed");
        trace?.Stage("foreground");
        nint foreground = GetForegroundWindow();
        nint root = GetAncestor(foreground, 2);
        bool overlay = request.OverlayHwnd != 0 && root == (nint)request.OverlayHwnd;
        // An unfocused Chromium edit need not expose a caret/selection. Keep its binding
        // for ReturnToSource, without turning the old observation into a fresh write lease.
        if (overlay)
            return Reject("overlay-focus");
        trace?.Stage("managed-focus");
        AutomationElement? focused;
        using (_metadataCache.Activate())
            focused = AutomationElement.FocusedElement;
        if (focused is null || root == 0)
            return Reject("no-focused-element");
        AutomationElement element = focused;
        if (!WakeElementCurrent(request, element))
            return Reject("wake-field-changed");
        // Raw and managed providers must refer to the same element, captured in the same MTA.
        trace?.Stage("raw-focus");
        string token = string.Join('.', element.GetRuntimeId());
        if (_native.BindFocused() != token)
            return Reject("focused-identity-changed");
        var info = element.Cached;
        if (request.WakeOrigin is { } wake
            && (info.ProcessId != wake.ProcessId || info.NativeWindowHandle != wake.ControlHwnd
                || element.GetCachedPropertyValue(AutomationElement.IsPasswordProperty, true) is not false
                || element.GetCachedPropertyValue(AutomationElement.IsEnabledProperty, true) is not true
                || element.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty, true) is not false))
            return Reject("wake-field-metadata-missing");
        // RootHwnd is a top-level identity, not the raw foreground handle. The
        // exceptional child must be this exact field, never a sibling or owned
        // dialog. Native lifetime/focus/IME checks remain mandatory below.
        if (foreground != root && foreground != (nint)info.NativeWindowHandle)
            return Reject("foreground-not-focused-field");
        bool comboBox = info.ControlType == ControlType.ComboBox;
        if (info.ControlType != ControlType.Edit && info.ControlType != ControlType.Document && !comboBox)
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
        bool? textReadOnly = value is null || comboBox ? _native.ReadOnly() : null;
        // Editable web search boxes may expose ComboBox rather than Edit. The
        // role alone also describes selectors: require both an editable value
        // and an editable text range before reading any content, and preview only.
        if (comboBox && (value?.Current.IsReadOnly != false || textReadOnly != false))
            return Reject("combobox-not-editable-text");
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
        if (!WakeElementCurrent(request, element))
            return Reject("wake-before-body-changed");
        trace?.Stage("native-capture");
        // Cross-thread IMM failure does not establish the source thread's IME
        // state. For an ordinary observation only, the qualified stock Edit
        // reader may supply real source-thread evidence without write capability.
        // An exact foreground child also needs this same native observation,
        // rather than a UIA-only identity, even when cross-thread IMM was Clear.
        InputSnapshot? native = !comboBox && request.CaptureDirect
            ? _edits.Capture(id, bounds, _inputEpoch, _focusEpoch)
            : !comboBox && request.Op is ("capture" or "capture-wake") && (_sourcePrototype || ime == ImeState.Unknown
                || foreground != root && ime == ImeState.Clear)
                ? request.WakeOrigin is not null
                    ? _edits.ObserveWake(id, bounds, _inputEpoch, _focusEpoch)
                    : _edits.Observe(id, bounds, _inputEpoch, _focusEpoch) : null;
        if (native is { } observed)
        {
            trace?.Stage("focus-recheck");
            if (!StillFocused(root, token, observed.Target.AsWindows()) || !WakeElementCurrent(request, element))
                return Reject("focus-changed-native");
            if (_previous?.Target != observed.Target || _lastFocus != FocusMember.Source)
                _focusEpoch++;
            if (_previous?.Target != observed.Target || _previous.Text != observed.Text
                || _lastSourceUserEpoch != request.SourceUserEpoch)
                _inputEpoch++;
            _lastSourceUserEpoch = request.SourceUserEpoch;
            _lastFocus = FocusMember.Source;
            _element = element;
            _application = app;
            _previous = observed with
            {
                InputEpoch = _inputEpoch,
                FocusEpoch = _focusEpoch,
                WriteSupported = request.WakeOrigin is null && (observed.WriteSupported || _sourcePrototype),
                DirectWriteSupported = request.WakeOrigin is null && observed.DirectWriteSupported
            };
            trace?.Result("observed-native");
            return (_previous, FocusMember.Source);
        }
        // A raw child cannot fall back from failed native identity/IME evidence
        // into a new UIA binding. In particular, Unknown stays a refusal.
        if (foreground != root)
            return Reject("native-child-unconfirmed");
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
            if (!WakeElementCurrent(request, element))
                return Reject("wake-before-preview-changed");
            trace?.Stage("preview-read");
            string? committedPrefix = ime == ImeState.Composing ? _native.ReadCommittedPrefix(Limits.MaxTextUnits, root) : null;
            string? preview = ime == ImeState.Unknown ? value?.Current.Value ?? _native.ReadText(Limits.MaxTextUnits)?.Document : null;
            if (preview is not null && (preview.Length > Limits.MaxTextUnits || !Utf16.IsWellFormed(preview)
                || preview.Contains('\0')))
                preview = null;
            multiline |= preview?.Contains('\n') == true || preview?.Contains('\r') == true;
            trace?.Stage("focus-recheck");
            if (!StillFocused(root, token) || !WakeElementCurrent(request, element))
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
        if (!WakeElementCurrent(request, element))
            return Reject("wake-before-text-changed");
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
        bool writeSupported = request.WakeOrigin is null && !comboBox && value is not null && selection is not null;
        selection ??= new TextRange(content.Length, content.Length);
        trace?.Stage("focus-recheck");
        if (!StillFocused(root, token) || !WakeElementCurrent(request, element))
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
    private bool StillFocused(nint root, string token, WindowsTarget? native = null) =>
        (GetForegroundWindow() == root || native is { } target && _edits.ConfirmsReturnFocus(target))
        && AutomationElement.FocusedElement is { } focused && string.Join('.', focused.GetRuntimeId()) == token;

    public WriteReceipt Write(BrokerRequest request)
    {
        long writeAt = WindowsInterruptClock.NowMs;
        TraceWrite("start", request, writeAt);
        if (request.Plan is not { } plan || WindowsInterruptClock.NowMs > request.ExpiresAtMs)
            return new(WriteStatus.RejectedBeforeMutation, Denial.StaleObservation);
        if (!WindowsTarget.TryDecode(plan.Expected.Target, out var target))
            return new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged);
        if (request.OwnedCapture is not null || (target.Provider == NativeEditTransactions.Provider
            && !plan.Expected.DirectWriteSupported))
            return new(WriteStatus.RejectedBeforeMutation, Denial.Unsupported);
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
            if (_element is null || (GetForegroundWindow() != (nint)target.RootHwnd
                && (target.Provider != NativeEditTransactions.Provider || !_edits.ConfirmsReturnFocus(target))))
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
                // A dispatch or result-cleanup exception cannot prove that no text was replaced.
                mutation = true;
                var nativeReceipt = _edits.WriteForeground(plan, request.ExpiresAtMs);
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
                // The system multiline Edit exposes TextPattern without ValuePattern.
                // Recheck the original element's explicit readonly attribute; missing or
                // mixed evidence still rejects. Native qualification is repeated atomically
                // at mutation, so a text pattern alone never authorizes a generic UIA write.
                bool readOnly = element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern)
                    ? ((ValuePattern)pattern).Current.IsReadOnly
                    : !element.TryGetCurrentPattern(TextPattern.Pattern, out object textPattern)
                        || ((TextPattern)textPattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not false;
                var metadata = new FieldMetadata(process.ProcessName.ToLowerInvariant(), info.Name ?? "",
                    info.AutomationId ?? "", info.HelpText ?? "", aria, info.IsPassword, info.IsEnabled,
                    readOnly, info.IsOffscreen, false, basis.Multiline, true, true);
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
    internal sealed record ReturnForegroundFacts(bool IsControl, bool RootIsSource, bool RootOwnerIsSource,
        bool OwnerIsSource, bool? IsChild, string WindowClass)
    {
        public bool NativeFocusRestored
        {
            get; init;
        }
        public long? ActivationCompletedAtMs
        {
            get; init;
        }
        public long? CorrectionStartedAtMs
        {
            get; init;
        }
        public long? CorrectionCompletedAtMs
        {
            get; init;
        }
        public bool? BeforeCorrectionIsRoot
        {
            get; init;
        }
        public bool? BeforeCorrectionIsControl
        {
            get; init;
        }
    }

    private static ReturnForegroundFacts DescribeReturnForeground(nint foreground, WindowsTarget binding)
    {
        // Only a rejected same-process snapshot is classified. Never read a
        // title or emit handles/class text, and never use these facts to authorize.
        var name = new StringBuilder(64);
        int length = GetClassNameW(foreground, name, name.Capacity);
        string kind = length == 0 ? "Unavailable" : name.ToString() switch
        {
            "Edit" => "Edit",
            "IME" => "Ime",
            "#32770" => "Dialog",
            _ => "Other"
        };
        Marshal.SetLastPInvokeError(0);
        long style = (long)GetWindowLongPtrW(foreground, -16);
        bool? child = style != 0 || Marshal.GetLastPInvokeError() == 0 ? (style & 0x40000000) != 0 : null;
        return new(foreground == (nint)binding.ControlHwnd,
            GetAncestor(foreground, 2) == (nint)binding.RootHwnd,
            GetAncestor(foreground, 3) == (nint)binding.RootHwnd,
            GetWindow(foreground, 4) == (nint)binding.RootHwnd, child, kind);
    }

    private static bool CompleteReturnActivation(BrokerRequest request, WindowsTarget binding, out string reason)
    {
        long remaining = request.ExpiresAtMs - WindowsInterruptClock.NowMs;
        reason = "return-expired";
        if (remaining <= 0 || remaining > 800)
            return false;
        reason = "activation-barrier";
        // ABORTIFHUNG | ERRORONEXIT, never NOTIMEOUTIFNOTHUNG. Only the original
        // source root receives WM_NULL; this cannot activate or authorize a write.
        if (SendMessageTimeoutW((nint)binding.RootHwnd, 0, 0, 0, 0x22, (uint)remaining, out _) == 0)
            return false;
        reason = "return-expired";
        return WindowsInterruptClock.NowMs < request.ExpiresAtMs;
    }

    public bool ReturnToSource(BrokerRequest request, out string reason, out ReturnForegroundFacts? foregroundFacts)
    {
        foregroundFacts = null;
        // Fixed outcome/stage names only. The caller writes them after replying.
        bool glossary = request.Op == "return-glossary";
        using var authorization = glossary ? SourceGlossaryReturnGuard.TryOpen(request) : null;
        reason = "return-authorization";
        if (glossary && authorization is null)
            return false;
        var target = request.BoundTarget;
        reason = "target-missing";
        if (target is null)
            return false;
        reason = "cached-target-changed";
        if (_previous?.Target != target)
            return false;
        reason = "cached-element-missing";
        if (_element is null)
            return false;
        reason = "target-invalid";
        if (!WindowsTarget.TryDecode(target.Value, out var binding))
            return false;
        if (glossary && binding.Provider != NativeEditTransactions.Provider)
            return false;
        reason = "element-identity";
        if (string.Join('.', _element.GetRuntimeId()) != binding.ElementToken)
        {
            reason = "element-identity-changed";
            return false;
        }
        reason = "native-source-changed";
        if (binding.Provider == NativeEditTransactions.Provider
            && (glossary ? !_edits.ConfirmsReturnSource(_previous) : !_edits.ConfirmsReturnIdentity(binding)))
            return false;
        if (glossary && !authorization!.BeforeFocus((nint)request.OverlayHwnd, out reason))
        {
            reason = "return-before-activation-" + reason;
            return false;
        }
        reason = "return-expired";
        if (!glossary && (request.ExpiresAtMs <= 0 || request.ExpiresAtMs - WindowsInterruptClock.NowMs is <= 0 or > 800))
            return false;
        reason = "foreground";
        if (GetForegroundWindow() != (nint)binding.RootHwnd && !SetForegroundWindow((nint)binding.RootHwnd))
        {
            reason = "foreground-denied";
            return false;
        }
        if (glossary)
        {
            // SetForegroundWindow crosses input queues. Complete that one
            // activation on the original root, without correcting field focus.
            // Activation is not yet acknowledged across the input queues.
            // Only the non-mutating WM_NULL may use this lifetime-only check;
            // the source foreground is mandatory again after completion.
            if (!authorization!.AuthorityCurrent(out reason))
            {
                reason = "return-before-barrier-" + reason;
                return false;
            }
            long remaining = request.ExpiresAtMs - WindowsInterruptClock.NowMs;
            reason = "return-expired";
            if (remaining <= 0 || remaining > SourceGlossaryReturnAuthorization.MaximumAgeMs)
                return false;
            reason = "activation-barrier";
            if (SendMessageTimeoutW((nint)binding.RootHwnd, 0, 0, 0, 0x22, (uint)remaining, out _) == 0)
                return false;
            if (!authorization.BeforeFocus((nint)binding.RootHwnd, out reason))
            {
                reason = "return-after-barrier-" + reason;
                return false;
            }
            reason = "native-source-changed";
            if (!_edits.ConfirmsReturnSource(_previous) || !_edits.ConfirmsReturnFocus(binding))
                return false;
            if (!authorization.BeforeFocus((nint)binding.RootHwnd, out reason))
            {
                reason = "return-after-source-check-" + reason;
                return false;
            }
        }
        reason = "return-expired";
        if (!glossary && WindowsInterruptClock.NowMs >= request.ExpiresAtMs)
            return false;
        bool nativeFocusRestored = false;
        bool traceReturn = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPELAY_TRACE"));
        long? activationCompletedAtMs = null, correctionStartedAtMs = null, correctionCompletedAtMs = null;
        bool? beforeCorrectionIsRoot = null, beforeCorrectionIsControl = null;
        if (!glossary && binding.Provider == NativeEditTransactions.Provider)
        {
            // Finish the one root activation before deciding whether the source
            // already restored the exact registered field. Avoid a redundant
            // focus operation after that exact native state has been verified.
            if (!CompleteReturnActivation(request, binding, out reason))
                return false;
            if (traceReturn)
                activationCompletedAtMs = WindowsInterruptClock.NowMs;
            reason = "native-source-changed";
            if (!_edits.ConfirmsReturnIdentity(binding))
                return false;
            nativeFocusRestored = _edits.ConfirmsReturnFocus(binding);
            // A failed focus check is not permission to correct a changed lifetime.
            if (!nativeFocusRestored && !_edits.ConfirmsReturnIdentity(binding))
                return false;
        }
        reason = "return-expired";
        if (!glossary && WindowsInterruptClock.NowMs >= request.ExpiresAtMs)
            return false;
        reason = "set-focus";
        // Glossary returns never correct field focus. Ordinary non-native or
        // not-yet-restored fields keep the existing UIA correction path.
        if (!glossary && !nativeFocusRestored)
        {
            // Diagnose only: these snapshots never authorize or retry focus.
            if (traceReturn)
            {
                nint beforeCorrection = GetForegroundWindow();
                beforeCorrectionIsRoot = beforeCorrection == (nint)binding.RootHwnd;
                beforeCorrectionIsControl = beforeCorrection == (nint)binding.ControlHwnd;
                correctionStartedAtMs = WindowsInterruptClock.NowMs;
            }
            _element.SetFocus();
            if (traceReturn)
                correctionCompletedAtMs = WindowsInterruptClock.NowMs;
        }
        if (!glossary)
        {
            // A correction needs its own completion, not a second activation.
            // Both bounded no-ops share the original absolute request deadline.
            if (!nativeFocusRestored && !CompleteReturnActivation(request, binding, out reason))
                return false;
            reason = "return-expired";
            if (WindowsInterruptClock.NowMs >= request.ExpiresAtMs)
                return false;
            nint foreground = GetForegroundWindow(); // One original post-barrier observation.
            if (foreground != (nint)binding.RootHwnd
                && (foreground != (nint)binding.ControlHwnd || binding.Provider != NativeEditTransactions.Provider
                    || !_edits.ConfirmsReturnFocus(binding)))
            {
                if (foreground == 0)
                    reason = "activation-foreground-none";
                else if (foreground == (nint)request.OverlayHwnd)
                    reason = "activation-foreground-overlay";
                else
                {
                    uint thread = GetWindowThreadProcessId(foreground, out uint process);
                    reason = thread == 0 ? "activation-foreground-other-unknown"
                        : process == binding.ProcessId ? "activation-foreground-other-source-process"
                        : "activation-foreground-other-process";
                    if (thread != 0 && process == binding.ProcessId
                        && traceReturn)
                    {
                        try
                        {
                            foregroundFacts = DescribeReturnForeground(foreground, binding) with
                            {
                                NativeFocusRestored = nativeFocusRestored,
                                ActivationCompletedAtMs = activationCompletedAtMs,
                                CorrectionStartedAtMs = correctionStartedAtMs,
                                CorrectionCompletedAtMs = correctionCompletedAtMs,
                                BeforeCorrectionIsRoot = beforeCorrectionIsRoot,
                                BeforeCorrectionIsControl = beforeCorrectionIsControl
                            };
                        }
                        catch (Exception error) when (error is not OutOfMemoryException) { }
                    }
                }
                return false;
            }
            reason = "activation-native-identity-changed";
            if (binding.Provider == NativeEditTransactions.Provider && !_edits.ConfirmsReturnIdentity(binding))
                return false;
        }
        reason = "return-expired";
        if (!glossary && WindowsInterruptClock.NowMs >= request.ExpiresAtMs)
            return false;
        reason = "focus-recheck";
        bool focused = string.Join('.', AutomationElement.FocusedElement.GetRuntimeId()) == binding.ElementToken;
        if (focused && (binding.Provider != NativeEditTransactions.Provider || _edits.ConfirmsReturnFocus(binding)))
        {
            if (!glossary && WindowsInterruptClock.NowMs >= request.ExpiresAtMs)
            {
                reason = "return-expired";
                return false;
            }
            if (glossary && !authorization!.BeforeFocus((nint)binding.RootHwnd, out reason))
            {
                reason = "return-after-uia-focus-" + reason;
                return false;
            }
            reason = "focused";
            return true;
        }
        reason = "focus-unconfirmed";
        if (!_edits.ConfirmsReturnFocus(binding))
            return false;

        // A registered stock Edit has an exact native focus/lifetime witness.
        // Reacquire its UIA identity and policy metadata between two such checks;
        // a delayed global UIA focus observation cannot substitute another field.
        reason = "native-return-metadata";
        AutomationElement current;
        using (_metadataCache.Activate())
            current = AutomationElement.FromHandle((nint)binding.ControlHwnd);
        var info = current.Cached;
        if (info.ProcessId != binding.ProcessId || info.NativeWindowHandle != binding.ControlHwnd
            || info.ControlType != ControlType.Edit || string.Join('.', current.GetRuntimeId()) != binding.ElementToken
            || current.GetCachedPropertyValue(AutomationElement.IsPasswordProperty, true) is not false
            || current.GetCachedPropertyValue(AutomationElement.IsEnabledProperty, true) is not true
            || current.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty, true) is not false)
            return false;
        long style = (long)GetWindowLongPtrW((nint)binding.ControlHwnd, -16);
        if (Marshal.GetLastPInvokeError() != 0 || (style & 0x40000000) == 0 || (style & 0x20) != 0
            || _application is null)
            return false;
        var ariaProperty = AutomationProperty.LookupById(30102);
        string aria = ariaProperty is null ? "" : current.GetCachedPropertyValue(ariaProperty, true) as string ?? "";
        var metadata = new FieldMetadata(_application, info.Name ?? "", info.AutomationId ?? "", info.HelpText ?? "",
            aria, false, true, (style & 0x800) != 0, false, false, (style & 4) != 0, false, false);
        reason = "native-return-policy";
        if (!FieldPolicy.Allows(metadata, request.Excluded ?? []))
            return false;
        reason = "native-return-focus-changed";
        if (!_edits.ConfirmsReturnFocus(binding))
            return false;
        // This acknowledges focus only. The next ordinary capture and Core's
        // saved edit basis still decide whether the draft may be retained.
        if (!glossary && WindowsInterruptClock.NowMs >= request.ExpiresAtMs)
        {
            reason = "return-expired";
            return false;
        }
        if (glossary && !authorization!.BeforeFocus((nint)binding.RootHwnd, out reason))
        {
            reason = "return-after-native-focus-" + reason;
            return false;
        }
        reason = "focused-native";
        return true;
    }
    public void Clear(TargetId? expected = null)
    {
        if (expected is { } id && _previous?.Target != id)
            return;
        _edits.ClearObservations();
        _previous = null;
        _ownedCapture = null;
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
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rectangle CaretRectangle;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rectangle rect);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wParam, nint lParam,
        uint flags, uint timeoutMs, out nuint result);
}
