#include <QApplication>
#include <QClipboard>
#include <QCryptographicHash>
#include <QFrame>
#include <QGenericPlugin>
#include <QInputMethodEvent>
#include <QJsonDocument>
#include <QJsonObject>
#include <QKeyEvent>
#include <QLabel>
#include <QLineEdit>
#include <QMouseEvent>
#include <QLocalSocket>
#include <QPlainTextEdit>
#include <QPalette>
#include <QPointer>
#include <QPushButton>
#include <QTextBlock>
#include <QTextDocument>
#include <QTextEdit>
#include <QTextFragment>
#include <QTextFrame>
#include <QTimer>
#include <QUuid>
#include <QVBoxLayout>
#include <QtEndian>
#include <sys/socket.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>
#include <algorithm>
#include <memory>
#include <typeinfo>
#include <vector>

namespace {
constexpr int MaxText = 8192, MaxFrame = 256 * 1024;
qint64 nowMs() {
    timespec value{};
    return clock_gettime(CLOCK_BOOTTIME, &value) == 0
        ? qint64(value.tv_sec) * 1000 + value.tv_nsec / 1000000 : INT64_MAX;
}
QString string(const QJsonObject &o, const char *key) { return o.value(QLatin1String(key)).toString(); }
qint64 number(const QJsonObject &o, const char *key, qint64 fallback = -1) {
    auto value = o.value(QLatin1String(key));
    return value.isDouble() && value.toDouble() == double(value.toInteger(fallback)) ? value.toInteger(fallback) : fallback;
}
QJsonObject object(const QJsonObject &o, const char *key) { return o.value(QLatin1String(key)).toObject(); }
bool validText(const QString &text) {
    if (text.size() > MaxText || text.contains(QChar(0))) return false;
    for (qsizetype i = 0; i < text.size(); ++i) {
        if (text[i].isHighSurrogate()) { if (++i >= text.size() || !text[i].isLowSurrogate()) return false; }
        else if (text[i].isLowSurrogate()) return false;
    }
    return true;
}
bool boundary(const QString &text, int at) {
    return at >= 0 && at <= text.size() && !(at > 0 && at < text.size()
        && text[at - 1].isHighSurrogate() && text[at].isLowSurrogate());
}
// Installed while QGuiApplication constructs the generic plugins. Retain only event
// provenance until QApplication and its widgets are available; never read text here.
struct EarlyState { QPointer<QWidget> widget; bool known; bool composing; };
class StartupObserver final : public QObject {
public:
    std::vector<EarlyState> states;
    explicit StartupObserver(QObject *parent) : QObject(parent) { parent->installEventFilter(this); }
    bool eventFilter(QObject *watched, QEvent *event) override {
        auto *w = qobject_cast<QWidget *>(watched);
        if (!w) return false;
        auto found = std::find_if(states.begin(), states.end(), [w](const EarlyState &s) { return s.widget == w; });
        if (event->type() == QEvent::Polish && found == states.end() && states.size() < 2048) {
            states.push_back({w, !w->hasFocus(), false});
        } else if (event->type() == QEvent::InputMethod && found != states.end()) {
            found->known = false; found->composing = !static_cast<QInputMethodEvent *>(event)->preeditString().isEmpty();
        }
        return false;
    }
};
struct Target {
    QPointer<QWidget> widget;
    QPointer<QTextDocument> document;
    QString id = QUuid::createUuid().toString(QUuid::WithoutBraces);
    qint64 content = 1, input = 1, focus = 1;
    bool known = false, composing = false, quarantined = false, mutating = false;
};
class Adapter final : public QObject {
public:
    explicit Adapter(QApplication *app, const std::vector<EarlyState> &early) : QObject(app), socket(this), timer(this) {
        app->installEventFilter(this);
        connect(&socket, &QLocalSocket::connected, this, [this] {
            ucred peer{}; socklen_t size = sizeof(peer);
            if (getsockopt(int(socket.socketDescriptor()), SOL_SOCKET, SO_PEERCRED, &peer, &size) || peer.uid != getuid()) { disconnectPeer(); return; }
            sendEvent("hello", {{"protocol", 2}, {"backend", "qt6"}, {"pid", qint64(getpid())}, {"lifetime", lifetime}});
        });
        connect(&socket, &QLocalSocket::readyRead, this, [this] { readFrames(); });
        connect(&socket, &QLocalSocket::disconnected, this, [this] { disconnectPeer(); });
        connect(app, &QApplication::focusChanged, this, [this](QWidget *old, QWidget *current) {
            if (auto *t = find(old)) invalidate(t, false, true);
            if (auto *t = find(current)) invalidate(t, false, true);
            syncOverlay();
            if (overlayTarget && focused() && focused() != overlayTarget) hideOverlay();
        });
        connect(&timer, &QTimer::timeout, this, [this] {
            connectPeer();
            if (overlay && (!overlayTarget || !overlayTarget->widget || !overlayTarget->widget->window()->isActiveWindow()
                || nowMs() > overlayExpires || geometry(overlayTarget).isEmpty())) hideOverlay();
            else if (overlay) positionOverlay();
        });
        timer.start(100);
        // Existing widgets can already have received IME events: late discovery stays Unknown.
        for (auto *widget : QApplication::allWidgets()) discover(widget, false);
        for (const auto &state : early) if (auto *t = find(state.widget)) { t->known = state.known; t->composing = state.composing; }
        connectPeer();
    }
private:
    QLocalSocket socket;
    QTimer timer;
    QByteArray incoming;
    QString lifetime = QUuid::createUuid().toString(QUuid::WithoutBraces);
    std::vector<std::unique_ptr<Target>> targets;
    QJsonObject publication, shortcuts, shortcutAuthority;
    QStringList consumed;
    int heldKey = 0, editPosition = 0;
    bool heldConsumed = false;
    QPointer<QFrame> overlay;
    QPointer<QPlainTextEdit> editor;
    QPointer<QLabel> status, hint;
    QPointer<QWidget> footer;
    QPointer<QPushButton> language;
    Target *overlayTarget = nullptr;
    QJsonObject view;
    qint64 overlayExpires = 0, overlayRevision = 0;
    bool settingText = false, pendingEdit = false, overlayFocused = false, overlayComposing = false, expanded = false;

