using System.Windows;
using System.Windows.Markup;

namespace TypeLay.App;

internal static partial class UiTheme
{
    public static ResourceDictionary ShortcutResources() => (ResourceDictionary)XamlReader.Parse("""
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <Style x:Key="SettingsShortcutRecorder" TargetType="TextBox">
        <Setter Property="Background" Value="{DynamicResource SettingsControlFill}"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="CaretBrush" Value="Transparent"/>
        <Setter Property="SelectionBrush" Value="{DynamicResource AccentSoft}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource SettingsControlLine}"/>
        <Setter Property="BorderThickness" Value="0,0,0,1"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
          <Grid>
            <Border x:Name="pulse" CornerRadius="11" Margin="-4" BorderThickness="4" BorderBrush="{DynamicResource AccentSoft}" Opacity="0" IsHitTestVisible="False"/>
            <Border x:Name="surface" CornerRadius="7" Background="{TemplateBinding Background}"/>
            <ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="Center" Focusable="False" HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
            <TextBlock x:Name="empty" Text="{Binding EmptyPrompt, RelativeSource={RelativeSource TemplatedParent}}" Foreground="{DynamicResource Tertiary}" HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed" IsHitTestVisible="False"/>
            <TextBlock x:Name="prompt" Text="{Binding RecordingPrompt, RelativeSource={RelativeSource TemplatedParent}}" HorizontalAlignment="Center" VerticalAlignment="Center" Margin="10,0" Visibility="Collapsed" IsHitTestVisible="False"/>
            <Border x:Name="outline" CornerRadius="7" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" IsHitTestVisible="False"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="surface" Property="Background" Value="{DynamicResource SettingsControlHover}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="outline" Property="BorderThickness" Value="1"/><Setter TargetName="outline" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="Text" Value=""><Setter TargetName="empty" Property="Visibility" Value="Visible"/></Trigger>
            <DataTrigger Binding="{Binding Recording, RelativeSource={RelativeSource Self}}" Value="True">
              <Setter TargetName="surface" Property="Background" Value="{DynamicResource AccentSoft}"/>
              <Setter TargetName="outline" Property="BorderBrush" Value="{DynamicResource Accent}"/>
              <Setter TargetName="outline" Property="BorderThickness" Value="1.5"/>
              <Setter Property="Foreground" Value="{DynamicResource AccentInk}"/>
              <Setter TargetName="PART_ContentHost" Property="Visibility" Value="Hidden"/>
              <Setter TargetName="empty" Property="Visibility" Value="Collapsed"/>
              <Setter TargetName="prompt" Property="Visibility" Value="Visible"/>
            </DataTrigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
    </ResourceDictionary>
    """);
}
