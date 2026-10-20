namespace TypeLay.Runtime;

public static class ApplicationSupportPresentation
{
    public static string LanguageName(string code) => code switch
    {
        "auto" => "Auto",
        "en" => "EN",
        _ => ModelCatalog.Packages.Packages.SelectMany(package => package.Directions)
            .SelectMany(direction => new[] { new ModelLanguageChoice(direction.Source, direction.SourceName),
                new ModelLanguageChoice(direction.Target, direction.TargetName) })
            .FirstOrDefault(language => language.Code == code)?.Label ?? code + "（当前不可用）"
    };

    public static TranslationPreparationView Preparation(TranslationPreparationFact facts,
        TranslationPreparationNextStep nextStep = TranslationPreparationNextStep.TryIt)
    {
        var view = PreparationCore(facts, nextStep);
        if (facts.Packages is { } packages)
            return PackagePreparation(facts, packages, view, nextStep);
        var installed = facts.InstalledDirections ?? [];
        if (facts.Phase == TranslationPreparationPhase.Idle && facts.FailureCategory is null)
        {
            if (!facts.InstallationIntentKnown)
                view = view with
                {
                    StatusText = "本地组件安装记录无法读取",
                    Guidance = "请完整修复中英双向组件。",
                    PrimaryAction = new(TranslationPreparationAction.Repair, "完整修复本地翻译…"),
                    SecondaryActions = []
                };
            else if (installed.Count > 0 && !ModelCatalog.IsCompleteDirectionSet(installed))
                view = view with
                {
                    SecondaryActions = [new(TranslationPreparationAction.CompleteDirections, "补齐另一方向"), .. view.SecondaryActions]
                };
            if (facts.ModelReady && facts.ReadyDirections is { Count: > 0 } ready)
                view = view with
                {
                    StatusText = "本地翻译已就绪 · " + string.Join("、", ready.Select(id =>
                {
                    var direction = ModelCatalog.Get(id);
                    return direction.SourceName + " → " + direction.TargetName;
                }))
                };
        }
        return view with
        {
            Directions = ModelCatalog.Directions.Select(direction => new TranslationPreparationDirectionView(
                direction.Id, direction.SourceName + " → " + direction.TargetName,
                installed.Contains(direction.Id, StringComparer.Ordinal))).ToArray(),
            CanSelectDirections = facts.InstallationIntentKnown && installed.Count == 0
                && facts.Phase == TranslationPreparationPhase.Idle && facts.FailureCategory is null
        };
    }
    private static TranslationPreparationView PackagePreparation(TranslationPreparationFact facts,
        ModelPackageManagementFact packages, TranslationPreparationView view, TranslationPreparationNextStep nextStep)
    {
        var rows = ModelCatalog.Packages.Packages.Select(package => PackageRow(package, packages)).ToArray();
        string currentText = !packages.InventoryKnown ? "当前模型无法确认"
            : packages.CurrentPackages.Count == 0 ? "尚未选择本地模型"
            : "当前模型：" + string.Join("、", packages.CurrentPackages.Select(item => PackageTitle(item.Package)));
        bool idle = facts.Phase == TranslationPreparationPhase.Idle && facts.FailureCategory is null;
        if (!packages.InventoryKnown)
            view = view with
            {
                StatusText = "本地组件安装记录无法读取",
                Guidance = "未更改模型。请重新打开 TypeLay 检查安装记录。",
                PrimaryAction = new(TranslationPreparationAction.Prepare, "暂不可操作", false),
                SecondaryActions = []
            };
        else if (idle && packages.InstalledPackages.Count > 0)
        {
            var readyDirections = packages.ReadyPackages.SelectMany(item => item.Directions).ToArray();
            view = view with
            {
                StatusText = facts.ModelReady && readyDirections.Length > 0
                    ? "本地翻译已就绪 · " + string.Join("、", readyDirections.Select(DirectionLabel))
                    : "本地翻译尚未就绪",
                PrimaryAction = new(TranslationPreparationAction.NextStep,
                    nextStep == TranslationPreparationNextStep.ChooseConnection ? "选择接入方式" : "试一试",
                    facts.ModelReady || nextStep == TranslationPreparationNextStep.ChooseConnection),
                SecondaryActions = []
            };
        }
        if (idle && packages.InventoryKnown && packages.CurrentPackages.Count > 0)
        {
            // Hello may cover only a subset of a package. Never expand it to catalog coverage.
            var actual = (facts.ModelReady ? packages.ReadyPackages : packages.CurrentPackages)
                .SelectMany(item => item.Directions);
            bool covered = packages.Source == packages.Target || actual.Any(direction =>
                direction.Target == packages.Target && (packages.Source == "auto" || direction.Source == packages.Source));
            if (!covered)
                view = view with
                {
                    Guidance = "当前模型不包含这个方向。请切换模型或调整语言。"
                };
        }
        if (idle && packages.InventoryKnown && facts.ModelReady && packages.ReadyPackages.Count == 0
            && facts.ReadyDirections is { Count: > 0 } legacyDirections)
        {
            // Legacy hello proves ordinary directions, not exact package identity.
            // Both hosts display that readiness without manufacturing ReadyPackages.
            var ready = legacyDirections.Select(ModelCatalog.Get).ToArray();
            bool covered = packages.Source == packages.Target || ready.Any(direction =>
                direction.Target == packages.Target && (packages.Source == "auto" || direction.Source == packages.Source));
            view = view with
            {
                StatusText = "本地翻译已就绪 · " + string.Join("、", ready.Select(DirectionLabel)),
                Guidance = covered ? "旧安装尚未提供精确包身份，可明确选择修复以升级安装记录。"
                    : "当前模型不包含这个方向。请切换模型或调整语言。"
            };
            rows = rows.Select(row => row.Current ? row with
            {
                AvailabilityText = "当前 · 旧版方向已加载，包身份未验证"
            } : row).ToArray();
        }
        return view with
        {
            CurrentModelText = currentText,
            PackageRows = rows,
            Directions = ModelCatalog.Directions.Select(direction => new TranslationPreparationDirectionView(
                direction.Id, DirectionLabel(direction), packages.InstalledPackages.Any(item =>
                    item.Directions.Any(installed => installed.Id == direction.Id)))).ToArray(),
            CanSelectDirections = packages.InventoryKnown && packages.InstalledPackages.Count == 0
                && !packages.OperationInProgress && idle
        };
    }

