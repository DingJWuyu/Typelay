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
static WNDPROC qualify(Transaction *tx) {
    HANDLE hwnd = (HANDLE)(uintptr_t)tx->hwnd;
    uint32_t pid = 0;
    if (tx->pid != GetCurrentProcessId() || tx->thread != GetCurrentThreadId()
        || GetWindowThreadProcessId(hwnd, &pid) != tx->thread || pid != tx->pid
        || GetAncestor(hwnd, 2) != (HANDLE)(uintptr_t)tx->root
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
    tx->multiline = (style & 4) != 0;
    return proc;
}
__declspec(dllexport) uint32_t TypeLayOwnedEditVersion(void) { return 2; }
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
static void execute_guarded(Transaction *tx, uint64_t focus_revision, int *accepted) {
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
    execute_guarded(tx, 0, 0);
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
    execute_guarded(tx, revision, 0);
}

// Local-only entry: the independent practice GUI owns this private buffer. The
// remote v2 wire cannot opt into it or select another process, field or operation.
// Return 0 means preflight refusal with no selection/text change; return 1 means
// accepted and tx.status is the real execution result (including unknown).
__declspec(dllexport) int TypeLayOwnedEditTryExecute(Transaction *tx, uint32_t bytes,
    uint64_t expected_focus_revision) {
    if (!tx || bytes != sizeof(Transaction)) return 0;
    if (tx->status || tx->op != 5 || !expected_focus_revision
        || !owned.hwnd || owned.hwnd != (HANDLE)(uintptr_t)tx->hwnd
        || owned.thread != GetCurrentThreadId() || tx->thread != owned.thread
        || tx->pid != GetCurrentProcessId() || owned.retired || !owned.automatic || tx->cookie != owned.cookie) {
        tx->status = 2; tx->reason = 2;
        return 0;
    }
    int accepted = 0;
    execute_guarded(tx, expected_focus_revision, &accepted);
    return accepted;
}
__declspec(dllexport) LRESULT TypeLayCallWndProc(int code, WPARAM wp, LPARAM lp) {
    if (code >= 0) {
        const CallMessage *msg = (const CallMessage *)lp;
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
