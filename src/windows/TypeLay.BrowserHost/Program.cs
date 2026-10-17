using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using TypeLay.Protocol;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.BrowserHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        bool native = args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.Ordinal);
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException("windows-required");
            string executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TypeLay.BrowserHost.exe"));
            // GetModuleFileName can preserve the launcher's 8.3 spelling. Compare the
            // same normalization used for the installed path and pipe-peer image paths.
            if (Environment.ProcessPath is not { } processPath
                || !string.Equals(Path.GetFullPath(processPath), executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("native-apphost-required");
            var registration = new BrowserRegistration(executable);
            if (native)
                return await RelayAsync(args, registration);
            if (args is ["register", "--extension-id", var extensionId])
            {
                registration.Register(extensionId);
                Console.WriteLine("Browser host registered for the current user.");
            }
            else if (args is ["unregister"])
            {
                registration.Unregister();
                Console.WriteLine("Browser host unregistered for the current user.");
            }
            else if (args is ["status"])
            {
                string? origin = registration.RegisteredOrigin();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    registered = origin is not null,
                    origin
                }, Wire.Json));
            }
            else if (args is ["--help"])
                Console.WriteLine("TypeLay.BrowserHost.exe register --extension-id <Chrome extension ID> | unregister | status");
            else
                throw new ArgumentException("arguments");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Never print exception messages, page data or paths into Chrome's protocol/logs.
            Console.Error.WriteLine(native ? "browser-host-unavailable" : "browser-host-command-failed:" + ErrorCode(error));
            return 2;
        }
    }

    private static string ErrorCode(Exception error) => error switch
    {
        RegistrationConflictException => "registration-conflict",
        ArgumentException => "invalid-arguments",
        UnauthorizedAccessException => "access-denied",
        IOException => "file-or-registry-access",
        _ => "unavailable"
    };

    private static async Task<int> RelayAsync(string[] args, BrowserRegistration registration)
    {
        if (args.Length != 2 || !BrowserHostProtocol.IsOrigin(args[0])
            || !args[1].StartsWith("--parent-window=", StringComparison.Ordinal)
            || !ulong.TryParse(args[1][16..], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || registration.RegisteredOrigin() != args[0])
            throw new ArgumentException("native-origin");
        // In MV3 this value is normally zero. It never identifies a field or grants access.
        using var identity = WindowsIdentity.GetCurrent();
        using var currentProcess = Process.GetCurrentProcess();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        string name = BrowserHostProtocol.PipeName(identity.User?.Value ?? throw new UnauthorizedAccessException(),
            currentProcess.SessionId, root);
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(handshake.Token);
        using var peer = BrowserPeerIdentity.AuthenticateServer(pipe, Path.Combine(root, "app", "TypeLay.App.exe"));
        Guid lifetime = Guid.NewGuid();
        await Wire.WriteAsync(pipe, new
        {
            @event = "hello",
            data = new BrowserHostHello(BrowserHostProtocol.Version, args[0], lifetime)
        }, handshake.Token);
        JsonObject welcome = await Wire.ReadAsync<JsonObject>(pipe, handshake.Token);
        if (welcome.Count != 2 || welcome["event"]?.GetValue<string>() != "welcome"
            || welcome["data"] is not JsonObject data || data.Count != 2
            || data.Deserialize<BrowserHostWelcome>(Wire.Json) is not { } accepted
            || accepted.Protocol != BrowserHostProtocol.Version || accepted.Lifetime != lifetime || !peer.Alive)
            throw new InvalidDataException("native-handshake");

        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        using var stopped = new CancellationTokenSource();
        Task fromChrome = ForwardAsync(input, pipe, peer, fromApp: false, stopped.Token);
        Task fromApp = ForwardAsync(pipe, output, peer, fromApp: true, stopped.Token);
        Task finished = await Task.WhenAny(fromChrome, fromApp);
        stopped.Cancel();
        pipe.Dispose();
        // Console pipe reads can be synchronous on Windows. Returning ends this browser-owned
        // process rather than awaiting a second read whose remote endpoint is no longer useful.
        await finished;
        return 0;
    }

    private static async Task ForwardAsync(Stream input, Stream output, BrowserPeerIdentity peer, bool fromApp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            JsonObject frame;
            try
            {
                frame = await Wire.ReadAsync<JsonObject>(input, ct);
            }
            catch (EndOfStreamException) { return; }
            if (!peer.Alive || frame["event"]?.GetValue<string>() is "hello" or "welcome")
                throw new InvalidDataException("native-connection");
            if (fromApp)
                frame["clock"] = new JsonObject
                {
                    ["boottimeMs"] = WindowsInterruptClock.NowMs,
                    ["wallTimeMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            else
                frame.Remove("clock");
            // One in-flight frame per direction provides bounded backpressure without queues.
            await Wire.WriteAsync(output, frame, ct);
        }
    }

}
