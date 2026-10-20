using System.IO;
using TypeLay.Runtime;

namespace TypeLay.Windows.Testing;

// Only the browser fixture's cleanup decision. A constructor may throw after
// creating a child, so successful assignment is not the ownership boundary.
internal sealed class BrowserFixtureProcess
{
    public IChildProcess? Child
    {
        get; private set;
    }
    public bool ExitConfirmed { get; private set; } = true;
    public T Start<T>(Func<T> start) where T : IChildProcess
    {
        if (!ExitConfirmed || Child is not null)
            throw new InvalidOperationException("fixture-child-already-started");
        ExitConfirmed = false;
        try
        {
            var child = start();
            Child = child;
            return child;
        }
        catch (ChildProcessCleanupUnconfirmedException error)
        {
            Child = error.Child;
            throw;
        }
    }
    public async Task StopAsync(Func<IChildProcess, Task> proveExit)
    {
        if (ExitConfirmed)
            return;
        if (Child is null)
            throw new IOException("fixture-child-exit-unconfirmed");
        await proveExit(Child);
        ExitConfirmed = true;
        Child.Dispose();
    }
}
