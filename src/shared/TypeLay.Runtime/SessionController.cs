using TypeLay.Core;
namespace TypeLay.Runtime;

public sealed partial class SessionController : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ITranslationService _model;
    private readonly TranslationReleasePolicy _translationRelease;
    private readonly ISessionView _overlay;
    private readonly IInputMonitor _hook;
    private readonly IInputService _broker;
    private readonly Session _session;
    private readonly TranslationActivation _activation;
    // Only identifies an explicit wake awaiting its first source observation; no text or permission.
    private sealed record PendingWakeCapture(TargetId Target, long Generation, long FeedbackRevision,
        Guid CommandGeneration, long CommandSequence, WakeCaptureIntent? Intent = null, long ConfigurationRevision = 0);
    private PendingWakeCapture? _pendingWakeCapture;
    private OnDemandPracticeProgress? _practiceWakeCommand, _pendingDirectPractice;
    private Guid _lastRetiredPracticeRun;
    private long _practiceBoundAt, _practicePreviewAt;
    private DirectTranslation? _pendingDirect;
    private bool _directRunning, _directWriteSubmitted;
    private long _lastCommandSequence, _commandSequence, _commandFeedbackRevision, _pendingDirectFeedbackRevision, _pendingDirectSequence;
    private Guid _lastCommandGeneration, _pendingDirectCommandGeneration;
    private long _configurationRevision;
    private sealed record StyleObservation(TargetId Target, string? Application, long SessionRevision,
        Guid CommandGeneration, long SourceEpoch, long ContentEpoch, long ForegroundEpoch, long WindowEpoch);
    private StyleObservation? _styleObservation;
    private bool _styleChangeRunning;
    private SessionShortcutAcknowledgement? _sessionShortcuts;
    // Provenance of the latest source observation, without retaining another text snapshot.
    private (long Revision, long SourceEpoch, long ContentEpoch, long ForegroundEpoch, long WindowEpoch) _alternativeObservation;
    // Provenance only: the existing Session remains the sole owner of manual text.
    private sealed record ManualObservation(long UserEpoch, long SourceEpoch, long ContentEpoch,
        long ForegroundEpoch, long WindowEpoch, long ConfigurationRevision, Guid CommandGeneration,
        Guid ConnectionId, long UnavailableUntil = 0);
    private ManualObservation? _manualObservation;
    private readonly ISessionLoop _loop;
    private long _generation, _nextCapture, _permitUntil, _editTransitionUntil, _retryBrokerAt;
    private AcceptancePermit? _presentationPermit;
    private bool _renderedAcceptanceReady;
    private TranslationMenuState? _renderedTranslationMenu;
    private bool _capturing, _writing, _translationRunning, _startingBroker, _disposed;
    private SessionViewToken? _sourceReturn;
    private bool _returningToSource => _sourceReturn is not null;
    private bool _inputReadFailed;
    private PauseAnchor? _pauseAnchor;
    private long _pauseViewRevision, _pauseWindowEpoch, _pauseForegroundEpoch;
    private bool _observingPause, _resumingPause;
    private const string ResumeSaveFailureStatus = "恢复设置未能保存，TypeLay 仍处于暂停状态。请检查设置存储后重试。";
    private string? _statusOverride;
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private string? _lastTrace;
    private int _captureFailureTraceCount;
    private string _lastShortcutStatus;
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
    public void ObserveOnDemandPreviewReadiness(Guid runId, TargetId? target, Guid commandGeneration, bool ready)
    {
        if (_disposed || PracticeProgress is not { } progress || progress.RunId != runId
            || progress.Target != target || progress.CommandGeneration != commandGeneration
            || ready && (commandGeneration != _hook.CommandGeneration || target is null)
            || progress.PreviewReady == ready)
            return;
        PracticeProgress = progress with
        {
            PreviewReady = ready
        };
        PracticeProgressChanged?.Invoke(PracticeProgress);
    }
    public void ObserveOnDemandDirectReadiness(Guid runId, TargetId? target, Guid commandGeneration, bool ready)
    {
        if (_disposed || PracticeProgress is not { } progress || progress.RunId != runId
            || progress.Target != target || progress.CommandGeneration != commandGeneration
            || ready && (commandGeneration != _hook.CommandGeneration || target is null)
            || progress.DirectReady == ready)
            return;
        PracticeProgress = progress with
        {
            DirectReady = ready
        };
        PracticeProgressChanged?.Invoke(PracticeProgress);
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
            DirectReplacementCompleted = direct,
            PreviewReady = progress.PreviewReady,
            DirectReady = progress.DirectReady
        };
        PracticeProgressChanged?.Invoke(PracticeProgress);
    }
    public SessionController(SettingsStore settings, ITranslationService model, ISessionView overlay,
        IInputMonitor input, IInputService broker, ISessionLoop loop,
        Func<OwnedPracticeBinding, Task<bool>>? retireOwnedPractice = null,
        Func<OwnedPracticeBinding, Task<bool>>? confirmRetiredOwnedPractice = null,
        TranslationReleasePolicy? translationRelease = null)
    {
        _retireOwnedPractice = retireOwnedPractice;
        _confirmRetiredOwnedPractice = confirmRetiredOwnedPractice;
        _broker = broker;
        _settings = settings;
        _model = model;
        _translationRelease = translationRelease ?? TranslationReleasePolicy.Current;
        _overlay = overlay;
        _hook = input;
        _lastShortcutStatus = ShortcutStatus;
        _loop = loop;
        _session = new Session(settings.Value.Direction);
        _configuredGlossary = GlossaryForTranslation();
        _session.SetGlossary(_configuredGlossary, _loop.NowMs);
        _activation = new TranslationActivation(settings.Value.Mode);
        _loop.Tick += Tick;
        overlay.BeginEditing = BeginEdit;
        overlay.DraftChanged = ChangePracticeDraft;
        overlay.ReturnRequested = token => { if (ViewCommandIsCurrent(token, true)) _ = ReturnFromEditor(); };
        overlay.EscapeRequested = token => { if (ViewCommandIsCurrent(token, false)) _ = Escape(); };
        overlay.AlternativeRequested = token => RequestAlternative(token, fromView: true);
        overlay.StyleRequested = ChangeViewStyleAsync;
        overlay.BeginGlossaryRequested = BeginGlossaryEntry;
        overlay.AddGlossaryRequested = AddGlossaryEntryAsync;
        overlay.CloseGlossaryRequested = CloseGlossaryEntry;
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
        _styleObservation = null;
        _editorGlossaryHandoff = null;
        _pendingPracticeCompletion = null;
        RetireAutomaticPractice(false);
        if (PracticeProgress is { } practice)
            EndOnDemandPractice(practice.RunId);
        _pendingWakeCapture = null;
        InvalidatePauseAnchor();
        _sessionShortcuts = null;
        _nextCapture = 0;
        _configurationRevision++;
        _manualObservation = null;
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
        _configuredGlossary = GlossaryForTranslation();
        _session.SetGlossary(_configuredGlossary, _loop.NowMs);
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
        if (notice.WakeCapture is not null)
        {
            RequestWakeCapture(notice);
            return;
        }
        if (notice.Source is null)
        {
            RejectShortcut(notice.Kind, Denial.Unsupported);
            return;
        }
        if (notice.CommandCapture is { } capture)
        {
            Denial reason = notice.AcceptedAtMs == capture.TriggeredAtMs
                ? capture.Check(notice.Source, _loop.NowMs) : Denial.StaleObservation;
            if (reason != Denial.None)
            {
                RejectShortcut(notice.Kind, reason);
                return;
            }
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
        string shortcutStatus = ShortcutStatus;
        if (_lastShortcutStatus != shortcutStatus)
        {
            _lastShortcutStatus = shortcutStatus;
            Changed?.Invoke();
        }
        RefreshConnectionStatus();
        CheckAutomaticPracticeIdentity();
        if (!ReferenceEquals(_configuredGlossary, GlossaryForTranslation()))
            ApplyGlossarySettings();
        DrainInputNotices();
        if (_awaitingPractice is not null && (_automaticRetirementUnconfirmed || _automaticRetirement is { IsCompleted: false }))
            return;
        long now = _loop.NowMs;
        if (_manualObservation is { UnavailableUntil: > 0 } && !ManualObservationIsCurrent(now))
            StopSession("manual-observation-retired");
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
        if (_pendingWakeCapture is { Intent: not null } freshWake && !WakeCaptureCurrent(freshWake))
            FailWakeCapture(freshWake);
        else if (_pendingWakeCapture is { } wake && _hook.GetFocus(wake.Target) == FocusMember.Other)
            _pendingWakeCapture = null;
        // A platform-proven no-write return can briefly have no foreground
        // window. Preserve only its form/basis, not focus or input authority.
        // The platform keeps the original deadline and cancels on foreign
        // foreground events; equal epochs alone never provide this proof.
        if (SourceGlossaryReturnIsPending())
        {
            RevokeInput("glossary-return-pending");
            _permitUntil = 0;
            return;
        }
        if (_session.Current is { } current)
        {
            bool ordinaryReturn = _sourceReturn is { } returning && _ownedPractice is null
                && returning.Target == current.Target && returning.Revision == _generation
                && returning.CommandGeneration == _hook.CommandGeneration
                && _session.State == SessionState.ReturningToSource && _hook.Ready && _broker.Available;
            bool retain = ordinaryReturn ? _hook.CanRetainDraftDuringSourceReturn(current.Target)
                : _hook.GetFocus(current.Target) != FocusMember.Other;
            if (!retain && !PracticeReturnIsPending(current.Target))
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
        if (_glossaryEntry is { Entry.SourceOrigin: true } sourceForm)
        {
            RetireInvalidGlossaryEntry();
            if (ReferenceEquals(_glossaryEntry, sourceForm))
            {
                RevokeInput("glossary-entry");
                _permitUntil = 0;
                // Keep the original source basis, not an invented observation.
                // Platform form focus includes its existing liveness deadline.
                if (GlossaryAuxiliaryCurrent(sourceForm) || sourceForm.Returning
                    && _hook.GetFocus(sourceForm.Entry.Token.Target) == FocusMember.Source)
                    return;
                // Only an actual auxiliary owner may suspend normal source
                // observation. An abandoned opening is bounded by its original
                // observation, and external focus ends the form immediately.
                if (_hook.GetFocus(sourceForm.Entry.Token.Target) == FocusMember.Other
                    || !sourceForm.Returned && (sourceForm.Source is not { } basis
                        || now - basis.ObservedAtMs > Limits.ObservationMaxAgeMs))
                    CancelGlossaryEntry();
            }
        }
        // The editor owns focus and its saved source basis until the user returns.
        // Chromium may expose no source selection when another window has focus.
        // Revalidate the source on return instead of polling an unfocused TextPattern.
        if (_session.State == SessionState.EditingTranslation && _session.Current is { } editingSource && _hook.GetFocus(editingSource.Target) == FocusMember.Overlay)
        {
            RevokeInput("editing-translation");
            _permitUntil = 0;
            CheckEditorGlossaryHandoff();
            // Editor focus/composition arrives after BeginEdit's synchronous render.
            // Refresh only changed read-only qualifications, never a source lease.
            if (_renderedTranslationMenu != GetTranslationMenuState())
                Render();
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
        if (_pendingWakeCapture?.Intent is null && !_translationRunning && !_directRunning && _pendingDirect is null
            && !_model.Busy && _model.Ready && !_writing)
        {
            var job = _session.TakeTranslationJob(now);
            // A manual seed can retain a dormant style while newly saved rules
            // apply to its next addition. Freeze both choices for explicit rejection.
            if (job?.Glossary?.CouldApply(job.Direction) == true && job.Style is null && _settings.Value.Styles is { } styles)
                job = job with
                {
                    Style = styles.Resolve(_styleObservation?.Application)
                };
            if (job is not null)
                _ = Translate(job);
        }
        if (_permitUntil != 0 && now > _permitUntil)
        {
            _permitUntil = 0;
            Render();
        }
        else if (_renderedAcceptanceReady != CanPresentAcceptance(now)
            || _session.State == SessionState.EditingTranslation
                && _renderedTranslationMenu != GetTranslationMenuState())
            Render();
    }
    private SessionShortcutBindings CurrentSessionBindings =>
        SessionShortcutBindings.Restore(_settings.Value.AcceptShortcut, _settings.Value.ExitShortcut);
    private bool SessionBindingsReady(TargetId target, bool editor) => _settings.Value.Enabled
        && _manualObservation is not { UnavailableUntil: > 0 }
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
        long configurationRevision = _configurationRevision;
        Guid connectionId = _hook.ConnectionId;
        long windowEpoch = _hook.WindowEpoch;
        long contentEpoch = _hook.ContentEpoch;
        long sourceGlossaryReadRevision = _sourceGlossaryReadRevision;
        var request = Context();
        var freshWake = _pendingWakeCapture is { Intent: not null } intent ? intent : null;
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
        string capturePhase = "before-capture";
        try
        {
            if (freshWake is not null && !WakeCaptureCurrent(freshWake))
            {
                FailWakeCapture(freshWake);
                return;
            }
            capturePhase = "input-capture";
            var result = await _broker.CaptureAsync(request);
            capturePhase = "apply-observation";
            if (traceCapture)
            {
                resumedAtMs = _loop.NowMs;
                observedAtMs = result.Snapshot?.ObservedAtMs;
                resultFocus = result.Focus;
            }
            // A source read started before an auxiliary transfer cannot replace
            // its frozen basis or publish a new permit. Its explicit return uses
            // a separate, freshly checked read in RefreshGlossarySourceAsync.
            if (sourceGlossaryReadRevision != _sourceGlossaryReadRevision)
            {
                captureExit = "glossary-entry-changed";
                return;
            }
            if (_glossaryEntry is { Entry.SourceOrigin: true } sourceForm
                && (!sourceForm.Returned || sourceForm.Returning || GlossaryAuxiliaryCurrent(sourceForm)))
                return;
            // Input already queued before this read completed owns the earlier action.
            // Accepting it advances generation before an obsolete read can clear it.
            if (!_disposed)
                DrainInputNotices();
            // Only the exact old read displaced by this pending age fence may
            // bypass null retirement. It still passes identity/generation checks
            // below and never becomes a new source observation or authority.
            bool fencedPracticeRead = !_disposed
                && result is
                {
                    Snapshot: null, OwnedSource: null, ForegroundExpired: false, Focus: FocusMember.Other,
                    SessionShortcuts: null, FencedPracticeRead: { } fenced
                }
                && request.PracticeRead is { } fencedBinding && ReferenceEquals(fencedBinding, _awaitingPractice)
                && fenced == fencedBinding && _hook.PracticeForegroundSuspension == fencedBinding
                && fencedBinding.NativeFocusRevision > 0 && fencedBinding.ContextGeneration > 0
                && AutomaticPracticeProgress is { AwaitingFreshSource: true, EditingStarted: false } progress
                && progress.RunId == fencedBinding.RunId && progress.CommandGeneration == fencedBinding.CommandGeneration
                && configurationRevision == _configurationRevision && windowEpoch == _hook.WindowEpoch
                && request.ForegroundEpoch == _hook.ForegroundEpoch
                && fencedBinding.ConnectionId == _hook.ConnectionId && fencedBinding.CommandGeneration == _hook.CommandGeneration
                && _settings.Value.Enabled && _settings.Value.Mode == WorkMode.Automatic && _broker.Available
                && (_hook.Ready ? _hook.GetFocus(fencedBinding.Target) == FocusMember.Source : _hook.ForegroundExpired);
            // A native refusal remains unknown even if foreground age expired
            // while this awaiting read was in flight. Never relabel it as age.
            if (!_disposed && _awaitingPractice is { } awaiting && request.PracticeRead == awaiting with
            {
                CommandGeneration = _hook.CommandGeneration
            }
                && result.Snapshot is null && result.OwnedSource is null && !result.ForegroundExpired && !fencedPracticeRead)
                StopSession("practice-continuity-lost");
            CheckAutomaticPracticeIdentity();
            if (_disposed || generation != _generation)
            {
                captureExit = _disposed ? "disposed" : "generation-changed";
                return;
            }
            if (freshWake is not null)
            {
                if (!ReferenceEquals(freshWake, _pendingWakeCapture) || !WakeCaptureCurrent(freshWake)
                    || result.Snapshot is not { } freshSource || freshSource.Target != freshWake.Target
                    || result.Focus != FocusMember.Source || result.UserEpoch != freshWake.Intent!.UserEpoch
                    || result.ForegroundEpoch != freshWake.Intent.ForegroundEpoch
                    || freshSource.ObservedAtMs < freshWake.Intent.TriggeredAtMs || freshSource.ObservedAtMs > _loop.NowMs
                    || freshSource.Access != FieldAccess.Supported
                    || freshSource.Ime != ImeState.Clear
                        && !(freshSource.Ime == ImeState.Unknown && freshSource.PreviewText is not null))
                {
                    FailWakeCapture(freshWake);
                    captureExit = "wake-capture-invalid";
                    return;
                }
                // Only this newly completed, fully rebound read may activate preview.
                // The intent and its old shortcut lease never supply write authority.
                if (_session.State == SessionState.NoSession)
                    _session.LeaveFamily();
                _activation.Wake(freshSource.Target);
                _practiceWakeCommand = PracticeCommand(new("wake", AcceptedAtMs: freshWake.Intent!.TriggeredAtMs,
                    Source: freshSource, CommandGeneration: freshWake.CommandGeneration,
                    CommandSequence: freshWake.CommandSequence), false);
                _pendingWakeCapture = null;
                OperationStatus = null;
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
            // A read can start after BeginEdit, before the editor-focus notice
            // arrives. Its empty source result does not revoke the editor that
            // now owns this same session, just as Tick no longer polls that source.
            // This preserves no source/IME proof: changed identity, unavailable
            // input and explicit observations still take the ordinary refusal path.
            if (result is
                {
                    Snapshot: null, Focus: FocusMember.Other, ForegroundExpired: false,
                    SessionShortcuts: null, OwnedSource: null, FencedPracticeRead: null
                }
                && _session.State == SessionState.EditingTranslation && _session.Current is { } editingSource
                && request.BoundTarget == editingSource.Target
                && configurationRevision == _configurationRevision && connectionId == _hook.ConnectionId
                && request.SessionShortcuts?.Generation == _hook.CommandGeneration
                && request.ForegroundEpoch == _hook.ForegroundEpoch && contentEpoch == _hook.ContentEpoch
                && _hook.Ready && _broker.Available && _hook.GetFocus(editingSource.Target) == FocusMember.Overlay)
            {
                captureExit = "editor-owns-focus";
                RevokeInput("editing-translation");
                _permitUntil = 0;
                return;
            }
            if (result.Snapshot is not { } snapshot || result.Focus == FocusMember.Other)
            {
                captureExit = "capture-empty";
                var pendingWake = _pendingWakeCapture;
                if (result.Snapshot is null && !result.ForegroundExpired && SuspendManualObservation(now))
                    return;
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
            if (_manualObservation is { UnavailableUntil: > 0 })
            {
                // Only a new safe observation of the exact original source can
                // expose the retained draft. Unknown or changed evidence retires it.
                if (!ManualObservationIsCurrent(now) || result.ForegroundExpired || result.Focus != FocusMember.Source
                    || result.UserEpoch != _hook.UserEpoch || result.ForegroundEpoch != _hook.ForegroundEpoch
                    || !snapshot.IsUsable || _session.Current is not { } basis
                    || snapshot.ObservedAtMs < _manualObservation.UnavailableUntil - Limits.ObservationMaxAgeMs
                    || snapshot with
                    {
                        ObservedAtMs = basis.ObservedAtMs,
                        Bounds = basis.Bounds
                    } != basis)
                    StopSession("manual-observation-changed");
                _manualObservation = null;
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
            string? styleApplication = _settings.NormalizeStyleApplication(result.Application);
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
                    _hook.PublishShortcut(new(snapshot, contentEpoch, request.ForegroundEpoch, styleApplication));
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
            if (ManualAcceptanceModifier(result, request, now) is { } modifier)
            {
                RevokeInput("acceptance-modifier", modifier);
                _permitUntil = 0;
                if (request.UserEpoch == modifier.Before.UserEpoch)
                {
                    // A read already in flight before Ctrl is not its new basis.
                    // Keep only the unchanged candidate provenance until a fresh read.
                    _nextCapture = 0;
                    Render();
                    return;
                }
                if (_session.RebaseManualCandidate(_session.Current!, snapshot, now))
                    _generation++;
            }
            _session.Observe(snapshot, result.Focus, now);
            bool styled = _settings.Value.Styles is not null && (_model.StyledTranslationCapability is not null
                || GlossaryForTranslation()?.CouldApply(_settings.Value.Direction) == true);
            TranslationStyle? effectiveStyle = styled ? _settings.Value.Styles!.Resolve(styleApplication) : null;
            bool applicationChanged = styled && _styleObservation is { } priorStyle
                && priorStyle.Target == snapshot.Target && priorStyle.Application != styleApplication;
            if (_session.SetStyle(effectiveStyle, now, applicationChanged) && applicationChanged)
            {
                _generation++;
                RevokeInput("style-application-changed");
                _permitUntil = 0;
            }
            _styleObservation = new(snapshot.Target, styleApplication, _session.Revision, _hook.CommandGeneration,
                request.SourceUserEpoch, contentEpoch, request.ForegroundEpoch, windowEpoch);
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
            _manualObservation = inputUnchanged && _session.State == SessionState.EditedTranslationPending
                && result.Focus == FocusMember.Source
                ? new(request.UserEpoch, request.SourceUserEpoch, contentEpoch, request.ForegroundEpoch,
                    windowEpoch, configurationRevision, request.SessionShortcuts!.Generation, connectionId) : null;
            _alternativeObservation = inputUnchanged
                ? (_session.Revision, request.SourceUserEpoch, contentEpoch, request.ForegroundEpoch, windowEpoch)
                : default;
            var practiceBinding = AutomaticPracticeFor(snapshot.Target);
            if (freshWake is null && _glossaryEntry is null && inputUnchanged && result.Focus == FocusMember.Source && _model.Ready
                && !RetiredAutomaticTarget(snapshot.Target)
                && (_settings.Value.ExperimentalWriteback || practiceBinding is not null
                    || _sessionShortcuts is { SourceLocalAcceptanceVersion: 1 })
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
                practiceBinding?.RunId, practiceBinding?.ConnectionId), permit);
            if (result.Focus == FocusMember.Source)
            {
                _hook.PublishShortcut(new(snapshot, contentEpoch, request.ForegroundEpoch, styleApplication));
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
            if (sourceGlossaryReadRevision != _sourceGlossaryReadRevision)
            {
                captureExit = "glossary-entry-changed";
                return;
            }
            if (!_disposed)
                DrainInputNotices();
            if (_disposed || generation != _generation)
            {
                captureExit = _disposed ? "disposed" : "generation-changed";
                return;
            }
            // Keep the first 16 current capture failures per controller, before
            // retirement erases the session. No extra focus/system-port probes.
            if (!string.IsNullOrWhiteSpace(_tracePath) && _captureFailureTraceCount < 16)
            {
                _captureFailureTraceCount++;
                try
                {
                    Trace("capture-error", detail: new
                    {
                        phase = capturePhase,
                        errorCategory = ex switch
                        {
                            OperationCanceledException => "cancelled",
                            EndOfStreamException => "end-of-stream",
                            IOException => "io",
                            System.Text.Json.JsonException => "json",
                            ObjectDisposedException => "disposed",
                            InvalidOperationException => "invalid-operation",
                            _ => "other"
                        },
                        hResult = ex.HResult,
                        cancellationRequested = ex is OperationCanceledException cancelled && cancelled.CancellationToken.CanBeCanceled
                            ? (bool?)cancelled.CancellationToken.IsCancellationRequested : null
                    }, inspectFocus: false);
                }
                catch { }
            }
            var pendingWake = _pendingWakeCapture;
            if (freshWake is not null)
            {
                if (!FailWakeCapture(freshWake, report: false))
                    return;
            }
            else
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
        TranslationStyle? style = _settings.Value.Styles is { } preferences && (_model.StyledTranslationCapability is not null
            || GlossaryForTranslation()?.CouldApply(_settings.Value.Direction) == true)
            ? preferences.Resolve(_settings.NormalizeStyleApplication(notice.Application)) : null;
        var (operation, reason) = notice.CommandCapture is { } capture
            ? DirectTranslation.StartFromCommandCapture(source, _settings.Value.Direction, capture, _loop.NowMs, style, _configurationRevision, GlossaryForTranslation())
            : DirectTranslation.Start(source, _settings.Value.Direction, notice.AcceptedAtMs, style, _configurationRevision, GlossaryForTranslation());
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
            var response = await DispatchTranslationAsync(operation.Job);
            if (_disposed || configuration != _configurationRevision || !ReferenceEquals(_pendingDirect, operation))
                return;
            if (response.Status != "translated")
            {
                if (response.Code == "language-unavailable")
                    SetCommandStatus("尚未准备本次翻译所需方向，请到“准备翻译”补充模型；原文未作替换。", feedbackRevision, "language-unavailable");
                else if (response.Code.StartsWith("glossary-", StringComparison.Ordinal))
                    SetCommandStatus("术语规则未能应用，未执行直接替换。", feedbackRevision, response.Code);
                else if (response.Code.StartsWith("style-", StringComparison.Ordinal))
                    SetCommandStatus("翻译风格未能应用，未执行直接替换。", feedbackRevision, response.Code);
                else
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
            Trace("direct-write-start", plan.Id, inspectFocus: false);
            var result = await _broker.WriteDirectAsync(plan, _settings.Value.Excluded);
            if (_disposed)
                return;
            Trace("direct-write-result", plan.Id, new
            {
                status = result.Status.ToString(),
                reason = result.Reason.ToString()
            }, inspectFocus: false);
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
    // No platform entry is opened here. State and commands share qualification.
    public StyleSettingsState GetStyleSettingsState(StyleScope scope = StyleScope.Global,
        TranslationStyle? style = TranslationStyle.Natural)
    {
        string? application = _styleObservation is { } observation && observation.Target == _session.Current?.Target
            ? observation.Application : null;
        var token = CurrentTranslationToken();
        string? reason = QualifyStyleChange(scope, style, _settings.Revision, token, false, default);
        string? resetReason = QualifyStyleChange(StyleScope.Global, null, _settings.Revision, token, true, default);
        bool available = TechnicalTranslationAvailable(TranslationOperation.Style);
        return new(_settings.Revision, _settings.Value.Styles, StyleApplicationProblem() is null ? _settings.Value.Styles?.Resolve(application) : null,
            application, token, available, reason, reason is null, resetReason is null, resetReason,
            _translationRelease.IsReleased(TranslationOperation.Style));
    }

    private SessionViewToken? CurrentTranslationToken() => _session.Current is { } source
        ? new(source.Target, _hook.CommandGeneration, _generation) : null;

    private string? QualifyStyleChange(StyleScope scope, TranslationStyle? style, long expectedRevision,
        SessionViewToken? token, bool reset, CancellationToken cancellationToken, bool ownsChange = false,
        string? savedApplication = null)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
            return "style-canceled";
        if (_settings.Revision != expectedRevision || _settings.HasChanges(_settings.Value))
            return "style-stale";
        if (reset && _settings.Value.Styles is null)
            return "style-unchanged";
        if (savedApplication is not null && (_session.Current is not null
            || style is null && _settings.Value.Styles?.Applications?.ContainsKey(savedApplication) != true))
            return "style-application-unavailable";
        if (!reset && (!Enum.IsDefined(scope) || style is { } selected && !Enum.IsDefined(selected)
            || savedApplication is null && scope == StyleScope.Global && style is null))
            return "invalid-style";
        bool removal = reset || style is null && (savedApplication is not null || scope == StyleScope.CurrentApplication);
        if (!removal)
        {
            if (StyleApplicationProblem() is { } unavailable)
                return unavailable;
        }
        if (_session.ManualStyleLocked || _overlay.EditingComposition)
            return "manual-style-locked";
        if (!ownsChange && _styleChangeRunning || _glossaryChangeRunning || TranslationSettingsBusy()
            || _directRunning || _pendingDirect is not null || _returningToSource)
            return "style-busy";
        if (_session.Current is null)
            return (reset || scope == StyleScope.Global) && token is null ? null : "style-stale";
        if (!reset && scope == StyleScope.CurrentApplication && _styleObservation?.Application is null)
            return "style-application-unavailable";
        return token is not null && StyleSourceCurrent(token) ? null : "style-stale";
    }

    private string? StyleApplicationProblem() =>
        QualifyTranslationOperation(TranslationOperation.Style, _settings.Value.Direction)
            ?? (GlossaryForTranslation()?.CouldApply(_settings.Value.Direction) == true
                ? QualifyTranslationOperation(TranslationOperation.StyledGlossary, _settings.Value.Direction) : null);

    private bool TranslationSettingsBusy() => AutomaticPracticeProgress is not null || PracticeProgress is not null
        || _pendingPracticeCompletion is not null || _ownedPractice is not null || _awaitingPractice is not null
        || _awaitingPracticeReturn is not null || _writing || _directWriteSubmitted
        || _session.State is SessionState.Applying or SessionState.OutcomeUnknown;

    public Task<StyleChangeResult> ChangeStyleAsync(StyleScope scope, TranslationStyle? style,
        long expectedSettingsRevision, SessionViewToken? token = null, CancellationToken cancellationToken = default) =>
        SaveStyleAsync(scope, style, expectedSettingsRevision, token, false, cancellationToken);

    // The host must explicitly confirm clearing the global choice AND every app override.
    // Natural is an explicit style choice and must never call this recovery action.
    public Task<StyleChangeResult> ResetTranslationStyleAsync(long expectedSettingsRevision,
        SessionViewToken? token = null, CancellationToken cancellationToken = default) =>
        SaveStyleAsync(StyleScope.Global, null, expectedSettingsRevision, token, true, cancellationToken);

    // Settings can remove a named, already saved override after the source session
    // ended. This never identifies a current application or creates an override.
    public StyleOverrideRemovalState GetStyleOverrideRemovalState(string application)
    {
        string? reason = string.IsNullOrWhiteSpace(application) ? "style-application-unavailable"
            : QualifyStyleChange(StyleScope.Global, null, _settings.Revision, null, false, default,
                savedApplication: application);
        return new(_settings.Revision, reason is null, reason);
    }

    public Task<StyleChangeResult> RemoveStyleOverrideAsync(string application, long expectedSettingsRevision,
        CancellationToken cancellationToken = default) => string.IsNullOrWhiteSpace(application)
        ? Task.FromResult(new StyleChangeResult(false, "style-application-unavailable"))
        : SaveStyleAsync(StyleScope.Global, null, expectedSettingsRevision, null, false, cancellationToken, application);

    // A settings row names a user-selected application, not the focused source.
    // It can be changed only without an active source session, and never grants
    // input support, model quality, or permission to write to that application.
    public StyleSettingsState GetApplicationStyleSettingsState(string application,
        TranslationStyle? style = TranslationStyle.Natural)
    {
        string? normalized = _settings.NormalizeStyleApplication(application);
        string? reason = normalized is null ? "style-application-unavailable"
            : QualifyStyleChange(StyleScope.Global, style, _settings.Revision, null, false, default,
                savedApplication: normalized);
        return new(_settings.Revision, _settings.Value.Styles,
            StyleApplicationProblem() is null ? _settings.Value.Styles?.Resolve(normalized) : null,
            normalized, null, TechnicalTranslationAvailable(TranslationOperation.Style), reason,
            reason is null, IsReleased: _translationRelease.IsReleased(TranslationOperation.Style));
    }

    public Task<StyleChangeResult> ChangeApplicationStyleAsync(string application, TranslationStyle? style,
        long expectedSettingsRevision, CancellationToken cancellationToken = default) =>
        _settings.NormalizeStyleApplication(application) is { } normalized
            ? SaveStyleAsync(StyleScope.Global, style, expectedSettingsRevision, null, false, cancellationToken, normalized)
            : Task.FromResult(new StyleChangeResult(false, "style-application-unavailable"));

    private async Task<StyleChangeResult> SaveStyleAsync(StyleScope scope, TranslationStyle? style,
        long expectedSettingsRevision, SessionViewToken? token, bool reset, CancellationToken cancellationToken,
        string? savedApplication = null)
    {
        var capability = _model.StyledTranslationCapability;
        var combinedCapability = _model.StyledGlossaryTranslationCapability;
        var identity = _model.VerifiedIdentity;
        var observation = _styleObservation;
        long generation = _generation, configuration = _configurationRevision;
        bool removal = reset || style is null && (savedApplication is not null || scope == StyleScope.CurrentApplication);
        string? Check(bool ownsChange = false)
        {
            if (QualifyStyleChange(scope, style, expectedSettingsRevision, token, reset, cancellationToken, ownsChange, savedApplication) is { } reason)
                return reason;
            if (generation != _generation || configuration != _configurationRevision || observation != _styleObservation)
                return "style-stale";
            if (!removal && (!ReferenceEquals(capability, _model.StyledTranslationCapability)
                || !ReferenceEquals(combinedCapability, _model.StyledGlossaryTranslationCapability)
                || !ReferenceEquals(identity, _model.VerifiedIdentity)))
                return "style-unavailable";
            return null;
        }
        if (Check() is { } rejection)
            return new(reset && rejection == "style-unchanged", rejection);
        _styleChangeRunning = true;
        try
        {
            if (_automaticRetirement is not null || _automaticRetirementUnconfirmed)
            {
                if (!await RetireAutomaticPracticeAsync())
                    return new(false, "practice-retirement-unconfirmed");
                if (Check(true) is { } stale)
                    return new(false, stale);
            }
            TranslationStyles? next = null;
            if (!reset)
            {
                var current = _settings.Value.Styles ?? new TranslationStyles();
                var applications = new Dictionary<string, TranslationStyle>(current.Applications
                    ?? new Dictionary<string, TranslationStyle>(), StringComparer.OrdinalIgnoreCase);
                if (savedApplication is not null)
                {
                    if (style is { } selected)
                        applications[savedApplication] = selected;
                    else
                        applications.Remove(savedApplication);
                }
                else if (scope == StyleScope.CurrentApplication)
                {
                    string application = observation!.Application!;
                    if (style is { } value)
                        applications[application] = value;
                    else
                        applications.Remove(application);
                }
                if (applications.Count > 256)
                    return new(false, "style-settings-full");
                next = _settings.Value.Styles is null && scope == StyleScope.CurrentApplication && style is null
                    ? null : new(scope == StyleScope.Global && savedApplication is null ? style!.Value : current.Default, applications);
            }
            long before = _settings.Revision;
            try
            {
                _settings.CommitStyles(expectedSettingsRevision, next, ApplyStyleSettings);
            }
            catch (SettingsPersistenceException) { return new(false, "style-save-failed"); }
            catch (Exception error) when (error is not OutOfMemoryException && _settings.Revision != before)
            {
                return new(true, "style-saved-notification-failed");
            }
            return new(true, "style-saved");
        }
        finally { _styleChangeRunning = false; }
    }

    private bool TechnicalTranslationAvailable(TranslationOperation operation) =>
        TechnicalTranslationAvailable(operation, _model.VerifiedIdentity, _settings.Value.Direction);

    private bool TechnicalTranslationAvailable(TranslationOperation operation, VerifiedModelIdentity? identity,
        LanguageDirection direction) => _model.Ready
        && identity is not null && operation switch
        {
            TranslationOperation.Style => _model.StyledTranslationCapability?.Supports(direction) == true,
            TranslationOperation.Glossary => _model.GlossaryTranslationCapability?.Supports(direction) == true,
            TranslationOperation.StyledGlossary => _model.StyledGlossaryTranslationCapability?.Supports(direction) == true,
            TranslationOperation.Alternative => identity.AlternativeTranslationAvailable,
            _ => false
        };

    private string? QualifyTranslationOperation(TranslationOperation operation, LanguageDirection direction) =>
        QualifyTranslationOperation(operation, direction, out _);

    private string? QualifyTranslationOperation(TranslationOperation operation, LanguageDirection direction, out VerifiedModelIdentity? identity)
    {
        identity = _model.VerifiedIdentity;
        string name = operation == TranslationOperation.StyledGlossary ? "glossary-combination" : operation.ToString().ToLowerInvariant();
        if (!TechnicalTranslationAvailable(operation, identity, direction) || !ReferenceEquals(identity, _model.VerifiedIdentity))
            return name + "-unavailable";
        return _translationRelease.IsReleased(operation)
            && _translationRelease.Allows(operation, identity!, direction) ? null : name + "-not-released";
    }

    private string? QualifyTranslationJob(TranslationJob job, out VerifiedModelIdentity? identity)
    {
        identity = null;
        bool glossary = job.Glossary?.CouldApply(job.Direction) == true;
        if (glossary && job.Kind == TranslationKind.Alternative)
            return "glossary-combination-unavailable";
        if (job.Kind == TranslationKind.Alternative)
            return job.Style is not null ? "alternative-unavailable" : QualifyTranslationOperation(TranslationOperation.Alternative, job.Direction, out identity);
        if (glossary && job.Style is not null)
            return QualifyTranslationOperation(TranslationOperation.StyledGlossary, job.Direction, out identity);
        return glossary ? QualifyTranslationOperation(TranslationOperation.Glossary, job.Direction, out identity)
            : job.Style is not null ? QualifyTranslationOperation(TranslationOperation.Style, job.Direction, out identity) : null;
    }

    private async Task<TypeLay.Protocol.ModelResponse> DispatchTranslationAsync(TranslationJob job)
    {
        if (QualifyTranslationJob(job, out var identity) is { } reason)
            return new(0, "error", Code: reason);
        bool special = job.Style is not null || job.Glossary?.CouldApply(job.Direction) == true || job.Kind == TranslationKind.Alternative;
        var result = special ? await _model.TranslateAsync(job, identity!) : await _model.TranslateAsync(job);
        return special && !ReferenceEquals(identity, _model.VerifiedIdentity) ? new(0, "error", Code: "not-ready") : result;
    }

    private bool StyleSourceCurrent(SessionViewToken token) => _styleObservation is { } observation
        && token.Revision == _generation && token.CommandGeneration == _hook.CommandGeneration
        && observation.CommandGeneration == _hook.CommandGeneration && observation.Target == token.Target
        && _session.Current is { IsUsable: true } source && source.Target == token.Target
        && observation.SessionRevision == _session.Revision
        && observation.SourceEpoch == _hook.SourceUserEpoch && observation.ContentEpoch == _hook.ContentEpoch
        && observation.ForegroundEpoch == _hook.ForegroundEpoch && observation.WindowEpoch == _hook.WindowEpoch
        && _hook.GetFocus(token.Target) != FocusMember.Other
        && _loop.NowMs >= source.ObservedAtMs && _loop.NowMs - source.ObservedAtMs <= Limits.ObservationMaxAgeMs;

    private void ApplyStyleSettings()
    {
        _configurationRevision++;
        _commandFeedbackRevision++;
        _generation++;
        _permitUntil = 0;
        _presentationPermit = null;
        _statusOverride = null;
        TranslationStyle? style = _settings.Value.Styles?.Resolve(_styleObservation?.Application);
        try
        {
            RevokeInput("style-settings");
        }
        finally
        {
            // Even a failing host revoke callback cannot retain a Core candidate
            // from the previous persisted configuration.
            _session.SetStyle(style, _loop.NowMs, configurationChanged: true);
            if (_styleObservation is { } observation)
                _styleObservation = observation with
                {
                    SessionRevision = _session.Revision
                };
            _nextCapture = 0;
        }
        Render();
        Changed?.Invoke();
    }

    public AlternativeState GetAlternativeState()
    {
        var token = CurrentTranslationToken();
        string? reason = QualifyAlternative(token);
        return new(token, reason is null, reason, _translationRelease.IsReleased(TranslationOperation.Alternative));
    }

    private string? QualifyAlternative(SessionViewToken? token)
    {
        if (QualifyTranslationOperation(TranslationOperation.Alternative, _settings.Value.Direction) is { } unavailable)
            return unavailable;
        if (!_session.CanBeginAlternative || _session.ManualStyleLocked)
            return "alternative-unavailable";
        if (token is null || !ViewCommandIsCurrent(token, true) || _overlay.EditingComposition
            || _alternativeObservation != (_session.Revision, _hook.SourceUserEpoch, _hook.ContentEpoch,
                _hook.ForegroundEpoch, _hook.WindowEpoch)
            || _session.Current is not { IsUsable: true } source
            || _hook.GetFocus(source.Target) == FocusMember.Other)
            return "alternative-source-unavailable";
        if (_model.Busy || _translationRunning || _directRunning || _pendingDirect is not null || _returningToSource
            || TranslationSettingsBusy() || _styleChangeRunning || _glossaryChangeRunning
            || _automaticRetirement is not null || _automaticRetirementUnconfirmed)
            return "alternative-busy";
        return null;
    }

    public bool RequestAlternative(SessionViewToken token) => RequestAlternative(token, fromView: false);

    private bool RequestAlternative(SessionViewToken token, bool fromView)
    {
        if (QualifyAlternative(token) is { } rejection)
        {
            if (fromView)
                SetTranslationMenuFeedback(token, _commandFeedbackRevision, TranslationMenuPresentation.Reason(rejection));
            return false;
        }
        var job = _session.BeginAlternative();
        if (job is null)
            return false;
        _generation++;
        _commandFeedbackRevision++;
        _alternativeMenuRequest = fromView ? (CurrentTranslationToken()!, _commandFeedbackRevision) : null;
        _translationMenuFeedback = null;
        RevokeInput("alternative");
        _permitUntil = 0;
        bool feedbackChanged = OperationStatus is not null;
        OperationStatus = null;
        Render();
        _ = Translate(job);
        if (feedbackChanged && OperationStatus is null)
            Changed?.Invoke();
        return true;
    }

    private async Task Translate(TranslationJob job)
    {
        _translationRunning = true;
        _statusOverride = null;
        Trace("translate-start");
        var practiceCommand = _practiceWakeCommand;
        var automaticPractice = AutomaticPracticeFor(job.Basis.Target);
        long generation = _generation, feedbackRevision = _commandFeedbackRevision;
        var commandGeneration = _hook.CommandGeneration;
        long sourceEpoch = _hook.SourceUserEpoch, contentEpoch = _hook.ContentEpoch,
            foregroundEpoch = _hook.ForegroundEpoch, windowEpoch = _hook.WindowEpoch;
        bool completed = false;
        try
        {
            var result = await DispatchTranslationAsync(job);
            if (_disposed)
                return;
            if (job.Kind == TranslationKind.Alternative)
            {
                if (generation != _generation || commandGeneration != _hook.CommandGeneration
                    || sourceEpoch != _hook.SourceUserEpoch || contentEpoch != _hook.ContentEpoch
                    || foregroundEpoch != _hook.ForegroundEpoch || windowEpoch != _hook.WindowEpoch
                    || result.Code == "not-ready" || !SessionBindingsReady(job.Basis.Target, true)
                    || _hook.GetFocus(job.Basis.Target) == FocusMember.Other)
                {
                    if (_session.AbandonAlternative(job))
                    {
                        RevokeInput("alternative-obsolete");
                        _permitUntil = 0;
                        _nextCapture = 0;
                        Render();
                    }
                    return;
                }
                if (result.Status == "translated")
                    _session.Complete(job, result.Text);
                else if (result.Status == "no-alternative" && _session.CompleteNoAlternative(job))
                    SetAlternativeMenuStatus("未找到不同说法，已保留原译文。", generation, commandGeneration, job.Basis.Target, feedbackRevision);
                else if (_session.Fail(job))
                    SetAlternativeMenuStatus("暂时无法换种说法，已保留原译文。", generation, commandGeneration, job.Basis.Target, feedbackRevision);
                Render();
                Trace("translate-done");
                return; // Alternative generation never earns tutorial progress.
            }
            if (result.Status == "translated")
                completed = _session.Complete(job, result.Text);
            else if (result.Status == "no-translation")
                _session.CompleteNoTranslation(job);
            else if (_session.Fail(job))
                _statusOverride = result.Code switch
                {
                    "segment-too-long" => "文本较长，请缩短选区",
                    "language-unavailable" => "尚未准备本次翻译所需方向，请到“准备翻译”补充模型",
                    "style-not-released" => "当前模型方向尚未开放翻译风格",
                    "glossary-not-released" => "当前模型方向尚未开放术语应用",
                    "style-unavailable" => "当前模型不支持翻译风格",
                    "glossary-unavailable" => "当前模型不支持应用术语表",
                    "glossary-combination-unavailable" => "暂不支持同时应用风格与术语表",
                    "glossary-combination-not-released" => "当前模型方向尚未开放风格与术语同时应用",
                    "glossary-protected-conflict" => "术语命中受保护内容或跨越分段，无法应用",
                    "glossary-output-missing" => "译文未包含指定译法，未提供可接受候选",
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
        _editorGlossaryHandoff = _styleObservation is { } observation
            && observation.Target == source.Target && observation.SourceEpoch == _hook.SourceUserEpoch
            && observation.CommandGeneration == _hook.CommandGeneration
                ? new(observation, source, new(source.Target, _hook.CommandGeneration, _generation),
                    _configurationRevision, _settings.Revision, _hook.ConnectionId, _hook.WindowEpoch, _editTransitionUntil)
                : null;
        RevokeInput("begin-edit");
        _permitUntil = 0;
        Render();
        return true;
    }
    private async Task<bool> ReturnFromEditor(PendingEditorExit? exit = null)
    {
        if (_returningToSource || _overlay.EditingComposition || !_session.RequestSourceReturn() || _session.Current is not { } source)
            return false;
        long generation = ++_generation;
        RevokeInput("return-from-editor");
        _permitUntil = 0;
        Render();
        var operation = new SessionViewToken(source.Target, _hook.CommandGeneration, generation);
        _sourceReturn = operation;
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
        finally
        {
            if (ReferenceEquals(_sourceReturn, operation))
                _sourceReturn = null;
        }
        if (_disposed || generation != _generation)
            return false;
        if (result.ContextLost)
        {
            StopSession("return-context-lost");
            return false;
        }
        if (result.Returned)
        {
            if (request is not null && !AwaitPracticeReturn(request, result.Practice))
            {
                StopSession("practice-return-unproven");
                return false;
            }
            if (exit is not null)
            {
                // Rotation/progress callbacks may have cancelled this attempt. Only
                // the original object can receive its one verified continuation.
                if (!ReferenceEquals(_pendingEditorExit, exit) || !EditorExitInputCurrent(exit)
                    || request is null || !ReferenceEquals(request.Binding, exit.Binding)
                    || request.Token != exit.ReturningToken || result.Practice is not { } returned
                    || !ReferenceEquals(_awaitingPracticeReturn, returned) || _generation != generation + 1
                    || _hook.CommandGeneration == request.Binding.CommandGeneration
                    || _session.State != SessionState.ReturningToSource
                    || AutomaticPracticeProgress is not { AwaitingFreshSource: true } progress
                    || progress.RunId != exit.Binding.RunId || progress.CommandGeneration != _hook.CommandGeneration
                    || _awaitingPracticeWindowEpoch != _hook.WindowEpoch
                    || _hook.GetFocus(exit.Binding.Target) != FocusMember.Source)
                    return false;
                exit.Confirmation = new(returned, _generation, _hook.CommandGeneration,
                    _hook.WindowEpoch, _hook.ForegroundEpoch);
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
            || !(_settings.Value.ExperimentalWriteback || AutomaticPracticeFor(permit.Plan.Expected.Target) is not null)
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
            if (_ownedPractice is { } binding
                && _overlay.IsEditorExitPrearmed(new(binding.Target, _hook.CommandGeneration, _generation)))
            {
                if (_pendingEditorExit is not null)
                    return;
                var exit = new PendingEditorExit(binding, new(binding.Target, _hook.CommandGeneration, _generation + 1),
                    _configurationRevision, _hook.UserEpoch, _hook.SourceUserEpoch);
                _pendingEditorExit = exit;
                if (!await ReturnFromEditor(exit) && ReferenceEquals(_pendingEditorExit, exit))
                    _pendingEditorExit = null;
                return; // BindAutomaticPractice consumes this exit after fresh Core confirmation.
            }
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
    // Model replacement invalidates unsent work without changing ordinary
    // StopSession's detached direct-write semantics. Submitted writes retain
    // their original receipt and feedback revision, and are never replayed.
    public async Task<bool> PrepareModelChangeAsync()
    {
        if (_disposed || !await RetireAutomaticPracticeAsync() || _disposed)
            return false;
        _pendingPracticeCompletion = null;
        if (PracticeProgress is { } practice)
            EndOnDemandPractice(practice.RunId);
        _configurationRevision++;
        _pendingWakeCapture = null;
        _practiceWakeCommand = null;
        _pendingDirectPractice = null;
        if (!_directWriteSubmitted)
        {
            _commandFeedbackRevision++;
            if (_pendingDirect is not null)
                SetServiceStatus("模型已改变，未执行本次直接替换。", "direct-canceled",
                    _pendingDirectCommandGeneration, _pendingDirectSequence);
            _pendingDirect = null;
        }
        StopSession("model-change");
        _hook.ConfigureShortcuts(_settings.Value.Enabled && _settings.Value.Mode == WorkMode.OnDemand,
            _settings.Value.WakeShortcut, _settings.Value.DirectShortcut, CurrentSessionBindings, _settings.Value.Enabled);
        return true;
    }

    private bool ManualObservationIsCurrent(long now) => _manualObservation is { } observed
        && _session.State == SessionState.EditedTranslationPending && _session.Current is { } source
        && (observed.UnavailableUntil == 0 || now < observed.UnavailableUntil)
        && observed.UserEpoch == _hook.UserEpoch && observed.SourceEpoch == _hook.SourceUserEpoch
        && observed.ContentEpoch == _hook.ContentEpoch && observed.ForegroundEpoch == _hook.ForegroundEpoch
        && observed.WindowEpoch == _hook.WindowEpoch && observed.ConfigurationRevision == _configurationRevision
        && observed.CommandGeneration == _hook.CommandGeneration && observed.ConnectionId == _hook.ConnectionId
        && _hook.Ready && _broker.Available && _hook.GetFocus(source.Target) == FocusMember.Source;

    private bool SuspendManualObservation(long now)
    {
        if (!ManualObservationIsCurrent(now))
            return false;
        if (_manualObservation!.UnavailableUntil == 0)
        {
            if (!_session.SuspendManualObservation())
                return false;
            _manualObservation = _manualObservation with
            {
                UnavailableUntil = now + Limits.ObservationMaxAgeMs
            };
            _generation++;
            _sessionShortcuts = null;
        }
        RevokeInput("manual-observation-unavailable");
        _permitUntil = 0;
        _overlay.HideQuietly();
        return true;
    }

    public void StopSession() => StopSession("stop-session");
    private void StopSession(string reason)
    {
        CancelGlossaryEntry();
        _manualObservation = null;
        _styleObservation = null;
        _editorGlossaryHandoff = null;
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
        RetireInvalidGlossaryEntry();
        if (_disposed)
            return;
        if (_manualObservation is { UnavailableUntil: > 0 })
        {
            _renderedAcceptanceReady = false;
            _overlay.HideQuietly();
            return;
        }
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
                SessionBindingsReady(source.Target, true), GetTranslationMenuState()) : null;
        _renderedTranslationMenu = context?.TranslationActions;
        _renderedAcceptanceReady = CanPresentAcceptance(_loop.NowMs);
        _overlay.Render(_session, _settings.Value, _renderedAcceptanceReady, status, context);
    }
    // Hosts call this only after their existing install/revoke boundary changes.
    // It repaints the current candidate; it cannot issue a plan or update a lease.
    public void RefreshAcceptancePresentation() => Render();
    private bool CanPresentAcceptance(long nowMs)
    {
        if (_manualObservation is { UnavailableUntil: > 0 }
            || _glossaryEntry is not null || _presentationPermit is not { } issued || _permitUntil == 0 || nowMs > _permitUntil
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
    private void RevokeInput(string reason, AcceptanceModifierTransition? modifier = null)
    {
        _presentationPermit = null;
        if (modifier is null)
            _hook.Revoke();
        else
            _hook.RevokeAcceptanceModifier(modifier);
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
        CancelGlossaryEntry();
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
