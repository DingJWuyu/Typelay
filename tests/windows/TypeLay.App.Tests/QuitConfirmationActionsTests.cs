using System.Windows;
using System.Windows.Automation;
using TypeLay.App;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class QuitConfirmationActionsTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PublicQuitConfirmationCanBeInvokedWithOrWithoutAVisibleOwner(bool visibleOwner) => desktop.RunAsync(async () =>
    {
        var owner = new Window { Width = 400, Height = 250, ShowInTaskbar = false };
        QuitConfirmationWindow? dialog = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (visibleOwner)
                owner.Show();
            dialog = new QuitConfirmationWindow(owner);
            var current = dialog;
            using var cleanup = deadline.Token.Register(() => current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (current.IsVisible)
                    current.Close();
            })));
            var interaction = Task.Run(async () =>
            {
                try
                {
                    AutomationElement? button;
                    do
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        button = QuitConfirmationActions.Find(Environment.ProcessId, deadline.Token, out _);
                        if (button is null)
                            await Task.Delay(40, deadline.Token);
                    } while (button is null);
                    var invoke = (InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern);
                    deadline.Token.ThrowIfCancellationRequested();
                    invoke.Invoke();
                }
                catch
                {
                    // A failed public lookup must still release the modal test loop.
                    await current.Dispatcher.InvokeAsync(() => { if (current.IsVisible) current.Close(); });
                    throw;
                }
            });
            bool? confirmed = current.ShowDialog();
            await interaction.WaitAsync(deadline.Token);
            Assert.True(confirmed);
            Assert.Equal(visibleOwner, owner.IsVisible);
            Assert.Null(await Task.Run(() => QuitConfirmationActions.Find(Environment.ProcessId, deadline.Token, out _), deadline.Token));
        }
        finally
        {
            if (dialog?.IsVisible == true)
                dialog.Close();
            owner.Close();
        }
    });
}