    Target *find(QWidget *widget) const {
        for (const auto &target : targets) if (target->widget == widget && widget) return target.get();
        return nullptr;
    }
    Target *lookup(const QString &id) const {
        for (const auto &target : targets) if (target->id == id && target->widget) return target.get();
        return nullptr;
    }
    bool inOverlay(QWidget *widget) const { return overlay && widget && (widget == overlay || overlay->isAncestorOf(widget)); }
    Target *focused() const {
        auto *widget = QApplication::focusWidget();
        return widget && widget->window()->isActiveWindow() ? find(widget) : nullptr;
    }
    QTextDocument *document(QWidget *w) const {
        if (auto *e = qobject_cast<QPlainTextEdit *>(w)) return e->document();
        if (auto *e = qobject_cast<QTextEdit *>(w)) return e->document();
        return nullptr;
    }
    void discover(QWidget *w, bool beforeInput) {
        if (!w || inOverlay(w) || find(w) || targets.size() >= 2048) return;
        if (typeid(*w) != typeid(QLineEdit) && typeid(*w) != typeid(QPlainTextEdit) && typeid(*w) != typeid(QTextEdit)) return;
        auto item = std::make_unique<Target>();
        auto *t = item.get(); t->widget = w; t->known = beforeInput && !w->hasFocus(); t->document = document(w);
        targets.push_back(std::move(item));
        connect(w, &QObject::destroyed, this, [this, t] {
            publication = {}; shortcutAuthority = {};
            if (overlayTarget == t) hideOverlay();
            t->widget = nullptr; sendEvent("destroy", {{"target", t->id}});
        });
        if (auto *e = qobject_cast<QLineEdit *>(w)) {
            connect(e, &QLineEdit::textChanged, this, [this, t] { invalidate(t, true, false); });
            connect(e, &QLineEdit::selectionChanged, this, [this, t] { invalidate(t, false, false); });
            connect(e, &QLineEdit::cursorPositionChanged, this, [this, t] { invalidate(t, false, false); });
        } else if (auto *e = qobject_cast<QPlainTextEdit *>(w)) {
            connect(e, &QPlainTextEdit::textChanged, this, [this, t] { invalidate(t, true, false); });
            connect(e, &QPlainTextEdit::selectionChanged, this, [this, t] { invalidate(t, false, false); });
            connect(e, &QPlainTextEdit::cursorPositionChanged, this, [this, t] { invalidate(t, false, false); });
        } else if (auto *e = qobject_cast<QTextEdit *>(w)) {
            connect(e, &QTextEdit::textChanged, this, [this, t] { invalidate(t, true, false); });
            connect(e, &QTextEdit::selectionChanged, this, [this, t] { invalidate(t, false, false); });
            connect(e, &QTextEdit::cursorPositionChanged, this, [this, t] { invalidate(t, false, false); });
        }
    }
    void invalidate(Target *t, bool text, bool focus) {
        if (!t || !t->widget) return;
        if (t->mutating) { if (text) ++t->content; if (focus) ++t->focus; return; }
        if (focus) { ++t->focus; heldKey = 0; heldConsumed = false; shortcutAuthority = {}; }
        else ++t->input;
        if (text) ++t->content;
        publication = {};
        sendEvent("input", {{"target", t->id}, {"textChanged", text}, {"focusChanged", focus}, {"focused", focused() == t},
            {"inputRevision", t->input}, {"contentRevision", t->content}, {"focusRevision", t->focus}});
    }
    bool supported(Target *t) const {
        if (!t || !t->widget || !t->widget->testAttribute(Qt::WA_WState_Created)) return false;
        if (qobject_cast<QLineEdit *>(t->widget)) return true;
        auto *doc = document(t->widget);
        if (!doc || doc != t->document || typeid(*doc) != typeid(QTextDocument) || doc->maximumBlockCount() != 0 || !doc->rootFrame()->childFrames().isEmpty()) return false;
        for (auto block = doc->begin(); block.isValid(); block = block.next())
            for (auto it = block.begin(); !it.atEnd(); ++it)
                if (it.fragment().isValid() && it.fragment().charFormat().objectType() != QTextFormat::NoObject) return false;
        if (auto *edit = qobject_cast<QTextEdit *>(t->widget)) {
            if (edit->acceptRichText()) return false;
            for (auto block = doc->begin(); block.isValid(); block = block.next()) {
                if (!block.blockFormat().properties().isEmpty() || block.textList()) return false;
                for (auto it = block.begin(); !it.atEnd(); ++it)
                    if (it.fragment().isValid() && !it.fragment().charFormat().properties().isEmpty()) return false;
            }
        }
        return true;
    }
    QJsonObject metadata(Target *t) const {
        if (!supported(t)) return {};
        QWidget *w = t->widget;
        bool password = bool(w->inputMethodHints() & (Qt::ImhHiddenText | Qt::ImhSensitiveData));
        bool readOnly = true;
        if (auto *e = qobject_cast<QLineEdit *>(w)) { password |= e->echoMode() != QLineEdit::Normal; readOnly = e->isReadOnly() || e->validator() || !e->inputMask().isEmpty(); }
        else if (auto *e = qobject_cast<QPlainTextEdit *>(w)) readOnly = e->isReadOnly();
        else if (auto *e = qobject_cast<QTextEdit *>(w)) readOnly = e->isReadOnly();
        readOnly |= bool(w->inputMethodHints() & (Qt::ImhDigitsOnly | Qt::ImhFormattedNumbersOnly
            | Qt::ImhDialableCharactersOnly | Qt::ImhEmailCharactersOnly | Qt::ImhUrlCharactersOnly));
        return {{"process", ""}, {"label", w->accessibleName()}, {"automationId", w->objectName()},
            {"help", w->accessibleDescription() + " " + w->toolTip()}, {"aria", QString::fromLatin1(w->metaObject()->className())},
            {"password", password}, {"enabled", w->isEnabled()}, {"readOnly", readOnly}, {"offscreen", !w->isVisible()},
            {"inDocument", false}, {"multiline", !qobject_cast<QLineEdit *>(w)}, {"hasValue", true}, {"hasTextEdit", t->known}};
    }
    QString metadataKey(const QJsonObject &m) const {
        return QString::fromLatin1(QCryptographicHash::hash(QJsonDocument(m).toJson(QJsonDocument::Compact), QCryptographicHash::Sha256).toHex());
    }
    bool matches(Target *t, const QJsonObject &basis) const {
        auto m = metadata(t);
        return !m.isEmpty() && string(basis, "target") == t->id && string(basis, "metadataKey") == metadataKey(m)
            && !m["password"].toBool() && !m["readOnly"].toBool() && m["enabled"].toBool();
    }
    QJsonObject authority(Target *t, const QJsonObject &m) const {
        return {{"target", t->id}, {"metadataKey", metadataKey(m)}, {"contentRevision", t->content},
            {"inputRevision", t->input}, {"focusRevision", t->focus}, {"observedAtMs", nowMs()}};
    }
    QJsonObject geometry(Target *t) const {
        if (!t || !t->widget || !t->widget->isVisible()) return {};
        QWidget *w = t->widget, *top = w->window();
        QRect bounds(w->mapTo(top, QPoint()), w->size());
        for (auto *p = w->parentWidget(); p; p = p->parentWidget()) {
            if (!p->isVisible()) return {};
            bounds &= QRect(p->mapTo(top, QPoint()), p->size());
            if (p == top) break;
        }
        bounds &= top->rect();
        if (bounds.isEmpty()) return {};
        return {{"space", "qt-toplevel"}, {"bounds", QJsonObject{{"x", bounds.x()}, {"y", bounds.y()}, {"width", bounds.width()}, {"height", bounds.height()}}},
            {"frameWidth", top->width()}, {"frameHeight", top->height()}, {"scale", top->devicePixelRatioF()}};
    }
    QJsonObject describe(Target *t) const {
        auto m = metadata(t); if (m.isEmpty()) return {};
        return {{"target", t->id}, {"metadata", m}, {"authority", authority(t, m)}, {"geometry", geometry(t)}, {"focused", focused() == t}};
    }
    QString text(Target *t, int &start, int &end) const {
        if (auto *e = qobject_cast<QLineEdit *>(t->widget)) {
            start = e->hasSelectedText() ? e->selectionStart() : e->cursorPosition();
            end = start + e->selectedText().size(); return e->text();
        }
        QTextCursor cursor = qobject_cast<QPlainTextEdit *>(t->widget)
            ? qobject_cast<QPlainTextEdit *>(t->widget)->textCursor() : qobject_cast<QTextEdit *>(t->widget)->textCursor();
        start = cursor.selectionStart(); end = cursor.selectionEnd();
        // toPlainText normalizes NBSP into spaces. Keep every UTF-16 unit, only
        // mapping Qt's one-unit paragraph/line delimiters to protocol newlines.
        QString value = t->document->toRawText();
        value.replace(QChar::ParagraphSeparator, QChar('\n'));
        value.replace(QChar::LineSeparator, QChar('\n'));
        return value;
    }
    QJsonObject capture(Target *t, const QJsonObject &basis) const {
        auto age = nowMs() - number(basis, "observedAtMs", 0);
        if (age < 0 || age > 150 || !matches(t, basis) || !t->known || t->composing) return {};
        int start, end; QString value = text(t, start, end);
        if (!validText(value)) return {};
        auto result = describe(t);
        result["text"] = value; result["selection"] = QJsonObject{{"start", start}, {"end", end}};
        result["ime"] = !t->known ? "Unknown" : t->composing ? "Composing" : "Clear";
        result["writeSupported"] = t->known && !t->composing && !t->quarantined;
        result["observedAtMs"] = nowMs(); return result;
    }
    QJsonObject receipt(const char *statusValue, const char *reason) const { return {{"status", statusValue}, {"reason", reason}}; }
    QJsonObject write(Target *t, const QJsonObject &request, bool direct) {
        const auto plan = object(request, "plan"), basis = object(request, "authority"), expected = object(plan, "expected");
        const auto approved = direct ? object(request, "approved") : basis;
        if (!t || plan.isEmpty() || expected.isEmpty() || basis.isEmpty() || approved.isEmpty()) return receipt("RejectedBeforeMutation", "BadPlan");
        qint64 now = nowMs(), until = number(request, "expiresAtMs", 0);
        if (until < now || until - now > (direct ? 250 : 100)) return receipt("RejectedBeforeMutation", "StaleObservation");
        if (!matches(t, basis) || !matches(t, approved)) return receipt("RejectedBeforeMutation", "Sensitive");
        if (!t->known || t->composing || t->quarantined) return receipt("RejectedBeforeMutation", "ImeUnclear");
        if (t->content != number(basis, "contentRevision")) return receipt("RejectedBeforeMutation", "SourceChanged");
        if (!direct && (focused() != t || t->input != number(basis, "inputRevision") || t->focus != number(basis, "focusRevision"))) return receipt("RejectedBeforeMutation", "EpochChanged");
        int start, end; QString original = text(t, start, end);
        auto selection = object(expected, "selection");
        if (original != string(expected, "text")) return receipt("RejectedBeforeMutation", "SourceChanged");
        if (!direct && (start != number(selection, "start") || end != number(selection, "end"))) return receipt("RejectedBeforeMutation", "SelectionChanged");
        auto scope = object(plan, "scope");
        const qint64 firstNumber = scope.isEmpty() ? 0 : number(scope, "start"), lastNumber = scope.isEmpty() ? original.size() : number(scope, "end");
        if (firstNumber < 0 || lastNumber < firstNumber || lastNumber > original.size() || (!scope.isEmpty() && firstNumber == lastNumber)) return receipt("RejectedBeforeMutation", "BadPlan");
        int first = int(firstNumber), last = int(lastNumber);
        QString replacement = string(plan, "replacement"), result = string(plan, "resultText");
        if (!validText(original) || replacement.isEmpty() || !validText(replacement) || !validText(result)
            || !boundary(original, first) || !boundary(original, last) || original.left(first) + replacement + original.mid(last) != result
            || number(plan, "caret") != first + replacement.size()) return receipt("RejectedBeforeMutation", "BadPlan");
        if (auto *e = qobject_cast<QLineEdit *>(t->widget)) {
            if (result.size() > e->maxLength() || replacement.contains('\n') || replacement.contains('\r')) return receipt("RejectedBeforeMutation", "InvalidText");
        }
        // All checks and insertion remain on the application's UI thread, without pumping events.
        // QPointer and post-verification detect synchronous application callbacks, with no retry.
        QPointer<QWidget> widget = t->widget; QPointer<QTextDocument> doc = t->document;
        const int caret = first + int(replacement.size());
        t->mutating = true; publication = {};
        if (auto *e = qobject_cast<QLineEdit *>(widget)) {
            e->setSelection(first, last - first);
            // Selection callbacks may mutate/delete the widget synchronously.
            int a, b;
            if (widget && t->content == number(basis, "contentRevision")
                && (direct || (focused() == t && t->focus == number(basis, "focusRevision")))
                && matches(t, approved) && matches(t, basis) && text(t, a, b) == original && a == first && b == last
                && t->known && !t->composing && nowMs() <= until)
                e->insert(replacement);
        } else {
            QTextCursor cursor(doc); cursor.setPosition(first); cursor.setPosition(last, QTextCursor::KeepAnchor);
            cursor.beginEditBlock(); cursor.insertText(replacement); cursor.endEditBlock();
            if (widget && doc == document(widget)) {
                if (auto *e = qobject_cast<QPlainTextEdit *>(widget)) e->setTextCursor(cursor);
                else if (auto *e = qobject_cast<QTextEdit *>(widget)) e->setTextCursor(cursor);
            }
        }
        t->mutating = false; ++t->content;
        bool verified = widget && doc == t->document && matches(t, approved) && t->known && !t->composing;
        QString after;
        if (verified) { after = text(t, start, end); verified = after == result && start == caret && end == caret; }
        auto reply = receipt(verified ? "Verified" : "OutcomeUnknown", verified ? "None" : "UnknownOutcome");
        if (!verified) t->quarantined = true;
        else { auto snapshot = expected; snapshot["text"] = after; snapshot["selection"] = QJsonObject{{"start", caret}, {"end", caret}};
            snapshot["ime"] = "Clear"; snapshot["access"] = "Supported"; snapshot["observedAtMs"] = nowMs(); reply["verifiedSnapshot"] = snapshot; }
        return reply;
    }
    bool returnSource(Target *t, const QJsonObject &basis) {
        if ((overlayTarget == t && overlayComposing) || !matches(t, basis) || !t->known || t->composing
            || t->content != number(basis, "contentRevision") || !t->widget->window()->isActiveWindow()) return false;
        t->widget->setFocus(Qt::OtherFocusReason); return focused() == t;
    }
    void send(const QJsonObject &frame) {
        if (socket.state() != QLocalSocket::ConnectedState) return;
        auto bytes = QJsonDocument(frame).toJson(QJsonDocument::Compact);
        if (bytes.isEmpty() || bytes.size() > MaxFrame || socket.bytesToWrite() + bytes.size() > 4 * MaxFrame) { disconnectPeer(); return; }
        quint32 length = qToLittleEndian(quint32(bytes.size()));
        socket.write(reinterpret_cast<const char *>(&length), 4); socket.write(bytes);
    }
    void sendEvent(const char *event, const QJsonObject &data) { send({{"event", event}, {"data", data}}); }
    void disconnectPeer() {
        publication = {}; shortcutAuthority = {}; shortcuts = {}; incoming.clear();
        hideOverlay(); if (socket.state() != QLocalSocket::UnconnectedState) socket.abort();
    }
    void connectPeer() {
        if (socket.state() != QLocalSocket::UnconnectedState) return;
        QString runtime = qEnvironmentVariable("XDG_RUNTIME_DIR"); if (!runtime.startsWith('/')) return;
        auto identity = qgetenv("WAYLAND_DISPLAY") + '\n' + qgetenv("NIRI_SOCKET");
        QString path = runtime + "/typelay/" + QString::fromLatin1(QCryptographicHash::hash(identity, QCryptographicHash::Sha256).toHex().left(16)) + "/native.sock";
        struct stat info{}; auto encoded = path.toLocal8Bit();
        if (lstat(encoded.constData(), &info) || !S_ISSOCK(info.st_mode) || info.st_uid != getuid() || (info.st_mode & 0077)) return;
        socket.connectToServer(path, QIODevice::ReadWrite);
    }
    void readFrames() {
        incoming += socket.readAll();
        if (incoming.size() > 2 * MaxFrame) { disconnectPeer(); return; }
        while (incoming.size() >= 4) {
            quint32 length = qFromLittleEndian<quint32>(incoming.constData());
            if (!length || length > MaxFrame) { disconnectPeer(); return; }
            if (incoming.size() < length + 4) return;
            QJsonParseError error{};
            auto frame = QJsonDocument::fromJson(incoming.mid(4, length), &error);
            incoming.remove(0, length + 4);
            if (error.error != QJsonParseError::NoError || !frame.isObject()) { disconnectPeer(); return; }
            command(frame.object());
        }
    }
    void ui(const char *operation, QJsonValue value = QJsonValue(QJsonValue::Null)) {
        if (overlayTarget) sendEvent("ui", {{"target", overlayTarget->id}, {"operation", operation}, {"revision", overlayRevision}, {"value", value}});
    }
    void syncOverlay() {
        bool focus = overlay && overlay->isVisible() && inOverlay(QApplication::focusWidget());
        if (focus != overlayFocused) { overlayFocused = focus; ui("focus", focus); }
    }
    void hideOverlay() {
        if (!overlay) { overlayTarget = nullptr; return; }
        if (overlayFocused) ui("focus", false);
        if (overlayComposing) ui("composition", false);
        ui("visible", false);
        auto old = overlay; overlay = nullptr; editor = nullptr; overlayTarget = nullptr;
        overlayFocused = overlayComposing = pendingEdit = expanded = false;
        old->hide(); old->deleteLater();
    }
    void positionOverlay() {
        if (!overlay || !overlayTarget || !overlayTarget->widget) return;
        auto g = geometry(overlayTarget); if (g.isEmpty()) { hideOverlay(); return; }
        auto bounds = object(g, "bounds"); QWidget *top = overlayTarget->widget->window();
        int x = int(number(bounds, "x")) + int(number(bounds, "width")) - overlay->width();
        int y = int(number(bounds, "y")) - overlay->height() - 4;
        if (y < 0) y = int(number(bounds, "y")) + int(number(bounds, "height")) + 4;
        overlay->move(qBound(0, x, qMax(0, top->width() - overlay->width())), qBound(0, y, qMax(0, top->height() - overlay->height())));
        overlay->raise();
    }
    QPushButton *button(QBoxLayout *layout, const QString &label, const char *operation) {
        auto *b = new QPushButton(label, overlay); b->setFocusPolicy(Qt::NoFocus); layout->addWidget(b);
        connect(b, &QPushButton::clicked, this, [this, operation] {
            if (qstrcmp(operation, "copy") == 0) { if (editor) QApplication::clipboard()->setText(editor->toPlainText()); }
            else if (qstrcmp(operation, "expand") == 0) {
                expanded = !expanded;
                if (editor && overlay) { editor->setFixedHeight(expanded ? 320 : 130); overlay->adjustSize(); positionOverlay(); }
            }
            else ui(operation);
        }); return b;
    }
    void createOverlay(Target *t) {
        overlayTarget = t;
        overlay = new QFrame(t->widget->window()); overlay->setObjectName("typelay-overlay");
        overlay->setFrameShape(QFrame::StyledPanel); overlay->setAutoFillBackground(true);
        auto *layout = new QVBoxLayout(overlay); layout->setContentsMargins(12, 8, 12, 8);
        auto *header = new QHBoxLayout; layout->addLayout(header);
        language = button(header, "", "settings"); status = new QLabel(overlay); header->addWidget(status);
        editor = new QPlainTextEdit(overlay); editor->setObjectName("typelay-candidate");
        editor->setReadOnly(true); editor->setFocusPolicy(Qt::NoFocus); editor->setTabChangesFocus(true); layout->addWidget(editor);
        connect(editor, &QPlainTextEdit::textChanged, this, [this] {
            if (!settingText && editor && !editor->isReadOnly()) ui("draft", editor->toPlainText());
        });
        hint = new QLabel(overlay); layout->addWidget(hint);
        footer = new QWidget(overlay); auto *actions = new QHBoxLayout(footer); layout->addWidget(footer);
        button(actions, QStringLiteral("隐藏"), "escape"); button(actions, QStringLiteral("重译"), "regenerate");
        button(actions, QStringLiteral("展开"), "expand"); button(actions, QStringLiteral("复制"), "copy");
        button(actions, QStringLiteral("排除应用"), "exclude");
    }
    QJsonObject overlayCommand(const QString &op, Target *t, const QJsonObject &data) {
        if (op == "overlay-hide") hideOverlay();
        else if (op == "overlay-render" || op == "overlay-renew") {
            qint64 until = number(data, "expiresAtMs", 0), now = nowMs();
            auto candidate = object(data, "view"), presentation = object(candidate, "presentation");
            bool eligible = t && matches(t, object(data, "authority")) && t->known && !t->composing && !t->quarantined
                && until >= now && until - now <= 1000 && t->widget->window()->isActiveWindow()
                && (focused() == t || (overlayTarget == t && overlayFocused)) && !geometry(t).isEmpty();
            if (op == "overlay-render") eligible &= presentation["visible"].toBool();
            else eligible &= overlay && overlayTarget == t;
            if (!eligible) hideOverlay();
            else {
                if (overlayTarget != t || !overlay || overlay->parentWidget() != t->widget->window()) { hideOverlay(); createOverlay(t); }
                overlayExpires = until;
                if (op == "overlay-render") {
                    QPalette palette = QApplication::palette();
                    const QString theme = string(data, "theme");
                    if (theme == QLatin1String("light") || theme == QLatin1String("dark")) {
                        const bool dark = theme == QLatin1String("dark");
                        const QColor background(dark ? "#25282e" : "#ffffff");
                        const QColor foreground(dark ? "#f2f3f5" : "#202329");
                        for (QPalette::ColorRole role : {QPalette::Window, QPalette::Base, QPalette::Button}) palette.setColor(role, background);
                        for (QPalette::ColorRole role : {QPalette::WindowText, QPalette::Text, QPalette::ButtonText}) palette.setColor(role, foreground);
                        palette.setColor(QPalette::Highlight, QColor(dark ? "#557dad" : "#c3defb"));
                        palette.setColor(QPalette::HighlightedText, foreground);
                    }
                    overlay->setPalette(palette);
                    overlayRevision = number(data, "revision", 0); view = candidate;
                    bool editing = string(view, "state") == "EditingTranslation";
                    if (!(presentation["editorOwnsText"].toBool() && !editor->isReadOnly())) {
                        settingText = true; editor->setPlainText(string(view, "text")); settingText = false;
                    }
                    editor->setReadOnly(!editing); editor->setFocusPolicy(editing ? Qt::StrongFocus : Qt::NoFocus);
                    language->setText(string(view, "sourceLanguage") + QStringLiteral(" → ") + string(view, "targetLanguage"));
                    status->setText(string(view, "status")); hint->setText(string(presentation, "tabHint"));
                    bool capsule = string(view, "state") == "IdleCapsule";
                    editor->setVisible(!capsule); hint->setVisible(!capsule); status->setVisible(!capsule); footer->setVisible(!capsule);
                    editor->setFixedHeight(expanded ? 320 : 130);
                    overlay->setFixedWidth(qMin(capsule ? 240 : qBound(390, t->widget->width(), 520), t->widget->window()->width()));
                    overlay->adjustSize(); overlay->show(); positionOverlay(); syncOverlay();
                }
            }
        } else if (op == "overlay-edit") {
            if (overlay && overlayTarget == t && pendingEdit && data["allowed"].toBool()
                && string(view, "state") == "EditingTranslation" && t->widget->window()->isActiveWindow()) {
                auto cursor = editor->textCursor(); cursor.setPosition(qBound(0, editPosition, editor->document()->characterCount() - 1));
                editor->setTextCursor(cursor); editor->setFocus(Qt::OtherFocusReason);
            }
            pendingEdit = false; syncOverlay();
        } else if (op == "overlay-resume" && overlay && overlayTarget == t) status->setText(QStringLiteral("无法返回原输入框，请点击译文继续编辑"));
        return {{"visible", overlay && overlay->isVisible()}, {"editing", editor && !editor->isReadOnly()}};
    }
    bool triggerShortcut(const QString &kind, Target *t, const QJsonObject &basis) {
        auto age = nowMs() - number(basis, "observedAtMs", 0);
        if ((kind != "wake" && kind != "direct") || !shortcuts["enabled"].toBool() || !t || t != focused()
            || !t->known || t->composing || t->quarantined || age < 0 || age > 150 || !matches(t, basis)
            || t->input != number(basis, "inputRevision") || t->focus != number(basis, "focusRevision") || t->content != number(basis, "contentRevision")) return false;
        auto read = capture(t, basis); if (read.isEmpty()) return false;
        publication = {}; shortcutAuthority = {};
        sendEvent("shortcut", {{"kind", kind}, {"generation", string(shortcuts, "generation")}, {"native", read}, {"acceptedAtMs", nowMs()}});
        return socket.state() == QLocalSocket::ConnectedState;
    }
    void command(const QJsonObject &frame) {
        QString op = string(frame, "op"); auto data = object(frame, "data");
        auto *t = data.contains("target") ? lookup(string(data, "target")) : focused();
        QJsonValue response(QJsonValue::Null);
        if (op.startsWith("overlay-")) response = overlayCommand(op, t, data);
        else if (op == "describe") { auto d = describe(t); if (!d.isEmpty()) response = d; }
        else if (op == "capture") { auto d = capture(t, object(data, "authority")); if (!d.isEmpty()) response = d; }
        else if (op == "direct") {
            QString id = string(object(data, "plan"), "id");
            if (id.isEmpty() || consumed.contains(id)) response = receipt("RejectedBeforeMutation", "BadPlan");
            else { consumed.append(id); while (consumed.size() > 128) consumed.removeFirst(); response = write(t, data, true); }
        }
        else if (op == "return") response = QJsonObject{{"returned", returnSource(t, object(data, "authority"))}};
        else if (op == "quarantine") { if (t) t->quarantined = true; publication = {}; shortcutAuthority = {}; }
        else if (op == "configure-shortcuts") { shortcuts = data; shortcutAuthority = {}; }
        else if (op == "publish-shortcut") {
            shortcutAuthority = {};
            if (t == focused() && matches(t, object(data, "authority")) && string(data, "generation") == string(shortcuts, "generation")) shortcutAuthority = data;
        } else if (op == "trigger-shortcut") {
            auto basis = object(data, "authority"); auto age = nowMs() - number(data, "triggeredAtMs", 0);
            bool allowed = !shortcutAuthority.isEmpty() && age >= 0 && age <= 150 && string(data, "generation") == string(shortcuts, "generation")
                && string(shortcutAuthority, "target") == (t ? t->id : QString());
            response = QJsonObject{{"triggered", allowed && triggerShortcut(string(data, "kind"), t, basis)}};
        } else if (op == "revoke") { publication = {}; if (number(data, "shortcuts", 0)) shortcutAuthority = {}; }
        else if (op == "publish") {
            publication = {}; if (t == focused() && matches(t, object(data, "authority"))) publication = data;
            response = QJsonObject{{"published", !publication.isEmpty()}};
        } else { disconnectPeer(); return; }
        auto id = number(frame, "id", 0); if (id > 0) send({{"id", id}, {"data", response}});
    }
    bool keyEvent(QKeyEvent *key) {
        bool down = key->type() == QEvent::KeyPress;
        if (heldKey == key->key()) {
            bool result = heldConsumed;
            if (!down && !key->isAutoRepeat()) { heldKey = 0; heldConsumed = false; }
            return result;
        }
        if (!down || key->isAutoRepeat() || !key->spontaneous()) return false;
        if (overlayFocused && !overlayComposing && key->modifiers() == Qt::NoModifier
            && (key->key() == Qt::Key_Tab || key->key() == Qt::Key_Escape)) {
            if (key->key() == Qt::Key_Tab && (!editor || editor->isReadOnly())) return false;
            ui(key->key() == Qt::Key_Tab ? "return" : "escape"); heldKey = key->key(); heldConsumed = true; return true;
        }
        auto *t = focused();
        if (!t || !t->known || t->composing || t->quarantined) return false;
        // Desktop configuration uses GDK keyvals/masks for letters, F1-F35 and modifiers.
        quint32 modifiers = 0;
        if (key->modifiers() & Qt::ShiftModifier) modifiers |= 1;
        if (key->modifiers() & Qt::ControlModifier) modifiers |= 4;
        if (key->modifiers() & Qt::AltModifier) modifiers |= 8;
        if (key->modifiers() & Qt::MetaModifier) modifiers |= 1u << 26;
        const int keyval = key->key() >= Qt::Key_A && key->key() <= Qt::Key_Z ? key->key()
            : key->key() >= Qt::Key_F1 && key->key() <= Qt::Key_F35 ? 65470 + key->key() - Qt::Key_F1 : 0;
        if (keyval && !(key->modifiers() & (Qt::KeypadModifier | Qt::GroupSwitchModifier)) && !shortcutAuthority.isEmpty()) {
            for (const char *kind : {"wake", "direct"}) {
                auto binding = object(shortcuts, kind);
                if (keyval == number(binding, "keyval")
                    && modifiers == number(binding, "modifiers") && triggerShortcut(QLatin1String(kind), t, object(shortcutAuthority, "authority"))) {
                    heldKey = key->key(); heldConsumed = true; return true;
                }
            }
        }
        if (key->modifiers() != Qt::NoModifier || (key->key() != Qt::Key_Tab && key->key() != Qt::Key_Escape) || publication.isEmpty()) return false;
        auto basis = object(publication, "authority");
        if (!matches(t, basis) || t->input != number(basis, "inputRevision") || t->focus != number(basis, "focusRevision")) { publication = {}; return false; }
        if (key->key() == Qt::Key_Escape) {
            auto lease = object(publication, "lease"); auto age = nowMs() - number(object(lease, "source"), "observedAtMs", 0);
            publication = {};
            if (!lease["escapeAllowed"].toBool() || age < 0 || age > 150) return false;
            sendEvent("escape", {}); heldKey = key->key(); heldConsumed = true; return true;
        }
        auto permit = object(publication, "permit"), plan = object(permit, "plan"); QString id = string(plan, "id");
        publication = {};
        if (id.isEmpty() || consumed.contains(id)) return false;
        consumed.append(id); while (consumed.size() > 128) consumed.removeFirst();
        auto stamp = nowMs();
        auto reply = write(t, {{"plan", plan}, {"authority", basis}, {"expiresAtMs", number(permit, "expiresAtMs", 0)}}, false);
        if (string(reply, "status") == "RejectedBeforeMutation") return false;
        sendEvent("accepted", {{"planId", id}, {"acceptedAtMs", stamp}, {"receipt", reply}});
        heldKey = key->key(); heldConsumed = true; return true;
    }
    bool eventFilter(QObject *watched, QEvent *event) override {
        auto *w = qobject_cast<QWidget *>(watched);
        if (!w) return false;
        if (event->type() == QEvent::Polish) discover(w, true);
        if (inOverlay(w)) {
            if (event->type() == QEvent::InputMethod && (w == editor || (editor && editor->isAncestorOf(w)))) {
                // Public filters run before the editor handles the IME event. There is
                // no reliable after-notify boundary under processEvents reentrancy.
                overlayComposing = true; ui("composition", true);
            }
            if (event->type() == QEvent::MouseButtonPress && editor && (w == editor || w == editor->viewport()) && editor->isReadOnly()) {
                if (view["editable"].toBool() && !pendingEdit) {
                    const auto *mouse = static_cast<QMouseEvent *>(event);
                    auto point = w == editor->viewport() ? mouse->position().toPoint() : editor->viewport()->mapFrom(w, mouse->position().toPoint());
                    editPosition = editor->cursorForPosition(point).position(); pendingEdit = true; ui("beginEdit");
                }
                return true;
            }
        }
        if (event->type() == QEvent::KeyPress || event->type() == QEvent::KeyRelease) {
            if (keyEvent(static_cast<QKeyEvent *>(event))) return true;
            if (event->type() == QEvent::KeyPress && !inOverlay(w)) invalidate(focused(), false, false);
        }
        if (auto *t = find(w)) {
            if (event->type() == QEvent::InputMethod) {
                // No public post-dispatch boundary exists for arbitrary application
                // processEvents() reentrancy. Once an IME event arrives, this target
                // remains Unknown for its lifetime; never infer Clear from a timer.
                t->known = false;
                t->composing = !static_cast<QInputMethodEvent *>(event)->preeditString().isEmpty();
                invalidate(t, false, false);
            } else if (event->type() == QEvent::MouseButtonPress || event->type() == QEvent::Wheel) invalidate(t, false, false);
            else if (event->type() == QEvent::WindowDeactivate) { invalidate(t, false, true); hideOverlay(); }
        }
        if (overlay && event->type() == QEvent::WindowDeactivate && w == overlay->window()) hideOverlay();
        return false;
    }
};
}

class TypeLayPlugin final : public QGenericPlugin {
    Q_OBJECT
    Q_PLUGIN_METADATA(IID "org.qt-project.Qt.QGenericPluginFactoryInterface" FILE "typelay-qt6.json")
public:
    QObject *create(const QString &key, const QString &) override {
        if (key.compare(QStringLiteral("typelay"), Qt::CaseInsensitive) != 0) return nullptr;
        // QGenericPlugin is constructed during QGuiApplication initialization, before QApplication.
        auto *lifetime = new StartupObserver(QCoreApplication::instance());
        QTimer::singleShot(0, lifetime, [lifetime] {
            if (auto *app = qobject_cast<QApplication *>(QCoreApplication::instance())) {
                if (!app->property("typelay-adapter-installed").toBool()) {
                    app->setProperty("typelay-adapter-installed", true); new Adapter(app, lifetime->states);
                }
            }
            QCoreApplication::instance()->removeEventFilter(lifetime);
            lifetime->states.clear();
        });
        return lifetime;
    }
};
#include "typelay-qt6.moc"
