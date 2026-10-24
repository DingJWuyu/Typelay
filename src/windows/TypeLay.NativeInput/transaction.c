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
typedef struct { uint32_t device_type, origin_id; } InputMessageSource;
_Static_assert(sizeof(InputMessageSource) == 8, "Windows INPUT_MESSAGE_SOURCE layout");
API int GetCurrentInputMessageSource(InputMessageSource *);
API LRESULT SendMessageTimeoutW(HANDLE, uint32_t, WPARAM, LPARAM, uint32_t, uint32_t, uintptr_t *);
API HANDLE GetKeyboardLayout(uint32_t);
API int16_t GetKeyState(int);
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
// Source-only diagnostics; managed consumers preserve conservative denials.
#define SOURCE_PENDING_WORK 0x10000u
#define SOURCE_ORIGIN_UNTRUSTED 0x20000u
#define SOURCE_FRESHNESS_UNPROVEN 0x20001u
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
typedef struct {
    HANDLE hwnd; uint32_t message; WPARAM wparam; LPARAM lparam;
    uint32_t time; Point point; uint32_t private_value;
} QueueMessage;
_Static_assert(sizeof(QueueMessage) == 48, "Windows x64 MSG layout");
static int same_queue_message(const QueueMessage *a, const QueueMessage *b) {
    return a->hwnd == b->hwnd && a->message == b->message && a->wparam == b->wparam
        && a->lparam == b->lparam && a->time == b->time && a->point.x == b->point.x
        && a->point.y == b->point.y && a->private_value == b->private_value;
}
static const WCHAR property[] = L"TypeLay.NativeInput.Lifetime.v2";
static const WCHAR focus_property[] = L"TypeLay.OwnedFocusRevision.v1";
// Only the independent practice process opts in. Its GUI thread retains this
// record through retirement and until process exit; a wire capture/release may
// never create, remove or rotate it. The process pins this DLL for that lifetime.
static struct { HANDLE hwnd; uint32_t thread; uint64_t cookie, focus_revision; int retired, automatic; } owned;
static const WCHAR message_name[] = L"TypeLay.NativeInput.Transaction.v2";
static const WCHAR owned_capture_message_name[] = L"TypeLay.NativeInput.OwnedCapture.v2";

__declspec(dllexport) uint32_t TypeLayNativeProtocolVersion(void) { return VERSION; }

