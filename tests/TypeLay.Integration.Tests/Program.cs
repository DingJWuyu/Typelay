using System.Buffers.Binary;
using System.Text.Json;
using TypeLay.Core;
using TypeLay.Protocol;

internal static class Program
{
    private static readonly List<(string Name, Action Test)> Tests = [];
    private static void Case(string name, Action test) => Tests.Add((name, test));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("assertion"); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("missing-error"); }
    private static int Main()
    {
        Case("wire-model-round-trip-unicode", () =>
        {
            using var stream = new MemoryStream(); var request = new ModelRequest(7, "translate", "你好 🙂\r\nworld");
            Wire.WriteAsync(stream, request).AsTask().GetAwaiter().GetResult(); stream.Position = 0;
            Check(Wire.ReadAsync<ModelRequest>(stream).AsTask().GetAwaiter().GetResult() == request);
        });
        Case("wire-web-case-contract", () =>
        { string json = JsonSerializer.Serialize(new ModelRequest(1, "hello"), Wire.Json); Check(json.Contains("\"op\":\"hello\"", StringComparison.Ordinal)); });
        Case("wire-keeps-source-input-version-separate-from-editor-keystrokes", () =>
        {
            var request = new BrokerRequest(Wire.Version, 7, "capture", "", UserEpoch: 10, SourceUserEpoch: 2);
            using var stream = new MemoryStream(); Wire.WriteAsync(stream, request).AsTask().GetAwaiter().GetResult();
            stream.Position = 0; var received = Wire.ReadAsync<BrokerRequest>(stream).AsTask().GetAwaiter().GetResult();
            Check(received.UserEpoch == 10 && received.SourceUserEpoch == 2);
        });
        Case("wire-preserves-preview-only-ime-prefix", () =>
        {
            var target = new TargetId(1, 2, 3, 4, "uia-value-text-v2", "field", Guid.NewGuid());
            var snapshot = new InputSnapshot(target, "", new(0, 0), new(1, 2, 300, 30), false,
                FieldAccess.Supported, ImeState.Composing, 1000, 5, 6, false, "你好");
            using var stream = new MemoryStream(); var response = new BrokerResponse(Wire.Version, 7, "observed", snapshot, FocusMember.Source);
            Wire.WriteAsync(stream, response).AsTask().GetAwaiter().GetResult(); stream.Position = 0;
            var received = Wire.ReadAsync<BrokerResponse>(stream).AsTask().GetAwaiter().GetResult();
            Check(received.Snapshot == snapshot && received.Snapshot!.CommittedPrefix == "你好" && !received.Snapshot.WriteSupported);
        });
        Case("wire-preserves-unconfirmed-preview-without-promoting-ime", () =>
        {
            var target = new TargetId(1, 2, 3, 4, "uia-value-text-v2", "field", Guid.NewGuid());
            var snapshot = new InputSnapshot(target, "", new(0, 0), new(1, 2, 300, 30), false,
                FieldAccess.Supported, ImeState.Unknown, 1000, 5, 6, false, PreviewText: "你好");
            using var stream = new MemoryStream(); Wire.WriteAsync(stream, snapshot).AsTask().GetAwaiter().GetResult();
            stream.Position = 0; var received = Wire.ReadAsync<InputSnapshot>(stream).AsTask().GetAwaiter().GetResult();
            Check(received == snapshot && !received.IsUsable && !received.WriteSupported && received.CommittedPrefix is null);
        });
        foreach (int length in new[] { -1, 0, Wire.MaxFrameBytes + 1, int.MaxValue })
            Case("wire-reject-size-" + length, () => { byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
                using var stream = new MemoryStream(prefix); Throws<InvalidDataException>(() => Wire.ReadAsync<ModelRequest>(stream).AsTask().GetAwaiter().GetResult()); });
        Case("wire-reject-truncated-header", () => { using var stream = new MemoryStream(new byte[3]); Throws<EndOfStreamException>(() => Wire.ReadAsync<ModelRequest>(stream).AsTask().GetAwaiter().GetResult()); });
        Case("wire-reject-truncated-body", () => { using var stream = new MemoryStream(new byte[] { 5,0,0,0,123 }); Throws<EndOfStreamException>(() => Wire.ReadAsync<ModelRequest>(stream).AsTask().GetAwaiter().GetResult()); });
        Case("wire-reject-null", () => { using var stream = new MemoryStream(new byte[] { 4,0,0,0,110,117,108,108 }); Throws<InvalidDataException>(() => Wire.ReadAsync<ModelRequest>(stream).AsTask().GetAwaiter().GetResult()); });
        Case("uia-selection-normal", () => Check(TextMapping.Selection("A你好B", "A你好B", "A", "你好") == new TextRange(1,3)));
        Case("uia-selection-crlf-normalization", () => Check(TextMapping.Selection("A\r\n你好B", "A\n你好B", "A\n", "你好") == new TextRange(3,5)));
        Case("uia-selection-surrogate-safe", () => Check(TextMapping.Selection("🙂你好", "🙂你好", "🙂", "你好") == new TextRange(2,4)));
        Case("uia-selection-cannot-split-surrogate", () => Check(TextMapping.ToSourceOffset("🙂你好", "\ud83d") is null));
        Case("uia-selection-text-mismatch", () => Check(TextMapping.Selection("abc", "abd", "a", "b") is null));
        Case("uia-selection-prefix-mismatch", () => Check(TextMapping.Selection("abc", "abc", "z", "b") is null));
        Case("uia-selection-empty-caret", () => Check(TextMapping.Selection("abc", "abc", "ab", "") == new TextRange(2,2)));
        Case("uia-selection-caret-at-end", () => Check(TextMapping.Selection("A\r\n", "A\n", "A\n", "") == new TextRange(3,3)));
        var normal = new FieldMetadata("msedge", "Message", "body", "", "multiline=true", false, true, false, false, true, true, true, true);
        Case("policy-allows-labelled-message", () => Check(FieldPolicy.Allows(normal, [])));
        Case("policy-allows-chinese-comment", () => Check(FieldPolicy.Allows(normal with { Label = "评论", AutomationId = "" }, [])));
        foreach (var text in new[] { "Password message", "PIN message", "CVV comment", "message verification", "API key reply", "银行卡消息", "信用卡正文", "消息验证码", "private token message", "message terminal", "shell comment", "monaco body" })
            Case("policy-sensitive-or-machine-" + text, () => Check(!FieldPolicy.Allows(normal with { Label = text }, [])));
        Case("policy-unlabelled-edit-is-discovered", () => Check(FieldPolicy.Allows(normal with { Label = "", AutomationId = "" }, [])));
        Case("policy-desktop-edit-is-discovered", () => Check(FieldPolicy.Allows(normal with { Process = "notepad", InDocument = false }, [])));
        Case("policy-windows-search-is-discovered", () => Check(FieldPolicy.Allows(normal with { Process = "searchhost", Label = "Search", InDocument = false }, [])));
        Case("policy-own-app-is-excluded", () => Check(!FieldPolicy.Allows(normal with { Process = "typelay.app" }, [])));
        Case("policy-browser-address-bar-is-excluded", () => Check(!FieldPolicy.Allows(normal with { Label = "address bar", InDocument = false }, [])));
        Case("policy-exclusion-case-insensitive", () => Check(!FieldPolicy.Allows(normal, ["MSEDGE"])));
        Case("policy-password-first", () => Check(!FieldPolicy.Allows(normal with { Password = true }, [])));
        Case("policy-textedit-is-not-a-discovery-requirement", () => Check(FieldPolicy.Allows(normal with { HasTextEdit = false }, [])));
        Case("policy-readonly", () => Check(!FieldPolicy.Allows(normal with { ReadOnly = true }, [])));
        Case("policy-offscreen", () => Check(!FieldPolicy.Allows(normal with { Offscreen = true }, [])));
        Case("policy-disabled", () => Check(!FieldPolicy.Allows(normal with { Enabled = false }, [])));
        Case("policy-text-only-editor-is-discovered", () => Check(FieldPolicy.Allows(normal with { HasValue = false }, [])));
        Case("manual-return-failure-preserves-draft", () =>
        {
            var session = Ready(); Check(session.BeginEdit()); session.ChangeDraft("Hand edited"); Check(session.RequestSourceReturn());
            Check(session.CancelSourceReturn()); Check(session.State == SessionState.EditingTranslation && session.Draft == "Hand edited");
        });
        Case("explicit-regenerate-reopens-valid-candidate", () =>
        {
            var session = Ready(); Check(session.Retranslate(1500)); Check(session.State == SessionState.Translating);
            Check(session.TakeTranslationJob(1799) is null); Check(session.TakeTranslationJob(1800) is not null);
        });
        var results = new List<object>(); int failed = 0;
        foreach (var (name, body) in Tests)
        { try { body(); results.Add(new { name, status = "passed" }); } catch (Exception ex) { failed++; results.Add(new { name, status = "failed", errorType = ex.GetType().Name }); } }
        Console.WriteLine(JsonSerializer.Serialize(new { suite = "protocol-policy-and-session", kind = "logic-only-not-Windows-GUI", total = Tests.Count, passed = Tests.Count - failed, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
    private static Session Ready()
    {
        var target = new TargetId(1, 100, 2, 3, "test", "element", Guid.NewGuid());
        var snapshot = new InputSnapshot(target,"你好",new TextRange(2,2),new RectPx(100,400,400,50),true,FieldAccess.Supported,ImeState.Clear,1000,1,1);
        var session = new Session(new("auto","en")); session.Observe(snapshot,FocusMember.Source,1000);
        var job=session.TakeTranslationJob(1300) ?? throw new InvalidOperationException(); Check(session.Complete(job,"Hello"));
        session.Observe(snapshot with { ObservedAtMs = 1300 },FocusMember.Source,1300); return session;
    }
}
