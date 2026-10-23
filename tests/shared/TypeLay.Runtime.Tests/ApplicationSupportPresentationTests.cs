using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class ApplicationSupportPresentationTests
{
    [Theory]
    [InlineData(ApplicationSupportState.AwaitingApproval)]
    [InlineData(ApplicationSupportState.NeedsInstall)]
    [InlineData(ApplicationSupportState.Error)]
    public void EndConnectionIsExplicitAndSeparateFromRemovingComponents(ApplicationSupportState state)
    {
        var row = ApplicationSupportPresentation.Create([
            new("desktop-text", state,
                ApplicationSupportReason.UserApprovalRequired,
                SupportedActions: [ApplicationSupportAction.Refresh, ApplicationSupportAction.Disconnect, ApplicationSupportAction.Remove])
        ]).Rows[0];
        var action = Assert.Single(row.Actions, item => item.Id == ApplicationSupportAction.Disconnect);
        Assert.Equal("结束本次连接…", action.Label);
        Assert.True(action.RequiresConfirmation);
        Assert.Contains("保留接入组件", action.ConfirmationText);
        Assert.Contains("不会自动恢复", action.ConfirmationText);
        if (state != ApplicationSupportState.NeedsInstall)
            Assert.Contains(row.Actions, item => item.Id == ApplicationSupportAction.Remove);
        Assert.NotEqual(action, row.PrimaryAction);
    }

    [Fact]
    public void BuiltInDesktopDoesNotInventAnOwnedConnectionToEnd()
    {
        var row = ApplicationSupportPresentation.Create([
            new("desktop-text", ApplicationSupportState.BuiltInAvailable,
                ApplicationSupportReason.NoFocusedField,
                SupportedActions: [ApplicationSupportAction.Refresh, ApplicationSupportAction.OpenInstructions])
        ]).Rows[0];
        Assert.DoesNotContain(row.Actions, item => item.Id == ApplicationSupportAction.Disconnect);
    }

    [Theory]
    [InlineData("auto", "Auto")]
    [InlineData("zh", "中文")]
    [InlineData("en", "EN")]
    [InlineData("ja", "日本語")]
    [InlineData("ko", "한국어")]
    [InlineData("fr", "Français")]
    [InlineData("es", "Español")]
    [InlineData("de", "Deutsch")]
    [InlineData("pt", "Português")]
    [InlineData("unknown", "unknown（当前不可用）")]
    public void LanguageLabelsNeverPresentAnUnknownSavedCodeAsEnglishOrAuto(string code, string expected) =>
        Assert.Equal(expected, ApplicationSupportPresentation.LanguageName(code));

    [Theory]
    [InlineData(ApplicationSupportState.Connected, ApplicationSupportReason.None,
        "已确认预览、Tab 接受和直接替换。")]
    [InlineData(ApplicationSupportState.Connected, ApplicationSupportReason.LimitedFields,
        "已确认预览、Tab 接受和直接替换。")]
    [InlineData(ApplicationSupportState.BuiltInAvailable, ApplicationSupportReason.NoDirectWrite,
        "已确认预览和 Tab 接受，尚未确认直接替换。")]
    [InlineData(ApplicationSupportState.Connected, ApplicationSupportReason.PreviewOnly,
        "目前仅确认预览译文，尚未确认替换能力。")]
    [InlineData(ApplicationSupportState.Connected, ApplicationSupportReason.Unknown,
        "还没有可靠的检查结果。请选择目标应用并检查连接。")]
    [InlineData(ApplicationSupportState.Error, ApplicationSupportReason.ActionFailed,
        "操作未完成。请查看详情，处理原因后再试；未将这次操作标为成功。")]
    [InlineData(ApplicationSupportState.AwaitingApproval, ApplicationSupportReason.AwaitingApproval,
        "请在应用或浏览器中完成扩展及权限批准，然后重新检查。")]
    [InlineData(ApplicationSupportState.RestartRequired, ApplicationSupportReason.RestartRequired,
        "接入配置已更新。请保存工作并重新打开应用，然后检查连接。")]
    public void HostsReceiveAnActionableSummaryWithoutHidingUnknownOrBlockedStates(
        ApplicationSupportState state, ApplicationSupportReason reason, string expected)
    {
        var row = ApplicationSupportPresentation.Create([
            new("desktop-text", state, reason, new(true, true, true))
        ]).Rows[0];

        var wire = JsonSerializer.SerializeToElement(row, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expected, wire.GetProperty("summaryText").GetString());
    }

    [Fact]
    public void PackageConfirmationNamesTheActualPlatformRuntimeSources()
    {
        ModelPackageManagementFact facts = new(null, true, [], [], [], false, "native-unavailable", "auto", "en", false,
            RuntimeDownloadSources: "NuGet、PyPI");
        var windows = ApplicationSupportPresentation.Preparation(new(false, false, Packages: facts));
        var install = Assert.Single(windows.PackageRows.Single(row => row.Id == "argos-zh-en").Actions);
        Assert.Contains("NuGet、PyPI", install.ConfirmationText);
        Assert.DoesNotContain("Astral", install.ConfirmationText);
        Assert.Empty(windows.PackageRows.Single(row => row.Id == "hy-mt2-1-8b-q4-k-m").Actions);
        var linux = ApplicationSupportPresentation.Preparation(new(false, false,
            Packages: facts with
            {
                RuntimeDownloadSources = "Astral、PyPI"
            }));
        Assert.Contains("Astral、PyPI", Assert.Single(linux.PackageRows.Single(row => row.Id == "argos-zh-en").Actions).ConfirmationText);
    }

    [Fact]
    public void PackageManagementKeepsInstalledCurrentAndActualHelloDirectionsDistinct()
    {
        var installed = PackageSelections("argos-zh-en", "hy-mt2-1-8b-q4-k-m");
        var current = installed.Where(item => item.Package.Backend == "hy-mt-llama-cpu").ToArray();
        var ready = PackageSelections("hy-mt2-1-8b-q4-k-m", onlyDirection: "zh_ja");
        var facts = new ModelPackageManagementFact(new string('a', 32), true, installed, current, ready,
            true, null, "ja", "zh", false);
        var view = ApplicationSupportPresentation.Preparation(new(true, true, Packages: facts));
        var hy = Assert.Single(view.PackageRows, row => row.Preview);
        Assert.True(hy.Installed);
        Assert.True(hy.Current);
        Assert.True(hy.Ready);
        Assert.Equal(["zh_ja"], hy.ReadyDirectionIds);
        Assert.Equal("Hy-MT2 1.8B · 中文多语", hy.Title);
        Assert.Equal("目录可用：中文 ↔ 日本語、한국어、Français、Español、Deutsch、Português、English", hy.CoverageText);
        Assert.Equal(14, hy.DirectionIds.Length);
        Assert.Equal(12, hy.InstalledDirectionIds.Length);
        Assert.DoesNotContain("仅开放中日双向", hy.Details);
        Assert.Contains("当前模型不包含这个方向", view.Guidance);
        Assert.DoesNotContain("日本語 → 中文", view.StatusText);
        Assert.DoesNotContain(hy.Actions, action => action.Request.Operation == ModelPackageOperation.Remove);
        var select = Assert.Single(view.PackageRows[0].Actions, action => action.Request.Operation == ModelPackageOperation.Select);
        Assert.Equal([], select.Request.PackageIds);
        Assert.Equal(["argos-zh-en"], select.Request.ActivePackages);
        Assert.Equal(new string('a', 32), select.Request.ExpectedCurrent);
    }

    [Theory]
    [InlineData(TranslationPreparationNextStep.TryIt)]
    [InlineData(TranslationPreparationNextStep.ChooseConnection)]
    public void LegacyOrdinaryReadinessDoesNotInventVerifiedPackageIdentity(TranslationPreparationNextStep nextStep)
    {
        var installed = PackageSelections("argos-zh-en", "argos-en-zh");
        var facts = new ModelPackageManagementFact(new string('a', 32), true, installed, installed, [],
            false, "native-unavailable", "zh", "en", false);
        var view = ApplicationSupportPresentation.Preparation(new(true, true,
            ReadyDirections: ["zh_en"], Packages: facts), nextStep);

        Assert.Equal("本地翻译已就绪 · 中文 → English", view.StatusText);
        Assert.Contains("包身份", view.Guidance);
        Assert.DoesNotContain("不包含这个方向", view.Guidance);
        Assert.True(view.PrimaryAction.Enabled);
        Assert.All(view.PackageRows, row =>
        {
            Assert.False(row.Ready);
            Assert.Empty(row.ReadyDirectionIds);
            if (row.Current)
                Assert.Equal("当前 · 旧版方向已加载，包身份未验证", row.AvailabilityText);
        });
    }

    [Theory]
    [InlineData("en", "zh", true)]
    [InlineData("auto", "en", false)]
    [InlineData("zh", "zh", false)]
    public void LegacyReadinessOnlyCoversItsObservedDirections(string source, string target, bool missing)
    {
        var installed = PackageSelections("argos-zh-en", "argos-en-zh");
        var facts = new ModelPackageManagementFact(new string('a', 32), true, installed, installed, [],
            false, "native-unavailable", source, target, false);
        var view = ApplicationSupportPresentation.Preparation(new(true, true,
            ReadyDirections: ["zh_en"], Packages: facts));
        Assert.Equal(missing, view.Guidance.Contains("不包含这个方向", StringComparison.Ordinal));
        Assert.DoesNotContain("English → 中文", view.StatusText);
        Assert.All(view.PackageRows, row => Assert.False(row.Ready));
    }

    [Theory]
    [InlineData(false, true, TranslationPreparationPhase.Idle)]
    [InlineData(true, false, TranslationPreparationPhase.Idle)]
    [InlineData(true, true, TranslationPreparationPhase.StartingModel)]
    [InlineData(true, true, TranslationPreparationPhase.Failed)]
    public void LegacyDirectionsDoNotOverrideMissingReadinessInventoryOrRecovery(
        bool ready, bool inventoryKnown, TranslationPreparationPhase phase)
    {
        var installed = PackageSelections("argos-zh-en");
        var packages = new ModelPackageManagementFact(new string('a', 32), inventoryKnown, installed, installed, [],
            false, "native-unavailable", "zh", "en", false);
        var facts = new TranslationPreparationFact(ready, true, phase, Packages: packages);
        var before = ApplicationSupportPresentation.Preparation(facts);
        var after = ApplicationSupportPresentation.Preparation(facts with
        {
            ReadyDirections = ["zh_en"]
        });
        Assert.Equal(before.StatusText, after.StatusText);
        Assert.Equal(before.Guidance, after.Guidance);
        Assert.Equal(before.PrimaryAction, after.PrimaryAction);
        Assert.All(after.PackageRows, row => Assert.False(row.Ready));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DamagedPackageInventoryNeverOffersLegacyRepairOrAnotherMutation(bool nativeAvailable)
    {
        var facts = new ModelPackageManagementFact(new string('b', 32), false, [], [], [],
            nativeAvailable, nativeAvailable ? null : "native-unavailable", "auto", "en", false);
        var view = ApplicationSupportPresentation.Preparation(new(false, false, Packages: facts));
        Assert.False(view.PrimaryAction.Enabled);
        Assert.Empty(view.SecondaryActions);
        Assert.False(view.CanSelectDirections);
        Assert.All(view.PackageRows, row =>
        {
            Assert.Empty(row.Actions);
            Assert.Contains("安装状态无法确认", row.AvailabilityText);
        });
        if (!nativeAvailable)
            Assert.Contains("不含可用", Assert.Single(view.PackageRows, row => row.Preview).AvailabilityText);
        Assert.Contains("无法确认", view.CurrentModelText);
    }

    [Theory]
    [InlineData(false, "native-unavailable")]
    [InlineData(true, "unexpected-reason")]
    public void UnverifiedNativeRuntimeCannotOfferHyDownloadsOrUsage(bool available, string reason)
    {
        var legacy = PackageSelections("argos-zh-en");
        var facts = new ModelPackageManagementFact(new string('c', 32), true, legacy, legacy, legacy,
            available, reason, "zh", "en", false);
        var view = ApplicationSupportPresentation.Preparation(new(true, true, Packages: facts));
        var hy = Assert.Single(view.PackageRows, row => row.Preview);
        Assert.Empty(hy.Actions);
        Assert.Contains("不含可用", hy.AvailabilityText);
        Assert.Equal(1_133_092_087L, hy.DownloadBytes);
        Assert.Equal("1.13 GB 下载", hy.DownloadText);
        Assert.EndsWith("/a0c709d9fac510f2c807aa3af52872340dc37a4a/LICENSE.txt", hy.LicenseUrl);
        Assert.Equal(TranslationPreparationAction.NextStep, view.PrimaryAction.Id);
        Assert.Empty(view.SecondaryActions);
    }

    [Fact]
    public void PackageRequestsKeepTheObservedGenerationAndExplicitActiveFamily()
    {
        var legacy = PackageSelections("argos-zh-en", "argos-en-zh");
        var initial = new ModelPackageManagementFact(new string('d', 32), true, legacy, legacy, legacy,
            true, null, "auto", "en", false);
        var download = Assert.Single(ApplicationSupportPresentation.Preparation(new(true, true, Packages: initial))
            .PackageRows.Single(row => row.Preview).Actions);
        Assert.Equal(ModelPackageOperation.Install, download.Request.Operation);
        Assert.Equal(1, download.Request.Format);
        Assert.Equal(["hy-mt2-1-8b-q4-k-m"], download.Request.PackageIds);
        Assert.Equal(download.Request.PackageIds, download.Request.ActivePackages);
        Assert.True(download.RequiresConfirmation);
        Assert.Contains("1.13 GB", download.ConfirmationText);
        var all = legacy.Concat(PackageSelections("hy-mt2-1-8b-q4-k-m")).ToArray();
        var facts = initial with
        {
            InstalledPackages = all
        };
        var hy = ApplicationSupportPresentation.Preparation(new(true, true, Packages: facts)).PackageRows.Single(row => row.Preview);
        Assert.True(hy.Installed);
        Assert.False(hy.Current);
        Assert.False(hy.Ready);
        Assert.Empty(hy.ReadyDirectionIds);
        var remove = Assert.Single(hy.Actions, action => action.Request.Operation == ModelPackageOperation.Remove);
        Assert.Equal(["hy-mt2-1-8b-q4-k-m"], remove.Request.PackageIds);
        Assert.Equal(["argos-zh-en", "argos-en-zh"], remove.Request.ActivePackages);
        Assert.Equal(new string('d', 32), remove.Request.ExpectedCurrent);
        var busy = ApplicationSupportPresentation.Preparation(new(true, true, Packages: facts with
        {
            OperationInProgress = true
        }));
        Assert.All(busy.PackageRows.SelectMany(row => row.Actions), action => Assert.False(action.Enabled));
    }

    [Fact]
    public void AnUnavailableInactivePreviewCanStillBeRemovedWhileKeepingArgos()
    {
        var installed = PackageSelections("argos-zh-en", "hy-mt2-1-8b-q4-k-m");
        var active = installed.Where(item => item.Package.Backend == "argos").ToArray();
        var facts = new ModelPackageManagementFact(new string('f', 32), true, installed, active, active,
            false, "native-unavailable", "zh", "en", false);
        var row = ApplicationSupportPresentation.Preparation(new(true, true, Packages: facts)).PackageRows.Single(item => item.Preview);
        var remove = Assert.Single(row.Actions);
        Assert.Equal(ModelPackageOperation.Remove, remove.Request.Operation);
        Assert.Equal(["hy-mt2-1-8b-q4-k-m"], remove.Request.PackageIds);
        Assert.Equal(["argos-zh-en"], remove.Request.ActivePackages);
        Assert.Equal(new string('f', 32), remove.Request.ExpectedCurrent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShippedCatalogOffersAnExplicitEnglishPreviewWithoutSelectingAnotherModel(bool hyCurrent)
    {
        var catalog = ModelCatalog.Packages;
        Assert.Equal(14, catalog.Packages.Single(package => package.Preview).Directions.Count);
        var installed = PackageSelections("argos-zh-en", "hy-mt2-1-8b-q4-k-m");
        var current = installed.Where(item => (item.Package.Backend == "hy-mt-llama-cpu") == hyCurrent).ToArray();
        var facts = new ModelPackageManagementFact(new string('a', 32), true, installed, current, current,
            true, null, "zh", "en", false);
        var view = ApplicationSupportPresentation.Preparation(new(true, true, Packages: facts), catalog: catalog);
        var hy = Assert.Single(view.PackageRows, row => row.Preview);
        var supplement = Assert.Single(hy.Actions, action => action.Request.Format == 2);

        Assert.Equal("补充中英方向…", supplement.Label);
        Assert.True(supplement.Enabled);
        Assert.True(supplement.RequiresConfirmation);
        Assert.Equal(ModelPackageOperation.Install, supplement.Request.Operation);
        Assert.Equal(["hy-mt2-1-8b-q4-k-m"], supplement.Request.PackageIds);
        Assert.Equal(hyCurrent ? ["hy-mt2-1-8b-q4-k-m"] : ["argos-zh-en"], supplement.Request.ActivePackages);
        Assert.Equal(new string('a', 32), supplement.Request.ExpectedCurrent);
        Assert.Equal(["zh_en", "en_zh"], Assert.IsType<string[]>(supplement.Request.AddDirections));
        Assert.Contains("不会下载模型", supplement.ConfirmationText);
        Assert.Contains("当前模型选择和语言设置保持不变", supplement.ConfirmationText);
        Assert.Contains("取消不会更改", supplement.ConfirmationText);
        Assert.Contains("中英预览暂不支持风格和术语表", supplement.ConfirmationText);
        Assert.Contains("可能出现错译或漏译", supplement.ConfirmationText);
        Assert.Contains("不支持直接降级旧版本", supplement.ConfirmationText);
        Assert.Contains("可在当前版本切回 Argos 继续使用", supplement.ConfirmationText);
        Assert.Contains("若未安装 Argos，需先另行确认下载", supplement.ConfirmationText);
        Assert.Contains("中英预览暂不支持风格和术语表", hy.Details);
        Assert.StartsWith("目录可用：", hy.CoverageText);
        Assert.Contains("English", hy.CoverageText);
        Assert.Contains("已安装：", hy.AvailabilityText);
        Assert.DoesNotContain("English", hy.AvailabilityText);
        Assert.Equal(14, hy.DirectionIds.Length);
        Assert.Equal(12, hy.InstalledDirectionIds.Length);
        Assert.DoesNotContain("zh_en", hy.InstalledDirectionIds);
        Assert.DoesNotContain("en_zh", hy.CurrentDirectionIds);
        Assert.DoesNotContain("zh_en", hy.ReadyDirectionIds);
        Assert.All(hy.Actions.Where(action => action != supplement), action =>
        {
            Assert.Equal(1, action.Request.Format);
            Assert.Null(action.Request.AddDirections);
        });
        Assert.Equal(12, ModelCatalog.DefaultInstallationDirections(catalog.Packages.Single(package => package.Preview)).Count);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("zh_en")]
    [InlineData("en_zh")]
    public void APartialOrOldCatalogNeverOffersTheEnglishSupplement(string direction)
    {
        var catalog = EnglishCandidateCatalog(direction == "none" ? [] : [direction]);
        var installed = PackageSelections("hy-mt2-1-8b-q4-k-m");
        var facts = new ModelPackageManagementFact(new string('b', 32), true, installed, installed, [],
            true, null, "zh", "ja", false);
        var view = ApplicationSupportPresentation.Preparation(new(false, true, Packages: facts), catalog: catalog);
        Assert.DoesNotContain(view.PackageRows.SelectMany(row => row.Actions), action => action.Request.Format == 2);
    }

    [Fact]
    public void AnEnglishCandidateStillOffersOnlyTheExistingDefaultInstallForANewPackage()
    {
        var catalog = EnglishCandidateCatalog();
        var facts = new ModelPackageManagementFact(null, true, [], [], [], true, null, "zh", "en", false);
        var hy = ApplicationSupportPresentation.Preparation(new(false, false, Packages: facts), catalog: catalog)
            .PackageRows.Single(row => row.Preview);
        var install = Assert.Single(hy.Actions);
        Assert.Equal("下载并使用…", install.Label);
        Assert.Contains("本次默认安装十二方向，不包含中英方向", install.ConfirmationText);
        Assert.Equal(1, install.Request.Format);
        Assert.Null(install.Request.AddDirections);
        Assert.Equal(12, ModelCatalog.DefaultInstallationDirections(catalog.Packages.Single(package => package.Preview)).Count);
        Assert.Empty(hy.InstalledDirectionIds);
    }

    [Theory]
    [InlineData("zh_en")]
    [InlineData("en_zh")]
    [InlineData("both")]
    public void AnAlreadyInstalledEnglishDirectionCannotBeSilentlyReplacedOrCompleted(string english)
    {
        var catalog = EnglishCandidateCatalog();
        string[] directions = english == "both" ? ["zh_ja", "zh_en", "en_zh"] : ["zh_ja", english];
        var installed = PackageSelections("hy-mt2-1-8b-q4-k-m", catalog: catalog, directionIds: directions);
        var facts = new ModelPackageManagementFact(new string('b', 32), true, installed, installed, [],
            true, null, "zh", "en", false);
        var row = ApplicationSupportPresentation.Preparation(new(false, true, Packages: facts), catalog: catalog)
            .PackageRows.Single(item => item.Preview);
        Assert.DoesNotContain(row.Actions, action => action.Request.Format == 2);
        var repair = Assert.Single(row.Actions, action => action.Request.Operation == ModelPackageOperation.Repair);
        Assert.Equal(1, repair.Request.Format);
        Assert.Null(repair.Request.AddDirections);
    }

    [Theory]
    [InlineData("inventory-unknown")]
    [InlineData("generation-unknown")]
    [InlineData("generation-invalid")]
    [InlineData("current-unknown")]
    [InlineData("current-mixed")]
    [InlineData("current-duplicate")]
    [InlineData("current-not-installed")]
    [InlineData("runtime-unavailable")]
    [InlineData("runtime-unverified")]
    public void UnknownOrUnsupportedExistingStateCannotOfferAnEnglishSupplement(string state)
    {
        var installed = PackageSelections("argos-zh-en", "hy-mt2-1-8b-q4-k-m");
        var active = installed.Where(item => item.Package.Backend == "hy-mt-llama-cpu").ToArray();
        var facts = new ModelPackageManagementFact(new string('c', 32), true, installed, active, [],
            true, null, "zh", "en", false);
        facts = state switch
        {
            "inventory-unknown" => facts with
            {
                InventoryKnown = false
            },
            "generation-unknown" => facts with
            {
                Generation = null
            },
            "generation-invalid" => facts with
            {
                Generation = "old-generation"
            },
            "current-unknown" => facts with
            {
                CurrentPackages = []
            },
            "current-mixed" => facts with
            {
                CurrentPackages = installed
            },
            "current-duplicate" => facts with
            {
                CurrentPackages = [active[0], active[0]]
            },
            "current-not-installed" => facts with
            {
                CurrentPackages = PackageSelections("argos-en-zh")
            },
            "runtime-unavailable" => facts with
            {
                HyRuntimeAvailable = false
            },
            "runtime-unverified" => facts with
            {
                HyUnavailableReason = "native-unavailable"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        var view = ApplicationSupportPresentation.Preparation(new(false, true, Packages: facts), catalog: EnglishCandidateCatalog());
        Assert.DoesNotContain(view.PackageRows.SelectMany(row => row.Actions), action => action.Request.Format == 2);
    }

    [Fact]
    public void AnEnglishSupplementKeepsTheExactObservedActiveOrderAndIndependentFrozenArrays()
    {
        var catalog = EnglishCandidateCatalog();
        var argos = PackageSelections("argos-zh-en", "argos-en-zh");
        var installed = argos.Concat(PackageSelections("hy-mt2-1-8b-q4-k-m", onlyDirection: "zh_ja")).ToArray();
        var current = new[] { argos[1], argos[0] };
        var facts = new ModelPackageManagementFact(new string('d', 32), true, installed, current, [],
            true, null, "auto", "en", false);
        var view = ApplicationSupportPresentation.Preparation(new(false, true, Packages: facts), catalog: catalog);
        var row = view.PackageRows.Single(item => item.Preview);
        var request = Assert.Single(row.Actions, action => action.Request.Format == 2).Request;
        var next = ApplicationSupportPresentation.Preparation(new(false, true, Packages: facts), catalog: catalog)
            .PackageRows.Single(item => item.Preview).Actions.Single(action => action.Request.Format == 2).Request;
        Assert.Equal(["argos-en-zh", "argos-zh-en"], request.ActivePackages);
        Assert.Equal(["zh_ja"], row.InstalledDirectionIds);
        current[0] = installed.Single(item => item.Package.Preview);
        Assert.Equal(["argos-en-zh", "argos-zh-en"], request.ActivePackages);
        Assert.Equal(new string('d', 32), request.ExpectedCurrent);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var wire = JsonSerializer.SerializeToElement(request, options);
        Assert.Equal(2, wire.GetProperty("format").GetInt32());
        Assert.Equal("Install", wire.GetProperty("operation").GetString());
        Assert.Equal(["zh_en", "en_zh"], wire.GetProperty("addDirections").EnumerateArray().Select(item => item.GetString()));
        request.ActivePackages[0] = "changed";
        Assert.IsType<string[]>(request.AddDirections)[0] = "changed";
        Assert.Equal(["argos-en-zh", "argos-zh-en"], next.ActivePackages);
        Assert.Equal(["zh_en", "en_zh"], Assert.IsType<string[]>(next.AddDirections));
        Assert.Equal(["argos-en-zh", "argos-zh-en"],
            row.Actions.Single(action => action.Request.Operation == ModelPackageOperation.Repair).Request.ActivePackages);
    }

    [Fact]
    public void AnExistingOperationDisablesTheSupplementWithoutChangingItsConfirmedScope()
    {
        var installed = PackageSelections("hy-mt2-1-8b-q4-k-m");
        var facts = new ModelPackageManagementFact(new string('e', 32), true, installed, installed, [],
            true, null, "zh", "en", true);
        var view = ApplicationSupportPresentation.Preparation(new(false, true, TranslationPreparationPhase.Paused,
            Packages: facts), catalog: EnglishCandidateCatalog());
        var action = Assert.Single(view.PackageRows.Single(row => row.Preview).Actions, action => action.Request.Format == 2);
        Assert.False(action.Enabled);
        Assert.Equal(TranslationPreparationAction.Resume, view.PrimaryAction.Id);
        Assert.Equal(["zh_en", "en_zh"], Assert.IsType<string[]>(action.Request.AddDirections));
    }

    private static ModelPackageCatalog EnglishCandidateCatalog(string[]? englishDirections = null)
    {
        using var source = typeof(ModelCatalog).Assembly.GetManifestResourceStream("TypeLay.ModelCatalog.json")!;
        var value = JsonNode.Parse(source)!;
        var packages = value["packages"]!.AsArray();
        var hy = packages.Single(package => package!["backend"]!.GetValue<string>() == "hy-mt-llama-cpu")!;
        hy["directions"] = new JsonArray(hy["directions"]!.AsArray()
            .Where(item => item!["id"]!.GetValue<string>() is not ("zh_en" or "en_zh"))
            .Select(item => item!.DeepClone()).ToArray());
        foreach (var package in packages.Where(package => package!["backend"]!.GetValue<string>() == "argos"
            && (englishDirections is null || englishDirections.Contains(package!["directions"]![0]!["id"]!.GetValue<string>()))))
            hy["directions"]!.AsArray().Add(package!["directions"]![0]!.DeepClone());
        using var document = JsonDocument.Parse(value.ToJsonString());
        return ModelPackageCatalog.Parse(document.RootElement);
    }

    [Theory]
    [InlineData(TranslationPreparationPhase.Failed)]
    [InlineData(TranslationPreparationPhase.Paused)]
    public void MissingCurrentDirectionDoesNotReplaceThePreparationRecoveryReason(TranslationPreparationPhase phase)
    {
        var legacy = PackageSelections("argos-zh-en");
        var facts = new ModelPackageManagementFact(new string('a', 32), true, legacy, legacy, legacy,
            true, null, "zh", "ja", true);
        var expected = ApplicationSupportPresentation.Preparation(new(true, true, phase));
        var actual = ApplicationSupportPresentation.Preparation(new(true, true, phase, Packages: facts));
        Assert.Equal(expected.Guidance, actual.Guidance);
    }

    [Theory]
    [InlineData(TranslationPreparationPhase.Paused, TranslationPreparationAction.Resume)]
    [InlineData(TranslationPreparationPhase.Failed, TranslationPreparationAction.Retry)]
    public void APackageOperationKeepsTheExistingFrozenRequestRecoveryAction(TranslationPreparationPhase phase,
        TranslationPreparationAction expected)
    {
        var legacy = PackageSelections("argos-zh-en");
        var facts = new ModelPackageManagementFact(new string('e', 32), true, legacy, legacy, legacy,
            true, null, "zh", "en", true);
        var view = ApplicationSupportPresentation.Preparation(new(true, true, phase, Packages: facts));
        Assert.Equal(expected, view.PrimaryAction.Id);
        Assert.Empty(view.SecondaryActions);
        Assert.All(view.PackageRows.SelectMany(row => row.Actions), action => Assert.False(action.Enabled));
    }

    private static IReadOnlyList<ModelPackageSelection> PackageSelections(string first, string? second = null,
        string? onlyDirection = null, ModelPackageCatalog? catalog = null, string[]? directionIds = null)
    {
        catalog ??= ModelCatalog.Packages;
        string[] ids = second is null ? [first] : [first, second];
        var inventory = JsonSerializer.SerializeToElement(new
        {
            schema = 3,
            packages = catalog.Packages.Where(package => ids.Contains(package.Id)).Select(package => new
            {
                id = package.Id,
                version = package.Version,
                directions = (onlyDirection is null && directionIds is null
                    ? ModelCatalog.DefaultInstallationDirections(package) : package.Directions)
                    .Where(direction => (onlyDirection is null || direction.Id == onlyDirection)
                    && (directionIds is null || directionIds.Contains(direction.Id)))
                    .Select(direction => direction.Id).ToArray(),
                files = new[] { new { path = "fixture.bin", sha256 = new string('0', 64) } }
            }).ToArray()
        });
        return catalog.ReadInstallationSelection(inventory);
    }

    [Fact]
    public void MissingTranslationOffersPreparationWithoutClaimingAnApplicationConnection()
    {
        var view = ApplicationSupportPresentation.Preparation(new(false, false));
        Assert.Equal("本地翻译组件尚未准备", view.StatusText);
        Assert.Equal(TranslationPreparationAction.Prepare, view.PrimaryAction.Id);
        Assert.Equal("准备本地翻译…", view.PrimaryAction.Label);
        Assert.True(view.PrimaryAction.Enabled);
        Assert.Equal(TranslationPreparationAction.NextStep, Assert.Single(view.SecondaryActions).Id);
    }

    [Fact]
    public void SubsetPreparationKeepsInstalledIntentSeparateFromActualReadiness()
    {
        var view = ApplicationSupportPresentation.Preparation(new(false, true,
            InstalledDirections: ["zh_en"], ReadyDirections: []));
        Assert.Equal("本地翻译尚未就绪", view.StatusText);
        Assert.False(view.CanSelectDirections);
        Assert.Equal(["zh_en"], view.Directions.Where(direction => direction.Installed).Select(direction => direction.Id));
        Assert.Contains(view.SecondaryActions, action => action.Id == TranslationPreparationAction.CompleteDirections);
        var ready = ApplicationSupportPresentation.Preparation(new(true, true,
            InstalledDirections: ["zh_en"], ReadyDirections: ["zh_en"]));
        Assert.Contains("中文 → English", ready.StatusText);
        Assert.DoesNotContain("English → 中文", ready.StatusText);
        var empty = ApplicationSupportPresentation.Preparation(new(false, false, InstalledDirections: []));
        Assert.True(empty.CanSelectDirections);
        Assert.Equal(2, empty.Directions.Length);
        var damaged = ApplicationSupportPresentation.Preparation(new(false, false, InstallationIntentKnown: false));
        Assert.False(damaged.CanSelectDirections);
        Assert.Equal(TranslationPreparationAction.Repair, damaged.PrimaryAction.Id);
        Assert.Contains("完整修复", damaged.PrimaryAction.Label);
    }

    [Fact]
    public void UnreadyTranslationKeepsPracticeDisabledWhileConnectionDetailsRemainDiscoverable()
    {
        var windows = ApplicationSupportPresentation.Preparation(new(false, false));
        Assert.False(Assert.Single(windows.SecondaryActions).Enabled);
        var linux = ApplicationSupportPresentation.Preparation(new(false, false), TranslationPreparationNextStep.ChooseConnection);
        Assert.True(Assert.Single(linux.SecondaryActions).Enabled);
    }

    [Theory]
    [InlineData(TranslationPreparationPhase.Preparing, TranslationPreparationAction.Pause, "暂停下载", true)]
    [InlineData(TranslationPreparationPhase.Pausing, TranslationPreparationAction.Pause, "正在暂停…", false)]
    [InlineData(TranslationPreparationPhase.Paused, TranslationPreparationAction.Resume, "继续准备", true)]
    [InlineData(TranslationPreparationPhase.StartingModel, TranslationPreparationAction.Prepare, "正在启动翻译…", false)]
    public void AnActivePreparationPhaseTakesPriorityOverAnOlderReadyModel(
        TranslationPreparationPhase phase, TranslationPreparationAction action, string label, bool enabled)
    {
        foreach (bool ready in new[] { false, true })
        {
            var view = ApplicationSupportPresentation.Preparation(new(ready, true, phase));
            Assert.Equal(action, view.PrimaryAction.Id);
            Assert.Equal(label, view.PrimaryAction.Label);
            Assert.Equal(enabled, view.PrimaryAction.Enabled);
            Assert.Empty(view.SecondaryActions);
            Assert.NotEqual("本地翻译组件已就绪", view.StatusText);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationFailureKeepsTheClassifiedReasonAndRetryEvenWithAReadyModel(bool ready)
    {
        var view = ApplicationSupportPresentation.Preparation(new(ready, true,
            TranslationPreparationPhase.Failed, SetupFailureCategory.Network));
        Assert.Equal("下载连接失败，请检查网络。", view.StatusText);
        Assert.Contains("检查网络或代理后重新准备", view.Guidance);
        Assert.Equal(TranslationPreparationAction.Retry, view.PrimaryAction.Id);
        Assert.Equal("重新准备", view.PrimaryAction.Label);
        Assert.Empty(view.SecondaryActions);
        Assert.Equal(ready, view.Guidance.Contains("当前模型仍可用"));
    }

    [Fact]
    public void ABlockedRecoveryKeepsBothTheOriginalReasonAndItsActualNextStep()
    {
        var view = ApplicationSupportPresentation.Preparation(new(false, true,
            TranslationPreparationPhase.Failed, SetupFailureCategory.Runtime, SetupFailureCategory.Permission));
        Assert.Equal("翻译运行环境准备或自检失败。", view.StatusText);
        Assert.Contains("组件文件访问受限", view.Guidance);
        Assert.Contains("检查文件占用", view.Guidance);
    }

    [Fact]
    public void InstalledButUnreadyTranslationDoesNotClaimThatComponentsAreMissing()
    {
        var view = ApplicationSupportPresentation.Preparation(new(false, true));
        Assert.Equal("本地翻译尚未就绪", view.StatusText);
        Assert.Equal(TranslationPreparationAction.Retry, view.PrimaryAction.Id);
        Assert.Equal("重新准备", view.PrimaryAction.Label);
    }

    [Theory]
    [InlineData(TranslationPreparationNextStep.TryIt, "试一试")]
    [InlineData(TranslationPreparationNextStep.ChooseConnection, "选择接入方式")]
    public void ReadyTranslationEmphasizesTheActualHostNextStepAndMakesRepairSecondary(
        TranslationPreparationNextStep nextStep, string label)
    {
        var view = ApplicationSupportPresentation.Preparation(new(true, true), nextStep);
        Assert.Equal("本地翻译组件已就绪", view.StatusText);
        Assert.Equal(TranslationPreparationAction.NextStep, view.PrimaryAction.Id);
        Assert.Equal(label, view.PrimaryAction.Label);
        Assert.Equal(TranslationPreparationAction.Repair, Assert.Single(view.SecondaryActions).Id);
        Assert.Contains("模型就绪不代表应用已经连接", view.Guidance);
    }

    [Fact]
    public void UnknownPreparationValuesCannotBePresentedAsReady()
    {
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Preparation(
            new(true, true, (TranslationPreparationPhase)99)));
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Preparation(
            new(true, true), (TranslationPreparationNextStep)99));
    }

    [Fact]
    public void BrowserInstructionsHaveASharedLabelWithoutGrantingConnection()
    {
        var view = ApplicationSupportPresentation.Create([
            new("google-chrome", ApplicationSupportState.AwaitingApproval, ApplicationSupportReason.AwaitingApproval,
                SupportedActions: [ApplicationSupportAction.OpenInstructions])
        ]);
        Assert.Equal("打开浏览器", view.Labels.OpenBrowser);
        Assert.Equal("复制", view.Labels.Copy);
        var browser = view.Rows.Single(row => row.Id == "google-chrome");
        Assert.Equal(ApplicationSupportAction.OpenInstructions, browser.PrimaryAction?.Id);
        Assert.Equal(new ApplicationSupportCapabilities(), browser.Capabilities);
    }

    [Fact]
    public void PausedInputCannotRetainConfirmedCapabilities()
    {
        var view = ApplicationSupportPresentation.Create([
            new("desktop-text", ApplicationSupportState.Connected, ApplicationSupportReason.Paused,
                new(true, true, true), [ApplicationSupportAction.Refresh])
        ]);
        var row = view.Rows.Single(row => row.Id == "desktop-text");
        Assert.Equal(new ApplicationSupportCapabilities(), row.Capabilities);
        Assert.Contains("常规设置", row.ReasonText);
        Assert.Contains("启用", row.ReasonText);
    }

    [Fact]
    public void AnUnconnectedBrowserCannotAdvertiseObservedFieldCapabilities()
    {
        var view = ApplicationSupportPresentation.Create([
            new("firefox", ApplicationSupportState.Registered, ApplicationSupportReason.AwaitingConnection,
                new(true, true, true), [ApplicationSupportAction.OpenInstructions, ApplicationSupportAction.Remove])
        ]);

        var browser = Assert.Single(view.Rows, row => row.Id == "firefox");
        Assert.Equal("已注册，尚未连接", browser.StatusText);
        Assert.False(browser.Capabilities.Preview);
        Assert.False(browser.Capabilities.TabAccept);
        Assert.False(browser.Capabilities.DirectReplace);
        Assert.Contains("浏览器", browser.ReasonText);
        Assert.Contains(browser.Actions, action => action.Id == ApplicationSupportAction.OpenInstructions);
    }

    [Fact]
    public void ManualEditorPreviewCannotBePresentedAsTabOrDirectReplacement()
    {
        var view = ApplicationSupportPresentation.Create([
            new("vscode", ApplicationSupportState.Connected, ApplicationSupportReason.PreviewOnly, new(true, true, true))
        ]);

        var editor = Assert.Single(view.Rows, row => row.Id == "vscode");
        Assert.True(editor.Capabilities.Preview);
        Assert.False(editor.Capabilities.TabAccept);
        Assert.False(editor.Capabilities.DirectReplace);
        Assert.Contains("手动", editor.Title);
        Assert.Contains("不提供自动浮层", editor.Description);
    }

    [Fact]
    public void BothHostsReceiveTheSameTaskOrderAndMissingFactsStayUnknown()
    {
        var view = ApplicationSupportPresentation.Create([
            new("vscode", ApplicationSupportState.Unsupported, ApplicationSupportReason.NotImplemented),
            new("firefox", ApplicationSupportState.Registered, ApplicationSupportReason.AwaitingConnection)
        ]);

        Assert.Equal(["desktop-text", "firefox", "chromium", "google-chrome", "microsoft-edge", "vscode"],
            view.Rows.Select(row => row.Id));
        Assert.Equal(ApplicationSupportState.Unknown, view.Rows[0].State);
        Assert.False(view.Rows[0].Capabilities.Preview);
        Assert.Contains("模型就绪不代表应用已经连接", view.Summary);
        Assert.Contains("自动/按需是触发方式", view.CapabilityNote);
    }

    [Fact]
    public void BuiltInServiceAvailabilityDoesNotClaimAnObservedField()
    {
        var view = ApplicationSupportPresentation.Create([
            new("desktop-text", ApplicationSupportState.BuiltInAvailable, ApplicationSupportReason.NoFocusedField,
                SupportedActions: [ApplicationSupportAction.Refresh, ApplicationSupportAction.Install])
        ]);

        var desktop = view.Rows[0];
        Assert.Equal("内置接入可用", desktop.StatusText);
        Assert.Equal(new ApplicationSupportCapabilities(), desktop.Capabilities);
        Assert.Equal(ApplicationSupportReason.NoFocusedField, desktop.Reason);
        Assert.Contains("切回", desktop.ReasonText);
        Assert.Equal(ApplicationSupportAction.Refresh, Assert.Single(desktop.Actions).Id);
    }

    [Theory]
    [InlineData(ApplicationSupportState.Unknown)]
    [InlineData(ApplicationSupportState.NeedsInstall)]
    [InlineData(ApplicationSupportState.AwaitingApproval)]
    [InlineData(ApplicationSupportState.RestartRequired)]
    [InlineData(ApplicationSupportState.Registered)]
    [InlineData(ApplicationSupportState.Unsupported)]
    [InlineData(ApplicationSupportState.Error)]
    public void LosingReadinessClearsPreviouslyObservedCapabilities(ApplicationSupportState state)
    {
        var connected = new ApplicationSupportFact("firefox", ApplicationSupportState.Connected,
            ApplicationSupportReason.LimitedFields, new(true, true, true));
        Assert.True(ApplicationSupportPresentation.Create([connected]).Rows[1].Capabilities.TabAccept);

        var disconnected = ApplicationSupportPresentation.Create([connected with { State = state }]);
        Assert.Equal(new ApplicationSupportCapabilities(), disconnected.Rows[1].Capabilities);
    }

    [Theory]
    [InlineData(ApplicationSupportState.Unsupported, ApplicationSupportReason.NotImplemented)]
    [InlineData(ApplicationSupportState.Unknown, ApplicationSupportReason.Unknown)]
    [InlineData(ApplicationSupportState.Error, ApplicationSupportReason.RegistrationConflict)]
    public void UnsupportedUnknownOrForeignRegistrationCannotOfferMutatingActions(
        ApplicationSupportState state, ApplicationSupportReason reason)
    {
        var view = ApplicationSupportPresentation.Create([
            new("firefox", state, reason, SupportedActions: Enum.GetValues<ApplicationSupportAction>())
        ]);

        Assert.Equal([ApplicationSupportAction.Refresh, ApplicationSupportAction.OpenInstructions],
            view.Rows[1].Actions.Select(action => action.Id));
        Assert.All(view.Rows[1].Actions, action => Assert.False(action.RequiresConfirmation));
    }

    [Fact]
    public void InstallationAndRecoveryOnlyOfferImplementedActionsWithExplicitConfirmation()
    {
        var fact = new ApplicationSupportFact("firefox", ApplicationSupportState.NeedsInstall,
            ApplicationSupportReason.RegistrationMissing,
            SupportedActions: [ApplicationSupportAction.Connect, ApplicationSupportAction.Connect, ApplicationSupportAction.Remove]);
        var action = Assert.Single(ApplicationSupportPresentation.Create([fact]).Rows[1].Actions);
        Assert.Equal(ApplicationSupportAction.Connect, action.Id);
        Assert.True(action.RequiresConfirmation);
        Assert.Contains("批准", action.ConfirmationText);

        var retry = ApplicationSupportPresentation.Create([fact with
        {
            State = ApplicationSupportState.Error,
            Reason = ApplicationSupportReason.RegistrationFailed,
            SupportedActions = [ApplicationSupportAction.Repair]
        }]);
        Assert.Equal(ApplicationSupportAction.Repair, Assert.Single(retry.Rows[1].Actions).Id);
        Assert.Contains("重试", retry.Rows[1].ReasonText);
    }

    [Fact]
    public void PreviewAndDirectReplacementRemainIndependentAndKeepTheCheckTime()
    {
        var checkedAt = new DateTimeOffset(2026, 10, 13, 9, 30, 0, TimeSpan.Zero);
        var view = ApplicationSupportPresentation.Create([
            new("desktop-text", ApplicationSupportState.BuiltInAvailable, ApplicationSupportReason.LimitedFields,
                new(false, false, true), CheckedAt: checkedAt)
        ]);

        Assert.False(view.Rows[0].Capabilities.Preview);
        Assert.False(view.Rows[0].Capabilities.TabAccept);
        Assert.True(view.Rows[0].Capabilities.DirectReplace);
        Assert.Equal(checkedAt, view.Rows[0].CheckedAt);
        Assert.Contains("每次写回仍须单独通过安全检查", view.CapabilityNote);
    }

    [Fact]
    public void InvalidOrAmbiguousFactSetsCannotMasqueradeAsAValidView()
    {
        var fact = new ApplicationSupportFact("firefox", ApplicationSupportState.Registered);
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Create([fact, fact]));
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Create([fact with { Id = "unrecognized-app" }]));
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Create([fact with { State = (ApplicationSupportState)999 }]));
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Create([fact with { Reason = (ApplicationSupportReason)999 }]));
    }

    [Fact]
    public void ChoosingAComponentOnlyChecksItAndIsTheSingleNextStep()
    {
        var view = ApplicationSupportPresentation.Create([
            new("desktop-text", ApplicationSupportState.NeedsInstall, ApplicationSupportReason.ComponentMissing,
                SupportedActions: [ApplicationSupportAction.Refresh, ApplicationSupportAction.OpenInstructions, ApplicationSupportAction.CheckComponent])
        ]);

        var primary = Assert.IsType<ApplicationSupportActionView>(view.Rows[0].PrimaryAction);
        Assert.Equal(ApplicationSupportAction.CheckComponent, primary.Id);
        Assert.Equal("选择并检查组件…", primary.Label);
        Assert.False(primary.RequiresConfirmation);
        Assert.DoesNotContain(view.Rows[0].SecondaryActions, action => action.Id == primary.Id);
        Assert.Equal(2, view.Rows[0].SecondaryActions.Length);
    }

    [Fact]
    public void OnlyExplicitlyUndetectedApplicationsGoIntoMoreWithoutHidingUnknownOrConnectedOnes()
    {
        var view = ApplicationSupportPresentation.Create([
            new("firefox", ApplicationSupportState.NeedsInstall, ApplicationDetected: false),
            new("chromium", ApplicationSupportState.Unknown),
            new("google-chrome", ApplicationSupportState.Connected, ApplicationDetected: false),
            new("microsoft-edge", ApplicationSupportState.Unsupported, ApplicationSupportReason.NotImplemented, ApplicationDetected: true)
        ]);

        Assert.True(view.Rows[1].ShowInMore);
        Assert.False(view.Rows[2].ShowInMore);
        Assert.False(view.Rows[3].ShowInMore);
        Assert.False(view.Rows[4].ShowInMore);
        Assert.Equal("当前尚不支持", view.Rows[4].StatusText);
    }

    [Theory]
    [InlineData(ApplicationSupportReason.Unknown)]
    [InlineData(ApplicationSupportReason.NoFocusedField)]
    [InlineData(ApplicationSupportReason.FieldUnobserved)]
    [InlineData(ApplicationSupportReason.DetectionFailed)]
    public void AConnectionWithNoReliableFieldObservationCannotRetainCapabilityClaims(ApplicationSupportReason reason)
    {
        var view = ApplicationSupportPresentation.Create([
            new("firefox", ApplicationSupportState.Connected, reason, new(true, true, true))
        ]);

        Assert.Equal(ApplicationSupportState.Connected, view.Rows[1].State);
        Assert.Equal(new ApplicationSupportCapabilities(), view.Rows[1].Capabilities);
    }
}
