using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.Linux;

internal static class Program
{
    private sealed record PlacementRequest(RectPx Input, RectPx Work, double Width, double Height, double Gap, double MinimumHeight);
    public static int Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.WriteLine("TypeLay Linux preview 0.2.0");
            return 0;
        }
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
        using var model = new ModelClient(new(Path.Combine(data, "components", "current", "runtime", "venv", "bin", "python"),
            Path.Combine(home, "worker", "run_worker.py"), Path.Combine(data, "components", "current", "models")),
            start => new LinuxChildProcess(start, Path.Combine(home, "native", "typelay-supervise")));
        using var controller = new SessionController(settings, model, view, input, input, loop);
        void Status() => link.Send("status", new
        {
            model = model.Status,
            modelReady = model.Ready,
            controller.ServiceStatus,
            controller.ShortcutStatus,
            controller.OperationStatus,
            controller.LastApplication
        });
        controller.Changed += Status;
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
                    view.ReturnRequested?.Invoke();
                    break;
                case "escape":
                    view.EscapeRequested?.Invoke();
                    break;
                case "regenerate":
                    view.RegenerateRequested?.Invoke();
                    break;
                case "exclude":
                    view.ExcludeRequested?.Invoke();
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
                case "beginEdit":
                    return view.BeginEditing?.Invoke() ?? false;
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
