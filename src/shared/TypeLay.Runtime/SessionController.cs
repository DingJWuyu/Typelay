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
    private bool _directRunning, _directWriteSubmitted;
    private long _lastCommandSequence, _commandSequence, _commandFeedbackRevision, _pendingDirectFeedbackRevision, _pendingDirectSequence;
    private Guid _lastCommandGeneration, _pendingDirectCommandGeneration;
    private long _configurationRevision;
    private readonly ISessionLoop _loop;
    private long _generation, _nextCapture, _permitUntil, _editTransitionUntil, _retryBrokerAt;
    private bool _capturing, _writing, _translationRunning, _startingBroker, _returningToSource, _disposed;
    private PauseAnchor? _pauseAnchor;
    private long _pauseViewRevision, _pauseWindowEpoch, _pauseForegroundEpoch;
    private bool _observingPause, _resumingPause;
    private const string ResumeSaveFailureStatus = "恢复设置未能保存，TypeLay 仍处于暂停状态。请检查设置存储后重试。";
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
    // Hosts discard queued feedback older than this revision, including when a
    // newer command succeeds and deliberately emits no failure notification.
    public long CommandFeedbackRevision => _commandFeedbackRevision;
    public string ServiceStatus { get; private set; } = "正在连接输入服务";
    public event Action? Changed;
    public event Action<CommandFeedback>? OperationFeedback;
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
        overlay.ExcludeRequested = () =>
        {
            if (_disposed || !_settings.Value.Enabled)
                return;
            if (LastApplication is { } app)
                _settings.Exclude(app);
            StopSession();
        };
        overlay.ResumeRequested = revision => _ = ResumePause(revision);
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
        InvalidatePauseAnchor();
        _nextCapture = 0;
        _configurationRevision++;
        _commandFeedbackRevision++;
        if (_settings.Value.Enabled && OperationStatus == ResumeSaveFailureStatus)
        {
            OperationStatus = null;
            if (ServiceStatus == ResumeSaveFailureStatus)
                ServiceStatus = !_broker.Available ? "输入服务连接中断，正在重试"
                    : _hook.Ready ? "输入服务已连接（开发版）" : "输入监听未能初始化";
        }
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
    private void DrainInputNotices()
    {
        while (_hook.TryRead(out var notice))
        {
            if (notice?.CompletedReceipt is not null && notice.Permit is not null)
                CompleteNativeWrite(notice);
            else if (notice?.Kind is "wake" or "direct")
                Shortcut(notice);
            else if (notice?.Kind == "escape")
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
            reason = reason.ToString()
        });
    }
    private void Tick()
    {
        if (_disposed)
            return;
        DrainInputNotices();
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
        if (_session.Current is { } current)
        {
            if (_hook.GetFocus(current.Target) == FocusMember.Other)
                StopSession("focus-left");
        }
        if (!_hook.Ready || !_broker.Available)
        {
            _hook.Revoke();
            InvalidatePauseAnchor();
            _overlay.HideQuietly();
            return;
        }
        if (!_settings.Value.Enabled)
        {
            _hook.Revoke();
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
        try
        {
            var result = await _broker.CaptureAsync(request);
            // Input already queued before this read completed owns the earlier action.
            // Accepting it advances generation before an obsolete read can clear it.
            if (!_disposed)
                DrainInputNotices();
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
            if (!_disposed)
                DrainInputNotices();
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
        void Record(string reason, bool? generationMatches = null, bool? foregroundMatches = null, bool? focusMatches = null)
        {
            if (!string.IsNullOrWhiteSpace(_tracePath))
                Trace("wake-" + reason, detail: new
                {
                    ageMs = _loop.NowMs - notice.AcceptedAtMs,
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
            if (_session.State == SessionState.NoSession)
                _session.LeaveFamily();
            _generation++;
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
        _pendingDirectFeedbackRevision = _commandFeedbackRevision;
        _pendingDirectCommandGeneration = notice.CommandGeneration;
        _pendingDirectSequence = notice.CommandSequence;
        SetServiceStatus("正在直接翻译；可以切换输入框，当前操作仍绑定原输入框。");
    }
    private async Task TranslateDirect(DirectTranslation operation, long feedbackRevision)
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
        ServiceStatus = status;
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
            _hook.Revoke();
            _permitUntil = 0;
            _session.FinishWrite(plan, receipt);
            _nextCapture = 0;
            Render();
        }
        Trace("write-" + receipt.Status + "-" + receipt.Reason, plan.Id);
        if (receipt.Status != WriteStatus.Verified)
        {
            ServiceStatus = receipt.Status == WriteStatus.OutcomeUnknown ? "写回结果未确认，原目标已隔离" : "写回前安全校验拒绝了本次操作";
            Changed?.Invoke();
        }
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
        _hook.Revoke();
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
        InvalidatePauseAnchor();
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
        if (!_settings.Value.Enabled && PauseAnchorIsCurrent(_loop.NowMs))
        {
            _overlay.RenderPaused(_pauseAnchor!, _pauseViewRevision);
            return;
        }
        string? status = _session.State == SessionState.Translating && !_model.Ready ? "正在准备翻译"
            : _session.State == SessionState.TranslationFailed ? _statusOverride : null;
        _overlay.Render(_session, _settings.Value, _permitUntil >= _loop.NowMs && _permitUntil != 0, status);
    }
    // Opt-in local diagnostics contain counts and state only, never field text,
    // labels, window titles, exception messages, source or translated content.
    private void Trace(string operation, Guid? planId = null, object? detail = null)
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
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    public void Dispose()
    {
        _disposed = true;
        InvalidatePauseAnchor();
        _overlay.HideQuietly();
        _loop.Stop();
        _loop.Tick -= Tick;
        _pendingDirect = null;
        _hook.ConfigureShortcuts(false, _settings.Value.WakeShortcut, _settings.Value.DirectShortcut);
        _hook.Dispose();
        _broker.Dispose();
        _settings.Changed -= Configure;
    }
}
