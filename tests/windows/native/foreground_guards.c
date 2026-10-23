// Deterministic API-boundary regression for the production transaction executor.
// Faults change state at the selection, IME and capture-read API boundaries.
// This does not demonstrate Windows accessibility callbacks or OS reentrancy.
// Compile with 16-bit wchar_t, function sections and unused-section elimination.
#define __declspec(value)
#include "../../../src/windows/TypeLay.NativeInput/transaction.c"
#undef __declspec
#undef API

_Static_assert(sizeof(L"") == sizeof(WCHAR), "Compile with 16-bit wchar_t");

enum Scenario {
    Unchanged,
    FocusChangedAfterSelection,
    SelectionChangedAfterSelection,
    AuthorizationExpiredAfterSelection,
    OwnedFocusRoundTripAfterSelection,
    InputContextChangedAfterSelection,
    ForegroundChangedAfterSelection
};
enum {
    SourceWindow = 1, RootWindow = 2, OtherField = 3, OtherRoot = 4,
    ProcessId = 41, ThreadId = 42, LifetimeCookie = 43,
    GetText = 0x0d, GetTextLength = 0x0e, GetSelection = 0xb0,
    SetSelection = 0xb1, ReplaceSelection = 0xc2,
    GetPasswordChar = 0xd2, GetLimitText = 0xd5
};
static const WCHAR initial_text[] = L"pre old post";
static const WCHAR replacement_text[] = L"fresh";
static const WCHAR written_text[] = L"pre fresh post";
static Transaction request;
// One bounded source wire at the existing OS API seam. Production still owns
// installation, claim, invalidation and retirement through its exported hook.
static SourceInstallation source_wire, source_copy;
static int source_wire_enabled, source_mapped, source_allocated;
static uint64_t source_command;
// Public x64 MSG layout, not a fabricated keyboard-origin witness. The same
// caller-owned buffer must be changed before the first control side effect.
typedef struct {
    HANDLE hwnd; uint32_t message; WPARAM wparam; LPARAM lparam;
    uint32_t time; Point point; uint32_t private_value;
} TestQueueMessage;
static TestQueueMessage *retrieved_message;
static int message_next_calls, message_next_fault, message_nested_calls, message_change_at_limit;
static uint32_t message_pending;
static void send_source(uint32_t message);
LRESULT TypeLaySourceGetMessageProc(int code, WPARAM wp, LPARAM lp);
enum { InstallMessage = 0xc101, RevokeMessage, PollMessage, ActivateMessage };
static struct {
    enum Scenario scenario;
    HANDLE foreground, focus, source_ancestor, root_ancestor;
    uint32_t root_pid, root_thread;
    uint64_t coarse_ms, precise_100ns, cookie, focus_revision;
    int start, end, length;
    int set_calls, replace_calls, text_reads, length_reads, selection_reads, injected_changes, boundary_checked, invalid_call;
    int composition_bytes, fail_focus_property;
    HANDLE input_context, keyboard_layout;
    int context_gets, context_releases, layout_reads, change_input_at_limit, change_input_at_release, release_failure;
    int capture_fault, capture_boundary, password, disabled;
    int change_input_at_password, change_input_at_read;
    int qualification_fault, qualification_ready, limit_read, selection_read_after_set, instance_changed, class_changed;
    int hidden, root_disabled, minimized, clipped, bad_parent, root_hidden, rectangle_failure;
    intptr_t style;
    WCHAR text[CAPACITY];
} field;

static HANDLE handle(uintptr_t value) { return (HANDLE)value; }
static void require(int condition) { if (!condition) field.invalid_call = 1; }
static void require_source(HANDLE hwnd) { require(hwnd == handle(SourceWindow)); }
static void change_capture_state(int boundary) {
    if (!field.capture_fault || field.capture_boundary != boundary || field.injected_changes) return;
    field.injected_changes++;
    switch (field.capture_fault) {
    case 1: field.style |= 0x800; break;
    case 2: field.password = '*'; break;
    case 3: field.disabled = 1; break;
    case 4: field.hidden = 1; break;
    case 5: field.root_disabled = 1; break;
    case 6: field.minimized = 1; break;
    case 7: field.clipped = 1; break;
    case 8: field.bad_parent = 1; break;
    case 9: field.root_hidden = 1; break;
    case 10: field.rectangle_failure = 1; break;
    }
}

// These are controlled OS responses, not Windows IPC/queue evidence. Every
// non-source test keeps the original fail-if-called boundary.
LRESULT CallNextHookEx(HANDLE hook, int code, WPARAM wp, LPARAM lp) {
    (void)hook; (void)code; (void)wp; (void)lp;
    require(source_wire_enabled);
    if (retrieved_message && lp == (LPARAM)retrieved_message) {
        message_next_calls++;
        int fault = message_next_fault;
        message_next_fault = 0;
        if (fault == 1) retrieved_message->wparam = 0x76;
        if (fault == 2) {
            TestQueueMessage nested;
            copy(&nested, retrieved_message, sizeof(nested));
            message_nested_calls++;
            TypeLaySourceGetMessageProc(0, 1, (LPARAM)&nested);
            require(nested.message == 0x100 && !source_wire.invoked && !source_wire.claim);
        }
        if (fault == 3) {
            uint64_t ack = source_wire.acknowledgement;
            send_source(RevokeMessage);
            send_source(InstallMessage);
            require(source_wire.acknowledgement == ack && source_wire.state == 1
                && !source_wire.claim && !source_wire.invoked);
        }
        if (fault == 4) require(TypeLaySourceWithdraw(&source_wire, sizeof(source_wire)) == 2);
    }
    return 0;
}
static int same_name(const WCHAR *a, const WCHAR *b) {
    while (*a && *a == *b) { a++; b++; }
    return *a == *b;
}
uint32_t RegisterWindowMessageW(const WCHAR *name) {
    require(source_wire_enabled);
    if (same_name(name, (const WCHAR *)L"TypeLay.NativeInput.SourceInstall.v3")) return InstallMessage;
    if (same_name(name, (const WCHAR *)L"TypeLay.NativeInput.SourceRevoke.v3")) return RevokeMessage;
    if (same_name(name, (const WCHAR *)L"TypeLay.NativeInput.SourcePoll.v3")) return PollMessage;
    if (same_name(name, (const WCHAR *)L"TypeLay.NativeInput.SourceActivate.v3")) return ActivateMessage;
    if (same_name(name, message_name)) return 0xc105;
    if (same_name(name, owned_capture_message_name)) return 0xc106;
    field.invalid_call = 1;
    return 0;
}
HANDLE OpenFileMappingW(uint32_t access, int inherit, const WCHAR *name) {
    require(source_wire_enabled && access == 6 && !inherit
        && same_name(name, (const WCHAR *)L"Local\\TypeLay.Source.0000000000000044"));
    return handle(8);
}
void *MapViewOfFile(HANDLE mapping, uint32_t access, uint32_t high, uint32_t low, size_t bytes) {
    require(source_wire_enabled && mapping == handle(8) && access == 6 && !high && !low
        && bytes == sizeof(source_wire) && !source_mapped);
    source_mapped = 1;
    return &source_wire;
}
int UnmapViewOfFile(const void *view) {
    require(source_wire_enabled && source_mapped && view == &source_wire);
    source_mapped = 0;
    return 1;
}
int CloseHandle(HANDLE value) { require(source_wire_enabled && value == handle(8)); return 1; }
HANDLE GetProcessHeap(void) { require(source_wire_enabled); return handle(9); }
void *HeapAlloc(HANDLE heap, uint32_t flags, size_t bytes) {
    require(source_wire_enabled && heap == handle(9) && flags == 8
        && bytes == sizeof(source_copy) && !source_allocated);
    source_allocated = 1;
    return &source_copy;
}
int HeapFree(HANDLE heap, uint32_t flags, void *value) {
    require(source_wire_enabled && heap == handle(9) && !flags
        && value == &source_copy && source_allocated);
    source_allocated = 0;
    return 1;
}

