using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using TypeLay.Core;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private bool _generalPageHidden;

    private void GeneralVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!IsVisible)
        {
            _generalPageHidden = true;
            _generalModeVisibility++;
            RetireGeneralModeThumb();
        }
        else if (_generalPageHidden && _page == "general")
            Refresh(); // A direct Hide/Show also creates new controls, never revives old peers.
    }

    private void General()
    {
        object page = _pageIdentity;
        object? consent = null;
        _generalPageHidden = false;
        IsVisibleChanged -= GeneralVisibilityChanged;
        IsVisibleChanged += GeneralVisibilityChanged;
        SizeChanged -= GeneralModeWindowSizeChanged;
        SizeChanged += GeneralModeWindowSizeChanged;
        bool CurrentPage() => IsVisible && !_generalPageHidden && ReferenceEquals(page, _pageIdentity)
            && _page == "general" && !_emergencyShutdown && _closeDialog is null;
        bool CanChange() => CurrentPage() && consent is null;
        Heading(UiTheme.L("通用", "General"));
        BeginGroup();
        AutomationProperties.SetAutomationId(_group!, "GeneralServiceGroup");
        var hero = new Grid { Margin = new(14, 12, 14, 12), MinHeight = 48 };
        hero.ColumnDefinitions.Add(new()
        {
            Width = new(58)
        });
        hero.ColumnDefinitions.Add(new());
        hero.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        hero.Children.Add(UiTheme.Mark(44, appIcon: true));
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(UiTheme.Text("TypeLay", 15, bold: true));
        var statusLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 3, 0, 0) };
        var statusDot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(statusDot, "GeneralTranslationStatusDot");
        statusLine.Children.Add(statusDot);
        var status = UiTheme.Text("", 12, true);
        AutomationProperties.SetAutomationId(status, "GeneralTranslationStatus");
        _refreshGeneralStatus = () =>
        {
            statusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                !_settings.Value.Enabled ? "Tertiary" : _model.Ready ? "Good" : "Warn");
            // Readiness describes the currently usable model. A separate failed
            // preparation must not replace the working model's Running state.
            status.Text = !_settings.Value.Enabled ? UiTheme.L("已暂停", "Paused")
                : _model.Ready ? UiTheme.L("运行中", "Running") : UiTheme.L("模型未就绪", "Model not ready");
            status.ToolTip = UiStrings.Translate(_model.Status);
            AutomationProperties.SetHelpText(status, UiStrings.Translate(_model.Status));
        };
        _refreshGeneralStatus();
        statusLine.Children.Add(status);
        identity.Children.Add(statusLine);
        Grid.SetColumn(identity, 1);
        hero.Children.Add(identity);
        var enabled = UiTheme.Toggle(UiTheme.L("启用 TypeLay", "Enable TypeLay"), () => _settings.Value.Enabled,
            async value =>
            {
                if (CanChange())
                    await SaveAsync(_settings.Value with
                    {
                        Enabled = value
                    }, callerCurrent: CanChange);
            });
        AutomationProperties.SetAutomationId(enabled, "GeneralEnable");
        Grid.SetColumn(enabled, 2);
        hero.Children.Add(enabled);
        _group!.Children.Add(hero);
        RegisterSearchTarget("enable", hero);

        BeginGroup();
        AutomationProperties.SetAutomationId(_group!, "GeneralPreferencesGroup");
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        var modeChoices = new List<RadioButton>();
        var modeStyle = GeneralModeStyle();
        foreach (var (mode, zh, en) in new[] { (WorkMode.Automatic, "自动", "Automatic"), (WorkMode.OnDemand, "按需", "On demand") })
        {
            var choice = new RadioButton
            {
                Content = UiTheme.L(zh, en),
                GroupName = "SettingsWorkMode",
                Style = modeStyle,
                IsChecked = _settings.Value.Mode == mode,
                IsTabStop = _settings.Value.Mode == mode
            };
            AutomationProperties.SetName(choice, UiTheme.L("工作模式 " + zh, "Mode " + en));
            AutomationProperties.SetAutomationId(choice, "GeneralMode_" + mode);
            choice.Checked += async (_, _) =>
            {
                if (CanChange())
                    await ChangeGeneralModeAsync(mode, CanChange);
            };
            modeChoices.Add(choice);
            modes.Children.Add(choice);
        }
        modes.PreviewKeyDown += (_, e) =>
        {
            if (!CanChange() || Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is not RadioButton focused)
                return;
            int step = e.Key is Key.Right or Key.Down ? 1 : e.Key is Key.Left or Key.Up ? -1 : 0;
            int index = modeChoices.IndexOf(focused);
            if (step == 0 || index < 0)
                return;
            e.Handled = true;
            var next = modeChoices[(index + step + modeChoices.Count) % modeChoices.Count];
            if (next.Focus())
                next.IsChecked = true;
        };
        var track = UiTheme.Border(GeneralModeSurface(modes, modeChoices, CurrentPage), 8, "SettingsControlFill", 2);
        track.BorderThickness = new(0);
        track.Height = 28;
        track.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(track, "GeneralModeTrack");
        Row(UiTheme.L("工作模式", "Mode"), "", track);
        var startup = UiTheme.Toggle(UiTheme.L("登录时启动", "Open at login"), () => _settings.Value.Startup,
            async value =>
            {
                if (CanChange())
                    await SaveAsync(_settings.Value with
                    {
                        Startup = value
                    }, callerCurrent: CanChange);
            });
        startup.ToolTip = UiTheme.L("登录 Windows 后自动启动 TypeLay。", "Open TypeLay automatically after signing in to Windows.");
        AutomationProperties.SetAutomationId(startup, "GeneralStartup");
        AutomationProperties.SetHelpText(startup, (string)startup.ToolTip);
        Row(UiTheme.L("登录时启动", "Open at login"), "", startup);
        var writeback = UiTheme.Toggle(UiTheme.L("原生输入框替换", "Native field replacement"), () => _settings.Value.ExperimentalWriteback, async value =>
        {
            if (!CanChange())
                return;
            if (!value)
            {
                await SaveAsync(_settings.Value with
                {
                    ExperimentalWriteback = false
                }, callerCurrent: CanChange);
                return;
            }
            object intent = consent = new();
            long revision = _settings.Revision;
            bool CurrentConsent() => CurrentPage() && ReferenceEquals(consent, intent) && _settings.Revision == revision;
            try
            {
                var answer = MessageBox.Show(this, UiTheme.L(
                    "此构建未完成 Windows 实机与真实输入法验收。\n\nUIA 校验与写回之间仍有竞态：极端情况下可能覆盖最新原文，或拒写后消耗一次 Tab。该开关仅用于受控测试，不代表安全验收通过。\n\n请只用已备份的测试文本。是否允许实验性 Tab 写回？",
                    "This build has not completed validation on Windows hardware and real input methods.\n\nA race remains between UIA validation and writeback: in extreme cases it may overwrite the latest source text, or consume one Tab even when the write is refused. This switch is for controlled testing only, not evidence of safety approval.\n\nUse only backed-up test text. Allow experimental Tab writeback?"),
                    UiTheme.L("TypeLay · 实验性写回", "TypeLay · Experimental writeback"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer == MessageBoxResult.Yes && CurrentConsent())
                    await SaveAsync(_settings.Value with
                    {
                        ExperimentalWriteback = true
                    }, callerCurrent: CurrentConsent);
            }
            finally
            {
                if (ReferenceEquals(consent, intent))
                    consent = null;
            }
        });
        AutomationProperties.SetAutomationId(writeback, "GeneralWriteback");
        writeback.ToolTip = UiTheme.L("实验性 Tab 写回，仍需逐次通过输入法、焦点和原文校验。", "Experimental Tab writeback still requires input-method, focus and source validation for every acceptance.");
        AutomationProperties.SetHelpText(writeback, (string)writeback.ToolTip);
        Row(UiTheme.L("原生输入框替换", "Native field replacement"), "", writeback, badge: "Beta");

        BeginGroup();
        AutomationProperties.SetAutomationId(_group!, "GeneralLearningGroup");
        var tutorial = GeneralPageButton(UiTheme.L("打开", "Open"), () => { if (CanChange()) StartPractice(); });
        AutomationProperties.SetAutomationId(tutorial, "GeneralTutorial");
        Row(UiTheme.L("使用教程", "Tutorial"), "", tutorial);
        var setup = GeneralPageButton(UiTheme.L("重新运行", "Run again"), () =>
        {
            if (!CanChange())
                return;
            if (ShowOnboarding is { } show)
                show();
            else
                ShowPage("model");
        });
        AutomationProperties.SetAutomationId(setup, "GeneralFirstRunSetup");
        Row(UiTheme.L("首次设置", "First-run setup"), "", setup);
        _practiceStatus = UiTheme.Text("", 12, true);
        AutomationProperties.SetAutomationId(_practiceStatus, "GeneralPracticeProgress");
        _content.Children.Add(_practiceStatus);
        RefreshPracticeProgress();
    }

    // Translate only the finite product notices returned by the existing lesson
    // presentation. Unknown notices remain unchanged, never guessed as success.
    private string GeneralPracticeNotice(string message)
    {
        if (!UiTheme.English)
            return message;
        string? translated = message switch
        {
            "开始练习后，这里会显示进度。" => "Progress will appear here after you start the tutorial.",
            "请点击练习输入框，等待原生连接确认。" => "Click the practice field and wait for its native connection to be confirmed.",
            "完整练习已完成：已验证自动接受和编辑后接受。" => "Tutorial completed: automatic acceptance and acceptance after editing were verified.",
            "请实际修改浮层中的译文。" => "Make a change to the translation in the overlay.",
            "已验证第一次接受。请在原输入框输入新的原文，等待译文后进入编辑。" => "The first acceptance was verified. Enter new source text, wait for the translation, then edit it.",
            "已识别练习输入框，等待自动生成预览。" => "The practice field was identified. Waiting for an automatic preview.",
            "练习输入框尚未连接，请点击输入框；若仍未连接，请检查应用支持。" => "The practice field is not connected. Click it; if it remains disconnected, check application support.",
            "按需练习已完成：已生成预览，并已验证直接替换。" => "On-demand tutorial completed: a preview was generated and direct replacement was verified.",
            "已生成预览。请回到练习输入框，等待直接替换快捷键就绪。" => "A preview was generated. Return to the practice field and wait for the direct replacement shortcut to be ready.",
            "已生成预览。请回到练习输入框，完成第 2 步直接替换。" => "A preview was generated. Return to the practice field and complete step 2: direct replacement.",
            "已识别练习输入框，正在观察；请等待预览快捷键就绪。" => "The practice field was identified and is being observed. Wait for the preview shortcut to be ready.",
            "已识别练习输入框，尚未生成预览。" => "The practice field was identified. No preview has been generated yet.",
            _ => null
        };
        if (translated is not null)
            return translated;
        string accept = _settings.Value.AcceptShortcut, direct = _settings.Value.DirectShortcut, wake = _settings.Value.WakeShortcut;
        if (message == $"已回到原输入框，原文未变。请再按 {accept} 接受修改后的译文。")
            return $"Returned to the source field without changing it. Press {accept} again to accept the edited translation.";
        if (message == $"已修改译文。请按 {accept} 回到原输入框，此次不会写入。")
            return $"The translation was edited. Press {accept} to return to the source field without writing this time.";
        if (message == $"已生成预览。请在练习输入框按 {accept} 接受。")
            return $"A preview was generated. Press {accept} in the practice field to accept it.";
        if (message == $"已生成预览。直接替换快捷键已就绪，可按 {direct} 完成第 2 步。")
            return $"A preview was generated and the direct replacement shortcut is ready. Press {direct} to complete step 2.";
        if (message == $"预览快捷键已就绪，可按 {wake} 查看译文。")
            return $"The preview shortcut is ready. Press {wake} to see the translation.";
        return message;
    }

    private static Button GeneralPageButton(string label, Action action)
    {
        var button = UiTheme.Button(label, action);
        button.Height = button.MinHeight = 26;
        button.FontSize = 12.5;
        button.FontWeight = FontWeights.Medium;
        button.Padding = new(12, 0, 12, 0);
        return button;
    }

    private static Style GeneralModeStyle() => (Style)XamlReader.Parse("""
    <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton">
      <Setter Property="Height" Value="24"/><Setter Property="MinHeight" Value="24"/><Setter Property="MinWidth" Value="56"/>
      <Setter Property="Padding" Value="12,0"/><Setter Property="FontSize" Value="12"/><Setter Property="FontWeight" Value="Medium"/>
      <Setter Property="Foreground" Value="{DynamicResource Muted}"/><Setter Property="FontFamily" Value="{DynamicResource UiFont}"/>
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="Cursor" Value="Hand"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
        <Border x:Name="surface" CornerRadius="6" Background="Transparent" Padding="{TemplateBinding Padding}" BorderThickness="0.5" BorderBrush="Transparent">
          <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsChecked" Value="True"><Setter Property="Foreground" Value="{DynamicResource Text}"/></Trigger>
          <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="surface" Property="BorderBrush" Value="{DynamicResource SettingsLink}"/></Trigger>
          <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    """);
}
