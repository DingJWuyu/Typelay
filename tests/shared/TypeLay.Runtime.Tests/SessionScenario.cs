using TypeLay.Core;
using TypeLay.Protocol;

namespace TypeLay.Runtime.Tests;

// Controlled public ports exercise production orchestration, not desktop APIs or model quality.
// No sleeps, native handles, reflection or alternate implementation of Session/WriteSafety.
internal sealed class SessionScenario : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typelay-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
    private readonly SerialContext _context = new();
    public ManualLoop Loop { get; } = new();
    public InputMonitor Input { get; } = new();
    public SessionView View { get; } = new();
    public Translator Model { get; } = new();
    public InputService Service
    {
        get;
    }
    public SettingsStore Settings
    {
        get;
    }
    public SessionController Controller
    {
        get;
    }
    public InputSnapshot Source
    {
        get; set;
    }

    public SessionScenario(WorkMode mode = WorkMode.Automatic, bool experimental = false)
    {
        SynchronizationContext.SetSynchronizationContext(_context);
        Source = new(new("controlled", "field-a", Guid.NewGuid()), "hello", new(5, 5),
            new(10, 20, 300, 40), true, FieldAccess.Supported, ImeState.Clear, Loop.NowMs, 1, 1,
            WriteSupported: true, DirectWriteSupported: true);
        Input.FocusedTarget = Source.Target;
        Service = new(this);
        Settings = new(Path.Combine(_directory, "settings.json"));
        Settings.Update(Settings.Value with
        {
            Mode = mode,
            ExperimentalWriteback = experimental
        });
        Controller = new(Settings, Model, View, Input, Service, Loop);
        Controller.StartAsync().GetAwaiter().GetResult();
    }

    public void BlockSettingsWrites() => Directory.CreateDirectory(Path.Combine(_directory, "settings.json.tmp"));
    public void AllowSettingsWrites() => Directory.Delete(Path.Combine(_directory, "settings.json.tmp"));
    public void Tick(long elapsed = 40)
    {
        Loop.Advance(elapsed);
        _context.Drain();
    }
    public void ReadyCandidate()
    {
        Tick();
        Tick(Limits.DebounceMs);
        CompleteTranslation("translated");
        Tick();
    }
    public void CompleteTranslation(string text, string status = "translated")
    {
        Model.Complete(text, status);
        _context.Drain();
    }
    public void CompleteCapture(InputCapture capture)
    {
        var pending = Service.PendingCapture ?? throw new InvalidOperationException("No pending capture.");
        Service.PendingCapture = null;
        pending.SetResult(capture);
        _context.Drain();
    }
    public PauseObservation FreshPause => new(
        new(Source.Target, Source.Bounds, Source.Access, Loop.NowMs, Input.ForegroundEpoch),
        Input.GetFocus(Source.Target), "org.typelay.ControlledApp", Input.ForegroundEpoch);
    public void CompletePauseObservation(PauseObservation observation)
    {
        var pending = Service.PendingPauseObservation ?? throw new InvalidOperationException("No pending pause observation.");
        Service.PendingPauseObservation = null;
        pending.SetResult(observation);
        _context.Drain();
    }
    public void FailCapture()
    {
        var pending = Service.PendingCapture ?? throw new InvalidOperationException("No pending capture.");
        Service.PendingCapture = null;
        pending.SetException(new IOException("controlled capture failure"));
        _context.Drain();
    }
    public InputSnapshot Fresh => Source with { ObservedAtMs = Loop.NowMs };
    public void Command(string kind, long sequence = 0) => Input.Notices.Enqueue(new(kind, AcceptedAtMs: Loop.NowMs,
        UserEpoch: Input.UserEpoch, ForegroundEpoch: Input.ForegroundEpoch,
        Source: Fresh, CommandGeneration: Input.CommandGeneration, CommandSequence: sequence, Target: Source.Target, SessionRevision: View.Token.Revision));
    public void FocusAnotherField()
    {
        Source = Source with
        {
            Target = new("controlled", "field-b", Guid.NewGuid()),
            Text = "other",
            Selection = new(5, 5)
        };
        Input.FocusedTarget = Source.Target;
        Input.WindowEpoch++;
        Input.ForegroundEpoch++;
    }
    public void Dispose()
    {
        Controller.Dispose();
        SynchronizationContext.SetSynchronizationContext(_previous);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    private sealed class SerialContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();
        public override void Post(SendOrPostCallback callback, object? state) => _pending.Enqueue((callback, state));
        public void Drain()
        {
            while (_pending.TryDequeue(out var item))
                item.Callback(item.State);
        }
    }

    internal sealed class ManualLoop : ISessionLoop
    {
        public long NowMs { get; private set; } = 5000;
        public bool Running
        {
            get; private set;
        }
        public event Action? Tick;
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Advance(long elapsed)
        {
            NowMs += elapsed;
            if (Running)
                Tick?.Invoke();
        }
    }
    internal sealed class InputMonitor : IInputMonitor
    {
        public bool Ready { get; set; } = true;
        public bool ShortcutsReady { get; set; } = true;
        public bool CanInterceptAcceptance { get; set; } = true;
        public string ShortcutStatus => "controlled";
        public long UserEpoch { get; set; } = 1;
        public long SourceUserEpoch { get; set; } = 1;
        public long ForegroundEpoch { get; set; } = 1;
        public long WindowEpoch { get; set; } = 1;
        public long ContentEpoch { get; set; } = 1;
        public Guid CommandGeneration { get; private set; } = Guid.NewGuid();
        public TargetId FocusedTarget
        {
            get; set;
        }
        public bool InEditor
        {
            get; set;
        }
        public bool ShortcutsEnabled
        {
            get; private set;
        }
        public bool Disposed
        {
            get; private set;
        }
        public Queue<InputNotice> Notices { get; } = new();
        public AcceptancePermit? Permit
        {
            get; private set;
        }
        public ShortcutLease? Shortcut
        {
            get; private set;
        }
        public int FocusChecks
        {
            get; private set;
        }
        public FocusMember GetFocus(TargetId target)
        {
            FocusChecks++;
            return InEditor ? FocusMember.Overlay : target == FocusedTarget ? FocusMember.Source : FocusMember.Other;
        }
        public SessionShortcutBindings SessionBindings { get; private set; } = SessionShortcutBindings.Default;
        public bool SessionEnabled
        {
            get; private set;
        }
        public void ConfigureShortcuts(bool enabled, string wake, string direct, SessionShortcutBindings sessionBindings, bool sessionEnabled)
        {
            ShortcutsEnabled = enabled;
            SessionBindings = sessionBindings;
            SessionEnabled = sessionEnabled;
            CommandGeneration = Guid.NewGuid();
        }
        public bool TryRead(out InputNotice? notice) => Notices.TryDequeue(out notice);
        public List<InputLease> PublishedLeases { get; } = [];
        public List<ShortcutLease> PublishedShortcuts { get; } = [];
        public void Publish(InputLease lease, AcceptancePermit? permit)
        {
            PublishedLeases.Add(lease);
            Permit = permit;
        }
        public void PublishShortcut(ShortcutLease lease)
        {
            PublishedShortcuts.Add(lease);
            Shortcut = lease;
        }
        public void Revoke()
        {
            Permit = null;
            Shortcut = null;
        }
        public void Dispose() => Disposed = true;
    }
    internal sealed class InputService(SessionScenario host) : IInputService
    {
        public bool Available
        {
            get; set;
        }
        public bool Disposed
        {
            get; private set;
        }
        public bool ReturnAllowed { get; set; } = true;
        public TaskCompletionSource<bool>? PendingReturn
        {
            get; set;
        }
        public int Captures
        {
            get; private set;
        }
        public int Returns
        {
            get; private set;
        }
        public List<WritePlan> Writes { get; } = [];
        public List<WritePlan> DirectWrites { get; } = [];
        public TaskCompletionSource<WriteReceipt>? PendingDirectWrite
        {
            get; set;
        }
        public WriteReceipt DirectReceipt { get; set; } = new(WriteStatus.RejectedBeforeMutation, Denial.Unsupported);
        public TaskCompletionSource<InputCapture>? PendingCapture
        {
            get; set;
        }
        public Task StartAsync()
        {
            Available = true;
            return Task.CompletedTask;
        }
        public Func<SessionShortcutAcknowledgement, SessionShortcutAcknowledgement?> TransformAcknowledgement { get; set; } = value => value;
        public Task<InputCapture> CaptureAsync(InputContext context)
        {
            Captures++;
            return PendingCapture?.Task ?? Task.FromResult(new InputCapture(host.Fresh,
                host.Input.GetFocus(host.Source.Target), "org.typelay.ControlledApp", context.UserEpoch, context.ForegroundEpoch,
                TransformAcknowledgement(new(host.Source.Target, host.Input.CommandGeneration, host.Input.SessionBindings,
                    host.Input.SessionEnabled, host.Input.SessionEnabled))));
        }
        public List<InputContext> PauseRequests { get; } = [];
        public TaskCompletionSource<PauseObservation>? PendingPauseObservation
        {
            get; set;
        }
        public PauseObservation? PauseResult
        {
            get; set;
        }
        public Task<PauseObservation> ObservePauseAsync(InputContext context)
        {
            PauseRequests.Add(context);
            return PendingPauseObservation?.Task ?? Task.FromResult(PauseResult ?? host.FreshPause);
        }
        public async Task<bool> ReturnToSourceAsync(InputContext context)
        {
            Returns++;
            bool returned = PendingReturn is { } pending ? await pending.Task : ReturnAllowed;
            if (returned)
                host.Input.InEditor = false;
            return returned;
        }
        public Task<WriteReceipt> WriteAsync(WritePlan plan, InputContext context, long expiresAtMs)
        {
            Writes.Add(plan);
            return Task.FromResult(new WriteReceipt(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome));
        }
        public Task<WriteReceipt> WriteDirectAsync(WritePlan plan, string[] excluded)
        {
            DirectWrites.Add(plan);
            return PendingDirectWrite?.Task ?? Task.FromResult(DirectReceipt);
        }
        public Task ClearAsync(InputContext context) => Task.CompletedTask;
        public void Dispose()
        {
            Available = false;
            Disposed = true;
        }
    }
    internal sealed class Translator : ITranslationService
    {
        public bool Ready { get; set; } = true;
        public bool Busy => Pending is not null;
        public List<TranslationJob> Jobs { get; } = [];
        public TaskCompletionSource<ModelResponse>? Pending
        {
            get; private set;
        }
        public Task<ModelResponse> TranslateAsync(TranslationJob job)
        {
            Jobs.Add(job);
            Pending = new();
            return Pending.Task;
        }
        public void Complete(string text, string status = "translated")
        {
            var pending = Pending ?? throw new InvalidOperationException("No pending translation.");
            Pending = null;
            pending.SetResult(new(1, status, text));
        }
    }
    internal sealed class SessionView : ISessionView
    {
        public Func<SessionViewToken, bool>? BeginEditing
        {
            get; set;
        }
        public Action<string>? DraftChanged
        {
            get; set;
        }
        public Action<SessionViewToken>? ReturnRequested
        {
            get; set;
        }
        public Action<SessionViewToken>? EscapeRequested
        {
            get; set;
        }
        public Action? RegenerateRequested
        {
            get; set;
        }
        public Action? ExcludeRequested
        {
            get; set;
        }
        public Action<long>? ResumeRequested
        {
            get; set;
        }
        public long PauseRevision
        {
            get; private set;
        }
        public PauseAnchor? Anchor
        {
            get; private set;
        }
        public bool EditingComposition
        {
            get; set;
        }
        public bool Visible
        {
            get; private set;
        }
        public bool CanAccept
        {
            get; private set;
        }
        public string Preview { get; private set; } = "";
        public string Draft { get; private set; } = "";
        public SessionState State
        {
            get; private set;
        }
        public int Resumes
        {
            get; private set;
        }
        public void Configure(UserSettings settings)
        {
        }
        public SessionViewContext? Context
        {
            get; private set;
        }
        public SessionViewToken Token => Context?.Token ?? new(new("none", "none", Guid.Empty), Guid.Empty, -1);
        public void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null, SessionViewContext? context = null)
        {
            Context = context;
            State = session.State;
            Preview = session.Preview;
            Draft = session.Draft;
            CanAccept = canAccept;
            Visible = session.State is not (SessionState.NoSession or SessionState.Paused);
        }
        public void RenderPaused(PauseAnchor anchor, long revision)
        {
            Context = null;
            Anchor = anchor;
            PauseRevision = revision;
            State = SessionState.Paused;
            Preview = Draft = "";
            CanAccept = false;
            Visible = true;
        }
        public void HideQuietly()
        {
            Context = null;
            Visible = false;
            Anchor = null;
        }
        public void ResumeEditor() => Resumes++;
    }
}
