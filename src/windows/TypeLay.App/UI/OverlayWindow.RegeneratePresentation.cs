using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    // Presentation only: callers retain the exact buttons, action handlers,
    // focus ownership, confirmation identity and retirement/return rules.
    private static Border CreateRegenerateSheet(Button keep, Button replace)
    {
        var title = UiTheme.Text(UiTheme.L("替换你修改过的译文？", "Replace your edited translation?"), 18);
        title.FontFamily = UiTheme.DisplayFontForSize(18);
        title.FontWeight = FontWeights.Medium;
        title.LineHeight = 22.5;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        AutomationProperties.SetAutomationId(title, "RegenerateConfirmationTitle");
        // Adjacent block margins in the prototype collapse to the larger 20px
        // actions margin; do not add h2's 14px a second time in a WPF stack.
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new(0, 20, 0, 0)
        };
        var style = (Style)XamlReader.Parse(RegenerateButtonStyle);
        foreach (var button in new[] { keep, replace })
        {
            button.Style = style;
            button.Height = 26;
            button.MinHeight = 26;
            button.Padding = new(12, 0, 12, 0);
            button.FontSize = 12.5;
            button.FontWeight = FontWeights.Medium;
            button.Margin = new(0);
            actions.Children.Add(button);
        }
        keep.Margin = new(0, 0, 8, 0);
        var body = new StackPanel { Margin = new(22) };
        body.Children.Add(title);
        body.Children.Add(actions);
        var content = new Grid();
        content.Children.Add(body);
        // CSS inset highlight does not consume the sheet's border-box padding.
        var edge = new Border { CornerRadius = new(16), BorderThickness = new(.5), IsHitTestVisible = false };
        edge.SetResourceReference(Border.BorderBrushProperty, "GlassEdge");
        content.Children.Add(edge);
        var sheet = new Border
        {
            Width = 340,
            HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new(16),
            Child = content
        };
        sheet.SetResourceReference(Border.BackgroundProperty, "GlassStrong");
        return sheet;
    }

    private const string RegenerateButtonStyle = """
    <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
      <Setter Property="Foreground" Value="{DynamicResource Text}"/>
      <Setter Property="Background" Value="{DynamicResource Control}"/>
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
        <Grid>
          <Border x:Name="fill" CornerRadius="7" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
          </Border>
          <Border CornerRadius="7" BorderThickness="0.5" BorderBrush="{DynamicResource Line}" IsHitTestVisible="False"/>
          <Border x:Name="focus" CornerRadius="8" BorderThickness="3" BorderBrush="{DynamicResource Accent}" Opacity="0.55" Margin="-4" Visibility="Collapsed" IsHitTestVisible="False"/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property="IsPressed" Value="True"><Setter TargetName="fill" Property="Background" Value="{DynamicResource Fill2}"/></Trigger>
          <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="focus" Property="Visibility" Value="Visible"/></Trigger>
          <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/><Setter Property="Cursor" Value="Arrow"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    """;
}
