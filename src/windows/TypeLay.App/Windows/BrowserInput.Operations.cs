using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

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
        else if (_editorFocused)
            rejection = ObservationReason.EditorFocused;
        if (rejection != ObservationReason.Ready)
        {
            TraceObservation(ObservationStage.Shortcut, rejection);
            return;
        }
        TraceObservation(ObservationStage.Shortcut, ObservationReason.Ready);
    }
    private sealed class CommandOwner(BrowserPeer peer, BrowserRoute route, nint root, long windowEpoch, Guid generation, long startedAt)
    {
        public BrowserPeer Peer { get; } = peer;
        public BrowserRoute Route { get; } = route;
        public nint Root { get; } = root;
        public long WindowEpoch { get; } = windowEpoch;
        public Guid Generation { get; } = generation;
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
    internal string? CommandStatus => _registrationDeferred || OwnsForeground
        ? _commandOwner is { Active: true, ReadyPolicy: { } policy } owner
            && owner.RetiredAt == long.MaxValue && OwnerForeground(owner) && policy.ExpiresAt >= WindowsInterruptClock.NowMs
            ? "浏览器快捷键已就绪。"
            : "浏览器快捷键尚未就绪，请返回受支持的输入框。"
        : null;
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
    private bool OwnerForeground(CommandOwner owner) => owner.Peer.Alive && owner.Generation == _hook.CommandGeneration
        && owner.WindowEpoch == _hook.WindowEpoch && owner.Root != 0 && owner.Root == ForegroundRoot()
        && _active.GetValueOrDefault(owner.Peer.ConnectionId) == owner.Route;
    private void RetireCommandOwner()
    {
        if (_commandOwner is not { } owner)
            return;
        owner.RetiredAt = WindowsInterruptClock.NowMs;
        owner.Active = false;
        owner.ReadyPolicy = owner.PendingPolicy = null;
        _commandOwner = null;
        _retiredCommandOwners.Add(owner);
        if (owner.MaxIssuedExpiry != 0 && owner.Peer.Alive)
            owner.Peer.Send("deactivate-command-owner", new
            {
                documentToken = owner.Route.DocumentToken,
                ownerId = owner.Id,
                generation = owner.Generation,
                grantSequence = owner.GrantSequence
            });
        else if (owner.MaxIssuedExpiry == 0)
            owner.RetirementAcknowledged = true;
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
            if (!OwnerForeground(current) || !_commandsEnabled || !_sessionEnabled
                || !current.Active && now > current.EstablishUntil
                || current.Active && (now > current.MaxIssuedExpiry || current.PendingGrant != 0 && now - current.LastGrantAt > 200))
                RetireCommandOwner();
            else if (current.Prepared && !current.Active && current.MaxIssuedExpiry == 0 && _hook.CommandsReleased)
                ActivateCommandOwner(current, now);
            else if (current.Active && current.PendingGrant == 0 && now - current.LastGrantAt >= 200)
                SendOwnerGrant(current, false, now);
        }
        if (_commandOwner is not null)
            return;
        bool safelyRetired = _retiredCommandOwners.All(owner => owner.RetirementAcknowledged || now > owner.MaxIssuedExpiry);
        if (!safelyRetired || !_hook.CommandsReleased)
            return;
        if (_ownerRefreshRequested && _commandsEnabled && _sessionEnabled && _commandExcluded is not null
            && _retiredCommandOwners.Count < 4 && _wake is not null && _direct is not null)
        {
            var peer = ForegroundPeer(out _);
            if (peer is not null && _active.TryGetValue(peer.ConnectionId, out var route))
            {
                _ownerRefreshRequested = false;
                var owner = new CommandOwner(peer, route, ForegroundRoot(), _hook.WindowEpoch, _hook.CommandGeneration, now);
                owner.Wake = _wake;
                owner.Direct = _direct;
                _commandOwner = owner;
                _ = PrepareCommandOwnerAsync(owner);
                return;
            }
        }
        if (_registrationDeferred && !_restorationAttempted)
        {
            _restorationAttempted = true;
            if (_hook.RestoreCommandRegistration())
                _registrationDeferred = false;
        }
    }
    private async Task PrepareCommandOwnerAsync(CommandOwner owner)
    {
        var reply = await owner.Peer.RequestAsync("prepare-command-owner", new
        {
            documentToken = owner.Route.DocumentToken,
            ownerId = owner.Id,
            generation = owner.Generation
        }, (int)Math.Max(1, owner.EstablishUntil - WindowsInterruptClock.NowMs));
        if (_commandOwner != owner)
            return;
        if (!OwnerForeground(owner) || WindowsInterruptClock.NowMs > owner.EstablishUntil
            || reply?.Route != owner.Route || String(reply.Data, "ownerId") != owner.Id
            || Number(reply.Data, "commandLocalVersion") != 1
            || !CommandKeyMatches(reply.Data, "wake", _wake) || !CommandKeyMatches(reply.Data, "direct", _direct)
            || !Approved(owner.Peer, reply, _commandExcluded!, out _, out _, out _, out _)
            || !Boolean(reply.Data, "focused"))
        {
            RetireCommandOwner();
            return;
        }
        owner.Preparation = reply;
        owner.Prepared = true;
        if (_hook.CommandsReleased)
            ActivateCommandOwner(owner, WindowsInterruptClock.NowMs);
    }
    private void ActivateCommandOwner(CommandOwner owner, long now)
    {
        if (owner.Preparation is not { } reply || !OwnerForeground(owner) || now > owner.EstablishUntil
            || !_hook.CommandsReleased)
        {
            RetireCommandOwner();
            return;
        }
        var authority = reply.Data.GetProperty("authority").Clone();
        long expiry = Number(authority, "observedAtMs") + Limits.ObservationMaxAgeMs;
        if (now > expiry)
        {
            RetireCommandOwner();
            return;
        }
        var target = new TargetId("windows-browser", String(authority, "target")!, owner.Peer.ConnectionId);
        bool suspended = _hook.SuspendCommandRegistration();
        _hook.DeferCommandRegistration();
        _registrationDeferred = true;
        _restorationAttempted = false;
        if (!suspended)
        {
            RetireCommandOwner();
            return;
        }
        // A native press racing the release remains owned until its real up.
        if (!_hook.CommandsReleased)
            return;
        owner.PendingPolicy = new(target, authority, _commandExcluded!.ToArray(), expiry);
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
                releasedAtMs = _hook.CommandsReleasedAt
            });
        }
        else
            owner.Peer.Send("renew-command-owner", new
            {
                documentToken = owner.Route.DocumentToken,
                ownerId = owner.Id,
                generation = owner.Generation,
                grantSequence = sequence,
                expiresAtMs = expiry
            });
    }
    private void OwnerAcknowledged(BrowserPeer peer, BrowserRoute route, JsonElement data)
    {
        string? id = String(data, "ownerId");
        var owner = _retiredCommandOwners.Append(_commandOwner).OfType<CommandOwner>()
            .FirstOrDefault(item => item.Peer == peer && item.Route == route && item.Id == id);
        if (owner is null || String(data, "generation") != owner.Generation.ToString()
            || !CommandKeyMatches(data, "wake", owner.Wake) || !CommandKeyMatches(data, "direct", owner.Direct))
            return;
        long sequence = Number(data, "grantSequence");
        if (sequence != owner.GrantSequence)
            return;
        if (String(data, "state") == "retired")
        {
            owner.RetirementAcknowledged = true;
            if (_commandOwner == owner)
                RetireCommandOwner();
            return;
        }
        if (_commandOwner != owner || owner.RetiredAt != long.MaxValue)
            return;
        long now = WindowsInterruptClock.NowMs;
        if (String(data, "state") != "active" || sequence != owner.PendingGrant
            || now - owner.LastGrantAt > 200 || !owner.EverActive && now > owner.EstablishUntil
            || !owner.Grants.TryGetValue(sequence, out long expiry) || Number(data, "expiresAtMs") != expiry
            || now > expiry || !OwnerForeground(owner))
        {
            RetireCommandOwner();
            return;
        }
        var policy = owner.Policies.FindLast(item => item.Token == String(data, "policyToken") && item.Target.Value == String(data, "target"));
        if (!owner.EverActive && (policy is null || owner.PendingPolicy != policy))
        {
            RetireCommandOwner();
            return;
        }
        owner.Active = true;
        owner.EverActive = true;
        owner.PendingGrant = 0;
        if (policy is not null && owner.PendingPolicy == policy)
            owner.ReadyPolicy = policy;
    }
    private void ApproveCommandPolicy(BrowserPeer peer, BrowserReply description, TargetId target, JsonElement authority, string[] excluded)
    {
        if (_commandOwner is not { Active: true } owner || owner.Peer != peer || owner.Route != description.Route
            || !OwnerForeground(owner) || !Boolean(description.Data, "focused") || owner.Policies.Count >= 16)
            return;
        long expiry = Number(authority, "observedAtMs") + Limits.ObservationMaxAgeMs;
        if (WindowsInterruptClock.NowMs > expiry)
            return;
        var policy = new CommandPolicy(target, authority.Clone(), excluded.ToArray(), expiry);
        if (owner.Policies.Any(item => item.Token == policy.Token))
            return;
        owner.Policies.Add(policy);
        owner.PendingPolicy = policy;
        _ = ApproveCommandPolicyAsync(owner, policy);
    }
    private async Task ApproveCommandPolicyAsync(CommandOwner owner, CommandPolicy policy)
    {
        var reply = await owner.Peer.RequestAsync("approve-command-policy", new
        {
            target = policy.Target.Value,
            ownerId = owner.Id,
            generation = owner.Generation,
            authority = policy.Authority
        }, (int)Math.Max(1, policy.ExpiresAt - WindowsInterruptClock.NowMs));
        if (_commandOwner == owner && owner.PendingPolicy == policy && OwnerForeground(owner) && reply?.Route == owner.Route
            && Boolean(reply.Data, "applied") && String(reply.Data, "ownerId") == owner.Id
            && String(reply.Data, "generation") == owner.Generation.ToString()
            && String(reply.Data, "target") == policy.Target.Value && String(reply.Data, "policyToken") == policy.Token
            && CommandKeyMatches(reply.Data, "wake", _wake) && CommandKeyMatches(reply.Data, "direct", _direct)
            && WindowsInterruptClock.NowMs <= policy.ExpiresAt)
            owner.ReadyPolicy = policy;
    }
    private void SourceCommand(BrowserPeer peer, BrowserRoute route, JsonElement data)
    {
        var owner = _retiredCommandOwners.Append(_commandOwner).OfType<CommandOwner>().FirstOrDefault(item =>
            item.Peer == peer && item.Route == route && item.Id == String(data, "ownerId"));
        long commandId = Number(data, "commandId"), t0 = Number(data, "triggeredAtMs"), now = WindowsInterruptClock.NowMs;
        string? kind = String(data, "kind");
        if (owner is null || !owner.EverActive || owner.Generation != _hook.CommandGeneration || String(data, "generation") != owner.Generation.ToString()
            || kind is not ("wake" or "direct") || commandId is <= 0 or > 9007199254740991 || commandId <= owner.LastCommandId
            || !owner.Grants.TryGetValue(Number(data, "ownerGrantSequence"), out long ownerExpiry)
            || t0 < 0 || now < t0 || now - t0 > Limits.ObservationMaxAgeMs)
            return;
        owner.LastCommandId = commandId;
        if (_notices.Count >= 16)
            return;
        long sequence = _hook.NextCommandSequence();
        void Reject(Denial reason) => _notices.Enqueue((new(kind, AcceptedAtMs: t0,
            CommandGeneration: owner.Generation, Rejection: reason, CommandSequence: sequence), t0 + Limits.ObservationMaxAgeMs));
        if (!data.TryGetProperty("native", out var native))
        {
            Reject(String(data, "reason") == "ImeUnclear" ? Denial.ImeUnclear : Denial.StaleObservation);
            return;
        }
        var policy = owner.Policies.FindLast(item => item.Token == String(data, "policyToken"));
        long capturedAt = Number(data, "capturedAtMs"), upper = Number(data, "captureUpperMs");
        if (policy is null || upper < capturedAt || upper > t0 + Limits.ObservationMaxAgeMs
            || upper > ownerExpiry || upper > policy.ExpiresAt || upper > owner.RetiredAt
            || !Approved(peer, new(native, route), policy.Excluded, out var authority, out var metadata, out var target, out var ack)
            || target != policy.Target || !native.TryGetProperty("sessionBindings", out var applied)
            || String(applied, "target") != policy.Target.Value
            || String(authority, "metadataKey") != String(policy.Authority, "metadataKey")
            || !Boolean(native, "focused"))
        {
            Reject(Denial.StaleObservation);
            return;
        }
        var source = Snapshot(native, target, metadata!);
        var evidence = new CommandCaptureEvidence(target, t0, capturedAt);
        if (source is null || evidence.Check(source, now) != Denial.None || source.Ime != ImeState.Clear || !source.DirectWriteSupported)
        {
            Reject(Denial.StaleObservation);
            return;
        }
        if (kind == "direct" && _frozen.Count >= 4)
        {
            Reject(Denial.PlatformFailure);
            return;
        }
        var epochs = _hook.BrowserEpochs;
        var record = new BrowserObservation(peer, route, authority, target, capturedAt, owner.Root, epochs, ack!);
        _observations.Remember(record);
        if (OwnerForeground(owner))
            _current = record;
        if (kind == "direct")
            _frozen.TryAdd((target, capturedAt), new(record, owner.Generation, sequence, t0 + 212000));
        _notices.Enqueue((new(kind, AcceptedAtMs: t0, UserEpoch: epochs.User, ForegroundEpoch: epochs.Foreground,
            Source: source, CommandGeneration: owner.Generation, CommandSequence: sequence, CommandCapture: evidence),
            t0 + Limits.ObservationMaxAgeMs));
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
        var description = await original.Peer.RequestAsync("describe", new
        {
            target = original.Target.Value
        });
        if (!Approved(original.Peer, description, excluded, out var authority, out _, out var target, out _)
            || target != original.Target || description!.Route != original.Route
            || token.CommandGeneration != _hook.CommandGeneration || !ForegroundCurrent(original)
            || GetFocus(original.Target) != FocusMember.Overlay
            || String(authority, "metadataKey") != String(original.Authority, "metadataKey")
            || Number(authority, "contentRevision") != Number(original.Authority, "contentRevision")
            || Number(authority, "inputRevision") != Number(original.Authority, "inputRevision"))
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
            if (focusChanged && _commandOwner is { } owner && owner.Peer == peer && owner.Route == route)
                owner.ReadyPolicy = owner.PendingPolicy = null;
            if (focusChanged && Boolean(value, "focused") && _commandOwner is not { Active: true }
                && _active.GetValueOrDefault(peer.ConnectionId) == route && ProcessFor(ForegroundRoot()) == peer.BrowserProcessId)
                RequestCommandOwnerRefresh(); // A new focused field is only a metadata candidate.
            if (_current?.Peer == peer && _current.Route == route && ForegroundRoot() == _current.Root)
            {
                if (_editorTransition is { } transition && !transition.Completion.Task.IsCompleted
                    && (target != transition.Token.Target.Value || !Boolean(value, "focusChanged") || Boolean(value, "focused")))
                    CancelEditorTransition();
                bool textChanged = Boolean(value, "textChanged") || name == "destroy";
                if (name == "destroy" && _commandOwner is { } destroyedOwner
                    && destroyedOwner.Peer == peer && destroyedOwner.Route == route)
                    destroyedOwner.ReadyPolicy = destroyedOwner.PendingPolicy = null;
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
        _editorFocused = focused;
        _hook.NotifyBrowserChange(false, false, source: false);
    }
}
