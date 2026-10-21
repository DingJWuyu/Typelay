// Isolated test DLL, never a registered TIP or production NativeInput dependency.
#include "protocol.h"
#include <msctf.h>
#include <new>
#include <cwchar>

namespace {
using namespace probe;
struct State {
    Exchange* exchange;
    HANDLE parent;
    HMODULE lifetimeModule;
    HWND root, field;
    DWORD thread;
    volatile LONG destroyed = 0;
};
// One bounded request. Mapping, parent handle and ordinary module reference live
// until this exclusively owned test process exits. There is no runtime-unload API.
State* state = nullptr;
bool attempted = false;
void Finish(State* s, Status status, Reason reason) noexcept {
    s->exchange->reason = reason;
    InterlockedExchange(&s->exchange->status, status);
}
Reason BasicGuard(State* s) noexcept {
    auto* e = s->exchange;
    const auto lifetime = LifetimeReason(InterlockedCompareExchange(&e->cancelled, 0, 0) != 0,
        GetTickCount64(), e->deadline, WaitForSingleObject(s->parent, 0) == WAIT_TIMEOUT,
        InterlockedCompareExchange(&s->destroyed, 0, 0) != 0);
    if (lifetime != None) return lifetime;
    DWORD pid = 0;
    if (GetCurrentThreadId() != s->thread || !IsWindow(s->field)
        || GetWindowThreadProcessId(s->field, &pid) != s->thread || pid != e->targetPid
        || GetAncestor(s->field, GA_ROOT) != s->root) return TargetGone;
    if (GetForegroundWindow() != s->root || GetFocus() != s->field) return FocusChanged;
    wchar_t name[32]{};
    if (!GetClassNameW(s->field, name, 32) || std::wcscmp(name, L"Edit") != 0
        || (GetWindowLongPtrW(s->field, GWL_STYLE) & (ES_PASSWORD | ES_READONLY))
        || !IsWindowVisible(s->field) || !IsWindowEnabled(s->field)) return TargetGone;
    return None;
}
bool SameObject(IUnknown* a, IUnknown* b) noexcept {
    IUnknown *first = nullptr, *second = nullptr;
    const HRESULT left = a->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&first));
    const HRESULT right = b->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&second));
    const bool same = SUCCEEDED(left) && SUCCEEDED(right) && first == second;
    if (first) first->Release();
    if (second) second->Release();
    return same;
}
class Session final : public ITfEditSession {
    LONG references_ = 1;
    State* state_;
    ITfThreadMgr* manager_;
    ITfContext* context_;
    Reason Guard() noexcept {
        auto reason = BasicGuard(state_);
        if (reason != None) return reason;
        ITfDocumentMgr* document = nullptr;
        ITfContext* current = nullptr;
        ITfContextView* view = nullptr;
        HWND window = nullptr;
        HRESULT hr = manager_->GetFocus(&document);
        if (SUCCEEDED(hr) && document) hr = document->GetTop(&current);
        const bool same = SUCCEEDED(hr) && current && SameObject(current, context_);
        if (current) current->Release();
        if (document) document->Release();
        if (!same) return FocusChanged;
        hr = context_->GetActiveView(&view);
        if (SUCCEEDED(hr) && view) hr = view->GetWnd(&window);
        if (view) view->Release();
        if (FAILED(hr) || window != state_->field) return AmbiguousWindow;
        return BasicGuard(state_);
    }
    HRESULT Read(TfEditCookie cookie) {
        auto* e = state_->exchange;
        auto reason = Guard();
        if (reason != None) { Finish(state_, Rejected, reason); return S_OK; }
        TF_STATUS status{};
        if (FAILED(context_->GetStatus(&status))) { Finish(state_, Unsupported, NoContext); return S_OK; }
        if (status.dwStaticFlags & TF_SS_TRANSITORY) { Finish(state_, Unsupported, Transitory); return S_OK; }
        ITfContextComposition* compositions = nullptr;
        IEnumITfCompositionView* enumerator = nullptr;
        ITfCompositionView* composition = nullptr;
        ULONG fetched = 0;
        reason = Guard();
        if (reason != None) { Finish(state_, Rejected, reason); return S_OK; }
        HRESULT hr = context_->QueryInterface(IID_ITfContextComposition, reinterpret_cast<void**>(&compositions));
        if (SUCCEEDED(hr)) hr = compositions->EnumCompositions(&enumerator);
        if (SUCCEEDED(hr)) {
            reason = Guard();
            hr = reason == None ? enumerator->Next(1, &composition, &fetched) : E_ABORT;
        }
        if (composition) composition->Release();
        if (enumerator) enumerator->Release();
        if (compositions) compositions->Release();
        if (reason != None) { Finish(state_, Rejected, reason); return S_OK; }
        if (FAILED(hr)) { Finish(state_, Unsupported, ReadFailed); return S_OK; }
        e->compositionCount = fetched;
        if (fetched) { Finish(state_, Rejected, Composing); return S_OK; }
        ITfRange *range = nullptr, *end = nullptr, *prefix = nullptr, *selected = nullptr;
        TF_SELECTION selection{};
        wchar_t text[kMaxText + 1]{};
        wchar_t scratch[kMaxText + 1]{};
        ULONG read = 0, selectionStart = 0, selectionLength = 0;
        // Generic ranges only: ACP offsets are reserved for the text-store owner.
        // Reading advances these private range clones, never the document/selection.
        auto ReadBounded = [&](ITfRange* source, wchar_t* buffer, ULONG* count) -> HRESULT {
            reason = Guard();
            if (reason != None) return E_ABORT;
            HRESULT result = source->GetText(cookie, TF_TF_MOVESTART, buffer,
                static_cast<ULONG>(kMaxText + 1), count);
            BOOL empty = FALSE;
            if (SUCCEEDED(result)) {
                reason = Guard();
                result = reason == None ? source->IsEmpty(cookie, &empty) : E_ABORT;
            }
            if (SUCCEEDED(result) && (!empty || *count > kMaxText)) {
                reason = TooLong; return E_ABORT;
            }
            return result;
        };
        reason = Guard();
        hr = reason == None ? context_->GetStart(cookie, &range) : E_ABORT;
        if (SUCCEEDED(hr)) { reason = Guard(); hr = reason == None ? context_->GetEnd(cookie, &end) : E_ABORT; }
        if (SUCCEEDED(hr)) { reason = Guard(); hr = reason == None ? range->ShiftEndToRange(cookie, end, TF_ANCHOR_END) : E_ABORT; }
        if (SUCCEEDED(hr)) {
            reason = Guard();
            hr = reason == None ? context_->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selection, &fetched) : E_ABORT;
        }
        if (SUCCEEDED(hr) && (fetched != 1 || !selection.range)) hr = E_FAIL;
        if (SUCCEEDED(hr)) hr = range->Clone(&prefix);
        if (SUCCEEDED(hr)) {
            reason = Guard();
            hr = reason == None ? prefix->ShiftEndToRange(cookie, selection.range, TF_ANCHOR_START) : E_ABORT;
        }
        if (SUCCEEDED(hr)) hr = selection.range->Clone(&selected);
        if (SUCCEEDED(hr)) hr = ReadBounded(range, text, &read);
        if (SUCCEEDED(hr)) hr = ReadBounded(prefix, scratch, &selectionStart);
        SecureZeroMemory(scratch, sizeof(scratch));
        if (SUCCEEDED(hr)) hr = ReadBounded(selected, scratch, &selectionLength);
        SecureZeroMemory(scratch, sizeof(scratch));
        if (SUCCEEDED(hr) && !RangeLengthsValid(read, selectionStart, selectionLength)) hr = E_FAIL;
        if (SUCCEEDED(hr)) reason = Guard();
        if (selected) selected->Release();
        if (prefix) prefix->Release();
        if (selection.range) selection.range->Release();
        if (end) end->Release();
        if (range) range->Release();
        if (reason != None) { SecureZeroMemory(text, sizeof(text)); Finish(state_, Rejected, reason); return S_OK; }
        if (FAILED(hr)) { SecureZeroMemory(text, sizeof(text)); Finish(state_, Unsupported, ReadFailed); return S_OK; }
        e->textLength = read;
        e->selectionStart = selectionStart;
        e->selectionLength = selectionLength;
        e->fingerprint = Fingerprint(text, read);
        e->exactSyntheticMatch = read == kTextLength && std::wmemcmp(text, kText, read) == 0;
        SecureZeroMemory(text, sizeof(text));
        Finish(state_, e->exactSyntheticMatch ? Observed : Rejected, e->exactSyntheticMatch ? None : ContentMismatch);
        return S_OK;
    }
