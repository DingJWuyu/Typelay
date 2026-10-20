using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

// Coordinates only outstanding RPCs. Field approval, identity, and the original
// authority deadline remain BrowserInput's responsibility. No completed round
// is a cache, and a body reply is never a prerequisite for the next round.
internal sealed class BrowserPolicyRefresh
{
    private sealed class Cycle(TaskCompletionSource<BrowserReply?> description, bool claimed)
    {
        public TaskCompletionSource<BrowserReply?> Description { get; } = description;
        public Task Completion = Task.CompletedTask;
        public bool Claimed = claimed, Invalid;
    }
    private Cycle? _cycle;
    private TaskCompletionSource<BrowserReply?>? _capture;
    private long _nextStart;
    private static TaskCompletionSource<BrowserReply?> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Poll(long now, Func<Task<BrowserReply?>> describe, Func<BrowserReply, Task> approve)
    {
        if (_cycle is { Completion.IsCompleted: false } || now < _nextStart)
            return _cycle?.Completion ?? Task.CompletedTask;
        _nextStart = now + 35; // Same minimum interval as ordinary Runtime observation.
        var cycle = new Cycle(_capture ?? Completion(), _capture is not null);
        _capture = null;
        _cycle = cycle;
        cycle.Completion = Run(cycle, describe, approve);
        return cycle.Completion;
    }
    private static async Task Run(Cycle cycle, Func<Task<BrowserReply?>> describe, Func<BrowserReply, Task> approve)
    {
        try
        {
            var reply = await describe();
            if (cycle.Invalid || reply is null)
                return;
            // Enqueue the sole approval before waking the body consumer, but do
            // not make that consumer await the policy ACK.
            Task acknowledgement = approve(reply);
            cycle.Description.TrySetResult(reply);
            await acknowledgement;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally { cycle.Description.TrySetResult(null); }
    }
    public Task<BrowserReply?> CaptureAsync()
    {
        if (_cycle is { Invalid: false, Claimed: false, Completion.IsCompleted: false } cycle)
        {
            cycle.Claimed = true;
            return cycle.Description.Task;
        }
        // Runtime has one outstanding body capture. Bound even an accidental
        // second caller instead of letting two bodies consume one description.
        if (_capture is not null)
            return Task.FromResult<BrowserReply?>(null);
        _capture = Completion();
        return _capture.Task;
    }
    public void Invalidate()
    {
        if (_cycle is { } cycle)
        {
            cycle.Invalid = true;
            cycle.Description.TrySetResult(null);
        }
        _capture?.TrySetResult(null);
        _capture = null;
        // RequestAsync timeout/disposal is the terminal RPC event. Invalidation
        // cannot cancel a command already queued to the page or bypass its ACK.
    }
}

internal sealed partial class BrowserInput
{
    private sealed record Issued(BrowserObservation Observation, InputLease Lease, AcceptancePermit Permit, long Deadline);
    private sealed record Publication(TargetId Target, Guid Plan, Guid Generation, long Revision, long ReadyUntil);
    private readonly Dictionary<Guid, Issued> _issued = new();
    private Publication? _publication;
    public bool AcceptanceReady(SessionViewToken token, AcceptancePermit issued, long now) => _publication is { } publication
        && publication.Target == token.Target && publication.Plan == issued.Plan.Id
        && publication.Generation == token.CommandGeneration && publication.Revision == token.Revision
        && publication.ReadyUntil >= now && issued.ExpiresAtMs >= now && GetFocus(token.Target) == FocusMember.Source;
    public void Publish(InputLease lease, AcceptancePermit? permit)
    {
        var record = Observation(lease.Source);
        var rejection = ObservationReason.Ready;
        if (record is null)
            rejection = ObservationReason.ObservationMissing;
        else if (!ForegroundCurrent(record))
            rejection = ObservationReason.ForegroundNotCurrent;
        else if (!AcknowledgementCurrent(record))
            rejection = ObservationReason.AcknowledgementNotCurrent;
        else if (lease.CommandGeneration != _hook.CommandGeneration)
            rejection = ObservationReason.CommandGenerationChanged;
        if (rejection != ObservationReason.Ready)
        {
            Revoke();
            TraceObservation(ObservationStage.Publish, rejection);
            return;
        }
        _lease = lease;
        if (permit is not null)
        {
            // Repeated publication of one plan renews only its actual issued permit.
            _issued[permit.Plan.Id] = new(record!, lease, permit, permit.ExpiresAtMs + 2000);
            while (_issued.Count > 128)
                _issued.Remove(_issued.Keys.First());
        }
        record!.Peer.Send("publish", new
        {
            target = record.Target.Value,
            authority = record.Authority,
            generation = _hook.CommandGeneration,
            lease,
            permit
        });
        TraceObservation(ObservationStage.Publish, ObservationReason.Ready);
    }
    public void PublishShortcut(ShortcutLease lease)
    {
        var record = Observation(lease.Source);
        var rejection = ObservationReason.Ready;
        if (record is null)
            rejection = ObservationReason.ObservationMissing;
        else if (!ForegroundCurrent(record))
            rejection = ObservationReason.ForegroundNotCurrent;
        else if (!AcknowledgementCurrent(record))
            rejection = ObservationReason.AcknowledgementNotCurrent;
        else if (!lease.Source.DirectWriteSupported)
            rejection = ObservationReason.DirectWriteUnsupported;
        else if (_editorFocused || GlossaryFocus is not null)
            rejection = ObservationReason.EditorFocused;
        if (rejection != ObservationReason.Ready)
        {
            TraceObservation(ObservationStage.Shortcut, rejection);
            return;
        }
        TraceObservation(ObservationStage.Shortcut, ObservationReason.Ready);
    }
    private sealed class CommandOwner(BrowserPeer peer, BrowserRoute route, nint root, long windowEpoch, Guid generation, long startedAt, string[] excluded)
    {
        public BrowserPeer Peer { get; } = peer;
        public BrowserRoute Route { get; } = route;
        public nint Root { get; } = root;
        public long WindowEpoch { get; } = windowEpoch;
        public Guid Generation { get; } = generation;
        public string[] Excluded { get; } = excluded.ToArray();
        public BrowserPolicyRefresh PolicyRefresh { get; } = new();
        public long FocusRevision;
        public string Id { get; } = Guid.NewGuid().ToString();
        public long EstablishUntil { get; } = startedAt + 200;
        public long MaxIssuedExpiry, GrantSequence, PendingGrant, LastGrantAt, LastCommandId;
        public long RetiredAt = long.MaxValue;
        public bool Active, EverActive, Prepared, RetirementAcknowledged;
        public BrowserReply? Preparation;
        public CommandPolicy? ReadyPolicy, PendingPolicy;
        public BrowserCommandKey? Wake, Direct;
        public readonly Dictionary<long, long> Grants = new();
        public readonly List<CommandPolicy> Policies = new();
    }
    private sealed record CommandPolicy(TargetId Target, JsonElement Authority, string[] Excluded, long ExpiresAt)
    {
        public string Token => String(Authority, "token")!;
    }
    private sealed record BrowserCommandKey(uint Keyval, int Modifiers);
    private CommandOwner? _commandOwner;
    private readonly List<CommandOwner> _retiredCommandOwners = new();
    private bool _commandsEnabled, _ownerRefreshRequested = true, _registrationDeferred, _restorationAttempted;
    private string[]? _commandExcluded;
    private BrowserCommandKey? _wake, _direct;
    internal string? CommandStatus
    {
        get
        {
            if (!(_registrationDeferred || OwnsForeground))
            {
                TraceOwner(OwnerStage.Status, OwnerReason.Native);
                return null;
            }
            var owner = _commandOwner;
            OwnerReason reason;
            long? now = null;
            if (owner is not { Active: true, ReadyPolicy: { } policy })
                reason = OwnerReason.PolicyUnavailable;
            else if (owner.RetiredAt != long.MaxValue)
                reason = OwnerReason.Retired;
            else if (!OwnerForeground(owner, out reason))
            {
            }
            else
            {
                now = WindowsInterruptClock.NowMs;
                reason = policy.ExpiresAt >= now ? OwnerReason.Ready : OwnerReason.PolicyExpired;
            }
            TraceOwner(OwnerStage.Status, reason, owner, now);
            return reason == OwnerReason.Ready ? "浏览器快捷键已就绪。" : "浏览器快捷键尚未就绪，请返回受支持的输入框。";
        }
    }
    private static BrowserCommandKey? CommandKey(string text)
    {
        if (!TranslationShortcut.TryParse(text, out var chord))
            return null;
        uint key = chord!.Key.Length == 1 ? chord.Key[0]
            : 65469u + uint.Parse(chord.Key.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
        int modifiers = (chord.Modifiers.HasFlag(ShortcutModifiers.Shift) ? 1 : 0)
            | (chord.Modifiers.HasFlag(ShortcutModifiers.Control) ? 4 : 0)
            | (chord.Modifiers.HasFlag(ShortcutModifiers.Alt) ? 8 : 0);
        return new(key, modifiers);
    }
    private static bool CommandKeyMatches(JsonElement data, string name, BrowserCommandKey? expected) => expected is not null
        && data.TryGetProperty(name, out var key) && Number(key, "keyval") == expected.Keyval && Number(key, "modifiers") == expected.Modifiers;
    internal void BeforeConfigure()
    {
        RetireCommandOwner();
        _commandExcluded = null;
        _ownerRefreshRequested = true;
        _restorationAttempted = false;
    }
    private void RequestCommandOwnerRefresh()
    {
        RetireCommandOwner();
        _ownerRefreshRequested = true;
        _restorationAttempted = false;
    }
    private bool OwnerForeground(CommandOwner owner) => OwnerForeground(owner, out _);
    private bool OwnerForeground(CommandOwner owner, out OwnerReason reason)
    {
        reason = !owner.Peer.Alive ? OwnerReason.PeerDisconnected
            : owner.Generation != _hook.CommandGeneration ? OwnerReason.GenerationChanged
            : owner.WindowEpoch != _hook.WindowEpoch ? OwnerReason.WindowChanged
            : owner.Root == 0 ? OwnerReason.RootMissing
            : owner.Root != ForegroundRoot() ? OwnerReason.RootChanged
            : _active.GetValueOrDefault(owner.Peer.ConnectionId) != owner.Route ? OwnerReason.DocumentChanged
            : OwnerReason.Ready;
        return reason == OwnerReason.Ready;
    }
    private void RetireCommandOwner(OwnerReason reason = OwnerReason.Lifecycle)
    {
        if (_commandOwner is not { } owner)
            return;
        owner.RetiredAt = WindowsInterruptClock.NowMs;
        owner.Active = false;
        owner.PolicyRefresh.Invalidate();
        owner.ReadyPolicy = owner.PendingPolicy = null;
        _commandOwner = null;
        _retiredCommandOwners.Add(owner);
        if (owner.MaxIssuedExpiry != 0 && owner.Peer.Alive)
            owner.Peer.Send("deactivate-command-owner", new
            {
                documentToken = owner.Route.DocumentToken,
                ownerId = owner.Id,
                generation = owner.Generation,
                grantSequence = owner.GrantSequence,
                traceOwner = TraceEnabled
            });
        else if (owner.MaxIssuedExpiry == 0)
            owner.RetirementAcknowledged = true;
        TraceOwner(OwnerStage.Retire, reason, owner, owner.RetiredAt);
    }
    private void PollCommandOwner(long now)
    {
        foreach (var owner in _retiredCommandOwners.Append(_commandOwner).OfType<CommandOwner>())
        {
            owner.Policies.RemoveAll(policy => now > policy.ExpiresAt + Limits.ObservationMaxAgeMs);
            foreach (long sequence in owner.Grants.Where(grant => now > grant.Value + Limits.ObservationMaxAgeMs).Select(grant => grant.Key).ToArray())
                owner.Grants.Remove(sequence);
        }
        _retiredCommandOwners.RemoveAll(owner => now > Math.Max(owner.MaxIssuedExpiry, owner.RetiredAt) + Limits.ObservationMaxAgeMs);
        if (_commandOwner is { } current)
        {
            OwnerReason reason;
            if (!OwnerForeground(current, out reason))
            {
            }
            else if (!_commandsEnabled)
                reason = OwnerReason.CommandsDisabled;
            else if (!_sessionEnabled)
                reason = OwnerReason.SessionDisabled;
            else if (!current.Active && now > current.EstablishUntil)
                reason = OwnerReason.EstablishExpired;
            else if (current.Active)
                reason = now > current.MaxIssuedExpiry ? OwnerReason.GrantExpired
                    : current.PendingGrant != 0 && now - current.LastGrantAt > 200 ? OwnerReason.AckExpired : OwnerReason.Ready;
            else
                reason = OwnerReason.Ready;
            if (reason != OwnerReason.Ready)
                RetireCommandOwner(reason);
            else if (current.Prepared && !current.Active && current.MaxIssuedExpiry == 0 && _hook.CommandsReleased)
                ActivateCommandOwner(current, now);
            else if (current.Active && current.PendingGrant == 0 && now - current.LastGrantAt >= 200)
                SendOwnerGrant(current, false, now);
        }
        if (_commandOwner is { } activeOwner)
        {
            if (activeOwner.Active && !_editorFocused && _editorTransition is null)
                PollCommandPolicy(activeOwner, now);
            return;
        }
        bool safelyRetired = _retiredCommandOwners.All(owner => owner.RetirementAcknowledged || now > owner.MaxIssuedExpiry);
        if (!safelyRetired)
        {
            TraceOwner(OwnerStage.Candidate, OwnerReason.RetirementPending, now: now);
            return;
        }
        bool released = _hook.CommandsReleased;
        if (!released)
        {
            TraceOwner(OwnerStage.Candidate, OwnerReason.KeysHeld, now: now, released: released);
            return;
        }
        var candidateReason = !_ownerRefreshRequested ? OwnerReason.NoRefresh
            : !_commandsEnabled ? OwnerReason.CommandsDisabled
            : !_sessionEnabled ? OwnerReason.SessionDisabled
            : _commandExcluded is null ? OwnerReason.PolicyContextMissing
            : _retiredCommandOwners.Count >= 4 ? OwnerReason.RetiredLimit
            : _wake is null ? OwnerReason.WakeMissing
            : _direct is null ? OwnerReason.DirectMissing : OwnerReason.Ready;
        if (candidateReason == OwnerReason.Ready)
        {
            var peer = ForegroundPeer(out int candidateCount);
            if (peer is not null && _active.TryGetValue(peer.ConnectionId, out var route))
            {
                _ownerRefreshRequested = false;
                var owner = new CommandOwner(peer, route, ForegroundRoot(), _hook.WindowEpoch, _hook.CommandGeneration, now, _commandExcluded!);
                owner.Wake = _wake;
                owner.Direct = _direct;
                _commandOwner = owner;
                _ = PrepareCommandOwnerAsync(owner);
                TraceOwner(OwnerStage.Candidate, OwnerReason.Started, owner, now, released);
                return;
            }
            candidateReason = peer is null ? candidateCount == 0 ? OwnerReason.PeerMissing : OwnerReason.PeerAmbiguous
                : OwnerReason.DocumentMissing;
        }
        if (_registrationDeferred && !_restorationAttempted)
        {
            _restorationAttempted = true;
            bool restored = _hook.RestoreCommandRegistration();
            if (restored)
                _registrationDeferred = false;
            TraceOwner(OwnerStage.Restore, restored ? OwnerReason.Ready : OwnerReason.RestoreFailed, now: now);
        }
        TraceOwner(OwnerStage.Candidate, candidateReason, now: now, released: released);
    }
    private async Task PrepareCommandOwnerAsync(CommandOwner owner)
    {
        var reply = await owner.Peer.RequestAsync("prepare-command-owner", new
        {
            documentToken = owner.Route.DocumentToken,
            ownerId = owner.Id,
            generation = owner.Generation,
            traceOwner = TraceEnabled
        }, (int)Math.Max(1, owner.EstablishUntil - WindowsInterruptClock.NowMs));
        if (_commandOwner != owner)
        {
            TraceOwner(OwnerStage.Prepare, OwnerReason.Replaced, owner);
            return;
        }
        OwnerReason reason;
        long? now = null;
        if (!OwnerForeground(owner, out reason))
        {
        }
        else if ((now = WindowsInterruptClock.NowMs) > owner.EstablishUntil)
            reason = OwnerReason.EstablishExpired;
        else if (reply?.Route != owner.Route)
            reason = reply is null ? OwnerReason.ReplyMissing : OwnerReason.RouteMismatch;
        else if (String(reply.Data, "ownerId") != owner.Id)
            reason = OwnerReason.OwnerMismatch;
        else if (Number(reply.Data, "commandLocalVersion") != 1)
            reason = OwnerReason.VersionMismatch;
        else if (!CommandKeyMatches(reply.Data, "wake", _wake))
            reason = OwnerReason.WakeMismatch;
        else if (!CommandKeyMatches(reply.Data, "direct", _direct))
            reason = OwnerReason.DirectMismatch;
        else if (!Approved(owner.Peer, reply, owner.Excluded, out _, out _, out _, out _, ObservationStage.OwnerPrepare))
            reason = OwnerReason.ApprovalRejected;
        else if (!Boolean(reply.Data, "focused"))
            reason = OwnerReason.SourceNotFocused;
        else
            reason = OwnerReason.Ready;
        if (reason != OwnerReason.Ready)
        {
            RetireCommandOwner(reason);
            TraceOwner(OwnerStage.Prepare, reason, owner, now);
            return;
        }
        owner.Preparation = reply;
        owner.Prepared = true;
        bool released = _hook.CommandsReleased;
        if (released)
            ActivateCommandOwner(owner, WindowsInterruptClock.NowMs);
        TraceOwner(OwnerStage.Prepare, OwnerReason.Ready, owner, now, released);
    }
    private void ActivateCommandOwner(CommandOwner owner, long now)
    {
        OwnerReason reason;
        bool? released = null;
        if (owner.Preparation is not { } reply)
            reason = OwnerReason.PreparationMissing;
        else if (!OwnerForeground(owner, out reason))
        {
        }
        else if (now > owner.EstablishUntil)
            reason = OwnerReason.EstablishExpired;
        else if (!(released = _hook.CommandsReleased).Value)
            reason = OwnerReason.KeysHeld;
        else
            reason = OwnerReason.Ready;
        if (reason != OwnerReason.Ready)
        {
            RetireCommandOwner(reason);
            TraceOwner(OwnerStage.Activate, reason, owner, now, released);
            return;
        }
        var authority = owner.Preparation!.Data.GetProperty("authority").Clone();
        long expiry = Number(authority, "observedAtMs") + Limits.ObservationMaxAgeMs;
        if (now > expiry)
        {
            RetireCommandOwner(OwnerReason.PolicyExpired);
            TraceOwner(OwnerStage.Activate, OwnerReason.PolicyExpired, owner, now, released);
            return;
        }
        var target = new TargetId("windows-browser", String(authority, "target")!, owner.Peer.ConnectionId);
        bool suspended = _hook.SuspendCommandRegistration();
        _hook.DeferCommandRegistration();
        _registrationDeferred = true;
        _restorationAttempted = false;
        if (!suspended)
        {
            RetireCommandOwner(OwnerReason.SuspendFailed);
            TraceOwner(OwnerStage.Activate, OwnerReason.SuspendFailed, owner, now, released);
            return;
        }
        // A native press racing the release remains owned until its real up.
        released = _hook.CommandsReleased;
        if (!released.Value)
        {
            TraceOwner(OwnerStage.Activate, OwnerReason.KeysHeld, owner, now, released);
            return;
        }
        owner.PendingPolicy = new(target, authority, owner.Excluded, expiry);
        owner.Policies.Add(owner.PendingPolicy);
        SendOwnerGrant(owner, true, now);
    }
    private void SendOwnerGrant(CommandOwner owner, bool activate, long now)
    {
        long sequence = ++owner.GrantSequence;
        long expiry = now + 800;
        owner.MaxIssuedExpiry = Math.Max(owner.MaxIssuedExpiry, expiry); // Includes a grant whose ACK may be lost.
        owner.Grants.Add(sequence, expiry);
        owner.PendingGrant = sequence;
        owner.LastGrantAt = now;
        if (activate)
        {
            var policy = owner.Policies[^1];
            owner.Peer.Send("activate-command-owner", new
            {
                target = policy.Target.Value,
                ownerId = owner.Id,
                generation = owner.Generation,
                grantSequence = sequence,
                expiresAtMs = expiry,
                authority = policy.Authority,
                releasedAtMs = _hook.CommandsReleasedAt,
                traceOwner = TraceEnabled
            });
        }
        else
            owner.Peer.Send("renew-command-owner", new
            {
                documentToken = owner.Route.DocumentToken,
                ownerId = owner.Id,
                generation = owner.Generation,
                grantSequence = sequence,
                expiresAtMs = expiry,
                traceOwner = TraceEnabled
            });
        TraceOwner(activate ? OwnerStage.Activate : OwnerStage.Renew, OwnerReason.Sent, owner, now);
    }
    private void OwnerAcknowledged(BrowserPeer peer, BrowserRoute route, JsonElement data)
    {
        string? id = String(data, "ownerId");
        var owner = _retiredCommandOwners.Append(_commandOwner).OfType<CommandOwner>()
            .FirstOrDefault(item => item.Peer == peer && item.Route == route && item.Id == id);
        var stage = owner is { EverActive: true } ? OwnerStage.RenewAck : OwnerStage.InitialAck;
        var reason = owner is null ? OwnerReason.OwnerMissing
            : String(data, "generation") != owner.Generation.ToString() ? OwnerReason.GenerationMismatch
            : !CommandKeyMatches(data, "wake", owner.Wake) ? OwnerReason.WakeMismatch
            : !CommandKeyMatches(data, "direct", owner.Direct) ? OwnerReason.DirectMismatch : OwnerReason.Ready;
        if (reason != OwnerReason.Ready)
        {
            TraceOwner(stage, reason, owner, page: data);
            return;
        }
        long sequence = Number(data, "grantSequence");
        if (sequence != owner!.GrantSequence)
        {
            TraceOwner(stage, OwnerReason.GrantMismatch, owner, page: data);
            return;
        }
        if (String(data, "state") == "retired")
        {
            owner.RetirementAcknowledged = true;
            if (_commandOwner == owner)
                RetireCommandOwner(OwnerReason.PageRetired);
            TraceOwner(OwnerStage.RetireAck, OwnerReason.Ready, owner, page: data);
            return;
        }
        if (_commandOwner != owner || owner.RetiredAt != long.MaxValue)
        {
            TraceOwner(stage, OwnerReason.Retired, owner, page: data);
            return;
        }
        long now = WindowsInterruptClock.NowMs;
        if (String(data, "state") != "active")
            reason = OwnerReason.PageNotActive;
        else if (sequence != owner.PendingGrant)
            reason = OwnerReason.PendingGrantMismatch;
        else if (now - owner.LastGrantAt > 200)
            reason = OwnerReason.AckExpired;
        else if (!owner.EverActive && now > owner.EstablishUntil)
            reason = OwnerReason.EstablishExpired;
        else if (!owner.Grants.TryGetValue(sequence, out long expiry))
            reason = OwnerReason.GrantMissing;
        else if (Number(data, "expiresAtMs") != expiry)
            reason = OwnerReason.ExpiryMismatch;
        else if (now > expiry)
            reason = OwnerReason.GrantExpired;
        else if (!OwnerForeground(owner, out reason))
        {
        }
        else
            reason = OwnerReason.Ready;
        if (reason != OwnerReason.Ready)
        {
            RetireCommandOwner(reason);
            TraceOwner(stage, reason, owner, now, page: data);
            return;
        }
        var policy = owner.Policies.FindLast(item => item.Token == String(data, "policyToken") && item.Target.Value == String(data, "target"));
        if (!owner.EverActive && (policy is null || owner.PendingPolicy != policy))
        {
            RetireCommandOwner(OwnerReason.PolicyAckMismatch);
            TraceOwner(stage, OwnerReason.PolicyAckMismatch, owner, now, page: data);
            return;
        }
        owner.Active = true;
        owner.EverActive = true;
        owner.PendingGrant = 0;
        if (policy is not null && owner.PendingPolicy == policy)
            owner.ReadyPolicy = policy;
        TraceOwner(stage, OwnerReason.Ready, owner, now, page: data);
    }
    private bool PolicyContextCurrent(CommandOwner owner, long focusRevision, long foregroundEpoch) =>
        _commandOwner == owner && owner.Active && owner.RetiredAt == long.MaxValue
        && owner.FocusRevision == focusRevision && _hook.ForegroundEpoch == foregroundEpoch
        && owner.Generation == _configurationGeneration && OwnerForeground(owner)
        && _commandsEnabled && _sessionEnabled && !_editorFocused && GlossaryFocus is null && _editorTransition is null
        && _commandExcluded is not null && owner.Excluded.SequenceEqual(_commandExcluded);
    private static void InvalidateCommandPolicy(CommandOwner owner)
    {
        owner.FocusRevision++;
        owner.PolicyRefresh.Invalidate();
        owner.ReadyPolicy = owner.PendingPolicy = null;
    }
    private void PollCommandPolicy(CommandOwner owner, long now)
    {
        long focusRevision = owner.FocusRevision, foregroundEpoch = _hook.ForegroundEpoch;
        if (!PolicyContextCurrent(owner, focusRevision, foregroundEpoch))
            return;
        _ = owner.PolicyRefresh.Poll(now,
            () => DescribeCommandPolicyAsync(owner, focusRevision, foregroundEpoch),
            description => ApproveCommandPolicy(owner, focusRevision, foregroundEpoch, description));
    }
    private async Task<BrowserReply?> DescribeCommandPolicyAsync(CommandOwner owner, long focusRevision, long foregroundEpoch)
    {
        if (!PolicyContextCurrent(owner, focusRevision, foregroundEpoch))
            return null;
        var description = await owner.Peer.RequestAsync("describe", new
        {
            documentToken = owner.Route.DocumentToken
        });
        if (!PolicyContextCurrent(owner, focusRevision, foregroundEpoch)
            || description?.Route != owner.Route
            || !Approved(owner.Peer, description, owner.Excluded, out var authority, out _, out _, out _, ObservationStage.Describe)
            || !Boolean(description.Data, "focused"))
            return null;
        long now = WindowsInterruptClock.NowMs, observed = Number(authority, "observedAtMs");
        if (now < observed || now - observed > Limits.ObservationMaxAgeMs)
            return null;
        TraceObservation(ObservationStage.Describe, ObservationReason.Ready, description.Data, false);
        return description;
    }
    private Task ApproveCommandPolicy(CommandOwner owner, long focusRevision, long foregroundEpoch, BrowserReply description)
    {
        // This is the only renewal submitter. The same context is checked again
        // immediately before enqueue and after ACK; a late describe cannot send
        // an old approval after a replacement round has begun.
        if (!PolicyContextCurrent(owner, focusRevision, foregroundEpoch)
            || !Approved(owner.Peer, description, owner.Excluded, out var authority, out _, out var target, out _))
            return Task.CompletedTask;
        OwnerReason reason;
        if (owner.Route != description.Route)
            reason = OwnerReason.RouteMismatch;
        else if (!OwnerForeground(owner, out reason))
        {
        }
        else if (!Boolean(description.Data, "focused"))
            reason = OwnerReason.SourceNotFocused;
        else if (owner.Policies.Count >= 16)
            reason = OwnerReason.PolicyLimit;
        else
            reason = OwnerReason.Ready;
        if (reason != OwnerReason.Ready)
        {
            TraceOwner(OwnerStage.Policy, reason, owner);
            return Task.CompletedTask;
        }
        long observed = Number(authority, "observedAtMs"), expiry = observed + Limits.ObservationMaxAgeMs;
        long now = WindowsInterruptClock.NowMs;
        if (now < observed || now > expiry)
        {
            TraceOwner(OwnerStage.Policy, OwnerReason.PolicyExpired, owner, now);
            return Task.CompletedTask;
        }
        var policy = new CommandPolicy(target, authority.Clone(), owner.Excluded, expiry);
        if (owner.Policies.Any(item => item.Token == policy.Token))
        {
            TraceOwner(OwnerStage.Policy, OwnerReason.PolicyDuplicate, owner, now);
            return Task.CompletedTask;
        }
        owner.Policies.Add(policy);
        owner.PendingPolicy = policy;
        Task acknowledgement = ApproveCommandPolicyAsync(owner, policy, focusRevision, foregroundEpoch);
        TraceOwner(OwnerStage.Policy, OwnerReason.Sent, owner, now);
        return acknowledgement;
    }
    private async Task ApproveCommandPolicyAsync(CommandOwner owner, CommandPolicy policy, long focusRevision, long foregroundEpoch)
    {
        if (!PolicyContextCurrent(owner, focusRevision, foregroundEpoch))
            return;
        var reply = await owner.Peer.RequestAsync("approve-command-policy", new
        {
            target = policy.Target.Value,
            ownerId = owner.Id,
            generation = owner.Generation,
            authority = policy.Authority,
            traceOwner = TraceEnabled
        }, (int)Math.Max(1, policy.ExpiresAt - WindowsInterruptClock.NowMs));
        OwnerReason reason;
        long? now = null;
        if (_commandOwner != owner)
            reason = OwnerReason.Replaced;
        else if (owner.PendingPolicy != policy)
            reason = OwnerReason.PolicyReplaced;
        else if (!PolicyContextCurrent(owner, focusRevision, foregroundEpoch))
            reason = OwnerReason.Replaced;
        else if (reply?.Route != owner.Route)
            reason = reply is null ? OwnerReason.ReplyMissing : OwnerReason.RouteMismatch;
        else if (!Boolean(reply.Data, "applied"))
            reason = OwnerReason.PageNotApplied;
        else if (String(reply.Data, "ownerId") != owner.Id)
            reason = OwnerReason.OwnerMismatch;
        else if (String(reply.Data, "generation") != owner.Generation.ToString())
            reason = OwnerReason.GenerationMismatch;
        else if (String(reply.Data, "target") != policy.Target.Value)
            reason = OwnerReason.TargetMismatch;
        else if (String(reply.Data, "policyToken") != policy.Token)
            reason = OwnerReason.PolicyAckMismatch;
        else if (!CommandKeyMatches(reply.Data, "wake", _wake))
            reason = OwnerReason.WakeMismatch;
        else if (!CommandKeyMatches(reply.Data, "direct", _direct))
            reason = OwnerReason.DirectMismatch;
        else if ((now = WindowsInterruptClock.NowMs) > policy.ExpiresAt)
            reason = OwnerReason.PolicyExpired;
        else
            reason = OwnerReason.Ready;
        if (reason == OwnerReason.Ready)
            owner.ReadyPolicy = policy;
        TraceOwner(OwnerStage.PolicyAck, reason, owner, now, page: reply?.Data ?? default);
    }
    private void SourceCommand(BrowserPeer peer, BrowserRoute route, JsonElement data)
    {
        var owner = _retiredCommandOwners.Append(_commandOwner).OfType<CommandOwner>().FirstOrDefault(item =>
            item.Peer == peer && item.Route == route && item.Id == String(data, "ownerId"));
        long commandId = Number(data, "commandId"), t0 = Number(data, "triggeredAtMs"), now = WindowsInterruptClock.NowMs;
        string? kind = String(data, "kind");
        long ownerExpiry = 0;
        var reason = owner is null ? CommandPathReason.OwnerMissing
            : !owner.EverActive ? CommandPathReason.OwnerNotActive
            : owner.Generation != _hook.CommandGeneration ? CommandPathReason.CurrentGenerationChanged
            : String(data, "generation") != owner.Generation.ToString() ? CommandPathReason.MessageGenerationChanged
            : kind is not ("wake" or "direct") ? CommandPathReason.KindInvalid
            : commandId is <= 0 or > 9007199254740991 ? CommandPathReason.CommandIdInvalid
            : commandId <= owner.LastCommandId ? CommandPathReason.CommandReplayed
            : !owner.Grants.TryGetValue(Number(data, "ownerGrantSequence"), out ownerExpiry) ? CommandPathReason.GrantMissing
            : t0 < 0 ? CommandPathReason.TriggerInvalid
            : now < t0 ? CommandPathReason.TriggerFuture
            : now - t0 > Limits.ObservationMaxAgeMs ? CommandPathReason.TriggerExpired : CommandPathReason.Queued;
        if (reason != CommandPathReason.Queued)
        {
            TraceCommandPath(CommandPathStage.Ingress, reason, kind, ageMs: t0 >= 0 ? now - t0 : null);
            return;
        }
        owner!.LastCommandId = commandId;
        if (_notices.Count >= 16)
        {
            TraceCommandPath(CommandPathStage.Ingress, CommandPathReason.NoticeCapacity, kind, ageMs: now - t0);
            return;
        }
        long sequence = _hook.NextCommandSequence();
        void Reject(Denial denial, CommandPathReason rejected)
        {
            _notices.Enqueue((new(kind!, AcceptedAtMs: t0,
                CommandGeneration: owner.Generation, Rejection: denial, CommandSequence: sequence), t0 + Limits.ObservationMaxAgeMs));
            TraceCommandPath(CommandPathStage.Ingress, rejected, kind, ageMs: now - t0);
        }
        if (!data.TryGetProperty("native", out var native))
        {
            Reject(String(data, "reason") == "ImeUnclear" ? Denial.ImeUnclear : Denial.StaleObservation, CommandPathReason.NativeMissing);
            return;
        }
        var policy = owner.Policies.FindLast(item => item.Token == String(data, "policyToken"));
        long capturedAt = Number(data, "capturedAtMs"), upper = Number(data, "captureUpperMs");
        JsonElement authority = default, applied = default;
        FieldMetadata? metadata = null;
        TargetId target = default;
        SessionShortcutAcknowledgement? ack = null;
        reason = policy is null ? CommandPathReason.PolicyMissing
            : upper < capturedAt ? CommandPathReason.CaptureUpperInvalid
            : upper > t0 + Limits.ObservationMaxAgeMs ? CommandPathReason.CaptureBudgetExceeded
            : upper > ownerExpiry ? CommandPathReason.OwnerExpired
            : upper > policy.ExpiresAt ? CommandPathReason.PolicyExpired
            : upper > owner.RetiredAt ? CommandPathReason.OwnerRetired
            : !Approved(peer, new(native, route), policy.Excluded, out authority, out metadata, out target, out ack) ? CommandPathReason.ApprovalRejected
            : target != policy.Target ? CommandPathReason.TargetMismatch
            : !native.TryGetProperty("sessionBindings", out applied) ? CommandPathReason.KeyAcknowledgementMissing
            : String(applied, "target") != policy.Target.Value ? CommandPathReason.KeyTargetMismatch
            : String(authority, "metadataKey") != String(policy.Authority, "metadataKey") ? CommandPathReason.MetadataChanged
            : !Boolean(native, "focused") ? CommandPathReason.SourceNotFocused : CommandPathReason.Queued;
        if (reason != CommandPathReason.Queued)
        {
            Reject(Denial.StaleObservation, reason);
            return;
        }
        var source = Snapshot(native, target, metadata!);
        var evidence = new CommandCaptureEvidence(target, t0, capturedAt);
        reason = source is null ? CommandPathReason.SnapshotUnavailable
            : evidence.Check(source, now) != Denial.None ? CommandPathReason.EvidenceRejected
            : source.Ime != ImeState.Clear ? CommandPathReason.ImeUnclear
            : !source.DirectWriteSupported ? CommandPathReason.DirectUnsupported : CommandPathReason.Queued;
        if (reason != CommandPathReason.Queued)
        {
            Reject(Denial.StaleObservation, reason);
            return;
        }
        if (kind == "direct" && _frozen.Count >= 4)
        {
            Reject(Denial.PlatformFailure, CommandPathReason.FrozenCapacity);
            return;
        }
        var epochs = _hook.BrowserEpochs;
        var record = new BrowserObservation(peer, route, authority, target, capturedAt, owner.Root, epochs, ack!);
        _observations.Remember(record);
        if (OwnerForeground(owner))
            _current = record;
        if (kind == "direct")
            _frozen.TryAdd((target, capturedAt), new(record, owner.Generation, sequence, t0 + 212000));
        _notices.Enqueue((new(kind!, AcceptedAtMs: t0, UserEpoch: epochs.User, ForegroundEpoch: epochs.Foreground,
            Source: source, CommandGeneration: owner.Generation, CommandSequence: sequence, CommandCapture: evidence, Application: "chrome"),
            t0 + Limits.ObservationMaxAgeMs));
        TraceCommandPath(CommandPathStage.Ingress, CommandPathReason.Queued, kind, hasNative: true, ageMs: now - t0);
    }
    public async Task<WriteReceipt> WriteDirectAsync(WritePlan plan, string[] excluded)
    {
        if (!_frozen.Remove((plan.Expected.Target, plan.Expected.ObservedAtMs), out var frozen))
            return Refused();
        var original = frozen.Observation;
        if (!original.Peer.Alive || !AcknowledgementCurrent(original))
            return Refused();
        Guid generation = _hook.CommandGeneration;
        var description = await original.Peer.RequestAsync("describe", new
        {
            target = original.Target.Value
        });
        if (!Approved(original.Peer, description, excluded, out var authority, out _, out var target, out _)
            || target != original.Target || description!.Route != original.Route || generation != _hook.CommandGeneration)
            return Refused();
        // Direct uses the original scope/text and document. No foreground/focus
        // restoration or current-selection requirement is introduced here.
        var reply = await original.Peer.RequestAsync("direct", new
        {
            target = original.Target.Value,
            authority = original.Authority,
            approved = authority,
            generation,
            plan,
            expiresAtMs = WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs
        }, 1000);
        if (reply is null || reply.Route != original.Route)
        {
            original.Peer.Send("quarantine", new
            {
                target = original.Target.Value
            });
            return Unknown();
        }
        var receipt = Receipt(reply.Data, original, plan);
        if (receipt.Status == WriteStatus.OutcomeUnknown)
            original.Peer.Send("quarantine", new
            {
                target = original.Target.Value
            });
        Trace("browser-direct-" + receipt.Status, new
        {
            reason = receipt.Reason
        });
        return receipt;
    }
    internal void ObserveFeedback(CommandFeedback feedback) => ReleaseCommand(feedback.CommandGeneration, feedback.CommandSequence);
    private void ReleaseCommand(Guid generation, long sequence)
    {
        foreach (var key in _frozen.Where(item => item.Value.Generation == generation
            && item.Value.Sequence == sequence).Select(item => item.Key).ToArray())
            _frozen.Remove(key);
    }
    private static WriteReceipt Refused() => new(WriteStatus.RejectedBeforeMutation, Denial.StaleObservation);
    private static WriteReceipt Unknown() => new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
    private static WriteReceipt Receipt(JsonElement value, BrowserObservation record, WritePlan plan)
    {
        try
        {
            var result = value.Deserialize<WriteReceipt>(BrowserServer.Json);
            if (result is null || !Enum.IsDefined(result.Status) || !Enum.IsDefined(result.Reason))
                return Unknown();
            if (result.Status == WriteStatus.Verified && (result.VerifiedSnapshot is not { } verified
                || verified.Target != record.Target || verified.Text != plan.ResultText
                || verified.Selection != new TextRange(plan.Caret, plan.Caret)))
                return Unknown();
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return Unknown(); }
    }
    internal async Task<JsonElement?> DescribeEditorAsync(BrowserObservation original, SessionViewToken token, string[] excluded)
    {
        if (token.Target != original.Target || token.CommandGeneration != _hook.CommandGeneration
            || GetFocus(original.Target) != FocusMember.Overlay || !ForegroundCurrent(original))
            return null;
        var glossary = GlossaryFocus;
        var description = await original.Peer.RequestAsync("describe", new
        {
            target = original.Target.Value
        });
        if (!Approved(original.Peer, description, excluded, out var authority, out _, out var target, out _, glossary: glossary)
            || target != original.Target || description!.Route != original.Route
            || token.CommandGeneration != _hook.CommandGeneration || !ForegroundCurrent(original)
            || GetFocus(original.Target) != FocusMember.Overlay
            || String(authority, "metadataKey") != String(original.Authority, "metadataKey")
            || Number(authority, "contentRevision") != Number(original.Authority, "contentRevision")
            || Number(authority, "inputRevision") != Number(original.Authority, "inputRevision")
            || glossary is not null && !GlossaryEvidence(description.Data, glossary))
            return null;
        return authority; // Metadata-only UI clock evidence. Never a new source snapshot.
    }
    public async Task<InputReturn> ReturnAsync(InputContext context)
    {
        if (context.BoundTarget is not { } target || Latest(target) is not { } record
            || !ForegroundCurrent(record) || !AcknowledgementCurrent(record))
            return new(false);
        var reply = await record.Peer.RequestAsync("return", new
        {
            target = target.Value,
            authority = record.Authority,
            generation = _hook.CommandGeneration
        });
        if (reply is null || reply.Route != record.Route || !Boolean(reply.Data, "returned"))
            return new(false);
        _editorFocused = false;
        return new(true);
    }
    public Task ClearAsync(InputContext context)
    {
        Revoke();
        return Task.CompletedTask;
    }
    public Task<PauseObservation> ObservePauseAsync(InputContext context)
    {
        // A paused configuration intentionally has no source-ready ACK. Until
        // its metadata-only pause route is qualified, return zero authority.
        return Task.FromResult(new PauseObservation(null, ForegroundEpoch: _hook.ForegroundEpoch));
    }
    private void Receive(BrowserEvent message)
    {
        var (peer, name, value, route) = message;
        if (name == "command-owner")
        {
            OwnerAcknowledged(peer, route, value);
            return;
        }
        // An authenticated route transports diagnostics, but never makes them
        // commands. This branch cannot enqueue a Runtime notice or read a field.
        if (name == "source-command-diagnostic")
        {
            TracePageCommand(value);
            return;
        }
        if (name == "source-command")
        {
            SourceCommand(peer, route, value);
            return;
        }
        if (name == "document-focus")
        {
            bool changed = false;
            if (value.ValueKind == JsonValueKind.True)
            {
                changed = !_active.TryGetValue(peer.ConnectionId, out var previous) || previous != route;
                _active[peer.ConnectionId] = route;
            }
            else if (_active.TryGetValue(peer.ConnectionId, out var previous) && previous == route)
            {
                _active.Remove(peer.ConnectionId);
                changed = true;
            }
            if (changed)
                RequestCommandOwnerRefresh();
            if (changed && _current?.Peer == peer)
            {
                _editorFocused = false;
                _glossaryFocus = null;
                _hook.NotifyBrowserChange(false, true);
                Revoke(true);
                TraceObservation(ObservationStage.Lifecycle, ObservationReason.DocumentFocusChanged);
            }
            return;
        }
        if (name == "document-closed")
        {
            if (_commandOwner?.Peer == peer && _commandOwner.Route == route)
                RequestCommandOwnerRefresh();
            if (_active.GetValueOrDefault(peer.ConnectionId) == route)
                _active.Remove(peer.ConnectionId);
            if (_current?.Peer == peer && _current.Route == route)
            {
                _hook.NotifyBrowserChange(false, true);
                Revoke(true);
                TraceObservation(ObservationStage.Lifecycle, ObservationReason.DocumentClosed);
            }
            foreach (var key in _frozen.Where(item => item.Value.Observation.Peer == peer && item.Value.Observation.Route == route).Select(item => item.Key).ToArray())
                _frozen.Remove(key);
            return;
        }
        if (name == "accepted")
        {
            Accepted(peer, route, value);
            return;
        }
        if (name == "publication")
        {
            RememberPublication(peer, route, value);
            return;
        }
        string? target = String(value, "target");
        if (target is null || !route.Contains(target))
            return;
        if (name == "input" || name == "destroy")
        {
            bool focusChanged = Boolean(value, "focusChanged");
            if ((focusChanged || name == "destroy") && _commandOwner is { } owner && owner.Peer == peer && owner.Route == route)
                InvalidateCommandPolicy(owner);
            if (focusChanged && Boolean(value, "focused") && _commandOwner is not { Active: true }
                && _active.GetValueOrDefault(peer.ConnectionId) == route && ProcessFor(ForegroundRoot()) == peer.BrowserProcessId)
                RequestCommandOwnerRefresh(); // A new focused field is only a metadata candidate.
            if (_current?.Peer == peer && _current.Route == route && ForegroundRoot() == _current.Root)
            {
                if (_editorTransition is { } transition && !transition.Completion.Task.IsCompleted
                    && (target != transition.Token.Target.Value || !Boolean(value, "focusChanged") || Boolean(value, "focused")))
                    CancelEditorTransition();
                bool textChanged = Boolean(value, "textChanged") || name == "destroy";
                _hook.NotifyBrowserChange(textChanged, focusChanged);
                _publication = null;
                if (name == "destroy")
                    Revoke(true);
                TraceObservation(ObservationStage.Input, name == "destroy" ? ObservationReason.Destroyed : ObservationReason.InputChanged,
                    textChanged: textChanged, focusChanged: focusChanged);
            }
            return;
        }
        if (_current is not { } current || current.Peer != peer || current.Route != route || current.Target.Value != target
            || !AcknowledgementCurrent(current) || !ForegroundCurrent(current))
            return;
        if (name == "ui")
        {
            Ui?.Invoke(message);
            return;
        }
        if (name == "escape" && _lease is { EscapeAllowed: true } lease
            && String(value, "generation") == _hook.CommandGeneration.ToString()
            && Number(value, "sessionRevision") == lease.SessionRevision)
            _notices.Enqueue((new("escape", AcceptedAtMs: WindowsInterruptClock.NowMs, UserEpoch: _hook.UserEpoch,
                ForegroundEpoch: _hook.ForegroundEpoch, CommandGeneration: _hook.CommandGeneration,
                Target: current.Target, SessionRevision: lease.SessionRevision), 0));
        // Legacy "shortcut" events never enter the Windows command sequence domain.
    }
    private void RememberPublication(BrowserPeer peer, BrowserRoute route, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return;
        if (value.TryGetProperty("publication", out var nested))
            value = nested;
        if (!Guid.TryParse(String(value, "planId"), out var id) || !_issued.TryGetValue(id, out var issued)
            || issued.Observation.Peer != peer || issued.Observation.Route != route)
            return;
        _publication = Boolean(value, "ready") ? new(issued.Observation.Target, id, issued.Lease.CommandGeneration,
            issued.Lease.SessionRevision, Math.Min(Number(value, "readyUntilHostMs"), issued.Permit.ExpiresAtMs)) : null;
    }
    private void Accepted(BrowserPeer peer, BrowserRoute route, JsonElement value)
    {
        if (!Guid.TryParse(String(value, "planId"), out var id) || !_issued.TryGetValue(id, out var issued)
            || issued.Observation.Peer != peer || issued.Observation.Route != route)
            return;
        _issued.Remove(id);
        _publication = null;
        long at = Number(value, "acceptedAtMs");
        var receipt = value.TryGetProperty("receipt", out var raw) ? Receipt(raw, issued.Observation, issued.Permit.Plan) : Unknown();
        if (at < issued.Permit.ObservedAtMs || at > issued.Permit.ExpiresAtMs)
            receipt = Unknown();
        Trace("browser-accept-" + receipt.Status, new
        {
            reason = receipt.Reason
        });
        _notices.Enqueue((new("accept", issued.Permit, at, issued.Lease.UserEpoch, issued.Lease.ForegroundEpoch,
            CommandGeneration: issued.Lease.CommandGeneration, CompletedReceipt: receipt,
            Target: issued.Observation.Target, SessionRevision: issued.Lease.SessionRevision), 0));
        if (receipt.Status == WriteStatus.OutcomeUnknown)
            peer.Send("quarantine", new
            {
                target = issued.Observation.Target.Value
            });
    }
    internal bool BeginEditorTransition(SessionViewToken token)
    {
        CancelEditorTransition();
        if (_current is not { } current || current.Target != token.Target
            || token.CommandGeneration != _hook.CommandGeneration || !ForegroundCurrent(current))
            return false;
        if (_commandOwner is { } owner)
            InvalidateCommandPolicy(owner);
        _editorFocused = false;
        _editorTransition = new(token, WindowsInterruptClock.NowMs + 500,
            new(TaskCreationOptions.RunContinuationsAsynchronously));
        return true;
    }
    internal void CancelEditorTransition()
    {
        _editorTransition?.Completion.TrySetResult(false);
        _editorTransition = null;
        _editorToken = null;
        _editorFocused = false;
        _glossaryEntry = null;
        _glossaryFocus = null;
    }
    internal void EditorFocus(SessionViewToken token, bool focused)
    {
        if (_editorTransition is { } transition)
        {
            if (transition.Token != token)
                return;
            if (!focused || WindowsInterruptClock.NowMs > transition.Deadline)
            {
                CancelEditorTransition();
                return;
            }
            _editorToken = token;
            _editorTransition = null;
            transition.Completion.TrySetResult(true);
        }
        else if (_editorToken is not { } editor || editor.Target != token.Target
            || editor.CommandGeneration != token.CommandGeneration)
            return;
        if (_editorFocused != focused && _commandOwner is { } owner)
            InvalidateCommandPolicy(owner);
        // Only the original editor's actual focus event uses this route. The
        // auxiliary form reports its independent role and never sends true here.
        if (focused)
            _glossaryFocus = null;
        _editorFocused = focused;
        _hook.NotifyBrowserChange(false, false, source: false);
    }
    internal bool OpenGlossaryEntry(SessionGlossaryEntry entry)
    {
        if (_glossaryEntry is not null || entry.FormId == Guid.Empty || _editorToken != entry.Token
            || GetFocus(entry.Token.Target) != FocusMember.Overlay)
            return false;
        _glossaryEntry = entry;
        return true;
    }
    internal void CloseGlossaryEntry(Guid formId)
    {
        if (_glossaryEntry?.FormId != formId)
            return;
        // A user close is sent after the page's actual editor focus event.
        // Clearing the auxiliary role never manufactures or restores that event.
        _glossaryEntry = null;
        _glossaryFocus = null;
    }
    internal void SetGlossaryFocus(SessionGlossaryFocus? focus)
    {
        if (focus is not null && (_glossaryEntry is not { } entry || entry.FormId != focus.FormId
            || entry.Token != focus.Token || !Enum.IsDefined(focus.Ime)
            || _current is not { } current || current.Target != focus.Token.Target || !ForegroundCurrent(current)))
            return;
        if (_glossaryFocus == focus)
            return;
        _glossaryFocus = focus;
        if (focus is not null)
            _editorFocused = false;
        if (_commandOwner is { } owner)
            InvalidateCommandPolicy(owner);
        _hook.NotifyBrowserChange(false, false, source: false);
    }
    private bool GlossaryEvidence(JsonElement data, SessionGlossaryFocus expected)
    {
        if (GlossaryFocus != expected || _glossaryEntry?.FormId != expected.FormId || _glossaryEntry.Token != expected.Token
            || MatchingGlossaryFocus(data, expected) is not { } current)
            return false;
        _glossaryFocus = current;
        return true;
    }
    private static SessionGlossaryFocus? MatchingGlossaryFocus(JsonElement data, SessionGlossaryFocus expected)
    {
        if (!data.TryGetProperty("glossaryFocus", out var raw))
            return null;
        try
        {
            var current = raw.Deserialize<SessionGlossaryFocus>(BrowserServer.Json);
            if (current is null || current.FormId != expected.FormId || current.Token != expected.Token
                || !Enum.IsDefined(current.Ime))
                return null;
            return current;
        }
        catch (JsonException) { return null; }
    }

}
