using System.Text.Json;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class ModelCatalogTests
{
    [Theory]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData("zh_en", true, false)]
    [InlineData("en_zh", true, false)]
    [InlineData("en_zh,zh_en", true, true)]
    [InlineData("zh_en,zh_en", false, false)]
    [InlineData("zh_en,en_zh,fr_en", false, false)]
    public void AVerifiedSubsetDoesNotBecomeACompletePreparationBundle(string? ids, bool valid, bool complete)
    {
        string[]? directions = ids is null ? null : ids.Length == 0 ? [] : ids.Split(',');
        Assert.Equal(valid, ModelCatalog.IsValidDirectionSet(directions));
        Assert.Equal(complete, ModelCatalog.IsCompleteDirectionSet(directions));
    }

    [Theory]
    [InlineData("{\"schema\":1}", "zh_en,en_zh")]
    [InlineData("{\"schema\":1,\"directions\":[\"en_zh\",\"zh_en\"]}", "zh_en,en_zh")]
    [InlineData("{\"schema\":2,\"directions\":[\"en_zh\"]}", "en_zh")]
    public void InstallationIntentPreservesLegacyAndExplicitSubsets(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected.Split(','), ModelCatalog.ReadInstallationDirections(document.RootElement));
    }

    [Theory]
    [InlineData("{\"schema\":1,\"directions\":[\"zh_en\"]}")]
    [InlineData("{\"schema\":1,\"directions\":null}")]
    [InlineData("{\"schema\":2}")]
    [InlineData("{\"schema\":2,\"directions\":[]}")]
    [InlineData("{\"schema\":2,\"directions\":[\"zh_en\",\"zh_en\"]}")]
    [InlineData("{\"schema\":2,\"directions\":[\"fr_en\"]}")]
    [InlineData("{\"schema\":3,\"directions\":[\"zh_en\"]}")]
    public void DamagedIntentNeverBecomesAnEmptyOrSmallerInstallation(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => ModelCatalog.ReadInstallationDirections(document.RootElement));
    }

    [Fact]
    public void TheFixedCatalogOffersOnlyTheExistingDirectionsWithoutAnInstallation()
    {
        Assert.Equal(["zh_en", "en_zh"], ModelCatalog.Directions.Select(direction => direction.Id));
        Assert.Equal(["auto", "zh", "en"], ModelCatalog.SourceChoices.Select(choice => choice.Code));
        Assert.Equal(["zh", "en"], ModelCatalog.TargetChoices.Select(choice => choice.Code));
        var chinese = ModelCatalog.Get("zh_en");
        Assert.Equal(("zh", "en", "1.9"), (chinese.Source, chinese.Target, chinese.Version));
        Assert.Equal("https://argos-net.com/v1/translate-zh_en-1_9.argosmodel", chinese.Url);
        Assert.Equal("你好，我想确认一下明天下午的会议时间。", chinese.SelfCheckText);
        Assert.Throws<ArgumentException>(() => ModelCatalog.Get("fr_en"));
    }

    [Theory]
    [InlineData("auto", "en", true)]
    [InlineData("auto", "zh", true)]
    [InlineData("zh", "en", true)]
    [InlineData("en", "zh", true)]
    [InlineData("zh", "zh", true)]
    [InlineData("en", "en", true)]
    [InlineData("en", "auto", false)]
    [InlineData("fr", "en", false)]
    [InlineData("auto", "fr", false)]
    [InlineData("ZH", "en", false)]
    public void ConfigurationsPreserveAutoAndSameLanguageWithoutCreatingNewDirections(string source, string target, bool valid)
    {
        Assert.Equal(valid, ModelCatalog.IsSupportedSetting(source, target));
    }
}
