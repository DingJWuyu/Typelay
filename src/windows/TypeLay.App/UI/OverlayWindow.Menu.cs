using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Input;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private sealed class EditorMenuButton(OverlayWindow owner) : Button
    {
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            // ButtonBase normally focuses itself on mouse-down. Keep an already
            // focused ordinary editor as the menu's natural restoration target;
            // keyboard navigation and every owned-source handoff stay unchanged.
            bool focusable = Focusable;
            bool preserve = owner._editing && owner._editor.IsKeyboardFocused && !owner.EditingComposition
                && owner._ownedEditor is null && owner._viewContext is { CanEdit: true } context
                && owner._ownedEditorHost?.IsOwnedEditorTarget(context.Token.Target) != true;
            try
            {
                if (preserve)
                    SetCurrentValue(FocusableProperty, false);
                base.OnMouseLeftButtonDown(e);
            }
            finally
            {
                if (preserve)
                    SetCurrentValue(FocusableProperty, focusable);
            }
        }
    }

    private static void StyleMenu(ContextMenu menu, Button anchor)
    {
        // Preserve MenuItem, its PART_Popup and the native WPF menu keyboard/
        // automation behavior. These templates change only presentation.
        menu.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="ContextMenu">
                <Setter Property="MinWidth" Value="208"/>
                <Setter Property="FontSize" Value="12.5"/>
                <Setter Property="Foreground" Value="{DynamicResource Text}"/>
                <Setter Property="Background" Value="{DynamicResource Glass}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource Line}"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ContextMenu">
                  <Border CornerRadius="12" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="0.5" Padding="5" Grid.IsSharedSizeScope="True">
                    <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" Focusable="False">
                      <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/>
                    </ScrollViewer>
                  </Border>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="MenuItem">
                <Setter Property="Foreground" Value="{DynamicResource Text}"/>
                <Setter Property="FontSize" Value="12.5"/>
                <Setter Property="Padding" Value="7,6"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem">
                  <Grid>
                    <Border x:Name="row" CornerRadius="7" Background="Transparent" Padding="{TemplateBinding Padding}">
                      <Grid>
                        <Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*" SharedSizeGroup="MenuHeader"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="16"/></Grid.ColumnDefinitions>
                        <Path x:Name="check" Width="10" Height="8" Stretch="Uniform" Data="M1 4 L4 7 L10 1" Stroke="{DynamicResource AccentInk}" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Visibility="Collapsed"/>
                        <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
                        <TextBlock Grid.Column="2" Margin="14,0,2,0" Text="{TemplateBinding InputGestureText}" Foreground="{DynamicResource Tertiary}" VerticalAlignment="Center"/>
                        <Path x:Name="arrow" Grid.Column="3" Width="5" Height="9" Stretch="Uniform" Data="M1 1 L5 5 L1 9" Stroke="{DynamicResource Muted}" StrokeThickness="1.4" Visibility="Collapsed"/>
                      </Grid>
                    </Border>
                    <Popup x:Name="PART_Popup" Placement="Right" HorizontalOffset="3" AllowsTransparency="True" Focusable="False" PopupAnimation="None"
                        IsOpen="{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}" PlacementTarget="{Binding RelativeSource={RelativeSource TemplatedParent}}">
                      <Border CornerRadius="12" Background="{DynamicResource Glass}" BorderBrush="{DynamicResource Line}" BorderThickness="0.5" Padding="5" MinWidth="168" Grid.IsSharedSizeScope="True">
                        <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" Focusable="False">
                          <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/>
                        </ScrollViewer>
                      </Border>
                    </Popup>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource AccentSoft}"/></Trigger>
                    <Trigger Property="IsChecked" Value="True"><Setter TargetName="check" Property="Visibility" Value="Visible"/></Trigger>
                    <Trigger Property="HasItems" Value="True"><Setter TargetName="arrow" Property="Visibility" Value="Visible"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{DynamicResource Tertiary}"/><Setter TargetName="row" Property="Opacity" Value="0.55"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="Separator"><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="0.5" Margin="8,5" Background="{DynamicResource Line}"/></ControlTemplate></Setter.Value></Setter></Style>
            </ResourceDictionary>
            """));
        menu.FontFamily = UiTheme.UiFont;
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (size, target, _) =>
        [
            new(new Point(target.Width - size.Width, -size.Height - 6), PopupPrimaryAxis.Vertical),
            new(new Point(target.Width - size.Width, target.Height + 6), PopupPrimaryAxis.Vertical)
        ];
    }
}
