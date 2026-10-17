// SPDX-License-Identifier: MIT
// TypeLay 2026-10-12: CI research ABI; exact Qt provider build required.
#ifndef TYPELAY_FCITX_QT_V1_H
#define TYPELAY_FCITX_QT_V1_H
#include <QtCore/QObject>
#include <QtCore/QEvent>
#include <QtCore/QtPlugin>

inline constexpr char TypeLayFcitxQtProviderPropertyV1[] = "typelay.fcitx.qt.provider.v1";
inline constexpr char TypeLayFcitxQtConsumerPropertyV1[] = "typelay.fcitx.qt.consumer.v1";

// QVariantList of uint native/XKB keyvals from the consumer's host configuration.
inline constexpr char TypeLayFcitxQtControlKeysPropertyV1[] = "typelay.fcitx.qt.control-keys.v1";
// Only installed Ctrl+Fn symbols. This advertises routing, never permission.
inline constexpr char TypeLayFcitxQtOrderedKeysPropertyV1[] = "typelay.fcitx.qt.ordered-keys.v1";

enum TypeLayFcitxQtFlagV1 : quint32 {
    TlQtKnown = 1u, TlQtFocused = 2u, TlQtPending = 4u, TlQtComposing = 8u,
    TlQtDetached = 16u
};
enum TypeLayFcitxQtCauseV1 : quint32 {
    TlQtInput = 1u, TlQtFocus = 2u, TlQtBinding = 4u, TlQtModifier = 8u,
    // Only the exact original Ctrl submission/replay, not ForwardKey(Control).
    TlQtOrderedPrefix = 16u
};
struct TypeLayFcitxQtStateV1 {
    quint32 size = sizeof(TypeLayFcitxQtStateV1);
    quint32 version = 1;
    quint64 contextGeneration = 0;
    quint64 fieldGeneration = 0;
    quint64 epoch = 0;
    quint64 inputEpoch = 0;
    quint64 serverSerial = 0;
    quint64 appliedSerial = 0;
    quint32 flags = 0;
};
// The focused object, or a proven same-window detached QLineEdit, holds a
// non-owning QObject* provider property. Known|Detached is direct-write-only;
// its epoch and sealed serial belong to the old field, not the current IM.
// prepareCaret on Detached is local validation only and never resets another field.
// Consumers must hold QPointer, qobject_cast the IID, and reacquire before calls.
// A successful inspect establishes a short-lived proof for this exact object.
// validateLocal does no IPC/event iteration and cannot independently establish Clear.
// Pure focus changes invalidate epoch, but do not advance the field's inputEpoch.
class TypeLayFcitxQtV1 {
public:
    virtual ~TypeLayFcitxQtV1() = default;
    virtual bool inspect(QObject *actual, TypeLayFcitxQtStateV1 *state) = 0;
    virtual bool validateLocal(QObject *actual, const TypeLayFcitxQtStateV1 *state) = 0;
    virtual bool prepareCaret(QObject *actual, const TypeLayFcitxQtStateV1 *state) = 0;
};
#define TypeLayFcitxQtV1_iid "org.typelay.FcitxQtProvider/1.0"
Q_DECLARE_INTERFACE(TypeLayFcitxQtV1, TypeLayFcitxQtV1_iid)

// Additive capability: old V1 providers do not advertise dynamic key ownership.
// This checks the actual provider/field binding and installed XKB key list without
// IPC or event iteration. It never establishes an IME proof or grants a permit.
class TypeLayFcitxQtControlKeysV1 {
public:
    virtual ~TypeLayFcitxQtControlKeysV1() = default;
    virtual bool supportsControlKeys(QObject *actual, quint32 accept, quint32 exit) const = 0;
};
#define TypeLayFcitxQtControlKeysV1_iid "org.typelay.FcitxQtControlKeys/1.0"
Q_DECLARE_INTERFACE(TypeLayFcitxQtControlKeysV1, TypeLayFcitxQtControlKeysV1_iid)

