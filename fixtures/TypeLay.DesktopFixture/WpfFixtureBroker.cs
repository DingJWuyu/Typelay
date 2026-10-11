using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Protocol;

// Test-only backend for these two owned controls. Production discovery and COM/pipe
// behavior are intentionally not claimed by this desktop UI regression fixture.
internal sealed class WpfFixtureBroker : IInputBroker
{
    private readonly Window _window;
    private readonly Field[] _fields;
    private Field? _bound;
    private long _focusEpoch;
    private FocusMember _lastFocus;
    public bool Available { get; private set; }
    public int ProcessId => 0;
    public int Writes { get; private set; }
    public string LastField { get; private set; } = "none";
    public ImeState LastIme { get; private set; } = ImeState.Clear;
    public WriteStatus LastWrite { get; private set; } = WriteStatus.RejectedBeforeMutation;
    public TextRange? LastScope { get; private set; }
    public event Action? Written;

    private sealed class Field
    {
        public required TextBox Box;
        public required string Name;
        public Guid Lifetime = Guid.NewGuid();
        public long InputEpoch;
        public bool Composing;
    }

    public WpfFixtureBroker(Window window, TextBox a, TextBox b)
    {
        _window = window;
        _fields = [new() { Box = a, Name = "a" }, new() { Box = b, Name = "b" }];
        foreach (var f in _fields)
        {
            f.Box.TextChanged += (_, _) => f.InputEpoch++;
            TextCompositionManager.AddPreviewTextInputStartHandler(f.Box, (_, _) => f.Composing = true);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(f.Box, (_, e) => f.Composing = !string.IsNullOrEmpty(e.TextComposition.CompositionText));
            TextCompositionManager.AddPreviewTextInputHandler(f.Box, (_, _) => f.Composing = false);
        }
    }

    public Task StartAsync() { Available = true; return Task.CompletedTask; }
    public void ResetCounts() { Writes = 0; LastScope = null; LastWrite = WriteStatus.RejectedBeforeMutation; }
    public void Dispose() => Available = false;
    private TargetId Target(Field f)
    {
        using var process = Process.GetCurrentProcess();
        return new(Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks,
            new WindowInteropHelper(_window).Handle, Array.IndexOf(_fields, f) + 1, "WPF-test-fixture", f.Name, f.Lifetime);
    }

    private (InputSnapshot?, FocusMember) Capture(BrokerRequest request)
    {
        nint foreground = Native.GetForegroundWindow();
        nint root = new WindowInteropHelper(_window).Handle;
        FocusMember focus = foreground == root ? FocusMember.Source
            : request.OverlayHwnd != 0 && foreground == (nint)request.OverlayHwnd ? FocusMember.Overlay : FocusMember.Other;
        Field? field = focus == FocusMember.Source ? _fields.SingleOrDefault(f => f.Box.IsKeyboardFocused)
            : focus == FocusMember.Overlay && _bound is not null && request.BoundTarget == Target(_bound) ? _bound : null;
        if (field is null) return (null, FocusMember.Other);
        if (field != _bound || focus != _lastFocus) _focusEpoch++;
        _bound = field; _lastFocus = focus; LastField = field.Name;
        LastIme = field.Composing ? ImeState.Composing : ImeState.Clear;
        var point = field.Box.PointToScreen(new(0, 0)); var dpi = VisualTreeHelper.GetDpi(field.Box);
        var bounds = new RectPx(point.X, point.Y, field.Box.ActualWidth * dpi.DpiScaleX, field.Box.ActualHeight * dpi.DpiScaleY);
        string text = field.Composing ? "" : field.Box.Text;
        var selection = field.Composing ? new TextRange(0, 0)
            : new TextRange(field.Box.SelectionStart, field.Box.SelectionStart + field.Box.SelectionLength);
        return (new(Target(field), text, selection, bounds, field.Box.AcceptsReturn, FieldAccess.Supported,
            LastIme, Environment.TickCount64, field.InputEpoch, _focusEpoch), focus);
    }

    public Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
    {
        BrokerResponse response;
        if (request.Op == "capture")
        {
            var (snapshot, focus) = Capture(request);
            response = new(Wire.Version, request.Id, snapshot is null ? "silent" : "observed", snapshot, focus,
                Application: "desktop-fixture", UserEpoch: request.UserEpoch, ForegroundEpoch: request.ForegroundEpoch);
        }
        else if (request.Op == "return")
        {
            bool matches = _bound is not null && request.BoundTarget == Target(_bound);
            if (matches) { _window.Activate(); _bound!.Box.Focus(); }
            response = new(Wire.Version, request.Id, matches && _bound!.Box.IsKeyboardFocused ? "focused" : "denied");
        }
        else if (request.Op == "write") response = new(Wire.Version, request.Id, "receipt", Receipt: Write(request));
        else if (request.Op == "clear") { _bound = null; response = new(Wire.Version, request.Id, "cleared"); }
        else response = new(Wire.Version, request.Id, "ready");
        return Task.FromResult(response);
    }

    private WriteReceipt Write(BrokerRequest request)
    {
        var (fresh, focus) = Capture(request with { OverlayHwnd = 0 });
        if (request.Plan is not { } plan || fresh is null || focus != FocusMember.Source || _bound is null)
            return new(WriteStatus.RejectedBeforeMutation, Denial.NotSourceFocus);
        if (Environment.TickCount64 > request.ExpiresAtMs) return new(WriteStatus.RejectedBeforeMutation, Denial.StaleObservation);
        var denial = WriteSafety.Validate(plan, fresh, Environment.TickCount64);
        if (denial != Denial.None) return new(WriteStatus.RejectedBeforeMutation, denial);
        _bound.Box.Text = plan.ResultText; _bound.Box.Select(plan.Caret, 0);
        var (after, afterFocus) = Capture(request with { OverlayHwnd = 0 });
        LastScope = plan.Scope;
        bool verified = afterFocus == FocusMember.Source && after?.Text == plan.ResultText && after.Selection == new TextRange(plan.Caret, plan.Caret);
        LastWrite = verified ? WriteStatus.Verified : WriteStatus.OutcomeUnknown;
        if (verified) Writes++;
        Written?.Invoke();
        return new(LastWrite, verified ? Denial.None : Denial.UnknownOutcome, after);
    }
}
