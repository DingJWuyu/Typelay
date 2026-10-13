// Real standard widgets with synthetic test content. No production diagnostics.
#include <QApplication>
#include <QAbstractButton>
#include <QClipboard>
#include <QIntValidator>
#include <QInputMethodEvent>
#include <QEventLoop>
#include <QDynamicPropertyChangeEvent>
#include <QHash>
#include <QJsonDocument>
#include <QJsonObject>
#include <QKeyEvent>
#include <QLabel>
#include <QLineEdit>
#include <QPlainTextDocumentLayout>
#include <QPlainTextEdit>
#include <QPointer>
#include <QTextBlock>
#include <QTextDocument>
#include <QTextFrame>
#include <QTextList>
#include <QSaveFile>
#include <QSocketNotifier>
#include <QTimer>
#include <QToolTip>
#include <QVBoxLayout>
#include <fcntl.h>
#include <unistd.h>

class KeyObserver final : public QObject {
    Q_OBJECT
public:
    QLineEdit *first = nullptr, *second = nullptr;
    QPlainTextEdit *plain = nullptr;
    QJsonObject counts;
    bool firstPreedit = false, secondPreedit = false, plainPreedit = false, editorPreedit = false;
    QString shortcutState, commandQueuedAction, commandDispatchedAction;
    quint64 shortcutId = 0, shortcutActionCount = 0, shortcutActionId = 0, focusTransitions = 0;
    void applyCommandAction(QString &action) {
        const auto operation = action;
        action.clear();
        if (operation.isEmpty()) return;
        ++shortcutActionCount;
        shortcutActionId = shortcutId;
        if (operation == "rewrite") first->setText(QStringLiteral("Application changed this field"));
        else if (operation.startsWith("focus-second")) {
            second->setFocus();
            if (operation == "focus-second-rewrite") first->setText(QStringLiteral("Application changed this field"));
            else if (operation == "focus-second-sensitive") first->setAccessibleName(QStringLiteral("Password"));
        }
    }
public slots:
    void shortcutChanged(const QString &state, quint64 id) {
        shortcutState = state;
        shortcutId = id;
        if (state == "queued") applyCommandAction(commandQueuedAction);
        if (state == "dispatched") applyCommandAction(commandDispatchedAction);
    }
public:
    bool eventFilter(QObject *object, QEvent *event) override {
        if (object == qApp && event->type() == QEvent::DynamicPropertyChange
            && static_cast<QDynamicPropertyChangeEvent *>(event)->propertyName() == "typelay.qt.native.v1") {
            auto *native = qApp->property("typelay.qt.native.v1").value<QObject *>();
            if (native) connect(native, SIGNAL(shortcutStateChangedV1(QString,quint64)),
                this, SLOT(shortcutChanged(QString,quint64)), Qt::UniqueConnection);
        }
        if (event->type() == QEvent::InputMethod) {
            const bool composing = !static_cast<QInputMethodEvent *>(event)->preeditString().isEmpty();
            if (object == first) firstPreedit = composing;
            else if (object == second) secondPreedit = composing;
            else if (object == plain) plainPreedit = composing;
            else if (object->objectName() == QStringLiteral("typelay-editor")) editorPreedit = composing;
        }
        if (object != first && object != second && object != plain) return false;
        if (event->type() == QEvent::FocusIn || event->type() == QEvent::FocusOut) {
            const QString focusName = (object == first ? QStringLiteral("first")
                : object == second ? QStringLiteral("second") : QStringLiteral("plain"))
                + (event->type() == QEvent::FocusIn ? QStringLiteral("FocusIn") : QStringLiteral("FocusOut"));
            counts.insert(focusName, counts.value(focusName).toInt() + 1);
        }
        if (event->type() != QEvent::KeyPress && event->type() != QEvent::KeyRelease) return false;
        const auto *key = static_cast<QKeyEvent *>(event);
        const QString name = (object == first ? QStringLiteral("first")
            : object == second ? QStringLiteral("second") : QStringLiteral("plain"))
            + (key->key() == Qt::Key_Tab ? QStringLiteral("Tab") : QStringLiteral("Key"))
            + (event->type() == QEvent::KeyPress ? QStringLiteral("Press") : QStringLiteral("Release"));
        counts.insert(name, counts.value(name).toInt() + 1);
        return false;
    }
};

