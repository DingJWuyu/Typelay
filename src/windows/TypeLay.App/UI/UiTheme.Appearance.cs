using System.Windows;
using System.Windows.Markup;

namespace TypeLay.App;

internal static partial class UiTheme
{
    public static ResourceDictionary AppearanceResources() => (ResourceDictionary)XamlReader.Parse("""
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <RadialGradientBrush x:Key="AppearanceSceneryWarm" MappingMode="Absolute" RadiusX="1200" RadiusY="700">
        <GradientStop Color="{DynamicResource AppearanceWarmColor}" Offset="0"/><GradientStop Color="Transparent" Offset="0.6"/>
      </RadialGradientBrush>
      <RadialGradientBrush x:Key="AppearanceSceneryGreen" MappingMode="Absolute" RadiusX="900" RadiusY="700">
        <GradientStop Color="{DynamicResource AppearanceGreenColor}" Offset="0"/><GradientStop Color="Transparent" Offset="0.6"/>
      </RadialGradientBrush>
      <RadialGradientBrush x:Key="AppearanceSceneryGold" MappingMode="Absolute" RadiusX="700" RadiusY="500">
        <GradientStop Color="{DynamicResource AppearanceGoldColor}" Offset="0"/><GradientStop Color="Transparent" Offset="0.65"/>
      </RadialGradientBrush>
      <Style x:Key="SettingsThemeChoice" TargetType="RadioButton">
        <Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="{DynamicResource Muted}"/>
        <Setter Property="FontSize" Value="12"/><Setter Property="FontWeight" Value="Medium"/>
        <Setter Property="Cursor" Value="Hand"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
          <Grid Width="76">
            <Grid.RowDefinitions><RowDefinition Height="50"/><RowDefinition Height="7"/><RowDefinition Height="12"/></Grid.RowDefinitions>
            <Border x:Name="selectionSoft" Margin="-5" CornerRadius="14" BorderThickness="2.5" BorderBrush="{DynamicResource AccentSoft}" Visibility="Hidden" IsHitTestVisible="False"/>
            <Border x:Name="selection" Margin="-2.5" CornerRadius="11.5" BorderThickness="2.5" BorderBrush="{DynamicResource Accent}" Visibility="Hidden" IsHitTestVisible="False"/>
            <Border x:Name="art" CornerRadius="9" BorderThickness="0.5" BorderBrush="{DynamicResource SettingsControlLine}" Effect="{DynamicResource SettingsModeThumbShadow}"><ContentPresenter Margin="-0.5"/></Border>
            <TextBlock Grid.Row="2" Text="{TemplateBinding Tag}" Foreground="{TemplateBinding Foreground}" FontSize="12" FontWeight="Medium" LineHeight="12" LineStackingStrategy="BlockLineHeight" HorizontalAlignment="Center"/>
            <Border x:Name="focus" Grid.RowSpan="3" BorderBrush="Transparent" BorderThickness="1" CornerRadius="10" Margin="-6" IsHitTestVisible="False"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter Property="Foreground" Value="{DynamicResource Text}"/>
              <Setter TargetName="selectionSoft" Property="Visibility" Value="Visible"/><Setter TargetName="selection" Property="Visibility" Value="Visible"/>
              <Setter TargetName="art" Property="Effect" Value="{x:Null}"/><Setter TargetName="art" Property="BorderBrush" Value="Transparent"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="focus" Property="BorderBrush" Value="{DynamicResource Text}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="AppearanceFontChoice" TargetType="Button">
        <Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="Width" Value="76"/><Setter Property="Height" Value="69"/><Setter Property="MinHeight" Value="69"/>
        <Setter Property="Padding" Value="0"/><Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Cursor" Value="Hand"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
          <Grid>
            <Grid.RowDefinitions><RowDefinition Height="50"/><RowDefinition Height="19"/></Grid.RowDefinitions>
            <Border x:Name="selectionSoft" Margin="-5" CornerRadius="14" BorderThickness="2.5" BorderBrush="{DynamicResource AccentSoft}" Visibility="Hidden" IsHitTestVisible="False"/>
            <Border x:Name="selection" Margin="-2.5" CornerRadius="11.5" BorderThickness="2.5" BorderBrush="{DynamicResource Accent}" Visibility="Hidden" IsHitTestVisible="False"/>
            <Border x:Name="artShadow" CornerRadius="9" Background="{DynamicResource AppearancePaper}" Effect="{DynamicResource SettingsModeThumbShadow}" IsHitTestVisible="False"/>
            <ContentPresenter Grid.RowSpan="2"/>
            <Border x:Name="art" CornerRadius="9" BorderThickness="0.5" BorderBrush="{DynamicResource SettingsControlLine}" IsHitTestVisible="False"/>
            <Border x:Name="focus" Grid.RowSpan="2" BorderBrush="Transparent" BorderThickness="1" CornerRadius="10" Margin="-6" IsHitTestVisible="False"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="Tag" Value="selected"><Setter TargetName="artShadow" Property="Effect" Value="{x:Null}"/><Setter TargetName="selectionSoft" Property="Visibility" Value="Visible"/><Setter TargetName="selection" Property="Visibility" Value="Visible"/><Setter TargetName="art" Property="BorderBrush" Value="Transparent"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="focus" Property="BorderBrush" Value="{DynamicResource Text}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="AppearanceSwatch" TargetType="Button">
        <Setter Property="Width" Value="20"/><Setter Property="Height" Value="20"/><Setter Property="MinHeight" Value="20"/>
        <Setter Property="Padding" Value="0"/><Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Cursor" Value="Hand"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
          <Grid>
            <Border x:Name="selection" Margin="-4" CornerRadius="14" BorderThickness="2" BorderBrush="{TemplateBinding Background}" Background="{DynamicResource Panel}" Visibility="Hidden" IsHitTestVisible="False"/>
            <Border CornerRadius="10" Background="{TemplateBinding Background}" BorderBrush="#26000000" BorderThickness="0.5"/>
            <Border x:Name="focus" CornerRadius="15" BorderBrush="Transparent" BorderThickness="1" Margin="-6" IsHitTestVisible="False"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="Tag" Value="selected"><Setter TargetName="selection" Property="Visibility" Value="Visible"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="focus" Property="BorderBrush" Value="{DynamicResource Text}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="AppearanceSizeTrackButton" TargetType="RepeatButton">
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton">
          <Border Height="4" CornerRadius="2" Background="{TemplateBinding Background}" Margin="0,0,-10,0" VerticalAlignment="Center"/>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="AppearanceSizeRemaining" TargetType="RepeatButton" BasedOn="{StaticResource AppearanceSizeTrackButton}">
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton">
          <Border Height="4" CornerRadius="2" Background="{TemplateBinding Background}" Margin="-10,0,0,0" VerticalAlignment="Center"/>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="AppearanceSizeSlider" TargetType="Slider">
        <Setter Property="Height" Value="20"/><Setter Property="MinHeight" Value="20"/>
        <Setter Property="Background" Value="Transparent"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Slider">
          <Grid>
            <Border Height="4" CornerRadius="2" Background="{DynamicResource AppearanceFill3}"/>
            <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{Binding Value, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
              <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Style="{StaticResource AppearanceSizeTrackButton}" Background="{DynamicResource Accent}"/></Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb x:Name="thumb" Width="20" Height="20" Focusable="False">
                <Thumb.Template><ControlTemplate TargetType="Thumb"><Border CornerRadius="10" Background="White" BorderBrush="#14000000" BorderThickness="0.5"><Border.Effect><DropShadowEffect Color="Black" BlurRadius="3" ShadowDepth="1" Direction="270" Opacity="0.3"/></Border.Effect></Border></ControlTemplate></Thumb.Template>
              </Thumb></Track.Thumb>
              <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Style="{StaticResource AppearanceSizeRemaining}" Background="{DynamicResource AppearanceFill3}"/></Track.IncreaseRepeatButton>
            </Track>
            <Border x:Name="focus" BorderBrush="Transparent" BorderThickness="1" CornerRadius="12" Margin="-3" IsHitTestVisible="False"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="focus" Property="BorderBrush" Value="{DynamicResource SettingsLink}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="AppearancePopup" TargetType="ComboBox">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="FontFamily" Value="{DynamicResource UiFont}"/>
        <Setter Property="VerticalAlignment" Value="Center"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Height" Value="26"/><Setter Property="MinHeight" Value="26"/>
        <Setter Property="FontSize" Value="12.5"/><Setter Property="FontWeight" Value="Medium"/>
        <Setter Property="Padding" Value="10,0,28,0"/><Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Background" Value="{DynamicResource SettingsLanguageThumb}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
          <Grid>
            <ToggleButton Focusable="False" ClickMode="Press" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
              <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                <Border Background="{DynamicResource SettingsLanguageThumb}" CornerRadius="7" Effect="{DynamicResource SettingsModeThumbShadow}">
                  <Canvas Width="10" Height="12" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,9,0"><Path Data="M2 4.5 5 1.8 8 4.5 M2 7.5 5 10.2 8 7.5" Stroke="#8B8984" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/></Canvas>
                </Border>
              </ControlTemplate></ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible="False" Margin="{TemplateBinding Padding}" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}" ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}"/>
            <Border x:Name="focus" CornerRadius="7" BorderBrush="Transparent" BorderThickness="1" IsHitTestVisible="False"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" VerticalOffset="4" AllowsTransparency="True" Focusable="False" IsOpen="{TemplateBinding IsDropDownOpen}">
              <Border Background="{DynamicResource Panel}" BorderBrush="{DynamicResource Line}" BorderThickness="0.5" CornerRadius="8" Padding="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="{TemplateBinding MaxDropDownHeight}">
                <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="focus" Property="BorderBrush" Value="{DynamicResource SettingsLink}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
    </ResourceDictionary>
    """);
}
