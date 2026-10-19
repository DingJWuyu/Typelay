// The platform IM owns input ordering; this additive plugin owns only native
// field access and presentation. Session/model/acceptance policy stays in Runtime.
#include "../fcitx5/typelay-fcitx-qt-v1.h"
#include <QApplication>
#include <QButtonGroup>
#include <QClipboard>
#include <QCryptographicHash>
#include <QDynamicPropertyChangeEvent>
#include <QFrame>
#include <QGenericPlugin>
#include <QGridLayout>
#include <QJsonArray>
#include <QInputMethodEvent>
#include <QJsonDocument>
#include <QJsonObject>
#include <QKeyEvent>
#include <QLabel>
#include <QLineEdit>
#include <QMimeData>
#include <QPalette>
#include <QPlainTextDocumentLayout>
#include <QPlainTextEdit>
#include <QPointer>
#include <QPushButton>
#include <QRadioButton>
#include <QScopedValueRollback>
#include <QScopeGuard>
#include <QSocketNotifier>
#include <QStyleHints>
#include <QStyle>
#include <QTextBlock>
#include <QTextDocument>
#include <QTextFrame>
#include <QThread>
#include <QTimer>
#include <QUuid>
#include <QVariant>
#include <QVariantAnimation>
#include <QVBoxLayout>
#include <QtEndian>
#include <algorithm>
#include <cerrno>
#include <cstring>
#include <cstdio>
#include <limits>
#include <optional>
#include <memory>
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
bool modifierKey(int key) {
    return key == Qt::Key_Shift || key == Qt::Key_Control || key == Qt::Key_Alt
        || key == Qt::Key_Meta || key == Qt::Key_AltGr;
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
    QPointer<QWidget> field;
    QPointer<QObject> provider;
    QPointer<QTextDocument> document;
    QList<QMetaObject::Connection> documentConnections;
    quint64 documentGeneration = 0;
    int changePosition = -1, changeRemoved = -1, changeAdded = -1;
    QLineEdit *line() const { return qobject_cast<QLineEdit *>(field.data()); }
    QPlainTextEdit *plain() const { return qobject_cast<QPlainTextEdit *>(field.data()); }
    QString id = QUuid::createUuid().toString(QUuid::WithoutBraces);
    quint64 content = 1, input = 1, focus = 1, storage = 0, selection = 1;
    TypeLayFcitxQtStateV1 state{};
    bool observed = false, probing = false, mutating = false, quarantined = false;
};

struct ShortcutRequest {
    Target *target = nullptr;
    QPointer<QWidget> field;
    QPointer<QObject> provider;
    QString kind;
    QJsonObject basis;
    QJsonValue generation;
    quint64 id = 0, content = 0, selection = 0, focus = 0, context = 0, binding = 0, connection = 0;
    qint64 until = 0;
};

struct OrderedTicket {
    quint64 id = 0, authorization = 0, connection = 0, selection = 0;
    Target *target = nullptr;
    QPointer<QObject> provider;
    QJsonObject publication;
    TypeLayFcitxQtStateV1 before;
    quint32 ctrlScan = 0;
    ulong ctrlTimestamp = 0;
    bool revoked = false;
};
// A displayed editor command is independent of source write publication.
// It may only request Runtime's existing return/exit flow, never carry a plan.
struct EditorOrderedTicket {
    quint64 id = 0, connection = 0, input = 0, content = 0;
    Target *target = nullptr;
    QPointer<QWidget> source;
    QPointer<QFrame> card;
    QPointer<QPlainTextEdit> editor;
    QPointer<QTextDocument> document;
    QPointer<QObject> provider;
    QJsonObject view;
    QJsonValue generation;
    TypeLayFcitxQtStateV1 before;
    qint64 revision = 0, until = 0;
    int documentRevision = 0;
    quint32 ctrlScan = 0;
    ulong ctrlTimestamp = 0;
    bool revoked = false;
};
struct OrderedWriteContext {
    std::shared_ptr<OrderedTicket> ticket;
    bool mutationStarted = false;
};

class Native final : public QObject, public TypeLayFcitxQtControlV1, public TypeLayFcitxQtOrderedControlV1 {
    Q_OBJECT
    Q_INTERFACES(TypeLayFcitxQtControlV1 TypeLayFcitxQtOrderedControlV1)
public:
    explicit Native(QObject *parent) : QObject(parent) {
        qApp->installEventFilter(this);
        connect(qApp, &QApplication::focusChanged, this, [this](QWidget *old, QWidget *current) {
            ++focusGeneration_;
            revokeEditorOrdered();
            for (auto *widget : {old, current}) if (auto *target = discover(widget)) invalidate(target, false, true);
            syncOverlay();
        });
        connect(QGuiApplication::styleHints(), &QStyleHints::colorSchemeChanged, this,
            [this](Qt::ColorScheme) { refreshSystemTheme(); });
        connect(&reconnect_, &QTimer::timeout, this, &Native::connectPeer);
        reconnect_.start(1000);
        connect(&expiry_, &QTimer::timeout, this, [this] {
            advanceShortcut();
            if (card_ && (now() > overlayUntil_ || !overlayTarget_ || !overlayTarget_->field
                || !overlayTarget_->field->window()->isActiveWindow()
                || (paused() && !pauseCurrent(overlayTarget_, overlayAuthority_)))) hideOverlay();
            refreshHint();
        });
        expiry_.start(25);
        discover(QApplication::focusWidget());
        connectPeer();
    }
    ~Native() override {
        revokeEditorOrdered();
        revokeOrdered();
        if (fd_ >= 0) ::close(fd_);
        qDeleteAll(targets_);
        qDeleteAll(retired_);
    }

    static int sessionKey(const QJsonValue &value, bool accept) {
        if (!value.isString()) return 0;
        auto name = value.toString();
        const bool ordered = name.startsWith(QStringLiteral("Ctrl+"));
        if (ordered) name.remove(0, 5);
        if (!ordered && name == (accept ? QStringLiteral("Tab") : QStringLiteral("Esc")))
            return accept ? Qt::Key_Tab : Qt::Key_Escape;
        bool number = false;
        const int index = name.mid(1).toInt(&number);
        return name.startsWith('F') && number && index >= 1 && index <= 24
            && name == QStringLiteral("F") + QString::number(index) ? Qt::Key_F1 + index - 1 : 0;
    }
    static bool orderedBinding(const QJsonValue &value) {
        return value.isString() && value.toString().startsWith(QStringLiteral("Ctrl+"));
    }
    bool orderedConfigured() const {
        return orderedBinding(shortcutConfiguration_.value("accept")) || orderedBinding(shortcutConfiguration_.value("exit"));
    }
    static quint32 sessionSymbol(int key) {
        return key == Qt::Key_Tab ? 0xff09u : key == Qt::Key_Escape ? 0xff1bu
            : key >= Qt::Key_F1 && key <= Qt::Key_F24 ? 0xffbeu + quint32(key - Qt::Key_F1) : 0;
    }
    bool sessionApplied() const {
        const auto accept = sessionKey(shortcutConfiguration_.value("accept"), true);
        const auto exit = sessionKey(shortcutConfiguration_.value("exit"), false);
        return shortcutConfiguration_.value("sessionEnabled").isBool()
            && shortcutConfiguration_.value("generation").isString()
            && !shortcutConfiguration_.value("generation").toString().isEmpty()
            && accept && exit && accept != exit;
    }
    bool sessionCurrent(const QJsonValue &generation) const {
        return sessionApplied() && shortcutConfiguration_.value("sessionEnabled").toBool()
            && generation == shortcutConfiguration_.value("generation");
    }
    bool sessionProvider(QObject *field) const {
        if (!sessionApplied() || !field || qApp->property(TypeLayFcitxQtConsumerPropertyV1).value<QObject *>() != this)
            return false;
        const QPointer<QObject> owner = field->property(ProviderProperty).value<QObject *>();
        auto *keys = owner ? qobject_cast<TypeLayFcitxQtControlKeysV1 *>(owner.data()) : nullptr;
        const auto accept = sessionSymbol(sessionKey(shortcutConfiguration_.value("accept"), true));
        const auto exit = sessionSymbol(sessionKey(shortcutConfiguration_.value("exit"), false));
        auto *ordered = owner ? qobject_cast<TypeLayFcitxQtOrderedProviderV1 *>(owner.data()) : nullptr;
        return keys && owner->thread() == thread() && keys->supportsControlKeys(field, accept, exit)
            && (!orderedConfigured() || (ordered && ordered->supportsOrderedKeys(field,
                orderedBinding(shortcutConfiguration_.value("accept")) ? accept : 0u,
                orderedBinding(shortcutConfiguration_.value("exit")) ? exit : 0u)));
    }
    bool editorProvider(QObject *field) const {
        if (!orderedConfigured()) return sessionProvider(field);
        const QPointer<QObject> owner = field ? field->property(ProviderProperty).value<QObject *>() : nullptr;
        auto *editorRoute = owner ? qobject_cast<TypeLayFcitxQtEditorOrderedProviderV1 *>(owner.data()) : nullptr;
        return sessionProvider(field) && editorRoute && editorRoute->supportsEditorOrderedKeys(field,
            orderedBinding(shortcutConfiguration_.value("accept"))
                ? sessionSymbol(sessionKey(shortcutConfiguration_.value("accept"), true)) : 0u,
            orderedBinding(shortcutConfiguration_.value("exit"))
                ? sessionSymbol(sessionKey(shortcutConfiguration_.value("exit"), false)) : 0u);
    }
    QJsonObject sessionBindings(Target *target) const {
        // Source focus moves into our editor without retaining an IME proof on
        // the source. Keep only the same-generation key-route confirmation from
        // creation, and verify the actual editor now uses that exact provider.
        // This grants no Clear state, return authority or source write permit.
        const bool editorRoute = target && target == overlayTarget_ && overlayActive()
            && overlayGeneration_ == shortcutConfiguration_.value("generation")
            && overlaySourceProvider_ && target->provider == overlaySourceProvider_
            && editor_ && editor_->hasFocus()
            && editor_->property(ProviderProperty).value<QObject *>() == overlaySourceProvider_
            && sessionProvider(editor_);
        const bool enabled = sessionCurrent(shortcutConfiguration_.value("generation"));
        const bool sourceRoute = target && sessionProvider(target->field);
        const bool editing = target == overlayTarget_ && editor_ && editor_->hasFocus();
        return {{"generation", shortcutConfiguration_.value("generation")},
            {"accept", shortcutConfiguration_.value("accept")}, {"exit", shortcutConfiguration_.value("exit")},
            {"sourceReady", enabled && (sourceRoute || editorRoute)},
            {"editorReady", enabled && (editing ? editorRoute && editorProvider(editor_)
                : sourceRoute && editorProvider(target ? target->field.data() : nullptr))}};
    }
    bool viewCurrent(Target *target, const QJsonObject &data) const {
        const auto view = data.value("view").toObject();
        if (!target || !sessionApplied() || data.value("generation") != shortcutConfiguration_.value("generation")) return false;
        if (view.value("state").toString() == "Paused") return true;
        const auto context = view.value("viewContext").toObject();
        const auto token = context.value("token").toObject();
        const auto identity = token.value("target").toObject();
        const auto bindings = context.value("bindings").toObject();
        return shortcutConfiguration_.value("sessionEnabled").toBool()
            && identity.value("backend").toString() == "linux-qt6" && identity.value("value").toString() == target->id
            && identity.value("lifetime").toString() == lifetime_ && token.value("commandGeneration") == data.value("generation")
            && bindings.value("accept") == shortcutConfiguration_.value("accept")
            && bindings.value("exit") == shortcutConfiguration_.value("exit");
    }
    bool overlayCurrent(const QJsonObject &data) const {
        return sessionCurrent(data.value("generation")) && data.value("generation") == overlayGeneration_
            && integer(data, "revision", -1) == overlayRevision_;
    }
    bool filterControlKey(QObject *actualField, QEvent *inputEvent) override {
        if (!inputEvent || inputEvent->type() != QEvent::KeyPress) return false;
        const auto *event = static_cast<QKeyEvent *>(inputEvent);
        traceGate("hook-enter", targets_.value(actualField, nullptr), publication_.value("authority").toObject(),
            integer(publication_.value("permit").toObject(), "expiresAtMs", 0));
        if (handlingKey_ || event->isAutoRepeat() || !onThread()) {
            traceGate("hook-busy-repeat-thread", targets_.value(actualField, nullptr), {}, 0); return false;
        }
        const auto generation = shortcutConfiguration_.value("generation");
        const auto acceptKey = sessionKey(shortcutConfiguration_.value("accept"), true);
        const auto exitKey = sessionKey(shortcutConfiguration_.value("exit"), false);
        const auto shortcut = shortcutKind(event);
        if (shortcut.isEmpty() && (event->modifiers() != Qt::NoModifier
            || (event->key() != acceptKey && event->key() != exitKey)
            || (event->key() == acceptKey && orderedBinding(shortcutConfiguration_.value("accept")))
            || (event->key() == exitKey && orderedBinding(shortcutConfiguration_.value("exit")))
            || !sessionCurrent(generation) || !sessionProvider(actualField))) return false;
        QScopedValueRollback<bool> handling(handlingKey_, true);
        if (!shortcut.isEmpty()) return queueShortcut(shortcut, discover(qobject_cast<QWidget *>(actualField)),
            shortcutAuthority_.value("authority").toObject());
        if (actualField == editor_ && editor_ && editor_->hasFocus() && overlayActive()) {
            if (overlayGeneration_ != generation || (event->key() != acceptKey && event->key() != exitKey)) return false;
            const QPointer<QPlainTextEdit> editor = editor_;
            auto *source = overlayTarget_;
            TypeLayFcitxQtStateV1 state{};
            if (!sessionBindings(source).value("editorReady").toBool()
                || !widgetClear(editor, &state) || !editor || editor_ != editor || source != overlayTarget_
                || !overlayActive() || !editor->hasFocus() || composing_ || !sessionCurrent(generation)
                || overlayGeneration_ != generation || !sessionProvider(editor)
                || !sessionBindings(source).value("editorReady").toBool()) return false;
            if (event->key() == acceptKey && !editor_->isReadOnly()) {
                if (!validText(editor->toPlainText())) {
                    if (hint_) hint_->setText(QStringLiteral("译文超过 8192 字符限制，请缩短后再返回"));
                    return false;
                }
                clearPermit();
                ui("return"); // Runtime authorizes return, then issues a fresh source permit.
                return true;
            }
            if (event->key() == exitKey) { clearPermit(); ui("escape"); return true; }
            return false;
        }
        auto *target = discover(qobject_cast<QWidget *>(actualField));
        if (!target || target != focused() || publication_.isEmpty()) {
            traceGate("source-unavailable", target, publication_.value("authority").toObject(), 0); return false;
        }
        const auto current = publication_;
        if (!inspect(target)) {
            traceGate("source-inspect", target, publication_.value("authority").toObject(), 0); return false;
        }
        const auto basis = current.value("authority").toObject();
        if (!authorityMatches(target, basis, true) || !clear(target->state) || publication_ != current
            || !sessionCurrent(generation) || current.value("generation") != generation || !sessionProvider(target->field)) {
            traceGate("source-authority", target, basis, integer(current.value("permit").toObject(), "expiresAtMs", 0));
            clearPermit(); return false;
        }
        if (event->key() == exitKey) {
            const auto lease = current.value("lease").toObject();
            const qint64 age = now() - integer(lease.value("source").toObject(), "observedAtMs");
            clearPermit();
            if (lease.value("escapeAllowed").toBool() && age >= 0 && age <= 150) {
                sendEvent("escape", {{"target", target->id}, {"generation", current.value("generation")},
                    {"sessionRevision", lease.value("sessionRevision")}}); return true;
            }
            return false;
        }
        if (event->key() != acceptKey || !ready(target, current)) {
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
            {"expiresAtMs", permit.value("expiresAtMs")}, {"generation", generation}}, false);
        if (result.value("status").toString() == "RejectedBeforeMutation") {
            traceGate("source-write-rejected", target, basis, integer(permit, "expiresAtMs", 0)); return false;
        }
        traceGate("source-consumed", target, basis, integer(permit, "expiresAtMs", 0));
        sendEvent("accepted", {{"planId", id}, {"acceptedAtMs", accepted}, {"receipt", result}});
        return true;
    }

    bool beginOrdered(QObject *actual, quint64 id, const QEvent *input,
        const TypeLayFcitxQtStateV1 *before, TypeLayFcitxQtOrderedGrantV1 *grant) override {
        if (!onThread() || handlingKey_ || ordered_ || editorOrdered_ || !id || !before || !grant || !input
            || input->type() != QEvent::KeyPress || !orderedConfigured()) return false;
        const auto *key = static_cast<const QKeyEvent *>(input);
        if (actual == editor_) return beginEditorOrdered(id, key, before, grant);
        auto *target = targets_.value(actual, nullptr);
        const auto current = publication_;
        if (!target || target != focused() || current.isEmpty() || !clear(*before)
            || current.value("authority").toObject().value("contextStamp").toObject() != stamp(*before)
            || !sessionCurrent(current.value("generation")) || !sessionProvider(actual)
            || !authorityMatches(target, current.value("authority").toObject(), true)) return false;
        auto ticket = std::make_shared<OrderedTicket>();
        ticket->id = id;
        ticket->target = target;
        ticket->provider = target->provider;
        ticket->publication = current;
        ticket->before = *before;
        ticket->authorization = authorizationGeneration_;
        ticket->connection = connectionGeneration_;
        ticket->selection = target->selection;
        ticket->ctrlScan = key->nativeScanCode();
        ticket->ctrlTimestamp = key->timestamp();
        ordered_ = ticket;
        *grant = {};
        grant->accept = sessionSymbol(sessionKey(shortcutConfiguration_.value("accept"), true));
        grant->exit = sessionSymbol(sessionKey(shortcutConfiguration_.value("exit"), false));
        if (orderedBinding(shortcutConfiguration_.value("accept")) && ready(target, current))
            grant->acceptUntil = integer(current.value("permit").toObject(), "expiresAtMs", 0);
        const auto lease = current.value("lease").toObject();
        const auto observed = integer(lease.value("source").toObject(), "observedAtMs", 0);
        const auto age = now() - observed;
        if (orderedBinding(shortcutConfiguration_.value("exit")) && lease.value("escapeAllowed").toBool()
            && age >= 0 && age < 150) grant->exitUntil = observed + 150;
        if (ordered_ != ticket || ticket->revoked || publication_ != current
            || ticket->authorization != authorizationGeneration_ || ticket->connection != connectionGeneration_
            || (grant->acceptUntil <= now() && grant->exitUntil <= now())) {
            revokeOrdered(); return false;
        }
        return true;
    }
    bool decideOrdered(QObject *actual, quint64 id, const QEvent *input,
        const TypeLayFcitxQtStateV1 *current) override {
        if (editorOrdered_) return decideEditorOrdered(actual, id, input, current);
        const auto ticket = ordered_;
        if (!onThread() || handlingKey_ || !ticket || ticket->id != id || ticket->revoked
            || !current || !input || input->type() != QEvent::KeyPress) return false;
        const auto *key = static_cast<const QKeyEvent *>(input);
        auto *target = ticket->target;
        if (!target || target->field != actual || target != focused() || key->isAutoRepeat()
            || key->modifiers() != Qt::ControlModifier || target->selection != ticket->selection) return false;
        target->state = *current;
        target->observed = true;
        if (!orderedValid(ticket)) return false;
        QScopedValueRollback<bool> handling(handlingKey_, true);
        const auto publication = ticket->publication;
        const auto generation = publication.value("generation");
        const auto basis = publication.value("authority").toObject();
        const auto exit = shortcutConfiguration_.value("exit");
        if (orderedBinding(exit) && key->key() == sessionKey(exit, false)) {
            const auto lease = publication.value("lease").toObject();
            const auto age = now() - integer(lease.value("source").toObject(), "observedAtMs");
            if (!lease.value("escapeAllowed").toBool() || age < 0 || age >= 150 || !orderedValid(ticket)) return false;
            auto *provider = qobject_cast<TypeLayFcitxQtOrderedProviderV1 *>(ticket->provider.data());
            if (!provider || !provider->claimOrdered(target->field, id, &ticket->before, &target->state)) return false;
            loseProof(); // Execution take, not an external revoke.
            sendEvent("escape", {{"target", target->id}, {"generation", generation},
                {"sessionRevision", lease.value("sessionRevision")}});
            return true;
        }
        const auto accept = shortcutConfiguration_.value("accept");
        const auto permit = publication.value("permit").toObject();
        const auto plan = permit.value("plan").toObject();
        const auto planId = plan.value("id").toString();
        if (!orderedBinding(accept) || key->key() != sessionKey(accept, true)
            || planId.isEmpty() || consumed_.contains(planId) || now() >= integer(permit, "expiresAtMs", 0)) return false;
        consumed_.append(planId);
        while (consumed_.size() > 128) consumed_.removeFirst();
        const auto accepted = now();
        loseProof();
        OrderedWriteContext context{ticket, false};
        const auto result = write(target, {{"plan", plan}, {"authority", basis},
            {"expiresAtMs", permit.value("expiresAtMs")}, {"generation", generation}}, false, &context);
        if (result.value("status").toString() == "RejectedBeforeMutation" && !context.mutationStarted) return false;
        sendEvent("accepted", {{"planId", planId}, {"acceptedAtMs", accepted}, {"receipt", result}});
        return true;
    }
    void retireOrdered(quint64 ticket) override {
        if (ordered_ && ordered_->id == ticket) ordered_.reset();
        if (editorOrdered_ && editorOrdered_->id == ticket) editorOrdered_.reset();
    }

