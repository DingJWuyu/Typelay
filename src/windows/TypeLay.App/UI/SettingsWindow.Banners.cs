using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private Border? _modelBanner, _saveBanner;
    private TextBlock? _modelBannerText;
    private Action<bool>? _setModelWarning;
    private SaveIssue? _saveIssue;
    private object? _bannerRetry;
    private object? _renderedSaveIssue;
    private bool _renderedSaveRetry;

    // Only a confirmed persistence failure has a retry. Unknown results and
    // notification failures expose the current settings, never replay a write.
    private sealed record SaveIssue(string Page, long Revision, bool Unknown, bool Committed,
        Func<bool>? StillCurrent = null, Func<Func<bool>, Task>? Retry = null);

    private void BuildSettingsBanners()
    {
        object page = _pageIdentity;
        var view = UiTheme.Button(UiTheme.L("查看", "View"), () =>
        {
            if (IsVisible && _modelBanner?.IsVisible == true && _closeDialog is null && ReferenceEquals(page, _pageIdentity))
                NavigatePage("model", clearSearch: true);
        });
        AutomationProperties.SetAutomationId(view, "SettingsModelBannerView");
        _modelBannerText = UiTheme.Text("", 13);
        _modelBannerText.FontWeight = FontWeights.Medium;
        AutomationProperties.SetAutomationId(_modelBannerText, "SettingsModelBannerText");
        _modelBanner = Banner(_modelBannerText, view, "SettingsModelBanner", "SettingsModelWarningInk");
        _saveBanner = new Border { Visibility = Visibility.Collapsed };
        _renderedSaveIssue = null;
        _content.Children.Add(_saveBanner);
        _content.Children.Add(_modelBanner);
        RefreshSettingsBanners();
    }

    private static Border Banner(TextBlock text, Button action, string id, string color)
    {
        action.Padding = new(10, 4, 10, 4);
        action.Margin = new(12, 0, 0, 0);
        action.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(action, Dock.Right);
        row.Children.Add(action);
        var warning = PrototypeGlyph("M12,4 L2.8,19.5 H21.2 Z M12,10 V14.5 M12,17 V17.01", color);
        warning.Width = warning.Height = 18;
        warning.Margin = new(0, 0, 12, 0);
        DockPanel.SetDock(warning, Dock.Left);
        row.Children.Add(warning);
        row.Children.Add(text);
        var card = UiTheme.Border(row, 12);
        card.Padding = new(14, 12, 14, 12);
        card.BorderThickness = new(.5);
        card.Margin = new(0, 0, 0, 18);
        card.Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = .03 };
        AutomationProperties.SetAutomationId(card, id);
        AutomationProperties.SetLiveSetting(card, id == "SettingsSaveBanner" ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        return card;
    }

    private static void ShowBanner(Border card, bool visible)
    {
        bool newlyVisible = card.Visibility != Visibility.Visible && visible;
        card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible || UiTheme.ReduceMotion)
            card.BeginAnimation(OpacityProperty, null);
        else if (newlyVisible)
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { FillBehavior = FillBehavior.Stop });
    }

    private void RefreshSettingsBanners()
    {
        bool modelAttention = !_model.Ready || _install is not null || _startingModel || _setupPaused || _setupFailure is not null;
        _setModelWarning?.Invoke(modelAttention);
        if (_modelBanner is not null && _modelBannerText is not null)
        {
            _modelBannerText.Text = _startingModel ? UiTheme.L("正在启动本地模型…", "Starting the local model…")
                : _install is { IsCancellationRequested: true } ? UiTheme.L("正在暂停模型准备…", "Pausing model preparation…")
                : _install is not null ? UiTheme.L("正在准备本地模型…", "Preparing the local model…")
                : _setupPaused ? UiTheme.L("模型准备已暂停", "Model preparation paused")
                : _setupFailure is not null ? UiStrings.Translate(PreparationView().StatusText)
                : _model.Installed ? UiTheme.L("模型尚未就绪", "Model not ready")
                : UiTheme.L("需要下载本地翻译模型", "Download the local translation model");
            _modelBanner.Resources["SettingsModelWarningInk"] = FindResource(_setupFailure is not null ? "Danger" : "Warn");
            _modelBanner.ToolTip = UiStrings.Translate(_model.Status);
            ShowBanner(_modelBanner, modelAttention && _page is not ("model" or "translation"));
        }
        RefreshSaveBanner();
    }

    private void ClearSaveIssue()
    {
        _saveIssue = null;
        RefreshSaveBanner();
    }

    private void SetSaveIssue(bool unknown, bool committed = false, Func<bool>? current = null, Func<Func<bool>, Task>? retry = null, string? page = null)
    {
        _saveIssue = new(page ?? _page, _settings.Revision, unknown, committed, current, retry);
        RefreshSaveBanner();
    }

    private void RetireBannerRetry()
    {
        if (_bannerRetry is null)
            return;
        _bannerRetry = null;
        _translationRequest?.Cancel();
    }

    private void RefreshSaveBanner()
    {
        if (_saveBanner is null)
            return;
        var issue = _saveIssue;
        if (issue is null)
        {
            ShowBanner(_saveBanner, false);
            _renderedSaveIssue = null;
            return;
        }
        bool retry = !issue.Unknown && issue.Retry is not null && issue.Page == _page
            && issue.Revision == _settings.Revision && issue.StillCurrent?.Invoke() == true;
        if (!ReferenceEquals(_renderedSaveIssue, issue) || retry != _renderedSaveRetry)
        {
            _renderedSaveIssue = issue;
            _renderedSaveRetry = retry;
            object page = _pageIdentity;
            var label = UiTheme.Text(issue.Committed ? UiTheme.L("设置已保存，部分状态未能更新。", "Settings were saved, but some status updates failed.")
                : issue.Unknown ? UiTheme.L("无法确认设置是否已保存", "Couldn’t confirm settings were saved")
                : UiTheme.L("设置未能保存", "Settings weren’t saved"), 13);
            label.FontWeight = FontWeights.Medium;
            AutomationProperties.SetAutomationId(label, "SettingsSaveBannerText");
            var action = UiTheme.Button(retry ? UiTheme.L("重试", "Retry") : UiTheme.L("查看", "View"), async () =>
            {
                if (!IsVisible || _closeDialog is not null || _savingSettings || _savingTranslation
                    || !ReferenceEquals(page, _pageIdentity) || !ReferenceEquals(issue, _saveIssue))
                    return;
                if (!retry || issue.Page != _page || issue.Revision != _settings.Revision || issue.StillCurrent?.Invoke() != true)
                {
                    NavigatePage(issue.Page, clearSearch: true);
                    return;
                }
                object attempt = _bannerRetry = new();
                bool Current() => ReferenceEquals(attempt, _bannerRetry) && IsVisible && _closeDialog is null
                    && !_emergencyShutdown && ReferenceEquals(page, _pageIdentity);
                ClearSaveIssue(); // Retire this button before entering a reentrant save callback.
                try
                {
                    await issue.Retry!(Current);
                }
                finally { if (ReferenceEquals(attempt, _bannerRetry)) _bannerRetry = null; }
            });
            AutomationProperties.SetAutomationId(action, "SettingsSaveBannerAction");
            var card = Banner(label, action, "SettingsSaveBanner", "Danger");
            _saveBanner.Child = card;
        }
        ShowBanner(_saveBanner, true);
        foreach (var action in VisualControls(_saveBanner).OfType<Button>())
            action.IsEnabled = !_savingSettings && !_savingTranslation;
    }
}