// Called on the UI thread before the original IM key request, only for control
// keys. True means the consumer synchronously authorized and consumed this key.
// False leaves original input processing intact. Never retain the event pointer.
class TypeLayFcitxQtControlV1 {
public:
    virtual ~TypeLayFcitxQtControlV1() = default;
    virtual bool filterControlKey(QObject *actual, QEvent *event) = 0;
};
#define TypeLayFcitxQtControlV1_iid "org.typelay.FcitxQtControl/1.0"
Q_DECLARE_INTERFACE(TypeLayFcitxQtControlV1, TypeLayFcitxQtControlV1_iid)

struct TypeLayFcitxQtOrderedGrantV1 {
    quint32 accept = 0, exit = 0;
    // Existing CLOCK_BOOTTIME deadlines, independently frozen before Ctrl.
    qint64 acceptUntil = 0, exitUntil = 0;
};
class TypeLayFcitxQtOrderedProviderV1 {
public:
    virtual ~TypeLayFcitxQtOrderedProviderV1() = default;
    // Zero marks a plain action. Every nonzero symbol must be installed.
    virtual bool supportsOrderedKeys(QObject *actual, quint32 accept, quint32 exit) const = 0;
    virtual void cancelOrdered(quint64 ticket) = 0;
    // Private, synchronous Decide-stack proof. Fresh inspect alone is not proof
    // of continuity; the original source authority must remain unchanged.
    virtual bool validateOrdered(QObject *actual, quint64 ticket,
        const TypeLayFcitxQtStateV1 *before, const TypeLayFcitxQtStateV1 *current) = 0;
    // Called immediately before the first possible mutation (including selection)
    // or exit dispatch. On success this physical Fn pair cannot be replayed.
    virtual bool claimOrdered(QObject *actual, quint64 ticket,
        const TypeLayFcitxQtStateV1 *before, const TypeLayFcitxQtStateV1 *current) = 0;
};
#define TypeLayFcitxQtOrderedProviderV1_iid "org.typelay.FcitxQtOrderedProvider/1.0"
Q_DECLARE_INTERFACE(TypeLayFcitxQtOrderedProviderV1, TypeLayFcitxQtOrderedProviderV1_iid)

// Additive editor route: the provider retains the claimed Ctrl/Fn group until
// both original scans are released, including across source focus return.
// This is routing only; the consumer freezes its current no-write editor token
// and a deadline no later than the original Ctrl admission + 150ms.
class TypeLayFcitxQtEditorOrderedProviderV1 {
public:
    virtual ~TypeLayFcitxQtEditorOrderedProviderV1() = default;
    virtual bool supportsEditorOrderedKeys(QObject *actual, quint32 accept, quint32 exit) const = 0;
};
#define TypeLayFcitxQtEditorOrderedProviderV1_iid "org.typelay.FcitxQtEditorOrderedProvider/1.0"
Q_DECLARE_INTERFACE(TypeLayFcitxQtEditorOrderedProviderV1, TypeLayFcitxQtEditorOrderedProviderV1_iid)

class TypeLayFcitxQtOrderedControlV1 {
public:
    virtual ~TypeLayFcitxQtOrderedControlV1() = default;
    // The provider reserves ticket before this call. Neither side retains event
    // pointers. True freezes an existing grant; it does not consume Ctrl.
    virtual bool beginOrdered(QObject *actual, quint64 ticket, const QEvent *ctrl,
        const TypeLayFcitxQtStateV1 *before, TypeLayFcitxQtOrderedGrantV1 *grant) = 0;
    virtual bool decideOrdered(QObject *actual, quint64 ticket, const QEvent *key,
        const TypeLayFcitxQtStateV1 *current) = 0;
    // Retirement must not call cancelOrdered back or release held scan ownership.
    virtual void retireOrdered(quint64 ticket) = 0;
};
#define TypeLayFcitxQtOrderedControlV1_iid "org.typelay.FcitxQtOrderedControl/1.0"
Q_DECLARE_INTERFACE(TypeLayFcitxQtOrderedControlV1, TypeLayFcitxQtOrderedControlV1_iid)
#endif
