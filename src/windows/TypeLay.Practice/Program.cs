using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.Practice;

internal static partial class Program
{
    private const string WindowClassName = "TypeLayPracticeWindow";
    private const string SettingsUnavailable = "无法读取当前设置。请回到 TypeLay 设置检查后再练习。";
    private const int SettingsLimit = 512 * 1024;
    private const uint RootStyle = 0x00CF0000 | 0x00200000 | 0x02000000; // Overlapped, vertical scroll, clip children.
    private const uint ChildStyle = 0x50000000;
    private const int EditId = 101, RestartId = 102, ProgressId = 103, PreviewId = 104, ReplaceId = 105, CloseId = 2;
    private const uint ProgressMessage = 0x8001;
    private static readonly Native.WindowProc Procedure = WindowProc;
    private static readonly List<Control> Controls = [];
    private static nint _window, _instance, _bodyFont, _titleFont, _backgroundBrush;
    private static Control? _title, _summary, _capability, _notice, _inputLabel, _edit, _preview, _replace, _progressNotice, _restart, _close;
    private static OnDemandPracticeProgress? _progress;
    private static readonly ConcurrentQueue<PracticeMessage> PendingControls = new();
    private static int _pendingCount, _disconnected;
    private static OwnedPracticeField? _owned;
    private static Guid _connection, _run;
    private static OwnedMapping? _witnessMapping, _resultMapping;
    private static OwnedWitnessReader? _witness;
    private static OwnedAcceptancePlan? _plan;
    private static OwnedEditorControl? _editorFence;
    private static EditorHandoff _editorHandoff;
    private static bool _editorAppLeft;
    private static uint _editorOverlayThread, _editorOverlayProcess;
    private static long _editorEventSequence;
    private static string? _editorNotice;
    private enum SourceReturnState
    {
        None, Armed, RootActive, FieldFocused, Returned, Invalid
    }
    private static SourceReturnState _returnState;
    private static OwnedReturnControl? _returnControl;
    private static OwnedReturnMouse? _mouseReturnNotice;
    private static OwnedReturnKind _returnKind;
    private static bool _returnAppActive, _returnResumed;
    private static long _returnBaseline, _returnExpiresAt, _returnFocusRevision;
    private static string? _returnNotice;
    private static (OwnedReturnControl Control, OwnedCaptureDescriptor Descriptor)? _returnedSeal;
    private static readonly string? EditorTracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private static readonly List<EditorTrace> EditorTraces = [];
    private static int _editorTraceCount;
    private enum EditorEvent
    {
        Fenced, FenceRejected, App, Root, FocusGained, FocusLost, Left, Invalid,
        ReturnMouse, ReturnFenced, ReturnApp, ReturnRoot, ReturnFocus, Returned, ReturnInvalid
    }
    private enum EditorReason
    {
        None, Fence, App, Root, Field, Deadline, Cancel, Retire, Close, Restart
    }
    private enum EditorPeer
    {
        None, Overlay, OverlayThread, Other
    }
    private sealed record EditorTrace(long AtMs, int Order, Guid Intent, long SourceSequence,
        EditorHandoff State, EditorEvent Event, EditorReason Reason, EditorPeer Peer, bool? Active);
    private enum EditorHandoff
    {
        None, Armed, RootLeft, FieldLeft, Left, Invalid
    }
    private static AutomaticPracticeProgress? _automaticProgress;
    private static byte[]? _transaction;
    private static long _lastPlanSequence, _installWitnessFloor, _lastWitnessAcknowledged, _sourceReadyRevision;
    private static bool _automatic, _allowInjectedInput, _planActive, _planReported, _witnessFailed;
    private static bool _observeInputCompletion;
    private static long _inputCompletionSequence;
    private static Queue<PracticeMessage>? _inputCompletions;
    private static bool _tabHeld, _tabConsumed, _tabExtended;
    private static uint _tabScanCode;
    private static readonly long ProcessStartUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    private static long _sequence;
    private static bool _retired = true, _closePending, _sendRestart;
    private static bool _progressConnected, _restartPending;
    private static string _settingsPath = "";
    private static string _theme = "system";
    private static uint _backgroundColor, _textColor;
    private static int _dpi = 96, _scroll, _contentHeight, _exitCode;
    private static bool _layingOut, _themeInitialized, _systemColors, _dark;

