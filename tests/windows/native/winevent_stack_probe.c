// One-shot fixture-only WinEvent probes. No text reads, event synthesis, direct
// transaction execution or control subclass; never shipped in the product.
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
typedef intptr_t (*HOOKPROC)(int, uintptr_t, intptr_t);
API HANDLE SetWindowsHookExW(int, HOOKPROC, HANDLE, uint32_t);
API intptr_t CallNextHookEx(HANDLE, int, uintptr_t, intptr_t);
API int UnhookWindowsHookEx(HANDLE);
API uint32_t RegisterWindowMessageW(const WCHAR *);
API HANDLE GetPropW(HANDLE, const WCHAR *);
API HANDLE RemovePropW(HANDLE, const WCHAR *);
API HANDLE OpenFileMappingW(uint32_t, int, const WCHAR *);
API void *MapViewOfFile(HANDLE, uint32_t, uint32_t, uint32_t, size_t);
API int UnmapViewOfFile(const void *);
API int CloseHandle(HANDLE);

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

// Only the wire header is mapped, read-only. The fixture never examines the
// mapping's source/replacement/observed body buffers or changes its result.
typedef struct {
    uint32_t version, status, reason, op, pid, thread;
    uint64_t hwnd, root, cookie, deadline;
    int32_t length, start, end, replacement_length;
    int32_t selection_start, selection_end, multiline, ime;
    int32_t expected_selection_start, expected_selection_end;
    int64_t authorization_deadline_ms;
} WireHeader;
_Static_assert(sizeof(WireHeader) == 104, "Wire header layout");
typedef struct { intptr_t lparam; uintptr_t wparam; uint32_t message; HANDLE hwnd; } CallMessage;
typedef int (*OwnedControl)(HANDLE, uint32_t, uint64_t);
typedef struct {
    uint32_t hooks_installed, hooks_removed, transaction_calls, matching_callbacks, retire_calls;
    int32_t retire_result, initial_start, initial_end, callback_start, callback_end;
    uint32_t mapping_unmapped, mapping_closed;
} RetirementObservation;
_Static_assert(sizeof(RetirementObservation) == 48, "Managed retirement layout");

static const WCHAR lifetime[] = L"TypeLay.NativeInput.Lifetime.v2";
static HANDLE transaction_hook, retirement_event_hook, mapping;
static volatile const WireHeader *pending;
static OwnedControl retire;
static WNDPROC edit_procedure;
static HANDLE retirement_root;
static uint32_t transaction_message;
static uint64_t first_nonce, second_nonce, current_cookie;
static volatile int inside_transaction;
static RetirementObservation retirement;

static int pending_request(void) {
    return pending && pending->version == 2 && pending->status == 0 && pending->op == 5
        && pending->pid == process_id && pending->thread == thread_id
        && pending->hwnd == (uint64_t)(uintptr_t)source
        && pending->root == (uint64_t)(uintptr_t)retirement_root
        && pending->cookie == current_cookie && pending->length == 12
        && pending->start == 4 && pending->end == 7 && pending->replacement_length == 5
        && pending->expected_selection_start == 0 && pending->expected_selection_end == 0;
}

static void on_retirement_event(HANDLE hook, uint32_t event, HANDLE hwnd, int32_t object,
                                int32_t child, uint32_t event_thread, uint32_t event_time) {
    (void)hook; (void)event_time;
    if (!inside_transaction || event != LocationChange || object != -8 || child != 0
        || hwnd != source || event_thread != thread_id || GetCurrentThreadId() != thread_id
        || GetCurrentProcessId() != process_id || !pending_request()) return;
    int32_t start = -1, end = -1;
    CallWindowProcW(edit_procedure, source, 0xb0, (uintptr_t)&start, (intptr_t)&end);
    if (start != 4 || end != 7) return;
    increment(&retirement.matching_callbacks);
    retirement.callback_start = start; retirement.callback_end = end;
    // Count every matching callback, but never repeat the state-changing action.
    if (!retirement.retire_calls && (uint64_t)(uintptr_t)GetPropW(source, lifetime) == current_cookie) {
        retirement.retire_calls++;
        retirement.retire_result = retire(source, 3, 0);
    }
}

