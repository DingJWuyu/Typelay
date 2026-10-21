// Never invoke on a user desktop. Real execution requires a separately reviewed
// one-shot command on a disposable GitHub-hosted Windows VM.
#include "protocol.h"
#include "outcome.h"
#include <objbase.h>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <string>
#include <stdexcept>

namespace {
using namespace probe;
struct Windows { DWORD pid; HWND root = nullptr, field = nullptr; unsigned roots = 0, fields = 0; };
BOOL CALLBACK Child(HWND hwnd, LPARAM data) noexcept {
    auto* windows = reinterpret_cast<Windows*>(data);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    wchar_t name[64]{};
    if (pid == windows->pid && GetClassNameW(hwnd, name, 64) && std::wcscmp(name, L"Edit") == 0) {
        windows->field = hwnd; ++windows->fields;
    }
    return TRUE;
}
BOOL CALLBACK Top(HWND hwnd, LPARAM data) noexcept {
    auto* windows = reinterpret_cast<Windows*>(data);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    wchar_t name[64]{};
    if (pid == windows->pid && IsWindowVisible(hwnd) && GetAncestor(hwnd, GA_ROOT) == hwnd
        && GetClassNameW(hwnd, name, 64) && std::wcscmp(name, L"Notepad") == 0) {
        windows->root = hwnd; ++windows->roots;
    }
    return TRUE;
}
Windows FindWindows(DWORD pid) noexcept {
    Windows windows{pid};
    EnumWindows(Top, reinterpret_cast<LPARAM>(&windows));
    if (windows.roots == 1) EnumChildWindows(windows.root, Child, reinterpret_cast<LPARAM>(&windows));
    return windows;
}
bool Env(const wchar_t* name, const wchar_t* expected) noexcept {
    wchar_t value[128]{};
    const DWORD size = GetEnvironmentVariableW(name, value, 128);
    return size && size < 128 && std::wcscmp(value, expected) == 0;
}
struct Diagnostics {
    bool policyQueried = false, signatureKnown = false, dynamicKnown = false;
    DWORD signatureError = 0, dynamicError = 0, signatureFlags = 0, dynamicFlags = 0;
    bool sendReturned = false;
    LRESULT sendReturn = 0;
    DWORD sendError = 0;
    DWORD_PTR messageResult = 0;
    ULONGLONG sendStarted = 0, sendEnded = 0;
    void QueryPolicies(HANDLE process) noexcept {
        policyQueried = true;
        PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY signature{};
        SetLastError(ERROR_SUCCESS);
        signatureKnown = GetProcessMitigationPolicy(process, ProcessSignaturePolicy, &signature, sizeof(signature)) != FALSE;
        signatureError = GetLastError();
        if (signatureKnown) signatureFlags = signature.Flags & 0x1f;
        PROCESS_MITIGATION_DYNAMIC_CODE_POLICY dynamic{};
        SetLastError(ERROR_SUCCESS);
        dynamicKnown = GetProcessMitigationPolicy(process, ProcessDynamicCodePolicy, &dynamic, sizeof(dynamic)) != FALSE;
        dynamicError = GetLastError();
        if (dynamicKnown) dynamicFlags = dynamic.Flags & 0x0f;
    }
    void Print(LONG phases, bool attempted) const noexcept {
        std::printf("\"diagnostics\":{\"protocolVersion\":%u,\"targetPhases\":%ld,"
            "\"policyQueried\":%s,\"signatureKnown\":%s,\"signatureError\":%lu,\"signatureFlags\":%lu,"
            "\"dynamicKnown\":%s,\"dynamicError\":%lu,\"dynamicFlags\":%lu,"
            "\"sendAttempted\":%s,\"sendReturned\":%s,\"sendReturn\":%lld,\"sendLastError\":%lu,"
            "\"sendStartedTick\":%llu,\"sendEndedTick\":%llu,\"messageResultValid\":%s,\"messageResult\":",
            kVersion, phases, policyQueried ? "true" : "false", signatureKnown ? "true" : "false", signatureError, signatureFlags,
            dynamicKnown ? "true" : "false", dynamicError, dynamicFlags, attempted ? "true" : "false", sendReturned ? "true" : "false",
            static_cast<long long>(sendReturn), sendError, sendStarted, sendEnded, attempted && sendReturned && sendReturn != 0 ? "true" : "false");
        if (attempted && sendReturned && sendReturn != 0) std::printf("%llu", static_cast<unsigned long long>(messageResult));
        else std::printf("null");
        std::printf("}");
    }
};
struct Owned {
    HANDLE job = nullptr, process = nullptr, thread = nullptr, targetThread = nullptr, mapping = nullptr;
    Exchange* exchange = nullptr;
    HMODULE module = nullptr;
    HHOOK hook = nullptr;
    DWORD pid = 0;
    bool resumed = false, normalExit = false, forced = false, jobEmpty = false;
    void Close(Exchange* snapshot = nullptr) noexcept {
        if (exchange) InterlockedExchange(&exchange->cancelled, 1);
        if (process) {
            if (WaitForSingleObject(process, 0) == WAIT_TIMEOUT && resumed) {
                const auto windows = FindWindows(pid);
                // Only our held process's exact current root, never a title match.
                DWORD currentPid = 0;
                if (windows.roots == 1) GetWindowThreadProcessId(windows.root, &currentPid);
                const bool requested = currentPid == pid && PostMessageW(windows.root, WM_CLOSE, 0, 0);
                DWORD exitCode = STILL_ACTIVE;
                normalExit = requested && WaitForSingleObject(process, 5000) == WAIT_OBJECT_0
                    && GetExitCodeProcess(process, &exitCode) && exitCode == 0;
            }
            if (!normalExit && WaitForSingleObject(process, 0) == WAIT_TIMEOUT) {
                forced = true;
                if (job) TerminateJobObject(job, 125);
                WaitForSingleObject(process, 5000);
            }
        }
        if (job) {
            const ULONGLONG deadline = GetTickCount64() + 5000;
            do {
                JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
                if (QueryInformationJobObject(job, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr)
                    && accounting.ActiveProcesses == 0) { jobEmpty = true; break; }
                Sleep(20);
            } while (GetTickCount64() < deadline);
        }
        if (job && !jobEmpty) {
            forced = true;
            TerminateJobObject(job, 125);
            const ULONGLONG deadline = GetTickCount64() + 5000;
            do {
                JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
                if (QueryInformationJobObject(job, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr)
                    && accounting.ActiveProcesses == 0) { jobEmpty = true; break; }
                Sleep(20);
            } while (GetTickCount64() < deadline);
        }
        // Snapshot only after all owned target code has exited, not racing a
        // late session destructor or RequestEditSession return publication.
        if (snapshot && exchange && jobEmpty) *snapshot = *exchange;
        // No early unhook/unload while a TSF object might still reference code.
        if (hook) { UnhookWindowsHookEx(hook); hook = nullptr; }
        if (module) { FreeLibrary(module); module = nullptr; }
        if (exchange) { UnmapViewOfFile(exchange); exchange = nullptr; }
        if (mapping) { CloseHandle(mapping); mapping = nullptr; }
        if (targetThread) { CloseHandle(targetThread); targetThread = nullptr; }
        if (thread) { CloseHandle(thread); thread = nullptr; }
        if (process) { CloseHandle(process); process = nullptr; }
        if (job) { CloseHandle(job); job = nullptr; }
    }
    ~Owned() { if (job || process || module || mapping) Close(); }
};
void Require(bool condition, const char* reason) { if (!condition) throw std::runtime_error(reason); }
std::wstring SystemNotepad() {
    wchar_t directory[MAX_PATH]{};
    const UINT size = GetSystemDirectoryW(directory, MAX_PATH);
    Require(size && size < MAX_PATH, "system-directory-unavailable");
    return std::wstring(directory) + L"\\notepad.exe";
}
int ProtocolSelfTest() {
    Exchange e{};
    Require(!HeaderValid(e), "empty-header-accepted");
    e.magic = kMagic; e.version = kVersion; e.size = sizeof(e);
    e.parentPid = 1; e.targetPid = 2; e.targetThread = 3;
    e.parentCreated = 1; e.targetCreated = 2; e.root = 3; e.field = 4; e.deadline = 5;
    Require(HeaderValid(e), "valid-header-refused");
    --e.size; Require(!HeaderValid(e), "truncated-header-accepted");
    ++e.size; e.version = 1; Require(!HeaderValid(e), "old-version-accepted");
    e.version = kVersion + 1; Require(!HeaderValid(e), "unknown-version-accepted");
    Require(kTextLength < kMaxText, "synthetic-input-too-long");
    Require(LifetimeReason(false, 9, 10, true, false) == None, "live-request-refused");
    Require(LifetimeReason(false, 10, 10, true, false) == Expired, "deadline-accepted");
    Require(LifetimeReason(true, 9, 10, true, false) == Expired, "cancelled-accepted");
    Require(LifetimeReason(false, 9, 10, false, false) == ParentGone, "dead-parent-accepted");
    Require(LifetimeReason(false, 9, 10, true, true) == TargetGone, "destroyed-target-accepted");
    Require(RangeLengthsValid(2, 2, 0), "empty-end-selection-refused");
    Require(RangeLengthsValid(2, 0, 2), "surrogate-pair-unit-range-refused");
    Require(!RangeLengthsValid(2, 3, 0), "outside-start-accepted");
    Require(!RangeLengthsValid(2, 1, UINT32_MAX), "overflow-length-accepted");
    Require(!RangeLengthsValid(kMaxText + 1, 0, 0), "oversized-text-accepted");
    std::puts("{\"kind\":\"native-protocol-selftest\",\"passed\":true,\"windowsTsfVerified\":false}");
    return 0;
}
}
int wmain(int argc, wchar_t** argv) {
    using namespace probe;
    if (argc == 2 && std::wcscmp(argv[1], L"--protocol-selftest") == 0) {
        try { return ProtocolSelfTest(); } catch (...) { return 1; }
    }
    // Explicit independent admission; preflight success never enables this mode.
    if (argc != 4 || (!Env(L"GITHUB_ACTIONS", L"true") || !Env(L"RUNNER_ENVIRONMENT", L"github-hosted")
        || !Env(L"RUNNER_OS", L"Windows") || !Env(L"TYPELAY_ISOLATED_TSF_NATIVE", L"1"))) {
        std::puts("{\"status\":\"blocked\",\"reason\":\"separate-isolated-execution-approval-required\"}"); return 2;
    }
    const bool read = std::wcscmp(argv[1], L"--single-read") == 0;
    if (!read && std::wcscmp(argv[1], L"--lifecycle-only") != 0) return 2;
    Owned owned;
    Exchange result{};
    Diagnostics diagnostics;
    const char* reason = "not-run";
    bool started = false, lifecycleValidated = false, dispatched = false, syntheticCreated = false;
    std::wstring synthetic;
    try {
        // argv[2]: caller-created empty artifact directory; argv[3]: exact test DLL.
        const DWORD attributes = GetFileAttributesW(argv[2]);
        Require(attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY)
            && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT), "artifact-directory-invalid");
        GUID nonce{};
        Require(SUCCEEDED(CoCreateGuid(&nonce)), "nonce-unavailable");
        wchar_t nonceText[40]{};
        Require(StringFromGUID2(nonce, nonceText, 40) > 0, "nonce-format-failed");
        synthetic = std::wstring(argv[2]) + L"\\typelay-tsf-" + nonceText + L".txt";
        HANDLE file = CreateFileW(synthetic.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        Require(file != INVALID_HANDLE_VALUE, "synthetic-file-create-failed");
        syntheticCreated = true;
        const wchar_t bom = 0xfeff;
        DWORD written = 0;
        bool wrote = WriteFile(file, &bom, sizeof(bom), &written, nullptr) && written == sizeof(bom);
        wrote = wrote && WriteFile(file, kText, kTextLength * sizeof(wchar_t), &written, nullptr)
            && written == kTextLength * sizeof(wchar_t);
        CloseHandle(file);
        Require(wrote, "synthetic-file-write-failed");
        owned.job = CreateJobObjectW(nullptr, nullptr);
        Require(owned.job != nullptr, "owned-job-unavailable");
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        Require(SetInformationJobObject(owned.job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) != FALSE, "owned-job-limits-failed");
        const std::wstring notepad = SystemNotepad();
        DWORD binaryType = 0;
        Require(GetBinaryTypeW(notepad.c_str(), &binaryType) && binaryType == SCS_64BIT_BINARY, "not-classic-x64-notepad");
        std::wstring command = L"\"" + notepad + L"\" \"" + synthetic + L"\"";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup);
        PROCESS_INFORMATION process{};
        Require(CreateProcessW(notepad.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED,
            nullptr, nullptr, &startup, &process) != FALSE, "notepad-start-failed");
        owned.process = process.hProcess; owned.thread = process.hThread; owned.pid = process.dwProcessId;
        // If assignment fails, the still-suspended child must be killed directly;
        // it is the exact freshly created process handle, not an enumerated PID.
        if (!AssignProcessToJobObject(owned.job, owned.process)) {
            TerminateProcess(owned.process, 125); WaitForSingleObject(owned.process, 5000);
            owned.forced = true; throw std::runtime_error("owned-job-assignment-failed");
        }
        wchar_t actualImage[32768]{}; DWORD imageSize = 32768;
        Require(QueryFullProcessImageNameW(owned.process, 0, actualImage, &imageSize)
            && _wcsicmp(actualImage, notepad.c_str()) == 0, "target-image-mismatch");
        Require(ResumeThread(owned.thread) != static_cast<DWORD>(-1), "target-resume-failed");
        owned.resumed = true; started = true;
        Require(WaitForInputIdle(owned.process, 5000) == 0, "target-input-idle-unavailable");
        Windows windows{owned.pid};
        const ULONGLONG startDeadline = GetTickCount64() + 5000;
        do {
            windows = FindWindows(owned.pid);
            if (windows.roots == 1 && windows.fields == 1) break;
            if (WaitForSingleObject(owned.process, 0) != WAIT_TIMEOUT) break;
            Sleep(20);
        } while (GetTickCount64() < startDeadline);
        Require(windows.roots == 1 && windows.fields == 1, "unique-classic-edit-unavailable");
        lifecycleValidated = true;
        reason = "lifecycle-observed";
        if (read) {
            // Query only the held, newly created owned process. Never relax policy.
            diagnostics.QueryPolicies(owned.process);
            Require(diagnostics.signatureKnown && diagnostics.dynamicKnown, "target-policy-unknown");
            // These signature restrictions cannot admit this locally built unsigned DLL.
            Require((diagnostics.signatureFlags & 7) == 0, "target-signature-policy-incompatible");
            // Dynamic-code prohibition alone is not proof that image loading is blocked.
            DWORD targetPid = 0;
            const DWORD thread = GetWindowThreadProcessId(windows.field, &targetPid);
            Require(thread && targetPid == owned.pid, "target-thread-mismatch");
            owned.targetThread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, thread);
            Require(owned.targetThread && GetProcessIdOfThread(owned.targetThread) == owned.pid
                && WaitForSingleObject(owned.targetThread, 0) == WAIT_TIMEOUT
                && WaitForSingleObject(owned.process, 0) == WAIT_TIMEOUT, "target-thread-not-live");
            owned.mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Exchange), nullptr);
            Require(owned.mapping != nullptr, "mapping-create-failed");
            owned.exchange = static_cast<Exchange*>(MapViewOfFile(owned.mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Exchange)));
            Require(owned.exchange != nullptr, "mapping-view-failed");
            auto* e = owned.exchange;
            e->magic = kMagic; e->version = kVersion; e->size = sizeof(Exchange);
            e->parentPid = GetCurrentProcessId(); e->targetPid = owned.pid; e->targetThread = thread;
            e->parentCreated = Created(GetCurrentProcess()); e->targetCreated = Created(owned.process);
            e->root = reinterpret_cast<uint64_t>(windows.root); e->field = reinterpret_cast<uint64_t>(windows.field);
            e->client = nonce; e->deadline = GetTickCount64() + 3000;
            owned.module = LoadLibraryExW(argv[3], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
            Require(owned.module != nullptr, "test-module-load-failed");
            const auto callback = reinterpret_cast<HOOKPROC>(GetProcAddress(owned.module, "TypeLayTsfReadOnlyHook"));
            Require(callback != nullptr, "test-export-missing");
            // TID API is not an atomic process-creation identity capability. These
            // checks reduce the window, not prove exclusion of every TID reuse.
            Require(WaitForSingleObject(owned.targetThread, 0) == WAIT_TIMEOUT
                && WaitForSingleObject(owned.process, 0) == WAIT_TIMEOUT, "target-ended-before-hook");
            owned.hook = SetWindowsHookExW(WH_CALLWNDPROC, callback, owned.module, thread);
            Require(owned.hook != nullptr, "target-hook-unavailable");
            HANDLE remoteMapping = nullptr;
            Require(DuplicateHandle(GetCurrentProcess(), owned.mapping, owned.process, &remoteMapping, 0, FALSE, DUPLICATE_SAME_ACCESS) != FALSE, "mapping-transfer-failed");
            const UINT message = RegisterWindowMessageW(kMessage);
            Require(message != 0, "message-unavailable");
            dispatched = true;
            // Timeout is not cancellation; remote handle/state belongs to target
            // lifetime. Never close a possibly already-consumed remote handle.
            diagnostics.sendStarted = GetTickCount64();
            SetLastError(ERROR_SUCCESS);
            diagnostics.sendReturn = SendMessageTimeoutW(windows.field, message, reinterpret_cast<WPARAM>(remoteMapping), 0,
                SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, 500, &diagnostics.messageResult);
            diagnostics.sendError = GetLastError(); // No intervening API may overwrite this.
            diagnostics.sendReturned = true;
            diagnostics.sendEnded = GetTickCount64();
            while (GetTickCount64() < e->deadline && WaitForSingleObject(owned.process, 0) == WAIT_TIMEOUT) {
                if (InterlockedCompareExchange(&e->status, 0, 0) != Pending) break;
                Sleep(10);
            }
            InterlockedExchange(&e->cancelled, 1);
            reason = "single-read-not-demonstrated";
        }
    } catch (const std::exception& error) {
        // All exception messages above are fixed classifications, never content.
        reason = error.what();
        // Copy into stable storage before the exception object is destroyed.
        static char saved[96]{};
        strncpy_s(saved, reason, _TRUNCATE); reason = saved;
    } catch (...) { reason = "unexpected-runner-error"; }
    owned.Close(&result);
    const bool removed = started && owned.normalExit && owned.jobEmpty && !owned.forced;
    if (dispatched && result.status == Observed) reason = "single-read-observed";
    if (started && !removed) reason = "normal-exit-or-removal-not-proven";
    // Delete only this synthetic file after target lifetime has demonstrably ended.
    const bool syntheticRemoved = !syntheticCreated || (owned.jobEmpty && DeleteFileW(synthetic.c_str()));
    if (!syntheticRemoved) reason = "synthetic-cleanup-incomplete";
    const bool observed = LifecycleObserved(lifecycleValidated, owned.normalExit, owned.jobEmpty, owned.forced, syntheticRemoved)
        && (!read || (result.status == Observed && result.exactSyntheticMatch == 1));
    std::printf("{\"kind\":\"isolated-tsf-native\",\"status\":\"%s\",\"reason\":\"%s\","
        "\"targetStarted\":%s,\"requestDispatched\":%s,\"normalExit\":%s,\"forcedCleanup\":%s,\"ownedJobEmpty\":%s,"
        "\"syntheticFileRemoved\":%s,\"removalProvenByProcessExit\":%s,\"liveUnloadProven\":false,\"imeVerified\":false,\"writebackAllowed\":false,"
        "\"readSnapshotValid\":%s,\"readStatus\":%ld,\"readReason\":%ld,\"requestHr\":%ld,\"sessionHr\":%ld,\"callbackCount\":%ld,\"sessionDestroyed\":%ld,"
        "\"textLength\":%u,\"selectionStart\":%u,\"selectionLength\":%u,\"compositionCount\":%u,"
        "\"syntheticExactMatch\":%s,\"noncryptographicFingerprint\":\"%016llx\",",
        observed ? "observed-not-acceptance" : "not-supported-or-incomplete", reason,
        started ? "true" : "false", dispatched ? "true" : "false", owned.normalExit ? "true" : "false",
        owned.forced ? "true" : "false", owned.jobEmpty ? "true" : "false", syntheticRemoved ? "true" : "false", removed ? "true" : "false",
        result.status == Observed ? "true" : "false", result.status, result.reason, result.requestHr, result.sessionHr, result.callbackCount, result.sessionDestroyed,
        result.textLength, result.selectionStart, result.selectionLength, result.compositionCount,
        result.exactSyntheticMatch ? "true" : "false", result.fingerprint);
    diagnostics.Print(result.phases, dispatched);
    std::puts("}");
    return observed ? 0 : 2;
}
