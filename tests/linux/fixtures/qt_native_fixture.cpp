// Real standard widgets with synthetic test content. No production diagnostics.
#include <QApplication>
#include <QAbstractButton>
#include <QClipboard>
#include <QImage>
#include <QIntValidator>
#include <QInputMethodEvent>
#include <QInputMethod>
#include <QEventLoop>
#include <QDynamicPropertyChangeEvent>
#include <QHash>
#include <QJsonDocument>
#include <QJsonArray>
#include <QDir>
#include <QJsonObject>
#include <QKeyEvent>
#include <QLabel>
#include <QLineEdit>
#include <QMouseEvent>
#include <QPalette>
#include <QPlainTextDocumentLayout>
#include <QPlainTextEdit>
#include <QPointer>
#include <QPixmap>
#include <QRadioButton>
#include <QTextBlock>
#include <QTextDocument>
#include <QTextFrame>
#include <QTextList>
#include <QSaveFile>
#include <QSocketNotifier>
#include <QStyleHints>
#include <QStyle>
#include <QStyleOptionButton>
#include <QTimer>
#include <QToolTip>
#include <QVBoxLayout>
#include <cstdio>
#include <fcntl.h>
#include <time.h>
#include <utility>
#include <unistd.h>

qint64 observedAtMs() {
    timespec value{};
    if (clock_gettime(CLOCK_BOOTTIME, &value) != 0) qFatal("Fixture clock unavailable");
    return qint64(value.tv_sec) * 1000 + value.tv_nsec / 1000000;
}

