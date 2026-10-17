using System.Text.Json;
using TypeLay.Protocol;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.Windows.Contracts.Tests;

public sealed class BrowserHostContractTests
{
    [Theory]
    [InlineData("abcdefghijklmnopabcdefghijklmnop", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("Abcdefghijklmnopabcdefghijklmnop", false)]
    [InlineData("abcdefghijklmnopabcdefghijklmnox", false)]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/", false)]
    [InlineData("", false)]
    public void RegistrationAcceptsOnlyAnExactChromeExtensionId(string value, bool accepted)
    {
        Assert.Equal(accepted, BrowserHostProtocol.IsExtensionId(value));
    }

    [Fact]
    public void OriginRequiresTheExactExtensionSchemeAndTrailingSlash()
    {
        const string extension = "abcdefghijklmnopabcdefghijklmnop";
        const string origin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop/";
        Assert.Equal(origin, BrowserHostProtocol.OriginForExtensionId(extension));
        Assert.True(BrowserHostProtocol.IsOrigin(origin));
        foreach (string invalid in new[] { origin[..^1], origin + "page.html", origin + "?id=1",
            origin.ToUpperInvariant(), "https://" + extension + "/", "chrome-extension://*/" })
            Assert.False(BrowserHostProtocol.IsOrigin(invalid));
        Assert.Throws<ArgumentException>(() => BrowserHostProtocol.OriginForExtensionId("invalid"));
    }

    [Fact]
    public void PipeIdentitySeparatesUsersSessionsAndInstallations()
    {
        string root = Path.Combine(Path.GetTempPath(), "TypeLay Browser Contract");
        string name = BrowserHostProtocol.PipeName("S-1-5-21-1000", 2, root);
        Assert.Equal(name, BrowserHostProtocol.PipeName("S-1-5-21-1000", 2,
            root.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.NotEqual(name, BrowserHostProtocol.PipeName("S-1-5-21-1001", 2, root));
        Assert.NotEqual(name, BrowserHostProtocol.PipeName("S-1-5-21-1000", 3, root));
        Assert.NotEqual(name, BrowserHostProtocol.PipeName("S-1-5-21-1000", 2, root + " other"));
        Assert.DoesNotContain("S-1-5-21", name);
        Assert.Throws<ArgumentException>(() => BrowserHostProtocol.PipeName("", 2, root));
        Assert.Throws<ArgumentException>(() => BrowserHostProtocol.PipeName("S-1-5-21-1000", -1, root));
        Assert.Throws<ArgumentException>(() => BrowserHostProtocol.PipeName("S-1-5-21-1000", 2, "relative"));
    }

    [Fact]
    public async Task HandshakeRetainsTheOriginAndConnectionLifetimeWithoutClaimingProcessIdentity()
    {
        var lifetime = Guid.Parse("24828a8a-151f-44ba-b052-b87a97581740");
        const string origin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop/";
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, new
        {
            @event = "hello",
            data = new BrowserHostHello(1, origin, lifetime)
        }, TestContext.Current.CancellationToken);
        stream.Position = 0;
        JsonElement hello = await Wire.ReadAsync<JsonElement>(stream, TestContext.Current.CancellationToken);
        Assert.Equal("hello", hello.GetProperty("event").GetString());
        JsonElement data = hello.GetProperty("data");
        Assert.Equal(new[] { "lifetime", "origin", "protocol" }, data.EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal(new BrowserHostHello(1, origin, lifetime), data.Deserialize<BrowserHostHello>(Wire.Json));
        stream.SetLength(0);
        await Wire.WriteAsync(stream, new
        {
            @event = "welcome",
            data = new BrowserHostWelcome(1, lifetime)
        }, TestContext.Current.CancellationToken);
        stream.Position = 0;
        JsonElement welcome = await Wire.ReadAsync<JsonElement>(stream, TestContext.Current.CancellationToken);
        Assert.Equal("welcome", welcome.GetProperty("event").GetString());
        Assert.Equal(new BrowserHostWelcome(1, lifetime), welcome.GetProperty("data").Deserialize<BrowserHostWelcome>(Wire.Json));
    }
}
