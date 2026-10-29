using System.Globalization;

namespace TypeLay.Runtime;

// Labels describe existing choices; they never add a direction or grant model readiness.
public sealed record LanguageChoicePresentation(string LabelZh, string LabelEn, string NativeLabel)
{
    public static LanguageChoicePresentation Create(string code, bool available = true)
    {
        string chinese = code switch
        {
            "auto" => "自动检测",
            "zh" => "中文",
            "en" => "英语",
            "ja" => "日语",
            "ko" => "韩语",
            "fr" => "法语",
            "es" => "西班牙语",
            "de" => "德语",
            "pt" => "葡萄牙语",
            _ => code
        };
        string english = code;
        string native = code;
        if (code == "auto")
        {
            english = "Detect automatically";
            native = "Auto";
        }
        else if (chinese != code)
        {
            // EnglishName and NativeName are independent of the host UI culture.
            var culture = CultureInfo.GetCultureInfo(code);
            english = culture.EnglishName;
            native = culture.NativeName;
        }
        return new(available ? chinese : chinese + "（当前不可用）",
            available ? english : english + " (currently unavailable)", native);
    }
}
