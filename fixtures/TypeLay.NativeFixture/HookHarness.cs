using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using N = TypeLay.Platform.Windows.NativeMethods;

// Narrow native Hook regression fixture. This is not the browser UIA backend and
// its fixed candidate is explicitly synthetic, not evidence of model inference.
[SupportedOSPlatform("windows")]
internal sealed class HookHarness : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly KeyboardHook _hook;
    private readonly NativeEditAdapter _adapter;
    private readonly Session _session = new(new("auto", "en"));
    private readonly FixtureDescriptor _descriptor;
    private readonly nint _status;
    private readonly bool _automation;
    private int _accepted, _escaped;
    private string _receipt = "none", _lastReport = "";

    public HookHarness(FixtureDescriptor descriptor, nint status, bool automation)
    {
        _descriptor = descriptor; _status = status; _automation = automation;
        _adapter = new(_process, descriptor); _hook = new(automation);
    }

    public void Tick(int submits)
    {
        while (_hook.TryRead(out var notice))
        {
            if (notice?.Kind == "escape") { _escaped++; _session.Escape(); }
            else if (notice?.Permit is { } permit && _session.BeginWrite(permit.Plan))
            {
                var receipt = _adapter.TryWrite(permit.Plan);
                _session.FinishWrite(permit.Plan, receipt);
                _receipt = receipt.Status.ToString();
                if (receipt.Status == WriteStatus.Verified) _accepted++;
            }
        }
        long now = Environment.TickCount64;
        var snapshot = _adapter.Capture();
        string field = snapshot is null ? "other" : _descriptor.Fields.Single(f => f.Hwnd == snapshot.Target.ControlHwnd).Name;
        if (snapshot is null) { _hook.Revoke(); _session.LeaveFamily(); }
        else
        {
            _session.Observe(snapshot, FocusMember.Source, now);
            var job = _session.TakeTranslationJob(now);
            if (job is not null) _session.Complete(job, "HOOK TEST");
            var context = new AcceptanceContext(FocusMember.Source, false, false, snapshot.Ime,
                snapshot.InputEpoch, snapshot.FocusEpoch);
            var (plan, _) = _session.Prepare(snapshot, context, now);
            var permit = plan is null ? null : new AcceptancePermit(plan, snapshot.InputEpoch, snapshot.FocusEpoch,
                snapshot.ObservedAtMs, snapshot.ObservedAtMs + Limits.PermitMaxAgeMs);
            bool escape = _session.State is SessionState.ValidTranslation or SessionState.Translating;
            _hook.Publish(new(snapshot, _hook.UserEpoch, _hook.ForegroundEpoch, escape), permit);
        }
        string report = JsonSerializer.Serialize(new { suite = "native-keyboard-hook", automation = _automation,
            ready = _hook.Ready, field, ime = snapshot?.Ime.ToString() ?? "none", state = _session.State.ToString(),
            accepted = _accepted, escaped = _escaped, receipt = _receipt, submits,
            candidateKind = "synthetic-fixed-hook-test" });
        if (report == _lastReport) return;
        _lastReport = report;
        N.SendString(_status, N.WM_SETTEXT, $"Mode: {(_automation ? "automation" : "default")}  Field: {field}\r\nAccepts: {_accepted}  Escapes: {_escaped}  IME: {snapshot?.Ime}  Submit: {submits}");
        Console.WriteLine(report); Console.Out.Flush();
    }

    public void Dispose() { _hook.Dispose(); _process.Dispose(); }
}