class KeyObserver final : public QObject {
    Q_OBJECT
public:
    QLineEdit *first = nullptr, *second = nullptr;
    QPlainTextEdit *plain = nullptr;
    QWidget *window = nullptr;
    struct OverlayObject { QString role; quint64 instance; QRect bounds; };
    QHash<QObject *, OverlayObject> overlayObjects;
    quint64 overlayInstance = 0, overlayEventCount = 0, languageClickEventCount = 0;
    QJsonObject counts;
    QJsonArray providerEvents;
    quint64 providerSequence = 0;
    quint64 commandSequence = 0;
    bool plainPendingSwitch = false;
    bool firstPreedit = false, secondPreedit = false, plainPreedit = false, editorPreedit = false;
    QString shortcutState, commandQueuedAction, commandDispatchedAction, controlAction, plainHandoffAction;
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
signals:
    void plainHandoff(const QString &action);
public slots:
    void providerChanged(QObject *field, uint causes) {
        if (!field) return;
        const auto name = field->objectName();
        if (field != first && field != second && field != plain
            && name != QStringLiteral("typelay-editor")
            && name != QStringLiteral("typelay-glossary-source")
            && name != QStringLiteral("typelay-glossary-target")) return;
        // Bounded real provider notifications, no input text or object addresses.
        providerEvents.append(QJsonObject{{"sequence", qint64(++providerSequence)},
            {"atMs", observedAtMs()}, {"role", name}, {"causes", qint64(causes)}});
        while (providerEvents.size() > 64) providerEvents.removeFirst();
        // The provider emits Focus from tlBind between its detach capture and
        // finish. Mutate through ordinary widget API on that real callback stack.
        if (field == second && causes == 2u && !plainHandoffAction.isEmpty()) {
            const auto action = std::exchange(plainHandoffAction, QString());
            counts.insert("plainHandoffs", counts.value("plainHandoffs").toInt() + 1);
            emit plainHandoff(action);
        }
    }
    void shortcutChanged(const QString &state, quint64 id) {
        shortcutState = state;
        shortcutId = id;
        if (state == "queued") applyCommandAction(commandQueuedAction);
        if (state == "dispatched") applyCommandAction(commandDispatchedAction);
    }
public:
    void overlayEvent(const char *event, const OverlayObject &object, QJsonObject extra = {}) {
        // Owned TypeLay control roles and numeric geometry/timing only. Never
        // log widget text, unrelated object names or native addresses.
        // Preserve the existing total bound, reserving twelve events for the
        // first real Auto/English clicks after the preceding desktop cases.
        const bool languageClick = (object.role == QStringLiteral("typelay-source-auto")
            || object.role == QStringLiteral("typelay-source-en"))
            && (qstrcmp(event, "mouse-press") == 0 || qstrcmp(event, "mouse-release") == 0
                || qstrcmp(event, "pressed") == 0 || qstrcmp(event, "released") == 0
                || qstrcmp(event, "clicked") == 0);
        if (overlayEventCount >= 160 || (languageClick ? languageClickEventCount >= 12
                : overlayEventCount - languageClickEventCount >= 148)) return;
        if (languageClick) ++languageClickEventCount;
        const auto &bounds = object.bounds;
        extra.insert("sequence", qint64(++overlayEventCount));
        extra.insert("atMs", observedAtMs());
        extra.insert("event", QLatin1String(event));
        extra.insert("role", object.role);
        extra.insert("instance", qint64(object.instance));
        extra.insert("geometry", QJsonObject{{"x", bounds.x()}, {"y", bounds.y()},
            {"width", bounds.width()}, {"height", bounds.height()}});
        const auto bytes = QJsonDocument(extra).toJson(QJsonDocument::Compact);
        std::fprintf(stderr, "qt-overlay-event %s\n", bytes.constData());
    }
    void observeOverlay(QWidget *widget) {
        if (!widget || !window || widget->window() != window) return;
        const auto name = widget->objectName();
        const bool control = qobject_cast<QAbstractButton *>(widget) && name.startsWith(QStringLiteral("typelay-"));
        if (!control && name != QStringLiteral("typelay-overlay") && name != QStringLiteral("typelay-menu")) return;
        const QRect bounds(widget->mapTo(window, QPoint()), widget->size());
        if (overlayObjects.contains(widget)) { overlayObjects[widget].bounds = bounds; return; }
        overlayObjects.insert(widget, {name, ++overlayInstance, bounds});
        connect(widget, &QObject::destroyed, this, [this, widget] {
            // QWidget geometry is no longer safe to read from destroyed().
            overlayEvent("destroy", overlayObjects.take(widget));
        });
        if (auto *button = qobject_cast<QAbstractButton *>(widget)) {
            auto record = [this, button](const char *phase) {
                observeOverlay(button);
                overlayEvent(phase, overlayObjects.value(button));
            };
            connect(button, &QAbstractButton::pressed, this, [record] { record("pressed"); });
            connect(button, &QAbstractButton::released, this, [record] { record("released"); });
            connect(button, &QAbstractButton::clicked, this, [record] { record("clicked"); });
        }
    }
    bool eventFilter(QObject *object, QEvent *event) override {
        const auto type = event->type();
        if (type == QEvent::Show || type == QEvent::Hide || type == QEvent::Move || type == QEvent::Resize
            || type == QEvent::MouseButtonPress || type == QEvent::MouseButtonRelease) {
            auto *widget = qobject_cast<QWidget *>(object);
            observeOverlay(widget);
            if (overlayObjects.contains(object) && (type == QEvent::Show || type == QEvent::Hide))
                overlayEvent(type == QEvent::Show ? "show" : "hide", overlayObjects.value(object));
            if (widget && window && widget->window() == window
                && (type == QEvent::MouseButtonPress || type == QEvent::MouseButtonRelease)) {
                const auto *mouse = static_cast<QMouseEvent *>(event);
                const auto point = widget->mapTo(window, mouse->position().toPoint());
                const auto observed = overlayObjects.value(object, {QStringLiteral("other"), 0,
                    QRect(widget->mapTo(window, QPoint()), widget->size())});
                overlayEvent(type == QEvent::MouseButtonPress ? "mouse-press" : "mouse-release", observed,
                    {{"x", point.x()}, {"y", point.y()}, {"spontaneous", event->spontaneous()}});
            }
        }
        if (object == qApp && event->type() == QEvent::DynamicPropertyChange
            && static_cast<QDynamicPropertyChangeEvent *>(event)->propertyName() == "typelay.qt.native.v1") {
            auto *native = qApp->property("typelay.qt.native.v1").value<QObject *>();
            if (native) connect(native, SIGNAL(shortcutStateChangedV1(QString,quint64)),
                this, SLOT(shortcutChanged(QString,quint64)), Qt::UniqueConnection);
        }
        if (event->type() == QEvent::DynamicPropertyChange
            && static_cast<QDynamicPropertyChangeEvent *>(event)->propertyName() == "typelay.fcitx.qt.provider.v1") {
            auto *provider = object->property("typelay.fcitx.qt.provider.v1").value<QObject *>();
            if (provider) connect(provider, SIGNAL(typelayStateChangedV1(QObject*,uint)),
                this, SLOT(providerChanged(QObject*,uint)), Qt::UniqueConnection);
        }
        if (event->type() == QEvent::InputMethod) {
            const bool composing = !static_cast<QInputMethodEvent *>(event)->preeditString().isEmpty();
            if (object == first) firstPreedit = composing;
            else if (object == second) secondPreedit = composing;
            else if (object == plain) plainPreedit = composing;
            else if (object->objectName() == QStringLiteral("typelay-editor")) editorPreedit = composing;
        }
        const bool editor = object->objectName() == QStringLiteral("typelay-editor");
        if (object != first && object != second && object != plain && !editor) return false;
        if (event->type() == QEvent::FocusIn || event->type() == QEvent::FocusOut) {
            const QString focusName = (object == first ? QStringLiteral("first")
                : object == second ? QStringLiteral("second") : editor ? QStringLiteral("editor") : QStringLiteral("plain"))
                + (event->type() == QEvent::FocusIn ? QStringLiteral("FocusIn") : QStringLiteral("FocusOut"));
            counts.insert(focusName, counts.value(focusName).toInt() + 1);
        }
        if (event->type() != QEvent::KeyPress && event->type() != QEvent::KeyRelease) return false;
        const auto *key = static_cast<QKeyEvent *>(event);
        if (plainPendingSwitch && object == plain && event->type() == QEvent::KeyPress
            && qApp->property("tlqtPendingForward").toInt() > 0) {
            plainPendingSwitch = false;
            second->setFocus(Qt::OtherFocusReason);
            counts.insert("plainPendingSwitches", counts.value("plainPendingSwitches").toInt() + 1);
        }
        if (object == first && key->key() == Qt::Key_Control && event->type() == QEvent::KeyPress) {
            const auto action = std::exchange(controlAction, QString());
            if (action == "rewrite") first->setText(QStringLiteral("Application changed this field"));
            else if (action == "focus-second") second->setFocus();
            else if (action == "select") first->setSelection(0, 1);
            else if (action == "empty-ime") {
                QInputMethodEvent input;
                QCoreApplication::sendEvent(first, &input);
            }
        }
        const QString name = (object == first ? QStringLiteral("first")
            : object == second ? QStringLiteral("second") : editor ? QStringLiteral("editor") : QStringLiteral("plain"))
            + (key->key() == Qt::Key_Tab ? QStringLiteral("Tab") : QStringLiteral("Key"))
            + (event->type() == QEvent::KeyPress ? QStringLiteral("Press") : QStringLiteral("Release"));
        counts.insert(name, counts.value(name).toInt() + 1);
        if (key->key() == Qt::Key_Control) {
            const auto controlName = QStringLiteral("control")
                + (event->type() == QEvent::KeyPress ? QStringLiteral("Press") : QStringLiteral("Release"));
            counts.insert(controlName, counts.value(controlName).toInt() + 1);
        }
        if (key->key() >= Qt::Key_F1 && key->key() <= Qt::Key_F24) {
            const QString functionName = (object == first ? QStringLiteral("first")
                : object == second ? QStringLiteral("second") : editor ? QStringLiteral("editor") : QStringLiteral("plain"))
                + QStringLiteral("F") + QString::number(key->key() - Qt::Key_F1 + 1)
                + (event->type() == QEvent::KeyPress ? QStringLiteral("Press") : QStringLiteral("Release"));
            counts.insert(functionName, counts.value(functionName).toInt() + 1);
            counts.insert(functionName + QStringLiteral("Timestamp"), qint64(key->timestamp()));
            counts.insert(functionName + QStringLiteral("ObservedAtMs"), observedAtMs());
        }
        return false;
    }
};

