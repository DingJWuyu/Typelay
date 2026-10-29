using System.Windows;
using System.Windows.Markup;

namespace TypeLay.App;

internal static partial class UiTheme
{
    // Only settings and first-run configuration merge this dictionary. The
    // source editor, overlay and their focus/input templates are not restyled.
    public static ResourceDictionary ConfigurationScrollResources() => (ResourceDictionary)XamlReader.Parse("""
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <ControlTemplate x:Key="ConfigurationScrollPage" TargetType="RepeatButton">
        <Border Background="Transparent"/>
      </ControlTemplate>
      <Style x:Key="ConfigurationScrollThumbStyle" TargetType="Thumb">
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb">
          <Border x:Name="thumb" Margin="1" CornerRadius="3" Background="{DynamicResource ConfigurationScrollThumb}"/>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="thumb" Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style TargetType="ScrollBar">
        <Setter Property="OverridesDefaultStyle" Value="True"/>
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="Width" Value="8"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
          <Border Background="{TemplateBinding Background}">
            <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="True"
              Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" ViewportSize="{TemplateBinding ViewportSize}"
              Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
              <Track.DecreaseRepeatButton><RepeatButton x:Name="before" Command="{x:Static ScrollBar.PageUpCommand}" Template="{StaticResource ConfigurationScrollPage}" Focusable="False" IsTabStop="False"/></Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb Style="{StaticResource ConfigurationScrollThumbStyle}"/></Track.Thumb>
              <Track.IncreaseRepeatButton><RepeatButton x:Name="after" Command="{x:Static ScrollBar.PageDownCommand}" Template="{StaticResource ConfigurationScrollPage}" Focusable="False" IsTabStop="False"/></Track.IncreaseRepeatButton>
            </Track>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
              <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
              <Setter TargetName="before" Property="Command" Value="{x:Static ScrollBar.PageLeftCommand}"/>
              <Setter TargetName="after" Property="Command" Value="{x:Static ScrollBar.PageRightCommand}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
        <Style.Triggers>
          <Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="Height" Value="8"/></Trigger>
        </Style.Triggers>
      </Style>
    </ResourceDictionary>
    """);
}