    private static ModelPackageRow PackageRow(ModelPackage package, ModelPackageManagementFact? facts)
    {
        var installed = facts?.InstalledPackages.FirstOrDefault(item => SamePackage(item, package));
        var current = facts?.CurrentPackages.FirstOrDefault(item => SamePackage(item, package));
        var ready = facts?.ReadyPackages.FirstOrDefault(item => SamePackage(item, package));
        bool available = package.Backend == "argos" || facts is { HyRuntimeAvailable: true, HyUnavailableReason: null };
        string unavailable = available ? "" : "当前安装包不含可用的 Hy 运行组件";
        string status = facts is { InventoryKnown: false }
            ? "安装状态无法确认" + (unavailable.Length > 0 ? " · " + unavailable : "")
            : unavailable.Length > 0 ? unavailable
            : current is not null ? ready is not null ? "当前 · 已就绪" : "当前 · 尚未就绪"
            : installed is not null ? "已安装" : "尚未安装";
        long? bytes = ModelCatalog.PackageDownloadBytes(package);
        string download = bytes is { } count ? (count / 1_000_000_000d).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GB 下载" : "";
        string license = package.Backend == "hy-mt-llama-cpu" ? new Uri(new Uri(package.Url), "LICENSE.txt").AbsoluteUri : "";
        return new(package.Id, package.Version, PackageTitle(package), package.Preview,
            package.Directions.Select(direction => direction.Id).ToArray(), PackageCoverage(package),
            installed is not null, current is not null, ready is not null,
            installed?.Directions.Select(direction => direction.Id).ToArray() ?? [],
            current?.Directions.Select(direction => direction.Id).ToArray() ?? [],
            ready?.Directions.Select(direction => direction.Id).ToArray() ?? [], bytes, download, license,
            package.Preview ? "预览版可能出现漏译或表达偏差，请核对译文。仅支持列出的中文双向直译，不会经英语中转。模型许可证随包保留。"
                : "翻译在本机运行，输入内容不会上传。模型许可证随包保留。",
            status, facts is null || !facts.InventoryKnown ? [] : PackageActions(package, facts, installed is not null, current is not null, available));
    }

    private static bool SamePackage(ModelPackageSelection selection, ModelPackage package) =>
        selection.Package.Id == package.Id && selection.Package.Version == package.Version;

