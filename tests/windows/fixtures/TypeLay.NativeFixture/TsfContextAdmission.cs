using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using N = TypeLay.Platform.Windows.NativeMethods;

// Stage one only: query the existing TSF objects of this fixture's own stock Edit.
// No activation, context creation, sink, message-pump replacement or edit session.
[SupportedOSPlatform("windows")]
internal static partial class TsfContextAdmission
{
    internal const uint ObserveMessage = 0x8090;

    internal static int RunSta(Func<int> run)
    {
        int result = 2;
        var thread = new Thread(() =>
        {
            int initialized = CoInitializeEx(0, 2); // COINIT_APARTMENTTHREADED
            if (initialized < 0)
                return;
            try
            {
                result = run();
            }
            catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); }
            finally { CoUninitialize(); } // Balance S_OK and S_FALSE on this thread.
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // The host timer allows normal teardown. A stalled COM call must not keep
        // this diagnostic process alive; the external owner also proves exit.
        return thread.Join(TimeSpan.FromSeconds(29)) ? result : 2;
    }

    internal static Result Observe(nint root, nint field, Func<int> focusVersion)
    {
        var result = new Result
        {
            Process = Environment.ProcessId,
            Thread = GetCurrentThreadId(),
            ExpectedField = (long)field,
            FocusVersionBefore = focusVersion(),
            Apartment = Thread.CurrentThread.GetApartmentState().ToString()
        };
        ITfThreadMgr? manager = null;
        ITfDocumentMgr? document = null, recheckedDocument = null;
        ITfContext? context = null, recheckedContext = null;
        ITfContextView? view = null;
        try
        {
            if (!ExactNativeFocus(root, field, result.Thread))
                return Denied("native_focus_mismatch");
            result.Stage = "get_existing_thread_manager";
            result.HResult = TF_GetThreadMgr(out manager);
            result.ManagerPresent = manager is not null;
            if (result.HResult != 0 || manager is null)
                return Denied(manager is null && result.HResult == 0 ? "no_existing_thread_manager" : "thread_manager_failed");
            result.Stage = "get_focus_document";
            result.HResult = manager.GetFocus(out document);
            result.DocumentPresent = document is not null;
            if (result.HResult != 0 || document is null)
                return Denied("no_focus_document");
            result.Stage = "get_top_context";
            result.HResult = document.GetTop(out context);
            result.ContextPresent = context is not null;
            if (result.HResult != 0 || context is null)
                return Denied("no_top_context");
            result.Stage = "get_active_view";
            result.HResult = context.GetActiveView(out view);
            if (result.HResult != 0 || view is null)
                return Denied("no_active_view");
            result.Stage = "get_view_window";
            result.HResult = view.GetWnd(out nint window);
            result.ViewWindow = (long)window;
            if (result.HResult != 0 || window == 0 || window != field)
                return Denied("view_window_not_exact_field");
            result.Stage = "recheck_focus_context";
            result.HResult = manager.GetFocus(out recheckedDocument);
            if (result.HResult != 0 || recheckedDocument is null || !SameIdentity(document, recheckedDocument))
                return Denied("focus_document_changed");
            result.HResult = recheckedDocument.GetTop(out recheckedContext);
            if (result.HResult != 0 || recheckedContext is null || !SameIdentity(context, recheckedContext))
                return Denied("top_context_changed");
            result.SameContextOnRecheck = true;
            if (!ExactNativeFocus(root, field, result.Thread) || focusVersion() != result.FocusVersionBefore)
                return Denied("native_focus_changed");
            result.Stage = "exact_existing_context_observed";
            return result;
        }
        catch (Exception error)
        {
            result.HResult = error.HResult;
            return Denied("observation_exception");
        }
        finally
        {
            // Release once for each returned RCW reference, not FinalRelease:
            // a recheck can return the same COM identity as the first query.
            result.OwnedReferencesReleased = true;
            Release(recheckedContext);
            Release(recheckedDocument);
            Release(view);
            Release(context);
            Release(document);
            Release(manager);
            result.FocusVersionAfter = focusVersion();
        }

        Result Denied(string reason)
        {
            result.Denial = reason;
            return result;
        }
        void Release(object? value)
        {
            if (value is null)
                return;
            try
            {
                Marshal.ReleaseComObject(value);
            }
            catch { result.OwnedReferencesReleased = false; }
        }
    }

