using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

// WPF permits only one Application per process, including after Shutdown.
// The non-parallel Desktop collection owns its STA and closes each test's windows.
public sealed class WpfDesktopFixture : IAsyncLifetime
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _timedOut;

    public async ValueTask InitializeAsync()
    {
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                try
                {
                    UiTheme.Initialize("light");
                    _ready.SetResult(application.Dispatcher);
                    application.Run();
                }
                finally { application.Shutdown(); }
                _stopped.SetResult();
            }
            catch (Exception error)
            {
                _ready.TrySetException(error);
                _stopped.SetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "TypeLay WPF desktop tests"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public Task RunAsync(Action scenario) => RunAsync(() =>
    {
        scenario();
        return Task.CompletedTask;
    });

    public async Task RunAsync(Func<Task> scenario)
    {
        Assert.False(_timedOut, "The WPF dispatcher exceeded a previous test's deadline and cannot run another scenario safely.");
        var dispatcher = await _ready.Task;
        Task run = dispatcher.InvokeAsync(async () =>
        {
            UiTheme.Apply("light");
            try
            {
                await scenario();
            }
            finally
            {
                foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray())
                {
                    // SettingsWindow normally hides on Close. Test cleanup must
                    // release the actual window without changing that behavior.
                    window.Closing += AllowClose;
                    try
                    {
                        window.Close();
                    }
                    finally { window.Closing -= AllowClose; }
                }
            }
        }).Task.Unwrap();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(40));
        }
        catch (TimeoutException) when (!run.IsCompleted)
        {
            _timedOut = true;
            throw;
        }
    }

    private static void AllowClose(object? sender, CancelEventArgs args) => args.Cancel = false;

    public async ValueTask DisposeAsync()
    {
        if (_ready.Task.IsCompletedSuccessfully)
        {
            var dispatcher = _ready.Task.Result;
            if (!dispatcher.HasShutdownStarted)
                _ = dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
        }
        await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
