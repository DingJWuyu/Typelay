using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private object _modelPageEpoch = new();
    private object? _modelPageIntent;
    private QuickPanelPreparationState? _modelPageState;
    private TextBlock? _modelTitle, _modelDetailGuidance;
    private Border? _modelDot, _modelPrimaryHost, _modelRepairHost;
    private StackPanel? _modelProgressBody;
    private Expander? _modelDetails;

    private void InitializeModelPageLifetime()
    {
        IsVisibleChanged += (_, _) =>
        {
            _modelPageEpoch = new();
            _modelPageIntent = null;
            if (_progress is not null)
                _progress.Tag = "static";
            if (IsVisible && _page is "model" or "translation")
                RefreshPreparation();
        };
        SystemParameters.StaticPropertyChanged += ModelMotionChanged;
        Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= ModelMotionChanged;
            RetireModelPage();
        };
    }

    private void ModelMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation))
            return;
        Dispatcher.BeginInvoke(new Action(RefreshModelPageProgress));
    }

    private void RetireModelPage()
    {
        _modelPageEpoch = new();
        _modelPageIntent = null;
        _modelPageState = null;
        if (_progress is not null)
            _progress.Tag = "static";
        _modelTitle = _modelDetailGuidance = null;
        _modelDot = _modelPrimaryHost = _modelRepairHost = null;
        _modelProgressBody = null;
        _modelDetails = null;
    }

    private bool CurrentModelPage(object epoch, QuickPanelPreparationState state) => IsVisible
        && _page is "model" or "translation" && ReferenceEquals(epoch, _modelPageEpoch)
        && ReferenceEquals(state, _modelPageState) && IsCurrentQuickPanelPreparation(state);

    private void RunModelPageIntent(QuickPanelPreparationState state, Action<Func<bool>> action)
    {
        object epoch = _modelPageEpoch;
        if (_modelPageIntent is not null || !CurrentModelPage(epoch, state))
            return;
        object intent = _modelPageIntent = new();
        bool Current() => ReferenceEquals(intent, _modelPageIntent) && CurrentModelPage(epoch, state);
        try { action(Current); }
        finally
        {
            // The native consent pumps the dispatcher. A hidden/reopened page
            // owns a different intent; an old completion may not clear it.
            if (ReferenceEquals(intent, _modelPageIntent))
                _modelPageIntent = null;
            if (ReferenceEquals(epoch, _modelPageEpoch))
                RefreshPreparation();
        }
    }

    private void Translation()
    {
        Heading(UiTheme.L("本地模型", "Local Model"));
        BeginGroup();
        _preparationCard = (Border)_content.Children[_content.Children.Count - 1];
        _preparationCard.Name = "TranslationPreparationCard";
        AutomationProperties.SetName(_preparationCard, UiTheme.L("准备翻译", "Prepare Translation"));
        var hero = new Grid { MinHeight = 72, Margin = new(14, 0, 14, 0) };
        hero.ColumnDefinitions.Add(new() { Width = new(58) });
        hero.ColumnDefinitions.Add(new());
        hero.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        hero.Children.Add(ModelChip());
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 8, 14, 8) };
        _modelTitle = UiTheme.Text("", 15, bold: true);
        labels.Children.Add(_modelTitle);
        var status = new Grid { Margin = new(0, 3, 0, 0) };
        status.ColumnDefinitions.Add(new() { Width = new(13) });
        status.ColumnDefinitions.Add(new());
        _modelDot = new Border { Width = 7, Height = 7, CornerRadius = new(4), Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_modelDot, "ModelStatusDot");
        status.Children.Add(_modelDot);
        _preparationStatus = UiTheme.Text("", 12, true);
        _preparationStatus.Name = "PreparationStatus";
        Grid.SetColumn(_preparationStatus, 1);
        status.Children.Add(_preparationStatus);
        labels.Children.Add(status);
        Grid.SetColumn(labels, 1);
        hero.Children.Add(labels);
        _modelPrimaryHost = new Border { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_modelPrimaryHost, 2);
        hero.Children.Add(_modelPrimaryHost);
        _group!.Children.Add(hero);
        RegisterSearchTarget("model", hero);
        RegisterSearchTarget("preparation", _group);

        _modelProgressBody = new StackPanel { Margin = new(14, 0, 14, 12), Visibility = Visibility.Collapsed };
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, Template = UiTheme.ModelProgressTemplate(3) };
        AutomationProperties.SetAutomationId(_progress, "ModelPreparationProgress");
        AutomationProperties.SetName(_progress, UiTheme.L("本地组件准备进度", "Local component preparation progress"));
        _modelProgressBody.Children.Add(_progress);
        _progressLabel = UiTheme.Text("", 12, true);
        _progressLabel.Name = "PreparationProgress";
        _progressLabel.Margin = new(0, 7, 0, 0);
        Typography.SetNumeralAlignment(_progressLabel, FontNumeralAlignment.Tabular);
        _modelProgressBody.Children.Add(_progressLabel);
        _group.Children.Add(_modelProgressBody);
        var choice = _modelChoice = new ComboBox { MinWidth = 230, MaxWidth = 280 };
        object choiceEpoch = _modelPageEpoch;
        AutomationProperties.SetName(_modelChoice, UiTheme.L("选择本地模型", "Choose local model"));
        AutomationProperties.SetAutomationId(_modelChoice, "LocalModelSelection");
        choice.SelectionChanged += (sender, e) =>
        {
            if (_updatingModelChoice || !ReferenceEquals(sender, choice) || !ReferenceEquals(choice, _modelChoice)
                || !ReferenceEquals(choiceEpoch, _modelPageEpoch) || !IsVisible
                || choice.SelectedItem is not ComboBoxItem { Tag: ModelPackageActionView action } item
                || e.AddedItems.Count != 1 || !ReferenceEquals(e.AddedItems[0], item) || !choice.Items.Contains(item)
                || _modelPageState is not { } state)
                return;
            RefreshModelChoices(state.View);
            RunModelPageIntent(state, current => RunPackageAction(action, current));
        };
        Row(UiTheme.L("选择模型", "Select model"), "", _modelChoice);
        var updates = ModelPageButton(UiTheme.L("下载更新", "Download update"), () => { });
        updates.IsEnabled = false;
        AutomationProperties.SetAutomationId(updates, "ModelUpdateUnavailable");
        Row(UiTheme.L("模型更新", "Model updates"), UiTheme.L("尚未提供", "Not available yet"), updates);

        BeginGroup();
        var devices = new ComboBox { ItemsSource = new[] { UiTheme.L("自动", "Automatic"), "CPU", "GPU", "NPU" }, Text = UiTheme.L("尚未提供", "Not available"), IsEditable = true, IsReadOnly = true, IsEnabled = false, MinWidth = 140 };
        AutomationProperties.SetAutomationId(devices, "ModelDeviceUnavailable");
        devices.ToolTip = UiTheme.L("尚未提供设备选择；由当前运行组件管理。", "Device selection is not available; managed by the current runtime.");
        ToolTipService.SetShowOnDisabled(devices, true);
        Row(UiTheme.L("计算设备", "Compute device"), "", devices);
        var resident = UiTheme.Toggle(UiTheme.L("常驻内存", "Keep in memory"), () => false, _ => { });
        resident.IsEnabled = false;
        resident.ToolTip = UiTheme.L("尚未提供；不会改变模型生命周期。", "Not available; model lifetime is unchanged.");
        ToolTipService.SetShowOnDisabled(resident, true);
        AutomationProperties.SetAutomationId(resident, "ModelResidencyUnavailable");
        Row(UiTheme.L("常驻内存", "Keep in memory"), "", resident);
        BeginGroup();
        _modelRepairHost = new Border { VerticalAlignment = VerticalAlignment.Center };
        Row(UiTheme.L("本地组件", "Local components"), "", _modelRepairHost);
        Rule();

        _preparationGuidance = UiTheme.Text("", 12, true);
        _preparationGuidance.Name = "PreparationGuidance";
        _preparationGuidance.Margin = new(4, 0, 4, 12);
        _content.Children.Add(_preparationGuidance);
        var detailsBody = new StackPanel();
        _modelDetailGuidance = UiTheme.Text("", 12, true);
        _modelDetailGuidance.Margin = new(0, 8, 0, 8);
        detailsBody.Children.Add(_modelDetailGuidance);
        detailsBody.Children.Add(UiTheme.Text(UiTheme.L(
            "新安装默认准备 Index Translate 2B；旧模型与明确选择保留。仅在本机推理，无需自行安装 Python，也不上传输入文字。",
            "New installations prepare Index Translate 2B by default. Existing models and explicit selections are preserved. Inference stays on this device; no separate Python installation or upload of input text is needed."), 12, true));
        _preparationPackages = new StackPanel();
        detailsBody.Children.Add(_preparationPackages);
        _preparationDirections = new StackPanel { Margin = new(0, 0, 0, 8) };
        detailsBody.Children.Add(_preparationDirections);
        var runtime = ModelPageButton(UiTheme.L("微软运行库说明", "Microsoft runtime information"), () => OpenUri("https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist"));
        runtime.HorizontalAlignment = HorizontalAlignment.Left;
        detailsBody.Children.Add(runtime);
        var longer = UiTheme.Text(UiTheme.L(
            "较长文本：输入长度限制因模型而异；超过限制时会停止，请缩短选区后重试。不会在后台截断原文后给出可接受候选。换行、URL、mention 和编号采用显式结构保护。",
            "Longer text: input limits vary by model. Over-limit input is stopped; shorten the selection and try again. The source is never silently truncated to produce an acceptable candidate. Line breaks, URLs, mentions and numbering use explicit structural protection."), 12, true);
        longer.Margin = new(0, 12, 0, 0);
        AutomationProperties.SetAutomationId(longer, "ModelLongTextGuidance");
        detailsBody.Children.Add(longer);
        _modelDetails = new Expander { Header = UiTheme.L("组件与安装说明", "Components and installation"), Content = detailsBody };
        AutomationProperties.SetAutomationId(_modelDetails, "ModelComponentDetails");
        _content.Children.Add(_modelDetails);
        _preparationDiagnostics = new StackPanel();
        _content.Children.Add(_preparationDiagnostics);
        RefreshPreparation();
    }

    private static Button ModelPageButton(string label, Action action)
    {
        var button = UiTheme.Button(label, action);
        button.Height = 26;
        button.FontSize = 12.5;
        button.FontWeight = FontWeights.Medium;
        button.Padding = new(12, 0, 12, 0);
        return button;
    }

    private static Border ModelChip()
    {
        var grid = new Grid();
        var glyph = PrototypeGlyph("M8.5,6 L15.5,6 Q18,6 18,8.5 L18,15.5 Q18,18 15.5,18 L8.5,18 Q6,18 6,15.5 L6,8.5 Q6,6 8.5,6 M9.5,2.5 V6 M14.5,2.5 V6 M9.5,18 V21.5 M14.5,18 V21.5 M2.5,9.5 H6 M2.5,14.5 H6 M18,9.5 H21.5 M18,14.5 H21.5", stroke: 1.6);
        glyph.Width = glyph.Height = 26.4;
        grid.Children.Add(glyph);
        grid.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(89, 255, 255, 255)), BorderThickness = new(0, 1, 0, 0), CornerRadius = new(11), IsHitTestVisible = false });
        var chip = new Border { Width = 44, Height = 44, CornerRadius = new(11), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(Color.FromRgb(79, 174, 119), Color.FromRgb(42, 122, 75), 70), Child = grid,
            Effect = new DropShadowEffect { Color = Color.FromRgb(30, 100, 60), Opacity = .3, BlurRadius = 6, ShadowDepth = 2, Direction = 270 } };
        AutomationProperties.SetAutomationId(chip, "ModelHeroChip");
        return chip;
    }

    private bool ModelSelectionIsReady(QuickPanelPreparationState state)
    {
        if (!state.Ready || !state.InventoryKnown || state.Phase != TranslationPreparationPhase.Idle)
            return false;
        var current = state.View.PackageRows.Where(row => row.Current).ToArray();
        if (current.Length == 0)
            return false;
        if (_model.AvailablePackages.Count > 0)
            return _model.AvailablePackages.Count == current.Length && current.All(row => row.Ready
                && row.CurrentDirectionIds.Order(StringComparer.Ordinal).SequenceEqual(row.ReadyDirectionIds.Order(StringComparer.Ordinal)));
        // Older Argos hello has directions, but no package/version claim.
        return current.All(row => row.Id.StartsWith("argos-", StringComparison.Ordinal))
            && current.SelectMany(row => row.CurrentDirectionIds).Order(StringComparer.Ordinal)
                .SequenceEqual(_model.AvailableDirections.Order(StringComparer.Ordinal));
    }

    private void RefreshModelPage()
    {
        if (_modelTitle is null || _modelPrimaryHost is null || _modelDot is null || _modelRepairHost is null
            || _preparationStatus is null || _preparationGuidance is null)
            return;
        var state = _modelPageState = ReadQuickPanelPreparation();
        object epoch = _modelPageEpoch;
        bool ready = ModelSelectionIsReady(state);
        var selected = state.View.PackageRows.Where(row => row.Current).ToArray();
        var recommended = state.InventoryKnown ? state.View.DefaultPackageAction?.Request.PackageIds.FirstOrDefault() ?? ModelCatalog.Packages.DefaultPackage?.Id : null;
        var package = selected.FirstOrDefault() ?? state.View.PackageRows.FirstOrDefault(row => row.Id == recommended);
        _modelTitle.Text = selected.Length > 1 ? "Argos Translate" : package is not null ? ModelCopy(package.Title) : ModelCopy(state.View.CurrentModelText);
        bool mismatch = state.InventoryKnown && state.Ready && state.Phase == TranslationPreparationPhase.Idle && !ready;
        string originalStatus = mismatch ? UiTheme.L("本地翻译尚未就绪", "Local translation is not ready") : ModelCopy(state.View.StatusText);
        _preparationStatus.Text = ready ? UiTheme.L("就绪", "Ready")
            : state.Phase == TranslationPreparationPhase.Idle && state.InventoryKnown && selected.Length == 0 ? UiTheme.L("未下载", "Not downloaded") : originalStatus;
        AutomationProperties.SetName(_preparationStatus, originalStatus);
        _preparationStatus.ToolTip = null;
        AutomationProperties.SetHelpText(_preparationStatus, "");
        if (ready && selected.Length == 1 && _model.VerifiedIdentity is not null && selected[0].Ready)
        {
            var active = selected[0];
            _preparationStatus.Text += " · " + active.Version;
            if (active.DownloadBytes is { } bytes)
            {
                _preparationStatus.Text += " · " + UiTheme.L("下载大小 ", "Download size ") + ModelDownloadSize(bytes);
                string exact = UiTheme.L("下载大小：", "Download size: ") + ExactBytes(bytes);
                _preparationStatus.ToolTip = exact;
                AutomationProperties.SetHelpText(_preparationStatus, exact);
            }
        }
        _modelDot.SetResourceReference(Border.BackgroundProperty, state.Phase == TranslationPreparationPhase.Failed ? "Danger"
            : ready ? "Good" : state.Phase is TranslationPreparationPhase.Preparing or TranslationPreparationPhase.StartingModel ? "Warn" : "Tertiary");
        _preparationGuidance.Text = mismatch ? UiTheme.L("已加载模型与当前选择不一致，请查看组件状态。", "The loaded model differs from the current selection. Check component status.") : ModelCopy(state.View.Guidance);
        _preparationGuidance.Visibility = ready || _preparationGuidance.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_modelDetailGuidance is not null)
            _modelDetailGuidance.Text = _preparationGuidance.Text;
        var primary = state.View.PrimaryAction;
        _preparationPrimary = ModelPageButton(ModelPageActionLabel(state), () =>
        {
            if (!CurrentModelPage(epoch, state) || !primary.Enabled || primary.Id == TranslationPreparationAction.NextStep)
                return;
            RunModelPageIntent(state, current => RunPreparationAction(primary, current));
        });
        _preparationPrimary.Name = "PreparationPrimaryAction";
        _preparationPrimary.IsEnabled = primary.Enabled && primary.Id != TranslationPreparationAction.NextStep;
        _preparationPrimary.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        _preparationPrimary.ToolTip = ModelCopy(state.View.Guidance);
        ToolTipService.SetShowOnDisabled(_preparationPrimary, true);
        _modelPrimaryHost.Child = _preparationPrimary;
        var repairs = selected.SelectMany(row => row.Actions).Where(action => action.Request.Operation == ModelPackageOperation.Repair).ToArray();
        var repair = ModelPageButton(UiTheme.L("修复…", "Repair…"), () =>
        {
            if (!CurrentModelPage(epoch, state) || _modelPageIntent is not null || _install is not null || _packageRequest is not null)
                return;
            if (repairs.Length == 1 && repairs[0].Enabled)
            {
                RunModelPageIntent(state, current => RunPackageAction(repairs[0], current));
            }
            else if (repairs.Any(action => action.Enabled) && _modelDetails is not null)
            {
                _preparationGuidance.Text = UiTheme.L("请选择要修复的组件。", "Choose a component to repair.");
                _preparationGuidance.Visibility = Visibility.Visible;
                _modelDetails.IsExpanded = true;
                _modelDetails.BringIntoView();
            }
        });
        repair.IsEnabled = repairs.Any(action => action.Enabled) && _install is null && _packageRequest is null;
        AutomationProperties.SetAutomationId(repair, "ModelRepairAction");
        _modelRepairHost.Child = repair;
        RefreshModelChoices(state.View);
        RefreshPreparationDirections(state.View);
        RefreshPackageRows(state.View);
        RefreshModelPageProgress();
    }

    private static string ModelPageActionLabel(QuickPanelPreparationState state) => state.View.PrimaryAction.Id switch
    {
        TranslationPreparationAction.Pause => state.View.PrimaryAction.Enabled ? UiTheme.L("暂停", "Pause") : UiTheme.L("暂停中…", "Pausing…"),
        TranslationPreparationAction.Resume => UiTheme.L("继续", "Resume"),
        TranslationPreparationAction.Retry => UiTheme.L("重试", "Retry"),
        TranslationPreparationAction.Prepare when !state.InventoryKnown => UiTheme.L("不可用", "Unavailable"),
        TranslationPreparationAction.Prepare when state.Phase == TranslationPreparationPhase.StartingModel => UiTheme.L("载入中…", "Loading…"),
        TranslationPreparationAction.Prepare => UiTheme.L("下载", "Download"),
        TranslationPreparationAction.NextStep => UiTheme.L("尚未就绪", "Not ready"),
        _ => ModelCopy(state.View.PrimaryAction.Label)
    };

    private void RefreshModelPageProgress()
    {
        if (_modelPageState is not { } state || _modelProgressBody is null || _progress is null || _progressLabel is null)
            return;
        var progress = ReadQuickPanelProgress(state);
        bool active = state.Phase is TranslationPreparationPhase.Preparing or TranslationPreparationPhase.Pausing or TranslationPreparationPhase.StartingModel;
        bool visible = active || progress is not null && state.Phase is TranslationPreparationPhase.Paused or TranslationPreparationPhase.Failed;
        _modelProgressBody.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _progress.Tag = IsVisible && !UiTheme.ReduceMotion ? "animate" : "static";
        _progress.IsIndeterminate = active && (progress?.Total is not > 0 || state.Phase == TranslationPreparationPhase.StartingModel);
        _progress.Value = progress?.Total is > 0 ? Math.Clamp(100.0 * progress.Downloaded / progress.Total.Value, 0, 100) : 0;
        _progress.SetResourceReference(ForegroundProperty, active ? "Accent" : "Tertiary");
        _progressLabel.Text = progress is null ? "" : ModelCopy(progress.Label) + (progress.Downloaded > 0
            ? " · " + (progress.Total is > 0 ? ModelDownloadCount(progress.Downloaded, progress.Total.Value) : ModelDownloadSize(progress.Downloaded)) : "");
        string exact = progress is null ? "" : UiTheme.L("已下载：", "Downloaded: ") + ExactBytes(progress.Downloaded)
            + UiTheme.L("；总量：", "; total: ") + (progress.Total is { } total ? ExactBytes(total) : UiTheme.L("未知", "unknown"));
        _progressLabel.ToolTip = exact;
        AutomationProperties.SetHelpText(_progressLabel, exact);
    }

    private static string ExactBytes(long bytes) => bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
    private static (double Divisor, string Unit) ModelDownloadUnit(long bytes) => bytes >= 1_000_000_000 ? (1_000_000_000, "GB")
        : bytes >= 1_000_000 ? (1_000_000, "MB") : bytes >= 1_000 ? (1_000, "KB") : (1, "bytes");
    private static string ModelDownloadSize(long bytes)
    {
        var (divisor, unit) = ModelDownloadUnit(bytes);
        return (divisor == 1 ? bytes.ToString(CultureInfo.InvariantCulture) : (bytes / divisor).ToString(divisor >= 1_000_000_000 ? "0.00" : "0.0", CultureInfo.InvariantCulture)) + " " + unit;
    }
    private static string ModelDownloadCount(long downloaded, long total)
    {
        var (divisor, unit) = ModelDownloadUnit(total);
        string format = divisor == 1 ? "0" : "0.0";
        return (downloaded / divisor).ToString(format, CultureInfo.InvariantCulture) + " / " + (total / divisor).ToString(format, CultureInfo.InvariantCulture) + " " + unit;
    }

    // Only product-owned catalogue/status copy reaches this function, never a
    // user's source, translation, glossary rule or model output.
    private static string ModelCopy(string value)
    {
        if (!UiTheme.English)
            return value;
        string result = UiStrings.Translate(value);
        foreach (var (chinese, english) in new (string, string)[]
        {
            ("当前模型无法确认", "The current model cannot be confirmed"),
            ("推荐默认", "Recommended default"), ("中文多语", "Chinese multilingual"),
            ("当前 · 旧版方向已加载，包身份未验证", "Current · Legacy directions loaded; package identity is unverified"),
            ("当前 · 已就绪", "Current · Ready"), ("当前 · 尚未就绪", "Current · Not ready"),
            ("安装状态无法确认", "Installation status cannot be confirmed"),
            ("当前安装包不含可用的 Index 运行组件", "This installation has no usable Index runtime components"),
            ("旧安装尚未提供精确包身份，可明确选择修复以升级安装记录。", "The legacy installation does not report exact package identity. Explicitly select Repair to upgrade its installation records."),
            ("旧组件退出或清理尚未确认，暂保留旧副本，未释放对应磁盘空间。", "Exit or cleanup of the old components is not confirmed. Their copies and disk space are retained."),
            ("预览版可能出现漏译或表达偏差，请核对译文。仅支持列出的中文双向直译，不会经英语中转。模型许可证随包保留。", "This preview can omit or mistranslate text. Check the result. Only the listed direct Chinese language pairs are supported, without an English pivot. The model license is retained with the package."),
            ("中英预览暂不支持风格和术语表；可能出现错译或漏译，请核对译文。", "The Chinese-English preview does not support tone or glossary. It can mistranslate or omit text; check the result."),
            ("推荐默认的本地翻译模型。仅支持列出的十四个已有方向；译文可能有误，请核对。输入内容不会上传，Apache-2.0 模型许可证随包保留。", "The recommended local translation model supports only the fourteen listed directions. Results can be incorrect; check them. Input is not uploaded, and the Apache-2.0 model license is retained."),
            ("翻译在本机运行，输入内容不会上传。模型许可证随包保留。", "Translation runs on this device. Input is not uploaded, and the model license is retained."),
            ("本次操作需要复制保留的模型和运行组件。旧副本未确认退出时会继续保留，不会立即释放磁盘空间。", "This operation copies retained models and runtime components. Old copies are retained until their exit is confirmed; their disk space is not immediately released."),
            ("缺少的私有运行组件由 ", "Missing private runtime components are prepared from "),
            (" 准备。安装还需要运行组件、缓存和切换副本空间，开始前会检查可用空间。现有模型保留；语言设置不变。", ". Installation also needs space for the runtime, cache and switching copies. Free space is checked before starting. Existing models and language settings are preserved."),
            ("这是预览版，请核对译文。", "This is a preview; check the translation."),
            ("翻译在本机运行，输入内容不会上传。", "Translation runs on this device; input is not uploaded."),
            ("本次默认安装十二方向，不包含中英方向；之后可单独确认补充。", "This installs twelve directions by default, excluding Chinese-English. Those can be added later with separate confirmation."),
            ("。切换时翻译会短暂不可用；不会下载模型，语言设置不变。", ". Translation is briefly unavailable while switching. No model is downloaded; language settings are preserved."),
            ("，可能需要下载。当前模型选择和语言设置保持不变。", "; downloading may be needed. The current model selection and language settings are preserved."),
            (" 的已安装副本。当前模型继续保留；以后使用时需要重新下载。", " from the installed packages. The current model is retained. Future use requires a new download."),
            (" 补充中文 → English 和 English → 中文。", " with Chinese → English and English → Chinese."),
            ("复用已验证的本地权重，不会下载模型，也不会修复运行组件。", "Verified local weights are reused. This does not download a model or repair runtime components."),
            ("当前模型选择和语言设置保持不变；新增方向仍须模型加载验证后才能使用。取消不会更改已安装方向。", "The current model and language settings are preserved. Added directions remain unavailable until model loading verifies them. Cancelling leaves installed directions unchanged."),
            ("启用中英预览后不支持直接降级旧版本；可在当前版本切回 Argos 继续使用；若未安装 Argos，需先另行确认下载。", "Direct downgrade is not supported after enabling this preview. You can switch back to Argos in this version; if it is absent, a separate download confirmation is required."),
            ("将为已安装的 ", "Extend the installed "), ("将重新准备 ", "Prepare again: "),
            ("将从 ", "Download from "), (" 下载并使用 ", " and use "), ("将切换到 ", "Switch to "),
            ("将移除 ", "Remove "), ("下载约 ", "Download approximately "),
            ("目录可用：", "Available in the catalogue: "), ("已安装：", "Installed: "),
            (" · 预览", " · Preview"), (" GB 下载", " GB download"),
            ("错误类别：", "Error category: "), ("安装阶段：", "Installation stage: "),
            ("恢复阻塞：", "Recovery blocked: "), ("切换阶段：", "Switch phase: "),
            ("切换原因：", "Switch reason: "), ("发布确认：", "Publication: "),
            ("发布操作：", "Publication operation: "), ("发布错误：", "Publication error: ")
        })
            result = result.Replace(chinese, english, StringComparison.Ordinal);
        foreach (var language in ModelCatalog.Packages.Packages.SelectMany(package => package.Directions)
            .SelectMany(direction => new[] { (direction.SourceName, direction.Source), (direction.TargetName, direction.Target) }).Distinct())
            result = result.Replace(language.Item1, LanguageChoicePresentation.Create(language.Item2).LabelEn, StringComparison.Ordinal);
        return result.Replace("、", ", ", StringComparison.Ordinal).Replace("。", ". ", StringComparison.Ordinal);
    }
}
