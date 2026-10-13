using System.Runtime.InteropServices;

namespace TypeLay.Windows;

// One Windows-only timestamp domain for App/Broker observations and permits.
// Linked into both executables; the protocol/DTO assembly remains platform-free.
internal static class WindowsInterruptClock
{
    // Explicitly includes suspend time and is independent of .NET's TickCount
    // implementation. Integer flooring contributes less than one millisecond.
    public static long NowMs
    {
        get
        {
            QueryInterruptTimePrecise(out ulong time);
            return (long)(time / 10_000);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern void QueryInterruptTimePrecise(out ulong interruptTime);
}
