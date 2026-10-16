using TypeLay.Core;
namespace TypeLay.Runtime;

public sealed partial class SessionController : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ITranslationService _model;
    private readonly ISessionView _overlay;
    private readonly IInputMonitor _hook;
    private readonly IInputService _broker;
    private readonly Session _session;
    private readonly TranslationActivation _activation;
    // Only identifies an explicit wake awaiting its first source observation; no text or permission.
    private sealed record PendingWakeCapture(TargetId Target, long Generation, long FeedbackRevision,
        Guid CommandGeneration, long CommandSequence);
    private PendingWakeCapture? _pendingWakeCapture;
    private OnDemandPracticeProgress? _practiceWakeCommand, _pendingDirectPractice;
    private Guid _lastRetiredPracticeRun;
    private long _practiceBoundAt, _practicePreviewAt;
    private DirectTranslation? _pendingDirect;
    private bool _directRunning, _directWriteSubmitted;
    private long _lastCommandSequence, _commandSequence, _commandFeedbackRevision, _pendingDirectFeedbackRevision, _pendingDirectSequence;
    private Guid _lastCommandGeneration, _pendingDirectCommandGeneration;
    private long _configurationRevision;
    private SessionShortcutAcknowledgement? _sessionShortcuts;
    private readonly ISessionLoop _loop;
    private long _generation, _nextCapture, _permitUntil, _editTransitionUntil, _retryBrokerAt;
    private AcceptancePermit? _presentationPermit;
    private bool _renderedAcceptanceReady;
    private bool _capturing, _writing, _translationRunning, _startingBroker, _returningToSource, _disposed;
    private bool _inputReadFailed;
    private PauseAnchor? _pauseAnchor;
    private long _pauseViewRevision, _pauseWindowEpoch, _pauseForegroundEpoch;
    private bool _observingPause, _resumingPause;
    private const string ResumeSaveFailureStatus = "恢复设置未能保存，TypeLay 仍处于暂停状态。请检查设置存储后重试。";
    private string? _statusOverride;
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private string? _lastTrace;
    private Guid _practiceCaptureTraceGeneration;
    private int _practiceCaptureTraceCount;
    private bool _practiceCaptureTracePublished;
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
    // Hosts discard queued feedback older than this revision, including when a
    // newer command succeeds and deliberately emits no failure notification.
    public long CommandFeedbackRevision => _commandFeedbackRevision;
    public InputConnectionState ConnectionState { get; private set; } = InputConnectionState.Unavailable;
    public string ConnectionStatus => ApplicationSupportPresentation.ConnectionStatus(ConnectionState);
    // Kept for existing hosts; new views can present connection and operation separately.
    public string ServiceStatus => OperationStatus ?? ConnectionStatus;
    public event Action? Changed;
    public event Action<CommandFeedback>? OperationFeedback;
    public OnDemandPracticeProgress? PracticeProgress
    {
        get; private set;
    }
    public event Action<OnDemandPracticeProgress?>? PracticeProgressChanged;

    // Hosts call Begin only for an explicit new/restarted owned practice window.
    // Binding must come from that host's native identity verification, before the
    // command is consumed. Neither registration nor progress changes write safety.
    public bool BeginOnDemandPractice(Guid runId)
    {
        if (_disposed || runId == Guid.Empty || runId == _lastRetiredPracticeRun
            || !_settings.Value.Enabled || _settings.Value.Mode != WorkMode.OnDemand)
            return false;
        if (PracticeProgress?.RunId == runId)
            return true;
        if (PracticeProgress is { } previous)
            _lastRetiredPracticeRun = previous.RunId;
        _practiceWakeCommand = null;
        PracticeProgress = new(runId, null, _hook.CommandGeneration, 0, _commandFeedbackRevision, false, false);
        PracticeProgressChanged?.Invoke(PracticeProgress);
        return true;
    }
    public bool BindOnDemandPractice(Guid runId, TargetId target)
    {
        if (_disposed || PracticeProgress is not { } progress || progress.RunId != runId
            || progress.CommandGeneration != _hook.CommandGeneration
            || string.IsNullOrWhiteSpace(target.Backend) || string.IsNullOrWhiteSpace(target.Value)
            || target.Lifetime == Guid.Empty)
            return false;
        if (progress.Target is { } existing)
            return existing == target;
        _practiceBoundAt = _loop.NowMs;
        PracticeProgress = progress with
        {
            Target = target
        };
        PracticeProgressChanged?.Invoke(PracticeProgress);
        return true;
    }
    public bool EndOnDemandPractice(Guid runId)
    {
        if (PracticeProgress?.RunId != runId)
            return false;
        _lastRetiredPracticeRun = runId;
        _practiceWakeCommand = null;
        PracticeProgress = null;
        PracticeProgressChanged?.Invoke(null);
        return true;
    }
    private OnDemandPracticeProgress? PracticeCommand(InputNotice notice, bool direct)
    {
        // AcceptedAtMs is the existing adapter acceptance timestamp, not proof of
        // a physical key-down. Ties are conservative: queued earlier commands
        // cannot become practice evidence just because they were drained later.
        if (PracticeProgress is not { Target: not null } progress || notice.Source?.Target != progress.Target
            || notice.CommandGeneration == Guid.Empty || notice.CommandGeneration != progress.CommandGeneration
            || notice.CommandSequence <= 0
            || notice.AcceptedAtMs <= _practiceBoundAt || notice.AcceptedAtMs > _loop.NowMs
            || direct && (!progress.PreviewCompleted || progress.DirectReplacementCompleted
                || notice.AcceptedAtMs <= _practicePreviewAt))
            return null;
        return progress with
        {
            CommandSequence = notice.CommandSequence,
            Revision = _commandFeedbackRevision
        };
    }
    private void CompletePracticeCommand(OnDemandPracticeProgress? command, bool direct)
    {
        // Begin publishes the current command revision as a watermark. Even a
        // reused host run ID cannot adopt work accepted before that registration.
        if (command is null || PracticeProgress is not { } progress || progress.RunId != command.RunId
            || progress.Target != command.Target || command.CommandGeneration != _hook.CommandGeneration
            || command.Revision != _commandFeedbackRevision || command.Revision <= progress.Revision
            || direct && (!command.PreviewCompleted || !progress.PreviewCompleted || progress.DirectReplacementCompleted)
            || !direct && progress.PreviewCompleted)
            return;
        if (!direct)
            _practicePreviewAt = _loop.NowMs;
        PracticeProgress = command with
        {
            PreviewCompleted = true,
            DirectReplacementCompleted = direct
        };
        PracticeProgressChanged?.Invoke(PracticeProgress);
    }
    public SessionController(SettingsStore settings, ITranslationService model, ISessionView overlay,
        IInputMonitor input, IInputService broker, ISessionLoop loop,
        Func<OwnedPracticeBinding, Task<bool>>? retireOwnedPractice = null,
        Func<OwnedPracticeBinding, Task<bool>>? confirmRetiredOwnedPractice = null)
    {
        _retireOwnedPractice = retireOwnedPractice;
        _confirmRetiredOwnedPractice = confirmRetiredOwnedPractice;
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
        overlay.DraftChanged = ChangePracticeDraft;
        overlay.ReturnRequested = token => { if (ViewCommandIsCurrent(token, true)) _ = ReturnFromEditor(); };
        overlay.EscapeRequested = token => { if (ViewCommandIsCurrent(token, false)) _ = Escape(); };
        overlay.RegenerateRequested = () => _ = Regenerate();
        overlay.ExcludeRequested = () => _ = ExcludeCurrentApplication();
        overlay.ResumeRequested = revision => _ = ResumePause(revision);
        settings.Changed += Configure;
    }
    private async Task ExcludeCurrentApplication()
    {
        if (_disposed || !_settings.Value.Enabled)
            return;
        if (LastApplication is not { } app)
        {
            StopSession();
            return;
        }
        var next = _settings.Excluding(app);
        bool keepPractice = AutomaticPracticeProgress is not null && !_settings.HasChanges(next);
        if (!await PrepareSettingsChangeAsync(next) || _disposed)
        {
            if (!_disposed)
                SetServiceStatus("练习尚未确认结束，未更改应用排除设置。请重启 TypeLay 后重试。", "practice-retirement-unconfirmed");
            return;
        }
        // Recompute against the current settings after the retirement await.
        try
        {
            _settings.Exclude(app);
            if (!keepPractice)
                StopSession();
        }
        catch (SettingsPersistenceException)
        {
            SetServiceStatus("排除设置未能保存，请检查设置存储后重试。", "exclude-save-failed");
        }
    }
    public async Task StartAsync()
    {
        await StartBroker();
        Configure();
        _loop.Start();
        Changed?.Invoke();
    }
    private void RefreshConnectionStatus()
    {
        var state = _startingBroker ? InputConnectionState.Connecting
            : !_broker.Available ? _retryBrokerAt > 0 ? InputConnectionState.Reconnecting : InputConnectionState.Unavailable
            : !_hook.Ready ? InputConnectionState.ListenerUnavailable
            : _inputReadFailed ? InputConnectionState.Unavailable : InputConnectionState.Connected;
        if (ConnectionState == state)
            return;
        ConnectionState = state;
        Changed?.Invoke();
    }
    private async Task StartBroker()
    {
        if (_disposed || _startingBroker)
            return;
        if (_broker.Available)
        {
            RefreshConnectionStatus();
            return;
        }
        _startingBroker = true;
        long generation = _generation;
        RefreshConnectionStatus();
        try
        {
            await _broker.StartAsync();
            if (_disposed)
                return;
            if (generation == _generation)
                _inputReadFailed = false;
            _nextCapture = 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed)
                _retryBrokerAt = _loop.NowMs + 2000;
        }
        finally
        {
            _startingBroker = false;
            if (!_disposed)
                RefreshConnectionStatus();
        }
    }
    private void Configure()
    {
        _pendingPracticeCompletion = null;
        RetireAutomaticPractice(false);
        if (PracticeProgress is { } practice)
            EndOnDemandPractice(practice.RunId);
        _pendingWakeCapture = null;
        InvalidatePauseAnchor();
        _sessionShortcuts = null;
        _nextCapture = 0;
        _configurationRevision++;
        _commandFeedbackRevision++;
        if (_settings.Value.Enabled && OperationStatus == ResumeSaveFailureStatus)
            OperationStatus = null;
        _lastCommandSequence = 0;
        if (_pendingDirect is not null)
            SetServiceStatus(_directWriteSubmitted
                ? "设置已改变，但写回请求已经发出；结果尚未确认，请检查原输入框，不会自动重试。"
                : "设置已改变，未执行本次直接替换。",
                _directWriteSubmitted ? "write-unconfirmed" : "direct-canceled",
                _pendingDirectCommandGeneration, _pendingDirectSequence);
        _pendingDirect = null;
        _activation.SetMode(_settings.Value.Mode);
        _activation.End();
        _hook.ConfigureShortcuts(_settings.Value.Enabled && _settings.Value.Mode == WorkMode.OnDemand,
            _settings.Value.WakeShortcut, _settings.Value.DirectShortcut, CurrentSessionBindings, _settings.Value.Enabled);
        ClearBrokerSnapshot();
        _generation++;
        RevokeInput("configure");
        _permitUntil = 0;
        _session.SetPaused(!_settings.Value.Enabled);
        _session.SetDirection(_settings.Value.Direction, _loop.NowMs);
        _overlay.Configure(_settings.Value);
        Render();
        Changed?.Invoke();
    }
    private void DrainInputNotices()
    {
        while (_hook.TryRead(out var notice))
        {
            if (notice?.CompletedReceipt is not null && notice.Permit is not null)
                CompleteNativeWrite(notice);
            else if (notice?.Kind is "wake" or "direct")
                Shortcut(notice);
            else if (notice?.Kind == "escape" && notice.CommandGeneration == _hook.CommandGeneration
                && notice.SessionRevision == _generation && notice.UserEpoch == _hook.UserEpoch
                && notice.ForegroundEpoch == _hook.ForegroundEpoch
                && notice.Target is { } target && _session.Current?.Target == target && SessionBindingsReady(target, false))
                _ = Escape();
            else if (notice?.Permit is not null)
                _ = Accept(notice);
        }
    }
    private void Shortcut(InputNotice notice)
    {
        if (!_settings.Value.Enabled || _settings.Value.Mode != WorkMode.OnDemand
            || notice.CommandGeneration != _hook.CommandGeneration || notice.CommandSequence < 0)
            return;
        if (_lastCommandGeneration != notice.CommandGeneration)
        {
            _lastCommandGeneration = notice.CommandGeneration;
            _lastCommandSequence = 0;
        }
        if (notice.CommandSequence > 0)
        {
            if (notice.CommandSequence <= _lastCommandSequence)
                return;
            _lastCommandSequence = notice.CommandSequence;
        }
        _commandFeedbackRevision++;
        _commandSequence = notice.CommandSequence;
        if (notice.Rejection != Denial.None)
        {
            RejectShortcut(notice.Kind, notice.Rejection);
            return;
        }
        if (notice.Source is null)
        {
            RejectShortcut(notice.Kind, Denial.Unsupported);
            return;
        }
        if (notice.Kind == "wake")
            Wake(notice);
        else
            QueueDirect(notice);
    }
    private void RejectShortcut(string kind, Denial reason)
    {
        string action = kind == "wake" ? "唤醒翻译" : "直接替换";
        string explanation = reason switch
        {
            Denial.ImeUnclear => "当前输入框的输入法状态无法确认。",
            Denial.StaleObservation => "输入观察尚不可用或已过期，请在目标输入框重试。",
            Denial.SourceChanged or Denial.SelectionChanged or Denial.EpochChanged => "输入内容或选区已变化，请重试。",
            Denial.NotSourceFocus or Denial.TargetChanged => "输入框焦点已变化，请在目标输入框重试。",
            Denial.Sensitive or Denial.ReadOnly => "当前字段不允许此操作。",
            Denial.Unsupported => kind == "direct" ? "当前输入框不支持可靠的直接替换。" : "当前输入框尚未连接或不支持读取。",
            Denial.PlatformFailure => "输入服务尚未就绪，请在应用支持中检查连接。",
            _ => "当前输入状态不满足安全条件。"
        };
        SetServiceStatus($"未执行本次{action}：{explanation}", "shortcut-" + reason.ToString().ToLowerInvariant());
        Trace("shortcut-rejected", detail: new
        {
            action = kind,
            reason = reason.ToString(),
            commandGeneration = _lastCommandGeneration,
            commandSequence = _commandSequence
        });
    }
    private void Tick()
    {
        if (_disposed)
            return;
        RefreshConnectionStatus();
        CheckAutomaticPracticeIdentity();
        DrainInputNotices();
        if (_awaitingPractice is not null && (_automaticRetirementUnconfirmed || _automaticRetirement is { IsCompleted: false }))
            return;
        long now = _loop.NowMs;
        if (_pendingDirect is { } pending && !_directRunning)
        {
            if (!_model.Ready || !_broker.Available || now - pending.TriggeredAtMs > 120_000)
            {
                _pendingDirect = null;
                SetCommandStatus("本地服务不可用或等待超时，未执行直接替换。", _pendingDirectFeedbackRevision, "service-unavailable");
            }
            else if (!_translationRunning && !_model.Busy)
                _ = TranslateDirect(pending, _pendingDirectFeedbackRevision);
        }
        if (!_broker.Available && !_startingBroker && now >= _retryBrokerAt)
            _ = StartBroker();
        if (_pendingWakeCapture is { } wake && _hook.GetFocus(wake.Target) == FocusMember.Other)
            _pendingWakeCapture = null;
        if (_session.Current is { } current)
        {
            if (_hook.GetFocus(current.Target) == FocusMember.Other)
                StopSession("focus-left");
        }
        if (!_hook.Ready || !_broker.Available)
        {
            RevokeInput("input-unavailable");
            InvalidatePauseAnchor();
            _overlay.HideQuietly();
            return;
        }
        // A finished owned field has no remaining translation or acceptance
        // work. Its body-free receipt survives ordinary observation changes.
        if (_pendingPracticeCompletion is { } completed
            && _hook.GetFocus(completed.Binding.Target) != FocusMember.Other)
            return;
        if (!_settings.Value.Enabled)
        {
            RevokeInput("paused");
            _permitUntil = 0;
            if (_pauseAnchor is not null && !PauseAnchorIsCurrent(now))
            {
                InvalidatePauseAnchor();
                _overlay.HideQuietly();
            }
            if (!_observingPause && !_resumingPause && now >= _nextCapture)
            {
                _nextCapture = now + 35;
                _ = ObservePause();
            }
            return;
        }
        // Focus restoration may await the real input context. A concurrent read
        // before that operation completes must not discard the saved editor draft.
        if (_returningToSource)
        {
            RevokeInput("returning-to-source");
            _permitUntil = 0;
            return;
        }
        // A private handoff is bounded by the existing edit deadline. Do not
        // read an unfocused source while GTK is still transferring its IM context.
        if (_ownedPractice is not null && _editTransitionUntil != 0
            && _session.State == SessionState.EditingTranslation && _session.Current is { } handoffSource)
        {
            if (_hook.GetFocus(handoffSource.Target) == FocusMember.Overlay)
                _editTransitionUntil = 0;
            else
            {
                if (now >= _editTransitionUntil)
                    StopSession("practice-editor-timeout");
                return;
            }
        }
        // The editor owns focus and its saved source basis until the user returns.
        // Chromium may expose no source selection when another window has focus.
        // Revalidate the source on return instead of polling an unfocused TextPattern.
        if (_session.State == SessionState.EditingTranslation && _session.Current is { } editingSource && _hook.GetFocus(editingSource.Target) == FocusMember.Overlay)
        {
            RevokeInput("editing-translation");
            _permitUntil = 0;
            return;
        }
        // UI independently expires observations while a broker call is blocked.
        if (!_writing && _session.Current is { } old && now - old.ObservedAtMs > Limits.ObservationMaxAgeMs)
        {
            RevokeInput("observation-expired");
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
        else if (_renderedAcceptanceReady != CanPresentAcceptance(now))
            Render();
    }
    private SessionShortcutBindings CurrentSessionBindings =>
        SessionShortcutBindings.Restore(_settings.Value.AcceptShortcut, _settings.Value.ExitShortcut);
    private bool SessionBindingsReady(TargetId target, bool editor) => _settings.Value.Enabled
        && _hook.Ready && _broker.Available && _sessionShortcuts is { SourceReady: true } acknowledgement
        && acknowledgement.Target == target && acknowledgement.Generation == _hook.CommandGeneration
        && acknowledgement.Bindings == CurrentSessionBindings && (!editor || acknowledgement.EditorReady);
    private bool ViewCommandIsCurrent(SessionViewToken token, bool editor) => !_disposed
        && token.Revision == _generation && token.CommandGeneration == _hook.CommandGeneration
        && _session.Current?.Target == token.Target && SessionBindingsReady(token.Target, editor);
    private InputContext Context() => new(_hook.UserEpoch, _hook.ForegroundEpoch,
        _hook.SourceUserEpoch, _session.Current?.Target, _settings.Value.Excluded,
        _settings.Value.Enabled && _settings.Value.Mode == WorkMode.OnDemand,
        new(_hook.CommandGeneration, CurrentSessionBindings), PracticeReturnRead() ?? ContinuousPracticeRead());
    private bool PauseAnchorIsCurrent(long now) => _pauseAnchor is { IsUsable: true } anchor
        && _pauseWindowEpoch == _hook.WindowEpoch && _pauseForegroundEpoch == _hook.ForegroundEpoch
        && anchor.FocusEpoch == _hook.ForegroundEpoch && _hook.GetFocus(anchor.Target) == FocusMember.Source
        && now >= anchor.ObservedAtMs && now - anchor.ObservedAtMs <= Limits.ObservationMaxAgeMs;
    private void InvalidatePauseAnchor()
    {
        _pauseAnchor = null;
        _pauseViewRevision++;
    }
    private bool PauseObservationIsCurrent(PauseObservation result, InputContext request, long windowEpoch)
    {
        long now = _loop.NowMs;
        return !_disposed && !_settings.Value.Enabled && _hook.Ready && _broker.Available
            && windowEpoch == _hook.WindowEpoch && request.ForegroundEpoch == _hook.ForegroundEpoch
            && result.ForegroundEpoch == request.ForegroundEpoch && result.Focus == FocusMember.Source
            && result.Anchor is { IsUsable: true } anchor && anchor.FocusEpoch == request.ForegroundEpoch
            && now >= anchor.ObservedAtMs && now - anchor.ObservedAtMs <= Limits.ObservationMaxAgeMs
            && _hook.GetFocus(anchor.Target) == FocusMember.Source
            && !string.IsNullOrWhiteSpace(result.Application)
            && !_settings.Value.Excluded.Contains(result.Application, StringComparer.OrdinalIgnoreCase);
    }
    private async Task ObservePause()
    {
        _observingPause = true;
        long generation = _generation;
        long revision = _pauseViewRevision;
        long windowEpoch = _hook.WindowEpoch;
        var request = Context() with
        {
            BoundTarget = null,
            CaptureDirect = false
        };
        try
        {
            var result = await _broker.ObservePauseAsync(request);
            if (_disposed || generation != _generation || revision != _pauseViewRevision || _settings.Value.Enabled)
                return;
            if (!PauseObservationIsCurrent(result, request, windowEpoch))
            {
                InvalidatePauseAnchor();
                _overlay.HideQuietly();
                return;
            }
            if (_pauseAnchor?.Target != result.Anchor!.Target)
                _pauseViewRevision++;
            _pauseAnchor = result.Anchor;
            _pauseWindowEpoch = windowEpoch;
            _pauseForegroundEpoch = request.ForegroundEpoch;
            _overlay.RenderPaused(_pauseAnchor, _pauseViewRevision);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed && generation == _generation && revision == _pauseViewRevision)
            {
                InvalidatePauseAnchor();
                _overlay.HideQuietly();
            }
        }
        finally { _observingPause = false; }
    }
    private async Task ResumePause(long revision)
    {
        if (_disposed || _settings.Value.Enabled || _resumingPause || revision != _pauseViewRevision)
            return;
        if (!PauseAnchorIsCurrent(_loop.NowMs) || !_hook.Ready || !_broker.Available)
        {
            InvalidatePauseAnchor();
            _overlay.HideQuietly();
            return;
        }
        var target = _pauseAnchor!.Target;
        long generation = _generation;
        long windowEpoch = _hook.WindowEpoch;
        var request = Context() with
        {
            BoundTarget = null,
            CaptureDirect = false
        };
        _resumingPause = true;
        _commandFeedbackRevision++;
        bool saving = false;
        try
        {
            // A click is not proof that the field's metadata stayed safe. Re-read
            // metadata only, then retire the action if any configuration/view won.
            var result = await _broker.ObservePauseAsync(request);
            if (_disposed || generation != _generation || revision != _pauseViewRevision || _settings.Value.Enabled)
                return;
            if (!PauseObservationIsCurrent(result, request, windowEpoch) || result.Anchor!.Target != target)
            {
                InvalidatePauseAnchor();
                _overlay.HideQuietly();
                return;
            }
            saving = true;
            _settings.Update(_settings.Value with
            {
                Enabled = true
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed && generation == _generation && revision == _pauseViewRevision)
            {
                InvalidatePauseAnchor();
                _overlay.HideQuietly();
                if (saving && !_settings.Value.Enabled)
                    SetServiceStatus(ResumeSaveFailureStatus, "resume-failed", Guid.Empty, 0);
            }
        }
        finally { _resumingPause = false; }
    }
    private async Task Capture()
    {
        _capturing = true;
        long generation = _generation;
        long windowEpoch = _hook.WindowEpoch;
        long contentEpoch = _hook.ContentEpoch;
        var request = Context();
        bool traceCapture = false;
        if (!string.IsNullOrWhiteSpace(_tracePath) && _settings.Value.Mode == WorkMode.OnDemand
            && PracticeProgress is { } practice && request.SessionShortcuts is { } configuration
            && practice.CommandGeneration == configuration.Generation)
        {
            if (_practiceCaptureTraceGeneration != configuration.Generation)
            {
                _practiceCaptureTraceGeneration = configuration.Generation;
                _practiceCaptureTraceCount = 0;
                _practiceCaptureTracePublished = false;
            }
            traceCapture = !_practiceCaptureTracePublished && _practiceCaptureTraceCount < 16;
            if (traceCapture)
                _practiceCaptureTraceCount++;
        }
        long startedAtMs = traceCapture ? _loop.NowMs : 0;
        long? resumedAtMs = null, observedAtMs = null;
        FocusMember? resultFocus = null, checkedFocus = null;
        string captureExit = "exception";
        try
        {
            var result = await _broker.CaptureAsync(request);
            if (traceCapture)
            {
                resumedAtMs = _loop.NowMs;
                observedAtMs = result.Snapshot?.ObservedAtMs;
                resultFocus = result.Focus;
            }
            // Input already queued before this read completed owns the earlier action.
            // Accepting it advances generation before an obsolete read can clear it.
            if (!_disposed)
                DrainInputNotices();
            // A native refusal remains unknown even if foreground age expired
            // while this awaiting read was in flight. Never relabel it as age.
            if (!_disposed && _awaitingPractice is { } awaiting && request.PracticeRead == awaiting with
            {
                CommandGeneration = _hook.CommandGeneration
            }
                && result.Snapshot is null && result.OwnedSource is null && !result.ForegroundExpired)
                StopSession("practice-continuity-lost");
            CheckAutomaticPracticeIdentity();
            if (_disposed || generation != _generation)
            {
                captureExit = _disposed ? "disposed" : "generation-changed";
                return;
            }
            if (_pendingWakeCapture is { } pending && _hook.GetFocus(pending.Target) == FocusMember.Other)
                _pendingWakeCapture = null;
            if (windowEpoch != _hook.WindowEpoch)
            {
                captureExit = "window-changed";
                _pendingWakeCapture = null;
                Trace("capture-window-changed", detail: new
                {
                    captureGeneration = generation,
                    currentGeneration = _generation,
                    captureCommandGeneration = request.SessionShortcuts?.Generation,
                    commandGeneration = _hook.CommandGeneration,
                    captureWindowEpoch = windowEpoch,
                    captureContentEpoch = contentEpoch,
                    contentEpoch = _hook.ContentEpoch,
                    captureForegroundEpoch = request.ForegroundEpoch,
                    resultForegroundEpoch = result.ForegroundEpoch,
                    hasSnapshot = result.Snapshot is not null,
                    resultFocus = result.Focus.ToString()
                });
                return;
            }
            long now = _loop.NowMs;
            bool observationFresh = result.Snapshot is { } observation && now >= observation.ObservedAtMs
                && now - observation.ObservedAtMs <= Limits.ObservationMaxAgeMs;
            if (result.Snapshot is null || observationFresh)
            {
                _inputReadFailed = false;
                RefreshConnectionStatus();
            }
            if (SuspendAutomaticRead(request, result, now))
            {
                captureExit = "automatic-read-suspended";
                return;
            }
            if (ObserveAwaitingPractice(result, now) || ObservePracticeReturn(result, now))
            {
                captureExit = "awaiting-practice";
                return;
            }
            if (result.Snapshot is not { } snapshot || result.Focus == FocusMember.Other)
            {
                captureExit = "capture-empty";
                var pendingWake = _pendingWakeCapture;
                StopSession("capture-empty");
                if (result.Snapshot is null && pendingWake is not null && pendingWake.Generation == generation
                    && pendingWake.FeedbackRevision == _commandFeedbackRevision
                    && pendingWake.CommandGeneration == _hook.CommandGeneration)
                    SetServiceStatus("暂时无法读取当前输入框，请保持焦点后再试一次。", "wake-capture-unavailable",
                        pendingWake.CommandGeneration, pendingWake.CommandSequence);
                return;
            }
            checkedFocus = _hook.GetFocus(snapshot.Target);
            if (checkedFocus == FocusMember.Other)
            {
                captureExit = "focus-changed";
                return;
            }
            if (!observationFresh)
            {
                captureExit = "capture-expired";
                RevokeInput("capture-expired");
                _permitUntil = 0;
                Render();
                return;
            }
            if (_editTransitionUntil > now && result.Focus == FocusMember.Source
                && _session.State == SessionState.EditingTranslation)
            {
                captureExit = "edit-transition";
                return;
            }
            if (result.Focus == FocusMember.Overlay)
                _editTransitionUntil = 0;
            _sessionShortcuts = result.SessionShortcuts is { } acknowledgement
                && acknowledgement.Target == snapshot.Target && acknowledgement.Generation == _hook.CommandGeneration
                && acknowledgement.Bindings == CurrentSessionBindings ? acknowledgement : null;
            LastApplication = result.Application;
            bool active = _activation.Observe(snapshot.Target, result.Focus);
            if (!active)
            {
                _pendingWakeCapture = null;
                if (_session.Current is not null)
                {
                    _generation++;
                    _session.LeaveFamily();
                }
                _permitUntil = 0;
                _hook.Publish(new(snapshot, request.UserEpoch, request.ForegroundEpoch, false, _hook.CommandGeneration, _generation), null);
                if (result.Focus == FocusMember.Source)
                {
                    _hook.PublishShortcut(new(snapshot, contentEpoch, request.ForegroundEpoch));
                    captureExit = "shortcut-publish-called";
                    if (traceCapture)
                        _practiceCaptureTracePublished = true;
                }
                else
                    captureExit = "inactive-non-source";
                _overlay.HideQuietly();
                return;
            }
            if (_session.Current is { } previous && previous.Target != snapshot.Target)
            {
                // A pending capture can cross fields before Tick observes focus
                // leaving. Retire old view tokens even if the user later returns.
                _generation++;
                RevokeInput("target-changed");
                _permitUntil = 0;
            }
            _session.Observe(snapshot, result.Focus, now);
            if (_awaitingPracticeReturn is not null && _session.State != SessionState.EditedTranslationPending)
            {
                StopSession("practice-return-source-changed");
                return;
            }
            ObserveAutomaticReturn();
            // Reading the source completes wake even when translation or writing remains unavailable.
            if (_pendingWakeCapture is { } wake && wake.Generation == generation
                && wake.CommandGeneration == _hook.CommandGeneration && wake.Target == snapshot.Target
                && result.Focus == FocusMember.Source && _hook.GetFocus(snapshot.Target) == FocusMember.Source)
                _pendingWakeCapture = null;
            AcceptancePermit? permit = null;
            // A keystroke arriving during a remote read must revoke acceptance, but
            // must not starve preview capture while the user continues typing.
            bool inputUnchanged = result.UserEpoch == _hook.UserEpoch && result.ForegroundEpoch == _hook.ForegroundEpoch;
            var practiceBinding = AutomaticPracticeFor(snapshot.Target);
            if (inputUnchanged && result.Focus == FocusMember.Source && _model.Ready
                && !RetiredAutomaticTarget(snapshot.Target)
                && (_settings.Value.ExperimentalWriteback || practiceBinding is not null)
                && snapshot.WriteSupported && _hook.CanInterceptAcceptance && SessionBindingsReady(snapshot.Target, false))
            {
                var context = new AcceptanceContext(FocusMember.Source, false, false, snapshot.Ime,
                    snapshot.InputEpoch, snapshot.FocusEpoch);
                var (plan, _) = _session.Prepare(snapshot, context, now);
                if (plan is not null)
                    permit = new(plan, snapshot.InputEpoch, snapshot.FocusEpoch, snapshot.ObservedAtMs,
                        snapshot.ObservedAtMs + Limits.PermitMaxAgeMs);
            }
            _permitUntil = permit?.ExpiresAtMs ?? 0;
            _presentationPermit = permit;
            bool escape = SessionBindingsReady(snapshot.Target, false)
                && _session.State is not (SessionState.NoSession or SessionState.Paused or SessionState.OutcomeUnknown);
            RememberAutomaticPermit(practiceBinding, permit);
            _hook.Publish(new(snapshot, request.UserEpoch, request.ForegroundEpoch, escape, _hook.CommandGeneration, _generation,
                permit is null ? null : practiceBinding?.RunId, permit is null ? null : practiceBinding?.ConnectionId), permit);
            if (result.Focus == FocusMember.Source)
            {
                _hook.PublishShortcut(new(snapshot, contentEpoch, request.ForegroundEpoch));
                captureExit = "shortcut-publish-called";
                if (traceCapture)
                    _practiceCaptureTracePublished = true;
            }
            else
                captureExit = "active-non-source";
            Render();
            Trace(inputUnchanged ? "capture" : "capture-input-changed");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (traceCapture)
                resumedAtMs ??= _loop.NowMs;
            captureExit = "capture-failed";
            if (!_disposed)
                DrainInputNotices();
            if (_disposed || generation != _generation)
            {
                captureExit = _disposed ? "disposed" : "generation-changed";
                return;
            }
            var pendingWake = _pendingWakeCapture;
            StopSession("capture-failed");
            _inputReadFailed = true;
            _retryBrokerAt = _loop.NowMs + 2000;
            RefreshConnectionStatus();
            if (pendingWake is not null && pendingWake.Generation == generation
                && pendingWake.FeedbackRevision == _commandFeedbackRevision
                && pendingWake.CommandGeneration == _hook.CommandGeneration
                && windowEpoch == _hook.WindowEpoch && _hook.GetFocus(pendingWake.Target) != FocusMember.Other)
                SetServiceStatus("暂时无法读取当前输入框，请保持焦点后再试一次。", "wake-capture-unavailable",
                    pendingWake.CommandGeneration, pendingWake.CommandSequence);
        }
        finally
        {
            _capturing = false;
            // Only the first bounded observations of a registered on-demand practice.
            // Record existing gate results; never probe native focus for diagnostics.
            if (traceCapture)
                Trace("practice-capture-exit", detail: new
                {
                    reason = captureExit,
                    captureGeneration = generation,
                    currentGeneration = _generation,
                    captureCommandGeneration = request.SessionShortcuts?.Generation,
                    commandGeneration = _hook.CommandGeneration,
                    captureWindowEpoch = windowEpoch,
                    captureContentEpoch = contentEpoch,
                    captureForegroundEpoch = request.ForegroundEpoch,
                    startedAtMs,
                    resumedAtMs,
                    awaitMs = resumedAtMs - startedAtMs,
                    afterResumeMs = _loop.NowMs - resumedAtMs,
                    observedAtMs,
                    resultFocus = resultFocus?.ToString(),
                    checkedFocus = checkedFocus?.ToString()
                }, inspectFocus: false);
        }
    }
    private void Wake(InputNotice notice)
    {
        void Record(string reason, bool? generationMatches = null, bool? foregroundMatches = null, bool? focusMatches = null)
        {
            if (!string.IsNullOrWhiteSpace(_tracePath))
                Trace("wake-" + reason, detail: new
                {
                    ageMs = _loop.NowMs - notice.AcceptedAtMs,
                    commandGeneration = notice.CommandGeneration,
                    commandSequence = notice.CommandSequence,
                    generationMatches,
                    foregroundMatches,
                    focusMatches
                });
        }
        if (!_settings.Value.Enabled)
        {
            Record("disabled");
            return;
        }
        if (_settings.Value.Mode != WorkMode.OnDemand)
        {
            Record("mode");
            return;
        }
        if (notice.Source is not { } source)
        {
            Record("source-missing");
            return;
        }
        if (notice.CommandGeneration != _hook.CommandGeneration)
        {
            Record("generation", false);
            return;
        }
        if (notice.ForegroundEpoch != _hook.ForegroundEpoch)
        {
            Record("foreground-epoch", true, false);
            RejectShortcut(notice.Kind, Denial.NotSourceFocus);
            return;
        }
        if (_hook.GetFocus(source.Target) != FocusMember.Source)
        {
            Record("focus", true, true, false);
            RejectShortcut(notice.Kind, Denial.NotSourceFocus);
            return;
        }
        if (_activation.Wake(source.Target))
        {
            OperationStatus = null;
            Changed?.Invoke();
            // Explicit wake can reopen an Esc-suppressed field; repeated wake must
            // not erase a live candidate or a user's translation draft.
            bool awaitingCapture = _pendingWakeCapture?.Generation == _generation
                || _session.Current?.Target != source.Target || _session.State == SessionState.NoSession;
            if (_session.State == SessionState.NoSession)
                _session.LeaveFamily();
            _generation++;
            _practiceWakeCommand = PracticeCommand(notice, false);
            _pendingWakeCapture = awaitingCapture
                ? new(source.Target, _generation, _commandFeedbackRevision, notice.CommandGeneration, notice.CommandSequence)
                : null;
            _nextCapture = 0;
            Record("awakened", true, true, true);
        }
        else
            Record("already-awake", true, true, true);
    }
    private void QueueDirect(InputNotice notice)
    {
        if (!_settings.Value.Enabled || _settings.Value.Mode != WorkMode.OnDemand || notice.Source is not { } source
            || notice.CommandGeneration != _hook.CommandGeneration)
            return;
        if (_pendingDirect is not null || _directRunning)
        {
            SetServiceStatus("已有一次直接翻译正在处理，未追加操作。", "direct-busy");
            return;
        }
        if (!source.DirectWriteSupported)
        {
            RejectShortcut(notice.Kind, Denial.Unsupported);
            return;
        }
        if (!_model.Ready)
        {
            SetServiceStatus("本地模型尚未就绪，未执行直接替换。", "model-not-ready");
            return;
        }
        var (operation, reason) = DirectTranslation.Start(source, _settings.Value.Direction, notice.AcceptedAtMs);
        if (operation is null)
        {
            SetServiceStatus(reason == Denial.NoCandidate ? "没有需要直接翻译的文本。" : "无法确认原输入内容的安全状态，未执行直接替换。", "shortcut-" + reason.ToString().ToLowerInvariant());
            return;
        }
        // The trigger's snapshot, not the currently focused field, owns this operation.
        // Ending a foreground session never clears a pending direct operation.
        StopSession();
        _pendingDirect = operation;
        _pendingDirectPractice = PracticeCommand(notice, true);
        _pendingDirectFeedbackRevision = _commandFeedbackRevision;
        _pendingDirectCommandGeneration = notice.CommandGeneration;
        _pendingDirectSequence = notice.CommandSequence;
        SetServiceStatus("正在直接翻译；可以切换输入框，当前操作仍绑定原输入框。");
    }
    private async Task TranslateDirect(DirectTranslation operation, long feedbackRevision)
    {
        _directRunning = true;
        long configuration = _configurationRevision;
        var practiceCommand = _pendingDirectPractice;
        try
        {
            var response = await _model.TranslateAsync(operation.Job);
            if (_disposed || configuration != _configurationRevision)
                return;
            if (response.Status != "translated")
            {
                SetCommandStatus(response.Status == "no-translation" ? "当前文本无需翻译，未作替换。" : "直接翻译未完成，原文未作替换。", feedbackRevision,
                    response.Status == "no-translation" ? "no-translation" : "translation-failed");
                return;
            }
            var (plan, _) = operation.Complete(response.Text);
            if (plan is null)
            {
                SetCommandStatus("译文未通过内容校验，未执行直接替换。", feedbackRevision, "translation-invalid");
                return;
            }
            // Never route detached work through foreground-only write/return operations.
            _directWriteSubmitted = true;
            var result = await _broker.WriteDirectAsync(plan, _settings.Value.Excluded);
            if (_disposed)
                return;
            // Only an explicit pre-mutation rejection proves that TypeLay did not
            // replace text. Reasons, including Unsupported, cannot override an
            // unknown status. Future or malformed status values stay conservative.
            var (message, code) = result.Status switch
            {
                WriteStatus.Verified => ("已直接替换原输入框文字。", (string?)null),
                WriteStatus.RejectedBeforeMutation => (DirectWriteRejection(result.Reason), "write-rejected"),
                _ => ("直接写回未获确认，请检查原输入框；不会自动重试。", "write-unconfirmed")
            };
            SetCommandStatus(message, feedbackRevision, code);
            if (practiceCommand?.Target == plan.Expected.Target
                && result.Status == WriteStatus.Verified && result.Reason == Denial.None
                && result.VerifiedSnapshot is { IsUsable: true } after && after.Target == plan.Expected.Target
                && after.Text == plan.ResultText && after.Selection == new TextRange(plan.Caret, plan.Caret))
                CompletePracticeCommand(practiceCommand, true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed)
                SetCommandStatus(_directWriteSubmitted
                    ? "直接写回未获确认，请检查原输入框；不会自动重试。"
                    : "直接翻译服务中断，TypeLay 未替换文字。", feedbackRevision,
                    _directWriteSubmitted ? "write-unconfirmed" : "service-unavailable");
        }
        finally
        {
            if (ReferenceEquals(_pendingDirect, operation))
                _pendingDirect = null;
            _directRunning = false;
            _directWriteSubmitted = false;
        }
    }
    private static string DirectWriteRejection(Denial reason) => reason switch
    {
        Denial.Unsupported => "当前输入框不支持可靠的直接替换，TypeLay 未替换文字。",
        Denial.Sensitive or Denial.ReadOnly => "当前字段不允许直接替换，TypeLay 未替换文字。",
        Denial.ImeUnclear => "输入法状态尚未确认，TypeLay 未替换文字。请完成或取消当前输入后再操作。",
        Denial.SourceChanged or Denial.SelectionChanged or Denial.TargetChanged or Denial.EpochChanged
            => "输入内容、选区或字段已变化，TypeLay 未替换文字。请确认目标输入框后再操作。",
        _ => "直接替换未通过安全检查，TypeLay 未替换文字。请检查原输入框。"
    };
    private void SetCommandStatus(string status, long feedbackRevision, string? feedbackCode = null)
    {
        if (feedbackRevision == _commandFeedbackRevision)
            SetServiceStatus(status, feedbackCode);
    }
    private void SetServiceStatus(string status, string? feedbackCode = null,
        Guid? commandGeneration = null, long? commandSequence = null)
    {
        OperationStatus = status;
        if (feedbackCode is not null)
            OperationFeedback?.Invoke(new(_commandFeedbackRevision, commandGeneration ?? _lastCommandGeneration,
                commandSequence ?? _commandSequence, feedbackCode, status));
        Changed?.Invoke();
    }
    private async Task Translate(TranslationJob job)
    {
        _translationRunning = true;
        _statusOverride = null;
        Trace("translate-start");
        var practiceCommand = _practiceWakeCommand;
        var automaticPractice = AutomaticPracticeFor(job.Basis.Target);
        bool completed = false;
        try
        {
            var result = await _model.TranslateAsync(job);
            if (_disposed)
                return;
            if (result.Status == "translated")
                completed = _session.Complete(job, result.Text);
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
            if (completed && practiceCommand?.Target == job.Basis.Target && job.Kind == TranslationKind.FullOrSelection
                && _hook.GetFocus(job.Basis.Target) != FocusMember.Other)
                CompletePracticeCommand(practiceCommand, false);
            if (completed)
                CompleteAutomaticPreview(automaticPractice, job);
            Trace("translate-done");
        }
        finally { _translationRunning = false; }
    }
    private bool BeginEdit(SessionViewToken token)
    {
        var source = _session.Current;
        // Entering the local editor performs no source mutation. Freshness is required
        // again after returning to the source, before an acceptance permit is issued.
        if (source is null || source.Ime != ImeState.Clear || !ViewCommandIsCurrent(token, true))
            return false;
        if (!_session.BeginEdit())
            return false;
        BeginAutomaticEdit();
        _generation++;
        _editTransitionUntil = _loop.NowMs + 500;
        RevokeInput("begin-edit");
        _permitUntil = 0;
        Render();
        return true;
    }
    private async Task<bool> ReturnFromEditor()
    {
        if (_returningToSource || _overlay.EditingComposition || !_session.RequestSourceReturn() || _session.Current is not { } source)
            return false;
        long generation = ++_generation;
        RevokeInput("return-from-editor");
        _permitUntil = 0;
        Render();
        _returningToSource = true;
        var request = _ownedPractice is { } owned
            ? new OwnedPracticeReturnRequest(owned, new(source.Target, _hook.CommandGeneration, generation)) : null;
        _automaticPlans.Clear();
        InputReturn result = new(false);
        try
        {
            result = await _broker.ReturnToSourceAsync(Context() with
            {
                PracticeReturn = request
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { _returningToSource = false; }
        if (_disposed || generation != _generation)
            return false;
        if (result.Returned)
        {
            if (request is not null && !AwaitPracticeReturn(request, result.Practice))
            {
                StopSession("practice-return-unproven");
                return false;
            }
            _nextCapture = 0;
            return true;
        }
        _session.CancelSourceReturn();
        _overlay.ResumeEditor();
        Render();
        return false;
    }
    private void CompleteNativeWrite(InputNotice notice)
    {
        var plan = notice.Permit!.Plan;
        var receipt = notice.CompletedReceipt!;
        if (receipt.Status == WriteStatus.Verified
            && (receipt.VerifiedSnapshot is not { } after || !after.IsUsable
                || after.Target != plan.Expected.Target || after.Text != plan.ResultText
                || after.Selection != new TextRange(plan.Caret, plan.Caret)))
            receipt = new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
        // The native UI thread has already written. Never issue another write,
        // nor discard its terminal receipt because a later observation won a race.
        // Only the original session/plan may consume it as its current UI result.
        if (!_writing && _session.BeginWrite(plan))
        {
            _generation++;
            RevokeInput("native-write-completed");
            _permitUntil = 0;
            _session.FinishWrite(plan, receipt);
            CompleteAutomaticAcceptance(notice, receipt);
            _nextCapture = 0;
            Render();
        }
        Trace("write-" + receipt.Status + "-" + receipt.Reason, plan.Id);
        if (receipt.Status != WriteStatus.Verified)
        {
            OperationStatus = receipt.Status == WriteStatus.OutcomeUnknown ? "写回结果未确认，原目标已隔离" : "写回前安全校验拒绝了本次操作";
            Changed?.Invoke();
        }
    }
    private async Task Accept(InputNotice notice)
    {
        RevokeInput("accept");
        _permitUntil = 0;
        if (notice.Permit is not { } permit || notice.CommandGeneration != _hook.CommandGeneration
            || !SessionBindingsReady(permit.Plan.Expected.Target, false)
            || _loop.NowMs - notice.AcceptedAtMs > Limits.WriteExecutionMaxAgeMs
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
        CompleteAutomaticAcceptance(notice, receipt);
        Trace("write-" + receipt.Status + "-" + receipt.Reason);
        if (receipt.Status != WriteStatus.Verified)
        {
            OperationStatus = receipt.Status == WriteStatus.OutcomeUnknown ? "写回结果未确认，当前目标已隔离" : "写回前安全校验拒绝了本次操作";
            Changed?.Invoke();
        }
        _writing = false;
        _nextCapture = 0;
        Render();
    }
    private async Task Escape()
    {
        if (_disposed || !_settings.Value.Enabled || _overlay.EditingComposition)
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
        RevokeInput("escape");
        _permitUntil = 0;
        _session.Escape();
        _overlay.HideQuietly();
    }
    private async Task Regenerate()
    {
        if (_disposed || !_settings.Value.Enabled || _overlay.EditingComposition)
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
        RevokeInput("regenerate");
        _permitUntil = 0;
        _statusOverride = null;
        _session.Retranslate(_loop.NowMs);
        _nextCapture = 0;
        Render();
    }
    public void StopSession() => StopSession("stop-session");
    private void StopSession(string reason)
    {
        if (_ownedPractice is not null || _awaitingPractice is not null || _awaitingPracticeReturn is not null)
            RetireAutomaticPractice();
        if (_session.Current is not null)
            Trace(reason);
        _activation.End();
        _pendingWakeCapture = null;
        _practiceWakeCommand = null;
        _sessionShortcuts = null;
        InvalidatePauseAnchor();
        ClearBrokerSnapshot();
        _generation++;
        RevokeInput(reason);
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
        if (!_settings.Value.Enabled && PauseAnchorIsCurrent(_loop.NowMs))
        {
            _renderedAcceptanceReady = false;
            _overlay.RenderPaused(_pauseAnchor!, _pauseViewRevision);
            return;
        }
        string? status = _session.State == SessionState.Translating && !_model.Ready ? "正在准备翻译"
            : _session.State == SessionState.TranslationFailed ? _statusOverride : null;
        // The token orders even a read-only preview; it is not an acknowledgement
        // or permission. Every action separately checks the target-bound ACK.
        SessionViewContext? context = _session.Current is { } source
            ? new(new(source.Target, _hook.CommandGeneration, _generation), CurrentSessionBindings,
                SessionBindingsReady(source.Target, true)) : null;
        _renderedAcceptanceReady = CanPresentAcceptance(_loop.NowMs);
        _overlay.Render(_session, _settings.Value, _renderedAcceptanceReady, status, context);
    }
    // Hosts call this only after their existing install/revoke boundary changes.
    // It repaints the current candidate; it cannot issue a plan or update a lease.
    public void RefreshAcceptancePresentation() => Render();
    private bool CanPresentAcceptance(long nowMs)
    {
        if (_presentationPermit is not { } issued || _permitUntil == 0 || nowMs > _permitUntil
            || _session.State is not (SessionState.ValidTranslation or SessionState.EditedTranslationPending)
            || _session.Current is not { } source)
            return false;
        return issued.Plan.Revision == _session.Revision && issued.Plan.Replacement == _session.Candidate
            && issued.Plan.Expected with
            {
                ObservedAtMs = source.ObservedAtMs,
                Bounds = source.Bounds
            } == source
            && _hook.AcceptanceReady(new(source.Target, _hook.CommandGeneration, _generation), issued, nowMs);
    }
    private void RevokeInput(string reason)
    {
        _presentationPermit = null;
        _hook.Revoke();
        if (!string.IsNullOrWhiteSpace(_tracePath))
            Trace("input-revoked", detail: new
            {
                reason,
                commandGeneration = _hook.CommandGeneration,
                // Context only: a previous command need not have caused this revocation.
                lastCommandGeneration = _lastCommandGeneration,
                lastCommandSequence = _commandSequence
            }, inspectFocus: false);
    }
    // Opt-in local diagnostics contain counts and state only, never field text,
    // labels, window titles, exception messages, source or translated content.
    private void Trace(string operation, Guid? planId = null, object? detail = null, bool inspectFocus = true)
    {
        if (string.IsNullOrWhiteSpace(_tracePath))
            return;
        string state = System.Text.Json.JsonSerializer.Serialize(new
        {
            operation,
            planId,
            detail,
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
            focus = !inspectFocus ? null : _session.Current is { } current ? _hook.GetFocus(current.Target).ToString() : "Other",
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
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    public void Dispose()
    {
        _pendingPracticeCompletion = null;
        RetireAutomaticPractice(false);
        if (PracticeProgress is { } practice)
            EndOnDemandPractice(practice.RunId);
        _disposed = true;
        _pendingWakeCapture = null;
        InvalidatePauseAnchor();
        _overlay.HideQuietly();
        _loop.Stop();
        _loop.Tick -= Tick;
        _pendingDirect = null;
        _hook.ConfigureShortcuts(false, _settings.Value.WakeShortcut, _settings.Value.DirectShortcut, CurrentSessionBindings, false);
        _hook.Dispose();
        _broker.Dispose();
        _settings.Changed -= Configure;
    }
}
