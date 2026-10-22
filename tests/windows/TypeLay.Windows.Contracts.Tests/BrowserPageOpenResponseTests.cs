using System.Text.Json;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.Windows.Contracts.Tests;

public sealed class BrowserPageOpenResponseTests
{
    [Theory]
    [InlineData("{\"frameId\":\"owned-frame\"}", true)]
    [InlineData("{\"errorText\":\"net::ERR_CONNECTION_REFUSED http://synthetic.invalid/private\"}", false)]
    [InlineData("{\"errorText\":\"\"}", false)]
    [InlineData("{\"errorText\":null}", false)]
    [InlineData("{\"errorText\":42}", false)]
    [InlineData("null", false)]
    public void NavigationErrorCannotBecomeAReadinessWait(string json, bool success)
    {
        using var response = JsonDocument.Parse(json);
        Assert.Equal(success, BrowserPageOpenResponse.NavigationSucceeded(response.RootElement));
    }

    [Theory]
    [InlineData("net::ERR_ABORTED", "aborted")]
    [InlineData("net::ERR_CONNECTION_REFUSED", "connection-refused")]
    [InlineData("net::ERR_CONNECTION_RESET", "connection-reset")]
    [InlineData("net::ERR_CONNECTION_CLOSED", "connection-closed")]
    [InlineData("net::ERR_EMPTY_RESPONSE", "empty-response")]
    [InlineData("net::ERR_INVALID_HTTP_RESPONSE", "invalid-http-response")]
    [InlineData("net::ERR_BLOCKED_BY_CLIENT", "blocked-by-client")]
    [InlineData("net::ERR_BLOCKED_BY_ADMINISTRATOR", "blocked-by-administrator")]
    [InlineData("net::ERR_UNSAFE_PORT", "unsafe-port")]
    [InlineData("net::ERR_TIMED_OUT", "timed-out")]
    [InlineData("", "unknown")]
    [InlineData("net::ERR_NEW_ERROR", "unknown")]
    [InlineData("net::ERR_CONNECTION_REFUSED http://synthetic.invalid/private", "unknown")]
    public void NavigationDiagnosticsOnlyPublishExactAllowlistedCategories(string error, string expected)
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            errorText = error
        });
        Assert.Equal(expected, BrowserPageOpenResponse.NavigationFailure(response));
        Assert.False(BrowserPageOpenResponse.NavigationSucceeded(response));
    }

    [Theory]
    [InlineData("{}", "none")]
    [InlineData("null", "invalid-response")]
    [InlineData("{\"errorText\":null}", "unknown")]
    [InlineData("{\"errorText\":42}", "unknown")]
    public void MissingOrMalformedNavigationErrorPreservesItsObservation(string json, string expected)
    {
        using var response = JsonDocument.Parse(json);
        Assert.Equal(expected, BrowserPageOpenResponse.NavigationFailure(response.RootElement));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ReadinessPreservesBothActualObservations(bool documentComplete, bool stateAvailable)
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            documentComplete,
            stateAvailable
        });
        Assert.Equal((documentComplete, stateAvailable), BrowserPageOpenResponse.Readiness(response));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("{\"documentComplete\":true}")]
    [InlineData("{\"documentComplete\":true,\"stateAvailable\":\"true\"}")]
    public void MissingOrMalformedReadinessDoesNotInventAFalseOrTrueObservation(string json)
    {
        using var response = JsonDocument.Parse(json);
        Assert.Null(BrowserPageOpenResponse.Readiness(response.RootElement));
    }
}
