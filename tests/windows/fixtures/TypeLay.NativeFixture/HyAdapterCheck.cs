using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// A single explicitly requested fixture sequence, never part of the normal native mode.
internal static class HyAdapterCheck
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string ModelHash = "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699";
    private const string TemplateHash = "b7491ec0e9c869dfce20f2176758099bf248d979dd05530ede99deb21698acee";
    private const string RuntimeHash = "ccb5971d01c039bb90ff90957678adada36b9eba639f2e6d6697a09fc75a1404";
    private const string Literal = "字面\\n与\\t及\\\\；真实换行\n下一行\t末尾\r\n<｜hy_begin▁of▁sentence｜><｜hy_end▁of▁sentence｜><｜hy_▁pad▁｜><｜hy_fim▁hole｜><｜hy_fim▁begin｜><｜hy_fim▁end｜><｜hy_User｜><｜hy_Assistant｜><｜hy_EOT｜><｜hy_place▁holder▁no▁11｜><｜hy_place▁holder▁no▁12｜><｜hy_place▁holder▁no▁13｜><｜hy_place▁holder▁no▁14｜><｜hy_place▁holder▁no▁15｜><｜hy_place▁holder▁no▁16｜><｜hy_place▁holder▁no▁17｜><｜hy_place▁holder▁no▁18｜><｜hy_place▁holder▁no▁19｜><｜hy_place▁holder▁no▁0｜><｜hy_place▁holder▁no▁1｜><｜hy_place▁holder▁no▁2｜><｜hy_place▁holder▁no▁3｜><｜hy_place▁holder▁no▁4｜><｜hy_place▁holder▁no▁5｜><｜hy_place▁holder▁no▁6｜><｜hy_place▁holder▁no▁7｜><｜hy_place▁holder▁no▁8｜><｜hy_place▁holder▁no▁9｜><｜hy_place▁holder▁no▁10｜><think></think><answer></answer><tool_call></tool_call><tool_calls></tool_calls><tool_response></tool_response>/think/no_think<tool_responses></tool_responses><tool_sep>\n重复<｜hy_User｜><｜hy_Assistant｜>尾巴";

    internal static int Run(string[] args)
    {
        if (args.Length != 6 || args.Any(path => !Path.IsPathFullyQualified(path)))
            return 2;
        string results = args[5];
        Directory.CreateDirectory(results);
        string resultPath = Path.Combine(results, "result.json");
        if (File.Exists(resultPath))
            throw new IOException("hy-result-already-exists");
        return RunAsync(args, resultPath).GetAwaiter().GetResult();
    }
    private static async Task<int> RunAsync(string[] args, string resultPath)
    {
        var clock = Stopwatch.StartNew();
        string stage = "cancel-start";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(165));
        var events = new List<JsonElement>();
        var records = new List<object>();
        var report = new Dictionary<string, object?>
        {
            ["scope"] = "Windows isolated ABI5: real-decode cancellation, job-zero/reap, restart and one original zh-ja EOG plus canonical audits; not product or quality certification",
            ["passed"] = false,
            ["started_utc"] = DateTimeOffset.UtcNow,
            ["events"] = events,
            ["workers"] = records,
            ["execution_seconds_limit"] = 180,
            ["operation_seconds_limit"] = 165,
            ["cleanup_reserve_seconds"] = 15,
            ["request_seconds_limit"] = 60,
            ["memory_limit_bytes"] = HyOwnedWorker.MemoryLimit,
            ["resource_semantics"] = "Windows process/job committed-memory cap; not Linux RLIMIT_AS or an RSS cap"
        };
        try
        {
            using (var worker = new HyOwnedWorker(args[0], args[1], args[2], args[3], args[4], Path.Combine(args[5], "cancel.stderr")))
            {
                var workerClock = Stopwatch.StartNew();
                try
                {
                    stage = "cancel-ready";
                    JsonElement ready = await Event(worker, events, deadline.Token);
                    CheckReady(ready, worker, args[3]);
                    stage = "cancel-decode";
                    await Send(worker, new
                    {
                        id = "cancel_decode",
                        op = "translate",
                        direction = "zh-ja",
                        text = string.Concat(Enumerable.Repeat("这是一段用于验证取消操作的合成文本。请保留备份，等待确认后再继续。", 12))
                    }, deadline.Token);
                    JsonElement entered = await Event(worker, events, deadline.Token);
                    Require(entered.GetProperty("event").GetString() == "decode_entered" &&
                        entered.GetProperty("id").GetString() == "cancel_decode" && entered.GetProperty("decode_callbacks").GetUInt32() > 0, "real-decode-cancel-gate");
                    stage = "cancel-reap";
                    await worker.Stop(true);
                    Require(worker.Reaped && worker.ActiveAfterExit == 0 && worker.ExitCode == 2, "cancel-reap");
                    report["cancel"] = new
                    {
                        real_decode_observed = true,
                        result_received = false,
                        active_processes = worker.ActiveAfterExit,
                        reap_seconds = worker.ReapSeconds
                    };
                }
                finally { await CleanupAndRecord(worker, records, workerClock); }
            }
            // No second worker exists before the prior job and root have both ended.
            stage = "normal-start";
            using (var worker = new HyOwnedWorker(args[0], args[1], args[2], args[3], args[4], Path.Combine(args[5], "normal.stderr")))
            {
                var workerClock = Stopwatch.StartNew();
                try
                {
                    stage = "normal-ready";
                    JsonElement ready = await Event(worker, events, deadline.Token);
                    CheckReady(ready, worker, args[3]);
                    report["ready"] = ready;
                    report["loaded_runtime_modules"] = worker.RuntimeModules();
                    report["launch_to_ready_seconds"] = worker.ElapsedSeconds;
                    stage = "normal-eog";
                    var requestClock = Stopwatch.StartNew();
                    await Send(worker, new
                    {
                        id = "success_zh_ja",
                        op = "translate",
                        direction = "zh-ja",
                        text = "我没有同意删除备份。"
                    }, deadline.Token);
                    JsonElement translated = await Result(worker, "success_zh_ja", events, deadline.Token);
                    report["request_roundtrip_seconds"] = requestClock.Elapsed.TotalSeconds;
                    report["launch_to_translation_result_seconds"] = worker.ElapsedSeconds;
                    Require(translated.GetProperty("code").GetInt32() == 0 && translated.GetProperty("complete").GetBoolean() &&
                        translated.GetProperty("completion_reason").GetString() == "eog" &&
                        translated.GetProperty("metrics").GetProperty("eog_token").GetInt32() == 120020 &&
                        !string.IsNullOrWhiteSpace(translated.GetProperty("text").GetString()), "translation-eog");
                    Require(translated.GetProperty("metrics").GetProperty("roundtrip_ok").GetInt32() == 1 &&
                        translated.GetProperty("metrics").GetProperty("forbidden_source_tokens").GetInt32() == 0, "translation-source-boundary");
                    report["translation"] = translated;
                    stage = "normal-literal";
                    await Send(worker, new
                    {
                        id = "literal_audit",
                        op = "audit",
                        text = Literal
                    }, deadline.Token);
                    JsonElement literal = await Result(worker, "literal_audit", events, deadline.Token);
                    JsonElement metric = literal.GetProperty("metrics"), source = literal.GetProperty("source_audit");
                    Require(literal.GetProperty("code").GetInt32() == 0 && literal.GetProperty("validated_only").GetBoolean() &&
                        metric.GetProperty("roundtrip_ok").GetInt32() == 1 && metric.GetProperty("forbidden_source_tokens").GetInt32() == 0 &&
                        metric.GetProperty("split_count").GetInt32() == 47 && metric.GetProperty("decode_calls").GetInt32() == 0 &&
                        metric.GetProperty("user_defined_vocab_count").GetInt32() == 15, "literal-token-boundary");
                    Require(source.GetProperty("original_sha256").GetString() == "e870ee09a4a9036cb0483aec5545302d7b2838a2baa048db9acca818d2ebfc76" &&
                        source.GetProperty("canonical_sha256").GetString() == "d4d7ac72a43318ae349ea1e68b2fea9a72a0a358efb5c4dc6816cb0d1f22417c" &&
                        source.GetProperty("token_roundtrip_sha256").GetString() == source.GetProperty("canonical_sha256").GetString(), "literal-canonical-bytes");
                    report["literal_audit"] = literal;
                    report["literal_original_hex"] = Convert.ToHexString(Utf8.GetBytes(Literal)).ToLowerInvariant();
                    report["literal_canonical_hex"] = Convert.ToHexString(Utf8.GetBytes(Literal.Replace("\r\n", "\n", StringComparison.Ordinal))).ToLowerInvariant();
                    stage = "normal-barecr";
                    await Send(worker, new
                    {
                        id = "bare_cr_refusal",
                        op = "audit",
                        text = "前\r后"
                    }, deadline.Token);
                    JsonElement rejected = await Result(worker, "bare_cr_refusal", events, deadline.Token);
                    Require(rejected.GetProperty("code").GetInt32() == 3 && rejected.GetProperty("error").GetString() == "standalone CR is not supported" &&
                        rejected.GetProperty("metrics").GetProperty("decode_calls").GetInt32() == 0, "bare-cr-refusal");
                    report["bare_cr_refusal"] = rejected;
                    stage = "normal-reap";
                    await worker.Stop(false);
                    Require(worker.Reaped && worker.ActiveAfterExit == 0 && worker.ExitCode == 0, "normal-reap");
                }
                finally { await CleanupAndRecord(worker, records, workerClock); }
            }
            stage = "complete";
            report["passed"] = true;
        }
        catch (Exception error)
        {
            report["error"] = "hy-fixture-failed";
            report["exception_type"] = error.GetType().Name;
            if (error is FixtureCheckException check)
                report["check_code"] = check.Code;
            if (error is Win32Exception native)
                report["win32_error"] = native.NativeErrorCode;
        }
        finally
        {
            report["stage"] = stage;
            report["elapsed_seconds"] = clock.Elapsed.TotalSeconds;
            report["adapter_sha256"] = File.Exists(args[4]) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[4]))).ToLowerInvariant() : null;
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n", Utf8);
        }
        return Equals(report["passed"], true) ? 0 : 1;
    }
    private static async Task CleanupAndRecord(HyOwnedWorker worker, List<object> records, Stopwatch clock)
    {
        try
        {
            if (!worker.Reaped)
                await worker.Stop(true);
        }
        finally
        {
            records.Add(new
            {
                pid = worker.ProcessId,
                before_resume = worker.BeforeResume,
                reaped = worker.Reaped,
                active_after_exit = worker.ActiveAfterExit,
                exit_code = worker.ExitCode,
                reap_seconds = worker.ReapSeconds,
                elapsed_seconds = clock.Elapsed.TotalSeconds,
                samples = worker.Samples
            });
        }
    }
    private static async Task<JsonElement> Event(HyOwnedWorker worker, List<JsonElement> events, CancellationToken cancellation)
    {
        using var eventDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        eventDeadline.CancelAfter(TimeSpan.FromSeconds(65));
        Task<byte[]> read = worker.ReadLine(eventDeadline.Token);
        while (!read.IsCompleted)
        {
            await Task.WhenAny(read, Task.Delay(25, eventDeadline.Token));
            eventDeadline.Token.ThrowIfCancellationRequested();
            worker.Sample();
        }
        using JsonDocument document = JsonDocument.Parse(await read);
        JsonElement result = document.RootElement.Clone();
        events.Add(result);
        return result;
    }
    private static async Task<JsonElement> Result(HyOwnedWorker worker, string id, List<JsonElement> events, CancellationToken cancellation)
    {
        while (true)
        {
            JsonElement result = await Event(worker, events, cancellation);
            if (result.GetProperty("event").GetString() == "decode_entered")
            {
                Require(result.GetProperty("id").GetString() == id, "decode-event-id");
                continue;
            }
            Require(result.GetProperty("event").GetString() == "result" && result.GetProperty("id").GetString() == id, "result-id");
            return result;
        }
    }
    private static Task Send(HyOwnedWorker worker, object request, CancellationToken cancellation) =>
        worker.Send(Utf8.GetBytes(JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n"), cancellation);
    private static void CheckReady(JsonElement ready, HyOwnedWorker worker, string runtime)
    {
        Require(ready.GetProperty("event").GetString() == "ready" && ready.GetProperty("abi").GetInt32() == 5 &&
            ready.GetProperty("model_sha256").GetString() == ModelHash && ready.GetProperty("runtime_sha256").GetString() == RuntimeHash &&
            ready.GetProperty("template_sha256").GetString() == TemplateHash && ready.GetProperty("ctx").GetInt32() == 1024 &&
            ready.GetProperty("threads").GetInt32() == 2, "ready-identity");
        string selected = ready.GetProperty("cpu_backend").GetString()!;
        int score = ready.GetProperty("cpu_backend_score").GetInt32();
        JsonElement scores = ready.GetProperty("cpu_selection").GetProperty("scores");
        Require(scores.GetArrayLength() == 14 && score > 0 && score == scores.EnumerateArray().Max(item => item.GetProperty("score").GetInt32()), "cpu-maximum-positive");
        string[] modules = worker.RuntimeModules();
        Require(modules.Length > 0 && modules.Any(path => string.Equals(Path.GetFileName(path), selected, StringComparison.OrdinalIgnoreCase)), "cpu-actual-module");
        foreach (string path in modules)
        {
            string name = Path.GetFileName(path);
            Require(string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtime)), StringComparison.OrdinalIgnoreCase) &&
                (name.Equals(selected, StringComparison.OrdinalIgnoreCase) || name is "llama.dll" or "ggml.dll" or "ggml-base.dll"), "cpu-module-closure");
        }
    }
    // Only Require below constructs this exception, with fixed fixture-owned codes.
    private sealed class FixtureCheckException(string code) : Exception
    {
        internal string Code { get; } = code;
    }
    private static void Require(bool condition, string code)
    {
        if (!condition)
            throw new FixtureCheckException(code);
    }
}
