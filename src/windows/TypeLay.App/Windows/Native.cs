using System.Runtime.InteropServices;
namespace TypeLay.App;

internal static class Native
{
    public const uint WmApp = 0x8000;
    public delegate nint HookProc(int code, nint wParam, nint lParam);
    public delegate void WinEventProc(nint hook, uint ev, nint hwnd, int obj, int child, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct KeyData
    {
        public uint Key, Scan, Flags, Time; public nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern nint SetWindowsHookExW(int type, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint id, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] public static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc cb, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] public static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] public static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? module);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint handle);
}
