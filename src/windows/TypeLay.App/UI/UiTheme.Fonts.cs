using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;

namespace TypeLay.App;

internal static partial class UiTheme
{
    // Each static Serif family is a pinned optical-size cut. Sizes are WPF DIPs,
    // not physical pixels; DPI scaling belongs to the renderer, not this lookup.
    private static readonly double[] ContentOpticalSizes = [13, 14, 15, 16, 17, 18, 19];
    private static readonly double[] DisplayOpticalSizes = [11, 13.5, 15, 17, 18, 20, 22, 26, 30];
    private static string InterfaceLanguage => English ? "en" : "zh-CN";
    public static FontFamily UiFont => RoleFont(false, InterfaceLanguage);
    public static FontFamily UiFontForLanguage(string? language) => RoleFont(false, language);
    public static FontFamily DisplayFontForSize(double size) => RoleFont(_preferences.Font == "claude", InterfaceLanguage, size, display: true);
    public static FontFamily ContentFont => ContentFontForLanguage(_preferences.Target, ContentSize);
    public static FontFamily ContentFontForLanguage(string? language, double size) => RoleFont(_preferences.Font == "claude", language, size);

    // Font tiles preview each choice regardless of the currently selected face.
    public static FontFamily BundledSerifFont(double size, bool display, string? language) =>
        BundledRoleFont(SerifFamily(size, display), true, language);

    // WPF needs an absolute base URI and a relative family name. A single string
    // beginning with pack: silently falls back instead of loading the resource.
    public static FontFamily BundledFont(string family, string? fallback = null) =>
        new(new Uri("pack://application:,,,/TypeLay.App;component/"), "./UI/Fonts/#" + family + (string.IsNullOrWhiteSpace(fallback) ? "" : ", " + fallback));

    private static FontFamily RoleFont(bool serif, string? language, double size = 15, bool display = false)
    {
        if (_preferences.Font == "system")
            return new("Segoe UI, " + (Region(language) == "JP" ? "Yu Gothic UI" : Region(language) == "KR" ? "Malgun Gothic" : "Microsoft YaHei UI"));
        return BundledRoleFont(serif ? SerifFamily(size, display) : "Inter", serif, language);
    }

    private static string SerifFamily(double size, bool display)
    {
        double[] available = display ? DisplayOpticalSizes : ContentOpticalSizes;
        if (!available.Contains(size))
        {
            double fallback = display ? 22 : 15;
            Trace.WriteLine($"TypeLay Serif: unmapped {(display ? "display" : "content")} size {size}; using {fallback} optical cut (not an exact match).");
            size = fallback;
        }
        return "TypeLay Serif " + size.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string Region(string? language) =>
        language?.StartsWith("ja", StringComparison.OrdinalIgnoreCase) == true ? "JP"
            : language?.StartsWith("ko", StringComparison.OrdinalIgnoreCase) == true ? "KR" : "SC";

    private static FontFamily BundledRoleFont(string latin, bool serif, string? language)
    {
        string region = Region(language);
        string cjk = serif ? "Noto Serif " : "Noto Sans ";
        string[] regions = [region, .. new[] { "SC", "JP", "KR" }.Where(value => value != region)];
        // Every offline fallback needs its own resource locator. Bare family
        // names would query installed fonts and silently lose bundled CJK.
        return BundledFont(latin, string.Join(", ", regions.Select(value => "./UI/Fonts/#" + cjk + value)));
    }
}
