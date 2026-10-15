using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.Practice;

internal static class Program
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
    private static Control? _title, _notice, _inputLabel, _edit, _preview, _replace, _progressNotice, _restart, _close;
    private static OnDemandPracticeProgress? _progress;
    private static readonly ConcurrentQueue<PracticeMessage> PendingControls = new();
    private static int _pendingCount, _disconnected;
    private static OwnedPracticeField? _owned;
    private static Guid _connection, _run;
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
            Add("Static", presentation.Summary);
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
            Add("Static", presentation.CapabilityNote);
            _restart = Add("Button", presentation.RestartLabel, 0x00010000 | 0x4000, RestartId);
            Native.EnableWindow(_restart.Handle, false);
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
            while (true)
            {
                int result = Native.GetMessageW(out var message, 0, 0, 0);
                if (result == 0)
                    return _exitCode;
                if (result < 0)
                    return Fail();
                // GetMessage may dispatch sent messages or run a nested pump.
                // Retirement is acknowledged only after that stack has unwound.
                ApplyControls();
                if (_window == 0)
                    continue;
                if (!Native.IsDialogMessageW(_window, ref message))
                {
                    Native.TranslateMessage(ref message);
                    Native.DispatchMessageW(ref message);
                }
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
            var view = OnDemandPracticePresentation.Create(settings!, _progress);
            changed = SetText(_notice, view.CurrentStateNotice)
                | SetText(_preview, view.PreviewStep) | SetText(_replace, view.ReplaceStep)
                | SetText(_progressNotice, view.ProgressNotice);
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
        if (!_closePending && !_sendRestart && PendingControls.IsEmpty && Volatile.Read(ref _disconnected) != 1)
            return;
        if (_owned is null)
        {
            if (_closePending)
                Native.DestroyWindow(_window);
            return;
        }
        try
        {
            if (Volatile.Read(ref _disconnected) != 0)
            {
                _owned.Retire();
                _retired = true;
                _progressConnected = false;
                _progress = null;
                _sendRestart = false;
                Interlocked.Exchange(ref _disconnected, 2);
                while (PendingControls.TryDequeue(out _))
                {
                }
            }
            else
            {
                while (PendingControls.TryDequeue(out var message))
                {
                    Interlocked.Decrement(ref _pendingCount);
                    if (Volatile.Read(ref _disconnected) != 0)
                        throw new IOException("practice-control-disconnected");
                    if (message.Connection != _connection)
                        throw new InvalidDataException("practice-control-connection");
                    if (message.Kind == "begin" && _retired && message.Run != _run && message.Sequence == _sequence + 1)
                    {
                        _owned.Begin();
                        _run = message.Run;
                        _sequence = message.Sequence;
                        _retired = false;
                        _progressConnected = true;
                        _restartPending = false;
                        _progress = null;
                        Reply("started");
                    }
                    else if (message.Kind == "retire" && message.Run == _run && message.Sequence == _sequence + 1)
                    {
                        _owned.Retire();
                        _retired = true;
                        _sequence = message.Sequence;
                        _progress = null;
                        Reply("retired");
                    }
                    else if (message.Kind == "progress" && !_retired && message.Run == _run && message.Sequence == _sequence)
                        _progress = message.Progress;
                    else
                        throw new InvalidDataException("practice-control-order");
                }
                if (Volatile.Read(ref _disconnected) != 0)
                    throw new IOException("practice-control-disconnected");
                if (_sendRestart)
                {
                    _sendRestart = false;
                    Reply("restart");
                }
            }
            if (_closePending)
            {
                _owned.Retire();
                _retired = true;
                if (_progressConnected && _sequence != 0)
                    Reply("closed");
                Native.DestroyWindow(_window);
                return;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
        {
            _owned.Retire();
            _retired = true;
            Interlocked.Exchange(ref _disconnected, 2);
            _sendRestart = false;
            _progressConnected = false;
            _progress = null;
            if (_closePending)
                Native.DestroyWindow(_window);
        }
        if (_restart is not null)
            Native.EnableWindow(_restart.Handle, _progressConnected && !_restartPending);
        RefreshSettings();
    }

    private static void Reply(string kind)
    {
        // This small private control exchange is bounded and deliberately does
        // not pump GUI messages. An ACK cannot overtake an executing transaction.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        PracticeProgressPipe.WriteAsync(Console.OpenStandardOutput(),
            new(kind, _connection, _run, _sequence), deadline.Token).GetAwaiter().GetResult();
    }

    private static void RestartPractice()
    {
        _restartPending = true;
        _sendRestart = true;
        _progress = null;
        Native.EnableWindow(_restart!.Handle, false);
        Native.PostMessageW(_window, ProgressMessage, 0, 0);
    }

    private static void ClosePractice()
    {
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
                control.Bounds = new(margin, y, control == _close || control == _restart ? Scale(144) : width, height);
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
                case 0x0006: // WM_ACTIVATE
                    if ((wParam & 0xffff) != 0)
                        RefreshSettings();
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
                    else if (id == EditId && notification == 0x0100 && _edit is not null)
                        EnsureVisible(_edit);
                    else if (notification == 6 && id is CloseId or RestartId)
                        EnsureVisible(id == CloseId ? _close! : _restart!);
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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetMessageW(out Message message, nint window, uint minimum, uint maximum);
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
