using TypeLay.Core;

namespace TypeLay.Windows.Contracts;

// Source-only native diagnostic words. Queue flags are hints, not proof that a
// particular key/message can be retrieved. This never grants write authority.
public static class SourceNativeReason
{
    public const uint PendingWork = 0x10000;
    public const uint PendingQueueMask = 0x1dff;

    public static bool TryDecode(uint nativeReason, out Denial reason)
    {
        // Also accept older native modules' base-only pending-work diagnostic.
        // Bits outside the originally queried queue flags are not a known code.
        reason = (nativeReason & ~PendingQueueMask) == PendingWork
            ? Denial.EpochChanged : (Denial)nativeReason;
        return Enum.IsDefined(reason);
    }
}