static intptr_t bracket_transaction(int code, uintptr_t wp, intptr_t lp) {
    if (code >= 0 && lp && GetCurrentThreadId() == thread_id && GetCurrentProcessId() == process_id) {
        const CallMessage *message = (const CallMessage *)lp;
        if (message->hwnd == source && message->message == transaction_message
            && (uint64_t)message->wparam == first_nonce && (uint64_t)message->lparam == second_nonce
            && pending_request() && (uint64_t)(uintptr_t)GetPropW(source, lifetime) == current_cookie) {
            increment(&retirement.transaction_calls);
            // Installed after production capture: this hook is at the head of
            // the same GUI-thread chain. The real downstream hook owns execute.
            inside_transaction = 1;
            intptr_t result = CallNextHookEx(0, code, wp, lp);
            inside_transaction = 0;
            return result;
        }
    }
    return CallNextHookEx(0, code, wp, lp);
}

// Status 0 armed; 1 owner/repeated invocation; 2 non-stock Edit; 3 focus;
// 4 initial selection; 5 identity/header; 6 mapping; 7/8 hook installation.
__declspec(dllexport) uint32_t ArmOwnedRetirement(HANDLE root, HANDLE edit,
    uint64_t first, uint64_t second, OwnedControl control) {
    if (used || !module || !control) return 1;
    used = 1;
    process_id = GetCurrentProcessId(); thread_id = GetCurrentThreadId(); source = edit;
    retirement_root = root; first_nonce = first; second_nonce = second; retire = control;
    uint32_t owner = 0;
    if (!edit || GetWindowThreadProcessId(edit, &owner) != thread_id
        || owner != process_id || GetAncestor(edit, 2) != root) return 1;
    WindowClass stock;
    WCHAR name[5];
    if (!IsWindowUnicode(edit) || !IsWindowEnabled(edit) || GetClassNameW(edit, name, 5) != 4
        || name[0] != 'E' || name[1] != 'd' || name[2] != 'i' || name[3] != 't'
        || !GetClassInfoW(0, (const WCHAR *)L"Edit", &stock) || !stock.procedure
        || GetWindowLongPtrW(edit, -4) != (intptr_t)stock.procedure
        || GetClassLongPtrW(edit, -24) != (intptr_t)stock.procedure
        || (GetWindowLongPtrW(edit, -16) & (4 | 0x20 | 0x800))) return 2;
    edit_procedure = stock.procedure;
    if (!IsWindowVisible(edit) || GetForegroundWindow() != root || GetFocus() != edit) return 3;
    CallWindowProcW(edit_procedure, edit, 0xb0,
        (uintptr_t)&retirement.initial_start, (intptr_t)&retirement.initial_end);
    if (retirement.initial_start != 0 || retirement.initial_end != 0) return 4;
    current_cookie = (uint64_t)(uintptr_t)GetPropW(edit, lifetime);
    transaction_message = RegisterWindowMessageW((const WCHAR *)L"TypeLay.NativeInput.Transaction.v2");
    if (!current_cookie || !transaction_message) return 5;
    WCHAR mapping_name[80];
    const WCHAR prefix[] = L"Local\\TypeLay.Input.";
    const WCHAR digits[] = L"0123456789abcdef";
    int i = 0;
    for (; prefix[i]; i++) mapping_name[i] = prefix[i];
    for (int part = 0; part < 2; part++) {
        uint64_t bits = part ? second : first;
        for (int shift = 60; shift >= 0; shift -= 4) mapping_name[i++] = digits[(bits >> shift) & 15];
    }
    mapping_name[i] = 0;
    mapping = OpenFileMappingW(4, 0, mapping_name);
    if (!mapping) return 6;
    pending = MapViewOfFile(mapping, 4, 0, 0, sizeof(WireHeader));
    if (!pending) return 6;
    if (!pending_request()) return 5;
    retirement_event_hook = SetWinEventHook(LocationChange, LocationChange, module, on_retirement_event,
        process_id, thread_id, InContext);
    if (!retirement_event_hook) return 7;
    retirement.hooks_installed++;
    transaction_hook = SetWindowsHookExW(4, bracket_transaction, module, thread_id); // WH_CALLWNDPROC
    if (!transaction_hook) return 8;
    retirement.hooks_installed++;
    return 0;
}

