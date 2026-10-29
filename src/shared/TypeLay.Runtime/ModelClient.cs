using System.Diagnostics;
using System.Runtime.ExceptionServices;
using TypeLay.Core;
using TypeLay.Protocol;
namespace TypeLay.Runtime;

public sealed class ModelClient : ITranslationService, IDisposable
{
    private sealed class Connection(IChildProcess child, ModelLoadPlan plan, long admission)
    {
        public IChildProcess Child { get; } = child;
        public ModelLoadPlan Plan { get; } = plan;
        public long Admission { get; } = admission;
        public StyledTranslationCapability? StyledTranslationCapability
        {
            get; set;
        }
        public GlossaryTranslationCapability? GlossaryTranslationCapability
        {
            get; set;
        }
        public StyledGlossaryTranslationCapability? StyledGlossaryTranslationCapability
        {
            get; set;
        }
        public VerifiedModelIdentity? VerifiedIdentity
        {
            get; set;
        }
        public IReadOnlyList<string> Directions { get; set; } = NoDirections;
        public CancellationTokenSource ExchangeCancellation { get; } = new();
        public long Sequence;
        public int Requests;
    }
    private sealed class Operation(CancellationToken request, Action<ModelReplacementFailure>? replacementFailed) : IDisposable
    {
        public CancellationTokenSource Superseded { get; } = new();
        public Action<ModelReplacementFailure>? ReplacementFailed { get; } = replacementFailed;
        public bool FailureReported;
        private CancellationTokenSource? _linked;
        public CancellationToken Token => (_linked ??= CancellationTokenSource.CreateLinkedTokenSource(request, Superseded.Token)).Token;
        public void Dispose()
        {
            _linked?.Dispose();
            Superseded.Dispose();
        }
    }
    private readonly object _owner = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1), _serial = new(1, 1);
    private readonly Func<ProcessStartInfo, ModelLoadPlan, IChildProcess> _startProcess;
    private readonly HashSet<IChildProcess> _retiring = [];
    private ModelRuntimePaths _paths;
    private Connection? _active, _candidate;
    private Operation? _operation;
    private bool _disposed, _switching, _unconfirmedFactory;
    private long _admission;
    private string _status = "未安装本地模型";
    private string? _failureCode;
    private static readonly IReadOnlyList<string> NoDirections = Array.AsReadOnly(Array.Empty<string>());

    public ModelClient(ModelRuntimePaths paths, Func<ProcessStartInfo, IChildProcess> startProcess)
        : this(paths, (info, _) => startProcess(info))
    {
        ArgumentNullException.ThrowIfNull(startProcess);
    }
    public ModelClient(ModelRuntimePaths paths, Func<ProcessStartInfo, ModelLoadPlan, IChildProcess> startProcess)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(startProcess);
        _paths = paths;
        _startProcess = startProcess;
    }
    public bool Ready
    {
        get
        {
            lock (_owner)
                return !_disposed && _active is not null;
        }
    }
    public bool Busy
    {
        get
        {
            lock (_owner)
                return _switching || _active?.Requests > 0;
        }
    }
    public VerifiedModelIdentity? VerifiedIdentity
    {
        get
        {
            lock (_owner)
            {
                if (_disposed || _switching || _operation is not null || _active is not { } active
                    || active.ExchangeCancellation.IsCancellationRequested)
                    return null;
                try
                {
                    return active.Child.Process.HasExited ? null : active.VerifiedIdentity;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    return null;
                }
            }
        }
    }
    public StyledTranslationCapability? StyledTranslationCapability
    {
        get
        {
            lock (_owner)
            {
                if (_disposed || _switching || _operation is not null || _active is not { } active
                    || active.ExchangeCancellation.IsCancellationRequested || active.StyledTranslationCapability is null)
                    return null;
                try
                {
                    return active.Child.Process.HasExited ? null : active.StyledTranslationCapability;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    return null;
                }
            }
        }
    }
    public GlossaryTranslationCapability? GlossaryTranslationCapability
    {
        get
        {
            lock (_owner)
            {
                if (_disposed || _switching || _operation is not null || _active is not { } active
                    || active.ExchangeCancellation.IsCancellationRequested || active.GlossaryTranslationCapability is null)
                    return null;
                try
                {
                    return active.Child.Process.HasExited ? null : active.GlossaryTranslationCapability;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    return null;
                }
            }
        }
    }
    public StyledGlossaryTranslationCapability? StyledGlossaryTranslationCapability
    {
        get
        {
            lock (_owner)
            {
                if (_disposed || _switching || _operation is not null || _active is not { } active
                    || active.ExchangeCancellation.IsCancellationRequested || active.StyledGlossaryTranslationCapability is null)
                    return null;
                try
                {
                    return active.Child.Process.HasExited ? null : active.StyledGlossaryTranslationCapability;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    return null;
                }
            }
        }
    }
    public string Status
    {
        get
        {
            lock (_owner)
                return _status;
        }
    }
    public string? FailureCode
    {
        get
        {
            lock (_owner)
                return _failureCode;
        }
    }
    public IReadOnlyList<string> AvailableDirections
    {
        get
        {
            lock (_owner)
                return _active?.Directions ?? NoDirections;
        }
    }
    public IReadOnlyList<ModelPackageSelection> AvailablePackages
    {
        get
        {
            lock (_owner)
                return _active?.Plan.ActivePackages ?? Array.Empty<ModelPackageSelection>();
        }
    }
    public event Action? Changed;
    // Markers permit an attempt; the immutable plan and actual hello grant Ready.
    public bool Installed
    {
        get
        {
            lock (_owner)
                return HasMarkers(_paths);
        }
    }
    private static bool HasMarkers(ModelRuntimePaths paths) => File.Exists(paths.Python)
        && File.Exists(Path.Combine(paths.Models, "installed.json"));

    public async Task StartAsync()
    {
        Task<bool> starting;
        lock (_owner)
        {
            if (_disposed || _active is not null || _operation is not null || !HasMarkers(_paths))
                return;
            try
            {
                starting = ReloadAsync(_paths, () => { });
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            {
                _status = "模型无法加载（请检查本地组件）";
                _failureCode = "model-inventory";
                NotifyChanged();
                return;
            }
        }
        try
        {
            if (!await starting)
            {
                bool notify = false;
                lock (_owner)
                {
                    if (!_disposed && _active is null && _operation is null && _status != "模型无法加载（请检查本地组件）")
                    {
                        _status = "模型无法加载（请检查本地组件）";
                        _failureCode = "model-inventory";
                        notify = true;
                    }
                }
                if (notify)
                    NotifyChanged();
            }
        }
        catch (OperationCanceledException) { /* A newer lifecycle owns the client. */ }
    }

    // Compatibility for the original Argos API. Validate the inventory before
    // admission, including rejecting schema3; never infer a package plan from hello.
    public Task<bool> ReloadAsync(ModelRuntimePaths paths, Action publishInstallation, CancellationToken ct = default,
        IEnumerable<string>? expectedDirections = null)
    {
        ArgumentNullException.ThrowIfNull(publishInstallation);
        var expected = expectedDirections?.ToArray();
        if (expected is not null && !ModelCatalog.IsValidDirectionSet(expected))
            throw new ArgumentException("model-directions", nameof(expectedDirections));
        lock (_owner)
            ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasMarkers(paths))
        {
            lock (_owner)
                if (_active is null && _operation is null)
                {
                    _status = "模型无法加载（请检查本地组件）";
                    _failureCode = "model-inventory";
                }
            NotifyChanged();
            return Task.FromResult(false);
        }
        var plan = ModelLoadPlan.ReadLegacy(paths);
        if (expected is not null && !expected.ToHashSet(StringComparer.Ordinal).SetEquals(plan.Directions))
            return Task.FromResult(false);
        return ReloadLegacyAsync(plan, publishInstallation, ct);
    }
    private async Task<bool> ReloadLegacyAsync(ModelLoadPlan plan, Action publish, CancellationToken ct)
    {
        Exception? publicationFailure = null;
        var result = await ReloadAsync(plan, () =>
        {
            try
            {
                publish();
                return ModelPublicationOutcome.Confirmed;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                publicationFailure = error;
                return ModelPublicationOutcome.Unconfirmed;
            }
        }, _ => false, ct);
        if (publicationFailure is not null)
            ExceptionDispatchInfo.Capture(publicationFailure).Throw();
        return result == ModelPublicationOutcome.Confirmed;
    }

    // Caller holds its installation lease. Callbacks are synchronous and
    // must not acquire store/process/UI locks. Unknown publication is never recovery evidence.
    // The optional observer receives only this call's initial failure, before recovery.
    public Task<ModelPublicationOutcome> ReloadAsync(ModelLoadPlan plan, Func<ModelPublicationOutcome> publishConfirmed,
        Func<ModelLoadPlan, bool> confirmCurrent, CancellationToken ct = default,
        Action<ModelReplacementFailure>? replacementFailed = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(publishConfirmed);
        ArgumentNullException.ThrowIfNull(confirmCurrent);
        ct.ThrowIfCancellationRequested();
        lock (_owner)
            ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasMarkers(plan.Paths) || !File.Exists(plan.Paths.Worker))
            return Task.FromResult(ModelPublicationOutcome.Unchanged);
        lock (_owner)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previousOperation = _operation;
            var operation = new Operation(ct, replacementFailed);
            _operation = operation;
            ++_admission;
            if (_active is null)
                _status = "正在准备翻译";
            // Cancellation may synchronously finish the old lifecycle. Revoke
            // its ownership first so its cleanup cannot restore or clear this one.
            previousOperation?.Superseded.Cancel();
            return ReplaceAsync(plan, publishConfirmed, confirmCurrent, operation);
        }
    }
    private bool Owns(Operation operation) => !_disposed && ReferenceEquals(_operation, operation);
    private void RequireOwner(Operation operation)
    {
        operation.Token.ThrowIfCancellationRequested();
        if (!Owns(operation))
            throw new OperationCanceledException(operation.Token);
    }
    private async Task<ModelPublicationOutcome> ReplaceAsync(ModelLoadPlan plan, Func<ModelPublicationOutcome> publish,
        Func<ModelLoadPlan, bool> confirmCurrent, Operation operation)
    {
        Connection? candidate = null, previous = null;
        bool lifecycle = false, serial = false, committed = false, exclusive = false, reapingFailed = false;
        var outcome = ModelPublicationOutcome.Unchanged;
        try
        {
            await _lifecycle.WaitAsync(operation.Token);
            lifecycle = true;
            lock (_owner)
            {
                RequireOwner(operation);
                previous = _active;
                exclusive = plan.UsesNativeLlm || previous?.Plan.UsesNativeLlm == true;
                if (exclusive)
                {
                    _switching = true;
                    _active = null;
                    _status = "正在切换模型";
                    if (previous is not null)
                        Retire(previous);
                }
            }
            // Cancellation precedes waiting for serial: an old blocked read must
            // not hold the switch hostage until its normal inference deadline.
            if (exclusive)
                NotifyChanged();
            await _serial.WaitAsync(operation.Token);
            serial = true;
            if (!await ReapRetiringAsync())
            {
                reapingFailed = true;
                lock (_owner)
                {
                    RequireOwner(operation);
                    RecordReplacementFailure(operation, ModelReplacementPhase.PreviousExit,
                        ModelReplacementFailureCode.ChildCleanupUnconfirmed, outcome);
                }
                return outcome;
            }
            lock (_owner)
            {
                RequireOwner(operation);
                if (!exclusive)
                    previous = _active;
            }
            try
            {
                candidate = await StartConnectionAsync(plan, operation, operation.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Disposing a pipe can surface as an I/O failure even when its
                // owning replacement has already been cancelled or superseded.
                lock (_owner)
                    RequireOwner(operation);
                RecordFailure(operation, error);
                RecordReplacementFailure(operation, ModelReplacementPhase.CandidateStart,
                    ReplacementFailureCode(error), outcome);
                return outcome;
            }
            lock (_owner)
            {
                RequireOwner(operation);
                try
                {
                    outcome = publish();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    outcome = ModelPublicationOutcome.Unconfirmed;
                }
                if (outcome is not (ModelPublicationOutcome.Confirmed or ModelPublicationOutcome.Unchanged))
                {
                    outcome = ModelPublicationOutcome.Unconfirmed;
                    _failureCode = "model-publication-unconfirmed";
                    RecordReplacementFailure(operation, ModelReplacementPhase.Publication,
                        ModelReplacementFailureCode.PublicationUnconfirmed, outcome);
                    if (_active is { } active)
                        Retire(active);
                    _active = null;
                    return outcome;
                }
                if (outcome == ModelPublicationOutcome.Unchanged)
                    RecordReplacementFailure(operation, ModelReplacementPhase.Publication,
                        ModelReplacementFailureCode.PublicationUnchanged, outcome);
                if (outcome == ModelPublicationOutcome.Unchanged || !Owns(operation))
                    return outcome;
                // No cancellation/await between confirmed publication and takeover.
                _active = candidate;
                _candidate = null;
                _paths = plan.Paths;
                _status = "本地翻译已就绪";
                _failureCode = null;
                committed = true;
            }
            return outcome;
        }
        finally
        {
            if (lifecycle)
            {
                lock (_owner)
                {
                    if (!committed && _candidate is { } pending)
                        Retire(pending);
                    if (!committed && candidate is not null)
                        Retire(candidate);
                    if (committed && !exclusive && previous is not null)
                        Retire(previous);
                    if (ReferenceEquals(_candidate, candidate) || !committed)
                        _candidate = null;
                }
                bool reclaimed = !reapingFailed && await ReapRetiringAsync();
                bool restore = false;
                lock (_owner)
                {
                    if (!committed && exclusive && reclaimed && previous is not null
                        && outcome != ModelPublicationOutcome.Unconfirmed && Owns(operation))
                    {
                        try
                        {
                            restore = confirmCurrent(previous.Plan);
                        }
                        catch (Exception error) when (error is not OutOfMemoryException) { }
                    }
                }
                if (restore)
                {
                    // User cancellation still permits bounded recovery; a newer
                    // lifecycle owner or Dispose cancels it independently.
                    using var recovery = CancellationTokenSource.CreateLinkedTokenSource(operation.Superseded.Token);
                    recovery.CancelAfter(TimeSpan.FromSeconds(120));
                    try
                    {
                        if (!serial)
                        {
                            await _serial.WaitAsync(recovery.Token);
                            serial = true;
                        }
                        var restored = await StartConnectionAsync(previous!.Plan, operation, recovery.Token, recovering: true);
                        lock (_owner)
                        {
                            if (!Owns(operation))
                                throw new OperationCanceledException(recovery.Token);
                            _active = restored;
                            _candidate = null;
                            _paths = restored.Plan.Paths;
                            _status = "本地翻译已就绪";
                        }
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        RecordFailure(operation, error);
                        lock (_owner)
                        {
                            if (_candidate is { } failed)
                                Retire(failed);
                            _candidate = null;
                        }
                        await ReapRetiringAsync();
                    }
                }
            }
            bool notify;
            lock (_owner)
            {
                notify = Owns(operation);
                if (notify)
                {
                    _operation = null;
                    _switching = false;
                    if (_active is null)
                        _status = "模型无法加载（请检查本地组件）";
                }
            }
            if (serial)
                _serial.Release();
            if (lifecycle)
                _lifecycle.Release();
            operation.Dispose();
            if (notify)
                NotifyChanged();
        }
    }
    private async Task<Connection> StartConnectionAsync(ModelLoadPlan plan, Operation operation, CancellationToken ct,
        bool recovering = false)
    {
        var paths = plan.Paths;
        if (!HasMarkers(paths))
            throw new InvalidDataException("model-inventory");
        string? inferenceProfile = await IndexInferenceProfile.VerifyAsync(plan, ct);
        var info = new ProcessStartInfo(paths.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-I");
        info.ArgumentList.Add("-B");
        info.ArgumentList.Add(paths.Worker);
        info.ArgumentList.Add("--models");
        info.ArgumentList.Add(paths.Models);
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["OMP_NUM_THREADS"] = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)).ToString();
        Connection connection;
        lock (_owner)
        {
            ct.ThrowIfCancellationRequested();
            if (!Owns(operation) || _unconfirmedFactory || _retiring.Count != 0)
                throw new IOException("child-cleanup-unconfirmed");
            if (!recovering)
                RequireOwner(operation);
            try
            {
                connection = new(_startProcess(info, plan), plan, _admission);
            }
            catch (ChildProcessCleanupUnconfirmedException error)
            {
                if (error.Child is { } child)
                    _retiring.Add(child);
                else
                    _unconfirmedFactory = true;
                throw;
            }
            _candidate = connection;
        }
        var hello = await ExchangeAsync(connection, new(0, "hello"), TimeSpan.FromSeconds(plan.UsesIndex ? 60 : 120), ct);
        ct.ThrowIfCancellationRequested();
        ValidateHello(plan, hello);
        // Identity requires exact packages from this worker connection. A legacy
        // worker may omit attestation and retain ordinary Ready only.
        if (hello.Packages is not null)
            connection.VerifiedIdentity = new(plan, hello.AlternativeTranslationVersion == 1, inferenceProfile);
        connection.Directions = Array.AsReadOnly(hello.Directions!.ToArray());
        AdvancedTranslationScope? scope = hello.StyledTranslationVersion is not null || hello.GlossaryTranslationVersion is not null
            ? new(plan.ActivePackages.SelectMany(item => item.Directions), hello.AdvancedTranslationDirections ?? plan.Directions)
            : null;
        if (hello.StyledTranslationVersion == 1)
            connection.StyledTranslationCapability = new(connection.Admission, scope);
        if (hello.GlossaryTranslationVersion == 1)
            connection.GlossaryTranslationCapability = new(connection.Admission, scope);
        if (hello.StyledGlossaryTranslationVersion == 1)
            connection.StyledGlossaryTranslationCapability = new(connection.Admission, scope);
        return connection;
    }
    private static void ValidateHello(ModelLoadPlan plan, ModelResponse hello)
    {
        if (hello.Status != "ready")
            throw new IOException("model-hello-status");
        if (hello.AlternativeTranslationVersion is { } alternativeVersion && (alternativeVersion != 1 || !plan.UsesIndex))
            throw new IOException("model-hello-capabilities");
        if (hello.StyledTranslationVersion is { } styledVersion && (styledVersion != 1 || !plan.UsesNativeLlm))
            throw new IOException("model-hello-capabilities");
        if (hello.GlossaryTranslationVersion is { } glossaryVersion && (glossaryVersion != 1 || !plan.UsesNativeLlm))
            throw new IOException("model-hello-capabilities");
        if (hello.StyledGlossaryTranslationVersion is { } combinedVersion
            && (combinedVersion != 1 || !plan.UsesNativeLlm || hello.StyledTranslationVersion != 1 || hello.GlossaryTranslationVersion != 1))
            throw new IOException("model-hello-capabilities");
        if (hello.Directions is not { Length: > 0 } directions || directions.Any(string.IsNullOrEmpty)
            || directions.Distinct(StringComparer.Ordinal).Count() != directions.Length
            || !directions.ToHashSet(StringComparer.Ordinal).SetEquals(plan.Directions))
            throw new IOException("model-hello-directions");
        bool advanced = hello.StyledTranslationVersion is not null || hello.GlossaryTranslationVersion is not null
            || hello.StyledGlossaryTranslationVersion is not null;
        if (hello.AdvancedTranslationDirections is { } scope)
        {
            if (!advanced || !plan.UsesNativeLlm || scope.Length == 0 || scope.Any(string.IsNullOrEmpty)
                || scope.Distinct(StringComparer.Ordinal).Count() != scope.Length
                || scope.Any(direction => !plan.Directions.Contains(direction, StringComparer.Ordinal)))
                throw new IOException("model-hello-capabilities");
            // This optional scope cannot grant new native operations. Only the
            // model-specific lock supplies the permitted advanced pairs.
            var native = ModelCatalog.DefaultInstallationDirections(plan.ActivePackages.Single().Package);
            if (scope.Any(direction => !native.Any(item => item.Id == direction)))
                throw new IOException("model-hello-capabilities");
        }
        else if (advanced && plan.UsesHy && plan.Directions.Any(direction => direction is "zh_en" or "en_zh"))
            throw new IOException("model-hello-capabilities");
        if (plan.InventorySchema != 3 && hello.Packages is null)
            return;
        if (hello.Packages is not { Length: > 0 } packages || packages.Length != plan.ActivePackages.Count)
            throw new IOException("model-hello-packages");
        HashSet<string> seen = new(StringComparer.Ordinal), union = new(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var expected = package is null ? null : plan.ActivePackages.FirstOrDefault(item => item.Package.Id == package.Id);
            if (expected is null || !seen.Add(package!.Id) || expected.Package.Version != package.Version
                || package.Directions is not { Length: > 0 } selected || selected.Any(string.IsNullOrEmpty)
                || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length
                || !selected.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Directions.Select(item => item.Id)))
                throw new IOException("model-hello-packages");
            union.UnionWith(selected);
        }
        if (!union.SetEquals(directions))
            throw new IOException("model-hello-directions");
    }
    // Called with owner held. A canceled exchange can never be reused.
    private void Retire(Connection connection)
    {
        connection.ExchangeCancellation.Cancel();
        _retiring.Add(connection.Child);
    }
    private async Task<bool> ReapRetiringAsync()
    {
        IChildProcess[] children;
        lock (_owner)
        {
            if (_disposed || _unconfirmedFactory)
                return false;
            children = _retiring.ToArray();
        }
        foreach (var child in children)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await child.TerminateAndWaitAsync(deadline.Token);
                lock (_owner)
                    _retiring.Remove(child);
                child.Dispose();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                lock (_owner)
                {
                    if (!_disposed)
                        _failureCode = "child-cleanup-unconfirmed";
                }
                return false;
            }
        }
        lock (_owner)
            return !_disposed && !_unconfirmedFactory && _retiring.Count == 0;
    }
    private void RecordFailure(Operation operation, Exception error)
    {
        lock (_owner)
            if (Owns(operation))
                _failureCode = error is ChildProcessCleanupUnconfirmedException
                ? "child-cleanup-unconfirmed"
                : error is IOException && error.Message is "model-hello-packages" or "model-hello-directions" or "model-sequence" or "model-inventory"
                    ? error.Message : "model-start-failed";
    }
    private void RecordReplacementFailure(Operation operation, ModelReplacementPhase phase,
        ModelReplacementFailureCode code, ModelPublicationOutcome outcome)
    {
        lock (_owner)
        {
            if (!Owns(operation) || operation.FailureReported || operation.ReplacementFailed is not { } report)
                return;
            operation.FailureReported = true;
            try
            {
                report(new(phase, code, outcome));
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }
    private static ModelReplacementFailureCode ReplacementFailureCode(Exception error) => error switch
    {
        ChildProcessCleanupUnconfirmedException => ModelReplacementFailureCode.ChildCleanupUnconfirmed,
        IOException { Message: "model-hello-packages" } => ModelReplacementFailureCode.HelloPackages,
        IOException { Message: "model-hello-directions" } => ModelReplacementFailureCode.HelloDirections,
        IOException { Message: "model-sequence" } => ModelReplacementFailureCode.Sequence,
        IOException { Message: "model-inventory" } => ModelReplacementFailureCode.Inventory,
        _ => ModelReplacementFailureCode.StartFailed
    };
    public Task<ModelResponse> TranslateAsync(TranslationJob job) => TranslateAsync(job, null);

    public Task<ModelResponse> TranslateAsync(TranslationJob job, VerifiedModelIdentity? expectedIdentity)
    {
        if (job.Style is { } style && (!Enum.IsDefined(style)
            || job.Kind == TranslationKind.Alternative || job.PreviousCandidate is not null))
            return Task.FromResult(new ModelResponse(0, "error", Code: "invalid-request"));
        ModelGlossary? glossary = job.Glossary?.CouldApply(job.Direction) == true
            ? new(job.Glossary.Version, job.Glossary.Rules.Select(rule => new ModelGlossaryRule(
                rule.SourceTerm, rule.TargetTerm, rule.SourceLanguage, rule.TargetLanguage)).ToArray()) : null;
        if (glossary is not null && (job.Kind == TranslationKind.Alternative || job.PreviousCandidate is not null))
            return Task.FromResult(new ModelResponse(0, "error", Code: "glossary-combination-unavailable"));
        string? wireStyle = job.Style switch
        {
            TranslationStyle.Natural => "natural",
            TranslationStyle.Formal => "formal",
            _ => null
        };
        return TranslateRequestAsync(new(0, glossary is not null && wireStyle is not null ? "translate-styled-glossary-v1"
            : glossary is not null ? "translate-glossary-v1" : wireStyle is not null ? "translate-styled-v1"
            : job.Kind == TranslationKind.Alternative ? "translate-alternative" : "translate",
            job.SourceText, job.Direction.Source, job.Direction.Target, job.PreservedManualText,
            job.PreviousCandidate, wireStyle, glossary), expectedIdentity);
    }
    public Task<ModelResponse> TranslateTextAsync(string text, LanguageDirection direction) => TranslateRequestAsync(
        new(0, "translate", text, direction.Source, direction.Target));
    private async Task<ModelResponse> TranslateRequestAsync(ModelRequest request, VerifiedModelIdentity? expectedIdentity = null)
    {
        Connection connection;
        long admission;
        lock (_owner)
        {
            if (_disposed || _active is null || _operation is not null
                || expectedIdentity is not null && !ReferenceEquals(expectedIdentity, _active.VerifiedIdentity))
                return new(0, "error", Code: "not-ready");
            var direction = new LanguageDirection(request.Source, request.Target);
            if (request.Op == "translate-styled-glossary-v1" && (!_active.Plan.UsesNativeLlm
                || _active.StyledGlossaryTranslationCapability?.Supports(direction) != true))
                return new(0, "error", Code: "glossary-combination-unavailable");
            if (request.Op == "translate-glossary-v1" && (!_active.Plan.UsesNativeLlm
                || _active.GlossaryTranslationCapability?.Supports(direction) != true))
                return new(0, "error", Code: "glossary-unavailable");
            if (request.Op == "translate-styled-v1" && (!_active.Plan.UsesNativeLlm
                || _active.StyledTranslationCapability?.Supports(direction) != true))
                return new(0, "error", Code: "style-unavailable");
            if (request.Op == "translate-alternative" && _active.Plan.UsesNativeLlm
                && _active.VerifiedIdentity?.AlternativeTranslationAvailable != true)
                return new(0, "error", Code: "alternative-unavailable");
            bool valid = _active.Plan.InventorySchema == 3
                ? ModelCatalog.IsSupportedPackageRequest(request.Source, request.Target)
                : ModelCatalog.IsSupportedLegacySetting(request.Source, request.Target);
            if (!valid || request.Op == "translate-alternative"
                && (!WriteSafety.ValidReplacement(request.Text) || !WriteSafety.ValidReplacement(request.PreviousCandidate ?? "")
                    || request.Preserved is not null))
                return new(0, "error", Code: "invalid-request");
            connection = _active;
            admission = _admission;
            ++connection.Requests;
        }
        await _serial.WaitAsync();
        bool notify = false;
        try
        {
            lock (_owner)
                if (!ReferenceEquals(_active, connection) || admission != _admission || _operation is not null)
                    return new(0, "error", Code: "not-ready");
            try
            {
                var response = await ExchangeAsync(connection, request, TimeSpan.FromSeconds(connection.Plan.UsesIndex ? 60 : 90), CancellationToken.None);
                if (response.AdvancedTranslationDirections is not null || response.AlternativeTranslationVersion is not null)
                    throw new IOException("model-translation-response");
                if (request.Op is "translate-alternative" or "translate-styled-v1" or "translate-glossary-v1" or "translate-styled-glossary-v1")
                {
                    lock (_owner)
                        if (!ReferenceEquals(_active, connection) || admission != _admission || _operation is not null)
                            return new(0, "error", Code: "not-ready");
                    bool valid = response.Directions is null && response.Packages is null && response.StyledTranslationVersion is null && response.GlossaryTranslationVersion is null && response.StyledGlossaryTranslationVersion is null
                        && double.IsFinite(response.ElapsedMs) && response.ElapsedMs >= 0
                        && (response.Status switch
                        {
                            "translated" => WriteSafety.ValidReplacement(response.Text) && response.Code == "",
                            "no-alternative" => request.Op == "translate-alternative" && response.Text == "" && response.Code == "",
                            "no-translation" => request.Op is ("translate-styled-v1" or "translate-glossary-v1" or "translate-styled-glossary-v1") && response.Text == "" && response.Code == "",
                            "error" => response.Text == "" && !string.IsNullOrWhiteSpace(response.Code),
                            _ => false
                        });
                    if (!valid)
                        throw new IOException("model-translation-response");
                }
                return response;
            }
            catch
            {
                lock (_owner)
                {
                    if (!ReferenceEquals(_active, connection))
                        return new(0, "error", Code: "not-ready");
                    _active = null;
                    Retire(connection);
                    _status = "本地翻译进程已停止";
                    notify = !_disposed;
                }
                await ReapRetiringAsync();
                return new(0, "error", Code: "worker-stopped");
            }
        }
        finally
        {
            lock (_owner)
                --connection.Requests;
            try
            {
                if (notify)
                    NotifyChanged();
            }
            finally { _serial.Release(); }
        }
    }
    private static async Task<ModelResponse> ExchangeAsync(Connection connection, ModelRequest request, TimeSpan timeout, CancellationToken ct)
    {
        long id = ++connection.Sequence;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.ExchangeCancellation.Token);
        deadline.CancelAfter(timeout);
        var exchange = ExchangeFrameAsync(connection, request with
        {
            Id = id
        }, deadline.Token);
        try
        {
            // A synchronous native pipe may ignore managed cancellation. The same
            // deadline releases the lifecycle gate; callers retire this entire owner.
            return await exchange.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Owned termination closes the peer's pipe ends. Observe any eventual
            // I/O failure without logging or reusing the abandoned connection.
            _ = exchange.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
    private static async Task<ModelResponse> ExchangeFrameAsync(Connection connection, ModelRequest request, CancellationToken ct)
    {
        await Wire.WriteAsync(connection.Child.StandardInput, request, ct);
        var result = await Wire.ReadAsync<ModelResponse>(connection.Child.StandardOutput, ct);
        if (result.Id != request.Id)
            throw new IOException("model-sequence");
        return result;
    }
    private void NotifyChanged()
    {
        if (Changed is not { } changed)
            return;
        foreach (Action observer in changed.GetInvocationList())
            try
            {
                observer();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    public void Dispose()
    {
        IChildProcess[] children;
        lock (_owner)
        {
            if (_disposed)
                return;
            _disposed = true;
            ++_admission;
            _operation?.Superseded.Cancel();
            _operation = null;
            if (_active is { } active)
                Retire(active);
            if (_candidate is { } candidate)
                Retire(candidate);
            _active = null;
            _candidate = null;
            _switching = false;
            _status = "本地翻译已停止";
            children = _retiring.ToArray();
        }
        foreach (var child in children)
            try
            {
                child.Dispose();
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
