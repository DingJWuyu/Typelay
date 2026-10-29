using System.Globalization;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class LanguageChoicePresentationTests
{
    [Theory]
    [InlineData("auto", "自动检测", "Detect automatically", "Auto")]
    [InlineData("zh", "中文", "Chinese", "中文")]
    [InlineData("en", "英语", "English", "English")]
    [InlineData("ja", "日语", "Japanese", "日本語")]
    [InlineData("ko", "韩语", "Korean", "한국어")]
    [InlineData("fr", "法语", "French", "français")]
    [InlineData("es", "西班牙语", "Spanish", "español")]
    [InlineData("de", "德语", "German", "Deutsch")]
    [InlineData("pt", "葡萄牙语", "Portuguese", "português")]
    public void SupportedLanguageLabelsDoNotDependOnHostUiCulture(string code, string chinese, string english, string native)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string culture in new[] { "zh-CN", "en-US" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var choice = LanguageChoicePresentation.Create(code);
                Assert.Equal(chinese, choice.LabelZh);
                Assert.Equal(english, choice.LabelEn);
                Assert.Equal(native, choice.NativeLabel);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void SavedUnavailableChoiceRetainsItsIdentityAndStatusInBothUiLanguages()
    {
        var choice = LanguageChoicePresentation.Create("ja", available: false);
        Assert.Equal("日语（当前不可用）", choice.LabelZh);
        Assert.Equal("Japanese (currently unavailable)", choice.LabelEn);
        Assert.Equal("日本語", choice.NativeLabel);
        var unknown = LanguageChoicePresentation.Create("future-language", available: false);
        Assert.Equal("future-language（当前不可用）", unknown.LabelZh);
        Assert.Equal("future-language (currently unavailable)", unknown.LabelEn);
        Assert.Equal("future-language", unknown.NativeLabel);
    }
}
