extern alias BrokerProduction;

using System.Collections.Concurrent;
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
    [MTAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            Console.Error.WriteLine("Native fixture requires Windows x64.");
            return 2;
        }
        if (args.Length == 1 && args[0] == "--source-broker")
            return BrokerProduction::TypeLay.Broker.Program.Run(sourcePrototype: true);
        if (args.Length == 3 && args[0] == "--broker-queue-probe" && Guid.TryParse(args[1], out _)
            && Path.IsPathFullyQualified(args[2]))
            return BrokerQueueProbe.Run(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length == 7 && args[0] == "--verify-hy-adapter")
            return HyAdapterCheck.Run(args[1..]);
        if (args.Length == 2 && args[0] == "--winevent-stack-probe" && Path.IsPathFullyQualified(args[1]))
            return WinEventStackProbe.Run(args[1]);
        if (args.Length == 2 && args[0] == "--source-consumption-order"
            && args[1] is "completion" or "deadline")
            return SourceKeyboardProbe.RunConsumptionOrder(args[1]);
        if (args.Length == 2 && args[0] == "--stock-controls" && args[1] == "--tsf-context-admission")
        {
            _tsfContextAdmission = true;
            return TsfContextAdmission.RunSta(RunTsfAdmissionWindows);
        }
        if (args.Length == 3 && args[0] == "--stock-controls" && args[1] == "--tsf-key-routing"
            && args[2] is "Tab" or "End")
        {
            _tsfContextAdmission = true;
            _tsfProbeKey = args[2] == "Tab" ? 9u : 0x23u;
            return TsfContextAdmission.RunSta(RunTsfAdmissionWindows);
        }
        if (args.Length == 2 && args[0] == "--stock-controls" && args[1] == "--tsf-source-key-read")
        {
            _tsfContextAdmission = true;
            _tsfProbeKey = 9;
            _tsfSourceKeyboardRead = true;
            return TsfContextAdmission.RunSta(RunTsfAdmissionWindows);
        }
        bool hookTest = args.Length == 1 && args[0] is "--hook-test" or "--hook-test-automation";
        bool ownedControls = (args.Length == 4 || args.Length == 5) && args[0] == "--owned-controls"
            && Path.IsPathFullyQualified(args[1]) && Guid.TryParse(args[2], out _ownedConnection)
            && Guid.TryParse(args[3], out _ownedRun)
            && (args.Length == 4 || Path.IsPathFullyQualified(args[4]));
        if (ownedControls)
        {
            _ownedPath = args[1];
            _retirementProbePath = args.Length == 5 ? args[4] : null;
        }
        bool returnFocusCase = args.Length == 3 && args[0] == "--stock-controls"
            && args[1] == "--return-focus" && args[2] is "root" or "second";
        if (returnFocusCase)
            _returnFocusCase = args[2];
        bool sourceClaimCase = args.Length == 3 && args[0] == "--stock-controls" && args[1] == "--source-claim-probe";
        if (sourceClaimCase)
            _sourceClaimProbe = args[2];
        _sourceKeyboardProbeEnabled = args.Length == 2 && args[0] == "--stock-controls" && args[1] == "--source-keyboard-probe";
        bool synchronousProbe = args.Length == 3 && args[0] == "--stock-controls"
            && args[1] == "--source-sync-probe" && Path.IsPathFullyQualified(args[2]);
        if (synchronousProbe)
        {
            _sourceKeyboardProbeEnabled = true;
            _sourceSynchronousPath = args[2];
        }
        if (args.Length == 3 && args[0] == "--stock-controls"
            && args[1] == "--source-message-probe" && Path.IsPathFullyQualified(args[2]))
        {
            _sourceKeyboardProbeEnabled = true;
            _sourceMessagePath = args[2];
        }
        bool stockControls = _sourceKeyboardProbeEnabled || ownedControls || returnFocusCase || sourceClaimCase || (args.Length == 1 && args[0] == "--stock-controls");
        if (args.Length != 0 && !hookTest && !stockControls)
            return 2;
        return RunWindows(hookTest, args.Contains("--hook-test-automation", StringComparer.Ordinal), stockControls);
    }

    private static string? _sourceSynchronousPath, _sourceMessagePath;
    private static bool _tsfContextAdmission;
    private static int _tsfFocusVersion, _tsfObservations;
    private static uint _tsfProbeKey;
    private static bool _tsfSourceKeyboardRead;
    private static TsfContextAdmission.KeyProbe? _tsfKeyProbe;
    private static bool _tsfKeyResultWritten, _tsfCloseRequested;
    private static bool _sourceKeyboardProbeEnabled;
    private static SourceKeyboardProbe? _sourceKeyboardProbe;
    private static string? _ownedPath;
    private static string? _retirementProbePath;
    private static string? _sourceClaimProbe;
    private static Guid _ownedConnection, _ownedRun;
    private static long _ownedSequence = 1;
    private static OwnedPracticeField? _ownedField;
    private static readonly ConcurrentQueue<PracticeMessage> OwnedControls = new();
    [SupportedOSPlatform("windows")]
    private static async Task ReadOwnedControls()
    {
        while (true)
        {
            var message = await PracticeProgressPipe.ReadAsync(Console.OpenStandardInput(), CancellationToken.None);
            OwnedControls.Enqueue(message);
            if (!N.PostMessage(_root, 0x8050, 0, 0))
                throw new InvalidOperationException("Owned fixture wake failed.");
        }
    }
    private static void ApplyOwnedControls()
    {
        while (OwnedControls.TryDequeue(out var command))
        {
            if (_ownedField is null || command.Connection != _ownedConnection || command.Sequence != _ownedSequence + 1)
                throw new InvalidDataException("Owned fixture control identity.");
            if (command.Kind == "retire" && command.Run == _ownedRun)
                _ownedField.Retire();
            else if (command.Kind == "begin" && command.Run != _ownedRun)
            {
                _ownedField.Begin();
                _ownedRun = command.Run;
            }
            else
                throw new InvalidDataException("Owned fixture control order.");
            _ownedSequence = command.Sequence;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            PracticeProgressPipe.WriteAsync(Console.OpenStandardOutput(), command with
            {
                Kind = command.Kind == "retire" ? "retired" : "started"
            }, deadline.Token).GetAwaiter().GetResult();
        }
    }

    [SupportedOSPlatform("windows")]
    private static int RunTsfAdmissionWindows()
    {
        try
        {
            return RunWindows(false, false, true);
        }
        finally
        {
            CloseTsfKeyProbe();
            // Covers startup failures as well as the message loop. The stock
            // controls must be destroyed before RunSta balances COM ownership.
            if (_root != 0 && N.IsWindow(_root))
            {
                KillTimer(_root, 4);
                if (!N.DestroyWindow(_root))
                    throw new InvalidOperationException("TSF fixture window cleanup failed.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CloseTsfKeyProbe()
    {
        if (_tsfKeyProbe is null || _tsfKeyResultWritten)
            return;
        bool clean = _tsfKeyProbe.Close();
        _tsfKeyResultWritten = true;
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "closed",
                result = _tsfKeyProbe.Result
            }));
            Console.Out.Flush();
        }
        finally
        {
            // Unadvise failure means the CCW may still be callable. Keep all
            // strong references and stop this owned process even if evidence
            // output fails; never unload COM beneath a potentially live sink.
            if (!clean)
                Environment.Exit(2);
        }
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
        using var sourceReentry = stockControls && _ownedPath is null && !_tsfContextAdmission
            ? new SourceInstallationReentry(_root, barrierEntered!, barrierRelease!) : null;
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
        if (_sourceKeyboardProbeEnabled)
            _sourceKeyboardProbe = new(_root, _replayField, _sourceSynchronousPath, _sourceMessagePath);
        if (_sourceClaimProbe is not null)
            WinEventStackProbe.LoadSourceClaim(_sourceClaimProbe);
        if (!hookTest)
        {
            N.CreateWindow(0, "Button", "Continuous input (100 ms / committed-text replay)", 0x50010000,
                350, 515, 480, 34, _root, (nint)201, instance, 0);
            N.CreateWindow(0, "Button", "Focus switching (20 ms / 100 changes)", 0x50010000,
                350, 560, 480, 34, _root, (nint)202, instance, 0);
            _replayStatus = N.CreateWindow(0, "Static", "Replay has not run", 0x50000000,
                30, 610, 800, 24, _root, 0, instance, 0);
        }
        if (_ownedPath is not null)
        {
            _ownedField = new OwnedPracticeField(_replayField, _ownedPath);
            _ownedField.Begin();
            if (_retirementProbePath is not null)
                WinEventStackProbe.LoadRetirement(_retirementProbePath, _ownedPath);
            _ = Task.Run(ReadOwnedControls);
        }
        N.ShowWindow(_root, 5);
        N.SetForegroundWindow(_root);
        N.SetFocus((nint)fields[0].Hwnd);
        if (_tsfContextAdmission && SetTimer(_root, 4, 25_000, 0) == 0)
            throw new InvalidOperationException("TSF admission deadline could not be installed.");
        var descriptor = new FixtureDescriptor(Environment.ProcessId, (long)_root, (long)_submit,
            Guid.NewGuid(), fields.ToArray());
        if (hookTest)
        {
            nint status = N.CreateWindow(0, "Static", "Hook starting", 0x50000000,
                350, 515, 480, 48, _root, 0, instance, 0);
            _hookHarness = new HookHarness(descriptor, status, automation);
            if (SetTimer(_root, 1, 20, 0) == 0)
                throw new InvalidOperationException("Hook fixture timer failed.");
        }
        _readyDescriptor = descriptor;
        // Publish readiness only after the GUI dispatches its queued startup work.
        // The regression holds that same queue using the existing stock-control barrier.
        if (stockControls && Environment.GetEnvironmentVariable("TYPELAY_FIXTURE_HOLD_READY") == "1"
            && !N.PostMessage(_root, N.FixtureBlockGui, 0, 0))
            throw new InvalidOperationException("Fixture readiness barrier could not be queued.");
        if (!N.PostMessage(_root, ReadyMessage, 0, 0))
            throw new InvalidOperationException("Fixture readiness could not be queued.");
        try
        {
            while (true)
            {
                _sourceKeyboardProbe?.BeforeRead();
                int status = N.GetMessage(out N.MessageStruct message, 0, 0, 0);
                _sourceKeyboardProbe?.MessageRetrievalReturned(status, message);
                if (status == 0)
                    return 0;
                if (status < 0)
                    return 1;
                // Freeze this retrieved mouse event before any dispatch/reentry.
                // Only our controlled stock fixture consumes this test marker.
                nint preparationClickMarker = message.Message == 0x0202 && StockFields.Contains(message.Hwnd)
                    ? GetMessageExtraInfo() : 0;
                if (preparationClickMarker != 0 && _preparationClickBarrierField == message.Hwnd)
                {
                    _preparationClickBarrierField = 0;
                    _barrierEntered!.Set();
                    _barrierStatus = WaitForSingleObject(_barrierRelease!.SafeWaitHandle.DangerousGetHandle(), 2_000) == 0 ? 1 : 2;
                }
                bool dispatchProbe = SourceDispatchProbe.BeforeDispatch(_root, message);
                ApplyOwnedControls(); // The same outer-loop retirement boundary as Practice.
                var dispatched = message;
                _sourceKeyboardProbe?.Dispatch(dispatched, after: false);
                if (!N.IsDialogMessage(_root, ref message))
                {
                    N.TranslateMessage(ref message);
                    N.DispatchMessage(ref message);
                }
                // Publish only after the actual field's mouse-up handling returns,
                // never after SendInput insertion or a separate posted/sent barrier.
                if (preparationClickMarker != 0
                    && (!N.SetProp(_root, N.FixtureClickField, dispatched.Hwnd)
                        || !N.SetProp(_root, N.FixtureClickReceipt, preparationClickMarker)))
                    throw new InvalidOperationException("Fixture preparation click receipt failed.");
                _tsfKeyProbe?.AfterDispatch(dispatched);
                if (_tsfCloseRequested)
                {
                    _tsfCloseRequested = false;
                    N.PostMessage(_root, 0x0010, 0, 0);
                }
                _sourceKeyboardProbe?.Dispatch(dispatched, after: true);
                sourceReentry?.ObserveDispatchedInput(dispatched);
                if (dispatchProbe)
                    SourceDispatchProbe.AfterDispatch(_root);
            }
        }
        finally
        {
            _sourceKeyboardProbe?.Dispose();
            _hookHarness?.Dispose();
        }

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

    private const uint ReadyMessage = 0x8054;
    private static FixtureDescriptor? _readyDescriptor;
    private static nint _root, _submit;
    private static string? _returnFocusCase;
    private static nint _replayField, _secondField, _replayStatus, _lastFocusedField;
    private static int _replayIndex, _focusSwitches;
    private static long _focusStarted;
    private static long _replayStarted;
    private const string ReplayText = "今天我们连续输入中文测试每个字都没有标点希望翻译在打字期间不断刷新并且不会抢走输入框的焦点";
    private static int _submits;
    private static HookHarness? _hookHarness;
    private static EventWaitHandle? _barrierEntered, _barrierRelease, _barrierDrained;
    private static int _barrierStatus;
    private static bool _blockNextNull;
    private static bool _changeAfterNull;
    private static nint _preparationClickBarrierField;
    private static readonly Dictionary<nint, nint> PreviousProcedures = new();
    private static readonly HashSet<nint> StockFields = new();
    // Keep delegates alive as long as the HWNDs exist.
    [SupportedOSPlatform("windows")] private static readonly N.WindowProc RootProc = RootWindowProc;
    [SupportedOSPlatform("windows")] private static readonly N.WindowProc EditProc = EditWindowProc;

    [SupportedOSPlatform("windows")]
    private static nint RootWindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (_tsfProbeKey != 0 && message == 0x0010) // WM_CLOSE, outside any TSF callback.
        {
            // Destruction waits until the TSF callback stack has returned.
            if (_tsfKeyProbe?.ComOperationActive == true)
            {
                _tsfCloseRequested = true;
                return 0;
            }
            CloseTsfKeyProbe();
        }
        if (message == 0x0002)
        {
            N.PostQuitMessage(0);
            return 0;
        }
        _sourceKeyboardProbe?.ObserveFocus(message, wParam, lParam);
        if (SourceDispatchProbe.Handle(message, wParam, lParam))
            return 0;
        if (SourceQueueWaitProbe.Handle(hwnd, message, wParam))
            return 0;
        // Stock Edit reports its actual focus through the normal parent
        // notification. Do not subclass it or synthesize IME/lifetime evidence.
        if (message == 0x0111 && ((wParam >> 16) & 0xffff) == 0x0100 && StockFields.Contains(lParam))
        {
            _lastFocusedField = lParam; // EN_SETFOCUS
        }
        if (_tsfContextAdmission && (message == 0x0111 && StockFields.Contains(lParam)
            && ((wParam >> 16) & 0xffff) is 0x0100 or 0x0200 // EN_SETFOCUS / EN_KILLFOCUS
            || message == 0x0006 && (wParam & 0xffff) == 0)) // Actual root deactivation.
        {
            _tsfFocusVersion++;
            _tsfKeyProbe?.FocusChanged();
        }
        if (_tsfProbeKey != 0 && message == TsfContextAdmission.BeginKeyMessage
            && _tsfKeyProbe is null && StockFields.Contains((nint)wParam) && lParam == 0)
        {
            _tsfKeyProbe = new(_root, (nint)wParam, _tsfProbeKey, () => _tsfFocusVersion, _tsfSourceKeyboardRead);
            _tsfKeyProbe.Begin();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "begun",
                result = _tsfKeyProbe.Result
            }));
            Console.Out.Flush();
            return 1;
        }
        if (_tsfProbeKey != 0 && message == TsfContextAdmission.KeyProgressMessage && _tsfKeyProbe is not null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "progress",
                inputUpObserved = _tsfKeyProbe.Result.InputUpObserved
            }));
            Console.Out.Flush();
            return 1;
        }
        if (_tsfContextAdmission && message == TsfContextAdmission.ObserveMessage
            && ++_tsfObservations <= 4 && StockFields.Contains((nint)wParam) && lParam == 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(TsfContextAdmission.Observe(
                _root, (nint)wParam, () => _tsfFocusVersion)));
            Console.Out.Flush();
            return 1;
        }
        if (_tsfContextAdmission && message == 0x0113 && wParam == 4)
        {
            KillTimer(_root, 4);
            if (_tsfProbeKey != 0)
                N.PostMessage(_root, 0x0010, 0, 0);
            else
                N.DestroyWindow(_root);
            return 0;
        }
        if (message == 0x0007 && _lastFocusedField != 0 && N.IsWindow(_lastFocusedField))
        {
            // Controlled host policies exercise activation that does not restore
            // the original field. Glossary return must not correct either;
            // ordinary explicit return may correct field focus.
            if (_returnFocusCase != "root")
                N.SetFocus(_returnFocusCase == "second" ? _secondField : _lastFocusedField);
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
        if (message == ReadyMessage && _readyDescriptor is { } descriptor)
        {
            _readyDescriptor = null;
            Console.WriteLine(JsonSerializer.Serialize(descriptor));
            Console.Out.Flush(); // Metadata only; never output text or IME preedit content.
            return 0;
        }
        if (message == N.FixtureSubmitCount)
            return (nint)_submits;
        if (message == WinEventStackProbe.ArmRetirementMessage && _retirementProbePath is not null)
            return (nint)WinEventStackProbe.Arm(_root, _replayField, wParam, lParam);
        if (message == WinEventStackProbe.FinishRetirementMessage && _retirementProbePath is not null)
            return (nint)WinEventStackProbe.Finish();
        if (message == 0x8065 && _sourceClaimProbe is not null)
            return (nint)WinEventStackProbe.ArmSourceClaim(_root, _replayField, (ulong)wParam, (uint)lParam);
        if (message == 0x8066 && _sourceClaimProbe is not null)
            return (nint)WinEventStackProbe.FinishSourceClaim(_root);
        if (message == N.FixtureArmNullBarrier && _barrierEntered is not null && _barrierRelease is not null)
        {
            _barrierEntered.Reset();
            _barrierRelease.Reset();
            _barrierStatus = 0;
            _blockNextNull = true;
            _changeAfterNull = wParam == 1;
            return 1;
        }
        if (message == N.FixtureArmClickBarrier && _barrierEntered is not null && _barrierRelease is not null
            && StockFields.Contains((nint)wParam) && lParam == 0)
        {
            _barrierEntered.Reset();
            _barrierRelease.Reset();
            _barrierStatus = 0;
            _preparationClickBarrierField = (nint)wParam;
            return 1;
        }
        if ((message == N.FixtureBlockGui || (message == 0 && _blockNextNull))
            && _barrierEntered is not null && _barrierRelease is not null)
        {
            _blockNextNull = false;
            _barrierEntered.Set();
            // Non-alertable kernel wait: no sent messages may reenter the GUI
            // thread here. The bounded escape prevents a failed test hanging it.
            _barrierStatus = WaitForSingleObject(_barrierRelease.SafeWaitHandle.DangerousGetHandle(), 2_000) == 0 ? 1 : 2;
            if (_changeAfterNull)
            {
                _changeAfterNull = false;
                N.SendString(_replayField, N.WM_SETTEXT, "Changed while form owns focus");
            }
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
        if (_sourceKeyboardProbe is { } keyboardProbe && message == SourceKeyboardProbe.Arm)
        {
            keyboardProbe.Configure((int)wParam);
            return 1;
        }
        if (_sourceKeyboardProbe is { } closingProbe && message == SourceKeyboardProbe.Close)
        {
            try
            {
                closingProbe.Dispose();
                _sourceKeyboardProbe = null;
                return 1;
            }
            catch { return 0; }
        }
        if (_sourceKeyboardProbe is { } snapshotProbe && message == SourceKeyboardProbe.Snapshot)
        {
            Console.WriteLine(JsonSerializer.Serialize(snapshotProbe.Read()));
            Console.Out.Flush();
            return 1;
        }
        if (message == N.FixtureAllowBrokerForeground)
        {
            // The owned foreground stand-in performs the same targeted handoff
            // as the real App before its Broker return. Never activate here.
            return StockFields.Count != 0 && hwnd == _root && N.GetForegroundWindow() == _root
                && wParam > 0 && wParam < uint.MaxValue && lParam == 0
                && AllowSetForegroundWindow((uint)wParam) ? 1 : 0;
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
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