int main(int argc, char **argv) {
    QApplication app(argc, argv);
    const QPalette initialPalette = app.palette();
    int applicationThemeChanges = 0;
    int themeSampleCount = 0;
    QJsonObject themeSamples;
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
    observer.first = first; observer.second = second; observer.plain = plain; observer.window = &window;
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
    QJsonObject plainLastChange, plainChangeContext;
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
    QObject::connect(plain, &QPlainTextEdit::textChanged, [&] {
        ++plainChanges;
        observeDocument();
        // Same synchronous observation as firstChangeContext, for the actual
        // multiline body notification. Never query or reset the current IM.
        plainChangeContext = {{"plainChanges", plainChanges}, {"secondFocused", second->hasFocus()},
            {"secondText", second->text()}, {"secondPreedit", observer.secondPreedit},
            {"secondCursor", second->cursorPosition()}, {"secondSelectionStart", second->selectionStart()},
            {"secondSelectionLength", second->selectionLength()},
            {"focusTransitions", qint64(observer.focusTransitions)},
            {"plainFocusIn", observer.counts.value("plainFocusIn")},
            {"plainFocusOut", observer.counts.value("plainFocusOut")},
            {"tlqtCommits", QJsonValue::fromVariant(qApp->property("tlqtCommits"))},
            {"tlqtPreedits", QJsonValue::fromVariant(qApp->property("tlqtPreedits"))}};
    });
    QString selectionAction, textAction, plainSelectionAction, plainTextAction;
    QJsonObject selectionHandoff;
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
    QObject::connect(&observer, &KeyObserver::plainHandoff, [&](const QString &action) {
        if (action == "document") replaceDocument(plain->toPlainText());
        else if (action == "text") plain->setPlainText(QStringLiteral("Handoff source must survive"));
    });
    auto applicationChange = [&](QString &action) {
        const auto pending = action;
        action.clear();
        if (pending == "rewrite") first->setText(QStringLiteral("Application changed this field"));
        else if (pending == "focus-second") {
            // Observe the real selection callback around Qt's focus-out path.
            // QLineEdit may deselect on focus loss; the consumer must not undo
            // application/Qt state after reporting an unknown write outcome.
            const int start = first->selectionStart(), length = first->selectionLength();
            second->setFocus();
            selectionHandoff = {{"beforeStart", start}, {"beforeLength", length},
                {"afterStart", first->selectionStart()}, {"afterLength", first->selectionLength()},
                {"secondFocused", second->hasFocus()}};
        }
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
    QPointer<QObject> savedEditorProvider;
    QObject unavailableEditorProvider;
    QString editorProviderAction;
    QJsonArray motionFrames;
    int motionRecordings = 0, motionArmed = 0;
    bool motionRecording = false;
    const auto initialStyleSheet = app.styleSheet();
    auto publish = [&] {
        if (path.isEmpty()) return;
        const auto paletteState = [](const QPalette &palette) {
            return QJsonObject{{"window", palette.color(QPalette::Active, QPalette::Window).name()},
                {"windowText", palette.color(QPalette::Active, QPalette::WindowText).name()},
                {"base", palette.color(QPalette::Active, QPalette::Base).name()},
                {"text", palette.color(QPalette::Active, QPalette::Text).name()}};
        };
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
        QJsonObject glossary;
        auto *form = window.findChild<QWidget *>(QStringLiteral("typelay-glossary"));
        glossary.insert("visible", form && form->isVisible());
        auto *feedback = form ? form->findChild<QLabel *>(QStringLiteral("typelay-glossary-feedback")) : nullptr;
        glossary.insert("feedback", feedback ? feedback->text() : QString());
        for (const auto *name : {"source", "target"}) {
            auto *term = form ? form->findChild<QLineEdit *>(QStringLiteral("typelay-glossary-") + QLatin1String(name)) : nullptr;
            if (term) glossary.insert(QLatin1String(name), QJsonObject{{"text", term->text()},
                {"focused", term->hasFocus()}, {"geometry", rectangle(term)}});
        }
        QJsonObject overlayTextColors;
        for (const auto *name : {"typelay-status", "typelay-hint"}) {
            const auto *label = window.findChild<QLabel *>(QLatin1String(name));
            if (label && label->isVisible()) overlayTextColors.insert(QLatin1String(name),
                label->palette().color(QPalette::Active, QPalette::WindowText).name());
        }
        observer.observeOverlay(overlay);
        observer.observeOverlay(menu);
        QJsonObject overlayControls;
        for (auto *button : window.findChildren<QAbstractButton *>()) {
            const auto name = button->objectName();
            if (!name.startsWith(QStringLiteral("typelay-"))) continue;
            observer.observeOverlay(button);
            QJsonObject control{
                {"text", button->text()}, {"focused", button->hasFocus()},
                {"visible", button->isVisible()}, {"enabled", button->isEnabled()},
                {"underMouse", button->underMouse()},
                {"checked", button->isChecked()}, {"geometry", rectangle(button)},
                {"textColor", button->palette().color(QPalette::Active, QPalette::ButtonText).name()},
                {"backgroundColor", button->palette().color(QPalette::Active, QPalette::Button).name()},
                {"instance", qint64(observer.overlayObjects.value(button).instance)}};
            if (qobject_cast<QRadioButton *>(button)
                && (name == QStringLiteral("typelay-source-auto") || name == QStringLiteral("typelay-source-en"))) {
                QStyleOptionButton option;
                option.initFrom(button);
                option.text = button->text(); option.icon = button->icon(); option.iconSize = button->iconSize();
                option.state |= button->isChecked() ? QStyle::State_On : QStyle::State_Off;
                const auto local = button->style()->subElementRect(QStyle::SE_RadioButtonClickRect, &option, button);
                const auto origin = button->mapTo(&window, local.topLeft());
                control.insert("styleClickGeometry", QJsonObject{{"x", origin.x()}, {"y", origin.y()},
                    {"width", local.width()}, {"height", local.height()}});
            }
            overlayControls.insert(name, control);
        }
        auto *clipboard = QApplication::clipboard();
        QJsonObject state{{"pid", qint64(getpid())}, {"qtVersion", QString::fromLatin1(qVersion())},
            {"observedAtMs", observedAtMs()}, {"overlayEventCount", qint64(observer.overlayEventCount)},
            {"applicationColorScheme", int(app.styleHints()->colorScheme())},
            {"applicationPalette", paletteState(app.palette())},
            {"applicationThemeChanges", applicationThemeChanges},
            {"themeSampleCount", themeSampleCount}, {"themeSamples", themeSamples},
            { "motionArmed", motionArmed }, {"motionFrames", motionFrames}, {"motionRecordings", motionRecordings}, {"motionRecording", motionRecording},
            {"animationDuration", overlay ? overlay->style()->styleHint(QStyle::SH_Widget_Animation_Duration, nullptr, overlay) : -1},
            {"fullscreen", window.isFullScreen()}, {"width", window.width()}, {"height", window.height()},
            {"scale", window.devicePixelRatioF()}, {"keys", observer.counts},
            {"commandSequence", qint64(observer.commandSequence)},
            {"focusTransitions", qint64(observer.focusTransitions)}, {"firstChangeContext", firstChangeContext}, {"selectionHandoff", selectionHandoff}, {"plainChangeContext", plainChangeContext},
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
            {"glossary", glossary}, {"providerEvents", observer.providerEvents},
            {"providerSequence", qint64(observer.providerSequence)},
            {"overlayVisible", overlay && overlay->isVisible()},
            {"overlayPalette", overlay ? paletteState(overlay->palette()) : QJsonObject()},
            {"overlayTextColors", overlayTextColors},
            {"overlayGeometry", overlay ? rectangle(overlay) : QJsonObject()},
            {"overlayMenuVisible", menu && menu->isVisible()}, {"overlayControls", overlayControls},
            {"clipboardText", clipboard->ownsClipboard() ? clipboard->text() : QString()},
            {"editorVisible", editor && editor->isVisible()},
            {"editorFocused", editor && editor->hasFocus()},
            {"editorProviderPresent", editor && editor->property("typelay.fcitx.qt.provider.v1").value<QObject *>()},
            {"editorProviderOriginal", editor && savedEditorProvider
                && editor->property("typelay.fcitx.qt.provider.v1").value<QObject *>() == savedEditorProvider},
            {"editorProviderAction", editorProviderAction},
            {"editorReadOnly", !editor || editor->isReadOnly()},
            {"editorText", editor ? editor->toPlainText() : QString()},
            {"editorPalette", editor ? paletteState(editor->palette()) : QJsonObject()},
            {"editorViewportPalette", editor ? paletteState(editor->viewport()->palette()) : QJsonObject()},
            {"editorSelectionBackground", editor ? editor->palette().color(QPalette::Active, QPalette::Highlight).name() : QString()},
            {"editorSelectionText", editor ? editor->palette().color(QPalette::Active, QPalette::HighlightedText).name() : QString()},
            {"editorAccessibleName", editor ? editor->accessibleName() : QString()},
            {"editorCursor", editor ? editor->textCursor().position() : -1},
            {"editorAnchor", editor ? editor->textCursor().anchor() : -1},
            {"editorUndoRedoEnabled", editor && editor->isUndoRedoEnabled()},
            {"editorUndoSteps", editor ? editor->document()->availableUndoSteps() : 0},
            {"editorUndoAvailable", editor && editor->document()->isUndoAvailable()},
            {"editorRedoAvailable", editor && editor->document()->isRedoAvailable()},
            {"editorGeometry", editor ? rectangle(editor) : QJsonObject()}};
        // Read the actual provider's optional research counters without issuing
        // another IM RPC that could perturb the consumer's short-lived proof.
        for (const auto *name : {"tlqtPendingDbus", "tlqtPendingForward", "tlqtOutputDepth", "tlqtEngine", "tlqtRetired", "tlqtKeyReplies", "tlqtCommits", "tlqtPreedits",
                                 "tlqtEditorProgrammaticSets", "tlqtEditorOwnedRenderSkips", "tlqtDelayedCompletion"}) {
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
            if (op == "consumer-retirement-opt-in") {
                auto *consumer = qApp->property("typelay.fcitx.qt.consumer.v1").value<QObject *>();
                if (!consumer) qFatal("Actual cooperative consumer missing");
                consumer->setProperty("typelay.fcitx.qt.detached-retirement.v1",
                    command.contains("value") ? command.value("value").toVariant() : QVariant());
            }
            else if (op == "reset-ime") QGuiApplication::inputMethod()->reset();
            else if (op == "application-motion") {
                // Exercise Qt's public style preference on this isolated app.
                // This does not claim the compositor exports an OS preference.
                app.setStyleSheet(command.value("reduced").toBool()
                    ? initialStyleSheet + QStringLiteral("\n* { widget-animation-duration: 0; }") : initialStyleSheet);
            }
            else if (op == "observe-motion") {
                auto *expand = window.findChild<QAbstractButton *>(QStringLiteral("typelay-expand"));
                if (!expand || motionRecording) qFatal("Overlay motion observation unavailable");
                const auto directory = command.value("directory").toString();
                if (!directory.isEmpty() && !QDir().mkpath(directory)) qFatal("Motion output directory unavailable");
                QObject::connect(expand, &QAbstractButton::clicked, &window, [&, directory] {
                    motionFrames = {};
                    motionRecording = true;
                    const auto started = observedAtMs();
                    const QPointer<QWidget> surface = window.findChild<QWidget *>(QStringLiteral("typelay-overlay"));
                    const QPointer<QPlainTextEdit> translation = window.findChild<QPlainTextEdit *>(QStringLiteral("typelay-editor"));
                    const QPointer<QWidget> footer = window.findChild<QWidget *>(QStringLiteral("typelay-footer"));
                    auto *samples = new QTimer(&window);
                    auto sample = [&, samples, surface, translation, footer, started, directory] {
                        const auto elapsed = observedAtMs() - started;
                        const auto cursor = translation ? translation->textCursor() : QTextCursor();
                        motionFrames.append(QJsonObject{{"ms", elapsed},
                            {"visible", surface && surface->isVisible()},
                            {"overlay", surface ? rectangle(surface) : QJsonObject()},
                            {"editor", translation ? rectangle(translation) : QJsonObject()},
                            {"footer", footer ? rectangle(footer) : QJsonObject()},
                            {"text", translation ? translation->toPlainText() : QString()},
                            {"cursor", translation ? cursor.position() : -1},
                            {"anchor", translation ? cursor.anchor() : -1},
                            {"editorFocused", translation && translation->hasFocus()}, {"sourceFocused", first->hasFocus()}});
                        // Finite real-widget paint frames, never an animation mock.
                        const auto index = motionFrames.size();
                        if (!directory.isEmpty() && (index == 1 || index == 5 || index == 10 || index == 18 || elapsed >= 450)) {
                            if (!window.grab().save(directory + QStringLiteral("/frame-%1.png").arg(index, 2, 10, QLatin1Char('0'))))
                                qFatal("Motion frame could not be saved");
                        }
                        if (elapsed >= 450) {
                            samples->stop(); samples->deleteLater();
                            motionRecording = false;
                            ++motionRecordings;
                        }
                    };
                    QObject::connect(samples, &QTimer::timeout, &window, sample);
                    sample();
                    samples->start(16);
                }, Qt::SingleShotConnection);
                ++motionArmed;
            }
            else if (op == "sample-theme") {
                // Render only two fixed blank surface points on demand. Do not
                // add painting to the fixture's periodic state observations.
                auto *overlay = window.findChild<QWidget *>(QStringLiteral("typelay-overlay"));
                auto *editor = window.findChild<QPlainTextEdit *>(QStringLiteral("typelay-editor"));
                const auto sample = [](QWidget *surface, const QPoint &point) {
                    if (!surface || !surface->isVisible() || !surface->rect().contains(point)) return QString();
                    const auto color = surface->grab(QRect(point, QSize(1, 1))).toImage().pixelColor(0, 0);
                    return color.alpha() == 255 ? color.name() : QString();
                };
                auto *viewport = editor ? editor->viewport() : nullptr;
                themeSamples = {{"overlay", sample(overlay, QPoint(6, overlay ? overlay->height() / 2 : 0))},
                    {"editor", sample(viewport, viewport ? QPoint(viewport->width() - 2, viewport->height() - 2) : QPoint())}};
                ++themeSampleCount;
            }
            else if (op == "application-theme") {
                // Only the owned fixture changes application-wide appearance.
                // The published value is Qt's real result, not this request.
                const auto theme = command.value("theme").toString();
                if (theme == QStringLiteral("system")) {
                    app.styleHints()->unsetColorScheme();
                    app.setPalette(initialPalette);
                } else if (theme == QStringLiteral("light") || theme == QStringLiteral("dark")) {
                    const bool dark = theme == QStringLiteral("dark");
                    app.styleHints()->setColorScheme(dark ? Qt::ColorScheme::Dark : Qt::ColorScheme::Light);
                    auto palette = initialPalette;
                    const QColor background(dark ? QStringLiteral("#303030") : QStringLiteral("#f0f0f0"));
                    const QColor foreground(dark ? QStringLiteral("#f0f0f0") : QStringLiteral("#202020"));
                    for (const auto role : {QPalette::Window, QPalette::Base, QPalette::Button})
                        palette.setColor(role, background);
                    for (const auto role : {QPalette::WindowText, QPalette::Text, QPalette::ButtonText})
                        palette.setColor(role, foreground);
                    app.setPalette(palette);
                } else qFatal("Unknown fixture application theme");
                ++applicationThemeChanges;
            }
            else if (op == "plain-visible") { plainLabel->setVisible(command.value("enabled").toBool()); plain->setVisible(command.value("enabled").toBool()); }
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
            else if (op == "plain-handoff") observer.plainHandoffAction = command.value("action").toString();
            else if (op == "plain-pending-switch") observer.plainPendingSwitch = true;
            else if (op == "on-control") observer.controlAction = command.value("action").toString();
            else if (op == "editor-provider") {
                auto *editor = window.findChild<QPlainTextEdit *>(QStringLiteral("typelay-editor"));
                if (editor) {
                    const auto action = command.value("action").toString();
                    editorProviderAction = action;
                    if (action == "restore") editor->setProperty("typelay.fcitx.qt.provider.v1",
                        QVariant::fromValue(savedEditorProvider.data()));
                    else {
                        savedEditorProvider = editor->property("typelay.fcitx.qt.provider.v1").value<QObject *>();
                        editor->setProperty("typelay.fcitx.qt.provider.v1", action == "replace"
                            ? QVariant::fromValue<QObject *>(&unavailableEditorProvider) : QVariant());
                    }
                }
            }
            else if (op == "editor-set" && window.findChild<QPlainTextEdit *>(QStringLiteral("typelay-editor")))
                window.findChild<QPlainTextEdit *>(QStringLiteral("typelay-editor"))->setPlainText(command.value("text").toString());
            else if (op == "fullscreen") { if (command.value("enabled").toBool()) window.showFullScreen(); else window.showNormal(); }
            else if (op == "quit") app.quit();
            else if (op == "observe") {
                // Acknowledge a fresh snapshot without changing widgets. This
                // can follow a native-socket reply that predates the timer.
            }
            ++observer.commandSequence;
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
