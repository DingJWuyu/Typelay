using TypeLay.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using N = TypeLay.Platform.Windows.NativeMethods;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            Console.Error.WriteLine("Native fixture requires Windows x64.");
            return 2;
        }
        bool hookTest = args.Length == 1 && args[0] is "--hook-test" or "--hook-test-automation";
        bool stockControls = args.Length == 1 && args[0] == "--stock-controls";
        if (args.Length != 0 && !hookTest && !stockControls)
            return 2;
        return RunWindows(hookTest, args.Contains("--hook-test-automation", StringComparer.Ordinal), stockControls);
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows(bool hookTest, bool automation, bool stockControls)
    {
        N.SetProcessDpiAwarenessContext((nint)(-4));
        nint instance = N.GetModuleHandle(null);
        var klass = new N.WindowClass
        {
            Procedure = RootProc,
            ClassName = "TypeLayMvpFixture",
            Instance = instance,
            Background = (nint)6
        };
        if (N.RegisterClass(ref klass) == 0)
            return 1;
        string title = hookTest ? "TypeLay Hook Fixture - " + (automation ? "automation" : "default")
            : "TypeLay MVP - Native fixture - TEST TEXT ONLY";
        _root = N.CreateWindow(0, klass.ClassName, title,
            0x00CF0000, 180, 100, 900, 720, 0, 0, instance, 0);
        if (_root == 0)
            return 1;
        // The stock Edit remains untouched. These fixture-owned events only
        // hold its GUI thread before a queued production transaction dispatch.
        using var barrierEntered = stockControls ? new EventWaitHandle(false, EventResetMode.ManualReset,
            N.FixtureGuiBarrierName(Environment.ProcessId, "entered")) : null;
        using var barrierRelease = stockControls ? new EventWaitHandle(false, EventResetMode.ManualReset,
            N.FixtureGuiBarrierName(Environment.ProcessId, "release")) : null;
        using var barrierDrained = stockControls ? new EventWaitHandle(false, EventResetMode.ManualReset,
            N.FixtureGuiBarrierName(Environment.ProcessId, "drained")) : null;
        _barrierEntered = barrierEntered;
        _barrierRelease = barrierRelease;
        _barrierDrained = barrierDrained;
        var fields = new List<FixtureField>();
        Add("A / single line", "a", "你好，我想测试 TypeLay。", 0, FieldAccess.Supported, 50);
        Add("B / multiline and selection", "b", "Hello 🙂 明天下午见, thanks.\r\nSecond line.", 4 | 0x1000 | 0x40,
            FieldAccess.Supported, 135, 95);
        Add("Password - must not be read", "password", "synthetic-password", 0x20, FieldAccess.Supported, 275);
        Add("Readonly - must not be read", "readonly", "synthetic-readonly", 0x800, FieldAccess.Supported, 355);
        Add("PIN - must not be read", "pin", "123456", 0, FieldAccess.Sensitive, 435);
        _submit = N.CreateWindow(0, "Button", "Submit (positive control only)", 0x50010000,
            30, 515, 300, 34, _root, (nint)200, instance, 0);
        _replayField = (nint)fields[0].Hwnd;
        _secondField = (nint)fields[1].Hwnd;
        if (!hookTest)
        {
            N.CreateWindow(0, "Button", "Continuous input (100 ms / committed-text replay)", 0x50010000,
                350, 515, 480, 34, _root, (nint)201, instance, 0);
            N.CreateWindow(0, "Button", "Focus switching (20 ms / 100 changes)", 0x50010000,
                350, 560, 480, 34, _root, (nint)202, instance, 0);
            _replayStatus = N.CreateWindow(0, "Static", "Replay has not run", 0x50000000,
                30, 610, 800, 24, _root, 0, instance, 0);
        }
        N.ShowWindow(_root, 5);
        N.SetForegroundWindow(_root);
        N.SetFocus((nint)fields[0].Hwnd);
        var descriptor = new FixtureDescriptor(Environment.ProcessId, (long)_root, (long)_submit,
            Guid.NewGuid(), fields.ToArray());
        Console.WriteLine(JsonSerializer.Serialize(descriptor));
        Console.Out.Flush(); // Metadata only; never output text or IME preedit content.
        if (hookTest)
        {
            nint status = N.CreateWindow(0, "Static", "Hook starting", 0x50000000,
                350, 515, 480, 48, _root, 0, instance, 0);
            _hookHarness = new HookHarness(descriptor, status, automation);
            if (SetTimer(_root, 1, 20, 0) == 0)
                throw new InvalidOperationException("Hook fixture timer failed.");
        }
        try
        {
            while (true)
            {
                int status = N.GetMessage(out N.MessageStruct message, 0, 0, 0);
                if (status == 0)
                    return 0;
                if (status < 0)
                    return 1;
                if (!N.IsDialogMessage(_root, ref message))
                {
                    N.TranslateMessage(ref message);
                    N.DispatchMessage(ref message);
                }
            }
        }
        finally { _hookHarness?.Dispose(); }

        void Add(string caption, string name, string text, uint editStyle, FieldAccess classification, int y, int height = 32)
        {
            N.CreateWindow(0, "Static", caption, 0x50000000, 30, y - 22, 750, 22, _root, 0, instance, 0);
            nint hwnd = N.CreateWindow(0x200, "Edit", text, 0x50010080 | editStyle,
                30, y, 790, height, _root, (nint)(101 + fields.Count), instance, 0);
            if (hwnd == 0)
                throw new InvalidOperationException("Fixture control creation failed.");
            int cookie = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            if (stockControls)
            {
                StockFields.Add(hwnd);
                fields.Add(new(name, (long)hwnd, classification, cookie));
                return; // Unmodified system Edit: no fixture IME or lifetime properties.
            }
            if (!N.SetProp(hwnd, N.ImeProperty, (nint)1)
                || !N.SetProp(hwnd, N.LifetimeProperty, (nint)cookie))
                throw new InvalidOperationException("Fixture metadata initialization failed.");
            nint old = N.SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(EditProc));
            if (old == 0)
                throw new InvalidOperationException("Fixture subclass failed.");
            PreviousProcedures[hwnd] = old;
            fields.Add(new(name, (long)hwnd, classification, cookie));
        }
    }

    private static nint _root, _submit;
    private static nint _replayField, _secondField, _replayStatus, _lastFocusedField;
    private static int _replayIndex, _focusSwitches;
    private static long _focusStarted;
    private static long _replayStarted;
    private const string ReplayText = "今天我们连续输入中文测试每个字都没有标点希望翻译在打字期间不断刷新并且不会抢走输入框的焦点";
    private static int _submits;
    private static HookHarness? _hookHarness;
    private static EventWaitHandle? _barrierEntered, _barrierRelease, _barrierDrained;
    private static int _barrierStatus;
    private static readonly Dictionary<nint, nint> PreviousProcedures = new();
    private static readonly HashSet<nint> StockFields = new();
    // Keep delegates alive as long as the HWNDs exist.
    [SupportedOSPlatform("windows")] private static readonly N.WindowProc RootProc = RootWindowProc;
    [SupportedOSPlatform("windows")] private static readonly N.WindowProc EditProc = EditWindowProc;

    [SupportedOSPlatform("windows")]
    private static nint RootWindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0002)
        {
            N.PostQuitMessage(0);
            return 0;
        }
        if (message == 0x0007 && _lastFocusedField != 0 && N.IsWindow(_lastFocusedField))
        {
            N.SetFocus(_lastFocusedField);
            return 0;
        }
        if (message == 0x0113 && wParam == 1)
        {
            _hookHarness?.Tick(_submits);
            return 0;
        }
        if (message == 0x0113 && wParam == 2)
        {
            if (N.GetForegroundWindow() != _root || GetFocus() != _replayField)
            {
                FinishReplay("cancelled-focus-changed");
                return 0;
            }
            N.Send(_replayField, N.EM_SETSEL, unchecked((nuint)(-1)), (nint)(-1));
            N.SendString(_replayField, N.EM_REPLACESEL, ReplayText.Substring(_replayIndex++, 1), 1);
            N.SendString(_replayStatus, N.WM_SETTEXT, "Replay: " + _replayIndex + "/" + ReplayText.Length);
            if (_replayIndex == ReplayText.Length)
                FinishReplay("completed");
            return 0;
        }
        if (message == 0x0113 && wParam == 3)
        {
            if (N.GetForegroundWindow() != _root)
            {
                FinishFocusReplay("cancelled-window-changed");
                return 0;
            }
            N.SetFocus(++_focusSwitches % 2 == 0 ? _replayField : _secondField);
            if (_focusSwitches == 100)
                FinishFocusReplay("completed");
            return 0;
        }
        if (message == N.FixtureSubmitCount)
            return (nint)_submits;
        if (message == N.FixtureBlockGui && _barrierEntered is not null && _barrierRelease is not null)
        {
            _barrierEntered.Set();
            // Non-alertable kernel wait: no sent messages may reenter the GUI
            // thread here. The bounded escape prevents a failed test hanging it.
            _barrierStatus = WaitForSingleObject(_barrierRelease.SafeWaitHandle.DangerousGetHandle(), 2_000) == 0 ? 1 : 2;
            return 0;
        }
        if (message == N.FixtureGuiDrained && _barrierDrained is not null)
        {
            _barrierDrained.Set();
            return 0;
        }
        if (message == N.FixtureBarrierStatus)
            return _barrierStatus;
        if (message == N.FixtureClearPassword)
        {
            // Only this fixture's own unmodified single-line Edit may leave the
            // synthetic password state. Production Broker never calls this path.
            nint field = (nint)wParam;
            if (!StockFields.Contains(field) || lParam != 0 || N.GetAncestor(field, 2) != _root
                || N.GetWindowThreadProcessId(field, out uint pid) == 0 || pid != Environment.ProcessId
                || (N.GetStyle(field, -16) & 0x4) != 0)
                return 0;
            try
            {
                if (N.Send(field, 0x00d2) != '*')
                    return 0;
                N.Send(field, 0x00cc, 0);
                return N.Send(field, 0x00d2) == 0 ? 1 : 0;
            }
            catch (N.NativeMessageException) { return 0; }
        }
        if (message == N.FixtureFocus && (PreviousProcedures.ContainsKey((nint)wParam) || StockFields.Contains((nint)wParam)))
        {
            N.SetForegroundWindow(hwnd);
            N.SetFocus((nint)wParam);
            return 0;
        }
        if (message == 0x0111 && (wParam & 0xffff) == 200 && ((wParam >> 16) & 0xffff) == 0)
        {
            _submits++;
            if (_replayStatus != 0)
                N.SendString(_replayStatus, N.WM_SETTEXT, "Submit count: " + _submits);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                operation = "submit-positive-control",
                atMs = WindowsInterruptClock.NowMs,
                submitCount = _submits
            }));
            Console.Out.Flush();
            return 0;
        }
        if (message == 0x0111 && (wParam & 0xffff) == 201 && ((wParam >> 16) & 0xffff) == 0)
        {
            KillTimer(_root, 2);
            KillTimer(_root, 3);
            _replayIndex = 0;
            _replayStarted = WindowsInterruptClock.NowMs;
            N.SendString(_replayField, N.WM_SETTEXT, "");
            N.SetFocus(_replayField);
            N.SendString(_replayStatus, N.WM_SETTEXT, "Replay running: committed text, not physical IME");
            if (SetTimer(_root, 2, 100, 0) == 0)
                throw new InvalidOperationException("Replay timer failed.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                operation = "replay-start",
                atMs = _replayStarted,
                kind = "committed-text-programmatic-not-physical-ime",
                total = ReplayText.Length
            }));
            return 0;
        }
        if (message == 0x0111 && (wParam & 0xffff) == 202 && ((wParam >> 16) & 0xffff) == 0)
        {
            KillTimer(_root, 2);
            KillTimer(_root, 3);
            _focusSwitches = 0;
            _focusStarted = WindowsInterruptClock.NowMs;
            N.SetFocus(_replayField);
            N.SendString(_replayStatus, N.WM_SETTEXT, "Focus replay running: no source mutations");
            if (SetTimer(_root, 3, 20, 0) == 0)
                throw new InvalidOperationException("Focus replay timer failed.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                operation = "focus-replay-start",
                atMs = _focusStarted,
                kind = "own-window-programmatic-focus-not-physical-keyboard",
                total = 100
            }));
            return 0;
        }
        return N.DefWindowProc(hwnd, message, wParam, lParam);
    }

    [SupportedOSPlatform("windows")]
    private static void FinishReplay(string status)
    {
        KillTimer(_root, 2);
        N.SendString(_replayStatus, N.WM_SETTEXT, "Replay: " + status + ", source " + _replayIndex
            + ", submit " + _submits);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            operation = "replay-finish",
            status,
            atMs = WindowsInterruptClock.NowMs,
            elapsedMs = WindowsInterruptClock.NowMs - _replayStarted,
            sourceLength = _replayIndex,
            submitCount = _submits
        }));
        Console.Out.Flush();
    }

    [SupportedOSPlatform("windows")]
    private static void FinishFocusReplay(string status)
    {
        KillTimer(_root, 3);
        N.SendString(_replayStatus, N.WM_SETTEXT, "Focus replay: " + status + ", changes " + _focusSwitches
            + ", submit " + _submits);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            operation = "focus-replay-finish",
            status,
            atMs = WindowsInterruptClock.NowMs,
            elapsedMs = WindowsInterruptClock.NowMs - _focusStarted,
            changes = _focusSwitches,
            finalField = GetFocus() == _replayField ? "a" : "other",
            submitCount = _submits
        }));
        Console.Out.Flush();
    }

    [SupportedOSPlatform("windows")]
    private static nint EditWindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0007)
            _lastFocusedField = hwnd;
        if (message == N.WM_GETTEXT)
        {
            long count = (long)N.GetProp(hwnd, N.ReadCountProperty);
            N.SetProp(hwnd, N.ReadCountProperty, (nint)(count + 1));
        }
        // This instrumentation belongs to our own process only. It is NOT a global IME observer.
        if (message == 0x010D)
            N.SetProp(hwnd, N.ImeProperty, (nint)2);
        nint result = N.CallWindowProc(PreviousProcedures[hwnd], hwnd, message, wParam, lParam);
        if (message == 0x010E)
            N.SetProp(hwnd, N.ImeProperty, (nint)1);
        if (message == 0x0082)
        {
            N.RemoveProp(hwnd, N.ImeProperty);
            N.RemoveProp(hwnd, N.LifetimeProperty);
            N.RemoveProp(hwnd, N.ReadCountProperty);
            PreviousProcedures.Remove(hwnd);
        }
        return result;
    }
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint hwnd, nuint id, uint interval, nint callback);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