    [STAThread]
    private static int Main(string[] args)
    {
        // This process owns only an ordinary source field. The running App owns
        // the production shortcuts, model, Broker, and this child process.
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || args.Length is not (2 or 4) || args[0] != "--settings" || !Path.IsPathFullyQualified(args[1])
            || (args.Length == 4 && (args[2] != "--progress" || !Guid.TryParseExact(args[3], "N", out _connection) || _connection == Guid.Empty)))
            return Fail();
        _settingsPath = args[1];
        if (!TryReadSettings(out var settings))
            return Fail();
        var presentation = OnDemandPracticePresentation.Create(settings!);
        _theme = settings!.Theme;
        bool registered = false;
        try
        {
            _instance = Native.GetModuleHandleW(null);
            var windowClass = new Native.WindowClass
            {
                Procedure = Procedure,
                Instance = _instance,
                Cursor = Native.LoadCursorW(0, (nint)32512),
                Background = (nint)6,
                ClassName = WindowClassName
            };
            if (_instance == 0 || Native.RegisterClassW(ref windowClass) == 0)
                return Fail();
            registered = true;
            _window = Native.CreateWindowExW(0, WindowClassName, presentation.Title, RootStyle,
                int.MinValue, int.MinValue, 800, 720, 0, 0, _instance, 0);
            if (_window == 0 || _exitCode != 0)
                return Fail();
            _dpi = (int)Native.GetDpiForWindow(_window);
            if (_dpi == 0)
                return Fail();

            _title = Add("Static", presentation.Title);
            _summary = Add("Static", presentation.Summary);
            _notice = Add("Static", presentation.CurrentStateNotice);
            // A stock STATIC immediately precedes its stock Edit in the child
            // order, giving the system accessibility provider a field label.
            _inputLabel = Add("Static", presentation.InputLabel);
            _edit = Add("Edit", presentation.SampleText,
                0x00010000 | 0x00200000 | 0x0004 | 0x0040 | 0x1000, EditId, 0x0200);
            if (_connection != Guid.Empty)
                _owned = new OwnedPracticeField(_edit.Handle, Path.Combine(AppContext.BaseDirectory, "TypeLay.NativeInput.dll"));
            _preview = Add("Static", presentation.PreviewStep, id: PreviewId);
            _replace = Add("Static", presentation.ReplaceStep, id: ReplaceId);
            _progressNotice = Add("Static", presentation.ProgressNotice, id: ProgressId);
            _capability = Add("Static", presentation.CapabilityNote);
            _restart = Add("Button", presentation.RestartLabel, 0x00010000 | 0x4000, RestartId);
            Native.EnableWindow(_restart.Handle, false);
            InitializeCompletionControl();
            _close = Add("Button", presentation.CloseLabel, 0x00010000 | 0x4000, CloseId);
            RefreshTheme();
            SetFonts();
            SizeInitialWindow();
            Layout();
            if (Native.SetTimer(_window, 1, 1000, 0) == 0)
                return Fail();

            if (_connection != Guid.Empty)
                _ = Task.Run(ReadProgressAsync);
            Native.ShowWindow(_window, 1);
            int inputBurst = 0;
            while (true)
            {
                ApplyControls();
                // The queue class, rather than WM_KEYDOWN's numeric ID, is the
                // provenance. Posted/fallback messages can never authorize a write.
                bool inputQueue = inputBurst < 8;
                bool available = Native.PeekMessageW(out var message, 0, 0, 0,
                    inputQueue ? 0x04070001u : 0x00f80001u); // PM_REMOVE | PM_QS_INPUT.
                ApplyControls(); // PeekMessage may synchronously dispatch sent messages.
                if (!available && inputQueue)
                {
                    inputQueue = false;
                    available = Native.PeekMessageW(out message, 0, 0, 0, 0x00f80001u);
                    ApplyControls();
                }
                if (!available)
                {
                    // TranslateMessage may enqueue WM_CHAR. The queue fixture
                    // observes completion only after those messages also drain.
                    // A fairness-only posted-message turn has not checked the
                    // input queue; let its existing wake/reset run first.
                    if (inputBurst < 8)
                        while (_inputCompletions?.TryDequeue(out var completion) == true)
                            WriteReply(completion);
                    inputBurst = 0;
                    uint wait = _returnState is SourceReturnState.Armed or SourceReturnState.RootActive or SourceReturnState.FieldFocused
                        ? (uint)Math.Clamp(_returnExpiresAt - WindowsInterruptClock.NowMs, 0, 500)
                        : _editorFence is { } pendingEditor && _editorHandoff is EditorHandoff.Armed or EditorHandoff.RootLeft or EditorHandoff.FieldLeft
                        ? (uint)Math.Clamp(pendingEditor.ExpiresAtMs - WindowsInterruptClock.NowMs, 0, 500)
                        : uint.MaxValue;
                    if (Native.MsgWaitForMultipleObjectsEx(0, 0, wait, 0x04ff, 4) == uint.MaxValue)
                        return Fail();
                    ApplyControls();
                    continue;
                }
                inputBurst = inputQueue ? inputBurst + 1 : 0;
                if (message.Id == 0x0012) // WM_QUIT.
                    return _exitCode;
                if (_window == 0)
                    continue;
                bool consumed = ProcessInput(ref message, inputQueue);
                if (!consumed && !Native.IsDialogMessageW(_window, ref message))
                {
                    Native.TranslateMessage(ref message);
                    Native.DispatchMessageW(ref message);
                }
                // Only the queue fixture opts in. Ordinary production and real
                // model sessions emit nothing; no input authority is changed.
                // The original MSG has completed its decision and, if passed,
                // its normal dispatch before this content-free notification.
                if (_observeInputCompletion && inputQueue && message.WParam == 9
                    && message.Id is 0x0100 or 0x0101)
                {
                    if (_inputCompletions!.Count >= 8)
                        throw new InvalidDataException("practice-input-completion-limit");
                    _inputCompletions.Enqueue(new("test-tab-dispatched", _connection, _run, _sequence,
                        InputCompletion: new(checked(++_inputCompletionSequence), message.Id == 0x0100, consumed)));
                }
                if (consumed)
                    continue;
                ApplyControls();
            }
        }
        catch
        {
            // Native and JSON errors can include paths or user content. Only
            // a fixed error token may reach the parent's redirected stderr.
            return Fail();
        }
        finally
        {
            ReleaseAcceptance();
            if (_window != 0)
                Native.DestroyWindow(_window);
            if (_bodyFont != 0)
                Native.DeleteObject(_bodyFont);
            if (_titleFont != 0)
                Native.DeleteObject(_titleFont);
            if (_backgroundBrush != 0)
                Native.DeleteObject(_backgroundBrush);
            if (registered)
                Native.UnregisterClassW(WindowClassName, _instance);
        }
    }

    private static int Fail()
    {
        try
        {
            Console.Error.WriteLine("practice-unavailable");
        }
        catch (IOException) { }
        return 1;
    }

    private static bool TryReadSettings(out UserSettings? settings)
    {
        settings = null;
        try
        {
            // Do not construct SettingsStore here: it creates directories and
            // substitutes defaults on failure. A lesson must not imply those
            // defaults are the running App's actual settings.
            using var stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > SettingsLimit)
                return false;
            var bytes = new byte[SettingsLimit + 1];
            int count = 0, read;
            while (count < bytes.Length && (read = stream.Read(bytes.AsSpan(count))) != 0)
                count += read;
            if (count > SettingsLimit)
                return false;
            int start = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            settings = JsonSerializer.Deserialize<UserSettings>(bytes.AsSpan(start, count - start));
            return settings is not null && settings.Schema == 1
                && settings.Source is "auto" or "zh" or "en" && settings.Target is "zh" or "en"
                && settings.Theme is "system" or "light" or "dark" && settings.Excluded.Length <= 256
                && Enum.IsDefined(settings.Mode)
                && TranslationShortcut.TryParse(settings.WakeShortcut, out var wake)
                && TranslationShortcut.TryParse(settings.DirectShortcut, out var direct) && wake != direct;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static Control Add(string className, string text, uint style = 0, int id = 0, uint extendedStyle = 0)
    {
        // STATIC must display literal ampersands, including customized chords.
        if (className == "Static")
            style |= 0x0080; // SS_NOPREFIX
        nint handle = Native.CreateWindowExW(extendedStyle, className, text, ChildStyle | style,
            0, 0, 1, 1, _window, (nint)id, _instance, 0);
        if (handle == 0)
            throw new InvalidOperationException();
        var control = new Control(handle, text);
        Controls.Add(control);
        return control;
    }

    private static void RefreshSettings()
    {
        if (_notice is null || _preview is null || _replace is null || _progressNotice is null)
            return;
        bool changed;
        if (!TryReadSettings(out var settings))
        {
            changed = SetText(_notice, SettingsUnavailable)
                | SetText(_preview, "") | SetText(_replace, "") | SetText(_progressNotice, "");
        }
        else
        {
            _theme = settings!.Theme;
            if (_automatic)
            {
                var view = AutomaticPracticePresentation.Create(settings!, _automaticProgress);
                string progress = _automaticProgress?.Completed == true ? view.ProgressNotice
                    : _automaticProgress?.AcceptanceCompleted == true
                    ? "第一次接受已验证"
                    : _automaticProgress?.PreviewCompleted == true
                        ? "已生成预览。"
                        : _automaticProgress?.Target is not null ? "已识别练习输入框，等待自动生成预览。"
                        : "请点击练习输入框，等待原生连接确认。";
                changed = SetText(_title!, "自动接受练习")
                    | SetText(_summary!, "输入一句话，查看译文后按 Tab 接受。")
                    | SetText(_notice, view.CurrentStateNotice) | SetText(_preview, "自动预览")
                    | SetText(_replace, "Tab 接受")
                    | SetText(_capability!, "编辑练习尚未开放")
                    | SetText(_progressNotice, CompletionProgressNotice(progress));
                Native.SetWindowTextW(_window, "自动接受练习");
            }
            else
            {
                var view = OnDemandPracticePresentation.Create(settings!, _progress);
                changed = SetText(_title!, view.Title) | SetText(_summary!, view.Summary)
                    | SetText(_capability!, view.CapabilityNote) | SetText(_notice, view.CurrentStateNotice)
                    | SetText(_preview, view.PreviewStep) | SetText(_replace, view.ReplaceStep)
                    | SetText(_progressNotice, view.ProgressNotice);
                Native.SetWindowTextW(_window, view.Title);
            }
        }
        // The source field is intentionally never read or reset by refresh.
        if (changed)
            Layout();
        RefreshTheme();
    }

    private static async Task ReadProgressAsync()
    {
        try
        {
            var input = Console.OpenStandardInput();
            while (true)
            {
                var message = await PracticeProgressPipe.ReadAsync(input, CancellationToken.None);
                if (Interlocked.Increment(ref _pendingCount) > 16)
                    throw new InvalidDataException("practice-control-overflow");
                PendingControls.Enqueue(message);
                if (!Native.PostMessageW(_window, ProgressMessage, 0, 0))
                    throw new IOException("practice-control-wake");
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
        {
            Interlocked.Exchange(ref _disconnected, 1);
            Native.PostMessageW(_window, ProgressMessage, 0, 0);
        }
    }

    private static void ApplyControls()
    {
        if (_window == 0)
            return;
        if (_owned is null)
        {
            if (_closePending)
                Native.DestroyWindow(_window);
            return;
        }
        bool changed = false;
        try
        {
            // Closing always wins over queued Begin/Install. Retirement and its
            // ACK happen at this outer boundary, never from the pipe reader.
            if (_closePending)
            {
                ResetCompletion();
                InvalidateEditorHandoff(EditorReason.Close);
                _owned.Retire();
                _retired = true;
                ReleaseAcceptance();
                if (_progressConnected && _sequence != 0)
                    Reply("closed");
                Native.DestroyWindow(_window);
                return;
            }
            if (Volatile.Read(ref _disconnected) == 1)
                throw new IOException("practice-control-disconnected");
            if (Volatile.Read(ref _disconnected) == 2)
                return;
            // The reader bounds queued controls to 16. Snapshot the bound so a
            // continuing producer cannot starve input or closing indefinitely.
            int count = Math.Min(16, Volatile.Read(ref _pendingCount));
            for (int index = 0; index < count && PendingControls.TryDequeue(out var message); index++)
            {
                Interlocked.Decrement(ref _pendingCount);
                if (Volatile.Read(ref _disconnected) != 0 || message.Connection != _connection)
                    throw new InvalidDataException("practice-control-connection");
                changed = true;
                if (HandleCompletionControl(message))
                    continue;
                if (message.Kind is "begin" or "begin-auto" && _retired
                    && message.Run != _run && message.Sequence == _sequence + 1 && _plan is null)
                {
                    ResetCompletion();
                    ReleaseAcceptance();
                    _owned.Begin(message.Kind == "begin-auto");
                    _run = message.Run;
                    _sequence = message.Sequence;
                    _retired = false;
                    _progressConnected = true;
                    _restartPending = false;
                    _progress = null;
                    _automaticProgress = null;
                    _automatic = message.Kind == "begin-auto";
                    _installWitnessFloor = _lastWitnessAcknowledged = _sourceReadyRevision = 0;
                    _witnessFailed = false;
                    _allowInjectedInput = false;
                    _observeInputCompletion = message.Acceptance?.ObserveInputCompletion == true;
                    _inputCompletionSequence = 0;
                    _inputCompletions = _observeInputCompletion ? new() : null;
                    if (_automatic)
                    {
                        var acceptance = message.Acceptance ?? throw new InvalidDataException("practice-witness-missing");
                        if (acceptance.WitnessBytes != OwnedPracticeAcceptanceProtocol.WitnessBytes)
                            throw new InvalidDataException("practice-witness-size");
                        _witnessMapping = OwnedMapping.Open(acceptance.WitnessHandle, acceptance.WitnessBytes, false);
                        _witness = new(_witnessMapping);
                        _allowInjectedInput = acceptance.AllowInjectedInput;
                    }
                    Reply("started", _automatic ? new(Descriptor: Descriptor()) : null);
                }
                else if (message.Kind == "retire" && message.Run == _run && message.Sequence == _sequence + 1)
                {
                    PrepareCompletionRetirement(message);
                    InvalidateEditorHandoff(EditorReason.Retire);
                    _owned.Retire();
                    _retired = true;
                    // Retirement is the terminal ACK for this generation. Do
                    // not emit its queued editor notice under the next sequence.
                    ClearEditorHandoff();
                    _sequence = message.Sequence;
                    _planActive = false;
                    if (_transaction is not null)
                        CryptographicOperations.ZeroMemory(_transaction);
                    _transaction = null;
                    CloseWitness();
                    // Keep the result until exact release-plan ACK. Only the
                    // prior press decision survives; no authority or old mapping does.
                    _progress = null;
                    _automaticProgress = null;
                    Reply("retired");
                }
                else if (message.Run == _run && message.Sequence == _sequence
                    && (!_retired || message.Kind is "revoke-plan" or "release-plan"))
                {
                    switch (message.Kind)
                    {
                        case "progress" when !_automatic:
                            _progress = message.Progress;
                            break;
                        case "auto-progress" when _automatic:
                            _automaticProgress = message.AutomaticProgress;
                            break;
                        case "edit-fence" when _automatic:
                            FenceEditor(message.Editing ?? throw new InvalidDataException("practice-editor-missing"));
                            break;
                        case "edit-cancel" when _automatic:
                            if (_editorFence is null || message.Editing != _editorFence)
                                throw new InvalidDataException("practice-editor-order");
                            InvalidateEditorHandoff(EditorReason.Cancel);
                            break;
                        case "return-fence" when _automatic:
                            FenceSourceReturn(message.Returning ?? throw new InvalidDataException("practice-return-missing"),
                                message.Acceptance ?? throw new InvalidDataException("practice-return-witness"));
                            break;
                        case "return-resume" when _automatic:
                            ResumeSourceReturn(message.Returning ?? throw new InvalidDataException("practice-return-missing"));
                            break;
                        case "return-cancel" when _automatic:
                            if (message.Returning is not { } cancelledReturn)
                                throw new InvalidDataException("practice-return-order");
                            if (_returnControl is null && _returnedSeal is { } returnedSeal && cancelledReturn == returnedSeal.Control)
                            {
                                // Return is only a focus result. Until fresh Core
                                // validation/rebinding finishes, cancellation still
                                // permanently fences this exact replacement reader.
                                _returnControl = cancelledReturn;
                                _returnState = SourceReturnState.Returned;
                                _returnedSeal = null;
                                _witnessFailed = true;
                                CloseWitness();
                            }
                            if (_returnControl != cancelledReturn)
                                throw new InvalidDataException("practice-return-order");
                            InvalidateSourceReturn(EditorReason.Cancel);
                            break;
                        case "install" when _automatic:
                            InstallPlan(message.Acceptance ?? throw new InvalidDataException("practice-install-missing"));
                            break;
                        case "revoke-plan" when _automatic:
                            RequirePlan(message.Acceptance);
                            _planActive = false;
                            Reply("revoked-plan", PlanControl());
                            break;
                        case "release-plan" when _automatic:
                            RequirePlan(message.Acceptance);
                            var released = PlanControl();
                            ReleasePlan();
                            Reply("released-plan", released);
                            break;
                        default:
                            throw new InvalidDataException("practice-control-order");
                    }
                }
                else
                    throw new InvalidDataException("practice-control-order");
            }
            if (Volatile.Read(ref _disconnected) != 0)
                throw new IOException("practice-control-disconnected");
            if (_sendRestart)
            {
                _sendRestart = false;
                _planActive = false;
                Reply("restart");
            }
            if (_editorFence is { } editor && _editorHandoff is EditorHandoff.Armed or EditorHandoff.RootLeft or EditorHandoff.FieldLeft
                && WindowsInterruptClock.NowMs >= editor.ExpiresAtMs)
                InvalidateEditorHandoff(EditorReason.Deadline);
            if (_editorNotice is { } editorNotice && _editorFence is { } fenced)
            {
                _editorNotice = null;
                WriteReply(new(editorNotice, _connection, _run, _sequence,
                    Editing: fenced with
                    {
                        SourceEventSequence = _editorEventSequence
                    }));
            }
            if (_mouseReturnNotice is { } mouseReturn)
            {
                _mouseReturnNotice = null;
                WriteReply(new("mouse-intent", _connection, _run, _sequence, MouseReturn: mouseReturn));
            }
            if (_returnState is SourceReturnState.Armed or SourceReturnState.RootActive or SourceReturnState.FieldFocused
                && WindowsInterruptClock.NowMs >= _returnExpiresAt)
                InvalidateSourceReturn(EditorReason.Deadline);
            CompleteSourceReturn();
            if (_returnNotice is { } returnNotice && _returnControl is { } returned)
            {
                _returnNotice = null;
                var descriptor = returnNotice == "source-returned" ? Descriptor() : null;
                WriteReply(new(returnNotice, _connection, _run, _sequence, Returning: returned with
                {
                    SourceEventSequence = _editorEventSequence,
                    Descriptor = descriptor
                }));
                if (descriptor is not null)
                {
                    ClearEditorHandoff();
                    _returnedSeal = (returned, descriptor);
                    _sourceReadyRevision = descriptor.NativeFocusRevision;
                }
            }
            if (_automatic && !_retired && !_witnessFailed && _editorFence is null && SourceFocused())
            {
                long revision = _owned.FocusRevision;
                if (revision > 0 && revision != _sourceReadyRevision)
                {
                    _sourceReadyRevision = revision;
                    Reply("source-ready", new(Descriptor: Descriptor()));
                }
            }
            FlushCompletionRequest();
            if (_witness is not null && _witness.Consumed != _lastWitnessAcknowledged)
            {
                _lastWitnessAcknowledged = _witness.Consumed;
                Reply("witness-consumed", new(WitnessSequence: _lastWitnessAcknowledged));
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException
            or ArgumentException or OverflowException)
        {
            ResetCompletion();
            _owned.Retire();
            _retired = true;
            ReleaseAcceptance();
            Interlocked.Exchange(ref _disconnected, 2);
            _sendRestart = false;
            _progressConnected = false;
            _progress = null;
            _automaticProgress = null;
            changed = true;
        }
        finally
        {
            FlushEditorTrace();
        }
        if (changed)
        {
            if (_restart is not null)
                Native.EnableWindow(_restart.Handle, _progressConnected && !_restartPending);
            RefreshSettings();
        }
    }

    private static OwnedCaptureDescriptor Descriptor() => new(Environment.ProcessId, ProcessStartUtcTicks,
        (long)_window, (long)_edit!.Handle, Native.GetCurrentThreadId(), _owned!.Cookie,
        _owned.FocusRevision, _sequence);

    private static bool SourceFocused() => _edit is not null && Native.GetFocus() == _edit.Handle
        && Native.GetForegroundWindow() == _window;

    private static void FenceEditor(OwnedEditorControl control)
    {
        // This runs at the same outer GUI boundary as InstallPlan, never on the
        // pipe reader. Fence only the old generation; keep this field's cookie,
        // lifetime and any result mapping until its ordinary release ACK.
        if (!_allowInjectedInput || _retired || _editorFence is not null || _witnessFailed
            || control.SourceEventSequence != 0 || control.Descriptor != Descriptor()
            || !SourceFocused() || _closePending || _restartPending
            || WindowsInterruptClock.NowMs >= control.ExpiresAtMs)
        {
            QueueEditorTrace(control.Intent, EditorEvent.FenceRejected, EditorReason.Fence);
            throw new InvalidDataException("practice-editor-fence");
        }
        uint overlayThread = Native.GetWindowThreadProcessId((nint)control.OverlayHwnd, out uint overlayProcess);
        if (control.OverlayHwnd == 0 || overlayThread == 0 || overlayProcess == 0)
        {
            QueueEditorTrace(control.Intent, EditorEvent.FenceRejected, EditorReason.Fence);
            throw new InvalidDataException("practice-editor-fence");
        }
        _planActive = false;
        _editorFence = control;
        _editorHandoff = EditorHandoff.Armed;
        _editorAppLeft = false;
        _editorOverlayThread = overlayThread;
        _editorOverlayProcess = overlayProcess;
        _editorNotice = null;
        WriteReply(new("edit-fenced", _connection, _run, _sequence,
            Editing: control with
            {
                SourceEventSequence = checked(++_editorEventSequence)
            }));
        QueueEditorTrace(control.Intent, EditorEvent.Fenced);
    }

    private static void ObserveEditorApp(nuint activation, nint peerThread)
    {
        if (_returnState != SourceReturnState.None)
        {
            ObserveReturnApp(activation, peerThread);
            return;
        }
        if (_editorFence is not { } editor)
            return;
        QueueEditorTrace(editor.Intent, EditorEvent.App,
            peer: peerThread == 0 ? EditorPeer.None : peerThread == (nint)_editorOverlayThread ? EditorPeer.OverlayThread : EditorPeer.Other,
            active: activation != 0);
        if (_editorHandoff == EditorHandoff.Invalid)
            return;
        // This thread edge is independent of root->EN ordering. It does not
        // identify a root HWND; the local root and field evidence remain required.
        if (!_editorAppLeft && activation == 0 && _editorOverlayThread != 0
            && peerThread == (nint)_editorOverlayThread
            && Native.GetWindowThreadProcessId((nint)editor.OverlayHwnd, out uint process) == _editorOverlayThread
            && process == _editorOverlayProcess && WindowsInterruptClock.NowMs < editor.ExpiresAtMs)
        {
            _editorEventSequence = checked(_editorEventSequence + 1);
            _editorAppLeft = true;
            CompleteEditorHandoff();
        }
        else
            InvalidateEditorHandoff(EditorReason.App);
    }

    private static void ObserveEditorRoot(nuint activation, nint peer)
    {
        if (_returnState != SourceReturnState.None)
        {
            ObserveReturnRoot(activation, peer);
            return;
        }
        if (_editorFence is not { } editor)
            return;
        QueueEditorTrace(editor.Intent, EditorEvent.Root,
            peer: peer == 0 ? EditorPeer.None : peer == (nint)editor.OverlayHwnd ? EditorPeer.Overlay : EditorPeer.Other,
            active: (activation & 0xffff) != 0);
        if (_editorHandoff == EditorHandoff.Invalid)
            return;
        if (_editorHandoff == EditorHandoff.Armed && (activation & 0xffff) == 0
            && (peer == 0 || peer == (nint)editor.OverlayHwnd)
            && WindowsInterruptClock.NowMs < editor.ExpiresAtMs)
        {
            _editorEventSequence = checked(_editorEventSequence + 1);
            _editorHandoff = EditorHandoff.RootLeft;
        }
        else
            InvalidateEditorHandoff(EditorReason.Root);
    }

    private static void ObserveEditorField(int notification)
    {
        if (_returnState != SourceReturnState.None)
        {
            ObserveReturnField(notification);
            return;
        }
        if (_editorFence is not { } editor)
            return;
        QueueEditorTrace(editor.Intent, notification == 0x0200 ? EditorEvent.FocusLost : EditorEvent.FocusGained);
        if (_editorHandoff == EditorHandoff.Invalid)
            return;
        // An early EN_KILLFOCUS cannot be saved and repaired by a later root
        // edge. Only the exact root->one Edit loss path closes this side.
        if (_editorHandoff == EditorHandoff.RootLeft && notification == 0x0200
            && WindowsInterruptClock.NowMs < editor.ExpiresAtMs)
        {
            _editorEventSequence = checked(_editorEventSequence + 1);
            _editorHandoff = EditorHandoff.FieldLeft;
            CompleteEditorHandoff();
        }
        else
            InvalidateEditorHandoff(EditorReason.Field);
    }

    private static void CompleteEditorHandoff()
    {
        if (!_editorAppLeft || _editorHandoff != EditorHandoff.FieldLeft || _editorFence is not { } editor)
            return;
        _editorHandoff = EditorHandoff.Left;
        _editorNotice = "edit-left";
        QueueEditorTrace(editor.Intent, EditorEvent.Left);
    }

    private static void InvalidateEditorHandoff(EditorReason reason)
    {
        if (_returnState != SourceReturnState.None)
        {
            InvalidateSourceReturn(reason);
            return;
        }
        if (_editorFence is null || _editorHandoff == EditorHandoff.Invalid)
            return;
        _planActive = false;
        _editorEventSequence = checked(_editorEventSequence + 1);
        _editorHandoff = EditorHandoff.Invalid;
        // Invalid wins over an unsent left edge. WndProc records only; stdio
        // is emitted by ApplyControls after normal native dispatch unwinds.
        _editorNotice = "edit-invalid";
        QueueEditorTrace(_editorFence.Intent, EditorEvent.Invalid, reason);
    }

    private static void ArmSourceReturn(OwnedReturnKind kind, long expiresAt)
    {
        _returnKind = kind;
        _returnState = SourceReturnState.Armed;
        _returnBaseline = checked(++_editorEventSequence);
        _returnExpiresAt = expiresAt;
        _returnFocusRevision = _owned!.FocusRevision;
        _returnAppActive = _returnResumed = false;
    }

    private static void ObserveReturnMouse(nuint root, nint parameters)
    {
        if (_editorFence is not { } editor || _editorHandoff != EditorHandoff.Left
            || _returnState != SourceReturnState.None || _retired || _closePending || _restartPending
            || root != (nuint)_window || (parameters.ToInt64() & 0xffff) != 1
            || ((parameters.ToInt64() >> 16) & 0xffff) != 0x0201)
            return;
        uint position = Native.GetMessagePos();
        var point = new Native.Point { X = unchecked((short)position), Y = unchecked((short)(position >> 16)) };
        if (_edit is null || Native.WindowFromPoint(point) != _edit.Handle)
            return;
        // A local candidate grants no authority. Let the original press, drag
        // and release reach the stock Edit; the host independently checks LL input.
        ArmSourceReturn(OwnedReturnKind.Mouse, WindowsInterruptClock.NowMs + 500);
        _mouseReturnNotice = new(editor.Intent, _returnBaseline, point.X, point.Y);
        QueueEditorTrace(editor.Intent, EditorEvent.ReturnMouse);
    }

    private static void FenceSourceReturn(OwnedReturnControl control, OwnedAcceptanceControl acceptance)
    {
        bool handedOff = false;
        try
        {
            if (_editorFence is not { } editor || _editorHandoff != EditorHandoff.Left
                || _returnControl is not null || _retired || _closePending || _restartPending || _plan is not null
                || control.EditorIntent != editor.Intent || control.SourceEventSequence != 0 || control.Descriptor is not null
                || control.ReturningToken.Target != editor.AdmittedToken.Target
                || control.ReturningToken.CommandGeneration != editor.AdmittedToken.CommandGeneration
                || control.ReturningToken.Revision != editor.AdmittedToken.Revision + 1
                || acceptance.WitnessBytes != OwnedPracticeAcceptanceProtocol.WitnessBytes)
                throw new InvalidDataException("practice-return-fence");
            if (control.Kind == OwnedReturnKind.Tab && _returnState == SourceReturnState.None)
                ArmSourceReturn(control.Kind, control.ExpiresAtMs);
            else if (_returnState == SourceReturnState.None || control.Kind != _returnKind)
                throw new InvalidDataException("practice-return-intent");
            _returnControl = control;
            _returnExpiresAt = Math.Min(_returnExpiresAt, control.ExpiresAtMs);
            if (_returnState == SourceReturnState.Invalid || WindowsInterruptClock.NowMs >= _returnExpiresAt)
            {
                InvalidateSourceReturn(EditorReason.Deadline);
                _returnNotice = "return-invalid";
                return;
            }
            // The replacement is still blank and detached from the host hook.
            // This single GUI ACK proves the old reader is closed and the new
            // reader exists before any input can be published to its ring.
            ReleasePlan();
            CloseWitness();
            handedOff = true; // Open owns closure, including a failed MapViewOfFile.
            _witnessMapping = OwnedMapping.Open(acceptance.WitnessHandle, acceptance.WitnessBytes, false);
            _witness = new(_witnessMapping);
            _witnessFailed = false;
            _lastWitnessAcknowledged = _installWitnessFloor = 0;
            WriteReply(new("return-fenced", _connection, _run, _sequence,
                Returning: control with
                {
                    SourceEventSequence = _returnBaseline
                }));
            QueueEditorTrace(editor.Intent, EditorEvent.ReturnFenced);
        }
        finally
        {
            if (!handedOff && acceptance.WitnessHandle != 0)
                Native.CloseHandle((nint)acceptance.WitnessHandle);
        }
    }

    private static void ResumeSourceReturn(OwnedReturnControl control)
    {
        if (_returnControl != control || _returnState is SourceReturnState.None or SourceReturnState.Invalid or SourceReturnState.Returned
            || _returnResumed || _witness is null || _witnessMapping is null || _witness.Poisoned || _witnessFailed
            || WindowsInterruptClock.NowMs >= _returnExpiresAt)
            throw new InvalidDataException("practice-return-resume");
        _returnResumed = true;
    }

    private static void ObserveReturnApp(nuint activation, nint peerThread)
    {
        if (_editorFence is not { } editor)
            return;
        QueueEditorTrace(editor.Intent, EditorEvent.ReturnApp,
            peer: peerThread == 0 ? EditorPeer.None : peerThread == (nint)_editorOverlayThread ? EditorPeer.OverlayThread : EditorPeer.Other,
            active: activation != 0);
        if (_returnState == SourceReturnState.Invalid)
            return;
        if (!_returnAppActive && activation != 0 && peerThread == (nint)_editorOverlayThread && _editorOverlayThread != 0
            && Native.GetWindowThreadProcessId((nint)editor.OverlayHwnd, out uint process) == _editorOverlayThread
            && process == _editorOverlayProcess && WindowsInterruptClock.NowMs < _returnExpiresAt)
        {
            _returnAppActive = true;
            _editorEventSequence = checked(_editorEventSequence + 1);
        }
        else
            InvalidateSourceReturn(EditorReason.App);
    }

    private static void ObserveReturnRoot(nuint activation, nint peer)
    {
        if (_editorFence is not { } editor)
            return;
        QueueEditorTrace(editor.Intent, EditorEvent.ReturnRoot,
            peer: peer == 0 ? EditorPeer.None : peer == (nint)editor.OverlayHwnd ? EditorPeer.Overlay : EditorPeer.Other,
            active: (activation & 0xffff) != 0);
        if (_returnState == SourceReturnState.Invalid)
            return;
        if (_returnState == SourceReturnState.Armed && (activation & 0xffff) != 0
            && (peer == 0 || peer == (nint)editor.OverlayHwnd) && WindowsInterruptClock.NowMs < _returnExpiresAt)
        {
            _returnState = SourceReturnState.RootActive;
            _editorEventSequence = checked(_editorEventSequence + 1);
        }
        else
            InvalidateSourceReturn(EditorReason.Root);
    }

    private static void ObserveReturnField(int notification)
    {
        if (_editorFence is not { } editor)
            return;
        QueueEditorTrace(editor.Intent, EditorEvent.ReturnFocus);
        if (_returnState == SourceReturnState.Invalid)
            return;
        if (_returnState == SourceReturnState.RootActive && notification == 0x0100
            && WindowsInterruptClock.NowMs < _returnExpiresAt)
        {
            _returnState = SourceReturnState.FieldFocused;
            _editorEventSequence = checked(_editorEventSequence + 1);
        }
        else
            InvalidateSourceReturn(EditorReason.Field);
    }

    private static void CompleteSourceReturn()
    {
        if (_returnState != SourceReturnState.FieldFocused || !_returnAppActive || !_returnResumed || _returnControl is null)
            return;
        if (_editorEventSequence != _returnBaseline + 3 || _owned!.FocusRevision <= _returnFocusRevision
            || !SourceFocused() || WindowsInterruptClock.NowMs >= _returnExpiresAt || _witness is null || _witness.Poisoned)
        {
            InvalidateSourceReturn(EditorReason.Field);
            return;
        }
        _returnState = SourceReturnState.Returned;
        _returnNotice = "source-returned";
        QueueEditorTrace(_returnControl.EditorIntent, EditorEvent.Returned);
    }

    private static void InvalidateSourceReturn(EditorReason reason)
    {
        if (_returnState is SourceReturnState.None or SourceReturnState.Invalid)
            return;
        _planActive = false;
        _returnState = SourceReturnState.Invalid;
        _editorEventSequence = checked(_editorEventSequence + 1);
        _returnNotice = "return-invalid";
        if (_editorFence is { } editor)
            QueueEditorTrace(editor.Intent, EditorEvent.ReturnInvalid, reason);
    }

    private static void QueueEditorTrace(Guid intent, EditorEvent kind, EditorReason reason = EditorReason.None,
        EditorPeer peer = EditorPeer.None, bool? active = null)
    {
        if (string.IsNullOrWhiteSpace(EditorTracePath) || _editorTraceCount >= 16)
            return;
        EditorTraces.Add(new(WindowsInterruptClock.NowMs, ++_editorTraceCount, intent, _editorEventSequence,
            _editorHandoff, kind, reason, peer, active));
    }

    private static void FlushEditorTrace()
    {
        if (EditorTraces.Count == 0)
            return;
        try
        {
            // Only this outer GUI boundary writes, after the original ACK path.
            // WndProc retains bounded enum/sequence facts, never text or handles.
            var lines = new StringBuilder();
            foreach (var entry in EditorTraces)
                lines.AppendLine(JsonSerializer.Serialize(new
                {
                    atMs = entry.AtMs,
                    operation = "owned-editor-source",
                    order = entry.Order,
                    intent = entry.Intent,
                    sourceSequence = entry.SourceSequence,
                    state = entry.State.ToString(),
                    kind = entry.Event.ToString(),
                    reason = entry.Reason.ToString(),
                    peer = entry.Peer.ToString(),
                    active = entry.Active
                }));
            File.AppendAllText(EditorTracePath + ".practice.jsonl", lines.ToString());
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally { EditorTraces.Clear(); }
    }

    private static void InstallPlan(OwnedAcceptanceControl control)
    {
        if (_editorFence is not null || _witnessFailed || _witness is null || _witness.Poisoned || _plan is not null
            || control.PlanSequence <= _lastPlanSequence || control.PlanId == Guid.Empty
            || control.PlanBytes != OwnedPracticeAcceptanceProtocol.BodyBytes
            || control.ResultBytes != OwnedPracticeAcceptanceProtocol.BodyBytes)
            throw new InvalidDataException("practice-install-order");
        using var mapping = OwnedMapping.Open(control.PlanHandle, control.PlanBytes, false);
        var plan = OwnedPracticeAcceptanceProtocol.ReadPlan(mapping); // Copy before granting anything.
        _resultMapping = OwnedMapping.Open(control.ResultHandle, control.ResultBytes, true);
        if (plan.Connection != _connection || plan.Run != _run || plan.PlanSequence != control.PlanSequence
            || plan.Permit.Plan.Id != control.PlanId || plan.Descriptor != Descriptor()
            || _returnedSeal is { } returned && (plan.Descriptor != returned.Descriptor
                || plan.CommandGeneration == returned.Control.ReturningToken.CommandGeneration
                || plan.SessionRevision <= returned.Control.ReturningToken.Revision)
            || !SourceFocused() || plan.InstallWitnessSequence > _witness.Sequence
            || !WindowsTarget.TryDecode(plan.Permit.Plan.Expected.Target, out var target)
            || target.ProcessId != Environment.ProcessId || target.ProcessStartUtcTicks != ProcessStartUtcTicks
            || target.RootHwnd != (long)_window || target.ControlHwnd != (long)_edit!.Handle
            || target.Provider != "win32-edit-transaction-v2" || target.Lifetime == Guid.Empty
            || WriteSafety.Validate(plan.Permit.Plan, plan.Permit.Plan.Expected, WindowsInterruptClock.NowMs) != Denial.None)
            throw new InvalidDataException("practice-install-identity");
        _plan = plan;
        _lastPlanSequence = plan.PlanSequence;
        _installWitnessFloor = _witness.Sequence;
        _transaction = CreateTransaction(plan);
        _planReported = false;
        _planActive = true;
        Reply("installed", PlanControl());
    }

    private static void RequirePlan(OwnedAcceptanceControl? control)
    {
        if (_plan is null || control is null || control.PlanSequence != _plan.PlanSequence
            || control.PlanId != _plan.Permit.Plan.Id)
            throw new InvalidDataException("practice-plan-order");
    }

    private static OwnedAcceptanceControl PlanControl(long witness = 0) => new(
        PlanId: _plan!.Permit.Plan.Id, PlanSequence: _plan.PlanSequence, WitnessSequence: witness);

    private static void ReleasePlan()
    {
        _planActive = false;
        _plan = null;
        _resultMapping?.Dispose();
        _resultMapping = null;
        if (_transaction is not null)
            CryptographicOperations.ZeroMemory(_transaction);
        _transaction = null;
    }

    private static void ClearEditorHandoff()
    {
        _editorFence = null;
        _editorHandoff = EditorHandoff.None;
        _editorAppLeft = false;
        _editorOverlayThread = _editorOverlayProcess = 0;
        _editorNotice = null;
        _returnState = SourceReturnState.None;
        _returnControl = null;
        _mouseReturnNotice = null;
        _returnNotice = null;
        _returnAppActive = _returnResumed = false;
        _returnBaseline = _returnExpiresAt = _returnFocusRevision = 0;
        _returnedSeal = null;
    }

    private static void ReleaseAcceptance()
    {
        ClearEditorHandoff();
        ReleasePlan();
        CloseWitness();
    }

    private static void CloseWitness()
    {
        _witness = null;
        _witnessMapping?.Dispose();
        _witnessMapping = null;
    }

    private static void Reply(string kind, OwnedAcceptanceControl? acceptance = null) =>
        WriteReply(new(kind, _connection, _run, _sequence, Acceptance: acceptance));

    private static void WriteReply(PracticeMessage message)
    {
        // Bounded stdio never pumps GUI messages. ACK cannot overtake execution.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        PracticeProgressPipe.WriteAsync(Console.OpenStandardOutput(),
            message, deadline.Token).GetAwaiter().GetResult();
    }

    private static void InvalidateFocus()
    {
        _planActive = false;
        if (_owned is not null && _owned.AdvanceFocusRevision() == 0)
            Interlocked.Exchange(ref _disconnected, 1);
    }

    private static bool ProcessInput(ref Native.Message message, bool inputQueue)
    {
        bool down = message.Id is 0x0100 or 0x0104;
        if (!down && message.Id is not (0x0101 or 0x0105))
            return false;
        if (!_automatic || _witnessFailed)
            return false;
        if (_witness is null)
            return inputQueue && PairRetiredPress(ref message, down);
        if (!inputQueue)
        {
            // Neither posted messages nor a fallback retrieval can establish
            // physical ownership or consume an outstanding physical key release.
            _planActive = false;
            return false;
        }
        bool accepted = false;
        try
        {
            if (!_witness.TryRead(out var witness))
            {
                if (_tabHeld)
                    return PairRetiredPress(ref message, down);
                if (_plan is not null)
                    FailWitness();
                return false;
            }
            ulong bits = unchecked((ulong)(long)message.LParam);
            bool extended = (bits & (1UL << 24)) != 0;
            bool repeated = down && (bits & (1UL << 30)) != 0;
            // LL input distinguishes left/right modifiers; WM_KEY* uses the
            // generic modifier VK. Scan code and extended bit still identify the
            // exact physical key; this does not relax their FIFO comparison.
            uint messageKey = witness.VirtualKey switch
            {
                0xa0 or 0xa1 => 0x10,
                0xa2 or 0xa3 => 0x11,
                0xa4 or 0xa5 => 0x12,
                _ => witness.VirtualKey
            };
            bool tupleMatches = messageKey == (uint)message.WParam
                && witness.MessageTime == message.Time && witness.ScanCode == ((bits >> 16) & 0xff)
                && ((witness.NativeFlags & 1) != 0) == extended
                && ((witness.NativeFlags & 0x80) == 0) == down && witness.Down == down
                && (!down || witness.Repeated == repeated) && (bits & 0xffff) == 1
                && witness.Injected == ((witness.NativeFlags & 0x12) != 0);
            if (!tupleMatches)
            {
                FailWitness();
                return false;
            }
            if (message.WParam != 9)
            {
                _planActive = false;
                return false;
            }
            if (!down)
            {
                bool consume = _tabHeld && _tabConsumed && witness.ScanCode == _tabScanCode
                    && extended == _tabExtended;
                _tabHeld = _tabConsumed = false;
                return consume;
            }
            if (_tabHeld)
            {
                if (repeated && witness.ScanCode == _tabScanCode && extended == _tabExtended)
                    return _tabConsumed;
                // A release may have reached another window. A fresh down is
                // a new press; it must not inherit the old consume decision.
                _tabHeld = _tabConsumed = false;
                if (repeated)
                {
                    _planActive = false;
                    return false;
                }
            }
            _tabHeld = true;
            _tabConsumed = false;
            _tabScanCode = witness.ScanCode;
            _tabExtended = extended;
            var plan = _plan;
            if (_retired || plan is null || !_planActive || _planReported || _transaction is null || _resultMapping is null)
                return false;
            _planActive = false; // One attempt; rejection must not be resurrected by a repeat.
            long now = WindowsInterruptClock.NowMs;
            bool qualified = !repeated && witness.Eligible && !witness.Modified
                && (!witness.Injected || _allowInjectedInput)
                && message.Id == 0x0100 && message.Window == _edit!.Handle
                && (bits & (1UL << 29)) == 0 && !ModifiersDown()
                && witness.Sequence > _installWitnessFloor && witness.Sequence > plan.InstallWitnessSequence
                && witness.PlanSequence == plan.PlanSequence
                && witness.NativeFocusRevision == plan.Descriptor.NativeFocusRevision
                && _owned!.FocusRevision == plan.Descriptor.NativeFocusRevision
                && witness.UserEpoch == plan.UserEpoch && witness.ForegroundEpoch == plan.ForegroundEpoch
                && witness.ObservedAtMs >= plan.Permit.ObservedAtMs && witness.ObservedAtMs <= plan.Permit.ExpiresAtMs
                && now >= witness.ObservedAtMs && now <= plan.Permit.ExpiresAtMs
                && SourceFocused() && Volatile.Read(ref _disconnected) == 0
                && !_closePending && !_sendRestart && PendingControls.IsEmpty;
            WriteReceipt receipt;
            if (!qualified)
                receipt = new(WriteStatus.RejectedBeforeMutation, Denial.EpochChanged);
            else
            {
                BinaryPrimitives.WriteUInt64LittleEndian(_transaction.AsSpan(48), Native.GetTickCount64() + 500);
                accepted = _owned!.TryExecute(_transaction, plan.Descriptor.NativeFocusRevision);
                receipt = ReadReceipt(plan, _transaction);
            }
            _tabConsumed = accepted;
            _planReported = true;
            var result = new OwnedAcceptanceResult(_connection, _run, plan.PlanSequence, plan.Permit.Plan.Id,
                witness.Sequence, accepted ? witness.ObservedAtMs : 0, receipt);
            OwnedPracticeAcceptanceProtocol.WriteResult(_resultMapping, result);
            Reply("receipt", PlanControl(witness.Sequence));
            return accepted;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException
            or ArgumentException or OverflowException)
        {
            _planActive = false;
            Interlocked.Exchange(ref _disconnected, 1);
            // If native crossed its acceptance point, failure to return a receipt
            // is unknown to the parent. Passing the original Tab would be a second
            // action; keep that press consumed and never attempt another write.
            _tabConsumed = accepted;
            return accepted;
        }
    }

    private static bool PairRetiredPress(ref Native.Message message, bool down)
    {
        if (!_tabHeld || message.WParam != 9)
            return false;
        ulong bits = unchecked((ulong)(long)message.LParam);
        if (((bits >> 16) & 0xff) != _tabScanCode || ((bits & (1UL << 24)) != 0) != _tabExtended)
            return false;
        bool consume = _tabConsumed;
        if (!down)
            _tabHeld = _tabConsumed = false;
        else if ((bits & (1UL << 30)) == 0)
        {
            // A new press cannot inherit a missing release or the old decision.
            _tabHeld = _tabConsumed = false;
            return false;
        }
        return consume;
    }

    private static bool ModifiersDown() => (Native.GetKeyState(0x10) & 0x8000) != 0
        || (Native.GetKeyState(0x11) & 0x8000) != 0 || (Native.GetKeyState(0x12) & 0x8000) != 0
        || (Native.GetKeyState(0x5b) & 0x8000) != 0 || (Native.GetKeyState(0x5c) & 0x8000) != 0;

    private static void FailWitness()
    {
        _witnessFailed = true;
        _planActive = false;
        _tabHeld = _tabConsumed = false;
        Reply("witness-failed");
    }

    private static byte[] CreateTransaction(OwnedAcceptancePlan installed)
    {
        var plan = installed.Permit.Plan;
        var expected = plan.Expected;
        var descriptor = installed.Descriptor;
        byte[] bytes = new byte[49264];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)descriptor.ProcessId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), descriptor.ThreadId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), descriptor.ControlHwnd);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), descriptor.RootHwnd);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), descriptor.NativeCookie);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), expected.Text.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), plan.Scope?.Start ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(64), plan.Scope?.End ?? expected.Text.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(68), plan.Replacement.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(88), expected.Selection!.Value.Start);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(92), expected.Selection.Value.End);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(96), installed.Permit.ExpiresAtMs);
        Encoding.Unicode.GetBytes(expected.Text, bytes.AsSpan(104));
        Encoding.Unicode.GetBytes(plan.Replacement, bytes.AsSpan(104 + 16386));
        return bytes;
    }

    private static WriteReceipt ReadReceipt(OwnedAcceptancePlan installed, byte[] bytes)
    {
        uint status = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var reason = (Denial)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        if (status == 2)
            return new(WriteStatus.RejectedBeforeMutation, Enum.IsDefined(reason) ? reason : Denial.PlatformFailure);
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(56));
        int start = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(72));
        int end = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(76));
        var ime = (ImeState)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(84));
        var plan = installed.Permit.Plan;
        if (status != 1 || length < 0 || length > Limits.MaxTextUnits || ime != ImeState.Clear
            || start != plan.Caret || end != plan.Caret)
            return new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
        string text = Encoding.Unicode.GetString(bytes, 104 + 32772, length * 2);
        if (text != plan.ResultText)
            return new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
        return new(WriteStatus.Verified, Denial.None, plan.Expected with
        {
            Text = text,
            Selection = new(start, end),
            Ime = ime,
            ObservedAtMs = WindowsInterruptClock.NowMs
        });
    }

    private static void RestartPractice()
    {
        ResetCompletion();
        InvalidateEditorHandoff(EditorReason.Restart);
        _planActive = false;
        _restartPending = true;
        _sendRestart = true;
        _progress = null;
        Native.EnableWindow(_restart!.Handle, false);
        Native.PostMessageW(_window, ProgressMessage, 0, 0);
    }

    private static void ClosePractice()
    {
        ResetCompletion();
        InvalidateEditorHandoff(EditorReason.Close);
        _planActive = false;
        _owned?.Retire();
        _closePending = true;
        Native.PostMessageW(_window, ProgressMessage, 0, 0);
    }

    private static void RefreshTheme()
    {
        if (_window == 0)
            return;
        var contrast = new Native.HighContrast { Size = (uint)Marshal.SizeOf<Native.HighContrast>() };
        // If accessibility settings cannot be read, retain Windows' colors.
        // DefaultScheme is borrowed, despite the Learn page's ownership claim.
        // The Windows Forms implementation documents the correction:
        // https://source.dot.net/System.Private.Windows.Core/Windows/Win32/PInvokeCore.SystemParametersInfo.cs.html
        bool systemColors = !Native.SystemParametersInfoW(0x0042, contrast.Size, ref contrast, 0)
            || (contrast.Flags & 1) != 0;
        bool dark = !systemColors && _theme == "dark";
        if (!systemColors && _theme == "system")
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                systemColors = true;
            }
        }
        // Same Panel/Text palette as the Windows App's UiTheme. COLORREF stores
        // these RGB colors with red in the low byte.
        uint background = systemColors ? Native.GetSysColor(5) : dark ? 0x00302824u : 0x00ffffffu;
        uint text = systemColors ? Native.GetSysColor(8) : dark ? 0x00f1ebe8u : 0x00312a25u;
        if (_themeInitialized && background == _backgroundColor && text == _textColor
            && systemColors == _systemColors && dark == _dark)
            return;

        nint brush = systemColors ? 0 : Native.CreateSolidBrush(background);
        if (!systemColors && brush == 0)
            throw new InvalidOperationException();
        nint previous = _backgroundBrush;
        _backgroundBrush = brush;
        _backgroundColor = background;
        _textColor = text;
        _systemColors = systemColors;
        _dark = dark;
        _themeInitialized = true;
        if (previous != 0)
            Native.DeleteObject(previous);

        int darkTitle = dark ? 1 : 0;
        // Attribute 20 is the documented DWM title-bar opt-in. Older Windows
        // versions may reject it; their native frame remains readable.
        _ = Native.DwmSetWindowAttribute(_window, 20, ref darkTitle, sizeof(int));
        Native.RedrawWindow(_window, 0, 0, 0x0001 | 0x0004 | 0x0080 | 0x0400);
    }

    private static nint BackgroundBrush => _backgroundBrush != 0 ? _backgroundBrush : Native.GetSysColorBrush(5);

    private static bool SetText(Control control, string value)
    {
        if (control.Text == value)
            return false;
        if (!Native.SetWindowTextW(control.Handle, value))
            throw new InvalidOperationException();
        control.Text = value;
        return true;
    }

    private static void SetFonts()
    {
        nint body = Native.CreateFontW(-Scale(16), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
        nint title = Native.CreateFontW(-Scale(24), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
        if (body == 0 || title == 0)
        {
            if (body != 0)
                Native.DeleteObject(body);
            if (title != 0)
                Native.DeleteObject(title);
            throw new InvalidOperationException();
        }
        foreach (var control in Controls)
            Native.SendMessageW(control.Handle, 0x0030, (nuint)(control == _title ? title : body), 1);
        nint oldBody = _bodyFont, oldTitle = _titleFont;
        _bodyFont = body;
        _titleFont = title;
        if (oldBody != 0)
            Native.DeleteObject(oldBody);
        if (oldTitle != 0)
            Native.DeleteObject(oldTitle);
    }

    private static void SizeInitialWindow()
    {
        var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfoW(Native.MonitorFromWindow(_window, 2), ref monitor))
            throw new InvalidOperationException();
        int width = Math.Min(Scale(800), monitor.Work.Right - monitor.Work.Left);
        int height = Math.Min(Scale(740), monitor.Work.Bottom - monitor.Work.Top);
        Native.SetWindowPos(_window, 0, monitor.Work.Left + (monitor.Work.Right - monitor.Work.Left - width) / 2,
            monitor.Work.Top + (monitor.Work.Bottom - monitor.Work.Top - height) / 2,
            width, height, 0x0014); // Do not activate or change Z order.
    }

    private static void Layout()
    {
        if (_close is null || _bodyFont == 0 || _layingOut)
            return;
        _layingOut = true;
        nint dc = Native.GetDC(_window);
        if (dc == 0)
            throw new InvalidOperationException();
        nint previous = 0;
        try
        {
            Native.GetClientRect(_window, out var client);
            int margin = Scale(24), width = Math.Max(Scale(120), client.Right - 2 * margin);
            int y = margin;
            previous = Native.SelectObject(dc, _bodyFont);
            foreach (var control in Controls)
            {
                int height;
                if (control == _edit)
                    height = Scale(128);
                else if (control == _finish)
                    height = CompletionControlVisible ? Scale(36) : 0;
                else if (control == _close || control == _restart)
                    height = Scale(36);
                else if (control.Text.Length == 0)
                    height = 0;
                else
                {
                    Native.SelectObject(dc, control == _title ? _titleFont : _bodyFont);
                    var measure = new Native.Rect { Right = width };
                    Native.DrawTextW(dc, control.Text, -1, ref measure, 0x0010 | 0x0400 | 0x0800 | 0x2000);
                    height = measure.Bottom + Scale(4);
                }
                control.Bounds = new(margin, y, control == _close || control == _restart || control == _finish ? Scale(144) : width, height);
                if (height != 0)
                    y += height + Scale(control == _inputLabel ? 6 : 12);
            }
            _contentHeight = y + margin - Scale(12);
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentHeight - client.Bottom));
            var scroll = new Native.ScrollInfo
            {
                Size = (uint)Marshal.SizeOf<Native.ScrollInfo>(),
                Mask = 0x0001 | 0x0002 | 0x0004 | 0x0008,
                Maximum = _contentHeight - 1,
                Page = (uint)Math.Max(1, client.Bottom),
                Position = _scroll
            };
            Native.SetScrollInfo(_window, 1, ref scroll, true);
            foreach (var control in Controls)
                Native.MoveWindow(control.Handle, control.Bounds.X, control.Bounds.Y - _scroll,
                    control.Bounds.Width, control.Bounds.Height, true);
        }
        finally
        {
            if (previous != 0)
                Native.SelectObject(dc, previous);
            Native.ReleaseDC(_window, dc);
            _layingOut = false;
        }
    }

    private static void ScrollTo(int position)
    {
        Native.GetClientRect(_window, out var client);
        _scroll = Math.Clamp(position, 0, Math.Max(0, _contentHeight - client.Bottom));
        Layout();
    }

    private static void EnsureVisible(Control control)
    {
        Native.GetClientRect(_window, out var client);
        if (control.Bounds.Y < _scroll)
            ScrollTo(control.Bounds.Y - Scale(12));
        else if (control.Bounds.Y + control.Bounds.Height > _scroll + client.Bottom)
            ScrollTo(control.Bounds.Y + control.Bounds.Height - client.Bottom + Scale(12));
    }

    private static int Scale(int value) => (int)Math.Round(value * _dpi / 96d);

    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case ProgressMessage:
                    // Wake only; the outer loop owns control consumption.
                    return 0;
                case 0x0002: // WM_DESTROY, including partial construction.
                    Native.KillTimer(window, 1);
                    _window = 0;
                    Native.PostQuitMessage(_exitCode);
                    return 0;
                case 0x0005: // WM_SIZE
                    Layout();
                    return 0;
                case 0x0021: // WM_MOUSEACTIVATE: retain ordinary stock Edit mouse processing.
                    ObserveReturnMouse(wParam, lParam);
                    break;
                case 0x0006: // WM_ACTIVATE
                    InvalidateFocus();
                    ObserveEditorRoot(wParam, lParam); // Before DefWindowProc can notify the stock Edit.
                    if ((wParam & 0xffff) != 0)
                        RefreshSettings();
                    break;
                case 0x001c: // WM_ACTIVATEAPP
                    ObserveEditorApp(wParam, lParam);
                    break;
                case 0x0007: // WM_SETFOCUS; never activate another window.
                    if (_edit is not null)
                        Native.SetFocus(_edit.Handle);
                    return 0;
                case 0x0010: // WM_CLOSE
                    ClosePractice();
                    return 0;
                case 0x0014: // WM_ERASEBKGND
                    Native.GetClientRect(window, out var background);
                    Native.FillRect((nint)wParam, ref background, BackgroundBrush);
                    return 1;
                case 0x0015: // WM_SYSCOLORCHANGE
                case 0x001A: // WM_SETTINGCHANGE, including high-contrast changes.
                    RefreshTheme();
                    break;
                case 0x0024: // WM_GETMINMAXINFO
                    var minimum = Marshal.PtrToStructure<Native.MinMaxInfo>(lParam);
                    minimum.MinTrack = new()
                    {
                        X = Scale(360),
                        Y = Scale(280)
                    };
                    Marshal.StructureToPtr(minimum, lParam, false);
                    return 0;
                case 0x0111: // WM_COMMAND
                    int id = (int)(wParam & 0xffff), notification = (int)((wParam >> 16) & 0xffff);
                    if (id == CloseId && notification == 0)
                        ClosePractice();
                    else if (id == RestartId && notification == 0 && _progressConnected && !_restartPending)
                        RestartPractice();
                    else if (id == FinishId && notification == 0)
                        QueueFinish();
                    else if (id == EditId && notification is 0x0100 or 0x0200
                        && _edit is not null && lParam == _edit.Handle)
                    {
                        InvalidateFocus(); // Before layout or any other reentrant call.
                        ObserveEditorField(notification);
                        if (notification == 0x0100)
                            EnsureVisible(_edit);
                    }
                    else if (notification == 6 && id is CloseId or RestartId or FinishId)
                        EnsureVisible(id == CloseId ? _close! : id == RestartId ? _restart! : _finish!);
                    return 0;
                case 0x0113: // WM_TIMER
                    if (wParam == 1)
                        RefreshSettings();
                    return 0;
                case 0x0115: // WM_VSCROLL
                    Native.GetClientRect(window, out var client);
                    var scroll = new Native.ScrollInfo
                    {
                        Size = (uint)Marshal.SizeOf<Native.ScrollInfo>(),
                        Mask = 0x0010
                    };
                    Native.GetScrollInfo(window, 1, ref scroll);
                    ScrollTo((int)(wParam & 0xffff) switch
                    {
                        0 => _scroll - Scale(32),
                        1 => _scroll + Scale(32),
                        2 => _scroll - client.Bottom,
                        3 => _scroll + client.Bottom,
                        4 or 5 => scroll.TrackPosition,
                        6 => 0,
                        7 => _contentHeight,
                        _ => _scroll
                    });
                    return 0;
                case 0x0133: // WM_CTLCOLOREDIT; the Edit's WndProc stays untouched.
                case 0x0138: // WM_CTLCOLORSTATIC
                    Native.SetTextColor((nint)wParam, _themeInitialized ? _textColor : Native.GetSysColor(8));
                    Native.SetBkColor((nint)wParam, _themeInitialized ? _backgroundColor : Native.GetSysColor(5));
                    return BackgroundBrush;
                case 0x020A: // WM_MOUSEWHEEL not consumed by the stock Edit.
                    ScrollTo(_scroll - (short)((wParam >> 16) & 0xffff) * Scale(48) / 120);
                    return 0;
                case 0x02E0: // WM_DPICHANGED
                    _dpi = (int)(wParam & 0xffff);
                    var suggested = Marshal.PtrToStructure<Native.Rect>(lParam);
                    if (_close is not null)
                        SetFonts();
                    Native.SetWindowPos(window, 0, suggested.Left, suggested.Top,
                        suggested.Right - suggested.Left, suggested.Bottom - suggested.Top, 0x0014);
                    Layout();
                    return 0;
            }
            return Native.DefWindowProcW(window, message, wParam, lParam);
        }
        catch
        {
            // Managed exceptions cannot unwind through the native WndProc.
            _exitCode = Fail();
            Native.PostQuitMessage(_exitCode);
            return 0;
        }
    }

    private sealed class Control(nint handle, string text)
    {
        public nint Handle { get; } = handle;
        public string Text { get; set; } = text;
        public Bounds Bounds
        {
            get; set;
        }
    }

    private readonly record struct Bounds(int X, int Y, int Width, int Height);

    private static class Native
    {
        internal delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            public uint Style;
            public WindowProc Procedure;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background;
            public string? MenuName;
            public string ClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int X, Y;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left, Top, Right, Bottom;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            public nint Window;
            public uint Id;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public Point Point;
            public uint Private;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MinMaxInfo
        {
            public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            public int Size; public Rect Monitor, Work; public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ScrollInfo
        {
            public uint Size, Mask; public int Minimum, Maximum; public uint Page; public int Position, TrackPosition;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct HighContrast
        {
            public uint Size, Flags;
            public nint DefaultScheme;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? module);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern ushort RegisterClassW(ref WindowClass windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClassW(string className, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowExW(uint exStyle, string className, string text, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint LoadCursorW(nint instance, nint resource);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool PeekMessageW(out Message message, nint window, uint minimum, uint maximum, uint remove);
        [DllImport("user32.dll")] internal static extern uint MsgWaitForMultipleObjectsEx(uint count, nint handles, uint milliseconds, uint wakeMask, uint flags);
        [DllImport("user32.dll")] internal static extern nint GetFocus();
        [DllImport("user32.dll")] internal static extern uint GetMessagePos();
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern short GetKeyState(int virtualKey);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
        [DllImport("kernel32.dll")] internal static extern ulong GetTickCount64();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool IsDialogMessageW(nint window, ref Message message);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DispatchMessageW(ref Message message);
        [DllImport("user32.dll")] internal static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern bool EnableWindow(nint window, bool enabled);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int exitCode);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] internal static extern nint SetFocus(nint window);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool MoveWindow(nint window, int x, int y, int width, int height, bool repaint);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetWindowTextW(nint window, string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);
        [DllImport("user32.dll")] internal static extern bool KillTimer(nint window, nuint id);
        [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] internal static extern int SetScrollInfo(nint window, int bar, ref ScrollInfo info, bool redraw);
        [DllImport("user32.dll")] internal static extern bool GetScrollInfo(nint window, int bar, ref ScrollInfo info);
        [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int DrawTextW(nint dc, string text, int length, ref Rect rect, uint format);
        [DllImport("user32.dll")] internal static extern uint GetSysColor(int index);
        [DllImport("user32.dll")] internal static extern nint GetSysColorBrush(int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SystemParametersInfoW(uint action, uint parameter, ref HighContrast value, uint flags);
        [DllImport("user32.dll")] internal static extern int FillRect(nint dc, ref Rect rect, nint brush);
        [DllImport("user32.dll")] internal static extern bool RedrawWindow(nint window, nint rect, nint region, uint flags);
        [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
        [DllImport("gdi32.dll")] internal static extern nint CreateSolidBrush(uint color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);
        [DllImport("gdi32.dll")] internal static extern uint SetTextColor(nint dc, uint color);
        [DllImport("gdi32.dll")] internal static extern uint SetBkColor(nint dc, uint color);
    }
}