public:
    // Takes ownership of the caller's references, not pointers to its stack.
    Session(State* s, ITfThreadMgr* manager, ITfContext* context) : state_(s), manager_(manager), context_(context) {}
    ~Session() {
        context_->Release();
        manager_->Release();
        InterlockedExchange(&state_->exchange->sessionDestroyed, 1);
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid != IID_IUnknown && iid != IID_ITfEditSession) return E_NOINTERFACE;
        *value = static_cast<ITfEditSession*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references_); }
    ULONG STDMETHODCALLTYPE Release() override {
        const LONG count = InterlockedDecrement(&references_);
        if (!count) delete this;
        return count;
    }
    HRESULT STDMETHODCALLTYPE DoEditSession(TfEditCookie cookie) override {
        if (InterlockedIncrement(&state_->exchange->callbackCount) != 1) {
            Finish(state_, Rejected, InvalidRequest); return S_OK;
        }
        try { return Read(cookie); }
        catch (...) { Finish(state_, Fault, CallbackException); return E_FAIL; }
    }
};
void Submit(State* s) {
    InterlockedOr(&s->exchange->phases, SubmitEntered);
    auto reason = BasicGuard(s);
    if (reason != None) { Finish(s, Rejected, reason); return; }
    const HMODULE tsf = LoadLibraryExW(L"msctf.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!tsf) { Finish(s, Unsupported, NoManager); return; }
    using GetManager = HRESULT (WINAPI*)(ITfThreadMgr**);
    auto get = reinterpret_cast<GetManager>(GetProcAddress(tsf, "TF_GetThreadMgr"));
    ITfThreadMgr* manager = nullptr;
    HRESULT hr = get ? get(&manager) : E_NOINTERFACE;
    // Keep msctf loaded for this owned process too; never unload under COM objects.
    if (FAILED(hr) || !manager) { Finish(s, Unsupported, NoManager); return; }
    ITfClientId* clients = nullptr;
    TfClientId id = TF_CLIENTID_NULL;
    hr = manager->QueryInterface(IID_ITfClientId, reinterpret_cast<void**>(&clients));
    if (SUCCEEDED(hr)) hr = clients->GetClientId(s->exchange->client, &id);
    if (clients) clients->Release();
    if (FAILED(hr) || id == TF_CLIENTID_NULL) { manager->Release(); Finish(s, Unsupported, NoClient); return; }
    ITfDocumentMgr* document = nullptr;
    ITfContext* context = nullptr;
    hr = manager->GetFocus(&document);
    if (SUCCEEDED(hr) && document) hr = document->GetTop(&context);
    if (document) document->Release();
    if (FAILED(hr) || !context) { manager->Release(); Finish(s, Unsupported, NoContext); return; }
    auto* session = new(std::nothrow) Session(s, manager, context);
    if (!session) { context->Release(); manager->Release(); Finish(s, Fault, CallbackException); return; }
    HRESULT sessionHr = E_FAIL;
    InterlockedOr(&s->exchange->phases, RequestCalled);
    hr = context->RequestEditSession(id, session, TF_ES_ASYNC | TF_ES_READ, &sessionHr);
    s->exchange->requestHr = hr;
    s->exchange->sessionHr = sessionHr;
    InterlockedOr(&s->exchange->phases, RequestReturned);
    if (FAILED(hr) || sessionHr != TF_S_ASYNC) Finish(s, Unsupported, SessionDenied);
    session->Release();
}
}

