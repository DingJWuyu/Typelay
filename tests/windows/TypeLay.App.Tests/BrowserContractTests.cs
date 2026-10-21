using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using TypeLay.Core;
using TypeLay.Protocol;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class BrowserContractTests
{
    [Fact]
    public void ReturnedSourceRequiresTheCurrentFormAndActualReturnReceiptForReadOnlyObservation()
    {
        var target = new TargetId("windows-browser", Document + ":1", Guid.NewGuid());
        var token = new SessionViewToken(target, Guid.NewGuid(), 4);
        var entry = new SessionGlossaryEntry(Guid.NewGuid(), token, 2, [], SourceOrigin: true);
        JsonElement Description(Guid formId, SessionViewToken receiptToken, bool focused = true, bool supported = true,
            int version = 0, SessionGlossaryFocus? focus = null) => JsonSerializer.SerializeToElement(new
            {
                sessionBindings = new
                {
                    sourceReady = false,
                    editorReady = false,
                    sourceLocalAcceptanceVersion = version,
                    target = target.Value
                },
                focused,
                sourceGlossarySupported = supported,
                glossaryFocus = focus,
                glossarySource = new
                {
                    formId,
                    token = receiptToken
                }
            }, BrowserServer.Json);
        var description = Description(entry.FormId, token);
        var ack = BrowserInput.ApproveObservationRoles(description, target, token.CommandGeneration,
            SessionShortcutBindings.Default, entry, returned: entry);
        Assert.NotNull(ack);
        Assert.False(ack.SourceReady);
        Assert.False(ack.EditorReady);
        Assert.Equal(0, ack.SourceLocalAcceptanceVersion);
        Assert.Null(BrowserInput.ApproveObservationRoles(description, target, token.CommandGeneration,
            SessionShortcutBindings.Default, entry)); // A page assertion alone is not a return receipt.
        Assert.Null(BrowserInput.ApproveObservationRoles(description, target, token.CommandGeneration,
            SessionShortcutBindings.Default, returned: entry)); // The form must still be current.
        Assert.Null(BrowserInput.ApproveObservationRoles(description, target, Guid.NewGuid(),
            SessionShortcutBindings.Default, entry, returned: entry));
        foreach (var refused in new[]
        {
            Description(Guid.NewGuid(), token),
            Description(entry.FormId, token with { Revision = 5 }),
            Description(entry.FormId, token with { Target = target with { Lifetime = Guid.NewGuid() } }),
            Description(entry.FormId, token, focused: false),
            Description(entry.FormId, token, supported: false),
            Description(entry.FormId, token, version: 1),
            Description(entry.FormId, token, focus: new(entry.FormId, token, ImeState.Clear))
        })
            Assert.Null(BrowserInput.ApproveObservationRoles(refused, target, token.CommandGeneration,
                SessionShortcutBindings.Default, entry, returned: entry));
        var editorEntry = entry with
        {
            SourceOrigin = false
        };
        Assert.Null(BrowserInput.ApproveObservationRoles(description, target, token.CommandGeneration,
            SessionShortcutBindings.Default, editorEntry, returned: editorEntry));
    }

    [Fact]
    public void CurrentAuxiliaryObservationRetainsReadOnlyAcknowledgementWithoutSourceOrEditorTickets()
    {
        var target = new TargetId("windows-browser", Document + ":1", Guid.NewGuid());
        var token = new SessionViewToken(target, Guid.NewGuid(), 4);
        var entry = new SessionGlossaryEntry(Guid.NewGuid(), token, 2, []);
        var focus = new SessionGlossaryFocus(entry.FormId, token, ImeState.Clear);
        JsonElement Description(SessionGlossaryFocus? reported, int version = 0) => JsonSerializer.SerializeToElement(new
        {
            sessionBindings = new
            {
                sourceReady = false,
                editorReady = false,
                sourceLocalAcceptanceVersion = version,
                target = target.Value
            },
            glossaryFocus = reported
        }, BrowserServer.Json);
        var description = Description(focus);
        var ack = BrowserInput.ApproveObservationRoles(description, target, token.CommandGeneration,
            SessionShortcutBindings.Default, entry, focus, focus);
        Assert.NotNull(ack);
        Assert.Equal(target, ack.Target);
        Assert.False(ack.SourceReady);
        Assert.False(ack.EditorReady);
        Assert.Equal(0, ack.SourceLocalAcceptanceVersion);
        Assert.Null(BrowserInput.ApproveObservationRoles(description, target, token.CommandGeneration, SessionShortcutBindings.Default));
        Assert.Null(BrowserInput.ApproveObservationRoles(description, target, token.CommandGeneration,
            SessionShortcutBindings.Default, entry, focus, null));
        Assert.Null(BrowserInput.ApproveObservationRoles(Description(focus with
        {
            FormId = Guid.NewGuid()
        }), target,
            token.CommandGeneration, SessionShortcutBindings.Default, entry, focus, focus));
        Assert.Null(BrowserInput.ApproveObservationRoles(Description(focus with
        {
            Token = token with
            {
                Revision = 5
            }
        }), target,
            token.CommandGeneration, SessionShortcutBindings.Default, entry, focus, focus));
        Assert.Null(BrowserInput.ApproveObservationRoles(Description(focus, 1), target, token.CommandGeneration,
            SessionShortcutBindings.Default, entry, focus, focus));
    }

    private const string Extension = "abcdefghijklmnopabcdefghijklmnop";
    private const string Document = "10000000-0000-0000-0000-000000000001";
    private static BrowserRoute Route => new(Extension, "chrome-document-1", Document, 1, 2, 0, "https:");
    private static JsonElement Authority(long input = 1, long focus = 2, long content = 3,
        string metadata = "approved", string token = "10000000-0000-0000-0000-000000000003", long observed = 100) =>
        JsonSerializer.SerializeToElement(new
        {
            target = Document + ":1",
            metadataKey = metadata,
            contentRevision = content,
            inputRevision = input,
            focusRevision = focus,
            token,
            observedAtMs = observed
        });
    [Fact(Timeout = 5000)]
    public async Task BrowserReaderCapturesReceiptsWhileUiIsBlockedAndDrainsEarlierEventsFirst()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var delivered = new List<string>();
        await using var transport = await BrowserIngress.CreateAsync(name =>
        {
            delivered.Add(name);
            if (name == "input")
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            }
        });
        var receipt = transport.Request("direct", "direct-1");
        await transport.Event("input");
        await transport.WaitUntilReadAsync();
        Task drain = Task.Run(transport.Ui.RunNext, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            await transport.Event("accepted");
            await transport.Reply("direct-1", "verified");
            transport.Client.Dispose();
            // EOF is observed by the real reader while its UI callback is still
            // blocked. The captured receipt must survive that close, but cannot
            // be used before the preceding events finish on the UI owner.
            await Assert.ThrowsAnyAsync<IOException>(() => transport.Reader);
            Assert.False(receipt.IsCompleted);
            Assert.Equal(new[] { "input" }, delivered);
        }
        finally { release.Set(); }
        await drain;
        Assert.Equal(new[] { "input", "accepted" }, delivered);
        Assert.Equal("verified", (await receipt)!.Data.GetString());
        await transport.Inbox.Completion;
    }

    [Fact(Timeout = 5000)]
    public async Task BrowserReplyCannotOvertakeAQueuedRevocationAndSharesTheSixteenRequestLimit()
    {
        bool revoked = false;
        await using var transport = await BrowserIngress.CreateAsync(name => revoked |= name == "destroy");
        Task<BrowserReply?>[] requests = Enumerable.Range(0, 16)
            .Select(index => transport.Request("describe", "request-" + index)).ToArray();
        await transport.Event("destroy");
        for (int index = 0; index < requests.Length; index++)
            await transport.Reply("request-" + index, "description");
        // Observe the next pipe read, not private pending state or elapsed time.
        await transport.WaitUntilReadAsync();
        bool sent = false;
        Assert.Null(await transport.Inbox.RequestAsync("seventeenth", "describe", () => sent = true, 3000));
        Assert.False(sent);
        Assert.All(requests, request => Assert.False(request.IsCompleted));
        transport.Ui.RunNext();
        Assert.True(revoked);
        foreach (var request in requests)
            Assert.NotNull(await request);
    }

    [Fact(Timeout = 5000)]
    public async Task BrowserOverflowKeepsTheLastAcceptedEventAndCapturedDirectReceiptOnly()
    {
        var delivered = new List<string>();
        await using var transport = await BrowserIngress.CreateAsync(delivered.Add);
        var direct = transport.Request("direct", "direct-1");
        var missing = transport.Request("direct", "direct-missing");
        var authority = transport.Request("describe", "authority-1");
        await transport.Event("destroy");
        await transport.Reply("direct-1", "verified");
        await transport.Reply("authority-1", "approved");
        for (int index = 1; index < 64; index++)
            await transport.Event("input");
        await transport.Event("accepted"); // The bounded overflow frame is terminal too.
        await transport.Reader;
        Assert.Null(await missing);
        Assert.Null(await authority);
        Assert.False(direct.IsCompleted);
        Assert.Null(await transport.Request("describe", "closed"));
        transport.Ui.RunNext();
        Assert.Equal(new[] { "accepted" }, delivered);
        Assert.Equal("verified", (await direct)!.Data.GetString());
        await transport.Inbox.Completion;
    }

    [Fact(Timeout = 5000)]
    public async Task BrowserTimeoutDoesNotRestartAtTheReplyBarrierOrLoseAnOnTimeDirectReceipt()
    {
        await using var transport = await BrowserIngress.CreateAsync(_ => { });
        var direct = transport.Request("direct", "direct-1", 500);
        var authority = transport.Request("describe", "authority-1", 500);
        await transport.Event("input");
        await transport.Reply("direct-1", "verified");
        await transport.Reply("authority-1", "approved");
        await transport.WaitUntilReadAsync();
        Assert.Null(await authority); // Original overall deadline, with UI still queued.
        Assert.False(direct.IsCompleted);
        transport.Ui.RunNext();
        Assert.Equal("verified", (await direct)!.Data.GetString());
    }

    [Fact(Timeout = 5000)]
    public async Task BrowserCancellationAndLateIdsCannotCompleteANewerRequest()
    {
        await using var transport = await BrowserIngress.CreateAsync(_ => { });
        var expired = transport.Request("direct", "old", 20);
        Assert.Null(await expired);
        var current = transport.Request("direct", "current");
        await transport.Reply("old", "late");
        await transport.Reply("other-peer:current", "wrong-peer");
        await transport.WaitUntilReadAsync();
        Assert.False(current.IsCompleted);
        transport.Owner.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.Reader);
        Assert.Null(await current);
        await transport.Inbox.Completion;
    }

    [Fact(Timeout = 5000)]
    public async Task BrowserUiShutdownSettlesReceivedTerminalsOnceWithoutReentrantDrain()
    {
        var delivered = new List<string>();
        BrowserIngress? active = null;
        await using var transport = await BrowserIngress.CreateAsync(name =>
        {
            delivered.Add(name + "-start");
            if (name == "input")
            {
                active!.Inbox.CloseOnUiThread();
                Assert.Equal(new[] { "input-start" }, delivered);
            }
            delivered.Add(name + "-end");
        });
        active = transport;
        var direct = transport.Request("direct", "direct-1");
        await transport.Event("input");
        await transport.Event("accepted");
        await transport.Reply("direct-1", "verified");
        await transport.WaitUntilReadAsync();
        transport.Ui.RunNext();
        await transport.Inbox.Completion;
        Assert.Equal(new[] { "input-start", "input-end", "accepted-start", "accepted-end" }, delivered);
        Assert.Equal("verified", (await direct)!.Data.GetString());
        transport.Inbox.CloseOnUiThread();
        Assert.Equal(new[] { "input-start", "input-end", "accepted-start", "accepted-end" }, delivered);
    }

    [Fact(Timeout = 5000)]
    public async Task BrowserCallbackFailureClosesABlockedReaderAndStillSettlesReceivedTerminals()
    {
        var failure = new InvalidOperationException("callback-failed");
        int accepted = 0;
        await using var transport = await BrowserIngress.CreateAsync(name =>
        {
            if (name == "input")
                throw failure;
            if (name == "accepted")
                accepted++;
        });
        transport.ExpectedFailure = failure;
        var direct = transport.Request("direct", "direct-1");
        await transport.Event("input");
        await transport.Event("accepted");
        await transport.Reply("direct-1", "verified");
        await transport.WaitUntilReadAsync(); // No further frame will wake this read.
        transport.Ui.RunNext();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.Reader);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => transport.Inbox.Completion));
        Assert.Null(await transport.Request("describe", "closed"));
        Assert.Equal(1, accepted);
        Assert.Equal("verified", (await direct)!.Data.GetString());
    }

    private sealed class BrowserUi
    {
        private readonly Queue<(Action Action, TaskCompletionSource Completion)> _queue = new();
        public Task Post(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_queue)
                _queue.Enqueue((action, completion));
            return completion.Task;
        }
        public void RunNext()
        {
            (Action Action, TaskCompletionSource Completion) next;
            lock (_queue)
                next = _queue.Dequeue();
            try
            {
                next.Action();
                next.Completion.SetResult();
            }
            catch (Exception error) { next.Completion.SetException(error); throw; }
        }
        public void RunAll()
        {
            while (true)
            {
                lock (_queue)
                    if (_queue.Count == 0)
                        return;
                RunNext();
            }
        }
    }

    private sealed class BrowserIngress : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly ReadObservedStream _observed;
        private long _sentBytes;
        public NamedPipeClientStream Client
        {
            get;
        }
        public BrowserUi Ui { get; } = new();
        public CancellationTokenSource Owner { get; } = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        public BrowserPeerInbox Inbox
        {
            get;
        }
        public Task Reader
        {
            get;
        }
        public Exception? ExpectedFailure
        {
            get; set;
        }
        private BrowserIngress(NamedPipeServerStream server, NamedPipeClientStream client, Action<string> receive)
        {
            _server = server;
            Client = client;
            Inbox = new("chrome-extension://" + Extension + "/", Ui.Post, (name, _, _) => receive(name), Owner.Cancel);
            _observed = new(server);
            Reader = Inbox.ReadAsync(_observed, () => true, Owner.Token);
        }
        public static async Task<BrowserIngress> CreateAsync(Action<string> receive)
        {
            string name = "typelay-inbound-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await Task.WhenAll(server.WaitForConnectionAsync(TestContext.Current.CancellationToken),
                    client.ConnectAsync(3000, TestContext.Current.CancellationToken));
            }
            catch
            {
                server.Dispose();
                client.Dispose();
                throw;
            }
            return new(server, client, receive);
        }
        public Task<BrowserReply?> Request(string op, string id, int timeoutMs = 3000) =>
            Inbox.RequestAsync(id, op, () => true, timeoutMs);
        public Task Event(string name) => Frame(new { @event = name, route = Route, data = new { } });
        public Task Reply(string id, string data) => Frame(new { id, route = Route, data });
        private async Task Frame(object message)
        {
            _sentBytes += 4 + JsonSerializer.SerializeToUtf8Bytes(message, Wire.Json).Length;
            await Wire.WriteAsync(Client, message, TestContext.Current.CancellationToken);
        }
        public Task WaitUntilReadAsync() => _observed.WaitForReadAfterAsync(_sentBytes).WaitAsync(Owner.Token);
        public async ValueTask DisposeAsync()
        {
            Inbox.Close();
            Owner.Cancel();
            Client.Dispose();
            _server.Dispose();
            Ui.RunAll();
            try
            {
                await Reader;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            try
            {
                await Inbox.Completion;
            }
            catch (Exception error) when (ReferenceEquals(error, ExpectedFailure)) { }
            Owner.Dispose();
        }
    }

    // This adapter observes the next real stream read. It changes neither wire
    // contents nor reader completion, and avoids timing guesses in queue tests.
    private sealed class ReadObservedStream(Stream inner) : Stream
    {
        private readonly object _gate = new();
        private long _bytesRead, _readStartedAt = -1, _waitingAt;
        private TaskCompletionSource? _waiting;
        public Task WaitForReadAfterAsync(long position)
        {
            lock (_gate)
            {
                if (_readStartedAt >= position)
                    return Task.CompletedTask;
                _waitingAt = position;
                _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _waiting.Task;
            }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _readStartedAt = _bytesRead;
                if (_waiting is not null && _readStartedAt >= _waitingAt)
                {
                    _waiting.TrySetResult();
                    _waiting = null;
                }
            }
            int read = await inner.ReadAsync(buffer, cancellationToken);
            lock (_gate)
                _bytesRead += read;
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void BrowserConnectionConfigurationRequiresOneExactPublishedIdentity()
    {
        var empty = BrowserConnectionConfiguration.Parse("""{"version":1,"extensionId":null,"storeUrl":null}""");
        Assert.Null(empty.ExtensionId);
        Assert.Null(empty.StoreUrl);
        Assert.Equal(Extension, BrowserConnectionConfiguration.Parse(JsonSerializer.Serialize(
            new
            {
                version = 1,
                extensionId = Extension,
                storeUrl = (string?)null
            })).ExtensionId);
        foreach (string invalid in new[]
        {
            "{}", "[]", "null", "{", "{\"version\":1,\"extensionId\":null,\"storeUrl\":null,\"extra\":1}",
            "{\"version\":1,\"extensionId\":null,\"storeUrl\":null,\"version\":1}"
        })
            Assert.Throws<InvalidDataException>(() => BrowserConnectionConfiguration.Parse(invalid));
        string url = "https://chromewebstore.google.com/detail/typelay/" + Extension;
        var configured = BrowserConnectionConfiguration.Parse(JsonSerializer.Serialize(new
        {
            version = 1,
            extensionId = Extension,
            storeUrl = url
        }));
        Assert.Equal(Extension, configured.ExtensionId);
        Assert.Equal(url, configured.StoreUrl);
        foreach (string invalid in new[]
        {
            "https://example.com/detail/typelay/" + Extension,
            url + "?source=another", url + "#fragment", url.Replace("https:", "http:"),
            url.Replace(Extension, new string('p', 32)), url.Replace(".com/", ".com:444/"),
            url.Replace("//chrome", "//user@chrome")
        })
            Assert.Throws<InvalidDataException>(() => BrowserConnectionConfiguration.Parse(
                JsonSerializer.Serialize(new
                {
                    version = 1,
                    extensionId = Extension,
                    storeUrl = invalid
                })));
        Assert.Throws<InvalidDataException>(() => BrowserConnectionConfiguration.Parse(
            JsonSerializer.Serialize(new
            {
                version = 2,
                extensionId = Extension,
                storeUrl = url
            })));
        Assert.Throws<InvalidDataException>(() => BrowserConnectionConfiguration.Parse(
            JsonSerializer.Serialize(new
            {
                version = 1,
                extensionId = (string?)null,
                storeUrl = url
            })));
        Assert.Throws<InvalidDataException>(() => BrowserConnectionConfiguration.Parse(
            JsonSerializer.Serialize(new
            {
                version = 1,
                extensionId = new string('q', 32),
                storeUrl = (string?)null
            })));
    }

    [Fact]
    public void BrowserTraceNoiseCannotConsumeReservedCommandAndReceiptBudget()
    {
        var budget = new BrowserTraceBudget();
        for (int index = 0; index < 224; index++)
            Assert.True(budget.TryTake("browser-observation"));
        Assert.False(budget.GeneralAvailable);
        Assert.True(budget.CommandsAvailable);
        Assert.False(budget.TryTake("browser-owner"));
        Assert.False(budget.TryTake("browser-direct-unrecognized"));
        string[] commands = ["browser-command-approved", "browser-command-path", "browser-direct-Verified",
            "browser-direct-RejectedBeforeMutation", "browser-direct-OutcomeUnknown",
            "browser-accept-Verified", "browser-accept-RejectedBeforeMutation", "browser-accept-OutcomeUnknown"];
        for (int index = 0; index < 32; index++)
            Assert.True(budget.TryTake(commands[index % commands.Length]));
        Assert.False(budget.CommandsAvailable);
        foreach (string operation in commands)
            Assert.False(budget.TryTake(operation));
        Assert.False(budget.TryTake("browser-observation"));
    }
    [Fact]
    public void RouteRequiresAuthenticatedExtensionTopDocumentAndNormalWebScheme()
    {
        string origin = "chrome-extension://" + Extension + "/";
        Assert.True(Route.Valid(origin));
        Assert.True(Route.Contains(Document + ":1"));
        Assert.False(Route.Contains("another-document:1"));
        Assert.False((Route with
        {
            DocumentId = ""
        }).Valid(origin));
        Assert.False((Route with
        {
            DocumentToken = "invalid"
        }).Valid(origin));
        Assert.False((Route with
        {
            FrameId = 1
        }).Valid(origin));
        Assert.False((Route with
        {
            Scheme = "chrome:"
        }).Valid(origin));
        Assert.False((Route with
        {
            ExtensionId = "invalid"
        }).Valid(origin));
        Assert.False(Route.Valid("chrome-extension://pppppppppppppppppppppppppppppppp/"));
    }
    [Fact]
    public void SameSourceRenewalAllowsNewTokenAndTimeButNotAnyChangedWatermark()
    {
        var original = Authority();
        Assert.True(BrowserInput.SameAuthority(original, Authority(token: "10000000-0000-0000-0000-000000000004", observed: 120)));
        Assert.False(BrowserInput.SameAuthority(original, Authority(input: 2)));
        Assert.False(BrowserInput.SameAuthority(original, Authority(focus: 3)));
        Assert.False(BrowserInput.SameAuthority(original, Authority(content: 4)));
        Assert.False(BrowserInput.SameAuthority(original, Authority(metadata: "changed")));
        Assert.False(BrowserInput.SameAuthority(original, Authority(token: "invalid")));
        Assert.False(BrowserInput.SameAuthority(original, default));
    }
    [Fact(Timeout = 5000)]
    public async Task BrowserPolicyRefreshContinuesWhileThePreviousBodyReplyIsPending()
    {
        // These are transport completion barriers, not page approval or clock
        // proofs. The production coordinator must never await the body barrier.
        var refresh = new BrowserPolicyRefresh();
        Assert.Equal(BrowserPolicyRenewalPhase.Idle, refresh.DiagnosticPhase);
        Assert.Null(refresh.DiagnosticCycleStartedAt);
        Assert.Equal(0, refresh.DiagnosticCycleSequence);
        var description = new TaskCompletionSource<BrowserReply?>();
        var acknowledgement = new TaskCompletionSource();
        var body = new TaskCompletionSource();
        var first = new BrowserReply(Authority(), Route);
        BrowserReply? firstApproval = null, secondApproval = null;
        int descriptions = 0, approvals = 0;
        Task<BrowserReply?> Describe()
        {
            descriptions++;
            return description.Task;
        }
        Task Approve(BrowserReply reply)
        {
            firstApproval = reply;
            approvals++;
            return acknowledgement.Task;
        }
        var round = refresh.Poll(100, Describe, Approve);
        Assert.Equal(BrowserPolicyRenewalPhase.Describe, refresh.DiagnosticPhase);
        Assert.Equal(100, refresh.DiagnosticCycleStartedAt);
        Assert.Equal(1, refresh.DiagnosticCycleSequence);
        var capturedDescription = refresh.CaptureAsync();
        description.SetResult(first);
        Assert.Same(first, await capturedDescription);
        Assert.Equal(BrowserPolicyRenewalPhase.Approve, refresh.DiagnosticPhase);
        async Task ReadBody()
        {
            await capturedDescription;
            await body.Task;
        }
        var reading = ReadBody();
        Assert.False(reading.IsCompleted);
        Assert.Same(round, refresh.Poll(300, Describe, Approve));
        Assert.Equal(100, refresh.DiagnosticCycleStartedAt);
        Assert.Equal(1, refresh.DiagnosticCycleSequence);
        Assert.Equal(1, descriptions); // An ACK timeout has not been reported yet.
        Assert.Equal(1, approvals);
        Assert.Same(first, firstApproval);
        acknowledgement.SetResult();
        await round;
        Assert.Equal(BrowserPolicyRenewalPhase.Idle, refresh.DiagnosticPhase);
        var nextDescription = new TaskCompletionSource<BrowserReply?>();
        var nextAck = new TaskCompletionSource();
        var next = new BrowserReply(Authority(observed: 300), Route);
        var nextRound = refresh.Poll(300, () =>
        {
            descriptions++;
            return nextDescription.Task;
        }, reply =>
        {
            secondApproval = reply;
            approvals++;
            return nextAck.Task;
        });
        Assert.Equal(300, refresh.DiagnosticCycleStartedAt);
        Assert.Equal(2, refresh.DiagnosticCycleSequence);
        nextDescription.SetResult(next);
        Assert.Same(next, await refresh.CaptureAsync());
        Assert.Equal(BrowserPolicyRenewalPhase.Approve, refresh.DiagnosticPhase);
        Assert.Equal(2, descriptions);
        Assert.Equal(2, approvals);
        Assert.Same(next, secondApproval);
        Assert.False(reading.IsCompleted); // The scheduler clock advanced by 200ms.
        nextAck.SetResult();
        await nextRound;
        Assert.Equal(BrowserPolicyRenewalPhase.Idle, refresh.DiagnosticPhase);
        body.SetResult();
        await reading;
    }

    [Fact(Timeout = 5000)]
    public async Task BrowserPolicyRefreshSharesEachDescriptionWithOnlyOneBodyRead()
    {
        var refresh = new BrowserPolicyRefresh();
        var description = new TaskCompletionSource<BrowserReply?>();
        var acknowledgement = new TaskCompletionSource();
        var first = new BrowserReply(Authority(), Route);
        var round = refresh.Poll(100, () => description.Task, _ => acknowledgement.Task);
        var capture = refresh.CaptureAsync();
        var nextCapture = refresh.CaptureAsync();
        Assert.Null(await refresh.CaptureAsync()); // No unbounded queue or shared second body.
        description.SetResult(first);
        Assert.Same(first, await capture);
        Assert.False(nextCapture.IsCompleted);
        acknowledgement.SetResult();
        await round;
        bool described = false;
        Task<BrowserReply?> Describe()
        {
            described = true;
            return Task.FromResult<BrowserReply?>(new(Authority(observed: 135), Route));
        }
        await refresh.Poll(134, Describe, _ => Task.CompletedTask);
        Assert.False(described);
        await refresh.Poll(135, Describe, _ => Task.CompletedTask);
        var next = await nextCapture;
        Assert.True(described);
        Assert.NotNull(next);
        Assert.NotSame(first, next);
        Assert.Equal(135, next.Data.GetProperty("observedAtMs").GetInt64());
    }

    [Fact(Timeout = 5000)]
    public async Task BrowserPolicyRefreshRejectsLateDescriptionsAndWaitsForTheOldAck()
    {
        var refresh = new BrowserPolicyRefresh();
        var description = new TaskCompletionSource<BrowserReply?>();
        bool approved = false;
        var round = refresh.Poll(100, () => description.Task, _ =>
        {
            approved = true;
            return Task.CompletedTask;
        });
        var capture = refresh.CaptureAsync();
        refresh.Invalidate();
        Assert.Null(await capture);
        Assert.Equal(BrowserPolicyRenewalPhase.Describe, refresh.DiagnosticPhase);
        description.SetResult(new(Authority(), Route));
        await round;
        Assert.False(approved);
        Assert.Equal(BrowserPolicyRenewalPhase.Idle, refresh.DiagnosticPhase);

        var ack = new TaskCompletionSource();
        var second = refresh.Poll(135, () => Task.FromResult<BrowserReply?>(new(Authority(), Route)), _ => ack.Task);
        refresh.Invalidate();
        Assert.Equal(BrowserPolicyRenewalPhase.Approve, refresh.DiagnosticPhase);
        bool nextStarted = false;
        Assert.Same(second, refresh.Poll(300, () =>
        {
            nextStarted = true;
            return Task.FromResult<BrowserReply?>(null);
        }, _ => Task.CompletedTask));
        Assert.False(nextStarted);
        ack.SetResult(); // Includes RequestAsync returning null after its bounded timeout.
        await second;
        Assert.Equal(BrowserPolicyRenewalPhase.Idle, refresh.DiagnosticPhase);
        await refresh.Poll(300, () =>
        {
            nextStarted = true;
            return Task.FromResult<BrowserReply?>(null);
        }, _ => Task.CompletedTask);
        Assert.True(nextStarted);
    }
    [Fact]
    public void ContinuousCapturesKeepTheirOwnExactObservationAndRetireOnlyTheOldestArrival()
    {
        var history = new BrowserObservationHistory();
        var target = new TargetId("windows-browser", Document + ":1", Guid.NewGuid());
        var generation = Guid.NewGuid();
        BrowserObservation Record(long at) => new(null!, Route, Authority(observed: at), target, at, 0,
            new(0, 0, 0, 0, 0, generation), new(target, generation, SessionShortcutBindings.Default, true, true));
        for (int at = 1; at <= 130; at++)
        {
            // Only retention is under test. No authenticated peer or browser
            // authority is claimed by these in-memory records.
            var current = Record(at);
            history.Remember(current);
            Assert.Same(current, history.Observation(target, at));
            Assert.Same(current, history.Latest(target));
        }
        Assert.Null(history.Observation(target, 1));
        Assert.Null(history.Observation(target, 2));
        Assert.NotNull(history.Observation(target, 3));
        var renewed = Record(3);
        history.Remember(renewed);
        Assert.Same(renewed, history.Observation(target, 3));
        Assert.Same(renewed, history.Latest(target));
        var other = Record(131) with
        {
            Target = target with
            {
                Value = Document + ":2"
            }
        };
        history.Remember(other);
        Assert.Null(history.Observation(target, 4));
        Assert.Same(renewed, history.Observation(target, 3));
        Assert.Same(renewed, history.Latest(target));
        Assert.Same(other, history.Latest(other.Target));
        Assert.NotNull(history.Observation(target, 130));
    }
    [Fact]
    public void FailedHotkeyReleaseCannotBecomePageOwnershipAndSameConfigurationCanRestore()
    {
        var registered = new HashSet<int>();
        bool refuseDirectRelease = true, refuseWakeRegistration = false;
        using var hotkeys = new OnDemandHotkeys(0,
            (_, id, _, _) => !(id == 0x5451 && refuseWakeRegistration) && registered.Add(id),
            (_, id) => id == 0x5452 && refuseDirectRelease ? false : registered.Remove(id));
        hotkeys.Configure(true, "Ctrl+Shift+F9", "Ctrl+Shift+F10");
        Assert.True(hotkeys.Registered);
        Assert.False(hotkeys.Suspend());
        Assert.True(hotkeys.HasRegistration);
        Assert.Equal([0x5452], registered);
        Assert.True(hotkeys.Resume());
        Assert.Equal(2, registered.Count);
        refuseDirectRelease = false;
        Assert.True(hotkeys.Suspend());
        Assert.Empty(registered);
        Assert.True(hotkeys.Resume()); // Same desired tuple must really re-register.
        Assert.Equal(2, registered.Count);
        Assert.True(hotkeys.Suspend());
        refuseWakeRegistration = true;
        Assert.False(hotkeys.Resume());
        Assert.False(hotkeys.Registered);
        Assert.True(hotkeys.HasRegistration);
        Assert.Equal([0x5452], registered);
        Assert.Contains("唤醒翻译 UI", hotkeys.Status);
        Assert.True(hotkeys.Suspend());
        Assert.Empty(registered);
    }
    [Fact]
    public void BrowserWireUsesSharedPageEnumSpelling()
    {
        var frame = JsonSerializer.SerializeToElement(new
        {
            state = SessionState.EditingTranslation,
            ime = ImeState.Clear
        }, BrowserServer.Json);
        Assert.Equal("EditingTranslation", frame.GetProperty("state").GetString());
        Assert.Equal("Clear", frame.GetProperty("ime").GetString());
    }
}
