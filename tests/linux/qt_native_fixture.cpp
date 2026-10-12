// Real standard widgets with synthetic test content. No production diagnostics.
#include <QApplication>
#include <QIntValidator>
#include <QInputMethodEvent>
#include <QEventLoop>
#include <QHash>
#include <QJsonDocument>
#include <QJsonObject>
#include <QKeyEvent>
#include <QLabel>
#include <QLineEdit>
#include <QPlainTextEdit>
#include <QSaveFile>
#include <QSocketNotifier>
#include <QTimer>
#include <QVBoxLayout>
#include <fcntl.h>
#include <unistd.h>

class KeyObserver final : public QObject {
public:
    QLineEdit *first = nullptr, *second = nullptr;
    QJsonObject counts;
    bool firstPreedit = false, secondPreedit = false, editorPreedit = false;
    bool eventFilter(QObject *object, QEvent *event) override {
        if (event->type() == QEvent::InputMethod) {
            const bool composing = !static_cast<QInputMethodEvent *>(event)->preeditString().isEmpty();
            if (object == first) firstPreedit = composing;
            else if (object == second) secondPreedit = composing;
            else if (object->objectName() == QStringLiteral("typelay-editor")) editorPreedit = composing;
        }
        if (object != first && object != second) return false;
        if (event->type() != QEvent::KeyPress && event->type() != QEvent::KeyRelease) return false;
        const auto *key = static_cast<QKeyEvent *>(event);
        const QString name = (object == first ? QStringLiteral("first") : QStringLiteral("second"))
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
    KeyObserver observer;
    observer.first = first; observer.second = second;
    app.installEventFilter(&observer);
    first->setObjectName(QStringLiteral("first"));
    second->setObjectName(QStringLiteral("second"));
    first->setAccessibleName(QStringLiteral("Message one"));
    second->setAccessibleName(QStringLiteral("Message two"));
    auto *firstLabel = new QLabel(QStringLiteral("Message one"), &window);
    auto *secondLabel = new QLabel(QStringLiteral("Message two"), &window);
    firstLabel->setBuddy(first);
    secondLabel->setBuddy(second);
    layout->addWidget(firstLabel);
    layout->addWidget(first);
    layout->addWidget(secondLabel);
    layout->addWidget(second);
    layout->addStretch();
    window.resize(700, 500);
    window.show();
    first->setFocus();
    int firstChanges = 0, secondChanges = 0, firstEdits = 0, secondEdits = 0;
    QObject::connect(first, &QLineEdit::textChanged, [&] { ++firstChanges; });
    QObject::connect(second, &QLineEdit::textChanged, [&] { ++secondChanges; });
    QObject::connect(first, &QLineEdit::textEdited, [&] { ++firstEdits; });
    QObject::connect(second, &QLineEdit::textEdited, [&] { ++secondEdits; });
    QString selectionAction, textAction;
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
        auto *overlay = window.findChild<QWidget *>(QStringLiteral("typelay-overlay"));
        auto *editor = window.findChild<QPlainTextEdit *>(QStringLiteral("typelay-editor"));
        QJsonObject state{{"pid", qint64(getpid())}, {"qtVersion", QString::fromLatin1(qVersion())},
            {"fullscreen", window.isFullScreen()}, {"width", window.width()}, {"height", window.height()},
            {"scale", window.devicePixelRatioF()}, {"keys", observer.counts},
            {"firstPreedit", observer.firstPreedit}, {"secondPreedit", observer.secondPreedit},
            {"editorPreedit", observer.editorPreedit},
            {"first", field(first, firstChanges, firstEdits)},
            {"second", field(second, secondChanges, secondEdits)},
            {"overlayVisible", overlay && overlay->isVisible()},
            {"editorFocused", editor && editor->hasFocus()},
            {"editorReadOnly", !editor || editor->isReadOnly()},
            {"editorText", editor ? editor->toPlainText() : QString()},
            {"editorGeometry", editor ? rectangle(editor) : QJsonObject()}};
        // Read the actual provider's optional research counters without issuing
        // another IM RPC that could perturb the consumer's short-lived proof.
        for (const auto *name : {"tlqtPendingDbus", "tlqtPendingForward", "tlqtOutputDepth", "tlqtEngine", "tlqtRetired", "tlqtKeyReplies"}) {
            const auto value = qApp->property(name);
            state.insert(QLatin1String(name), value.isValid() ? QJsonValue::fromVariant(value) : QJsonValue());
        }
        QSaveFile file(path);
        const auto bytes = QJsonDocument(state).toJson(QJsonDocument::Compact);
        if (!file.open(QIODevice::WriteOnly) || file.write(bytes) != bytes.size() || !file.commit())
            app.exit(3);
    };
    QByteArray input;
    QHash<QLineEdit *, QPoint> clippedPositions;
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
            auto *field = command.value("field").toString() == "second" ? second : first;
            if (op == "set") field->setText(command.value("text").toString());
            else if (op == "focus") field->setFocus();
            else if (op == "select") field->setSelection(command.value("start").toInt(), command.value("length").toInt());
            else if (op == "undo") field->undo();
            else if (op == "read-only") field->setReadOnly(command.value("enabled").toBool());
            else if (op == "password") field->setEchoMode(command.value("enabled").toBool() ? QLineEdit::Password : QLineEdit::Normal);
            else if (op == "input-mask") field->setInputMask(command.value("text").toString());
            else if (op == "validator") field->setValidator(command.value("enabled").toBool() ? &validator : nullptr);
            else if (op == "input-hints") field->setInputMethodHints(Qt::InputMethodHints(command.value("value").toInt()));
            else if (op == "metadata") field->setAccessibleName(command.value("text").toString());
            else if (op == "buddy-label") (field == first ? firstLabel : secondLabel)->setText(command.value("text").toString());
            else if (op == "offscreen") {
                if (command.value("enabled").toBool()) {
                    if (!clippedPositions.contains(field)) clippedPositions.insert(field, field->pos());
                    layout->setEnabled(false);
                    field->move(window.width() + 16, field->y());
                } else if (clippedPositions.contains(field)) {
                    field->move(clippedPositions.take(field));
                    if (clippedPositions.isEmpty()) { layout->setEnabled(true); layout->invalidate(); layout->activate(); }
                }
            }
            else if (op == "on-selection") selectionAction = command.value("action").toString();
            else if (op == "on-text") textAction = command.value("action").toString();
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
