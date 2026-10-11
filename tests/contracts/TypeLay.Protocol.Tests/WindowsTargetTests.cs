using System.Globalization;
using TypeLay.Core;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.Protocol.Tests;

public sealed class WindowsTargetTests
{
    private static readonly WindowsTarget Source = new(42, 638_000_000_000_000_000, 4096, 8192,
        "uia/value", "field/🙂%20", Guid.Parse("3508fb51-9f2a-4a90-815b-e07361aee541"));

    [Fact]
    public void WindowsBindingRoundTripsWithoutLosingAnyLifetimeOrNativeIdentity()
    {
        Assert.True(WindowsTarget.TryDecode(Source.Id, out var decoded));
        Assert.Equal(Source, decoded);
        Assert.Equal("windows", Source.Id.Backend);
        Assert.Equal("42/638000000000000000/4096/8192/uia%2Fvalue/field%2F%F0%9F%99%82%2520", Source.Id.Value);
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("fr-FR")]
    public void BindingEncodingIsIndependentOfTheHostCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal("42/638000000000000000/4096/8192/uia%2Fvalue/field%2F%F0%9F%99%82%2520", Source.Id.Value);
            Assert.True(WindowsTarget.TryDecode(Source.Id, out var decoded));
            Assert.Equal(Source, decoded);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("42/2/3/4/uia/field/extra")]
    [InlineData("042/2/3/4/uia/field")]
    [InlineData("+42/2/3/4/uia/field")]
    [InlineData("42/2/3/4/uia%2fvalue/field")]
    [InlineData("42/2/3/4/uia/field%")]
    [InlineData("42/2/3/4/uia/field%GG")]
    [InlineData("42/2/3/4/uia/%ED%A0%80")]
    [InlineData("2147483648/2/3/4/uia/field")]
    [InlineData("42/9223372036854775808/3/4/uia/field")]
    [InlineData("")]
    [InlineData(null)]
    public void MalformedAndNoncanonicalBindingsAreRejected(string? value)
    {
        Assert.False(WindowsTarget.TryDecode(new("windows", value!, Source.Lifetime), out _));
    }

    [Fact]
    public void WindowsCannotInterpretAnotherBackendsIdentity()
    {
        var foreign = Source.Id with
        {
            Backend = "controlled"
        };
        Assert.False(WindowsTarget.TryDecode(foreign, out _));
        Assert.Throws<InvalidDataException>(() => foreign.AsWindows());
    }

    [Fact]
    public void ChangingAnyBindingComponentCreatesADistinctOpaqueTarget()
    {
        WindowsTarget[] different = [Source with { ProcessId = 43 }, Source with { ProcessStartUtcTicks = 1 },
            Source with { RootHwnd = 1 }, Source with { ControlHwnd = 1 }, Source with { Provider = "other" },
            Source with { ElementToken = "other" }, Source with { Lifetime = Guid.NewGuid() }];
        foreach (var value in different)
            Assert.NotEqual(Source.Id, value.Id);
    }

    [Fact]
    public async Task BrokerFramePreservesTheOriginalTargetAndIndependentWriteCapabilities()
    {
        var snapshot = new InputSnapshot(Source.Id, "hello🙂", new(0, 5), new(0, 0, 100, 20), true,
            FieldAccess.Supported, ImeState.Clear, 100, 2, 3, WriteSupported: false, DirectWriteSupported: true);
        var request = new BrokerRequest(BrokerProtocol.Version, 7, "capture", "private", BoundTarget: Source.Id,
            UserEpoch: 2, ForegroundEpoch: 3, SourceUserEpoch: 4, CaptureDirect: true, OverlayHwnd: 9000);
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        await Wire.WriteAsync(stream, new BrokerResponse(BrokerProtocol.Version, 7, "observed", snapshot), TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<BrokerRequest>(stream, TestContext.Current.CancellationToken);
        Assert.Equal(request, received);
        var response = await Wire.ReadAsync<BrokerResponse>(stream, TestContext.Current.CancellationToken);
        Assert.Equal(snapshot, response.Snapshot);
        Assert.Equal(Source, response.Snapshot!.Target.AsWindows());
    }
}
