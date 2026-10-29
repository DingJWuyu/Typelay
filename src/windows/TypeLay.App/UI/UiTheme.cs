using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using TypeLay.Runtime;
using System.Globalization;
using System.Windows.Shapes;
namespace TypeLay.App;

internal static partial class UiTheme
{
    private static UserSettings _preferences = new();
    public static bool English => _preferences.UiLanguage == "en" || _preferences.UiLanguage == "system" && !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public static bool ReduceMotion => _preferences.ShouldReduceMotion(!SystemParameters.ClientAreaAnimation);
    // WPF requires an absolute base URI plus a relative family name. A single
    // string starting with pack: silently falls back instead of loading a font.
    public static FontFamily BundledFont(string family, string? fallback = null) =>
        new(new Uri("pack://application:,,,/TypeLay.App;component/"), "./UI/Fonts/#" + family + (string.IsNullOrWhiteSpace(fallback) ? "" : ", " + fallback));
    private static string InterfaceLanguage => English ? "en" : "zh-CN";
    public static FontFamily UiFont => RoleFont(serif: false, InterfaceLanguage);
    public static FontFamily UiFontForLanguage(string? language) => RoleFont(serif: false, language);
    public static FontFamily DisplayFont => ContentFontForLanguage(InterfaceLanguage);
    public static FontFamily ContentFont => ContentFontForLanguage(_preferences.Target);
    public static FontFamily ContentFontForLanguage(string? language) => RoleFont(_preferences.Font == "claude", language);

