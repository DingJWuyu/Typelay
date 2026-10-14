using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.Linux;

internal static class Program
{
    private sealed record SettingsDraftRequest(ShortcutSettingsDraft Shortcuts, string[]? ExcludedApplications = null);
    private sealed record PlacementRequest(RectPx Input, RectPx Work, double Width, double Height, double Gap, double MinimumHeight);
    private sealed record ApplicationSupportRequest(ApplicationSupportFact[] Facts);
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
        using var controller = new SessionController(settings, model, view, input, input, loop);
        void Status() => link.Send("status", new
        {
            model = model.Status,
            modelReady = model.Ready,
            controller.ServiceStatus,
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
                case "onDemandPractice":
                    return OnDemandPracticePresentation.Create(settings.Value);
                case "startOnDemandPractice":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        || !model.Ready || !settings.Value.Enabled
                        || settings.Value.Source == settings.Value.Target
                        || (settings.Value.Mode != WorkMode.OnDemand && !value.GetBoolean()))
                        return false;
                    // Check and update on the settings owner's event loop. A GTK
                    // snapshot may lag an already queued tray or language change.
                    if (settings.Value.Mode != WorkMode.OnDemand)
                        settings.Update(settings.Value with
                        {
                            Mode = WorkMode.OnDemand
                        });
                    return true;
                case "setupFailure":
                    return SetupPresentation(value.ValueKind == JsonValueKind.String ? value.GetString() : null);
                case "applicationSupport":
                    // The desktop owns detection and explicit installation actions.
                    // Reading this view never starts either of them.
                    return ApplicationSupportPresentation.Create(DesktopLink.Decode<ApplicationSupportRequest>(value).Facts);
                case "place":
                    var placement = DesktopLink.Decode<PlacementRequest>(value);
                    return OverlayPlacement.Place(placement.Input, placement.Work, placement.Width,
                        placement.Height, placement.Gap, placement.MinimumHeight);
                case "settings":
                    var next = DesktopLink.Decode<UserSettings>(value);
                    if (next.Schema != 1 || next.Theme is not ("system" or "light" or "dark")
                        || next.Excluded.Length > 256 || next.Excluded.Any(app => string.IsNullOrWhiteSpace(app) || app.Length > 128))
                        return false;
                    settings.Update(next);
                    return true;
                case "reloadModel":
                    if (model.Busy || !modelStart.IsCompleted)
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
