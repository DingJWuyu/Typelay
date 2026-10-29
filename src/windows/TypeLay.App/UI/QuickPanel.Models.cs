using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class QuickPanel
{
    private readonly SettingsWindow? _preparationHost;
    private QuickPanelPreparationState? _preparationState;
    private object? _modelActionIdentity;
    private Button? _modelAction;
    private ProgressBar? _modelProgress;
    private TextBlock? _modelProgressText;

    private void BuildModelStatus()
    {
        var body = new StackPanel { Margin = new(4, 10, 4, 10) };
        var row = _statusRow = new Grid();
        row.ColumnDefinitions.Add(new()
        {
            Width = new(15)
        });
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var dot = _statusDot = new Border { Width = 7, Height = 7, CornerRadius = new(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(dot, "QuickPanelModelStatusDot");
        row.Children.Add(dot);
        var text = _statusText = UiTheme.Text("", 11.5, true);
        AutomationProperties.SetAutomationId(text, "QuickPanelModelStatus");
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        body.Children.Add(row);
        if (_preparationHost is null)
        {
            var local = UiTheme.Text(UiTheme.L("本机", "On-device"), 10.5, true);
            Grid.SetColumn(local, 2);
            row.Children.Add(local);
        }
        else
        {
            _modelProgress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 4,
                Margin = new(0, 7, 0, 0),
                Visibility = Visibility.Collapsed,
                Template = (ControlTemplate)XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ProgressBar">
                  <Grid><Border x:Name="PART_Track" CornerRadius="2" Background="{DynamicResource Panel2}"/>
                    <Border x:Name="PART_Indicator" CornerRadius="2" HorizontalAlignment="Left" Background="{TemplateBinding Foreground}"/>
                    <Border x:Name="pending" CornerRadius="2" Width="64" HorizontalAlignment="Left" Opacity="0.5" Background="{TemplateBinding Foreground}" Visibility="Collapsed"/>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsIndeterminate" Value="True">
                      <Setter TargetName="PART_Indicator" Property="Visibility" Value="Collapsed"/>
                      <Setter TargetName="pending" Property="Visibility" Value="Visible"/>
                    </Trigger>
                    <MultiTrigger><MultiTrigger.Conditions><Condition Property="IsIndeterminate" Value="True"/><Condition Property="Tag" Value="animate"/></MultiTrigger.Conditions>
                      <MultiTrigger.EnterActions><BeginStoryboard x:Name="pulse"><Storyboard>
                        <DoubleAnimation Storyboard.TargetName="pending" Storyboard.TargetProperty="Opacity" From="0.35" To="0.8" Duration="0:0:0.8" AutoReverse="True" RepeatBehavior="Forever"/>
                      </Storyboard></BeginStoryboard></MultiTrigger.EnterActions>
                      <MultiTrigger.ExitActions><RemoveStoryboard BeginStoryboardName="pulse"/></MultiTrigger.ExitActions>
                    </MultiTrigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
                """)
            };
            AutomationProperties.SetAutomationId(_modelProgress, "QuickPanelModelProgress");
            AutomationProperties.SetName(_modelProgress, UiTheme.L("本地模型准备进度", "Local model preparation progress"));
            body.Children.Add(_modelProgress);
            _modelProgressText = UiTheme.Text("", 11.5, true);
            _modelProgressText.Margin = new(0, 5, 0, 0);
            AutomationProperties.SetAutomationId(_modelProgressText, "QuickPanelModelProgressText");
            body.Children.Add(_modelProgressText);
        }
        var border = UiTheme.Border(body, 0, "Glass");
        border.BorderThickness = new(0, 0, 0, .5);
        _body.Children.Add(border);
    }

    private void RefreshModelStatus()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshModelStatus));
            return;
        }
        if (_preparationHost is null || _statusRow is null || _statusText is null || _statusDot is null)
            return;
        var state = _preparationState = _preparationHost.ReadQuickPanelPreparation();
        bool ready = state.Phase == TranslationPreparationPhase.Idle && state.Ready;
        _statusText.Text = ready ? UiTheme.L("本地模型就绪", "Local model ready")
            : state.Phase == TranslationPreparationPhase.Preparing ? UiTheme.L("正在准备本地翻译…", "Preparing local translation…")
            : state.Phase == TranslationPreparationPhase.Pausing ? UiTheme.L("正在暂停…", "Pausing…")
            : state.Phase == TranslationPreparationPhase.Paused && state.View.PrimaryAction.Enabled ? UiTheme.L("模型准备已暂停", "Model preparation paused")
            : state.Phase == TranslationPreparationPhase.Idle && state.InventoryKnown && state.View.DefaultPackageAction is not null
                ? UiTheme.L("需要下载本地翻译模型", "Download the local translation model") : UiStrings.Translate(state.View.StatusText);
        _statusRow.ToolTip = UiStrings.Translate(state.View.StatusText + "\n" + state.View.Guidance + "\n" + state.View.CurrentModelText + "\n" + _session.ServiceStatus);
        _statusDot.SetResourceReference(Border.BackgroundProperty, state.Phase == TranslationPreparationPhase.Failed ? "Danger"
            : ready ? "Good" : state.Phase is TranslationPreparationPhase.Preparing or TranslationPreparationPhase.StartingModel ? "Warn" : "Tertiary");
        if (_modelAction is not null)
            _statusRow.Children.Remove(_modelAction);
        _modelAction = null;
        _modelActionIdentity = null;
        if (!ready && state.View.PrimaryAction.Id != TranslationPreparationAction.NextStep)
        {
            object actionIdentity = _modelActionIdentity = new object();
            object rendered = _renderIdentity;
            var action = _modelAction = UiTheme.Button(ModelActionLabel(state), () =>
            {
                if (!IsVisible || !ReferenceEquals(rendered, _renderIdentity) || !ReferenceEquals(actionIdentity, _modelActionIdentity)
                    || !ReferenceEquals(state, _preparationState) || !state.View.PrimaryAction.Enabled)
                    return;
                _modelActionIdentity = null;
                _preparationHost.RunQuickPanelPreparation(state);
                // Cancellation of a native consent dialog does not notify a
                // new phase; rebuild the action without reviving its old peer.
                RefreshModelStatus();
            });
            action.Height = 22;
            action.FontSize = 11.5;
            action.Padding = new(9, 0, 9, 0);
            action.Margin = new(7, 0, 0, 0);
            action.IsEnabled = state.View.PrimaryAction.Enabled;
            action.ToolTip = UiStrings.Translate(state.View.Guidance);
            ToolTipService.SetShowOnDisabled(action, true);
            AutomationProperties.SetAutomationId(action, "QuickPanelModelAction");
            AutomationProperties.SetHelpText(action, UiStrings.Translate(state.View.PrimaryAction.Label));
            Grid.SetColumn(action, 2);
            _statusRow.Children.Add(action);
        }
        RefreshModelProgress();
    }

    private static string ModelActionLabel(QuickPanelPreparationState state) => state.View.PrimaryAction.Id switch
    {
        TranslationPreparationAction.Pause => state.View.PrimaryAction.Enabled ? UiTheme.L("暂停", "Pause") : UiTheme.L("暂停中…", "Pausing…"),
        TranslationPreparationAction.Resume => UiTheme.L("继续", "Resume"),
        TranslationPreparationAction.Retry => UiTheme.L("重试", "Retry"),
        TranslationPreparationAction.Prepare when !state.InventoryKnown => UiTheme.L("不可用", "Unavailable"),
        TranslationPreparationAction.Prepare when state.Phase == TranslationPreparationPhase.StartingModel => UiTheme.L("载入中…", "Loading…"),
        TranslationPreparationAction.Prepare => UiTheme.L("下载", "Download"),
        _ => UiStrings.Translate(state.View.PrimaryAction.Label)
    };

    private void RefreshModelProgress()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshModelProgress));
            return;
        }
        if (_preparationHost is null || _preparationState is not { } state || _modelProgress is null || _modelProgressText is null)
            return;
        var progress = _preparationHost.ReadQuickPanelProgress(state);
        bool active = state.Phase is TranslationPreparationPhase.Preparing or TranslationPreparationPhase.Pausing or TranslationPreparationPhase.StartingModel;
        bool visible = active || progress is not null && state.Phase is TranslationPreparationPhase.Paused or TranslationPreparationPhase.Failed;
        _modelProgress.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _modelProgress.Tag = !IsVisible || UiTheme.ReduceMotion ? "static" : "animate";
        _modelProgress.IsIndeterminate = active && (progress?.Total is not > 0 || state.Phase == TranslationPreparationPhase.StartingModel);
        _modelProgress.Value = progress?.Total is > 0 ? Math.Clamp(100.0 * progress.Downloaded / progress.Total.Value, 0, 100) : 0;
        _modelProgress.SetResourceReference(ForegroundProperty, active ? "Accent" : "Tertiary");
        _modelProgressText.Text = progress is null ? "" : UiStrings.Translate(progress.Label)
            + (progress.Downloaded > 0 ? progress.Total is > 0
                ? $" · {progress.Downloaded / 1048576.0:0.0} / {progress.Total.Value / 1048576.0:0.0} MB"
                : $" · {progress.Downloaded / 1048576.0:0.0} MB" : "");
        _modelProgressText.Visibility = visible && progress is not null ? Visibility.Visible : Visibility.Collapsed;
    }
}
