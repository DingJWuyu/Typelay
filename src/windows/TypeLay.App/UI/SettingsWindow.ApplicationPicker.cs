using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    // Only the app chooser uses these native-control templates. Its existing
    // modal, settings revision, and choice-generation checks remain the actions.
    private static ResourceDictionary ApplicationPickerResources() => (ResourceDictionary)XamlReader.Parse("""
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <Style x:Key="ApplicationPickerSearch" TargetType="TextBox">
        <Setter Property="Background" Value="{DynamicResource SettingsLanguageFill}"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="CaretBrush" Value="{DynamicResource Text}"/>
        <Setter Property="SelectionBrush" Value="{DynamicResource AccentSoft}"/><Setter Property="FontFamily" Value="{DynamicResource UiFont}"/>
        <Setter Property="FontSize" Value="13"/><Setter Property="FontWeight" Value="Normal"/>
        <Setter Property="Height" Value="30"/><Setter Property="Padding" Value="31,0,9,0"/>
        <Setter Property="BorderThickness" Value="0"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
          <Border CornerRadius="8" Background="{TemplateBinding Background}">
            <ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="Center" Focusable="False"
              HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
          </Border>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="ApplicationPickerOption" TargetType="Button">
        <Setter Property="Height" Value="30"/><Setter Property="MinHeight" Value="30"/><Setter Property="Padding" Value="8,0"/>
        <Setter Property="FontSize" Value="13"/><Setter Property="FontWeight" Value="Normal"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderThickness" Value="0"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="HorizontalContentAlignment" Value="Left"/><Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
          <Border x:Name="surface" CornerRadius="7" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource AccentStrong}"/><Setter Property="Foreground" Value="White"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter Property="Background" Value="{DynamicResource AccentStrong}"/><Setter Property="Foreground" Value="White"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
    </ResourceDictionary>
    """);

    private static FrameworkElement ApplicationPickerSearch(TextBox input, string placeholder)
    {
        var host = new Grid { Height = 30, Margin = new(0, 14, 0, 6) };
        input.SetResourceReference(StyleProperty, "ApplicationPickerSearch");
        host.Children.Add(input);
        var icon = PrototypeGlyph("M17,11 A6,6 0 1 1 5,11 A6,6 0 1 1 17,11 M20,20 L15.5,15.5", "Tertiary", stroke: 1.6);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new(9, 0, 0, 0);
        icon.IsHitTestVisible = false;
        host.Children.Add(icon);
        var label = UiTheme.Text(placeholder, 13);
        label.Margin = new(31, 0, 9, 0);
        label.SetResourceReference(TextBlock.ForegroundProperty, "Tertiary");
        label.IsHitTestVisible = false;
        AutomationProperties.SetAutomationId(label, "ApplicationPickerPlaceholder");
        host.Children.Add(label);
        void Update() => label.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => Update();
        Update();
        return host;
    }
}