    private static string PackageTitle(ModelPackage package) => package.Backend == "hy-mt-llama-cpu"
        ? "Hy-MT2 1.8B · 中文多语" : "Argos · " + string.Join("、", package.Directions.Select(DirectionLabel));
    private static string PackageCoverage(ModelPackage package)
    {
        var fromChinese = package.Directions.Where(direction => direction.Source == "zh").ToArray();
        if (package.Backend == "hy-mt-llama-cpu" && fromChinese.Length * 2 == package.Directions.Count
            && fromChinese.All(direction => package.Directions.Any(reverse =>
                reverse.Source == direction.Target && reverse.Target == "zh")))
            return "中文 ↔ " + string.Join("、", fromChinese.Select(direction => direction.TargetName));
        return string.Join("、", package.Directions.Select(DirectionLabel));
    }
    private static string DirectionLabel(ModelDirection direction) => direction.SourceName + " → " + direction.TargetName;

    private static ModelPackageActionView[] PackageActions(ModelPackage package, ModelPackageManagementFact facts,
        bool installed, bool current, bool available)
    {
        List<ModelPackageActionView> actions = [];
        string[] active = facts.CurrentPackages.Select(item => item.Package.Id).ToArray();
        string[] selected = package.Backend == "argos"
            ? ModelCatalog.Packages.Packages.Where(item => item.Backend == "argos"
                && (item.Id == package.Id || facts.InstalledPackages.Any(entry => SamePackage(entry, item))))
                .Select(item => item.Id).ToArray()
            : [package.Id];
        bool enabled = !facts.OperationInProgress;
        ModelPackageActionView Action(ModelPackageOperation operation, string label, string[] ids,
            string[] next, string confirmation) => new(label, enabled, true, confirmation,
                new(1, operation, ids, next, facts.Generation));
        string size = ModelCatalog.PackageDownloadBytes(package) is { } bytes
            ? $"下载约 {(bytes / 1_000_000_000d).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} GB。" : "";
        if (!installed && available)
            actions.Add(Action(ModelPackageOperation.Install, "下载并使用…", [package.Id], selected,
                "将从 " + (package.Backend == "hy-mt-llama-cpu" ? "Hugging Face" : "Argos")
                + " 下载并使用 " + PackageTitle(package) + "。" + size
                + "缺少的私有运行组件由 " + facts.RuntimeDownloadSources + " 准备。安装还需要运行组件、缓存和切换副本空间，开始前会检查可用空间。现有模型保留；语言设置不变。"
                + (package.Preview ? "这是预览版，请核对译文。" : "")
                + "翻译在本机运行，输入内容不会上传。"));
        else if (installed)
        {
            if (!current && available)
                actions.Add(Action(ModelPackageOperation.Select, "使用此模型", [], selected,
                    "将切换到 " + string.Join("、", ModelCatalog.Packages.Packages.Where(item => selected.Contains(item.Id)).Select(PackageTitle))
                    + "。切换时翻译会短暂不可用；不会下载模型，语言设置不变。"));
            bool activeAvailable = facts.CurrentPackages.All(item => item.Package.Backend == "argos")
                || facts is { HyRuntimeAvailable: true, HyUnavailableReason: null };
            if (activeAvailable && active.Length > 0)
            {
                if (available)
                    actions.Add(Action(ModelPackageOperation.Repair, "修复…", [package.Id], active,
                        "将重新准备 " + PackageTitle(package) + "，可能需要下载。当前模型选择和语言设置保持不变。"));
                if (!current)
                    actions.Add(Action(ModelPackageOperation.Remove, "移除…", [package.Id], active,
                        "将移除 " + PackageTitle(package) + " 的已安装副本。当前模型继续保留；以后使用时需要重新下载。"));
            }
        }
        return actions.ToArray();
    }

