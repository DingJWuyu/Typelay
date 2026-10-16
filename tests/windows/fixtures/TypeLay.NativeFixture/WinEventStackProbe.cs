using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using N = TypeLay.Platform.Windows.NativeMethods;

[SupportedOSPlatform("windows")]
internal static class WinEventStackProbe
{
    public const uint ArmRetirementMessage = 0x8052, FinishRetirementMessage = 0x8053;
    [StructLayout(LayoutKind.Sequential)]
    private struct RetirementObservation
    {
        public uint HooksInstalled, HooksRemoved, TransactionCalls, MatchingCallbacks, RetireCalls;
        public int RetireResult, InitialStart, InitialEnd, CallbackStart, CallbackEnd;
        public uint MappingUnmapped, MappingClosed;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ArmRetirement(nint root, nint edit, ulong first, ulong second, nint control);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint FinishRetirement(ref RetirementObservation observation);
    private static ArmRetirement? _armRetirement;
    private static FinishRetirement? _finishRetirement;
    private static nint _ownedControl;
    private static uint _armStatus;

    public static void LoadRetirement(string testLibraryPath, string productionLibraryPath)
    {
        // OwnedPracticeField has already loaded and pinned this exact production
        // DLL. Do not load a second instance with independent owned state.
        nint production = N.GetModuleHandle(productionLibraryPath);
        if (production == 0)
            throw new InvalidOperationException("retirement-production-module");
        _ownedControl = NativeLibrary.GetExport(production, "TypeLayOwnedEditControl");
        // Keep callback code loaded until fixture process exit, even on cleanup failure.
        nint probe = NativeLibrary.Load(testLibraryPath);
        _armRetirement = Marshal.GetDelegateForFunctionPointer<ArmRetirement>(
            NativeLibrary.GetExport(probe, "ArmOwnedRetirement"));
        _finishRetirement = Marshal.GetDelegateForFunctionPointer<FinishRetirement>(
            NativeLibrary.GetExport(probe, "FinishOwnedRetirement"));
    }

    public static uint Arm(nint root, nint edit, nuint first, nint second)
    {
        if (_armRetirement is null)
            return 1;
        _armStatus = _armRetirement(root, edit, first, unchecked((ulong)second), _ownedControl);
        return _armStatus;
    }

    public static uint Finish()
    {
        if (_finishRetirement is null)
            return 1;
        var observation = new RetirementObservation();
        uint cleanupStatus = _finishRetirement(ref observation);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            armStatus = _armStatus,
            cleanupStatus,
            hooksInstalled = observation.HooksInstalled,
            hooksRemoved = observation.HooksRemoved,
            transactionCalls = observation.TransactionCalls,
            matchingCallbacks = observation.MatchingCallbacks,
            retireCalls = observation.RetireCalls,
            retireResult = observation.RetireResult,
            initialSelection = new[] { observation.InitialStart, observation.InitialEnd },
            callbackSelection = new[] { observation.CallbackStart, observation.CallbackEnd },
            mappingUnmapped = observation.MappingUnmapped,
            mappingClosed = observation.MappingClosed
        }));
        Console.Out.Flush();
        return cleanupStatus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Observation
    {
        public uint HooksInstalled, HooksRemoved, SetSelCalls;
        public int InitialStart, InitialEnd, FinalStart, FinalEnd;
        public uint CaretLocationEvents, CaretLocationInside, TextSelectionEvents, TextSelectionInside;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Observe(nint root, nint edit, ref Observation observation);
    private static readonly N.WindowProc RootProc = WindowProc;
    private const uint RunProbe = 0x8051;

    public static int Run(string libraryPath)
    {
        nint root = 0;
        string stage = "load-test-dll";
        uint nativeStatus = 0;
        bool completed = false;
        var observation = new Observation();
        try
        {
            // The process owns this one-shot DLL for its lifetime. In particular,
            // never unload callback code if UnhookWinEvent reported a failure.
            nint module = NativeLibrary.Load(libraryPath);
            var observe = Marshal.GetDelegateForFunctionPointer<Observe>(
                NativeLibrary.GetExport(module, "ObserveStockEditSelection"));
            stage = "create-fixture";
            nint instance = N.GetModuleHandle(null);
            var definition = new N.WindowClass
            {
                Procedure = RootProc,
                ClassName = "TypeLayWinEventStackProbe",
                Instance = instance,
                Background = (nint)6
            };
            if (N.RegisterClass(ref definition) == 0)
                throw new InvalidOperationException();
            root = N.CreateWindow(0, definition.ClassName, "TypeLay WinEvent - TEST TEXT ONLY",
                0x00CF0000, 180, 100, 600, 180, 0, 0, instance, 0);
            if (root == 0)
                throw new InvalidOperationException();
            nint edit = N.CreateWindow(0x200, "Edit", "pre old post", 0x50010080,
                30, 30, 500, 32, root, (nint)101, instance, 0);
            if (edit == 0)
                throw new InvalidOperationException();
            stage = "initialize-caret";
            N.ShowWindow(root, 5);
            N.SetForegroundWindow(root);
            N.SetFocus(edit);
            // Ordinary collapsed caret, before either hook is installed. The
            // observed 4..7 range changes both selection and the active end.
            N.Send(edit, N.EM_SETSEL, 0, 0);
            if (!UpdateWindow(root) || !UpdateWindow(edit))
                throw new InvalidOperationException();
            if (!N.PostMessage(root, RunProbe, 0, 0))
                throw new InvalidOperationException();
            stage = "message-loop";
            while (true)
            {
                int result = N.GetMessage(out N.MessageStruct message, 0, 0, 0);
                if (result <= 0)
                    throw new InvalidOperationException();
                if (message.Hwnd == root && message.Message == RunProbe)
                {
                    stage = "native-observation";
                    nativeStatus = observe(root, edit, ref observation);
                    completed = nativeStatus == 0;
                    break;
                }
                N.TranslateMessage(ref message);
                N.DispatchMessage(ref message);
            }
        }
        catch (Exception)
        {
            // Fixed stage only; no raw exception, paths, handles or body text.
        }
        finally
        {
            if (root != 0 && !N.DestroyWindow(root))
            {
                stage = "destroy-fixture";
                completed = false;
            }
        }
        bool observed = observation.CaretLocationInside > 0 || observation.TextSelectionInside > 0;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            result = completed ? observed ? "observed" : "not-observed" : "error",
            stage = completed ? "completed" : stage,
            nativeStatus,
            hooksInstalled = observation.HooksInstalled,
            hooksRemoved = observation.HooksRemoved,
            setSelCalls = observation.SetSelCalls,
            initialSelection = new[] { observation.InitialStart, observation.InitialEnd },
            finalSelection = new[] { observation.FinalStart, observation.FinalEnd },
            caretLocationEvents = observation.CaretLocationEvents,
            caretLocationInside = observation.CaretLocationInside,
            textSelectionEvents = observation.TextSelectionEvents,
            textSelectionInside = observation.TextSelectionInside
        }));
        return completed ? 0 : 1;
    }

    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam) =>
        N.DefWindowProc(hwnd, message, wParam, lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(nint hwnd);
}
