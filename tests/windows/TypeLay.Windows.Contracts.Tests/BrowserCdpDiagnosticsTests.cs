using System.Text.Json;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.Windows.Contracts.Tests;

public sealed class BrowserCdpDiagnosticsTests
{
    [Theory]
    [InlineData("Page.frameStartedLoading")]
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

}
