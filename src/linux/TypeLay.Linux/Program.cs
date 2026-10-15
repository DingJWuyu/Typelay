using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.Linux;

internal static class Program
{
    private sealed record SettingsDraftRequest(ShortcutSettingsDraft Shortcuts, string[]? ExcludedApplications = null);
    private sealed record PlacementRequest(RectPx Input, RectPx Work, double Width, double Height, double Gap, double MinimumHeight);
    private sealed record ApplicationSupportRequest(ApplicationSupportFact[] Facts);
    private sealed record TranslationPreparationRequest(TranslationPreparationPhase Phase,
        string? FailureCode = null);
    private sealed record PracticeBindingRequest(Guid RunId, TargetId Target);
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
        var view = new DesktopView(link);
        var settings = new SettingsStore(Path.Combine(config, "settings.json"),
            enabled => SetStartup(enabled, home, config), app => Path.GetFileName(app.Trim()).ToLowerInvariant());
        using var model = LinuxModelFactory.Create(home, data);
        using var controller = new SessionController(settings, model, view, input, input, loop,
            async binding => (await link.Ask("retireAutomaticPractice", binding, timeoutMs: 2000)).ValueKind == JsonValueKind.True);
        async Task<bool> ApplySettings(UserSettings next)
        {
            var expected = settings.Value;
            if (!await controller.PrepareSettingsChangeAsync(next) || !ReferenceEquals(expected, settings.Value))
                return false;
            settings.Update(next);
            return true;
        }
        void Status() => link.Send("status", new
        {
            model = model.Status,
            modelReady = model.Ready,
            modelInstalled = model.Installed,
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
        model.Changed += Status;
        link.Notification = (operation, value) =>
        {
            input.Receive(operation, value);
            switch (operation)
            {
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
                    modelStart = model.StartAsync();
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
                case "settingsDraft":
                    return SettingsDraftPresentation.Create();
                case "sessionShortcutSettings":
                    return SettingsDraftPresentation.SessionShortcuts(
                        SessionShortcutBindings.Restore(settings.Value.AcceptShortcut, settings.Value.ExitShortcut));
                case "saveSettingsDraft":
                    var draft = DesktopLink.Decode<SettingsDraftRequest>(value);
                    if (!draft.Shortcuts.TryApply(settings.Value, out var updated, out var error))
                        return new
                        {
                            saved = false,
                            message = SettingsDraftPresentation.ShortcutErrorMessage(error)
                        };
                    if (draft.ExcludedApplications is { } exclusions)
                    {
                        if (exclusions.Length > 256 || exclusions.Any(app => string.IsNullOrWhiteSpace(app) || app.Length > 128))
                            return new
                            {
                                saved = false,
                                message = SettingsDraftPresentation.Create().InvalidMessage
                            };
                        updated = updated! with
                        {
                            ExcludedApplications = exclusions
                        };
                    }
                    try
                    {
                        if (!await ApplySettings(updated!))
                            return new
                            {
                                saved = false,
                                message = "练习尚未确认结束，设置未保存。请重启 TypeLay 后重试。"
                            };
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
                    return ApplicationSupportPresentation.Preparation(new(model.Ready, model.Installed,
                        preparation.Phase == TranslationPreparationPhase.Idle && !modelStart.IsCompleted
                            ? TranslationPreparationPhase.StartingModel : preparation.Phase,
                        preparation.FailureCode is null ? null
                            : SetupFailurePresentation.CategoryFromCode(preparation.FailureCode)),
                        TranslationPreparationNextStep.ChooseConnection);
                case "applicationSupport":
                    // The desktop owns detection and explicit installation actions.
                    // Reading this view never starts either of them.
                    return ApplicationSupportPresentation.Create(DesktopLink.Decode<ApplicationSupportRequest>(value).Facts);
                case "place":
                    var placement = DesktopLink.Decode<PlacementRequest>(value);
                    return OverlayPlacement.Place(placement.Input, placement.Work, placement.Width,
                        placement.Height, placement.Gap, placement.MinimumHeight);
                case "traySettings":
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
                    var next = DesktopLink.Decode<UserSettings>(value);
                    if (next.Schema != 1 || next.Theme is not ("system" or "light" or "dark")
                        || next.Excluded.Length > 256 || next.Excluded.Any(app => string.IsNullOrWhiteSpace(app) || app.Length > 128))
                        return false;
                    return await ApplySettings(next);
                case "reloadModel":
                    if (model.Busy || !modelStart.IsCompleted)
                        return false;
                    if (!await controller.RetireAutomaticPracticeAsync() || model.Busy || !modelStart.IsCompleted)
                        return false;
                    model.Dispose();
                    modelStart = model.StartAsync();
                    await modelStart;
                    return model.Ready;
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
                case "source" when property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is "auto" or "zh" or "en":
                    updated = updated with
                    {
                        Source = property.Value.GetString()!
                    };
                    break;
                case "target" when property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is "zh" or "en":
                    updated = updated with
                    {
                        Target = property.Value.GetString()!
                    };
                    break;
                default:
                    return false;
            }
        }
        return fields.Count > 0;
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