signals:
    void shortcutStateChangedV1(const QString &state, quint64 id);

private slots:
    void providerChanged(QObject *field, uint causes) {
        auto *target = targets_.value(field, nullptr);
        const bool editorPrefix = causes == (TlQtModifier | TlQtOrderedPrefix) && editorOrdered_
            && editorOrdered_->editor == field && editorOrdered_->provider == sender();
        if (!editorPrefix) revokeEditorOrdered();
        if (causes == (TlQtModifier | TlQtOrderedPrefix) && ordered_
            && ordered_->target == target && ordered_->provider == sender()) loseProof();
        else clearPermit();
        if (!target || sender() != target->provider) return;
        if (causes & TlQtBinding) shortcutAuthority_ = {};
        if (causes & (TlQtInput | TlQtBinding)) invalidate(target, false, false);
        if (causes & TlQtFocus) invalidate(target, false, true);
        // TlQtModifier still revokes the live proof. It does not itself edit
        // source text/selection; actual IM output has separate Input causes.
    }

private:
    bool onThread() const { return QThread::currentThread() == thread(); }
    void traceGate(const char *phase, Target *target, const QJsonObject &basis, qint64 until,
                   const char *reason = "none", qint64 writeStarted = 0,
                   qint64 inspectMs = -1, qint64 prepareMs = -1, qint64 selectionMs = -1) const {
        static unsigned traceLines = 0;
        if (!qEnvironmentVariableIsSet("TYPELAY_TRACE") || traceLines >= 128) return;
        ++traceLines;
        const auto expected = basis.value("contextStamp").toObject();
        const auto current = now();
        const bool field = target && target->field;
        const bool observed = target && target->observed;
        // Fixed phases/reasons and numeric/boolean guards only. Never print
        // field body, labels, metadata hashes, plan strings or target IDs.
        std::fprintf(stderr, "TypeLay Qt6 phase=%s reason=%s remainingMs=%lld field=%d provider=%d focused=%d "
            "published=%d mutating=%d probing=%d flags=%u contentSame=%d inputSame=%d focusSame=%d "
            "contextSame=%d fieldSame=%d epochSame=%d imeInputSame=%d serverSame=%d appliedSame=%d "
            "writeElapsedMs=%lld initialInspectMs=%lld prepareMs=%lld selectionMs=%lld\n",
            phase, reason, static_cast<long long>(until - current), int(field), int(target && target->provider),
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
            int(observed && integer(expected, "applied") == qint64(target->state.appliedSerial)),
            static_cast<long long>(writeStarted ? current - writeStarted : -1),
            static_cast<long long>(inspectMs), static_cast<long long>(prepareMs), static_cast<long long>(selectionMs));
    }
    bool insideOverlay(QObject *object) const {
        for (auto *parent = object; parent; parent = parent->parent()) if (parent == card_) return true;
        return false;
    }
    static bool standardField(QWidget *widget) {
        return widget && ((widget->metaObject() == &QLineEdit::staticMetaObject && typeid(*widget) == typeid(QLineEdit))
            || (widget->metaObject() == &QPlainTextEdit::staticMetaObject && typeid(*widget) == typeid(QPlainTextEdit)));
    }
    void syncDocument(Target *target) {
        auto *plain = target->plain();
        if (!plain || target->document == plain->document()) return;
        for (const auto &connection : target->documentConnections) disconnect(connection);
        target->documentConnections.clear();
        target->document = plain->document();
        ++target->documentGeneration;
        ++target->content;
        invalidate(target, true, false);
        if (target == overlayTarget_) hideOverlay();
        if (!target->document) return;
        const auto generation = target->documentGeneration;
        auto *document = target->document.data();
        target->documentConnections.append(connect(document, &QTextDocument::contentsChange, this,
            [this, target, generation](int position, int removed, int added) {
                if (target->documentGeneration != generation) return;
                target->changePosition = position;
                target->changeRemoved = removed;
                target->changeAdded = added;
                if (!target->mutating) invalidate(target, false, false);
            }));
        target->documentConnections.append(connect(document, &QTextDocument::contentsChanged, this,
            [this, target, generation] {
                if (target->documentGeneration != generation) return;
                ++target->storage;
                ++target->content;
                if (!target->mutating) invalidate(target, true, false);
            }));
        target->documentConnections.append(connect(document, &QTextDocument::documentLayoutChanged, this,
            [this, target, generation] {
                if (target->documentGeneration == generation) invalidate(target, false, false);
            }));
        target->documentConnections.append(connect(document, &QObject::destroyed, this,
            [this, target, generation] {
                if (target->documentGeneration != generation) return;
                target->document = nullptr;
                ++target->documentGeneration;
                ++target->content;
                invalidate(target, true, false);
                if (target == overlayTarget_) hideOverlay();
            }));
    }
    Target *discover(QWidget *widget) {
        if (!standardField(widget) || insideOverlay(widget)) return nullptr;
        if (auto *existing = targets_.value(widget, nullptr)) { syncDocument(existing); return existing; }
        auto *target = new Target;
        target->field = widget;
        targets_.insert(widget, target);
        const auto selectionChanged = [this, target] {
            ++target->selection;
            if (!target->mutating) invalidate(target, false, false);
        };
        if (auto *line = target->line()) {
            connect(line, &QLineEdit::textChanged, this, [this, target] {
                ++target->storage;
                ++target->content;
                if (!target->mutating) invalidate(target, true, false);
            });
            connect(line, &QLineEdit::selectionChanged, this, selectionChanged);
            connect(line, &QLineEdit::cursorPositionChanged, this, selectionChanged);
        } else if (auto *plain = target->plain()) {
            syncDocument(target);
            connect(plain, &QPlainTextEdit::selectionChanged, this, selectionChanged);
            connect(plain, &QPlainTextEdit::cursorPositionChanged, this, selectionChanged);
            // setDocument has no documentChanged signal. Widget textChanged,
            // discovery and every provider inspection recheck the real identity.
            connect(plain, &QPlainTextEdit::textChanged, this, [this, target] { syncDocument(target); });
        }
        connect(widget, &QObject::destroyed, this, [this, target, widget] {
            cancelShortcut(target, "TargetChanged");
            shortcutAuthority_ = {};
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
        return target && standardField(target->field) && target->field->isVisible()
            && target->field->window()->windowHandle()
            && (!target->plain() || (target->document && target->document == target->plain()->document()));
    }
    static bool textFormat(const QTextFormat &format) {
        if (format.objectType() != QTextFormat::NoObject || format.objectIndex() != -1) return false;
        const auto properties = format.properties();
        for (auto it = properties.cbegin(); it != properties.cend(); ++it) {
            if (it.key() >= QTextFormat::UserProperty) return false;
        }
        if (!format.isCharFormat()) return true;
        const auto character = format.toCharFormat();
        return !character.isAnchor() && character.anchorHref().isEmpty() && character.anchorNames().isEmpty();
    }
    bool plainStructure(Target *target) const {
        auto *plain = target->plain();
        auto *document = target->document.data();
        if (!plain || !document || plain->document() != document || document->maximumBlockCount() != 0
            || document->characterCount() < 1 || document->characterCount() - 1 > MaxText
            || document->metaObject() != &QTextDocument::staticMetaObject || typeid(*document) != typeid(QTextDocument)) return false;
        auto *layout = document->documentLayout();
        if (!layout || layout->metaObject() != &QPlainTextDocumentLayout::staticMetaObject
            || typeid(*layout) != typeid(QPlainTextDocumentLayout)
            || !document->rootFrame() || !document->rootFrame()->childFrames().isEmpty()) return false;
        // Inspect structure and format metadata, never fragment text. Ordinary
        // font/color/alignment styling retains Qt's normal cursor semantics.
        for (auto block = document->begin(); block.isValid(); block = block.next()) {
            const auto format = block.blockFormat();
            if (block.textList() || !block.isVisible() || !textFormat(format) || !textFormat(block.charFormat())
                || format.headingLevel() != 0 || format.hasProperty(QTextFormat::BlockCodeLanguage)
                || format.hasProperty(QTextFormat::BlockCodeFence)
                || format.marker() != QTextBlockFormat::MarkerType::NoMarker) return false;
            for (auto it = block.begin(); !it.atEnd(); ++it) {
                const auto fragment = it.fragment();
                if (fragment.isValid() && !textFormat(fragment.charFormat())) return false;
            }
        }
        return true;
    }
    QJsonObject metadata(Target *target) const {
        if (!supported(target)) return {};
        auto *field = target->field.data();
        auto *line = target->line();
        auto *plain = target->plain();
        const auto hints = field->inputMethodHints();
        const bool password = (line && line->echoMode() != QLineEdit::Normal)
            || (hints & (Qt::ImhHiddenText | Qt::ImhSensitiveData)) != 0;
        const bool constrained = (hints & (Qt::ImhExclusiveInputMask | Qt::ImhDate | Qt::ImhTime)) != 0;
        const bool readOnly = line ? line->isReadOnly() || line->validator() || !line->inputMask().isEmpty()
            : !plain || plain->isReadOnly() || !plainStructure(target);
        QString labels = field->accessibleName();
        for (const auto *label : field->window()->findChildren<QLabel *>()) {
            if (label->buddy() == field) labels += ' ' + label->text();
        }
        return {{"process", ""}, {"label", labels},
            {"automationId", field->objectName()},
            {"help", (field->accessibleDescription() + ' ' + field->toolTip() + ' '
                + (line ? line->placeholderText() : plain->placeholderText()))},
            {"aria", line ? "QLineEdit" : "QPlainTextEdit"}, {"password", password}, {"enabled", field->isEnabled()},
            {"readOnly", readOnly || constrained},
            {"offscreen", geometry(target).isEmpty()}, {"inDocument", false}, {"multiline", bool(plain)},
            {"hasValue", true}, {"hasTextEdit", bool(target->provider)},
            {"maximumLength", line ? line->maxLength() : int(MaxText)}, {"inputMethodHints", qint64(hints)},
            {"documentGeneration", qint64(target->documentGeneration)}};
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
        // The provider may retain this property for a proven Detached field.
        // Absence still grants no capability and is not itself a text edit;
        // only inspect can distinguish focused and sealed background proof.
        if (!owner) { clearPermit(); return nullptr; }
        if (target->provider != owner) {
            shortcutAuthority_ = {};
            target->provider = owner;
            target->observed = false;
            invalidate(target, false, false);
        }
        if (owner && !providers_.contains(owner)) {
            providers_.insert(owner);
            connect(owner, SIGNAL(typelayStateChangedV1(QObject*,uint)), this, SLOT(providerChanged(QObject*,uint)), Qt::DirectConnection);
            connect(owner, &QObject::destroyed, this, [this, owner] {
                providers_.remove(owner); revokeEditorOrdered(); clearPermit();
            });

        }
        return owner ? capability : nullptr;
    }
    static bool clear(const TypeLayFcitxQtStateV1 &state) {
        return state.flags == 3u && state.serverSerial == state.appliedSerial
            && state.contextGeneration != 0 && state.fieldGeneration != 0;
    }
    bool clearForWrite(Target *target, bool direct) const {
        if (clear(target->state)) return true;
        const auto &state = target->state;
        return direct && target->line() && state.flags == (TlQtKnown | TlQtDetached)
            && state.serverSerial == state.appliedSerial
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
        syncDocument(target);
        auto *capability = bind(target);
        if (!capability) return false;
        QPointer<QObject> owner = target->provider;
        QPointer<QWidget> field = target->field;
        TypeLayFcitxQtStateV1 state{};
        QScopedValueRollback<bool> probing(target->probing, true);
        const bool valid = capability->inspect(field, &state) && field && owner && target->provider == owner
            && field->property(ProviderProperty).value<QObject *>() == owner
            && state.size == sizeof state && state.version == 1u && (state.flags & ~(TlQtKnown | TlQtFocused | TlQtPending | TlQtComposing | TlQtDetached)) == 0;
        if (!valid) {
            if (ordered_ && ordered_->target == target && ordered_->provider == owner) loseProof();
            else clearPermit();
            return false;
        }
        if (target->observed && stamp(target->state) != stamp(state)) {
            const auto &before = ordered_ ? ordered_->before : target->state;
            if (ordered_ && ordered_->target == target && ordered_->provider == owner
                && state.contextGeneration == before.contextGeneration && state.fieldGeneration == before.fieldGeneration
                && state.inputEpoch == before.inputEpoch && state.serverSerial == before.serverSerial
                && state.appliedSerial == before.appliedSerial) loseProof();
            else clearPermit();
            if (target->state.contextGeneration != state.contextGeneration
                || target->state.fieldGeneration != state.fieldGeneration) shortcutAuthority_ = {};
            if (target->state.contextGeneration != state.contextGeneration
                || target->state.fieldGeneration != state.fieldGeneration
                || target->state.inputEpoch != state.inputEpoch) invalidate(target, false, false);
        }
        target->state = state;
        target->observed = true;
        return true;
    }
    bool local(Target *target, const TypeLayFcitxQtStateV1 &state) const {
        if (!supported(target) || !target->provider || target->probing || !onThread()
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
    bool sourceAuthorityMatches(Target *target, const QJsonObject &basis, bool requireFocus) {
        const auto context = basis.value("contextStamp").toObject();
        return metadataMatches(target, basis) && integer(basis, "contentRevision") == qint64(target->content)
            && integer(basis, "inputRevision") == qint64(target->input)
            && (!requireFocus || (focused() == target && integer(basis, "focusRevision") == qint64(target->focus)))
            && target->observed && integer(context, "context") == qint64(target->state.contextGeneration)
            && integer(context, "field") == qint64(target->state.fieldGeneration)
            && integer(context, "input") == qint64(target->state.inputEpoch);
    }
    bool authorityMatches(Target *target, const QJsonObject &basis, bool requireFocus) {
        return sourceAuthorityMatches(target, basis, requireFocus)
            && basis.value("contextStamp").toObject() == stamp(target->state);
    }
    QJsonObject describe(Target *target) {
        if (!target || target->mutating || target->probing) return {};
        inspect(target);
        if (!supported(target)) return {};
        return {{"target", target->id}, {"metadata", metadata(target)}, {"authority", authority(target)},
            {"geometry", geometry(target)}, {"focused", target == focused()}, {"publication", publicationState(target)},
            {"sessionBindings", sessionBindings(target)}};
    }
    QJsonObject selection(Target *target) const {
        if (const auto *line = target->line()) {
            const int start = line->selectionStart() < 0 ? line->cursorPosition() : line->selectionStart();
            return {{"start", start}, {"end", start + line->selectionLength()}};
        }
        const auto cursor = target->plain()->textCursor();
        return {{"start", cursor.selectionStart()}, {"end", cursor.selectionEnd()}};
    }
    std::optional<QString> body(Target *target) const {
        if (const auto *line = target->line()) return line->text();
        if (!target->document || target->document != target->plain()->document()) return std::nullopt;
        auto text = target->document->toRawText();
        // A block separator occupies one UTF-16 unit. Map only paragraphs to
        // LF; toPlainText would also lose NBSP and genuine U+2028 line breaks.
        if (text.size() != target->document->characterCount() - 1
            || text.contains(QChar::ObjectReplacementCharacter) || text.contains(QChar(0xfdd0))
            || text.contains(QChar(0xfdd1)) || text.contains('\r')) return std::nullopt;
        text.replace(QChar::ParagraphSeparator, QLatin1Char('\n'));
        return text;
    }
    QJsonObject capture(Target *target, const QJsonObject &basis,
                        qint64 until = std::numeric_limits<qint64>::max()) {
        const auto configuration = shortcutConfiguration_;
        const qint64 age = now() - integer(basis, "observedAtMs");
        if (age < 0 || age > 150 || !inspect(target) || !clear(target->state)
            || !authorityMatches(target, basis, true) || !local(target, target->state) || now() >= until) return {};
        const auto captured = body(target);
        if (!captured || !validText(*captured)) return {};
        const auto text = *captured;
        const auto selected = selection(target);
        if (!boundary(text, integer(selected, "start")) || !boundary(text, integer(selected, "end"))) return {};
        auto result = describe(target);
        if (result.isEmpty() || !authorityMatches(target, basis, true) || !local(target, target->state)
            || configuration != shortcutConfiguration_ || now() - integer(basis, "observedAtMs") > 150 || now() >= until) return {};
        result.insert("text", text);
        result.insert("selection", selection(target));
        result.insert("ime", "Clear");
        result.insert("writeSupported", !target->quarantined && sessionBindings(target).value("sourceReady").toBool());
        result.insert("observedAtMs", now());
        return result;
    }
    void invalidate(Target *target, bool textChanged, bool focusChanged) {
        if (!target || !target->field) return;
        if (editorOrdered_ && editorOrdered_->target == target) revokeEditorOrdered();
        cancelShortcut(target, focusChanged ? "NotSourceFocus" : textChanged ? "SourceChanged" : "EpochChanged");
        if (focusChanged) shortcutAuthority_ = {};
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
            || !sessionCurrent(publication.value("generation")) || !sessionProvider(target->field)
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
    void loseProof() { publication_ = {}; refreshHint(); }
    void revokeOrdered() {
        const auto ticket = std::move(ordered_);
        if (!ticket) return;
        ticket->revoked = true;
        auto *provider = ticket->provider ? qobject_cast<TypeLayFcitxQtOrderedProviderV1 *>(ticket->provider.data()) : nullptr;
        if (provider) provider->cancelOrdered(ticket->id);
    }
    void revokeEditorOrdered() {
        const auto ticket = std::move(editorOrdered_);
        if (!ticket) return;
        ticket->revoked = true;
        auto *provider = ticket->provider ? qobject_cast<TypeLayFcitxQtOrderedProviderV1 *>(ticket->provider.data()) : nullptr;
        if (provider) provider->cancelOrdered(ticket->id);
    }
    bool editorOrderedCurrent(const std::shared_ptr<EditorOrderedTicket> &ticket) const {
        return ticket && !ticket->revoked && editorOrdered_ == ticket && fd_ >= 0
            && ticket->connection == connectionGeneration_ && ticket->target && ticket->source
            && ticket->card && ticket->editor && ticket->document && ticket->provider
            && overlayTarget_ == ticket->target && ticket->target->field == ticket->source
            && card_ == ticket->card && editor_ == ticket->editor
            && ticket->editor->document() == ticket->document
            && ticket->document->revision() == ticket->documentRevision
            && overlayRevision_ == ticket->revision && overlayView_ == ticket->view
            && overlayGeneration_ == ticket->generation && sessionCurrent(ticket->generation)
            && ticket->target->input == ticket->input && ticket->target->content == ticket->content
            && metadataMatches(ticket->target, overlayAuthority_) && overlayActive() && !paused()
            && ticket->editor->hasFocus() && QGuiApplication::focusObject() == ticket->editor
            && !ticket->editor->isReadOnly() && !composing_ && now() < ticket->until
            && overlaySourceProvider_ == ticket->provider && ticket->target->provider == ticket->provider
            && ticket->editor->property(ProviderProperty).value<QObject *>() == ticket->provider
            && sessionBindings(ticket->target).value("editorReady").toBool()
            && ticket->view.value("viewContext").toObject().value("canEdit").toBool()
            && ticket->view.value("presentation").toObject().value("editorOwnsText").toBool();
    }
    bool beginEditorOrdered(quint64 id, const QKeyEvent *key, const TypeLayFcitxQtStateV1 *before,
        TypeLayFcitxQtOrderedGrantV1 *grant) {
        if (!overlayTarget_ || !editor_ || !card_ || !clear(*before)
            || !key->nativeScanCode() || key->isAutoRepeat() || key->key() != Qt::Key_Control
            || (key->modifiers() & ~Qt::ControlModifier) || !validText(editor_->toPlainText())) return false;
        auto ticket = std::make_shared<EditorOrderedTicket>();
        ticket->id = id;
        ticket->connection = connectionGeneration_;
        ticket->target = overlayTarget_;
        ticket->source = overlayTarget_->field;
        ticket->card = card_;
        ticket->editor = editor_;
        ticket->document = editor_->document();
        ticket->documentRevision = ticket->document->revision();
        ticket->provider = editor_->property(ProviderProperty).value<QObject *>();
        ticket->view = overlayView_;
        ticket->generation = overlayGeneration_;
        ticket->revision = overlayRevision_;
        ticket->input = overlayTarget_->input;
        ticket->content = overlayTarget_->content;
        ticket->before = *before;
        ticket->ctrlScan = key->nativeScanCode();
        ticket->ctrlTimestamp = key->timestamp();
        ticket->until = std::min(overlayUntil_, now() + 150);
        editorOrdered_ = ticket;
        auto *provider = ticket->provider ? qobject_cast<TypeLayFcitxQtV1 *>(ticket->provider.data()) : nullptr;
        if (!editorOrderedCurrent(ticket) || !provider || !provider->validateLocal(ticket->editor, before)
            || !editorOrderedCurrent(ticket)) { revokeEditorOrdered(); return false; }
        *grant = {};
        grant->accept = sessionSymbol(sessionKey(shortcutConfiguration_.value("accept"), true));
        grant->exit = sessionSymbol(sessionKey(shortcutConfiguration_.value("exit"), false));
        if (orderedBinding(shortcutConfiguration_.value("accept"))) grant->acceptUntil = ticket->until;
        if (orderedBinding(shortcutConfiguration_.value("exit"))) grant->exitUntil = ticket->until;
        return true;
    }
    bool decideEditorOrdered(QObject *actual, quint64 id, const QEvent *input,
        const TypeLayFcitxQtStateV1 *current) {
        const auto ticket = editorOrdered_;
        if (!onThread() || handlingKey_ || !ticket || ticket->id != id || ticket->editor != actual
            || !input || input->type() != QEvent::KeyPress || !current || !clear(*current)) return false;
        const auto *key = static_cast<const QKeyEvent *>(input);
        const bool accept = orderedBinding(shortcutConfiguration_.value("accept"))
            && key->key() == sessionKey(shortcutConfiguration_.value("accept"), true);
        const bool exit = orderedBinding(shortcutConfiguration_.value("exit"))
            && key->key() == sessionKey(shortcutConfiguration_.value("exit"), false);
        if ((!accept && !exit) || key->isAutoRepeat() || key->modifiers() != Qt::ControlModifier
            || !editorOrderedCurrent(ticket) || !validText(ticket->editor->toPlainText())) return false;
        QScopedValueRollback<bool> handling(handlingKey_, true);
        auto *provider = qobject_cast<TypeLayFcitxQtOrderedProviderV1 *>(ticket->provider.data());
        if (!provider || !provider->validateOrdered(actual, id, &ticket->before, current)
            || !editorOrderedCurrent(ticket) || !provider->claimOrdered(actual, id, &ticket->before, current)) return false;
        // Claim fixes the original Fn pair before any host/focus callback. This
        // message cannot write; Runtime must return and freshly capture the source.
        loseProof();
        sendEvent("ui", {{"target", ticket->target->id}, {"operation", exit ? "escape" : "return"},
            {"revision", ticket->revision}, {"generation", ticket->generation}});
        return true;
    }
    void clearPermit() { revokeOrdered(); loseProof(); }
    bool orderedLive(const std::shared_ptr<OrderedTicket> &ticket) const {
        return ticket && !ticket->revoked && ordered_ == ticket && ticket->target && ticket->provider
            && fd_ >= 0 && ticket->authorization == authorizationGeneration_
            && ticket->connection == connectionGeneration_ && ticket->target->provider == ticket->provider
            && sessionCurrent(ticket->publication.value("generation"));
    }
    bool orderedContinuity(const std::shared_ptr<OrderedTicket> &ticket) {
        if (!orderedLive(ticket)) return false;
        auto *provider = qobject_cast<TypeLayFcitxQtOrderedProviderV1 *>(ticket->provider.data());
        return provider && provider->validateOrdered(ticket->target->field, ticket->id,
            &ticket->before, &ticket->target->state);
    }
    bool orderedValid(const std::shared_ptr<OrderedTicket> &ticket) {
        return orderedLive(ticket)
            && sourceAuthorityMatches(ticket->target, ticket->publication.value("authority").toObject(), true)
            && orderedContinuity(ticket);
    }
    bool sameOrderedPublication(const QJsonObject &next) const {
        if (!ordered_) return false;
        auto previous = ordered_->publication;
        auto incoming = next;
        // Only expiry/observation renewal of the exact same candidate can coexist
        // with a frozen ticket. It never replaces that ticket's original clock.
        for (auto *object : {&previous, &incoming}) {
            auto authority = object->value("authority").toObject();
            authority.remove("observedAtMs");
            auto stamp = authority.value("contextStamp").toObject();
            // Renewal can retain the ticket, never grant continuity. Only Decide
            // proves this local epoch belongs to the original Ctrl prefix.
            stamp.remove("epoch"); authority.insert("contextStamp", stamp);
            object->insert("authority", authority);
            auto permit = object->value("permit").toObject();
            permit.remove("expiresAtMs"); permit.remove("observedAtMs");
            auto plan = permit.value("plan").toObject(); plan.remove("id");
            auto expected = plan.value("expected").toObject(); expected.remove("observedAtMs");
            plan.insert("expected", expected); permit.insert("plan", plan);
            object->insert("permit", permit);
            auto lease = object->value("lease").toObject();
            auto source = lease.value("source").toObject(); source.remove("observedAtMs");
            lease.insert("source", source); object->insert("lease", lease);
        }
        return previous == incoming;
    }
    QString shortcutKind(const QKeyEvent *event) const {
        if (!shortcutConfiguration_.value("enabled").toBool()) return {};
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
    void rejectShortcut(const QString &kind, const QJsonValue &generation, quint64 commandId, const char *reason) {
        if (!shortcutConfiguration_.value("enabled").toBool()
            || generation != shortcutConfiguration_.value("generation")) return;
        sendEvent("shortcut-rejected", {{"kind", kind}, {"generation", generation},
            {"commandId", qint64(commandId)}, {"reason", QLatin1String(reason)}});
    }
    quint64 startShortcut(const QString &kind, const QJsonValue &generation, qint64 triggeredAt) {
        // A delayed compositor command must not cancel a newer native key.
        // Equal timestamps cannot establish a newer command either.
        if (triggeredAt <= shortcutStartedAt_) return 0;
        shortcutStartedAt_ = triggeredAt;
        const auto commandId = ++shortcutSequence_;
        // Register the newer command first; an older cancellation must not
        // supersede its feedback or be mistaken for this command's outcome.
        sendEvent("shortcut-started", {{"kind", kind}, {"generation", generation},
            {"commandId", qint64(commandId)}, {"triggeredAtMs", triggeredAt}});
        cancelShortcut();
        return commandId;
    }
    const char *shortcutRefusal(Target *target, const QJsonObject &basis) {
        if (!supported(target) || target->quarantined) return "Unsupported";
        if (target != focused()) return "NotSourceFocus";
        const auto fields = metadata(target); // Metadata only, never source text.
        if (fields.value("password").toBool()) return "Sensitive";
        if (fields.value("readOnly").toBool()) return "ReadOnly";
        const auto observed = integer(basis, "observedAtMs"), current = now();
        if (observed < 0 || current < observed || current - observed >= 150) return "StaleObservation";
        if (!target->provider || !target->observed || !clear(target->state)) return "ImeUnclear";
        return "EpochChanged";
    }
    void cancelShortcut(Target *target = nullptr, const char *reason = "EpochChanged", bool report = true) {
        if (!pendingShortcut_ || (target && pendingShortcut_->target != target)) return;
        const auto request = *pendingShortcut_;
        pendingShortcut_.reset();
        traceGate("shortcut-cancelled", request.target, request.basis, request.until, reason);
        if (report) rejectShortcut(request.kind, request.generation, request.id, reason);
        emit shortcutStateChangedV1(QStringLiteral("cancelled"), request.id);
    }
    bool queueShortcut(const QString &kind, Target *target, const QJsonObject &basis) {
        if (!shortcutConfiguration_.value("enabled").toBool()) return false;
        const auto generation = shortcutConfiguration_.value("generation");
        const auto commandId = startShortcut(kind, generation, now());
        if (!commandId) return false;
        const qint64 observed = integer(basis, "observedAtMs"), current = now();
        if (!target || target != focused() || target->quarantined || target->mutating || !target->observed
            || !target->provider || observed < 0 || current < observed || current - observed >= 150
            || !metadataMatches(target, basis) || shortcutAuthority_.isEmpty()
            || shortcutAuthority_.value("target").toString() != target->id
            || shortcutAuthority_.value("generation") != shortcutConfiguration_.value("generation")
            || shortcutAuthority_.value("authority").toObject() != basis
            || target->field->property(ProviderProperty).value<QObject *>() != target->provider) {
            rejectShortcut(kind, generation, commandId, shortcutRefusal(target, basis));
            emit shortcutStateChangedV1(QStringLiteral("rejected"), commandId);
            return false; // Feedback does not grant ownership of the key.
        }
        pendingShortcut_ = ShortcutRequest{target, target->field, target->provider, kind, basis,
            generation, commandId, target->content,
            target->selection, target->focus, target->state.contextGeneration, target->state.fieldGeneration,
            connectionGeneration_, observed + 150};
        clearPermit();
        shortcutAuthority_ = {};
        traceGate("shortcut-queued", target, basis, observed + 150);
        emit shortcutStateChangedV1(QStringLiteral("queued"), commandId);
        return true; // Only this explicit command is consumed. No body has been read.
    }
    const char *shortcutFailure(const ShortcutRequest &request) {
        if (!pendingShortcut_ || pendingShortcut_->id != request.id) return "EpochChanged";
        if (!request.field || request.target->field != request.field) return "TargetChanged";
        if (focused() != request.target || request.target->focus != request.focus) return "NotSourceFocus";
        if (request.target->content != request.content) return "SourceChanged";
        if (request.target->selection != request.selection) return "SelectionChanged";
        if (connectionGeneration_ != request.connection || fd_ < 0) return "PlatformFailure";
        if (now() >= request.until) return "StaleObservation";
        if (!request.provider || request.target->provider != request.provider
            || request.target->state.contextGeneration != request.context
            || request.target->state.fieldGeneration != request.binding
            || request.field->property(ProviderProperty).value<QObject *>() != request.provider
            || !shortcutConfiguration_.value("enabled").toBool()
            || shortcutConfiguration_.value("generation") != request.generation
            || !metadataMatches(request.target, request.basis)) return "EpochChanged";
        return nullptr;
    }
    void advanceShortcut() {
        if (!pendingShortcut_ || advancingShortcut_) return;
        QScopedValueRollback<bool> advancing(advancingShortcut_, true);
        const auto request = *pendingShortcut_;
        auto unchanged = [&] {
            const auto *reason = shortcutFailure(request);
            if (!reason) return true;
            if (pendingShortcut_ && pendingShortcut_->id == request.id) cancelShortcut(request.target, reason);
            return false;
        };
        if (!unchanged()) return;
        // The timer does not grant Clear. Original DBus/output/forward retirement
        // and the exact provider's fresh proof are still mandatory.
        if (!inspect(request.target)) return;
        if (!unchanged()) return;
        if (!clear(request.target->state)) {
            if (request.target->state.flags & TlQtComposing) cancelShortcut(request.target, "ImeUnclear");
            return;
        }
        const auto description = describe(request.target);
        if (!unchanged()) return;
        const auto read = capture(request.target, description.value("authority").toObject(), request.until);
        if (!unchanged()) return;
        if (read.isEmpty()) return;
        pendingShortcut_.reset();
        clearPermit();
        sendEvent("shortcut", {{"kind", request.kind}, {"generation", request.generation},
            {"commandId", qint64(request.id)}, {"native", read}, {"acceptedAtMs", now()}});
        traceGate("shortcut-dispatched", request.target, request.basis, request.until);
        emit shortcutStateChangedV1(QStringLiteral("dispatched"), request.id);
    }
    bool triggerShortcut(const QString &kind, Target *target, const QJsonObject &basis, qint64 triggeredAt) {
        if ((kind != "wake" && kind != "direct") || !shortcutConfiguration_.value("enabled").toBool()) return false;
        const auto generation = shortcutConfiguration_.value("generation");
        const auto commandId = startShortcut(kind, generation, triggeredAt);
        if (!commandId) return false;
        auto reject = [&](const char *reason) {
            rejectShortcut(kind, generation, commandId, reason);
            emit shortcutStateChangedV1(QStringLiteral("rejected"), commandId);
            return false;
        };
        const qint64 age = now() - integer(basis, "observedAtMs");
        if (shortcutAuthority_.isEmpty() || !target || target != focused() || age < 0 || age > 150
            || shortcutAuthority_.value("target").toString() != target->id
            || shortcutAuthority_.value("generation") != shortcutConfiguration_.value("generation")
            || shortcutAuthority_.value("authority").toObject() != basis) return reject(shortcutRefusal(target, basis));
        if (!inspect(target) || !clear(target->state)) return reject("ImeUnclear");
        if (!authorityMatches(target, basis, true)) return reject(shortcutRefusal(target, basis));
        const auto description = describe(target);
        const auto read = capture(target, description.value("authority").toObject());
        if (read.isEmpty() || generation != shortcutConfiguration_.value("generation")) return reject(shortcutRefusal(target, basis));
        clearPermit(); shortcutAuthority_ = {};
        sendEvent("shortcut", {{"kind", kind}, {"generation", generation}, {"commandId", qint64(commandId)},
            {"native", read}, {"acceptedAtMs", now()}});
        return fd_ >= 0;
    }
    QJsonObject write(Target *target, const QJsonObject &request, bool direct,
                      OrderedWriteContext *ordered = nullptr) {
        // Sample existing operations only; emit their timings with the final
        // outcome, never add an IM request or logging between safety checks.
        const qint64 writeStarted = now();
        qint64 inspectMs = -1, prepareMs = -1, selectionMs = -1;
        const auto plan = request.value("plan").toObject();
        const auto expected = plan.value("expected").toObject();
        const auto basis = request.value("authority").toObject();
        const auto approved = direct ? request.value("approved").toObject() : basis;
        const auto identity = expected.value("target").toObject();
        auto reject = [&](const char *phase, const char *reason) {
            traceGate(phase, target, basis, integer(request, "expiresAtMs", 0), reason,
                writeStarted, inspectMs, prepareMs, selectionMs);
            if (ordered && ordered->mutationStarted) {
                if (target) target->quarantined = true;
                return receipt("OutcomeUnknown", "UnknownOutcome");
            }
            return receipt("RejectedBeforeMutation", reason);
        };
        traceGate("write-enter", target, basis, integer(request, "expiresAtMs", 0));
        const auto generation = request.value("generation");
        const auto authorization = authorizationGeneration_;
        if (!sessionCurrent(generation)) return reject("write-generation", "EpochChanged");
        if (!target || plan.isEmpty() || expected.isEmpty() || basis.isEmpty() || approved.isEmpty()
            || identity.value("backend").toString() != "linux-qt6" || identity.value("value").toString() != target->id
            || identity.value("lifetime").toString() != lifetime_)
            return reject("write-header", "BadPlan");
        const qint64 inspectStarted = now();
        const bool inspected = !target->mutating && !target->quarantined && inspect(target) && clearForWrite(target, direct);
        inspectMs = now() - inspectStarted;
        if (!inspected)
            return reject("write-inspect", "ImeUnclear");
        const qint64 until = integer(request, "expiresAtMs", 0), current = now();
        if (until <= current || until - current > (direct ? 250 : 100))
            return reject("write-deadline", "StaleObservation");
        const bool background = target->state.flags == (TlQtKnown | TlQtDetached);
        const bool directLine = direct && target->line();
        const qint64 approvedAge = now() - integer(approved, "observedAtMs");
        const bool authorized = direct
            ? sourceAuthorityMatches(target, basis, !directLine) && authorityMatches(target, approved, !background)
                && integer(approved, "focusRevision") == qint64(target->focus)
                && approvedAge >= 0 && approvedAge <= 150
            : ordered ? orderedValid(ordered->ticket) : authorityMatches(target, basis, true);
        if (!metadataMatches(target, approved) || !authorized || !sessionCurrent(generation)
            || authorizationGeneration_ != authorization || !sessionProvider(target->field))
            return reject("write-authority", "EpochChanged");
        QPointer<QWidget> field = target->field;
        QPointer<QObject> provider = target->provider;
        const QPointer<QWidget> active = QApplication::activeWindow();
        const QPointer<QWidget> focusedWidget = QApplication::focusWidget();
        const QPointer<QObject> focusedObject = QGuiApplication::focusObject();
        const auto focusGeneration = focusGeneration_;
        auto focusUnchanged = [&] {
            return field && active && focusedWidget && focusedObject && focusGeneration_ == focusGeneration
                && QApplication::activeWindow() == active && QApplication::focusWidget() == focusedWidget
                && QGuiApplication::focusObject() == focusedObject
                && (background ? focusedWidget != field && focusedObject != field
                    && active == field->window() && focusedWidget->window() == field->window()
                    : focused() == target);
        };
        if (!focusUnchanged()) return reject("write-current-focus", "EpochChanged");
        const QPointer<QTextDocument> document = target->document;
        const auto documentGeneration = target->documentGeneration;
        const auto captured = body(target);
        if (!captured) return reject("write-mapping", "InvalidText");
        const auto text = *captured;
        const bool multiline = target->plain() != nullptr;
        const auto selected = selection(target);
        if (!expected.value("text").isString() || text != expected.value("text").toString())
            return reject("write-source", "SourceChanged");
        const auto expectedSelection = expected.value("selection").toObject();
        const auto requestedStart = expectedSelection.value("start");
        const auto requestedEnd = expectedSelection.value("end");
        // The shared TextRange wire object also contains derived properties.
        // Only the two exact UTF-16 offsets define selection equality.
        if (!requestedStart.isDouble() || !requestedEnd.isDouble()
            || requestedStart.toDouble() != selected.value("start").toInt()
            || requestedEnd.toDouble() != selected.value("end").toInt())
            return reject("write-selection", "SelectionChanged");
        const auto replacement = plan.value("replacement").toString();
        const auto result = plan.value("resultText").toString();
        const auto scope = plan.value("scope").toObject();
        const qint64 from = scope.isEmpty() ? 0 : integer(scope, "start");
        const qint64 to = scope.isEmpty() ? text.size() : integer(scope, "end");
        if (!validText(text) || !validText(replacement) || !validText(result) || replacement.isEmpty()
            || (!multiline && replacement.contains('\n')) || replacement.contains('\r')
            || (multiline && (replacement.contains(QChar::ParagraphSeparator)
                || replacement.contains(QChar::ObjectReplacementCharacter) || replacement.contains(QChar(0xfdd0))
                || replacement.contains(QChar(0xfdd1))))
            || !boundary(text, from) || !boundary(text, to) || to < from || (!scope.isEmpty() && to == from)
            || integer(plan, "caret") != from + replacement.size()
            || (!multiline && result.size() > target->line()->maxLength())
            || result != text.left(qsizetype(from)) + replacement + text.mid(qsizetype(to)))
            return reject("write-text", "InvalidText");
        const auto initialState = target->state;
        const auto connection = connectionGeneration_;
        const auto input = target->input, focus = target->focus, storage = target->storage;
        const auto content = target->content;
        auto unchanged = [&] {
            return field && provider && fd_ >= 0 && connectionGeneration_ == connection
                && authorizationGeneration_ == authorization && sessionCurrent(generation)
                && sessionProvider(field) && target->field == field && target->provider == provider
                && focusUnchanged() && target->input == input && target->focus == focus
                && target->storage == storage && target->content == content
                && metadataMatches(target, basis) && metadataMatches(target, approved)
                && target->document == document && target->documentGeneration == documentGeneration
                && body(target) == captured && (!ordered || orderedLive(ordered->ticket));
        };
        auto *capability = qobject_cast<TypeLayFcitxQtV1 *>(provider);
        if (!capability || !local(target, initialState))
            return reject("write-local-before-prepare", "ImeUnclear");
        // Prepare against the real provider while the original body is intact,
        // then inspect again. Detached prepare is a provider-local validation,
        // never a reset of the currently focused field. Standard Clear selection
        // has no preedit to commit; unexpected output still invalidates proof.
        {
            QScopedValueRollback<bool> probing(target->probing, true);
            const qint64 prepareStarted = now();
            const bool prepared = capability->prepareCaret(field, &initialState);
            prepareMs = now() - prepareStarted;
            if (!prepared)
                return reject("write-prepare", "ImeUnclear");
        }
        if (!unchanged() || !inspect(target) || !clearForWrite(target, direct)
            || target->state.contextGeneration != initialState.contextGeneration
            || target->state.fieldGeneration != initialState.fieldGeneration
            || target->state.inputEpoch != initialState.inputEpoch || !unchanged()
            || (ordered && !orderedValid(ordered->ticket)))
            return reject("write-after-prepare", "EpochChanged");
        const auto baseline = target->state;
        QScopedValueRollback<bool> mutation(target->mutating, true);
        const qint64 selectionStarted = now();
        if (ordered) {
            auto *control = provider ? qobject_cast<TypeLayFcitxQtOrderedProviderV1 *>(provider.data()) : nullptr;
            if (!orderedValid(ordered->ticket) || !control || !control->claimOrdered(field, ordered->ticket->id,
                &ordered->ticket->before, &target->state)) return reject("write-ordered-claim", "EpochChanged");
            ordered->mutationStarted = true; // Selection itself is observable application state.
        }
        if (auto *line = target->line()) line->setSelection(int(from), int(to - from));
        else {
            QTextCursor cursor(document);
            cursor.setPosition(int(from));
            cursor.setPosition(int(to), QTextCursor::KeepAnchor);
            target->plain()->setTextCursor(cursor);
        }
        selectionMs = now() - selectionStarted;
        // Selection signals are application code. Recheck every source/input/IM
        // condition after them and immediately before the first body mutation.
        const auto preparedSelection = QJsonObject{{"start", from}, {"end", to}};
        if (!unchanged() || selection(target) != preparedSelection || !local(target, baseline))
            return reject("write-after-selection", "EpochChanged");
        if (now() >= until) return reject("write-selection-deadline", "StaleObservation");
        const auto caret = integer(plan, "caret");
        const QJsonObject writtenSelection{{"start", caret}, {"end", caret}};
        auto written = [&] {
            return field && provider && target->field == field && target->provider == provider
                && target->document == document && target->documentGeneration == documentGeneration
                && focusUnchanged() && target->input == input && target->focus == focus
                && metadataMatches(target, basis) && metadataMatches(target, approved)
                && target->storage == storage + 1 && target->content == content + 1
                && body(target) == std::optional<QString>(result) && local(target, baseline)
                && (!ordered || orderedContinuity(ordered->ticket));
        };
        if (auto *line = target->line()) line->insert(replacement);
        else {
            auto cursor = target->plain()->textCursor();
            cursor.beginEditBlock();
            cursor.insertText(replacement);
            cursor.endEditBlock(); // Native single undo item, never setPlainText.
            // Application callbacks can change/destroy the document during the
            // edit notification. Do not then reset their new cursor/document.
            if (written() && selection(target) == writtenSelection) target->plain()->setTextCursor(cursor);
        }
        const bool verified = written() && selection(target) == writtenSelection
            && (!multiline || (target->changePosition == from && target->changeRemoved == to - from
                && target->changeAdded == replacement.size()));
        ++target->input;
        clearPermit();
        if (!verified) {
            target->quarantined = true;
            traceGate("write-readback", target, basis, until, "UnknownOutcome",
                writeStarted, inspectMs, prepareMs, selectionMs);
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
        traceGate("write-verified", target, basis, until, "None", writeStarted, inspectMs, prepareMs, selectionMs);
        return outcome;
    }

    void ui(const char *operation, const QJsonValue &value = QJsonValue()) {
        if (overlayTarget_) sendEvent("ui", {{"target", overlayTarget_->id}, {"operation", QLatin1String(operation)},
            {"revision", overlayRevision_}, {"generation", overlayGeneration_}, {"value", value}});
    }
    bool paused() const {
        return overlayView_.value("state").toString() == "Paused";
    }
    bool pauseCurrent(Target *target, QJsonObject basis) {
        // Provider inspection may dispatch source invalidation. Keep a copy of
        // the rendered authority and recheck all metadata generations after it.
        return inspect(target) && clear(target->state) && authorityMatches(target, basis, true)
            && local(target, target->state) && !geometry(target).isEmpty();
    }
    bool overlayActive() const {
        return card_ && card_->isVisible() && overlayTarget_ && overlayTarget_->field
            && overlayTarget_->field->window()->isActiveWindow() && now() <= overlayUntil_;
    }
    void syncOverlay() {
        const bool focusedEditor = overlayActive() && insideOverlay(QApplication::focusWidget());
        if (focusedEditor != overlayFocused_) { overlayFocused_ = focusedEditor; ui("focus", focusedEditor); }
        auto *source = focused();
        if (card_ && source != overlayTarget_ && (paused() || source)) hideOverlay();
    }
    void stopOverlayMotion() {
        overlayMotion_.stop();
        disconnect(&overlayMotion_, nullptr, this, nullptr);
    }
    void hideOverlay() {
        revokeEditorOrdered();
        stopOverlayMotion();
        if (!card_) return;
        // deleteLater does not clear the live editor until a later event turn.
        // Withdraw its body synchronously, without publishing an empty draft.
        if (editor_) {
            QScopedValueRollback<bool> setting(settingText_, true);
            editor_->clear();
        }
        if (overlayFocused_) ui("focus", false);
        if (composing_) ui("composition", false);
        ui("visible", false);
        auto *card = card_.data();
        card_ = nullptr; editor_ = nullptr; hint_ = nullptr; status_ = nullptr;
        language_ = nullptr; exit_ = nullptr; footer_ = nullptr; menu_ = nullptr; tools_ = nullptr; swap_ = nullptr; resume_ = nullptr;
        sourceLanguages_ = nullptr; targetLanguages_ = nullptr; overlayTarget_ = nullptr;
        sourceLanguageLayout_ = nullptr; targetLanguageLayout_ = nullptr;
        sourceChoices_ = {}; targetChoices_ = {}; savedSourceLanguage_.clear(); savedTargetLanguage_.clear();
        overlayView_ = {}; overlayAuthority_ = {}; overlayGeneration_ = QJsonValue(); overlaySourceProvider_ = nullptr; overlayFocused_ = false; composing_ = false;
        overlayTheme_ = QStringLiteral("system");
        pendingEdit_ = false; pendingResume_ = false;
        card->hide();
        card->deleteLater();
    }
    void applyOverlayTheme() {
        if (!card_) return;
        bool dark = overlayTheme_ == QStringLiteral("dark");
        if (overlayTheme_ == QStringLiteral("system")) {
            const auto scheme = QGuiApplication::styleHints()->colorScheme();
            const auto palette = QApplication::palette();
            dark = scheme == Qt::ColorScheme::Dark || (scheme == Qt::ColorScheme::Unknown
                && palette.color(QPalette::Window).lightness() < palette.color(QPalette::WindowText).lightness());
        }
        const auto background = dark ? QStringLiteral("#20242c") : QStringLiteral("#ffffff");
        const auto foreground = dark ? QStringLiteral("#e8ebf2") : QStringLiteral("#171a21");
        const auto border = dark ? QStringLiteral("#48515e") : QStringLiteral("#dfe3eb");
        const auto muted = dark ? QStringLiteral("#aeb7c4") : QStringLiteral("#6c7482");
        const bool stale = overlayView_.value("state").toString() == QStringLiteral("Translating")
            && !overlayView_.value("text").toString().isEmpty()
            && !overlayView_.value("presentation").toObject().value("editorOwnsText").toBool();
        const auto editorForeground = stale ? muted : foreground;
        const auto hover = dark ? QStringLiteral("#303741") : QStringLiteral("#f1f3f6");
        const auto accent = dark ? QStringLiteral("#83a7ff") : QStringLiteral("#3267e3");
        const auto editorBorder = editor_ && !editor_->isReadOnly() ? accent : QStringLiteral("transparent");
        // Only our child surface is styled; the host application's palette and
        // the source field's input, focus and geometry remain unchanged.
        auto style = QStringLiteral(
            "QFrame#typelay-overlay { background:%1; color:%2; border:1px solid %3; border-radius:12px; }"
            "QFrame#typelay-overlay QLabel, QFrame#typelay-overlay QRadioButton { color:%2; background:transparent; }"
            "QFrame#typelay-overlay QLabel#typelay-status, QFrame#typelay-overlay QLabel#typelay-hint { color:%4; font-size:12px; }"
            "QFrame#typelay-overlay QPushButton { color:%4; background-color:%1; border:0; border-radius:6px;"
            " padding:4px 6px; min-height:20px; font-size:12px; }"
            "QFrame#typelay-overlay QPushButton:hover { color:%2; background-color:%5; }"
            "QFrame#typelay-overlay QPushButton:pressed { color:%2; background-color:%5; }"
            "QFrame#typelay-overlay QWidget#typelay-footer, QFrame#typelay-overlay QWidget#typelay-menu { border-top:1px solid %3; }"
            "QFrame#typelay-overlay QPlainTextEdit#typelay-editor { background:%1; color:%8; border:1px solid %7;"
            " border-radius:6px; padding:4px; font-size:15px; selection-background-color:%6; selection-color:%1; }")
            .arg(background, foreground, border, muted, hover, accent, editorBorder, editorForeground);
        if (editor_ && editor_->isReadOnly() && overlayView_.value("editable").toBool()
            && overlayView_.value("viewContext").toObject().value("canEdit").toBool())
            style += QStringLiteral("QFrame#typelay-overlay QPlainTextEdit#typelay-editor:hover { border-color:%1; }").arg(border);
        style += QStringLiteral("QFrame#typelay-overlay QPlainTextEdit#typelay-editor:focus { border-color:%1; }").arg(accent);
        if (card_->styleSheet() != style) card_->setStyleSheet(style);
    }
    void refreshSystemTheme() {
        // These synchronous notifications repaint only the currently displayed
        // generation; they neither render text nor renew authority or lifetime.
        if (overlayTheme_ == QStringLiteral("system") && overlayActive()
            && overlayGeneration_ == shortcutConfiguration_.value("generation")) applyOverlayTheme();
    }
    void refreshHint() {
        if (!hint_) return;
        const auto presentation = overlayView_.value("presentation").toObject();
        auto hint = presentation.value("tabHint").toString();
        const auto shortcuts = overlayView_.value("sessionShortcuts").toObject();
        if (presentation.value("editorOwnsText").toBool()) hint = shortcuts.value("returnHint").toString();
        else if (hint == shortcuts.value("acceptHint").toString() || hint == shortcuts.value("passThroughHint").toString())
            hint = shortcuts.value(ready(overlayTarget_, publication_) ? "acceptHint" : "passThroughHint").toString();
        hint_->setText(hint);
    }
    void arrangeOverlay(int editorHeight = -1) {
        if (editorHeight < 0) stopOverlayMotion();
        if (!card_ || !overlayTarget_ || !overlayTarget_->field) return;
        auto *field = overlayTarget_->field.data();
        auto *top = field->window();
        if (card_->parentWidget() != top) { hideOverlay(); return; }
        const bool capsule = paused() || overlayView_.value("state").toString() == "IdleCapsule";
        const int width = qMin(capsule && !menu_->isVisible() ? 160 : qBound(390, field->width(), 520),
            qMax(1, top->width() - 16));
        editor_->setFixedHeight(editorHeight < 0 ? (expanded_ ? 280 : 100) : editorHeight);
        card_->setFixedWidth(width);
        card_->layout()->activate();
        card_->adjustSize();
        const auto bounds = geometry(overlayTarget_).value("bounds").toObject();
        const int x = qBound(0, int(integer(bounds, "x") + integer(bounds, "width")) - width, qMax(0, top->width() - width));
        const int above = int(integer(bounds, "y")) - card_->height() - 6;
        const int y = above >= 0 ? above : int(integer(bounds, "y") + integer(bounds, "height")) + 6;
        card_->move(x, qBound(0, y, qMax(0, top->height() - card_->height())));
    }
    void resizeOverlay() {
        if (!card_ || !editor_ || !overlayActive()) { arrangeOverlay(); return; }
        const int startHeight = editor_->height();
        const QRect start = card_->geometry();
        arrangeOverlay();
        if (!card_) return;
        const QRect end = card_->geometry();
        const int endHeight = editor_->height();
        // The current Qt style is the public platform/application preference.
        // A zero duration disables motion; it is not a universal OS preference API.
        if (startHeight == endHeight || start.width() != end.width()
            || card_->style()->styleHint(QStyle::SH_Widget_Animation_Duration, nullptr, card_) <= 0)
            return;
        const auto bounds = geometry(overlayTarget_);
        const auto fieldBounds = bounds.value("bounds").toObject();
        const int sourceY = int(integer(fieldBounds, "y"));
        const bool above = start.bottom() < sourceY;
        // Keep the same upper bottom-edge or lower top-edge anchor throughout.
        // Near a parent edge a side change or clamp lands directly instead.
        if (above != (end.bottom() < sourceY)
            || (above ? start.bottom() != end.bottom() : start.top() != end.top())) return;
        const QPointer<QWidget> parent = card_->parentWidget();
        const QRect parentBounds = parent->geometry();
        motionRevision_ = overlayRevision_;
        motionSource_ = overlayTarget_->field;
        motionParent_ = parent;
        motionParentBounds_ = parentBounds;
        motionGeometry_ = bounds;
        motionEditor_ = editor_;
        motionDocument_ = editor_->document();
        motionDocumentRevision_ = motionDocument_->revision();
        motionReadOnly_ = editor_->isReadOnly();
        motionFocusPolicy_ = editor_->focusPolicy();
        const auto generation = overlayGeneration_;
        arrangeOverlay(startHeight);
        overlayMotion_.setDuration(280);
        QEasingCurve easing(QEasingCurve::BezierSpline);
        easing.addCubicBezierSegment(QPointF(.22, 1), QPointF(.36, 1), QPointF(1, 1));
        overlayMotion_.setEasingCurve(easing);
        overlayMotion_.setStartValue(startHeight);
        overlayMotion_.setEndValue(endHeight);
        connect(&overlayMotion_, &QVariantAnimation::valueChanged, this,
            [this, parent, parentBounds, bounds, generation](const QVariant &value) {
                if (!overlayActive()) { hideOverlay(); return; }
                if (!parent || card_->parentWidget() != parent || parent->geometry() != parentBounds
                    || overlayRevision_ != motionRevision_ || overlayGeneration_ != generation
                    || editor_ != motionEditor_ || overlayTarget_->field != motionSource_
                    || editor_->document() != motionDocument_ || editor_->document()->revision() != motionDocumentRevision_
                    || editor_->isReadOnly() != motionReadOnly_ || editor_->focusPolicy() != motionFocusPolicy_
                    || geometry(overlayTarget_) != bounds
                    || card_->style()->styleHint(QStyle::SH_Widget_Animation_Duration, nullptr, card_) <= 0) {
                    arrangeOverlay(); return;
                }
                arrangeOverlay(value.toInt());
            });
        // No completion callback: the final value only lays out the current
        // widgets. Nothing can show, restore text, focus or renew a hidden view.
        overlayMotion_.start();
    }
    static QJsonArray languageChoices(const QJsonObject &view, const char *side) {
        const auto value = view.value("languageChoices").toObject().value(QLatin1String(side));
        if (!value.isArray()) return {};
        const auto choices = value.toArray();
        if (choices.isEmpty() || choices.size() > 32) return {};
        QJsonArray result;
        for (const auto &item : choices) {
            if (!item.isObject()) return {};
            const auto choice = item.toObject();
            const auto code = choice.value("code").toString(), label = choice.value("label").toString();
            if (code.isEmpty() || code.size() > 16 || label.isEmpty() || label.size() > 80
                || code.contains(QChar::Null) || label.contains(QChar::Null) || languageIndex(result, code) >= 0) return {};
            result.append(QJsonObject{{"code", code}, {"label", label}});
        }
        return result;
    }
    static int languageIndex(const QJsonArray &choices, const QString &code) {
        for (qsizetype index = 0; index < choices.size(); ++index)
            if (choices[index].toObject().value("code").toString() == code) return int(index);
        return -1;
    }
    static QString languageTitle(const QJsonObject &view, const char *key, const QString &code, const QJsonArray &choices) {
        const auto label = view.value(QLatin1String(key)).toString();
        if (!label.isEmpty() && label.size() <= 80 && !label.contains(QChar::Null)) return label;
        const auto index = languageIndex(choices, code);
        return index >= 0 ? choices[index].toObject().value("label").toString() : code + QStringLiteral("（当前不可用）");
    }
    struct LanguageRender {
        QPointer<QFrame> card;
        QJsonObject view;
        QJsonValue generation;
        qint64 revision;
    };
    LanguageRender languageRender() const { return {card_, overlayView_, overlayGeneration_, overlayRevision_}; }
    bool languageCurrent(const LanguageRender &rendered) const {
        return rendered.card && card_ == rendered.card && overlayView_ == rendered.view
            && overlayGeneration_ == rendered.generation && overlayRevision_ == rendered.revision;
    }
    bool selectLanguage(QPointer<QButtonGroup> group, const QJsonArray &choices, const QString &code,
        const LanguageRender &rendered) {
        if (!group || !languageCurrent(rendered)) return false;
        // An unavailable saved code must never select an arbitrary existing language.
        const bool exclusive = group->exclusive();
        const auto restoreExclusive = qScopeGuard([group, exclusive] { if (group) group->setExclusive(exclusive); });
        group->setExclusive(false);
        QList<QPointer<QAbstractButton>> buttons;
        for (auto *choice : group->buttons()) buttons.append(choice);
        const auto selected = languageIndex(choices, code);
        for (const auto &choice : buttons) {
            if (!choice || !group || !languageCurrent(rendered)) return false;
            choice->setChecked(group->id(choice) == selected);
        }
        return group && languageCurrent(rendered);
    }
    bool resetLanguageSelection() {
        const auto rendered = languageRender();
        QScopedValueRollback<bool> setting(settingLanguages_, true);
        return selectLanguage(sourceLanguages_, sourceChoices_, savedSourceLanguage_, rendered)
            && selectLanguage(targetLanguages_, targetChoices_, savedTargetLanguage_, rendered);
    }
    bool syncLanguageChoices(QPointer<QButtonGroup> group, QPointer<QGridLayout> layout, QJsonArray &previous,
        QString &saved, const QJsonArray &choices, const QString &code, bool source, bool reset,
        const LanguageRender &rendered) {
        if (!group || !layout || !languageCurrent(rendered)) return false;
        const bool savedChanged = saved != code, choicesChanged = previous != choices;
        const int selected = group->checkedId();
        const auto selection = selected >= 0 && selected < previous.size()
            ? previous[selected].toObject().value("code").toString() : QString();
        if (choicesChanged) {
            // Reuse surviving codes, including their actual widget identity.
            // Updating the catalog must not destroy a still-valid focused choice.
            QList<QPointer<QAbstractButton>> oldButtons, retained;
            for (auto *button : group->buttons()) oldButtons.append(button);
            for (qsizetype index = 0; index < choices.size(); ++index) {
                const auto choice = choices[index].toObject();
                const auto name = (source ? QStringLiteral("typelay-source-") : QStringLiteral("typelay-target-"))
                    + choice.value("code").toString();
                QPointer<QAbstractButton> button;
                for (const auto &existing : oldButtons) if (existing && existing->objectName() == name) { button = existing; break; }
                if (!button) {
                    button = new QRadioButton(menu_);
                    if (!button || !group || !layout || !languageCurrent(rendered)) return false;
                    button->setFocusPolicy(Qt::NoFocus);
                    button->setObjectName(name);
                    if (!button || !group || !layout || !languageCurrent(rendered)) return false;
                    group->addButton(button);
                }
                button->setText(choice.value("label").toString());
                if (!button || !group || !layout || !languageCurrent(rendered)) return false;
                group->setId(button, int(index));
                layout->addWidget(button, int(index) / 3, int(index) % 3);
                if (!button || !group || !layout || !languageCurrent(rendered)) return false;
                retained.append(button);
            }
            for (const auto &button : oldButtons) if (button && !retained.contains(button)) {
                group->removeButton(button);
                delete button.data();
                if (!group || !layout || !languageCurrent(rendered)) return false;
            }
            previous = choices;
        }
        saved = code;
        if (reset || savedChanged || choicesChanged)
            return selectLanguage(group, choices, !reset && !savedChanged && languageIndex(choices, selection) >= 0 ? selection : code, rendered);
        return true;
    }
    bool renderLanguages(const QJsonObject &view) {
        const auto rendered = languageRender();
        const bool synchronizing = settingLanguages_;
        QScopedValueRollback<bool> setting(settingLanguages_, true);
        const auto sourceChoices = languageChoices(view, "source"), targetChoices = languageChoices(view, "target");
        const auto source = view.value("sourceLanguage").toString(), target = view.value("targetLanguage").toString();
        const bool valid = !sourceChoices.isEmpty() && !targetChoices.isEmpty();
        // Reentrant echoes must finish the confirmed selection, not retain a
        // partially updated radio group as a user draft.
        const bool reset = synchronizing || menu_->isHidden() || !valid;
        if (!syncLanguageChoices(sourceLanguages_, sourceLanguageLayout_, sourceChoices_, savedSourceLanguage_, sourceChoices, source, true, reset, rendered)
            || !syncLanguageChoices(targetLanguages_, targetLanguageLayout_, targetChoices_, savedTargetLanguage_, targetChoices, target, false, reset, rendered)) return false;
        QList<QPointer<QAbstractButton>> buttons;
        for (auto *group : {sourceLanguages_.data(), targetLanguages_.data()})
            for (auto *choice : group->buttons()) buttons.append(choice);
        for (const auto &choice : buttons) {
            if (!choice || !languageCurrent(rendered)) return false;
            choice->setEnabled(valid);
        }
        if (!languageCurrent(rendered)) return false;
        language_->setText(languageTitle(view, "sourceLanguageLabel", source, sourceChoices)
            + QStringLiteral(" → ") + languageTitle(view, "targetLanguageLabel", target, targetChoices) + QStringLiteral(" ▾"));
        if (!languageCurrent(rendered)) return false;
        swap_->setEnabled(valid && source != QStringLiteral("auto")
            && languageIndex(sourceChoices, target) >= 0 && languageIndex(targetChoices, source) >= 0);
        return languageCurrent(rendered);
    }
    void chooseLanguages(const QString &source, const QString &target) {
        if (settingLanguages_ || paused() || !menu_ || languageIndex(sourceChoices_, source) < 0 || languageIndex(targetChoices_, target) < 0) return;
        const auto menu = menu_;
        const auto card = card_;
        const auto view = overlayView_;
        const auto generation = overlayGeneration_;
        const auto revision = overlayRevision_;
        ui("languages", QJsonObject{{"source", source}, {"target", target}});
        if (card_ != card || menu_ != menu || overlayView_ != view || overlayGeneration_ != generation || overlayRevision_ != revision) return;
        menu_->hide();
        arrangeOverlay();
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
        overlaySourceProvider_ = sessionProvider(target->field) ? target->provider : QPointer<QObject>();
        auto *layout = new QVBoxLayout(frame);
        layout->setContentsMargins(12, 10, 12, 8);
        layout->setSpacing(8);
        auto button = [&](QWidget *parent, QBoxLayout *box, const QString &label, const char *operation) {
            auto *value = new QPushButton(label, parent);
            value->setFocusPolicy(Qt::NoFocus);
            connect(value, &QPushButton::clicked, this, [this, operation, value] {
                if (!insideOverlay(value)) return;
                if (qstrcmp(operation, "resume") == 0) {
                    auto *resumeTarget = overlayTarget_;
                    const auto revision = overlayRevision_;
                    if (value != resume_ || !paused() || pendingResume_) return;
                    if (!overlayActive() || !pauseCurrent(resumeTarget, overlayAuthority_)) { hideOverlay(); return; }
                    if (overlayTarget_ != resumeTarget || overlayRevision_ != revision || !paused() || !overlayActive()) return;
                    pendingResume_ = true;
                    resume_->setEnabled(false);
                    ui("resume");
                    return;
                }
                if (paused()) return;
                if (qstrcmp(operation, "menu") == 0) {
                    if (menu_->isHidden() && !resetLanguageSelection()) return;
                    menu_->setVisible(menu_->isHidden());
                } else if (qstrcmp(operation, "expand") == 0) {
                    menu_->hide();
                    expanded_ = !expanded_;
                    value->setText(expanded_ ? QStringLiteral("缩小") : QStringLiteral("展开"));
                    ui("expand", expanded_);
                    resizeOverlay();
                    return;
                } else {
                    menu_->hide();
                    if (qstrcmp(operation, "copy") == 0) {
                        if (editor_) QApplication::clipboard()->setText(editor_->toPlainText());
                    } else ui(operation);
                }
                arrangeOverlay();
            });
            box->addWidget(value);
            return value;
        };
        auto *header = new QHBoxLayout;
        language_ = button(frame, header, QString(), "menu");
        language_->setObjectName(QStringLiteral("typelay-menu-trigger"));
        language_->setAccessibleName(QStringLiteral("语言与更多操作"));
        language_->setToolTip(QStringLiteral("语言与更多操作"));
        status_ = new QLabel(frame);
        status_->setObjectName(QStringLiteral("typelay-status"));
        status_->setWordWrap(true);
        status_->setAlignment(Qt::AlignRight | Qt::AlignVCenter);
        header->addStretch(); header->addWidget(status_);
        tools_ = new QWidget(frame);
        auto *toolsLayout = new QHBoxLayout(tools_);
        toolsLayout->setContentsMargins(0, 0, 0, 0);
        toolsLayout->setSpacing(2);
        button(tools_, toolsLayout, QStringLiteral("重译"), "regenerate");
        button(tools_, toolsLayout, expanded_ ? QStringLiteral("缩小") : QStringLiteral("展开"), "expand")->setObjectName(QStringLiteral("typelay-expand"));
        header->addWidget(tools_);
        resume_ = button(frame, header, QStringLiteral("恢复"), "resume");
        resume_->setObjectName(QStringLiteral("typelay-resume"));
        resume_->hide();
        layout->addLayout(header);
        auto *translationEditor = new TranslationEditor(frame);
        editor_ = translationEditor;
        connect(translationEditor, &TranslationEditor::inputRejected, this, [this] {
            if (hint_) hint_->setText(QStringLiteral("译文超过 8192 字符限制，此次插入未生效"));
        });
        editor_->setObjectName(QStringLiteral("typelay-editor"));
        editor_->setAccessibleName(QStringLiteral("译文"));
        editor_->setReadOnly(true);
        editor_->setFocusPolicy(Qt::NoFocus);
        editor_->setTabChangesFocus(false);
        // Keep the default unlimited maximumBlockCount. Setting even zero disables native undo.
        layout->addWidget(editor_);
        connect(editor_, &QPlainTextEdit::textChanged, this, [this] {
            if (settingText_ || !editor_ || editor_->isReadOnly()) return;
            revokeEditorOrdered();
            const auto value = editor_->toPlainText();
            if (validText(value)) ui("draft", value);
            else if (hint_) hint_->setText(QStringLiteral("译文超过 8192 字符限制，请缩短后再返回"));
        });
        footer_ = new QWidget(frame);
        footer_->setObjectName(QStringLiteral("typelay-footer"));
        auto *footerLayout = new QHBoxLayout(footer_);
        footerLayout->setContentsMargins(0, 6, 0, 0);
        hint_ = new QLabel(footer_);
        hint_->setObjectName(QStringLiteral("typelay-hint"));
        hint_->setWordWrap(true);
        footerLayout->addWidget(hint_); footerLayout->addStretch();
        exit_ = button(footer_, footerLayout, QString(), "escape");
        layout->addWidget(footer_);
        menu_ = new QWidget(frame);
        menu_->setObjectName(QStringLiteral("typelay-menu"));
        auto *menuLayout = new QVBoxLayout(menu_);
        menuLayout->setContentsMargins(0, 8, 0, 0);
        // Keep choices inside the source window without a popup/focus grab.
        // Direction changes still belong to the host's existing settings path.
        for (const bool source : {true, false}) {
            menuLayout->addWidget(new QLabel(source ? QStringLiteral("源语言") : QStringLiteral("目标语言"), menu_));
            auto *row = new QGridLayout;
            auto *group = new QButtonGroup(menu_);
            if (source) { sourceLanguages_ = group; sourceLanguageLayout_ = row; }
            else { targetLanguages_ = group; targetLanguageLayout_ = row; }
            connect(group, &QButtonGroup::idClicked, this, [this, source, group](int index) {
                if (!menu_ || group != (source ? sourceLanguages_ : targetLanguages_)) return;
                const auto &choices = source ? sourceChoices_ : targetChoices_;
                if (index < 0 || index >= choices.size()) return;
                const int from = sourceLanguages_->checkedId(), to = targetLanguages_->checkedId();
                if (from < 0 || from >= sourceChoices_.size() || to < 0 || to >= targetChoices_.size()) return;
                chooseLanguages(sourceChoices_[from].toObject().value("code").toString(),
                    targetChoices_[to].toObject().value("code").toString());
            });
            menuLayout->addLayout(row);
        }
        swap_ = new QPushButton(QStringLiteral("交换语言"), menu_);
        swap_->setObjectName(QStringLiteral("typelay-swap"));
        swap_->setFocusPolicy(Qt::NoFocus);
        connect(swap_, &QPushButton::clicked, this, [this] {
            const auto source = overlayView_.value("sourceLanguage").toString();
            if (source != QStringLiteral("auto")) chooseLanguages(overlayView_.value("targetLanguage").toString(), source);
        });
        menuLayout->addWidget(swap_);
        button(menu_, menuLayout, QStringLiteral("复制译文"), "copy")->setObjectName(QStringLiteral("typelay-copy"));
        button(menu_, menuLayout, QStringLiteral("不要在这个应用中显示"), "exclude");
        button(menu_, menuLayout, QStringLiteral("设置…"), "settings");
        layout->addWidget(menu_);
        menu_->hide();
    }
    QJsonObject overlayCommand(const QString &op, Target *target, const QJsonObject &data) {
        if (op == "overlay-hide") hideOverlay();
        else if (op == "overlay-render") {
            if (!viewCurrent(target, data)) return {{"visible", false}, {"editing", false}};
            const auto view = data.value("view").toObject();
            const auto presentation = view.value("presentation").toObject();
            const auto basis = data.value("authority").toObject();
            const qint64 until = integer(data, "expiresAtMs", 0), current = now();
            if (overlayTarget_ == target && integer(data, "revision", 0) < overlayRevision_)
                return {{"visible", overlayActive()}, {"editing", editor_ && !editor_->isReadOnly()}};
            const bool isPaused = view.value("state").toString() == "Paused";
            const bool retainEditor = !isPaused && target && target == overlayTarget_
                && presentation.value("editorOwnsText").toBool() && target->field
                && integer(basis, "inputRevision") == qint64(target->input)
                && integer(basis, "contentRevision") == qint64(target->content);
            const bool valid = metadataMatches(target, basis) && presentation.value("visible").toBool()
                && until >= current && until - current <= 1000
                && (isPaused ? pauseCurrent(target, basis) : retainEditor || (inspect(target) && clear(target->state)))
                && (focused() == target || (!isPaused && target == overlayTarget_ && overlayFocused_))
                && target->field->window()->isActiveWindow() && !geometry(target).isEmpty() && viewCurrent(target, data);
            if (!valid) hideOverlay();
            else {
                const bool continueMotion = overlayMotion_.state() == QAbstractAnimation::Running
                    && integer(data, "visualExpansionFrom", -1) == overlayRevision_
                    && integer(data, "revision", -1) == overlayRevision_ + 1
                    && target == overlayTarget_ && target->field == motionSource_ && editor_ == motionEditor_
                    && editor_ && editor_->document() == motionDocument_ && editor_->document()->revision() == motionDocumentRevision_
                    && editor_->isReadOnly() == motionReadOnly_ && editor_->focusPolicy() == motionFocusPolicy_
                    && card_ && card_->parentWidget() == motionParent_ && motionParent_
                    && motionParent_->geometry() == motionParentBounds_ && target->field->window() == motionParent_
                    && geometry(target) == motionGeometry_ && data.value("generation") == overlayGeneration_
                    && view == overlayView_ && data.value("theme").toString(QStringLiteral("system")) == overlayTheme_;
                if (!card_ || overlayTarget_ != target) { hideOverlay(); createOverlay(target); }
                if (overlayRevision_ != integer(data, "revision", 0) || overlayView_ != view) revokeEditorOrdered();
                if (overlayRevision_ != integer(data, "revision", 0)) pendingResume_ = false;
                overlayView_ = view; overlayAuthority_ = basis; overlayGeneration_ = data.value("generation");
                if (focused() == target && sessionProvider(target->field)) overlaySourceProvider_ = target->provider;
                exit_->setText(view.value("sessionShortcuts").toObject().value("exitHint").toString());
                overlayUntil_ = until; overlayRevision_ = integer(data, "revision", 0);
                const bool editing = !isPaused && presentation.value("editorOwnsText").toBool()
                    && view.value("viewContext").toObject().value("canEdit").toBool();
                const QString text = isPaused ? QString() : view.value("text").toString();
                {
                    QScopedValueRollback<bool> setting(settingText_, true);
                    if (editor_->toPlainText() != text) {
                        // Once editing starts, the native editor owns its text,
                        // caret and undo history. A delayed draft echo is not an edit.
                        const bool retained = editing && !editor_->isReadOnly();
                        if (qEnvironmentVariableIsSet("TYPELAY_QT_PROBE")) {
                            const auto *counter = retained ? "tlqtEditorOwnedRenderSkips" : "tlqtEditorProgrammaticSets";
                            qApp->setProperty(counter, qApp->property(counter).toInt() + 1);
                        }
                        if (!retained) editor_->setPlainText(text);
                    }
                }
                editor_->setReadOnly(!editing);
                editor_->setFocusPolicy(editing ? Qt::StrongFocus : Qt::NoFocus);
                const bool editable = !isPaused && view.value("editable").toBool()
                    && view.value("viewContext").toObject().value("canEdit").toBool();
                const auto editHint = editing ? QStringLiteral("编辑译文")
                    : editable ? QStringLiteral("点击译文以编辑") : QString();
                editor_->setToolTip(editHint);
                editor_->setAccessibleDescription(editHint);
                if (!renderLanguages(view)) return {{"visible", overlayActive()}, {"editing", editor_ && !editor_->isReadOnly()}};
                status_->setText(isPaused ? QStringLiteral("已暂停") : view.value("status").toString());
                refreshHint();
                const bool capsule = isPaused || view.value("state").toString() == "IdleCapsule";
                editor_->setVisible(!capsule); footer_->setVisible(!capsule); tools_->setVisible(!capsule);
                status_->setVisible(isPaused || !capsule);
                language_->setVisible(!isPaused); resume_->setVisible(isPaused); resume_->setEnabled(!pendingResume_);
                if (isPaused) menu_->hide();
                // Only the exact one-use expansion echo may continue geometry.
                // All current view/authority/edit state above has already applied.
                if (continueMotion) motionRevision_ = overlayRevision_;
                arrangeOverlay(continueMotion ? editor_->height() : -1);
                if (!card_) return {{"visible", false}, {"editing", false}};
                overlayTheme_ = data.value("theme").toString(QStringLiteral("system"));
                applyOverlayTheme();
                card_->show(); card_->raise(); syncOverlay();
            }
        } else if (op == "overlay-renew") {
            if (!sessionApplied() || data.value("generation") != shortcutConfiguration_.value("generation"))
                return {{"visible", false}, {"editing", false}};
            const qint64 until = integer(data, "expiresAtMs", 0), current = now();
            if (target == overlayTarget_ && overlayActive() && sessionApplied()
                && data.value("generation") == shortcutConfiguration_.value("generation")
                && data.value("generation") == overlayGeneration_ && until >= current && until - current <= 1000
                && metadataMatches(target, data.value("authority").toObject()) && !geometry(target).isEmpty()
                && (!paused() || pauseCurrent(target, data.value("authority").toObject()))
                && data.value("generation") == shortcutConfiguration_.value("generation")
                && data.value("generation") == overlayGeneration_) {
                overlayUntil_ = until;
                overlayAuthority_ = data.value("authority").toObject();
            }
            else hideOverlay();
        } else if (op == "overlay-edit") {
            if (!overlayCurrent(data)) return {{"visible", overlayActive()}, {"editing", editor_ && !editor_->isReadOnly()}};
            if (target == overlayTarget_ && overlayActive() && overlayCurrent(data) && pendingEdit_ && data.value("allowed").toBool()
                && overlayView_.value("state").toString() == "EditingTranslation" && !editor_->isReadOnly()) editor_->setFocus();
            pendingEdit_ = false;
            syncOverlay();
        } else if (op == "overlay-resume" && target == overlayTarget_ && overlayActive() && overlayCurrent(data) && !paused()) {
            editor_->setReadOnly(false); editor_->setFocusPolicy(Qt::StrongFocus); editor_->setFocus(); syncOverlay();
        }
        return {{"visible", overlayActive()}, {"editing", editor_ && !editor_->isReadOnly()}};
    }
    bool eventFilter(QObject *object, QEvent *event) override {
        if (!onThread()) return false;
        if (object == qApp && event->type() == QEvent::ApplicationPaletteChange) refreshSystemTheme();
        if (editor_ && (object == editor_ || object == editor_->viewport())) {
            if (event->type() == QEvent::KeyPress) {
                const auto *key = static_cast<QKeyEvent *>(event);
                const bool prefix = editorOrdered_ && editorOrdered_->editor == object
                    && editorOrdered_->provider == editor_->property(ProviderProperty).value<QObject *>()
                    && key->key() == Qt::Key_Control && !key->isAutoRepeat()
                    && key->nativeScanCode() == editorOrdered_->ctrlScan
                    && key->timestamp() == editorOrdered_->ctrlTimestamp;
                if (!prefix) revokeEditorOrdered();
            } else if (event->type() == QEvent::InputMethod || event->type() == QEvent::MouseButtonPress
                || event->type() == QEvent::Wheel || event->type() == QEvent::TouchBegin
                || event->type() == QEvent::FocusOut || event->type() == QEvent::Hide
                || event->type() == QEvent::EnabledChange || event->type() == QEvent::ReadOnlyChange) revokeEditorOrdered();
            if (object == editor_ && event->type() == QEvent::DynamicPropertyChange
                && static_cast<QDynamicPropertyChangeEvent *>(event)->propertyName() == ProviderProperty
                && editor_->property(ProviderProperty).value<QObject *>() != overlaySourceProvider_) {
                // A lost/replaced editor route cannot revive from a later property
                // restore. Only a newly verified source route can authorize it again.
                overlaySourceProvider_ = nullptr;
                revokeEditorOrdered();
                clearPermit();
            }
            if (event->type() == QEvent::MouseButtonPress && editor_->isReadOnly() && overlayActive()) {
                if (!paused() && sessionCurrent(overlayGeneration_)
                    && overlayView_.value("viewContext").toObject().value("canEdit").toBool()
                    && overlayView_.value("editable").toBool() && !pendingEdit_
                    && overlayTarget_ && inspect(overlayTarget_) && clear(overlayTarget_->state)
                    && sessionCurrent(overlayGeneration_) && sessionProvider(overlayTarget_->field)) {
                    pendingEdit_ = true; ui("beginEdit"); return true;
                }
            }
            if (event->type() == QEvent::InputMethod) {
                const bool composing = !static_cast<QInputMethodEvent *>(event)->preeditString().isEmpty();
                if (composing != composing_) { composing_ = composing; ui("composition", composing); }
            }
        }
        if (insideOverlay(object)) return false;
        auto *widget = qobject_cast<QWidget *>(object);
        auto *target = discover(widget);
        if (!target && widget) {
            auto *plain = qobject_cast<QPlainTextEdit *>(widget->parentWidget());
            if (plain && plain->viewport() == widget) target = discover(plain);
        }
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
        } else if (type == QEvent::KeyPress) {
            const auto *key = static_cast<QKeyEvent *>(event);
            if (ordered_ && ordered_->target == target && key->key() == Qt::Key_Control
                && key->nativeScanCode() == ordered_->ctrlScan && key->timestamp() == ordered_->ctrlTimestamp
                && !key->isAutoRepeat()) loseProof();
            else if (modifierKey(key->key())) clearPermit();
            else invalidate(target, false, false);
        } else if (type == QEvent::MouseButtonPress || type == QEvent::Wheel
            || type == QEvent::TouchBegin) invalidate(target, false, false);
        if (type == QEvent::DynamicPropertyChange) bind(target);
        if (type == QEvent::Hide || type == QEvent::EnabledChange || type == QEvent::ReadOnlyChange) {
            invalidate(target, false, false);
            if (target == overlayTarget_) hideOverlay();
        }
        return false; // Ordinary Qt/Fcitx input is never replayed or swallowed here.
    }
    void returnSource(Target *target, const QJsonObject &basis, const QJsonValue &bindingGeneration, qint64 id) {
        if (!sessionCurrent(bindingGeneration)) { respond(id, QJsonObject{{"returned", false}}); return; }
        clearPermit();
        const bool allowed = sessionCurrent(bindingGeneration) && overlayGeneration_ == bindingGeneration
            && target && target == overlayTarget_ && overlayActive() && overlayFocused_
            && sessionBindings(target).value("editorReady").toBool() && metadataMatches(target, basis) && target->input == quint64(integer(basis, "inputRevision", 0))
            && target->content == quint64(integer(basis, "contentRevision", 0))
            && overlayView_.value("presentation").toObject().value("editorOwnsText").toBool();
        if (!allowed) { respond(id, QJsonObject{{"returned", false}}); return; }
        const QPointer<QWidget> source = target->field;
        const QPointer<QPlainTextEdit> editor = editor_;
        const QPointer<QFrame> card = card_;
        TypeLayFcitxQtStateV1 editorState{};
        if (!widgetClear(editor, &editorState) || !source || !editor || target->field != source
            || editor_ != editor || card_ != card || !overlayActive() || overlayTarget_ != target
            || !editor->hasFocus() || !metadataMatches(target, basis)
            || target->input != quint64(integer(basis, "inputRevision", 0))
            || target->content != quint64(integer(basis, "contentRevision", 0))
            || !sessionCurrent(bindingGeneration) || overlayGeneration_ != bindingGeneration || !sessionProvider(editor)
            || !sessionBindings(target).value("editorReady").toBool()) {
            respond(id, QJsonObject{{"returned", false}}); return;
        }
        source->setFocus(Qt::OtherFocusReason);
        const auto generation = ++returnGeneration_;
        const auto connection = connectionGeneration_;
        const qint64 until = now() + 450;
        auto *timer = new QTimer(this);
        connect(timer, &QTimer::timeout, this, [this, timer, target, basis, id, until, generation, connection, bindingGeneration] {
            const bool same = generation == returnGeneration_ && connection == connectionGeneration_
                && sessionCurrent(bindingGeneration)
                && target->field && focused() == target && metadataMatches(target, basis)
                && target->input == quint64(integer(basis, "inputRevision", 0))
                && target->content == quint64(integer(basis, "contentRevision", 0));
            const auto originalStamp = basis.value("contextStamp").toObject();
            const bool done = same && inspect(target) && clear(target->state)
                && qint64(target->state.contextGeneration) == integer(originalStamp, "context")
                && qint64(target->state.fieldGeneration) == integer(originalStamp, "field")
                && qint64(target->state.inputEpoch) == integer(originalStamp, "input")
                && target->input == quint64(integer(basis, "inputRevision", 0))
                && local(target, target->state) && sessionCurrent(bindingGeneration);
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
        else if (op == "direct") { revokeEditorOrdered(); respond(id, write(target, data, true)); }
        else if (op == "return") returnSource(target, data.value("authority").toObject(), data.value("generation"), id);
        else if (op == "quarantine") { revokeEditorOrdered(); ++authorizationGeneration_; if (target) target->quarantined = true; cancelShortcut(); clearPermit(); respond(id); }
        else if (op == "revoke") {
            ++authorizationGeneration_; clearPermit();
            if (integer(data, "shortcuts", 0)) { revokeEditorOrdered(); cancelShortcut(); shortcutAuthority_ = {}; }
            respond(id);
        }
        else if (op == "publish") {
            if (!sessionCurrent(data.value("generation"))) { respond(id, QJsonObject{{"published", false}}); return; }
            const bool renewal = sameOrderedPublication(data);
            if (!renewal) { ++authorizationGeneration_; clearPermit(); }
            else loseProof();
            if (target && sessionCurrent(data.value("generation")) && focused() == target
                && metadataMatches(target, data.value("authority").toObject()) && inspect(target) && clear(target->state)
                && sessionCurrent(data.value("generation")) && sessionProvider(target->field)) publication_ = data;
            const auto state = publicationState(target);
            respond(id, QJsonObject{{"published", !publication_.isEmpty()
                && (data.value("permit").isNull() || data.value("permit").isUndefined() || state.value("ready").toBool())}});
            if (target) { auto report = state; report.insert("target", target->id); sendEvent("publication", report); }
            refreshHint();
        } else if (op == "configure-shortcuts") {
            ++authorizationGeneration_; ++returnGeneration_;
            clearPermit(); hideOverlay();
            if (data.value("generation") != shortcutConfiguration_.value("generation")) shortcutStartedAt_ = -1;
            shortcutConfiguration_ = data;
            cancelShortcut();
            shortcutAuthority_ = {};
            QVariantList keys, orderedKeys;
            if (data.value("enabled").toBool()) for (const auto *kind : {"wake", "direct"}) {
                const auto symbol = integer(data.value(QLatin1String(kind)).toObject(), "keyval", 0);
                if (symbol > 0 && symbol <= std::numeric_limits<quint32>::max()) {
                    keys.append(quint32(symbol));
                    if (symbol >= 'A' && symbol <= 'Z') keys.append(quint32(symbol - 'A' + 'a'));
                    else if (symbol >= 'a' && symbol <= 'z') keys.append(quint32(symbol - 'a' + 'A'));
                }
            }
            if (sessionCurrent(data.value("generation"))) {
                keys.append(sessionSymbol(sessionKey(data.value("accept"), true)));
                keys.append(sessionSymbol(sessionKey(data.value("exit"), false)));
                for (const auto *action : {"accept", "exit"})
                    if (orderedBinding(data.value(QLatin1String(action))))
                        orderedKeys.append(sessionSymbol(sessionKey(data.value(QLatin1String(action)), qstrcmp(action, "accept") == 0)));
            }
            qApp->setProperty(TypeLayFcitxQtControlKeysPropertyV1, keys);
            qApp->setProperty(TypeLayFcitxQtOrderedKeysPropertyV1, orderedKeys);
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
                && triggerShortcut(data.value("kind").toString(), target, data.value("authority").toObject(),
                    integer(data, "triggeredAtMs"))}});
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
        cancelShortcut(nullptr, "PlatformFailure", false);
        ++connectionGeneration_;
        if (reader_) { reader_->setEnabled(false); reader_->deleteLater(); reader_ = nullptr; }
        if (writer_) { writer_->setEnabled(false); writer_->deleteLater(); writer_ = nullptr; }
        if (fd_ >= 0) { ::close(fd_); fd_ = -1; }
        outgoing_.clear(); incoming_.clear();
        shortcutAuthority_ = {}; shortcutConfiguration_ = {};
        qApp->setProperty(TypeLayFcitxQtControlKeysPropertyV1, QVariantList{});
        qApp->setProperty(TypeLayFcitxQtOrderedKeysPropertyV1, QVariantList{});
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
    QVariantAnimation overlayMotion_;
    QPointer<QWidget> motionSource_, motionParent_;
    QPointer<QPlainTextEdit> motionEditor_;
    QPointer<QTextDocument> motionDocument_;
    int motionDocumentRevision_ = 0;
    bool motionReadOnly_ = true;
    Qt::FocusPolicy motionFocusPolicy_ = Qt::NoFocus;
    QRect motionParentBounds_;
    QJsonObject motionGeometry_;
    qint64 motionRevision_ = 0;
    int fd_ = -1;
    QPointer<QSocketNotifier> reader_, writer_;
    QByteArray incoming_, outgoing_;
    QJsonObject publication_, overlayView_, overlayAuthority_, shortcutConfiguration_, shortcutAuthority_;
    QString overlayTheme_ = QStringLiteral("system");
    std::optional<ShortcutRequest> pendingShortcut_;
    std::shared_ptr<OrderedTicket> ordered_;
    std::shared_ptr<EditorOrderedTicket> editorOrdered_;
    quint64 shortcutSequence_ = 0;
    qint64 shortcutStartedAt_ = -1;
    quint64 connectionGeneration_ = 0, returnGeneration_ = 0, authorizationGeneration_ = 0, focusGeneration_ = 0;
    bool dispatching_ = false, handlingKey_ = false, advancingShortcut_ = false;
    QPointer<QFrame> card_;
    QPointer<QPlainTextEdit> editor_;
    QPointer<QObject> overlaySourceProvider_;
    QPointer<QLabel> hint_, status_;
    QPointer<QPushButton> language_, swap_, resume_, exit_;
    QPointer<QButtonGroup> sourceLanguages_, targetLanguages_;
    QPointer<QGridLayout> sourceLanguageLayout_, targetLanguageLayout_;
    QJsonArray sourceChoices_, targetChoices_;
    QString savedSourceLanguage_, savedTargetLanguage_;
    QPointer<QWidget> footer_, menu_, tools_;
    Target *overlayTarget_ = nullptr;
    qint64 overlayUntil_ = 0, overlayRevision_ = 0;
    QJsonValue overlayGeneration_;
    bool overlayFocused_ = false, composing_ = false, settingText_ = false, pendingEdit_ = false, expanded_ = false;
    bool pendingResume_ = false, settingLanguages_ = false;
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
