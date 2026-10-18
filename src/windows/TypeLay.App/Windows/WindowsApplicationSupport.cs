using TypeLay.Windows;
using System.Diagnostics;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

// Holds connection facts, never source text. Product rows, ordering, wording and
// next-step selection belong to the common presentation consumed by both hosts.
internal sealed class WindowsApplicationSupport
{
    private sealed record Observation(int ProcessId, long StartedAt, ApplicationSupportCapabilities Capabilities,
        ApplicationSupportReason Reason, DateTimeOffset CheckedAt);
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.Ordinal);
    private IInputBroker? _broker;
    private IInputMonitor? _monitor;
    private bool? _available;
    private bool _canIntercept;
    private string? _requested;
    private long _requestedAt;
    public long CheckGeneration
    {
        get; private set;
    }
    public event Action<string>? CheckStarted;
    public event Action<string>? CheckCompleted;

    public BrowserConnection? Browser
    {
        get; private set;
    }
    public event Action? BrowserChanged;
    public void BindBrowser(BrowserInput input)
    {
        Browser = new(input);
        Browser.Changed += () => BrowserChanged?.Invoke();
        _ = Browser.RefreshAsync();
    }
    public Task CompletePendingAsync() => Browser?.CompletePendingAsync() ?? Task.CompletedTask;

    public void Bind(IInputBroker broker, IInputMonitor monitor)
    {
        _broker = broker;
        _monitor = monitor;
    }

    public void BeginCheck(string id)
    {
        CheckGeneration++;
        _requested = id;
        _requestedAt = WindowsInterruptClock.NowMs;
        CheckStarted?.Invoke(id);
    }
    public void CancelCheck()
    {
        CheckGeneration++;
        _requested = null;
    }

    // Runs on the existing host loop, even when capture is paused by a failed
    // service. A user-initiated check must always get a bounded terminal result.
    public void Poll()
    {
        Browser?.Poll();
        if (_requested is null)
            return;
        if (_broker?.Available != true || _monitor?.Ready != true)
        {
            Disconnected();
            return;
        }
        if (_requested is { } waiting && WindowsInterruptClock.NowMs - _requestedAt > 10_000)
        {
            _requested = null;
            _observations.Remove(waiting);
            CheckCompleted?.Invoke(waiting);
        }
    }

    public void Observe(InputCapture capture, bool available, bool canIntercept, long startedAt, long checkGeneration)
    {
        _available = available;
        _canIntercept = canIntercept;
        if (!available)
        {
            Disconnected();
            return;
        }
        if (capture.Snapshot is { } source && capture.Focus == FocusMember.Source
            && WindowsTarget.TryDecode(source.Target, out var native)
            && Native.GetForegroundWindow() == (nint)native.RootHwnd)
        {
            long now = WindowsInterruptClock.NowMs;
            bool fresh = source.ObservedAtMs <= now && now - source.ObservedAtMs <= Limits.ObservationMaxAgeMs;
            bool preview = fresh && source.Access == FieldAccess.Supported && source.Bounds.IsValid
                && (source.IsUsable || source.PreviewText is not null || source.CommittedPrefix is not null);
            var capabilities = new ApplicationSupportCapabilities(preview,
                preview && source.WriteSupported && source.Ime == ImeState.Clear && canIntercept,
                source.DirectWriteSupported && fresh && source.Ime == ImeState.Clear);
            string id = Id(capture.Application, native.ProcessId, native.ProcessStartUtcTicks);
            _observations[id] = new(native.ProcessId, native.ProcessStartUtcTicks, capabilities,
                !preview ? ApplicationSupportReason.FieldUnobserved
                : !capabilities.TabAccept && !capabilities.DirectReplace ? ApplicationSupportReason.PreviewOnly
                : !capabilities.DirectReplace ? ApplicationSupportReason.NoDirectWrite : ApplicationSupportReason.LimitedFields,
                DateTimeOffset.UtcNow);
            CompleteCheck(id, Math.Min(startedAt, source.ObservedAtMs), checkGeneration);
            return;
        }
        // The settings window taking focus must not erase the result the user
        // just came here to inspect. An unsupported external field does revoke it.
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint processId);
        if (processId == 0 || processId == Environment.ProcessId)
            return;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            string id = Id(process.ProcessName, process.Id, process.StartTime.ToUniversalTime().Ticks);
            _observations[id] = new(process.Id, process.StartTime.ToUniversalTime().Ticks,
                new(), ApplicationSupportReason.FieldUnobserved, DateTimeOffset.UtcNow);
            CompleteCheck(id, startedAt, checkGeneration);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or OverflowException)
        {
        }
    }

    private void CompleteCheck(string id, long observedAt, long checkGeneration)
    {
        if (_requested != id || observedAt < _requestedAt || checkGeneration != CheckGeneration)
            return;
        _requested = null;
        CheckCompleted?.Invoke(id);
    }

    public void Disconnected()
    {
        _available = false;
        _canIntercept = false;
        _observations.Clear();
        if (_requested is { } pending)
        {
            _requested = null;
            CheckCompleted?.Invoke(pending);
        }
    }

    public ApplicationSupportView Read(UserSettings settings)
    {
        // A stopped monitor no longer schedules capture. Recheck the live host
        // even when no subsequent capture can arrive to revoke prior facts.
        if (_broker is { } broker && _monitor is { } monitor)
        {
            bool ready = broker.Available && monitor.Ready;
            if (ready && broker.ProcessId != 0)
            {
                try
                {
                    using var process = Process.GetProcessById(broker.ProcessId);
                    ready = !process.HasExited;
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                    or System.ComponentModel.Win32Exception)
                {
                    ready = false;
                }
            }
            if (!ready && _available is not null)
                Disconnected();
            _canIntercept = ready && monitor.CanInterceptAcceptance;
        }

        // These rows describe ordinary applications, not the exact owned
        // practice ACK used by WindowsInputService and the controller.
        bool ordinaryKeys = OwnedPracticeAcceptanceProtocol.TryParseKey(settings.AcceptShortcut, out _, out var acceptModifiers)
            && acceptModifiers == ShortcutModifiers.None
            && OwnedPracticeAcceptanceProtocol.TryParseKey(settings.ExitShortcut, out _, out var exitModifiers)
            && exitModifiers == ShortcutModifiers.None;
        ApplicationSupportFact Fact(string id)
        {
            bool detected = id == "desktop-text" || _observations.ContainsKey(id) || Installed(id);
            if (id == "google-chrome")
                return Browser?.Read(detected) ?? new(id, ApplicationSupportState.NeedsInstall,
                    ApplicationSupportReason.RegistrationMissing,
                    SupportedActions: [ApplicationSupportAction.Refresh, ApplicationSupportAction.OpenInstructions],
                    Details: "此发行版尚未配置 Chrome 连接。", ApplicationDetected: detected);
            if (id is not ("desktop-text" or "firefox" or "chromium" or "google-chrome" or "microsoft-edge"))
                return new(id, ApplicationSupportState.Unsupported, ApplicationSupportReason.NotImplemented,
                    SupportedActions: [ApplicationSupportAction.OpenInstructions], ApplicationDetected: detected);
            if (!settings.Enabled)
                return new(id, ApplicationSupportState.BuiltInAvailable, ApplicationSupportReason.Paused,
                    SupportedActions: [ApplicationSupportAction.Refresh, ApplicationSupportAction.OpenInstructions],
                    ApplicationDetected: detected);
            if (_available == false)
                return new(id, ApplicationSupportState.Error, ApplicationSupportReason.DetectionFailed,
                    SupportedActions: [ApplicationSupportAction.Refresh, ApplicationSupportAction.OpenInstructions],
                    Details: "请退出 TypeLay，再从完整安装目录重新启动；仍无法连接时，重新解压完整安装包。不要混用不同版本的 App 与 Broker。",
                    ApplicationDetected: detected);
            if (_observations.TryGetValue(id, out var observed))
            {
                bool alive;
                try
                {
                    using var process = Process.GetProcessById(observed.ProcessId);
                    alive = !process.HasExited && process.StartTime.ToUniversalTime().Ticks == observed.StartedAt;
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                    or System.ComponentModel.Win32Exception)
                {
                    alive = false;
                }
                if (alive)
                {
                    var capabilities = observed.Capabilities with
                    {
                        TabAccept = observed.Capabilities.TabAccept && _canIntercept && ordinaryKeys && settings.ExperimentalWriteback
                    };
                    return new(id, capabilities.Preview ? ApplicationSupportState.Connected : ApplicationSupportState.BuiltInAvailable,
                        observed.Reason, capabilities, [ApplicationSupportAction.Refresh, ApplicationSupportAction.OpenInstructions],
                        CheckedAt: observed.CheckedAt, ApplicationDetected: true);
                }
                _observations.Remove(id);
            }
            return new(id, ApplicationSupportState.BuiltInAvailable, ApplicationSupportReason.NoFocusedField,
                SupportedActions: [ApplicationSupportAction.Refresh, ApplicationSupportAction.OpenInstructions],
                ApplicationDetected: detected);
        }
        return ApplicationSupportPresentation.Create(ApplicationSupportPresentation.Create([]).Rows.Select(row => Fact(row.Id)));
    }

    private static bool Installed(string id)
    {
        // Detection only decides which rows appear first. A file or running
        // process never establishes a connection or grants any input capability.
        string? relative = id switch
        {
            "firefox" => @"Mozilla Firefox\firefox.exe",
            "chromium" => @"Chromium\Application\chrome.exe",
            "google-chrome" => @"Google\Chrome\Application\chrome.exe",
            "microsoft-edge" => @"Microsoft\Edge\Application\msedge.exe",
            "vscode" => @"Microsoft VS Code\Code.exe",
            _ => null
        };
        return relative is not null && new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        }.Any(root => File.Exists(Path.Combine(root, relative)));
    }

    private string Id(string? application, int processId, long startedAt)
    {
        foreach (var (id, observed) in _observations)
            if (observed.ProcessId == processId && observed.StartedAt == startedAt)
                return id;
        // Chromium and Google Chrome often use the same executable name.
        // Ambiguous branding stays with generic desktop fields rather than
        // attributing a successful connection to a different application.
        if (string.Equals(application, "chrome", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.MainModule?.FileVersionInfo.ProductName switch
                {
                    "Google Chrome" => "google-chrome",
                    "Chromium" => "chromium",
                    _ => "desktop-text"
                };
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return "desktop-text";
            }
        }
        return application?.ToLowerInvariant() switch
        {
            "chromium" => "chromium",
            "msedge" => "microsoft-edge",
            "firefox" => "firefox",
            "code" => "vscode",
            _ => "desktop-text"
        };
    }
}