int main(int argc, char **argv) {
    QApplication app(argc, argv);
    QWidget window;
    window.setWindowTitle(QStringLiteral("TypeLay Qt native fixture"));
    auto *layout = new QVBoxLayout(&window);
    auto *first = new QLineEdit(&window);
    auto *second = new QLineEdit(&window);
    auto *plain = new QPlainTextEdit(&window);
    plain->setObjectName(QStringLiteral("plain"));
    plain->setAccessibleName(QStringLiteral("Multiline message"));
    plain->setMaximumHeight(140);
    KeyObserver observer;
    observer.first = first; observer.second = second; observer.plain = plain;
    app.installEventFilter(&observer);
    QObject::connect(&app, &QApplication::focusChanged, &observer, [&observer] { ++observer.focusTransitions; });
    first->setObjectName(QStringLiteral("first"));
    second->setObjectName(QStringLiteral("second"));
    first->setAccessibleName(QStringLiteral("Message one"));
    second->setAccessibleName(QStringLiteral("Message two"));
    auto *firstLabel = new QLabel(QStringLiteral("Message one"), &window);
    auto *secondLabel = new QLabel(QStringLiteral("Message two"), &window);
    firstLabel->setBuddy(first);
    secondLabel->setBuddy(second);
    auto *plainLabel = new QLabel(QStringLiteral("Multiline message"), &window);
    plainLabel->setBuddy(plain);
    layout->addWidget(firstLabel);
    layout->addWidget(first);
    layout->addWidget(secondLabel);
    layout->addWidget(second);
    layout->addWidget(plainLabel);
    layout->addWidget(plain);
    plainLabel->hide(); plain->hide();
    layout->addStretch();
    window.resize(700, 500);
    window.show();
    first->setFocus();
    int firstChanges = 0, secondChanges = 0, firstEdits = 0, secondEdits = 0;
    QJsonObject firstChangeContext;
    QObject::connect(first, &QLineEdit::textChanged, [&] {
        ++firstChanges;
        // Synthetic fixture observation only: record what really owned focus
        // and whether B was composing at the instant A's body changed.
        firstChangeContext = {{"firstChanges", firstChanges}, {"secondFocused", second->hasFocus()},
            {"secondText", second->text()}, {"secondPreedit", observer.secondPreedit},
            {"focusTransitions", qint64(observer.focusTransitions)},
            {"firstFocusIn", observer.counts.value("firstFocusIn")},
            {"firstFocusOut", observer.counts.value("firstFocusOut")}};
    });
    QObject::connect(second, &QLineEdit::textChanged, [&] { ++secondChanges; });
    QObject::connect(first, &QLineEdit::textEdited, [&] { ++firstEdits; });
    QObject::connect(second, &QLineEdit::textEdited, [&] { ++secondEdits; });
    int plainChanges = 0, plainContentsChanges = 0, plainDocumentGeneration = 0;
    QJsonObject plainLastChange;
    QPointer<QTextDocument> observedDocument;
    QList<QMetaObject::Connection> plainConnections;
    auto observeDocument = [&] {
        if (observedDocument == plain->document()) return;
        for (const auto &connection : plainConnections) QObject::disconnect(connection);
        plainConnections.clear();
        observedDocument = plain->document();
        ++plainDocumentGeneration;
        plainConnections.append(QObject::connect(observedDocument, &QTextDocument::contentsChange,
            [&](int position, int removed, int added) {
                ++plainContentsChanges;
                plainLastChange = {{"position", position}, {"removed", removed}, {"added", added}};
            }));
    };
    observeDocument();
    QObject::connect(plain, &QPlainTextEdit::textChanged, [&] { ++plainChanges; observeDocument(); });
    QString selectionAction, textAction, plainSelectionAction, plainTextAction;
    auto replaceDocument = [&](const QString &text) {
        QPointer<QTextDocument> previous = plain->document();
        auto *document = new QTextDocument(plain);
        document->setDocumentLayout(new QPlainTextDocumentLayout(document));
        document->setPlainText(text);
        // setDocument synchronously deletes control-owned documents. A normal
        // application callback can replace the document while its signal is
        // on the stack, so keep that old QObject alive until the callback ends.
        if (previous) previous->setParent(plain);
        plain->setDocument(document);
        observeDocument();
        if (previous) previous->deleteLater();
    };
    auto applicationChange = [&](QString &action) {
        const auto pending = action;
        action.clear();
        if (pending == "rewrite") first->setText(QStringLiteral("Application changed this field"));
        else if (pending == "focus-second") second->setFocus();
        else if (pending == "read-only") first->setReadOnly(true);
        else if (pending == "nested") QCoreApplication::processEvents(QEventLoop::AllEvents);
    };
    QObject::connect(first, &QLineEdit::selectionChanged, [&] { applicationChange(selectionAction); });
    QObject::connect(first, &QLineEdit::textChanged, [&] { applicationChange(textAction); });
    auto plainApplicationChange = [&](QString &action) {
        const auto pending = action;
        action.clear();
        if (pending == "rewrite") plain->setPlainText(QStringLiteral("Application changed this document"));
        else if (pending == "replace-document") replaceDocument(QStringLiteral("Replacement document must survive"));
        else if (pending == "focus-second") second->setFocus();
        else if (pending == "read-only") plain->setReadOnly(true);
        else if (pending == "move-caret") { auto cursor = plain->textCursor(); cursor.setPosition(0); plain->setTextCursor(cursor); }
        else if (pending == "nested") QCoreApplication::processEvents(QEventLoop::AllEvents);
    };
    QObject::connect(plain, &QPlainTextEdit::selectionChanged, [&] { plainApplicationChange(plainSelectionAction); });
    QObject::connect(plain, &QPlainTextEdit::textChanged, [&] { plainApplicationChange(plainTextAction); });
    QIntValidator validator(0, 999999, &window);
    const QString path = qEnvironmentVariable("TYPELAY_QT_NATIVE_STATE");
    auto rectangle = [&](QWidget *widget) {
        const QPoint origin = widget->mapTo(&window, QPoint());
        return QJsonObject{{"x", origin.x()}, {"y", origin.y()},
                           {"width", widget->width()}, {"height", widget->height()}};
    };
    auto publish = [&] {
        if (path.isEmpty()) return;
        auto field = [&](QLineEdit *edit, int changes, int edits) {
            const auto *label = edit == first ? firstLabel : secondLabel;
            return QJsonObject{{"text", edit->text()}, {"cursor", edit->cursorPosition()},
                {"selectionStart", edit->selectionStart()}, {"selectionLength", edit->selectionLength()},
                {"undoAvailable", edit->isUndoAvailable()}, {"focused", edit->hasFocus()},
                {"echoMode", int(edit->echoMode())}, {"readOnly", edit->isReadOnly()},
                {"validator", bool(edit->validator())}, {"inputMask", edit->inputMask()},
                {"accessibleName", edit->accessibleName()}, {"inputMethodHints", int(edit->inputMethodHints())},
                {"buddyLabel", label->text()}, {"buddyBound", label->buddy() == edit}, {"visible", edit->isVisible()},
                {"changes", changes}, {"edits", edits}, {"geometry", rectangle(edit)}};
        };
        observeDocument();
        const auto cursor = plain->textCursor();
        auto raw = plain->document()->toRawText();
        raw.replace(QChar::ParagraphSeparator, QLatin1Char('\n'));
        bool list = false, object = false, anchor = false, tag = false;
        for (auto block = plain->document()->begin(); block.isValid(); block = block.next()) {
            list = list || block.textList();
            for (auto it = block.begin(); !it.atEnd(); ++it) {
                const auto format = it.fragment().charFormat();
                object = object || format.objectType() != QTextFormat::NoObject;
                anchor = anchor || format.isAnchor();
                tag = tag || format.hasProperty(QTextFormat::UserProperty);
            }
        }
        const QJsonObject plainState{{"text", raw}, {"cursor", cursor.position()},
            {"selectionStart", cursor.hasSelection() ? cursor.selectionStart() : -1},
            {"selectionLength", cursor.selectionEnd() - cursor.selectionStart()},
            {"undoAvailable", plain->document()->isUndoAvailable()},
            {"redoAvailable", plain->document()->isRedoAvailable()}, {"undoRedoEnabled", plain->isUndoRedoEnabled()},
            {"focused", plain->hasFocus()},
            {"focusObject", QGuiApplication::focusObject() == plain},
            {"readOnly", plain->isReadOnly()}, {"maximumBlockCount", plain->maximumBlockCount()},
            {"accessibleName", plain->accessibleName()}, {"inputMethodHints", int(plain->inputMethodHints())},
            {"buddyLabel", plainLabel->text()}, {"buddyBound", plainLabel->buddy() == plain},
            {"visible", plain->isVisible()}, {"changes", plainChanges}, {"contentsChanges", plainContentsChanges},
            {"lastChange", plainLastChange}, {"documentGeneration", plainDocumentGeneration},
            {"documentClass", plain->document()->metaObject()->className()},
            {"layoutClass", plain->document()->documentLayout()->metaObject()->className()},
            {"blocks", plain->document()->blockCount()}, {"characters", plain->document()->characterCount()},
            {"list", list}, {"object", object}, {"anchor", anchor}, {"tag", tag},
            {"childFrames", plain->document()->rootFrame()->childFrames().size()},
            {"selectionAction", plainSelectionAction}, {"textAction", plainTextAction}, {"geometry", rectangle(plain)}};
        auto *overlay = window.findChild<QWidget *>(QStringLiteral("typelay-overlay"));
        auto *editor = window.findChild<QPlainTextEdit *>(QStringLiteral("typelay-editor"));
        auto *menu = window.findChild<QWidget *>(QStringLiteral("typelay-menu"));
        QJsonObject overlayControls;
        for (const auto *name : {"typelay-menu-trigger", "typelay-copy", "typelay-source-auto", "typelay-source-en"}) {
            auto *button = window.findChild<QAbstractButton *>(QLatin1String(name));
            if (button) overlayControls.insert(QLatin1String(name), QJsonObject{
                {"visible", button->isVisible()}, {"enabled", button->isEnabled()},
                {"checked", button->isChecked()}, {"geometry", rectangle(button)}});
        }
        auto *clipboard = QApplication::clipboard();
        QJsonObject state{{"pid", qint64(getpid())}, {"qtVersion", QString::fromLatin1(qVersion())},
            {"fullscreen", window.isFullScreen()}, {"width", window.width()}, {"height", window.height()},
            {"scale", window.devicePixelRatioF()}, {"keys", observer.counts},
            {"focusTransitions", qint64(observer.focusTransitions)}, {"firstChangeContext", firstChangeContext},
            {"shortcutState", observer.shortcutState}, {"shortcutId", qint64(observer.shortcutId)},
            {"shortcutActionCount", qint64(observer.shortcutActionCount)},
            {"shortcutActionId", qint64(observer.shortcutActionId)}, {"shortcutHint", QToolTip::text()},
            {"commandQueuedAction", observer.commandQueuedAction},
            {"commandDispatchedAction", observer.commandDispatchedAction},
            {"firstPreedit", observer.firstPreedit}, {"secondPreedit", observer.secondPreedit},
            {"editorPreedit", observer.editorPreedit}, {"plainPreedit", observer.plainPreedit},
            {"plain", plainState},
            {"first", field(first, firstChanges, firstEdits)},
            {"second", field(second, secondChanges, secondEdits)},
            {"overlayVisible", overlay && overlay->isVisible()},
            {"overlayMenuVisible", menu && menu->isVisible()}, {"overlayControls", overlayControls},
            {"clipboardText", clipboard->ownsClipboard() ? clipboard->text() : QString()},
            {"editorFocused", editor && editor->hasFocus()},
            {"editorReadOnly", !editor || editor->isReadOnly()},
            {"editorText", editor ? editor->toPlainText() : QString()},
            {"editorGeometry", editor ? rectangle(editor) : QJsonObject()}};
        // Read the actual provider's optional research counters without issuing
        // another IM RPC that could perturb the consumer's short-lived proof.
        for (const auto *name : {"tlqtPendingDbus", "tlqtPendingForward", "tlqtOutputDepth", "tlqtEngine", "tlqtRetired", "tlqtKeyReplies",
                                 "tlqtEditorProgrammaticSets", "tlqtEditorOwnedRenderSkips"}) {
            const auto value = qApp->property(name);
            state.insert(QLatin1String(name), value.isValid() ? QJsonValue::fromVariant(value) : QJsonValue());
        }
        QSaveFile file(path);
        const auto bytes = QJsonDocument(state).toJson(QJsonDocument::Compact);
        if (!file.open(QIODevice::WriteOnly) || file.write(bytes) != bytes.size() || !file.commit())
            app.exit(3);
    };
    QByteArray input;
    QHash<QWidget *, QPoint> clippedPositions;
    fcntl(STDIN_FILENO, F_SETFL, fcntl(STDIN_FILENO, F_GETFL) | O_NONBLOCK);
    QSocketNotifier stdinNotifier(STDIN_FILENO, QSocketNotifier::Read);
    QObject::connect(&stdinNotifier, &QSocketNotifier::activated, [&] {
        char buffer[4096];
        for (;;) {
            const auto count = read(STDIN_FILENO, buffer, sizeof buffer);
            if (count <= 0) break;
            input.append(buffer, count);
        }
        while (input.contains('\n')) {
            const auto newline = input.indexOf('\n');
            const auto command = QJsonDocument::fromJson(input.left(newline)).object();
            input.remove(0, newline + 1);
            const auto op = command.value("op").toString();
            const bool multiline = command.value("field").toString() == "plain";
            auto *field = command.value("field").toString() == "second" ? second : first;
            QWidget *widget = multiline ? static_cast<QWidget *>(plain) : field;
            if (op == "plain-visible") { plainLabel->setVisible(command.value("enabled").toBool()); plain->setVisible(command.value("enabled").toBool()); }
            else if (multiline && op == "set") plain->setPlainText(command.value("text").toString());
            else if (multiline && op == "select") {
                auto selected = plain->textCursor();
                selected.setPosition(command.value("start").toInt());
                selected.setPosition(command.value("start").toInt() + command.value("length").toInt(), QTextCursor::KeepAnchor);
                plain->setTextCursor(selected);
            }
            else if (multiline && op == "undo") plain->undo();
            else if (multiline && op == "redo") plain->redo();
            else if (multiline && op == "read-only") plain->setReadOnly(command.value("enabled").toBool());
            else if (multiline && op == "replace-document") replaceDocument(command.value("text").toString());
            else if (multiline && op == "maximum-block-count") plain->setMaximumBlockCount(command.value("value").toInt());
            else if (multiline && op == "on-selection") plainSelectionAction = command.value("action").toString();
            else if (multiline && op == "on-text") plainTextAction = command.value("action").toString();
            else if (multiline && op == "structure") {
                const auto kind = command.value("kind").toString();
                auto selected = plain->textCursor();
                selected.select(QTextCursor::Document);
                if (kind == "list") selected.createList(QTextListFormat::ListDisc);
                else if (kind == "frame") selected.insertFrame(QTextFrameFormat());
                else if (kind == "table") selected.insertTable(1, 1);
                else {
                    QTextCharFormat format;
                    if (kind == "object") format.setObjectType(QTextFormat::UserObject);
                    else if (kind == "anchor") { format.setAnchor(true); format.setAnchorHref(QStringLiteral("https://example.invalid")); }
                    else if (kind == "tag") format.setProperty(QTextFormat::UserProperty, QStringLiteral("fixture-tag"));
                    else if (kind == "style") { format.setFontWeight(QFont::Bold); format.setForeground(Qt::blue); }
                    selected.mergeCharFormat(format);
                }
            }
            else if (op == "set") field->setText(command.value("text").toString());
            else if (op == "focus") widget->setFocus();
            else if (op == "select") field->setSelection(command.value("start").toInt(), command.value("length").toInt());
            else if (op == "undo") field->undo();
            else if (op == "read-only") field->setReadOnly(command.value("enabled").toBool());
            else if (!multiline && op == "password") field->setEchoMode(command.value("enabled").toBool() ? QLineEdit::Password : QLineEdit::Normal);
            else if (!multiline && op == "input-mask") field->setInputMask(command.value("text").toString());
            else if (!multiline && op == "validator") field->setValidator(command.value("enabled").toBool() ? &validator : nullptr);
            else if (op == "input-hints") widget->setInputMethodHints(Qt::InputMethodHints(command.value("value").toInt()));
            else if (op == "metadata") widget->setAccessibleName(command.value("text").toString());
            else if (op == "buddy-label") (multiline ? plainLabel : field == first ? firstLabel : secondLabel)->setText(command.value("text").toString());
            else if (op == "offscreen") {
                if (command.value("enabled").toBool()) {
                    if (!clippedPositions.contains(widget)) clippedPositions.insert(widget, widget->pos());
                    layout->setEnabled(false);
                    widget->move(window.width() + 16, widget->y());
                } else if (clippedPositions.contains(widget)) {
                    widget->move(clippedPositions.take(widget));
                    if (clippedPositions.isEmpty()) { layout->setEnabled(true); layout->invalidate(); layout->activate(); }
                }
            }
            else if (op == "on-selection") selectionAction = command.value("action").toString();
            else if (op == "on-text") textAction = command.value("action").toString();
            else if (op == "on-command-queued") observer.commandQueuedAction = command.value("action").toString();
            else if (op == "on-command-dispatched") observer.commandDispatchedAction = command.value("action").toString();
            else if (op == "editor-set" && window.findChild<QPlainTextEdit *>(QStringLiteral("typelay-editor")))
                window.findChild<QPlainTextEdit *>(QStringLiteral("typelay-editor"))->setPlainText(command.value("text").toString());
            else if (op == "fullscreen") { if (command.value("enabled").toBool()) window.showFullScreen(); else window.showNormal(); }
            else if (op == "quit") app.quit();
            publish();
        }
    });
    QTimer timer;
    QObject::connect(&timer, &QTimer::timeout, publish);
    timer.start(20);
    QTimer::singleShot(60000, &app, &QCoreApplication::quit);
    return app.exec();
}
#include "qt_native_fixture.moc"