    private static FontFamily RoleFont(bool serif, string? language)
    {
        string region = language?.StartsWith("ja", StringComparison.OrdinalIgnoreCase) == true ? "JP"
            : language?.StartsWith("ko", StringComparison.OrdinalIgnoreCase) == true ? "KR" : "SC";
        if (_preferences.Font == "system")
            return new("Segoe UI, " + (region == "JP" ? "Yu Gothic UI" : region == "KR" ? "Malgun Gothic" : "Microsoft YaHei UI"));
        string cjk = serif ? "Noto Serif " : "Noto Sans ";
        // Every bundled fallback needs its own resource locator. A bare Noto
        // family name would query installed fonts and silently lose offline CJK.
        string[] regions = [region, .. new[] { "SC", "JP", "KR" }.Where(value => value != region)];
        string fallback = string.Join(", ", regions.Select(value => "./UI/Fonts/#" + cjk + value));
        return BundledFont(serif ? "Source Serif 4" : "Inter", fallback);
    }
    public static double ContentSize => _preferences.TextSize;
    public static string L(string chinese, string english) => English ? english : chinese;
    private const string StyleXml = """
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <Style TargetType="Expander"><Setter Property="Foreground" Value="{DynamicResource Text}"/></Style>
      <Style TargetType="Button"><Setter Property="FontSize" Value="13"/><Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="Background" Value="{DynamicResource Panel}"/><Setter Property="BorderBrush" Value="{DynamicResource Line}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="11,7"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="border" CornerRadius="8" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="border" Property="Opacity" Value="0.82"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.40"/><Setter Property="Cursor" Value="Arrow"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="border" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
      <Style x:Key="SettingToggle" TargetType="CheckBox">
        <Setter Property="Width" Value="48"/>
        <Setter Property="Height" Value="40"/>
        <Setter Property="VerticalAlignment" Value="Center"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
          <Border x:Name="focus" Background="Transparent" BorderBrush="Transparent" BorderThickness="2" CornerRadius="8">
            <Border x:Name="track" Width="38" Height="22" CornerRadius="11" Background="{DynamicResource SwitchOff}" Padding="3">
              <Border x:Name="thumb" Width="16" Height="16" CornerRadius="8" Background="White" HorizontalAlignment="Left"/>
            </Border>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="track" Property="Background" Value="{DynamicResource Accent}"/>
              <Setter TargetName="thumb" Property="HorizontalAlignment" Value="Right"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="focus" Property="BorderBrush" Value="{DynamicResource Text}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/><Setter Property="Cursor" Value="Arrow"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style TargetType="ComboBoxItem">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Padding" Value="10,8"/>
        <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="item" Background="{TemplateBinding Background}" CornerRadius="6" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsSelected" Value="True"><Setter TargetName="item" Property="Background" Value="{DynamicResource AccentSoft}"/></Trigger>
            <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="item" Property="Background" Value="{DynamicResource AccentSoft}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style TargetType="ComboBox">
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Height" Value="30"/>
        <Setter Property="MinHeight" Value="30"/>
        <Setter Property="VerticalAlignment" Value="Center"/>
        <Setter Property="Padding" Value="11,0,32,0"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="Background" Value="{DynamicResource Panel}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Line}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
        <Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
          <Grid SnapsToDevicePixels="True">
            <ToggleButton x:Name="toggle" Focusable="False" ClickMode="Press" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
              <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="8">
                  <Path Data="M 0 0 L 4 4 L 8 0" Stroke="{DynamicResource Muted}" StrokeThickness="1.3" StrokeStartLineCap="Round" StrokeEndLineCap="Round" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,0"/>
                </Border>
              </ControlTemplate></ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter x:Name="selection" IsHitTestVisible="False" Margin="{TemplateBinding Padding}" VerticalAlignment="Center" HorizontalAlignment="Left" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}" ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}"/>
            <TextBox x:Name="PART_EditableTextBox" Style="{x:Null}" Visibility="Collapsed" Margin="11,1,32,1" Padding="0" Background="Transparent" Foreground="{TemplateBinding Foreground}" CaretBrush="{TemplateBinding Foreground}" FontSize="{TemplateBinding FontSize}" FontFamily="{TemplateBinding FontFamily}" VerticalContentAlignment="Center" IsReadOnly="{TemplateBinding IsReadOnly}" Focusable="True">
              <TextBox.Template><ControlTemplate TargetType="TextBox">
                <ScrollViewer x:Name="PART_ContentHost" Focusable="False" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
              </ControlTemplate></TextBox.Template>
            </TextBox>
            <Popup x:Name="PART_Popup" Placement="Bottom" VerticalOffset="4" AllowsTransparency="True" Focusable="False" IsOpen="{TemplateBinding IsDropDownOpen}">
              <Border Background="{DynamicResource Panel}" BorderBrush="{DynamicResource Line}" BorderThickness="1" CornerRadius="8" Padding="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="{TemplateBinding MaxDropDownHeight}">
                <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
                  <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEditable" Value="True">
              <Setter Property="IsTabStop" Value="False"/>
              <Setter TargetName="selection" Property="Visibility" Value="Collapsed"/>
              <Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/>
            </Trigger>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="toggle" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="toggle" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="IsDropDownOpen" Value="True"><Setter TargetName="toggle" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style TargetType="TextBox"><Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="Background" Value="{DynamicResource Panel}"/><Setter Property="BorderBrush" Value="{DynamicResource Line}"/><Setter Property="FontSize" Value="13"/></Style>
    </ResourceDictionary>
    """;
    public static ResourceDictionary QuickPanelResources() => (ResourceDictionary)XamlReader.Parse("""
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <Style x:Key="QuickPanelModeChoice" TargetType="RadioButton">
        <Setter Property="Background" Value="{DynamicResource Panel2}"/>
        <Setter Property="Foreground" Value="{DynamicResource Muted}"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="MinHeight" Value="34"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
          <Border x:Name="segment" Background="{TemplateBinding Background}" BorderBrush="Transparent" BorderThickness="2" CornerRadius="6" Padding="8,5">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="segment" Property="Background" Value="{DynamicResource Panel}"/>
              <Setter Property="Foreground" Value="{DynamicResource Text}"/>
              <Setter Property="FontWeight" Value="SemiBold"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="segment" Property="BorderBrush" Value="{DynamicResource Text}"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
    </ResourceDictionary>
    """);
    // Local aliases keep the settings surface neutral without recoloring the
    // overlay or quick panel. Dynamic colors still follow system theme changes.
    public static ResourceDictionary SettingsResources() => (ResourceDictionary)XamlReader.Parse("""
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <SolidColorBrush x:Key="Panel" Color="{DynamicResource SettingsPanelColor}"/>
      <SolidColorBrush x:Key="Panel2" Color="{DynamicResource SettingsSidebarColor}"/>
      <SolidColorBrush x:Key="Line" Color="{DynamicResource SettingsLineColor}"/>
      <SolidColorBrush x:Key="Text" Color="{DynamicResource SettingsTextColor}"/>
      <SolidColorBrush x:Key="Muted" Color="{DynamicResource SettingsMutedColor}"/>

      <SolidColorBrush x:Key="SettingsLink" Color="{DynamicResource SettingsLinkColor}"/>
      <SolidColorBrush x:Key="SettingsSelection" Color="{DynamicResource SettingsSelectionColor}"/>
      <SolidColorBrush x:Key="SettingsControlFill" Color="{DynamicResource SettingsControlFillColor}"/>
      <SolidColorBrush x:Key="SettingsControlHover" Color="{DynamicResource SettingsControlHoverColor}"/>
      <SolidColorBrush x:Key="SettingsControlLine" Color="{DynamicResource SettingsControlLineColor}"/>
      <!-- Explicit settings-only keys: never restyle a source/overlay editor. -->
      <Style x:Key="SettingsSearchField" TargetType="TextBox">
        <Setter Property="Background" Value="{DynamicResource SettingsControlFill}"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="CaretBrush" Value="{DynamicResource Text}"/>
        <Setter Property="SelectionBrush" Value="{DynamicResource AccentSoft}"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="BorderBrush" Value="{DynamicResource SettingsControlLine}"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
          <Grid>
            <Border x:Name="surface" CornerRadius="8" Background="{TemplateBinding Background}"/>
            <ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="Center" Focusable="False"
              HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
            <Border x:Name="outline" CornerRadius="8" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" IsHitTestVisible="False"/>
            <Border x:Name="focus" CornerRadius="11" BorderBrush="{DynamicResource AccentSoft}" BorderThickness="3" Margin="-3" Visibility="Collapsed" IsHitTestVisible="False"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocusWithin" Value="True">
              <Setter TargetName="outline" Property="BorderBrush" Value="{DynamicResource Accent}"/>
              <Setter TargetName="outline" Property="BorderThickness" Value="1"/>
              <Setter TargetName="focus" Property="Visibility" Value="Visible"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="SettingsListSearchField" TargetType="TextBox" BasedOn="{StaticResource SettingsSearchField}">
        <Setter Property="Background" Value="{DynamicResource Panel}"/>
        <Setter Property="BorderThickness" Value="0.5"/>
      </Style>
      <Style x:Key="SettingsThemeChoice" TargetType="RadioButton">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
          <Grid>
            <Grid.RowDefinitions><RowDefinition Height="50"/><RowDefinition Height="24"/></Grid.RowDefinitions>
            <Border x:Name="preview" BorderBrush="{DynamicResource Line}" BorderThickness="1" CornerRadius="8" Padding="3">
              <ContentPresenter/>
            </Border>
            <DockPanel Grid.Row="1" Margin="0,5,0,0">
              <TextBlock x:Name="selected" DockPanel.Dock="Right" Text="✓" Foreground="{DynamicResource SettingsLink}" Visibility="Hidden" Margin="8,0,0,0"/>
              <TextBlock Text="{TemplateBinding Tag}" Foreground="{TemplateBinding Foreground}"/>
            </DockPanel>
            <Border x:Name="focus" Grid.RowSpan="2" BorderBrush="Transparent" BorderThickness="2" CornerRadius="10" Margin="-3" IsHitTestVisible="False"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="preview" Property="BorderBrush" Value="{DynamicResource SettingsLink}"/>
              <Setter TargetName="preview" Property="BorderThickness" Value="2"/>
              <Setter TargetName="preview" Property="Padding" Value="2"/>
              <Setter TargetName="selected" Property="Visibility" Value="Visible"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="focus" Property="BorderBrush" Value="{DynamicResource Text}"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
    </ResourceDictionary>
    """);

