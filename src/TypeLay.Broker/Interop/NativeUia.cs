using System.Runtime.InteropServices;
using TypeLay.Core;
using TypeLay.Protocol;
namespace TypeLay.Broker;

// Keep text-range calls in native UIA. The managed TextPatternRange.GetText wrapper can
// terminate the broker with an access violation on Windows 11.
// IUIAutomation: GetFocusedElement=8. Element: GetRuntimeId=4, GetCurrentPattern=16.
// TextEdit inherits six TextPattern methods: GetActiveComposition=9.
internal sealed unsafe class NativeUia : IDisposable
{
    private nint _automation, _element;
    private bool _comInitialized;
    private int _prefixStage;
    private int _lastPrefixStage = -1;
    private static readonly Guid Clsid = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid AutomationIid = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    private static readonly Guid EditIid = new("17E21576-996C-4870-99D9-BFF323380C06");
    private static readonly Guid TextIid = new("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A");
    private static readonly Guid Text2Iid = new("506A921A-FCC9-409F-B23B-37EB74106872");
    public NativeUia()
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
        _comInitialized = true;
        Guid c = Clsid, i = AutomationIid;
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref c, 0, 1, ref i, out _automation));
        }
        catch { CoUninitialize(); _comInitialized = false; throw; }
    }
    private static nint Slot(nint p, int index) => ((nint*)*(nint*)p)[index];
    public string? BindFocused()
    {
        ReleaseElement();
        nint value = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_automation, 8))(_automation, &value);
        if (hr < 0 || value == 0)
            return null;
        _element = value;
        nint array = 0;
        hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_element, 4))(_element, &array);
        if (hr < 0 || array == 0)
            return null;
        try
        {
            if (SafeArrayGetDim(array) != 1 || SafeArrayGetLBound(array, 1, out int lo) < 0
                || SafeArrayGetUBound(array, 1, out int hi) < 0 || hi < lo || hi - lo > 128)
                return null;
            if (SafeArrayAccessData(array, out nint data) < 0)
                return null;
            try
            {
                int[] ids = new int[hi - lo + 1];
                Marshal.Copy(data, ids, 0, ids.Length);
                return string.Join('.', ids);
            }
            finally { SafeArrayUnaccessData(array); }
        }
        finally { SafeArrayDestroy(array); }
    }
    public ImeState Composition(nint root)
    {
        if (_element == 0)
            return ImeState.Unknown;
        nint pattern = 0, edit = 0, range = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(_element, 16))(_element, 10032, &pattern);
        if (hr < 0 || pattern == 0)
        {
            TraceIme("textedit-unavailable", hr);
            return NativeComposition(root);
        }
        try
        {
            Guid iid = EditIid;
            hr = Marshal.QueryInterface(pattern, in iid, out edit);
            if (hr < 0 || edit == 0)
            {
                TraceIme("textedit-interface", hr);
                return ImeState.Unknown;
            }
            hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(edit, 9))(edit, &range);
            TraceIme("textedit-composition", hr < 0 ? hr : range == 0 ? 0 : 1);
            return hr < 0 ? ImeState.Unknown : range == 0 ? ImeState.Clear : ImeState.Composing;
        }
        finally
        {
            if (range != 0)
                Marshal.Release(range);
            if (edit != 0)
                Marshal.Release(edit);
            Marshal.Release(pattern);
        }
    }
    public bool? ReadOnly()
    {
        nint pattern = 0, document = 0;
        byte* variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        try
        {
            pattern = TextPattern();
            if (pattern == 0 || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pattern, 7))(pattern, &document) < 0
                || document == 0)
                return null;
            int hr = ((delegate* unmanaged[Stdcall]<nint, int, nint, int>)Slot(document, 9))(document, 40015, (nint)variant);
            return hr >= 0 && *(ushort*)variant == 11 ? *(short*)(variant + 8) != 0 : null;
        }
        finally
        {
            VariantClear((nint)variant);
            if (document != 0)
                Marshal.Release(document);
            if (pattern != 0)
                Marshal.Release(pattern);
        }
    }
    public string? ReadCommittedPrefix(int maxUnits, nint root)
    {
        _prefixStage = 1;
        nint pattern = 0, edit = 0, composition = 0, textPattern = 0, document = 0, prefix = 0;
        try
        {
            if (_element == 0 || ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(_element, 16))
                (_element, 10032, &pattern) < 0 || pattern == 0)
                return ReadImmCommittedPrefix(maxUnits, root);
            Guid iid = EditIid;
            _prefixStage = 2;
            if (Marshal.QueryInterface(pattern, in iid, out edit) < 0 || edit == 0
                || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(edit, 9))(edit, &composition) < 0
                || composition == 0)
                return null;
            _prefixStage = 3;
            textPattern = TextPattern();
            if (textPattern == 0 || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(textPattern, 7))(textPattern, &document) < 0
                || document == 0 || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(document, 3))(document, &prefix) < 0
                || prefix == 0)
                return ReadImmCommittedPrefix(maxUnits, root);
            _prefixStage = 4;
            // Clip before the active composition. GetText never receives the
            // composition range or the entire document containing candidate text.
            if (((delegate* unmanaged[Stdcall]<nint, int, nint, int, int>)Slot(prefix, 15))(prefix, 1, composition, 0) < 0)
                return null;
            string? text = RangeText(prefix, maxUnits + 1);
            _prefixStage = text is not null && text.Length <= maxUnits ? 0 : 5;
            return text is not null && text.Length <= maxUnits ? text : null;
        }
        finally
        {
            if (prefix != 0)
                Marshal.Release(prefix);
            if (document != 0)
                Marshal.Release(document);
            if (textPattern != 0)
                Marshal.Release(textPattern);
            if (composition != 0)
                Marshal.Release(composition);
            if (edit != 0)
                Marshal.Release(edit);
            if (pattern != 0)
                Marshal.Release(pattern);
            TracePrefixStage();
        }
    }
    private string? ReadImmCommittedPrefix(int maxUnits, nint root)
    {
        _prefixStage = 10;
        uint thread = GetWindowThreadProcessId(root, out _);
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref gui) || gui.Focus == 0
            || GetAncestor(gui.Focus, 2) != root)
            return null;
        nint context = ImmGetContext(gui.Focus);
        _prefixStage = 11;
        if (context == 0)
            return null;
        nint pattern = 0, document = 0, selections = 0, caret = 0, prefix = 0;
        try
        {
            int bytes = ImmGetCompositionStringW(context, 8, 0, 0);
            int cursor = ImmGetCompositionStringW(context, 128, 0, 0);
            _prefixStage = 12;
            if (bytes <= 0 || bytes > maxUnits * 2 || cursor < 0 || cursor > bytes / 2)
                return null;
            pattern = TextPattern();
            _prefixStage = 13;
            if (pattern == 0 || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pattern, 7))(pattern, &document) < 0
                || document == 0 || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pattern, 5))(pattern, &selections) < 0
                || selections == 0)
                return null;
            int count = 0;
            _prefixStage = 14;
            if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(selections, 3))(selections, &count) < 0 || count != 1
                || ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(selections, 4))(selections, 0, &caret) < 0
                || caret == 0)
                return null;
            int comparison = 0;
            _prefixStage = 15;
            if (((delegate* unmanaged[Stdcall]<nint, int, nint, int, int*, int>)Slot(caret, 5))(caret, 0, caret, 1, &comparison) < 0
                || comparison != 0)
                return null; // Require an actual collapsed caret.
            _prefixStage = 16;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(document, 3))(document, &prefix) < 0 || prefix == 0
                || ((delegate* unmanaged[Stdcall]<nint, int, nint, int, int>)Slot(prefix, 15))(prefix, 1, caret, 0) < 0)
                return null;
            int moved = 0;
            _prefixStage = 17;
            if (cursor > 0 && (((delegate* unmanaged[Stdcall]<nint, int, int, int, int*, int>)Slot(prefix, 14))
                (prefix, 1, 0, -cursor, &moved) < 0 || moved != -cursor))
                return null;
            // IMM reports a UTF-16 cursor count. Character-range movement can
            // exclude extra committed graphemes; never use it as a write offset.
            _prefixStage = 18;
            if (ImmGetCompositionStringW(context, 8, 0, 0) != bytes
                || ImmGetCompositionStringW(context, 128, 0, 0) != cursor)
                return null;
            string? text = RangeText(prefix, maxUnits + 1);
            _prefixStage = text is not null && text.Length <= maxUnits ? 0 : 19;
            return text is not null && text.Length <= maxUnits ? text : null;
        }
        finally
        {
            if (prefix != 0)
                Marshal.Release(prefix);
            if (caret != 0)
                Marshal.Release(caret);
            if (selections != 0)
                Marshal.Release(selections);
            if (document != 0)
                Marshal.Release(document);
            if (pattern != 0)
                Marshal.Release(pattern);
            ImmReleaseContext(gui.Focus, context);
        }
    }
    private void TracePrefixStage()
    {
        string? path = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
        if (string.IsNullOrWhiteSpace(path) || _lastPrefixStage == _prefixStage)
            return;
        _lastPrefixStage = _prefixStage;
        try
        {
            System.IO.File.AppendAllText(path + ".prefix.jsonl", "{\"atMs\":" + Environment.TickCount64
            + ",\"stage\":" + _prefixStage + "}" + Environment.NewLine);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }
    private readonly Dictionary<string, int> _imeDiagnostics = new();
    private ImeState NativeComposition(nint root)
    {
        uint thread = GetWindowThreadProcessId(root, out _);
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref gui) || gui.Focus == 0
            || GetAncestor(gui.Focus, 2) != root)
        {
            TraceIme("imm-focus", 0);
            return ImeState.Unknown;
        }
        nint context = ImmGetContext(gui.Focus);
        if (context == 0)
        {
            TraceIme("imm-context", 0);
            return ImeState.Unknown;
        }
        try
        {
            // Query lengths only, without retrieving IME candidate text.
            int composition = ImmGetCompositionStringW(context, 8, 0, 0);
            TraceIme("imm-composition-bytes", composition);
            return composition < 0 ? ImeState.Unknown : composition == 0 ? ImeState.Clear : ImeState.Composing;
        }
        finally { ImmReleaseContext(gui.Focus, context); }
    }
    private void TraceIme(string stage, int value)
    {
        string? path = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
        if (string.IsNullOrWhiteSpace(path) || _imeDiagnostics.TryGetValue(stage, out int previous) && previous == value)
            return;
        _imeDiagnostics[stage] = value;
        try
        {
            System.IO.File.AppendAllText(path + ".imm.jsonl", "{\"atMs\":" + Environment.TickCount64
            + ",\"stage\":\"" + stage + "\",\"value\":" + value + "}" + Environment.NewLine);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }
    public RectPx? CaretBounds(nint root)
    {
        uint thread = GetWindowThreadProcessId(root, out _);
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (thread != 0 && GetGUIThreadInfo(thread, ref gui) && gui.Caret != 0
            && GetAncestor(gui.Caret, 2) == root && gui.Bottom > gui.Top)
        {
            var point = new NativePoint { X = gui.Left, Y = gui.Top };
            if (ClientToScreen(gui.Caret, ref point))
                return new RectPx(point.X, point.Y, Math.Max(1, gui.Right - gui.Left), gui.Bottom - gui.Top);
        }
        // XAML editors may draw their own caret instead of registering a Win32
        // system caret. Query the focused text provider's line geometry instead.
        // No text is read and no selection or document is changed.
        nint pattern = 0, text2 = 0, caret = 0, selections = 0, line = 0;
        try
        {
            pattern = TextPattern();
            if (pattern == 0)
                return null;
            Guid iid = Text2Iid;
            int active = 0;
            if (Marshal.QueryInterface(pattern, in iid, out text2) >= 0 && text2 != 0)
            {
                int hr = ((delegate* unmanaged[Stdcall]<nint, int*, nint*, int>)Slot(text2, 10))(text2, &active, &caret);
                if (hr < 0 || active == 0)
                {
                    if (caret != 0)
                        Marshal.Release(caret);
                    caret = 0;
                }
            }
            if (caret == 0)
            {
                int count = 0;
                if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pattern, 5))(pattern, &selections) < 0
                    || selections == 0
                    || ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(selections, 3))(selections, &count) < 0
                    || count != 1
                    || ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(selections, 4))(selections, 0, &caret) < 0
                    || caret == 0)
                    return null;
            }
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(caret, 3))(caret, &line) < 0 || line == 0
                || ((delegate* unmanaged[Stdcall]<nint, int, nint, int, int>)Slot(line, 15))(line, 0, line, 1) < 0
                || ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(line, 6))(line, 3) < 0)
                return null;
            return RangeBounds(line);
        }
        finally
        {
            if (line != 0)
                Marshal.Release(line);
            if (caret != 0)
                Marshal.Release(caret);
            if (selections != 0)
                Marshal.Release(selections);
            if (text2 != 0)
                Marshal.Release(text2);
            if (pattern != 0)
                Marshal.Release(pattern);
        }
    }
    private static RectPx? RangeBounds(nint range)
    {
        nint array = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(range, 10))(range, &array) < 0 || array == 0)
            return null;
        try
        {
            if (SafeArrayGetDim(array) != 1 || SafeArrayGetLBound(array, 1, out int lo) < 0
                || SafeArrayGetUBound(array, 1, out int hi) < 0 || hi - lo + 1 < 4
                || (hi - lo + 1) % 4 != 0 || hi - lo > 4096
                || SafeArrayAccessData(array, out nint data) < 0)
                return null;
            try
            {
                double* rect = (double*)data;
                for (int i = 0; i < hi - lo + 1; i += 4)
                {
                    var bounds = new RectPx(rect[i], rect[i + 1], rect[i + 2], rect[i + 3]);
                    if (bounds.IsValid)
                        return bounds;
                }
                return null;
            }
            finally { SafeArrayUnaccessData(array); }
        }
        finally { SafeArrayDestroy(array); }
    }
    public (string Document, string Prefix, string Selection)? ReadText(int maxUnits)
    {
        nint pattern = 0, document = 0, selections = 0, selected = 0, prefix = 0;
        try
        {
            pattern = TextPattern();
            if (pattern == 0)
                return null;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pattern, 7))(pattern, &document) < 0
                || document == 0)
                return null;
            string? all = RangeText(document, maxUnits + 1);
            if (all is null || all.Length > maxUnits)
                return null;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pattern, 5))(pattern, &selections) < 0
                || selections == 0)
                return null;
            int length = 0;
            if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(selections, 3))(selections, &length) < 0
                || length != 1)
                return null;
            if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(selections, 4))(selections, 0, &selected) < 0
                || selected == 0)
                return null;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(document, 3))(document, &prefix) < 0
                || prefix == 0)
                return null;
            if (((delegate* unmanaged[Stdcall]<nint, int, nint, int, int>)Slot(prefix, 15))(prefix, 1, selected, 0) < 0)
                return null;
            string? before = RangeText(prefix, maxUnits + 1);
            string? chosen = RangeText(selected, maxUnits + 1);
            return before is null || chosen is null || before.Length > maxUnits || chosen.Length > maxUnits
                ? null : (all, before, chosen);
        }
        finally
        {
            if (prefix != 0)
                Marshal.Release(prefix);
            if (selected != 0)
                Marshal.Release(selected);
            if (selections != 0)
                Marshal.Release(selections);
            if (document != 0)
                Marshal.Release(document);
            if (pattern != 0)
                Marshal.Release(pattern);
        }
    }
    public bool PlaceCaret(string source, int offset, int maxUnits)
    {
        nint pattern = 0, document = 0, range = 0;
        try
        {
            pattern = TextPattern();
            if (pattern == 0 || offset < 0 || offset > source.Length)
                return false;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pattern, 7))(pattern, &document) < 0
                || document == 0)
                return false;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(document, 3))(document, &range) < 0
                || range == 0)
                return false;
            if (((delegate* unmanaged[Stdcall]<nint, int, nint, int, int>)Slot(range, 15))(range, 1, range, 0) < 0)
                return false;
            string desired = TextMapping.Canonical(source[..offset]);
            for (int i = 0; i < 24; i++)
            {
                string? read = RangeText(range, maxUnits + 1);
                if (read is null || read.Length > maxUnits)
                    return false;
                string actual = TextMapping.Canonical(read);
                if (actual == desired)
                {
                    return ((delegate* unmanaged[Stdcall]<nint, int, nint, int, int>)Slot(range, 15))(range, 0, range, 1) >= 0
                        && ((delegate* unmanaged[Stdcall]<nint, int>)Slot(range, 16))(range) >= 0;
                }
                int delta = desired.Length - actual.Length;
                if (delta == 0)
                    return false;
                int moved = 0;
                int count = Math.Sign(delta) * Math.Max(1, Math.Abs(delta) / (i < 2 ? 1 : 2));
                if (((delegate* unmanaged[Stdcall]<nint, int, int, int, int*, int>)Slot(range, 14))
                    (range, 1, 0, count, &moved) < 0 || moved == 0)
                    return false;
            }
            return false;
        }
        finally
        {
            if (range != 0)
                Marshal.Release(range);
            if (document != 0)
                Marshal.Release(document);
            if (pattern != 0)
                Marshal.Release(pattern);
        }
    }
    private nint TextPattern()
    {
        if (_element == 0)
            return 0;
        nint pattern = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(_element, 16))(_element, 10014, &pattern);
        if (hr < 0 || pattern == 0)
            return 0;
        try
        {
            Guid iid = TextIid;
            return Marshal.QueryInterface(pattern, in iid, out nint text) >= 0 ? text : 0;
        }
        finally { Marshal.Release(pattern); }
    }
    private static string? RangeText(nint range, int maxUnits)
    {
        nint value = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(range, 12))(range, maxUnits, &value);
        if (hr < 0)
        {
            if (value != 0)
                Marshal.FreeBSTR(value);
            return null;
        }
        try
        {
            return value == 0 ? "" : Marshal.PtrToStringBSTR(value);
        }
        finally { if (value != 0) Marshal.FreeBSTR(value); }
    }
    public void ClearElement() => ReleaseElement();
    private void ReleaseElement()
    {
        if (_element != 0)
        {
            Marshal.Release(_element);
            _element = 0;
        }
    }
    public void Dispose()
    {
        ReleaseElement();
        if (_automation != 0)
        {
            Marshal.Release(_automation);
            _automation = 0;
        }
        if (_comInitialized)
        {
            CoUninitialize();
            _comInitialized = false;
        }
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coinit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint result);
    [DllImport("oleaut32.dll")] private static extern uint SafeArrayGetDim(nint array);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayGetLBound(nint array, uint dim, out int bound);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayGetUBound(nint array, uint dim, out int bound);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayAccessData(nint array, out nint data);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayUnaccessData(nint array);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayDestroy(nint array);
    [DllImport("oleaut32.dll")] private static extern int VariantClear(nint value);
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size; public uint Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint hwnd);
    [DllImport("imm32.dll")] private static extern int ImmGetCompositionStringW(nint context, uint index, nint buffer, uint bytes);
    [DllImport("imm32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ImmReleaseContext(nint hwnd, nint context);
}
