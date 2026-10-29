using System.Windows;
using System.Windows.Markup;

namespace TypeLay.App;

internal static partial class UiTheme
{
    // These templates belong only to the settings language page. In particular,
    // they do not replace the source/overlay editor or its focus contract.
    public static ResourceDictionary LanguageResources() => (ResourceDictionary)XamlReader.Parse("""
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <Style x:Key="SettingsLanguageSearch" TargetType="TextBox">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="CaretBrush" Value="{DynamicResource Text}"/>
        <Setter Property="SelectionBrush" Value="{DynamicResource AccentSoft}"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
          <Border CornerRadius="7" Background="{TemplateBinding Background}">
            <ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="Center" Focusable="False"
              HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
          </Border>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="SettingsLanguageOption" TargetType="ComboBoxItem">
        <Setter Property="Focusable" Value="False"/><Setter Property="Height" Value="30"/><Setter Property="Padding" Value="8,0"/>
        <Setter Property="FontSize" Value="13"/><Setter Property="FontWeight" Value="Normal"/>
        <Setter Property="FontFamily" Value="{DynamicResource UiFont}"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="surface" CornerRadius="7" Background="Transparent" Padding="{TemplateBinding Padding}">
            <Grid VerticalAlignment="Center">
              <Grid.ColumnDefinitions><ColumnDefinition Width="22"/><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
              <Viewbox x:Name="tick" Width="14" Height="14" Visibility="Hidden" HorizontalAlignment="Left"><Canvas Width="24" Height="24"><Path x:Name="tickStroke" Data="M4,12 L9,17 L20,6" Stroke="{DynamicResource SettingsLink}" StrokeThickness="1.7" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/></Canvas></Viewbox>
              <TextBlock Grid.Column="1" Text="{Binding Label}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
              <TextBlock Grid.Column="2" Text="{Binding NativeName}" FontSize="12" Opacity="0.55" Margin="8,0,0,0" VerticalAlignment="Center"/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsSelected" Value="True"><Setter TargetName="tick" Property="Visibility" Value="Visible"/></Trigger>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="surface" Property="Background" Value="{DynamicResource AccentStrong}"/><Setter Property="Foreground" Value="White"/><Setter TargetName="tickStroke" Property="Stroke" Value="White"/></Trigger>
            <Trigger Property="Tag" Value="selected"><Setter TargetName="surface" Property="Background" Value="{DynamicResource AccentStrong}"/><Setter Property="Foreground" Value="White"/><Setter TargetName="tickStroke" Property="Stroke" Value="White"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="SettingsLanguagePicker" TargetType="ComboBox">
        <Setter Property="Height" Value="44"/><Setter Property="MinHeight" Value="44"/>
        <Setter Property="Padding" Value="14,0,34,0"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="Background" Value="{DynamicResource SettingsLanguageFill}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource SettingsControlLine}"/>
        <Setter Property="BorderThickness" Value="0.5"/>
        <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
        <Setter Property="VirtualizingPanel.IsVirtualizing" Value="False"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
          <Grid>
            <ToggleButton x:Name="anchor" Tag="{TemplateBinding Tag}" Focusable="False" ClickMode="Press" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
              <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="10">
                  <Viewbox x:Name="chevron" Width="14" Height="14" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,0"><Canvas Width="24" Height="24"><Path Data="M6,9 L12,15 L18,9" Stroke="{DynamicResource Tertiary}" StrokeThickness="1.7"/></Canvas></Viewbox>
                </Border>
                <ControlTemplate.Triggers><Trigger Property="Tag" Value="tray"><Setter TargetName="chevron" Property="Visibility" Value="Collapsed"/></Trigger></ControlTemplate.Triggers>
              </ControlTemplate></ToggleButton.Template>
            </ToggleButton>
            <TextBlock Text="{Binding SelectedItem.Label, RelativeSource={RelativeSource TemplatedParent}}" Margin="{TemplateBinding Padding}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" IsHitTestVisible="False"/>
            <Popup x:Name="PART_Popup" AllowsTransparency="True" Focusable="False" IsOpen="{TemplateBinding IsDropDownOpen}">
              <Border Width="280" CornerRadius="12" Padding="6" Background="{DynamicResource Panel}" BorderBrush="{DynamicResource SettingsControlLine}" BorderThickness="0.5">
                <StackPanel>
                  <Grid Margin="0,0,0,6" Height="30">
                    <TextBox x:Name="PART_LanguageSearch" Style="{DynamicResource SettingsLanguageSearch}" Background="{DynamicResource SettingsLanguageFill}" FontFamily="{DynamicResource UiFont}" FontWeight="Normal" FontSize="13" Height="30" Padding="31,0,9,0"/>
                    <Viewbox Width="14" Height="14" HorizontalAlignment="Left" VerticalAlignment="Center" Margin="9,0,0,0" IsHitTestVisible="False"><Canvas Width="24" Height="24"><Path Data="M17,11 A6,6 0 1 1 5,11 A6,6 0 1 1 17,11 M20,20 L15.5,15.5" Stroke="{DynamicResource Tertiary}" StrokeThickness="1.6"/></Canvas></Viewbox>
                    <TextBlock x:Name="PART_LanguagePlaceholder" Margin="31,0,9,0" FontFamily="{DynamicResource UiFont}" FontWeight="Normal" FontSize="13" Foreground="{DynamicResource Tertiary}" VerticalAlignment="Center" IsHitTestVisible="False"/>
                  </Grid>
                  <ScrollViewer MaxHeight="260" CanContentScroll="False" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
                    <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                  </ScrollViewer>
                  <TextBlock x:Name="PART_LanguageEmpty" Visibility="Collapsed" FontFamily="{DynamicResource UiFont}" FontWeight="Normal" FontSize="13" Foreground="{DynamicResource Tertiary}" Padding="10" LineHeight="18" LineStackingStrategy="BlockLineHeight" TextAlignment="Left"/>
                </StackPanel>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="anchor" Property="Background" Value="{DynamicResource SettingsControlFill}"/></Trigger>
            <Trigger Property="IsDropDownOpen" Value="True"><Setter TargetName="anchor" Property="Background" Value="{DynamicResource SettingsControlFill}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="anchor" Property="BorderBrush" Value="{DynamicResource SettingsLink}"/><Setter TargetName="anchor" Property="BorderThickness" Value="1.5"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="TrayLanguagePicker" TargetType="ComboBox" BasedOn="{StaticResource SettingsLanguagePicker}">
        <Setter Property="Tag" Value="tray"/><Setter Property="Height" Value="34"/><Setter Property="MinHeight" Value="34"/>
        <Setter Property="Padding" Value="10,0"/><Setter Property="Background" Value="{DynamicResource Panel}"/>
        <Setter Property="FontSize" Value="13.5"/><Setter Property="FontWeight" Value="Medium"/><Setter Property="FontFamily" Value="{DynamicResource DisplayFont13.5}"/>
      </Style>
      <Style x:Key="SettingsLanguageSwap" TargetType="Button">
        <Setter Property="Width" Value="36"/><Setter Property="Height" Value="36"/><Setter Property="Padding" Value="0"/>
        <Setter Property="Foreground" Value="{DynamicResource Muted}"/><Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
          <Border x:Name="surface" CornerRadius="18" Background="Transparent" BorderBrush="Transparent" BorderThickness="1">
            <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="surface" Property="Background" Value="{DynamicResource SettingsControlFill}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="surface" Property="BorderBrush" Value="{DynamicResource SettingsLink}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.35"/><Setter Property="Cursor" Value="Arrow"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="SettingsToneChoice" TargetType="Button">
        <Setter Property="MinWidth" Value="56"/><Setter Property="Height" Value="24"/>
        <Setter Property="Padding" Value="12,0"/><Setter Property="FontSize" Value="12"/><Setter Property="FontWeight" Value="Medium"/>
        <Setter Property="Foreground" Value="{DynamicResource Muted}"/><Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
          <Border x:Name="segment" Background="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}" BorderBrush="Transparent" BorderThickness="0.5">
            <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="Tag" Value="selected"><Setter TargetName="segment" Property="Background" Value="{DynamicResource SettingsLanguageThumb}"/><Setter Property="Foreground" Value="{DynamicResource Text}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="segment" Property="BorderBrush" Value="{DynamicResource SettingsLink}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/><Setter Property="Cursor" Value="Arrow"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
    </ResourceDictionary>
    """);
}