static LRESULT stock_edit(HANDLE hwnd, uint32_t message, WPARAM wp, LPARAM lp) {
    require_source(hwnd);
    switch (message) {
    case GetPasswordChar:
        if (field.limit_read && field.qualification_fault
            && (field.qualification_fault != 4 || field.selection_read_after_set)) field.qualification_ready = 1;
        if (field.change_input_at_password == 2) {
            field.change_input_at_password = 0;
            field.input_context = handle(6);
            field.injected_changes++;
        }
        return field.password;
    case GetLimitText:
        if (message_change_at_limit && retrieved_message) {
            message_change_at_limit = 0;
            retrieved_message->lparam ^= 0x10000;
        }
        field.limit_read = 1;
        if (field.change_input_at_password == 1) field.change_input_at_password = 2;
        if (field.change_input_at_limit) {
            field.keyboard_layout = handle(6);
            field.injected_changes++;
        }
        return MAX_UNITS;
    case GetTextLength: field.length_reads++; return field.length;
    case GetText:
        field.text_reads++;
        require(wp > (WPARAM)field.length && lp != 0);
        if (wp <= (WPARAM)field.length || !lp) return 0;
        copy((void *)lp, field.text, ((size_t)field.length + 1) * sizeof(WCHAR));
        return field.length;
    case GetSelection:
        field.selection_reads++;
        if (field.set_calls) field.selection_read_after_set = 1;
        require(wp != 0 && lp != 0);
        if (wp && lp) {
            *(int32_t *)wp = field.start;
            *(int32_t *)lp = field.end;
        }
        change_capture_state(2);
        if (field.selection_reads == field.change_input_at_read) {
            field.input_context = handle(6);
            field.injected_changes++;
        }
        return 0;
    case SetSelection:
        field.set_calls++;
        if (retrieved_message) require(retrieved_message->message == 0 && source_wire.claim == 1);
        // Every case must arrive here with the original authorization, focus,
        // text and expected selection. The replacement scope is deliberately
        // different from that initial collapsed selection.
        require(field.set_calls == 1 && field.replace_calls == 0);
        require((field.foreground == handle(RootWindow) || field.foreground == handle(SourceWindow))
            && field.focus == handle(SourceWindow));
        require(field.start == 12 && field.end == 12 && field.length == 12);
        require(equal(field.text, initial_text, 13));
        require(field.coarse_ms == 100 && request.deadline == 600);
        require(field.precise_100ns == 10000000 && request.authorization_deadline_ms == 1000);
        require(wp == 4 && lp == 7);
        field.boundary_checked++;
        field.start = (int)wp;
        field.end = (int)lp;
        switch (field.scenario) {
        case Unchanged: break;
        case FocusChangedAfterSelection:
            // Same root, different field: checking only the root is insufficient.
            field.focus = handle(OtherField);
            field.injected_changes++;
            break;
        case ForegroundChangedAfterSelection:
            // The source still has keyboard focus, but a different child is
            // foreground. A shared ancestor cannot authorize the replacement.
            field.foreground = handle(OtherField);
            field.injected_changes++;
            break;
        case SelectionChangedAfterSelection:
            field.start = 0;
            field.end = 0;
            field.injected_changes++;
            break;
        case AuthorizationExpiredAfterSelection:
            // Only the original precise-clock authorization expires. The
            // transaction's independent 500ms coarse budget remains fresh.
            field.precise_100ns = 10010000;
            field.injected_changes++;
            break;
        case InputContextChangedAfterSelection:
            field.input_context = handle(6);
            field.injected_changes++;
            break;
        case OwnedFocusRoundTripAfterSelection:
            // Deterministic API boundary, not evidence of Windows callbacks:
            // source -> another child -> source must advance twice even though
            // the final HWND and text still match the original observation.
            field.focus = handle(OtherField);
            require(TypeLayOwnedFocusRevision(hwnd, 1) != 0);
            field.focus = hwnd;
            require(TypeLayOwnedFocusRevision(hwnd, 1) != 0);
            field.injected_changes++;
            break;
        }
        return 0;
    case ReplaceSelection: {
        const WCHAR *replacement = (const WCHAR *)lp;
        static WCHAR updated[CAPACITY];
        int replacement_length = 0, offset = 0;
        field.replace_calls++;
        require(wp == 1 && replacement != 0);
        require(field.start >= 0 && field.end >= field.start && field.end <= field.length);
        if (!replacement || field.start < 0 || field.end < field.start || field.end > field.length) return 0;
        while (replacement_length < CAPACITY && replacement[replacement_length]) replacement_length++;
        require(field.length - (field.end - field.start) + replacement_length <= MAX_UNITS);
        if (field.length - (field.end - field.start) + replacement_length > MAX_UNITS) return 0;
        for (int i = 0; i < field.start; i++) updated[offset++] = field.text[i];
        for (int i = 0; i < replacement_length; i++) updated[offset++] = replacement[i];
        for (int i = field.end; i < field.length; i++) updated[offset++] = field.text[i];
        updated[offset] = 0;
        copy(field.text, updated, ((size_t)offset + 1) * sizeof(WCHAR));
        field.length = offset;
        field.start += replacement_length;
        field.end = field.start;
        return 0;
    }
    default: field.invalid_call = 1; return 0;
    }
}

