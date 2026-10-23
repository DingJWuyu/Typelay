using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public void LegacyPackageRequestWireShapeDoesNotAcquireTheCandidateField()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var legacy = new ModelPackageRequest(1, ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(legacy, options));
        Assert.Equal(new[] { "activePackages", "expectedCurrent", "format", "operation", "packageIds" },
            wire.RootElement.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Install", wire.RootElement.GetProperty("operation").GetString());
        var candidate = legacy with
        {
            Format = 2,
            AddDirections = ["zh_en", "en_zh"]
        };
        using var explicitWire = JsonDocument.Parse(JsonSerializer.Serialize(candidate, options));
        Assert.Equal(new[] { "zh_en", "en_zh" }, explicitWire.RootElement.GetProperty("addDirections")
            .EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void ShippedPreviewRetainsOldTwelveDirectionIntentAndOldCatalogRejectsFourteenWithoutTrimming()
    {
        using var source = typeof(ModelCatalog).Assembly.GetManifestResourceStream("TypeLay.ModelCatalog.json")!;
        var value = JsonNode.Parse(source)!;
        var hy = value["packages"]![2]!;
        hy["directions"] = new JsonArray(hy["directions"]!.AsArray()
            .Where(item => item!["id"]!.GetValue<string>() is not ("zh_en" or "en_zh"))
            .Select(item => item!.DeepClone()).ToArray());
        using var document = JsonDocument.Parse(value.ToJsonString());
        var oldCatalog = ModelPackageCatalog.Parse(document.RootElement);
        var catalog = ModelCatalog.Packages;
        var package = oldCatalog.Packages.Single(item => item.Backend == "hy-mt-llama-cpu");
        Assert.Equal(package.Directions.Select(item => item.Id),
            ModelCatalog.DefaultInstallationDirections(catalog.Packages[2]).Select(item => item.Id));
        Assert.Equal(14, catalog.Packages[2].Directions.Count);
        var inventory = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            schema = 3,
            packages = new[] { new { id = package.Id, version = package.Version,
                directions = package.Directions.Select(item => item.Id).ToArray(),
                files = new[] { new { path = "model.gguf", sha256 = new string('a', 64) } } } },
            activePackages = new[] { package.Id }
        }))!;
        using (var old = JsonDocument.Parse(inventory.ToJsonString()))
        {
            Assert.Equal(12, Assert.Single(catalog.ReadActiveSelection(old.RootElement)).Directions.Count);
            Assert.Equal(12, Assert.Single(oldCatalog.ReadActiveSelection(old.RootElement)).Directions.Count);
        }
        foreach (string direction in new[] { "zh_en", "en_zh" })
            inventory["packages"]![0]!["directions"]!.AsArray().Add(JsonValue.Create(direction));
        string frozen = inventory.ToJsonString();
        using var upgraded = JsonDocument.Parse(frozen);
        var paths = new ModelRuntimePaths("python", "worker.py", "models");
        Assert.Equal(14, ModelLoadPlan.Create(paths, upgraded.RootElement, catalog).Directions.Count);
        Assert.Throws<InvalidDataException>(() => ModelLoadPlan.Create(paths, upgraded.RootElement, oldCatalog));
        Assert.Equal(frozen, inventory.ToJsonString());
        Assert.Equal(12, package.Directions.Count);
    }

    [Fact]
    public void MalformedArchivePinCannotSilentlyBecomeAnUnpinnedPackage()
    {
        var value = PreviewCatalog();
        value["packages"]![0]!["archivePin"] = new JsonObject
        {
            ["sha256"] = new string('a', 64),
            ["bytes"] = false,
            ["unpackedBytes"] = 100
        };
        using var document = JsonDocument.Parse(value.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ModelPackageCatalog.Parse(document.RootElement));
    }

    [Fact]
    public void ArchivePinIsOptionalAndBoundToThePackageAndItsLegacyDirection()
    {
        var value = PreviewCatalog();
        using (var missing = JsonDocument.Parse(value.ToJsonString()))
        {
            var package = ModelPackageCatalog.Parse(missing.RootElement).Packages[0];
            Assert.Null(package.ArchivePin);
            Assert.Null(package.Directions[0].ArchivePin);
            Assert.Null(ModelCatalog.PackageDownloadBytes(package));
        }
        value["packages"]![0]!["archivePin"] = JsonNode.Parse("""
            {"sha256":"62e7af5a3a48b530e47b7b3e5c78c2de79073ecd815750d2bf3ab35b4a67da2d",
             "bytes":74481402,"unpackedBytes":86137496}
            """);
        using var document = JsonDocument.Parse(value.ToJsonString());
        var catalog = ModelPackageCatalog.Parse(document.RootElement);
        var pinned = catalog.Packages[0];
        var pin = Assert.IsType<ModelArchivePin>(pinned.ArchivePin);
        Assert.Equal("argos-zh-en", pinned.Id);
        Assert.Equal("1.9", pinned.Version);
        Assert.Equal("https://argos-net.com/v1/translate-zh_en-1_9.argosmodel", pinned.Url);
        Assert.Same(pin, catalog.LegacyDirections[0].ArchivePin);
        Assert.Equal(74481402, ModelCatalog.PackageDownloadBytes(pinned));
        Assert.Equal(86137496, pin.UnpackedBytes);
    }

    [Theory]
    [MemberData(nameof(InvalidArchivePins))]
    public void PresentArchivePinMustHaveExactlyThreeValidFields(string pin)
    {
        var value = PreviewCatalog();
        // Insert the raw pin so duplicate JSON properties remain observable to Parse.
        value["packages"]![0]!["archivePin"] = "PIN";
        using var document = JsonDocument.Parse(value.ToJsonString().Replace("\"PIN\"", pin, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ModelPackageCatalog.Parse(document.RootElement));
    }

    public static IEnumerable<object[]> InvalidArchivePins()
    {
        foreach (string pin in new[] { "null", "false", "[]", "{}", "1", "\"pin\"" })
            yield return [pin];
        string valid = "{\"sha256\":\"" + new string('a', 64) + "\",\"bytes\":1,\"unpackedBytes\":1}";
        foreach (string field in new[] { "sha256", "bytes", "unpackedBytes" })
        {
            var missing = JsonNode.Parse(valid)!;
            missing.AsObject().Remove(field);
            yield return [missing.ToJsonString()];
            foreach (string invalid in field == "sha256"
                ? new[] { "null", "false", "1", "\"short\"", "\"" + new string('g', 64) + "\"", "\"" + new string('A', 64) + "\"" }
                : new[] { "null", "false", "true", "\"1\"", "0", "-1", "1.5", "1.0", "1e0", "9223372036854775808",
                    field == "bytes" ? "1073741825" : "2147483649" })
            {
                var damaged = JsonNode.Parse(valid)!;
                damaged[field] = JsonNode.Parse(invalid);
                yield return [damaged.ToJsonString()];
            }
            yield return [valid[..^1] + ",\"" + field + "\":" + (field == "sha256" ? "\"" + new string('a', 64) + "\"" : "1") + "}"];
        }
        yield return [valid[..^1] + ",\"extra\":1}"];
    }

    [Fact]
    public void ArchivePinAcceptsItsLimitsButCannotDescribeHyOrMultipleDirections()
    {
        var value = PreviewCatalog();
        var pin = new JsonObject { ["sha256"] = new string('f', 64), ["bytes"] = 1073741824L, ["unpackedBytes"] = 2147483648L };
        value["packages"]![0]!["archivePin"] = pin;
        using (var document = JsonDocument.Parse(value.ToJsonString()))
            Assert.Equal(2147483648L, ModelPackageCatalog.Parse(document.RootElement).Packages[0].ArchivePin!.UnpackedBytes);
        value["packages"]![2]!["archivePin"] = pin.DeepClone();
        using (var hy = JsonDocument.Parse(value.ToJsonString()))
            Assert.Throws<InvalidDataException>(() => ModelPackageCatalog.Parse(hy.RootElement));
        value["packages"]![2]!.AsObject().Remove("archivePin");
        value["packages"]![0]!["directions"]!.AsArray().Add(value["packages"]![1]!["directions"]![0]!.DeepClone());
        using var multiple = JsonDocument.Parse(value.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ModelPackageCatalog.Parse(multiple.RootElement));
    }

    [Fact]
    public void ArchivePinCannotBeDuplicatedOrPlacedOnADirectionInsteadOfItsPackage()
    {
        var value = PreviewCatalog();
        value["packages"]![0]!["directions"]![0]!["archivePin"] = null;
        using (var misplaced = JsonDocument.Parse(value.ToJsonString()))
            Assert.Throws<InvalidDataException>(() => ModelPackageCatalog.Parse(misplaced.RootElement));
        value["packages"]![0]!["directions"]![0]!.AsObject().Remove("archivePin");
        value["packages"]![0]!["archivePin"] = "PIN";
        string pin = "{\"sha256\":\"" + new string('a', 64) + "\",\"bytes\":1,\"unpackedBytes\":1}";
        string raw = value.ToJsonString().Replace("\"PIN\"", pin + ",\"archivePin\":" + pin, StringComparison.Ordinal);
        using var duplicate = JsonDocument.Parse(raw);
        Assert.Throws<InvalidDataException>(() => ModelPackageCatalog.Parse(duplicate.RootElement));
    }

    [Theory]
    [InlineData("schema", "1")]
    [InlineData("id", "\"ignored-id\"")]
    [InlineData("version", "\"0.0\"")]
    [InlineData("url", "\"https://ignored.example/model\"")]
    [InlineData("source", "\"ignored-language\"")]
    [InlineData("target", "\"ignored-language\"")]
    public void CatalogIdentityCannotDependOnLastWinsForDuplicateObjectKeys(string property, string earlierValue)
    {
        string key = "\"" + property + "\":";
        string raw = PreviewCatalog().ToJsonString().Replace(key, key + earlierValue + "," + key, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        Assert.Throws<InvalidDataException>(() => ModelPackageCatalog.Parse(document.RootElement));
    }

    [Fact]
    public void PackageMetadataPreservesTheOriginalPreparationBundle()
    {
        Assert.Equal(["argos-zh-en", "argos-en-zh"], ModelCatalog.Packages.Packages.Take(2).Select(package => package.Id));
        Assert.All(ModelCatalog.Packages.Packages.Take(2), package =>
        {
            Assert.Equal("argos", package.Backend);
            Assert.Equal("1.9", package.Version);
            Assert.False(package.Preview);
            Assert.Single(package.Directions);
            Assert.Equal(package.Url, package.Directions[0].Url);
        });
        Assert.Equal(ModelCatalog.Directions, ModelCatalog.Packages.LegacyDirections);
        Assert.Equal(ModelCatalog.Directions, ModelCatalog.Packages.DefaultDirections);
    }

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
        Assert.Equal(new ModelDirection("en_zh", "en", "zh", "English", "中文", "1.9",
            "https://argos-net.com/v1/translate-en_zh-1_9.argosmodel", "Please send me the final version.",
            ModelCatalog.Packages.Packages.Single(package => package.Id == "argos-en-zh").ArchivePin),
            ModelCatalog.Get("en_zh"));
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
    [InlineData("auto", "fr", true)]
    [InlineData("ZH", "en", false)]
    public void ConfigurationsPreserveAutoAndSameLanguageWithoutCreatingNewDirections(string source, string target, bool valid)
    {
        Assert.Equal(valid, ModelCatalog.IsSupportedSetting(source, target));
    }

    [Theory]
    [InlineData("zh", "ja", true)]
    [InlineData("ja", "zh", true)]
    [InlineData("auto", "ja", true)]
    [InlineData("ja", "ja", true)]
    [InlineData("en", "ja", false)]
    [InlineData("ja", "en", false)]
    public void PackageSettingsKeepKnownDirectMappingsWithoutExpandingLegacyRequests(string source, string target, bool valid)
    {
        Assert.Equal(valid, ModelCatalog.IsSupportedSetting(source, target));
        Assert.False(ModelCatalog.IsSupportedLegacySetting(source, target));
    }

    [Fact]
    public void PublishedHyCatalogOffersFourteenButDefaultsToTwelveWithoutPromotingOldInventory()
    {
        var package = ModelCatalog.Packages.Packages.Single(item => item.Backend == "hy-mt-llama-cpu");
        Assert.Equal(["zh_ja", "ja_zh", "zh_ko", "ko_zh", "zh_fr", "fr_zh", "zh_es", "es_zh", "zh_de", "de_zh", "zh_pt", "pt_zh", "zh_en", "en_zh"],
            package.Directions.Select(item => item.Id));
        Assert.Equal(["zh_ja", "ja_zh", "zh_ko", "ko_zh", "zh_fr", "fr_zh", "zh_es", "es_zh", "zh_de", "de_zh", "zh_pt", "pt_zh"],
            ModelCatalog.DefaultInstallationDirections(package).Select(item => item.Id));
        foreach (string language in new[] { "ja", "ko", "fr", "es", "de", "pt" })
        {
            Assert.True(ModelCatalog.IsSupportedSetting("zh", language));
            Assert.True(ModelCatalog.IsSupportedSetting(language, "zh"));
            Assert.True(ModelCatalog.IsSupportedSetting("auto", language));
            Assert.True(ModelCatalog.IsSupportedSetting(language, language));
            Assert.False(ModelCatalog.IsSupportedLegacySetting("auto", language));
        }
        Assert.False(ModelCatalog.IsSupportedSetting("fr", "de"));
        using var old = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schema = 3,
            activePackages = new[] { package.Id },
            packages = new[] { new { id = package.Id, version = package.Version,
                directions = new[] { "zh_ja", "ja_zh" },
                files = new[] { new { path = "fixture", sha256 = new string('0', 64) } } } }
        }));
        Assert.Equal(["zh_ja", "ja_zh"], ModelCatalog.ReadActiveSelection(old.RootElement).Single().Directions.Select(item => item.Id));
    }

    [Fact]
    public void FixedDownloadSizeComesFromThePublishedModelLock()
    {
        var selected = ModelCatalog.Packages.Packages.Single(package => package.Id == "argos-en-zh");
        Assert.Equal(selected.ArchivePin?.Bytes, ModelCatalog.PackageDownloadBytes(selected));
        Assert.Equal(1133092087L, ModelCatalog.PackageDownloadBytes(ModelCatalog.Packages.Packages.Single(package => package.Backend == "hy-mt-llama-cpu")));
    }

    [Fact]
    public void APreviewPackageKeepsTwelveDirectMappingsAndItsOwnSelectionIdentity()
    {
        using var document = JsonDocument.Parse(PreviewCatalog().ToJsonString());
        var catalog = ModelPackageCatalog.Parse(document.RootElement);
        var preview = catalog.Packages[2];
        Assert.True(preview.Preview);
        Assert.Equal("hy-mt-llama-cpu", preview.Backend);
        Assert.Equal(12, preview.Directions.Count);
        Assert.Equal(["zh_en", "en_zh"], catalog.LegacyDirections.Select(direction => direction.Id));
        Assert.Equal(catalog.LegacyDirections, catalog.DefaultDirections);
        using var entire = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schema = 3,
            packages = new[] { new { id = preview.Id, version = preview.Version,
                directions = preview.Directions.Reverse().Select(direction => direction.Id).ToArray(),
                files = new[] { new { path = "fixture.bin", sha256 = new string('0', 64) } } } }
        }));
        var fullSelection = Assert.Single(catalog.ReadInstallationSelection(entire.RootElement));
        Assert.Same(preview, fullSelection.Package);
        Assert.Equal(preview.Directions, fullSelection.Directions);
        using var subset = JsonDocument.Parse(WithFiles("""
            {"schema":3,"packages":[
              {"id":"hy-mt-preview","version":"1.5","directions":["pt_zh","zh_ja"]},
              {"id":"argos-en-zh","version":"1.9","directions":["en_zh"]}]}
            """));
        var selection = catalog.ReadInstallationSelection(subset.RootElement);
        Assert.Equal(["argos-en-zh", "hy-mt-preview"], selection.Select(item => item.Package.Id));
        Assert.Equal(["zh_ja", "pt_zh"], selection[1].Directions.Select(direction => direction.Id));
        using var oldDefault = JsonDocument.Parse("{\"schema\":1}");
        Assert.Equal(["argos-zh-en", "argos-en-zh"],
            catalog.ReadInstallationSelection(oldDefault.RootElement).Select(item => item.Package.Id));
        foreach (int schema in new[] { 1, 2 })
        {
            using var invalid = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                schema,
                directions = new[] { "zh_ja" }
            }));
            Assert.Throws<InvalidDataException>(() => catalog.ReadInstallationSelection(invalid.RootElement));
        }
        Assert.Equal(["auto", "zh", "en"], ModelCatalog.SourceChoices.Select(choice => choice.Code));
        Assert.True(ModelCatalog.IsSupportedSetting("zh", "ja"));
    }

    [Theory]
    [InlineData("{\"schema\":1}", "argos-zh-en,argos-en-zh")]
    [InlineData("{\"schema\":1,\"directions\":[\"en_zh\",\"zh_en\"]}", "argos-zh-en,argos-en-zh")]
    [InlineData("{\"schema\":2,\"directions\":[\"en_zh\"]}", "argos-en-zh")]
    [InlineData("{\"schema\":3,\"packages\":[{\"id\":\"argos-zh-en\",\"version\":\"1.9\",\"directions\":[\"zh_en\"]}]}", "argos-zh-en")]
    public void TheNewReaderPreservesLegacyPackageIdentity(string json, string expected)
    {
        using var document = JsonDocument.Parse(WithFiles(json));
        var selection = ModelCatalog.ReadInstallationSelection(document.RootElement);
        Assert.Equal(expected.Split(','), selection.Select(item => item.Package.Id));
        Assert.All(selection, item => Assert.Equal(item.Package.Directions, item.Directions));
    }

    [Fact]
    public void OverlappingInventoryIsAllowedButActiveProvidersCannotConflict()
    {
        var value = PreviewCatalog();
        value["packages"]![2]!["directions"]!.AsArray().Add(value["packages"]![0]!["directions"]![0]!.DeepClone());
        using var document = JsonDocument.Parse(value.ToJsonString());
        var catalog = ModelPackageCatalog.Parse(document.RootElement);
        Assert.Equal("1.9", catalog.LegacyDirections[0].Version);
        using var overlapping = JsonDocument.Parse(WithFiles("""
            {"schema":3,"packages":[
              {"id":"argos-zh-en","version":"1.9","directions":["zh_en"]},
              {"id":"hy-mt-preview","version":"1.5","directions":["zh_en"]}],"activePackages":["argos-zh-en","hy-mt-preview"]}
            """));
        Assert.Equal(2, catalog.ReadInstallationSelection(overlapping.RootElement).Count);
        Assert.Throws<InvalidDataException>(() => catalog.ReadActiveSelection(overlapping.RootElement));
        using var onlyPreview = JsonDocument.Parse(WithFiles("""
            {"schema":3,"packages":[{"id":"hy-mt-preview","version":"1.5","directions":["zh_en"]}],"activePackages":["hy-mt-preview"]}
            """));
        Assert.Equal("hy-mt-preview", Assert.Single(catalog.ReadInstallationSelection(onlyPreview.RootElement)).Package.Id);
        Assert.Throws<InvalidDataException>(() => ModelCatalog.ReadInstallationDirections(onlyPreview.RootElement));
    }

    [Theory]
    [MemberData(nameof(InvalidCatalogs))]
    public void DamagedPackageMetadataIsRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => ModelPackageCatalog.Parse(document.RootElement));
    }

    public static IEnumerable<object[]> InvalidCatalogs()
    {
        foreach (string json in new[] { "null", "[]", "{}", "{\"schema\":1,\"packages\":[]}",
            "{\"schema\":true,\"packages\":[]}", "{\"schema\":2}", "{\"schema\":2,\"packages\":[]}",
            "{\"schema\":2,\"packages\":{}}", "{\"schema\":2,\"packages\":[null]}" })
            yield return [json];
        foreach (string field in new[] { "id", "version", "backend", "preview", "url", "directions" })
        {
            var missing = PreviewCatalog();
            missing["packages"]![0]!.AsObject().Remove(field);
            yield return [missing.ToJsonString()];
            foreach (JsonNode? invalid in new JsonNode?[] { null, JsonValue.Create(5), new JsonObject() })
            {
                var value = PreviewCatalog();
                value["packages"]![0]![field] = invalid?.DeepClone();
                yield return [value.ToJsonString()];
            }
        }
        foreach (var (field, invalid) in new[] { ("id", "bad/id"), ("backend", "bad.module"),
            ("preview", "false"), ("version", "1"), ("url", "http://argos-net.com/model"),
            ("url", "https://other.invalid/model"), ("url", "https://user@argos-net.com/model") })
        {
            var value = PreviewCatalog();
            value["packages"]![0]![field] = invalid;
            yield return [value.ToJsonString()];
        }
        foreach (string field in new[] { "id", "source", "target", "sourceName", "targetName", "selfCheckText" })
        {
            var value = PreviewCatalog();
            value["packages"]![0]!["directions"]![0]!.AsObject().Remove(field);
            yield return [value.ToJsonString()];
        }
        foreach (var (field, invalid) in new[] { ("source", "ZH"), ("target", "zh"), ("id", "en_zh"),
            ("sourceName", ""), ("selfCheckText", " "), ("version", "9.9"), ("url", "https://example.invalid") })
        {
            var value = PreviewCatalog();
            value["packages"]![0]!["directions"]![0]![field] = invalid;
            yield return [value.ToJsonString()];
        }
        foreach (var (field, invalid) in new[] { ("id", "1package"), ("id", new string('a', 65)),
            ("backend", "unknown"), ("version", " "), ("version", "bad/version"), ("version", new string('a', 129)),
            ("url", "https://example.invalid:444/model"), ("url", "https://example.invalid/model?key=value"),
            ("url", "https://example.invalid/model#fragment") })
        {
            var value = PreviewCatalog();
            value["packages"]![2]![field] = invalid;
            yield return [value.ToJsonString()];
        }
        var rootDirections = PreviewCatalog();
        rootDirections["directions"] = new JsonArray();
        yield return [rootDirections.ToJsonString()];
        var duplicatePackage = PreviewCatalog();
        duplicatePackage["packages"]!.AsArray().Add(duplicatePackage["packages"]![0]!.DeepClone());
        yield return [duplicatePackage.ToJsonString()];
        var duplicateDirection = PreviewCatalog();
        duplicateDirection["packages"]![0]!["directions"]!.AsArray().Add(duplicateDirection["packages"]![0]!["directions"]![0]!.DeepClone());
        yield return [duplicateDirection.ToJsonString()];
        var emptyDirections = PreviewCatalog();
        emptyDirections["packages"]![0]!["directions"] = new JsonArray();
        yield return [emptyDirections.ToJsonString()];
    }

    [Theory]
    [MemberData(nameof(InvalidSelections))]
    public void DamagedSelectionNeverLosesItsPackageIdentity(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => ModelCatalog.ReadInstallationSelection(document.RootElement));
    }

    public static IEnumerable<object[]> InvalidSelections()
    {
        foreach (string json in new[] { "null", "[]", "{}", "{\"schema\":true}", "{\"schema\":0}",
            "{\"schema\":1,\"directions\":[\"zh_en\"]}", "{\"schema\":2}", "{\"schema\":3}",
            "{\"schema\":3,\"packages\":[]}", "{\"schema\":3,\"packages\":{}}", "{\"schema\":3,\"packages\":[null]}" })
            yield return [json];
        var valid = JsonNode.Parse(WithFiles("""
            {"schema":3,"packages":[{"id":"argos-zh-en","version":"1.9","directions":["zh_en"]}]}
            """))!;
        foreach (string field in new[] { "id", "version", "directions", "files" })
        {
            var missing = valid.DeepClone();
            missing["packages"]![0]!.AsObject().Remove(field);
            yield return [missing.ToJsonString()];
            foreach (JsonNode? invalid in new JsonNode?[] { null, JsonValue.Create(1), new JsonObject() })
            {
                var value = valid.DeepClone();
                value["packages"]![0]![field] = invalid?.DeepClone();
                yield return [value.ToJsonString()];
            }
        }
        foreach (var (field, invalid) in new[] { ("id", "unknown"), ("version", "1.8"),
            ("version", "1.9 "), ("directions", "zh_en") })
        {
            var value = valid.DeepClone();
            value["packages"]![0]![field] = invalid;
            yield return [value.ToJsonString()];
        }
        foreach (string directions in new[] { "[]", "[null]", "[1]", "[\"zh_en\",\"zh_en\"]", "[\"en_zh\"]", "[\"fr_en\"]" })
        {
            var value = valid.DeepClone();
            value["packages"]![0]!["directions"] = JsonNode.Parse(directions);
            yield return [value.ToJsonString()];
        }
        var mixed = valid.DeepClone();
        mixed["directions"] = new JsonArray("zh_en");
        yield return [mixed.ToJsonString()];
        var duplicate = valid.DeepClone();
        duplicate["packages"]!.AsArray().Add(duplicate["packages"]![0]!.DeepClone());
        yield return [duplicate.ToJsonString()];
        foreach (int schema in new[] { 1, 2 })
        {
            var mixedLegacy = valid.DeepClone();
            mixedLegacy["schema"] = schema;
            mixedLegacy["directions"] = new JsonArray("zh_en", "en_zh");
            yield return [mixedLegacy.ToJsonString()];
            foreach (string directions in new[] { "null", "[]", "[1]", "\"zh_en\"", "[\"zh_en\",\"zh_en\"]", "[\"fr_en\"]" })
                yield return [$"{{\"schema\":{schema},\"directions\":{directions}}}"];
        }
    }

    private static string WithFiles(string json)
    {
        var value = JsonNode.Parse(json)!;
        if (value["packages"] is JsonArray packages)
            foreach (var package in packages)
                package!["files"] = new JsonArray(new JsonObject { ["path"] = "fixture.bin", ["sha256"] = new string('0', 64) });
        return value.ToJsonString();
    }

    [Fact]
    public void ActiveSelectionRequiresAnExplicitKnownNonemptyFamily()
    {
        string json = WithFiles("""
            {"schema":3,"packages":[{"id":"argos-zh-en","version":"1.9","directions":["zh_en"]}]}
            """);
        foreach (string? active in new[] { null, "null", "[]", "[1]", "[\"unknown\"]", "[\"argos-zh-en\",\"argos-zh-en\"]" })
        {
            var value = JsonNode.Parse(json)!;
            if (active is not null)
                value["activePackages"] = JsonNode.Parse(active);
            using var document = JsonDocument.Parse(value.ToJsonString());
            Assert.Throws<InvalidDataException>(() => ModelCatalog.ReadActiveSelection(document.RootElement));
        }
        var valid = JsonNode.Parse(json)!;
        valid["activePackages"] = new JsonArray("argos-zh-en");
        using var selected = JsonDocument.Parse(valid.ToJsonString());
        Assert.Equal("argos-zh-en", Assert.Single(ModelCatalog.ReadActiveSelection(selected.RootElement)).Package.Id);
    }

    private static JsonObject PreviewCatalog()
    {
        var packages = new JsonArray(ModelCatalog.Packages.Packages.Take(2).Select(package => JsonSerializer.SerializeToNode(new
        {
            id = package.Id,
            version = package.Version,
            backend = package.Backend,
            preview = package.Preview,
            url = package.Url,
            directions = package.Directions.Select(direction => new
            {
                id = direction.Id,
                source = direction.Source,
                target = direction.Target,
                sourceName = direction.SourceName,
                targetName = direction.TargetName,
                selfCheckText = direction.SelfCheckText
            })
        })).ToArray());
        string[] pairs = ["zh_ja", "ja_zh", "zh_ko", "ko_zh", "zh_fr", "fr_zh", "zh_es", "es_zh", "zh_de", "de_zh", "zh_pt", "pt_zh"];
        packages.Add(JsonSerializer.SerializeToNode(new
        {
            id = "hy-mt-preview",
            version = "1.5",
            backend = "hy-mt-llama-cpu",
            preview = true,
            url = "https://example.invalid/hy-test-only.gguf",
            directions = pairs.Select(pair => new
            {
                id = pair,
                source = pair[..2],
                target = pair[3..],
                sourceName = pair[..2],
                targetName = pair[3..],
                selfCheckText = "Test only."
            })
        }));
        return new JsonObject { ["schema"] = 2, ["packages"] = packages };
    }
}
