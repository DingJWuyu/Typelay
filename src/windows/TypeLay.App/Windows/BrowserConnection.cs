using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

// Optional published release identity, never a page claim or environment override.
internal sealed record BrowserConnectionConfiguration(string? ExtensionId, string? StoreUrl)
{
    public static BrowserConnectionConfiguration Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var value = document.RootElement;
            if (json.Length > 4096 || value.ValueKind != JsonValueKind.Object
                || !value.EnumerateObject().Select(item => item.Name).Order().SequenceEqual(new[] { "extensionId", "storeUrl", "version" })
                || value.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("browser-connection-configuration");
            string? id = value.GetProperty("extensionId").GetString(), url = value.GetProperty("storeUrl").GetString();
            if (id is not null && !BrowserHostProtocol.IsExtensionId(id))
                throw new InvalidDataException("browser-connection-identity");
            if (url is not null)
            {
                const string prefix = "https://chromewebstore.google.com/detail/";
                string[] parts = url.StartsWith(prefix, StringComparison.Ordinal) ? url[prefix.Length..].Split('/') : [];
                if (id is null || parts.Length != 2 || parts[1] != id || parts[0].Length == 0
                    || parts[0].Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
                    throw new InvalidDataException("browser-store-identity");
            }
            return new(id, url);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("browser-connection-configuration", error);
        }
    }
}