uint64_t GetTickCount64(void) { return field.coarse_ms; }
void QueryInterruptTimePrecise(uint64_t *value) { *value = field.precise_100ns; }
uint32_t GetCurrentThreadId(void) { return ThreadId; }
int16_t GetKeyState(int key) { (void)key; return 0; }
uint32_t GetCurrentProcessId(void) { return ProcessId; }
uint32_t GetWindowThreadProcessId(HANDLE hwnd, uint32_t *pid) {
    require(hwnd == handle(SourceWindow) || hwnd == handle(RootWindow)
        || hwnd == handle(OtherField) || hwnd == handle(OtherRoot));
    *pid = hwnd == handle(RootWindow) ? field.root_pid : ProcessId;
    return hwnd == handle(RootWindow) ? field.root_thread : ThreadId;
}
HANDLE GetAncestor(HANDLE hwnd, uint32_t flags) {
    require(hwnd == handle(SourceWindow) || hwnd == handle(RootWindow)
        || hwnd == handle(OtherField) || hwnd == handle(OtherRoot));
    require(flags == 2 || flags == 3);
    // The sibling shares both source PID/TID and root. The owned dialog shares
    // PID/TID and root owner, but is a distinct top-level root.
    if (hwnd == handle(OtherField)) return handle(RootWindow);
    if (hwnd == handle(OtherRoot)) return handle(flags == 2 ? OtherRoot : RootWindow);
    return hwnd == handle(RootWindow) ? field.root_ancestor : field.source_ancestor;
}
HANDLE GetForegroundWindow(void) { return field.foreground; }
HANDLE GetFocus(void) { return field.focus; }
// Remote activation is not exercised; the exported source hook receives the
// controlled request directly. No claim about real Windows queue state follows.
HANDLE GetActiveWindow(void) { field.invalid_call = 1; return 0; }
uint32_t GetQueueStatus(uint32_t flags) { require(source_wire_enabled && flags == 0x1dff); return message_pending << 16; }
LRESULT SendMessageTimeoutW(HANDLE hwnd, uint32_t message, WPARAM wp, LPARAM lp,
    uint32_t flags, uint32_t timeout, uintptr_t *result) {
    (void)hwnd; (void)message; (void)wp; (void)lp; (void)flags; (void)timeout;
    field.invalid_call = 1;
    if (result) *result = 0;
    return 0;
}
HANDLE GetParent(HANDLE hwnd) { require_source(hwnd); return handle(field.bad_parent ? OtherField : RootWindow); }
int IsWindowUnicode(HANDLE hwnd) { require_source(hwnd); return 1; }
int IsWindowEnabled(HANDLE hwnd) {
    require(hwnd == handle(SourceWindow) || hwnd == handle(RootWindow));
    return hwnd == handle(RootWindow) ? !field.root_disabled : !field.disabled;
}
int IsWindowVisible(HANDLE hwnd) {
    require(hwnd == handle(SourceWindow) || hwnd == handle(RootWindow));
    return hwnd == handle(RootWindow) ? !field.root_hidden : !field.hidden;
}
int IsIconic(HANDLE hwnd) { require(hwnd == handle(RootWindow)); return field.minimized; }
int GetClientRect(HANDLE hwnd, Rect *rect) {
    require(hwnd == handle(SourceWindow) || hwnd == handle(RootWindow));
    rect->left = rect->top = 0;
    rect->right = rect->bottom = hwnd == handle(RootWindow) ? 300 : 100;
    return !field.rectangle_failure;
}
int ClientToScreen(HANDLE hwnd, Point *point) {
    require(hwnd == handle(SourceWindow) || hwnd == handle(RootWindow));
    point->x += 10;
    point->y += hwnd == handle(SourceWindow) && field.clipped ? 400 : 10;
    return 1;
}
intptr_t GetWindowLongPtrW(HANDLE hwnd, int index) {
    require_source(hwnd);
    require(index == -4 || index == -16);
    return index == -4 ? (intptr_t)stock_edit + field.instance_changed : field.style;
}
intptr_t GetClassLongPtrW(HANDLE hwnd, int index) {
    require_source(hwnd);
    require(index == -24);
    return (intptr_t)stock_edit + field.class_changed;
}
int GetClassInfoW(HANDLE instance, const WCHAR *name, WindowClass *result) {
    require(instance == 0 && equal(name, (const WCHAR *)L"Edit", 5));
    wipe(result, sizeof(*result));
    result->procedure = stock_edit;
    return 1;
}
int GetClassNameW(HANDLE hwnd, WCHAR *name, int capacity) {
    require_source(hwnd);
    require(capacity >= 5);
    if (capacity < 5) return 0;
    copy(name, L"Edit", 5 * sizeof(WCHAR));
    return 4;
}
HANDLE GetPropW(HANDLE hwnd, const WCHAR *name) {
    require_source(hwnd);
    require(name == property || name == focus_property);
    return handle((uintptr_t)(name == focus_property ? field.focus_revision : field.cookie));
}
int SetPropW(HANDLE hwnd, const WCHAR *name, HANDLE value) {
    require_source(hwnd);
    require(name == property || name == focus_property);
    if (name == focus_property) {
        if (field.fail_focus_property) return 0;
        field.focus_revision = (uint64_t)(uintptr_t)value;
    } else field.cookie = (uint64_t)(uintptr_t)value;
    return 1;
}
HANDLE RemovePropW(HANDLE hwnd, const WCHAR *name) {
    require_source(hwnd);
    require(name == property || name == focus_property);
    HANDLE previous = handle((uintptr_t)(name == focus_property ? field.focus_revision : field.cookie));
    if (name == focus_property) field.focus_revision = 0;
    else field.cookie = 0;
    return previous;
}
LRESULT CallWindowProcW(WNDPROC proc, HANDLE hwnd, uint32_t message, WPARAM wp, LPARAM lp) {
    require(proc == stock_edit);
    return stock_edit(hwnd, message, wp, lp);
}
HANDLE GetKeyboardLayout(uint32_t thread) {
    require(thread == ThreadId);
    field.layout_reads++;
    return field.keyboard_layout;
}
HANDLE ImmGetContext(HANDLE hwnd) {
    require_source(hwnd);
    if (field.input_context) field.context_gets++;
    return field.input_context;
}
int32_t ImmGetCompositionStringW(HANDLE context, uint32_t index, void *buffer, uint32_t bytes) {
    require(context == field.input_context && index == 8 && buffer == 0 && bytes == 0);
    return field.composition_bytes;
}
int ImmReleaseContext(HANDLE hwnd, HANDLE context) {
    require_source(hwnd);
    require(context == field.input_context);
    field.context_releases++;
    if (field.change_input_at_release) {
        field.change_input_at_release = 0;
        field.input_context = handle(6);
        field.injected_changes++;
    }
    if (field.qualification_ready && (field.qualification_fault != 4 || field.set_calls)) {
        switch (field.qualification_fault) {
        case 1: case 4: field.style |= 0x800; break;
        case 2: field.instance_changed = 1; break;
        case 3: field.class_changed = 1; break;
        }
        field.qualification_fault = field.qualification_ready = 0;
        field.injected_changes++;
    }
    change_capture_state(1);
    return !field.release_failure;
}

