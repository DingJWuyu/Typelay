using Xunit;

namespace TypeLay.Windows.Contracts.Tests;

public sealed class HyPackageDirectionsTests
{
    private static readonly string[] Original = ["zh_ja", "ja_zh", "zh_ko", "ko_zh", "zh_fr", "fr_zh", "zh_es", "es_zh", "zh_de", "de_zh", "zh_pt", "pt_zh"];

    [Fact]
    public void FullTwelveKeepsTheOriginalSuiteButDoesNotClaimEnglishAcceptance() =>
        Assert.False(Classify(Original.Reverse()));

    [Fact]
    public void FullCandidateRequiresEnglishAcceptanceInsteadOfSilentlyRunningTheOldSuite() =>
        Assert.True(Classify(Original.Concat(new[] { "zh_en", "en_zh" })));

    [Fact]
    public void PartialDuplicateOrUnexpectedDirectionsCannotSelectEitherSuite()
    {
        foreach (var directions in new[]
        {
            Original.Skip(1).ToArray(), Original.Concat(new[] { "zh_en" }).ToArray(),
            Original.Concat(new[] { "en_zh" }).ToArray(), Original.Concat(new[] { "zh_en", "en_zh", "ja_en" }).ToArray(),
            Original.Concat(new[] { "zh_en", "zh_en" }).ToArray(), Array.Empty<string>()
        })
            Assert.Throws<InvalidDataException>(() => Classify(directions));
    }

    [Theory]
    [InlineData("different-package", "a0c709d9fac510f2c807aa3af52872340dc37a4a", "hy-mt-llama-cpu")]
    [InlineData("hy-mt2-1-8b-q4-k-m", "unknown-version", "hy-mt-llama-cpu")]
    [InlineData("hy-mt2-1-8b-q4-k-m", "a0c709d9fac510f2c807aa3af52872340dc37a4a", "argos")]
    public void MatchingDirectionsCannotAuthorizeAnotherPackage(string id, string version, string backend) =>
        Assert.Throws<InvalidDataException>(() => HyPackageDirections.RequiresEnglishAcceptance(id, version, backend, Original));

    private static bool Classify(IEnumerable<string> directions) => HyPackageDirections.RequiresEnglishAcceptance(
        "hy-mt2-1-8b-q4-k-m", "a0c709d9fac510f2c807aa3af52872340dc37a4a", "hy-mt-llama-cpu", directions);
}