// Called explicitly on the owner thread after the real cross-process Send has
// returned, also on setup failure. Every acquired resource is attempted once.
__declspec(dllexport) uint32_t FinishOwnedRetirement(RetirementObservation *out) {
    if (!out || GetCurrentThreadId() != thread_id || inside_transaction) return 1;
    uint32_t status = 0;
    if (transaction_hook) {
        if (UnhookWindowsHookEx(transaction_hook)) retirement.hooks_removed++;
        else status = 2;
        transaction_hook = 0;
    }
    if (retirement_event_hook) {
        if (UnhookWinEvent(retirement_event_hook)) retirement.hooks_removed++;
        else status = 2;
        retirement_event_hook = 0;
    }
    if (pending) {
        retirement.mapping_unmapped = UnmapViewOfFile((const void *)pending) != 0;
        if (!retirement.mapping_unmapped) status = 3;
        pending = 0;
    }
    if (mapping) {
        retirement.mapping_closed = CloseHandle(mapping) != 0;
        if (!retirement.mapping_closed) status = 3;
        mapping = 0;
    }
    *out = retirement;
    return status;
}

// Ordinary source prototype regression: observe the actual stock notification
// stack after the shared claim, not a synthetic EM_SETSEL dispatch/subclass.
typedef struct {
    uint32_t version, bytes, state, operation;
    uint64_t generation[2], grant[2];
    int64_t observed;
    uint64_t acknowledgement;
    uint32_t claim, reserved;
    WireHeader tx;
} SourceClaimHeader;
_Static_assert(offsetof(SourceClaimHeader, tx) == 72, "Source claim header");
typedef struct { uint32_t installed, removed, callbacks, action, claim, event; } ClaimObservation;
static volatile const SourceClaimHeader *claim_header;
static HANDLE claim_mapping, claim_hook;
static uint32_t claim_mode;
static ClaimObservation claim_observation;
static void on_source_claim(HANDLE hook, uint32_t event, HANDLE hwnd, int32_t object,
    int32_t child, uint32_t event_thread, uint32_t event_time) {
    (void)hook; (void)event_time;
    if (!claim_header || claim_observation.action || hwnd != source || child != 0
        || event_thread != thread_id || GetCurrentThreadId() != thread_id
        || GetCurrentProcessId() != process_id || claim_header->state != 1
        || claim_header->claim != 1 || claim_header->tx.status != 0) return;
    if (claim_mode == 1) {
        if (event != LocationChange || object != -8) return;
        int32_t start = -1, end = -1;
        CallWindowProcW(edit_procedure, source, 0xb0, (uintptr_t)&start, (intptr_t)&end);
        if (start != 4 || end != 7) return;
    } else if (event != 0x800e || object != -4) return; // Real VALUECHANGE notification.
    claim_observation.callbacks++;
    claim_observation.claim = claim_header->claim;
    claim_observation.event = event;
    claim_observation.action = (uint64_t)(uintptr_t)RemovePropW(source, lifetime) == current_cookie ? 1 : 2;
}
__declspec(dllexport) uint32_t ArmSourceClaim(HANDLE root, HANDLE edit, uint64_t nonce, uint32_t mode) {
    if (used || !module || (mode != 1 && mode != 2)) return 1;
    used = 1;
    process_id = GetCurrentProcessId(); thread_id = GetCurrentThreadId(); source = edit;
    uint32_t pid = 0;
    WindowClass stock;
    if (GetWindowThreadProcessId(edit, &pid) != thread_id || pid != process_id
        || GetAncestor(edit, 2) != root || GetForegroundWindow() != root || GetFocus() != edit
        || !GetClassInfoW(0, (const WCHAR *)L"Edit", &stock) || !stock.procedure
        || GetWindowLongPtrW(edit, -4) != (intptr_t)stock.procedure
        || GetClassLongPtrW(edit, -24) != (intptr_t)stock.procedure) return 2;
    edit_procedure = stock.procedure;
    current_cookie = (uint64_t)(uintptr_t)GetPropW(edit, lifetime);
    WCHAR name[64];
    const WCHAR prefix[] = L"Local\\TypeLay.Source.", digits[] = L"0123456789abcdef";
    int i = 0;
    for (; prefix[i]; i++) name[i] = prefix[i];
    for (int shift = 60; shift >= 0; shift -= 4) name[i++] = digits[(nonce >> shift) & 15];
    name[i] = 0;
    claim_mapping = OpenFileMappingW(4, 0, name);
    if (!claim_mapping) return 3;
    claim_header = MapViewOfFile(claim_mapping, 4, 0, 0, 49416);
    if (!claim_header || claim_header->version != 7 || claim_header->bytes != 49416
        || claim_header->state != 1 || claim_header->claim || claim_header->tx.version != 2
        || claim_header->tx.pid != process_id || claim_header->tx.thread != thread_id
        || claim_header->tx.hwnd != (uint64_t)(uintptr_t)edit || claim_header->tx.cookie != current_cookie
        || claim_header->tx.length < 0 || claim_header->tx.length > 8192
        || claim_header->tx.start < 0 || claim_header->tx.end < claim_header->tx.start
        || claim_header->tx.end > claim_header->tx.length
        || claim_header->tx.expected_selection_start < 0
        || claim_header->tx.expected_selection_end < claim_header->tx.expected_selection_start
        || claim_header->tx.expected_selection_end > claim_header->tx.length
        // The caret-location mode observes one exact selection transition.
        // VALUECHANGE mode instead observes the native write for any valid scope.
        || (mode == 1 && (claim_header->tx.start != 4 || claim_header->tx.end != 7
            || claim_header->tx.expected_selection_start || claim_header->tx.expected_selection_end))) return 4;
    // The v7 key lives after the body, but this probe reads only metadata.
    // A valid configured main key is not evidence of origin or permission.
    uint64_t accept_key = *(volatile const uint64_t *)((volatile const uint8_t *)claim_header + 49376);
    if (accept_key != 9 && (accept_key < 0x70 || accept_key > 0x87)) return 4;
    volatile const uint32_t *chord = (volatile const uint32_t *)((volatile const uint8_t *)claim_header + 49384);
    // This sent-route probe is never an observer or a modified ordered grant.
    if (chord[0] != 1 || chord[1]) return 4;
    claim_mode = mode;
    uint32_t event = mode == 1 ? LocationChange : 0x800e;
    claim_hook = SetWinEventHook(event, event, module, on_source_claim, process_id, thread_id, InContext);
    if (!claim_hook) return 5;
    claim_observation.installed = 1;
    return 0;
}
__declspec(dllexport) uint32_t FinishSourceClaim(ClaimObservation *out) {
    if (!out || GetCurrentThreadId() != thread_id) return 1;
    uint32_t status = 0;
    if (claim_hook) {
        if (UnhookWinEvent(claim_hook)) claim_observation.removed++;
        else status = 2;
        claim_hook = 0;
    }
    if (claim_header && !UnmapViewOfFile((const void *)claim_header)) status = 3;
    if (claim_mapping && !CloseHandle(claim_mapping)) status = 3;
    claim_header = 0; claim_mapping = 0;
    *out = claim_observation;
    return status;
}

int DllMain(HANDLE instance, uint32_t reason, void *reserved) {
    (void)reserved;
    if (reason == 1) module = instance;
    return 1;
}
