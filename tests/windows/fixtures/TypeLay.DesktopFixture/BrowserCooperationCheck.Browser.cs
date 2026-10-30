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
    private sealed record ExtensionMetadataDiagnostic(int Candidate, string Stage, BrowserExtensionMetadataResponse? Response = null);
    private sealed record ExtensionDiscovery(int Targets, int ServiceWorkers, int Candidates, int MetadataRead, int MatchingManifests);
    private sealed class PageServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        public bool ConnectionAccepted
        {
            get; private set;
        }
        public bool PageRequestReceived
        {
            get; private set;
        }
        public bool PageResponseWritten
        {
            get; private set;
        }
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
                    ConnectionAccepted = true;
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
                        PageRequestReceived |= page;
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
                        PageResponseWritten |= page;
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
        private string? _pageTarget;
        private string? _mainFrame, _initialLoader, _navigationUrl;
        public BrowserCdpDiagnostics Diagnostics { get; } = new();
        public ExtensionDiscovery Discovery { get; private set; } = new(0, 0, 0, 0, 0);
        private readonly List<ExtensionMetadataDiagnostic> _metadataDiagnostics = new();
        public IReadOnlyList<ExtensionMetadataDiagnostic> MetadataDiagnostics => _metadataDiagnostics;
        public string NavigationFailure { get; private set; } = "unobserved";
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
            // Candidates were bounded to 32 before this single pass. Preserve one
            // entry per attempted worker, updating only its original call phase.
            int diagnosticIndex = _metadataDiagnostics.Count;
            _metadataDiagnostics.Add(new(diagnosticIndex + 1, "attach"));
            var attach = await CallAsync("Target.attachToTarget", new
            {
                targetId,
                flatten = true
            }, null, ct);
            string session = attach.GetProperty("sessionId").GetString() ?? throw new Failure("extension-metadata-session-missing");
            _metadataDiagnostics[diagnosticIndex] = _metadataDiagnostics[diagnosticIndex] with
            {
                Stage = "evaluate"
            };
            // Read only Chrome's public extension identity/manifest API in this
            // disposable profile. Never inspect ports, state, ACKs or page content.
            var response = await CallAsync("Runtime.evaluate", new
            {
                expression = """
                    (()=>{
                        const chromePresent=typeof chrome!=='undefined'&&chrome!==null;
                        const runtimePresent=chromePresent&&typeof chrome.runtime!=='undefined'&&chrome.runtime!==null;
                        const getManifestCallable=runtimePresent&&typeof chrome.runtime.getManifest==='function';
                        const availability={chromePresent,runtimePresent,getManifestCallable};
                        if(!getManifestCallable) return {availability};
                        return {availability,id:chrome.runtime.id,manifest:chrome.runtime.getManifest()};
                    })()
                    """,
                returnByValue = true,
                awaitPromise = false
            }, session, ct);
            _metadataDiagnostics[diagnosticIndex] = _metadataDiagnostics[diagnosticIndex] with
            {
                Response = BrowserExtensionMetadataResponse.Observe(response)
            };
            Require(!response.TryGetProperty("exceptionDetails", out _)
                && response.GetProperty("result").TryGetProperty("value", out _), "extension-metadata-unavailable");
            var metadata = response.GetProperty("result").GetProperty("value").Clone();
            // Availability is diagnostic only. Missing bindings still fail this
            // attempt; only the original id and full manifest can identify ours.
            Require(metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("id", out _)
                && metadata.TryGetProperty("manifest", out _), "extension-metadata-unavailable");
            // Successful discovery must not leave a debugger attached during the
            // product scenarios. On failure the caller closes the owned browser/CDP.
            _metadataDiagnostics[diagnosticIndex] = _metadataDiagnostics[diagnosticIndex] with
            {
                Stage = "detach"
            };
            await CallAsync("Target.detachFromTarget", new
            {
                sessionId = session
            }, null, ct);
            _metadataDiagnostics[diagnosticIndex] = _metadataDiagnostics[diagnosticIndex] with
            {
                Stage = "complete"
            };
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
            _pageTarget = pages[0].GetProperty("targetId").GetString();
            var attach = await CallAsync("Target.attachToTarget", new
            {
                targetId = _pageTarget,
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
            _navigationUrl = url;
            stage("inspect-initial-frame");
            var initial = await PageCallAsync("Page.getFrameTree", new
            {
            }, ct);
            if (initial.ValueKind == JsonValueKind.Object && initial.TryGetProperty("frameTree", out var tree) && tree.ValueKind == JsonValueKind.Object
                && tree.TryGetProperty("frame", out var frame) && frame.ValueKind == JsonValueKind.Object)
            {
                _mainFrame = frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                _initialLoader = frame.TryGetProperty("loaderId", out var loader) && loader.ValueKind == JsonValueKind.String ? loader.GetString() : null;
            }
            Diagnostics.RecordFrameSnapshot(initial, url, null, null, afterAbort: false);
            stage("enable-network-observation");
            await PageCallAsync("Network.enable", new
            {
            }, ct);
            Diagnostics.BeginNetworkObservation();
            try
            {
                stage("navigate");
                var navigation = await PageCallAsync("Page.navigate", new
                {
                    url
                }, ct);
                NavigationFailure = BrowserPageOpenResponse.NavigationFailure(navigation);
                Diagnostics.RecordNavigationResponse(navigation, _mainFrame, _initialLoader);
                if (NavigationFailure == "aborted")
                {
                    // Observe the original target once and drain already queued events.
                    // This cannot retry navigation, recover the scene or replace its error.
                    using var snapshot = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    snapshot.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        snapshot.Token.ThrowIfCancellationRequested();
                        var after = await PageCallAsync("Page.getFrameTree", new
                        {
                        }, snapshot.Token);
                        Diagnostics.RecordFrameSnapshot(after, url, _mainFrame, _initialLoader, afterAbort: true);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        Diagnostics.RecordAbortSnapshotFailure(error);
                    }
                }
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
                // Close only after successful page readiness. The failure path
                // keeps its original navigation error and closes the owned browser.
                stage("disable-network-observation");
                await PageCallAsync("Network.disable", new
                {
                }, ct);
            }
            finally { Diagnostics.EndNetworkObservation(); }
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
        // Last owned-control observation only; no labels, user text, retry or new input.
        public JsonElement? LastOverlayMenuObservation
        {
            get; private set;
        }
        public Task ClickOverlaySettingsAsync(CancellationToken ct) => ClickOverlayMenuActionAsync("设置", ct);
        private async Task ClickOverlayMenuActionAsync(string label, CancellationToken ct)
        {
            LastOverlayMenuObservation = null;
            JsonElement? observation = null;
            var retained = new List<string>();
            bool failed = false;
            string? id = await PreviewObjectAsync(ct);
            Require(id is not null, "preview-settings-unavailable");
            try
            {
                foreach (bool more in new[] { true, false })
                {
                    string? actionId = null, menuId = null;
                    if (!more && label == "加入术语表")
                    {
                        // Bind this publicly displayed action once. Waiting for its
                        // asynchronous qualification must not follow a replacement menu.
                        var action = await PageCallAsync("Runtime.callFunctionOn", new
                        {
                            objectId = id,
                            returnByValue = false,
                            functionDeclaration = "function(){const m=this.getRootNode().querySelectorAll('[role=group][aria-label=\"更多操作\"]');if(m.length!==1)return null;const b=[...m[0].querySelectorAll(':scope>button')].filter(x=>x.querySelector('span')?.textContent==='加入术语表');return b.length===1?b[0]:null;}"
                        }, ct);
                        Require(action.GetProperty("result").TryGetProperty("objectId", out var actionObject), "preview-glossary-action-missing");
                        actionId = actionObject.GetString()!;
                        retained.Add(actionId);
                        var menu = await PageCallAsync("Runtime.callFunctionOn", new
                        {
                            objectId = actionId,
                            returnByValue = false,
                            functionDeclaration = "function(){return this.parentElement;}"
                        }, ct);
                        Require(menu.GetProperty("result").TryGetProperty("objectId", out var menuObject), "preview-glossary-menu-missing");
                        menuId = menuObject.GetString()!;
                        retained.Add(menuId);
                    }
                    JsonElement point;
                    while (true)
                    {
                        var result = await PageCallAsync("Runtime.callFunctionOn", new
                        {
                            objectId = id,
                            returnByValue = true,
                            arguments = new object[] { new { value = more }, new { value = label },
                                actionId is null ? (object)new { value = (string?)null } : new { objectId = actionId },
                                menuId is null ? (object)new { value = (string?)null } : new { objectId = menuId } },
                            functionDeclaration = """
                                function(more,label,expectedAction,expectedMenu){
                                 const root=this.getRootNode(),menus=root.querySelectorAll('[role=group][aria-label="更多操作"]');
                                 const buttons=more?root.querySelectorAll('button[aria-controls="more-actions"]'):[...root.querySelectorAll('[role=group][aria-label="更多操作"]>button')].filter(b=>b.querySelector('span')?.textContent===label);
                                 const observation={more,action:label==='加入术语表'?'glossary':'settings',editorConnected:this.isConnected,editorReadOnly:this.readOnly,editorFocused:root.activeElement===this,documentFocused:document.hasFocus(),menuCount:menus.length,menuHidden:menus.length===1?menus[0].hidden:null,matchedCount:buttons.length};
                                 if(buttons.length!==1)return {...observation,visible:false,reason:!this.isConnected?'editor-retired':buttons.length===0?'action-missing':'action-ambiguous'};
                                 const b=buttons[0],r=b.getBoundingClientRect();
                                 if(expectedAction&&(b!==expectedAction||menus.length!==1||menus[0]!==expectedMenu||b.parentElement!==expectedMenu))return {...observation,visible:false,reason:'action-replaced'};
                                 // These exact product messages are unchanged by current uiText in both UI languages.
                                 // Never return arbitrary title text, user content or an inferred authority.
                                 const qualificationReasons={'术语功能尚未开放':'not-released','当前模型不支持同时应用风格与术语表':'combination-unavailable','当前模型方向尚未开放风格与术语同时应用':'combination-not-released','当前译文无法加入术语表':'editing-required','请先完成或取消当前输入':'ime','请回到当前术语表单':'focus','请先完成当前术语表单':'already-open','请等待当前操作完成':'busy','操作已取消':'cancelled','内容或设置已变化，请重新打开表单':'stale','请检查词语、方向、冲突规则及容量限制':'invalid-glossary','当前无法加入术语表':'unavailable'};
                                 const qualificationReason=label!=='加入术语表'||!b.title?null:Object.hasOwn(qualificationReasons,b.title)?qualificationReasons[b.title]:'other';
                                 return {...observation,qualificationReason,x:r.x+r.width/2,y:r.y+r.height/2,visible:b.isConnected&&!b.hidden&&!b.disabled&&r.width>0&&r.height>0,painted:Number(getComputedStyle(b).opacity)>0,buttonConnected:b.isConnected,buttonHidden:b.hidden,buttonDisabled:b.disabled,width:r.width,height:r.height,reason:!this.isConnected?'editor-retired':!b.isConnected?'button-retired':b.hidden?'button-hidden':b.disabled?'button-disabled':r.width<=0||r.height<=0?'button-not-laid-out':'visible'};
                                }
                                """
                        }, ct);
                        point = result.GetProperty("result").GetProperty("value");
                        observation = point.Clone();
                        if (actionId is not null && point.GetProperty("reason").GetString() == "button-disabled")
                        {
                            Require(point.GetProperty("editorConnected").GetBoolean()
                                && !point.GetProperty("editorReadOnly").GetBoolean()
                                && point.GetProperty("editorFocused").GetBoolean()
                                && point.GetProperty("documentFocused").GetBoolean()
                                && !point.GetProperty("menuHidden").GetBoolean()
                                && point.GetProperty("buttonConnected").GetBoolean()
                                && !point.GetProperty("buttonHidden").GetBoolean()
                                && point.GetProperty("painted").GetBoolean()
                                && point.GetProperty("width").GetDouble() > 0
                                && point.GetProperty("height").GetDouble() > 0,
                                "preview-glossary-action-retired");
                            // Same scene deadline; no click, re-open, lease renewal or
                            // private handoff acknowledgement while the public item is disabled.
                            await Task.Delay(40, ct);
                            continue;
                        }
                        Require(point.GetProperty("visible").GetBoolean(), more ? "preview-more-hidden" : "preview-settings-hidden");
                        if (actionId is not null)
                            Require(point.GetProperty("editorConnected").GetBoolean()
                                && !point.GetProperty("editorReadOnly").GetBoolean()
                                && point.GetProperty("editorFocused").GetBoolean()
                                && point.GetProperty("documentFocused").GetBoolean()
                                && !point.GetProperty("menuHidden").GetBoolean(),
                                "preview-glossary-action-retired");
                        // Reveal the real hover-only More control with a trusted pointer
                        // movement, never by mutating its style or calling its handler.
                        if (point.GetProperty("painted").GetBoolean())
                            break;
                        await PageCallAsync("Input.dispatchMouseEvent", new
                        {
                            type = "mouseMoved",
                            x = point.GetProperty("x").GetDouble(),
                            y = point.GetProperty("y").GetDouble()
                        }, ct);
                        await Task.Delay(40, ct);
                    }
                    await ClickPointAsync(point, ct);
                }
            }
            catch
            {
                failed = true;
                LastOverlayMenuObservation = observation;
                throw;
            }
            finally
            {
                Exception? cleanupFailure = null;
                foreach (string retainedId in retained.Append(id!))
                {
                    try
                    {
                        await PageCallAsync("Runtime.releaseObject", new
                        {
                            objectId = retainedId
                        }, ct);
                    }
                    catch (OutOfMemoryException) { throw; }
                    catch (Exception error) { cleanupFailure ??= error; }
                }
                if (!failed && cleanupFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }
        public async Task<string> OpenEditorGlossaryAsync(CancellationToken ct)
        {
            string? id = await PreviewObjectAsync(ct);
            Require(id is not null, "glossary-preview-unavailable");
            try
            {
                var point = await PageCallAsync("Runtime.callFunctionOn", new
                {
                    objectId = id,
                    returnByValue = true,
                    functionDeclaration = "function(){const r=this.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};}"
                }, ct);
                await ClickPointAsync(point.GetProperty("result").GetProperty("value"), ct);
                while (true)
                {
                    var state = await PageCallAsync("Runtime.callFunctionOn", new
                    {
                        objectId = id,
                        returnByValue = true,
                        functionDeclaration = "function(){return this.isConnected&&!this.readOnly&&this.getRootNode().activeElement===this;}"
                    }, ct);
                    if (state.GetProperty("result").GetProperty("value").GetBoolean())
                        break;
                    await Task.Delay(20, ct);
                }
                await ClickOverlayMenuActionAsync("加入术语表", ct);
                while (true)
                {
                    var form = await PageCallAsync("Runtime.callFunctionOn", new
                    {
                        objectId = id,
                        returnByValue = false,
                        functionDeclaration = "function(){return this.getRootNode().querySelector('[role=group][aria-label=\"加入术语表\"]');}"
                    }, ct);
                    if (form.GetProperty("result").TryGetProperty("objectId", out var objectId))
                        return objectId.GetString() ?? throw new Failure("glossary-form-object-invalid");
                    await Task.Delay(20, ct);
                }
            }
            finally { await PageCallAsync("Runtime.releaseObject", new { objectId = id }, ct); }
        }
        public async Task<JsonElement> GlossaryStateAsync(string id, CancellationToken ct)
        {
            var result = await PageCallAsync("Runtime.callFunctionOn", new
            {
                objectId = id,
                returnByValue = true,
                functionDeclaration = "function(){const r=this.getBoundingClientRect(),root=this.getRootNode();return {connected:this.isConnected,visible:this.isConnected&&!this.hidden&&r.width>0&&r.height>0,focused:this.contains(root.activeElement),inputs:this.querySelectorAll('input').length};}"
            }, ct);
            return result.GetProperty("result").GetProperty("value").Clone();
        }
        public JsonElement? LastSourceClickObservation
        {
            get; private set;
        }
        public async Task ClickSourceAsync(CancellationToken ct)
        {
            LastSourceClickObservation = null;
            // One point and one real click. The fixture page has no overlays of
            // its own: choose the largest exposed source interior around the
            // one public TypeLay host, then verify its actual hit before input.
            var point = await EvaluateAsync("""
                (()=>{const r=a.getBoundingClientRect(),selector='[role="region"][aria-label="TypeLay"]';
                  const hosts=[...document.querySelectorAll(selector)],rect=e=>{const b=e.getBoundingClientRect();return [b.left,b.top,b.width,b.height];};
                  const base={l:Math.max(0,r.left+4),t:Math.max(0,r.top+4),r:Math.min(innerWidth,r.right-4),b:Math.min(innerHeight,r.bottom-4)};
                  let areas=[base];
                  if(hosts.length===1){const o=hosts[0].getBoundingClientRect();
                    const l=Math.max(base.l,o.left),t=Math.max(base.t,o.top),right=Math.min(base.r,o.right),bottom=Math.min(base.b,o.bottom);
                    if(l<right&&t<bottom)areas=[{...base,b:t},{...base,t:bottom},{l:base.l,t,r:l,b:bottom},{l:right,t,r:base.r,b:bottom}];}
                  areas=areas.filter(q=>q.r-q.l>=16&&q.b-q.t>=16).sort((a,b)=>(b.r-b.l)*(b.b-b.t)-(a.r-a.l)*(a.b-a.t));
                  const q=a.isConnected&&hosts.length<=1?areas[0]:null,x=q?(q.l+q.r)/2:0,y=q?(q.t+q.b)/2:0;
                  const hit=q?document.elementFromPoint(x,y):null,sourceHit=!!hit&&(hit===a||a.contains(hit)),overlayHit=!!hit?.closest(selector);
                  return {x,y,sourcePointReady:sourceHit,observation:{hit:sourceHit?'source':overlayHit?'overlay':hit?'other':'none',
                    reason:!a.isConnected?'source-detached':hosts.length>1?'ambiguous-overlay':!q?'no-exposed-interior':!sourceHit?'point-not-source':'exposed-source',
                    sourceConnected:a.isConnected,sourceFocused:document.activeElement===a,pageFocused:document.hasFocus(),
                    sourceRect:[r.left,r.top,r.width,r.height],selectedRect:q?[q.l,q.t,q.r-q.l,q.b-q.t]:null,
                    overlayCount:hosts.length,overlayRect:hosts.length===1?rect(hosts[0]):null}};})()
                """, ct);
            LastSourceClickObservation = point.GetProperty("observation").Clone();
            Require(point.GetProperty("sourcePointReady").GetBoolean(), "source-click-no-exposed-interior");
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
            string phase = "send";
            try
            {
                await _socket.SendAsync(request, WebSocketMessageType.Text, true, ct);
                phase = "receive";
                byte[] buffer = new byte[16 * 1024];
                while (true)
                {
                    phase = "receive";
                    using var message = new MemoryStream();
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        Require(received.MessageType == WebSocketMessageType.Text, "owned-cdp-disconnected");
                        await message.WriteAsync(buffer.AsMemory(0, received.Count), ct);
                        Require(message.Length <= 4 * 1024 * 1024, "owned-cdp-response-limit");
                    } while (!received.EndOfMessage);
                    phase = "decode";
                    using var response = JsonDocument.Parse(message.ToArray());
                    var root = response.RootElement;
                    if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                    {
                        Diagnostics.Observe(root, _page, _pageTarget, _mainFrame, _navigationUrl);
                        continue;
                    }
                    phase = "response";
                    Require(!root.TryGetProperty("error", out _), "cdp-" + method.Replace('.', '-') + "-failed");
                    return root.GetProperty("result").Clone();
                }
            }
            catch (Exception error)
            {
                Diagnostics.RecordFailure(method, phase, error, ct.CanBeCanceled ? ct.IsCancellationRequested : null);
                throw;
            }
        }

        public void Dispose() => _socket.Dispose();
    }
}
