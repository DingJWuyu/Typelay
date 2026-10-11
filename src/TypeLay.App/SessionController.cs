using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Protocol;
namespace TypeLay.App;
internal sealed class SessionController : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ModelClient _model;
    private readonly OverlayWindow _overlay;
    private readonly KeyboardHook _hook;
    private readonly IInputBroker _broker;
    private readonly Session _session;
    private readonly DispatcherTimer _timer;
    private long _generation, _nextCapture, _permitUntil, _editTransitionUntil, _retryBrokerAt;
    private bool _capturing, _writing, _translationRunning, _startingBroker, _disposed;
    private string? _statusOverride;
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private string? _lastTrace;
    public bool InputAvailable => _broker.Available && _hook.Ready;
    public string? LastApplication { get; private set; }
    public string ServiceStatus { get; private set; } = "正在连接输入服务";
    public event Action? Changed;
    public SessionController(SettingsStore settings, ModelClient model, OverlayWindow overlay, bool automationTest = false,
        IInputBroker? broker = null)
    {
        _broker = broker ?? new BrokerClient();
        _settings = settings; _model = model; _overlay = overlay; _hook = new KeyboardHook(automationTest, overlay.Handle);
        _session = new Session(settings.Value.Direction);
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        _timer.Tick += (_, _) => Tick();
        overlay.BeginEditing = BeginEdit;
        overlay.DraftChanged = text => _session.ChangeDraft(text);
        overlay.ReturnRequested = () => _ = ReturnFromEditor();
        overlay.EscapeRequested = () => _ = Escape();
        overlay.RegenerateRequested = () => _ = Regenerate();
        overlay.ExcludeRequested = () => { if (LastApplication is { } app) _settings.Exclude(app); StopSession(); };
        settings.Changed += Configure;
    }
    public async Task StartAsync()
    {
        await StartBroker();
        Configure(); _timer.Start(); Changed?.Invoke();
    }
    private async Task StartBroker()
    {
        if (_disposed || _startingBroker || _broker.Available) return;
        _startingBroker = true;
        ServiceStatus = "正在连接输入服务";
        Changed?.Invoke();
        try
        {
            await _broker.StartAsync();
            if (_disposed) return;
            ServiceStatus = _hook.Ready ? "输入服务已连接（开发版）" : "输入监听未能初始化";
            _nextCapture = 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed)
            {
                ServiceStatus = "输入服务连接中断，正在重试";
                _retryBrokerAt = Environment.TickCount64 + 2000;
            }
        }
        finally { _startingBroker = false; if (!_disposed) Changed?.Invoke(); }
    }
    private void Configure()
    {
        ClearBrokerSnapshot();
        _generation++; _hook.Revoke(); _permitUntil = 0;
        _session.SetPaused(!_settings.Value.Enabled);
        _session.SetDirection(_settings.Value.Direction, Environment.TickCount64);
        UiTheme.Apply(_settings.Value.Theme); Render(); Changed?.Invoke();
    }
    private void Tick()
    {
        if (_disposed) return;
        while (_hook.TryRead(out var notice))
        {
            if (notice?.Kind == "escape") _ = Escape();
            else if (notice?.Permit is not null) _ = Accept(notice);
        }
        long now = Environment.TickCount64;
        if (!_broker.Available && !_startingBroker && now >= _retryBrokerAt) _ = StartBroker();
        if (_session.Current is { } current)
        {
            nint foreground = Native.GetForegroundWindow();
            if (foreground != (nint)current.Target.RootHwnd && foreground != _overlay.Handle) StopSession();
        }
        if (!_hook.Ready || !_broker.Available || !_settings.Value.Enabled)
        { _hook.Revoke(); _overlay.HideQuietly(); return; }
        // The editor owns focus and its saved source basis until the user returns.
        // Chromium may expose no source selection when another window has focus.
        // Revalidate the source on return instead of polling an unfocused TextPattern.
        if (_session.State == SessionState.EditingTranslation && Native.GetForegroundWindow() == _overlay.Handle)
        { _hook.Revoke(); _permitUntil = 0; return; }
        // UI independently expires observations while a broker call is blocked.
        if (!_writing && _session.Current is { } old && now - old.ObservedAtMs > Limits.ObservationMaxAgeMs)
        { _hook.Revoke(); if (_permitUntil != 0) { _permitUntil = 0; Render(); } }
        if (!_capturing && !_writing && now >= _nextCapture)
        { _nextCapture = now + 35; _ = Capture(); }
        if (!_translationRunning && !_model.Busy && _model.Ready && !_writing)
        {
            var job = _session.TakeTranslationJob(now);
            if (job is not null) _ = Translate(job);
        }
        if (_permitUntil != 0 && now > _permitUntil) { _permitUntil = 0; Render(); }
    }
    private BrokerRequest Request(string operation) => new(Wire.Version, 0, operation, "",
        _hook.UserEpoch, _hook.ForegroundEpoch, (long)_overlay.Handle, _session.Current?.Target,
        Excluded: _settings.Value.Excluded, SourceUserEpoch: _hook.SourceUserEpoch);
    private async Task Capture()
    {
        _capturing = true;
        long generation = _generation;
        long windowEpoch = _hook.WindowEpoch;
        var request = Request("capture");
        try
        {
            var result = await _broker.CallAsync(request);
            if (_disposed || generation != _generation) return;
            if (windowEpoch != _hook.WindowEpoch) { Trace("capture-window-changed"); return; }
            long now = Environment.TickCount64;
            if (result.Snapshot is not { } snapshot || result.Focus == FocusMember.Other)
            { StopSession(); return; }
            nint foreground = Native.GetForegroundWindow();
            if (foreground != (nint)snapshot.Target.RootHwnd && foreground != _overlay.Handle) return;
            if (now < snapshot.ObservedAtMs || now - snapshot.ObservedAtMs > Limits.ObservationMaxAgeMs)
            { _hook.Revoke(); _permitUntil = 0; Render(); return; }
            if (_editTransitionUntil > now && result.Focus == FocusMember.Source
                && _session.State == SessionState.EditingTranslation) return;
            if (result.Focus == FocusMember.Overlay) _editTransitionUntil = 0;
            LastApplication = result.Application;
            _session.Observe(snapshot, result.Focus, now);
            AcceptancePermit? permit = null;
            // A keystroke arriving during a remote read must revoke acceptance, but
            // must not starve preview capture while the user continues typing.
            bool inputUnchanged = result.UserEpoch == _hook.UserEpoch && result.ForegroundEpoch == _hook.ForegroundEpoch;
            if (inputUnchanged && result.Focus == FocusMember.Source && _model.Ready && _settings.Value.ExperimentalWriteback
                && snapshot.Target.Provider != "uia-text-preview-v2")
            {
                var context = new AcceptanceContext(FocusMember.Source, false, false, snapshot.Ime,
                    snapshot.InputEpoch, snapshot.FocusEpoch);
                var (plan, _) = _session.Prepare(snapshot, context, now);
                if (plan is not null)
                    permit = new(plan, snapshot.InputEpoch, snapshot.FocusEpoch, snapshot.ObservedAtMs,
                        snapshot.ObservedAtMs + Limits.PermitMaxAgeMs);
            }
            _permitUntil = permit?.ExpiresAtMs ?? 0;
            bool escape = _session.State is not (SessionState.NoSession or SessionState.Paused or SessionState.OutcomeUnknown);
            _hook.Publish(new(snapshot, request.UserEpoch, request.ForegroundEpoch, escape), permit);
            Render();
            Trace(inputUnchanged ? "capture" : "capture-input-changed");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StopSession(); ServiceStatus = "输入服务连接中断，正在重试";
            _retryBrokerAt = Environment.TickCount64 + 2000; Changed?.Invoke();
        }
        finally { _capturing = false; }
    }
    private async Task Translate(TranslationJob job)
    {
        _translationRunning = true; _statusOverride = null;
        Trace("translate-start");
        try
        {
            var result = await _model.TranslateAsync(job);
            if (_disposed) return;
            if (result.Status == "translated") _session.Complete(job, result.Text);
            else if (result.Status == "no-translation") _session.CompleteNoTranslation(job);
            else if (_session.Fail(job)) _statusOverride = result.Code switch
            { "segment-too-long" => "文本较长，请缩短选区", "language-unavailable" => "尚无该语言方向模型", _ => "暂时无法翻译" };
            Render();
            Trace("translate-done");
        }
        finally { _translationRunning = false; }
    }
    private bool BeginEdit()
    {
        var source = _session.Current;
        // Entering the local editor performs no source mutation. Freshness is required
        // again after returning to the source, before an acceptance permit is issued.
        if (source is null || source.Ime != ImeState.Clear) return false;
        if (!_session.BeginEdit()) return false;
        _generation++; _editTransitionUntil = Environment.TickCount64 + 500;
        _hook.Revoke(); _permitUntil = 0; Render(); return true;
    }
    private async Task<bool> ReturnFromEditor()
    {
        if (_overlay.EditingComposition || !_session.RequestSourceReturn() || _session.Current is not { } source) return false;
        _generation++; _hook.Revoke(); _permitUntil = 0; Render();
        try
        {
            if (_broker.ProcessId != 0) Native.AllowSetForegroundWindow((uint)_broker.ProcessId);
            Native.SetForegroundWindow((nint)source.Target.RootHwnd);
            var response = await _broker.CallAsync(Request("return"), TimeSpan.FromMilliseconds(800));
            if (response.Status == "focused") { _nextCapture = 0; return true; }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _session.CancelSourceReturn(); _overlay.ResumeEditor(); Render(); return false;
    }
    private async Task Accept(InputNotice notice)
    {
        _hook.Revoke(); _permitUntil = 0;
        if (notice.Permit is not { } permit || Environment.TickCount64 - notice.AcceptedAtMs > Limits.WriteExecutionMaxAgeMs
            || notice.UserEpoch != _hook.UserEpoch || notice.ForegroundEpoch != _hook.ForegroundEpoch)
        { Render(); return; }
        if (_writing || !_session.BeginWrite(permit.Plan)) return;
        _writing = true; _generation++; Render();
        Trace("write-start");
        WriteReceipt receipt;
        try
        {
            var response = await _broker.CallAsync(Request("write") with { Plan = permit.Plan,
                ExpiresAtMs = notice.AcceptedAtMs + Limits.WriteExecutionMaxAgeMs, OverlayHwnd = 0 },
                TimeSpan.FromSeconds(2));
            receipt = response.Receipt ?? new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { receipt = new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome); }
        _session.FinishWrite(permit.Plan, receipt);
        Trace("write-" + receipt.Status + "-" + receipt.Reason);
        if (receipt.Status != WriteStatus.Verified)
        {
            ServiceStatus = receipt.Status == WriteStatus.OutcomeUnknown ? "写回结果未确认，当前目标已隔离" : "写回前安全校验拒绝了本次操作";
            Changed?.Invoke();
        }
        _writing = false; _nextCapture = 0; Render();
    }
    private async Task Escape()
    {
        if (_overlay.EditingComposition) return;
        if (_session.State == SessionState.EditingTranslation) await ReturnFromEditor();
        _generation++; _hook.Revoke(); _permitUntil = 0; _session.Escape(); _overlay.HideQuietly();
    }
    private async Task Regenerate()
    {
        if (_overlay.EditingComposition) return;
        if (_session.State == SessionState.EditingTranslation && !await ReturnFromEditor()) return;
        _generation++; _hook.Revoke(); _permitUntil = 0; _statusOverride = null;
        _session.Retranslate(Environment.TickCount64); _nextCapture = 0; Render();
    }
    public void StopSession()
    { ClearBrokerSnapshot(); _generation++; _hook.Revoke(); _permitUntil = 0; _session.LeaveFamily(); _statusOverride = null; _overlay.HideQuietly(); }
    private void ClearBrokerSnapshot()
    {
        if (_broker.Available && _session.Current is { } current)
            _ = ClearBroker(Request("clear") with { BoundTarget = current.Target });
    }
    private async Task ClearBroker(BrokerRequest request)
    {
        try { await _broker.CallAsync(request); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private void Render()
    {
        if (_disposed) return;
        string? status = _session.State == SessionState.Translating && !_model.Ready ? "正在准备翻译"
            : _session.State == SessionState.TranslationFailed ? _statusOverride : null;
        _overlay.Render(_session, _settings.Value, _permitUntil >= Environment.TickCount64 && _permitUntil != 0, status);
    }
    // Opt-in local diagnostics contain counts and state only, never field text,
    // labels, window titles, exception messages, source or translated content.
    private void Trace(string operation)
    {
        if (string.IsNullOrWhiteSpace(_tracePath)) return;
        string state = System.Text.Json.JsonSerializer.Serialize(new { operation, state = _session.State.ToString(),
            sourceLength = _session.Current?.Text.Length ?? 0, previewLength = _session.Preview.Length,
            committedPrefixLength = _session.Current?.CommittedPrefix?.Length ?? 0,
            observedPreviewLength = _session.Current?.PreviewText?.Length ?? 0,
            candidateLength = _session.Candidate.Length, ime = _session.Current?.Ime.ToString(),
            userEpoch = _hook.UserEpoch, sourceUserEpoch = _hook.SourceUserEpoch, focusEpoch = _hook.ForegroundEpoch,
            writeSupported = _session.Current?.WriteSupported ?? false });
        if (state == _lastTrace) return;
        _lastTrace = state;
        try { File.AppendAllText(_tracePath, "{\"atMs\":" + Environment.TickCount64 + "," + state[1..] + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose()
    { _disposed = true; _timer.Stop(); _hook.Dispose(); _broker.Dispose(); _settings.Changed -= Configure; }
}