    private static bool ExactNativeFocus(nint root, nint field, uint thread) =>
        GetCurrentThreadId() == thread && N.IsWindow(root) && N.IsWindow(field)
        && N.GetForegroundWindow() == root && GetFocus() == field && N.GetAncestor(field, 2) == root
        && N.GetWindowThreadProcessId(field, out uint process) == thread && process == Environment.ProcessId;

    private static bool SameIdentity(object first, object second)
    {
        nint a = 0, b = 0;
        try
        {
            a = Marshal.GetIUnknownForObject(first);
            b = Marshal.GetIUnknownForObject(second);
            return a != 0 && a == b;
        }
        finally
        {
            if (b != 0)
                Marshal.Release(b);
            if (a != 0)
                Marshal.Release(a);
        }
    }

    internal sealed class Result
    {
        public bool ProductionReadiness => false;
        public string Stage { get; set; } = "native_focus_check";
        public string? Denial
        {
            get; set;
        }
        public int? HResult
        {
            get; set;
        }
        public int Process
        {
            get; set;
        }
        public uint Thread
        {
            get; set;
        }
        public string Apartment { get; set; } = "Unknown";
        public long ExpectedField
        {
            get; set;
        }
        public long ViewWindow
        {
            get; set;
        }
        public bool ManagerPresent
        {
            get; set;
        }
        public bool DocumentPresent
        {
            get; set;
        }
        public bool ContextPresent
        {
            get; set;
        }
        public bool SameContextOnRecheck
        {
            get; set;
        }
        public bool OwnedReferencesReleased
        {
            get; set;
        }
        public int FocusVersionBefore
        {
            get; set;
        }
        public int FocusVersionAfter
        {
            get; set;
        }
    }

    // SDK vtable order through the last method used here. Unused slots are
    // declared with their native pointer shapes; this probe never calls them.
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/msctf.h
    [ComImport, Guid("aa80e801-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfThreadMgr
    {
        [PreserveSig] int Activate(out uint client);
        [PreserveSig] int Deactivate();
        [PreserveSig] int CreateDocumentMgr(out nint document);
        [PreserveSig] int EnumDocumentMgrs(out nint documents);
        [PreserveSig] int GetFocus(out ITfDocumentMgr? document);
    }
    [ComImport, Guid("aa80e7f4-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfDocumentMgr
    {
        [PreserveSig] int CreateContext(uint client, uint flags, nint store, out nint context, out uint cookie);
        [PreserveSig] int Push(nint context);
        [PreserveSig] int Pop(uint flags);
        [PreserveSig] int GetTop(out ITfContext? context);
    }
    [ComImport, Guid("aa80e7fd-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfContext
    {
        [PreserveSig] int RequestEditSession(uint client, ITsfProbeEditSession session, uint flags, out int result);
        [PreserveSig] int InWriteSession(uint client, out int writing);
        [PreserveSig] int GetSelection(uint cookie, uint index, uint count, out Selection selection, out uint fetched);
        [PreserveSig] int SetSelection(uint cookie, uint count, nint selection);
        [PreserveSig] int GetStart(uint cookie, out nint range);
        [PreserveSig] int GetEnd(uint cookie, out nint range);
        [PreserveSig] int GetActiveView(out ITfContextView? view);
    }
    [ComImport, Guid("2433bf8e-0f9b-435c-ba2c-180611978c30"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfContextView
    {
        [PreserveSig] int GetRangeFromPoint(uint cookie, nint point, uint flags, out nint range);
        [PreserveSig] int GetTextExt(uint cookie, nint range, nint rectangle, out int clipped);
        [PreserveSig] int GetScreenExt(nint rectangle);
        [PreserveSig] int GetWnd(out nint window);
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("msctf.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int TF_GetThreadMgr(out ITfThreadMgr? manager);
}
