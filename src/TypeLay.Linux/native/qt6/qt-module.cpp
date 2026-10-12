// The platform IM owns input ordering; this additive plugin owns only native
// field access and presentation. Session/model/acceptance policy stays in Runtime.
#include "../fcitx5/typelay-fcitx-qt-v1.h"
#include <QApplication>
#include <QClipboard>
#include <QComboBox>
#include <QCryptographicHash>
#include <QFrame>
#include <QGenericPlugin>
#include <QInputMethodEvent>
#include <QJsonDocument>
#include <QJsonObject>
#include <QKeyEvent>
#include <QLabel>
#include <QLineEdit>
#include <QMimeData>
#include <QPlainTextEdit>
#include <QPointer>
#include <QPushButton>
#include <QScopedValueRollback>
#include <QSocketNotifier>
#include <QThread>
#include <QTimer>
#include <QUuid>
#include <QVariant>
#include <QVBoxLayout>
#include <QtEndian>
#include <cerrno>
#include <cstring>
#include <cstdio>
#include <limits>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <time.h>
#include <typeinfo>
#include <unistd.h>

namespace {
constexpr qsizetype MaxFrame = 256 * 1024;
constexpr qsizetype MaxText = 8192;
constexpr auto ProviderProperty = "typelay.fcitx.qt.provider.v1";
qint64 now() {
    timespec value{};
    return clock_gettime(CLOCK_BOOTTIME, &value) == 0
        ? qint64(value.tv_sec) * 1000 + value.tv_nsec / 1000000
        : std::numeric_limits<qint64>::max();
}
qint64 integer(const QJsonObject &object, const char *key, qint64 fallback = -1) {
    const auto value = object.value(QLatin1String(key));
    return value.isDouble() ? value.toInteger(fallback) : fallback;
}
QJsonObject rectangle(const QRect &rect) {
    return {{"x", rect.x()}, {"y", rect.y()}, {"width", rect.width()}, {"height", rect.height()}};
}
bool validText(const QString &text) {
    if (text.size() > MaxText || text.contains(QChar::Null)) return false;
    for (qsizetype index = 0; index < text.size(); ++index) {
        if (text[index].isHighSurrogate()) {
            if (++index >= text.size() || !text[index].isLowSurrogate()) return false;
        } else if (text[index].isLowSurrogate()) return false;
    }
    return true;
}
bool boundary(const QString &text, qint64 offset) {
    return offset >= 0 && offset <= text.size()
        && (offset == 0 || offset == text.size() || !text[qsizetype(offset)].isLowSurrogate());
}
QJsonObject receipt(const char *status, const char *reason) {
    return {{"status", QLatin1String(status)}, {"reason", QLatin1String(reason)}};
}

class TranslationEditor final : public QPlainTextEdit {
    Q_OBJECT
public:
    using QPlainTextEdit::QPlainTextEdit;
signals:
    void inputRejected();
protected:
    bool fits(const QString &insertion) {
        auto text = toPlainText();
        const auto cursor = textCursor();
        text.replace(cursor.selectionStart(), cursor.selectionEnd() - cursor.selectionStart(), insertion);
        if (validText(text)) return true;
        emit inputRejected();
        return false;
    }
    void insertFromMimeData(const QMimeData *mime) override {
        if (mime && fits(mime->text())) QPlainTextEdit::insertFromMimeData(mime);
    }
    void keyPressEvent(QKeyEvent *event) override {
        const auto text = event->text();
        const bool insertion = !text.isEmpty() && (text.front().isPrint() || text == "\n" || text == "\r" || text == "\t");
        if (insertion && !(event->modifiers() & (Qt::ControlModifier | Qt::MetaModifier)) && !fits(text)) {
            event->accept(); return;
        }
        QPlainTextEdit::keyPressEvent(event);
    }
    void inputMethodEvent(QInputMethodEvent *event) override {
        // Count the largest possible committed result; removing a replacement
        // range can only make it shorter. Never truncate an IM commit.
        if (!event->commitString().isEmpty() && !fits(event->commitString())) { event->accept(); return; }
        QPlainTextEdit::inputMethodEvent(event);
    }
};

struct Target {
    QPointer<QLineEdit> field;
    QPointer<QObject> provider;
    QString id = QUuid::createUuid().toString(QUuid::WithoutBraces);
    quint64 content = 1, input = 1, focus = 1, storage = 0;
    TypeLayFcitxQtStateV1 state{};
    bool observed = false, probing = false, mutating = false, quarantined = false;
};

class Native final : public QObject, public TypeLayFcitxQtControlV1 {
    Q_OBJECT
    Q_INTERFACES(TypeLayFcitxQtControlV1)
public:
    explicit Native(QObject *parent) : QObject(parent) {
        qApp->installEventFilter(this);
        connect(qApp, &QApplication::focusChanged, this, [this](QWidget *old, QWidget *current) {
            for (auto *widget : {old, current}) if (auto *target = discover(widget)) invalidate(target, false, true);
            syncOverlay();
        });
        connect(&reconnect_, &QTimer::timeout, this, &Native::connectPeer);
        reconnect_.start(1000);
        connect(&expiry_, &QTimer::timeout, this, [this] {
            if (card_ && (now() > overlayUntil_ || !overlayTarget_ || !overlayTarget_->field
                || !overlayTarget_->field->window()->isActiveWindow())) hideOverlay();
            refreshHint();
        });
        expiry_.start(25);
        discover(QApplication::focusWidget());
        connectPeer();
    }
    ~Native() override {
        if (fd_ >= 0) ::close(fd_);
        qDeleteAll(targets_);
        qDeleteAll(retired_);
    }

