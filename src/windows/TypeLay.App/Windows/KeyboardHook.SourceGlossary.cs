using System.Runtime.InteropServices;
using TypeLay.Windows;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class KeyboardHook
{
    private readonly object _sourceGlossarySync = new();
    private SourceGlossaryForegroundReceipt? _sourceGlossaryReceipt;
    internal bool SourceGlossaryCurrent(SourceGlossaryForegroundReceipt receipt, SessionGlossaryEntry entry, long original)
    {
        lock (_sourceGlossarySync)
            return ReferenceEquals(_sourceGlossaryReceipt, receipt)
                && receipt.IsCurrent(entry, original, WindowEpoch, CommandGeneration, WindowsInterruptClock.NowMs);
    }
    internal bool SourceGlossaryAuxiliary(SourceGlossaryForegroundReceipt receipt, SessionGlossaryEntry entry)
    {
        lock (_sourceGlossarySync)
            return ReferenceEquals(_sourceGlossaryReceipt, receipt)
                && receipt.IsAuxiliary(entry, WindowEpoch, CommandGeneration, WindowsInterruptClock.NowMs);
    }
    internal bool ArmSourceGlossaryReturn(SourceGlossaryForegroundReceipt receipt, SessionGlossaryEntry entry)
    {
        lock (_sourceGlossarySync)
            return ReferenceEquals(_sourceGlossaryReceipt, receipt)
                && receipt.ArmReturn(entry, WindowEpoch, CommandGeneration,
                    unchecked((uint)Environment.TickCount64), WindowsInterruptClock.NowMs);
    }
    internal void SetSourceGlossaryReceipt(SourceGlossaryForegroundReceipt receipt) =>
        Interlocked.Exchange(ref _sourceGlossaryReceipt, receipt)?.Revoke();
    internal void ClearSourceGlossaryReceipt(SourceGlossaryForegroundReceipt receipt) =>
        Interlocked.CompareExchange(ref _sourceGlossaryReceipt, null, receipt);
    private void ObserveSourceGlossaryForeground(nint window, uint eventTime) =>
        Volatile.Read(ref _sourceGlossaryReceipt)?.Observe((long)window, WindowEpoch, eventTime,
            CommandGeneration, WindowsInterruptClock.NowMs);
    internal static bool ExactNativeFieldFocused(WindowsTarget target)
    {
        uint thread = Native.GetWindowThreadProcessId((nint)target.ControlHwnd, out uint process);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && process == target.ProcessId && target.ControlHwnd != 0
            && Native.GetForegroundWindow() == (nint)target.RootHwnd
            && GetGUIThreadInfo(thread, ref info) && info.Focus == (nint)target.ControlHwnd;
    }
}
