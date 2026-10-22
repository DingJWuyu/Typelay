using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using N = TypeLay.Platform.Windows.NativeMethods;

// Only the controlled fixture's normal message pump observes this boundary.
// The real input MSG is neither replaced, consumed nor dispatched early.
[SupportedOSPlatform("windows")]
internal static class SourceDispatchProbe
{
    internal const uint Arm = 0x8074, Coordinate = 0x8075;
    private static nint _coordinator, _field;

    internal static bool Handle(uint message, nuint parameter, nint field)
    {
        if (message != Arm)
            return false;
        _coordinator = (nint)parameter;
        _field = field;
        return true;
    }

    internal static bool BeforeDispatch(nint root, N.MessageStruct message)
    {
        if (_coordinator == 0 || message.Hwnd != _field || message.Message != 0x100 || message.WParam != 0x27)
            return false;
        nint coordinator = _coordinator;
        _coordinator = 0;
        N.SetProp(root, "TypeLay.Test.Dispatch.Removed.v1", 1);
        N.SetProp(root, "TypeLay.Test.Dispatch.Thread.v1", (nint)GetCurrentThreadId());
        N.SetProp(root, "TypeLay.Test.Dispatch.Field.v1", message.Hwnd);
        uint pendingKeys = GetQueueStatus(1) >> 16;
        N.SetProp(root, "TypeLay.Test.Dispatch.PendingKeys.v1", (nint)pendingKeys);
        // QS_KEY is a wake hint, not proof of another retrievable key. Inspect
        // once without removing a message. The original Right remains in our
        // caller's local MSG until the unchanged outer loop dispatches it.
        bool found = PeekMessageW(out var next, 0, 0x100, 0x109, 0);
        N.SetProp(root, "TypeLay.Test.Dispatch.NextKeyFound.v1", found ? 1 : 0);
        N.SetProp(root, "TypeLay.Test.Dispatch.NextKeyMessage.v1", (nint)next.Message);
        N.SetProp(root, "TypeLay.Test.Dispatch.NextKeyCode.v1", (nint)next.WParam);
        N.SetProp(root, "TypeLay.Test.Dispatch.NextKeyBits.v1", next.LParam);
        N.SetProp(root, "TypeLay.Test.Dispatch.RemovedKeyBits.v1", message.LParam);
        nint result = SendMessageW(coordinator, Coordinate, (nuint)root, 0);
        N.SetProp(root, "TypeLay.Test.Dispatch.Coordinated.v1", result);
        return true;
    }

    internal static void AfterDispatch(nint root)
    {
        N.SetProp(root, "TypeLay.Test.Dispatch.Caret.v1", N.ReadSelection(_field).Start);
        N.SetProp(root, "TypeLay.Test.Dispatch.Dispatched.v1", 1);
    }

    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetQueueStatus(uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out N.MessageStruct message, nint hwnd, uint first, uint last, uint remove);
}