    public static void Initialize(string theme)
    {
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(StyleXml));
        Apply(new UserSettings(Theme: theme));
    }
    public static void Initialize(UserSettings preferences)
    {
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(StyleXml));
        Apply(preferences);
    }
    public static void Apply(string theme) => Apply(_preferences with { Theme = theme });
    public static void Apply(UserSettings preferences)
    {
        _preferences = preferences;
        bool dark = preferences.Theme == "dark";
        if (preferences.Theme == "system")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        var r = Application.Current.Resources;
        string card = dark ? "#2F2E2B" : "#FFFFFF", window = dark ? "#262624" : "#F7F6F2";
        string line = dark ? "#42413E" : "#E8E7E2", ink = dark ? "#F4F3EE" : "#141413";
        string muted = dark ? "#B5B3AB" : "#5C5B56";
        string sidebar = dark ? "#2C2B28" : "#ECE9E0";
        var accent = preferences.Accent switch
        {
            "ocean" => ("#2F7FE0", "#1F63BB", "#7AB4FF"),
            "sage" => ("#5D9A6E", "#3C7650", "#8FCC9F"),
            "iris" => ("#8A6CD8", "#6A4CBC", "#B9A4F5"),
            "graphite" => ("#77756F", "#55534E", "#C4C2BB"),
            _ => ("#D97757", "#B5552F", "#EC9A7C")
        };
        foreach (var (key, color) in new[] { ("SettingsPanelColor", card), ("SettingsSidebarColor", sidebar),
            ("SettingsLineColor", line), ("SettingsTextColor", ink), ("SettingsMutedColor", muted),
            ("SettingsLinkColor", dark ? accent.Item3 : accent.Item2), ("SettingsSelectionColor", dark ? "#41403C" : "#DEDCD4"),
            ("SettingsControlFillColor", dark ? "#17FFFFFF" : "#14141413"),
            ("SettingsControlHoverColor", dark ? "#26FFFFFF" : "#24141413"),
            ("SettingsControlLineColor", dark ? "#26FFFFFF" : "#29141413") })
            r[key] = (Color)ColorConverter.ConvertFromString(color);
        r["Panel"] = Brush(card);
        r["Panel2"] = Brush(dark ? "#383733" : "#F1F0EB");
        r["Window"] = Brush(window);
        r["Sidebar"] = Brush(sidebar);
        r["Line"] = Brush(line);
        r["Text"] = Brush(ink);
        r["Muted"] = Brush(muted);
        r["Tertiary"] = Brush(dark ? "#9A988F" : "#74726C");
        r["Accent"] = Brush(accent.Item1);
        r["AccentInk"] = Brush(dark ? accent.Item3 : preferences.Accent == "clay" ? "#AD4F2D" : accent.Item2);
        r["AccentStrong"] = Brush(accent.Item2);
        r["SettingsLanguageFill"] = Brush(dark ? "#0DFFFFFF" : "#0B141413");
        r["SettingsLanguageThumb"] = Brush(dark ? "#3A3936" : "#FFFFFF");
        r["AccentSoft"] = Brush((dark ? "#30" : "#24") + accent.Item1[1..]);
        r["SwitchOff"] = Brush(dark ? "#5C5B57" : "#D2D1CC");
        r["Glass"] = Brush(preferences.ReduceTransparency ? dark ? "#2C2B28" : "#FBFAF7" : dark ? "#EB282724" : "#E6FBFAF7");
        r["Good"] = Brush(dark ? "#63C48C" : "#2F8A57");
        r["Warn"] = Brush(dark ? "#E5AD55" : "#A5670E");
        r["Danger"] = Brush(dark ? "#F0806C" : "#C2412D");
        r["UiFont"] = UiFont;
        r["ContentFont"] = ContentFont;
        r["DisplayFont"] = DisplayFont;
        r["ContentSize"] = ContentSize;
    }
    public static FrameworkElement Mark(double size = 26, bool appIcon = false)
    {
        var drawing = new Grid { Width = 24, Height = 24 };
        var back = new Rectangle
        {
            Width = 14,
            Height = 10,
            RadiusX = 3,
            RadiusY = 3,
            StrokeThickness = 1.6,
            Stroke = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new(3, 4, 0, 0)
        };
        var front = new Rectangle
        {
            Width = 14,
            Height = 10,
            RadiusX = 3,
            RadiusY = 3,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new(7, 10, 0, 0)
        };
        drawing.Children.Add(back);
        drawing.Children.Add(front);
        var mark = new Viewbox { Width = appIcon ? size * .65 : size, Height = appIcon ? size * .65 : size, Child = drawing };
        if (!appIcon)
        {
            back.SetResourceReference(Shape.StrokeProperty, "AccentInk");
            front.SetResourceReference(Shape.FillProperty, "AccentInk");
            return mark;
        }
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new(size / 4),
            Child = mark,
            Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#E48A69"), (Color)ColorConverter.ConvertFromString("#C4573A"), 90)
        };
    }
    public static SolidColorBrush Brush(string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        return b;
    }
    public static TextBlock Text(string value, double size = 12, bool muted = false, bool bold = false)
    {
        var text = new TextBlock
        {
            Text = UiStrings.Translate(value),
            FontFamily = UiFont,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Text");
        text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        return text;
    }
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = UiStrings.Translate(text), FontFamily = UiFont, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary)
        {
            button.SetResourceReference(Control.BackgroundProperty, "AccentStrong");
            button.Foreground = Brushes.White;
            button.BorderThickness = new(0);
        }
        button.Click += (_, _) => action();
        return button;
    }
    public static Border Border(UIElement child, double radius = 12, string color = "Panel", double padding = 0)
    {
        var border = new Border { CornerRadius = new(radius), BorderThickness = new(1), Child = child, Padding = new(padding) };
        border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, color);
        border.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Line");
        return border;
    }
    public static CheckBox Toggle(string name, Func<bool> read, Action<bool> change)
    {
        var toggle = new CheckBox { IsChecked = read() };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "SettingToggle");
        AutomationProperties.SetName(toggle, name);
        bool changing = false;
        void Changed(object sender, RoutedEventArgs args)
        {
            if (changing)
                return;
            changing = true;
            try
            {
                change(toggle.IsChecked == true);
            }
            finally
            {
                // Confirmation may be declined or persistence may fail. Expose
                // the saved state without treating rollback as another action.
                toggle.IsChecked = read();
                changing = false;
            }
        }
        // Native pointer, Space and UI Automation all change IsChecked. Listening
        // here also supports TogglePattern, which does not raise Button.Click.
        toggle.Checked += Changed;
        toggle.Unchecked += Changed;
        return toggle;
    }
}
