namespace TypeLay.Runtime;

public static class ApplicationSupportPresentation
{
    public static TranslationPreparationView Preparation(TranslationPreparationFact facts,
        TranslationPreparationNextStep nextStep = TranslationPreparationNextStep.TryIt)
    {
        var view = PreparationCore(facts, nextStep);
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
