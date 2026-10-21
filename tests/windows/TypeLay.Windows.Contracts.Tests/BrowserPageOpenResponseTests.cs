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
