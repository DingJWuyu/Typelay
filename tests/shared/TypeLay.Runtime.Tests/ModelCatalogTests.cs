using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class ModelCatalogTests
{
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
