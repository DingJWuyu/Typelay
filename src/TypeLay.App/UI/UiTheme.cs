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
      <Style TargetType="ComboBox"><Setter Property="FontSize" Value="12"/><Setter Property="MinHeight" Value="36"/><Setter Property="Padding" Value="8,5"/><Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="Background" Value="{DynamicResource Panel}"/><Setter Property="BorderBrush" Value="{DynamicResource Line}"/></Style>
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