    private static TranslationPreparationView PreparationCore(TranslationPreparationFact facts,
        TranslationPreparationNextStep nextStep)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!Enum.IsDefined(facts.Phase) || !Enum.IsDefined(nextStep))
            throw new ArgumentException("translation-preparation-facts", nameof(facts));
        switch (facts.Phase)
        {
            case TranslationPreparationPhase.Preparing:
                return new("正在准备本地翻译组件…", "可暂停并保留已验证的下载进度。",
                    new(TranslationPreparationAction.Pause, "暂停下载"), []);
            case TranslationPreparationPhase.Pausing:
                return new("正在暂停，保留已验证的下载进度…", "暂停完成后可继续准备。",
                    new(TranslationPreparationAction.Pause, "正在暂停…", false), []);
            case TranslationPreparationPhase.Paused:
                return new(SetupFailurePresentation.PausedMessage, "",
                    new(TranslationPreparationAction.Resume, SetupFailurePresentation.ResumeLabel), []);
            case TranslationPreparationPhase.StartingModel:
                return new("正在启动本地翻译…", "启动完成后才能确认本地翻译是否就绪。",
                    new(TranslationPreparationAction.Prepare, "正在启动翻译…", false), []);
        }
        if (facts.Phase == TranslationPreparationPhase.Failed || facts.FailureCategory is not null)
        {
            var failure = SetupFailurePresentation.Create(facts.FailureCategory ?? SetupFailureCategory.Unknown);
            var recovery = facts.RecoveryFailureCategory is { } category ? SetupFailurePresentation.Create(category) : null;
            string guidance = recovery is null ? failure.NextStep : recovery.Message + "\n" + recovery.NextStep;
            if (facts.ModelReady)
                guidance += "\n当前模型仍可用；本次准备尚未完成。";
            return new(failure.Message, guidance, new(TranslationPreparationAction.Retry, failure.RetryLabel), []);
        }
        var next = new TranslationPreparationActionView(TranslationPreparationAction.NextStep,
            nextStep == TranslationPreparationNextStep.ChooseConnection ? "选择接入方式" : "试一试",
            facts.ModelReady || nextStep == TranslationPreparationNextStep.ChooseConnection);
        if (facts.ModelReady)
            return new("本地翻译组件已就绪", "模型就绪不代表应用已经连接；请继续检查实际输入框。",
                next, [new(TranslationPreparationAction.Repair, "修复本地翻译组件…")]);
        if (facts.ModelInstalled)
            return new("本地翻译尚未就绪", "组件已安装，但本地翻译尚未确认可用。请查看模型状态或重新准备。",
                new(TranslationPreparationAction.Retry, "重新准备"), [next]);
        return new("本地翻译组件尚未准备", "准备本地运行组件与模型后，再连接需要使用的应用。",
            new(TranslationPreparationAction.Prepare, "准备本地翻译…"), [next]);
    }

    public static string ConnectionStatus(InputConnectionState state) => state switch
    {
        InputConnectionState.Unavailable => "输入服务暂不可用",
        InputConnectionState.Connecting => "正在连接输入服务",
        InputConnectionState.Connected => "输入服务已连接",
        InputConnectionState.ListenerUnavailable => "输入监听尚未就绪",
        InputConnectionState.Reconnecting => "输入服务连接中断，正在重试",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private sealed record Entry(string Id, string Title, string Description, bool ManualOnly = false);

    // Product tasks, not toolkit/provider names. Both desktop hosts render this order.
    private static readonly Entry[] Catalog =
    [
        new("desktop-text", "桌面普通输入框", "聊天、邮件等应用中的普通文本输入框；具体能力由实际字段检查确认。"),
        new("firefox", "Firefox", "浏览器网页中的普通文本输入框。"),
        new("chromium", "Chromium", "浏览器网页中的普通文本输入框。"),
        new("google-chrome", "Google Chrome", "浏览器网页中的普通文本输入框。"),
        new("microsoft-edge", "Microsoft Edge", "浏览器网页中的普通文本输入框。"),
        new("vscode", "VS Code 手动翻译", "选择文字或明确选择整篇文档后，手动预览并确认替换；不提供自动浮层、Tab 接受或 F10 直接替换。", true)
    ];

    public static ApplicationSupportView Create(IEnumerable<ApplicationSupportFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var byId = new Dictionary<string, ApplicationSupportFact>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (fact is null || !Catalog.Any(entry => entry.Id == fact.Id)
                || !Enum.IsDefined(fact.State) || !Enum.IsDefined(fact.Reason)
                || !byId.TryAdd(fact.Id, fact))
                throw new ArgumentException("application-support-facts", nameof(facts));
        }
        var rows = Catalog.Select(entry => Row(entry, byId.GetValueOrDefault(entry.Id)
            ?? new(entry.Id, ApplicationSupportState.Unknown))).ToArray();
        return new("应用支持",
            "先准备本地翻译组件，再连接需要使用的应用，最后到实际输入框试一试。模型就绪不代表应用已经连接。",
            "能力表示最近一次检查结果；当前输入框与每次写回仍须单独通过安全检查。自动/按需是触发方式，不是能力等级。",
            rows, new("继续", "取消", "选择接入组件", "打开文件夹", "重新检查", "查看详情",
                "准备翻译", "连接应用", "试一试", "失败恢复", "其他应用", "打开浏览器", "复制"));
    }

    private static ApplicationSupportRow Row(Entry entry, ApplicationSupportFact fact)
    {
        var capabilities = fact.State is ApplicationSupportState.BuiltInAvailable or ApplicationSupportState.Connected
            ? fact.Capabilities ?? new() : new();
        if (fact.Reason is not (ApplicationSupportReason.None or ApplicationSupportReason.LimitedFields
            or ApplicationSupportReason.PreviewOnly or ApplicationSupportReason.NoDirectWrite))
            capabilities = new();
        capabilities = capabilities with
        {
            TabAccept = capabilities.TabAccept && capabilities.Preview && !entry.ManualOnly
                && fact.Reason != ApplicationSupportReason.PreviewOnly,
            DirectReplace = capabilities.DirectReplace && !entry.ManualOnly
                && fact.Reason is not (ApplicationSupportReason.PreviewOnly or ApplicationSupportReason.NoDirectWrite)
        };
        bool unavailable = fact.State == ApplicationSupportState.Unsupported;
        string Capability(bool supported) => supported ? "已确认" : unavailable ? "不支持" : "未确认";
        string tab = entry.ManualOnly ? "不提供" : Capability(capabilities.TabAccept);
        string direct = entry.ManualOnly ? "不提供" : Capability(capabilities.DirectReplace);
        string capabilityText = $"显示译文：{Capability(capabilities.Preview)} · Tab 接受译文：{tab} · 直接翻译并替换：{direct}";
        var actions = (fact.SupportedActions ?? []).Distinct().Where(action => Allowed(fact, action))
            .Order().Select(ActionView).ToArray();
        var primary = PrimaryAction(fact.State, actions);
        string capabilitySummary = CapabilitySummary(entry, capabilities);
        return new(entry.Id, entry.Title, entry.Description, fact.State, StatusText(fact.State),
            fact.Reason, ReasonText(fact.Reason), capabilities, capabilityText, actions,
            fact.Details, fact.CheckedAt, Guidance(fact.State), capabilitySummary,
            primary, actions.Where(action => action != primary).ToArray(),
            entry.Id != "desktop-text" && fact.ApplicationDetected == false && fact.State != ApplicationSupportState.Connected,
            PrimarySummary(fact, capabilitySummary));
    }

    private static string PrimarySummary(ApplicationSupportFact fact, string capabilitySummary) =>
        fact.State is ApplicationSupportState.Connected or ApplicationSupportState.BuiltInAvailable
        && fact.Reason is ApplicationSupportReason.None or ApplicationSupportReason.LimitedFields
            or ApplicationSupportReason.PreviewOnly or ApplicationSupportReason.NoDirectWrite
            ? capabilitySummary : ReasonText(fact.Reason);

    private static string CapabilitySummary(Entry entry, ApplicationSupportCapabilities capabilities)
    {
        if (entry.ManualOnly && capabilities.Preview)
            return "可手动预览并确认替换，不接管 Tab 或 F10。";
        return (capabilities.Preview, capabilities.TabAccept, capabilities.DirectReplace) switch
        {
            (true, true, true) => "已确认预览、Tab 接受和直接替换。",
            (true, true, false) => "已确认预览和 Tab 接受，尚未确认直接替换。",
            (true, false, true) => "已确认预览和直接替换，尚未确认 Tab 接受。",
            (true, false, false) => "目前仅确认预览译文，尚未确认替换能力。",
            (false, false, true) => "已确认直接替换，尚未确认预览或 Tab 接受。",
            _ => "尚未确认当前输入框的翻译能力。"
        };
    }

    private static ApplicationSupportActionView? PrimaryAction(ApplicationSupportState state, ApplicationSupportActionView[] actions)
    {
        ApplicationSupportAction[] order = state switch
        {
            ApplicationSupportState.NeedsInstall => [ApplicationSupportAction.CheckComponent, ApplicationSupportAction.Install,
                ApplicationSupportAction.Connect, ApplicationSupportAction.OpenInstructions, ApplicationSupportAction.Refresh],
            ApplicationSupportState.AwaitingApproval or ApplicationSupportState.RestartRequired or ApplicationSupportState.Registered =>
                [ApplicationSupportAction.OpenInstructions, ApplicationSupportAction.Connect, ApplicationSupportAction.CheckComponent, ApplicationSupportAction.Refresh],
            ApplicationSupportState.Error => [ApplicationSupportAction.Repair, ApplicationSupportAction.CheckComponent,
                ApplicationSupportAction.Install, ApplicationSupportAction.OpenInstructions, ApplicationSupportAction.Refresh],
            ApplicationSupportState.Unsupported => [ApplicationSupportAction.OpenInstructions],
            _ => [ApplicationSupportAction.Refresh, ApplicationSupportAction.OpenInstructions]
        };
        return order.Select(id => actions.FirstOrDefault(action => action.Id == id)).FirstOrDefault(action => action is not null);
    }

    private static bool Allowed(ApplicationSupportFact fact, ApplicationSupportAction action)
    {
        if (action is ApplicationSupportAction.Refresh or ApplicationSupportAction.OpenInstructions)
            return true;
        if (fact.Reason == ApplicationSupportReason.RegistrationConflict)
            return false;
        return action switch
        {
            ApplicationSupportAction.Install => fact.State == ApplicationSupportState.NeedsInstall
                || fact.State == ApplicationSupportState.Error && fact.Reason == ApplicationSupportReason.ComponentMissing,
            ApplicationSupportAction.Connect => fact.State is ApplicationSupportState.NeedsInstall or ApplicationSupportState.AwaitingApproval,
            ApplicationSupportAction.Repair => fact.State == ApplicationSupportState.Error,
            ApplicationSupportAction.CheckComponent => fact.State is ApplicationSupportState.NeedsInstall
                or ApplicationSupportState.AwaitingApproval or ApplicationSupportState.RestartRequired or ApplicationSupportState.Error,
            ApplicationSupportAction.Disconnect => fact.Id == "desktop-text" && fact.State is
                ApplicationSupportState.Connected or ApplicationSupportState.AwaitingApproval or ApplicationSupportState.Error
                or ApplicationSupportState.NeedsInstall,
            ApplicationSupportAction.Remove => fact.State is ApplicationSupportState.Registered or ApplicationSupportState.Connected
                or ApplicationSupportState.RestartRequired or ApplicationSupportState.AwaitingApproval or ApplicationSupportState.Error,
            _ => false
        };
    }

    private static ApplicationSupportActionView ActionView(ApplicationSupportAction action) => action switch
    {
        ApplicationSupportAction.Refresh => new(action, "检查连接", false),
        ApplicationSupportAction.OpenInstructions => new(action, "查看接入步骤", false),
        ApplicationSupportAction.CheckComponent => new(action, "选择并检查组件…", false),
        ApplicationSupportAction.Install => new(action, "安装接入组件…", true,
            "将安装所选应用的 TypeLay 接入组件。安装完成后仍须检查实际连接；不会自动切换输入法或结束应用进程。"),
        ApplicationSupportAction.Connect => new(action, "连接应用…", true,
            "将为所选应用配置 TypeLay 接入。需要应用或系统批准时，请按提示完成授权，再检查连接。"),
        ApplicationSupportAction.Repair => new(action, "修复连接…", true,
            "将修复此应用的 TypeLay 接入配置，保留设置、模型与个人数据。不会覆盖其他软件的注册项。"),
        ApplicationSupportAction.Disconnect => new(action, "结束本次连接…", true,
            "将结束仅由 TypeLay 启动的本次输入会话，保留接入组件、设置、模型与个人数据。请先正常关闭相关应用及其子窗口；不会关闭你的应用，也不会自动恢复原输入法，请按原方式手动恢复。"),
        ApplicationSupportAction.Remove => new(action, "移除接入…", true,
            "将移除此应用的 TypeLay 接入配置，保留设置、模型与个人数据。需要时可以重新连接。"),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static string StatusText(ApplicationSupportState state) => state switch
    {
        ApplicationSupportState.BuiltInAvailable => "内置接入可用",
        ApplicationSupportState.NeedsInstall => "需要安装或连接",
        ApplicationSupportState.AwaitingApproval => "等待用户授权",
        ApplicationSupportState.RestartRequired => "需要重新打开应用",
        ApplicationSupportState.Registered => "已注册，尚未连接",
        ApplicationSupportState.Connected => "已连接",
        ApplicationSupportState.Unsupported => "当前尚不支持",
        ApplicationSupportState.Error => "接入异常",
        _ => "尚未检查"
    };

    public static string ReasonText(ApplicationSupportReason reason) => reason switch
    {
        ApplicationSupportReason.None => "连接已确认；具体输入框仍需单独检查。",
        ApplicationSupportReason.ComponentMissing => "缺少所需的接入组件。请先安装或选择完整组件，再检查连接。",
        ApplicationSupportReason.RegistrationMissing => "尚未配置此应用的 TypeLay 接入。请按接入步骤完成连接。",
        ApplicationSupportReason.RegistrationInvalid => "接入配置不完整或已失效。请修复后重新检查。",
        ApplicationSupportReason.RegistrationConflict => "检测到不属于 TypeLay 的接入配置，未覆盖或移除。请查看详情处理冲突。",
        ApplicationSupportReason.AwaitingApproval => "请在应用或浏览器中完成扩展及权限批准，然后重新检查。",
        ApplicationSupportReason.UserApprovalRequired => "组件已检查，仍需你按接入步骤确认启用；尚未连接应用。",
        ApplicationSupportReason.RestartRequired => "接入配置已更新。请保存工作并重新打开应用，然后检查连接。",
        ApplicationSupportReason.AwaitingConnection => "接入配置已注册，但尚未收到实际连接。请打开应用或浏览器的普通输入框后重新检查。",
        ApplicationSupportReason.NotImplemented => "此平台尚未实现这项接入，不能通过安装模型或反复检查启用。",
        ApplicationSupportReason.Paused => "TypeLay 已暂停，请先在常规设置中启用后再检查。",
        ApplicationSupportReason.NoFocusedField => "尚未检查目标输入框。请切回需要翻译的普通文本框试一试，再查看检查结果。",
        ApplicationSupportReason.FieldUnobserved => "尚未取得目标输入框的可靠观察结果，不能确认预览或替换能力。",
        ApplicationSupportReason.PreviewOnly => "目前仅确认显示译文；Tab 接受和直接替换尚不可用。",
        ApplicationSupportReason.NoDirectWrite => "当前输入框未提供直接替换能力，不会改写其他输入框。",
        ApplicationSupportReason.LimitedFields => "仅已检查的普通文本控件可用；密码、敏感、代码及不兼容字段继续排除。",
        ApplicationSupportReason.RegistrationFailed => "接入配置未完成。请查看详情并在处理原因后重试，原有数据会保留。",
        ApplicationSupportReason.InvalidExtensionId => "尚未识别到有效的浏览器扩展。请按接入步骤选择或启用 TypeLay 扩展，再重新检查。",
        ApplicationSupportReason.DetectionFailed => "未能完成连接检查。请查看详情并重新检查，当前不确认任何能力。",
        ApplicationSupportReason.ActionFailed => "操作未完成。请查看详情，处理原因后再试；未将这次操作标为成功。",
        _ => "还没有可靠的检查结果。请选择目标应用并检查连接。"
    };

    private static string Guidance(ApplicationSupportState state) => state switch
    {
        ApplicationSupportState.NeedsInstall => "按接入步骤安装或启用组件。安装完成不代表已连接，完成后请重新检查。",
        ApplicationSupportState.AwaitingApproval => "请自行完成应用或系统要求的批准。取消后可回到这里继续；TypeLay 不会绕过权限提示。",
        ApplicationSupportState.RestartRequired => "先保存应用中的工作，再由你重新打开应用。TypeLay 不会自动结束应用进程。",
        ApplicationSupportState.Registered => "打开目标应用，确认扩展或接入组件已启用，再到普通输入框试一试。",
        ApplicationSupportState.Connected or ApplicationSupportState.BuiltInAvailable => "到实际输入框检查预览与允许的替换。连接正常不表示应用内所有字段均可使用。",
        ApplicationSupportState.Unsupported => "这项接入仍待实现，当前不会提供无效的安装或修复操作。",
        ApplicationSupportState.Error => "查看原因和详情，再选择可用的恢复操作；修复或移除接入会保留设置、模型及个人数据。",
        _ => "检查连接只读取状态，不会自动安装、授权或修改应用配置。"
    };
}
