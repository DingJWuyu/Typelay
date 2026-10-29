using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private void About()
    {
        Heading(UiTheme.L("关于与隐私", "About & Privacy"));
        object identity = _pageIdentity;
        var brand = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 8, 0, 24) };
        brand.Children.Add(UiTheme.Mark(76, appIcon: true));
        var name = UiTheme.Text("TypeLay", 26);
        name.FontFamily = UiTheme.DisplayFont;
        name.FontWeight = FontWeights.Medium;
        name.LineHeight = 29.9;
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Margin = new(0, 10, 0, 0);
        AutomationProperties.SetAutomationId(name, "AboutProductName");
        brand.Children.Add(name);
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? UiTheme.L("未知版本", "Unknown version");
        var versionText = UiTheme.Text(UiTheme.L("版本 ", "Version ") + version, 13, true);
        versionText.HorizontalAlignment = HorizontalAlignment.Center;
        versionText.Margin = new(0, 10, 0, 0);
        AutomationProperties.SetAutomationId(versionText, "AboutVersion");
        brand.Children.Add(versionText);
        _content.Children.Add(brand);

        BeginGroup();
        var location = AboutValue(UiTheme.L("仅此设备", "On this device"), "AboutTranslationLocation");
        var local = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var shield = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12 3 5 6v5.5c0 4.3 3 7.6 7 9.5 4-1.9 7-5.2 7-9.5V6Z M9 12l2.2 2.2L15.5 10"),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform
        };
        shield.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Good");
        local.Children.Add(new Viewbox { Width = 16, Height = 16, Child = shield, Margin = new(0, 0, 6, 0) });
        local.Children.Add(location);
        Row(UiTheme.L("翻译位置", "Translation runs"), "", local);
        RegisterSearchTarget("privacy", (Panel)local.Parent);
        Row(UiTheme.L("翻译历史", "Translation history"), "", AboutValue(UiTheme.L("不保存", "Not saved"), "AboutTranslationHistory"));
        Row(UiTheme.L("账号", "Account"), "", AboutValue(UiTheme.L("无需登录", "Not required"), "AboutAccountRequirement"));

        BeginGroup();
        string unavailable = UiTheme.L("尚未提供更新检查功能。", "Update checking is not available yet.");
        var automatic = UiTheme.Toggle(UiTheme.L("自动检查更新", "Check for updates automatically"), () => false, _ => { });
        automatic.IsEnabled = false;
        automatic.ToolTip = unavailable;
        ToolTipService.SetShowOnDisabled(automatic, true);
        AutomationProperties.SetAutomationId(automatic, "AboutAutomaticUpdates");
        Row(UiTheme.L("自动检查更新", "Check for updates automatically"), "", automatic);
        var update = UiTheme.Button(UiTheme.L("检查更新", "Check now"), () => { });
        update.IsEnabled = false;
        update.ToolTip = unavailable;
        ToolTipService.SetShowOnDisabled(update, true);
        AutomationProperties.SetAutomationId(update, "AboutCheckUpdates");
        Row(UiTheme.L("软件更新", "Software update"), "", update);
        RegisterSearchTarget("checkupdate", (Panel)update.Parent);

        // Keep the existing local-data action and development limitations,
        // without substituting those details for the prototype's privacy rows.
        BeginGroup();
        var open = UiTheme.Button(UiTheme.L("打开目录", "Open Folder"), () =>
        {
            if (IsVisible && _page == "about" && ReferenceEquals(identity, _pageIdentity))
                Process.Start(new ProcessStartInfo(AppPaths.Data) { UseShellExecute = true });
        });
        AutomationProperties.SetAutomationId(open, "AboutOpenDataFolder");
        Row(UiTheme.L("本地数据", "Local data"), UiTheme.L("设置、模型与下载缓存", "Settings, models and download cache"), open);
        var details = new StackPanel { Margin = new(0, 8, 0, 0) };
        var privacy = UiTheme.Text(UiTheme.L(
            "实际输入与译文不上传云端，不建立翻译历史数据库。运行组件与模型下载使用 HTTPS；推理子进程没有网络下载路径。",
            "Source text and translations are not uploaded. No translation history is stored. Runtime and model downloads use HTTPS; the inference process does not download content."), 12, true);
        privacy.LineHeight = 17;
        details.Children.Add(privacy);
        var limits = UiTheme.Text(UiTheme.L(
            "原生输入框写回默认关闭。跨进程非原子写回、真实输入法与目标应用仍需 Windows 实机验证；界面重构不代表这些安全门已通过。",
            "Native input replacement is off by default. Cross-process writeback, real IMEs and target apps still require Windows desktop validation; this interface rebuild does not qualify those safety gates."), 12, true);
        limits.Margin = new(0, 8, 0, 0);
        limits.LineHeight = 17;
        details.Children.Add(limits);
        var disclosure = new Expander
        {
            Header = UiTheme.L("隐私与开发版说明", "Privacy and development notes"),
            Content = details,
            Margin = new(4, 0, 4, 12)
        };
        AutomationProperties.SetAutomationId(disclosure, "AboutLocalDetails");
        _content.Children.Add(disclosure);
    }

    private static TextBlock AboutValue(string text, string id)
    {
        var value = UiTheme.Text(text, 13, true);
        value.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(value, id);
        return value;
    }
}
