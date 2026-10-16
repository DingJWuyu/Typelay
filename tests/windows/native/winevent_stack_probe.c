// One-shot test DLL: observe real stock Edit API-stack events, not production
// transactions or retirement. No text reads, event synthesis or control subclass.
#include <stdint.h>
#include <stddef.h>

typedef void *HANDLE;
typedef uint16_t WCHAR;
typedef intptr_t (*WNDPROC)(HANDLE, uint32_t, uintptr_t, intptr_t);
typedef void (*WINEVENTPROC)(HANDLE, uint32_t, HANDLE, int32_t, int32_t, uint32_t, uint32_t);
typedef struct {
    uint32_t style; WNDPROC procedure; int class_extra, window_extra;
    HANDLE instance, icon, cursor, background; const WCHAR *menu, *name;
} WindowClass;
#define API __declspec(dllimport)
API uint32_t GetCurrentThreadId(void);
API uint32_t GetCurrentProcessId(void);
API uint32_t GetWindowThreadProcessId(HANDLE, uint32_t *);
API HANDLE GetAncestor(HANDLE, uint32_t);
API HANDLE GetForegroundWindow(void);
API HANDLE GetFocus(void);
API int IsWindowVisible(HANDLE);
API int IsWindowUnicode(HANDLE);
API int IsWindowEnabled(HANDLE);
API intptr_t GetWindowLongPtrW(HANDLE, int);
API intptr_t GetClassLongPtrW(HANDLE, int);
API int GetClassNameW(HANDLE, WCHAR *, int);
API int GetClassInfoW(HANDLE, const WCHAR *, WindowClass *);
API intptr_t CallWindowProcW(WNDPROC, HANDLE, uint32_t, uintptr_t, intptr_t);
API HANDLE SetWinEventHook(uint32_t, uint32_t, HANDLE, WINEVENTPROC, uint32_t, uint32_t, uint32_t);
API int UnhookWinEvent(HANDLE);

enum { LocationChange = 0x800b, TextSelectionChanged = 0x8014, InContext = 4 };
typedef struct {
    uint32_t hooks_installed, hooks_removed, setsel_calls;
    int32_t initial_start, initial_end, final_start, final_end;
    uint32_t caret_location_events, caret_location_inside;
    uint32_t text_selection_events, text_selection_inside;
} Observation;
_Static_assert(sizeof(Observation) == 44, "Managed observation layout");
_Static_assert(sizeof(L"") == sizeof(WCHAR), "Windows UTF-16 literals");

static HANDLE module, source;
static uint32_t process_id, thread_id;
static int used;
static volatile int inside_setsel;
static volatile Observation counts;

static void increment(volatile uint32_t *value) {
    if (*value != UINT32_MAX) ++*value;
}

static void on_event(HANDLE hook, uint32_t event, HANDLE hwnd, int32_t object,
                     int32_t child, uint32_t event_thread, uint32_t event_time) {
    (void)hook; (void)event_time;
    // The hook is already restricted to this process/thread. Check callback
    // execution as well: queued/out-of-context delivery cannot count as inside.
    if (GetCurrentProcessId() != process_id || GetCurrentThreadId() != thread_id
        || event_thread != thread_id || hwnd != source || child != 0) return;
    if (event == LocationChange && object == -8) { // OBJID_CARET
        increment(&counts.caret_location_events);
        if (inside_setsel) increment(&counts.caret_location_inside);
    } else if (event == TextSelectionChanged && object == -4) { // OBJID_CLIENT
        increment(&counts.text_selection_events);
        if (inside_setsel) increment(&counts.text_selection_inside);
    }
}

// Status: 0 completed; 1 owner/repeated invocation; 2 non-stock Edit;
// 3 visible/foreground/focus precondition; 4 initial selection;
// 5/6 hook registration; 7 resulting selection; 8 focus changed; 9 unhook.
__declspec(dllexport) uint32_t ObserveStockEditSelection(HANDLE root, HANDLE edit, Observation *out) {
    if (!out || used) return 1;
    used = 1;
    process_id = GetCurrentProcessId(); thread_id = GetCurrentThreadId(); source = edit;
    uint32_t owner = 0;
    if (!module || !edit || GetWindowThreadProcessId(edit, &owner) != thread_id
        || owner != process_id || GetAncestor(edit, 2) != root) return 1;
    WCHAR name[5];
    WindowClass stock;
    if (!IsWindowUnicode(edit) || !IsWindowEnabled(edit)
        || GetClassNameW(edit, name, 5) != 4
        || name[0] != 'E' || name[1] != 'd' || name[2] != 'i' || name[3] != 't'
        || !GetClassInfoW(0, (const WCHAR *)L"Edit", &stock) || !stock.procedure
        || GetWindowLongPtrW(edit, -4) != (intptr_t)stock.procedure
        || GetClassLongPtrW(edit, -24) != (intptr_t)stock.procedure
        || (GetWindowLongPtrW(edit, -16) & (4 | 0x20 | 0x800))) return 2;
    if (!IsWindowVisible(edit) || GetForegroundWindow() != root || GetFocus() != edit) return 3;
    CallWindowProcW(stock.procedure, edit, 0xb0,
        (uintptr_t)&out->initial_start, (intptr_t)&out->initial_end);
    if (out->initial_start != 0 || out->initial_end != 0) return 4;

    // Exact event IDs, own PID/TID, native DLL callback. No SKIPOWN flags.
    HANDLE location = SetWinEventHook(LocationChange, LocationChange, module, on_event,
        process_id, thread_id, InContext);
    if (!location) return 5;
    out->hooks_installed++;
    HANDLE selection = SetWinEventHook(TextSelectionChanged, TextSelectionChanged, module, on_event,
        process_id, thread_id, InContext);
    uint32_t status = 6;
    if (selection) {
        out->hooks_installed++;
        out->setsel_calls++;
        // This is the sole observed call. No pump, callback invocation, fault
        // injection or other API is inserted between the marker and stock Edit.
        inside_setsel = 1;
        CallWindowProcW(stock.procedure, edit, 0xb1, 4, 7);
        inside_setsel = 0;
        CallWindowProcW(stock.procedure, edit, 0xb0,
            (uintptr_t)&out->final_start, (intptr_t)&out->final_end);
        // EM_SETSEL has no return value; confirm the requested result instead.
        status = out->final_start == 4 && out->final_end == 7 ? 0 : 7;
        if (!status && (GetForegroundWindow() != root || GetFocus() != edit)) status = 8;
        if (UnhookWinEvent(selection)) out->hooks_removed++;
        else status = 9;
    }
    if (UnhookWinEvent(location)) out->hooks_removed++;
    else status = 9;
    out->caret_location_events = counts.caret_location_events;
    out->caret_location_inside = counts.caret_location_inside;
    out->text_selection_events = counts.text_selection_events;
    out->text_selection_inside = counts.text_selection_inside;
    return status;
}

int DllMain(HANDLE instance, uint32_t reason, void *reserved) {
    (void)reserved;
    if (reason == 1) module = instance;
    return 1;
}