extern "C" __declspec(dllexport) LRESULT CALLBACK TypeLayTsfReadOnlyHook(int code, WPARAM wp, LPARAM lp) {
    if (code >= 0 && lp) {
        const auto* message = reinterpret_cast<const CWPSTRUCT*>(lp);
        if (state && message->hwnd == state->field && (message->message == WM_DESTROY || message->message == WM_NCDESTROY))
            InterlockedExchange(&state->destroyed, 1);
        if (!attempted && message->message == RegisterWindowMessageW(probe::kMessage)) {
            attempted = true;
            try {
                const HANDLE mapping = reinterpret_cast<HANDLE>(message->wParam);
                auto* e = static_cast<probe::Exchange*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(probe::Exchange)));
                if (e && probe::HeaderValid(*e) && e->targetPid == GetCurrentProcessId()
                    && e->targetThread == GetCurrentThreadId() && e->field == reinterpret_cast<uint64_t>(message->hwnd)
                    && e->targetCreated == probe::Created(GetCurrentProcess())) {
                    InterlockedOr(&e->phases, OwnedEntry);
                    HANDLE parent = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, e->parentPid);
                    HMODULE self = nullptr;
                    if (parent && probe::Created(parent) == e->parentCreated
                        && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                            reinterpret_cast<LPCWSTR>(&TypeLayTsfReadOnlyHook), &self)) {
                        InterlockedOr(&e->phases, ValidationPassed);
                        state = new State{e, parent, self, reinterpret_cast<HWND>(e->root), reinterpret_cast<HWND>(e->field), e->targetThread};
                        CloseHandle(mapping);
                        Submit(state);
                    } else {
                        if (parent) CloseHandle(parent);
                        e->reason = probe::InvalidRequest;
                        InterlockedExchange(&e->status, probe::Fault);
                        UnmapViewOfFile(e);
                        CloseHandle(mapping);
                    }
                } else if (e) UnmapViewOfFile(e);
            } catch (...) {
                if (state) Finish(state, probe::Fault, probe::CallbackException);
            }
        }
    }
    return CallNextHookEx(nullptr, code, wp, lp);
}
