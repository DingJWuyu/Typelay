using System.Diagnostics;
using TypeLay.Core;
using TypeLay.Protocol;
namespace TypeLay.Runtime;

public sealed class ModelClient : ITranslationService, IDisposable
{
    private sealed class Connection(IChildProcess child, IReadOnlyList<string> directions) : IDisposable
    {
        public IChildProcess Child { get; } = child;
        public IReadOnlyList<string> Directions { get; set; } = directions;
        public long Sequence;
        public int Requests;
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                try
                {
                    Child.Dispose();
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
    private readonly object _owner = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1), _serial = new(1, 1);
    private readonly Func<ProcessStartInfo, IChildProcess> _startProcess;
    private ModelRuntimePaths _paths;
    private Connection? _active, _candidate;
    private CancellationTokenSource? _operation;
    private long _admission;
    private string _status = "未安装本地模型";
    private static readonly IReadOnlyList<string> NoDirections = Array.AsReadOnly(Array.Empty<string>());

    public ModelClient(ModelRuntimePaths paths, Func<ProcessStartInfo, IChildProcess> startProcess)
    {
        _paths = paths;
        _startProcess = startProcess;
    }
    public bool Ready
    {
        get
        {
            lock (_owner)
                return _active is not null;
        }
    }
    public bool Busy
    {
        get
        {
            lock (_owner)
                return _active?.Requests > 0;
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
    public IReadOnlyList<string> AvailableDirections
    {
        get
        {
            lock (_owner)
                return _active?.Directions ?? NoDirections;
        }
    }
    public event Action? Changed;
    // Installation markers allow a startup attempt; only a live hello verifies capability.
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
            if (_active is not null || _operation is not null || !HasMarkers(_paths))
                return;
            starting = BeginReplacement(_paths, () => { }, CancellationToken.None);
        }
        try
        {
            await starting;
        }
        catch (OperationCanceledException) { /* A newer lifecycle operation owns the client. */ }
    }

    // The caller owns its installation lease before entering here. Publication is
    // synchronous and must never acquire a store lock or perform process/UI work.
    // A publication exception leaves the previous live connection unchanged.
    public Task<bool> ReloadAsync(ModelRuntimePaths paths, Action publishInstallation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publishInstallation);
        lock (_owner)
            return BeginReplacement(paths, publishInstallation, ct);
    }
    private Task<bool> BeginReplacement(ModelRuntimePaths paths, Action publish, CancellationToken ct)
    {
        _operation?.Cancel();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _operation = operation;
        ++_admission; // Reject even unsent requests queued before a failed replacement.
        if (_active is null)
            _status = "正在准备翻译";
        return ReplaceAsync(paths, publish, operation);
    }
    private async Task<bool> ReplaceAsync(ModelRuntimePaths paths, Action publish, CancellationTokenSource operation)
    {
        Connection? candidate = null, previous = null;
        bool lifecycle = false, serial = false, committed = false;
        try
        {
            await _lifecycle.WaitAsync(operation.Token);
            lifecycle = true;
            await _serial.WaitAsync(operation.Token);
            serial = true;
            operation.Token.ThrowIfCancellationRequested();
            if (!HasMarkers(paths))
                return false;
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
            try
            {
                lock (_owner)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    candidate = new(_startProcess(info), NoDirections);
                    _candidate = candidate;
                }
                var hello = await ExchangeAsync(candidate, new(0, "hello"), TimeSpan.FromSeconds(120), operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (hello.Status != "ready" || !ModelCatalog.IsValidDirectionSet(hello.Directions))
                    return false;
                candidate.Directions = Array.AsReadOnly(hello.Directions!.ToArray());
            }
            catch (Exception) when (operation.IsCancellationRequested)
            {
                throw new OperationCanceledException(operation.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return false;
            }
            lock (_owner)
            {
                operation.Token.ThrowIfCancellationRequested();
                // No await/cancellation point between durable publication and takeover.
                publish();
                previous = _active;
                _active = candidate;
                _candidate = null;
                _paths = paths;
                _status = "本地翻译已就绪";
                committed = true;
            }
            return true;
        }
        finally
        {
            bool notify = false;
            lock (_owner)
            {
                if (ReferenceEquals(_candidate, candidate))
                    _candidate = null;
                if (ReferenceEquals(_operation, operation))
                {
                    _operation = null;
                    // Preserve the old connection's current health, including an
                    // in-flight transport failure while this operation was waiting.
                    if (_active is null)
                        _status = "模型无法加载（请检查本地组件）";
                    notify = true;
                }
            }
            if (!committed)
                candidate?.Dispose();
            previous?.Dispose();
            if (serial)
                _serial.Release();
            if (lifecycle)
                _lifecycle.Release();
            operation.Dispose();
            if (notify)
                NotifyChanged();
        }
    }
    public Task<ModelResponse> TranslateAsync(TranslationJob job) => TranslateRequestAsync(
        new(0, "translate", job.SourceText, job.Direction.Source, job.Direction.Target, job.PreservedManualText));
    // Editor snapshots are explicit user-approved text, not observed input fields.
    public Task<ModelResponse> TranslateTextAsync(string text, LanguageDirection direction) => TranslateRequestAsync(
        new(0, "translate", text, direction.Source, direction.Target));
    private async Task<ModelResponse> TranslateRequestAsync(ModelRequest request)
    {
        Connection connection;
        long admission;
        lock (_owner)
        {
            if (_active is null || _operation is not null)
                return new(0, "error", Code: "not-ready");
            if (!ModelCatalog.IsSupportedSetting(request.Source, request.Target))
                return new(0, "error", Code: "invalid-request");
            connection = _active;
            admission = _admission;
            // The worker detects same-language text and manual append before it
            // needs a pair. A missing target direction cannot reject Auto here.
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
                return await ExchangeAsync(connection, request, TimeSpan.FromSeconds(90), CancellationToken.None);
            }
            catch
            {
                lock (_owner)
                {
                    if (ReferenceEquals(_active, connection))
                    {
                        _active = null;
                        _status = "本地翻译进程已停止";
                        notify = true;
                    }
                }
                connection.Dispose();
                return new(0, "error", Code: "worker-stopped");
            }
        }
        finally
        {
            lock (_owner)
                --connection.Requests;
            // Notify before allowing a successor to own state; an old finally
            // never writes the successor's status, busy count, or directions.
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        await Wire.WriteAsync(connection.Child.Process.StandardInput.BaseStream, request with
        {
            Id = id
        }, deadline.Token);
        var result = await Wire.ReadAsync<ModelResponse>(connection.Child.Process.StandardOutput.BaseStream, deadline.Token);
        if (result.Id != id)
            throw new IOException("model-sequence");
        return result;
    }
    private void NotifyChanged()
    {
        // Observers do not own the transport or publication transaction. A UI
        // notification failure must not strand a gate or turn a committed switch
        // into an installation failure; other observers still see current state.
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
        Connection? active, candidate;
        lock (_owner)
        {
            ++_admission;
            _operation?.Cancel();
            _operation = null;
            active = _active;
            candidate = _candidate;
            _active = null;
            _candidate = null;
            _status = "本地翻译已停止";
        }
        active?.Dispose();
        candidate?.Dispose();
    }
}
