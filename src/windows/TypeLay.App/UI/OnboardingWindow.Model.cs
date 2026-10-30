using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Media.Effects;
using WpfPath = System.Windows.Shapes.Path;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class OnboardingWindow
{
    private Action? _retireOnboardingModel;
    private object? _onboardingModelIntent;

    private void RetireOnboardingModel()
    {
        var retire = _retireOnboardingModel;
        _retireOnboardingModel = null;
        retire?.Invoke();
    }

    private void Model()
    {
        object page = _pageIdentity;
        long navigation = _navigationVersion;
        var state = _settingsWindow.ReadQuickPanelPreparation();
        var view = state.View;
        bool retired = false;
        bool Current() => !retired && CurrentPage(page) && navigation == _navigationVersion && _step == 3 && !_busy;
        bool ready = _settingsWindow.ModelSelectionIsReady(state);
        Heading(UiTheme.L("下载本地翻译模型", "Download the translation model"));
        var card = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        top.ColumnDefinitions.Add(new());
        top.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var chip = OnboardingModelChip();
        chip.Margin = new(0, 0, 12, 0);
        top.Children.Add(chip);
        var selected = view.PackageRows.Where(row => row.Current).ToArray();
        string? recommended = state.InventoryKnown ? view.DefaultPackageAction?.Request.PackageIds.FirstOrDefault()
            ?? ModelCatalog.Packages.DefaultPackage?.Id : null;
        var package = selected.FirstOrDefault() ?? view.PackageRows.FirstOrDefault(row => row.Id == recommended);
        var title = UiTheme.Text(selected.Length > 1 ? "Argos Translate"
            : SettingsWindow.ModelCopy(package?.Title ?? view.CurrentModelText), 13, bold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new(0, 0, 12, 0);
        AutomationProperties.SetAutomationId(title, "OnboardingModelTitle");
        Grid.SetColumn(title, 1);
        top.Children.Add(title);

        void Run(TranslationPreparationActionView action)
        {
            if (!Current() || !action.Enabled || _onboardingModelIntent is not null)
                return;
            object intent = _onboardingModelIntent = new();
            try
            {
                _settingsWindow.RunOnboardingPreparation(state, action,
                    () => Current() && ReferenceEquals(_onboardingModelIntent, intent));
            }
            finally
            {
                if (ReferenceEquals(_onboardingModelIntent, intent))
                    _onboardingModelIntent = null;
                // A cancelled native consent retires its old action too. An
                // accepted install already publishes its own new phase/page.
                if (Current())
                    Refresh();
            }
        }
        string primaryLabel = view.PrimaryAction.Id switch
        {
            TranslationPreparationAction.Prepare when state.Phase == TranslationPreparationPhase.StartingModel => UiTheme.L("载入中…", "Loading…"),
            TranslationPreparationAction.Prepare => UiTheme.L("下载", "Download"),
            TranslationPreparationAction.Pause => UiTheme.L("暂停", "Pause"),
            TranslationPreparationAction.Resume => UiTheme.L("继续", "Resume"),
            TranslationPreparationAction.Retry => UiTheme.L("重试", "Retry"),
            _ => SettingsWindow.ModelCopy(view.PrimaryAction.Label)
        };
        var primary = OnboardingModelButton(primaryLabel, () => Run(view.PrimaryAction));
        primary.IsEnabled = view.PrimaryAction.Enabled && view.PrimaryAction.Id != TranslationPreparationAction.NextStep;
        primary.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        primary.ToolTip = SettingsWindow.ModelCopy(view.Guidance);
        ToolTipService.SetShowOnDisabled(primary, true);
        AutomationProperties.SetAutomationId(primary, "OnboardingModelAction");
        Grid.SetColumn(primary, 2);
        top.Children.Add(primary);
        card.Children.Add(top);
        var progress = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 100,
            Margin = new(0, 12, 0, 0),
            Template = UiTheme.ModelProgressTemplate(3, separateDeterminateFill: true)
        };
        AutomationProperties.SetAutomationId(progress, "OnboardingModelProgress");
        AutomationProperties.SetName(progress, UiTheme.L("本地模型准备状态", "Local Model Preparation Status"));
        card.Children.Add(progress);
        var fill = new OnboardingProgressFill(progress, Current);
        var meta = new Grid { Margin = new(0, 12, 0, 0) };
        meta.ColumnDefinitions.Add(new());
        meta.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var bytes = UiTheme.Text("", 12, true);
        bytes.LineHeight = 16;
        bytes.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        Typography.SetNumeralAlignment(bytes, FontNumeralAlignment.Tabular);
        AutomationProperties.SetAutomationId(bytes, "OnboardingModelBytes");
        meta.Children.Add(bytes);
        string statusText = ready ? UiTheme.L("就绪", "Ready") : state.Phase switch
        {
            TranslationPreparationPhase.Preparing => UiTheme.L("准备中", "Preparing"),
            TranslationPreparationPhase.Pausing => UiTheme.L("正在暂停", "Pausing"),
            TranslationPreparationPhase.Paused => UiTheme.L("已暂停", "Paused"),
            TranslationPreparationPhase.StartingModel => UiTheme.L("载入中…", "Loading…"),
            TranslationPreparationPhase.Failed => UiTheme.L("准备失败", "Preparation failed"),
            _ => selected.Length == 0 && state.InventoryKnown ? UiTheme.L("未下载", "Not downloaded") : UiTheme.L("尚未就绪", "Not ready")
        };
        var status = UiTheme.Text(statusText, 12, true);
        status.LineHeight = 16;
        status.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        status.Margin = new(12, 0, 0, 0);
        status.ToolTip = SettingsWindow.ModelCopy(view.StatusText);
        AutomationProperties.SetAutomationId(status, "OnboardingModelStatus");
        AutomationProperties.SetHelpText(status, (string)status.ToolTip);
        Grid.SetColumn(status, 1);
        meta.Children.Add(status);
        card.Children.Add(meta);
        var detail = UiTheme.Text("", 12, true);
        detail.LineHeight = 16;
        detail.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        detail.Margin = new(0, 12, 0, 0);
        AutomationProperties.SetAutomationId(detail, "OnboardingModelProgressDetail");
        card.Children.Add(detail);
        if (!ready && view.Guidance.Length > 0)
        {
            var guidance = UiTheme.Text(SettingsWindow.ModelCopy(view.Guidance), 12, true);
            guidance.Margin = new(0, 12, 0, 0);
            AutomationProperties.SetAutomationId(guidance, "OnboardingModelGuidance");
            card.Children.Add(guidance);
        }
        var secondary = new WrapPanel { Margin = new(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var action in view.SecondaryActions.Where(action => action.Id != TranslationPreparationAction.NextStep))
        {
            var button = OnboardingModelButton(SettingsWindow.ModelCopy(action.Label), () => Run(action));
            button.IsEnabled = action.Enabled;
            button.Margin = new(6, 0, 0, 0);
            AutomationProperties.SetAutomationId(button, "OnboardingModel_" + action.Id);
            secondary.Children.Add(button);
        }
        if (secondary.Children.Count > 0)
            card.Children.Add(secondary);
        var border = UiTheme.Border(card, 14);
        border.Padding = new(16);
        border.BorderThickness = new(0);
        border.SetResourceReference(EffectProperty, "SettingsModeThumbShadow");
        AutomationProperties.SetAutomationId(border, "OnboardingModelCard");
        var surface = new Grid();
        surface.Children.Add(border);
        var edge = new Border { CornerRadius = new(14), BorderThickness = new(.5), IsHitTestVisible = false };
        edge.SetResourceReference(Border.BorderBrushProperty, "Line");
        surface.Children.Add(edge);
        _body.Children.Add(surface);

        void UpdateProgress()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateProgress));
                return;
            }
            if (!Current())
                return;
            var value = _settingsWindow.ReadQuickPanelProgress(state);
            if (!Current())
                return;
            bool active = state.Phase is TranslationPreparationPhase.Preparing or TranslationPreparationPhase.Pausing or TranslationPreparationPhase.StartingModel;
            progress.Tag = !UiTheme.ReduceMotion ? "animate" : "static";
            if (!Current())
                return;
            progress.IsIndeterminate = active && (value?.Total is not > 0 || state.Phase == TranslationPreparationPhase.StartingModel);
            if (!Current())
                return;
            progress.Value = ready ? 100 : value?.Total is > 0 ? Math.Clamp(100.0 * value.Downloaded / value.Total.Value, 0, 100) : 0;
            if (!Current())
                return;
            progress.SetResourceReference(Control.ForegroundProperty, ready || active ? "Accent" : "Tertiary");
            if (!Current())
                return;
            bytes.Text = value is null ? "—" : value.Total is > 0
                ? SettingsWindow.ModelDownloadCount(value.Downloaded, value.Total.Value) : SettingsWindow.ModelDownloadSize(value.Downloaded);
            if (!Current())
                return;
            string exact = value is null ? "" : UiTheme.L("已下载：", "Downloaded: ") + SettingsWindow.ExactBytes(value.Downloaded)
                + UiTheme.L("；总量：", "; total: ") + (value.Total is { } total ? SettingsWindow.ExactBytes(total) : UiTheme.L("未知", "unknown"));
            bytes.ToolTip = exact;
            if (!Current())
                return;
            AutomationProperties.SetHelpText(bytes, exact);
            if (!Current())
                return;
            detail.Text = value is null ? "" : SettingsWindow.ModelCopy(value.Label);
            if (!Current())
                return;
            detail.Visibility = detail.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (Current())
                fill.Update();
        }
        void MotionChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                UpdateProgress();
        }
        // Only the current visible page owns these subscriptions. Ordinary byte
        // reports update the same controls, without stealing keyboard focus.
        if (IsVisible)
        {
            _settingsWindow.QuickPanelProgressChanged += UpdateProgress;
            SystemParameters.StaticPropertyChanged += MotionChanged;
        }
        _retireOnboardingModel = () =>
        {
            retired = true;
            _settingsWindow.QuickPanelProgressChanged -= UpdateProgress;
            SystemParameters.StaticPropertyChanged -= MotionChanged;
            fill.Dispose();
            progress.Tag = "static";
        };
        UpdateProgress();
    }

    private static Button OnboardingModelButton(string label, Action action)
    {
        var button = UiTheme.Button(label, action);
        button.Height = 26;
        button.FontSize = 12.5;
        button.FontWeight = FontWeights.Medium;
        button.Padding = new(12, 0, 12, 0);
        button.BorderThickness = new(0);
        button.SetResourceReference(Control.BackgroundProperty, "Control");
        button.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Grid>
                <Border x:Name="surface" CornerRadius="7" Background="{TemplateBinding Background}" Effect="{DynamicResource SettingsModeThumbShadow}"/>
                <Border x:Name="edge" CornerRadius="7" BorderThickness=".5" BorderBrush="{DynamicResource SettingsModeThumbEdge}" IsHitTestVisible="False"/>
                <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="surface" Property="Background" Value="{DynamicResource Fill2}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="edge" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value=".45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        return button;
    }

    private static Border OnboardingModelChip()
    {
        var glyph = new WpfPath
        {
            Data = Geometry.Parse("M8.5,6 L15.5,6 Q18,6 18,8.5 L18,15.5 Q18,18 15.5,18 L8.5,18 Q6,18 6,15.5 L6,8.5 Q6,6 8.5,6 M9.5,2.5 V6 M14.5,2.5 V6 M9.5,18 V21.5 M14.5,18 V21.5 M2.5,9.5 H6 M2.5,14.5 H6 M18,9.5 H21.5 M18,14.5 H21.5"),
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        var canvas = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false };
        canvas.Children.Add(glyph);
        var content = new Grid();
        content.Children.Add(new Viewbox { Width = 15.6, Height = 15.6, Child = canvas });
        content.Children.Add(new Border { CornerRadius = new(7), BorderThickness = new(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(89, 255, 255, 255)), IsHitTestVisible = false });
        var chip = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new(7),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(Color.FromRgb(79, 174, 119), Color.FromRgb(42, 122, 75), 70),
            Child = content,
            Effect = new DropShadowEffect { Color = Color.FromRgb(150, 60, 30), Opacity = .3, BlurRadius = 6, ShadowDepth = 2, Direction = 270 }
        };
        AutomationProperties.SetAutomationId(chip, "OnboardingModelChip");
        return chip;
    }
}
