using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.Linux;

internal static class Program
{
    private sealed record ModelActivationRequest(string Identity, string Generation);
    private sealed record SettingsDraftRequest(Guid Session, long Request, ShortcutSettingsDraft Shortcuts,
        ExclusionSettingsDraft? Exclusions = null, ShortcutSettingsDraft? ConfirmedShortcuts = null);
    private sealed record SettingsDraftCancellation(Guid Session, long Request);
    private sealed record TranslationSettingsRequest(Guid Session, long Request, long ExpectedSettingsRevision,
        TranslationStyle? Style = null, string? Application = null, GlossaryRule[]? Rules = null);
    private sealed record PlacementRequest(RectPx Input, RectPx Work, double Width, double Height, double Gap, double MinimumHeight);
    private sealed record ApplicationSupportRequest(ApplicationSupportFact[] Facts);
    private sealed record TranslationPreparationRequest(TranslationPreparationPhase Phase,
        string? FailureCode = null, bool HyRuntimeAvailable = false, string? HyUnavailableReason = null,
        bool IndexRuntimeAvailable = false, string? IndexUnavailableReason = null);
    private sealed record PracticeBindingRequest(Guid RunId, TargetId Target);
    private static bool TranslationRequestValid(JsonElement value, string? payload)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        string[] names = payload is null ? ["session", "request", "expectedSettingsRevision"]
            : ["session", "request", "expectedSettingsRevision", payload];
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != names.Length || properties.Select(item => item.Name).Distinct().Count() != names.Length
            || properties.Any(item => !names.Contains(item.Name))
            || !value.TryGetProperty("session", out var session) || session.ValueKind != JsonValueKind.String
            || !session.TryGetGuid(out var identity) || identity == Guid.Empty
            || !value.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Number || !request.TryGetInt64(out long sequence) || sequence <= 0
            || !value.TryGetProperty("expectedSettingsRevision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out long version) || version < 0)
            return false;
        return payload switch
        {
            "style" => value.GetProperty(payload).ValueKind == JsonValueKind.String
                && value.GetProperty(payload).GetString() is "Natural" or "Formal",
            "application" => value.GetProperty(payload).ValueKind == JsonValueKind.String,
            "rules" => value.GetProperty(payload).ValueKind == JsonValueKind.Array,
            _ => true
        };
    }
    private static object SessionGlossaryResponse(GlossaryChangeResult result) => new
    {
        result.Saved,
        result.Code,
        message = GlossaryEntryPresentation.Message(result.Code)
    };
    private static bool SessionGlossaryRequestValid(JsonElement value)
    {
        if (!ExactObject(value, "formId", "token", "settingsRevision", "rule")
            || value.GetProperty("formId").ValueKind != JsonValueKind.String
            || !value.GetProperty("formId").TryGetGuid(out var formId) || formId == Guid.Empty
            || !ValidSessionToken(value.GetProperty("token"))
            || value.GetProperty("settingsRevision").ValueKind != JsonValueKind.Number
            || !value.GetProperty("settingsRevision").TryGetInt64(out long revision) || revision < 0)
            return false;
        var rule = value.GetProperty("rule");
        return ExactObject(rule, "sourceTerm", "targetTerm", "sourceLanguage", "targetLanguage")
            && rule.GetProperty("sourceTerm").ValueKind == JsonValueKind.String
            && rule.GetProperty("targetTerm").ValueKind == JsonValueKind.String
            && rule.GetProperty("sourceLanguage").ValueKind is JsonValueKind.String or JsonValueKind.Null
            && rule.GetProperty("targetLanguage").ValueKind is JsonValueKind.String or JsonValueKind.Null;
    }
    private static bool ExactObject(JsonElement value, params string[] keys) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == keys.Length
        && value.EnumerateObject().Select(property => property.Name).Distinct().Count() == keys.Length
        && keys.All(key => value.TryGetProperty(key, out _));
    private static bool ValidSessionToken(JsonElement value)
    {
        if (!ExactObject(value, "target", "commandGeneration", "revision")
            || value.GetProperty("commandGeneration").ValueKind != JsonValueKind.String
            || !value.GetProperty("commandGeneration").TryGetGuid(out var generation) || generation == Guid.Empty
            || value.GetProperty("revision").ValueKind != JsonValueKind.Number
            || !value.GetProperty("revision").TryGetInt64(out long revision) || revision <= 0)
            return false;
        var target = value.GetProperty("target");
        return ExactObject(target, "backend", "value", "lifetime")
            && target.GetProperty("backend").ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(target.GetProperty("backend").GetString())
            && target.GetProperty("value").ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(target.GetProperty("value").GetString())
            && target.GetProperty("lifetime").ValueKind == JsonValueKind.String && target.GetProperty("lifetime").TryGetGuid(out var lifetime)
            && lifetime != Guid.Empty;
    }
    public static int Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.WriteLine("TypeLay Linux preview 0.2.0");
            return 0;
        }
        if (args is ["--setup-presentation", var failureCode])
        {
            Console.WriteLine(JsonSerializer.Serialize(SetupPresentation(failureCode), DesktopLink.Json));
            return 0;
        }
        if (OperatingSystem.IsLinux() && args is ["--activate-model", var activationHome, var activationData, var generation]
            && Path.IsPathFullyQualified(activationHome) && Path.IsPathFullyQualified(activationData)
            && LinuxModelInstallationStore.ValidId(generation))
            return ActivateModel(activationHome, activationData, generation).GetAwaiter().GetResult();
        if (OperatingSystem.IsLinux() && args is ["--editor-host", var editorHome, var editorConfig, var editorData]
            && args.Skip(1).All(Path.IsPathFullyQualified))
            return EditorHost.Run(editorHome, editorConfig, editorData).GetAwaiter().GetResult();
        if (!OperatingSystem.IsLinux() || args.Length != 3 || args.Any(path => !Path.IsPathFullyQualified(path)))
        {
            Console.Error.WriteLine("TypeLay: start the desktop using the typelay launcher.");
            return 64;
        }
        string home = args[0], config = args[1], data = args[2];
        using var loop = new EventLoop();
        SynchronizationContext.SetSynchronizationContext(loop);
        using var link = new DesktopLink(loop);
        using var input = new DesktopInput(link);
        var view = new DesktopView(link, input);
        var settings = new SettingsStore(Path.Combine(config, "settings.json"),
            enabled => SetStartup(enabled, home, config), app => Path.GetFileName(app.Trim()).ToLowerInvariant());
        using var model = LinuxModelFactory.Create(home, data);
        var installations = new LinuxModelInstallationStore(home, data);
        (string Identity, CancellationTokenSource Cancellation)? activation = null;
        (string Identity, CancellationTokenSource Cancellation)? editorSnapshot = null;
        long modelRevision = 0;
        using var controller = new SessionController(settings, model, view, input, input, loop,
            async binding => (await link.Ask("retireAutomaticPractice", binding, timeoutMs: 2000)).ValueKind == JsonValueKind.True);
        SettingsDraftRequest? pendingSettingsDraft = null;
        (TranslationSettingsRequest Request, CancellationTokenSource Cancellation)? translationChange = null;
        settings.StyleChanged += () => link.Send("translationSettingsChanged");
        settings.GlossarySaved += () => link.Send("translationSettingsChanged");

        object StyleSettings()
        {
            var state = controller.GetStyleSettingsState();
            return new
            {
                state.SettingsRevision,
                state.SavedPreferences,
                state.IsReleased,
                canChange = state.CanChange && state.Session is null,
                unavailableReason = state.Session is not null ? "style-stale" : state.UnavailableReason,
                canReset = state.CanReset && state.Session is null,
                resetUnavailableReason = state.Session is not null ? "style-stale" : state.ResetUnavailableReason,
                applications = (state.SavedPreferences?.Applications ?? new Dictionary<string, TranslationStyle>())
                    .Select(item => new
                    {
                        application = item.Key,
                        style = item.Value,
                        removal = controller.GetStyleOverrideRemovalState(item.Key)
                    }).ToArray()
            };
        }
        object GlossarySettings(JsonElement value)
        {
            var rules = value.ValueKind == JsonValueKind.Null ? null : DesktopLink.Decode<GlossaryRule[]>(value);
            var state = controller.GetGlossarySettingsState(rules);
            return new
            {
                state.SettingsRevision,
                state.SavedSnapshot,
                state.IsReleased,
                state.CanApply,
                state.ApplyUnavailableReason,
                directions = ModelCatalog.Packages.Packages.SelectMany(package => package.Directions)
                    .DistinctBy(direction => direction.Id).Select(direction => new
                    {
                        direction.Source,
                        direction.Target,
                        direction.SourceName,
                        direction.TargetName
                    }).ToArray(),
                canSave = state.CanSave && state.Session is null,
                unavailableReason = state.Session is not null ? "glossary-stale" : state.UnavailableReason
            };
        }
        async Task<object> SaveTranslationSettings(string operation, JsonElement value)
        {
            string? payload = operation switch
            {
                "changeStyle" => "style",
                "removeStyleOverride" => "application",
                "changeGlossary" => "rules",
                _ => null
            };
            if (!TranslationRequestValid(value, payload))
                return new
                {
                    saved = false,
                    code = "invalid-translation-settings"
                };
            var request = DesktopLink.Decode<TranslationSettingsRequest>(value);
            if (translationChange is not null)
                return new
                {
                    saved = false,
                    code = "translation-settings-busy"
                };
            using var cancellation = new CancellationTokenSource();
            translationChange = (request, cancellation);
            try
            {
                // Settings owns no source session. Never reuse the last overlay token
                // or application; shared qualification rejects a still-live source.
                return operation switch
                {
                    "changeStyle" => await controller.ChangeStyleAsync(StyleScope.Global, request.Style,
                        request.ExpectedSettingsRevision, cancellationToken: cancellation.Token),
                    "resetTranslationStyle" => await controller.ResetTranslationStyleAsync(
                        request.ExpectedSettingsRevision, cancellationToken: cancellation.Token),
                    "removeStyleOverride" => await controller.RemoveStyleOverrideAsync(request.Application!,
                        request.ExpectedSettingsRevision, cancellation.Token),
                    "changeGlossary" => await controller.ChangeGlossaryAsync(request.Rules!,
                        request.ExpectedSettingsRevision, cancellationToken: cancellation.Token),
                    _ => new { saved = false, code = "invalid-translation-settings" }
                };
            }
            finally
            {
                if (translationChange?.Cancellation == cancellation)
                    translationChange = null;
            }
        }
        bool hyRuntimeAvailable = false;
        bool indexRuntimeAvailable = false;
        string? indexUnavailableReason = "native-unavailable";
        string? hyUnavailableReason = "native-unavailable";
        ModelPackageManagementFact PackageFacts(bool busy = false)
        {
            var state = installations.ReadInstallationState();
            return new(state.Generation, state.InventoryKnown, state.InstalledPackages, state.CurrentPackages,
                model.AvailablePackages, hyRuntimeAvailable, hyUnavailableReason,
                settings.Value.Source, settings.Value.Target, busy || activation is not null,
                IndexRuntimeAvailable: indexRuntimeAvailable, IndexUnavailableReason: indexUnavailableReason);
        }
        void RefreshPackageChoices() => view.ConfigureModelPackages(PackageFacts(), settings.Value);

        async Task<bool> ApplySettings(UserSettings next)
        {
            var expected = settings.Value;
            long expectedRevision = settings.Revision;
            if (!await controller.PrepareSettingsChangeAsync(next) || !ReferenceEquals(expected, settings.Value))
                return false;
            settings.Update(expectedRevision, next);
            return true;
        }
        void Status() => link.Send("status", new
        {
            model = model.Status,
            modelReady = model.Ready,
            modelInferenceProfileId = model.VerifiedIdentity?.InferenceProfileId,
            modelInstalled = model.Installed,
            modelDirections = model.AvailableDirections,
            modelPackages = model.AvailablePackages.Select(item => new
            {
                id = item.Package.Id,
                version = item.Package.Version,
                directions = item.Directions.Select(direction => direction.Id).ToArray()
            }),
            modelFailureCode = model.FailureCode,
            modelCleanupPending = installations.CleanupPending,
            controller.ServiceStatus,
            controller.ConnectionState,
            controller.ConnectionStatus,
            controller.ShortcutStatus,
            controller.OperationStatus,
            controller.CommandFeedbackRevision,
            controller.LastApplication
        });
        controller.Changed += Status;
        controller.OperationFeedback += feedback => link.Send("operationFeedback", feedback);
        controller.AutomaticPracticeCompletionRetiring += (binding, revision) =>
            link.Send("automaticPracticeCompletionRetiring", new
            {
                binding,
                revision
            });
        model.Changed += () => { ++modelRevision; RefreshPackageChoices(); Status(); };
        link.Notification = (operation, value) =>
        {
            input.Receive(operation, value);
            if (operation == "state")
                view.RefreshGlossaryFocus();
            switch (operation)
            {
                case "sessionGlossaryFocus":
                    if (value.ValueKind == JsonValueKind.Null)
                        view.SetGlossaryFocus(null);
                    else if (ExactObject(value, "formId", "token", "ime")
                        && value.GetProperty("formId").ValueKind == JsonValueKind.String
                        && value.GetProperty("formId").TryGetGuid(out var focusForm) && focusForm != Guid.Empty
                        && ValidSessionToken(value.GetProperty("token"))
                        && value.GetProperty("ime").ValueKind == JsonValueKind.String
                        && value.GetProperty("ime").GetString() is "Clear" or "Composing" or "Unknown")
                        view.SetGlossaryFocus(DesktopLink.Decode<SessionGlossaryFocus>(value));
                    else
                        view.SetGlossaryFocus(null);
                    break;
                case "sessionGlossaryClose":
                    if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var closeForm) && closeForm != Guid.Empty)
                    {
                        if (view.GlossaryEntryFocus?.FormId == closeForm)
                            view.SetGlossaryFocus(null);
                        view.CloseGlossaryRequested?.Invoke(closeForm);
                    }
                    break;
                case "cancelEditorSnapshot":
                    if (value.ValueKind == JsonValueKind.String && editorSnapshot is { } editor
                        && editor.Identity == value.GetString())
                        editor.Cancellation.Cancel();
                    break;
                case "cancelModelActivation":
                    if (value.ValueKind == JsonValueKind.String && activation is { } cancelling
                        && cancelling.Identity == value.GetString())
                        cancelling.Cancellation.Cancel();
                    break;
                case "cancelTranslationSettings":
                    if (value.ValueKind == JsonValueKind.Object
                        && value.TryGetProperty("session", out var cancelSession) && cancelSession.ValueKind == JsonValueKind.String && cancelSession.TryGetGuid(out var cancelId)
                        && value.TryGetProperty("request", out var cancelRequest) && cancelRequest.ValueKind == JsonValueKind.Number && cancelRequest.TryGetInt64(out var cancelNumber)
                        && translationChange is { } change && change.Request.Session == cancelId
                        && change.Request.Request == cancelNumber)
                        change.Cancellation.Cancel();
                    break;
                case "cancelSettingsDraft":
                    var cancellation = DesktopLink.Decode<SettingsDraftCancellation>(value);
                    if (pendingSettingsDraft is { } pending && pending.Session == cancellation.Session
                        && pending.Request == cancellation.Request)
                        pendingSettingsDraft = null;
                    break;
                case "draft":
                    view.DraftChanged?.Invoke(value.GetString()!);
                    break;
                case "composition":
                    view.EditingComposition = value.GetBoolean();
                    break;
                case "return":
                    view.ReturnRequested?.Invoke(DesktopLink.Decode<SessionViewToken>(value));
                    break;
                case "escape":
                    view.EscapeRequested?.Invoke(DesktopLink.Decode<SessionViewToken>(value));
                    break;
                case "alternative":
                    view.AlternativeRequested?.Invoke(DesktopLink.Decode<SessionViewToken>(value));
                    break;
                case "regenerate":
                    view.RegenerateRequested?.Invoke();
                    break;
                case "exclude":
                    view.ExcludeRequested?.Invoke();
                    break;
                case "resume":
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long revision))
                        view.ResumeRequested?.Invoke(revision);
                    break;
                case "quit":
                    loop.Quit();
                    break;
            }
        };
        bool started = false;
        Task modelStart = Task.CompletedTask;
        link.Request = async (operation, value) =>
        {
            switch (operation)
            {
                case "start":
                    if (started)
                        return false;
                    started = true;
                    await controller.StartAsync();
                    modelStart = activation is null ? installations.StartModelAsync(model) : Task.CompletedTask;
                    Status();
                    return true;
                case "allowField":
                    return settings.Value.Enabled && FieldPolicy.Allows(DesktopLink.Decode<FieldMetadata>(value), settings.Value.Excluded);
                case "allowPauseField":
                    return !settings.Value.Enabled && FieldPolicy.Allows(DesktopLink.Decode<FieldMetadata>(value), settings.Value.Excluded);
                case "beginEdit":
                    bool allowed = view.BeginEditing?.Invoke(DesktopLink.Decode<SessionViewToken>(value)) ?? false;
                    return new
                    {
                        allowed,
                        token = allowed ? view.Token : null
                    };
                case "sessionGlossaryBegin":
                    return ValidSessionToken(value)
                        ? view.BeginGlossaryRequested?.Invoke(DesktopLink.Decode<SessionViewToken>(value)) : null;
                case "sessionGlossaryAdd":
                    if (!SessionGlossaryRequestValid(value))
                        return SessionGlossaryResponse(new(false, "invalid-session-glossary"));
                    return SessionGlossaryResponse(view.AddGlossaryRequested is { } addGlossary
                        ? await addGlossary(DesktopLink.Decode<SessionGlossaryRequest>(value))
                        : new GlossaryChangeResult(false, "glossary-unavailable"));
                case "sessionStyle":
                    if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3
                        || !value.TryGetProperty("token", out var styleToken) || styleToken.ValueKind != JsonValueKind.Object
                        || !value.TryGetProperty("settingsRevision", out var styleRevision)
                        || styleRevision.ValueKind != JsonValueKind.Number || !styleRevision.TryGetInt64(out long expectedRevision)
                        || expectedRevision < 0 || !value.TryGetProperty("style", out var styleValue)
                        || !(styleValue.ValueKind == JsonValueKind.Null || styleValue.ValueKind == JsonValueKind.String
                            && styleValue.GetString() is "Natural" or "Formal"))
                        return new
                        {
                            saved = false,
                            code = "invalid-session-style"
                        };
                    var styleRequest = DesktopLink.Decode<SessionStyleRequest>(value);
                    return view.StyleRequested is { } styleRequested
                        ? await styleRequested(styleRequest)
                        : new
                        {
                            saved = false,
                            code = "style-unavailable"
                        };
                case "getStyleSettings":
                    return StyleSettings();
                case "getApplicationStyleSettings":
                    return value.ValueKind == JsonValueKind.String
                        ? controller.GetApplicationStyleSettingsState(value.GetString()!) : null;
                case "changeApplicationStyle":
                    if (!ExactObject(value, "application", "style", "expectedSettingsRevision")
                        || value.GetProperty("application").ValueKind != JsonValueKind.String
                        || value.GetProperty("style").ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                        || value.GetProperty("style").ValueKind == JsonValueKind.String && value.GetProperty("style").GetString() is not ("Natural" or "Formal")
                        || value.GetProperty("expectedSettingsRevision").ValueKind != JsonValueKind.Number
                        || !value.GetProperty("expectedSettingsRevision").TryGetInt64(out long applicationRevision) || applicationRevision < 0)
                        return new StyleChangeResult(false, "invalid-style");
                    return await controller.ChangeApplicationStyleAsync(value.GetProperty("application").GetString()!,
                        value.GetProperty("style").ValueKind == JsonValueKind.Null ? null : Enum.Parse<TranslationStyle>(value.GetProperty("style").GetString()!), applicationRevision);
                case "getGlossarySettings":
                    return GlossarySettings(value);
                case "changeStyle":
                case "resetTranslationStyle":
                case "removeStyleOverride":
                case "changeGlossary":
                    return await SaveTranslationSettings(operation, value);
                case "settingsDraft":
                    return SettingsDraftPresentation.Create();
                case "sessionShortcutSettings":
                    return SettingsDraftPresentation.SessionShortcuts(
                        SessionShortcutBindings.Restore(settings.Value.AcceptShortcut, settings.Value.ExitShortcut));
                case "saveSettingsDraft":
                    var draft = DesktopLink.Decode<SettingsDraftRequest>(value);
                    if (draft.Session == Guid.Empty || draft.Request <= 0 || draft.Shortcuts is null)
                        return new
                        {
                            saved = false,
                            message = SettingsDraftPresentation.Create().InvalidMessage
                        };
                    var expectedSettings = settings.Value;
                    if (!draft.Shortcuts.TryApply(expectedSettings, out var updated, out var error))
                        return new
                        {
                            saved = false,
                            message = SettingsDraftPresentation.ShortcutErrorMessage(error)
                        };
                    if (draft.Exclusions is { } exclusions && !exclusions.TryApply(updated!, out updated))
                        return new
                        {
                            saved = false,
                            message = SettingsDraftPresentation.Create().InvalidMessage
                        };
                    var shortcutWarning = SettingsDraftPresentation.ShortcutConflicts(ShortcutSettingsDraft.From(updated!));
                    if (ShortcutSettingsDraft.From(updated!) != ShortcutSettingsDraft.From(expectedSettings)
                        && shortcutWarning.RequiresConfirmation && draft.ConfirmedShortcuts != draft.Shortcuts)
                        return new
                        {
                            saved = false,
                            confirmationRequired = true,
                            message = shortcutWarning.Message,
                            saveLabel = shortcutWarning.SaveLabel,
                            cancelLabel = shortcutWarning.CancelLabel
                        };
                    pendingSettingsDraft = draft;
                    try
                    {
                        if (!await controller.PrepareSettingsChangeAsync(updated!))
                            return new
                            {
                                saved = false,
                                message = "练习尚未确认结束，设置未保存。请重启 TypeLay 后重试。"
                            };
                        if (!ReferenceEquals(pendingSettingsDraft, draft))
                            return new
                            {
                                saved = false,
                                message = "本次保存已取消。"
                            };
                        if (!ReferenceEquals(expectedSettings, settings.Value))
                            return new
                            {
                                saved = false,
                                message = "设置已更改，请重新保存。"
                            };
                        // The owner loop cannot process a cancellation between
                        // this final identity check and the synchronous commit.
                        settings.Update(updated!);
                        return new
                        {
                            saved = true,
                            message = SettingsDraftPresentation.Create().SavedMessage
                        };
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        return new
                        {
                            saved = false,
                            message = SettingsDraftPresentation.Create().InvalidMessage
                        };
                    }
                    finally
                    {
                        if (ReferenceEquals(pendingSettingsDraft, draft))
                            pendingSettingsDraft = null;
                    }
                case "automaticPractice":
                    var automaticProgress = controller.AutomaticPracticeProgress;
                    return AutomaticPracticePresentation.Create(settings.Value,
                        value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var automaticPresentationRun)
                            && automaticProgress?.RunId == automaticPresentationRun ? automaticProgress : null);
                case "automaticPracticeProgress":
                    return controller.AutomaticPracticeProgress;
                case "startAutomaticPractice":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        || !model.Ready || !settings.Value.Enabled || settings.Value.Source == settings.Value.Target
                        || settings.Value.Mode != WorkMode.Automatic && !value.GetBoolean())
                        return false;
                    if (settings.Value.Mode != WorkMode.Automatic)
                        return await ApplySettings(settings.Value with
                        {
                            Mode = WorkMode.Automatic
                        });
                    return true;
                case "beginAutomaticPractice":
                    return value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var automaticRun)
                        && controller.BeginAutomaticPractice(automaticRun);
                case "bindAutomaticPractice":
                    // Only the inherited desktop pipe reaches this operation. The
                    // desktop has authenticated its managed process and exact field;
                    // control-socket callers cannot supply this binding themselves.
                    if (!ValidAutomaticBinding(value))
                        return false;
                    return controller.BindAutomaticPractice(DesktopLink.Decode<OwnedPracticeBinding>(value));
                case "endAutomaticPractice":
                    return value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var automaticEnd)
                        && await controller.EndAutomaticPracticeAsync(automaticEnd);
                case "finishAutomaticPractice":
                    if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var automaticFinish))
                        return new
                        {
                            saved = false,
                            retrySave = false
                        };
                    bool automaticSaved = false;
                    try
                    {
                        automaticSaved = await controller.FinishAutomaticPracticeAsync(automaticFinish);
                    }
                    catch (SettingsPersistenceException) { }
                    return new
                    {
                        saved = automaticSaved,
                        retrySave = controller.CanRetryAutomaticPracticeSave(automaticFinish)
                    };
                case "onDemandPractice":
                    var practiceProgress = controller.PracticeProgress;
                    return OnDemandPracticePresentation.Create(settings.Value,
                        value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var presentationRun)
                            && practiceProgress?.RunId == presentationRun ? practiceProgress : null);
                case "onDemandPracticeProgress":
                    return controller.PracticeProgress;
                case "dismissOnboarding":
                    if (value.ValueKind != JsonValueKind.Null)
                        return false;
                    // Legacy onboarding completion is only a prompt preference.
                    return await ApplySettings(settings.Value with
                    {
                        OnboardingComplete = true
                    });
                case "finishOnDemandPractice":
                    if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var finishRun)
                        || controller.PracticeProgress is not { Completed: true } finishedPractice
                        || finishedPractice.RunId != finishRun)
                        return false;
                    return await ApplySettings(settings.Value with
                    {
                        OnboardingComplete = true
                    });
                case "beginOnDemandPractice":
                    if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var beginRun)
                        || !controller.BeginOnDemandPractice(beginRun))
                        return false;
                    // Explicit restart must request a fresh preview even when
                    // the ordinary practice field still has the same text.
                    controller.StopSession();
                    return true;
                case "endOnDemandPractice":
                    return value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var endRun)
                        && controller.EndOnDemandPractice(endRun);
                case "bindOnDemandPractice":
                    if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2
                        || !value.TryGetProperty("runId", out var bindingRun)
                        || bindingRun.ValueKind != JsonValueKind.String || !bindingRun.TryGetGuid(out _)
                        || !value.TryGetProperty("target", out var bindingTarget)
                        || bindingTarget.ValueKind != JsonValueKind.Object
                        || bindingTarget.EnumerateObject().Count() != 3
                        || !bindingTarget.TryGetProperty("backend", out var backend)
                        || backend.ValueKind != JsonValueKind.String || backend.GetString() != "linux-gtk3"
                        || !bindingTarget.TryGetProperty("value", out var field)
                        || field.ValueKind != JsonValueKind.String || !field.TryGetGuid(out _)
                        || !bindingTarget.TryGetProperty("lifetime", out var lifetime)
                        || lifetime.ValueKind != JsonValueKind.String || !lifetime.TryGetGuid(out _))
                        return false;
                    var binding = DesktopLink.Decode<PracticeBindingRequest>(value);
                    return controller.BindOnDemandPractice(binding.RunId, binding.Target);
                case "startOnDemandPractice":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        || !model.Ready || !settings.Value.Enabled
                        || settings.Value.Source == settings.Value.Target
                        || (settings.Value.Mode != WorkMode.OnDemand && !value.GetBoolean()))
                        return false;
                    // Check and update on the settings owner's event loop. A GTK
                    // snapshot may lag an already queued tray or language change.
                    if (settings.Value.Mode != WorkMode.OnDemand)
                        return await ApplySettings(settings.Value with
                        {
                            Mode = WorkMode.OnDemand
                        });
                    return true;
                case "setupFailure":
                    return SetupPresentation(value.ValueKind == JsonValueKind.String ? value.GetString() : null);
                case "translationPreparation":
                    var preparation = DesktopLink.Decode<TranslationPreparationRequest>(value);
                    hyRuntimeAvailable = preparation.HyRuntimeAvailable && preparation.HyUnavailableReason is null;
                    hyUnavailableReason = hyRuntimeAvailable ? null : "native-unavailable";
                    indexRuntimeAvailable = preparation.IndexRuntimeAvailable && preparation.IndexUnavailableReason is null;
                    indexUnavailableReason = indexRuntimeAvailable ? null : "native-unavailable";
                    var effectivePhase = preparation.Phase == TranslationPreparationPhase.Idle && !modelStart.IsCompleted
                        ? TranslationPreparationPhase.StartingModel : preparation.Phase;
                    var packageFacts = PackageFacts(effectivePhase is TranslationPreparationPhase.Preparing
                        or TranslationPreparationPhase.Pausing or TranslationPreparationPhase.StartingModel);
                    view.ConfigureModelPackages(packageFacts, settings.Value);
                    var installedDirections = packageFacts.InventoryKnown
                        ? packageFacts.InstalledPackages.SelectMany(item => item.Directions).Select(item => item.Id).ToArray() : null;
                    return ApplicationSupportPresentation.Preparation(new(model.Ready, model.Installed,
                        effectivePhase,
                        preparation.FailureCode is null ? null
                            : SetupFailurePresentation.CategoryFromCode(preparation.FailureCode),
                        InstalledDirections: installedDirections, ReadyDirections: model.AvailableDirections,
                        InstallationIntentKnown: packageFacts.InventoryKnown, Packages: packageFacts),
                        TranslationPreparationNextStep.ChooseConnection);
                case "applicationSupport":
                    // The desktop owns detection and explicit installation actions.
                    // Reading this view never starts either of them.
                    return ApplicationSupportPresentation.Create(DesktopLink.Decode<ApplicationSupportRequest>(value).Facts);
                case "place":
                    var placement = DesktopLink.Decode<PlacementRequest>(value);
                    return OverlayPlacement.Place(placement.Input, placement.Work, placement.Width,
                        placement.Height, placement.Gap, placement.MinimumHeight);
                case "applicationEnabled":
                    object ApplicationPolicyResult(bool saved, string code) => new
                    {
                        saved,
                        code,
                        settingsRevision = saved ? (long?)settings.Revision : null
                    };
                    if (!ExactObject(value, "application", "enabled", "expectedSettingsRevision")
                        || value.GetProperty("application").ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(value.GetProperty("application").GetString())
                        || value.GetProperty("application").GetString()!.Length > 128
                        || value.GetProperty("application").GetString()!.Any(char.IsControl)
                        || value.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        || value.GetProperty("expectedSettingsRevision").ValueKind is not (JsonValueKind.Null or JsonValueKind.Number)
                        || value.GetProperty("expectedSettingsRevision").ValueKind == JsonValueKind.Number
                            && (!value.GetProperty("expectedSettingsRevision").TryGetInt64(out long policyRevision) || policyRevision < 0))
                        return ApplicationPolicyResult(false, "invalid-application-policy");
                    if (value.GetProperty("expectedSettingsRevision").ValueKind == JsonValueKind.Number
                        && value.GetProperty("expectedSettingsRevision").GetInt64() != settings.Revision)
                        return ApplicationPolicyResult(false, "settings-changed");
                    var policyApplication = value.GetProperty("application").GetString()!.Trim();
                    var remainingApplications = settings.Value.Excluded.Where(item => !item.Equals(policyApplication, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (!value.GetProperty("enabled").GetBoolean() && remainingApplications.Length >= 256)
                        return ApplicationPolicyResult(false, "application-capacity");
                    try
                    {
                        bool saved = await ApplySettings(settings.Value with
                        {
                            ExcludedApplications = value.GetProperty("enabled").GetBoolean()
                                ? remainingApplications : [.. remainingApplications, policyApplication]
                        });
                        return ApplicationPolicyResult(saved, saved ? "application-saved" : "settings-changed");
                    }
                    catch (SettingsPersistenceException)
                    {
                        return ApplicationPolicyResult(false, "application-save-failed");
                    }
                case "getApplicationList":
                    if (!ExactObject(value, "knownApplications")
                        || !ApplicationIdentities(value.GetProperty("knownApplications"), out var knownApplications))
                        return null;
                    return new
                    {
                        applications = settings.Value.VisibleApplications(knownApplications),
                        customized = settings.Value.ApplicationListCustomized,
                        settingsRevision = settings.Revision
                    };
                case "changeApplicationList":
                    object ApplicationListResult(bool saved, string code) => new
                    {
                        saved,
                        code,
                        settingsRevision = saved ? (long?)settings.Revision : null
                    };
                    if (!ExactObject(value, "application", "included", "expectedSettingsRevision", "knownApplications")
                        || value.GetProperty("application").ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(value.GetProperty("application").GetString())
                        || value.GetProperty("application").GetString()!.Length > 128
                        || value.GetProperty("application").GetString()!.Any(char.IsControl)
                        || value.GetProperty("included").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        || value.GetProperty("expectedSettingsRevision").ValueKind != JsonValueKind.Number
                        || !value.GetProperty("expectedSettingsRevision").TryGetInt64(out long membershipRevision)
                        || membershipRevision < 0
                        || !ApplicationIdentities(value.GetProperty("knownApplications"), out var membershipKnown))
                        return ApplicationListResult(false, "invalid-application-list");
                    if (membershipRevision != settings.Revision)
                        return ApplicationListResult(false, "settings-changed");
                    var member = Path.GetFileName(value.GetProperty("application").GetString()!.Trim()).ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(member))
                        return ApplicationListResult(false, "invalid-application-list");
                    var members = settings.Value.VisibleApplications(membershipKnown)
                        .Where(item => !item.Equals(member, StringComparison.OrdinalIgnoreCase));
                    if (value.GetProperty("included").GetBoolean())
                        members = members.Append(member);
                    var nextMembers = members.Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (nextMembers.Length > 256)
                        return ApplicationListResult(false, "application-capacity");
                    try
                    {
                        bool saved = await ApplySettings(settings.Value with
                        {
                            ManagedApplications = nextMembers,
                            ApplicationListCustomized = true
                        });
                        return ApplicationListResult(saved, saved ? "application-list-saved" : "settings-changed");
                    }
                    catch (SettingsPersistenceException)
                    {
                        return ApplicationListResult(false, "application-save-failed");
                    }
                    catch (SettingsConflictException)
                    {
                        return ApplicationListResult(false, "settings-changed");
                    }
                case "manageApplication":
                    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
                        || value.GetString()!.Length > 128 || value.GetString()!.Any(char.IsControl))
                        return false;
                    var managedApplication = value.GetString()!.Trim();
                    if (settings.Value.Managed.Any(item => item.Equals(managedApplication, StringComparison.OrdinalIgnoreCase)))
                        return true;
                    if (settings.Value.Managed.Length >= 256)
                        return false;
                    try
                    {
                        return await ApplySettings(settings.Value with
                        {
                            ManagedApplications = [.. settings.Value.Managed, managedApplication]
                        });
                    }
                    catch (SettingsPersistenceException)
                    {
                        return false;
                    }
                case "togglePause":
                    if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                        return false;
                    return await ApplySettings(settings.Value with
                    {
                        Enabled = !settings.Value.Enabled
                    });
                case "settingsPatch":
                case "traySettings":
                    if (operation == "traySettings" && (value.ValueKind != JsonValueKind.Object
                        || value.EnumerateObject().Any(property => property.Name is not ("enabled" or "mode" or "source" or "target"))))
                        return false;
                    if (!TryApplyTraySettings(value, settings.Value, out var traySettings))
                        return false;
                    try
                    {
                        return await ApplySettings(traySettings);
                    }
                    catch (SettingsPersistenceException)
                    {
                        return false;
                    }
                case "settings":
                    // The ordinary DTO cannot write or clear dedicated preferences,
                    // even when an old form was opened before a dedicated save.
                    if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(property =>
                        property.Name.Equals("styles", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("glossary", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("motion", StringComparison.OrdinalIgnoreCase)
                            && (property.Value.ValueKind != JsonValueKind.String
                                || property.Value.GetString() is not ("system" or "full" or "reduce"))))
                        return false;
                    var next = DesktopLink.Decode<UserSettings>(value) with
                    {
                        Styles = settings.Value.Styles,
                        Glossary = settings.Value.Glossary,
                        ManagedApplications = settings.Value.ManagedApplications,
                        ApplicationListCustomized = settings.Value.ApplicationListCustomized
                    };
                    if (next.Schema != 1 || next.Theme is not ("system" or "light" or "dark")
                        || next.Excluded.Length > 256 || next.Excluded.Any(app => string.IsNullOrWhiteSpace(app) || app.Length > 128))
                        return false;
                    try
                    {
                        return await ApplySettings(next);
                    }
                    catch (SettingsPersistenceException)
                    {
                        return false;
                    }
                case "translateEditorSnapshot":
                    if (editorSnapshot is not null || value.ValueKind != JsonValueKind.Object
                        || value.EnumerateObject().Count() != 2
                        || !value.TryGetProperty("identity", out var editorIdentity) || editorIdentity.ValueKind != JsonValueKind.String
                        || !LinuxModelInstallationStore.ValidId(editorIdentity.GetString())
                        || !value.TryGetProperty("request", out var editorValue))
                        return null;
                    var editorRequest = EditorHost.ReadRequest(editorValue);
                    if (editorRequest is null)
                        return EditorHost.Failure(EditorHost.Identity(editorValue), "invalid-request");
                    var editorSettings = settings.Value;
                    string? editorGeneration = installations.ReadInstallationState().Generation;
                    long editorRevision = modelRevision;
                    using (var editorCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(215)))
                    {
                        editorSnapshot = (editorIdentity.GetString()!, editorCancellation);
                        try
                        {
                            return await EditorHost.TranslateAsync(editorRequest, model, editorSettings,
                                () => ReferenceEquals(settings.Value, editorSettings) && modelRevision == editorRevision
                                    && installations.ReadInstallationState().Generation == editorGeneration,
                                editorCancellation.Token);
                        }
                        finally
                        {
                            if (editorSnapshot?.Cancellation == editorCancellation)
                                editorSnapshot = null;
                        }
                    }
                case "activateModel":
                    if (activation is not null || value.ValueKind != JsonValueKind.Object
                        || value.EnumerateObject().Count() != 2
                        || !value.TryGetProperty("identity", out var identity) || identity.ValueKind != JsonValueKind.String
                        || !LinuxModelInstallationStore.ValidId(identity.GetString())
                        || !value.TryGetProperty("generation", out var selected) || selected.ValueKind != JsonValueKind.String
                        || !LinuxModelInstallationStore.ValidId(selected.GetString()))
                        return "unchanged";
                    var request = DesktopLink.Decode<ModelActivationRequest>(value);
                    using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(215)))
                    {
                        activation = (request.Identity, cancellation);
                        try
                        {
                            // The store's nonblocking flock precedes the model's
                            // lifecycle gates, including the retirement await.
                            using var lease = installations.Acquire();
                            var outcome = await lease.ActivateAsync(model, request.Generation, cancellation.Token,
                                controller.PrepareModelChangeAsync);
                            return outcome switch
                            {
                                ModelPublicationOutcome.Confirmed => "confirmed",
                                ModelPublicationOutcome.Unconfirmed => "unconfirmed",
                                _ => "unchanged"
                            };
                        }
                        catch (LinuxModelInstallationStore.PublicationUnconfirmedException) { return "unconfirmed"; }
                        catch (Exception activationError) when (activationError is IOException or UnauthorizedAccessException
                            or InvalidDataException or JsonException or OperationCanceledException or ArgumentException)
                        {
                            return "unchanged";
                        }
                        finally
                        {
                            if (activation?.Cancellation == cancellation)
                                activation = null;
                            RefreshPackageChoices();
                            Status();
                        }
                    }
                default:
                    return null;
            }
        };
        try
        {
            loop.Run();
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine("TypeLay: desktop session stopped safely.");
            return 1;
        }
        finally { activation?.Cancellation.Cancel(); editorSnapshot?.Cancellation.Cancel(); translationChange?.Cancellation.Cancel(); }
    }
    private static async Task<int> ActivateModel(string home, string data, string generation)
    {
        string result;
        string? failure = null;
        bool activationRequested = false;
        try
        {
            using var model = LinuxModelFactory.Create(home, data);
            using var lease = new LinuxModelInstallationStore(home, data).Acquire();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(215));
            // ActivateAsync validates the immutable paths and metadata before
            // returning the candidate task. No exception text crosses this port.
            var activating = lease.ActivateAsync(model, generation, cancellation.Token);
            activationRequested = true;
            result = await activating switch
            {
                ModelPublicationOutcome.Confirmed => "confirmed",
                ModelPublicationOutcome.Unconfirmed => "unconfirmed",
                _ => "unchanged"
            };
            failure = result switch
            {
                "unchanged" => "worker-not-ready",
                "unconfirmed" => "publication-unconfirmed",
                _ => null
            };
        }
        catch (LinuxModelInstallationStore.PublicationUnconfirmedException)
        {
            result = "unconfirmed";
            failure = "publication-unconfirmed";
        }
        catch (OperationCanceledException)
        {
            result = "unchanged";
            failure = "cancelled";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or ArgumentException)
        {
            result = "unchanged";
            failure = activationRequested ? "activation-failed" : "installation-invalid";
        }
        if (failure is not null)
            Console.Error.WriteLine(failure);
        Console.WriteLine(result);
        return result == "confirmed" ? 0 : 1;
    }
    private static bool ValidAutomaticBinding(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 6
            || !value.TryGetProperty("nativeFocusRevision", out var focus) || !PositiveNativeRevision(focus)
            || !value.TryGetProperty("contextGeneration", out var context) || !PositiveNativeRevision(context)
            || !value.TryGetProperty("runId", out var run) || !NonEmptyGuid(run)
            || !value.TryGetProperty("connectionId", out var connection) || !NonEmptyGuid(connection)
            || !value.TryGetProperty("commandGeneration", out var generation) || !NonEmptyGuid(generation)
            || !value.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object
            || target.EnumerateObject().Count() != 3
            || !target.TryGetProperty("backend", out var backend) || backend.ValueKind != JsonValueKind.String
            || backend.GetString() != "linux-gtk3"
            || !target.TryGetProperty("value", out var field) || !NonEmptyGuid(field)
            || !target.TryGetProperty("lifetime", out var lifetime) || !NonEmptyGuid(lifetime))
            return false;
        return true;
    }
    private static bool PositiveNativeRevision(JsonElement value) => value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long revision) && revision > 0 && revision < 1L << 53;

    private static bool ApplicationIdentities(JsonElement value, out string[] identities)
    {
        identities = [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 4096)
            return false;
        var items = value.EnumerateArray().ToArray();
        if (items.Any(item => item.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(item.GetString()) || item.GetString()!.Length > 128
            || item.GetString()!.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(Path.GetFileName(item.GetString()!.Trim()))))
            return false;
        identities = items.Select(item => Path.GetFileName(item.GetString()!.Trim()).ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return true;
    }

    private static bool NonEmptyGuid(JsonElement value) => value.ValueKind == JsonValueKind.String
        && value.TryGetGuid(out var result) && result != Guid.Empty;

    private static bool TryApplyTraySettings(JsonElement patch, UserSettings current, out UserSettings updated)
    {
        updated = current;
        if (patch.ValueKind != JsonValueKind.Object)
            return false;
        HashSet<string> fields = [];
        foreach (var property in patch.EnumerateObject())
        {
            if (!fields.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "enabled" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    updated = updated with
                    {
                        Enabled = property.Value.GetBoolean()
                    };
                    break;
                case "mode" when property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is "Automatic" or "OnDemand":
                    updated = updated with
                    {
                        Mode = property.Value.GetString() == "Automatic" ? WorkMode.Automatic : WorkMode.OnDemand
                    };
                    break;
                case "managedApplications" when property.Value.ValueKind == JsonValueKind.Array:
                    var managed = property.Value.EnumerateArray().ToArray();
                    if (managed.Length > 256 || managed.Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) || item.GetString()!.Length > 128))
                        return false;
                    updated = updated with
                    {
                        ManagedApplications = managed.Select(item => item.GetString()!).ToArray()
                    };
                    break;
                case "theme" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "system" or "light" or "dark":
                    updated = updated with
                    {
                        Theme = property.Value.GetString()!
                    };
                    break;
                case "font" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "claude" or "sans" or "system":
                    updated = updated with
                    {
                        Font = property.Value.GetString()!
                    };
                    break;
                case "textSize" when property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int size) && size is >= 13 and <= 19:
                    updated = updated with
                    {
                        TextSize = size
                    };
                    break;
                case "accent" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "clay" or "ocean" or "sage" or "iris" or "graphite":
                    updated = updated with
                    {
                        Accent = property.Value.GetString()!
                    };
                    break;
                case "uiLanguage" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "system" or "zh-CN" or "en":
                    updated = updated with
                    {
                        UiLanguage = property.Value.GetString()!
                    };
                    break;
                case "motion" when property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is "system" or "full" or "reduce":
                    updated = updated with
                    {
                        Motion = property.Value.GetString()!
                    };
                    break;
                case "reduceMotion" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    // Explicit mode wins independently of JSON property order.
                    // A legacy-only patch still represents the user's old boolean intent.
                    if (!patch.TryGetProperty("motion", out _))
                        updated = updated.WithLegacyReduceMotion(property.Value.GetBoolean());
                    break;
                case "reduceTransparency" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    updated = updated with
                    {
                        ReduceTransparency = property.Value.GetBoolean()
                    };
                    break;
                case "startup" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    updated = updated with
                    {
                        Startup = property.Value.GetBoolean()
                    };
                    break;
                case "experimentalWriteback" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    updated = updated with
                    {
                        ExperimentalWriteback = property.Value.GetBoolean()
                    };
                    break;
                case "source" when property.Value.ValueKind == JsonValueKind.String:
                    updated = updated with
                    {
                        Source = property.Value.GetString()!
                    };
                    break;
                case "target" when property.Value.ValueKind == JsonValueKind.String:
                    updated = updated with
                    {
                        Target = property.Value.GetString()!
                    };
                    break;
                default:
                    return false;
            }
        }
        return fields.Count > 0 && ModelCatalog.IsSupportedSetting(updated.Source, updated.Target);
    }

    private static object SetupPresentation(string? code) => new
    {
        Failure = SetupFailurePresentation.FromCode(code),
        SetupFailurePresentation.PausedMessage,
        SetupFailurePresentation.ResumeLabel
    };

    private static void SetStartup(bool enabled, string home, string config)
    {
        string directory = Path.Combine(Path.GetDirectoryName(config)!, "autostart");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "typelay.desktop");
        if (!enabled)
        {
            File.Delete(target);
            return;
        }
        string launcher = Path.Combine(home, "typelay");
        if (launcher.Any(c => char.IsControl(c) || c is '%' or '\\' or '"' or '`' or '$'))
            throw new IOException("autostart-path-unavailable");
        File.WriteAllText(target + ".tmp", "[Desktop Entry]\nType=Application\nName=TypeLay\n"
            + "Exec=\"" + launcher + "\" --background\nTerminal=false\nOnlyShowIn=niri;\n");
        File.Move(target + ".tmp", target, true);
    }
}
