using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal enum OwnedRetirementReason
{
    DescriptorChanged, TargetChanged, WitnessFailed, RejectedReceipt, Restart, BeginRejected, ProgressEnded
}

// Owns the independent ordinary input window, not a translation/permission path.
internal sealed partial class PracticeHost : IDisposable
{
    private IChildProcess? _child;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Func<ProcessStartInfo, IChildProcess> _start;
    private SessionController? _session;
    private CancellationTokenSource? _communication;
    private Channel<QueuedPracticeMessage>? _updates;
    private PracticeWriterTrace? _writerTrace;
    private Guid _connection;
    private long _sequence;
    private PracticeMessage? _pending;
    private bool _nativeActive, _restartAfterRetire;
    private Guid _runId;
    private TargetId? _target;
    private long _runStartedAt;
    private int _retirementTraceCount;
    private bool _disposed;
    public PracticeHost(Func<ProcessStartInfo, IChildProcess>? start = null) => _start = start ?? (info => new ChildProcess(info));
    public event Action? Failed;

    public void Bind(SessionController session)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null || _child is not null)
            throw new InvalidOperationException("practice-already-bound");
        _session = session;
        BindCompletion(session);
        session.PracticeProgressChanged += ProgressChanged;
        session.AutomaticPracticeProgressChanged += AutomaticProgressChanged;
    }

    public void Open(string settingsPath)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_child is not null && !_child.Process.HasExited)
        {
            _child.Process.Refresh();
            nint window = _child.Process.MainWindowHandle;
            if (window != 0 && Native.GetWindowThreadProcessId(window, out uint pid) != 0
                && pid == _child.Process.Id)
            {
                ShowWindow(window, 9); // User explicitly asked to return to this owned practice window.
                Native.SetForegroundWindow(window);
            }
            return;
        }
        Retire();
        _child?.Dispose();
        ReleaseAbandonedMappings();
        _child = null;
        var start = AppPaths.Child("TypeLay.Practice");
        start.ArgumentList.Add("--settings");
        start.ArgumentList.Add(Path.GetFullPath(settingsPath));
        if (_session is not null)
        {
            _connection = Guid.NewGuid();
            start.ArgumentList.Add("--progress");
            start.ArgumentList.Add(_connection.ToString("N"));
        }
        _automatic = _settings?.Value.Mode == WorkMode.Automatic;
        if (_automatic && (!OwnedPracticeAcceptanceProtocol.TryParseKey(_settings!.Value.AcceptShortcut, out _, out _)
            || !OwnedPracticeAcceptanceProtocol.TryParseKey(_settings.Value.ExitShortcut, out _, out _)))
            throw new InvalidOperationException("practice-session-bindings-unsupported");
        var child = _start(start);
        _child = child;
        child.Process.Exited += (_, _) =>
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                return;
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_retirementEvidence is { } evidence && ReferenceEquals(evidence.Child, child))
                    evidence.Confirmed = true; // This exact Process raised Exited.
                // A new practice or shutdown can overtake an exit notification.
                if (_disposed || !ReferenceEquals(_child, child))
                    return;
                _closedConfirmed = true;
                Retire();
                try
                {
                    if (child.Process.ExitCode != 0)
                        Failed?.Invoke();
                }
                catch (InvalidOperationException) { }
            }));
        };
        child.Process.EnableRaisingEvents = true;
        if (_session is null)
            return;
        _communication = new CancellationTokenSource();
        _updates = Channel.CreateBounded<QueuedPracticeMessage>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _writerTrace = _monitor is { OwnedEditorTraceEnabled: true } monitor ? new(monitor) : null;
        _ = SendProgressAsync(child, _updates.Reader, _communication.Token, _writerTrace);
        _ = ReadCommandsAsync(child, _communication.Token);
        BeginRun(child);
    }

    // Called on the WPF loop by the production capture adapter, before Runtime
    // publishes a shortcut lease. No source text enters the practice host.
    public void ObserveTarget(TargetId? target, FocusMember focus, long observedAt, long startedAt)
    {
        _dispatcher.VerifyAccess();
        long now = WindowsInterruptClock.NowMs;
        if (_disposed || !_nativeActive || _pending is not null || _runId == Guid.Empty || _session is null || target is not { } id
            || focus != FocusMember.Source || startedAt < _runStartedAt || observedAt < startedAt
            || observedAt > now || now - observedAt > Limits.ObservationMaxAgeMs || !Owns(id))
            return;
        if (_target is { } previous && previous != id)
        {
            // Broker rebinding or native field lifetime changes require an explicit restart.
            _session.EndOnDemandPractice(_runId);
            return;
        }
        if (_session.BindOnDemandPractice(_runId, id))
            _target = id;
    }

    private bool Owns(TargetId id)
    {
        if (_child is null || !WindowsTarget.TryDecode(id, out var target)
            || target.Provider != "win32-edit-transaction-v2" || target.Lifetime == Guid.Empty)
            return false;
        try
        {
            var process = _child.Process;
            if (process.HasExited || target.ProcessId != process.Id
                || target.ProcessStartUtcTicks != process.StartTime.ToUniversalTime().Ticks)
                return false;
            process.Refresh();
            nint root = process.MainWindowHandle, field = GetDlgItem(root, 101);
            if (root == 0 || field == 0 || target.RootHwnd != (long)root || target.ControlHwnd != (long)field
                || Native.GetAncestor(field, 2) != root
                || Native.GetWindowThreadProcessId(root, out uint rootProcess) == 0 || rootProcess != process.Id
                || Native.GetWindowThreadProcessId(field, out uint fieldProcess) == 0 || fieldProcess != process.Id)
                return false;
            var name = new StringBuilder(32);
            return GetClassNameW(field, name, name.Capacity) == 4 && name.ToString() == "Edit";
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void BeginRun(IChildProcess child)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !ReferenceEquals(_child, child) || child.Process.HasExited || _session is null
            || _nativeActive || _pending is not null)
            return;
        ResetCompletion();
        _session.StopSession();
        _disconnecting = false;
        _closedConfirmed = false;
        _runId = Guid.NewGuid();
        _target = null;
        _descriptor = null;
        _editorSealed = false;
        _editorAttempt = null;
        _returnAttempt = null;
        _lastOwnedBinding = null;
        if (_automatic)
        {
            if (_witnessMapping is not null)
                throw new InvalidOperationException("practice-witness-not-released");
            _witnessMapping = OwnedPracticeAcceptanceProtocol.CreateWitness(child.Process.Handle);
            _witnessWriter = new(_witnessMapping);
        }
        SendControl(child, _automatic ? "begin-auto" : "begin");
    }

    private void RetireRun(bool restart, OwnedRetirementReason reason,
        WriteStatus? receiptStatus = null, Denial? receiptReason = null, long? acceptedAtMs = null)
    {
        _dispatcher.VerifyAccess();
        if (_child is null || _runId == Guid.Empty)
            return;
        if (_retirementTraceCount < 16)
        {
            _monitor?.QueueOwnedEditorTrace(new
            {
                atMs = WindowsInterruptClock.NowMs,
                operation = "owned-practice-retire",
                order = ++_retirementTraceCount,
                connection = _connection,
                run = _runId,
                sequence = _sequence,
                reason = reason.ToString(),
                planSequence = _acceptance?.Plan.PlanSequence,
                planId = _acceptance?.Plan.Permit.Plan.Id,
                receiptStatus = receiptStatus?.ToString(),
                receiptReason = receiptReason?.ToString(),
                acceptedAtMs
            });
        }
        ResetCompletion();
        _restartAfterRetire |= restart;
        CancelEditorAttempt(sendControl: true);
        bool active = _nativeActive;
        _nativeActive = false; // Stop new binding before invalidating shared state.
        _target = null;
        RevokeAcceptance();
        if (_automatic)
            _session?.EndAutomaticPractice(_runId);
        else
            _session?.EndOnDemandPractice(_runId);
        _session?.StopSession();
        if (_pending is not null)
            return;
        if (active)
            SendControl(_child, "retire");
        else if (_restartAfterRetire && _acceptance is null)
        {
            _restartAfterRetire = false;
            BeginRun(_child); // The previous matched ACK already proved retirement.
        }
    }

    private void SendControl(IChildProcess child, string kind)
    {
        var message = new PracticeMessage(kind, _connection, _runId, checked(++_sequence),
            Acceptance: kind == "begin-auto" ? new(WitnessHandle: _witnessMapping!.RemoteHandle,
                WitnessBytes: _witnessMapping.Length, AllowInjectedInput: _allowInjectedInput) : null,
            Completion: kind == "retire" ? CompletionForRetirement() : null);
        _pending = message;
        var cancellation = _communication!.Token;
        Enqueue(message);
        _ = CheckAcknowledgementAsync(child, message, cancellation);
    }

    private async Task CheckAcknowledgementAsync(IChildProcess child, PracticeMessage message, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(message.Kind is "begin" or "begin-auto" ? 5 : 2), cancellation);
            if (ReferenceEquals(_pending, message))
                CommunicationFailed(child, cancellation);
        }
        catch (OperationCanceledException) { }
    }

    private void ProgressChanged(OnDemandPracticeProgress? progress)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _child is null || _child.Process.HasExited
            || (progress is not null && progress.RunId != _runId))
            return;
        if (progress is null || (progress.Target is { } target && !Owns(target)))
        {
            if (_nativeActive)
                RetireRun(false, OwnedRetirementReason.ProgressEnded);
            return;
        }
        if (_nativeActive && _pending is null)
            Enqueue(new("progress", _connection, _runId, _sequence, progress));
    }

    private void Enqueue(PracticeMessage message)
    {
        // Unlike coalesced display updates, control and ACKs may never be lost.
        // Exhaustion closes this connection rather than dropping a retirement.
        var trace = _writerTrace;
        var queued = trace?.Queued(message) ?? new QueuedPracticeMessage(message, null, false);
        bool accepted = _updates?.Writer.TryWrite(queued) == true;
        trace?.Published(queued, accepted);
        if (!accepted && _child is not null && _communication is not null)
            CommunicationFailed(_child, _communication.Token);
    }

    private async Task SendProgressAsync(IChildProcess child, ChannelReader<QueuedPracticeMessage> updates, CancellationToken cancellation, PracticeWriterTrace? writerTrace)
    {
        // The sole writer consumes immutable frames. Neither waiting for a
        // frame nor completing pipe I/O needs the UI that admitted the control.
        // Capture this configuration before the first context-free await.
        bool traceEditing = _monitor is { OwnedEditorTraceEnabled: true };
        try
        {
            var stream = child.Process.StandardInput.BaseStream;
            await foreach (var queued in updates.ReadAllAsync(cancellation).ConfigureAwait(false))
            {
                var update = queued.Message;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                bool traceFence = traceEditing && update.Kind == "edit-fence" && update.Editing is not null;
                if (traceFence)
                    NotifyFence(update.Editing!.Intent, OwnedEditorEvent.FenceSendStarted);
                writerTrace?.Started(queued);
                try
                {
                    await PracticeProgressPipe.WriteAsync(stream, update, deadline.Token).ConfigureAwait(false);
                }
                catch
                {
                    writerTrace?.Finished(queued, false);
                    throw;
                }
                writerTrace?.Finished(queued, true);
                if (traceFence)
                    NotifyFence(update.Editing!.Intent, OwnedEditorEvent.FenceSendCompleted);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
        {
            // One terminal notification; connection identity/cancellation and
            // all controller/host state remain owned by the original dispatcher.
            NotifyUi(() => CommunicationFailed(child, cancellation));
        }

        void NotifyFence(Guid intent, OwnedEditorEvent evt)
        {
            long transportAtMs = WindowsInterruptClock.NowMs;
            NotifyUi(() => TraceOwnedEditor(intent, evt, transportAtMs: transportAtMs));
        }
        void NotifyUi(Action notification)
        {
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                _dispatcher.BeginInvoke(notification);
        }
    }

    private sealed record QueuedPracticeMessage(PracticeMessage Message, WriterFrame? Frame, bool TraceFence,
        WriterFrame? PreviousQueued = null, WriterFrame? InFlightAtQueue = null);
    private sealed record WriterFrame(long Order, string Kind, long QueuedAtMs,
        long? WriteStartedAtMs = null, long? WriteEndedAtMs = null, bool? Completed = null);

    // Diagnostic-only, per connection. The writer owns the rolling window;
    // immutable in-flight/last-enqueued snapshots alone cross threads. Order
    // identifies samples, not channel ordering. These are managed await
    // boundaries, not proof of when a synchronous OS pipe write ran.
    private sealed class PracticeWriterTrace(KeyboardHook monitor)
    {
        private readonly Queue<WriterFrame> _recent = new(16);
        private WriterFrame? _inFlight, _lastQueued;
        private long _order;
        private int _fences;

        public QueuedPracticeMessage Queued(PracticeMessage message)
        {
            var frame = new WriterFrame(Interlocked.Increment(ref _order), message.Kind switch
            {
                "begin" or "begin-auto" or "progress" or "auto-progress" or "retire"
                    or "install" or "revoke-plan" or "release-plan" or "exit-install" or "exit-clear"
                    or "edit-fence" or "edit-cancel" or "return-fence" or "return-resume" or "return-cancel"
                    or "completion-clear" or "finish-denied" or "finish-saved" or "finish-retry" => message.Kind,
                _ => "other"
            }, WindowsInterruptClock.NowMs);
            var previous = Interlocked.Exchange(ref _lastQueued, frame);
            bool fence = message.Kind == "edit-fence" && Interlocked.Increment(ref _fences) <= 16;
            return new(message, frame, fence, previous, Volatile.Read(ref _inFlight));
        }

        public void Published(QueuedPracticeMessage queued, bool accepted)
        {
            if (queued.TraceFence)
                Record(queued, "queued", new
                {
                    accepted,
                    frame = queued.Frame,
                    queued.PreviousQueued,
                    queued.InFlightAtQueue
                });
        }

        public void Started(QueuedPracticeMessage queued)
        {
            var frame = queued.Frame! with
            {
                WriteStartedAtMs = WindowsInterruptClock.NowMs
            };
            Volatile.Write(ref _inFlight, frame);
            if (queued.TraceFence)
                Record(queued, "write-started", new
                {
                    frame,
                    preceding = _recent.ToArray()
                });
        }

        public void Finished(QueuedPracticeMessage queued, bool completed)
        {
            var frame = _inFlight! with
            {
                WriteEndedAtMs = WindowsInterruptClock.NowMs,
                Completed = completed
            };
            if (_recent.Count == 16)
                _recent.Dequeue();
            _recent.Enqueue(frame);
            Volatile.Write(ref _inFlight, null);
            if (queued.TraceFence || !completed)
                Record(queued, "write-ended", new
                {
                    frame
                });
        }

        private void Record(QueuedPracticeMessage queued, string stage, object detail) =>
            monitor.QueueOwnedEditorTrace(new
            {
                operation = "owned-editor-writer",
                stage,
                connection = queued.Message.Connection,
                run = queued.Message.Run,
                sequence = queued.Message.Sequence,
                frameOrder = queued.Frame!.Order,
                intent = queued.Message.Editing?.Intent,
                detail
            });
    }

    private async Task ReadCommandsAsync(IChildProcess child, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var message = await PracticeProgressPipe.ReadAsync(child.Process.StandardOutput.BaseStream, cancellation);
                if (_disposed || !ReferenceEquals(_child, child) || cancellation.IsCancellationRequested)
                    return;
                if (message.Connection != _connection || message.Run != _runId)
                    throw new InvalidDataException("practice-ack-identity");
                // A local restart click can cross our already-sent retirement.
                // It requests the next run; it is not an ACK for the new sequence.
                if (message.Kind == "restart" && _pending is { Kind: "retire" } retiring
                    && message.Sequence == retiring.Sequence - 1)
                {
                    _restartAfterRetire = true;
                    continue;
                }
                bool crossedRetirement = _pending is { Kind: "retire" } && message.Sequence == _sequence - 1;
                if (_automatic && (message.Sequence == _sequence || crossedRetirement) && HandleAcceptanceMessage(message))
                    continue;
                if (message.Sequence != _sequence)
                    throw new InvalidDataException("practice-ack-sequence");
                if (await HandleFinishMessageAsync(message))
                    continue;
                if (message.Kind == "closed")
                {
                    _closedConfirmed = true; // Native retirement and view closure precede this ACK.
                    ConfirmNativeRetirement();
                    _session?.StopSession();
                    Retire();
                    return;
                }
                if (message.Kind == "restart" && _pending is null)
                {
                    RetireRun(true, OwnedRetirementReason.Restart);
                    continue;
                }
                if (_pending is not { } pending || message.Kind != (pending.Kind is "begin" or "begin-auto" ? "started" : "retired"))
                    throw new InvalidDataException("practice-ack-order");
                _pending = null;
                if (pending.Kind is "begin" or "begin-auto")
                {
                    _nativeActive = true;
                    _runStartedAt = WindowsInterruptClock.NowMs;
                    if (_automatic)
                    {
                        if (message.Acceptance?.Descriptor is { } descriptor)
                            AcceptDescriptor(descriptor, controlPrefixObserved: false);
                        if (!_session!.BeginAutomaticPractice(_runId))
                            RetireRun(false, OwnedRetirementReason.BeginRejected);
                    }
                    else if (!_session!.BeginOnDemandPractice(_runId))
                        RetireRun(false, OwnedRetirementReason.BeginRejected);
                }
                else
                {
                    if (_automatic)
                        ReleaseRetiredWitness();
                    ConfirmNativeRetirement();
                    CompletionRetired(pending);
                    _retirement?.TrySetResult(true);
                    _retirement = null;
                }
                if (pending.Kind == "retire" && _restartAfterRetire && _acceptance is null)
                {
                    _restartAfterRetire = false;
                    BeginRun(child);
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            CommunicationFailed(child, cancellation);
        }
    }

    private void CommunicationFailed(IChildProcess child, CancellationToken cancellation)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || cancellation.IsCancellationRequested || !ReferenceEquals(_child, child))
            return;
        if (_editorAttempt is { } editor)
            TraceOwnedEditor(editor.Control.Intent, OwnedEditorEvent.PipeFailure, OwnedEditorReason.Connection);
        _session?.StopSession();
        Retire();
        // EOF queues native retirement on the child's outer loop. This is not
        // an acknowledgement; the closed connection can never start another run.
        try
        {
            child.Process.StandardInput.Close();
        }
        catch (IOException) { }
        // EOF can precede the process-exit event during an ordinary close.
        // The exit code, not pipe timing, decides launch failure feedback.
    }

    private void Retire()
    {
        ResetCompletion();
        _nativeActive = false;
        AbandonAcceptance();
        _pending = null;
        _restartAfterRetire = false;
        if (_automatic)
            _session?.EndAutomaticPractice(_runId);
        else
            _session?.EndOnDemandPractice(_runId);
        _runId = Guid.Empty;
        _sequence = 0;
        _connection = Guid.Empty;
        _target = null;
        _communication?.Cancel();
        _communication?.Dispose();
        _communication = null;
        _updates?.Writer.TryComplete();
        _updates = null;
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed)
            return;
        _disposed = true;
        UnbindCompletion();
        Retire();
        if (_session is not null)
        {
            _session.PracticeProgressChanged -= ProgressChanged;
            _session.AutomaticPracticeProgressChanged -= AutomaticProgressChanged;
        }
        _child?.Dispose();
        _child = null;
        ReleaseAbandonedMappings();
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder value, int size);
}
