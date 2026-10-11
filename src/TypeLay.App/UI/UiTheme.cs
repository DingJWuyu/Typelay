using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
namespace TypeLay.App;

internal static class UiTheme
{
    private const string StyleXml = """
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <Style TargetType="Button"><Setter Property="FontSize" Value="12"/><Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="Background" Value="{DynamicResource Panel}"/><Setter Property="BorderBrush" Value="{DynamicResource Line}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="11,7"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="border" CornerRadius="9" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="border" Property="Opacity" Value="0.82"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.40"/><Setter Property="Cursor" Value="Arrow"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="border" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
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
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Height" Value="36"/>
        <Setter Property="MinHeight" Value="36"/>
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
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="9">
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
              <Border Background="{DynamicResource Panel}" BorderBrush="{DynamicResource Line}" BorderThickness="1" CornerRadius="9" Padding="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="{TemplateBinding MaxDropDownHeight}">
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
    public static void Initialize(string theme)
    {
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(StyleXml));
        Apply(theme);
    }
    public static void Apply(string theme)
    {
        bool dark = theme == "dark";
        if (theme == "system")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        var r = Application.Current.Resources;
        r["Panel"] = Brush(dark ? "#242830" : "#FFFFFF");
        r["Panel2"] = Brush(dark ? "#20242B" : "#F7F8FA");
        r["Line"] = Brush(dark ? "#3B414C" : "#DFE3EB");
        r["Text"] = Brush(dark ? "#E8EBF1" : "#252A31");
        r["Muted"] = Brush(dark ? "#A6ADBB" : "#808794");
        r["Accent"] = Brush("#3267E3");
        r["AccentSoft"] = Brush(dark ? "#293B61" : "#E9F0FF");
        r["Good"] = Brush("#299766");
        r["Warn"] = Brush("#D18A21");
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
            Text = value,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Text");
        return text;
    }
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary)
        {
            button.SetResourceReference(Control.BackgroundProperty, "Accent");
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
    public static Button Toggle(bool on, Action toggle)
    {
        var circle = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new(8),
            Background = Brushes.White,
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        var button = new Button
        {
            Content = circle,
            Width = 38,
            Height = 22,
            Padding = new(3),
            BorderThickness = new(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        button.Background = on ? Brush("#3A6DE2") : Brush("#ADB5C1");
        button.Click += (_, _) => toggle();
        return button;
    }
}