    bool filterControlKey(QObject *actualField, QEvent *inputEvent) override {
        if (!inputEvent || inputEvent->type() != QEvent::KeyPress) return false;
        const auto *event = static_cast<QKeyEvent *>(inputEvent);
        traceGate("hook-enter", targets_.value(actualField, nullptr), publication_.value("authority").toObject(),
            integer(publication_.value("permit").toObject(), "expiresAtMs", 0));
        if (handlingKey_ || event->isAutoRepeat() || !onThread()) {
            traceGate("hook-busy-repeat-thread", targets_.value(actualField, nullptr), {}, 0); return false;
        }
        const auto shortcut = shortcutKind(event);
        if (shortcut.isEmpty() && (event->modifiers() != Qt::NoModifier
            || (event->key() != Qt::Key_Tab && event->key() != Qt::Key_Escape))) return false;
        QScopedValueRollback<bool> handling(handlingKey_, true);
        if (!shortcut.isEmpty()) return triggerShortcut(shortcut, discover(qobject_cast<QWidget *>(actualField)),
            shortcutAuthority_.value("authority").toObject());
        if (actualField == editor_ && editor_ && editor_->hasFocus() && overlayActive()) {
            if (event->key() != Qt::Key_Tab && event->key() != Qt::Key_Escape) return false;
            const QPointer<QPlainTextEdit> editor = editor_;
            auto *source = overlayTarget_;
            TypeLayFcitxQtStateV1 state{};
            if (!widgetClear(editor, &state) || !editor || editor_ != editor || source != overlayTarget_
                || !overlayActive() || !editor->hasFocus() || composing_) return false;
            if (event->key() == Qt::Key_Tab && !editor_->isReadOnly()) {
                if (!validText(editor->toPlainText())) {
                    if (hint_) hint_->setText(QStringLiteral("译文超过 8192 字符限制，请缩短后再返回"));
                    return false;
                }
                clearPermit();
                ui("return"); // Runtime authorizes return, then issues a fresh source permit.
                return true;
            }
            if (event->key() == Qt::Key_Escape) { clearPermit(); ui("escape"); return true; }
            return false;
        }
        auto *target = discover(qobject_cast<QWidget *>(actualField));
        if (!target || target != focused() || publication_.isEmpty()) {
            traceGate("source-unavailable", target, publication_.value("authority").toObject(), 0); return false;
        }
        if (!inspect(target)) {
            traceGate("source-inspect", target, publication_.value("authority").toObject(), 0); return false;
        }
        const auto current = publication_;
        const auto basis = current.value("authority").toObject();
        if (!authorityMatches(target, basis, true) || !clear(target->state)) {
            traceGate("source-authority", target, basis, integer(current.value("permit").toObject(), "expiresAtMs", 0));
            clearPermit(); return false;
        }
        if (event->key() == Qt::Key_Escape) {
            const auto lease = current.value("lease").toObject();
            const qint64 age = now() - integer(lease.value("source").toObject(), "observedAtMs");
            clearPermit();
            if (lease.value("escapeAllowed").toBool() && age >= 0 && age <= 150) {
                sendEvent("escape", {}); return true;
            }
            return false;
        }
        if (event->key() != Qt::Key_Tab || !ready(target, current)) {
            traceGate("source-permit", target, basis, integer(current.value("permit").toObject(), "expiresAtMs", 0));
            clearPermit(); return false;
        }
        const auto permit = current.value("permit").toObject();
        const auto plan = permit.value("plan").toObject();
        const auto id = plan.value("id").toString();
        consumed_.append(id);
        while (consumed_.size() > 128) consumed_.removeFirst();
        const qint64 accepted = now();
        clearPermit();
        const auto result = write(target, {{"plan", plan}, {"authority", basis},
            {"expiresAtMs", permit.value("expiresAtMs")}}, false);
        if (result.value("status").toString() == "RejectedBeforeMutation") {
            traceGate("source-write-rejected", target, basis, integer(permit, "expiresAtMs", 0)); return false;
        }
        traceGate("source-consumed", target, basis, integer(permit, "expiresAtMs", 0));
        sendEvent("accepted", {{"planId", id}, {"acceptedAtMs", accepted}, {"receipt", result}});
        return true;
    }

private slots:
    void providerChanged(QObject *field, uint causes) {
        auto *target = targets_.value(field, nullptr);
        clearPermit();
        if (!target || sender() != target->provider) return;
        if (causes & 5u) invalidate(target, false, false);
        if (causes & 2u) invalidate(target, false, true);
    }

private:
    bool onThread() const { return QThread::currentThread() == thread(); }
    void traceGate(const char *phase, Target *target, const QJsonObject &basis, qint64 until,
                   const char *reason = "none") const {
        static unsigned traceLines = 0;
        if (!qEnvironmentVariableIsSet("TYPELAY_TRACE") || traceLines >= 128) return;
        ++traceLines;
        const auto expected = basis.value("contextStamp").toObject();
        const bool field = target && target->field;
        const bool observed = target && target->observed;
        // Fixed phases/reasons and numeric/boolean guards only. Never print
        // field body, labels, metadata hashes, plan strings or target IDs.
        std::fprintf(stderr, "TypeLay Qt6 phase=%s reason=%s remainingMs=%lld field=%d provider=%d focused=%d "
            "published=%d mutating=%d probing=%d flags=%u contentSame=%d inputSame=%d focusSame=%d "
            "contextSame=%d fieldSame=%d epochSame=%d imeInputSame=%d serverSame=%d appliedSame=%d\n",
            phase, reason, static_cast<long long>(until - now()), int(field), int(target && target->provider),
            int(field && QApplication::focusWidget() == target->field && QGuiApplication::focusObject() == target->field),
            int(!publication_.isEmpty()), int(target && target->mutating), int(target && target->probing),
            observed ? target->state.flags : 0u,
            int(target && integer(basis, "contentRevision") == qint64(target->content)),
            int(target && integer(basis, "inputRevision") == qint64(target->input)),
            int(target && integer(basis, "focusRevision") == qint64(target->focus)),
            int(observed && integer(expected, "context") == qint64(target->state.contextGeneration)),
            int(observed && integer(expected, "field") == qint64(target->state.fieldGeneration)),
            int(observed && integer(expected, "epoch") == qint64(target->state.epoch)),
            int(observed && integer(expected, "input") == qint64(target->state.inputEpoch)),
            int(observed && integer(expected, "server") == qint64(target->state.serverSerial)),
            int(observed && integer(expected, "applied") == qint64(target->state.appliedSerial)));
    }
    bool insideOverlay(QObject *object) const {
        for (auto *parent = object; parent; parent = parent->parent()) if (parent == card_) return true;
        return false;
    }
    Target *discover(QWidget *widget) {
        if (!widget || widget->metaObject() != &QLineEdit::staticMetaObject
            || typeid(*widget) != typeid(QLineEdit) || insideOverlay(widget)) return nullptr;
        if (auto *existing = targets_.value(widget, nullptr)) return existing;
        auto *target = new Target;
        target->field = static_cast<QLineEdit *>(widget);
        targets_.insert(widget, target);
        connect(target->field, &QLineEdit::textChanged, this, [this, target] {
            ++target->storage;
            ++target->content;
            if (!target->mutating) invalidate(target, true, false);
        });
        connect(target->field, &QLineEdit::selectionChanged, this, [this, target] {
            if (!target->mutating) invalidate(target, false, false);
        });
        connect(target->field, &QLineEdit::cursorPositionChanged, this, [this, target] {
            if (!target->mutating) invalidate(target, false, false);
        });
        connect(widget, &QObject::destroyed, this, [this, target, widget] {
            target->field = nullptr;
            if (overlayTarget_ == target) hideOverlay();
            clearPermit();
            sendEvent("destroy", {{"target", target->id}});
            targets_.remove(widget);
            // A provider call can dispatch destruction. Keep the record until
            // the plugin is destroyed; QPointer has already revoked field access.
            // Nested provider event loops may run deleteLater/zero timers.
            retired_.append(target);
        });
        bind(target);
        return target;
    }
    Target *focused() {
        auto *widget = QApplication::focusWidget();
        return widget && widget->window()->isActiveWindow() && QGuiApplication::focusObject() == widget
            ? discover(widget) : nullptr;
    }
    bool supported(Target *target) const {
        return target && target->field && target->field->metaObject() == &QLineEdit::staticMetaObject && typeid(*target->field) == typeid(QLineEdit)
            && target->field->isVisible() && target->field->window()->windowHandle();
    }
    QJsonObject metadata(Target *target) const {
        if (!supported(target)) return {};
        auto *field = target->field.data();
        const auto hints = field->inputMethodHints();
        const bool password = field->echoMode() != QLineEdit::Normal
            || (hints & (Qt::ImhHiddenText | Qt::ImhSensitiveData)) != 0;
        const bool constrained = (hints & (Qt::ImhExclusiveInputMask | Qt::ImhDate | Qt::ImhTime)) != 0;
        return {{"process", ""}, {"label", field->accessibleName()},
            {"automationId", field->objectName()},
            {"help", (field->accessibleDescription() + ' ' + field->toolTip() + ' ' + field->placeholderText())},
            {"aria", "QLineEdit"}, {"password", password}, {"enabled", field->isEnabled()},
            {"readOnly", field->isReadOnly() || field->validator() || !field->inputMask().isEmpty() || constrained},
            {"offscreen", !field->isVisible()}, {"inDocument", false}, {"multiline", false},
            {"hasValue", true}, {"hasTextEdit", bool(target->provider)},
            {"maximumLength", field->maxLength()}, {"inputMethodHints", qint64(hints)}};
    }
    QString metadataKey(Target *target) const {
        return QString::fromLatin1(QCryptographicHash::hash(QJsonDocument(metadata(target)).toJson(QJsonDocument::Compact),
            QCryptographicHash::Sha256).toHex());
    }
    bool metadataMatches(Target *target, const QJsonObject &basis) const {
        if (!supported(target) || basis.value("target").toString() != target->id
            || basis.value("metadataKey").toString() != metadataKey(target)) return false;
        const auto fields = metadata(target);
        return !fields.value("password").toBool() && !fields.value("readOnly").toBool()
            && fields.value("enabled").toBool() && !fields.value("offscreen").toBool();
    }
    QJsonObject geometry(Target *target) const {
        if (!supported(target)) return {};
        auto *field = target->field.data();
        auto *top = field->window();
        QRect bounds(field->mapTo(top, QPoint()), field->size());
        for (auto *parent = field->parentWidget(); parent; parent = parent->parentWidget()) {
            if (!parent->isVisible()) return {};
            bounds = bounds.intersected(QRect(parent->mapTo(top, QPoint()), parent->size()));
            if (parent == top) break;
        }
        if (bounds.isEmpty()) return {};
        return {{"space", "qt-toplevel"}, {"bounds", rectangle(bounds)}, {"frameWidth", top->width()},
            {"frameHeight", top->height()}, {"scale", top->devicePixelRatioF()}};
    }
    TypeLayFcitxQtV1 *bind(Target *target) {
        if (!target || !target->field) return nullptr;
        auto *owner = target->field->property(ProviderProperty).value<QObject *>();
        auto *capability = owner ? qobject_cast<TypeLayFcitxQtV1 *>(owner) : nullptr;
        if (!capability || owner->thread() != thread()) owner = nullptr;
        // The provider publishes only on its current focus object. Keep its
        // weak identity while the source lends focus to our editor; absence is
        // never Clear, but is not itself a source-text edit.
        if (!owner) { clearPermit(); return nullptr; }
        if (target->provider != owner) {
            target->provider = owner;
            target->observed = false;
            invalidate(target, false, false);
        }
        if (owner && !providers_.contains(owner)) {
            providers_.insert(owner);
            connect(owner, SIGNAL(typelayStateChangedV1(QObject*,uint)), this, SLOT(providerChanged(QObject*,uint)), Qt::DirectConnection);
            connect(owner, &QObject::destroyed, this, [this, owner] { providers_.remove(owner); clearPermit(); });

        }
        return owner ? capability : nullptr;
    }
    static bool clear(const TypeLayFcitxQtStateV1 &state) {
        return state.flags == 3u && state.serverSerial == state.appliedSerial
            && state.contextGeneration != 0 && state.fieldGeneration != 0;
    }
    QJsonObject stamp(const TypeLayFcitxQtStateV1 &state) const {
        return {{"context", qint64(state.contextGeneration)}, {"field", qint64(state.fieldGeneration)},
            {"epoch", qint64(state.epoch)}, {"input", qint64(state.inputEpoch)},
            {"server", qint64(state.serverSerial)}, {"applied", qint64(state.appliedSerial)},
            {"flags", qint64(state.flags)}};
    }
    bool inspect(Target *target) {
        if (!onThread() || !target || !target->field || target->probing || target->mutating) return false;
        auto *capability = bind(target);
        if (!capability) return false;
        QPointer<QObject> owner = target->provider;
        QPointer<QLineEdit> field = target->field;
        TypeLayFcitxQtStateV1 state{};
        QScopedValueRollback<bool> probing(target->probing, true);
        const bool valid = capability->inspect(field, &state) && field && owner && target->provider == owner
            && field->property(ProviderProperty).value<QObject *>() == owner
            && state.size == sizeof state && state.version == 1u && (state.flags & ~15u) == 0;
        if (!valid) { clearPermit(); return false; }
        if (target->observed && stamp(target->state) != stamp(state)) {
            clearPermit();
            if (target->state.contextGeneration != state.contextGeneration
                || target->state.fieldGeneration != state.fieldGeneration
                || target->state.inputEpoch != state.inputEpoch) invalidate(target, false, false);
        }
        target->state = state;
        target->observed = true;
        return true;
    }
    bool local(Target *target, const TypeLayFcitxQtStateV1 &state) const {
        if (!target || !target->field || !target->provider || target->probing || !onThread()
            || target->field->property(ProviderProperty).value<QObject *>() != target->provider) return false;
        auto *capability = qobject_cast<TypeLayFcitxQtV1 *>(target->provider);
        return capability && capability->validateLocal(target->field, &state);
    }
    bool widgetClear(QWidget *widget, TypeLayFcitxQtStateV1 *state) {
        if (!widget || QGuiApplication::focusObject() != widget) return false;
        QPointer<QWidget> field = widget;
        QPointer<QObject> owner = widget->property(ProviderProperty).value<QObject *>();
        auto *capability = owner ? qobject_cast<TypeLayFcitxQtV1 *>(owner) : nullptr;
        return capability && capability->inspect(widget, state) && owner && field
            && field->property(ProviderProperty).value<QObject *>() == owner && clear(*state)
            && capability->validateLocal(widget, state);
    }
    QJsonObject authority(Target *target) const {
        QJsonObject result{{"target", target->id}, {"metadataKey", metadataKey(target)},
            {"contentRevision", qint64(target->content)}, {"inputRevision", qint64(target->input)},
            {"focusRevision", qint64(target->focus)}, {"observedAtMs", now()}};
        if (target->observed) result.insert("contextStamp", stamp(target->state));
        return result;
    }
    bool authorityMatches(Target *target, const QJsonObject &basis, bool requireFocus) {
        return metadataMatches(target, basis) && integer(basis, "contentRevision") == qint64(target->content)
            && integer(basis, "inputRevision") == qint64(target->input)
            && (!requireFocus || (focused() == target && integer(basis, "focusRevision") == qint64(target->focus)))
            && target->observed && basis.value("contextStamp").toObject() == stamp(target->state);
    }
    QJsonObject describe(Target *target) {
        if (!target || target->mutating || target->probing) return {};
        inspect(target);
        if (!supported(target)) return {};
        return {{"target", target->id}, {"metadata", metadata(target)}, {"authority", authority(target)},
            {"geometry", geometry(target)}, {"focused", target == focused()}, {"publication", publicationState(target)}};
    }
    QJsonObject selection(Target *target) const {
        auto *field = target->field.data();
        const int start = field->selectionStart() < 0 ? field->cursorPosition() : field->selectionStart();
        return {{"start", start}, {"end", start + field->selectionLength()}};
    }
    QJsonObject capture(Target *target, const QJsonObject &basis) {
        const qint64 age = now() - integer(basis, "observedAtMs");
        if (age < 0 || age > 150 || !inspect(target) || !clear(target->state)
            || !authorityMatches(target, basis, true) || !local(target, target->state)) return {};
        const auto text = target->field->text();
        if (!validText(text)) return {};
        auto result = describe(target);
        if (result.isEmpty() || !authorityMatches(target, basis, true) || !local(target, target->state)
            || now() - integer(basis, "observedAtMs") > 150) return {};
        result.insert("text", text);
        result.insert("selection", selection(target));
        result.insert("ime", "Clear");
        result.insert("writeSupported", !target->quarantined);
        result.insert("observedAtMs", now());
        return result;
    }
    void invalidate(Target *target, bool textChanged, bool focusChanged) {
        if (!target || !target->field) return;
        shortcutAuthority_ = {};
        if (focusChanged) ++target->focus;
        else ++target->input;
        clearPermit();
        if (target->mutating) return;
        sendEvent("input", {{"target", target->id}, {"textChanged", textChanged}, {"focusChanged", focusChanged},
            {"focused", QApplication::focusWidget() == target->field},
            {"inputRevision", qint64(target->input)}, {"contentRevision", qint64(target->content)},
            {"focusRevision", qint64(target->focus)}});
    }
    bool ready(Target *target, const QJsonObject &publication) {
        if (!target || target->quarantined || target->mutating || publication.isEmpty()
            || !clear(target->state) || !authorityMatches(target, publication.value("authority").toObject(), true)) return false;
        const auto permit = publication.value("permit").toObject();
        const auto plan = permit.value("plan").toObject();
        const auto identity = plan.value("expected").toObject().value("target").toObject();
        const auto id = plan.value("id").toString();
        const qint64 until = integer(permit, "expiresAtMs", 0), current = now();
        return !id.isEmpty() && id.size() <= 256 && !consumed_.contains(id)
            && publication.value("target").toString() == target->id
            && identity.value("backend").toString() == "linux-qt6" && identity.value("value").toString() == target->id
            && identity.value("lifetime").toString() == lifetime_ && until > current && until - current <= 100;
    }
    QJsonObject publicationState(Target *target) {
        const bool allowed = ready(target, publication_);
        const auto permit = publication_.value("permit").toObject();
        return {{"ready", allowed}, {"planId", allowed ? permit.value("plan").toObject().value("id") : QJsonValue()},
            {"readyUntilHostMs", allowed ? permit.value("expiresAtMs") : QJsonValue()}};
    }
    void clearPermit() { publication_ = {}; refreshHint(); }
    QString shortcutKind(const QKeyEvent *event) const {
        if (!shortcutConfiguration_.value("enabled").toBool() || shortcutAuthority_.isEmpty()) return {};
        const auto modifiers = event->modifiers();
        if (modifiers & ~(Qt::ShiftModifier | Qt::ControlModifier | Qt::AltModifier)) return {};
        const int mask = ((modifiers & Qt::ShiftModifier) ? 1 : 0)
            | ((modifiers & Qt::ControlModifier) ? 4 : 0) | ((modifiers & Qt::AltModifier) ? 8 : 0);
        // Linux GDK bindings use XKB keysyms. Qt function-key values are a
        // separate enum; translate them instead of conflating the two spaces.
        const int key = event->key();
        const quint32 symbol = key >= Qt::Key_F1 && key <= Qt::Key_F35
            ? quint32(0xffbe + key - Qt::Key_F1)
            : key >= 0x20 && key <= 0x7e ? quint32(key) : event->nativeVirtualKey();
        for (const auto *kind : {"wake", "direct"}) {
            const auto binding = shortcutConfiguration_.value(QLatin1String(kind)).toObject();
            const auto configured = integer(binding, "keyval");
            const auto normalized = configured >= 'a' && configured <= 'z' ? configured - 'a' + 'A' : configured;
            if (symbol && normalized == symbol && integer(binding, "modifiers") == mask) return QLatin1String(kind);
        }
        return {};
    }
    bool triggerShortcut(const QString &kind, Target *target, const QJsonObject &basis) {
        const qint64 age = now() - integer(basis, "observedAtMs");
        if ((kind != "wake" && kind != "direct") || !shortcutConfiguration_.value("enabled").toBool()
            || shortcutAuthority_.isEmpty() || !target || target != focused() || age < 0 || age > 150
            || shortcutAuthority_.value("target").toString() != target->id
            || shortcutAuthority_.value("generation") != shortcutConfiguration_.value("generation")
            || shortcutAuthority_.value("authority").toObject() != basis
            || !inspect(target) || !clear(target->state) || !authorityMatches(target, basis, true)) return false;
        const auto description = describe(target);
        const auto read = capture(target, description.value("authority").toObject());
        if (read.isEmpty()) return false;
        const auto generation = shortcutConfiguration_.value("generation");
        clearPermit(); shortcutAuthority_ = {};
        sendEvent("shortcut", {{"kind", kind}, {"generation", generation}, {"native", read}, {"acceptedAtMs", now()}});
        return fd_ >= 0;
    }
    QJsonObject write(Target *target, const QJsonObject &request, bool direct) {
        const auto plan = request.value("plan").toObject();
        const auto expected = plan.value("expected").toObject();
        const auto basis = request.value("authority").toObject();
        const auto approved = direct ? request.value("approved").toObject() : basis;
        const auto identity = expected.value("target").toObject();
        auto reject = [&](const char *phase, const char *reason) {
            traceGate(phase, target, basis, integer(request, "expiresAtMs", 0), reason);
            return receipt("RejectedBeforeMutation", reason);
        };
        traceGate("write-enter", target, basis, integer(request, "expiresAtMs", 0));
        if (!target || plan.isEmpty() || expected.isEmpty() || basis.isEmpty() || approved.isEmpty()
            || identity.value("backend").toString() != "linux-qt6" || identity.value("value").toString() != target->id
            || identity.value("lifetime").toString() != lifetime_)
            return reject("write-header", "BadPlan");
        if (target->mutating || target->quarantined || !inspect(target) || !clear(target->state))
            return reject("write-inspect", "ImeUnclear");
        const qint64 until = integer(request, "expiresAtMs", 0), current = now();
        if (until <= current || until - current > (direct ? 250 : 100))
            return reject("write-deadline", "StaleObservation");
        if (!metadataMatches(target, approved) || !authorityMatches(target, basis, true))
            return reject("write-authority", "EpochChanged");
        QPointer<QLineEdit> field = target->field;
        QPointer<QObject> provider = target->provider;
        const auto text = field->text();
        const auto selected = selection(target);
        if (!expected.value("text").isString() || text != expected.value("text").toString())
            return reject("write-source", "SourceChanged");
        if (selected != expected.value("selection").toObject())
            return reject("write-selection", "SelectionChanged");
        const auto replacement = plan.value("replacement").toString();
        const auto result = plan.value("resultText").toString();
        const auto scope = plan.value("scope").toObject();
        const qint64 from = scope.isEmpty() ? 0 : integer(scope, "start");
        const qint64 to = scope.isEmpty() ? text.size() : integer(scope, "end");
        if (!validText(text) || !validText(replacement) || !validText(result) || replacement.isEmpty()
            || replacement.contains('\n') || replacement.contains('\r')
            || !boundary(text, from) || !boundary(text, to) || to < from || (!scope.isEmpty() && to == from)
            || integer(plan, "caret") != from + replacement.size() || result.size() > field->maxLength()
            || result != text.left(qsizetype(from)) + replacement + text.mid(qsizetype(to)))
            return reject("write-text", "InvalidText");
        const auto initialState = target->state;
        const auto connection = connectionGeneration_;
        const auto authorization = authorizationGeneration_;
        const auto input = target->input, focus = target->focus, storage = target->storage;
        const auto content = target->content;
        auto unchanged = [&] {
            return field && provider && fd_ >= 0 && connectionGeneration_ == connection
                && authorizationGeneration_ == authorization && target->field == field && target->provider == provider
                && focused() == target && target->input == input && target->focus == focus
                && target->storage == storage && target->content == content
                && metadataMatches(target, basis) && metadataMatches(target, approved)
                && field->text() == text;
        };
        auto *capability = qobject_cast<TypeLayFcitxQtV1 *>(provider);
        if (!capability || !local(target, initialState))
            return reject("write-local-before-prepare", "ImeUnclear");
        // Prepare against the real provider while the original body is intact,
        // then inspect again. Standard Clear QLineEdit selection has no pending
        // preedit to commit; any unexpected reset/output still invalidates proof.
        {
            QScopedValueRollback<bool> probing(target->probing, true);
            if (!capability->prepareCaret(field, &initialState))
                return reject("write-prepare", "ImeUnclear");
        }
        if (!unchanged() || !inspect(target) || !clear(target->state)
            || target->state.contextGeneration != initialState.contextGeneration
            || target->state.fieldGeneration != initialState.fieldGeneration
            || target->state.inputEpoch != initialState.inputEpoch || !unchanged())
            return reject("write-after-prepare", "EpochChanged");
        const auto baseline = target->state;
        QScopedValueRollback<bool> mutation(target->mutating, true);
        field->setSelection(int(from), int(to - from));
        // Selection signals are application code. Recheck every source/input/IM
        // condition after them and immediately before the first body mutation.
        const auto preparedSelection = QJsonObject{{"start", from}, {"end", to}};
        if (!unchanged() || selection(target) != preparedSelection || !local(target, baseline) || now() >= until)
            return reject("write-after-selection", "EpochChanged");
        field->insert(replacement); // Native one-edit replacement and native undo.
        const bool verified = field && provider && target->field == field && target->provider == provider
            && focused() == target && target->input == input && target->focus == focus
            && metadataMatches(target, basis) && metadataMatches(target, approved)
            && target->storage == storage + 1 && target->content == content + 1
            && field->text() == result && field->cursorPosition() == integer(plan, "caret")
            && field->selectionLength() == 0 && local(target, baseline);
        ++target->input;
        clearPermit();
        if (!verified) {
            target->quarantined = true;
            traceGate("write-readback", target, basis, until, "UnknownOutcome");
            return receipt("OutcomeUnknown", "UnknownOutcome");
        }
        auto verifiedSnapshot = expected;
        verifiedSnapshot.insert("text", result);
        verifiedSnapshot.insert("selection", QJsonObject{{"start", integer(plan, "caret")}, {"end", integer(plan, "caret")}});
        verifiedSnapshot.insert("observedAtMs", now());
        verifiedSnapshot.insert("ime", "Clear");
        verifiedSnapshot.insert("access", "Supported");
        auto outcome = receipt("Verified", "None");
        outcome.insert("verifiedSnapshot", verifiedSnapshot);
        traceGate("write-verified", target, basis, until, "None");
        return outcome;
    }

