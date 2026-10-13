using System.Globalization;
using TypeLay.Core;

namespace TypeLay.Windows.Contracts;

/// <summary>Windows binding metadata. The shared core sees only its opaque Id.</summary>
public readonly record struct WindowsTarget(int ProcessId, long ProcessStartUtcTicks,
    long RootHwnd, long ControlHwnd, string Provider, string ElementToken, Guid Lifetime)
{
    public TargetId Id => new("windows", FormattableString.Invariant(
        $"{ProcessId}/{ProcessStartUtcTicks}/{RootHwnd}/{ControlHwnd}/{Uri.EscapeDataString(Provider)}/{Uri.EscapeDataString(ElementToken)}"), Lifetime);

    public static implicit operator TargetId(WindowsTarget target) => target.Id;

    public static bool TryDecode(TargetId id, out WindowsTarget target)
    {
        target = default;
        if (id.Backend != "windows" || id.Value is null)
            return false;
        string[] parts = id.Value.Split('/');
        if (parts.Length != 6
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int process)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long start)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long root)
            || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long control))
            return false;
        target = new(process, start, root, control, Uri.UnescapeDataString(parts[4]), Uri.UnescapeDataString(parts[5]), id.Lifetime);
        // Reject noncanonical encodings so distinct opaque identities cannot alias a binding.
        return target.Id == id;
    }
}

public static class WindowsTargetExtensions
{
    public static WindowsTarget AsWindows(this TargetId id) => WindowsTarget.TryDecode(id, out var target)
        ? target : throw new InvalidDataException("target-identity");
}
