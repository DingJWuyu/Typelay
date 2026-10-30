using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private static readonly (string Id, string Title, string Color)[] KnownApplications =
    [
        ("firefox", "Firefox", "#8256D0"), ("chrome", "Chrome / Chromium", "#8256D0"),
        ("msedge", "Microsoft Edge", "#8256D0"), ("code", "Visual Studio Code", "#2C6BB0")
    ];
    private static IEnumerable<string> KnownApplicationIds => KnownApplications.Select(app => app.Id);
    private static string ApplicationTitle(string application) => KnownApplications.FirstOrDefault(app => app.Id == application).Title ?? application + ".exe";

    private void Applications()
    {
        object identity = _pageIdentity;
        object rowsIdentity = new();
        bool CurrentPage() => IsVisible && ReferenceEquals(identity, _pageIdentity) && !_emergencyShutdown;
        var view = _support?.Read(_settings.Value) ?? ApplicationSupportPresentation.Create([]);
        Heading(UiTheme.L("应用", "Applications"));
        ApplicationConnections(view, CurrentPage);
        var groupTitle = UiTheme.Text(UiTheme.L("应用", "Apps"), 12, true, true);
        groupTitle.Margin = new(4, 22, 4, 7);
        _content.Children.Add(groupTitle);
        var search = new TextBox { Text = _applicationQuery, Margin = new(0, 0, 0, 8) };
        AutomationProperties.SetName(search, UiTheme.L("搜索应用", "Search applications"));
        AutomationProperties.SetAutomationId(search, "ApplicationSearch");
        _content.Children.Add(SearchField(search, UiTheme.L("搜索应用", "Search applications")));
        var table = new StackPanel();
        var list = new StackPanel();
        AutomationProperties.SetAutomationId(list, "ApplicationRows");
        table.Children.Add(list);
        var card = UiTheme.Border(table, 12);
        card.BorderThickness = new(.5);
        card.Padding = new(0);
        _content.Children.Add(card);
        RegisterSearchTarget("applist", table);
        var refreshStyles = new List<Action>();
        void RenderApplications()
        {
            object rendered = rowsIdentity = new object();
            bool CurrentRows() => CurrentPage() && ReferenceEquals(rendered, rowsIdentity) && _closeDialog is null;
            list.Children.Clear();
            refreshStyles.Clear();
            string query = search.Text.Trim();
            foreach (string application in _settings.Value.VisibleApplications(KnownApplicationIds).Where(app =>
                app.Contains(query, StringComparison.OrdinalIgnoreCase) || ApplicationTitle(app).Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                string title = ApplicationTitle(application);
                var row = ApplicationGrid();
                AutomationProperties.SetAutomationId(row, "ApplicationRow_" + application);
                row.Children.Add(ApplicationTile(application));
                var label = UiTheme.Text(title, 13);
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                var style = new ComboBox
                {
                    MinWidth = 112,
                    MaxWidth = 140,
                    Height = 24,
                    MinHeight = 24,
                    FontSize = 12,
                    FontWeight = FontWeights.Medium,
                    Margin = new(0, 0, 12, 0),
                    ItemsSource = new[]
                    {
                        new ComboBoxItem { Content = UiTheme.L("默认风格", "Default tone") },
                        new ComboBoxItem { Content = UiTheme.L("自然", "Natural") },
                        new ComboBoxItem { Content = UiTheme.L("正式", "Formal") },
                        new ComboBoxItem { Content = UiTheme.L("简洁", "Concise"), IsEnabled = false },
                        new ComboBoxItem { Content = UiTheme.L("口语", "Casual"), IsEnabled = false }
                    }
                };
                style.SetResourceReference(StyleProperty, "AppearancePopup");
                AutomationProperties.SetName(style, UiTheme.English ? "App tone " + application : "应用风格 " + application);
                AutomationProperties.SetAutomationId(style, "ApplicationStyle_" + application);
                ToolTipService.SetShowOnDisabled(style, true);
                bool refreshing = false;
                void RefreshStyle()
                {
                    refreshing = true;
                    style.SelectedIndex = _settings.Value.Styles?.Applications?.TryGetValue(application, out var selected) == true ? selected == TranslationStyle.Formal ? 2 : 1 : 0;
                    var state = _controller?.GetApplicationStyleSettingsState(application);
                    bool enabled = !_settings.Value.Excluded.Contains(application, StringComparer.OrdinalIgnoreCase);
                    bool canSet = enabled && !_savingTranslation && state?.CanChange == true;
                    bool canInherit = enabled && !_savingTranslation && _controller?.GetApplicationStyleSettingsState(application, null).CanChange == true;
                    ((ComboBoxItem)style.Items[0]).IsEnabled = canInherit;
                    ((ComboBoxItem)style.Items[1]).IsEnabled = ((ComboBoxItem)style.Items[2]).IsEnabled = canSet;
                    style.IsEnabled = canSet || canInherit;
                    string reason = !enabled ? UiTheme.L("此应用已停用。", "This app is disabled.")
                        : UiStrings.Translate(TranslationMenuPresentation.Reason(state?.UnavailableReason ?? "style-unavailable") ?? "");
                    style.ToolTip = canSet ? null : reason;
                    for (int i = 1; i < style.Items.Count; i++)
                    {
                        var item = (ComboBoxItem)style.Items[i];
                        string help = i >= 3 ? UiTheme.L("尚未提供", "Not available") : canSet ? "" : reason;
                        item.ToolTip = help;
                        ToolTipService.SetShowOnDisabled(item, true);
                        AutomationProperties.SetHelpText(item, help);
                    }
                    AutomationProperties.SetHelpText(style, canSet ? "" : reason);
                    refreshing = false;
                }
                style.SelectionChanged += async (_, _) =>
                {
                    if (refreshing || _controller is null || !CurrentRows()
                        || _settings.Value.Excluded.Contains(application, StringComparer.OrdinalIgnoreCase)
                        || style.SelectedIndex is < 0 or > 2)
                        return;
                    TranslationStyle? value = style.SelectedIndex == 0 ? null : style.SelectedIndex == 2 ? TranslationStyle.Formal : TranslationStyle.Natural;
                    var state = _controller.GetApplicationStyleSettingsState(application, value);
                    if (!state.CanChange)
                    {
                        RefreshStyle();
                        return;
                    }
                    await SaveTranslationChangeAsync(ct => _controller.ChangeApplicationStyleAsync(application, value, state.SettingsRevision, ct));
                };
                RefreshStyle();
                refreshStyles.Add(RefreshStyle);
                Grid.SetColumn(style, 2);
                row.Children.Add(style);
                var enabled = UiTheme.Toggle(UiTheme.English ? "Enable in " + application : "在 " + application + " 中启用", () => !_settings.Value.Excluded.Contains(application, StringComparer.OrdinalIgnoreCase),
                    async value => { if (CurrentRows()) await SetApplicationEnabledAsync(application, value, CurrentRows); });
                AutomationProperties.SetAutomationId(enabled, "ApplicationEnabled_" + application);
                Grid.SetColumn(enabled, 3);
                row.Children.Add(enabled);
                row.Background = Brushes.Transparent;
                // WPF routes both right-click and the keyboard context-menu key
                // here. The prototype opens confirmation directly, without a menu.
                row.ContextMenuOpening += async (_, args) =>
                {
                    args.Handled = true;
                    if (!CurrentRows())
                        return;
                    long revision = _settings.Revision;
                    if (!ConfirmApplicationRemoval(application, CurrentPage) || !CurrentRows() || _settings.Revision != revision)
                        return;
                    var members = _settings.Value.VisibleApplications(KnownApplicationIds).Where(app => !StringComparer.OrdinalIgnoreCase.Equals(app, application)).ToArray();
                    if (await SaveAsync(_settings.Value with
                    {
                        ApplicationListCustomized = true,
                        ManagedApplications = members
                    }, callerCurrent: CurrentRows))
                        Toast(UiTheme.L("已从列表移除", "Removed from the list"), async () =>
                        {
                            var undoCurrent = CaptureApplicationUndo();
                            var current = _settings.Value.VisibleApplications(KnownApplicationIds);
                            if (current.Contains(application, StringComparer.OrdinalIgnoreCase))
                                return;
                            if (await SaveAsync(_settings.Value with
                            {
                                ApplicationListCustomized = true,
                                ManagedApplications = current.Append(application).ToArray()
                            }, callerCurrent: undoCurrent))
                                Toast(UiTheme.L("已恢复到列表", "Restored to the list"));
                        }, () => !_settings.Value.VisibleApplications(KnownApplicationIds).Contains(application, StringComparer.OrdinalIgnoreCase));
                };
                if (list.Children.Count > 0)
                    list.Children.Add(ApplicationDivider());
                list.Children.Add(row);
            }
            if (list.Children.Count == 0)
            {
                var empty = UiTheme.Text(query.Length > 0 ? UiTheme.L("没有匹配的应用", "No matching apps") : UiTheme.L("尚未添加应用", "No apps added"), 13, true);
                empty.Margin = new(14, 20, 14, 20);
                empty.HorizontalAlignment = HorizontalAlignment.Center;
                AutomationProperties.SetAutomationId(empty, "ApplicationEmpty");
                list.Children.Add(empty);
            }
        }
        search.TextChanged += (_, _) => { if (CurrentPage()) { _applicationQuery = search.Text; RenderApplications(); } };
        RenderApplications();
        var add = UiTheme.Button("", async () =>
        {
            if (!CurrentPage() || _closeDialog is not null)
                return;
            long revision = _settings.Revision;
            string? application = PickApplication(CurrentPage);
            if (application is null || !CurrentPage() || _settings.Revision != revision)
                return;
            var members = _settings.Value.VisibleApplications(KnownApplicationIds);
            if (!members.Contains(application, StringComparer.OrdinalIgnoreCase)
                && !await SaveAsync(_settings.Value with
                {
                    ApplicationListCustomized = true,
                    ManagedApplications = members.Append(application).ToArray()
                }, callerCurrent: CurrentPage))
                return;
            // Save can redraw the page. Find only its current row, never retain an old control.
            _applicationQuery = "";
            Refresh();
            if (_page == "apps" && IsVisible)
            {
                FindApplicationRow(application)?.BringIntoView();
                Toast(UiTheme.L("已添加应用", "Application added"));
            }
        });
        add.Content = PrototypeGlyph("M12 5v14M5 12h14", "Muted");
        add.Width = 28;
        add.Height = 24;
        add.Padding = new(0);
        add.BorderThickness = new(0);
        add.SetResourceReference(Control.BackgroundProperty, "Panel");
        AutomationProperties.SetName(add, UiTheme.L("添加应用", "Add app"));
        AutomationProperties.SetAutomationId(add, "ApplicationAdd");
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(4) };
        bar.Children.Add(add);
        var footer = new Border { BorderThickness = new(0, .5, 0, 0), Child = bar };
        footer.SetResourceReference(Border.BorderBrushProperty, "Line");
        table.Children.Add(footer);
        _refreshTranslationSettings = () => { foreach (var refresh in refreshStyles) refresh(); };
    }

    private FrameworkElement? FindApplicationRow(string application)
    {
        FrameworkElement? Find(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement element && AutomationProperties.GetAutomationId(element) == "ApplicationRow_" + application)
                    return element;
                if (Find(child) is { } found)
                    return found;
            }
            return null;
        }
        UpdateLayout();
        return Find(_content);
    }

    private static Grid ApplicationGrid()
    {
        var row = new Grid { MinHeight = 44, Margin = new(14, 0, 14, 0) };
        row.ColumnDefinitions.Add(new()
        {
            Width = new(38)
        });
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        row.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        return row;
    }
    private static Border ApplicationDivider()
    {
        var line = new Border { Height = .5, Margin = new(14, 0, 0, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        return line;
    }
    private static Border ApplicationTile(string application)
    {
        var known = KnownApplications.FirstOrDefault(app => app.Id == application);
        var initial = UiTheme.Text(ApplicationTitle(application)[..1].ToUpperInvariant(), 12, bold: true);
        initial.Foreground = Brushes.White;
        initial.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border { Width = 26, Height = 26, CornerRadius = new(7), Background = UiTheme.Brush(known.Color ?? "#8256D0"), Child = initial, VerticalAlignment = VerticalAlignment.Center };
    }
    private void ApplicationConnections(ApplicationSupportView view, Func<bool> current)
    {
        var group = new StackPanel();
        var card = UiTheme.Border(group, 12);
        card.Padding = new(0);
        card.BorderThickness = new(.5);
        _content.Children.Add(card);
        var desktopDetails = new StackPanel { Margin = new(14, 0, 14, 12), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(desktopDetails, "ApplicationConnectionBody_desktop");
        var ordinary = view.Rows.FirstOrDefault(row => row.Id == "desktop-text");
        if (ordinary is not null)
            desktopDetails.Children.Add(new Expander { Header = UiTheme.L("其他普通输入框", "Other text fields"), Content = SupportRow(ordinary, view, checkInSummary: true) });
        foreach (var row in view.Rows.Where(row => row.Id != "desktop-text" && row.Id is not ("google-chrome" or "chromium" or "firefox" or "microsoft-edge")))
            desktopDetails.Children.Add(SupportRow(row, view));
        bool OffersDesktopCheck() => (_support?.Read(_settings.Value) ?? view).Rows.Any(row => row.Id == "desktop-text"
            && (row.PrimaryAction?.Id == ApplicationSupportAction.Refresh || row.SecondaryActions.Any(action => action.Id == ApplicationSupportAction.Refresh)));
        var desktopCheck = UiTheme.Button(UiTheme.L("重新检查", "Check again"), () =>
        {
            if (!current() || _closeDialog is not null || !_settings.Value.Enabled || !_model.Ready || !OffersDesktopCheck())
                return;
            _support?.BeginCheck("desktop-text");
            Hide();
        });
        desktopCheck.IsEnabled = _support is not null && _settings.Value.Enabled && _model.Ready && OffersDesktopCheck();
        AutomationProperties.SetAutomationId(desktopCheck, "ApplicationSupport_desktop-text_Refresh");
        _connectionChecks.Add(desktopCheck);
        var desktopStatus = UiTheme.Text(ordinary?.StatusText ?? UiTheme.L("尚未检查", "Not checked"), 12, true);
        var desktopSummary = ConnectionSummary("desktop", UiTheme.L("桌面应用", "Desktop apps"), "#5F5E5A", Pages.Single(page => page.Id == "shortcuts").Icon,
            desktopStatus, desktopDetails, current, out var desktopDot, desktopCheck);
        desktopDot.SetResourceReference(Shape.FillProperty, ordinary?.State == ApplicationSupportState.Connected ? "Good" : "Tertiary");
        RegisterSearchTarget("desktop", desktopSummary);
        group.Children.Add(desktopSummary);
        group.Children.Add(desktopDetails);
        group.Children.Add(ApplicationDivider());
        var browserDetails = new StackPanel { Margin = new(14, 0, 14, 12), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(browserDetails, "ApplicationConnectionBody_browser");
        browserDetails.Children.Add(BrowserSupportRow(view, out var connectBrowser, out var installBrowser));
        foreach (var row in view.Rows.Where(row => row.Id is "chromium" or "firefox" or "microsoft-edge"))
            browserDetails.Children.Add(SupportRow(row, view));
        var browserPrimary = UiTheme.Button("", () =>
        {
            if (!current() || _closeDialog is not null || _support?.Browser is not { } browser || browser.Busy || browser.MutationUnavailable)
                return;
            if (browser.CanConnect)
                connectBrowser();
            else if ((_support?.Read(_settings.Value) ?? view).Rows.Any(row => row.Id == "google-chrome" && row.State == ApplicationSupportState.NeedsInstall))
            {
                browserDetails.Visibility = Visibility.Visible;
                installBrowser();
            }
        });
        AutomationProperties.SetAutomationId(browserPrimary, "ApplicationConnectionPrimary_browser");
        var browserStatus = UiTheme.Text("", 12, true);
        var browserSummary = ConnectionSummary("browser", UiTheme.L("浏览器扩展", "Browser extension"), "#3478F6", Pages.Single(page => page.Id == "language").Icon,
            browserStatus, browserDetails, current, out var browserDot, browserPrimary);
        RegisterSearchTarget("browser", browserSummary);
        group.Children.Add(browserSummary);
        group.Children.Add(browserDetails);
        var refreshDetails = _refreshBrowserRow;
        void RefreshConnectionSummary()
        {
            refreshDetails?.Invoke();
            var browser = _support?.Browser;
            var actual = (_support?.Read(_settings.Value) ?? view).Rows.FirstOrDefault(row => row.Id == "google-chrome");
            browserPrimary.Content = browser?.CanConnect == true ? UiTheme.L("接入", "Connect") : UiTheme.L("安装…", "Install…");
            browserPrimary.Visibility = browser?.CanConnect == true || actual?.State == ApplicationSupportState.NeedsInstall ? Visibility.Visible : Visibility.Collapsed;
            browserPrimary.IsEnabled = browser is not null && !browser.Busy && !browser.MutationUnavailable;
            browserPrimary.ToolTip = browser is null ? UiTheme.L("此发行版尚未配置 Chrome 连接。", "Chrome connection is not configured in this installation.") : null;
            ToolTipService.SetShowOnDisabled(browserPrimary, true);
            browserDot.SetResourceReference(Shape.FillProperty, browser?.MutationUnavailable == true || actual?.State == ApplicationSupportState.Error ? "Danger"
                : browser?.Suspended != true && actual?.State == ApplicationSupportState.Connected ? "Good" : "Tertiary");
            browserStatus.Text = UiStrings.Translate(browser?.MutationUnavailable == true ? "操作结果未确认" : browser?.Busy == true ? "正在更新连接…"
                : browser?.Suspended == true ? UiTheme.L("注册仍在，连接已停止", "Registered, connection stopped") : actual?.StatusText ?? UiTheme.L("尚未连接", "Not connected"));
        }
        _refreshBrowserRow = RefreshConnectionSummary;
        RefreshConnectionSummary();
    }

    private Grid ConnectionSummary(string id, string title, string color, string icon, TextBlock status, FrameworkElement details, Func<bool> current, out Ellipse dot, Button primary)
    {
        var row = ApplicationGrid();
        var glyph = PrototypeGlyph(icon);
        glyph.Width = glyph.Height = 15;
        row.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new(7), Background = UiTheme.Brush(color), Child = glyph });
        var label = UiTheme.Text(title, 13);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        status.MaxWidth = 150;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.TextWrapping = TextWrapping.NoWrap;
        status.Margin = new(12, 0, 12, 0);
        AutomationProperties.SetAutomationId(status, "ApplicationConnectionStatus_" + id);
        dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        var state = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        state.Children.Add(dot);
        state.Children.Add(status);
        Grid.SetColumn(state, 2);
        row.Children.Add(state);
        var more = UiTheme.Button(UiTheme.L("查看详情", "View details"), () =>
        {
            if (current() && _closeDialog is null)
                details.Visibility = details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        });
        more.Content = PrototypeGlyph("M5 12h.01M12 12h.01M19 12h.01", "Muted", stroke: 3);
        AutomationProperties.SetName(more, title + " · " + UiTheme.L("查看详情", "View details"));
        more.Width = more.Height = 26;
        more.Padding = new(0);
        more.BorderThickness = new(0);
        more.Margin = new(4, 0, 0, 0);
        ApplicationActionSize(primary);
        AutomationProperties.SetAutomationId(more, "ApplicationConnectionDetails_" + id);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(primary);
        actions.Children.Add(more);
        Grid.SetColumn(actions, 3);
        row.Children.Add(actions);
        return row;
    }

    private string? PickApplication(Func<bool> pageCurrent)
    {
        long revision = _settings.Revision;
        var visible = _settings.Value.VisibleApplications(KnownApplicationIds);
        var choices = KnownApplicationIds.Concat(_settings.Value.Managed).Concat(_settings.Value.Excluded)
            .Concat(_settings.Value.Styles?.Applications?.Keys ?? []).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(app => !visible.Contains(app, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var dialog = ApplicationDialog(UiTheme.L("添加应用", "Add app"), "ApplicationPicker", 320);
        bool Current() => pageCurrent() && _settings.Revision == revision && ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible;
        string? result = null;
        bool chooseFile = false;
        var body = new StackPanel();
        body.Children.Add(ApplicationDialogTitle(UiTheme.L("添加应用", "Add app")));
        var search = new TextBox { Margin = new(0, 12, 0, 8) };
        AutomationProperties.SetAutomationId(search, "ApplicationPickerSearch");
        AutomationProperties.SetName(search, UiTheme.L("搜索应用", "Search apps"));
        body.Children.Add(SearchField(search, UiTheme.L("搜索应用", "Search apps")));
        var list = new StackPanel();
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        object choicesIdentity = new();
        void Draw()
        {
            object generation = choicesIdentity = new();
            list.Children.Clear();
            string query = search.Text.Trim();
            foreach (string application in choices.Where(app => app.Contains(query, StringComparison.OrdinalIgnoreCase) || ApplicationTitle(app).Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                var option = UiTheme.Button(ApplicationTitle(application), () =>
                {
                    if (!Current() || !ReferenceEquals(generation, choicesIdentity))
                        return;
                    result = application;
                    dialog.DialogResult = true;
                });
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                var tile = ApplicationTile(application);
                tile.Width = tile.Height = 20;
                tile.CornerRadius = new(5);
                tile.Margin = new(0, 0, 10, 0);
                ((TextBlock)tile.Child).FontSize = 10;
                content.Children.Add(tile);
                content.Children.Add(UiTheme.Text(ApplicationTitle(application), 13));
                option.Content = content;
                AutomationProperties.SetName(option, ApplicationTitle(application));
                option.Height = 30;
                option.HorizontalContentAlignment = HorizontalAlignment.Left;
                option.BorderThickness = new(0);
                AutomationProperties.SetAutomationId(option, "ApplicationPickerChoice_" + application);
                list.Children.Add(option);
            }
            if (list.Children.Count == 0)
            {
                var empty = UiTheme.Text(UiTheme.L("没有匹配的应用", "No matching apps"), 13, true);
                empty.Margin = new(0, 12, 0, 12);
                list.Children.Add(empty);
            }
        }
        search.TextChanged += (_, _) => { if (Current()) Draw(); };
        Draw();
        var information = UiTheme.Text(UiTheme.L("列表包含已知接入或已保存的应用，不表示已安装。也可选择本机应用。", "These are known integrations or saved apps, not an installed-app inventory. You can also choose a local app."), 11.5, true);
        information.Margin = new(0, 10, 0, 12);
        body.Children.Add(information);
        var actions = new DockPanel();
        var browse = UiTheme.Button(UiTheme.L("选择应用…", "Choose app…"), () =>
        {
            if (!Current())
                return;
            chooseFile = true;
            dialog.DialogResult = true;
        });
        ApplicationActionSize(browse);
        AutomationProperties.SetAutomationId(browse, "ApplicationPickerBrowse");
        DockPanel.SetDock(browse, Dock.Left);
        actions.Children.Add(browse);
        var cancel = UiTheme.Button(UiTheme.L("取消", "Cancel"), () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = false; });
        ApplicationActionSize(cancel);
        cancel.IsCancel = true;
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetAutomationId(cancel, "ApplicationPickerCancel");
        actions.Children.Add(cancel);
        body.Children.Add(actions);
        ((Border)dialog.Content).Child = body;
        dialog.Loaded += (_, _) => search.Focus();
        _closeDialog = dialog;
        try
        {
            if (dialog.ShowDialog() != true || !pageCurrent() || _settings.Revision != revision)
                return null;
        }
        finally { if (ReferenceEquals(_closeDialog, dialog)) _closeDialog = null; }
        if (chooseFile)
        {
            string? path = _selectApplication(this);
            if (!pageCurrent() || _settings.Revision != revision || path is null)
                return null;
            return _settings.NormalizeStyleApplication(path);
        }
        return result;
    }

    private bool ConfirmApplicationRemoval(string application, Func<bool> pageCurrent)
    {
        if (!pageCurrent() || _closeDialog is not null)
            return false;
        long revision = _settings.Revision;
        var dialog = ApplicationDialog(UiTheme.L("移除应用", "Remove app"), "ApplicationRemovalConfirmation", 340);
        var body = new StackPanel();
        body.Children.Add(ApplicationDialogTitle(UiTheme.English ? "Remove " + ApplicationTitle(application) + "?" : "移除「" + ApplicationTitle(application) + "」？"));
        var explanation = UiTheme.Text(UiTheme.L("仅从列表移除，停用与风格设置保留。重新添加后仍使用这些设置，不会改变输入权限。", "Only removes it from this list. Disabled state and tone are kept when you add it again. Input permissions do not change."), 13, true);
        explanation.Margin = new(0, 12, 0, 20);
        body.Children.Add(explanation);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = UiTheme.Button(UiTheme.L("取消", "Cancel"), () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = false; });
        ApplicationActionSize(cancel);
        cancel.IsCancel = true;
        cancel.Margin = new(0, 0, 8, 0);
        AutomationProperties.SetAutomationId(cancel, "ApplicationRemovalCancel");
        var confirm = UiTheme.Button(UiTheme.L("移除", "Remove"), () =>
        {
            if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible)
                dialog.DialogResult = pageCurrent() && _settings.Revision == revision;
        });
        ApplicationActionSize(confirm);
        AutomationProperties.SetAutomationId(confirm, "ApplicationRemovalConfirm");
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        body.Children.Add(actions);
        ((Border)dialog.Content).Child = body;
        dialog.Loaded += (_, _) => cancel.Focus();
        _closeDialog = dialog;
        try
        {
            return dialog.ShowDialog() == true && pageCurrent() && _settings.Revision == revision;
        }
        finally { if (ReferenceEquals(_closeDialog, dialog)) _closeDialog = null; }
    }

    private static TextBlock ApplicationDialogTitle(string value)
    {
        var title = UiTheme.Text(value, 18);
        title.FontWeight = FontWeights.Medium;
        title.FontFamily = UiTheme.DisplayFontForSize(18);
        AutomationProperties.SetAutomationId(title, "ApplicationDialogTitle");
        return title;
    }

    private static void ApplicationActionSize(Button button)
    {
        button.Height = button.MinHeight = 26;
        button.FontSize = 12.5;
        button.FontWeight = FontWeights.Medium;
        button.Padding = new(12, 0, 12, 0);
        button.VerticalAlignment = VerticalAlignment.Center;
    }

    private Window ApplicationDialog(string title, string id, double clientWidth)
    {
        // The supplied prototype width includes its padding, not the native
        // Windows title bar and resize frame. Keep that shell separate.
        var client = new Border { Width = clientWidth, Padding = new(22) };
        client.SetResourceReference(Border.BackgroundProperty, "Panel");
        AutomationProperties.SetAutomationId(client, "ApplicationDialogSurface");
        var dialog = new Window
        {
            Owner = this,
            Title = title,
            Content = client,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        dialog.SetResourceReference(BackgroundProperty, "Panel");
        AutomationProperties.SetAutomationId(dialog, id);
        return dialog;
    }

    private Func<bool> CaptureApplicationUndo()
    {
        object page = _pageIdentity;
        Button toast = _toastUndo;
        return () => IsVisible && !_emergencyShutdown && _closeDialog is null
            && ReferenceEquals(page, _pageIdentity) && ReferenceEquals(toast, _toastUndo);
    }

    private async Task SetApplicationEnabledAsync(string application, bool enabled, Func<bool>? callerCurrent = null)
    {
        bool wasExcluded = _settings.Value.Excluded.Contains(application, StringComparer.OrdinalIgnoreCase);
        var excluded = enabled ? _settings.Value.Excluded.Where(value => !StringComparer.OrdinalIgnoreCase.Equals(value, application)).ToArray()
            : _settings.Value.Excluded.Append(application).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!await SaveAsync(_settings.Value with
        {
            ExcludedApplications = excluded
        }, callerCurrent: callerCurrent))
            return;
        if (!enabled && !wasExcluded)
            Toast(UiTheme.English ? "Disabled in " + application : "已停用「" + application + "」", async () =>
            {
                var undoCurrent = CaptureApplicationUndo();
                if (!_settings.Value.Excluded.Contains(application, StringComparer.OrdinalIgnoreCase))
                    return;
                var restored = _settings.Value.Excluded.Where(value => !StringComparer.OrdinalIgnoreCase.Equals(value, application)).ToArray();
                if (await SaveAsync(_settings.Value with
                {
                    ExcludedApplications = restored
                }, callerCurrent: undoCurrent))
                    Toast(UiTheme.L("已恢复启用", "Enabled again"));
            }, () => _settings.Value.Excluded.Contains(application, StringComparer.OrdinalIgnoreCase));
    }
    private FrameworkElement SupportRow(ApplicationSupportRow row, ApplicationSupportView view, bool checkInSummary = false)
    {
        if (row.Id == "google-chrome")
            return BrowserSupportRow(view, out _, out _);
        object identity = _pageIdentity;
        bool CurrentPage() => IsVisible && ReferenceEquals(identity, _pageIdentity) && !_emergencyShutdown;
        var body = new StackPanel();
        var header = new DockPanel();
        var detailsBody = new StackPanel();
        var details = new Expander { Header = UiStrings.Translate(view.Labels.Details), Content = detailsBody, Margin = new(0, 10, 0, 0) };
        void Run(ApplicationSupportActionView action)
        {
            if (!CurrentPage() || _closeDialog is not null)
                return;
            if (action.Id == ApplicationSupportAction.Refresh)
            {
                _support?.BeginCheck(row.Id);
                Hide(); // The user's target must regain focus before a real input check can complete.
            }
            else if (action.Id == ApplicationSupportAction.OpenInstructions)
                details.IsExpanded = true;
        }
        if (row.PrimaryAction is { } primary && (!checkInSummary || primary.Id != ApplicationSupportAction.Refresh))
        {
            var next = UiTheme.Button(primary.Label, () => Run(primary));
            AutomationProperties.SetAutomationId(next, "ApplicationSupport_" + row.Id + "_" + primary.Id);
            next.IsEnabled = primary.Id != ApplicationSupportAction.Refresh || _settings.Value.Enabled && _model.Ready;
            if (primary.Id == ApplicationSupportAction.Refresh)
                _connectionChecks.Add(next);
            next.Margin = new(12, 0, 0, 0);
            DockPanel.SetDock(next, Dock.Right);
            header.Children.Add(next);
        }
        var label = new StackPanel();
        label.Children.Add(UiTheme.Text(row.Title, 13, bold: true));
        label.Children.Add(UiTheme.Text(row.StatusText, 12, true));
        header.Children.Add(label);
        body.Children.Add(header);
        var reason = UiTheme.Text(row.SummaryText, 12, true);
        reason.Margin = new(0, 8, 0, 0);
        if (row.Reason != ApplicationSupportReason.Paused)
            body.Children.Add(reason);
        detailsBody.Children.Add(UiTheme.Text(row.Description, 12, true));
        detailsBody.Children.Add(UiTheme.Text(row.ReasonText, 12, true));
        detailsBody.Children.Add(UiTheme.Text(row.Guidance, 12, true));
        detailsBody.Children.Add(UiTheme.Text(row.CapabilityText, 12, true));
        detailsBody.Children.Add(UiTheme.Text(view.CapabilityNote, 12, true));
        detailsBody.Children.Add(UiTheme.Text("普通文本框按当前工作模式启动翻译；仅提供文本读取能力的编辑器可预览译文。密码、敏感字段、代码和终端保持安静；未知输入法状态不接受写回。", 12, true));
        if (row.CheckedAt is { } checkedAt)
            detailsBody.Children.Add(UiTheme.Text(checkedAt.ToLocalTime().ToString("g"), 12, true));
        if (row.Details is { Length: > 0 } extra)
            detailsBody.Children.Add(UiTheme.Text(extra, 12, true));
        foreach (var action in row.SecondaryActions.Where(action => !checkInSummary || action.Id != ApplicationSupportAction.Refresh))
        {
            var button = UiTheme.Button(action.Label, () => Run(action));
            AutomationProperties.SetAutomationId(button, "ApplicationSupport_" + row.Id + "_" + action.Id);
            button.IsEnabled = action.Id != ApplicationSupportAction.Refresh || _settings.Value.Enabled && _model.Ready;
            if (action.Id == ApplicationSupportAction.Refresh)
                _connectionChecks.Add(button);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new(0, 8, 0, 0);
            detailsBody.Children.Add(button);
        }
        body.Children.Add(details);
        var card = UiTheme.Border(body, 0, "Panel", 0);
        card.BorderThickness = new(0, 0, 0, 1);
        card.Padding = new(0, 14, 0, 14);
        return card;
    }
    private FrameworkElement BrowserSupportRow(ApplicationSupportView view, out Action connect, out Action install)
    {
        object identity = _pageIdentity;
        bool CurrentPage() => IsVisible && ReferenceEquals(identity, _pageIdentity) && !_emergencyShutdown;
        var body = new StackPanel();
        var header = new DockPanel();
        var status = UiTheme.Text("", 12, true);
        AutomationProperties.SetAutomationId(status, "ChromeConnectionStatus");
        var summary = UiTheme.Text("", 12, true);
        summary.Margin = new(0, 8, 0, 0);
        var explanation = UiTheme.Text("", 12, true);
        var capabilities = UiTheme.Text("", 12, true);
        var detailsBody = new StackPanel();
        detailsBody.Children.Add(explanation);
        detailsBody.Children.Add(capabilities);
        var details = new Expander { Header = UiStrings.Translate(view.Labels.Details), Content = detailsBody, Margin = new(0, 10, 0, 0) };
        ApplicationSupportRow Current() => (_support?.Read(_settings.Value) ?? view).Rows.Single(item => item.Id == "google-chrome");
        var buttons = new Dictionary<ApplicationSupportAction, Button>();
        async void Run(ApplicationSupportAction action)
        {
            if (!CurrentPage() || _closeDialog is not null || _support?.Browser is not { } browser || browser.Busy)
                return;
            if (action == ApplicationSupportAction.Refresh)
                await browser.RefreshAsync();
            else if (action == ApplicationSupportAction.Connect && browser.ExtensionId is { } id)
            {
                if (ConfirmBrowserConnection(UiTheme.L("接入 Chrome", "Connect Chrome"), UiTheme.L("允许本用户的 Chrome 扩展连接 TypeLay？\n\n扩展 ID：", "Allow this user's Chrome extension to connect to TypeLay?\n\nExtension ID: ") + id
                    + UiTheme.L("\n本地程序：", "\nLocal program: ") + browser.HostPath
                    + UiTheme.L("\n\n这会注册本用户的连接，适用于使用此扩展的 Chrome 配置文件。仍需在 Chrome 中完成扩展安装和权限确认。", "\n\nThis registers the connection for this user and Chrome profiles using this extension. Extension installation and permission approval in Chrome are still required.")))
                    if (CurrentPage() && browser.ExtensionId == id)
                        await browser.ConnectAsync(id);
            }
            else if (action == ApplicationSupportAction.Remove && browser.RegisteredOrigin is { } origin)
            {
                if (ConfirmBrowserConnection(UiTheme.L("移除 Chrome 连接", "Remove Chrome connection"), UiTheme.L("停止此扩展的 TypeLay 连接并移除本用户注册？\n\n", "Stop this extension's TypeLay connection and remove this user's registration?\n\n") + origin
                    + UiTheme.L("\n\n扩展本身、浏览器和模型会保留。要再次连接，需要重新确认接入。", "\n\nThe extension, browser and model are kept. Connecting again requires a new confirmation.")))
                    if (CurrentPage() && browser.RegisteredOrigin == origin)
                        await browser.RemoveAsync(origin);
            }
        }
        connect = () => Run(ApplicationSupportAction.Connect);
        foreach (var action in new[] { ApplicationSupportAction.Connect, ApplicationSupportAction.Remove, ApplicationSupportAction.Refresh })
        {
            string label = action switch
            {
                ApplicationSupportAction.Connect => "接入",
                ApplicationSupportAction.Remove => "移除连接",
                _ => "刷新状态"
            };
            var button = UiTheme.Button(label, () => Run(action));
            AutomationProperties.SetAutomationId(button, "ChromeConnection" + action);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new(0, 8, 8, 0);
            buttons[action] = button;
        }
        var actions = new WrapPanel();
        foreach (var button in buttons.Values)
            actions.Children.Add(button);
        detailsBody.Children.Add(actions);
        var localInstall = new WrapPanel();
        void CopyInstallValue(string value)
        {
            if (!CurrentPage() || _closeDialog is not null)
                return;
            try
            {
                Clipboard.SetText(value);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                explanation.Text = UiTheme.L("无法复制，请稍后重试。", "Could not copy. Try again later.");
            }
        }
        var copyManagement = UiTheme.Button("复制扩展管理地址", () => CopyInstallValue("chrome://extensions/"));
        AutomationProperties.SetAutomationId(copyManagement, "ChromeConnectionCopyManagement");
        var copyDirectory = UiTheme.Button("复制组件目录", () =>
        {
            if (_support?.Browser is { } browser)
                CopyInstallValue(browser.ExtensionDirectory);
        });
        AutomationProperties.SetAutomationId(copyDirectory, "ChromeConnectionCopyDirectory");
        foreach (var button in new[] { copyManagement, copyDirectory })
        {
            button.Margin = new(0, 8, 8, 0);
            localInstall.Children.Add(button);
        }
        detailsBody.Children.Add(localInstall);
        void OpenInstallation()
        {
            if (!CurrentPage() || _closeDialog is not null)
                return;
            if (_support?.Browser?.StoreUrl is { } url)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    explanation.Text = UiTheme.L("无法打开浏览器。请检查默认浏览器设置后重试。", "Could not open the browser. Check the default browser settings and retry.");
                }
            }
            else
                details.IsExpanded = true; // Actual local installation path and extension-management address.
        }
        install = OpenInstallation;
        var store = UiTheme.Button("打开 Chrome 网上应用店", OpenInstallation);
        store.HorizontalAlignment = HorizontalAlignment.Left;
        store.Margin = new(0, 8, 0, 0);
        detailsBody.Children.Add(store);
        var primary = UiTheme.Button("", () =>
        {
            if (!CurrentPage() || _closeDialog is not null)
                return;
            var browser = _support?.Browser;
            if (browser?.CanConnect == true)
                Run(ApplicationSupportAction.Connect);
            else if (browser?.CanRemove == true)
                Run(ApplicationSupportAction.Remove);
            else
                details.IsExpanded = true;
        });
        primary.Margin = new(12, 0, 0, 0);
        DockPanel.SetDock(primary, Dock.Right);
        header.Children.Add(primary);
        var labelPanel = new StackPanel();
        labelPanel.Children.Add(UiTheme.Text("Google Chrome", 13, bold: true));
        labelPanel.Children.Add(status);
        header.Children.Add(labelPanel);
        body.Children.Add(header);
        body.Children.Add(summary);
        body.Children.Add(details);
        void Update()
        {
            var current = Current();
            var browser = _support?.Browser;
            bool busy = browser?.Busy == true;
            status.Text = UiStrings.Translate(browser?.MutationUnavailable == true ? "操作结果未确认" : busy ? "正在更新连接…"
                : browser?.Suspended == true ? UiTheme.L("注册仍在，连接已停止", "Registered, connection stopped")
                : current.State is ApplicationSupportState.Connected or ApplicationSupportState.Registered or ApplicationSupportState.Error
                ? current.StatusText : browser?.ExtensionId is null ? "待启用扩展" : current.StatusText);
            summary.Text = browser?.Suspended == true ? UiTheme.L("可重试移除，或确认重新接入。", "Retry removal or confirm a new connection.")
                : current.State == ApplicationSupportState.Connected ? UiTheme.L("浏览器已连接，当前输入框的支持情况见详情。", "Browser connected. See details for the current field's capabilities.")
                : current.State == ApplicationSupportState.Registered ? UiTheme.L("已接入，等待浏览器连接。", "Registered; waiting for the browser to connect.") : UiTheme.L("在详情中查看接入方式。", "See details for connection instructions.");
            explanation.Text = current.Details ?? UiTheme.L("此发行版尚未配置 Chrome 连接。", "Chrome connection is not configured in this installation.");
            capabilities.Text = UiStrings.Translate(current.CapabilityText);
            foreach (var (action, button) in buttons)
            {
                bool available = action switch
                {
                    ApplicationSupportAction.Connect => browser?.CanConnect == true,
                    ApplicationSupportAction.Remove => browser?.CanRemove == true,
                    _ => true
                };
                button.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                button.IsEnabled = !busy && browser is not null;
            }
            primary.Content = UiStrings.Translate(browser?.CanConnect == true ? "接入" : browser?.CanRemove == true ? "移除连接" : "查看详情");
            primary.IsEnabled = !busy;
            AutomationProperties.SetAutomationId(primary, "ChromeConnectionPrimary");
            store.Visibility = browser?.StoreUrl is not null ? Visibility.Visible : Visibility.Collapsed;
            localInstall.Visibility = browser?.StoreUrl is null ? Visibility.Visible : Visibility.Collapsed;
            copyDirectory.IsEnabled = browser is not null;
        }
        _refreshBrowserRow = Update;
        Update();
        var card = UiTheme.Border(body, 0, "Panel", 0);
        AutomationProperties.SetAutomationId(card, "ChromeConnection");
        card.BorderThickness = new(0, 0, 0, 1);
        card.Padding = new(0, 14, 0, 14);
        return card;
    }
    private bool ConfirmBrowserConnection(string title, string message)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = UiStrings.Translate(title),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        AutomationProperties.SetAutomationId(dialog, "ChromeConnectionConfirmation");
        dialog.SetResourceReference(BackgroundProperty, "Panel");
        var content = new StackPanel { Margin = new(22) };
        content.Children.Add(UiTheme.Text(message, 13));
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        var cancel = UiTheme.Button("取消", () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = false; });
        AutomationProperties.SetAutomationId(cancel, "ChromeConnectionCancel");
        cancel.IsCancel = true;
        cancel.Margin = new(0, 0, 8, 0);
        var confirm = UiTheme.Button("确认", () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = true; });
        AutomationProperties.SetAutomationId(confirm, "ChromeConnectionConfirm");
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        content.Children.Add(actions);
        dialog.Content = content;
        dialog.Loaded += (_, _) => cancel.Focus();
        _closeDialog = dialog;
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally { _closeDialog = null; }
    }
}
