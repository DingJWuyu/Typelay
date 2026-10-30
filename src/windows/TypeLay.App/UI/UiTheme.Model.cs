using System.Globalization;
using System.Windows.Controls;
using System.Windows.Markup;

namespace TypeLay.App;

internal static partial class UiTheme
{
    internal static ControlTemplate ModelProgressTemplate(double radius = 2) => (ControlTemplate)XamlReader.Parse(
"""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ProgressBar">
                  <Grid><Border x:Name="PART_Track" CornerRadius="2" Background="{DynamicResource Panel2}"/>
                    <Border x:Name="PART_Indicator" CornerRadius="2" HorizontalAlignment="Left" Background="{TemplateBinding Foreground}"/>
                    <Border x:Name="pending" CornerRadius="2" Width="64" HorizontalAlignment="Left" Opacity="0.5" Background="{TemplateBinding Foreground}" Visibility="Collapsed"/>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsIndeterminate" Value="True">
                      <Setter TargetName="PART_Indicator" Property="Visibility" Value="Collapsed"/>
                      <Setter TargetName="pending" Property="Visibility" Value="Visible"/>
                    </Trigger>
                    <MultiTrigger><MultiTrigger.Conditions><Condition Property="IsIndeterminate" Value="True"/><Condition Property="Tag" Value="animate"/></MultiTrigger.Conditions>
                      <MultiTrigger.EnterActions><BeginStoryboard x:Name="pulse"><Storyboard>
                        <DoubleAnimation Storyboard.TargetName="pending" Storyboard.TargetProperty="Opacity" From="0.35" To="0.8" Duration="0:0:0.8" AutoReverse="True" RepeatBehavior="Forever"/>
                      </Storyboard></BeginStoryboard></MultiTrigger.EnterActions>
                      <MultiTrigger.ExitActions><RemoveStoryboard BeginStoryboardName="pulse"/></MultiTrigger.ExitActions>
                    </MultiTrigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
                """.Replace("CornerRadius=\"2\"", "CornerRadius=\"" + radius.ToString(CultureInfo.InvariantCulture) + "\"", StringComparison.Ordinal));
}