static int foreground_identity(HANDLE hwnd, HANDLE root, uint32_t process, uint32_t thread) {
    HANDLE foreground = GetForegroundWindow();
    if (foreground == root) return 1;
    // UIA may leave the exact ordinary Edit as the raw foreground HWND. It is
    // not a new root or permission for sibling fields/owned dialogs. This shape
    // check never substitutes the caller's lifetime, IME or original deadline.
    uint32_t pid = 0;
    return hwnd && hwnd != root && foreground == hwnd && owned.hwnd != hwnd
        && GetAncestor(root, 2) == root && GetAncestor(hwnd, 2) == root
        && GetWindowThreadProcessId(hwnd, &pid) == thread && pid == process
        && GetWindowThreadProcessId(root, &pid) == thread && pid == process;
}
static int foreground_window(const Transaction *tx) {
    return foreground_identity((HANDLE)(uintptr_t)tx->hwnd, (HANDLE)(uintptr_t)tx->root, tx->pid, tx->thread);
}
static int foreground_source(const Transaction *tx) {
    // This hook executes on the source's GUI thread, so GetFocus identifies the
    // exact field, rather than merely another field inside the same root window.
    return foreground_window(tx)
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
// Window/class/style metadata only: no stock-control or IMM callback. Keep this
// part available after a final reentrant input query without reopening that query.
static WNDPROC qualify_field_metadata(HANDLE hwnd, HANDLE root, uint32_t process, uint32_t thread, int32_t *multiline) {
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
    if (style & (0x8 | 0x10 | 0x20 | 0x800 | 0x2000)) return 0;
    if (multiline) *multiline = (style & 4) != 0;
    return proc;
}
static WNDPROC qualify_field(HANDLE hwnd, HANDLE root, uint32_t process, uint32_t thread, int32_t *multiline) {
    WNDPROC proc = qualify_field_metadata(hwnd, root, process, thread, multiline);
    return proc && !CallWindowProcW(proc, hwnd, 0xd2, 0, 0) ? proc : 0;
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
// Source-only guard. The source slot is bound by the outer callback and cannot
// be replaced while it is active; this never uses owned practice qualification.
static int source_input_current(Transaction *tx);
typedef struct {
    uint32_t *claim; const uint32_t *reason;
    QueueMessage *message; const QueueMessage *expected_message;
} SourceAdmission;
static void execute_guarded(Transaction *tx, uint64_t focus_revision, int *accepted,
    const OwnedInputContext *expected_input, const SourceAdmission *admission) {
    tx->status = 2; tx->reason = 6; // RejectedBeforeMutation / Unsupported.
    // These private pointers are a pair, never a remotely selected bypass.
    if (admission && ((admission->message == 0) != (admission->expected_message == 0))) return;
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
        // Both source routes recheck current state before the first input side
        // effect. Neither route pumps/removes messages to obtain permission.
        if (*admission->reason || ime(hwnd) != 1) { tx->reason = 9; return; }
        if (read_text(tx, proc) - 1 != tx->length || !equal(tx->observed, tx->expected, tx->length)) {
            tx->reason = 3; return;
        }
        if (tx->selection_start != tx->expected_selection_start || tx->selection_end != tx->expected_selection_end) {
            tx->reason = 4; return;
        }
        if (!source_input_current(tx)) return;
        if (!admission->message) {
            // Sent activation can overtake queued work: retain its conservative
            // full mask and diagnostic. This clears queried new-message bits,
            // so it remains restricted to the controlled prototype pump.
            uint32_t pending_work = GetQueueStatus(0x1dff) >> 16;
            if (pending_work) { tx->reason = SOURCE_PENDING_WORK | pending_work; return; }
        }
        // The retrieved route owns this actual PM_REMOVE callback, not a queue
        // emptiness/completion witness. Unrelated hints cannot revoke it.
        if (admission->message && !same_queue_message(admission->message, admission->expected_message)) {
            tx->reason = 15; return;
        }
        uint32_t armed = 0;
        // This actual aligned shared word is the only acceptance linearization
        // point. A timeout's Withdrawn value can never be reset or overwritten.
        if (!__atomic_compare_exchange_n(admission->claim, &armed, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
            tx->reason = 14; return;
        }
        // This source-thread MSG is still inside the same PM_REMOVE callback.
        // No call, allocation or logging may separate claim from this local
        // commitment. A later refusal/unknown must not restore or reinject Tab.
        if (admission->message) admission->message->message = 0; // WM_NULL
    }
    // Record owned acceptance or actual-MSG tail debt before SETSEL can reenter.
    // Sent activation never supplies this output. The owned caller consumes only
    // on true; the actual MSG above is already committed, including on a later
    // refusal/unknown. Neither route may synthesize a replacement Tab.
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
    if (admission && !source_input_current(tx)) return;
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
    uint32_t claim, invoked; // Claim: Armed=0, Claimed=1, Withdrawn=2, Prepared=3; never reused.
    Transaction tx;
    uint64_t verified_at_ms; // Actual successful source verification, never poll time.
    uint64_t witness_lifetime[2], witness_boundary_tick, witness_boundary_sequence;
} SourceInstallation;
_Static_assert(offsetof(SourceInstallation, claim) == 64, "Aligned claim word");
_Static_assert(offsetof(SourceInstallation, tx) == 72, "Source envelope layout");
_Static_assert(sizeof(SourceInstallation) == 49376, "Source mapping length");
_Static_assert(offsetof(SourceInstallation, verified_at_ms) == 49336, "Source completion time layout");
#define SOURCE_WITNESS_CAPACITY 256
// Source-lifetime SPSC metadata, never text. The existing App LL thread is the
// sole producer; this source GUI thread is the sole consumer. No timestamp search.
typedef struct {
    uint64_t sequence, nonce, observed_ms, hwnd;
    uint32_t time, key, scan, flags, eligible, repeated;
    uint64_t reserved;
} SourceKeyWitness;
typedef struct {
    uint32_t version, bytes, poison, writer_ready;
    uint64_t lifetime[2], hwnd, root;
    uint32_t pid, thread;
    uint64_t published, consumed;
    uint64_t attached_nonce, attached_generation[2], attached_grant[2];
    uint64_t initial_tick, attached_sequence;
    uint32_t attached, recording_active;
    SourceKeyWitness records[SOURCE_WITNESS_CAPACITY];
} SourceWitness;
_Static_assert(sizeof(SourceKeyWitness) == 64, "Source witness record layout");
_Static_assert(offsetof(SourceWitness, records) == 136, "Source witness header layout");
_Static_assert(sizeof(SourceWitness) == 16520, "Source witness mapping length");

// One candidate per target process, owned by one GUI thread. Only owner is
// atomic; all remaining members are exclusively accessed by that thread.
// No retained body, cross-callback mapped view, subclass or module pin.
typedef struct {
    uint32_t owner;
    uint32_t busy, reason, completed_status, completed_reason;
    uint64_t nonce, generation[2], grant[2], hwnd, root, cookie, witness_lifetime[2];
    int64_t expires;
    OwnedInputContext input; // Source-local scalar identity, never sent over the wire.
    int press_owed, press_disabled, press_ordered;
    uint64_t press_boundary, witness_sequence;
    SourceWitness *inflight_witness; // Only the current guarded callback owns this view.
    uint32_t press_bits; // Accepted actual MSG's scan/extended/context identity.
} SourceSlot;
static SourceSlot source;
// Scalar source-lifetime attachment survives grant retirement. Views are opened
// only for the current callback; a stopped App cannot leave a dangling pointer.
static struct {
    uint32_t thread, pid;
    uint64_t lifetime[2], hwnd, root;
} source_witness_binding;
static uint32_t source_witness_binding_busy;
// Covers unmatched requests too: a downstream hook cannot retire A, install B,
// and let an outer rejected B request publish a false quiet ACK over that slot.
static uint32_t source_callback;
__declspec(dllexport) uint32_t TypeLaySourceInstallationVersion(void) { return 5; }

static int source_input_message(uint32_t message) {
    switch (message) {
    case 0x100: case 0x101: case 0x102: case 0x104: case 0x105:
    case 0x201: case 0x204: case 0x207:
        return 1;
    default: return 0;
    }
}
// Narrow same-field obligation, independent of the expired write grant. Losing
// certainty seals consumption; only a known scope departure or consumed original
// up settles it. No equality, poll, deadline or retirement can re-arm this press.
static void source_press_departed(void) {
    source.press_disabled = 1;
    source.press_owed = 0;
}
static int source_press_scope(void) {
    HANDLE hwnd = (HANDLE)(uintptr_t)source.hwnd, root = (HANDLE)(uintptr_t)source.root;
    uint32_t thread = GetCurrentThreadId(), process = GetCurrentProcessId(), pid = 0;
    if (GetFocus() != hwnd || !foreground_identity(hwnd, root, process, thread)
        || (uint64_t)(uintptr_t)GetPropW(hwnd, property) != source.cookie) {
        source_press_departed();
        return 0;
    }
    if (!qualify_field_metadata(hwnd, root, process, thread, 0)
        || GetWindowThreadProcessId(root, &pid) != thread || pid != process
        || !IsWindowVisible(hwnd) || !IsWindowVisible(root) || !IsWindowEnabled(root) || IsIconic(root)) {
        source.press_disabled = 1;
        return 0;
    }
    return source.press_owed && !source.press_disabled;
}
static int source_press_current(void) {
    if (!source_press_scope()) return 0;
    OwnedInputContext current;
    if (!read_input_context((HANDLE)(uintptr_t)source.hwnd, &current)) {
        // Unknown is not evidence of a physical up or departure.
        source.press_disabled = 1;
        return 0;
    }
    if (current.ime_context != source.input.ime_context
        || current.keyboard_layout != source.input.keyboard_layout) {
        source_press_departed();
        return 0;
    }
    // IMM may have reentered. Only callback-free metadata closes this check.
    return source_press_scope();
}
static void source_invalidate(const CallMessage *msg) {
    if (__atomic_load_n(&source.owner, __ATOMIC_ACQUIRE) != GetCurrentThreadId()) return;
    if (source.press_owed
        && ((msg->hwnd == (HANDLE)(uintptr_t)source.hwnd
                && (msg->message == 0x08 || msg->message == 0x82 || msg->message == 0x51
                    || msg->message == 0x10d || msg->message == 0x10e || msg->message == 0x10f || msg->message == 0x281))
            || (msg->hwnd == (HANDLE)(uintptr_t)source.root
                && (msg->message == 0x82 || (msg->message == 0x06 && !(msg->wparam & 0xffff))
                    || (msg->message == 0x1c && !msg->wparam))))) source_press_departed();
    if (source.reason) return;
    if (msg->hwnd == (HANDLE)(uintptr_t)source.hwnd) {
        if (source_input_message(msg->message)) { source.reason = 15; return; }
        switch (msg->message) {
        case 0x08: case 0x82: // KILLFOCUS / NCDESTROY: a round trip cannot revive it.
            source.reason = 2; break;
        case 0x0c: case 0xb1: case 0xc2: case 0xcc: case 0xcf:
        case 0x51: case 0x10d: case 0x10e: case 0x10f:
            source.reason = 15; break;
        case 0x281: // SETCONTEXT activation/deactivation only revoke, never re-arm.
            source.reason = 9; break;
        }
    }
    if (msg->hwnd == (HANDLE)(uintptr_t)source.root
        && (msg->message == 0x82 || (msg->message == 0x06 && !(msg->wparam & 0xffff))
            || (msg->message == 0x1c && !msg->wparam))) source.reason = 2;
}
static uint32_t source_withdraw(SourceInstallation *shared) {
    uint32_t state = __atomic_load_n(&shared->claim, __ATOMIC_ACQUIRE);
    while (state == 0 || state == 3) {
        if (__atomic_compare_exchange_n(&shared->claim, &state, 2, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) return 2;
    }
    return state;
}

static int source_identity(const SourceInstallation *request, uint64_t nonce) {
    return source.nonce == nonce
        && source.hwnd == request->tx.hwnd && source.root == request->tx.root
        && source.cookie == request->tx.cookie && source.expires == request->tx.authorization_deadline_ms
        && source.generation[0] == request->generation[0] && source.generation[1] == request->generation[1]
        && source.grant[0] == request->grant[0] && source.grant[1] == request->grant[1]
        && source.witness_lifetime[0] == request->witness_lifetime[0]
        && source.witness_lifetime[1] == request->witness_lifetime[1];
}
static uint32_t source_current(const Transaction *tx) {
    HANDLE hwnd = (HANDLE)(uintptr_t)tx->hwnd, root = (HANDLE)(uintptr_t)tx->root;
    uint32_t pid = 0;
    if (source.reason) return source.reason;
    if (source.inflight_witness
        && (__atomic_load_n(&source.inflight_witness->poison, __ATOMIC_ACQUIRE)
            || __atomic_load_n(&source.inflight_witness->writer_ready, __ATOMIC_ACQUIRE) != 1
            || __atomic_load_n(&source.inflight_witness->consumed, __ATOMIC_ACQUIRE) != source.witness_sequence))
        return SOURCE_FRESHNESS_UNPROVEN;
    if (__atomic_load_n(&source.owner, __ATOMIC_ACQUIRE) != GetCurrentThreadId()
        || source.hwnd != tx->hwnd || source.root != tx->root || source.cookie != tx->cookie
        || source.expires != tx->authorization_deadline_ms || owned.hwnd == hwnd
        || !qualify_field_metadata(hwnd, root, tx->pid, tx->thread, 0)
        || GetWindowThreadProcessId(root, &pid) != tx->thread || pid != tx->pid
        || !foreground_source(tx) || !IsWindowVisible(hwnd) || !IsWindowVisible(root)
        || !IsWindowEnabled(root) || IsIconic(root)
        || (uint64_t)(uintptr_t)GetPropW(hwnd, property) != tx->cookie) return 2;
    return authorization_current(tx) && GetTickCount64() <= tx->deadline ? 0 : 14;
}
static int source_input_current(Transaction *tx) {
    uint32_t reason = source_current(tx);
    if (!reason && !qualify(tx)) reason = 2;
    // Qualification contains a stock-control call. Sample after it and every
    // other body/selection callback, then close only with callback-free identity,
    // monotonic revocation and original clock checks. This is not a TSF profile
    // or continuous composition witness; equal samples cannot supply one.
    OwnedInputContext current;
    if (!reason) {
        if (!source.input.ime_context || !source.input.keyboard_layout
            || !read_input_context((HANDLE)(uintptr_t)tx->hwnd, &current)) {
            reason = 9;
            if (source.press_owed) source.press_disabled = 1;
        } else if (current.ime_context != source.input.ime_context
            || current.keyboard_layout != source.input.keyboard_layout) {
            reason = 9;
            if (source.press_owed) source_press_departed();
        }
    }
    if (!reason) reason = source_current(tx);
    if (reason && !source.reason) source.reason = reason;
    if (source.reason) { tx->reason = source.reason; return 0; }
    return 1;
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
    uint32_t current = source_current(tx);
    if (current) return current;
    if (!qualify(tx)) return 6;
    // Freeze once for this grant, before the body/selection reads. Later samples
    // compare only; neither poll nor restored state may rebind it.
    if (!read_input_context((HANDLE)(uintptr_t)tx->hwnd, &source.input)) return 9;
    current = source_current(tx);
    if (current) return current;
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
    if (!reason && !source_input_current(tx)) reason = tx->reason;
    return reason;
}
// Both transports execute the same ordinary-source plan. Only the message
// callback supplies an actual MSG; the inactive cross-thread route is unchanged.
static void source_execute_once(SourceInstallation *shared, SourceInstallation *request,
    QueueMessage *message, const QueueMessage *expected_message, int ordered) {
    Transaction *tx = &request->tx;
    if (!source.reason && request->state == 1) {
        SourceAdmission admission = { &shared->claim, &source.reason, message, expected_message };
        if (message && expected_message) {
            source.press_bits = (uint32_t)expected_message->lparam & 0x21ff0000u;
            source.press_ordered = ordered;
            source.press_boundary = request->witness_boundary_tick;
        }
        execute_guarded(tx, 0, message ? &source.press_owed : 0, 0, &admission);
    } else { tx->status = 2; tx->reason = source.reason ? source.reason : 6; }
    if (tx->status == 1) {
        uint64_t verified;
        QueryInterruptTimePrecise(&verified);
        request->verified_at_ms = verified / 10000;
    }
    source.completed_status = tx->status;
    source.completed_reason = tx->reason;
    if (!source.reason) source.reason = 15; // Single use, including preclaim refusal.
}
static void source_publish_completion(SourceInstallation *shared, const SourceInstallation *request) {
    if (request->tx.status != 1) return;
    shared->tx.length = request->tx.length;
    shared->tx.selection_start = request->tx.selection_start;
    shared->tx.selection_end = request->tx.selection_end;
    shared->tx.ime = request->tx.ime;
    copy(shared->tx.observed, request->tx.observed, sizeof(shared->tx.observed));
    shared->verified_at_ms = request->verified_at_ms;
}
static HANDLE source_mapping(uint64_t nonce) {
    WCHAR name[64];
    const WCHAR prefix[] = L"Local\\TypeLay.Source.";
    const WCHAR digits[] = L"0123456789abcdef";
    int i = 0;
    for (; prefix[i]; i++) name[i] = prefix[i];
    uint64_t bits = nonce;
    for (int shift = 60; shift >= 0; shift -= 4) name[i++] = digits[(bits >> shift) & 15];
    name[i] = 0;
    return OpenFileMappingW(6, 0, name);
}
static int source_uses_witness(const SourceInstallation *request) {
    return request->witness_lifetime[0] || request->witness_lifetime[1];
}
static HANDLE source_witness_mapping(const uint64_t lifetime[2]) {
    WCHAR name[80];
    const WCHAR prefix[] = L"Local\\TypeLay.SourceWitness.";
    const WCHAR digits[] = L"0123456789abcdef";
    const unsigned char *bytes = (const unsigned char *)lifetime;
    int length = 0;
    for (; prefix[length]; length++) name[length] = prefix[length];
    for (int i = 0; i < 16; i++) {
        name[length++] = digits[bytes[i] >> 4];
        name[length++] = digits[bytes[i] & 15];
    }
    name[length] = 0;
    return OpenFileMappingW(6, 0, name);
}
static int source_witness_matches(const SourceWitness *witness, const SourceInstallation *request) {
    return witness->version == 2 && witness->bytes == sizeof(SourceWitness)
        && __atomic_load_n(&witness->writer_ready, __ATOMIC_ACQUIRE) == 1
        && !__atomic_load_n(&witness->poison, __ATOMIC_ACQUIRE)
        && witness->lifetime[0] == request->witness_lifetime[0]
        && witness->lifetime[1] == request->witness_lifetime[1]
        && witness->hwnd == request->tx.hwnd && witness->root == request->tx.root
        && witness->pid == request->tx.pid && witness->thread == request->tx.thread;
}
static int source_attach_witness(const SourceInstallation *request, uint64_t nonce) {
    HANDLE mapping = source_witness_mapping(request->witness_lifetime);
    SourceWitness *witness = mapping ? MapViewOfFile(mapping, 6, 0, 0, sizeof(SourceWitness)) : 0;
    int attached = 0;
    uint32_t empty = 0;
    int locked = witness && source_witness_matches(witness, request)
        && __atomic_compare_exchange_n(&source_witness_binding_busy, &empty, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
    if (locked) {
        uint64_t published = __atomic_load_n(&witness->published, __ATOMIC_ACQUIRE);
        uint64_t consumed = __atomic_load_n(&witness->consumed, __ATOMIC_ACQUIRE);
        if (consumed <= published && published - consumed <= SOURCE_WITNESS_CAPACITY
            && !source.reason && source_identity(request, nonce)) {
            // Source hook + this Prepared owner now exist, and the exact App LL
            // writer already acknowledged its dormant mapping. Preserve any
            // active history across grants; attachment is not Arm authority.
            __atomic_store_n(&witness->attached, 0, __ATOMIC_RELEASE);
            witness->attached_nonce = nonce;
            copy(witness->attached_generation, request->generation, sizeof(request->generation));
            copy(witness->attached_grant, request->grant, sizeof(request->grant));
            witness->attached_sequence = published;
            source_witness_binding.thread = request->tx.thread;
            source_witness_binding.pid = request->tx.pid;
            source_witness_binding.hwnd = request->tx.hwnd;
            source_witness_binding.root = request->tx.root;
            copy(source_witness_binding.lifetime, request->witness_lifetime, sizeof(request->witness_lifetime));
            __atomic_store_n(&witness->attached, 1, __ATOMIC_RELEASE);
            attached = 1;
        }
    }
    if (locked) __atomic_store_n(&source_witness_binding_busy, 0, __ATOMIC_RELEASE);
    if (witness) UnmapViewOfFile(witness);
    if (mapping) CloseHandle(mapping);
    return attached;
}

static int source_witness_bound(const SourceWitness *witness) {
    return witness->version == 2 && witness->bytes == sizeof(SourceWitness)
        && __atomic_load_n(&witness->writer_ready, __ATOMIC_ACQUIRE) == 1
        && !__atomic_load_n(&witness->poison, __ATOMIC_ACQUIRE)
        && witness->lifetime[0] == source_witness_binding.lifetime[0]
        && witness->lifetime[1] == source_witness_binding.lifetime[1]
        && witness->hwnd == source_witness_binding.hwnd && witness->root == source_witness_binding.root
        && witness->pid == source_witness_binding.pid && witness->thread == source_witness_binding.thread;
}
static int source_time_after(uint32_t time, uint64_t boundary, uint64_t now) {
    // Normal Windows message time, not an origin credential or a unique ID.
    // Same-tick, future and wrap-ambiguous observations conservatively refuse.
    return boundary && now >= boundary && now - boundary <= 0x7fffffffu
        && (int32_t)(time - (uint32_t)boundary) > 0 && (int32_t)((uint32_t)now - time) >= 0;
}
static int source_witness_tuple(const SourceKeyWitness *edge, const QueueMessage *message) {
    uint32_t key = edge->key;
    if (key == 0xa0 || key == 0xa1) key = 0x10;
    if (key == 0xa2 || key == 0xa3) key = 0x11;
    if (key == 0xa4 || key == 0xa5) key = 0x12;
    uint32_t bits = (uint32_t)message->lparam;
    int down = message->message == 0x100 || message->message == 0x104;
    return edge->key > 0 && edge->key <= 254 && edge->scan > 0 && edge->scan <= 255
        && !(edge->flags & ~0xb3u)
        && edge->hwnd == (uint64_t)(uintptr_t)message->hwnd && key == message->wparam
        && edge->time == message->time && edge->scan == ((bits >> 16) & 0xff)
        && ((edge->flags & 1) != 0) == ((bits & 0x1000000u) != 0)
        && ((edge->flags & 0x20) != 0) == ((bits & 0x20000000u) != 0)
        && ((edge->flags & 0x80) == 0) == down
        && (!down || edge->repeated == ((bits & 0x40000000u) != 0))
        && (bits & 0xffff) == 1 && edge->eligible <= 1 && edge->repeated <= 1 && !edge->reserved;
}
static int source_witness_take(const QueueMessage *message, SourceKeyWitness *edge, uint32_t thread) {
    // A different source GUI thread can finish an old callback while a new
    // lifetime attaches. Do not race the scalar attachment or wait for it.
    uint32_t empty = 0;
    if (!__atomic_compare_exchange_n(&source_witness_binding_busy, &empty, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE))
        return 0;
    if (source_witness_binding.thread != thread || source_witness_binding.hwnd != (uint64_t)(uintptr_t)message->hwnd) {
        __atomic_store_n(&source_witness_binding_busy, 0, __ATOMIC_RELEASE);
        return 0;
    }
    HANDLE mapping = source_witness_mapping(source_witness_binding.lifetime);
    SourceWitness *witness = mapping ? MapViewOfFile(mapping, 6, 0, 0, sizeof(SourceWitness)) : 0;
    int matched = 0;
    if (witness && source_witness_bound(witness)
        && __atomic_load_n(&witness->recording_active, __ATOMIC_ACQUIRE) == 1) {
        uint64_t initial = __atomic_load_n(&witness->initial_tick, __ATOMIC_ACQUIRE);
        uint64_t now = GetTickCount64();
        // Dormant or normal pre-observation input cannot borrow even an equal
        // tuple from the head. The LL writer excludes this same initial prefix.
        if (!initial || (now >= initial && now - initial <= 0x7fffffffu
                && (int32_t)(message->time - (uint32_t)initial) <= 0)) goto witness_done;
        uint64_t published = __atomic_load_n(&witness->published, __ATOMIC_ACQUIRE);
        uint64_t consumed = __atomic_load_n(&witness->consumed, __ATOMIC_ACQUIRE);
        if (published < consumed || published - consumed > SOURCE_WITNESS_CAPACITY || consumed == UINT64_MAX) {
            __atomic_store_n(&witness->poison, 1, __ATOMIC_RELEASE);
        } else if (published != consumed) {
            const SourceKeyWitness *head = &witness->records[consumed % SOURCE_WITNESS_CAPACITY];
            uint64_t sequence = __atomic_load_n(&head->sequence, __ATOMIC_ACQUIRE);
            copy(edge, head, sizeof(*edge));
            if (sequence != consumed + 1 || __atomic_load_n(&head->sequence, __ATOMIC_ACQUIRE) != sequence) {
                __atomic_store_n(&witness->poison, 1, __ATOMIC_RELEASE);
            } else if (source_witness_tuple(edge, message)) {
                // Consume only the head, including ineligible pre-Arm history.
                // Tuple equality rejects mismatches; it never supplies origin.
                __atomic_store_n(&witness->consumed, sequence, __ATOMIC_RELEASE);
                matched = 1;
            } else {
                __atomic_store_n(&witness->poison, 1, __ATOMIC_RELEASE);
            }
        } else {
            __atomic_store_n(&witness->poison, 1, __ATOMIC_RELEASE);
        }
        // No grant refresh clears a record or changes the initial boundary.
    }
witness_done:
    if (witness) UnmapViewOfFile(witness);
    if (mapping) CloseHandle(mapping);
    __atomic_store_n(&source_witness_binding_busy, 0, __ATOMIC_RELEASE);
    return matched;
}
static int source_witness_sequence_current(uint64_t sequence) {
    HANDLE mapping = source_witness_mapping(source_witness_binding.lifetime);
    SourceWitness *witness = mapping ? MapViewOfFile(mapping, 6, 0, 0, sizeof(SourceWitness)) : 0;
    int current = witness && source_witness_bound(witness)
        && __atomic_load_n(&witness->consumed, __ATOMIC_ACQUIRE) == sequence;
    if (witness) UnmapViewOfFile(witness);
    if (mapping) CloseHandle(mapping);
    return current;
}
static int source_witness_admits(const SourceKeyWitness *edge, const QueueMessage *message,
    const SourceInstallation *request) {
    return source_uses_witness(request) && edge->nonce == source.nonce && edge->eligible == 1
        && edge->sequence > request->witness_boundary_sequence
        && edge->observed_ms >= (uint64_t)request->observed_at_ms
        && edge->observed_ms <= (uint64_t)request->tx.authorization_deadline_ms
        && source_time_after(message->time, request->witness_boundary_tick, GetTickCount64());
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
    HANDLE mapping = source_mapping((uint64_t)msg->wparam);
    SourceInstallation *shared = mapping ? MapViewOfFile(mapping, 6, 0, 0, sizeof(SourceInstallation)) : 0;
    SourceInstallation *request = shared ? HeapAlloc(GetProcessHeap(), 8, sizeof(SourceInstallation)) : 0;
    int matched = 0, quiet = 0, respond = 0;
    if (request) {
        copy(request, shared, sizeof(SourceInstallation));
        respond = (uint64_t)msg->lparam > request->acknowledgement;
        Transaction *tx = &request->tx;
        if (request->version == 5 && request->bytes == sizeof(SourceInstallation)
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
                copy(source.witness_lifetime, request->witness_lifetime, sizeof(source.witness_lifetime));
                source.hwnd = tx->hwnd; source.root = tx->root; source.cookie = tx->cookie;
                source.expires = tx->authorization_deadline_ms;
                source.input.ime_context = source.input.keyboard_layout = 0;
                source.press_owed = source.press_disabled = 0;
                source.press_bits = 0;
                source.busy++;
                uint32_t reason = source_preflight(request);
                if (!reason && source_uses_witness(request)
                    && (__atomic_load_n(&shared->claim, __ATOMIC_ACQUIRE) != 3
                        || !source_attach_witness(request, (uint64_t)msg->wparam)))
                    reason = SOURCE_FRESHNESS_UNPROVEN;
                if (reason && !source.reason) source.reason = reason;
                source.busy--;
            }
            matched = __atomic_load_n(&source.owner, __ATOMIC_ACQUIRE) == thread && source_identity(request, (uint64_t)msg->wparam);
            if (matched) {
                source.busy++;
                if (operation == 2 && !source.reason) source.reason = 15;
                if (!source.reason && !authorization_current(tx)) source.reason = 14;
                if (operation == 4 && source_uses_witness(request) && !source.reason)
                    source.reason = SOURCE_FRESHNESS_UNPROVEN;
                if (operation == 4 && !source.completed_status)
                    source_execute_once(shared, request, 0, 0, 0);
                // Only an explicit retirement may relinquish the source slot.
                // A nested revoke records cancellation but cannot claim quiet.
                if (operation == 2 && source.press_owed) source_press_scope();
                quiet = operation == 2 && source.busy == 1 && !source.press_owed;
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
            // Includes downstream install/poll reentry. An observed mismatch
            // latches revocation even if the next poll sees the old handles again.
            if (!source.reason && !source.completed_status) source_input_current(&request->tx);
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
            if (operation == 4) source_publish_completion(shared, request);
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
static LRESULT source_press_message(int code, WPARAM wp, LPARAM lp, const QueueMessage *entry, uint64_t sequence) {
    QueueMessage *message = (QueueMessage *)lp;
    uint32_t thread = GetCurrentThreadId(), empty_callback = 0;
    int up = message->message == 0x101 && ((uintptr_t)message->lparam & 0xc0000000u) == 0xc0000000u;
    int repeat = message->message == 0x100 && ((uintptr_t)message->lparam & 0xc0000000u) == 0x40000000u;
    if ((!up && !repeat) || ((uint32_t)message->lparam & 0x21ff0000u) != source.press_bits) {
        // A new first Tab or mismatching press must not leave the old press able
        // to swallow a later repeat/up, even if its fields become equal again.
        source.press_disabled = 1;
        return CallNextHookEx(0, code, wp, lp);
    }
    if (source.press_disabled) return CallNextHookEx(0, code, wp, lp);
    if (!__atomic_compare_exchange_n(&source_callback, &empty_callback, thread, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
        source.press_disabled = 1;
        return CallNextHookEx(0, code, wp, lp);
    }
    QueueMessage original;
    copy(&original, entry, sizeof(original));
    source.busy++;
    LRESULT result = CallNextHookEx(0, code, wp, lp);
    if (!same_queue_message(message, &original)
        || (sequence && !source_witness_sequence_current(sequence))) source.press_disabled = 1;
    if (source.press_owed && !source.press_disabled && source_press_current()) {
        // The input query can reenter the hook chain, so compare the actual MSG
        // and monotonic press state again after it, before scalar consumption.
        if (same_queue_message(message, &original) && source.press_owed && !source.press_disabled
            && (!sequence || source_witness_sequence_current(sequence))) {
            message->message = 0;
            if (up) source.press_owed = 0;
        } else source.press_disabled = 1;
    }
    source.busy--;
    __atomic_store_n(&source_callback, 0, __ATOMIC_RELEASE);
    return result;
}
// A strict refusal has no body/selection side effects and never clears an owed
// press. The complete downstream chain stays inside the original lifetime gate.
static LRESULT source_refuse_strict_message(int code, WPARAM wp, LPARAM lp, uint32_t thread, uint32_t reason) {
    if (!source.reason) source.reason = reason;
    if (source.press_owed) source.press_disabled = 1;
    uint32_t empty_callback = 0;
    if (!__atomic_compare_exchange_n(&source_callback, &empty_callback, thread, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE))
        return CallNextHookEx(0, code, wp, lp);
    source.busy++;
    HANDLE mapping = source_mapping(source.nonce);
    SourceInstallation *shared = mapping ? MapViewOfFile(mapping, 6, 0, 0, sizeof(SourceInstallation)) : 0;
    if (shared) source_withdraw(shared);
    LRESULT result = CallNextHookEx(0, code, wp, lp);
    if (shared) UnmapViewOfFile(shared);
    if (mapping) CloseHandle(mapping);
    source.busy--;
    __atomic_store_n(&source_callback, 0, __ATOMIC_RELEASE);
    return result;
}
// Only the fixed local exports select the policy. No wire field, App setting
// or remote skip flag can turn a strict callback into the controlled mechanism.
static LRESULT source_get_message(int code, WPARAM wp, LPARAM lp, int strict_origin) {
    QueueMessage *message = (QueueMessage *)lp;
    if (code != 0 || wp != 1 || !message) return CallNextHookEx(0, code, wp, lp);
    // Explicit byte copy keeps the original MSG stack-local and this
    // freestanding ABI independent of CRT helpers.
    QueueMessage original;
    copy(&original, message, sizeof(original));
    const int key_message = original.message == 0x100 || original.message == 0x101
        || original.message == 0x104 || original.message == 0x105;
    const int tab = original.wparam == 9 && key_message;
    uint32_t strict_reason = 0;
    if (strict_origin && tab) {
        // First application OS call for this original MSG, including each tail.
        // Never query after CallNext/IMM or reuse another callback's result.
        InputMessageSource input = {0, 0};
        int queried = GetCurrentInputMessageSource(&input);
        strict_reason = queried && input.device_type == 1 && input.origin_id == 1
            ? (strict_origin == 2 ? 0 : SOURCE_FRESHNESS_UNPROVEN) : SOURCE_ORIGIN_UNTRUSTED;
        // HARDWARE includes allowed UIAccess. The retained rejection entry
        // still has no freshness authority. The ordered entry separately needs
        // its source-lifetime FIFO and LL-serialized boundary below; the source
        // category or MSG.time alone never authorizes acceptance.
    }
    uint32_t thread = GetCurrentThreadId(), empty_callback = 0;
    SourceKeyWitness witness_edge = {0};
    int witness_matched = 0;
    if (strict_origin == 2 && key_message)
        witness_matched = source_witness_take(&original, &witness_edge, thread);
    if (__atomic_load_n(&source.owner, __ATOMIC_ACQUIRE) != thread
        || source.hwnd != (uint64_t)(uintptr_t)original.hwnd)
        return CallNextHookEx(0, code, wp, lp);
    if ((source.witness_lifetime[0] || source.witness_lifetime[1]) && strict_origin != 2)
        return source_refuse_strict_message(code, wp, lp, thread, SOURCE_FRESHNESS_UNPROVEN);
    if (strict_origin == 2 && key_message && !witness_matched && !strict_reason)
        strict_reason = SOURCE_FRESHNESS_UNPROVEN;
    if (strict_reason) return source_refuse_strict_message(code, wp, lp, thread, strict_reason);
    if (source.press_owed && message->wparam == 9
        && (message->message == 0x100 || message->message == 0x101
            || message->message == 0x104 || message->message == 0x105))
    {
        if (strict_origin == 2 && (!source.press_ordered || witness_edge.nonce != source.nonce
            || !source_time_after(original.time, source.press_boundary, GetTickCount64())))
            return source_refuse_strict_message(code, wp, lp, thread, SOURCE_FRESHNESS_UNPROVEN);
        return source_press_message(code, wp, lp, &original, strict_origin == 2 ? witness_edge.sequence : 0);
    }
    if (source.completed_status) return CallNextHookEx(0, code, wp, lp);
    const int first_tab = message->message == 0x100 && message->wparam == 9
        && !((uintptr_t)message->lparam & 0xc0000000u)
        && GetKeyState(0x10) >= 0 && GetKeyState(0x11) >= 0 && GetKeyState(0x12) >= 0
        && GetKeyState(0x5b) >= 0 && GetKeyState(0x5c) >= 0;
    if (!first_tab) {
        // Observe only this grant's actual removed source input, not NOREMOVE,
        // category hints or hypothetical pre-installation dispatch history.
        // Revoke before the next hook can reenter; never consume this message.
        if (source_input_message(message->message)) {
            if (!source.reason) source.reason = 15;
            // The Arm caller cannot see this process-local reason latch. Also
            // withdraw a Prepared grant before downstream code can Arm it.
            HANDLE mapping = source_mapping(source.nonce);
            SourceInstallation *shared = mapping ? MapViewOfFile(mapping, 6, 0, 0, sizeof(SourceInstallation)) : 0;
            if (shared) {
                if (__atomic_load_n(&shared->claim, __ATOMIC_ACQUIRE) == 3) source_withdraw(shared);
                UnmapViewOfFile(shared);
            }
            if (mapping) CloseHandle(mapping);
        }
        return CallNextHookEx(0, code, wp, lp);
    }
    if (!__atomic_compare_exchange_n(&source_callback, &empty_callback, thread, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
        // A nested real removal invalidates the held outer key as well as the
        // inner one. An unchanged outer MSG is not proof that no input intervened.
        if (!source.reason) source.reason = 15;
        return CallNextHookEx(0, code, wp, lp);
    }
    // Protect the complete downstream chain as well as execution. Nested revoke
    // may latch a reason, but cannot acknowledge quiet, replace this grant or
    // accept an inner retrieved message while this original MSG is still held.
    uint64_t nonce = source.nonce;
    source.busy++;
    HANDLE mapping = source_mapping(nonce);
    SourceInstallation *shared = mapping ? MapViewOfFile(mapping, 6, 0, 0, sizeof(SourceInstallation)) : 0;
    HANDLE witness_mapping = strict_origin == 2 ? source_witness_mapping(source.witness_lifetime) : 0;
    SourceWitness *witness_view = witness_mapping ? MapViewOfFile(witness_mapping, 6, 0, 0, sizeof(SourceWitness)) : 0;
    if (strict_origin == 2) {
        if (!witness_view || !source_witness_bound(witness_view)) {
            if (!source.reason) source.reason = SOURCE_FRESHNESS_UNPROVEN;
        } else {
            source.inflight_witness = witness_view;
            source.witness_sequence = witness_edge.sequence;
        }
    }
    // An input observed while Prepared cannot be made eligible by an Arm in a
    // downstream/reentrant callback. Revoke before entering that chain.
    if (shared && __atomic_load_n(&shared->claim, __ATOMIC_ACQUIRE) == 3) {
        if (!source.reason) source.reason = 15;
        source_withdraw(shared);
    }
    LRESULT result = CallNextHookEx(0, code, wp, lp);
    SourceInstallation *request = shared ? HeapAlloc(GetProcessHeap(), 8, sizeof(SourceInstallation)) : 0;
    if (request) {
        copy(request, shared, sizeof(SourceInstallation));
        if (request->version == 5 && request->bytes == sizeof(SourceInstallation)
            && __atomic_load_n(&source.owner, __ATOMIC_ACQUIRE) == thread
            && source_identity(request, nonce) && request->tx.thread == thread
            && request->tx.pid == GetCurrentProcessId()) {
            uint32_t unused = 0;
            if (!__atomic_compare_exchange_n(&shared->invoked, &unused, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)
                && !source.reason) source.reason = 14;
            if (!same_queue_message(message, &original) && !source.reason) source.reason = 15;
            if (strict_origin == 2 && !source.reason && !source_witness_admits(&witness_edge, &original, request))
                source.reason = SOURCE_FRESHNESS_UNPROVEN;
            source_execute_once(shared, request, message, &original, strict_origin == 2);
            source_publish_completion(shared, request);
            shared->tx.status = source.completed_status;
            shared->tx.reason = source.completed_reason;
            shared->state = 2;
            // This was not a sent protocol command. Poll/retire will publish
            // their own real command ACK; never fabricate one for this MSG.
        } else {
            source.completed_status = 2; source.completed_reason = 2;
            if (!source.reason) source.reason = 2;
        }
        wipe(request, sizeof(SourceInstallation));
        HeapFree(GetProcessHeap(), 0, request);
    } else {
        source.completed_status = 2; source.completed_reason = 6;
        if (!source.reason) source.reason = 6;
    }
    source.inflight_witness = 0;
    if (witness_view) UnmapViewOfFile(witness_view);
    if (witness_mapping) CloseHandle(witness_mapping);
    if (shared) UnmapViewOfFile(shared);
    if (mapping) CloseHandle(mapping);
    source.busy--;
    __atomic_store_n(&source_callback, 0, __ATOMIC_RELEASE);
    return result;
}
// Controlled synthetic-input mechanism only. Existing Notepad/fixture evidence
// remains about transaction/receipt mechanics, not strict source eligibility.
__declspec(dllexport) LRESULT TypeLaySourceGetMessageProc(int code, WPARAM wp, LPARAM lp) {
    return source_get_message(code, wp, lp, 0);
}
// Isolated strict-origin rejection entry. Main/capability0 never installs it.
// Source category is checked; queue freshness is unresolved, so no strict write
// or tail consumption is advertised. The v5 envelope rejects old v4 consumers;
// this export retains its rejection-only policy. A consumer selects this exact symbol or fails closed on older DLLs.
__declspec(dllexport) LRESULT TypeLaySourceStrictGetMessageProc(int code, WPARAM wp, LPARAM lp) {
    return source_get_message(code, wp, lp, 1);
}
// Called only by the inactive Windows adapter, with its already-open local view.
// The view stays owned through source quiet ACK, including all timeout paths.
// Different focus queues are observed, not locked: only the isolated fixture is
// known not to attach queues. This is NOT an ordinary-process readiness proof.
__declspec(dllexport) uint32_t TypeLaySourceActivate(SourceInstallation *shared, uint32_t bytes,
    uint64_t nonce, uint64_t command, uint32_t timeout, uint32_t message) {
    if (!shared || bytes != sizeof(SourceInstallation) || shared->version != 5
        || shared->bytes != bytes || source_uses_witness(shared)
        || !command || !message || !timeout || timeout > 10) return 0;
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
        && foreground_window(tx)
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
        if (!source_install) source_install = RegisterWindowMessageW((const WCHAR *)L"TypeLay.NativeInput.SourceInstall.v5");
        if (!source_revoke) source_revoke = RegisterWindowMessageW((const WCHAR *)L"TypeLay.NativeInput.SourceRevoke.v5");
        if (!source_poll) source_poll = RegisterWindowMessageW((const WCHAR *)L"TypeLay.NativeInput.SourcePoll.v5");
        if (!source_activate) source_activate = RegisterWindowMessageW((const WCHAR *)L"TypeLay.NativeInput.SourceActivate.v5");
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

// A local nonblocking withdrawal is not source Quiet or permission to free the
// Broker owner. If Claim already won, the caller must preserve that press.
__declspec(dllexport) uint32_t TypeLaySourceWithdraw(SourceInstallation *shared, uint32_t bytes) {
    if (!shared || bytes != sizeof(SourceInstallation) || shared->version != 5 || shared->bytes != bytes) return 4;
    return source_withdraw(shared);
}

// Installation is inert until the App owns its local withdrawal control. Arm
// changes only the one-way claim word, never the original identity or deadline.
__declspec(dllexport) uint32_t TypeLaySourceArm(SourceInstallation *shared, uint32_t bytes) {
    if (!shared || bytes != sizeof(SourceInstallation) || shared->version != 5 || shared->bytes != bytes
        || source_uses_witness(shared)
        || shared->state != 1 || __atomic_load_n(&shared->invoked, __ATOMIC_ACQUIRE)
        || !authorization_current(&shared->tx)) return 0;
    uint32_t prepared = 3;
    return __atomic_compare_exchange_n(&shared->claim, &prepared, 0, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
}

// Isolated ordered construction; retained rejection-only entry is unchanged.
__declspec(dllexport) LRESULT TypeLaySourceOrderedGetMessageProc(int code, WPARAM wp, LPARAM lp) {
    return source_get_message(code, wp, lp, 2);
}
__declspec(dllexport) uint32_t TypeLaySourceArmWitness(SourceInstallation *shared, uint32_t bytes,
    uint64_t nonce, SourceWitness *witness, uint32_t witness_bytes) {
    if (!shared || bytes != sizeof(SourceInstallation) || shared->version != 5 || shared->bytes != bytes
        || !source_uses_witness(shared) || shared->state != 1
        || __atomic_load_n(&shared->claim, __ATOMIC_ACQUIRE) != 3
        || __atomic_load_n(&shared->invoked, __ATOMIC_ACQUIRE)
        || !witness || witness_bytes != sizeof(SourceWitness) || !source_witness_matches(witness, shared)
        || __atomic_load_n(&witness->attached, __ATOMIC_ACQUIRE) != 1
        || witness->attached_nonce != nonce
        || witness->attached_generation[0] != shared->generation[0]
        || witness->attached_generation[1] != shared->generation[1]
        || witness->attached_grant[0] != shared->grant[0] || witness->attached_grant[1] != shared->grant[1]
        || !authorization_current(&shared->tx)) return 0;
    uint64_t published = __atomic_load_n(&witness->published, __ATOMIC_ACQUIRE);
    uint64_t consumed = __atomic_load_n(&witness->consumed, __ATOMIC_ACQUIRE);
    uint64_t boundary = GetTickCount64();
    uint32_t active = __atomic_load_n(&witness->recording_active, __ATOMIC_ACQUIRE);
    uint64_t initial = __atomic_load_n(&witness->initial_tick, __ATOMIC_ACQUIRE);
    if (!boundary || active > 1 || (active ? (!initial || boundary < initial || boundary - initial > 0x7fffffffu)
            : (initial || published || consumed))
        || consumed > published || published - consumed > SOURCE_WITNESS_CAPACITY
        || published < witness->attached_sequence) return 0;
    // Caller is the existing App LL thread, never the source or a worker. These
    // scalar writes precede the release Arm CAS. LL recording cannot interleave
    // this command; source input racing while Prepared can only withdraw it.
    shared->witness_boundary_sequence = published;
    shared->witness_boundary_tick = boundary;
    if (!active) {
        // First exact attach only. The source reader is already bound, and no
        // LL callback can interleave this writer command. Release activation
        // publishes the common lower bound; no FIFO entry is discarded. Even
        // if withdrawal now wins the Arm CAS, observation stays active forever
        // for this lifetime and later grants retain all subsequent records.
        __atomic_store_n(&witness->initial_tick, boundary, __ATOMIC_RELAXED);
        __atomic_store_n(&witness->recording_active, 1, __ATOMIC_RELEASE);
    }
    uint32_t prepared = 3;
    return __atomic_compare_exchange_n(&shared->claim, &prepared, 0, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
}
