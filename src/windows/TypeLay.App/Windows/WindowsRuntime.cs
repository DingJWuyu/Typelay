using Microsoft.Win32;
using System.Windows.Threading;
using TypeLay.Runtime;

namespace TypeLay.App;

// Composition and native side effects only. Product sequencing lives in TypeLay.Runtime.
internal static class WindowsRuntime
{
    public static SettingsStore CreateSettings(string? path = null) => new(
        path ?? Path.Combine(AppPaths.Data, "settings.json"), SetStartup,
        app => Path.GetFileNameWithoutExtension(app).Trim().ToLowerInvariant());

    public static ModelClient CreateModel() => new(new(AppPaths.Python, AppPaths.Worker, AppPaths.Models),
        info => new ChildProcess(info));

    public static SessionController CreateSession(SettingsStore settings, ModelClient model, OverlayWindow overlay,
        bool automationTest = false, IInputBroker? broker = null, WindowsApplicationSupport? support = null)
    {
        var monitor = new KeyboardHook(automationTest, overlay.Handle);
        broker ??= new BrokerClient();
        support?.Bind(broker, monitor);
        return new(settings, model, overlay, monitor,
            new WindowsInputService(broker, overlay.Handle, support, monitor), new DispatcherSessionLoop());
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            key.SetValue("TypeLay", "\"" + Path.Combine(AppPaths.Home, "TypeLay.exe") + "\"");
        else
            key.DeleteValue("TypeLay", false);
    }

    private sealed class DispatcherSessionLoop : ISessionLoop
    {
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        public long NowMs => Environment.TickCount64;
        public event Action? Tick;
        public DispatcherSessionLoop() => _timer.Tick += (_, _) => Tick?.Invoke();
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
    }
}
