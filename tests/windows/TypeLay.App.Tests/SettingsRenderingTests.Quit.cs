using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SettingsRenderingTests
{
    [Theory]
    [InlineData("zh-CN", "light", "cancel", true)]
    [InlineData("en", "dark", "escape", true)]
    [InlineData("zh-CN", "dark", "confirm", false)]
    [InlineData("en", "light", "close", false)]
    public Task QuitConfirmationRequiresAnExplicitChoiceAndKeepsItsOwnerVisible(string language, string theme, string choice, bool showOwner) => desktop.RunAsync(async () =>
    {
        UiTheme.Apply(new UserSettings(UiLanguage: language, Theme: theme, Motion: "reduce"));
        var owner = new Window { Content = new TextBlock { Text = "Existing work" }, Width = 480, Height = 300, ShowInTaskbar = false };
        QuitConfirmationWindow? dialog = null;
        try
        {
            if (showOwner)
            {
                owner.Show();
                owner.Activate();
            }
            // A never-shown Settings window must not be attached as modal owner.
            dialog = new QuitConfirmationWindow(owner);
            Assert.Equal(showOwner, dialog.Owner is not null);
            var current = dialog;
            var interaction = current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try
                {
                    current.Activate();
                    current.UpdateLayout();
                    Assert.Equal(language == "en" ? "Quit TypeLay?" : "退出 TypeLay？", current.Title);
                    var cancel = QuitButton(current, "QuitCancel");
                    var confirm = QuitButton(current, "QuitConfirm");
                    Assert.True(cancel.IsCancel);
                    Assert.True(cancel.IsDefault);
                    Assert.False(confirm.IsDefault);
                    Assert.True(cancel.IsKeyboardFocused);
                    Assert.Equal(26, cancel.ActualHeight);
                    Assert.Equal(26, confirm.ActualHeight);
                    Render(current, $"quit-confirmation-{language}-{theme}.png");
                    if (choice == "escape")
                    {
                        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(current)!, Environment.TickCount, Key.Escape)
                        {
                            RoutedEvent = Keyboard.PreviewKeyDownEvent
                        };
                        current.RaiseEvent(key);
                        Assert.True(key.Handled);
                    }
                    else if (choice == "close")
                        current.Close();
                    else
                        (choice == "confirm" ? confirm : cancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                finally { if (current.IsVisible) current.Close(); }
            }));
            bool? confirmed = current.ShowDialog();
            await interaction.Task;
            Assert.Equal(choice == "confirm", confirmed == true);
            Assert.Equal(showOwner, owner.IsVisible);
            Assert.Equal("Existing work", ((TextBlock)owner.Content).Text);
        }
        finally
        {
            if (dialog?.IsVisible == true)
                dialog.Close();
            owner.Close();
        }
    });

    [Fact]
    public Task RetiredQuitButtonsCannotConfirmOrDismissANewerPrompt() => desktop.RunAsync(async () =>
    {
        UiTheme.Apply(new UserSettings(Theme: "light", Motion: "reduce"));
        Button? oldConfirm = null;
        var first = new QuitConfirmationWindow();
        var firstInteraction = first.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                oldConfirm = QuitButton(first, "QuitConfirm");
                QuitButton(first, "QuitCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally { if (first.IsVisible) first.Close(); }
        }));
        Assert.NotEqual(true, first.ShowDialog());
        await firstInteraction.Task;
        var second = new QuitConfirmationWindow();
        var secondInteraction = second.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                oldConfirm!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(second.IsVisible);
                Assert.Null(second.DialogResult);
                var confirm = QuitButton(second, "QuitConfirm");
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally { if (second.IsVisible) second.Close(); }
        }));
        Assert.True(second.ShowDialog());
        await secondInteraction.Task;
    });

    private static Button QuitButton(Window window, string id) => Assert.Single(Descendants(window).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == id);
}