static void prepare_transaction(enum Scenario scenario) {
    wipe(&request, sizeof(request));
    field.scenario = scenario;
    field.set_calls = field.replace_calls = field.text_reads = field.injected_changes = field.boundary_checked = 0;
    field.length_reads = field.selection_reads = 0;
    field.composition_bytes = 0;
    field.input_context = handle(4);
    field.keyboard_layout = handle(5);
    field.context_gets = field.context_releases = field.layout_reads = 0;
    field.change_input_at_limit = field.change_input_at_release = field.release_failure = 0;
    field.capture_fault = field.capture_boundary = field.password = field.disabled = 0;
    field.change_input_at_password = field.change_input_at_read = 0;
    field.qualification_fault = field.qualification_ready = field.limit_read = field.selection_read_after_set = 0;
    field.instance_changed = field.class_changed = 0;
    field.hidden = field.root_disabled = field.minimized = field.clipped = field.bad_parent = 0;
    field.root_hidden = field.rectangle_failure = 0;
    field.style = 0x50000000; // Current WS_CHILD | WS_VISIBLE, not a configured safety flag.
    field.foreground = handle(RootWindow);
    field.focus = handle(SourceWindow);
    field.source_ancestor = field.root_ancestor = handle(RootWindow);
    field.root_pid = ProcessId;
    field.root_thread = ThreadId;
    field.coarse_ms = 100;
    field.precise_100ns = 10000000;
    field.start = field.end = field.length = 12;
    copy(field.text, initial_text, sizeof(initial_text));
    request.version = VERSION;
    request.op = 5;
    request.pid = ProcessId;
    request.thread = ThreadId;
    request.hwnd = SourceWindow;
    request.root = RootWindow;
    request.cookie = field.cookie;
    request.deadline = 600;
    request.authorization_deadline_ms = 1000;
    request.length = 12;
    request.start = 4;
    request.end = 7;
    request.expected_selection_start = request.expected_selection_end = 12;
    request.replacement_length = 5;
    copy(request.expected, initial_text, sizeof(initial_text));
    copy(request.replacement, replacement_text, sizeof(replacement_text));
}
static void run_transaction(enum Scenario scenario) {
    wipe(&field, sizeof(field));
    field.cookie = LifetimeCookie;
    prepare_transaction(scenario);
    execute(&request);
}

