using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using N = TypeLay.Platform.Windows.NativeMethods;

// One ordinary source-GUI pump boundary. No stock Edit subclass, input rewrite,
// production wake or claim about unknown applications' pumping conventions.
[SupportedOSPlatform("windows")]
internal static class SourceQueueWaitProbe
{
    internal const uint Begin = 0x8070, Coordinate = 0x8071, Work = 0x8072, Cleanup = 0x8073;
    private static bool _used;
    private static int _work;

    internal static bool Handle(nint root, uint message, nuint parameter)
    {
        if (message == Work)
        {
            N.SetProp(root, "TypeLay.Test.SourceWait.Work.v1", ++_work);
            N.SetProp(root, "TypeLay.Test.SourceWait.WorkThread.v1", (nint)GetCurrentThreadId());
            return true;
        }
        if (message == Cleanup)
        {
            N.SetProp(root, "TypeLay.Test.SourceWait.Cleanup.v1", 1);
            return true;
        }
        if (message != Begin)
            return false;
        if (_used || parameter == 0)
            return true;
        _used = true;
        // This is before coordination posts work. No queue-checking operation
        // occurs after SendMessage returns and before the actual WaitMessage.
        bool pending = PeekMessageW(out _, root, Work, Work, 0);
        N.SetProp(root, "TypeLay.Test.SourceWait.Empty.v1", pending ? 0 : 1);
        if (pending)
            return true;
        // Normal SendMessage permits incoming sent callbacks while waiting.
        // Its coordinator posts work and optionally injects a real LL Tab.
        nint coordinated = SendMessageW((nint)parameter, Coordinate, (nuint)root, 0);
        N.SetProp(root, "TypeLay.Test.SourceWait.Coordinated.v1", coordinated);
        N.SetProp(root, "TypeLay.Test.SourceWait.Entered.v1", 1);
        bool returned = WaitMessage();
        N.SetProp(root, "TypeLay.Test.SourceWait.Returned.v1", returned ? 1 : 2);
        // The unchanged outer GetMessage/IsDialogMessage loop handles work and
        // original keys. In particular a normal Tab may navigate A to B.
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out N.MessageStruct message,
        nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nuint wp, nint lp);
    [DllImport("user32.dll")] private static extern bool WaitMessage();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
