using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private readonly StackPanel _headerActions = new() { Orientation = Orientation.Horizontal };
    private readonly DockPanel _draftActions = new() { LastChildFill = true };
    private readonly Dictionary<string, DockPanel> _shortcutWarnings = [];
    private Button? _resetShortcuts;
    private string _editedShortcut = "wake";
    private ShortcutSettingsDraft? _shortcutBeforeAttempt, _visibleShortcutDraft;
    private bool _shortcutRecordedAttempt;
    private string? _shortcutBeforeError, _shortcutBeforeFeedback;
    private string _shortcutBeforeKey = "wake";
    private (long Revision, ShortcutSettingsDraft Draft, bool Busy)? _resetState;
    private object? _resetIdentity;
    private void Shortcuts()
    {
        object identity = _pageIdentity;
        bool Current() => IsVisible && _closeDialog is null && _page == "shortcuts" && ReferenceEquals(identity, _pageIdentity);
        void Changed(string key)
        {
            if (Current())
                ShortcutDraftChanged(key);
        }
        void AddShortcut(string key, string label, TextBox input)
        {
            if (input is ShortcutRecorder recorder)
            {
                recorder.AttachPage(Current);
                recorder.UnchangedRecording += () => Changed(key);
                recorder.RecordingStarted += () =>
                {
                    if (!Current())
                        return;
                    if (_shortcutConflictDraft is not null && _visibleShortcutDraft is { } visible)
                        _shortcutDraft = visible == ShortcutSettingsDraft.From(_settings.Value) ? null : visible;
                    ClearShortcutConfirmation();
                    if (_shortcutDraft is null)
                    {
                        _settingsError = null;
                        _draftFeedback = null;
                    }
                    RefreshDraftBar();
                };
            }
            AutomationProperties.SetAutomationId(input, "Shortcut_" + key);
            input.TextChanged += (_, _) => Changed(key);
            Row(label, "", input);
            RegisterSearchTarget(key, (Panel)_group!.Children[^1]);
            var warning = new DockPanel { LastChildFill = true, Margin = new(14, 0, 14, 12), Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(warning, "ShortcutWarning_" + key);
            AutomationProperties.SetLiveSetting(warning, AutomationLiveSetting.Assertive);
            _shortcutWarnings.Add(key, warning);
            _group!.Children.Add(warning);
        }
        var draft = _shortcutConflictDraft is not null && _visibleShortcutDraft is not null
            ? _visibleShortcutDraft : _shortcutDraft ?? ShortcutSettingsDraft.From(_settings.Value);
        _visibleShortcutDraft = draft;
        var messages = SettingsDraftPresentation.SessionShortcuts(SessionShortcutBindings.Restore(draft.Accept, draft.Exit));
        Heading(UiTheme.L("快捷键", "Shortcuts"));
        BeginGroup(UiTheme.L("翻译", "Translation"));
        var accept = _acceptShortcutInput = SessionShortcutInput(draft.Accept, "Tab", messages.AcceptLabel);
        var exit = _exitShortcutInput = SessionShortcutInput(draft.Exit, "Esc", messages.ExitLabel);
        accept.ToolTip = UiTheme.L("仅在译文与原文对应、焦点和输入法状态明确时尝试写回。", "Replaces text only when the candidate, focus and IME state are confirmed.");
        exit.ToolTip = UiTheme.L("不修改原文，不发送、不提交。输入法组合中优先交还输入法。", "Does not change or send text. Composition keys remain with the IME.");
        AddShortcut("accept", UiTheme.L("接受译文", "Accept translation"), accept);
        AddShortcut("exit", UiTheme.L("退出翻译", "Exit translation"), exit);
        _returnShortcutHint = UiTheme.Text("", 12, true);
        _returnShortcutHint.Margin = new(4, 0, 4, 14);
        BeginGroup(UiTheme.L("按需模式", "On demand"));
        var wake = _wakeShortcutInput = ShortcutInput(draft.Wake, "唤醒翻译 UI 快捷键");
        var direct = _directShortcutInput = ShortcutInput(draft.Direct, "直接翻译并替换快捷键");
        AddShortcut("wake", UiTheme.L("唤醒翻译", "Wake translation"), wake);
        AddShortcut("direct", UiTheme.L("直接翻译并替换", "Translate and replace"), direct);
        BeginGroup(UiTheme.L("全局", "Global"));
        var pause = _pauseShortcutInput = ShortcutInput(draft.Pause, "暂停或恢复 TypeLay 快捷键", allowEmpty: true);
        AddShortcut("pause", UiTheme.L("暂停或恢复", "Pause or resume"), pause);
        _shortcutStatus = UiTheme.Text("", 12, true);
        _shortcutStatus.Margin = new(4, 0, 4, 10);
        _content.Children.Add(_shortcutStatus);
        _pauseShortcutRetryButton = UiTheme.Button(UiTheme.L("重试全局暂停快捷键", "Retry Global Pause Shortcut"), () => { RetryPauseShortcut?.Invoke(); RefreshShortcutStatus(); });
        AutomationProperties.SetAutomationId(_pauseShortcutRetryButton, "RetryGlobalPauseShortcut");
        _pauseShortcutRetryButton.HorizontalAlignment = HorizontalAlignment.Left;
        _pauseShortcutRetryButton.Margin = new(0, 0, 0, 10);
        _content.Children.Add(_pauseShortcutRetryButton);
        _shortcutRetryButton = UiTheme.Button(UiTheme.L("重试启用快捷键", "Retry Shortcut Registration"), () =>
        {
            if (_shortcutFailure is { } failure)
                ShortcutRecovery?.Retry(failure.Generation);
            RefreshShortcutStatus();
        });
        AutomationProperties.SetAutomationId(_shortcutRetryButton, "RetrySavedShortcuts");
        _shortcutRetryButton.HorizontalAlignment = HorizontalAlignment.Left;
        _shortcutRetryButton.Margin = new(0, 0, 0, 14);
        _content.Children.Add(_shortcutRetryButton);
        var details = new StackPanel { Margin = new(0, 8, 0, 0) };
        _savedModeGuidance = UiTheme.Text("", 12, true);
        _savedAcceptanceGuidance = UiTheme.Text("", 12, true);
        _controlShortcutWarning = UiTheme.Text("", 12, true);
        AutomationProperties.SetAutomationId(_savedModeGuidance, "ShortcutSavedModeGuidance");
        AutomationProperties.SetAutomationId(_savedAcceptanceGuidance, "ShortcutSavedAcceptanceGuidance");
        AutomationProperties.SetAutomationId(_controlShortcutWarning, "ShortcutControlKeyRestriction");
        details.Children.Add(_returnShortcutHint);
        details.Children.Add(_savedModeGuidance);
        details.Children.Add(_savedAcceptanceGuidance);
        details.Children.Add(_controlShortcutWarning);
        details.Children.Add(UiTheme.Text(UiTheme.L("点击键帽后按下组合键，Esc 取消。接受与退出支持 Tab / Esc、功能键或 Ctrl+功能键；唤醒、直接替换与暂停支持 Ctrl / Alt / Shift 组合；这些全局键不支持 F12。", "Click a keycap and press a chord; Esc cancels. Accept and exit support Tab / Esc, function keys or Ctrl+function keys. Wake, direct replacement and pause support Ctrl / Alt / Shift combinations; F12 is reserved for these global shortcuts."), 12, true));
        details.Children.Add(UiTheme.Text(UiTheme.L("无法完整检测每个应用内部的快捷键。未发现冲突不代表没有冲突，实际启用状态以注册结果为准。直接替换仍绑定原输入框，不抢回焦点、不自动发送，输入法状态未知时拒绝。", "App-internal conflicts cannot be fully detected. Registration status is authoritative. Direct replacement stays bound to the original field, never takes focus or sends, and rejects unknown IME state."), 12, true));
        _content.Children.Add(new Expander { Header = UiTheme.L("快捷键与安全范围", "Shortcut and safety details"), Content = details, Margin = new(4, 0, 4, 0) });
        RefreshShortcuts();
    }
    private async void ShortcutDraftChanged(string key)
    {
        if (_updatingShortcuts || _wakeShortcutInput is null || _directShortcutInput is null
            || _acceptShortcutInput is null || _exitShortcutInput is null)
            return;
        ClearSaveIssue();
        var value = new ShortcutSettingsDraft(_wakeShortcutInput.Text, _directShortcutInput.Text,
            _acceptShortcutInput.Text, _exitShortcutInput.Text, _pauseShortcutInput?.Text ?? _settings.Value.PauseShortcut);
        _shortcutBeforeAttempt = _visibleShortcutDraft ?? _shortcutDraft ?? ShortcutSettingsDraft.From(_settings.Value);
        _shortcutBeforeError = _settingsError;
        _shortcutBeforeFeedback = _draftFeedback;
        _shortcutBeforeKey = _editedShortcut;
        _visibleShortcutDraft = value;
        _shortcutRecordedAttempt = ShortcutInputs().OfType<ShortcutRecorder>().Any(input => input.IsCommittingRecording);
        _editedShortcut = key;
        ClearShortcutConfirmation();
        _shortcutDraft = value == ShortcutSettingsDraft.From(_settings.Value) ? null : value;
        _draftFeedback = null;
        _settingsError = null;
        RefreshShortcuts();
        if (_shortcutDraft is not null)
            await SaveDraftAsync();
    }
    private void RefreshShortcuts()
    {
        if (_wakeShortcutInput is null || _directShortcutInput is null || _acceptShortcutInput is null
            || _exitShortcutInput is null || _shortcutStatus is null)
            return;
        if (_shortcutDraft is null)
        {
            _updatingShortcuts = true;
            try
            {
                UpdateShortcutText(_wakeShortcutInput, _settings.Value.WakeShortcut);
                UpdateShortcutText(_directShortcutInput, _settings.Value.DirectShortcut);
                if (_pauseShortcutInput is not null)
                    UpdateShortcutText(_pauseShortcutInput, _settings.Value.PauseShortcut);
                UpdateShortcutText(_acceptShortcutInput, _settings.Value.AcceptShortcut);
                UpdateShortcutText(_exitShortcutInput, _settings.Value.ExitShortcut);
            }
            finally { _updatingShortcuts = false; }
            _visibleShortcutDraft = ShortcutSettingsDraft.From(_settings.Value);
        }
        foreach (string key in new[] { "wake", "direct" })
            if (_searchTargets.TryGetValue(key, out var row) && row.Children.OfType<StackPanel>().FirstOrDefault() is { } labels)
                foreach (var label in labels.Children.OfType<TextBlock>())
                    label.SetResourceReference(TextBlock.ForegroundProperty, _settings.Value.Mode == WorkMode.OnDemand ? "Text" : "Tertiary");
        foreach (var recorder in ShortcutInputs().OfType<ShortcutRecorder>())
            recorder.RefreshPresentation();
        RefreshShortcutReset();
        var saved = _settings.Value;
        string accept = saved.AcceptShortcut;
        bool controlKeys = accept.StartsWith("Ctrl+", StringComparison.Ordinal) || saved.ExitShortcut.StartsWith("Ctrl+", StringComparison.Ordinal);
        if (_returnShortcutHint is not null)
            _returnShortcutHint.Text = controlKeys ? accept + UiTheme.L(" 返回原输入框", " returns to the original field") : accept + " → " + accept;
        var guidance = SettingsGuidancePresentation.Create(saved, exclusionsNeedSave: false);
        if (_savedModeGuidance is not null)
            _savedModeGuidance.Text = UiTheme.English
                ? (saved.Enabled ? "" : "TypeLay is paused. ") + (saved.Mode == WorkMode.OnDemand
                    ? $"On demand: {saved.WakeShortcut} starts translation in the current input field. After leaving that field, wake it again. {saved.DirectShortcut} translates the selection, or current field text if nothing is selected, and attempts safe replacement without preview or sending."
                    : $"Automatic: supported input fields show the language capsule and translate as you type. {saved.WakeShortcut} and {saved.DirectShortcut} are inactive in this mode.")
                : guidance.ModeDescription;
        if (_savedAcceptanceGuidance is not null)
            _savedAcceptanceGuidance.Text = UiTheme.English
                ? $"In the original input field, {saved.AcceptShortcut} attempts to accept a valid candidate after safety checks. After editing, the first {saved.AcceptShortcut} only returns to the original field; press it again to attempt acceptance. {saved.ExitShortcut} exits translation. Acceptance never sends or submits text."
                : guidance.AcceptanceDescription;
        if (_controlShortcutWarning is not null)
        {
            _controlShortcutWarning.Text = UiTheme.L("使用 Ctrl 会话键时，普通输入框仅支持退出和编辑回源；源内接受写回暂不可用。", "With Ctrl session shortcuts, ordinary input fields support only exit and returning from editing; in-field acceptance is unavailable.");
            _controlShortcutWarning.Visibility = controlKeys ? Visibility.Visible : Visibility.Collapsed;
        }
        RefreshDraftBar();
        RefreshShortcutStatus();
    }
    private void ClearShortcutConfirmation()
    {
        _shortcutConfirmation = null;
        _shortcutConflictDraft = null;
        _shortcutConflictBaseline = null;
        _shortcutConflictMessage = null;
    }
    private async Task SaveDraftAsync(bool forceShortcutSave = false, ShortcutSettingsDraft? confirmedDraft = null, ShortcutSettingsDraft? confirmedBaseline = null, object? confirmation = null, Func<bool>? callerCurrent = null)
    {
        if (_savingSettings || _savingTranslation || !HasDraft || callerCurrent?.Invoke() == false || _saveIssue?.Unknown == true)
            return;
        if (forceShortcutSave && (confirmation is null || !ReferenceEquals(confirmation, _shortcutConfirmation)))
            return;
        if (!HasOrdinaryDraft)
        {
            await SaveGlossaryDraftAsync();
            return;
        }
        var shortcuts = _shortcutDraft ?? ShortcutSettingsDraft.From(_settings.Value);
        if (!shortcuts.TryApply(_settings.Value, out var latest, out var error))
        {
            _settingsError = SettingsDraftPresentation.ShortcutErrorMessage(error);
            _draftFeedback = _draftMessages.InvalidMessage;
            if (_shortcutRecordedAttempt && _shortcutBeforeAttempt is { } before)
            {
                _shortcutDraft = before == ShortcutSettingsDraft.From(_settings.Value) ? null : before;
                SetShortcutInputs(before);
                ShortcutInputs().OfType<ShortcutRecorder>().FirstOrDefault(input => AutomationProperties.GetAutomationId(input) == "Shortcut_" + _editedShortcut)?.ShowInvalidRecording();
            }
            RefreshDraftBar();
            return;
        }
        var canonical = ShortcutSettingsDraft.From(latest!);
        var baseline = ShortcutSettingsDraft.From(_settings.Value);
        if (canonical != baseline)
        {
            bool confirmed = forceShortcutSave && confirmedDraft == canonical && confirmedBaseline == baseline
                && _shortcutConflictDraft == canonical && _shortcutConflictBaseline == baseline;
            if (!confirmed)
            {
                IReadOnlyList<string> unavailable;
                try
                {
                    unavailable = ShortcutRecovery?.InspectConflicts?.Invoke(canonical)
                        ?? ShortcutConflictProbe.Inspect(canonical);
                    unavailable = unavailable.Concat(PauseShortcutConflicts?.Invoke(canonical.Pause) ?? []).Distinct().ToArray();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    _settingsError = "无法完成快捷键注册检查，设置未保存。请稍后重试。";
                    RefreshDraftBar();
                    return;
                }
                var warning = SettingsDraftPresentation.ShortcutConflicts(canonical, unavailable);
                if (warning.RequiresConfirmation)
                {
                    _shortcutConflictDraft = canonical;
                    _shortcutConflictBaseline = baseline;
                    _shortcutConflictMessage = warning.Message;
                    // Replace the actual action peer for each review. A queued
                    // invocation of an older peer cannot authorize a new chord.
                    var reviewDraft = canonical;
                    var reviewBaseline = baseline;
                    var reviewIdentity = _shortcutConfirmation = new object();
                    var previous = _shortcutBeforeAttempt ?? baseline;
                    string edited = _editedShortcut;
                    string? previousError = _shortcutBeforeError, previousFeedback = _shortcutBeforeFeedback;
                    string previousKey = _shortcutBeforeKey;
                    SetShortcutInputs(RestoreShortcutField(shortcuts, previous, edited));
                    var oldButton = _forceShortcutButton;
                    _forceShortcutButton = UiTheme.Button(UiTheme.L("仍然使用", "Use Anyway"), async () => await SaveDraftAsync(true, reviewDraft, reviewBaseline, reviewIdentity));
                    AutomationProperties.SetAutomationId(_forceShortcutButton, "ForceSaveShortcuts");
                    DockPanel.SetDock(_forceShortcutButton, Dock.Right);
                    if (oldButton.Parent is Panel parent)
                    {
                        int index = parent.Children.IndexOf(oldButton);
                        parent.Children.RemoveAt(index);
                        parent.Children.Insert(index, _forceShortcutButton);
                    }
                    var oldEdit = _editShortcutButton;
                    _editShortcutButton = UiTheme.Button(UiTheme.L("取消", "Cancel"), () =>
                    {
                        if (!IsVisible || _closeDialog is not null || !ReferenceEquals(reviewIdentity, _shortcutConfirmation)
                            || _shortcutDraft is null || !_shortcutDraft.TryApply(_settings.Value, out var pending, out _)
                            || ShortcutSettingsDraft.From(pending!) != reviewDraft
                            || ShortcutSettingsDraft.From(_settings.Value) != reviewBaseline)
                            return;
                        var restored = RestoreShortcutField(_shortcutDraft, previous, edited);
                        ClearShortcutConfirmation();
                        _shortcutDraft = restored == ShortcutSettingsDraft.From(_settings.Value) ? null : restored;
                        _settingsError = previousError;
                        _draftFeedback = previousFeedback;
                        _editedShortcut = previousKey;
                        SetShortcutInputs(restored);
                        RefreshShortcuts();
                    });
                    AutomationProperties.SetAutomationId(_editShortcutButton, "EditConflictingShortcuts");
                    DockPanel.SetDock(_editShortcutButton, Dock.Right);
                    if (oldEdit.Parent is Panel editParent)
                    {
                        int index = editParent.Children.IndexOf(oldEdit);
                        editParent.Children.RemoveAt(index);
                        editParent.Children.Insert(index, _editShortcutButton);
                    }
                    _draftFeedback = null;
                    _settingsError = null;
                    RefreshDraftBar();
                    return;
                }
            }
        }
        ClearShortcutConfirmation();
        if (_page == "shortcuts")
            SetShortcutInputs(shortcuts);
        long submission = ++_draftSubmissionGeneration;
        bool saved = await SaveAsync(latest!, submission, callerCurrent);
        if (submission != _draftSubmissionGeneration)
            return;
        if (saved)
        {
            ClearShortcutConfirmation();
            // A newer recording may have arrived while session retirement was
            // pending. Never replace it with the submitted chord after commit.
            if (_shortcutDraft == shortcuts)
                _shortcutDraft = null;
            if (_draftFeedback != "已保存，请重新打开设置确认当前状态。")
                _draftFeedback = null;
        }
        else if (_saveIssue?.Unknown != true)
            _draftFeedback = _draftMessages.InvalidMessage;
        Refresh();
        if (saved && _shortcutDraft is not null)
            await SaveDraftAsync();
    }
    private void RefreshDraftBar()
    {
        _forceShortcutButton.Visibility = _shortcutConflictDraft is not null ? Visibility.Visible : Visibility.Collapsed;
        _editShortcutButton.Visibility = _forceShortcutButton.Visibility;
        _saveDraftButton.Visibility = _shortcutConflictDraft is null && HasDraft && _settingsError is not null && _saveIssue?.Unknown != true ? Visibility.Visible : Visibility.Collapsed;
        _forceShortcutButton.IsEnabled = !_savingSettings && !_savingTranslation;
        _draftNotice.Text = _shortcutConflictMessage ?? (_settingsError is { } error
            ? _saveIssue?.Unknown == true ? error : (_draftFeedback == _draftMessages.InvalidMessage ? _draftMessages.InvalidMessage : "设置未保存") + "\n" + error
            : _draftFeedback ?? (HasDraft ? UiTheme.L("尚未保存，请完成有效内容。", "Not saved. Complete a valid value.") : ""));
        _draftNotice.Text = UiStrings.Translate(_draftNotice.Text);
        _draftBar.Visibility = HasDraft || _draftFeedback is not null || _settingsError is not null
            ? Visibility.Visible : Visibility.Collapsed;
        _saveDraftButton.IsEnabled = HasDraft && !_savingSettings && !_savingTranslation;
        RefreshShortcutWarning();
        RefreshShortcutReset();
        RefreshSaveBanner();
    }
    private IEnumerable<TextBox> ShortcutInputs() => new[] { _acceptShortcutInput, _exitShortcutInput, _wakeShortcutInput, _directShortcutInput, _pauseShortcutInput }.OfType<TextBox>();
    private void SetShortcutInputs(ShortcutSettingsDraft value)
    {
        _visibleShortcutDraft = value;
        _updatingShortcuts = true;
        try
        {
            if (_wakeShortcutInput is not null)
                UpdateShortcutText(_wakeShortcutInput, value.Wake);
            if (_directShortcutInput is not null)
                UpdateShortcutText(_directShortcutInput, value.Direct);
            if (_acceptShortcutInput is not null)
                UpdateShortcutText(_acceptShortcutInput, value.Accept);
            if (_exitShortcutInput is not null)
                UpdateShortcutText(_exitShortcutInput, value.Exit);
            if (_pauseShortcutInput is not null)
                UpdateShortcutText(_pauseShortcutInput, value.Pause);
        }
        finally { _updatingShortcuts = false; }
    }
    private static ShortcutSettingsDraft RestoreShortcutField(ShortcutSettingsDraft current, ShortcutSettingsDraft previous, string key) => key switch
    {
        "accept" => current with { Accept = previous.Accept },
        "exit" => current with { Exit = previous.Exit },
        "direct" => current with { Direct = previous.Direct },
        "pause" => current with { Pause = previous.Pause },
        "all" => previous,
        _ => current with { Wake = previous.Wake }
    };
    private void RefreshShortcutReset()
    {
        if (_page != "shortcuts" || _renderedPage != _page)
            return;
        var current = _shortcutDraft ?? ShortcutSettingsDraft.From(_settings.Value);
        var state = (_settings.Revision, current, _savingSettings || _savingTranslation);
        if (_resetState == state && _resetShortcuts?.Parent == _headerActions)
            return;
        _resetState = state;
        var identity = _resetIdentity = new object();
        var page = _pageIdentity;
        var defaults = ShortcutSettingsDraft.From(new UserSettings());
        _resetShortcuts = UiTheme.Button(UiTheme.L("恢复默认", "Restore Defaults"), async () =>
        {
            if (!IsVisible || _closeDialog is not null || _page != "shortcuts" || !ReferenceEquals(page, _pageIdentity)
                || !ReferenceEquals(identity, _resetIdentity) || state.Item1 != _settings.Revision
                || current != (_shortcutDraft ?? ShortcutSettingsDraft.From(_settings.Value)) || _savingSettings || _savingTranslation)
                return;
            foreach (var input in ShortcutInputs().OfType<ShortcutRecorder>())
                input.CancelRecording();
            _shortcutBeforeAttempt = current;
            _shortcutBeforeError = _settingsError;
            _shortcutBeforeFeedback = _draftFeedback;
            _shortcutBeforeKey = _editedShortcut;
            ClearSaveIssue();
            _editedShortcut = "all";
            _shortcutRecordedAttempt = false;
            ClearShortcutConfirmation();
            _shortcutDraft = defaults == ShortcutSettingsDraft.From(_settings.Value) ? null : defaults;
            _settingsError = null;
            _draftFeedback = null;
            SetShortcutInputs(defaults);
            RefreshShortcuts();
            if (_shortcutDraft is not null)
                await SaveDraftAsync();
        });
        _resetShortcuts.IsEnabled = current != defaults && !state.Item3;
        _resetShortcuts.Height = 28;
        _resetShortcuts.Padding = new(10, 0, 10, 0);
        AutomationProperties.SetAutomationId(_resetShortcuts, "ResetShortcuts");
        _headerActions.Children.Clear();
        _headerActions.Children.Add(_resetShortcuts);
    }
    private void RefreshShortcutWarning()
    {
        // Move the existing transaction buttons, rather than creating a second
        // save route. Each conflict still replaces its actual confirmation peers.
        foreach (var warning in _shortcutWarnings.Values)
        {
            warning.Children.Clear();
            warning.Visibility = Visibility.Collapsed;
        }
        bool inline = _page == "shortcuts" && _renderedPage == _page && (_shortcutDraft is not null || _settingsError is not null)
            && _shortcutWarnings.TryGetValue(_editedShortcut == "all" ? "wake" : _editedShortcut, out _);
        DockPanel destination = inline ? _shortcutWarnings[_editedShortcut == "all" ? "wake" : _editedShortcut] : _draftActions;
        int actionIndex = 0;
        foreach (var button in new[] { _saveDraftButton, _forceShortcutButton, _editShortcutButton })
        {
            if (button.Parent is Panel owner)
                owner.Children.Remove(button);
            if (inline)
            {
                button.Height = 28;
                button.VerticalAlignment = VerticalAlignment.Center;
                button.Padding = new(10, 0, 10, 0);
            }
            else
            {
                button.ClearValue(HeightProperty);
                button.ClearValue(VerticalAlignmentProperty);
                button.ClearValue(Control.PaddingProperty);
            }
            destination.Children.Insert(actionIndex++, button);
        }
        if (!inline)
            return;
        var icon = PrototypeGlyph("M12,4 L2.8,19.5 L21.2,19.5 Z M12,10 L12,14.5 M12,17 L12,17.01", "Warn", stroke: 1.6);
        icon.Margin = new(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);
        destination.Children.Add(icon);
        var text = UiTheme.Text(_draftNotice.Text, 12);
        text.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        text.TextWrapping = TextWrapping.Wrap;
        destination.Children.Add(text);
        destination.Visibility = Visibility.Visible;
        if (_glossaryDraft is null)
            _draftBar.Visibility = Visibility.Collapsed;
    }
    private static void UpdateShortcutText(TextBox input, string value)
    {
        if (input.Text == value)
            return;
        int start = Math.Min(input.SelectionStart, value.Length);
        int length = Math.Min(input.SelectionLength, value.Length - start);
        input.Text = value;
        input.Select(start, length);
    }
    private static ShortcutRecorder SessionShortcutInput(string value, string defaultKey, string label)
    {
        var input = ShortcutRecorder.Create(value, label);
        ShortcutRecorder.AttachSession(input, defaultKey);
        return input;
    }
    private static ShortcutRecorder ShortcutInput(string value, string label, bool allowEmpty = false) => ShortcutRecorder.Create(value, label, allowEmpty);
}
