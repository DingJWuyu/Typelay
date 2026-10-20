using System.Runtime.InteropServices;

internal static partial class HyPackageCheck
{
    // Both the visible source and tray fallback use this exact native event
    // path. A successful insertion is not evidence of receiver focus or a click.
    internal static (bool Complete, bool ReleaseComplete) SendLeftClick()
    {
        var click = new[]
        {
            new MouseEvent { Mouse = new() { Flags = 2 } },
            new MouseEvent { Mouse = new() { Flags = 4 } }
        };
        uint inserted = SendInput(2, click, Marshal.SizeOf<MouseEvent>());
        bool released = inserted != 1 || SendInput(1, [click[1]], Marshal.SizeOf<MouseEvent>()) == 1;
        return (inserted == 2, released);
    }

    // INPUT's union contains MOUSEINPUT. Sequential native alignment retains
    // the pointer-sized extra-info member rather than guessing dwFlags' offset
    // (on x64 flags is at byte 20, whereas byte 24 is the event timestamp).
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEvent
    {
        public uint Type;
        public MouseData Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, MouseEvent[] inputs, int size);
}
