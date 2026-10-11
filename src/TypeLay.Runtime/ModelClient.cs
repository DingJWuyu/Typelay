using System.Diagnostics;
using TypeLay.Core;
using TypeLay.Protocol;
namespace TypeLay.Runtime;

public sealed class ModelClient : ITranslationService, IDisposable
{
    private IChildProcess? _child;
    private readonly ModelRuntimePaths _paths;
    private readonly Func<ProcessStartInfo, IChildProcess> _startProcess;
    public ModelClient(ModelRuntimePaths paths, Func<ProcessStartInfo, IChildProcess> startProcess)
    {
        _paths = paths;
        _startProcess = startProcess;
    }
    private readonly SemaphoreSlim _serial = new(1, 1);
    private long _id;
    public bool Ready
    {
        get; private set;
    }
    public bool Busy
    {
        get; private set;
    }
    public string Status { get; private set; } = "未安装本地模型";
    public event Action? Changed;
    public bool Installed => File.Exists(_paths.Python) && File.Exists(Path.Combine(_paths.Models, "installed.json"));
    public async Task StartAsync()
    {
        if (_child is not null || !Installed)
            return;
        Status = "正在准备翻译";
        Changed?.Invoke();
        var info = new ProcessStartInfo(_paths.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-I");
        info.ArgumentList.Add("-B");
        info.ArgumentList.Add(_paths.Worker);
        info.ArgumentList.Add("--models");
        info.ArgumentList.Add(_paths.Models);
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["OMP_NUM_THREADS"] = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)).ToString();
        try
        {
            _child = _startProcess(info);
            var response = await ExchangeAsync(new(0, "hello"), TimeSpan.FromSeconds(120));
            Ready = response.Status == "ready";
            Status = Ready ? "本地翻译已就绪" : "模型无法加载（请检查本地组件）";
            if (!Ready)
                Stop();
        }
        catch { Stop(); Status = "模型无法加载（请检查本地组件）"; }
        Changed?.Invoke();
    }
    public async Task<ModelResponse> TranslateAsync(TranslationJob job)
    {
        if (!Ready)
            return new(0, "error", Code: "not-ready");
        Busy = true;
        try
        {
            return await ExchangeAsync(new(0, "translate", job.SourceText, job.Direction.Source, job.Direction.Target,
                    job.PreservedManualText), TimeSpan.FromSeconds(90));
        }
        catch { Stop(); Status = "本地翻译进程已停止"; Changed?.Invoke(); return new(0, "error", Code: "worker-stopped"); }
        finally { Busy = false; }
    }
    private async Task<ModelResponse> ExchangeAsync(ModelRequest request, TimeSpan timeout)
    {
        await _serial.WaitAsync();
        try
        {
            var child = _child ?? throw new IOException("model-offline");
            long id = Interlocked.Increment(ref _id);
            using var ct = new CancellationTokenSource(timeout);
            await Wire.WriteAsync(child.Process.StandardInput.BaseStream, request with
            {
                Id = id
            }, ct.Token);
            var result = await Wire.ReadAsync<ModelResponse>(child.Process.StandardOutput.BaseStream, ct.Token);
            if (result.Id != id)
                throw new IOException("model-sequence");
            return result;
        }
        finally { _serial.Release(); }
    }
    private void Stop()
    {
        Ready = false;
        Interlocked.Exchange(ref _child, null)?.Dispose();
    }
    public void Dispose() => Stop();
}