    void ui(const char *operation, const QJsonValue &value = QJsonValue()) {
        if (overlayTarget_) sendEvent("ui", {{"target", overlayTarget_->id}, {"operation", QLatin1String(operation)},
            {"revision", overlayRevision_}, {"value", value}});
    }
    bool overlayActive() const {
        return card_ && card_->isVisible() && overlayTarget_ && overlayTarget_->field
            && overlayTarget_->field->window()->isActiveWindow() && now() <= overlayUntil_;
    }
    void syncOverlay() {
        const bool focusedEditor = overlayActive() && insideOverlay(QApplication::focusWidget());
        if (focusedEditor != overlayFocused_) { overlayFocused_ = focusedEditor; ui("focus", focusedEditor); }
        auto *source = focused();
        if (card_ && source && source != overlayTarget_) hideOverlay();
    }
    void hideOverlay() {
        if (!card_) return;
        if (overlayFocused_) ui("focus", false);
        if (composing_) ui("composition", false);
        ui("visible", false);
        auto *card = card_.data();
        card_ = nullptr; editor_ = nullptr; hint_ = nullptr; status_ = nullptr;
        language_ = nullptr; footer_ = nullptr; overlayTarget_ = nullptr;
        overlayView_ = {}; overlayFocused_ = false; composing_ = false; pendingEdit_ = false;
        card->hide();
        card->deleteLater();
    }
    void refreshHint() {
        if (!hint_) return;
        const auto presentation = overlayView_.value("presentation").toObject();
        auto hint = presentation.value("tabHint").toString();
        if (!presentation.value("editorOwnsText").toBool()
            && (hint == QStringLiteral("Tab 接受译文") || hint == QStringLiteral("Tab 交还应用")))
            hint = ready(overlayTarget_, publication_) ? QStringLiteral("Tab 接受译文") : QStringLiteral("Tab 交还应用");
        hint_->setText(hint);
    }
    void createOverlay(Target *target) {
        auto *top = target->field->window();
        auto *frame = new QFrame(top); // Child surface, never a new toplevel/input grab.
        frame->setObjectName(QStringLiteral("typelay-overlay"));
        frame->setFrameShape(QFrame::StyledPanel);
        frame->setAutoFillBackground(true);
        frame->setFocusPolicy(Qt::NoFocus);
        card_ = frame;
        overlayTarget_ = target;
        auto *layout = new QVBoxLayout(frame);
        layout->setContentsMargins(12, 8, 12, 8);
        auto *header = new QHBoxLayout;
        language_ = new QLabel(frame);
        status_ = new QLabel(frame);
        header->addWidget(language_); header->addStretch(); header->addWidget(status_);
        layout->addLayout(header);
        auto *translationEditor = new TranslationEditor(frame);
        editor_ = translationEditor;
        connect(translationEditor, &TranslationEditor::inputRejected, this, [this] {
            if (hint_) hint_->setText(QStringLiteral("译文超过 8192 字符限制，此次插入未生效"));
        });
        editor_->setObjectName(QStringLiteral("typelay-editor"));
        editor_->setReadOnly(true);
        editor_->setFocusPolicy(Qt::NoFocus);
        editor_->setTabChangesFocus(false);
        editor_->setMaximumBlockCount(0);
        layout->addWidget(editor_);
        connect(editor_, &QPlainTextEdit::textChanged, this, [this] {
            if (settingText_ || !editor_ || editor_->isReadOnly()) return;
            const auto value = editor_->toPlainText();
            if (validText(value)) ui("draft", value);
            else if (hint_) hint_->setText(QStringLiteral("译文超过 8192 字符限制，请缩短后再返回"));
        });
        footer_ = new QWidget(frame);
        auto *footerLayout = new QHBoxLayout(footer_);
        footerLayout->setContentsMargins(0, 0, 0, 0);
        hint_ = new QLabel(footer_);
        footerLayout->addWidget(hint_); footerLayout->addStretch();
        auto button = [&](const QString &label, const char *operation) {
            auto *value = new QPushButton(label, footer_);
            value->setFocusPolicy(Qt::NoFocus);
            connect(value, &QPushButton::clicked, this, [this, operation] {
                if (qstrcmp(operation, "copy") == 0) {
                    if (editor_) QApplication::clipboard()->setText(editor_->toPlainText());
                } else if (qstrcmp(operation, "expand") == 0) {
                    expanded_ = !expanded_; ui("expand", expanded_);
                } else ui(operation);
            });
            footerLayout->addWidget(value);
        };
        button(QStringLiteral("复制"), "copy");
        button(QStringLiteral("重译"), "regenerate");
        button(QStringLiteral("展开"), "expand");
        button(QStringLiteral("Esc 隐藏"), "escape");
        layout->addWidget(footer_);
    }
    QJsonObject overlayCommand(const QString &op, Target *target, const QJsonObject &data) {
        if (op == "overlay-hide") hideOverlay();
        else if (op == "overlay-render") {
            const auto view = data.value("view").toObject();
            const auto presentation = view.value("presentation").toObject();
            const auto basis = data.value("authority").toObject();
            const qint64 until = integer(data, "expiresAtMs", 0), current = now();
            const bool retainEditor = target && target == overlayTarget_
                && presentation.value("editorOwnsText").toBool() && target->field
                && integer(basis, "inputRevision") == qint64(target->input)
                && integer(basis, "contentRevision") == qint64(target->content);
            const bool valid = metadataMatches(target, basis) && presentation.value("visible").toBool()
                && until >= current && until - current <= 1000
                && (retainEditor || (inspect(target) && clear(target->state)))
                && (focused() == target || (target == overlayTarget_ && overlayFocused_))
                && target->field->window()->isActiveWindow() && !geometry(target).isEmpty();
            if (!valid) hideOverlay();
            else {
                if (!card_ || overlayTarget_ != target) { hideOverlay(); createOverlay(target); }
                overlayView_ = view; overlayUntil_ = until; overlayRevision_ = integer(data, "revision", 0);
                const bool editing = presentation.value("editorOwnsText").toBool();
                {
                    QScopedValueRollback<bool> setting(settingText_, true);
                    if (editor_->toPlainText() != view.value("text").toString()) editor_->setPlainText(view.value("text").toString());
                }
                editor_->setReadOnly(!editing);
                editor_->setFocusPolicy(editing ? Qt::StrongFocus : Qt::NoFocus);
                language_->setText(view.value("sourceLanguage").toString() + QStringLiteral(" → ") + view.value("targetLanguage").toString());
                status_->setText(view.value("status").toString());
                refreshHint();
                const bool capsule = view.value("state").toString() == "IdleCapsule";
                editor_->setVisible(!capsule); footer_->setVisible(!capsule); status_->setVisible(!capsule);
                auto *top = target->field->window();
                const int width = qMin(capsule ? 160 : qBound(390, target->field->width(), 520), qMax(1, top->width() - 16));
                editor_->setFixedHeight(expanded_ ? 280 : 100);
                card_->setFixedWidth(width); card_->adjustSize();
                const auto bounds = geometry(target).value("bounds").toObject();
                const int x = qBound(0, int(integer(bounds, "x") + integer(bounds, "width")) - width, qMax(0, top->width() - width));
                const int above = int(integer(bounds, "y")) - card_->height() - 6;
                const int y = above >= 0 ? above : int(integer(bounds, "y") + integer(bounds, "height")) + 6;
                card_->move(x, qBound(0, y, qMax(0, top->height() - card_->height())));
                const bool dark = data.value("theme").toString() == "Dark";
                card_->setStyleSheet(dark
                    ? QStringLiteral("QFrame#typelay-overlay { background:#20242c; color:#e8ebf2; border:1px solid #596170; border-radius:12px; }")
                    : QStringLiteral("QFrame#typelay-overlay { background:#ffffff; color:#171a21; border:1px solid #dfe3eb; border-radius:12px; }"));
                card_->show(); card_->raise(); syncOverlay();
            }
        } else if (op == "overlay-renew") {
            const qint64 until = integer(data, "expiresAtMs", 0), current = now();
            if (target == overlayTarget_ && overlayActive() && until >= current && until - current <= 1000
                && metadataMatches(target, data.value("authority").toObject()) && !geometry(target).isEmpty()) overlayUntil_ = until;
            else hideOverlay();
        } else if (op == "overlay-edit") {
            if (target == overlayTarget_ && overlayActive() && pendingEdit_ && data.value("allowed").toBool()
                && overlayView_.value("state").toString() == "EditingTranslation" && !editor_->isReadOnly()) editor_->setFocus();
            pendingEdit_ = false;
            syncOverlay();
        } else if (op == "overlay-resume" && target == overlayTarget_ && overlayActive()) {
            editor_->setReadOnly(false); editor_->setFocusPolicy(Qt::StrongFocus); editor_->setFocus(); syncOverlay();
        }
        return {{"visible", overlayActive()}, {"editing", editor_ && !editor_->isReadOnly()}};
    }
    bool eventFilter(QObject *object, QEvent *event) override {
        if (!onThread()) return false;
        if (editor_ && (object == editor_ || object == editor_->viewport())) {
            if (event->type() == QEvent::MouseButtonPress && editor_->isReadOnly() && overlayActive()) {
                if (overlayView_.value("editable").toBool() && !pendingEdit_
                    && overlayTarget_ && inspect(overlayTarget_) && clear(overlayTarget_->state)) {
                    pendingEdit_ = true; ui("beginEdit"); return true;
                }
            }
            if (event->type() == QEvent::InputMethod) {
                const bool composing = !static_cast<QInputMethodEvent *>(event)->preeditString().isEmpty();
                if (composing != composing_) { composing_ = composing; ui("composition", composing); }
            }
        }
        if (insideOverlay(object)) return false;
        auto *target = discover(qobject_cast<QWidget *>(object));
        if (!target) return false;
        const auto type = event->type();
        if (type == QEvent::InputMethod) {
            const auto *input = static_cast<QInputMethodEvent *>(event);
            // An empty focus/reset notification changes no body or selection.
            // It still revokes a permit; only the real provider can establish
            // Clear, and its composition/input epoch changes remain authoritative.
            const bool empty = input->commitString().isEmpty() && input->preeditString().isEmpty()
                && input->replacementStart() == 0 && input->replacementLength() == 0
                && input->attributes().isEmpty();
            if (empty) clearPermit();
            else invalidate(target, false, false);
        } else if (type == QEvent::KeyPress || type == QEvent::MouseButtonPress || type == QEvent::Wheel
            || type == QEvent::TouchBegin) invalidate(target, false, false);
        if (type == QEvent::DynamicPropertyChange) bind(target);
        if (type == QEvent::Hide || type == QEvent::EnabledChange || type == QEvent::ReadOnlyChange) {
            invalidate(target, false, false);
            if (target == overlayTarget_) hideOverlay();
        }
        return false; // Ordinary Qt/Fcitx input is never replayed or swallowed here.
    }
    void returnSource(Target *target, const QJsonObject &basis, qint64 id) {
        clearPermit();
        const bool allowed = target && target == overlayTarget_ && overlayActive() && overlayFocused_
            && metadataMatches(target, basis) && target->input == quint64(integer(basis, "inputRevision", 0))
            && target->content == quint64(integer(basis, "contentRevision", 0))
            && overlayView_.value("presentation").toObject().value("editorOwnsText").toBool();
        if (!allowed) { respond(id, QJsonObject{{"returned", false}}); return; }
        const QPointer<QLineEdit> source = target->field;
        const QPointer<QPlainTextEdit> editor = editor_;
        const QPointer<QFrame> card = card_;
        TypeLayFcitxQtStateV1 editorState{};
        if (!widgetClear(editor, &editorState) || !source || !editor || target->field != source
            || editor_ != editor || card_ != card || !overlayActive() || overlayTarget_ != target
            || !editor->hasFocus() || !metadataMatches(target, basis)
            || target->input != quint64(integer(basis, "inputRevision", 0))
            || target->content != quint64(integer(basis, "contentRevision", 0))) {
            respond(id, QJsonObject{{"returned", false}}); return;
        }
        source->setFocus(Qt::OtherFocusReason);
        const auto generation = ++returnGeneration_;
        const auto connection = connectionGeneration_;
        const qint64 until = now() + 450;
        auto *timer = new QTimer(this);
        connect(timer, &QTimer::timeout, this, [this, timer, target, basis, id, until, generation, connection] {
            const bool same = generation == returnGeneration_ && connection == connectionGeneration_
                && target->field && focused() == target && metadataMatches(target, basis)
                && target->input == quint64(integer(basis, "inputRevision", 0))
                && target->content == quint64(integer(basis, "contentRevision", 0));
            const auto originalStamp = basis.value("contextStamp").toObject();
            const bool done = same && inspect(target) && clear(target->state)
                && qint64(target->state.contextGeneration) == integer(originalStamp, "context")
                && qint64(target->state.fieldGeneration) == integer(originalStamp, "field")
                && qint64(target->state.inputEpoch) == integer(originalStamp, "input")
                && target->input == quint64(integer(basis, "inputRevision", 0))
                && local(target, target->state);
            if (!done && same && now() < until) return;
            timer->stop(); timer->deleteLater();
            if (connection == connectionGeneration_) respond(id, QJsonObject{{"returned", done && now() <= until}});
        });
        timer->start(10);
    }
    void command(const QJsonObject &frame) {
        const auto op = frame.value("op").toString();
        const auto data = frame.value("data").toObject();
        const auto id = integer(frame, "id", 0);
        Target *target = nullptr;
        const auto targetId = data.value("target").toString();
        if (targetId.isEmpty()) target = focused();
        else for (auto *candidate : targets_) if (candidate->id == targetId) { target = candidate; break; }
        if (op.startsWith("overlay-")) respond(id, overlayCommand(op, target, data));
        else if (op == "describe") respondObject(id, describe(target));
        else if (op == "capture") respondObject(id, capture(target, data.value("authority").toObject()));
        else if (op == "direct") respond(id, write(target, data, true));
        else if (op == "return") returnSource(target, data.value("authority").toObject(), id);
        else if (op == "quarantine") { ++authorizationGeneration_; if (target) target->quarantined = true; clearPermit(); respond(id); }
        else if (op == "revoke") {
            ++authorizationGeneration_; clearPermit();
            if (integer(data, "shortcuts", 0)) shortcutAuthority_ = {};
            respond(id);
        }
        else if (op == "publish") {
            ++authorizationGeneration_;
            clearPermit();
            if (target && focused() == target && metadataMatches(target, data.value("authority").toObject())
                && inspect(target) && clear(target->state)) publication_ = data;
            const auto state = publicationState(target);
            respond(id, QJsonObject{{"published", !publication_.isEmpty()
                && (data.value("permit").isNull() || data.value("permit").isUndefined() || state.value("ready").toBool())}});
            if (target) { auto report = state; report.insert("target", target->id); sendEvent("publication", report); }
            refreshHint();
        } else if (op == "configure-shortcuts") {
            shortcutAuthority_ = {}; shortcutConfiguration_ = data;
            QVariantList keys;
            if (data.value("enabled").toBool()) for (const auto *kind : {"wake", "direct"}) {
                const auto symbol = integer(data.value(QLatin1String(kind)).toObject(), "keyval", 0);
                if (symbol > 0 && symbol <= std::numeric_limits<quint32>::max()) {
                    keys.append(quint32(symbol));
                    if (symbol >= 'A' && symbol <= 'Z') keys.append(quint32(symbol - 'A' + 'a'));
                    else if (symbol >= 'a' && symbol <= 'z') keys.append(quint32(symbol - 'a' + 'A'));
                }
            }
            qApp->setProperty("typelay.fcitx.qt.control-keys.v1", keys);
            respond(id);
        } else if (op == "publish-shortcut") {
            shortcutAuthority_ = {};
            if (target && target == focused() && metadataMatches(target, data.value("authority").toObject())
                && data.value("generation") == shortcutConfiguration_.value("generation")) shortcutAuthority_ = data;
            respond(id);
        } else if (op == "trigger-shortcut") {
            const qint64 age = now() - integer(data, "triggeredAtMs");
            respond(id, QJsonObject{{"triggered", age >= 0 && age <= 150
                && data.value("generation") == shortcutConfiguration_.value("generation")
                && triggerShortcut(data.value("kind").toString(), target, data.value("authority").toObject())}});
        }
        else disconnectPeer();
    }
    void respond(qint64 id, const QJsonValue &value = QJsonValue()) {
        if (id > 0) sendFrame({{"id", id}, {"data", value}});
    }
    void respondObject(qint64 id, const QJsonObject &value) {
        respond(id, value.isEmpty() ? QJsonValue() : QJsonValue(value));
    }
    void sendEvent(const char *event, const QJsonObject &data) {
        sendFrame({{"event", QLatin1String(event)}, {"data", data}});
    }
    void sendFrame(const QJsonObject &frame) {
        if (fd_ < 0) return;
        const auto bytes = QJsonDocument(frame).toJson(QJsonDocument::Compact);
        if (bytes.isEmpty() || bytes.size() > MaxFrame || outgoing_.size() + bytes.size() + 4 > 4 * MaxFrame) {
            disconnectPeer(); return;
        }
        char prefix[4];
        qToLittleEndian<quint32>(quint32(bytes.size()), prefix);
        outgoing_.append(prefix, 4); outgoing_.append(bytes);
        if (writer_) writer_->setEnabled(true);
    }
    void disconnectPeer() {
        ++connectionGeneration_;
        if (reader_) { reader_->setEnabled(false); reader_->deleteLater(); reader_ = nullptr; }
        if (writer_) { writer_->setEnabled(false); writer_->deleteLater(); writer_ = nullptr; }
        if (fd_ >= 0) { ::close(fd_); fd_ = -1; }
        outgoing_.clear(); incoming_.clear();
        shortcutAuthority_ = {}; shortcutConfiguration_ = {};
        qApp->setProperty("typelay.fcitx.qt.control-keys.v1", QVariantList{});
        hideOverlay(); clearPermit();
    }
    void connectPeer() {
        if (fd_ >= 0) return;
        const auto runtime = qgetenv("XDG_RUNTIME_DIR");
        if (!runtime.startsWith('/')) return;
        const auto identity = qgetenv("WAYLAND_DISPLAY") + '\n' + qgetenv("NIRI_SOCKET");
        const auto hash = QCryptographicHash::hash(identity, QCryptographicHash::Sha256).toHex().left(16);
        const auto path = runtime + "/typelay/" + hash + "/native.sock";
        sockaddr_un address{};
        address.sun_family = AF_UNIX;
        struct stat info{};
        if (path.size() >= qsizetype(sizeof address.sun_path) || lstat(path.constData(), &info) != 0
            || !S_ISSOCK(info.st_mode) || info.st_uid != getuid() || (info.st_mode & 0077) != 0) return;
        std::memcpy(address.sun_path, path.constData(), size_t(path.size()) + 1);
        const int descriptor = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
        if (descriptor < 0) return;
        ucred peer{};
        socklen_t length = sizeof peer;
        if (::connect(descriptor, reinterpret_cast<sockaddr *>(&address), sizeof address) != 0
            || getsockopt(descriptor, SOL_SOCKET, SO_PEERCRED, &peer, &length) != 0 || peer.uid != getuid()) {
            ::close(descriptor); return;
        }
        fd_ = descriptor;
        reader_ = new QSocketNotifier(fd_, QSocketNotifier::Read, this);
        writer_ = new QSocketNotifier(fd_, QSocketNotifier::Write, this);
        writer_->setEnabled(false);
        connect(writer_, &QSocketNotifier::activated, this, [this] {
            while (fd_ >= 0 && !outgoing_.isEmpty()) {
                const auto count = send(fd_, outgoing_.constData(), size_t(outgoing_.size()), MSG_NOSIGNAL);
                if (count > 0) outgoing_.remove(0, qsizetype(count));
                else if (count < 0 && errno == EINTR) continue;
                else if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) return;
                else { disconnectPeer(); return; }
            }
            if (writer_) writer_->setEnabled(false);
        });
        connect(reader_, &QSocketNotifier::activated, this, [this] {
            char buffer[8192];
            while (fd_ >= 0) {
                const auto count = recv(fd_, buffer, sizeof buffer, 0);
                if (count > 0) incoming_.append(buffer, qsizetype(count));
                else if (count < 0 && errno == EINTR) continue;
                else if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) break;
                else { disconnectPeer(); return; }
                if (incoming_.size() > 2 * MaxFrame) { disconnectPeer(); return; }
            }
            if (dispatching_) return;
            QScopedValueRollback<bool> dispatch(dispatching_, true);
            while (fd_ >= 0 && incoming_.size() >= 4) {
                const auto count = qFromLittleEndian<quint32>(incoming_.constData());
                if (!count || count > MaxFrame) { disconnectPeer(); return; }
                if (incoming_.size() < qsizetype(count) + 4) return;
                QJsonParseError error;
                const auto frame = QJsonDocument::fromJson(incoming_.mid(4, count), &error);
                incoming_.remove(0, qsizetype(count) + 4);
                if (error.error != QJsonParseError::NoError || !frame.isObject()) { disconnectPeer(); return; }
                command(frame.object());
            }
        });
        sendEvent("hello", {{"protocol", 2}, {"backend", "qt6"}, {"lifetime", lifetime_}, {"pid", qint64(getpid())}});
    }

    QHash<QObject *, Target *> targets_;
    QSet<QObject *> providers_;
    QList<Target *> retired_;
    QStringList consumed_;
    QString lifetime_ = QUuid::createUuid().toString(QUuid::WithoutBraces);
    QTimer reconnect_, expiry_;
    int fd_ = -1;
    QPointer<QSocketNotifier> reader_, writer_;
    QByteArray incoming_, outgoing_;
    QJsonObject publication_, overlayView_, shortcutConfiguration_, shortcutAuthority_;
    quint64 connectionGeneration_ = 0, returnGeneration_ = 0, authorizationGeneration_ = 0;
    bool dispatching_ = false, handlingKey_ = false;
    QPointer<QFrame> card_;
    QPointer<QPlainTextEdit> editor_;
    QPointer<QLabel> hint_, status_, language_;
    QPointer<QWidget> footer_;
    Target *overlayTarget_ = nullptr;
    qint64 overlayUntil_ = 0, overlayRevision_ = 0;
    bool overlayFocused_ = false, composing_ = false, settingText_ = false, pendingEdit_ = false, expanded_ = false;
};

class Plugin final : public QGenericPlugin {
    Q_OBJECT
    Q_PLUGIN_METADATA(IID "org.qt-project.Qt.QGenericPluginFactoryInterface" FILE "typelay.json")
public:
    QObject *create(const QString &key, const QString &) override {
        if (key.compare(QStringLiteral("typelay"), Qt::CaseInsensitive) != 0) return nullptr;
        auto *token = new QObject;
        // Generic plugins load while QGuiApplication is still constructing.
        // Wait for QApplication before touching widget or focus APIs.
        QTimer::singleShot(0, qApp, [] {
            if (!qobject_cast<QApplication *>(QCoreApplication::instance())
                || qEnvironmentVariable("TYPELAY_FCITX_COOPERATIVE") != "1"
                || qEnvironmentVariableIsSet("TYPELAY_DESKTOP")
                || qApp->property("typelay.qt.native.v1").isValid()) return;
            auto *native = new Native(qApp);
            qApp->setProperty("typelay.qt.native.v1", QVariant::fromValue<QObject *>(native));
            qApp->setProperty(TypeLayFcitxQtConsumerPropertyV1, QVariant::fromValue<QObject *>(native));
        });
        return token;
    }
};
} // namespace
#include "qt-module.moc"
