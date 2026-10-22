// Runs only on a qualified stock Edit control's owning thread. No keyboard
// synthesis, focus changes, UIA calls or retained user text in the target process.
#include <stdint.h>
#include <stddef.h>

typedef void *HANDLE;
typedef uint16_t WCHAR;
typedef intptr_t LPARAM;
typedef uintptr_t WPARAM;
typedef intptr_t LRESULT;
typedef LRESULT (*WNDPROC)(HANDLE, uint32_t, WPARAM, LPARAM);
#define API __declspec(dllimport)
API LRESULT CallNextHookEx(HANDLE, int, WPARAM, LPARAM);
API uint32_t RegisterWindowMessageW(const WCHAR *);
API HANDLE OpenFileMappingW(uint32_t, int, const WCHAR *);
API void *MapViewOfFile(HANDLE, uint32_t, uint32_t, uint32_t, size_t);
API int UnmapViewOfFile(const void *);
API int CloseHandle(HANDLE);
API HANDLE GetProcessHeap(void);
API void *HeapAlloc(HANDLE, uint32_t, size_t);
API int HeapFree(HANDLE, uint32_t, void *);
API uint64_t GetTickCount64(void);
API void QueryInterruptTimePrecise(uint64_t *);
API uint32_t GetCurrentThreadId(void);
API uint32_t GetCurrentProcessId(void);
API uint32_t GetWindowThreadProcessId(HANDLE, uint32_t *);
API HANDLE GetAncestor(HANDLE, uint32_t);
API HANDLE GetParent(HANDLE);
API HANDLE GetForegroundWindow(void);
API HANDLE GetFocus(void);
API HANDLE GetActiveWindow(void);
API uint32_t GetQueueStatus(uint32_t);
API LRESULT SendMessageTimeoutW(HANDLE, uint32_t, WPARAM, LPARAM, uint32_t, uint32_t, uintptr_t *);
API HANDLE GetKeyboardLayout(uint32_t);
API int IsWindowUnicode(HANDLE);
API int IsWindowEnabled(HANDLE);
API int IsWindowVisible(HANDLE);
API int IsIconic(HANDLE);
typedef struct { int32_t x, y; } Point;
typedef struct { int32_t left, top, right, bottom; } Rect;
API int GetClientRect(HANDLE, Rect *);
API int ClientToScreen(HANDLE, Point *);
API intptr_t GetWindowLongPtrW(HANDLE, int);
API intptr_t GetClassLongPtrW(HANDLE, int);
typedef struct {
    uint32_t style; WNDPROC procedure; int class_extra, window_extra;
    HANDLE instance, icon, cursor, background; const WCHAR *menu, *name;
} WindowClass;
API int GetClassInfoW(HANDLE, const WCHAR *, WindowClass *);
API int GetClassNameW(HANDLE, WCHAR *, int);
API HANDLE GetPropW(HANDLE, const WCHAR *);
API int SetPropW(HANDLE, const WCHAR *, HANDLE);
API HANDLE RemovePropW(HANDLE, const WCHAR *);
API LRESULT CallWindowProcW(WNDPROC, HANDLE, uint32_t, WPARAM, LPARAM);
API HANDLE ImmGetContext(HANDLE);
API int32_t ImmGetCompositionStringW(HANDLE, uint32_t, void *, uint32_t);
API int ImmReleaseContext(HANDLE, HANDLE);

#define MAX_UNITS 8192
#define CAPACITY (MAX_UNITS + 1)
#define VERSION 2
// Aligned fixed header, followed by three bounded UTF-16 buffers.
typedef struct {
    uint32_t version, status, reason, op, pid, thread;
    uint64_t hwnd, root, cookie, deadline;
    int32_t length, start, end, replacement_length;
    int32_t selection_start, selection_end, multiline, ime;
    int32_t expected_selection_start, expected_selection_end;
    int64_t authorization_deadline_ms;
    WCHAR expected[CAPACITY], replacement[CAPACITY], observed[CAPACITY];
} Transaction;
_Static_assert(offsetof(Transaction, expected) == 104, "Protocol header layout");
_Static_assert(offsetof(Transaction, authorization_deadline_ms) == 96, "Authorization clock offset");
_Static_assert(sizeof(Transaction) == 49264, "Protocol mapping length");

typedef struct { LPARAM lparam; WPARAM wparam; uint32_t message; HANDLE hwnd; } CallMessage;
static const WCHAR property[] = L"TypeLay.NativeInput.Lifetime.v2";
static const WCHAR focus_property[] = L"TypeLay.OwnedFocusRevision.v1";
// Only the independent practice process opts in. Its GUI thread retains this
// record through retirement and until process exit; a wire capture/release may
// never create, remove or rotate it. The process pins this DLL for that lifetime.
static struct { HANDLE hwnd; uint32_t thread; uint64_t cookie, focus_revision; int retired, automatic; } owned;
static const WCHAR message_name[] = L"TypeLay.NativeInput.Transaction.v2";
static const WCHAR owned_capture_message_name[] = L"TypeLay.NativeInput.OwnedCapture.v2";

__declspec(dllexport) uint32_t TypeLayNativeProtocolVersion(void) { return VERSION; }

static int foreground_source(const Transaction *tx) {
    // This hook executes on the source's GUI thread, so GetFocus identifies the
    // exact field, rather than merely another field inside the same root window.
    return GetForegroundWindow() == (HANDLE)(uintptr_t)tx->root
        && GetFocus() == (HANDLE)(uintptr_t)tx->hwnd;
}
static int authorization_current(const Transaction *tx) {
    // Exactly the App/Broker interrupt-clock domain, including suspend time and
    // integer millisecond flooring. Never turn the private 500ms budget into a
    // renewed acceptance deadline. The API-set import is required on Windows 10+.
    uint64_t now;
    if (tx->authorization_deadline_ms <= 0) return 0;
    QueryInterruptTimePrecise(&now);
    return now / 10000 <= (uint64_t)tx->authorization_deadline_ms;
}