static int reached_selection_boundary(int injected_changes) {
    return !field.invalid_call && field.set_calls == 1 && field.boundary_checked == 1
        && field.injected_changes == injected_changes && field.foreground == handle(RootWindow)
        && field.coarse_ms == 100 && request.deadline == 600
        && request.authorization_deadline_ms == 1000;
}
static int rejected_without_text_write(uint32_t reason) {
    return reached_selection_boundary(1) && request.status == 2 && request.reason == reason
        && field.replace_calls == 0 && field.length == 12 && equal(field.text, initial_text, 13);
}
static int unchanged_writes_scope_and_verifies(void) {
    run_transaction(Unchanged);
    return reached_selection_boundary(0) && request.status == 1 && request.reason == 0
        && field.replace_calls == 1 && field.length == 14 && equal(field.text, written_text, 15)
        && field.start == 9 && field.end == 9 && request.length == 14
        && request.selection_start == 9 && request.selection_end == 9
        && equal(request.observed, written_text, 15)
        && field.focus == handle(SourceWindow) && field.precise_100ns == 10000000;
}
static int changed_focus_rejects_without_text_write(void) {
    run_transaction(FocusChangedAfterSelection);
    return rejected_without_text_write(10) && field.focus == handle(OtherField)
        && field.start == 4 && field.end == 7 && field.precise_100ns == 10000000;
}
static int changed_selection_rejects_without_text_write(void) {
    run_transaction(SelectionChangedAfterSelection);
    return rejected_without_text_write(3) && field.start == 0 && field.end == 0
        && field.focus == handle(SourceWindow) && field.precise_100ns == 10000000;
}
static int expired_original_authorization_rejects_with_fresh_transaction_budget(void) {
    run_transaction(AuthorizationExpiredAfterSelection);
    return rejected_without_text_write(14) && field.precise_100ns == 10010000
        && field.focus == handle(SourceWindow) && field.start == 4 && field.end == 7;
}
static int exact_field_foreground_keeps_the_original_transaction_guards(void) {
    for (int fault = 0; fault < 17; fault++) {
        wipe(&field, sizeof(field));
        field.cookie = LifetimeCookie;
        prepare_transaction(Unchanged);
        field.foreground = handle(SourceWindow);
        switch (fault) {
        case 0: break;
        case 1: field.foreground = handle(OtherField); break;
        case 2: field.source_ancestor = handle(OtherRoot); break;
        case 3: field.focus = handle(OtherField); break;
        case 4: field.cookie++; break;
        case 5: field.input_context = 0; break;
        case 6: request.authorization_deadline_ms = 999; break;
        case 7: request.pid++; break;
        case 8: request.thread++; break;
        case 9: field.root_ancestor = handle(OtherRoot); break;
        case 10: field.composition_bytes = 2; break;
        case 11: field.root_pid++; break;
        case 12: field.root_thread++; break;
        case 13: field.foreground = handle(OtherRoot); break;
        case 14: field.composition_bytes = -1; break;
        case 15: field.focus = 0; break;
        case 16: request.deadline = 99; break;
        }
        HANDLE foreground = field.foreground, focus = field.focus;
        uint64_t cookie = field.cookie;
        int64_t authorization = request.authorization_deadline_ms;
        execute(&request);
        if (field.invalid_call || field.foreground != foreground || field.focus != focus
            || field.cookie != cookie || request.authorization_deadline_ms != authorization) return 0;
        if (!fault) {
            if (request.status != 1 || request.reason || field.set_calls != 1 || field.replace_calls != 1
                || !equal(field.text, written_text, 15) || field.start != 9 || field.end != 9
                || request.length != 14 || request.selection_start != 9 || request.selection_end != 9
                || !equal(request.observed, written_text, 15)) return 0;
        } else if (request.status != 2 || field.set_calls || field.replace_calls
            || field.start != 12 || field.end != 12 || !equal(field.text, initial_text, 13)) return 0;
    }
    wipe(&field, sizeof(field));
    field.cookie = LifetimeCookie;
    prepare_transaction(ForegroundChangedAfterSelection);
    field.foreground = handle(SourceWindow);
    execute(&request);
    return request.status == 2 && request.reason == 10 && field.set_calls == 1 && !field.replace_calls
        && field.foreground == handle(OtherField) && field.focus == handle(SourceWindow)
        && equal(field.text, initial_text, 13) && !field.invalid_call;
}
static int unknown_wire_operations_cannot_read_the_source(void) {
    const uint32_t operations[] = {0, 6, UINT32_MAX};
    for (int i = 0; i < 3; i++) {
        wipe(&field, sizeof(field));
        field.cookie = LifetimeCookie;
        prepare_transaction(Unchanged);
        request.op = operations[i];
        execute(&request);
        if (request.status != 2 || request.reason != 6 || field.text_reads
            || field.set_calls || field.replace_calls || field.invalid_call) return 0;
    }
    return 1;
}
static int owned_lifetime_cannot_be_restored_by_capture_or_release(void) {
    run_transaction(Unchanged);
    HANDLE hwnd = handle(SourceWindow);
    if (!TypeLayOwnedEditControl(hwnd, 1, 100) || !TypeLayOwnedEditControl(hwnd, 2, 0)) return 0;
    request.cookie = 101; request.op = 4;
    execute(&request);
    if (request.status != 1 || field.cookie != 101) return 0;
    if (!TypeLayOwnedEditControl(hwnd, 3, 0) || field.cookie != 0) return 0;
    const uint32_t stale_ops[] = {1, 3, 4, 2, 5};
    for (int i = 0; i < 5; i++) {
        request.op = stale_ops[i];
        execute(&request);
        if (request.status != 2 || request.reason != 2 || field.cookie != 0) return 0;
    }
    if (!TypeLayOwnedEditControl(hwnd, 2, 0) || field.cookie != 102) return 0;
    request.op = 3; execute(&request);
    if (request.status != 2 || field.cookie != 102) return 0;
    request.op = 1; execute(&request);
    if (request.status != 2 || field.cookie != 102) return 0;
    request.cookie = 102; execute(&request);
    if (request.status != 1) return 0;
    request.op = 3; execute(&request);
    return request.status == 1 && field.cookie == 102 && !field.invalid_call
        && field.replace_calls == 1 && equal(field.text, written_text, 15);
}
static int automatic_lifetime_cannot_use_the_ordinary_write_route(void) {
    HANDLE hwnd = handle(SourceWindow);
    if (!TypeLayOwnedEditControl(hwnd, 3, 0) || !TypeLayOwnedEditControl(hwnd, 4, 0)) return 0;
    const uint32_t operations[] = {2, 5};
    for (int i = 0; i < 2; i++) {
        prepare_transaction(Unchanged);
        request.op = operations[i];
        execute(&request); // No managed plan is required to close this lifetime's v2 route.
        if (request.status != 2 || request.reason != 6 || field.text_reads
            || field.set_calls || field.replace_calls || field.invalid_call) return 0;
    }
    return 1;
}
static int owned_preflight_refuses_without_consuming_or_mutating(void) {
    HANDLE hwnd = handle(SourceWindow);
    uint64_t revision = TypeLayOwnedFocusRevision(hwnd, 0);
    if (!revision || field.focus_revision != revision) return 0;
    for (int fault = 0; fault < 7; fault++) {
        prepare_transaction(Unchanged);
        uint64_t expected_revision = TypeLayOwnedFocusRevision(hwnd, 0);
        switch (fault) {
        case 0:
            // Same-window round trip after installation, before this key.
            field.focus = handle(OtherField);
            if (!TypeLayOwnedFocusRevision(hwnd, 1)) return 0;
            field.focus = hwnd;
            if (!TypeLayOwnedFocusRevision(hwnd, 1)) return 0;
            break;
        case 1: request.expected[0] = 'X'; break;
        case 2: field.composition_bytes = 2; break;
        case 3: field.composition_bytes = -1; break;
        case 4: request.authorization_deadline_ms = 999; break;
        case 5: field.start = field.end = 0; break;
        case 6: field.foreground = hwnd; break;
        }
        int selection = field.start;
        if (TypeLayOwnedEditTryExecute(&request, sizeof(request), expected_revision) != 0
            || request.status != 2 || field.set_calls || field.replace_calls
            || field.start != selection || field.end != selection
            || field.focus != hwnd || field.foreground != (fault == 6 ? hwnd : handle(RootWindow))
            || !equal(field.text, initial_text, 13) || field.invalid_call) return 0;
        const uint32_t reasons[] = {10, 3, 9, 9, 14, 4, 10};
        if (request.reason != reasons[fault]) return 0;
    }
    return TypeLayOwnedFocusRevision(hwnd, 0) > revision;
}
static int owned_acceptance_reports_one_execution_and_cannot_replay(void) {
    prepare_transaction(Unchanged);
    uint64_t revision = TypeLayOwnedFocusRevision(handle(SourceWindow), 0);
    if (TypeLayOwnedEditTryExecute(&request, sizeof(request), revision) != 1
        || request.status != 1 || request.reason != 0 || field.replace_calls != 1
        || !equal(field.text, written_text, 15) || field.start != 9 || field.end != 9
        || field.invalid_call) return 0;
    // The native transaction terminal state cannot execute the same buffer twice.
    if (TypeLayOwnedEditTryExecute(&request, sizeof(request), revision) != 0
        || field.replace_calls != 1 || field.set_calls != 1
        || !equal(field.text, written_text, 15) || field.invalid_call) return 0;
    uint64_t cookie = field.cookie;
    prepare_transaction(Unchanged);
    execute(&request); // Consuming/exhausting a local plan cannot restore the old write route.
    return request.status == 2 && request.reason == 6 && field.cookie == cookie
        && !field.text_reads && !field.set_calls && !field.replace_calls;
}
static int owned_capture_rechecks_the_exact_focus_revision_before_reading(void) {
    HANDLE hwnd = handle(SourceWindow);
    uint64_t revision = TypeLayOwnedFocusRevision(hwnd, 0);
    prepare_transaction(Unchanged);
    request.op = 6;
    request.authorization_deadline_ms = (int64_t)revision;
    execute_owned_capture(&request);
    if (request.status != 1 || request.reason || field.text_reads != 1
        || !equal(request.observed, initial_text, 13) || field.set_calls || field.replace_calls) return 0;
    prepare_transaction(Unchanged);
    request.op = 6;
    request.authorization_deadline_ms = (int64_t)revision;
    field.foreground = hwnd;
    execute_owned_capture(&request);
    if (request.status != 2 || field.text_reads || field.set_calls || field.replace_calls
        || field.foreground != hwnd || field.focus != hwnd) return 0;
    // Same-root A -> B -> A; matching HWNDs and cookie do not recover the old read.
    field.focus = handle(OtherField);
    if (!TypeLayOwnedFocusRevision(hwnd, 1)) return 0;
    field.focus = hwnd;
    if (!TypeLayOwnedFocusRevision(hwnd, 1)) return 0;
    prepare_transaction(Unchanged);
    request.op = 6;
    request.authorization_deadline_ms = (int64_t)revision;
    execute_owned_capture(&request);
    if (request.status != 2 || field.text_reads || field.set_calls || field.replace_calls) return 0;
    prepare_transaction(Unchanged);
    request.authorization_deadline_ms = (int64_t)TypeLayOwnedFocusRevision(hwnd, 0);
    execute_owned_capture(&request); // The owned-capture entry may never route op5.
    return request.status == 2 && !field.text_reads && !field.set_calls && !field.replace_calls && !field.invalid_call;
}
static int accepted_owned_focus_round_trip_keeps_the_real_rejection(void) {
    prepare_transaction(OwnedFocusRoundTripAfterSelection);
    uint64_t revision = TypeLayOwnedFocusRevision(handle(SourceWindow), 0);
    return TypeLayOwnedEditTryExecute(&request, sizeof(request), revision) == 1
        && rejected_without_text_write(3) && field.focus == handle(SourceWindow)
        && field.start == 4 && field.end == 7
        && TypeLayOwnedFocusRevision(handle(SourceWindow), 0) == revision + 2;
}
static int owned_capture_rechecks_dynamic_qualification_around_reading(void) {
    for (int boundary = 1; boundary <= 2; boundary++) {
        for (int fault = 1; fault <= 10; fault++) {
            prepare_transaction(Unchanged);
            request.op = 6;
            request.authorization_deadline_ms = (int64_t)TypeLayOwnedFocusRevision(handle(SourceWindow), 0);
            field.capture_boundary = boundary;
            field.capture_fault = fault;
            execute_owned_capture(&request);
            if (request.status != 2 || field.injected_changes != 1 || field.text_reads != boundary - 1
                || field.set_calls || field.replace_calls || field.invalid_call
                || !equal(field.text, initial_text, 13) || field.start != 12 || field.end != 12) return 0;
        }
    }
    return 1;
}
static int owned_input_probe_is_body_free_and_requires_current_clear_identity(void) {
    HANDLE hwnd = handle(SourceWindow), root = handle(RootWindow);
    uint64_t revision = TypeLayOwnedFocusRevision(hwnd, 0);
    OwnedInputContext context;
    prepare_transaction(Unchanged);
    if (!TypeLayOwnedEditTryProbeInputContext(hwnd, root, ProcessId, ThreadId, field.cookie, revision, 1000, &context)
        || context.ime_context != 4 || context.keyboard_layout != 5
        || !field.context_gets || field.context_gets != field.context_releases
        || field.text_reads || field.length_reads || field.selection_reads
        || field.set_calls || field.replace_calls || field.invalid_call) return 0;
    for (int fault = 0; fault < 8; fault++) {
        prepare_transaction(Unchanged);
        switch (fault) {
        case 0: field.input_context = 0; break;
        case 1: field.keyboard_layout = 0; break;
        case 2: field.composition_bytes = 2; break;
        case 3: field.composition_bytes = -1; break;
        case 4: field.change_input_at_release = 1; break;
        case 5: field.precise_100ns = 10010000; break;
        case 6: field.release_failure = 1; break;
        case 7: field.capture_boundary = 1; field.capture_fault = 4; break;
        }
        context.ime_context = context.keyboard_layout = UINT64_MAX;
        if (TypeLayOwnedEditTryProbeInputContext(hwnd, root, ProcessId, ThreadId, field.cookie, revision, 1000, &context)
            || context.ime_context || context.keyboard_layout
            || field.context_gets != field.context_releases || field.text_reads
            || field.length_reads || field.selection_reads || field.set_calls || field.replace_calls || field.invalid_call) return 0;
    }
    prepare_transaction(Unchanged);
    if (TypeLayOwnedEditTryProbeInputContext(hwnd, root, ProcessId + 1, ThreadId, field.cookie, revision, 1000, &context)
        || TypeLayOwnedEditTryProbeInputContext(hwnd, root, ProcessId, ThreadId + 1, field.cookie, revision, 1000, &context)
        || field.context_gets || field.text_reads || field.length_reads || field.selection_reads
        || field.set_calls || field.replace_calls) return 0;
    field.keyboard_layout = 0; // The existing exit entry does not gain an identity requirement.
    return TypeLayOwnedEditCanExit(hwnd, root, field.cookie, revision, 1000)
        && !field.layout_reads && !field.text_reads && !field.length_reads && !field.selection_reads
        && !field.set_calls && !field.replace_calls;
}
static int owned_input_identity_is_rechecked_inside_acceptance(void) {
    HANDLE hwnd = handle(SourceWindow), root = handle(RootWindow);
    uint64_t revision = TypeLayOwnedFocusRevision(hwnd, 0);
    for (int fault = 0; fault < 4; fault++) {
        prepare_transaction(Unchanged);
        OwnedInputContext context;
        if (!TypeLayOwnedEditTryProbeInputContext(hwnd, root, ProcessId, ThreadId, field.cookie, revision, 1000, &context)) return 0;
        switch (fault) {
        case 0: field.input_context = handle(6); break;
        case 1: field.keyboard_layout = handle(6); break;
        case 2: field.change_input_at_limit = 1; break;
        case 3: field.change_input_at_release = 1; break;
        }
        if (TypeLayOwnedEditTryExecuteWithInputContext(&request, sizeof(request), revision,
                context.ime_context, context.keyboard_layout)
            || request.status != 2 || field.set_calls || field.replace_calls
            || field.start != 12 || field.end != 12 || !equal(field.text, initial_text, 13)
            || field.context_gets != field.context_releases || field.invalid_call) return 0;
        if (TypeLayOwnedEditCanExitWithInputContext(hwnd, root, ProcessId, ThreadId, field.cookie, revision, 1000,
                context.ime_context, context.keyboard_layout)) return 0;
    }
    return 1;
}
static int accepted_input_context_change_cannot_reach_text_mutation(void) {
    prepare_transaction(InputContextChangedAfterSelection);
    HANDLE hwnd = handle(SourceWindow), root = handle(RootWindow);
    uint64_t revision = TypeLayOwnedFocusRevision(hwnd, 0);
    OwnedInputContext context;
    if (!TypeLayOwnedEditTryProbeInputContext(hwnd, root, ProcessId, ThreadId, field.cookie, revision, 1000, &context)
        || TypeLayOwnedEditTryExecuteWithInputContext(&request, sizeof(request), revision,
            context.ime_context, context.keyboard_layout) != 1
        || request.status != 2 || !reached_selection_boundary(1) || field.replace_calls
        || !equal(field.text, initial_text, 13) || field.context_gets != field.context_releases) return 0;
    prepare_transaction(Unchanged);
    if (!TypeLayOwnedEditTryProbeInputContext(hwnd, root, ProcessId, ThreadId, field.cookie, revision, 1000, &context)
        || !TypeLayOwnedEditCanExitWithInputContext(hwnd, root, ProcessId, ThreadId, field.cookie, revision, 1000,
            context.ime_context, context.keyboard_layout)) return 0;
    return TypeLayOwnedEditTryExecuteWithInputContext(&request, sizeof(request), revision,
            context.ime_context, context.keyboard_layout) == 1
        && request.status == 1 && field.replace_calls == 1 && equal(field.text, written_text, 15)
        && field.context_gets == field.context_releases && !field.invalid_call;
}
static int failed_focus_mirror_permanently_retires_owned_authority(void) {
    HANDLE hwnd = handle(SourceWindow);
    prepare_transaction(Unchanged);
    uint64_t revision = TypeLayOwnedFocusRevision(hwnd, 0);
    if (!TypeLayOwnedEditControl(hwnd, 3, 0)
        || TypeLayOwnedFocusRevision(hwnd, 0) != revision
        || !TypeLayOwnedEditControl(hwnd, 4, 0)
        || TypeLayOwnedFocusRevision(hwnd, 0) != revision) return 0;
    request.cookie = field.cookie;
    field.fail_focus_property = 1;
    if (TypeLayOwnedFocusRevision(hwnd, 1) || field.cookie || field.focus_revision) return 0;
    field.fail_focus_property = 0;
    return !TypeLayOwnedEditControl(hwnd, 2, 0) && !TypeLayOwnedFocusRevision(hwnd, 1)
        && !TypeLayOwnedEditTryExecute(&request, sizeof(request), revision)
        && request.status == 2 && !field.set_calls && !field.replace_calls
        && field.start == 12 && field.end == 12 && equal(field.text, initial_text, 13)
        && !field.cookie && !field.focus_revision && !field.invalid_call;
}
static void send_source(uint32_t message) {
    CallMessage call = {(LPARAM)++source_command, 0x44, message, handle(RootWindow)};
    TypeLayCallWndProc(0, 0, (LPARAM)&call);
}
static int source_input_identity_is_bound_until_exact_retirement(void) {
    source_wire_enabled = 1;
    for (int fault = 0; fault < 14; fault++) {
        wipe(&field, sizeof(field));
        field.cookie = LifetimeCookie;
        prepare_transaction(Unchanged);
        wipe(&source_wire, sizeof(source_wire));
        source_command = 0;
        source_wire.version = 3; source_wire.bytes = sizeof(source_wire);
        source_wire.generation[0] = 1; source_wire.grant[0] = (uint64_t)fault + 1;
        source_wire.observed_at_ms = 900;
        copy(&source_wire.tx, &request, sizeof(request));
        if (fault == 9) field.change_input_at_read = 1;
        send_source(InstallMessage);
        if (fault == 9) {
            // The installation's own body/selection read changed HIMC. It must
            // compare with its earlier sample, not freeze a new identity here.
            if (source_wire.state != 2 || source_wire.tx.reason != 9 || source_wire.claim
                || field.set_calls || field.replace_calls || field.invalid_call) return 0;
            send_source(RevokeMessage);
            if (source_wire.state != 3 || source_mapped || source_allocated || field.invalid_call) return 0;
            continue;
        }
        if (source_wire.state != 1 || source_wire.claim || source_wire.tx.status
            || field.set_calls || field.replace_calls || field.invalid_call) return 0;
        switch (fault) {
        case 10: case 11: case 12: case 13: field.qualification_fault = fault - 9; break;
        case 0: case 8: break; // Fresh grants before and after the rejected grants can execute.
        case 1: field.input_context = handle(6); break;
        case 2: field.keyboard_layout = handle(6); break;
        case 3: field.change_input_at_password = 1; break; // Final qualification can reenter.
        case 4: field.scenario = InputContextChangedAfterSelection; break;
        case 5: {
            CallMessage context = {0, 0, 0x281, handle(SourceWindow)};
            TypeLayCallWndProc(0, 0, (LPARAM)&context);
            context.wparam = 1; // Same HIMC/HKL is not new authority after deactivate/activate.
            TypeLayCallWndProc(0, 0, (LPARAM)&context);
            break;
        }
        case 6:
            field.input_context = handle(6);
            send_source(PollMessage); // An observed identity mismatch must remain revoked.
            field.input_context = handle(4);
            break;
        case 7: {
            CallMessage composition = {0, 0, 0x10e, handle(SourceWindow)};
            TypeLayCallWndProc(0, 0, (LPARAM)&composition); // END never re-arms the grant.
            break;
        }
        }
        send_source(ActivateMessage);
        if (fault == 0 || fault == 8) {
            if (source_wire.claim != 1 || source_wire.tx.status != 1 || source_wire.tx.reason
                || field.set_calls != 1 || field.replace_calls != 1 || !equal(field.text, written_text, 15)) return 0;
        } else {
            int claimed = fault == 4 || fault == 13;
            if (source_wire.claim != (uint32_t)claimed || source_wire.tx.status != 2
                || field.set_calls != claimed || field.replace_calls || !equal(field.text, initial_text, 13)
                || field.start != (claimed ? 4 : 12) || field.end != (claimed ? 7 : 12)) return 0;
            if (source_wire.tx.reason != (uint32_t)(fault >= 10 ? 2 : fault == 7 ? 15 : 9)
                || (fault >= 10 && field.injected_changes != 1)) return 0;
        }
        uint32_t status = source_wire.tx.status, reason = source_wire.tx.reason;
        field.input_context = handle(4); field.keyboard_layout = handle(5);
        send_source(PollMessage); // Equality cannot reopen a terminal grant.
        if (source_wire.state != 2 || source_wire.tx.status != status || source_wire.tx.reason != reason) return 0;
        send_source(RevokeMessage);
        if (source_wire.state != 3 || source_mapped || source_allocated || field.invalid_call
            || field.context_gets != field.context_releases) return 0;
    }
    source_wire_enabled = 0;
    return 1;
}
static int prepare_source_message(void) {
    source_wire_enabled = 1;
    message_next_fault = message_nested_calls = message_change_at_limit = 0;
    message_pending = 0;
    wipe(&field, sizeof(field));
    field.cookie = LifetimeCookie;
    prepare_transaction(Unchanged);
    wipe(&source_wire, sizeof(source_wire));
    source_command = 0;
    source_wire.version = 3; source_wire.bytes = sizeof(source_wire);
    source_wire.generation[0] = 1; source_wire.grant[0] = 71;
    source_wire.observed_at_ms = 900;
    copy(&source_wire.tx, &request, sizeof(request));
    send_source(InstallMessage);
    if (source_wire.state != 1 || source_wire.claim || field.invalid_call) return 0;
    return 1;
}
static int source_removed_message_commits_before_selection(void) {
    if (!prepare_source_message()) return 0;
    TestQueueMessage message = {handle(SourceWindow), 0x100, 9, 0x000f0001, 1000, {0, 0}, 0};
    retrieved_message = &message;
    message_next_calls = 0;
    TypeLaySourceGetMessageProc(0, 1, (LPARAM)&message);
    retrieved_message = 0;
    if (message.message || message.wparam != 9 || message.lparam != 0x000f0001
        || message_next_calls != 1 || source_wire.claim != 1 || source_wire.invoked != 1
        || source_wire.acknowledgement != 1 || source_wire.acknowledged_operation != 1
        || field.set_calls != 1 || field.replace_calls != 1 || !equal(field.text, written_text, 15)
        || field.invalid_call || source_mapped || source_allocated) return 0;
    send_source(PollMessage);
    if (source_wire.tx.status != 1 || source_wire.state != 2 || source_wire.tx.reason
        || source_wire.tx.length != 14 || source_wire.verified_at_ms != 1000) return 0;
    send_source(RevokeMessage);
    source_wire_enabled = 0;
    return source_wire.state == 3 && !source_mapped && !source_allocated && !field.invalid_call;
}
static int source_message_noremove_and_nested_remove_do_not_spend_the_grant(void) {
    if (!prepare_source_message()) return 0;
    TestQueueMessage message = {handle(SourceWindow), 0x100, 9, 0x000f0001, 1000, {0, 0}, 0};
    retrieved_message = &message;
    message_next_calls = 0;
    for (int i = 0; i < 2; i++) {
        TypeLaySourceGetMessageProc(0, 0, (LPARAM)&message);
        if (message.message != 0x100 || source_wire.invoked || source_wire.claim
            || field.set_calls || field.replace_calls) return 0;
    }
    message_next_fault = 2;
    TypeLaySourceGetMessageProc(0, 1, (LPARAM)&message);
    retrieved_message = 0;
    if (message.message || source_wire.invoked != 1 || source_wire.claim != 1
        || message_next_calls != 3 || message_nested_calls != 1 || field.set_calls != 1
        || field.replace_calls != 1 || field.invalid_call) return 0;
    send_source(RevokeMessage);
    source_wire_enabled = 0;
    return source_wire.state == 3 && !source_mapped && !source_allocated && !field.invalid_call;
}
static int source_message_refusal_never_restores_or_replays_the_original(void) {
    // An entry bypass followed by source invalidation has the same public
    // EpochChanged denial, but never spent this grant's shared invoked word.
    if (!prepare_source_message()) return 0;
    TestQueueMessage bypassed = {handle(OtherField), 0x100, 9, 0x000f0001, 1000, {0, 0}, 0};
    TypeLaySourceGetMessageProc(0, 1, (LPARAM)&bypassed);
    if (source_wire.invoked || source_wire.claim || source_wire.tx.status || field.set_calls || field.replace_calls) return 0;
    CallMessage changed = {0, 0, 0x0c, handle(SourceWindow)};
    TypeLayCallWndProc(0, 0, (LPARAM)&changed);
    send_source(RevokeMessage);
    if (bypassed.message != 0x100 || source_wire.state != 3 || source_wire.tx.status != 2
        || source_wire.tx.reason != 15 || source_wire.invoked || source_wire.claim
        || source_mapped || source_allocated || field.invalid_call) return 0;
    source_wire_enabled = 0;
    for (int fault = 1; fault <= 10; fault++) {
        if (!prepare_source_message()) return 0;
        TestQueueMessage message = {handle(SourceWindow), 0x100, 9, 0x000f0001, 1000, {0, 0}, 0};
        retrieved_message = &message;
        message_next_calls = 0;
        switch (fault) {
        case 1: case 3: case 4: message_next_fault = fault; break;
        case 2: field.text[0] = 'X'; break;
        case 5: message_pending = 1; break;
        case 6: field.precise_100ns = 10010000; break;
        case 7: field.cookie = 0; break;
        case 8: {
            CallMessage changed = {0, 0, 0x0c, handle(SourceWindow)};
            TypeLayCallWndProc(0, 0, (LPARAM)&changed);
            break;
        }
        case 9: field.scenario = InputContextChangedAfterSelection; break;
        case 10: message_change_at_limit = 1; break;
        }
        TypeLaySourceGetMessageProc(0, 1, (LPARAM)&message);
        retrieved_message = 0;
        int claimed = fault == 9;
        const uint32_t reasons[] = {0, 15, 3, 15, 14, SOURCE_PENDING_WORK | 1, 14, 2, 15, 9, 15};
        if (message.message != (claimed ? 0u : 0x100u)
            || message.wparam != (fault == 1 ? 0x76u : 9u)
            || message.lparam != (fault == 10 ? 0x000e0001 : 0x000f0001)
            || source_wire.invoked != 1 || source_wire.claim != (fault == 4 ? 2u : (uint32_t)claimed)
            || source_wire.tx.status != 2 || source_wire.tx.reason != reasons[fault]
            || message_next_calls != 1 || field.set_calls != claimed || field.replace_calls
            || field.invalid_call || source_mapped || source_allocated) return 0;
        uint32_t reason = source_wire.tx.reason;
        // Equal state after refusal never gives this grant another attempt.
        field.cookie = LifetimeCookie; field.precise_100ns = 10000000;
        field.input_context = handle(4); field.text[0] = initial_text[0]; message_pending = 0;
        message.message = 0x100; message.wparam = 9; message.lparam = 0x000f0001;
        TypeLaySourceGetMessageProc(0, 1, (LPARAM)&message);
        if (message.message != 0x100 || source_wire.tx.reason != reason
            || field.set_calls != claimed || field.replace_calls) return 0;
        send_source(PollMessage);
        if (source_wire.state != 2 || source_wire.tx.reason != reason) return 0;
        send_source(RevokeMessage);
        if (source_wire.state != 3 || source_wire.tx.status != 2 || source_wire.tx.reason != reason
            || source_wire.invoked != 1 || source_mapped || source_allocated || field.invalid_call) return 0;
        source_wire_enabled = 0;
    }
    return 1;
}
static int run_checks(void) {
    if (!unchanged_writes_scope_and_verifies()) return 1;
    if (!changed_focus_rejects_without_text_write()) return 2;
    if (!changed_selection_rejects_without_text_write()) return 3;
    if (!expired_original_authorization_rejects_with_fresh_transaction_budget()) return 4;
    if (!exact_field_foreground_keeps_the_original_transaction_guards()) return 17;
    if (!unknown_wire_operations_cannot_read_the_source()) return 10;
    if (!source_input_identity_is_bound_until_exact_retirement()) return 18;
    if (!source_removed_message_commits_before_selection()) return 19;
    if (!source_message_noremove_and_nested_remove_do_not_spend_the_grant()) return 20;
    if (!source_message_refusal_never_restores_or_replays_the_original()) return 21;
    if (!owned_lifetime_cannot_be_restored_by_capture_or_release()) return 5;
    if (!automatic_lifetime_cannot_use_the_ordinary_write_route()) return 12;
    if (!owned_preflight_refuses_without_consuming_or_mutating()) return 6;
    if (!owned_acceptance_reports_one_execution_and_cannot_replay()) return 7;
    if (!accepted_owned_focus_round_trip_keeps_the_real_rejection()) return 8;
    if (!owned_capture_rechecks_the_exact_focus_revision_before_reading()) return 11;
    if (!owned_capture_rechecks_dynamic_qualification_around_reading()) return 13;
    if (!owned_input_probe_is_body_free_and_requires_current_clear_identity()) return 14;
    if (!owned_input_identity_is_rechecked_inside_acceptance()) return 15;
    if (!accepted_input_context_change_cannot_reach_text_mutation()) return 16;
    if (!failed_focus_mirror_permanently_retires_owned_authority()) return 9;
    return 0;
}

#ifdef _WIN32
__declspec(dllimport) void ExitProcess(uint32_t code);
void TestEntry(void) { ExitProcess((uint32_t)run_checks()); }
#else
int main(void) { return run_checks(); }
#endif
