using TypeLay.Core;
namespace TypeLay.Runtime;

public sealed class SessionController : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ITranslationService _model;
    private readonly ISessionView _overlay;
    private readonly IInputMonitor _hook;
    private readonly IInputService _broker;
    private readonly Session _session;
    private readonly TranslationActivation _activation;
    private DirectTranslation? _pendingDirect;
    private bool _directRunning;
    private long _configurationRevision;
    private readonly ISessionLoop _loop;
    private long _generation, _nextCapture, _permitUntil, _editTransitionUntil, _retryBrokerAt;
    private bool _capturing, _writing, _translationRunning, _startingBroker, _returningToSource, _disposed;
    private string? _statusOverride;
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private string? _lastTrace;
    public string ShortcutStatus => _settings.Value.Enabled && _settings.Value.Mode == WorkMode.OnDemand && !_hook.ShortcutsReady
        ? "快捷键输入观察未就绪，当前不会执行按需命令。" : _hook.ShortcutStatus;
    public bool InputAvailable => _broker.Available && _hook.Ready;
    public string? LastApplication
    {
        get; private set;
    }
    public string? OperationStatus
    {
        get; private set;
    }
    public string ServiceStatus { get; private set; } = "正在连接输入服务";
    public event Action? Changed;
    public SessionController(SettingsStore settings, ITranslationService model, ISessionView overlay,
        IInputMonitor input, IInputService broker, ISessionLoop loop)
    {
        _broker = broker;
        _settings = settings;
        _model = model;
        _overlay = overlay;
        _hook = input;
        _loop = loop;
        _session = new Session(settings.Value.Direction);
        _activation = new TranslationActivation(settings.Value.Mode);
        _loop.Tick += Tick;
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
        Configure();
        _loop.Start();
        Changed?.Invoke();
    }
    private async Task StartBroker()
    {
        if (_disposed || _startingBroker || _broker.Available)
            return;
        _startingBroker = true;
        ServiceStatus = "正在连接输入服务";
        Changed?.Invoke();
        try
        {
            await _broker.StartAsync();
            if (_disposed)
                return;
            ServiceStatus = _hook.Ready ? "输入服务已连接（开发版）" : "输入监听未能初始化";
            _nextCapture = 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed)
            {
                ServiceStatus = "输入服务连接中断，正在重试";
                _retryBrokerAt = _loop.NowMs + 2000;
            }
        }
        finally { _startingBroker = false; if (!_disposed) Changed?.Invoke(); }
    }
    private void Configure()
    {
        _configurationRevision++;
        if (_pendingDirect is not null)
            SetServiceStatus("设置已改变，未执行本次直接替换。");
        _pendingDirect = null;
        _activation.SetMode(_settings.Value.Mode);
        _activation.End();
        _hook.ConfigureShortcuts(_settings.Value.Enabled && _settings.Value.Mode == WorkMode.OnDemand,
            _settings.Value.WakeShortcut, _settings.Value.DirectShortcut);
        ClearBrokerSnapshot();
        _generation++;
        _hook.Revoke();
        _permitUntil = 0;
        _session.SetPaused(!_settings.Value.Enabled);
        _session.SetDirection(_settings.Value.Direction, _loop.NowMs);
        _overlay.Configure(_settings.Value);
        Render();
        Changed?.Invoke();
    }
    private void Tick()
    {
        if (_disposed)
            return;
        while (_hook.TryRead(out var notice))
        {
            if (notice?.Kind == "wake")
                Wake(notice);
            else if (notice?.Kind == "direct")
                QueueDirect(notice);
            else if (notice?.Kind == "escape")
                _ = Escape();
            else if (notice?.Permit is not null)
                _ = Accept(notice);
        }
        long now = _loop.NowMs;
        if (_pendingDirect is { } pending && !_directRunning)
        {
            if (!_model.Ready || !_broker.Available || now - pending.TriggeredAtMs > 120_000)
            {
                _pendingDirect = null;
                SetServiceStatus("本地服务不可用或等待超时，未执行直接替换。");
            }
            else if (!_translationRunning && !_model.Busy)
                _ = TranslateDirect(pending);
        }
        if (!_broker.Available && !_startingBroker && now >= _retryBrokerAt)
            _ = StartBroker();
        if (_session.Current is { } current)
        {
            if (_hook.GetFocus(current.Target) == FocusMember.Other)
                StopSession("focus-left");
        }
        if (!_hook.Ready || !_broker.Available || !_settings.Value.Enabled)
        {
            _hook.Revoke();
            _overlay.HideQuietly();
            return;
        }
        // Focus restoration may await the real input context. A concurrent read
        // before that operation completes must not discard the saved editor draft.
        if (_returningToSource)
        {
            _hook.Revoke();
            _permitUntil = 0;
            return;
        }
        // The editor owns focus and its saved source basis until the user returns.
        // Chromium may expose no source selection when another window has focus.
        // Revalidate the source on return instead of polling an unfocused TextPattern.
        if (_session.State == SessionState.EditingTranslation && _session.Current is { } editingSource && _hook.GetFocus(editingSource.Target) == FocusMember.Overlay)
        {
            _hook.Revoke();
            _permitUntil = 0;
            return;
        }
        // UI independently expires observations while a broker call is blocked.
        if (!_writing && _session.Current is { } old && now - old.ObservedAtMs > Limits.ObservationMaxAgeMs)
        {
            _hook.Revoke();
            if (_permitUntil != 0)
            {
                _permitUntil = 0;
                Render();
            }
        }
        if (!_capturing && !_writing && now >= _nextCapture)
        {
            _nextCapture = now + 35;
            _ = Capture();
        }
        if (!_translationRunning && !_directRunning && _pendingDirect is null && !_model.Busy && _model.Ready && !_writing)
        {
            var job = _session.TakeTranslationJob(now);
            if (job is not null)
                _ = Translate(job);
        }
        if (_permitUntil != 0 && now > _permitUntil)
        {
            _permitUntil = 0;
            Render();
        }
    }
    private InputContext Context() => new(_hook.UserEpoch, _hook.ForegroundEpoch,
        _hook.SourceUserEpoch, _session.Current?.Target, _settings.Value.Excluded,
        _settings.Value.Enabled && _settings.Value.Mode == WorkMode.OnDemand);
    private async Task Capture()
    {
        _capturing = true;
        long generation = _generation;
        long windowEpoch = _hook.WindowEpoch;
        long contentEpoch = _hook.ContentEpoch;
        var request = Context();
        try
        {
            var result = await _broker.CaptureAsync(request);
            if (_disposed || generation != _generation)
                return;
            if (windowEpoch != _hook.WindowEpoch)
            {
                Trace("capture-window-changed");
                return;
            }
            long now = _loop.NowMs;
            if (result.Snapshot is not { } snapshot || result.Focus == FocusMember.Other)
            {
                StopSession("capture-empty");
                return;
            }
            if (_hook.GetFocus(snapshot.Target) == FocusMember.Other)
                return;
            if (now < snapshot.ObservedAtMs || now - snapshot.ObservedAtMs > Limits.ObservationMaxAgeMs)
            {
                _hook.Revoke();
                _permitUntil = 0;
                Render();
                return;
            }
            if (_editTransitionUntil > now && result.Focus == FocusMember.Source
                && _session.State == SessionState.EditingTranslation)
                return;
            if (result.Focus == FocusMember.Overlay)
                _editTransitionUntil = 0;
            LastApplication = result.Application;
            bool active = _activation.Observe(snapshot.Target, result.Focus);
            if (!active)
            {
                if (_session.Current is not null)
                {
                    _generation++;
                    _session.LeaveFamily();
                }
                _permitUntil = 0;
                _hook.Publish(new(snapshot, request.UserEpoch, request.ForegroundEpoch, false), null);
                if (result.Focus == FocusMember.Source)
                    _hook.PublishShortcut(new(snapshot, contentEpoch, request.ForegroundEpoch));
                _overlay.HideQuietly();
                return;
            }
            _session.Observe(snapshot, result.Focus, now);
            AcceptancePermit? permit = null;
            // A keystroke arriving during a remote read must revoke acceptance, but
            // must not starve preview capture while the user continues typing.
            bool inputUnchanged = result.UserEpoch == _hook.UserEpoch && result.ForegroundEpoch == _hook.ForegroundEpoch;
            if (inputUnchanged && result.Focus == FocusMember.Source && _model.Ready && _settings.Value.ExperimentalWriteback
                && snapshot.WriteSupported && _hook.CanInterceptAcceptance)
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
            if (result.Focus == FocusMember.Source)
                _hook.PublishShortcut(new(snapshot, contentEpoch, request.ForegroundEpoch));
            Render();
            Trace(inputUnchanged ? "capture" : "capture-input-changed");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_disposed || generation != _generation)
                return;
            StopSession("capture-failed");
            ServiceStatus = "输入服务连接中断，正在重试";
            _retryBrokerAt = _loop.NowMs + 2000;
            Changed?.Invoke();
        }
        finally { _capturing = false; }
    }
    private void Wake(InputNotice notice)
    {
        if (!_settings.Value.Enabled || _settings.Value.Mode != WorkMode.OnDemand || notice.Source is not { } source
            || notice.CommandGeneration != _hook.CommandGeneration || notice.ForegroundEpoch != _hook.ForegroundEpoch
            || _hook.GetFocus(source.Target) != FocusMember.Source)
            return;
        if (_activation.Wake(source.Target))
        {
            // Explicit wake can reopen an Esc-suppressed field; repeated wake must
            // not erase a live candidate or a user's translation draft.
            if (_session.State == SessionState.NoSession)
                _session.LeaveFamily();
            _generation++;
            _nextCapture = 0;
        }
    }
    private void QueueDirect(InputNotice notice)
    {
        if (!_settings.Value.Enabled || _settings.Value.Mode != WorkMode.OnDemand || notice.Source is not { } source
            || notice.CommandGeneration != _hook.CommandGeneration)
            return;
        if (_pendingDirect is not null || _directRunning)
        {
            SetServiceStatus("已有一次直接翻译正在处理，未追加操作。");
            return;
        }
        if (!_model.Ready)
        {
            SetServiceStatus("本地模型尚未就绪，未执行直接替换。");
            return;
        }
        var (operation, reason) = DirectTranslation.Start(source, _settings.Value.Direction, notice.AcceptedAtMs);
        if (operation is null)
        {
            SetServiceStatus(reason == Denial.NoCandidate ? "没有需要直接翻译的文本。" : "无法确认原输入内容的安全状态，未执行直接替换。");
            return;
        }
        // The trigger's snapshot, not the currently focused field, owns this operation.
        // Ending a foreground session never clears a pending direct operation.
        StopSession();
        _pendingDirect = operation;
        SetServiceStatus("正在直接翻译；可以切换输入框，当前操作仍绑定原输入框。");
    }
    private async Task TranslateDirect(DirectTranslation operation)
    {
        _directRunning = true;
        long configuration = _configurationRevision;
        try
        {
            var response = await _model.TranslateAsync(operation.Job);
            if (_disposed || configuration != _configurationRevision)
                return;
            if (response.Status != "translated")
            {
                SetServiceStatus(response.Status == "no-translation" ? "当前文本无需翻译，未作替换。" : "直接翻译未完成，原文未作替换。");
                return;
            }
            var (plan, _) = operation.Complete(response.Text);
            if (plan is null)
            {
                SetServiceStatus("译文未通过内容校验，未执行直接替换。");
                return;
            }
            // Never route detached work through foreground-only write/return operations.
            var result = await _broker.WriteDirectAsync(plan, _settings.Value.Excluded);
            if (_disposed)
                return;
            SetServiceStatus(result.Status == WriteStatus.Verified ? "已直接替换原输入框文字。"
                : result.Reason == Denial.Unsupported ? "当前输入后端尚不支持可靠的直接写回，原文未作替换。"
                : "直接写回未获确认，请检查原输入框；不会自动重试。");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed)
                SetServiceStatus("直接翻译或写回服务中断；不会自动重试替换。");
        }
        finally
        {
            if (ReferenceEquals(_pendingDirect, operation))
                _pendingDirect = null;
            _directRunning = false;
        }
    }
    private void SetServiceStatus(string status)
    {
        ServiceStatus = status;
        OperationStatus = status;
        Changed?.Invoke();
    }
    private async Task Translate(TranslationJob job)
    {
        _translationRunning = true;
        _statusOverride = null;
        Trace("translate-start");
        try
        {
            var result = await _model.TranslateAsync(job);
            if (_disposed)
                return;
            if (result.Status == "translated")
                _session.Complete(job, result.Text);
            else if (result.Status == "no-translation")
                _session.CompleteNoTranslation(job);
            else if (_session.Fail(job))
                _statusOverride = result.Code switch
                {
                    "segment-too-long" => "文本较长，请缩短选区",
                    "language-unavailable" => "尚无该语言方向模型",
                    _ => "暂时无法翻译"
                };
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
        if (source is null || source.Ime != ImeState.Clear)
            return false;
        if (!_session.BeginEdit())
            return false;
        _generation++;
        _editTransitionUntil = _loop.NowMs + 500;
        _hook.Revoke();
        _permitUntil = 0;
        Render();
        return true;
    }
    private async Task<bool> ReturnFromEditor()
    {
        if (_returningToSource || _overlay.EditingComposition || !_session.RequestSourceReturn() || _session.Current is not { } source)
            return false;
        long generation = ++_generation;
        _hook.Revoke();
        _permitUntil = 0;
        Render();
        _returningToSource = true;
        bool returned = false;
        try
        {
            returned = await _broker.ReturnToSourceAsync(Context());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { _returningToSource = false; }
        if (_disposed || generation != _generation)
            return false;
        if (returned)
        {
            _nextCapture = 0;
            return true;
        }
        _session.CancelSourceReturn();
        _overlay.ResumeEditor();
        Render();
        return false;
    }
    private async Task Accept(InputNotice notice)
    {
        _hook.Revoke();
        _permitUntil = 0;
        if (notice.Permit is not { } permit || _loop.NowMs - notice.AcceptedAtMs > Limits.WriteExecutionMaxAgeMs
            || notice.UserEpoch != _hook.UserEpoch || notice.ForegroundEpoch != _hook.ForegroundEpoch)
        {
            Render();
            return;
        }
        if (_writing || !_session.BeginWrite(permit.Plan))
            return;
        _writing = true;
        _generation++;
        Render();
        Trace("write-start");
        WriteReceipt receipt;
        try
        {
            receipt = await _broker.WriteAsync(permit.Plan, Context(), notice.AcceptedAtMs + Limits.WriteExecutionMaxAgeMs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            receipt = new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
        }
        _session.FinishWrite(permit.Plan, receipt);
        Trace("write-" + receipt.Status + "-" + receipt.Reason);
        if (receipt.Status != WriteStatus.Verified)
        {
            ServiceStatus = receipt.Status == WriteStatus.OutcomeUnknown ? "写回结果未确认，当前目标已隔离" : "写回前安全校验拒绝了本次操作";
            Changed?.Invoke();
        }
        _writing = false;
        _nextCapture = 0;
        Render();
    }
    private async Task Escape()
    {
        if (_overlay.EditingComposition)
            return;
        if (_session.State == SessionState.EditingTranslation)
        {
            long expectedGeneration = _generation + 1;
            if (!await ReturnFromEditor() || _disposed || _generation != expectedGeneration)
                return;
        }
        if (_disposed)
            return;
        _generation++;
        _hook.Revoke();
        _permitUntil = 0;
        _session.Escape();
        _overlay.HideQuietly();
    }
    private async Task Regenerate()
    {
        if (_overlay.EditingComposition)
            return;
        if (_session.State == SessionState.EditingTranslation)
        {
            long expectedGeneration = _generation + 1;
            if (!await ReturnFromEditor() || _disposed || _generation != expectedGeneration)
                return;
        }
        if (_disposed)
            return;
        _generation++;
        _hook.Revoke();
        _permitUntil = 0;
        _statusOverride = null;
        _session.Retranslate(_loop.NowMs);
        _nextCapture = 0;
        Render();
    }
    public void StopSession() => StopSession("stop-session");
    private void StopSession(string reason)
    {
        if (_session.Current is not null)
            Trace(reason);
        _activation.End();
        ClearBrokerSnapshot();
        _generation++;
        _hook.Revoke();
        _permitUntil = 0;
        _session.LeaveFamily();
        _statusOverride = null;
        _overlay.HideQuietly();
    }
    private void ClearBrokerSnapshot()
    {
        if (_broker.Available && _session.Current is { } current)
            _ = ClearBroker(Context() with
            {
                BoundTarget = current.Target
            });
    }
    private async Task ClearBroker(InputContext request)
    {
        try
        {
            await _broker.ClearAsync(request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private void Render()
    {
        if (_disposed)
            return;
        string? status = _session.State == SessionState.Translating && !_model.Ready ? "正在准备翻译"
            : _session.State == SessionState.TranslationFailed ? _statusOverride : null;
        _overlay.Render(_session, _settings.Value, _permitUntil >= _loop.NowMs && _permitUntil != 0, status);
    }
    // Opt-in local diagnostics contain counts and state only, never field text,
    // labels, window titles, exception messages, source or translated content.
    private void Trace(string operation)
    {
        if (string.IsNullOrWhiteSpace(_tracePath))
            return;
        string state = System.Text.Json.JsonSerializer.Serialize(new
        {
            operation,
            state = _session.State.ToString(),
            sourceLength = _session.Current?.Text.Length ?? 0,
            previewLength = _session.Preview.Length,
            committedPrefixLength = _session.Current?.CommittedPrefix?.Length ?? 0,
            observedPreviewLength = _session.Current?.PreviewText?.Length ?? 0,
            candidateLength = _session.Candidate.Length,
            ime = _session.Current?.Ime.ToString(),
            userEpoch = _hook.UserEpoch,
            sourceUserEpoch = _hook.SourceUserEpoch,
            inputReady = _hook.Ready,
            focus = _session.Current is { } current ? _hook.GetFocus(current.Target).ToString() : "Other",
            windowEpoch = _hook.WindowEpoch,
            focusEpoch = _hook.ForegroundEpoch,
            writeSupported = _session.Current?.WriteSupported ?? false
        });
        if (state == _lastTrace)
            return;
        _lastTrace = state;
        try
        {
            File.AppendAllText(_tracePath, "{\"atMs\":" + _loop.NowMs + "," + state[1..] + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        _disposed = true;
        _loop.Stop();
        _loop.Tick -= Tick;
        _pendingDirect = null;
        _hook.ConfigureShortcuts(false, _settings.Value.WakeShortcut, _settings.Value.DirectShortcut);
        _hook.Dispose();
        _broker.Dispose();
        _settings.Changed -= Configure;
    }
}