static void copy(void *destination, const void *source, size_t count) {
    unsigned char *out = destination;
    const unsigned char *in = source;
    while (count--) *out++ = *in++;
}
static void wipe(void *value, size_t count) {
    volatile unsigned char *out = value;
    while (count--) *out++ = 0;
}
static int equal(const WCHAR *a, const WCHAR *b, int count) {
    for (int i = 0; i < count; i++) if (a[i] != b[i]) return 0;
    return 1;
}
static int valid(const WCHAR *value, int count) {
    if (count < 0 || count > MAX_UNITS || value[count] != 0) return 0;
    for (int i = 0; i < count; i++) {
        if (!value[i]) return 0;
        if (value[i] >= 0xd800 && value[i] <= 0xdbff) {
            if (++i == count || value[i] < 0xdc00 || value[i] > 0xdfff) return 0;
        } else if (value[i] >= 0xdc00 && value[i] <= 0xdfff) return 0;
    }
    return 1;
}
static int boundary(const WCHAR *value, int count, int offset) {
    return offset >= 0 && offset <= count && !(offset && offset < count
        && value[offset - 1] >= 0xd800 && value[offset - 1] <= 0xdbff
        && value[offset] >= 0xdc00 && value[offset] <= 0xdfff);
}
static int ime(HANDLE hwnd) {
    HANDLE context = ImmGetContext(hwnd);
    if (!context) return 0; // Unknown, never infer Clear from absent context.
    int32_t bytes = ImmGetCompositionStringW(context, 8, 0, 0);
    ImmReleaseContext(hwnd, context);
    return bytes == 0 ? 1 : bytes > 0 ? 2 : 0;
}
static WNDPROC qualify_field(HANDLE hwnd, HANDLE root, uint32_t process, uint32_t thread, int32_t *multiline) {
    uint32_t pid = 0;
    if (process != GetCurrentProcessId() || thread != GetCurrentThreadId()
        || GetWindowThreadProcessId(hwnd, &pid) != thread || pid != process
        || GetAncestor(hwnd, 2) != root
        || !IsWindowUnicode(hwnd) || !IsWindowEnabled(hwnd)) return 0;
    WCHAR name[32];
    if (GetClassNameW(hwnd, name, 32) != 4 || !equal(name, (const WCHAR *)L"Edit", 4)) return 0;
    // Require the unchanged system class AND instance procedure. Superclasses
    // and subclassed/custom/managed controls remain unsupported.
    WindowClass stock;
    if (!GetClassInfoW(0, (const WCHAR *)L"Edit", &stock)) return 0;
    WNDPROC proc = stock.procedure;
    if (!proc || GetWindowLongPtrW(hwnd, -4) != (intptr_t)proc
        || GetClassLongPtrW(hwnd, -24) != (intptr_t)proc) return 0;
    intptr_t style = GetWindowLongPtrW(hwnd, -16);
    if ((style & (0x8 | 0x10 | 0x20 | 0x800 | 0x2000)) || CallWindowProcW(proc, hwnd, 0xd2, 0, 0)) return 0;
    if (multiline) *multiline = (style & 4) != 0;
    return proc;
}
static WNDPROC qualify(Transaction *tx) {
    return qualify_field((HANDLE)(uintptr_t)tx->hwnd, (HANDLE)(uintptr_t)tx->root,
        tx->pid, tx->thread, &tx->multiline);
}
// Separate from the unchanged v2 transaction layout and owned-capture wire.
__declspec(dllexport) uint32_t TypeLayOwnedEditVersion(void) { return 3; }
__declspec(dllexport) int TypeLayOwnedEditControl(HANDLE hwnd, uint32_t operation, uint64_t seed) {
    uint32_t pid = 0;
    uint32_t thread = GetCurrentThreadId();
    if (!hwnd || GetWindowThreadProcessId(hwnd, &pid) != thread || pid != GetCurrentProcessId()) return 0;
    if (operation == 1) { // Register exactly once, before showing the field.
        if (owned.hwnd || !seed) return 0;
        WindowClass stock;
        WCHAR name[32];
        if (!IsWindowUnicode(hwnd) || GetClassNameW(hwnd, name, 32) != 4
            || !equal(name, (const WCHAR *)L"Edit", 4)
            || !GetClassInfoW(0, (const WCHAR *)L"Edit", &stock) || !stock.procedure
            || GetWindowLongPtrW(hwnd, -4) != (intptr_t)stock.procedure
            || GetClassLongPtrW(hwnd, -24) != (intptr_t)stock.procedure) return 0;
        owned.hwnd = hwnd; owned.thread = thread; owned.cookie = seed;
        owned.focus_revision = 1; owned.retired = 1;
        RemovePropW(hwnd, property);
        if (!SetPropW(hwnd, focus_property, (HANDLE)(uintptr_t)owned.focus_revision)) {
            owned.focus_revision = 0;
            return 0;
        }
        return 1;
    }
    if (owned.hwnd != hwnd || owned.thread != thread || seed) return 0;
    if (operation == 2 || operation == 4) { // BeginOnDemand / BeginAutomatic mint a new lifetime.
        if (!owned.retired || !owned.focus_revision || owned.cookie == UINT64_MAX) return 0;
        owned.cookie++;
        // The route restriction belongs to this entire cookie lifetime, not to
        // a currently installed managed plan. Revocation cannot reopen v2 writes.
        owned.automatic = operation == 4;
        if (!SetPropW(hwnd, property, (HANDLE)(uintptr_t)owned.cookie)) return 0;
        owned.retired = 0;
        return 1;
    }
    if (operation == 3) {
        owned.retired = 1;
        RemovePropW(hwnd, property);
        return 1;
    }
    return 0;
}
static int owned_current(const Transaction *tx) {
    return owned.hwnd != (HANDLE)(uintptr_t)tx->hwnd
        || (!owned.retired && owned.thread == GetCurrentThreadId() && tx->thread == owned.thread
            && tx->cookie == owned.cookie);
}
// The stock Edit sends focus notifications synchronously to its parent. Only
// that registered GUI thread may advance this revision, before it does anything
// that could reenter. A round trip to another child cannot restore old authority.
__declspec(dllexport) uint64_t TypeLayOwnedFocusRevision(HANDLE hwnd, uint32_t operation) {
    if (!owned.hwnd || owned.hwnd != hwnd || owned.thread != GetCurrentThreadId()) return 0;
    if (operation == 0) return owned.focus_revision;
    if (operation != 1) return 0;
    if (owned.focus_revision && owned.focus_revision < INT64_MAX) {
        ++owned.focus_revision;
        if (SetPropW(hwnd, focus_property, (HANDLE)(uintptr_t)owned.focus_revision))
            return owned.focus_revision;
    }
    owned.focus_revision = 0;
    owned.retired = 1;
    RemovePropW(hwnd, property);
    RemovePropW(hwnd, focus_property);
    return 0;
}
static int owned_focus_current(uint64_t expected) {
    return !expected || (owned.focus_revision == expected && !owned.retired);
}
// Local GUI-only exit qualification reads no body or selection and performs no
// Edit mutation. IMM may reenter; repeat the exact lifetime/focus/deadline gate.
static int owned_exit_current(HANDLE hwnd, HANDLE root, uint64_t cookie, uint64_t revision, int64_t expires) {
    uint64_t now;
    uint32_t pid = 0, thread = GetCurrentThreadId();
    QueryInterruptTimePrecise(&now);
    return expires > 0 && now / 10000 <= (uint64_t)expires
        && owned.hwnd == hwnd && owned.thread == thread && !owned.retired && owned.automatic
        && cookie && owned.cookie == cookie && revision && owned.focus_revision == revision
        && (uint64_t)(uintptr_t)GetPropW(hwnd, property) == cookie
        && (uint64_t)(uintptr_t)GetPropW(hwnd, focus_property) == revision
        && GetParent(hwnd) == root && GetWindowThreadProcessId(root, &pid) == thread && pid == GetCurrentProcessId()
        && IsWindowVisible(hwnd) && IsWindowVisible(root) && IsWindowEnabled(root) && !IsIconic(root)
        && GetForegroundWindow() == root && GetFocus() == hwnd;
}
__declspec(dllexport) int TypeLayOwnedEditCanExit(HANDLE hwnd, HANDLE root, uint64_t cookie,
    uint64_t revision, int64_t expires) {
    if (!owned_exit_current(hwnd, root, cookie, revision, expires)
        || !qualify_field(hwnd, root, GetCurrentProcessId(), GetCurrentThreadId(), 0)
        || !owned_exit_current(hwnd, root, cookie, revision, expires) || ime(hwnd) != 1) return 0;
    return qualify_field(hwnd, root, GetCurrentProcessId(), GetCurrentThreadId(), 0)
        && owned_exit_current(hwnd, root, cookie, revision, expires);
}
// Current local input identity only. These released handle values are compared
// as scalars, never retained as usable IMM handles. Equal samples do not prove
// that an input method/context was not switched away and back between samples.
typedef struct { uint64_t ime_context, keyboard_layout; } OwnedInputContext;
_Static_assert(sizeof(OwnedInputContext) == 16, "Owned input-context layout");
static int read_input_context(HANDLE hwnd, OwnedInputContext *result) {
    HANDLE layout = GetKeyboardLayout(GetCurrentThreadId());
    if (!layout) return 0;
    HANDLE context = ImmGetContext(hwnd);
    if (!context) return 0;
    int32_t bytes = ImmGetCompositionStringW(context, 8, 0, 0);
    int released = ImmReleaseContext(hwnd, context);
    if (!released || bytes != 0 || GetKeyboardLayout(GetCurrentThreadId()) != layout) return 0;
    // IMM can reenter. Retrieve the associated context again after querying and
    // releasing it rather than returning an identity captured before that call.
    HANDLE current = ImmGetContext(hwnd);
    if (!current) return 0;
    bytes = ImmGetCompositionStringW(current, 8, 0, 0);
    released = ImmReleaseContext(hwnd, current);
    if (!released || bytes != 0 || current != context || GetKeyboardLayout(GetCurrentThreadId()) != layout) return 0;
    result->ime_context = (uint64_t)(uintptr_t)current;
    result->keyboard_layout = (uint64_t)(uintptr_t)layout;
    return 1;
}
__declspec(dllexport) int TypeLayOwnedEditTryProbeInputContext(HANDLE hwnd, HANDLE root,
    uint32_t process, uint32_t thread, uint64_t cookie, uint64_t revision, int64_t expires,
    OwnedInputContext *result) {
    if (!result) return 0;
    result->ime_context = result->keyboard_layout = 0;
    OwnedInputContext current;
    if (process != GetCurrentProcessId() || thread != GetCurrentThreadId()
        || !owned_exit_current(hwnd, root, cookie, revision, expires)
        || !qualify_field(hwnd, root, process, thread, 0)
        || !owned_exit_current(hwnd, root, cookie, revision, expires)
        || !read_input_context(hwnd, &current)
        || !qualify_field(hwnd, root, process, thread, 0)
        || !owned_exit_current(hwnd, root, cookie, revision, expires)) return 0;
    *result = current;
    return 1;
}
__declspec(dllexport) int TypeLayOwnedEditCanExitWithInputContext(HANDLE hwnd, HANDLE root,
    uint32_t process, uint32_t thread, uint64_t cookie, uint64_t revision, int64_t expires,
    uint64_t expected_context, uint64_t expected_layout) {
    OwnedInputContext current;
    return expected_context && expected_layout
        && TypeLayOwnedEditTryProbeInputContext(hwnd, root, process, thread, cookie, revision, expires, &current)
        && current.ime_context == expected_context && current.keyboard_layout == expected_layout;
}
static int owned_input_current(const Transaction *tx, uint64_t revision, const OwnedInputContext *expected) {
    return !expected || TypeLayOwnedEditCanExitWithInputContext((HANDLE)(uintptr_t)tx->hwnd,
        (HANDLE)(uintptr_t)tx->root, tx->pid, tx->thread, tx->cookie, revision,
        tx->authorization_deadline_ms, expected->ime_context, expected->keyboard_layout);
}
static int client_bounds(HANDLE hwnd, Rect *bounds) {
    Rect client;
    if (!GetClientRect(hwnd, &client) || client.right <= client.left || client.bottom <= client.top) return 0;
    Point first = {client.left, client.top}, last = {client.right, client.bottom};
    if (!ClientToScreen(hwnd, &first) || !ClientToScreen(hwnd, &last)
        || last.x <= first.x || last.y <= first.y) return 0;
    bounds->left = first.x; bounds->top = first.y; bounds->right = last.x; bounds->bottom = last.y;
    return 1;
}
static int owned_capture_current(Transaction *tx, uint64_t revision) {
    HANDLE hwnd = (HANDLE)(uintptr_t)tx->hwnd, root = (HANDLE)(uintptr_t)tx->root;
    uint32_t pid = 0;
    Rect control, parent;
    // The trusted practice is one direct child, not a general clipping-tree
    // adapter. IsWindowVisible alone does not detect scrolling out of its parent.
    // Keep this stricter read gate exclusive to owned op6, never ordinary op1-5.
    if (!qualify(tx) || GetParent(hwnd) != root
        || GetWindowThreadProcessId(root, &pid) != tx->thread || pid != tx->pid
        || !IsWindowEnabled(root) || !IsWindowVisible(root) || !IsWindowVisible(hwnd) || IsIconic(root)
        || !client_bounds(hwnd, &control) || !client_bounds(root, &parent)
        || control.right <= parent.left || control.left >= parent.right
        || control.bottom <= parent.top || control.top >= parent.bottom) return 0;
    return owned_current(tx) && owned_focus_current(revision) && foreground_source(tx)
        && (uint64_t)(uintptr_t)GetPropW(hwnd, property) == tx->cookie;
}
static int read_text(Transaction *tx, WNDPROC proc) {
    HANDLE hwnd = (HANDLE)(uintptr_t)tx->hwnd;
    LRESULT length = CallWindowProcW(proc, hwnd, 0x0e, 0, 0);
    if (length < 0 || length > MAX_UNITS) return 0;
    LRESULT read = CallWindowProcW(proc, hwnd, 0x0d, CAPACITY, (LPARAM)tx->observed);
    if (read != length || !valid(tx->observed, (int)read)) return 0;
    CallWindowProcW(proc, hwnd, 0xb0, (WPARAM)&tx->selection_start, (LPARAM)&tx->selection_end);
    return (int)read + 1;
}
static int owned_input_preflight(Transaction *tx, WNDPROC proc, uint64_t revision,
    const OwnedInputContext *expected, int32_t start, int32_t end) {
    if (!expected) return 1;
    if (!owned_input_current(tx, revision, expected)) { tx->reason = 9; return 0; }
    // The IMM query above may reenter. Recheck the whole source and selection
    // afterwards, before either selection mutation or the actual replacement.
    if (read_text(tx, proc) - 1 != tx->length || !equal(tx->observed, tx->expected, tx->length)
        || tx->selection_start != start || tx->selection_end != end) { tx->reason = 3; return 0; }
    if (!qualify(tx) || !owned_current(tx) || !owned_focus_current(revision)
        || !owned_exit_current((HANDLE)(uintptr_t)tx->hwnd, (HANDLE)(uintptr_t)tx->root,
            tx->cookie, revision, tx->authorization_deadline_ms)) { tx->reason = 10; return 0; }
    if (GetTickCount64() > tx->deadline) { tx->reason = 14; return 0; }
    return 1;
}
typedef struct { uint32_t *claim; const uint32_t *reason; } SourceAdmission;
static void execute_guarded(Transaction *tx, uint64_t focus_revision, int *accepted,
    const OwnedInputContext *expected_input, const SourceAdmission *admission) {
    tx->status = 2; tx->reason = 6; // RejectedBeforeMutation / Unsupported.
    if (tx->version != VERSION || !tx->cookie || GetTickCount64() > tx->deadline) return;
    HANDLE hwnd = (HANDLE)(uintptr_t)tx->hwnd;
    if (!owned_current(tx)) { tx->reason = 2; return; }
    if (!owned_focus_current(focus_revision)) { tx->reason = 10; return; }
    if (tx->op == 3) {
        if (owned.hwnd == hwnd) { tx->status = 1; tx->reason = 0; return; }
        if ((uint64_t)(uintptr_t)GetPropW(hwnd, property) == tx->cookie) RemovePropW(hwnd, property);
        tx->status = 1; tx->reason = 0; return;
    }
    WNDPROC proc = qualify(tx);
    if (!proc) return;
    const int foreground = tx->op == 5 || tx->op == 6;
    if (tx->op == 5 && !authorization_current(tx)) { tx->reason = 14; return; }
    if (foreground && !foreground_source(tx)) { tx->reason = 10; return; }
    uint64_t cookie = (uint64_t)(uintptr_t)GetPropW(hwnd, property);
    if (tx->op == 1 && !cookie) {
        if (!SetPropW(hwnd, property, (HANDLE)(uintptr_t)tx->cookie)) return;
        cookie = tx->cookie;
    }
    if (cookie != tx->cookie) { tx->reason = 2; return; }
    tx->ime = ime(hwnd);
    if (tx->ime != 1) { tx->reason = 9; return; }
    if (!owned_input_current(tx, focus_revision, expected_input)) { tx->reason = 9; return; }
    // IMM may reenter. Qualification and visibility must still hold afterwards,
    // immediately before the first source-body read.
    if (tx->op == 6 && !owned_capture_current(tx, focus_revision)) { tx->reason = 10; return; }
    int observed = read_text(tx, proc) - 1;
    if (observed < 0) { tx->reason = 16; return; }
    if (tx->op == 1 || tx->op == 4 || tx->op == 6) {
        if (tx->op == 6 && (ime(hwnd) != 1 || !owned_capture_current(tx, focus_revision))) { tx->reason = 10; return; }
        tx->length = observed;
        tx->status = 1; tx->reason = 0; return;
    }
    if ((tx->op != 2 && !foreground) || !valid(tx->expected, tx->length)
        || !valid(tx->replacement, tx->replacement_length) || !tx->replacement_length
        || !boundary(tx->expected, tx->length, tx->start)
        || !boundary(tx->expected, tx->length, tx->end) || tx->end < tx->start
        || tx->length - (tx->end - tx->start) + tx->replacement_length > MAX_UNITS) return;
    if (observed != tx->length || !equal(tx->observed, tx->expected, observed)) { tx->reason = 3; return; }
    if (foreground) {
        if (!boundary(tx->expected, tx->length, tx->expected_selection_start)
            || !boundary(tx->expected, tx->length, tx->expected_selection_end)
            || tx->expected_selection_end < tx->expected_selection_start) { tx->reason = 5; return; }
        // Expected.Selection is the current selection after returning from the
        // editor. The replacement Scope can still be a different original range.
        if (tx->selection_start != tx->expected_selection_start
            || tx->selection_end != tx->expected_selection_end) { tx->reason = 4; return; }
    }
    if (!tx->multiline) for (int i = 0; i < tx->replacement_length; i++)
        if (tx->replacement[i] == '\r' || tx->replacement[i] == '\n') { tx->reason = 17; return; }
    LRESULT limit = CallWindowProcW(proc, hwnd, 0xd5, 0, 0); // EM_GETLIMITTEXT.
    if (limit < tx->length - (tx->end - tx->start) + tx->replacement_length) { tx->reason = 16; return; }
    // Validation and replacement execute in this one stock-control GUI-thread
    // invocation, not in a remote read/SetValue race. No focus-changing APIs are called.
    if (GetTickCount64() > tx->deadline) { tx->reason = 14; return; }
    if (foreground && !foreground_source(tx)) { tx->reason = 10; return; }
    if (tx->op == 5 && !authorization_current(tx)) { tx->reason = 14; return; }
    if (!owned_focus_current(focus_revision)) { tx->reason = 10; return; }
    if (!owned_input_preflight(tx, proc, focus_revision, expected_input,
        tx->expected_selection_start, tx->expected_selection_end)) return;
    if (admission) {
        // Recheck on this GUI thread, before the first input side effect. Queue
        // status is deliberately conservative: any pending input, posted work,
        // timer, paint or sent work refuses. Never pump/remove messages to pass.
        if (*admission->reason || ime(hwnd) != 1) { tx->reason = 9; return; }
        if (read_text(tx, proc) - 1 != tx->length || !equal(tx->observed, tx->expected, tx->length)) {
            tx->reason = 3; return;
        }
        if (tx->selection_start != tx->expected_selection_start || tx->selection_end != tx->expected_selection_end) {
            tx->reason = 4; return;
        }
        if (*admission->reason || !qualify(tx) || !foreground_source(tx)
            || (uint64_t)(uintptr_t)GetPropW(hwnd, property) != tx->cookie) { tx->reason = 2; return; }
        if (!authorization_current(tx) || GetTickCount64() > tx->deadline) { tx->reason = 14; return; }
        if (GetQueueStatus(0x1dff) >> 16) { tx->reason = 15; return; }
        uint32_t armed = 0;
        // This actual aligned shared word is the only acceptance linearization
        // point. A timeout's Withdrawn value can never be reset or overwritten.
        if (!__atomic_compare_exchange_n(admission->claim, &armed, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
            tx->reason = 14; return;
        }
    }
    // This is the only owned acceptance point. Everything above is a read-only
    // preflight. The caller has removed its single-use local plan; only a true
    // return may consume the original queue message, even if selection callbacks
    // subsequently reject execution. Never synthesize a replacement Tab.
    if (accepted) *accepted = 1;
    CallWindowProcW(proc, hwnd, 0xb1, (WPARAM)tx->start, (LPARAM)tx->end);
    // Selection changes may cause synchronous accessibility callbacks. Refuse if
    // any callback changed the source, lifetime, class or composition state.
    if (!owned_focus_current(focus_revision) || !owned_current(tx) || !qualify(tx) || (uint64_t)(uintptr_t)GetPropW(hwnd, property) != tx->cookie
        || ime(hwnd) != 1 || read_text(tx, proc) - 1 != tx->length
        || !equal(tx->observed, tx->expected, tx->length)
        || tx->selection_start != tx->start || tx->selection_end != tx->end
        || GetTickCount64() > tx->deadline) { tx->reason = 3; return; }
    if (foreground && !foreground_source(tx)) { tx->reason = 10; return; }
    if (tx->op == 5 && !authorization_current(tx)) { tx->reason = 14; return; }
    if (!owned_focus_current(focus_revision)) { tx->reason = 10; return; }
    if (!owned_input_preflight(tx, proc, focus_revision, expected_input, tx->start, tx->end)) return;
    tx->status = 3; tx->reason = 20; // Unknown once mutation starts; never retry.
    CallWindowProcW(proc, hwnd, 0xc2, 1, (LPARAM)tx->replacement); // EM_REPLACESEL, undo enabled.
    if (!owned_focus_current(focus_revision) || !owned_current(tx) || !qualify(tx) || (uint64_t)(uintptr_t)GetPropW(hwnd, property) != tx->cookie) return;
    if (!owned_focus_current(focus_revision) || (foreground && (!foreground_source(tx) || ime(hwnd) != 1))) return;
    int after = read_text(tx, proc) - 1;
    if (!owned_focus_current(focus_revision) || (foreground && (!foreground_source(tx) || ime(hwnd) != 1))) return;
    int caret = tx->start + tx->replacement_length;
    int total = tx->length - (tx->end - tx->start) + tx->replacement_length;
    if (after != total || tx->selection_start != caret || tx->selection_end != caret
        || !equal(tx->observed, tx->expected, tx->start)
        || !equal(tx->observed + tx->start, tx->replacement, tx->replacement_length)
        || !equal(tx->observed + caret, tx->expected + tx->end, tx->length - tx->end)) return;
    tx->length = after; tx->status = 1; tx->reason = 0;
}
static void execute(Transaction *tx) {
    // An unknown operation must never fall through to a source text read.
    if (tx->op < 1 || tx->op > 5) { tx->status = 2; tx->reason = 6; return; }
    if (owned.hwnd == (HANDLE)(uintptr_t)tx->hwnd && owned.automatic
        && (tx->op == 2 || tx->op == 5)) { tx->status = 2; tx->reason = 6; return; }
    execute_guarded(tx, 0, 0, 0, 0);
}

__declspec(dllexport) uint32_t TypeLayOwnedCaptureVersion(void) { return 2; }
static void execute_owned_capture(Transaction *tx) {
    // This independent message uses offset 96 as ExpectedFocusRevision, never
    // an authorization deadline. The old v2 route does not recognize op6.
    uint64_t revision = (uint64_t)tx->authorization_deadline_ms;
    if (tx->op != 6 || tx->authorization_deadline_ms <= 0 || !owned.hwnd
        || owned.hwnd != (HANDLE)(uintptr_t)tx->hwnd || owned.thread != GetCurrentThreadId()
        || tx->thread != owned.thread || tx->pid != GetCurrentProcessId()
        || owned.retired || !owned.automatic || tx->cookie != owned.cookie || !owned_focus_current(revision)) {
        tx->status = 2; tx->reason = 2; return;
    }
    execute_guarded(tx, revision, 0, 0, 0);
}

// Local-only entry: the independent practice GUI owns this private buffer. The
// remote v2 wire cannot opt into it or select another process, field or operation.
// Return 0 means preflight refusal with no selection/text change; return 1 means
// accepted and tx.status is the real execution result (including unknown).
static int try_execute_owned(Transaction *tx, uint32_t bytes, uint64_t expected_focus_revision,
    const OwnedInputContext *expected_input) {
    if (!tx || bytes != sizeof(Transaction)) return 0;
    if (tx->status || tx->op != 5 || !expected_focus_revision
        || !owned.hwnd || owned.hwnd != (HANDLE)(uintptr_t)tx->hwnd
        || owned.thread != GetCurrentThreadId() || tx->thread != owned.thread
        || tx->pid != GetCurrentProcessId() || owned.retired || !owned.automatic || tx->cookie != owned.cookie) {
        tx->status = 2; tx->reason = 2;
        return 0;
    }
    int accepted = 0;
    execute_guarded(tx, expected_focus_revision, &accepted, expected_input, 0);
    return accepted;
}
__declspec(dllexport) int TypeLayOwnedEditTryExecute(Transaction *tx, uint32_t bytes,
    uint64_t expected_focus_revision) {
    return try_execute_owned(tx, bytes, expected_focus_revision, 0);
}
__declspec(dllexport) int TypeLayOwnedEditTryExecuteWithInputContext(Transaction *tx, uint32_t bytes,
    uint64_t expected_focus_revision, uint64_t expected_context, uint64_t expected_layout) {
    const OwnedInputContext expected = {expected_context, expected_layout};
    return try_execute_owned(tx, bytes, expected_focus_revision, &expected);
}

// Inactive ordinary-Edit source prototype. No production publication enables
// this consumer. Its independent version is not SourceLocalAcceptanceVersion.
typedef struct {
    uint32_t version, bytes, state, acknowledged_operation;
    uint64_t generation[2], grant[2];
    int64_t observed_at_ms;
    uint64_t acknowledgement;
    uint32_t claim, invoked; // Claim: Armed=0, Claimed=1, Withdrawn=2; never reused.
    Transaction tx;
} SourceInstallation;
_Static_assert(offsetof(SourceInstallation, claim) == 64, "Aligned claim word");
_Static_assert(offsetof(SourceInstallation, tx) == 72, "Source envelope layout");
_Static_assert(sizeof(SourceInstallation) == 49336, "Source mapping length");
// One candidate per target process, owned by one GUI thread. Only owner is
// atomic; all remaining members are exclusively accessed by that thread.
// No retained body, mapped view, function pointer, subclass or module pin.
typedef struct {
    uint32_t owner;
    uint32_t busy, reason, completed_status, completed_reason;
    uint64_t nonce, generation[2], grant[2], hwnd, root, cookie;
    int64_t expires;
} SourceSlot;
static SourceSlot source;
// Covers unmatched requests too: a downstream hook cannot retire A, install B,
// and let an outer rejected B request publish a false quiet ACK over that slot.
static uint32_t source_callback;
__declspec(dllexport) uint32_t TypeLaySourceInstallationVersion(void) { return 2; }

static void source_invalidate(const CallMessage *msg) {
    if (__atomic_load_n(&source.owner, __ATOMIC_ACQUIRE) != GetCurrentThreadId() || source.reason) return;
    if (msg->hwnd == (HANDLE)(uintptr_t)source.hwnd) {
        switch (msg->message) {
        case 0x08: case 0x82: // KILLFOCUS / NCDESTROY: a round trip cannot revive it.
            source.reason = 2; break;
        case 0x0c: case 0xb1: case 0xc2: case 0xcc: case 0xcf:
        case 0x51: case 0x10d: case 0x10e: case 0x10f:
        case 0x100: case 0x101: case 0x102: case 0x104: case 0x105:
        case 0x201: case 0x204: case 0x207:
            source.reason = 15; break;
        }
    }
    if (msg->hwnd == (HANDLE)(uintptr_t)source.root
        && (msg->message == 0x82 || (msg->message == 0x06 && !(msg->wparam & 0xffff))
            || (msg->message == 0x1c && !msg->wparam))) source.reason = 2;
}
static int source_identity(const SourceInstallation *request, const CallMessage *msg) {
    return source.nonce == (uint64_t)msg->wparam
        && source.hwnd == request->tx.hwnd && source.root == request->tx.root
        && source.cookie == request->tx.cookie && source.expires == request->tx.authorization_deadline_ms
        && source.generation[0] == request->generation[0] && source.generation[1] == request->generation[1]
        && source.grant[0] == request->grant[0] && source.grant[1] == request->grant[1];
}
static uint32_t source_preflight(SourceInstallation *request) {
    Transaction *tx = &request->tx;
    uint64_t now;
    QueryInterruptTimePrecise(&now);
    if (request->observed_at_ms <= 0 || now / 10000 < (uint64_t)request->observed_at_ms
        || tx->authorization_deadline_ms < request->observed_at_ms
        || tx->authorization_deadline_ms - request->observed_at_ms > 100
        || !authorization_current(tx)) return 14;
    uint32_t root_pid = 0;
    if (owned.hwnd == (HANDLE)(uintptr_t)tx->hwnd || !foreground_source(tx)
        || GetWindowThreadProcessId((HANDLE)(uintptr_t)tx->root, &root_pid) != tx->thread
        || root_pid != tx->pid || !IsWindowVisible((HANDLE)(uintptr_t)tx->hwnd)
        || !IsWindowVisible((HANDLE)(uintptr_t)tx->root)
        || !IsWindowEnabled((HANDLE)(uintptr_t)tx->root) || IsIconic((HANDLE)(uintptr_t)tx->root)) return 2;
    if (tx->version != VERSION || tx->op != 5 || tx->status || !tx->cookie
        || !valid(tx->expected, tx->length) || !valid(tx->replacement, tx->replacement_length)
        || !tx->replacement_length || !boundary(tx->expected, tx->length, tx->start)
        || !boundary(tx->expected, tx->length, tx->end) || tx->end < tx->start
        || !boundary(tx->expected, tx->length, tx->expected_selection_start)
        || !boundary(tx->expected, tx->length, tx->expected_selection_end)
        || tx->expected_selection_end < tx->expected_selection_start
        || tx->length - (tx->end - tx->start) + tx->replacement_length > MAX_UNITS) return 18;
    int length = tx->length;
    // Reuse the production read-only operation, never op5 execution. Its result
    // is installation preflight, not a completed write or a renewed snapshot.
    tx->op = 4;
    execute_guarded(tx, 0, 0, 0, 0);
    tx->op = 5;
    uint32_t reason = tx->status == 1 ? 0 : tx->reason;
    if (!reason && (tx->length != length || !equal(tx->observed, tx->expected, length))) reason = 3;
    if (!reason && (tx->selection_start != tx->expected_selection_start
        || tx->selection_end != tx->expected_selection_end)) reason = 4;
    tx->length = length;
    if (!reason && (!qualify(tx) || !foreground_source(tx)
        || (uint64_t)(uintptr_t)GetPropW((HANDLE)(uintptr_t)tx->hwnd, property) != tx->cookie)) reason = 2;
    if (!reason && !authorization_current(tx)) reason = 14;
    return reason;
}
static HANDLE source_mapping(const CallMessage *msg) {
    WCHAR name[64];
    const WCHAR prefix[] = L"Local\\TypeLay.Source.";
    const WCHAR digits[] = L"0123456789abcdef";
    int i = 0;
    for (; prefix[i]; i++) name[i] = prefix[i];
    uint64_t bits = (uint64_t)msg->wparam;
    for (int shift = 60; shift >= 0; shift -= 4) name[i++] = digits[(bits >> shift) & 15];
    name[i] = 0;
    return OpenFileMappingW(6, 0, name);
}
static LRESULT source_message(int code, WPARAM wp, LPARAM lp, uint32_t operation) {
    const CallMessage *msg = (const CallMessage *)lp;
    uint32_t thread = GetCurrentThreadId(), empty_callback = 0;
    if (!__atomic_compare_exchange_n(&source_callback, &empty_callback, thread, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
        // Nested exact revocation may fence the outer installation, but cannot
        // acknowledge quiet while any outer source callback is still active.
        if (operation == 2 && __atomic_load_n(&source.owner, __ATOMIC_ACQUIRE) == thread
            && source.root == (uint64_t)(uintptr_t)msg->hwnd
            && source.nonce == (uint64_t)msg->wparam
            && !source.reason) source.reason = 15;
        return CallNextHookEx(0, code, wp, lp);
    }
    HANDLE mapping = source_mapping(msg);
    SourceInstallation *shared = mapping ? MapViewOfFile(mapping, 6, 0, 0, sizeof(SourceInstallation)) : 0;
    SourceInstallation *request = shared ? HeapAlloc(GetProcessHeap(), 8, sizeof(SourceInstallation)) : 0;
    int matched = 0, quiet = 0, respond = 0;
    if (request) {
        copy(request, shared, sizeof(SourceInstallation));
        respond = (uint64_t)msg->lparam > request->acknowledgement;
        Transaction *tx = &request->tx;
        if (request->version == 2 && request->bytes == sizeof(SourceInstallation)
            && respond
            && (request->generation[0] || request->generation[1]) && (request->grant[0] || request->grant[1])
            && tx->thread == thread && tx->pid == GetCurrentProcessId()
            && tx->root == (uint64_t)(uintptr_t)msg->hwnd) {
            uint32_t empty = 0;
            if (operation == 1 && request->state == 0
                && __atomic_compare_exchange_n(&source.owner, &empty, thread, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
                source.busy = 0; source.reason = 0;
                source.completed_status = source.completed_reason = 0;
                source.nonce = (uint64_t)msg->wparam;
                copy(source.generation, request->generation, sizeof(source.generation));
                copy(source.grant, request->grant, sizeof(source.grant));
                source.hwnd = tx->hwnd; source.root = tx->root; source.cookie = tx->cookie;
                source.expires = tx->authorization_deadline_ms;
                source.busy++;
                uint32_t reason = source_preflight(request);
                if (reason && !source.reason) source.reason = reason;
                source.busy--;
            }
            matched = __atomic_load_n(&source.owner, __ATOMIC_ACQUIRE) == thread && source_identity(request, msg);
            if (matched) {
                source.busy++;
                if (operation == 2 && !source.reason) source.reason = 15;
                if (!source.reason && !authorization_current(tx)) source.reason = 14;
                if (operation == 4 && !source.completed_status) {
                    if (!source.reason && request->state == 1) {
                        SourceAdmission admission = { &shared->claim, &source.reason };
                        execute_guarded(tx, 0, 0, 0, &admission);
                    } else { tx->status = 2; tx->reason = source.reason ? source.reason : 6; }
                    source.completed_status = tx->status;
                    source.completed_reason = tx->reason;
                    if (!source.reason) source.reason = 15; // Single use, including preclaim refusal.
                }
                // Only an explicit retirement may relinquish the source slot.
                // A nested revoke records cancellation but cannot claim quiet.
                quiet = operation == 2 && source.busy == 1;
            } else {
                request->state = 3;
                // A previous quiet ACK may have been lost after execution.
                // With the slot already gone, Claimed cannot be downgraded to
                // a claim that no mutation happened. Retire conservatively.
                int claimed = __atomic_load_n(&shared->claim, __ATOMIC_ACQUIRE) == 1;
                tx->status = claimed ? 3 : 2; tx->reason = claimed ? 20 : 2;
            }
        } else if (respond) {
            // Malformed identity cannot prove retirement of an existing slot.
            request->state = 2; tx->status = 2; tx->reason = 6;
        }
    }
    // No quiet acknowledgement before the rest of this callback chain returns.
    // Reentrant callbacks can only revoke the original slot, never replace it.
    LRESULT result = CallNextHookEx(0, code, wp, lp);
    if (request) {
        if (matched) {
            if (!source.reason && !authorization_current(&request->tx)) source.reason = 14;
            request->tx.status = source.completed_status ? source.completed_status : source.reason ? 2 : 0;
            request->tx.reason = source.completed_status ? source.completed_reason : source.reason;
            request->state = quiet ? 3 : source.completed_status || source.reason ? 2 : 1;
            source.busy--;
            if (quiet) {
                wipe(&source.nonce, sizeof(source) - offsetof(SourceSlot, nonce));
                source.reason = 0;
                source.completed_status = source.completed_reason = 0;
                __atomic_store_n(&source.owner, 0, __ATOMIC_RELEASE);
            }
        }
        // The broker writes the request once before dispatch. Only the source
        // writes response fields; polling never treats an unsynchronised read as
        // an ACK. SendMessage completion establishes callback completion.
        if (respond) {
            shared->tx.status = request->tx.status;
            shared->tx.reason = request->tx.reason;
            shared->state = request->state;
            shared->acknowledged_operation = operation;
            shared->acknowledgement = (uint64_t)msg->lparam;
        }
        wipe(request, sizeof(SourceInstallation));
        HeapFree(GetProcessHeap(), 0, request);
    }
    if (shared) UnmapViewOfFile(shared);
    if (mapping) CloseHandle(mapping);
    __atomic_store_n(&source_callback, 0, __ATOMIC_RELEASE);
    return result;
}
// Called only by the inactive Windows adapter, with its already-open local view.
// The view stays owned through source quiet ACK, including all timeout paths.
// Different focus queues are observed, not locked: only the isolated fixture is
// known not to attach queues. This is NOT an ordinary-process readiness proof.
__declspec(dllexport) uint32_t TypeLaySourceActivate(SourceInstallation *shared, uint32_t bytes,
    uint64_t nonce, uint64_t command, uint32_t timeout, uint32_t message) {
    if (!shared || bytes != sizeof(SourceInstallation) || shared->version != 2
        || shared->bytes != bytes || !command || !message || !timeout || timeout > 10) return 0;
    uint32_t unused = 0;
    // Global per-grant single use, including accidentally duplicated local
    // endpoint objects. Retirement never clears either this word or claim.
    if (!__atomic_compare_exchange_n(&shared->invoked, &unused, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) return 0;
    uint64_t started, ended;
    QueryInterruptTimePrecise(&started);
    Transaction *tx = &shared->tx;
    uint32_t pid = 0;
    LRESULT sent = 0;
    if (tx->thread != GetCurrentThreadId() && !GetFocus() && !GetActiveWindow()
        && GetWindowThreadProcessId((HANDLE)(uintptr_t)tx->root, &pid) == tx->thread && pid == tx->pid
        && GetForegroundWindow() == (HANDLE)(uintptr_t)tx->root
        && shared->state == 1 && __atomic_load_n(&shared->claim, __ATOMIC_ACQUIRE) == 0
        && authorization_current(tx)) {
        uintptr_t result = 0;
        // One exact window, no pumping, broadcast, retries or NOTIMEOUTIFNOTHUNG.
        sent = SendMessageTimeoutW((HANDLE)(uintptr_t)tx->root, message, (WPARAM)nonce,
            (LPARAM)command, 0x23, timeout, &result);
    }
    uint32_t armed = 0;
    __atomic_compare_exchange_n(&shared->claim, &armed, 2, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
    uint32_t flags = __atomic_load_n(&shared->claim, __ATOMIC_ACQUIRE) == 1 ? 1 : 0;
    if (sent && shared->acknowledgement == command && shared->acknowledged_operation == 4) flags |= 2;
    QueryInterruptTimePrecise(&ended);
    if (ended < started || ended - started > (uint64_t)timeout * 10000) flags |= 4;
    return flags;
}
__declspec(dllexport) LRESULT TypeLayCallWndProc(int code, WPARAM wp, LPARAM lp) {
    if (code >= 0) {
        const CallMessage *msg = (const CallMessage *)lp;
        static uint32_t source_install, source_revoke, source_poll, source_activate;
        if (!source_install) source_install = RegisterWindowMessageW((const WCHAR *)L"TypeLay.NativeInput.SourceInstall.v2");
        if (!source_revoke) source_revoke = RegisterWindowMessageW((const WCHAR *)L"TypeLay.NativeInput.SourceRevoke.v2");
        if (!source_poll) source_poll = RegisterWindowMessageW((const WCHAR *)L"TypeLay.NativeInput.SourcePoll.v2");
        if (!source_activate) source_activate = RegisterWindowMessageW((const WCHAR *)L"TypeLay.NativeInput.SourceActivate.v2");
        if (source_install && msg->message == source_install) return source_message(code, wp, lp, 1);
        if (source_revoke && msg->message == source_revoke) return source_message(code, wp, lp, 2);
        if (source_poll && msg->message == source_poll) return source_message(code, wp, lp, 3);
        if (source_activate && msg->message == source_activate) return source_message(code, wp, lp, 4);
        source_invalidate(msg);
        if (msg->message == 0x82) {
            if (msg->hwnd == owned.hwnd) owned.retired = 1;
            RemovePropW(msg->hwnd, property);
        }
        static uint32_t message, owned_capture_message;
        if (!message) message = RegisterWindowMessageW(message_name);
        if (!owned_capture_message) owned_capture_message = RegisterWindowMessageW(owned_capture_message_name);
        int capture = owned_capture_message && msg->message == owned_capture_message;
        if ((message && msg->message == message) || capture) {
            WCHAR name[80];
            const WCHAR prefix[] = L"Local\\TypeLay.Input.";
            int i = 0;
            for (; prefix[i]; i++) name[i] = prefix[i];
            const WCHAR digits[] = L"0123456789abcdef";
            for (int part = 0; part < 2; part++) {
                uint64_t bits = part ? (uint64_t)msg->lparam : (uint64_t)msg->wparam;
                for (int shift = 60; shift >= 0; shift -= 4) name[i++] = digits[(bits >> shift) & 15];
            }
            name[i] = 0;
            HANDLE mapping = OpenFileMappingW(6, 0, name);
            if (mapping) {
                Transaction *shared = MapViewOfFile(mapping, 6, 0, 0, sizeof(Transaction));
                if (shared) {
                    Transaction *tx = HeapAlloc(GetProcessHeap(), 8, sizeof(Transaction));
                    if (tx) {
                        copy(tx, shared, sizeof(Transaction));
                        if (tx->status == 0 && tx->hwnd == (uint64_t)(uintptr_t)msg->hwnd) {
                            if (capture) execute_owned_capture(tx);
                            else execute(tx);
                            copy(shared, tx, sizeof(Transaction));
                        }
                        wipe(tx, sizeof(Transaction));
                        HeapFree(GetProcessHeap(), 0, tx);
                    }
                    UnmapViewOfFile(shared);
                }
                CloseHandle(mapping);
            }
        }
    }
    return CallNextHookEx(0, code, wp, lp);
}
int DllMain(HANDLE module, uint32_t reason, void *reserved) {
    (void)module; (void)reason; (void)reserved; return 1;
}
