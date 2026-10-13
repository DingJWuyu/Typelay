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

enum TypeLayFcitxQtFlagV1 : quint32 {
    TlQtKnown = 1u, TlQtFocused = 2u, TlQtPending = 4u, TlQtComposing = 8u
};
enum TypeLayFcitxQtCauseV1 : quint32 {
    TlQtInput = 1u, TlQtFocus = 2u, TlQtBinding = 4u, TlQtModifier = 8u
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
// The actual focused object holds a non-owning QObject* provider property.
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
#endif
