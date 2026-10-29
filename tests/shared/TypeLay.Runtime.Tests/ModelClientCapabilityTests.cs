using System.Diagnostics;
using System.Text.Json;
using Xunit;
using TypeLay.Core;

namespace TypeLay.Runtime.Tests;

// A real pipe peer controls protocol responses only. It does not load a model
// and cannot establish inference quality or installed-file integrity.
public sealed class ModelClientCapabilityTests
{
    private const string HyInventory = """
        {"schema":3,"activePackages":["hy-mt2-1-8b-q4-k-m"],"packages":[
          {"id":"hy-mt2-1-8b-q4-k-m","version":"a0c709d9fac510f2c807aa3af52872340dc37a4a",
           "directions":["zh_ja"],"files":[{"path":"model.gguf","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}]}
        """;
    private const string HyHello = """
        {"status":"ready","directions":["zh_ja"],"packages":[
          {"id":"hy-mt2-1-8b-q4-k-m","version":"a0c709d9fac510f2c807aa3af52872340dc37a4a","directions":["zh_ja"]}]}
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("styledTranslationVersion")]
    [InlineData("glossaryTranslationVersion")]
    [InlineData("styledGlossaryTranslationVersion")]
    public async Task CandidateEnglishHelloGrantsOrdinaryTranslationButCannotClaimPackageWideAdvancedCapabilities(string? capability)
    {
        var catalog = CandidateCatalog();
        string hello = HyHello.Replace("zh_ja", "zh_en", StringComparison.Ordinal);
        if (capability is not null)
            hello = hello.Replace("\"status\":\"ready\"", "\"status\":\"ready\",\"" + capability + "\":1", StringComparison.Ordinal);
        using var peer = new ProtocolPeer(hello, """{"status":"translated","text":"ordinary English"}""",
            HyInventory.Replace("zh_ja", "zh_en", StringComparison.Ordinal));
        var outcome = await peer.Client.ReloadAsync(peer.CreateLoadPlan(catalog),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken);
        Assert.Equal(capability is null ? ModelPublicationOutcome.Confirmed : ModelPublicationOutcome.Unchanged, outcome);
        Assert.Null(peer.Client.StyledTranslationCapability);
        Assert.Null(peer.Client.GlossaryTranslationCapability);
        Assert.Null(peer.Client.StyledGlossaryTranslationCapability);
        Assert.Equal(capability is null, peer.Client.Ready);
        if (capability is null)
            Assert.Equal("ordinary English", (await peer.Client.TranslateTextAsync("你好", new("zh", "en"))).Text);
    }

    [Fact]
    public async Task IndexEnglishCapabilitiesAreBoundToItsOwnExactPackageAndNotHyScopeRules()
    {
        string hello = IndexIdentity(HyHello).Replace("\"status\":\"ready\"",
            "\"status\":\"ready\"," + AdvancedVersions, StringComparison.Ordinal);
        using var peer = new ProtocolPeer(hello, """{"status":"translated","text":"English result"}""", IndexIdentity(HyInventory));
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(ModelCatalog.Packages),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.True(peer.Client.StyledTranslationCapability!.Supports(new("zh", "en")));
        Assert.True(peer.Client.GlossaryTranslationCapability!.Supports(new("zh", "en")));
        Assert.False(peer.Client.GlossaryTranslationCapability.Supports(new("zh", "ja")));
        Assert.Equal("English result", (await peer.Client.TranslateTextAsync("你好", new("zh", "en"))).Text);
    }

    private static ModelPackageCatalog CandidateCatalog() => ModelCatalog.Packages;

    private const string MixedDirections = "[\"zh_ja\",\"ja_zh\",\"zh_en\",\"en_zh\"]";
    private const string AdvancedVersions = "\"styledTranslationVersion\":1,\"glossaryTranslationVersion\":1,\"styledGlossaryTranslationVersion\":1";
    private static string ScopedHello(string scope = "[\"zh_ja\",\"ja_zh\"]") =>
        HyHello.Replace("[\"zh_ja\"]", MixedDirections, StringComparison.Ordinal)
            .Replace("\"status\":\"ready\"", "\"status\":\"ready\"," + AdvancedVersions
                + ",\"advancedTranslationDirections\":" + scope, StringComparison.Ordinal);

    [Fact]
    public async Task CandidateScopedHelloKeepsOriginalAdvancedTranslationWithoutGrantingEnglish()
    {
        using var peer = new ProtocolPeer(ScopedHello(), """{"status":"translated","text":"scoped result"}""",
            HyInventory.Replace("[\"zh_ja\"]", MixedDirections, StringComparison.Ordinal));
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(CandidateCatalog()),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.True(peer.Client.StyledTranslationCapability!.Supports(new("zh", "ja")));
        Assert.True(peer.Client.GlossaryTranslationCapability!.Supports(new("auto", "ja")));
        Assert.False(peer.Client.StyledGlossaryTranslationCapability!.Supports(new("auto", "zh")));
        Assert.False(peer.Client.StyledTranslationCapability.Supports(new("zh", "en")));
        var job = GlossaryJob() with
        {
            Style = TranslationStyle.Formal
        };
        Assert.Equal("scoped result", (await peer.Client.TranslateAsync(job)).Text);
        Assert.Equal("glossary-combination-unavailable", (await peer.Client.TranslateAsync(job with
        {
            Direction = new("zh", "en"),
            Glossary = new(7, [new("你好", "fixed")])
        })).Code);
        Assert.Equal(3, (await peer.Client.TranslateTextAsync("你好", new("zh", "en"))).Id);
    }

    [Fact]
    public async Task DirectionScopeDoesNotInventMissingOperationVersions()
    {
        using var peer = new ProtocolPeer(ScopedHello().Replace(AdvancedVersions, "\"styledTranslationVersion\":1", StringComparison.Ordinal),
            """{"status":"translated","text":"styled result"}""",
            HyInventory.Replace("[\"zh_ja\"]", MixedDirections, StringComparison.Ordinal));
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(CandidateCatalog()),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.NotNull(peer.Client.StyledTranslationCapability);
        Assert.Null(peer.Client.GlossaryTranslationCapability);
        Assert.Null(peer.Client.StyledGlossaryTranslationCapability);
        var job = GlossaryJob();
        Assert.Equal("glossary-unavailable", (await peer.Client.TranslateAsync(job)).Code);
        Assert.Equal("glossary-combination-unavailable", (await peer.Client.TranslateAsync(job with
        {
            Style = TranslationStyle.Formal
        })).Code);
        var result = await peer.Client.TranslateAsync(job with
        {
            Glossary = null,
            Style = TranslationStyle.Formal
        });
        Assert.Equal("styled result", result.Text);
        Assert.Equal(2, result.Id);
    }

    [Fact]
    public void AdvancedScopeFreezesBothInputsAndRequiresAllPossibleAutoSources()
    {
        var catalog = CandidateCatalog();
        var selected = catalog.Packages.Single(package => package.Backend == "hy-mt-llama-cpu").Directions.ToArray();
        string[] advanced = ["zh_ja", "ja_zh"];
        var scope = new AdvancedTranslationScope(selected, advanced);
        advanced[0] = "zh_en";
        selected[Array.FindIndex(selected, direction => direction.Id == "zh_ja")] = selected.First(direction => direction.Id == "zh_en");
        Assert.True(scope.Supports(new("zh", "ja")));
        Assert.True(scope.Supports(new("auto", "ja")));
        Assert.False(scope.Supports(new("zh", "en")));
        Assert.False(scope.Supports(new("auto", "zh")));
        Assert.False(scope.Supports(new("auto", "missing")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"zh_ja\",\"zh_ja\"]")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"unknown\"]")]
    [InlineData("[\"zh_en\"]")]
    [InlineData("[\"en_zh\"]")]
    [InlineData("[\"zh_fr\"]")]
    [InlineData("\"zh_ja\"")]
    public async Task InvalidAdvancedScopeNeverGrantsReady(string scope)
    {
        using var peer = new ProtocolPeer(ScopedHello(scope),
            inventory: HyInventory.Replace("[\"zh_ja\"]", MixedDirections, StringComparison.Ordinal));
        Assert.Equal(ModelPublicationOutcome.Unchanged, await peer.Client.ReloadAsync(peer.CreateLoadPlan(CandidateCatalog()),
            () => throw new InvalidOperationException("must-not-publish"), _ => false, TestContext.Current.CancellationToken));
        Assert.False(peer.Client.Ready);
        Assert.Null(peer.Client.StyledTranslationCapability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvancedScopeCannotGrantVersionsOrAuthorizeNonHy(bool argos)
    {
        string hello = argos
            ? """{"status":"ready","directions":["zh_en","en_zh"],"advancedTranslationDirections":["zh_en"]}"""
            : ScopedHello().Replace(AdvancedVersions + ",", "", StringComparison.Ordinal);
        using var peer = new ProtocolPeer(hello, inventory: argos ? """{"schema":1}"""
            : HyInventory.Replace("[\"zh_ja\"]", MixedDirections, StringComparison.Ordinal));
        if (argos)
            await peer.Client.StartAsync();
        else
            Assert.Equal(ModelPublicationOutcome.Unchanged, await peer.Client.ReloadAsync(peer.CreateLoadPlan(CandidateCatalog()),
                () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.False(peer.Client.Ready);
    }

    [Theory]
    [InlineData("zh", "en")]
    [InlineData("en", "zh")]
    [InlineData("auto", "zh")]
    public async Task EveryScopedAdvancedOperationRejectsBeforeSendingWithoutOrdinaryFallback(string source, string target)
    {
        using var peer = new ProtocolPeer(ScopedHello(), """{"status":"translated","text":"ordinary remains"}""",
            HyInventory.Replace("[\"zh_ja\"]", MixedDirections, StringComparison.Ordinal));
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(CandidateCatalog()),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        var glossary = GlossaryJob(source, target, new(7, [new("你好", "fixed")]));
        Assert.Equal("style-unavailable", (await peer.Client.TranslateAsync(glossary with
        {
            Glossary = null,
            Style = TranslationStyle.Formal
        })).Code);
        Assert.Equal("glossary-unavailable", (await peer.Client.TranslateAsync(glossary)).Code);
        Assert.Equal("glossary-combination-unavailable", (await peer.Client.TranslateAsync(glossary with
        {
            Style = TranslationStyle.Formal
        })).Code);
        Assert.Equal(2, (await peer.Client.TranslateTextAsync("你好", new("zh", "en"))).Id);
    }

    [Fact]
    public async Task OldHelloWithoutStyleVersionNeverReceivesStyledRequests()
    {
        using var peer = new ProtocolPeer(HyHello, """{"status":"translated","text":"ordinary"}""", HyInventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.Equal("style-unavailable", (await peer.Client.TranslateAsync(GlossaryJob() with
        {
            Glossary = null,
            Style = TranslationStyle.Formal
        })).Code);
        Assert.Equal(2, (await peer.Client.TranslateTextAsync("你好", new("zh", "ja"))).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranslationResponseCannotChangeNegotiatedAdvancedScope(bool styled)
    {
        using var peer = new ProtocolPeer(ScopedHello(),
            """{"status":"translated","text":"untrusted","advancedTranslationDirections":["zh_en"]}""",
            HyInventory.Replace("[\"zh_ja\"]", MixedDirections, StringComparison.Ordinal));
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(CandidateCatalog()),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        var result = styled ? await peer.Client.TranslateAsync(GlossaryJob() with
        {
            Glossary = null,
            Style = TranslationStyle.Formal
        }) : await peer.Client.TranslateTextAsync("你好", new("zh", "ja"));
        Assert.Equal("error", result.Status);
        Assert.Empty(result.Text);
        Assert.False(peer.Client.Ready);
    }

    [Fact]
    public async Task LegacyHelloKeepsOrdinaryTranslationWithoutInventingVersionIdentity()
    {
        using var peer = new ProtocolPeer("""{"status":"ready","directions":["zh_en","en_zh"]}""",
            """{"status":"translated","text":"ordinary"}""");
        Assert.Null(peer.Client.VerifiedIdentity);
        await peer.Client.StartAsync();
        Assert.True(peer.Client.Ready);
        Assert.Null(peer.Client.VerifiedIdentity);
        Assert.Equal("ordinary", (await peer.Client.TranslateTextAsync("你好", new("zh", "en"))).Text);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task VerifiedPackageHelloAuthorizesOnlyItsLiveIdentity(int schema)
    {
        string hello = schema == 1
            ? """{"status":"ready","directions":["zh_en","en_zh"],"packages":[{"id":"argos-zh-en","version":"1.9","directions":["zh_en"]},{"id":"argos-en-zh","version":"1.9","directions":["en_zh"]}]}"""
            : """{"status":"ready","directions":["zh_en"],"packages":[{"id":"argos-zh-en","version":"1.9","directions":["zh_en"]}]}""";
        string inventory = schema switch
        {
            1 => """{"schema":1}""",
            2 => """{"schema":2,"directions":["zh_en"]}""",
            _ => """{"schema":3,"activePackages":["argos-zh-en"],"packages":[{"id":"argos-zh-en","version":"1.9","directions":["zh_en"],"files":[{"path":"metadata.json","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}]}"""
        };
        using var peer = new ProtocolPeer(hello, """{"status":"translated","text":"different"}""", inventory);
        if (schema == 3)
            Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
                () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        else
            await peer.Client.StartAsync();
        Assert.True(peer.Client.Ready);
        var identity = Assert.IsType<VerifiedModelIdentity>(peer.Client.VerifiedIdentity);
        Assert.Equal(schema == 1 ? 2 : 1, identity.Packages.Count);
        Assert.Equal("1.9", identity.Packages[0].Version);
        Assert.True(identity.AlternativeTranslationAvailable);
        Assert.False(TranslationReleasePolicy.Current.Allows(TranslationOperation.Alternative, identity, new("zh", "en")));
        Assert.Equal("different", (await peer.Client.TranslateAsync(AlternativeJob(), identity)).Text);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.NotSame(identity, peer.Client.VerifiedIdentity);
        Assert.True(peer.Client.VerifiedIdentity!.AlternativeTranslationAvailable);
        Assert.Equal("not-ready", (await peer.Client.TranslateAsync(AlternativeJob(), identity)).Code);
        peer.Client.Dispose();
        Assert.Null(peer.Client.VerifiedIdentity);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"id\":\"argos-zh-en\",\"version\":\"1.8\",\"directions\":[\"zh_en\"]}]")]
    [InlineData("[{\"id\":\"argos-zh-en\",\"version\":\"1.10\",\"directions\":[\"zh_en\"]}]")]
    [InlineData("[{\"id\":\"argos-zh-en\",\"version\":\"1.9\",\"directions\":[\"en_zh\"]}]")]
    [InlineData("[{\"id\":\"argos-en-zh\",\"version\":\"1.9\",\"directions\":[\"zh_en\"]}]")]
    [InlineData("[{\"id\":\"argos-zh-en\",\"version\":\"1.9\",\"directions\":[\"zh_en\",\"en_zh\"]}]")]
    public async Task LegacySubsetCannotAcceptMismatchedAdvertisedIdentity(string packages)
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"zh_en\"],\"packages\":" + packages + "}",
            inventory: """{"schema":2,"directions":["zh_en"]}""");
        await peer.Client.StartAsync();
        Assert.False(peer.Client.Ready);
        Assert.Null(peer.Client.VerifiedIdentity);
        Assert.Empty(peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpecialDispatchRejectsAnIdentityFromBeforeReplacementOrRestoration(bool restore)
    {
        using var peer = new ProtocolPeer(HyHello, """{"status":"translated","text":"ordinary"}""", HyInventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        var old = Assert.IsType<VerifiedModelIdentity>(peer.Client.VerifiedIdentity);
        var outcome = restore ? ModelPublicationOutcome.Unchanged : ModelPublicationOutcome.Confirmed;
        Assert.Equal(outcome, await peer.Client.ReloadAsync(peer.CreateLoadPlan(), () => outcome, _ => restore, TestContext.Current.CancellationToken));
        var current = Assert.IsType<VerifiedModelIdentity>(peer.Client.VerifiedIdentity);
        Assert.NotSame(old, current);
        Assert.NotEqual(old.ConnectionId, current.ConnectionId);
        var job = AlternativeJob() with
        {
            Kind = TranslationKind.FullOrSelection,
            PreviousCandidate = null,
            Direction = new("zh", "ja"),
            Style = TranslationStyle.Formal
        };
        Assert.Equal("not-ready", (await peer.Client.TranslateAsync(job, old)).Code);
        // Rejection happened before the new connection received any translation.
        Assert.Equal(2, (await peer.Client.TranslateTextAsync("你好", new("zh", "ja"))).Id);
        peer.Client.Dispose();
        Assert.Null(peer.Client.VerifiedIdentity);
    }

    [Fact]
    public async Task AdvertisedStyledCapabilityIsStableOnlyForItsLiveConnection()
    {
        using var peer = new ProtocolPeer(HyHello.Replace("\"status\":\"ready\"",
            "\"status\":\"ready\",\"styledTranslationVersion\":1"),
            """{"status":"translated","text":"Ordinary result"}""", HyInventory);
        ITranslationService service = peer.Client;
        Assert.Null(service.StyledTranslationCapability);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        var first = Assert.IsType<StyledTranslationCapability>(service.StyledTranslationCapability);
        Assert.True(first.Revision > 0);
        Assert.NotNull(first.Scope);
        Assert.True(first.Supports(new("zh", "ja")));
        Assert.True(first.Supports(new("auto", "ja")));
        Assert.False(first.Supports(new("zh", "fr")));
        Assert.False(first.Supports(new("auto", "zh")));
        Assert.Same(first, service.StyledTranslationCapability);
        Assert.Equal("Ordinary result", (await peer.Client.TranslateTextAsync("你好", new("zh", "ja"))).Text);
        Assert.Same(first, service.StyledTranslationCapability);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () =>
            {
                Assert.Null(service.StyledTranslationCapability);
                return ModelPublicationOutcome.Confirmed;
            }, _ => false, TestContext.Current.CancellationToken));
        var replacement = Assert.IsType<StyledTranslationCapability>(service.StyledTranslationCapability);
        Assert.NotSame(first, replacement);
        Assert.True(replacement.Revision > first.Revision);
        peer.Client.Dispose();
        Assert.Null(service.StyledTranslationCapability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyHyHelloRetainsOrdinaryTranslationWithoutGrantingStyle(bool explicitNull)
    {
        string hello = explicitNull ? HyHello.Replace("\"status\":\"ready\"",
            "\"status\":\"ready\",\"styledTranslationVersion\":null") : HyHello;
        using var peer = new ProtocolPeer(hello, """{"status":"translated","text":"Ordinary result"}""", HyInventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.True(peer.Client.Ready);
        Assert.Null(peer.Client.StyledTranslationCapability);
        Assert.Equal("Ordinary result", (await peer.Client.TranslateTextAsync("你好", new("zh", "ja"))).Text);
        Assert.Null(peer.Client.StyledTranslationCapability);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("\"1\"")]
    public async Task InvalidStyleVersionCannotPublishAWorkerOrGrantCapability(string version)
    {
        using var peer = new ProtocolPeer(HyHello.Replace("\"status\":\"ready\"",
            "\"status\":\"ready\",\"styledTranslationVersion\":" + version), inventory: HyInventory);
        Assert.Equal(ModelPublicationOutcome.Unchanged, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => throw new InvalidOperationException("must-not-publish"), _ => false, TestContext.Current.CancellationToken));
        Assert.False(peer.Client.Ready);
        Assert.Null(peer.Client.StyledTranslationCapability);
    }

    [Fact]
    public async Task ArgosCannotAdvertiseTheHyStyleCapability()
    {
        using var peer = new ProtocolPeer("""{"status":"ready","directions":["zh_en","en_zh"],"styledTranslationVersion":1}""");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(peer.Client.Ready);
        Assert.Null(peer.Client.StyledTranslationCapability);
    }

    [Fact]
    public async Task AStoppedWorkerRevokesItsAdvertisedStyleCapability()
    {
        using var peer = new ProtocolPeer(HyHello.Replace("\"status\":\"ready\"",
            "\"status\":\"ready\",\"styledTranslationVersion\":1"), inventory: HyInventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.NotNull(peer.Client.StyledTranslationCapability);
        Assert.Equal("worker-stopped", (await peer.Client.TranslateTextAsync("你好", new("zh", "ja"))).Code);
        Assert.Null(peer.Client.StyledTranslationCapability);
        Assert.False(peer.Client.Ready);
    }

    [Fact]
    public async Task AWorkerThatExitsAfterHelloLosesStyleCapabilityWithoutATranslationRequest()
    {
        using var peer = new ProtocolPeer(HyHello.Replace("\"status\":\"ready\"",
            "\"status\":\"ready\",\"styledTranslationVersion\":1"), inventory: HyInventory, exitAfterHello: true);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        await peer.WaitForExit();
        Assert.Null(peer.Client.StyledTranslationCapability);
    }

    [Fact]
    public async Task RestoringTheOldPackageCreatesANewConnectionCapability()
    {
        using var peer = new ProtocolPeer(HyHello.Replace("\"status\":\"ready\"",
            "\"status\":\"ready\",\"styledTranslationVersion\":1"), inventory: HyInventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        var first = Assert.IsType<StyledTranslationCapability>(peer.Client.StyledTranslationCapability);
        Assert.Equal(ModelPublicationOutcome.Unchanged, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Unchanged, _ => true, TestContext.Current.CancellationToken));
        var restored = Assert.IsType<StyledTranslationCapability>(peer.Client.StyledTranslationCapability);
        Assert.NotSame(first, restored);
        Assert.True(restored.Revision > first.Revision);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("version")]
    [InlineData("direction")]
    [InlineData("union")]
    [InlineData("unexpected-field")]
    public async Task SchemaThreeRequiresTheExactIndependentPackageSelection(string mismatch)
    {
        var hello = System.Text.Json.Nodes.JsonNode.Parse(HyHello)!;
        var packages = hello["packages"]!.AsArray();
        switch (mismatch)
        {
            case "missing":
                hello.AsObject().Remove("packages");
                break;
            case "null":
                hello["packages"] = null;
                break;
            case "empty":
                packages.Clear();
                break;
            case "duplicate":
                packages.Add(packages[0]!.DeepClone());
                break;
            case "unknown":
                packages[0]!["id"] = "unknown";
                break;
            case "version":
                packages[0]!["version"] = "different";
                break;
            case "direction":
                packages[0]!["directions"] = new System.Text.Json.Nodes.JsonArray("ja_zh");
                break;
            case "union":
                hello["directions"] = new System.Text.Json.Nodes.JsonArray("ja_zh");
                break;
            default:
                packages[0]!["backend"] = "hy-mt-llama-cpu";
                break;
        }
        using var peer = new ProtocolPeer(hello.ToJsonString(), inventory: HyInventory);
        Assert.Equal(ModelPublicationOutcome.Unchanged, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => throw new InvalidOperationException("must-not-publish"), _ => false, TestContext.Current.CancellationToken));
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("auto", "zh")]
    [InlineData("ja", "ja")]
    [InlineData("en", "ja")]
    [InlineData("auto", "pt")]
    [InlineData("ko", "zh")]
    [InlineData("es", "es")]
    public async Task SchemaThreeSyntaxReachesTheWorkerBeforeMissingPairChecks(string source, string target)
    {
        using var peer = new ProtocolPeer(HyHello, "{\"status\":\"no-translation\"}", HyInventory);
        var plan = peer.CreateLoadPlan();
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(plan,
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.True(peer.Client.Ready);
        Assert.Equal(["zh_ja"], peer.Client.AvailableDirections);
        Assert.Equal("no-translation", (await peer.Client.TranslateTextAsync("synthetic text", new(source, target))).Status);
        Assert.Equal("invalid-request", (await peer.Client.TranslateTextAsync("synthetic text", new("xx", target))).Code);
        Assert.True(peer.Client.Ready);
    }

    [Theory]
    [InlineData("zh_ko")]
    [InlineData("pt_zh")]
    public async Task NewHyDirectionRemainsAnExactLoadedSubset(string direction)
    {
        using var peer = new ProtocolPeer(HyHello.Replace("zh_ja", direction), inventory: HyInventory.Replace("zh_ja", direction));
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.True(peer.Client.Ready);
        Assert.Equal([direction], peer.Client.AvailableDirections);
        Assert.Equal([direction], peer.Client.AvailablePackages.Single().Directions.Select(item => item.Id));
    }

    [Fact]
    public async Task MissingWorkerPreflightNotifiesTheLoadFailureWithoutStartingAChild()
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"en_zh\"]}");
        File.Delete(peer.CreateLoadPlan().Paths.Worker);
        int notifications = 0;
        peer.Client.Changed += () => notifications++;
        await peer.Client.StartAsync();
        Assert.False(peer.Client.Ready);
        Assert.Equal("模型无法加载（请检查本地组件）", peer.Client.Status);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task LegacyEntryCannotAcceptSchemaThreeInventoryWithoutAnIndependentPlan()
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"en_zh\"]}",
            inventory: "{\"schema\":3}");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("{\"status\":\"ready\"}")]
    [InlineData("{\"status\":\"ready\",\"directions\":null}")]
    [InlineData("{\"status\":\"ready\",\"directions\":[]}")]
    [InlineData("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"zh_en\"]}")]
    [InlineData("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"en_zh\",\"fr_en\"]}")]
    [InlineData("{\"status\":\"ready\",\"directions\":[\"zh_en\",null]}")]
    [InlineData("{\"status\":\"ready\",\"directions\":\"zh_en,en_zh\"}")]
    [InlineData("{\"status\":\"error\",\"directions\":[\"zh_en\",\"en_zh\"]}")]
    public async Task AnUnconfirmedCatalogDoesNotGrantTranslationCapability(string response)
    {
        using var peer = new ProtocolPeer(response);
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("en_zh,zh_en")]
    [InlineData("zh_en")]
    [InlineData("en_zh")]
    public async Task OnlyAValidNonemptyHandshakeGrantsItsCapabilitiesAndDisposalClearsThem(string ids)
    {
        var directions = ids.Split(',');
        using var peer = new ProtocolPeer(JsonSerializer.Serialize(new
        {
            status = "ready",
            directions
        }), inventory: JsonSerializer.Serialize(new
        {
            schema = 2,
            directions
        }));
        Assert.True(peer.Client.Installed);
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(peer.Client.Ready);
        Assert.Equal(directions, peer.Client.AvailableDirections);
        if (peer.Client.AvailableDirections is IList<string> mutable)
            Assert.Throws<NotSupportedException>(() => mutable[0] = "fr_en");
        peer.Client.Dispose();
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("auto", "no-translation", "")]
    [InlineData("zh", "no-translation", "")]
    [InlineData("auto", "error", "language-unavailable")]
    [InlineData("zh", "error", "language-unavailable")]
    public async Task ValidRequestsReachTheSubsetWorkerForSameLanguageOrMissingDirection(string source, string status, string code)
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"en_zh\"]}",
            JsonSerializer.Serialize(new
            {
                status,
                code
            }), inventory: "{\"schema\":2,\"directions\":[\"en_zh\"]}");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var response = await peer.Client.TranslateTextAsync("English text", new(source, "en"))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(status, response.Status);
        Assert.Equal(code, response.Code);
        Assert.True(peer.Client.Ready);
        Assert.Equal(["en_zh"], peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("fr", "en")]
    [InlineData("zh", "ja")]
    [InlineData("auto", "ja")]
    [InlineData("ja", "ja")]
    [InlineData("auto", "ko")]
    [InlineData("pt", "zh")]
    public async Task AnUnsupportedLegacyDirectionDoesNotReachOrStopTheWorker(string source, string target)
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"en_zh\"]}");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var response = await peer.Client.TranslateTextAsync("hello", new(source, target))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("invalid-request", response.Code);
        Assert.True(peer.Client.Ready);
        Assert.Equal(["zh_en", "en_zh"], peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("auto", "en")]
    [InlineData("en", "en")]
    public async Task AnAllowedRequestReachesTheWorkerAndItsLossRevokesCapabilities(string source, string target)
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"en_zh\"]}");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(peer.Client.Ready);
        bool revoked = false;
        peer.Client.Changed += () => revoked = !peer.Client.Ready && peer.Client.AvailableDirections.Count == 0;
        var response = await peer.Client.TranslateTextAsync("hello", new(source, target))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("worker-stopped", response.Code);
        Assert.True(revoked);
    }

    private static TranslationJob AlternativeJob()
    {
        var source = new InputSnapshot(new("controlled", "field", Guid.NewGuid()), "你好", new(2, 2),
            new(1, 1, 100, 20), false, FieldAccess.Supported, ImeState.Clear, 1000, 1, 1);
        return new(Guid.NewGuid(), Guid.NewGuid(), 2, 1, source, null, source.Text,
            new("auto", "en"), TranslationKind.Alternative, null, "Hello");
    }

    [Fact]
    public async Task LoadedHyDoesNotReceiveAnAlternativeRequest()
    {
        using var peer = new ProtocolPeer(HyHello, inventory: HyInventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.False(Assert.IsType<VerifiedModelIdentity>(peer.Client.VerifiedIdentity).AlternativeTranslationAvailable);
        var result = await peer.Client.TranslateAsync(AlternativeJob());
        Assert.Equal("alternative-unavailable", result.Code);
        Assert.True(peer.Client.Ready);
        Assert.Equal(["zh_ja"], peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexAlternativeRequiresItsExplicitVerifiedHelloCapability(bool advertised)
    {
        string hello = IndexIdentity(HyHello);
        if (advertised)
            hello = hello.Replace("\"status\":\"ready\"", "\"status\":\"ready\",\"alternativeTranslationVersion\":1", StringComparison.Ordinal);
        using var peer = new ProtocolPeer(hello, """{"status":"translated","text":"Good day"}""", IndexIdentity(HyInventory));
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(ModelCatalog.Packages),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.Equal(advertised, peer.Client.VerifiedIdentity!.AlternativeTranslationAvailable);
        var result = await peer.Client.TranslateAsync(AlternativeJob(), peer.Client.VerifiedIdentity);
        Assert.Equal(advertised ? "translated" : "error", result.Status);
        Assert.Equal(advertised ? "Good day" : "", result.Text);
        Assert.Equal(advertised ? "" : "alternative-unavailable", result.Code);
    }

    private static string IndexIdentity(string value) => value
        .Replace("hy-mt2-1-8b-q4-k-m", "index-translate-2b-q4-k-m", StringComparison.Ordinal)
        .Replace("a0c709d9fac510f2c807aa3af52872340dc37a4a", "3a0eca6ce931d88d00614d427ab3ed8ff0743902", StringComparison.Ordinal)
        .Replace("zh_ja", "zh_en", StringComparison.Ordinal);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("\"1\"")]
    public async Task InvalidIndexAlternativeVersionCannotPublishAWorker(string version)
    {
        string hello = IndexIdentity(HyHello).Replace("\"status\":\"ready\"",
            "\"status\":\"ready\",\"alternativeTranslationVersion\":" + version, StringComparison.Ordinal);
        using var peer = new ProtocolPeer(hello, inventory: IndexIdentity(HyInventory));
        Assert.Equal(ModelPublicationOutcome.Unchanged, await peer.Client.ReloadAsync(peer.CreateLoadPlan(ModelCatalog.Packages),
            () => throw new InvalidOperationException("must-not-publish"), _ => false, TestContext.Current.CancellationToken));
        Assert.False(peer.Client.Ready);
        Assert.Null(peer.Client.VerifiedIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherBackendsCannotAdvertiseIndexAlternativeVersion(bool argos)
    {
        string hello = (argos ? """{"status":"ready","directions":["zh_en","en_zh"]}""" : HyHello)
            .Replace("\"status\":\"ready\"", "\"status\":\"ready\",\"alternativeTranslationVersion\":1", StringComparison.Ordinal);
        using var peer = new ProtocolPeer(hello, inventory: argos ? """{"schema":1}""" : HyInventory);
        if (argos)
            await peer.Client.StartAsync();
        else
            Assert.Equal(ModelPublicationOutcome.Unchanged, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
                () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.False(peer.Client.Ready);
        Assert.Null(peer.Client.VerifiedIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranslationResponsesCannotChangeTheNegotiatedAlternativeVersion(bool alternative)
    {
        string hello = IndexIdentity(HyHello).Replace("\"status\":\"ready\"",
            "\"status\":\"ready\",\"alternativeTranslationVersion\":1", StringComparison.Ordinal);
        using var peer = new ProtocolPeer(hello,
            """{"status":"translated","text":"untrusted","alternativeTranslationVersion":1}""", IndexIdentity(HyInventory));
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(ModelCatalog.Packages),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        var result = alternative ? await peer.Client.TranslateAsync(AlternativeJob())
            : await peer.Client.TranslateTextAsync("你好", new("zh", "en"));
        Assert.Equal("error", result.Status);
        Assert.Empty(result.Text);
        Assert.False(peer.Client.Ready);
        Assert.Null(peer.Client.VerifiedIdentity);
    }

    [Theory]
    [InlineData("translated", "Greetings", "")]
    [InlineData("no-alternative", "", "")]
    [InlineData("error", "", "unknown-operation")]
    public async Task AlternativeUsesADistinctOperationAndNeverFallsBack(string status, string text, string code)
    {
        string replies = JsonSerializer.Serialize(new
        {
            alternative = new
            {
                status,
                text,
                code
            },
            ordinary = new
            {
                status = "translated",
                text = "Ordinary result"
            }
        });
        using var peer = new ProtocolPeer("""{"status":"ready","directions":["zh_en","en_zh"]}""", replies,
            expectedRequest: """{"op":"translate-alternative","text":"你好","source":"auto","target":"en","preserved":null,"previousCandidate":"Hello"}""");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var response = await peer.Client.TranslateAsync(AlternativeJob());
        Assert.Equal((2, status, text, code), (response.Id, response.Status, response.Text, response.Code));
        Assert.True(peer.Client.Ready);
        var ordinary = await peer.Client.TranslateTextAsync("你好", new("auto", "en"));
        Assert.Equal((3, "Ordinary result"), (ordinary.Id, ordinary.Text));
    }

    [Theory]
    [InlineData("{\"status\":\"no-translation\"}")]
    [InlineData("{\"status\":\"no-alternative\",\"text\":\"fabricated\"}")]
    [InlineData("{\"status\":\"no-alternative\",\"text\":null}")]
    [InlineData("{\"status\":\"translated\",\"text\":\" \"}")]
    [InlineData("{\"status\":\"translated\",\"text\":\"valid\",\"code\":\"conflicting\"}")]
    [InlineData("{\"status\":\"error\",\"text\":\"fabricated\",\"code\":\"failure\"}")]
    [InlineData("{\"status\":\"error\"}")]
    [InlineData("{\"status\":\"no-alternative\",\"directions\":[\"zh_en\"]}")]
    public async Task AlternativeRejectsMalformedResponsesWithoutProducingText(string response)
    {
        using var peer = new ProtocolPeer("""{"status":"ready","directions":["zh_en","en_zh"]}""", response);
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var result = await peer.Client.TranslateAsync(AlternativeJob());
        Assert.Equal("worker-stopped", result.Code);
        Assert.Empty(result.Text);
        Assert.False(peer.Client.Ready);
    }

    [Fact]
    public async Task ModelReplacementRetiresAnAlreadyDispatchedAlternativeResponse()
    {
        using var peer = new ProtocolPeer("""{"status":"ready","directions":["zh_en","en_zh"]}""",
            """{"status":"translated","text":"Obsolete result"}""", holdAlternative: true);
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var pending = peer.Client.TranslateAsync(AlternativeJob());
        await peer.WaitForAlternative();
        var replacement = peer.Client.ReloadAsync(peer.CreateLoadPlan(), () => ModelPublicationOutcome.Confirmed,
            _ => false, TestContext.Current.CancellationToken);
        await peer.ReleaseAlternative();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("not-ready", result.Code);
        Assert.Empty(result.Text);
        Assert.Equal(ModelPublicationOutcome.Confirmed,
            await replacement.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(peer.Client.Ready);
    }

    [Theory]
    [InlineData(TranslationStyle.Natural, "natural", "translated", "Styled result", "")]
    [InlineData(TranslationStyle.Formal, "formal", "error", "", "unknown-operation")]
    public async Task StyledJobsUseTheFrozenStyleAndNeverFallbackToOrdinary(
        TranslationStyle style, string wireStyle, string status, string text, string code)
    {
        string replies = JsonSerializer.Serialize(new
        {
            styled = new
            {
                status,
                text,
                code
            },
            ordinary = new
            {
                status = "translated",
                text = "Ordinary result"
            }
        });
        using var peer = new ProtocolPeer(HyHello.Replace("\"status\":\"ready\"",
            "\"status\":\"ready\",\"styledTranslationVersion\":1", StringComparison.Ordinal), replies, HyInventory,
            expectedRequest: "{\"op\":\"translate-styled-v1\",\"text\":\"你好\",\"source\":\"zh\",\"target\":\"ja\",\"preserved\":null,\"style\":\"" + wireStyle + "\"}");
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        var job = AlternativeJob() with
        {
            Direction = new("zh", "ja"),
            Kind = TranslationKind.FullOrSelection,
            PreviousCandidate = null,
            Style = style
        };
        var result = await peer.Client.TranslateAsync(job);
        Assert.Equal((2, status, text, code), (result.Id, result.Status, result.Text, result.Code));
        Assert.True(peer.Client.Ready);
        var ordinary = await peer.Client.TranslateTextAsync("你好", new("zh", "ja"));
        Assert.Equal((3, "Ordinary result"), (ordinary.Id, ordinary.Text));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("en")]
    public async Task ArgosDoesNotReceiveStyledRequestsEvenForSameLanguage(string source)
    {
        using var peer = new ProtocolPeer("""{"status":"ready","directions":["zh_en","en_zh"]}""");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var job = AlternativeJob() with
        {
            Direction = new(source, "en"),
            Kind = TranslationKind.FullOrSelection,
            PreviousCandidate = null,
            Style = TranslationStyle.Formal
        };
        Assert.Equal("style-unavailable", (await peer.Client.TranslateAsync(job)).Code);
        Assert.True(peer.Client.Ready);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(99, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, false, true)]
    public async Task InvalidOrMixedStyledJobsNeverReachTheWorker(int style, bool alternative, bool previous)
    {
        using var peer = new ProtocolPeer(HyHello, inventory: HyInventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        var job = AlternativeJob() with
        {
            Direction = new("zh", "ja"),
            Kind = alternative ? TranslationKind.Alternative : TranslationKind.FullOrSelection,
            PreviousCandidate = previous ? "old" : null,
            Style = (TranslationStyle)style
        };
        Assert.Equal("invalid-request", (await peer.Client.TranslateAsync(job)).Code);
        Assert.True(peer.Client.Ready);
    }

    [Theory]
    [InlineData("""{"status":"no-alternative"}""")]
    [InlineData("""{"status":"no-translation","text":"fabricated"}""")]
    [InlineData("""{"status":"translated","text":" "}""")]
    [InlineData("""{"status":"translated","text":"valid","code":"conflicting"}""")]
    [InlineData("""{"status":"error","text":"fabricated","code":"failure"}""")]
    [InlineData("""{"status":"error"}""")]
    [InlineData("""{"status":"no-translation","directions":["zh_ja"]}""")]
    [InlineData("""{"status":"no-translation","styledTranslationVersion":1}""")]
    public async Task StyledRequestsRejectMalformedResponses(string response)
    {
        using var peer = new ProtocolPeer(HyHello.Replace("\"status\":\"ready\"",
            "\"status\":\"ready\",\"styledTranslationVersion\":1", StringComparison.Ordinal), response, HyInventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        var job = AlternativeJob() with
        {
            Direction = new("zh", "ja"),
            Kind = TranslationKind.FullOrSelection,
            PreviousCandidate = null,
            Style = TranslationStyle.Natural
        };
        var result = await peer.Client.TranslateAsync(job);
        Assert.Equal("worker-stopped", result.Code);
        Assert.Empty(result.Text);
        Assert.False(peer.Client.Ready);
    }

    [Fact]
    public async Task ModelReplacementRetiresAnAlreadyDispatchedStyledResponse()
    {
        using var peer = new ProtocolPeer(HyHello.Replace("\"status\":\"ready\"",
            "\"status\":\"ready\",\"styledTranslationVersion\":1"), """{"status":"translated","text":"Obsolete result"}""",
            HyInventory, holdAlternative: true);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        var job = AlternativeJob() with
        {
            Direction = new("zh", "ja"),
            Kind = TranslationKind.FullOrSelection,
            PreviousCandidate = null,
            Style = TranslationStyle.Formal
        };
        var capability = Assert.IsType<StyledTranslationCapability>(peer.Client.StyledTranslationCapability);
        var pending = peer.Client.TranslateAsync(job);
        await peer.WaitForAlternative();
        var replacement = peer.Client.ReloadAsync(peer.CreateLoadPlan(), () => ModelPublicationOutcome.Confirmed,
            _ => false, TestContext.Current.CancellationToken);
        Assert.Null(peer.Client.StyledTranslationCapability);
        // Hy replacement cancels and terminates the old peer while its response
        // is still held. Completion must not depend on releasing that response.
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("not-ready", result.Code);
        Assert.Empty(result.Text);
        Assert.Equal(ModelPublicationOutcome.Confirmed,
            await replacement.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(peer.Client.Ready);
        var replacementCapability = Assert.IsType<StyledTranslationCapability>(peer.Client.StyledTranslationCapability);
        Assert.NotSame(capability, replacementCapability);
    }


    private static string CombinedHello(string version = "1") => GlossaryHello().Replace("\"status\":\"ready\"",
        "\"status\":\"ready\",\"styledTranslationVersion\":1,\"styledGlossaryTranslationVersion\":" + version);

    [Fact]
    public async Task ExplicitCombinedCapabilitySendsBothChoicesInOneRequest()
    {
        string expected = JsonSerializer.Serialize(new
        {
            op = "translate-styled-glossary-v1",
            text = "你好",
            source = "zh",
            target = "ja",
            preserved = (string?)null,
            style = "formal",
            glossary = new
            {
                version = 7,
                rules = new[] { new { sourceTerm = "你好", targetTerm = "固定", sourceLanguage = "zh", targetLanguage = "ja" } }
            }
        });
        using var peer = new ProtocolPeer(CombinedHello(), """{"status":"translated","text":"固定"}""", HyInventory, expected);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.NotNull(peer.Client.StyledGlossaryTranslationCapability);
        var response = await peer.Client.TranslateAsync(GlossaryJob() with
        {
            Style = TranslationStyle.Formal
        });
        Assert.Equal((2, "translated", "固定"), (response.Id, response.Status, response.Text));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("\"1\"")]
    public async Task CombinedHelloRejectsUnsupportedOrNonNumericVersions(string version)
    {
        using var peer = new ProtocolPeer(CombinedHello(version), inventory: HyInventory);
        Assert.Equal(ModelPublicationOutcome.Unchanged, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.Null(peer.Client.StyledGlossaryTranslationCapability);
    }

    private static string GlossaryHello(string version = "1") => HyHello.Replace("\"status\":\"ready\"",
        "\"status\":\"ready\",\"glossaryTranslationVersion\":" + version);
    private static TranslationJob GlossaryJob(string source = "zh", string target = "ja", GlossarySnapshot? glossary = null) => AlternativeJob() with
    {
        Kind = TranslationKind.FullOrSelection,
        PreviousCandidate = null,
        Direction = new(source, target),
        Glossary = glossary ?? new(7, [new("你好", "固定", "zh", "ja")])
    };

    [Theory]
    [InlineData("zh")]
    [InlineData("auto")]
    public async Task GlossaryFramesFreezeVersionAndRulesForWorkerDirectionResolution(string source)
    {
        string expected = JsonSerializer.Serialize(new
        {
            op = "translate-glossary-v1",
            text = "你好",
            source,
            target = "ja",
            preserved = (string?)null,
            glossary = new
            {
                version = 7,
                rules = new[] { new { sourceTerm = "你好", targetTerm = "固定", sourceLanguage = "zh", targetLanguage = "ja" } }
            }
        });
        using var peer = new ProtocolPeer(GlossaryHello(), """{"status":"translated","text":"固定"}""", HyInventory, expected);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.NotNull(peer.Client.GlossaryTranslationCapability);
        var result = await peer.Client.TranslateAsync(GlossaryJob(source));
        Assert.Equal((2, "translated", "固定"), (result.Id, result.Status, result.Text));
    }

    [Theory]
    [InlineData("zh", "en", "ja", "zh")]
    [InlineData("auto", "en", "zh", "ja")]
    public async Task UnrelatedDirectionRulesPreserveOrdinaryArgosRequests(string source, string target, string ruleSource, string ruleTarget)
    {
        using var peer = new ProtocolPeer("""{"status":"ready","directions":["zh_en","en_zh"]}""",
            """{"status":"translated","text":"ordinary"}""");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var job = GlossaryJob(source, target, new(1, [new("hello", "fixed", ruleSource, ruleTarget)]));
        var result = await peer.Client.TranslateAsync(job);
        Assert.Equal((2, "ordinary"), (result.Id, result.Text));
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("auto")]
    public async Task PotentiallyApplicableRulesRejectNonHyWithoutFallback(string source)
    {
        using var peer = new ProtocolPeer("""{"status":"ready","directions":["zh_en","en_zh"]}""",
            """{"status":"translated","text":"must not be received"}""");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("glossary-unavailable", (await peer.Client.TranslateAsync(GlossaryJob(source, "en", new(1, [new("hello", "fixed")])))).Code);
        Assert.Equal(2, (await peer.Client.TranslateTextAsync("hello", new("zh", "en"))).Id);
    }

    [Fact]
    public async Task OldHyDoesNotReceiveGlossaryAndStyleCombinationNeverDropsEitherChoice()
    {
        using var old = new ProtocolPeer(HyHello, """{"status":"translated","text":"ordinary"}""", HyInventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await old.Client.ReloadAsync(old.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.Equal("glossary-unavailable", (await old.Client.TranslateAsync(GlossaryJob())).Code);
        Assert.Equal(2, (await old.Client.TranslateTextAsync("你好", new("zh", "ja"))).Id);
        using var peer = new ProtocolPeer(GlossaryHello(), """{"status":"translated","text":"ordinary"}""", HyInventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.Equal("glossary-combination-unavailable", (await peer.Client.TranslateAsync(GlossaryJob() with
        {
            Style = TranslationStyle.Formal
        })).Code);
        Assert.Equal(2, (await peer.Client.TranslateTextAsync("你好", new("zh", "ja"))).Id);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("true")]
    [InlineData("\"1\"")]
    public async Task GlossaryHelloRequiresTheExactNumericVersion(string version)
    {
        using var peer = new ProtocolPeer(GlossaryHello(version), inventory: HyInventory);
        Assert.Equal(ModelPublicationOutcome.Unchanged, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.False(peer.Client.Ready);
        Assert.Null(peer.Client.GlossaryTranslationCapability);
    }

    [Theory]
    [InlineData("{\"status\":\"translated\",\"text\":\" \"}")]
    [InlineData("{\"status\":\"translated\",\"text\":\"valid\",\"code\":\"bad\"}")]
    [InlineData("{\"status\":\"no-translation\",\"glossaryTranslationVersion\":1}")]
    [InlineData("{\"status\":\"no-alternative\"}")]
    public async Task GlossaryRejectsMalformedResponsesWithoutOrdinaryFallback(string response)
    {
        using var peer = new ProtocolPeer(GlossaryHello(), response, HyInventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.Equal("error", (await peer.Client.TranslateAsync(GlossaryJob())).Status);
        Assert.False(peer.Client.Ready);
    }

    // Exercise the actual handshake validator, but only with a controlled protocol
    // peer. This projection is test-port input, never inference or quality evidence.
    internal static VerifiedModelIdentity VerifyTestIdentity(bool hy) => Task.Run(async () =>
    {
        string hello = hy ? HyHello.Replace("[\"zh_ja\"]", "[\"zh_ja\",\"ja_zh\",\"fr_zh\"]")
            : """{"status":"ready","directions":["zh_en","en_zh"],"packages":[{"id":"argos-zh-en","version":"1.9","directions":["zh_en"]},{"id":"argos-en-zh","version":"1.9","directions":["en_zh"]}]}""";
        string inventory = hy ? HyInventory.Replace("[\"zh_ja\"]", "[\"zh_ja\",\"ja_zh\",\"fr_zh\"]") : """{"schema":3,"activePackages":["argos-zh-en","argos-en-zh"],"packages":[{"id":"argos-zh-en","version":"1.9","directions":["zh_en"],"files":[{"path":"metadata.json","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]},{"id":"argos-en-zh","version":"1.9","directions":["en_zh"],"files":[{"path":"metadata.json","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}]}""";
        using var peer = new ProtocolPeer(hello, inventory: inventory);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        return Assert.IsType<VerifiedModelIdentity>(peer.Client.VerifiedIdentity);
    }).GetAwaiter().GetResult();

    internal static VerifiedModelIdentity VerifyCandidateTestIdentity() => Task.Run(async () =>
    {
        var catalog = CandidateCatalog();
        string directions = JsonSerializer.Serialize(catalog.Packages.Single(package => package.Backend == "hy-mt-llama-cpu")
            .Directions.Select(direction => direction.Id));
        using var peer = new ProtocolPeer(HyHello.Replace("[\"zh_ja\"]", directions, StringComparison.Ordinal),
            inventory: HyInventory.Replace("[\"zh_ja\"]", directions, StringComparison.Ordinal));
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(catalog),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        return Assert.IsType<VerifiedModelIdentity>(peer.Client.VerifiedIdentity);
    }).GetAwaiter().GetResult();

    private sealed class ProtocolPeer : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "typelay-model-protocol-" + Guid.NewGuid().ToString("N"));
        private ModelRuntimePaths _paths = null!;
        private Process _process = null!;
        public async Task WaitForAlternative() => Assert.Equal("alternative",
            await _process.StandardError.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        public async Task WaitForExit() => await _process.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        public async Task ReleaseAlternative()
        {
            await _process.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
            await _process.StandardInput.BaseStream.FlushAsync(TestContext.Current.CancellationToken);
        }
        public ModelLoadPlan CreateLoadPlan(ModelPackageCatalog? catalog = null)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "installed.json")));
            return ModelLoadPlan.Create(_paths, document.RootElement, catalog);
        }
        public ModelClient Client
        {
            get;
        }

        public ProtocolPeer(string response, string? translationResponse = null, string inventory = "{\"schema\":1}",
            string? expectedRequest = null, bool holdAlternative = false, bool exitAfterHello = false)
        {
            Directory.CreateDirectory(_directory);
            string python = Path.Combine(_directory, "python-marker");
            File.WriteAllText(python, "controlled process port");
            File.WriteAllText(Path.Combine(_directory, "installed.json"), inventory);
            string script = Path.Combine(_directory, "peer.py");
            File.WriteAllText(script, """
                import json, struct, sys
                hello = json.loads(sys.argv[1])
                translation = json.loads(sys.argv[2])
                expected = json.loads(sys.argv[3])
                hold = sys.argv[4] == "true"
                exit_after_hello = sys.argv[5] == "true"
                while header := sys.stdin.buffer.read(4):
                    request = json.loads(sys.stdin.buffer.read(struct.unpack("<I", header)[0]))
                    if request["op"] in ("translate-alternative", "translate-styled-v1", "translate-glossary-v1", "translate-styled-glossary-v1"):
                        if expected is not None:
                            assert {k: v for k, v in request.items() if k != "id"} == expected
                        if hold:
                            print("alternative", file=sys.stderr, flush=True)
                            assert sys.stdin.buffer.read(1) == bytes([1])
                    response = hello if request["op"] == "hello" else translation
                    if response is not None and "alternative" in response:
                        response = response["alternative" if request["op"] == "translate-alternative" else "ordinary"]
                    if response is not None and "styled" in response:
                        response = response["styled" if request["op"] == "translate-styled-v1" else "ordinary"]
                    # Without a translation response, exercise the client's loss path.
                    if response is None: break
                    response["id"] = request["id"]
                    encoded = json.dumps(response).encode("utf-8")
                    sys.stdout.buffer.write(struct.pack("<I", len(encoded)) + encoded)
                    sys.stdout.buffer.flush()
                    if exit_after_hello and request["op"] == "hello": break
                """);
            _paths = new(python, script, _directory);
            Client = new(_paths, _ =>
            {
                var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("-I");
                start.ArgumentList.Add("-B");
                start.ArgumentList.Add(script);
                start.ArgumentList.Add(response);
                start.ArgumentList.Add(translationResponse ?? "null");
                start.ArgumentList.Add(expectedRequest ?? "null");
                start.ArgumentList.Add(holdAlternative ? "true" : "false");
                start.ArgumentList.Add(exitAfterHello ? "true" : "false");
                _process = Process.Start(start)!;
                return new OwnedProcess(_process);
            });
        }

        public void Dispose()
        {
            Client.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class OwnedProcess(Process process) : IChildProcess
    {
        public Process Process { get; } = process;
        public async Task TerminateAndWaitAsync(CancellationToken cancellationToken)
        {
            if (!Process.HasExited)
                Process.Kill(true);
            await Process.WaitForExitAsync(cancellationToken);
        }
        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                if (!Process.WaitForExit(5000))
                    throw new TimeoutException("protocol-peer-exit");
            }
            Process.Dispose();
        }
    }
}
