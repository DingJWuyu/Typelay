using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using TypeLay.Core;
namespace TypeLay.App;

internal sealed class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly ModelClient _model;
    private readonly StackPanel _content;
    private readonly StackPanel _nav;
    private readonly Action _openTutorial;
    private string _page = "general", _setupStatus = "";
    private string? _settingsError;
    public Func<string>? ShortcutStatus
    {
        get; set;
    }
    private CancellationTokenSource? _install;
    private ProgressBar? _progress;
    private TextBlock? _progressLabel;
    public Action? SessionMustEnd;
    public SettingsWindow(SettingsStore settings, ModelClient model, Action openTutorial)
    {
        _settings = settings;
        _model = model;
        _openTutorial = openTutorial;
        Title = "TypeLay 设置";
        Width = 944;
        Height = 674;
        MinWidth = 790;
        MinHeight = 560;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new("Segoe UI");
        var root = new Grid();
        root.RowDefinitions.Add(new()
        {
            Height = new(48)
        });
        root.RowDefinitions.Add(new());
        var title = new DockPanel { Margin = new(14, 0, 12, 0) };
        var close = UiTheme.Button("×", Hide);
        close.Width = 34;
        close.Height = 30;
        close.BorderThickness = new(0);
        close.Background = Brushes.Transparent;
        DockPanel.SetDock(close, Dock.Right);
        title.Children.Add(close);
        title.Children.Add(UiTheme.Text("T   TypeLay 设置", 12.5, bold: true));
        title.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
        var titlebar = UiTheme.Border(title, 0, "Panel2");
        titlebar.BorderThickness = new(0, 0, 0, 1);
        root.Children.Add(titlebar);
        var body = new Grid();
        body.ColumnDefinitions.Add(new()
        {
            Width = new(205)
        });
        body.ColumnDefinitions.Add(new());
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        _nav = new StackPanel { Margin = new(9, 12, 9, 12) };
        var navBorder = UiTheme.Border(_nav, 0, "Panel2");
        navBorder.BorderThickness = new(0, 0, 1, 0);
        body.Children.Add(navBorder);
        _content = new StackPanel { Margin = new(34, 27, 34, 38) };
        var scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1);
        body.Children.Add(scroll);
        var outer = UiTheme.Border(root, 20);
        outer.Margin = new(12);
        outer.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = .18 };
        Content = outer;
        root.SizeChanged += (_, _) => root.Clip = new RectangleGeometry(new Rect(0, 0, root.ActualWidth, root.ActualHeight), 19, 19);
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        settings.Changed += Refresh;
        model.Changed += Refresh;
        Refresh();
    }
    public void ShowPage(string page = "general")
    {
        SessionMustEnd?.Invoke();
        _page = page;
        Refresh();
        if (!IsVisible)
            Show();
        Activate();
    }
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }
        _nav.Children.Clear();
        foreach (var (key, title) in new[] { ("general", "⌂  常规"), ("language", "文  语言"), ("shortcuts", "⌨  快捷键"),
                     ("apps", "▦  应用"), ("translation", "A  翻译"), ("glossary", "≡  术语表"), ("about", "i  关于与隐私") })
        {
            var button = UiTheme.Button(title, () => { _page = key; Refresh(); });
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Height = 38;
            button.Margin = new(0, 0, 0, 3);
            button.BorderThickness = new(0);
            button.SetResourceReference(Control.BackgroundProperty, key == _page ? "AccentSoft" : "Panel2");
            if (key == _page)
            {
                button.FontWeight = FontWeights.SemiBold;
                button.SetResourceReference(Control.ForegroundProperty, "Accent");
            }
            _nav.Children.Add(button);
        }
        _content.Children.Clear();
        _progress = null;
        _progressLabel = null;
        if (_settingsError is { } error)
            Note("设置未保存", error);
        switch (_page)
        {
            case "general":
                General();
                break;
            case "language":
                Languages();
                break;
            case "shortcuts":
                Shortcuts();
                break;
            case "apps":
                Applications();
                break;
            case "translation":
                Translation();
                break;
            case "glossary":
                Glossary();
                break;
            default:
                About();
                break;
        }
    }
    private void Heading(string name, string description)
    {
        _content.Children.Add(UiTheme.Text(name, 22, bold: true));
        var sub = UiTheme.Text(description, 12, true);
        sub.Margin = new(0, 6, 0, 24);
        _content.Children.Add(sub);
    }
    private void Rule()
    {
        var line = new Border { Height = 1, Margin = new(0, 18, 0, 18) };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        _content.Children.Add(line);
    }
    private void Row(string name, string description, UIElement control)
    {
        var grid = new Grid { MinHeight = 58, Margin = new(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 20, 0) };
        texts.Children.Add(UiTheme.Text(name, 12.5));
        if (description.Length > 0)
        {
            var desc = UiTheme.Text(description, 10.5, true);
            desc.Margin = new(0, 3, 0, 0);
            texts.Children.Add(desc);
        }
        grid.Children.Add(texts);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        _content.Children.Add(grid);
    }
    private void Note(string title, string text)
    {
        var panel = new StackPanel();
        panel.Children.Add(UiTheme.Text(title, 12, bold: true));
        var body = UiTheme.Text(text, 11, true);
        body.Margin = new(0, 5, 0, 0);
        body.LineHeight = 17;
        panel.Children.Add(body);
        var box = UiTheme.Border(panel, 13, "Panel2", 14);
        box.Margin = new(0, 4, 0, 12);
        _content.Children.Add(box);
    }
    private void Save(UserSettings value)
    {
        try
        {
            _settingsError = null;
            _settings.Update(value);
        }
        catch (ArgumentException)
        {
            _settingsError = "请使用 Ctrl / Alt（可加 Shift）与字母、数字或 F1-F24 的组合，排除 F12。两个按需快捷键不能相同。";
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _settingsError = "请检查当前用户目录与组织策略。";
            Refresh();
        }
    }
    private void General()
    {
        Heading("常规", "TypeLay 的全局运行方式。");
        if (!_model.Ready)
            InstallCard();
        Row("启用 TypeLay", "暂停后保留托盘入口，输入框旁不显示翻译界面。", UiTheme.Toggle(_settings.Value.Enabled, () => Save(_settings.Value with { Enabled = !_settings.Value.Enabled })));
        var modes = new ComboBox
        {
            MinWidth = 155,
            ItemsSource = new[] { "自动模式（默认）", "按需模式" },
            SelectedIndex = _settings.Value.Mode == WorkMode.OnDemand ? 1 : 0
        };
        System.Windows.Automation.AutomationProperties.SetName(modes, "工作模式");
        modes.SelectionChanged += (_, _) => Save(_settings.Value with { Mode = modes.SelectedIndex == 1 ? WorkMode.OnDemand : WorkMode.Automatic });
        Row("工作模式", "自动模式保留实时翻译；按需模式保持静默，仅通过快捷键启动。", modes);
        Row("开发版实验：允许 Tab 写回", "默认关闭。跨进程非原子写回尚未完成实机安全验收；仅使用已备份的测试文本。", UiTheme.Toggle(_settings.Value.ExperimentalWriteback, () =>
        {
            if (_settings.Value.ExperimentalWriteback)
            {
                Save(_settings.Value with
                {
                    ExperimentalWriteback = false
                });
                return;
            }
            var answer = MessageBox.Show(this, "此构建未完成 Windows 实机与真实输入法验收。\n\nUIA 校验与写回之间仍有竞态：极端情况下可能覆盖最新原文，或拒写后消耗一次 Tab。该开关仅用于受控测试，不代表安全验收通过。\n\n请只用已备份的测试文本。是否允许实验性 Tab 写回？", "TypeLay · 实验性写回", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes)
                Save(_settings.Value with
                {
                    ExperimentalWriteback = true
                });
        }));
        Row("登录 Windows 后自动启动", "默认关闭。开启后依赖当前程序所在目录，请勿移动。", UiTheme.Toggle(_settings.Value.Startup, () => Save(_settings.Value with { Startup = !_settings.Value.Startup })));
        var themes = new ComboBox { MinWidth = 155, ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = _settings.Value.Theme switch { "light" => 1, "dark" => 2, _ => 0 } };
        themes.SelectionChanged += (_, _) => Save(_settings.Value with { Theme = themes.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" } });
        Row("外观", "使用系统字体和低干扰浮层。", themes);
        Rule();
        Note("文本与译文保留在本机", "推理完全在本地进行；默认不保存翻译历史，不需要账号或云同步。");
        Row("实操教程", "输入 → 本地译文 → Tab；编辑 → Tab 返回 → Tab 接受。", UiTheme.Button("打开教程", _openTutorial));
    }
    private void Languages()
    {
        Heading("语言", "Auto 只用于源语言，目标语言始终明确指定。");
        Row("源语言", "Auto 使用本地语言检测。当前已安装模型仅覆盖中文与英文。", LanguageCombo(true, _settings.Value, value => Save(value)));
        Row("目标语言", "选择希望写回的语言。", LanguageCombo(false, _settings.Value, value => Save(value)));
        var swap = UiTheme.Button("交换", () => Save(_settings.Value with { Source = _settings.Value.Target, Target = _settings.Value.Source }));
        swap.IsEnabled = _settings.Value.Source != "auto";
        Row("交换语言", "只改变语言配置，不修改当前原文或译文。", swap);
        Rule();
        Note("当前支持范围", "中文 ⇄ English。其他语言方向、下载选择器和更多语言模型尚未开放，界面不会把未安装的模型标成可用。");
    }
    internal static ComboBox LanguageCombo(bool source, UserSettings settings, Action<UserSettings> save)
    {
        var options = new List<LanguageChoice>();
        if (source)
            options.Add(new("auto", "Auto"));
        options.Add(new("zh", "中文"));
        options.Add(new("en", "English"));
        var combo = new ComboBox
        {
            ItemsSource = options,
            MinWidth = 126,
            IsEditable = true,
            IsTextSearchEnabled = true,
            StaysOpenOnEdit = true,
            SelectedItem = options.First(o => o.Code == (source ? settings.Source : settings.Target))
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is LanguageChoice choice)
                save(source ? settings with
                {
                    Source = choice.Code
                } : settings with
                {
                    Target = choice.Code
                });
        };
        combo.LostKeyboardFocus += (_, _) => { if (combo.SelectedItem is LanguageChoice choice) combo.Text = choice.Label; };
        return combo;
    }
    private sealed record LanguageChoice(string Code, string Label)
    {
        public override string ToString() => Label;
    }
    private void Shortcuts()
    {
        Heading("快捷键", "Tab 与 Esc 保留原有行为。两个按需快捷键可以分别配置。");
        Row("接受译文", "仅在候选与原文对应、焦点和输入法状态明确时尝试写回。", UiTheme.Text("Tab", 13, bold: true));
        Row("编辑后返回原输入框", "第一次 Tab 返回；确认焦点后，下一次 Tab 接受。", UiTheme.Text("Tab → Tab", 13, bold: true));
        Row("退出当前翻译", "不修改原文，不发送、不提交。组合输入时优先交还输入法。", UiTheme.Text("Esc", 13, bold: true));
        Rule();
        var wake = ShortcutInput(_settings.Value.WakeShortcut, "唤醒翻译 UI 快捷键");
        var direct = ShortcutInput(_settings.Value.DirectShortcut, "直接翻译并替换快捷键");
        Row("唤醒翻译 UI", "为当前输入框开启持续翻译；离开后结束。", wake);
        Row("直接翻译并替换", "一次性操作，绑定原输入框，不显示预览、不自动发送。", direct);
        var save = UiTheme.Button("保存按需快捷键", () => Save(_settings.Value with { WakeShortcut = wake.Text, DirectShortcut = direct.Text }));
        save.HorizontalAlignment = HorizontalAlignment.Right;
        save.Margin = new(0, 8, 0, 14);
        _content.Children.Add(save);
        Note("按需快捷键状态", ShortcutStatus?.Invoke() ?? "仅在启用 TypeLay 的按需模式下生效。自动模式不注册这两个快捷键。");
        Note("直接替换的当前范围", "Windows x64 中通过运行时校验的原生 Edit 控件支持后台写回。翻译时可以切换焦点，结果仍只写回原输入框；冲突、输入法状态不明或不兼容控件会安全拒写。不会抢回焦点，也不使用实验性 Tab 写回作为后备。结果与失败原因可在托盘快速面板查看。");
        Note("组合键格式", "例如 Ctrl+Shift+F9。支持 Ctrl / Alt（可加 Shift）与字母、数字或 F1-F24，排除系统保留的 F12。组合键被其他应用占用时，设置仍保存，但对应命令不会启用。");
        Rule();
        Note("保留原应用快捷键", "Ctrl / Alt / Shift / Win 与 Tab 的组合交还原应用。没有有效候选时，Tab 不执行接受。");
    }
    private static TextBox ShortcutInput(string value, string label)
    {
        var input = new TextBox
        {
            Text = value,
            Width = 178,
            MaxLength = 64,
            Padding = new(9, 8, 9, 8),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        return input;
    }
    private void Applications()
    {
        Heading("应用", "永久排除的应用中不显示任何 TypeLay UI。");
        if (_settings.Value.Excluded.Length == 0)
            Note("当前没有排除任何应用", "自动识别当前应用的可编辑文本区域，无需选择输入框类型或添加标签。");
        foreach (string app in _settings.Value.Excluded)
            Row(app + ".exe", "所有该进程名窗口均排除。", UiTheme.Button("移除", () => Save(_settings.Value with { ExcludedApplications = _settings.Value.Excluded.Where(a => a != app).ToArray() })));
        var add = UiTheme.Button("添加应用…", () =>
        {
            var dialog = new OpenFileDialog { Filter = "Windows 应用 (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true)
                Save(_settings.Value with
                {
                    ExcludedApplications = _settings.Value.Excluded.Append(Path.GetFileNameWithoutExtension(dialog.FileName).ToLowerInvariant()).Distinct().Take(256).ToArray()
                });
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new(0, 12, 0, 0);
        _content.Children.Add(add);
        Rule();
        Note("按控件能力提供功能", "普通文本框按当前工作模式启动翻译；仅提供文本读取能力的编辑器可预览译文。密码、敏感字段、代码和终端保持安静；未知输入法状态不接受写回。");
    }
    private void Translation()
    {
        Heading("翻译", "控制本地模型状态。当前模型采用自然、忠实的单一候选。");
        Row("默认风格", "其他风格尚未接入模型，不提供虚假切换。", new ComboBox { MinWidth = 155, ItemsSource = new[] { "自然" }, SelectedIndex = 0, IsEnabled = false });
        Row("本地翻译模型", "CTranslate2 · Argos 中英模型 · CPU INT8", UiTheme.Text(_model.Status, 11, true));
        InstallCard();
        Rule();
        Note("较长文本", "当前单段模型输入超过 384 个 token 时停止并提示缩短选区；不会在后台截断原文后给出可接受候选。换行、URL、mention 和编号采用显式结构保护。");
    }
    private void InstallCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(UiTheme.Text(_model.Installed ? "本地模型与运行组件" : "首次使用 · 准备本地翻译", 13, bold: true));
        var intro = UiTheme.Text(ModelInstaller.HasReusableLocalModels
            ? "已找到本机中英双向 Argos 模型。准备时会校验并复用权重，运行组件仍需单独准备；输入文字不会上传。"
            : "准备私有运行组件与中英模型。仅在本机推理；无需自行安装 Python，也不上传输入文字。", 11, true);
        intro.Margin = new(0, 6, 0, 12);
        panel.Children.Add(intro);
        _progressLabel = UiTheme.Text(_setupStatus.Length > 0 ? _setupStatus : _model.Status, 11, true);
        panel.Children.Add(_progressLabel);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new(0, 9, 0, 12), IsIndeterminate = _install is not null };
        panel.Children.Add(_progress);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var button = UiTheme.Button(_install is not null ? "暂停下载" : _model.Installed ? "重新安装 / 修复" : "检查并准备", () =>
        {
            if (_install is not null)
                _install.Cancel();
            else
                _ = Install();
        }, _install is null);
        button.Margin = new(0, 0, 8, 0);
        actions.Children.Add(button);
        var runtime = UiTheme.Button("微软运行库说明", () => OpenUri("https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist"));
        actions.Children.Add(runtime);
        panel.Children.Add(actions);
        var border = UiTheme.Border(panel, 13, "Panel2", 14);
        border.Margin = new(0, 0, 0, 16);
        _content.Children.Add(border);
    }
    private async Task Install()
    {
        SessionMustEnd?.Invoke();
        _model.Dispose();
        _install = new CancellationTokenSource();
        Refresh();
        try
        {
            var progress = new Progress<SetupProgress>(update =>
            {
                _setupStatus = update.Label + (update.Downloaded > 0 ? $" · {update.Downloaded / 1048576.0:F1} MB" : "");
                if (_progressLabel is not null)
                    _progressLabel.Text = _setupStatus;
                if (_progress is not null)
                {
                    _progress.IsIndeterminate = update.Total is null;
                    if (update.Total is > 0)
                        _progress.Value = 100.0 * update.Downloaded / update.Total.Value;
                }
            });
            await new ModelInstaller().InstallAsync(progress, _install.Token);
            await _model.StartAsync();
            if (_model.Ready)
            {
                Save(_settings.Value with
                {
                    OnboardingComplete = true
                });
                _setupStatus = "本地翻译已就绪。可打开实操教程。";
            }
        }
        catch (OperationCanceledException) { _setupStatus = "下载已暂停，已下载的部分会保留。再次点击继续。"; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _setupStatus = "准备未完成。网络、下载完整性或本地 CPU 自检失败；缺少 VC++ x64 运行库时请先安装微软官方组件，再重试。";
        }
        finally { _install?.Dispose(); _install = null; Refresh(); }
    }
    private void Glossary()
    {
        Heading("术语表", "显式术语能力计划保留；本次开发版尚未接入。");
        Note("当前不保存或应用术语规则", "人工编辑只属于当前会话，不会自动学习。为避免误导，此页面暂不提供可保存的编辑表格。");
        var add = UiTheme.Button("添加术语", () => { });
        add.IsEnabled = false;
        add.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Children.Add(add);
    }
    private void About()
    {
        Heading("关于与隐私", "TypeLay · Windows Development Build 0.2.0");
        Note("完全本地的输入翻译", "实际输入与译文不上传云端，不建立翻译历史数据库。运行组件与模型下载使用 HTTPS；推理子进程没有网络下载路径。");
        Row("本次交付状态", "这是开发版。已完成本机 Windows 构建和 Edge 教程验证；真实中文输入法、独立网站及跨应用适配尚未完成验收。", UiTheme.Text("部分验收", 11, true));
        Row("本地数据", "设置、模型、下载缓存、安装锁与无正文的安装自检结果。", UiTheme.Button("打开目录", () => Process.Start(new ProcessStartInfo(AppPaths.Data) { UseShellExecute = true })));
        Rule();
        Note("范围与已知风险", "当前支持策略严格依赖控件元数据，无法保证所有网页文本框均可用。UIA 的跨进程写回存在非原子竞态；极窄时间窗内可能覆盖最新原文，或安全拒写后消耗一次 Tab。默认关闭实验性写回。该问题没有被标为已解决，当前包不具备日常中文输入验收结论。");
    }
    private static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}
