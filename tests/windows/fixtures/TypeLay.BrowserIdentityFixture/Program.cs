using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace TypeLay.BrowserIdentityFixture;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This metadata-only probe requires a Windows desktop.");
            return 2;
        }
        if (args.Length != 0 && args[0].StartsWith("chrome-extension://", StringComparison.Ordinal))
            return await NativeHost.RunAsync(args);
        if (args.Length is < 4 or > 5 || args[0] != "--chrome" || args[2] != "--output" ||
            (args.Length == 5 && args[4] != "--strict-csp"))
        {
            Console.Error.WriteLine("Usage: TypeLay.BrowserIdentityFixture.exe --chrome <CfT chrome.exe> --output <new result.json> [--strict-csp]");
            return 2;
        }
        string chrome = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[3]);
        if (!File.Exists(chrome) || File.Exists(output))
            throw new InvalidOperationException("A verified CfT executable and an unused result path are required.");
        FileVersionInfo version = FileVersionInfo.GetVersionInfo(chrome);
        using var pin = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "chrome-for-testing.json")));
        if (version.ProductVersion != pin.RootElement.GetProperty("version").GetString() || version.ProductName != "Google Chrome for Testing")
            throw new InvalidOperationException("Only the pinned Chrome for Testing binary is accepted.");
        string run = Path.Combine(Path.GetTempPath(), "typelay-browser-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var events = new List<JsonElement>();
        string outcome = "failed";
        string failure = "not-started";
        bool cleanup = false;
        var cleanupErrors = new List<string>();
        OwnedRegistration? registration = null;
        OwnedProcess? browser = null;
        ProbeServer? server = null;
        nint expectedRoot = 0;
        nint beforeInsertRoot = 0;
        uint expectedPid = 0;
        int? tabId = null, windowId = null;
        string? documentId = null;
        string stage = "startup";
        CancellationTokenSource? deadline = null;
        Task<OwnedWindowDiagnostics.Result>? windowTask = null;
        OwnedWindowDiagnostics.Result? windowDiagnostics = null;
        try
        {
            string extension = Path.Combine(run, "extension");
            Directory.CreateDirectory(extension);
            foreach (string source in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "extension")))
                File.Copy(source, Path.Combine(extension, Path.GetFileName(source)));
            string host = Path.Combine(run, "host");
            Directory.CreateDirectory(host);
            foreach (string source in Directory.GetFiles(AppContext.BaseDirectory))
                File.Copy(source, Path.Combine(host, Path.GetFileName(source)));
            using RSA rsa = RSA.Create(2048);
            byte[] publicKey = rsa.ExportSubjectPublicKeyInfo();
            string extensionId = string.Concat(SHA256.HashData(publicKey).Take(16)
                .SelectMany(value => new[] { (char)('a' + (value >> 4)), (char)('a' + (value & 15)) }));
            string extensionOrigin = "chrome-extension://" + extensionId;
            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            string hostName = "com.typelay.browser_identity.r" + Guid.NewGuid().ToString("N");
            server = new ProbeServer(token, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "pages", "fixture.html")), args.Length == 5);
            await File.WriteAllTextAsync(Path.Combine(extension, "config.js"), "const PROBE = " + JsonSerializer.Serialize(new
            {
                origin = server.Origin,
                token,
                hostName
            }) + ";\n");
            await File.WriteAllTextAsync(Path.Combine(extension, "manifest.json"), JsonSerializer.Serialize(new
            {
                manifest_version = 3,
                name = "TypeLay isolated browser identity probe",
                version = "0.0.1",
                key = Convert.ToBase64String(publicKey),
                permissions = new[] { "nativeMessaging", "tabs", "webNavigation" },
                host_permissions = new[] { server.Origin + "/*" },
                background = new
                {
                    service_worker = "worker.js"
                },
                content_scripts = new[] { new { matches = new[] { server.Origin + "/*" }, js = new[] { "content.js" }, run_at = "document_idle", all_frames = false } },
                web_accessible_resources = new[] { new { resources = new[] { "bridge.html" }, matches = new[] { "http://127.0.0.1/*" } } }
            }, Json));
            string executable = Path.Combine(host, "TypeLay.BrowserIdentityFixture.exe");
            if (!File.Exists(executable))
                throw new InvalidOperationException("The native Windows apphost is required.");
            await File.WriteAllTextAsync(Path.Combine(host, "allowed-origin.txt"), extensionOrigin + "/");
            string manifest = Path.Combine(run, "native-host.json");
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new
            {
                name = hostName,
                description = "Ephemeral metadata-only identity probe",
                path = executable,
                type = "stdio",
                allowed_origins = new[] { extensionOrigin + "/" }
            }, Json));
            registration = new OwnedRegistration(hostName, manifest);
            registration.Configure();
            // Only known schema is retained. The loopback token and origin are also checked by the server.
            server.AcceptEvent = item =>
            {
                if (!IsMetadata(item))
                    return false;
                if (item.GetProperty("kind").GetString() == "top-ready")
                    beforeInsertRoot = Windows.GetForegroundWindow(); // Sample before HTTP acknowledgement permits insertion.
                return true;
            };
            server.Start(extensionOrigin);
            stage = "loopback-started";
            browser = new OwnedProcess(chrome, "--user-data-dir=" + Path.Combine(run, "profile"),
                "--no-first-run", "--no-default-browser-check", "--load-extension=" + extension,
                "--new-window", server.Origin + "/fixture.html");
            stage = "browser-resumed";
            long stageEnds = Stopwatch.GetTimestamp() + 35 * Stopwatch.Frequency;
            deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            windowTask = OwnedWindowDiagnostics.ObserveAsync(browser, output, stageEnds, deadline.Token);
            bool baseline = false, inserted = false, iframe = false;
            while (true)
            {
                var item = await server.Events.Reader.ReadAsync(deadline.Token);
                events.Add(item);
                string kind = item.GetProperty("kind").GetString()!;
                if (kind == "navigation")
                {
                    stage = "navigation-" + item.GetProperty("source").GetString();
                    continue; // Tab metadata observes startup; it cannot start navigation or grant a binding.
                }
                if (kind == "diagnostic")
                {
                    stage = item.GetProperty("stage").GetString()!;
                    continue; // Bootstrap evidence never satisfies baseline, HWND, or field-focus gates.
                }
                stage = kind;
                if (kind == "baseline")
                {
                    var result = item.GetProperty("result");
                    Require(!baseline && result.GetProperty("parentWindow").GetString() == "0" &&
                        !result.GetProperty("isWindow").GetBoolean() && result.GetProperty("rootWindow").GetString() == "0",
                        "worker-baseline-not-zero");
                    baseline = true;
                }
                else if (kind == "top-ready")
                {
                    Require(baseline && expectedRoot == 0, "unexpected-top-ready");
                    expectedRoot = beforeInsertRoot;
                    Require(browser.OwnsWindow(expectedRoot, out expectedPid), "owned-browser-not-foreground");
                    tabId = item.GetProperty("tabId").GetInt32();
                    windowId = item.GetProperty("windowId").GetInt32();
                    documentId = item.GetProperty("documentId").GetString();
                }
                else
                {
                    if (kind == "revoked")
                        throw new ProbeFailure("binding-revoked:" + item.GetProperty("reason").GetString());
                    Require(item.GetProperty("tabId").GetInt32() == tabId && item.GetProperty("windowId").GetInt32() == windowId &&
                        item.GetProperty("documentId").GetString() == documentId, "document-identity-changed");
                    Require(expectedRoot != 0 && Windows.GetForegroundWindow() == expectedRoot && browser.OwnsWindow(expectedRoot, out uint livePid) &&
                        livePid == expectedPid, "foreground-or-window-owner-changed");
                    if (kind == "inserted")
                    {
                        Require(!inserted && item.GetProperty("pageFocused").GetBoolean() && item.GetProperty("focusUnchanged").GetBoolean(),
                            "insertion-stole-focus");
                        inserted = true;
                    }
                    else if (kind == "iframe")
                    {
                        var result = item.GetProperty("result");
                        Require(!iframe && result.GetProperty("isWindow").GetBoolean() &&
                            long.TryParse(result.GetProperty("parentWindow").GetString(), out long parent) && parent != 0 &&
                            Windows.IsWindow((nint)parent) && Windows.GetAncestor((nint)parent, 2) == expectedRoot &&
                            long.Parse(result.GetProperty("rootWindow").GetString()!, System.Globalization.CultureInfo.InvariantCulture) == (long)expectedRoot &&
                            result.GetProperty("ownerPid").GetUInt32() == expectedPid, "iframe-hwnd-not-owned-foreground-root");
                        iframe = true;
                    }
                    else if (kind == "focus")
                    {
                        Require(inserted && iframe && item.GetProperty("pageFocused").GetBoolean() &&
                            item.GetProperty("focusUnchanged").GetBoolean() && item.GetProperty("fieldKind").GetString() is "INPUT" or "TEXTAREA",
                            "handshake-stole-focus");
                        outcome = "passed";
                        failure = "";
                        break;
                    }
                }
            }
        }
        catch (ProbeFailure error) { failure = error.Message; }
        catch (OperationCanceledException) { failure = "stage-timeout"; }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error.GetType().Name; }
        finally
        {
            // Cancel and drain the observer before process cleanup. A timed-out PrintWindow
            // task owns its own handles and may only discard bytes after cancellation.
            deadline?.Cancel();
            if (windowTask is not null)
                windowDiagnostics = await windowTask;
            if (windowDiagnostics is not null)
                foreach (var window in windowDiagnostics.Windows.Where(window => window.ScreenshotCleanupError.HasValue))
                    cleanupErrors.Add("screenshot:" + window.ScreenshotCleanupError!.Value.ToString("X8"));
            deadline?.Dispose();
            try
            {
                browser?.Dispose();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanupErrors.Add("browser:" + error.GetType().Name + ":" + error.HResult.ToString("X8")); }
            try
            {
                if (server is not null)
                    await server.DisposeAsync();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanupErrors.Add("server:" + error.GetType().Name + ":" + error.HResult.ToString("X8")); }
            bool registrationRemoved = false;
            try
            {
                registration?.Dispose();
                registrationRemoved = true;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanupErrors.Add("registration:" + error.GetType().Name + ":" + error.HResult.ToString("X8")); }
            // If ownership changed, keep its referenced manifest intact and report cleanup failure.
            if (registrationRemoved)
            {
                try
                {
                    Directory.Delete(run, recursive: true);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { cleanupErrors.Add("directory:" + error.GetType().Name + ":" + error.HResult.ToString("X8")); }
            }
            cleanup = cleanupErrors.Count == 0;
            if (!cleanup)
            {
                outcome = "failed";

            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                scenario = "single-window-initial-handshake",
                outcome,
                failure,
                stage,
                cleanup,
                cleanupErrors,
                chromeVersion = version.ProductVersion,
                chromeProduct = version.ProductName,
                strictCsp = args.Length == 5,
                expectedRoot = ((long)expectedRoot).ToString(System.Globalization.CultureInfo.InvariantCulture),
                expectedPid,
                events,
                windowDiagnostics,
                coordinatorElevationType = OwnedProcess.CoordinatorElevationType,
                browserBeforeResume = browser?.BeforeResume,
                browserBeforeCleanup = browser?.BeforeCleanup,
                browserAfterCleanup = browser?.AfterCleanup,
                loopback = server?.Diagnostics,
                notRun = new[] { "same-pid-two-windows", "navigation-rebinding", "tab-move-rebinding", "disconnect-rebinding",
                    "duplicate-bridge", "page-forgery", "replay", "branded-chrome-user-approved-loading", "physical-ime", "production-read-write" }
            }, Json));
        }
        Console.WriteLine($"Identity probe {outcome}; stage={stage}; cleanup={cleanup}. Result: {output}");
        return outcome == "passed" ? 0 : 1;
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition)
            throw new ProbeFailure(failure);
    }

    private static bool IsMetadata(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            return false;
        string[] common = ["kind", "tabId", "windowId", "documentId"];
        string[] fields = kind.GetString() switch
        {
            "navigation" => ["kind", "source", "sequence", "querySucceeded", "tabCount", "targetPresent", "pendingTargetPresent",
                "hasLoadingTab", "categories", "statuses", "attemptedTarget", "errorClass"],
            "diagnostic" => ["kind", "stage", "sequence"],
            "baseline" => ["kind", "result"],
            "top-ready" => [.. common, "fieldKind"],
            "inserted" => [.. common, "pageFocused", "focusUnchanged"],
            "iframe" => [.. common, "frameId", "bridgeDocumentId", "result"],
            "focus" => [.. common, "pageFocused", "focusUnchanged", "fieldKind"],
            "revoked" => [.. common, "reason"],
            _ => []
        };
        if (fields.Length == 0 || !item.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(fields.Order()))
            return false;
        if (kind.GetString() == "navigation")
        {
            string[] sources = ["startup", "tab-created", "tab-updated", "before-navigate", "target-error"];
            string[] errors = ["none", "aborted", "blocked", "connection", "other"];
            string[] categories = ["target", "new-tab", "blank", "browser-internal", "other", "unknown"];
            string[] statuses = ["loading", "complete", "unloaded", "unknown"];
            string[] flags = ["querySucceeded", "targetPresent", "pendingTargetPresent", "hasLoadingTab", "attemptedTarget"];
            if (item.GetProperty("source").ValueKind != JsonValueKind.String || !sources.Contains(item.GetProperty("source").GetString()) ||
                item.GetProperty("errorClass").ValueKind != JsonValueKind.String || !errors.Contains(item.GetProperty("errorClass").GetString()) ||
                item.GetProperty("sequence").ValueKind != JsonValueKind.Number || !item.GetProperty("sequence").TryGetInt32(out int sequence) ||
                sequence is < 1 or > 12 || item.GetProperty("tabCount").ValueKind != JsonValueKind.Number ||
                !item.GetProperty("tabCount").TryGetInt32(out int tabCount) || tabCount is < 0 or > 100 ||
                flags.Any(flag => item.GetProperty(flag).ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
                return false;
            var observedCategories = item.GetProperty("categories");
            var observedStatuses = item.GetProperty("statuses");
            return observedCategories.ValueKind == JsonValueKind.Array && observedCategories.GetArrayLength() <= categories.Length &&
                observedCategories.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String && categories.Contains(value.GetString())) &&
                observedStatuses.ValueKind == JsonValueKind.Array && observedStatuses.GetArrayLength() <= statuses.Length &&
                observedStatuses.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String && statuses.Contains(value.GetString()));
        }
        if (kind.GetString() == "diagnostic")
        {
            string[] stages = ["worker-started", "internal-port-received", "sender-extension-id-rejected", "sender-tab-missing",
                "sender-window-id-rejected", "sender-tab-id-rejected", "sender-document-id-rejected", "sender-document-inactive",
                "top-frame-rejected", "top-url-rejected", "top-origin-rejected", "top-port-accepted", "top-ready-received",
                "baseline-connect-requested", "baseline-reply-validated", "baseline-disconnected-before-reply", "field-not-focused"];
            return item.GetProperty("stage").ValueKind == JsonValueKind.String &&
                stages.Contains(item.GetProperty("stage").GetString()) &&
                item.GetProperty("sequence").ValueKind == JsonValueKind.Number &&
                item.GetProperty("sequence").TryGetInt32(out int sequence) && sequence is > 0 and <= 32;
        }
        foreach (var property in item.EnumerateObject())
        {
            if (property.Name == "result")
            {
                string[] nativeFields = ["challenge", "parentWindow", "isWindow", "rootWindow", "ownerPid", "hostPid", "connectionId", "lifetimeMs"];
                if (property.Value.ValueKind != JsonValueKind.Object ||
                    !property.Value.EnumerateObject().Select(value => value.Name).Order().SequenceEqual(nativeFields.Order()))
                    return false;
                foreach (var value in property.Value.EnumerateObject())
                    if (value.Value.ValueKind == JsonValueKind.String && value.Value.GetString()!.Length > 64)
                        return false;
            }
            else if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Length > 64)
                return false;
        }
        return true;
    }

    private sealed class ProbeFailure(string message) : Exception(message);
}
