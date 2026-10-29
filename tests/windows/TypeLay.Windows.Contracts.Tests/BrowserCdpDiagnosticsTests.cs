using System.Text.Json;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.Windows.Contracts.Tests;

public sealed class BrowserCdpDiagnosticsTests
{
    [Theory]
    [InlineData("Page.frameStartedLoading")]
    [InlineData("Page.frameStartedNavigating")]
    [InlineData("Page.frameStoppedLoading")]
    [InlineData("Page.frameNavigated")]
    [InlineData("Page.frameDetached")]
    public void PageNotificationsOnlyKeepSessionRelationAndAllowlistedReason(string method)
    {
        var diagnostics = new BrowserCdpDiagnostics();
        diagnostics.Observe(JsonSerializer.SerializeToElement(new
        {
            method,
            sessionId = "private-session",
            @params = new
            {
                frameId = "private-frame",
                reason = "private-reason",
                url = "https://private.invalid/body"
            }
        }), "private-session", "private-target");
        var entry = Assert.Single(diagnostics.Notifications);
        Assert.Equal(method, entry.Kind);
        Assert.True(entry.OwnedSession);
        Assert.Null(entry.OwnedTarget);
        Assert.Equal(method == "Page.frameDetached" ? "unknown" : null, entry.Reason);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(diagnostics));
    }

    [Theory]
    [InlineData("Target.detachedFromTarget")]
    [InlineData("Target.targetDestroyed")]
    [InlineData("Target.targetCrashed")]
    public void TargetNotificationsCompareIdentityWithoutRetainingIt(string method)
    {
        var diagnostics = new BrowserCdpDiagnostics();
        diagnostics.Observe(JsonSerializer.SerializeToElement(new
        {
            method,
            @params = new
            {
                targetId = "private-target",
                sessionId = "private-other-session",
                status = "private-status"
            }
        }), "private-owned-session", "private-target");
        var entry = Assert.Single(diagnostics.Notifications);
        Assert.False(entry.OwnedSession);
        Assert.True(entry.OwnedTarget);
        Assert.Null(entry.Reason);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(diagnostics));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"method\":42}")]
    [InlineData("{\"method\":\"Runtime.consoleAPICalled\",\"params\":{\"body\":\"private-body\"}}")]
    public void UnrelatedOrMalformedMessagesDoNotCreateNotificationEvidence(string json)
    {
        using var message = JsonDocument.Parse(json);
        var diagnostics = new BrowserCdpDiagnostics();
        diagnostics.Observe(message.RootElement, "private-session", "private-target");
        Assert.Empty(diagnostics.Notifications);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"targetId\":42,\"sessionId\":null}")]
    public void MissingOrMalformedIdentityIsUnknownNotAConfirmedMismatch(string parameters)
    {
        using var message = JsonDocument.Parse("{\"method\":\"Target.detachedFromTarget\",\"params\":" + parameters + "}");
        var diagnostics = new BrowserCdpDiagnostics();
        diagnostics.Observe(message.RootElement, "private-session", "private-target");
        var entry = Assert.Single(diagnostics.Notifications);
        Assert.Null(entry.OwnedSession);
        Assert.Null(entry.OwnedTarget);
    }

    [Fact]
    public void NotificationsKeepOnlyTheLatestSixteenAndMarkTruncation()
    {
        var diagnostics = new BrowserCdpDiagnostics();
        for (int i = 0; i < 20; i++)
            diagnostics.Observe(JsonSerializer.SerializeToElement(new
            {
                method = "Page.frameDetached",
                sessionId = "private-session",
                @params = new
                {
                    reason = i < 4 ? "remove" : "swap"
                }
            }), "private-session", null);
        Assert.Equal(16, diagnostics.Notifications.Count);
        Assert.True(diagnostics.NotificationsTruncated);
        Assert.All(diagnostics.Notifications, entry => Assert.Equal("swap", entry.Reason));
    }

    [Theory]
    [InlineData("http://127.0.0.1:12345/private-fixture", "expected-loopback", "differentDocument", "differentDocument")]
    [InlineData("about:blank", "about-blank", "restore", "restore")]
    [InlineData("https://private.invalid/source", "other", "private-navigation", "unknown")]
    public void NavigationEventsDistinguishTheOwnedMainFrameAndDestinationWithoutRetainingIdentity(
        string url, string category, string navigationType, string expectedType)
    {
        const string expectedUrl = "http://127.0.0.1:12345/private-fixture";
        var diagnostics = new BrowserCdpDiagnostics();
        diagnostics.Observe(JsonSerializer.SerializeToElement(new
        {
            method = "Page.frameStartedNavigating",
            sessionId = "private-session",
            @params = new
            {
                frameId = "private-frame",
                loaderId = "private-loader",
                url,
                navigationType
            }
        }), "private-session", "private-target", "private-frame", expectedUrl);
        diagnostics.Observe(JsonSerializer.SerializeToElement(new
        {
            method = "Page.frameNavigated",
            sessionId = "private-session",
            @params = new
            {
                frame = new
                {
                    id = "private-frame",
                    url,
                    loaderId = "private-loader"
                }
            }
        }), "private-session", "private-target", "private-frame", expectedUrl);
        Assert.Equal(2, diagnostics.Notifications.Count);
        Assert.All(diagnostics.Notifications, entry =>
        {
            Assert.True(entry.OwnedSession);
            Assert.True(entry.MainFrame);
            Assert.Equal(category, entry.UrlCategory);
            Assert.InRange(entry.ElapsedMs, 0, 600000);
        });
        Assert.Equal(expectedType, diagnostics.Notifications[0].NavigationType);
        Assert.Null(diagnostics.Notifications[1].NavigationType);
        Assert.True(diagnostics.Notifications[1].ElapsedMs >= diagnostics.Notifications[0].ElapsedMs);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(diagnostics));
        Assert.DoesNotContain("127.0.0.1", JsonSerializer.Serialize(diagnostics));
    }

    [Theory]
    [InlineData("{}", "missing", "missing", null)]
    [InlineData("{\"loaderId\":\"private-new-loader\",\"isDownload\":false}", "nonempty", "false", false)]
    [InlineData("{\"loaderId\":\"private-initial-loader\",\"isDownload\":true}", "nonempty", "true", true)]
    [InlineData("{\"loaderId\":null,\"isDownload\":null}", "malformed", "malformed", null)]
    [InlineData("{\"loaderId\":\"\"}", "empty", "missing", null)]
    [InlineData("null", "malformed", "malformed", null)]
    public void NavigationReplyKeepsMissingLoaderAndDownloadDistinctFromMalformedOrPresent(
        string json, string loaderState, string download, bool? initialLoader)
    {
        using var response = JsonDocument.Parse(json);
        var diagnostics = new BrowserCdpDiagnostics();
        diagnostics.RecordNavigationResponse(response.RootElement, "private-main-frame", "private-initial-loader");
        var reply = Assert.IsType<BrowserCdpDiagnostics.NavigationReply>(diagnostics.NavigationResponse);
        Assert.Equal(loaderState, reply.LoaderIdState);
        Assert.Equal(download, reply.Download);
        Assert.Equal(initialLoader, reply.InitialLoader);
        Assert.Null(reply.MainFrame);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(diagnostics));
    }

    [Theory]
    [InlineData("private-original-frame", "private-original-loader", true, true)]
    [InlineData("private-new-frame", "private-new-loader", false, false)]
    public void PostAbortFrameSnapshotPreservesOnlyOriginalIdentityRelations(string frame, string loader,
        bool sameFrame, bool sameLoader)
    {
        const string expectedUrl = "http://127.0.0.1:12345/private-fixture";
        var diagnostics = new BrowserCdpDiagnostics();
        var before = JsonSerializer.SerializeToElement(new
        {
            frameTree = new
            {
                frame = new
                {
                    id = "private-original-frame",
                    loaderId = "private-original-loader",
                    url = "about:blank"
                }
            }
        });
        diagnostics.RecordFrameSnapshot(before, expectedUrl, null, null, afterAbort: false);
        var after = JsonSerializer.SerializeToElement(new
        {
            frameTree = new
            {
                frame = new
                {
                    id = frame,
                    loaderId = loader,
                    url = "about:blank",
                    unreachableUrl = expectedUrl
                }
            }
        });
        diagnostics.RecordFrameSnapshot(after, expectedUrl, "private-original-frame", "private-original-loader", afterAbort: true);
        Assert.Equal("about-blank", diagnostics.BeforeNavigation!.UrlCategory);
        Assert.Null(diagnostics.BeforeNavigation.InitialFrame);
        var snapshot = Assert.IsType<BrowserCdpDiagnostics.FrameSnapshot>(diagnostics.AfterAbort);
        Assert.Equal("observed", snapshot.Status);
        Assert.Equal("about-blank", snapshot.UrlCategory);
        Assert.Equal("expected-loopback", snapshot.UnreachableUrlCategory);
        Assert.Equal(sameFrame, snapshot.InitialFrame);
        Assert.Equal(sameLoader, snapshot.InitialLoader);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(diagnostics));
        Assert.DoesNotContain("127.0.0.1", JsonSerializer.Serialize(diagnostics));
    }

    [Fact]
    public void DiagnosticFailureCannotReplaceTheOriginalAbortedReplyOrMakeNavigationSuccessful()
    {
        var diagnostics = new BrowserCdpDiagnostics();
        var response = JsonSerializer.SerializeToElement(new
        {
            errorText = "net::ERR_ABORTED",
            frameId = "private-frame"
        });
        diagnostics.RecordNavigationResponse(response, "private-frame", "private-loader");
        var original = diagnostics.NavigationResponse;
        diagnostics.RecordAbortSnapshotFailure(new IOException("private-path-and-message"));
        Assert.Same(original, diagnostics.NavigationResponse);
        Assert.True(original!.MainFrame);
        Assert.Equal("nonempty", original.FrameIdState);
        Assert.Equal("failed", diagnostics.AfterAbort!.Status);
        Assert.Equal("io", diagnostics.AfterAbort.ErrorCategory);
        Assert.Equal("aborted", BrowserPageOpenResponse.NavigationFailure(response));
        Assert.False(BrowserPageOpenResponse.NavigationSucceeded(response));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(diagnostics));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"frameTree\":{}}")]
    [InlineData("{\"frameTree\":{\"frame\":false}}")]
    public void MalformedFrameSnapshotDoesNotInventAConfirmedMainFrame(string json)
    {
        using var response = JsonDocument.Parse(json);
        var diagnostics = new BrowserCdpDiagnostics();
        diagnostics.RecordFrameSnapshot(response.RootElement, "http://127.0.0.1:12345/private-fixture",
            "private-frame", "private-loader", afterAbort: true);
        Assert.Equal("malformed", diagnostics.AfterAbort!.Status);
        Assert.Null(diagnostics.AfterAbort.InitialFrame);
        Assert.Null(diagnostics.AfterAbort.InitialLoader);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(diagnostics));
    }

    [Fact]
    public void CallFailureProjectionDoesNotRetainUntrustedMethodPhaseOrExceptionData()
    {
        var diagnostics = new BrowserCdpDiagnostics();
        var error = new IOException("private-message", unchecked((int)0x8007006d));
        error.Data["private-key"] = "private-body";
        diagnostics.RecordFailure("private-method", "private-phase", error, false);
        // A later failure cannot replace the original call evidence.
        diagnostics.RecordFailure("Page.navigate", "receive", new OperationCanceledException("private-cancel"), true);
        var failure = Assert.IsType<BrowserCdpDiagnostics.CallFailure>(diagnostics.Failure);
        Assert.Equal("other", failure.Method);
        Assert.Equal("other", failure.Phase);
        Assert.Equal("io", failure.ErrorCategory);
        Assert.Equal(unchecked((int)0x8007006d), failure.HResult);
        Assert.False(failure.CancellationRequested);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(diagnostics));
    }

    [Fact]
    public void OwnedDocumentFailureRetainsOnlyCorrelatedWhitelistedNetworkEvidence()
    {
        var diagnostics = new BrowserCdpDiagnostics();
        Assert.Equal("unobserved", diagnostics.NetworkObservation);
        diagnostics.BeginNetworkObservation();
        ObserveNetwork(diagnostics, "Network.requestWillBeSent", new
        {
            requestId = "private-request",
            frameId = "private-frame",
            type = "Document",
            request = new
            {
                url = "http://127.0.0.1:12345/private-fixture",
                headers = new
                {
                    secret = "private-header"
                }
            }
        });
        // A known request ID alone cannot authorize a foreign-session or subresource failure.
        ObserveNetwork(diagnostics, "Network.loadingFailed", new
        {
            requestId = "private-request",
            type = "Document",
            errorText = "net::ERR_ABORTED",
            canceled = true
        }, "private-other-session");
        ObserveNetwork(diagnostics, "Network.loadingFailed", new
        {
            requestId = "private-request",
            type = "XHR",
            errorText = "net::ERR_ABORTED",
            canceled = true
        });
        Assert.Single(diagnostics.NetworkEvents);
        ObserveNetwork(diagnostics, "Network.loadingFailed", new
        {
            requestId = "private-request",
            type = "Document",
            errorText = "net::ERR_ABORTED",
            canceled = true,
            blockedReason = "inspector",
            corsErrorStatus = new
            {
                corsError = "LocalNetworkAccessPermissionDenied",
                failedParameter = "private-parameter"
            }
        });
        Assert.Equal("enabled", diagnostics.NetworkObservation);
        Assert.Collection(diagnostics.NetworkEvents,
            request => { Assert.Equal("Network.requestWillBeSent", request.Kind); Assert.Equal(1, request.RequestNumber); },
            failure =>
            {
                Assert.Equal("Network.loadingFailed", failure.Kind);
                Assert.Equal(1, failure.RequestNumber);
                Assert.Equal("Document", failure.ResourceType);
                Assert.Equal("expected-loopback", failure.UrlCategory);
                Assert.Equal("aborted", failure.ErrorCategory);
                Assert.True(failure.Canceled);
                Assert.Equal("inspector", failure.BlockedReason);
                Assert.Equal("LocalNetworkAccessPermissionDenied", failure.CorsError);
            });
        Assert.Equal(0, diagnostics.NetworkEventsDropped);
        string json = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("127.0.0.1", json);
        Assert.DoesNotContain("headers", json);
        Assert.DoesNotContain("failedParameter", json);
    }


    [Theory]
    [InlineData("private-other-session", "private-frame", "Document", "private-frame")]
    [InlineData("private-session", "private-subframe", "Document", "private-frame")]
    [InlineData("private-session", "private-frame", "XHR", "private-frame")]
    [InlineData("private-session", "private-frame", "Document", null)]
    public void UnrelatedOrUnknownDocumentIdentityCannotAuthorizeFailureEvidence(string session, string frame,
        string resource, string? knownMainFrame)
    {
        var diagnostics = new BrowserCdpDiagnostics();
        diagnostics.BeginNetworkObservation();
        ObserveNetwork(diagnostics, "Network.requestWillBeSent", new
        {
            requestId = "private-request",
            frameId = frame,
            type = resource,
            request = new
            {
                url = "http://127.0.0.1:12345/private-fixture"
            }
        }, session, knownMainFrame);
        ObserveNetwork(diagnostics, "Network.loadingFailed", new
        {
            requestId = "private-request",
            type = "Document",
            errorText = "net::ERR_ABORTED",
            canceled = true
        });
        Assert.Empty(diagnostics.NetworkEvents);
        Assert.Equal(0, diagnostics.NetworkEventsDropped);
    }

    [Fact]
    public void MissingUnknownAndForeignFailureFieldsNeverBecomeAffirmativeEvidence()
    {
        var diagnostics = new BrowserCdpDiagnostics();
        diagnostics.BeginNetworkObservation();
        ObserveNetwork(diagnostics, "Network.loadingFailed", new
        {
            requestId = "private-request",
            type = "Document"
        });
        Assert.Empty(diagnostics.NetworkEvents);
        ObserveNetwork(diagnostics, "Network.requestWillBeSent", new
        {
            requestId = "private-request",
            frameId = "private-frame",
            type = "Document",
            request = new
            {
            }
        });
        ObserveNetwork(diagnostics, "Network.loadingFailed", new
        {
            requestId = "private-request",
            type = "Document"
        });
        ObserveNetwork(diagnostics, "Network.loadingFailed", new
        {
            requestId = "private-request",
            type = "Document",
            errorText = "net::ERR_ABORTED private-url",
            canceled = "private-value",
            blockedReason = "private-reason",
            corsErrorStatus = new
            {
                corsError = "private-cors",
                failedParameter = "private-secret"
            }
        });
        Assert.Equal(3, diagnostics.NetworkEvents.Count);
        Assert.All(diagnostics.NetworkEvents.Skip(1), failure =>
        {
            Assert.Equal("unknown", failure.UrlCategory);
            Assert.Equal("unknown", failure.ErrorCategory);
            Assert.Null(failure.Canceled);
            Assert.Equal("unknown", failure.BlockedReason);
            Assert.Equal("unknown", failure.CorsError);
        });
        Assert.DoesNotContain("private", JsonSerializer.Serialize(diagnostics));
    }

    [Fact]
    public void ObservationOnlyCoversItsExplicitWindowAndKeepsSixteenEventsWithDropEvidence()
    {
        var diagnostics = new BrowserCdpDiagnostics();
        var request = new
        {
            requestId = "private-request",
            frameId = "private-frame",
            type = "Document",
            request = new
            {
                url = "about:blank"
            }
        };
        ObserveNetwork(diagnostics, "Network.requestWillBeSent", request);
        Assert.Empty(diagnostics.NetworkEvents);
        diagnostics.BeginNetworkObservation();
        for (int index = 0; index < 20; index++)
            ObserveNetwork(diagnostics, "Network.requestWillBeSent", request);
        Assert.Equal(16, diagnostics.NetworkEvents.Count);
        Assert.Equal(4, diagnostics.NetworkEventsDropped);
        Assert.All(diagnostics.NetworkEvents, entry =>
        {
            Assert.Equal("about-blank", entry.UrlCategory);
            Assert.Equal(1, entry.RequestNumber);
        });
        diagnostics.EndNetworkObservation();
        ObserveNetwork(diagnostics, "Network.loadingFailed", new
        {
            requestId = "private-request",
            type = "Document"
        });
        Assert.Equal(16, diagnostics.NetworkEvents.Count);
        Assert.Equal(4, diagnostics.NetworkEventsDropped);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(diagnostics));
    }

    private static void ObserveNetwork(BrowserCdpDiagnostics diagnostics, string kind, object parameters,
        string session = "private-session", string? mainFrame = "private-frame") =>
        diagnostics.Observe(JsonSerializer.SerializeToElement(new
        {
            method = kind,
            sessionId = session,
            @params = parameters
        }),
            "private-session", "private-target", mainFrame, "http://127.0.0.1:12345/private-fixture");

}