// All operations run on the App dispatcher. Closing Settings does not cancel an
// already confirmed registration; Quit awaits this bounded single-flight task.
internal sealed class BrowserConnection
{
    private readonly BrowserInput _input;
    private readonly string _host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TypeLay.BrowserHost.exe"));
    private readonly BrowserConnectionConfiguration? _configuration;
    private Task _pending = Task.CompletedTask;
    private bool _busy, _mutationLocked;
    private string? _origin, _error, _discoveredExtensionId;
    private bool _known;
    private ApplicationSupportCapabilities _lastCapabilities = new();
    private bool _lastConnected;
    public event Action? Changed;
    public bool Busy => _busy || _mutationLocked;
    public bool MutationUnavailable => _mutationLocked;
    public bool Suspended => _origin is not null && _input.OriginSuspended(_origin);
    public bool CanConnect => _known && !_mutationLocked && ExtensionId is not null
        && (_origin is null || _origin == BrowserHostProtocol.OriginForExtensionId(ExtensionId) && Suspended)
        && (!_input.Connected || Suspended);
    public bool CanRemove => _known && !_mutationLocked && _origin is not null;
    public string? ExtensionId => _configuration?.ExtensionId ?? _discoveredExtensionId;
    public string ExtensionDirectory { get; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "browser", "chromium"));
    public string? StoreUrl => _configuration?.StoreUrl;
    public string? RegisteredOrigin => _origin;
    public string HostPath => _host;
    public BrowserConnection(BrowserInput input)
    {
        _input = input;
        try
        {
            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "browser", "chromium", "connection.json"));
            if (new FileInfo(path).Length > 4096)
                throw new InvalidDataException();
            _configuration = BrowserConnectionConfiguration.Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _error = "发行连接配置不可用，请重新解压完整安装包。";
        }
    }
    public void Poll()
    {
        var capabilities = _input.SupportCapabilities;
        bool connected = _input.Connected;
        if (capabilities != _lastCapabilities || connected != _lastConnected)
        {
            _lastCapabilities = capabilities;
            _lastConnected = connected;
            Changed?.Invoke();
        }
    }
    public Task CompletePendingAsync() => _pending;
    public Task RefreshAsync() => StartAsync(null, null);
    public Task ConnectAsync(string confirmedId) => StartAsync("register", confirmedId);
    public Task RemoveAsync(string confirmedOrigin) => StartAsync("unregister", confirmedOrigin);
    private Task StartAsync(string? action, string? confirmed)
    {
        if (Busy)
            return _pending;
        // Recheck after the user confirmation. A changed/ambiguous installation
        // must not register the identity from an older settings view.
        if (_configuration is { ExtensionId: null })
            _discoveredExtensionId = BrowserExtensionInstallation.FindUnique(ExtensionDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data"));
        if (action == "register" && (!CanConnect || confirmed != ExtensionId))
        {
            _error = "扩展安装或连接状态已变化。请刷新状态后重新确认接入。";
            Changed?.Invoke();
            return Task.CompletedTask;
        }
        if (action == "unregister" && (!CanRemove || confirmed != _origin))
            return Task.CompletedTask;
        _busy = true;
        if (action == "unregister")
            _input.SuspendOrigin(confirmed!);
        _pending = RunAsync(action, confirmed);
        Changed?.Invoke();
        return _pending;
    }
    private async Task RunAsync(string? action, string? confirmed)
    {
        string? failure = null;
        try
        {
            if (action is not null)
            {
                string id = action == "register" ? confirmed! : confirmed!["chrome-extension://".Length..^1];
                var result = await RunHostAsync([action, "--extension-id", id]);
                if (result.Code != 0)
                    failure = ErrorText(result.Error);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            failure = "操作未完成，请检查连接状态后重试。";
        }
        try
        {
            if (_mutationLocked)
                throw new IOException();
            var status = await RunHostAsync(["status"]);
            if (status.Code != 0)
                throw new IOException(ErrorText(status.Error));
            using var document = JsonDocument.Parse(status.Output);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(item => item.Name).Order()
                    .SequenceEqual(new[] { "origin", "registered" }))
                throw new InvalidDataException();
            string? origin = value.GetProperty("origin").GetString();
            if (value.GetProperty("registered").GetBoolean() != (origin is not null)
                || origin is not null && !BrowserHostProtocol.IsOrigin(origin))
                throw new InvalidDataException();
            _known = true;
            _origin = origin;
            if (action == "register" && failure is null && origin == BrowserHostProtocol.OriginForExtensionId(confirmed!))
                _input.ResumeOrigin(origin);
            else if (action == "register" && failure is null)
                failure = "接入尚未确认，请刷新后重试。";
            if (action == "unregister" && origin is not null && failure is null)
                failure = "尚未移除，请重试。当前连接已停止。";
            _error = failure;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or OperationCanceledException or JsonException or KeyNotFoundException)
        {
            _known = false;
            _origin = null;
            _error = _mutationLocked
                ? "无法确认连接进程已退出，本次运行不能继续操作。请退出并重新启动 TypeLay 核对状态。系统注册可能仍在，重新启动后可能重新连接。"
                : "无法确认注册状态，系统注册可能仍在。请刷新后重试；重新启动 TypeLay 后可能重新连接。其它安装的注册不会被覆盖。";
        }
        finally
        {
            _busy = false;
            Changed?.Invoke();
        }
    }
    private static string ErrorText(string error) => error.Trim() == "browser-host-command-failed:registration-conflict"
        ? "已有不同安装或扩展的注册，请先由原安装移除。" : "操作未完成，请检查连接状态后重试。";
    private async Task<(int Code, string Output, string Error)> RunHostAsync(string[] arguments)
    {
        var start = new ProcessStartInfo(_host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new IOException();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var error = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            deadline.Cancel();
            bool exited = false;
            try
            {
                exited = process.HasExited;
                if (!exited)
                {
                    process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    exited = process.HasExited;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                // No assumption of successful termination may unlock a later
                // mutation while this process can still finish registration.
            }
            if (!exited)
                _mutationLocked = true;
            try
            {
                await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException) { }
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellation)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
        {
            if (result.Length + count > 16 * 1024)
                throw new InvalidDataException();
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
    public ApplicationSupportFact Read(bool detected)
    {
        bool connected = _input.Connected;
        var actions = new List<ApplicationSupportAction>();
        if (CanConnect)
            actions.Add(ApplicationSupportAction.Connect);
        if (CanRemove)
            actions.Add(ApplicationSupportAction.Remove);
        actions.Add(ApplicationSupportAction.Refresh);
        actions.Add(ApplicationSupportAction.OpenInstructions);
        bool suspended = _origin is not null && _input.OriginSuspended(_origin);
        string details = _configuration is null ? "发行连接配置不可用，请重新解压完整安装包。"
            : StoreUrl is null ? "此发行版仅提供手动开发加载，不是商店安装。\n"
                + "1. 复制下方管理地址，在 Chrome 地址栏粘贴打开；开启开发者模式，选择加载已解压的扩展程序。\n"
                + "2. 选择下方本包组件目录，在浏览器确认网页访问权限。不要移动或删除此目录。\n"
                + "3. 保存工作，正常退出并重新打开 Chrome；回到此页刷新状态，再确认接入。无需填写扩展 ID。\n"
                + "4. 保存网页工作后刷新普通 HTTP(S) 网页，在文本框输入并检查实际连接。\n"
                + (ExtensionId is null ? "尚未找到与本包目录唯一匹配的已加载扩展。只检查标准 Chrome 配置；缺少或多条身份记录时不会注册。\n" : "已识别本包的扩展，仍须确认本地接入。\n")
                + "移除连接只撤销 TypeLay 的本地注册；扩展须在 Chrome 中移除。升级或搬迁后先移除旧连接和旧扩展，再加载新目录。浏览器策略禁用开发加载时，此路线不可用。"
            : "先从 Chrome 网上应用店安装扩展并完成 Chrome 的确认，再在此接入。";
        if (_origin is not null && ExtensionId is not null && _origin != BrowserHostProtocol.OriginForExtensionId(ExtensionId))
            details += " 当前注册属于先前扩展。请先移除此连接，再接入当前发行版。";
        if (suspended)
            details += " 本次运行的连接已停止，注册仍在。可重试移除；恢复需要重新确认接入。重新启动 TypeLay 后可能重新连接。";
        details += " 注册仅建立本用户连接，不代表当前网页可翻译。密码、敏感字段与不支持的编辑器保持安静。";
        if (_error is not null)
            details += " " + _error;
        var state = connected ? ApplicationSupportState.Connected : _origin is not null ? ApplicationSupportState.Registered
            : !_known && _error is not null ? ApplicationSupportState.Error : ApplicationSupportState.NeedsInstall;
        var reason = suspended ? ApplicationSupportReason.ActionFailed : connected ? ApplicationSupportReason.LimitedFields
            : _origin is not null ? ApplicationSupportReason.AwaitingConnection : ApplicationSupportReason.RegistrationMissing;
        return new("google-chrome", state, reason, connected ? _input.SupportCapabilities : new(), actions.ToArray(), details,
            ApplicationDetected: detected || connected || _origin is not null);
    }
}
