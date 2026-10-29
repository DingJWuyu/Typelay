using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TypeLay.App;

internal sealed class QuitConfirmationWindow : Window
{
    private bool _settled;

    public QuitConfirmationWindow(Window? owner = null)
    {
        Title = UiTheme.L("退出 TypeLay？", "Quit TypeLay?");
        Width = 356;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (owner?.IsVisible == true)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        FontFamily = UiTheme.UiFont;
        AutomationProperties.SetAutomationId(this, "TypeLayQuitConfirmation");
        var body = new StackPanel { Margin = new(22) };
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Cycle);
        var title = UiTheme.Text(Title, 18);
        title.FontFamily = UiTheme.DisplayFont;
        title.FontWeight = FontWeights.Medium;
        title.LineHeight = 22.5;
        title.Margin = new(0, 0, 0, 14);
        AutomationProperties.SetAutomationId(title, "QuitConfirmationTitle");
        body.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        var cancel = UiTheme.Button(UiTheme.L("取消", "Cancel"), () => Complete(false));
        cancel.IsCancel = true;
        cancel.IsDefault = true;
        cancel.Margin = new(0, 0, 8, 0);
        var quit = UiTheme.Button(UiTheme.L("退出", "Quit"), () => Complete(true), primary: true);
        foreach (var button in new[] { cancel, quit })
        {
            button.Height = 26;
            button.MinHeight = 26;
            button.Padding = new(12, 0, 12, 0);
            button.FontSize = 12.5;
            button.FontWeight = FontWeights.Medium;
            actions.Children.Add(button);
        }
        AutomationProperties.SetAutomationId(cancel, "QuitCancel");
        AutomationProperties.SetAutomationId(quit, "QuitConfirm");
        body.Children.Add(actions);
        var shell = UiTheme.Border(body, 16);
        shell.Margin = new(8);
        shell.BorderThickness = new(.5);
        shell.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = .18 };
        Content = shell;
        ContentRendered += (_, _) =>
        {
            if (!_settled && IsActive)
                cancel.Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _settled || !IsActive)
                return;
            e.Handled = true;
            Complete(false);
        };
        Closed += (_, _) => _settled = true;
    }

    private void Complete(bool confirmed)
    {
        if (_settled || !IsVisible)
            return;
        _settled = true;
        DialogResult = confirmed;
    }
}
