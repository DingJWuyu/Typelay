using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace TypeLay.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class NativeMethods
{
    public const uint WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, WM_SETTEXT = 0x000C;
    public const uint EM_GETSEL = 0x00B0, EM_SETSEL = 0x00B1, EM_REPLACESEL = 0x00C2;
    public const string ImeProperty = "TypeLay.Mvp.Fixture.Ime.v1";
    public const string LifetimeProperty = "TypeLay.Mvp.Fixture.Lifetime.v1";
    public const string ReadCountProperty = "TypeLay.Mvp.Fixture.TextReads.v1";
    public const uint FixtureFocus = 0x8001, FixtureSubmitCount = 0x8002, FixtureClearPassword = 0x8003;
    public const uint FixtureBlockGui = 0x8004, FixtureGuiDrained = 0x8005, FixtureBarrierStatus = 0x8006;

    public const uint FixtureArmNullBarrier = 0x8007, FixtureArmClickBarrier = 0x8008;
    public const uint FixtureAllowBrokerForeground = 0x8009;
    public const string FixtureClickField = "TypeLay.Test.PreparationClickField.v1";
    public const string FixtureClickReceipt = "TypeLay.Test.PreparationClickReceipt.v1";

    public static string FixtureGuiBarrierName(int processId, string state) =>
        $"Local\\TypeLay.Fixture.GuiBarrier.{processId}.{state}";

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MessageStruct
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WindowClass
    {
        public uint Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
    }

    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetStyle(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    public static extern nint GetProp(nint hwnd, string name);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProp(nint hwnd, string name, nint value);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)]
    public static extern nint RemoveProp(nint hwnd, string name);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendRaw(nint hwnd, uint message, nuint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendRead(nint hwnd, uint message, nuint wParam, StringBuilder text,
        uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendText(nint hwnd, uint message, nuint wParam, string text,
        uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClass(ref WindowClass definition);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindow(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static extern nint CallWindowProc(nint proc, nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    public static extern int GetMessage(out MessageStruct message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MessageStruct message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static extern nint DispatchMessage(ref MessageStruct message);
    [DllImport("user32.dll", EntryPoint = "IsDialogMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsDialogMessage(nint hwnd, ref MessageStruct message);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? module);

    public static nuint Send(nint hwnd, uint message, nuint wParam = 0, nint lParam = 0)
    {
        if (SendRaw(hwnd, message, wParam, lParam, 0x22, 100, out nuint result) == 0)
            throw new NativeMessageException(nameof(Send), message, Marshal.GetLastPInvokeError());
        return result;
    }
    public static void SendString(nint hwnd, uint message, string text, nuint wParam = 0, uint timeoutMs = 100)
    {
        if (SendText(hwnd, message, wParam, text, 0x22, timeoutMs, out _) == 0)
            throw new NativeMessageException(nameof(SendString), message, Marshal.GetLastPInvokeError());
    }
    public static string ReadText(nint hwnd, int limit)
    {
        nuint length = Send(hwnd, WM_GETTEXTLENGTH);
        if (length > (nuint)limit)
            throw new TextReadException(TextReadFailure.TextLimit, limit, (ulong)length);
        var buffer = new StringBuilder((int)length + 2);
        if (SendRead(hwnd, WM_GETTEXT, (nuint)buffer.Capacity, buffer, 0x22, 100, out nuint count) == 0)
            throw new NativeMessageException(nameof(ReadText), WM_GETTEXT, Marshal.GetLastPInvokeError());
        string result = buffer.ToString();
        if ((nuint)result.Length != count)
            throw new TextReadException(TextReadFailure.CountMismatch, limit, (ulong)length, (ulong)count, result.Length);
        nuint finalLength = Send(hwnd, WM_GETTEXTLENGTH);
        if (finalLength != (nuint)result.Length)
            throw new TextReadException(TextReadFailure.LengthChangedDuringRead, limit, (ulong)length,
                (ulong)count, result.Length, (ulong)finalLength);
        return result;
    }
    // Only for a caller already waiting for an expected text transition. A changed
    // length is an incomplete observation, never success or an internal retry.
    // Stable-source assertions must continue to use strict ReadText.
    public static bool TextMatches(nint hwnd, int limit, string expected, out TextReadException? changedDuringRead)
    {
        changedDuringRead = null;
        try
        {
            return ReadText(hwnd, limit) == expected;
        }
        catch (TextReadException error) when (error.Reason == TextReadFailure.LengthChangedDuringRead)
        {
            changedDuringRead = error;
            return false;
        }
    }
    public enum TextReadFailure
    {
        TextLimit, CountMismatch, LengthChangedDuringRead
    }
    // Numeric metadata only: no source text, HWND, path or arbitrary exception message.
    public sealed class TextReadException(TextReadFailure reason, int limit, ulong initialLength,
        ulong? returnedCount = null, int? managedLength = null, ulong? finalLength = null)
        : InvalidOperationException("Text observation was incomplete or invalid.")
    {
        public TextReadFailure Reason { get; } = reason;
        public int Limit { get; } = limit;
        public ulong InitialLength { get; } = initialLength;
        public ulong? ReturnedCount { get; } = returnedCount;
        public int? ManagedLength { get; } = managedLength;
        public ulong? FinalLength { get; } = finalLength;
    }
    // Public fixture labels change independently of observation. Read one bounded
    // WM_GETTEXT snapshot; never use this helper for source text or acceptance authority.
    public static string ReadStaticNotice(nint hwnd)
    {
        uint thread = GetWindowThreadProcessId(hwnd, out uint process);
        nint root = GetAncestor(hwnd, 2);
        bool StaticClass()
        {
            var name = new StringBuilder(32);
            return GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == "Static";
        }
        if (thread == 0 || process == 0 || root == 0 || !StaticClass())
            throw new InvalidOperationException("Public notice identity.");
        var buffer = new StringBuilder(4096);
        if (SendRead(hwnd, WM_GETTEXT, (nuint)buffer.Capacity, buffer, 0x22, 100, out nuint count) == 0)
            throw new NativeMessageException(nameof(ReadStaticNotice), WM_GETTEXT, Marshal.GetLastPInvokeError());
        string result = buffer.ToString();
        if (count >= (nuint)(buffer.Capacity - 1) || count != (nuint)result.Length)
            throw new InvalidOperationException("Public notice truncated or invalid.");
        if (GetWindowThreadProcessId(hwnd, out uint currentProcess) != thread || currentProcess != process
            || GetAncestor(hwnd, 2) != root || !StaticClass())
            throw new InvalidOperationException("Public notice identity changed.");
        return result;
    }
    public static TypeLay.Core.TextRange ReadSelection(nint hwnd)
    {
        // The adapter is capped at 8192 UTF-16 units. Thus packed 16-bit offsets are sufficient.
        nuint packed = Send(hwnd, EM_GETSEL);
        return new((int)(packed & 0xffff), (int)((packed >> 16) & 0xffff));
    }
    // These helpers target .NET, whose SetLastError=true marshaller clears and
    // caches the native error. Zero means generic failure, not proven timeout.
    // Never retain handles, message arguments, source text or exception strings.
    public sealed class NativeMessageException(string operation, uint messageId, int win32Error)
        : InvalidOperationException("Native message did not complete.")
    {
        public string Operation { get; } = operation;
        public uint MessageId { get; } = messageId;
        public int Win32Error { get; } = win32Error;
    }
    // A null GUI result means the call was skipped. GetForegroundWindow has no last-error contract.
    public readonly record struct FocusObservation(nint Root, nint Control, uint ThreadId, uint ProcessId,
        int? ThreadLookupError, bool? GuiThreadInfoSucceeded, int? GuiThreadInfoError);

    public static FocusObservation ReadFocus()
    {
        nint root = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(root, out uint process);
        int? threadError = thread == 0 ? Marshal.GetLastPInvokeError() : null;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0)
            return new(root, 0, thread, process, threadError, null, null);
        bool succeeded = GetGUIThreadInfo(thread, ref info);
        int? guiError = succeeded ? null : Marshal.GetLastPInvokeError();
        return new(root, succeeded ? info.Focus : 0, thread, process, threadError, succeeded, guiError);
    }

    public static (nint Root, nint Control) Focused()
    {
        var observation = ReadFocus();
        return (observation.Root, observation.Control);
    }
}
