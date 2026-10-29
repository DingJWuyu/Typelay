using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private LanguageConfiguration? _languageConfiguration;
    private bool _languageWindowClosed;
    public Action<Rect>? NativeLanguageRequested
    {
        get; set;
    }

    // This object contains configuration presentation only. In particular it
    // never retains a source Target, SessionViewToken, candidate or input permit.
    private sealed class LanguageConfiguration(SettingsStore settings, ModelClient model,
        Func<string, string, Func<bool>, Task<bool>> save)
    {
        public SettingsStore Settings { get; } = settings;
        public ModelClient Model { get; } = model;
        public Func<string, string, Func<bool>, Task<bool>> Save { get; } = save;
        public UserSettings Expected { get; } = settings.Value;
        public long Revision { get; } = settings.Revision;
        public bool ModelReady { get; } = model.Ready;
        public IReadOnlyList<string> Catalog { get; } = model.AvailableDirections;
        public object Query { get; set; } = new();
        public bool Retired, Saving, Composing;
        public bool InitialFocusPending = true;
        public int Highlight = -1;
        public TextBox Search { get; } = new();
        public StackPanel Options { get; } = new();
        public TextBlock Empty { get; } = UiTheme.Text(UiTheme.L("没有匹配的语言", "No matching languages"), 12, true);
        public TextBlock Feedback { get; } = UiTheme.Text("", 12, true);
        public ConfigurationPopup Popup { get; } = new();
        public Button Swap { get; set; } = null!;
        public Button Anchor { get; set; } = null!;
        public LanguagePickerChoice[] Choices { get; set; } = [];
        public Style OptionStyle { get; } = LanguageOptionStyle();
        public Action? SettingsChanged, ModelChanged;
    }

    private sealed class ConfigurationPopup : Popup
    {
        private bool _opened;
        public bool Retired
        {
            get; private set;
        }
        public Action? Retire
        {
            get; set;
        }
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            bool closing = e.Property == IsOpenProperty && !(bool)e.NewValue && _opened;
            if (e.Property == IsOpenProperty && (bool)e.NewValue)
                _opened = true;
            if (closing)
                Retired = true;
            base.OnPropertyChanged(e);
            if (closing)
                Retire?.Invoke(); // IsOpen, not the later Routed Closed.
        }
    }

    private void InitializeLanguageConfiguration()
    {
        Deactivated += (_, _) => { if (Native.GetForegroundWindow() != _hwnd) RetireLanguageConfiguration(hide: true); };
        Closed += (_, _) => { _languageWindowClosed = true; RetireLanguageConfiguration(hide: false); };
    }

    private void RequestLanguageConfiguration()
    {
        if (_languageConfiguration is not null || _languageWindowClosed || !IsVisible || EditingComposition || !_languageButton.IsVisible
            || _languageButton.ActualWidth <= 0 || _languageButton.ActualHeight <= 0)
            return;
        // Physical screen geometry is presentation data, never a target binding.
        var first = _languageButton.PointToScreen(new());
        var last = _languageButton.PointToScreen(new(_languageButton.ActualWidth, _languageButton.ActualHeight));
        NativeLanguageRequested?.Invoke(new(first, last));
    }

    public void ShowLanguageConfiguration(Rect anchor, SettingsStore settings, ModelClient model,
        Func<string, string, Func<bool>, Task<bool>> save)
    {
        RetireLanguageConfiguration(hide: true);
        // The composition root must run the existing complete StopSession first.
        // A visible candidate, editor or retained source is not a configuration shell.
        if (_languageWindowClosed || _viewContext is not null || _sourceAnchor is not null || _sourceGlossarySnapshot is not null
            || _ownedEditor is not null || _editing || _editor.Text.Length != 0 || !_editor.IsReadOnly
            || _editor.CanUndo || _editor.Focusable || anchor.IsEmpty || anchor.Width <= 0 || anchor.Height <= 0
            || !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y) || !double.IsFinite(anchor.Width) || !double.IsFinite(anchor.Height))
            return;
        var state = _languageConfiguration = new LanguageConfiguration(settings, model, save);
        state.Choices = LanguagePickerChoice.Create(false, state.Expected.Target,
            state.ModelReady ? state.Catalog : null, searchPopover: true);
        var label = UiTheme.Text(DirectionCode(settings.Value.Source) + " → " + DirectionCode(settings.Value.Target), 12, true);
        state.Anchor = new Button
        {
            Content = label,
            Height = 26,
            Padding = new(9, 0, 9, 0),
            Background = Brushes.Transparent,
            BorderThickness = new(0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(state.Anchor, UiTheme.L("翻译语言", "Translation Languages"));
        AutomationProperties.SetAutomationId(state.Anchor, "ConfigurationLanguageAnchor");
        state.Anchor.Click += (_, _) => { if (ReferenceEquals(state, _languageConfiguration)) RetireLanguageConfiguration(hide: true); };
        var capsule = UiTheme.Border(state.Anchor, 18, "Glass");
        capsule.Padding = new(5);
        capsule.Margin = new(12);
        Content = capsule;
        BuildLanguageConfiguration(state);
        state.SettingsChanged = () => Dispatcher.InvokeAsync(() => { if (ReferenceEquals(state, _languageConfiguration)) RetireLanguageConfiguration(hide: true); });
        state.ModelChanged = () => Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(state, _languageConfiguration) && !LanguageCatalogCurrent(state))
                RetireLanguageConfiguration(hide: true);
        });
        settings.Changed += state.SettingsChanged;
        model.Changed += state.ModelChanged;
        capsule.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        Width = capsule.DesiredSize.Width;
        Height = capsule.DesiredSize.Height;
        // Retired source geometry is not passed to Position(), which requires a
        // real source Target. Keep the language anchor at its original pixels.
        double dpi = Math.Max(1, Native.GetDpiForWindow(_hwnd) / 96.0);
        var near = new Native.Rect { Left = (int)anchor.Left, Top = (int)anchor.Top, Right = (int)Math.Ceiling(anchor.Right), Bottom = (int)Math.Ceiling(anchor.Bottom) };
        var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfoW(Native.MonitorFromRect(ref near, 2), ref monitor))
        {
            RetireLanguageConfiguration(hide: true);
            return;
        }
        int width = (int)Math.Ceiling(Width * dpi), height = (int)Math.Ceiling(Height * dpi);
        int x = Math.Clamp((int)Math.Round(anchor.Left - 17 * dpi), monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - width));
        int y = Math.Clamp((int)Math.Round(anchor.Top - 17 * dpi), monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - height));
        Left = x / dpi;
        Top = y / dpi;
        ResetFade();
        SetNoActivate(false);
        Show();
        UpdateLayout();
        if (!Native.SetWindowPos(_hwnd, (nint)(-1), x, y, width, height, 0x0010))
        {
            RetireLanguageConfiguration(hide: true);
            return;
        }
        // One normal activation for this explicit configuration action. Neither
        // a capture-empty repaint nor an async receipt may retry it.
        if (!Activate() || !LanguageConfigurationCurrent(state))
        {
            RetireLanguageConfiguration(hide: true);
            return;
        }
        state.Popup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!state.InitialFocusPending)
                return;
            state.InitialFocusPending = false;
            if (!LanguageConfigurationCurrent(state) || !state.Popup.IsOpen || state.Popup.Retired)
            {
                if (ReferenceEquals(state, _languageConfiguration))
                    RetireLanguageConfiguration(hide: true);
                return;
            }
            state.Search.Focus();
            if (!LanguageConfigurationCurrent(state) || !state.Search.IsKeyboardFocused)
                RetireLanguageConfiguration(hide: true);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private bool LanguageCatalogCurrent(LanguageConfiguration state) => state.Model.Ready == state.ModelReady
        && ReferenceEquals(state.Catalog, state.Model.AvailableDirections);
    private bool LanguageConfigurationCurrent(LanguageConfiguration state) => ReferenceEquals(state, _languageConfiguration)
        && !state.Retired && !_languageWindowClosed && IsVisible && Native.GetForegroundWindow() == _hwnd
        && state.Settings.Revision == state.Revision && ReferenceEquals(state.Expected, state.Settings.Value)
        && LanguageCatalogCurrent(state);
    private bool PreserveLanguageConfiguration()
    {
        if (_languageConfiguration is not { } state || !LanguageConfigurationCurrent(state))
            return false;
        // ClearSessionContent has already revoked all session-facing controls.
        // This restores only the configuration window's normal activation style.
        SetNoActivate(false);
        return true;
    }
    internal void RetireLanguageConfigurationForNewView() => RetireLanguageConfiguration(hide: true);
    private void RetireLanguageConfiguration(bool hide)
    {
        if (_languageConfiguration is not { } state)
            return;
        _languageConfiguration = null;
        state.Retired = true;
        state.Query = new();
        state.Popup.Retire = null;
        state.Popup.IsOpen = false;
        if (state.SettingsChanged is not null)
            state.Settings.Changed -= state.SettingsChanged;
        if (state.ModelChanged is not null)
            state.Model.Changed -= state.ModelChanged;
        Content = _shell;
        if (_languageWindowClosed)
            return;
        SetNoActivate(true);
        if (hide)
            HideImmediately();
    }

    private void BuildLanguageConfiguration(LanguageConfiguration state)
    {
        var body = new StackPanel();
        var header = new Grid { Margin = new(10, 6, 8, 8) };
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        string Name(string code)
        {
            var text = LanguageChoicePresentation.Create(code);
            return UiTheme.English ? text.LabelEn : text.LabelZh;
        }
        var direction = UiTheme.Text(Name(state.Expected.Source) + " → " + Name(state.Expected.Target), 13);
        direction.FontWeight = FontWeights.SemiBold;
        direction.TextWrapping = TextWrapping.NoWrap;
        direction.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(direction);
        state.Swap = IconButton("M4,8 H19 M15,4 L19,8 L15,12 M20,16 H5 M9,12 L5,16 L9,20");
        state.Swap.Width = state.Swap.Height = 28;
        AutomationProperties.SetAutomationId(state.Swap, "ConfigurationLanguageSwap");
        AutomationProperties.SetName(state.Swap, UiTheme.L("交换语言", "Swap languages"));
        state.Swap.ToolTip = UiTheme.L("交换语言", "Swap languages");
        state.Swap.IsEnabled = state.Expected.Source != "auto";
        state.Swap.Click += async (_, _) => await SaveLanguageConfiguration(state, null, state.Query, swap: true);
        Grid.SetColumn(state.Swap, 1);
        header.Children.Add(state.Swap);
        body.Children.Add(header);
        var searchRow = new Grid { Height = 30, Margin = new(2, 2, 2, 6) };
        searchRow.ColumnDefinitions.Add(new()
        {
            Width = new(30)
        });
        searchRow.ColumnDefinitions.Add(new());
        var icon = LanguageGlyph("M17,11 A6,6 0 1 1 5,11 A6,6 0 1 1 17,11 M20,20 L15.5,15.5", "Tertiary", 14);
        searchRow.Children.Add(icon);
        state.Search.Background = Brushes.Transparent;
        state.Search.BorderThickness = new(0);
        state.Search.Padding = new(0, 0, 8, 0);
        state.Search.FontSize = 13;
        state.Search.VerticalContentAlignment = VerticalAlignment.Center;
        state.Search.SetResourceReference(Control.ForegroundProperty, "Text");
        AutomationProperties.SetName(state.Search, UiTheme.L("搜索语言", "Search languages"));
        AutomationProperties.SetAutomationId(state.Search, "ConfigurationLanguageSearch");
        state.Search.ToolTip = UiTheme.L("搜索语言", "Search languages");
        Grid.SetColumn(state.Search, 1);
        searchRow.Children.Add(state.Search);
        var placeholder = UiTheme.Text(UiTheme.L("搜索语言", "Search languages"), 13, true);
        placeholder.IsHitTestVisible = false;
        Grid.SetColumn(placeholder, 1);
        searchRow.Children.Add(placeholder);
        state.Search.TextChanged += (_, _) =>
        {
            if (!ReferenceEquals(state, _languageConfiguration) || state.Saving)
                return;
            placeholder.Visibility = state.Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            state.Query = new();
            state.Highlight = state.Search.Text.Length == 0 ? -1 : 0;
            DrawLanguageChoices(state);
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(state.Search, (_, _) => SetLanguageComposition(state, true));
        // An update is not completion, even when the current composition text is
        // empty. Only this search control's terminal TextInput clears the guard.
        TextCompositionManager.AddPreviewTextInputUpdateHandler(state.Search, (_, _) => SetLanguageComposition(state, true));
        TextCompositionManager.AddPreviewTextInputHandler(state.Search, (_, _) => SetLanguageComposition(state, false));
        state.Search.PreviewKeyDown += async (_, e) =>
        {
            if (!LanguageConfigurationCurrent(state) || !state.Search.IsKeyboardFocused || state.Composing
                || e.Key == Key.ImeProcessed || state.Saving || Keyboard.Modifiers != ModifierKeys.None)
                return;
            var visible = state.Choices.Where(choice => choice.Available && choice.Matches(state.Search.Text.Trim())).ToArray();
            if (e.Key is Key.Up or Key.Down)
            {
                e.Handled = true;
                if (visible.Length == 0)
                    return;
                state.Highlight = state.Highlight < 0 ? e.Key == Key.Down ? 0 : visible.Length - 1
                    : (state.Highlight + (e.Key == Key.Down ? 1 : visible.Length - 1)) % visible.Length;
                DrawLanguageChoices(state, renewQuery: false);
                state.Options.Children.OfType<Button>().Where(button => button.IsEnabled).ElementAt(state.Highlight).BringIntoView();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (visible.Length > 0)
                    await SaveLanguageConfiguration(state, visible[Math.Clamp(state.Highlight, 0, visible.Length - 1)], state.Query, false);
            }
        };
        body.Children.Add(UiTheme.Border(searchRow, 8, "Panel2"));
        body.Children.Add(new ScrollViewer
        {
            Content = state.Options,
            MaxHeight = 232,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        });
        state.Empty.Margin = new(10);
        body.Children.Add(state.Empty);
        var unavailable = new Button
        {
            Content = UiTheme.L("更多语言 · 尚未提供", "More languages · Not available"),
            IsEnabled = false,
            Height = 30,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new(0),
            Padding = new(10, 0, 10, 0)
        };
        body.Children.Add(unavailable);
        state.Feedback.Margin = new(10, 6, 10, 6);
        state.Feedback.Visibility = Visibility.Collapsed;
        body.Children.Add(state.Feedback);
        var menu = UiTheme.Border(body, 12, "Glass", 5);
        menu.Width = 264;
        menu.BorderThickness = new(.5);
        menu.PreviewMouseDown += (_, _) => state.InitialFocusPending = false;
        menu.PreviewKeyDown += (_, _) => state.InitialFocusPending = false;
        state.Anchor.PreviewMouseDown += (_, _) => state.InitialFocusPending = false;
        state.Anchor.PreviewKeyDown += (_, _) => state.InitialFocusPending = false;
        void NewConfigurationFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (state.Popup.IsOpen && !ReferenceEquals(e.NewFocus, state.Search))
                state.InitialFocusPending = false;
        }
        // A newer accessibility focus choice is just as intentional as pointer
        // or keyboard input. The queued initial search focus must not replace it.
        menu.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(NewConfigurationFocus), true);
        state.Anchor.GotKeyboardFocus += NewConfigurationFocus;
        AutomationProperties.SetAutomationId(menu, "ConfigurationLanguageMenu");
        state.Popup.Child = menu;
        state.Popup.AllowsTransparency = true;
        state.Popup.StaysOpen = false;
        state.Popup.PlacementTarget = state.Anchor;
        state.Popup.Placement = PlacementMode.Custom;
        state.Popup.CustomPopupPlacementCallback = (size, target, _) =>
            [new(new(-4, target.Height + 6), PopupPrimaryAxis.Vertical), new(new(-4, -size.Height - 6), PopupPrimaryAxis.Vertical)];
        state.Popup.Retire = () => { if (ReferenceEquals(state, _languageConfiguration)) RetireLanguageConfiguration(hide: true); };
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !state.Composing && LanguageConfigurationCurrent(state))
            {
                e.Handled = true;
                RetireLanguageConfiguration(hide: true);
            }
        };
        DrawLanguageChoices(state);
    }

    private void SetLanguageComposition(LanguageConfiguration state, bool composing)
    {
        if (!ReferenceEquals(state, _languageConfiguration) || state.Retired)
            return;
        state.Composing = composing;
        state.Swap.IsEnabled = !composing && !state.Saving && state.Expected.Source != "auto";
        DrawLanguageChoices(state);
    }

    private void DrawLanguageChoices(LanguageConfiguration state, bool renewQuery = true)
    {
        if (renewQuery)
            state.Query = new();
        object query = state.Query;
        state.Options.Children.Clear();
        var visible = state.Choices.Where(choice => choice.Matches(state.Search.Text.Trim())).ToArray();
        state.Empty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var enabled = visible.Where(choice => choice.Available).ToArray();
        if (state.Highlight >= enabled.Length)
            state.Highlight = enabled.Length - 1;
        foreach (var choice in visible)
        {
            var option = new Button
            {
                Content = choice,
                Style = state.OptionStyle,
                IsEnabled = choice.Available && !state.Saving && !state.Composing
            };
            option.SetValue(Selector.IsSelectedProperty, choice.Code == state.Expected.Target);
            AutomationProperties.SetName(option, choice.Label);
            AutomationProperties.SetAutomationId(option, "ConfigurationLanguageOption_" + choice.Code);
            if (state.Highlight >= 0 && ReferenceEquals(enabled[state.Highlight], choice))
                option.Tag = "highlight";
            option.Click += async (_, _) =>
            {
                if (option.IsVisible && option.IsEnabled && state.Options.Children.Contains(option))
                    await SaveLanguageConfiguration(state, choice, query, false);
            };
            state.Options.Children.Add(option);
        }
    }

    private async Task SaveLanguageConfiguration(LanguageConfiguration state, LanguagePickerChoice? choice, object query, bool swap)
    {
        bool Current() => LanguageConfigurationCurrent(state) && state.Popup.IsOpen && !state.Popup.Retired
            && !state.Composing && ReferenceEquals(state.Query, query)
            && (swap ? state.Expected.Source != "auto" : choice is { Available: true } && state.Choices.Contains(choice)
                && choice.Matches(state.Search.Text.Trim()));
        if (state.Saving || !Current())
            return;
        state.Saving = true;
        state.Search.IsReadOnly = true;
        state.Swap.IsEnabled = false;
        foreach (Button row in state.Options.Children)
            row.IsEnabled = false;
        state.Feedback.Text = UiTheme.L("正在保存…", "Saving…");
        state.Feedback.Visibility = Visibility.Visible;
        try
        {
            string source = swap ? state.Expected.Target : state.Expected.Source;
            string target = swap ? state.Expected.Source : choice!.Code;
            bool saved = await state.Save(source, target, Current);
            if (!ReferenceEquals(state, _languageConfiguration) || state.Retired)
                return;
            if (saved)
                RetireLanguageConfiguration(hide: true);
            else
                state.Feedback.Text = UiTheme.L("尚未保存，请重试。", "Not saved. Try again.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (ReferenceEquals(state, _languageConfiguration) && !state.Retired)
                state.Feedback.Text = UiTheme.L("保存结果尚未确认，请在设置中检查。", "Save was not confirmed. Check Settings.");
        }
        finally
        {
            if (ReferenceEquals(state, _languageConfiguration) && !state.Retired)
            {
                state.Saving = false;
                state.Search.IsReadOnly = false;
                state.Swap.IsEnabled = state.Expected.Source != "auto" && !state.Composing;
                DrawLanguageChoices(state);
            }
        }
    }

    private static Style LanguageOptionStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Setter Property="Height" Value="30"/><Setter Property="Padding" Value="10,0"/>
          <Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="FontFamily" Value="{DynamicResource UiFont}"/>
          <Setter Property="FontSize" Value="13"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
            <Border x:Name="row" Background="Transparent" CornerRadius="7" Padding="{TemplateBinding Padding}">
              <Grid VerticalAlignment="Center">
                <Grid.ColumnDefinitions><ColumnDefinition Width="24"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                <Viewbox x:Name="check" Width="14" Height="14" HorizontalAlignment="Left" Visibility="Hidden">
                  <Canvas Width="24" Height="24"><Path x:Name="tick" Data="M5,12.5 L9.5,17 L19,7.5" Stroke="{DynamicResource AccentInk}"
                    StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/></Canvas>
                </Viewbox>
                <TextBlock Grid.Column="1" Text="{Binding Content.Label, RelativeSource={RelativeSource TemplatedParent}}" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="2" Margin="8,0,0,0" FontSize="12" Opacity=".55" VerticalAlignment="Center"
                  Text="{Binding Content.NativeName, RelativeSource={RelativeSource TemplatedParent}}"
                  FontFamily="{Binding Content.NativeFont, RelativeSource={RelativeSource TemplatedParent}}"/>
              </Grid>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="Selector.IsSelected" Value="True"><Setter TargetName="check" Property="Visibility" Value="Visible"/></Trigger>
              <Trigger Property="Tag" Value="highlight"><Setter TargetName="row" Property="Background" Value="{DynamicResource AccentStrong}"/><Setter Property="Foreground" Value="White"/><Setter TargetName="tick" Property="Stroke" Value="White"/></Trigger>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource AccentStrong}"/><Setter Property="Foreground" Value="White"/><Setter TargetName="tick" Property="Stroke" Value="White"/></Trigger>
              <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource AccentStrong}"/><Setter Property="Foreground" Value="White"/><Setter TargetName="tick" Property="Stroke" Value="White"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter TargetName="row" Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="{DynamicResource Tertiary}"/><Setter TargetName="tick" Property="Stroke" Value="{DynamicResource Tertiary}"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);

    private static FrameworkElement LanguageGlyph(string geometry, string color, double size)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geometry),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, color);
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, HorizontalAlignment = HorizontalAlignment.Center };
    }
}
