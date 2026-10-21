using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeLay.Windows.Testing;

internal static partial class BrowserCooperationCheck
{
    private sealed record PageState(string A, string B, string Focus, bool PageFocused, int AStart, int AEnd,
        int BStart, int BEnd, int Replacements, int Submits, int DirectKeys, int PreviewMounts);
    private sealed record Preview(bool Visible, string Text, string Hint);
    private sealed record ExtensionDiscovery(int Targets, int ServiceWorkers, int Candidates, int MetadataRead, int MatchingManifests);
    private sealed class PageServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        public string Url
        {
            get;
        }
        public PageServer()
        {
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/cooperation";
            _serving = ServeAsync();
        }
        private async Task ServeAsync()
        {
            byte[] body = Encoding.UTF8.GetBytes("""
                <!doctype html><html><head><meta charset="utf-8"><title>TypeLay cooperation</title>
                <style>body{padding:60px;font:20px sans-serif}textarea,input{display:block;width:650px;padding:12px;margin:16px}textarea{height:150px}</style>
                </head><body><form id="form"><label for="a">A message</label><textarea id="a" aria-label="A message"></textarea>
                <label for="b">B independent field</label><input id="b" aria-label="B independent field"><button type="submit">Submit</button></form>
                <script>
                const a=document.getElementById('a'),b=document.getElementById('b');
                const counts={replacements:0,submits:0,directKeys:0,previewMounts:0};
                document.getElementById('form').addEventListener('submit',event=>{event.preventDefault();counts.submits++;});
                for(const field of [a,b]) field.addEventListener('input',event=>{
                  if(event.inputType==='insertReplacementText') counts.replacements++;
                });
                document.addEventListener('keydown',event=>{
                  if(event.isTrusted && event.ctrlKey && event.shiftKey && !event.altKey && event.key==='F10') counts.directKeys++;
                });
                // The page has no fixed overlays of its own. Count inserted visible UI
                // hosts throughout a direct operation, including a transient preview.
                const observer=new MutationObserver(records=>{
                  for(const record of records) for(const node of record.addedNodes)
                    if(node instanceof HTMLElement && node.parentElement===document.documentElement
                      && getComputedStyle(node).position==='fixed') counts.previewMounts++;
                });
                observer.observe(document.documentElement,{childList:true});
                function resetCounts(){observer.takeRecords();for(const key of Object.keys(counts))counts[key]=0;}
                function state(){return {a:a.value,b:b.value,focus:document.activeElement?.id||'',pageFocused:document.hasFocus(),
                  aStart:a.selectionStart,aEnd:a.selectionEnd,bStart:b.selectionStart,bEnd:b.selectionEnd,...counts};}
                </script></body></html>
                """);
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    request.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                        string? line = await reader.ReadLineAsync(request.Token);
                        if (line is null)
                            continue;
                        bool page = line == "GET /cooperation HTTP/1.1";
                        int size = line?.Length ?? 0;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(request.Token)))
                        {
                            size += line.Length;
                            if (size > 16 * 1024)
                                throw new Failure("loopback-request-limit");
                        }
                        byte[] content = page ? body : [];
                        string header = $"HTTP/1.1 {(page ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), request.Token);
                        await stream.WriteAsync(content, request.Token);
                    }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { }
                    catch (IOException) { } // An owned Chrome speculative connection can close without a request.

                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _serving;
            }
            finally { _stop.Dispose(); }
        }
    }
    // Only this fixture's owned CfT endpoint is used. CDP drives/observes synthetic
    // page UI and reads public extension metadata; it never writes extension state,
    // native messages or product permits.
    private sealed class Cdp : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private int _nextId;
        private string? _page;
        public ExtensionDiscovery Discovery { get; private set; } = new(0, 0, 0, 0, 0);
        private static readonly JsonSerializerOptions ReadJson = new() { PropertyNameCaseInsensitive = true };
        public static async Task<Cdp> ConnectAsync(string profile, Action<string> setStage, CancellationToken ct)
        {
            string portFile = Path.Combine(profile, "DevToolsActivePort");
            string[] lines;
            setStage("wait-cft-cdp-port");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(portFile))
                {
                    setStage("read-cft-cdp-port");
                    try
                    {
                        lines = await File.ReadAllLinesAsync(portFile, ct);
                        if (lines.Length == 2 && int.TryParse(lines[0], out _))
                            break;
                    }
                    catch (IOException error) when (error.HResult == unchecked((int)0x80070020))
                    {
                        // An existing endpoint file can still be locked while being published.
                        // Keep observing under the original setup deadline.
                    }
                }
                setStage("wait-cft-cdp-port");
                await Task.Delay(40, ct);
            }
            setStage("validate-cft-cdp-endpoint");
            Require(int.TryParse(lines[0], out int port) && port is > 0 and <= 65535
                && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal)
                && !lines[1].Contains('?') && !lines[1].Contains('#'), "owned-cdp-endpoint-invalid");
            var cdp = new Cdp();
            try
            {
                setStage("connect-cft-cdp");
                await cdp._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{lines[1]}"), ct);
                return cdp;
            }
            catch { cdp.Dispose(); throw; }
        }
        public async Task<string> ExtensionIdAsync(string manifestPath, CancellationToken ct)
        {
            using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct));
            while (true)
            {
                var targets = (await CallAsync("Target.getTargets", new
                {
                }, null, ct)).GetProperty("targetInfos");
                var workers = targets.EnumerateArray().Where(target => target.GetProperty("type").GetString() == "service_worker").ToArray();
                var candidates = workers.Where(target => target.GetProperty("url").GetString() is { } url
                    && url.StartsWith("chrome-extension://", StringComparison.Ordinal)
                    && url.EndsWith("/background.js", StringComparison.Ordinal)).ToArray();
                Discovery = new(targets.GetArrayLength(), workers.Length, candidates.Length, 0, 0);
                Require(candidates.Length <= 32, "loaded-extension-candidate-limit");
                if (candidates.Length == 0)
                {
                    // Retain the original bounded startup observation, not a retry of
                    // a failed metadata match or any product command.
                    await Task.Delay(40, ct);
                    continue;
                }
                var matches = new HashSet<string>(StringComparer.Ordinal);
                foreach (var candidate in candidates)
                {
                    var metadata = await ExtensionMetadataAsync(candidate.GetProperty("targetId").GetString()!, ct);
                    Discovery = Discovery with
                    {
                        MetadataRead = Discovery.MetadataRead + 1
                    };
                    string? id = metadata.GetProperty("id").GetString();
                    Require(id is { Length: 32 } && id.All(c => c is >= 'a' and <= 'p')
                        && id == new Uri(candidate.GetProperty("url").GetString()!).Host, "loaded-extension-id-invalid");
                    // background.js is a common filename, including in other Chrome
                    // extensions. Only the full shipped manifest identifies ours.
                    if (JsonElement.DeepEquals(metadata.GetProperty("manifest"), expected.RootElement))
                        matches.Add(id!);
                    Discovery = Discovery with
                    {
                        MatchingManifests = matches.Count
                    };
                }
                Require(matches.Count != 0, "loaded-extension-manifest-not-found");
                Require(matches.Count == 1, "loaded-extension-manifest-ambiguous");
                return matches.Single();
            }
        }
        private async Task<JsonElement> ExtensionMetadataAsync(string targetId, CancellationToken ct)
        {
            var attach = await CallAsync("Target.attachToTarget", new
            {
                targetId,
                flatten = true
            }, null, ct);
            string session = attach.GetProperty("sessionId").GetString() ?? throw new Failure("extension-metadata-session-missing");
            // Read only Chrome's public extension identity/manifest API in this
            // disposable profile. Never inspect ports, state, ACKs or page content.
            var response = await CallAsync("Runtime.evaluate", new
            {
                expression = "({id: chrome.runtime.id, manifest: chrome.runtime.getManifest()})",
                returnByValue = true,
                awaitPromise = false
            }, session, ct);
            Require(!response.TryGetProperty("exceptionDetails", out _)
                && response.GetProperty("result").TryGetProperty("value", out _), "extension-metadata-unavailable");
            var metadata = response.GetProperty("result").GetProperty("value").Clone();
            // Successful discovery must not leave a debugger attached during the
            // product scenarios. On failure the caller closes the owned browser/CDP.
            await CallAsync("Target.detachFromTarget", new
            {
                sessionId = session
            }, null, ct);
            return metadata;
        }
        public async Task OpenPageAsync(string url, Action<string> stage, CancellationToken ct)
        {
            stage("get-targets");
            var targets = (await CallAsync("Target.getTargets", new
            {
            }, null, ct)).GetProperty("targetInfos");
            var pages = targets.EnumerateArray().Where(target => target.GetProperty("type").GetString() == "page"
                && target.GetProperty("url").GetString() == "about:blank").ToArray();
            Require(pages.Length == 1, "owned-initial-page-ambiguous");
            stage("attach-page");
            var attach = await CallAsync("Target.attachToTarget", new
            {
                targetId = pages[0].GetProperty("targetId").GetString(),
                flatten = true
            }, null, ct);
            _page = attach.GetProperty("sessionId").GetString() ?? throw new Failure("cdp-page-session-missing");
            stage("enable-page");
            await PageCallAsync("Page.enable", new
            {
            }, ct);
            stage("enable-dom");
            await PageCallAsync("DOM.enable", new
            {
            }, ct);
            stage("navigate");
            var navigation = await PageCallAsync("Page.navigate", new
            {
                url
            }, ct);
            Require(BrowserPageOpenResponse.NavigationSucceeded(navigation), "owned-page-navigation-failed");
            string lastReadiness = "unobserved";
            while (true)
            {
                stage("readiness-response-" + lastReadiness);
                var response = await EvaluateAsync("({documentComplete:document.readyState==='complete',stateAvailable:typeof state==='function'})", ct);
                var ready = BrowserPageOpenResponse.Readiness(response) ?? throw new Failure("owned-page-readiness-invalid");
                if (ready.DocumentComplete && ready.StateAvailable)
                    break;
                lastReadiness = (ready.DocumentComplete ? "document-complete" : "document-incomplete")
                    + (ready.StateAvailable ? "-state-available" : "-state-missing");
                stage("readiness-pending-" + lastReadiness);
                await Task.Delay(40, ct);
            }
        }
        public Task<JsonElement> PageCallAsync(string method, object parameters, CancellationToken ct) => CallAsync(method, parameters, _page, ct);
        public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct)
        {
            var result = await PageCallAsync("Runtime.evaluate", new
            {
                expression,
                returnByValue = true,
                awaitPromise = false
            }, ct);
            Require(!result.TryGetProperty("exceptionDetails", out _), "synthetic-page-evaluation-failed");
            Require(result.GetProperty("result").TryGetProperty("value", out var value), "synthetic-page-value-missing");
            return value.Clone();
        }
        public async Task<PageState> StateAsync(string before, CancellationToken ct) =>
            JsonSerializer.Deserialize<PageState>(await EvaluateAsync("(()=>{" + before + "return state();})()", ct), ReadJson)
                ?? throw new Failure("synthetic-page-state-invalid");
        private static IEnumerable<JsonElement> Descendants(JsonElement node)
        {
            yield return node;
            foreach (string property in new[] { "children", "shadowRoots" })
                if (node.TryGetProperty(property, out var children))
                    foreach (var child in children.EnumerateArray())
                        foreach (var descendant in Descendants(child))
                            yield return descendant;
        }
        private async Task<string?> PreviewObjectAsync(CancellationToken ct)
        {
            var root = (await PageCallAsync("DOM.getDocument", new
            {
                depth = -1,
                pierce = true
            }, ct)).GetProperty("root");
            var editors = Descendants(root).Where(node =>
            {
                if (node.GetProperty("nodeName").GetString() != "TEXTAREA" || !node.TryGetProperty("attributes", out var attributes))
                    return false;
                string?[] values = attributes.EnumerateArray().Select(attribute => attribute.GetString()).ToArray();
                for (int index = 0; index + 1 < values.Length; index += 2)
                    if (values[index] == "aria-label" && values[index + 1] == "TypeLay 译文")
                        return true;
                return false;
            }).ToArray();
            Require(editors.Length <= 1, "visible-preview-ambiguous");
            if (editors.Length == 0)
                return null;
            var resolved = await PageCallAsync("DOM.resolveNode", new
            {
                backendNodeId = editors[0].GetProperty("backendNodeId").GetInt32()
            }, ct);
            return resolved.GetProperty("object").GetProperty("objectId").GetString();
        }
        public async Task<Preview> PreviewAsync(CancellationToken ct)
        {
            string? id = await PreviewObjectAsync(ct);
            if (id is null)
                return new(false, "", "");
            try
            {
                var result = await PageCallAsync("Runtime.callFunctionOn", new
                {
                    objectId = id,
                    returnByValue = true,
                    functionDeclaration = "function(){const r=this.getBoundingClientRect();return {visible:this.isConnected&&r.width>0&&r.height>0&&!this.hidden,text:this.value,hint:this.getRootNode().querySelector('.hint')?.textContent||''};}"
                }, ct);
                return JsonSerializer.Deserialize<Preview>(result.GetProperty("result").GetProperty("value"), ReadJson)
                    ?? throw new Failure("visible-preview-invalid");
            }
            finally { await PageCallAsync("Runtime.releaseObject", new { objectId = id }, ct); }
        }
        public async Task ClickOverlaySettingsAsync(CancellationToken ct)
        {
            string? id = await PreviewObjectAsync(ct);
            Require(id is not null, "preview-settings-unavailable");
            JsonElement point;
            try
            {
                var result = await PageCallAsync("Runtime.callFunctionOn", new
                {
                    objectId = id,
                    returnByValue = true,
                    functionDeclaration = "function(){const b=this.getRootNode().querySelector('button[title=\"语言和设置\"]');const r=b.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2,visible:b.isConnected&&!b.hidden&&r.width>0&&r.height>0};}"
                }, ct);
                point = result.GetProperty("result").GetProperty("value");
            }
            finally { await PageCallAsync("Runtime.releaseObject", new { objectId = id }, ct); }
            Require(point.GetProperty("visible").GetBoolean(), "preview-settings-hidden");
            await ClickPointAsync(point, ct);
        }
        public async Task ClickSubmitAsync(CancellationToken ct)
        {
            var point = await EvaluateAsync("(()=>{const r=document.querySelector('button[type=submit]').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()", ct);
            await ClickPointAsync(point, ct);
        }
        private async Task ClickPointAsync(JsonElement point, CancellationToken ct)
        {
            double x = point.GetProperty("x").GetDouble(), y = point.GetProperty("y").GetDouble();
            foreach (string type in new[] { "mousePressed", "mouseReleased" })
                await PageCallAsync("Input.dispatchMouseEvent", new
                {
                    type,
                    x,
                    y,
                    button = "left",
                    clickCount = 1
                }, ct);
        }
        private async Task<JsonElement> CallAsync(string method, object parameters, string? session, CancellationToken ct)
        {
            int id = ++_nextId;
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id,
                method,
                @params = parameters,
                sessionId = session
            },
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            await _socket.SendAsync(request, WebSocketMessageType.Text, true, ct);
            byte[] buffer = new byte[16 * 1024];
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    Require(received.MessageType == WebSocketMessageType.Text, "owned-cdp-disconnected");
                    await message.WriteAsync(buffer.AsMemory(0, received.Count), ct);
                    Require(message.Length <= 4 * 1024 * 1024, "owned-cdp-response-limit");
                } while (!received.EndOfMessage);
                using var response = JsonDocument.Parse(message.ToArray());
                var root = response.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                    continue;
                Require(!root.TryGetProperty("error", out _), "cdp-" + method.Replace('.', '-') + "-failed");
                return root.GetProperty("result").Clone();
            }
        }
        public void Dispose() => _socket.Dispose();
    }
}
