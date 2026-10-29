using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using TypeLay.Core;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private static Button IconButton(string path)
    {
        var button = new Button { Width = 26, Height = 26, Padding = new(0), BorderThickness = new(0), Background = Brushes.Transparent };
        button.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Border x:Name="button" CornerRadius="7" Background="{TemplateBinding Background}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="button" Property="Background" Value="{DynamicResource Panel2}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="button" Property="Background" Value="{DynamicResource AccentSoft}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.35"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(path), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 15, Height = 15, Stretch = Stretch.Uniform };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Muted");
        button.Content = icon;
        return button;
    }

    private void UpdateSurfaceClip()
    {
        double radius = _shell.CornerRadius.TopLeft;
        _shell.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, _shell.ActualWidth), Math.Max(0, _shell.ActualHeight)), radius, radius);
        foreach (Border line in _skeleton.Children)
            line.Width = Math.Max(0, _body.ActualWidth - 20) * (double)line.Tag;
    }

    private static string DirectionCode(string code) => code switch
    {
        "auto" => "Auto",
        "zh" => "中",
        "en" => "EN",
        "ja" => "JA",
        "ko" => "KO",
        "fr" => "FR",
        "es" => "ES",
        "de" => "DE",
        "pt" => "PT",
        _ => code.ToUpperInvariant()
    };

    private static string Stage(SessionState state, bool hasPreview, bool previewOnly, bool unknownIme) => state switch
    {
        SessionState.Translating => hasPreview ? UiTheme.L("更新中", "Updating") : UiTheme.L("翻译中", "Translating"),
        SessionState.TranslationFailed => UiTheme.L("未能更新", "Couldn’t update"),
        SessionState.ValidTranslation or SessionState.EditedTranslationPending when previewOnly => UiTheme.L("仅预览", "Preview only"),
        SessionState.EditedTranslationPending => UiTheme.L("已修改", "Edited"),
        SessionState.Invalid when unknownIme => UiTheme.L("仅预览", "Preview only"),
        SessionState.Invalid => UiTheme.L("输入中", "Typing"),
        _ => ""
    };

    // These are messages produced by the session host, not translated source text.
    // Unknown messages retain the original rather than inventing a successful state.
    private static string LocalizedStatus(string value) => !UiTheme.English ? value : value switch
    {
        "正在翻译" => "Translating",
        "模型准备中" or "准备模型" => "Preparing model",
        "模型加载中" => "Loading model",
        "正在准备翻译模型" => "Preparing model",
        "正在重新翻译" => "Translating again",
        "暂时无法翻译" => "Couldn’t translate",
        "译文预览" or "仅预览" => "Preview only",
        "输入中" => "Typing",
        "已修改" => "Edited",
        "已复制" => "Copied",
        "复制译文" => "Copy Translation",
        "重新翻译" => "Translate Again",
        "跟随全局设置" => "Use Global Setting",
        "自然" => "Natural",
        "正式" => "Formal",
        "不要在这个应用中显示" => "Don’t Show in This App",
        "隐藏当前翻译" => "Hide for Now",
        "请先完成当前编辑" => "Finish editing first",
        "请等待当前操作完成" => "Wait for the current operation",
        "当前输入已变化，请重新打开菜单" => "The source changed. Open this menu again.",
        "未能保存，请重试" => "Couldn’t save. Try again.",
        "操作已取消" => "Canceled",
        "当前模型方向尚未开放翻译风格" => "Tone is not released for this model direction",
        "当前模型方向尚未开放换种说法" => "Rephrasing is not released for this model direction",
        "暂不支持同时应用风格与术语表" => "Tone and glossary cannot be combined yet",
        "当前模型方向尚未开放风格与术语同时应用" => "Combined tone and glossary is not released for this model direction",
        "当前状态暂不可用" => "Unavailable in the current state",
        "应用风格数量已达上限" => "The per-app tone limit has been reached",
        "请选择有效的翻译风格" => "Choose a valid tone",
        "已加入术语表，下次翻译生效" => "Added to glossary. Applies to the next translation.",
        "已保存到术语表，当前模型方向尚未开放术语应用" => "Saved to glossary. Application is not released for this model direction.",
        "术语表中已有相同规则" => "This rule already exists in the glossary",
        "术语功能尚未开放" => "Glossary application is not released",
        "当前模型不支持同时应用风格与术语表" => "This model cannot combine tone and glossary",
        "当前译文无法加入术语表" => "This translation cannot be added to the glossary",
        "未能返回原输入框，术语尚未保存" => "Couldn’t return to the source. The term was not saved.",
        "请先完成或取消当前输入" => "Finish or cancel the current composition first",
        "请回到当前术语表单" => "Return to the current glossary form",
        "请先完成当前术语表单" => "Finish the current glossary form first",
        "内容或设置已变化，请重新打开表单" => "Content or settings changed. Open the form again.",
        "请检查词语、方向、冲突规则及容量限制" => "Check the terms, direction, conflicting rules and capacity limits",
        "保存结果尚未确认，请检查术语表" => "Save was not confirmed. Check the glossary.",
        "当前无法加入术语表" => "Cannot add to glossary now",
        "当前模型暂不支持此操作" => "The current model doesn’t support this action",
        _ => UiStrings.Translate(value)
    };

    private void UpdateBar(Session session, bool permit, bool previewOnly, string? statusOverride)
    {
        bool idle = session.State == SessionState.IdleCapsule;
        _barBorder.Visibility = Visibility.Visible;
        _body.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        _surface.RowDefinitions[1].Height = new(36);
        _language.Text = DirectionCode(_settings.Source) + " → " + DirectionCode(_settings.Target);
        string stage = statusOverride is not null ? LocalizedStatus(statusOverride)
            : Stage(session.State, _hasPreview, previewOnly, session.Current?.Ime == ImeState.Unknown);
        if (stage.Length == 0 && !string.IsNullOrEmpty(_viewContext?.TranslationActions?.Feedback))
            stage = LocalizedStatus(_viewContext.TranslationActions.Feedback);
        _status.Text = stage;
        _status.Visibility = !idle && stage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _status.SetResourceReference(TextBlock.ForegroundProperty,
            session.State == SessionState.TranslationFailed ? "Warn" : session.State == SessionState.EditedTranslationPending ? "AccentInk" : "Tertiary");
        string help = previewOnly ? UiTheme.L("当前不能通过接受键写回这条译文，接受键会交还原应用。",
            "The accept key cannot write this preview back; it passes through to the original app.") : stage;
        _status.ToolTip = help.Length > 0 ? help : null;
        AutomationProperties.SetHelpText(_status, previewOnly ? help : "");
        string dotBrush = session.State == SessionState.TranslationFailed ? "Danger"
            : previewOnly || session.State == SessionState.Invalid ? "Tertiary" : "Accent";
        if (_dotBrushKey != dotBrush)
        {
            _dot.SetResourceReference(Border.BackgroundProperty, dotBrush);
            _dotBrushKey = dotBrush;
        }
        _dot.Visibility = Visibility.Visible;
        bool returning = session.State == SessionState.EditingTranslation && _viewContext?.CanEdit == true;
        bool accepting = permit && !previewOnly && session.State is SessionState.ValidTranslation or SessionState.EditedTranslationPending;
        // Update permission-dependent chrome before starting any visual animation.
        // No animation completion ever changes this value or grants a permit.
        _acceptHint.Visibility = returning || accepting ? Visibility.Visible : Visibility.Collapsed;
        _tabHint.Text = returning ? UiTheme.L("返回", "Back") : UiTheme.L("接受", "Accept");
        AutomationProperties.SetAutomationId(_tabHint, "TranslationAcceptHint");
        AutomationProperties.SetName(_tabHint, returning ? UiTheme.L("返回原输入框", "Return to Source") : UiTheme.L("接受译文", "Accept Translation"));
        _retryButton.Visibility = session.State == SessionState.TranslationFailed ? Visibility.Visible : Visibility.Collapsed;
        _retryButton.IsEnabled = !_composing;
        _actions.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        _expandButton.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        _skeleton.Visibility = !idle && !_hasPreview && session.State == SessionState.Translating ? Visibility.Visible : Visibility.Collapsed;
        _editor.Visibility = _skeleton.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        if (idle)
            _expanded = false;
    }
}
