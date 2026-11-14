using System.IO;
using System.Text.Json;

internal static partial class BrowserCooperationCheck
{
    private sealed class OwnerRecoveryObservation
    {
        public string Boundary { get; } = "one-identical-style-mutation-before-real-owner-preparation-reply";
        public JsonElement? Injection
        {
            get; set;
        }
        public JsonElement[] HostEvents { get; set; } = [];
        public bool BreakpointRemoved
        {
            get; set;
        }
        public bool ShippedSourceMatched
        {
            get; set;
        }
        public int ReplyLine
        {
            get; set;
        }
        public bool Verified
        {
            get; set;
        }
    }
    private static string? RecoveryText(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static JsonElement[] ReadRecoveryEvents(string path, long offset)
    {
        if (!File.Exists(path))
            return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Require(stream.Length <= 32 * 1024 * 1024, "trace-size-limit");
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var events = new List<JsonElement>();
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var entry = document.RootElement;
                if (RecoveryText(entry, "operation") is "browser-owner" or "browser-owner-source-refresh")
                    events.Add(entry.Clone());
            }
            catch (JsonException) { } // The writer can still own the final partial line.
        }
        return events.ToArray();
    }
    private static bool RecoveryStage(JsonElement entry, string stage, string reason, string? owner = null)
    {
        var detail = entry.GetProperty("detail");
        return RecoveryText(entry, "operation") == "browser-owner" && RecoveryText(detail, "stage") == stage
            && RecoveryText(detail, "firstReason") == reason && (owner is null || RecoveryText(detail, "ownerId") == owner);
    }
    private static async Task WaitRejectedOwnerAsync(Cdp cdp, string trace, long offset, OwnerRecoveryObservation observation, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            observation.Injection = await cdp.ReadOwnerInjectionAsync(ct);
            observation.HostEvents = ReadRecoveryEvents(trace, offset);
            if (observation.Injection is { ValueKind: JsonValueKind.Object } injection)
            {
                Require(injection.GetProperty("hits").GetInt32() == 1
                    && JsonElement.DeepEquals(injection.GetProperty("before"), injection.GetProperty("after")),
                    "owner-rejection-style-injection-invalid");
                var original = injection.GetProperty("preparations")[0];
                string owner = original.GetProperty("ownerId").GetString()!;
                var events = observation.HostEvents;
                int rejection = Array.FindIndex(events, entry => RecoveryStage(entry, "InitialAck", "PageNotActive", owner)
                    && RecoveryText(entry.GetProperty("detail"), "pageReason") == "Calibration"
                    && RecoveryText(entry.GetProperty("detail"), "clockReason") == "AuthorityMissing"
                    && RecoveryText(entry.GetProperty("detail"), "preparationToken") == RecoveryText(original, "token"));
                int retirement = rejection < 0 ? -1
                    : Array.FindIndex(events, rejection + 1, entry => RecoveryStage(entry, "RetireAck", "Ready", owner));
                int restore = retirement < 0 ? -1
                    : Array.FindIndex(events, retirement + 1, entry => RecoveryStage(entry, "Restore", "Ready"));
                Require(!events.Any(entry => RecoveryText(entry, "operation") == "browser-owner-source-refresh"),
                    "owner-recovered-before-new-source-input");
                if (rejection >= 0 && retirement > rejection && restore > retirement)
                    return;
            }
            await Task.Delay(10, ct);
        }
    }
    private static async Task VerifyOwnerRecoveryAsync(Cdp cdp, string trace, long offset, OwnerRecoveryObservation observation, CancellationToken ct)
    {
        observation.Injection = await cdp.ReadOwnerInjectionAsync(ct);
        observation.HostEvents = ReadRecoveryEvents(trace, offset);
        var injection = observation.Injection ?? throw new Failure("owner-rejection-injection-missing");
        var preparations = injection.GetProperty("preparations");
        Require(injection.GetProperty("hits").GetInt32() == 1 && preparations.GetArrayLength() == 2
            && injection.GetProperty("preparationCount").GetInt32() == 2, "owner-recovery-preparation-count");
        var original = preparations[0];
        var fresh = preparations[1];
        Require(RecoveryText(original, "ownerId") != RecoveryText(fresh, "ownerId")
            && RecoveryText(original, "token") != RecoveryText(fresh, "token")
            && RecoveryText(original, "target") == RecoveryText(fresh, "target")
            && fresh.GetProperty("input").GetInt64() > original.GetProperty("input").GetInt64()
            && fresh.GetProperty("content").GetInt64() > original.GetProperty("content").GetInt64()
            && fresh.GetProperty("focus").GetInt64() == original.GetProperty("focus").GetInt64(),
            "owner-recovery-new-source-unconfirmed");
        var events = observation.HostEvents;
        int refresh = Array.FindIndex(events, entry => RecoveryText(entry, "operation") == "browser-owner-source-refresh"
            && RecoveryText(entry.GetProperty("detail"), "previousToken") == RecoveryText(original, "token"));
        int preparation = Array.FindIndex(events, entry => RecoveryStage(entry, "Prepare", "Ready", RecoveryText(fresh, "ownerId"))
            && RecoveryText(entry.GetProperty("detail"), "preparationToken") == RecoveryText(fresh, "token"));
        int ack = Array.FindIndex(events, entry => RecoveryStage(entry, "InitialAck", "Ready", RecoveryText(fresh, "ownerId"))
            && RecoveryText(entry.GetProperty("detail"), "preparationToken") == RecoveryText(fresh, "token"));
        Require(refresh >= 0 && preparation > refresh && ack > preparation, "owner-recovery-host-chain-unconfirmed");
        var change = events[refresh].GetProperty("detail");
        Require(change.GetProperty("previousContent").GetInt64() == original.GetProperty("content").GetInt64()
            && change.GetProperty("previousInput").GetInt64() == original.GetProperty("input").GetInt64()
            && change.GetProperty("currentContent").GetInt64() > change.GetProperty("previousContent").GetInt64()
            && change.GetProperty("currentInput").GetInt64() > change.GetProperty("previousInput").GetInt64(),
            "owner-recovery-host-source-change-unconfirmed");
        observation.Verified = true;
    }
    private sealed partial class Cdp
    {
        private string? _recoveryScriptUrl, _recoveryScriptId, _recoveryBreakpoint;
        private int _recoveryContext, _recoveryScriptCount;
        private void ObserveRecoveryScript(JsonElement message)
        {
            Require(_recoveryScriptUrl is null || RecoveryText(message, "method") != "Debugger.paused",
                "owner-rejection-debugger-paused");
            if (_recoveryScriptUrl is null || RecoveryText(message, "method") != "Debugger.scriptParsed"
                || RecoveryText(message, "sessionId") != _page)
                return;
            var script = message.GetProperty("params");
            if (RecoveryText(script, "url") != _recoveryScriptUrl)
                return;
            _recoveryScriptCount++;
            _recoveryScriptId = script.GetProperty("scriptId").GetString();
            _recoveryContext = script.GetProperty("executionContextId").GetInt32();
        }
        public async Task ArmOwnerRejectionAsync(string distribution, string extensionId, OwnerRecoveryObservation observation, CancellationToken ct)
        {
            _recoveryScriptUrl = "chrome-extension://" + extensionId + "/content.js";
            await PageCallAsync("Debugger.enable", new
            {
            }, ct);
            Require(_recoveryScriptCount == 1 && _recoveryScriptId is not null, "owner-rejection-script-ambiguous");
            var script = await PageCallAsync("Debugger.getScriptSource", new
            {
                scriptId = _recoveryScriptId
            }, ct);
            string actual = script.GetProperty("scriptSource").GetString()!;
            string shipped = await File.ReadAllTextAsync(Path.Combine(distribution, "browser", "chromium", "content.js"), ct);
            Require(actual == shipped, "owner-rejection-script-not-shipped-source");
            observation.ShippedSourceMatched = true;
            const string marker = "    if (message.id) port.postMessage({id: message.id, data: result});";
            int position = actual.IndexOf(marker, StringComparison.Ordinal);
            Require(position >= 0 && actual.LastIndexOf(marker, StringComparison.Ordinal) == position,
                "owner-rejection-reply-boundary-ambiguous");
            int line = actual[..position].Count(character => character == '\n');
            observation.ReplyLine = line + 1;
            var breakpoint = await PageCallAsync("Debugger.setBreakpoint", new
            {
                location = new
                {
                    scriptId = _recoveryScriptId,
                    lineNumber = line,
                    columnNumber = 4
                },
                // This condition never pauses. Only the first real preparation
                // reassigns identical DOM styles; every protocol reply stays intact.
                condition = """
                    (() => {
                      if (message.op !== 'prepare-command-owner' || !result) return false;
                      const proof = globalThis.__typelayOwnerRecovery ??= {hits:0,preparations:[],preparationCount:0};
                      const a=result.authority;
                      proof.preparationCount++;
                      if (proof.preparations.length<2) proof.preparations.push({ownerId:result.ownerId,token:a.token,
                        target:a.target,content:a.contentRevision,input:a.inputRevision,focus:a.focusRevision});
                      if (proof.hits) return false;
                      const fields=[document.body,document.activeElement];
                      proof.before=fields.map(f=>f.getAttribute('style'));
                      fields.forEach((f,i)=>f.setAttribute('style',proof.before[i]));
                      proof.after=fields.map(f=>f.getAttribute('style'));
                      proof.hits++;
                      return false;
                    })()
                    """
            }, ct);
            _recoveryBreakpoint = breakpoint.GetProperty("breakpointId").GetString();
            Require(breakpoint.GetProperty("actualLocation").GetProperty("lineNumber").GetInt32() == line
                && RecoveryText(breakpoint.GetProperty("actualLocation"), "scriptId") == _recoveryScriptId,
                "owner-rejection-breakpoint-moved");
        }
        public async Task<JsonElement?> ReadOwnerInjectionAsync(CancellationToken ct)
        {
            var response = await PageCallAsync("Runtime.evaluate", new
            {
                expression = "globalThis.__typelayOwnerRecovery ?? null",
                contextId = _recoveryContext,
                returnByValue = true
            }, ct);
            Require(!response.TryGetProperty("exceptionDetails", out _), "owner-rejection-observation-failed");
            return response.GetProperty("result").GetProperty("value").Clone();
        }
        public async Task RemoveOwnerInjectionAsync(OwnerRecoveryObservation observation, CancellationToken ct)
        {
            if (_recoveryBreakpoint is null)
                return;
            await PageCallAsync("Debugger.removeBreakpoint", new
            {
                breakpointId = _recoveryBreakpoint
            }, ct);
            _recoveryBreakpoint = null;
            await PageCallAsync("Debugger.disable", new
            {
            }, ct);
            _recoveryScriptUrl = null;
            await PageCallAsync("Runtime.evaluate", new
            {
                expression = "delete globalThis.__typelayOwnerRecovery",
                contextId = _recoveryContext,
                returnByValue = true
            }, ct);
            observation.BreakpointRemoved = true;
        }
    }
}
