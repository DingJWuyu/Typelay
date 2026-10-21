#pragma once
// Test-only ABI. No source text crosses this mapping.
#include <windows.h>
#include <cstdint>
#include <type_traits>

namespace probe {
constexpr uint32_t kMagic = 0x46535450;
constexpr uint32_t kVersion = 2;
constexpr size_t kMaxText = 4096;
constexpr wchar_t kMessage[] = L"TypeLay.Isolated.TsfReadOnly.v2";
constexpr wchar_t kText[] = L"TypeLay isolated read-only probe\r\nThis document belongs to this test only: \u4e2d\u6587 \U0001F600.\r\n";
constexpr uint32_t kTextLength = static_cast<uint32_t>(sizeof(kText) / sizeof(wchar_t) - 1);
enum Status : LONG { Pending = 0, Observed = 1, Unsupported = 2, Rejected = 3, Fault = 4 };
enum Reason : LONG {
    None = 0, InvalidRequest, ParentGone, Expired, TargetGone, FocusChanged,
    NoManager, NoClient, NoContext, AmbiguousWindow, Transitory, SessionDenied,
    ReadFailed, TooLong, Composing, ContentMismatch, CallbackException
};
enum Phase : LONG { OwnedEntry = 1, ValidationPassed = 2, SubmitEntered = 4, RequestCalled = 8, RequestReturned = 16 };
struct Exchange {
    uint32_t magic, version, size, parentPid, targetPid, targetThread;
    uint64_t parentCreated, targetCreated, root, field, deadline;
    GUID client;
    volatile LONG cancelled, status, reason, sessionDestroyed, callbackCount, phases;
    LONG requestHr, sessionHr;
    uint32_t textLength, selectionStart, selectionLength, compositionCount;
    uint64_t fingerprint;
    uint32_t exactSyntheticMatch;
};
static_assert(sizeof(void*) == 8, "Only isolated x64 targets are supported");
static_assert(std::is_standard_layout_v<Exchange>);
static_assert(sizeof(Exchange) <= 256);
inline Reason LifetimeReason(bool cancelled, uint64_t now, uint64_t deadline, bool parentAlive, bool destroyed) {
    if (cancelled || now >= deadline) return Expired;
    if (!parentAlive) return ParentGone;
    return destroyed ? TargetGone : None;
}
inline bool RangeLengthsValid(uint32_t text, uint32_t start, uint32_t length) {
    return text <= kMaxText && start <= text && length <= text - start;
}
inline uint64_t Created(HANDLE process) {
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process, &created, &exited, &kernel, &user)) return 0;
    return (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
}
inline bool HeaderValid(const Exchange& e) {
    return e.magic == kMagic && e.version == kVersion && e.size == sizeof(Exchange)
        && e.parentPid && e.targetPid && e.targetThread && e.parentCreated && e.targetCreated
        && e.root && e.field && e.deadline;
}
inline uint64_t Fingerprint(const wchar_t* text, uint32_t length) {
    // Non-cryptographic diagnostic only; success also requires exact comparison.
    uint64_t value = 14695981039346656037ULL;
    for (uint32_t i = 0; i < length; ++i) {
        value ^= static_cast<uint16_t>(text[i]);
        value *= 1099511628211ULL;
    }
    return value;
}
}
