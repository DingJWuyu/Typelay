using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    // A finite projection of the settings that actually exist. No hidden pages
    // are constructed to search them, and a result never invokes a setting.
    private sealed record SearchSetting(string Page, string Key, string Chinese, string English, string Keywords = "")
    {
        internal string Label => UiTheme.L(Chinese, English);
    }

    private static readonly SearchSetting[] SearchSettings =
    [
        new("general", "enable", "TypeLay", "TypeLay", "启用 enable"),
        new("general", "mode", "工作模式", "Mode", "自动 按需 automatic on demand"),
        new("general", "startup", "登录时启动", "Open at login", "Windows startup 自动启动"),
        new("general", "writeback", "原生输入框替换", "Native field replacement", "beta"),
        new("general", "tutorial", "使用教程", "Tutorial", "翻译练习 Translation practice"),
        new("general", "setup", "首次设置", "First-run setup", "重新设置 重新运行 Run again onboarding"),
        new("appearance", "theme", "外观", "Appearance", "主题 theme 自动 auto 浅色 light 深色 dark"),
        new("appearance", "accent", "强调色", "Accent color"),
        new("appearance", "font", "字体", "Font"),
        new("appearance", "size", "译文字号", "Translation size", "text size"),
        new("appearance", "uilang", "界面语言", "Interface language"),
        new("appearance", "transparency", "降低透明度", "Reduce transparency"),
        new("appearance", "motion", "动态效果", "Motion", "减少动态效果 reduce motion 动画 animation 完整 full"),
        new("language", "direction", "源语言", "From", "目标语言 翻译方向 source target to direction"),
        new("language", "style", "默认风格", "Default tone", "style 自然 正式 natural formal"),
        new("glossary", "glossary", "原文", "Source", "术语 译文 范围 terms translation scope"),
        new("shortcuts", "accept", "接受译文", "Accept translation"),
        new("shortcuts", "exit", "退出翻译", "Exit translation"),
        new("shortcuts", "wake", "唤醒翻译", "Wake translation"),
        new("shortcuts", "direct", "直接翻译并替换", "Translate and replace"),
        new("shortcuts", "pause", "暂停或恢复", "Pause or resume", "TypeLay"),
        new("apps", "applist", "应用列表", "Application list", "启用 排除 风格 enable exclude style"),
        new("apps", "desktop", "桌面应用", "Desktop apps", "其他普通输入框 Other text fields desktop"),
        new("apps", "browser", "浏览器扩展", "Browser extension", "Google Chrome 连接 browser connection"),
        new("model", "model", "本地翻译模型", "Local translation model"),
        new("model", "selection", "选择模型", "Select model", "Index Argos Hy"),
        new("model", "preparation", "准备翻译", "Prepare Translation", "下载 修复 download repair"),
        new("model", "device", "计算设备", "Compute device", "CPU GPU NPU"),
        new("model", "resident", "常驻内存", "Keep in memory", "模型常驻 Keep model in memory"),
        new("model", "update", "模型更新", "Model updates"),
        new("about", "checkupdate", "软件更新", "Software updates"),
        new("about", "data", "本地数据", "Local data"),
        new("about", "privacy", "完全本地的输入翻译", "Translation stays on your device", "隐私 历史 账号 privacy history account")
    ];

    private readonly Dictionary<string, Panel> _searchTargets = [];
    private object _settingsQuery = new();
    private object? _searchResults;
    private Action? _firstSearchResult;
    private Action? _clearSearchFlash;
    private Style? _searchResultStyle;
    private object? _searchScrollIdentity;
    private Action? _cancelSearchScroll;

    private void SettingsQueryChanged()
    {
        _settingsQuery = new();
        ClearSearchLocation();
        RefreshNavigation();
    }

    private void SettingsSearchKey(object sender, KeyEventArgs e)
    {
        if (!IsVisible || !_settingsSearch.IsKeyboardFocused)
            return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _settingsSearch.Clear();
        }
        else if (e.Key == Key.Enter && _settingsSearch.Text.Trim().Length > 0)
        {
            e.Handled = true;
            _firstSearchResult?.Invoke();
        }
    }

    private bool RenderSettingsSearch()
    {
        _firstSearchResult = null;
        object results = _searchResults = new();
        string query = _settingsSearch.Text.Trim();
        if (query.Length == 0)
            return false;
        object search = _settingsQuery;
        var hits = SearchSettings.Where(setting =>
        {
            var page = Pages.Single(page => page.Id == setting.Page);
            return (setting.Chinese + " " + setting.English + " " + setting.Keywords + " " + page.Chinese + " " + page.English)
                .Contains(query, StringComparison.OrdinalIgnoreCase);
        }).Take(12).ToArray();
        foreach (var setting in hits)
        {
            var page = Pages.Single(page => page.Id == setting.Page);
            string pageName = UiTheme.L(page.Chinese, page.English);
            var labels = new StackPanel();
            var label = UiTheme.Text(setting.Label, 13);
            label.LineHeight = 13;
            label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            labels.Children.Add(label);
            var detail = UiTheme.Text(pageName, 11, true);
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Tertiary");
            detail.Margin = new(0, 2, 0, 0);
            detail.LineHeight = 11;
            detail.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            labels.Children.Add(detail);
            void Open()
            {
                if (IsVisible && _closeDialog is null && ReferenceEquals(results, _searchResults) && ReferenceEquals(search, _settingsQuery))
                    NavigatePage(setting.Page, clearSearch: false, searchRow: setting.Key, search: search);
            }
            _firstSearchResult ??= Open;
            var button = UiTheme.Button("", Open);
            button.Style = _searchResultStyle ??= (Style)XamlReader.Parse(SearchResultStyle);
            button.Content = labels;
            button.MinHeight = 32;
            button.Padding = new(8, 6, 8, 6);
            button.Margin = new(0, 0, 0, 1);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.BorderThickness = new(0);
            button.Background = Brushes.Transparent;
            AutomationProperties.SetName(button, setting.Label + " · " + pageName);
            AutomationProperties.SetAutomationId(button, "SettingsSearchResult_" + setting.Page + "_" + setting.Key);
            _nav.Children.Add(button);
        }
        if (hits.Length == 0)
        {
            var empty = UiTheme.Text("—", 13, true);
            empty.Margin = new(8);
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Tertiary");
            AutomationProperties.SetName(empty, UiTheme.L("没有找到设置", "No matching settings"));
            AutomationProperties.SetAutomationId(empty, "SettingsSearchEmpty");
            _nav.Children.Add(empty);
        }
        return true;
    }

    private const string SearchResultStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Setter Property="Foreground" Value="{DynamicResource Text}"/>
          <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
            <Grid>
              <Border x:Name="surface" CornerRadius="8" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
              </Border>
              <Border x:Name="focus" CornerRadius="6" BorderBrush="{DynamicResource AccentSoft}" BorderThickness="3" Margin="-1" Visibility="Collapsed" IsHitTestVisible="False"/>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="surface" Property="Background" Value="{DynamicResource SettingsLanguageFill}"/></Trigger>
              <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="focus" Property="Visibility" Value="Visible"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """;

    private void RegisterSearchRow(string label, Panel row)
    {
        var setting = SearchSettings.SingleOrDefault(setting => setting.Page == _page
            && (setting.Chinese == label || setting.English == label));
        if (setting is not null)
            RegisterSearchTarget(setting.Key, row);
    }

    private void RegisterSearchTarget(string key, Panel row)
    {
        _searchTargets[key] = row;
        AutomationProperties.SetAutomationId(row, "SettingsRow_" + _page + "_" + key);
    }

    private void LocateSearchRow(string key, object navigation, object query)
    {
        if (!IsVisible || !ReferenceEquals(navigation, _pageNavigation) || !ReferenceEquals(query, _settingsQuery)
            || !_searchTargets.TryGetValue(key, out var row))
            return;
        object page = _pageIdentity;
        bool Current() => IsVisible && ReferenceEquals(page, _pageIdentity) && ReferenceEquals(navigation, _pageNavigation)
            && ReferenceEquals(query, _settingsQuery) && _searchTargets.TryGetValue(key, out var current)
            && ReferenceEquals(current, row) && row.IsVisible && row.IsDescendantOf(_content);
        // Reveal an existing connection-detail ancestor, never invoke its
        // connect, check, download, or other action to make a result visible.
        for (DependencyObject? parent = row.Parent; parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is Expander details)
                details.IsExpanded = true;
        UpdateLayout();
        if (!Current())
            return;
        double top = row.TranslatePoint(new Point(), _pageScroll).Y;
        double center = Math.Max(0, (_pageScroll.ViewportHeight - row.ActualHeight) / 2);
        double destination = Math.Clamp(_pageScroll.VerticalOffset + top - center, 0, _pageScroll.ScrollableHeight);
        if (UiTheme.ReduceMotion)
        {
            _pageScroll.ScrollToVerticalOffset(destination);
            UpdateLayout();
            if (!Current())
                return;
        }
        else
            ScrollToSearchRow(row, key, destination, navigation, query);
        if (Current())
            FlashSearchRow(row);
    }

    private void ScrollToSearchRow(Panel row, string key, double destination, object navigation, object query)
    {
        object identity = _searchScrollIdentity = new();
        object page = _pageIdentity;
        double start = _pageScroll.VerticalOffset;
        var animation = new DoubleAnimation(start, destination, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var clock = animation.CreateClock();
        bool Current() => IsVisible && ReferenceEquals(identity, _searchScrollIdentity)
            && ReferenceEquals(page, _pageIdentity) && ReferenceEquals(navigation, _pageNavigation)
            && ReferenceEquals(query, _settingsQuery) && _searchTargets.TryGetValue(key, out var current)
            && ReferenceEquals(current, row) && row.IsVisible && row.IsDescendantOf(_content);
        void Tick(object? sender, EventArgs args)
        {
            if (!Current())
            {
                if (ReferenceEquals(identity, _searchScrollIdentity))
                    ClearSearchLocation();
                return;
            }
            _pageScroll.ScrollToVerticalOffset(animation.GetCurrentValue(start, destination, clock));
        }
        void Completed(object? sender, EventArgs args)
        {
            if (Current())
                _pageScroll.ScrollToVerticalOffset(destination);
            if (ReferenceEquals(identity, _searchScrollIdentity))
                CancelSearchScroll();
        }
        _cancelSearchScroll = () =>
        {
            clock.CurrentTimeInvalidated -= Tick;
            clock.Completed -= Completed;
            clock.Controller?.Remove();
        };
        clock.CurrentTimeInvalidated += Tick;
        clock.Completed += Completed;
        clock.Controller?.Begin();
    }

    private void CancelSearchScroll()
    {
        _searchScrollIdentity = null;
        var cancel = _cancelSearchScroll;
        _cancelSearchScroll = null;
        cancel?.Invoke();
    }

    private void ClearSearchLocation()
    {
        CancelSearchScroll();
        ClearSearchFlash();
    }

    private void FlashSearchRow(Panel row)
    {
        ClearSearchFlash();
        Brush? previous = row.Background;
        var tint = ((SolidColorBrush)FindResource("AccentSoft")).Clone();
        row.Background = tint;
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.4), FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(UiTheme.ReduceMotion ? 1.4 : .56))));
        if (!UiTheme.ReduceMotion)
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        Action clear = () =>
        {
            tint.BeginAnimation(Brush.OpacityProperty, null);
            if (ReferenceEquals(row.Background, tint))
                row.Background = previous;
        };
        _clearSearchFlash = clear;
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(_clearSearchFlash, clear))
                ClearSearchFlash();
        };
        tint.BeginAnimation(Brush.OpacityProperty, fade);
    }

    private void ClearSearchFlash()
    {
        var clear = _clearSearchFlash;
        _clearSearchFlash = null;
        clear?.Invoke();
    }
}
