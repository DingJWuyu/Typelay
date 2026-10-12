// Isolated Qt/Fcitx acknowledgement fixture. Never publishes field contents.
#include <QApplication>
#include <QEvent>
#include <QEventLoop>
#include <QInputMethodEvent>
#include <QJsonDocument>
#include <QJsonObject>
#include <QKeyEvent>
#include <QLineEdit>
#include <QSaveFile>
#include <QSocketNotifier>
#include <QTimer>
#include <QVariant>
#include <QVBoxLayout>
#include <QWindow>
#include <QDebug>
#include <cerrno>
#include <fcntl.h>
#include <unistd.h>

class Observer final : public QObject {
public:
    Observer(QLineEdit *first, QLineEdit *second, QWidget *root, QString path)
        : first_(first), second_(second), root_(root), path_(std::move(path)) {
        static const char *counters[] = {
            "widgetCallbacks", "firstCallbacks", "secondCallbacks", "widgetKeyPresses",
            "widgetKeyReleases", "widgetF24Presses", "widgetF24Releases",
            "widgetMenuCallbacks", "widgetImeCallbacks",
            "widgetCommitCallbacks", "widgetNonemptyPreeditCallbacks", "callbacksDuringPending",
            "callbacksDuringForward", "callbacksDuringOutput", "imeDuringOutput", "nestedPumpChecks",
            "nestedForwardChecks", "pendingHeldDuringNestedPump", "pendingDroppedDuringNestedPump",
            "forwardHeldDuringNestedPump", "forwardDroppedDuringNestedPump",
            "outputHeldDuringNestedPump", "outputDroppedDuringNestedPump", "unknownCommands"
        };
        for (const char *key : counters) counts_.insert(QString::fromLatin1(key), 0);
    }

    int field(const QObject *object) const {
        return object == first_ ? 1 : object == second_ ? 2 : 0;
    }

    void publish() {
        QJsonObject state = counts_;
        state.insert("qtVersion", QString::fromLatin1(qVersion()));
        state.insert("firstLength", first_->text().size());
        state.insert("secondLength", second_->text().size());
        state.insert("focus", field(QApplication::focusWidget()));
        state.insert("actualFocusObject", field(QGuiApplication::focusObject()));
        state.insert("quitting", quitting);
        static const char *properties[] = {
            "tlqtPendingDbus", "tlqtPendingForward", "tlqtRetired", "tlqtBindingEpoch",
            "tlqtOutputDepth", "tlqtCommits", "tlqtPreedits", "tlqtMenus", "tlqtSyncReplies",
            "tlqtContexts", "tlqtEngine"
        };
        for (const char *name : properties) {
            const QVariant value = qApp->property(name);
            // A missing plugin observation is unknown, not an invented zero.
            state.insert(QString::fromLatin1(name), value.isValid()
                         ? QJsonValue::fromVariant(value) : QJsonValue(QJsonValue::Null));
        }
        QObject *focusObject = qApp->property("tlqtFocusObject").value<QObject *>();
        QObject *focusWindow = qApp->property("tlqtFocusWindow").value<QObject *>();
        state.insert("tlqtFocusObject", field(focusObject));
        state.insert("tlqtFocusObjectPresent", focusObject != nullptr);
        state.insert("tlqtFocusWindow", focusWindow && focusWindow == root_->windowHandle());
        state.insert("tlqtFocusWindowPresent", focusWindow != nullptr);
        const QByteArray bytes = QJsonDocument(state).toJson(QJsonDocument::Compact) + '\n';
        QSaveFile file(path_);
        if (!file.open(QIODevice::WriteOnly) || file.write(bytes) != bytes.size() || !file.commit()) {
            qCritical("FAIL fixture state save");
            QCoreApplication::exit(3);
        }
    }

    void increment(const char *key) {
        counts_.insert(QString::fromLatin1(key), counts_.value(key).toInteger() + 1);
    }

    bool eventFilter(QObject *object, QEvent *event) override {
        if (!field(object)) return false;
        const auto type = event->type();
        const bool keyPress = type == QEvent::KeyPress;
        const bool keyRelease = type == QEvent::KeyRelease;
        const bool ime = type == QEvent::InputMethod;
        const bool menu = type == QEvent::ContextMenu;
        if (!keyPress && !keyRelease && !ime && !menu) return false;

        increment("widgetCallbacks");
        increment(field(object) == 1 ? "firstCallbacks" : "secondCallbacks");
        if (keyPress) increment("widgetKeyPresses");
        if (keyRelease) increment("widgetKeyReleases");
        if ((keyPress || keyRelease) && static_cast<QKeyEvent *>(event)->key() == Qt::Key_F24) {
            increment(keyPress ? "widgetF24Presses" : "widgetF24Releases");
        }
        if (menu) increment("widgetMenuCallbacks");
        if (ime) {
            increment("widgetImeCallbacks");
            const auto *input = static_cast<QInputMethodEvent *>(event);
            if (!input->commitString().isEmpty()) increment("widgetCommitCallbacks");
            if (!input->preeditString().isEmpty()) increment("widgetNonemptyPreeditCallbacks");
        }

        const int dbusBefore = qApp->property("tlqtPendingDbus").toInt();
        const int forwardBefore = qApp->property("tlqtPendingForward").toInt();
        const int outputBefore = qApp->property("tlqtOutputDepth").toInt();
        if (dbusBefore + forwardBefore > 0) increment("callbacksDuringPending");
        if (forwardBefore > 0) increment("callbacksDuringForward");
        if (outputBefore > 0) increment("callbacksDuringOutput");
        if (ime && outputBefore > 0) increment("imeDuringOutput");

        // This is application code on the actual target's event-delivery stack.
        // Nested event processing must not retire that still-running delivery.
        if (dbusBefore + forwardBefore > 0 || outputBefore > 0) {
            increment("nestedPumpChecks");
            if (forwardBefore > 0) increment("nestedForwardChecks");
            QCoreApplication::processEvents(QEventLoop::ExcludeUserInputEvents);
            const int dbusAfter = qApp->property("tlqtPendingDbus").toInt();
            const int forwardAfter = qApp->property("tlqtPendingForward").toInt();
            if (dbusBefore + forwardBefore > 0) {
                increment(dbusAfter + forwardAfter > 0
                          ? "pendingHeldDuringNestedPump" : "pendingDroppedDuringNestedPump");
            }
            if (forwardBefore > 0) {
                increment(forwardAfter > 0 ? "forwardHeldDuringNestedPump"
                                          : "forwardDroppedDuringNestedPump");
            }
            if (outputBefore > 0) {
                increment(qApp->property("tlqtOutputDepth").toInt() > 0
                          ? "outputHeldDuringNestedPump" : "outputDroppedDuringNestedPump");
            }
            publish();
        }
        if (menu) {
            event->accept();
            return true; // Avoid a modal context menu in the unattended fixture.
        }
        return false; // The standard QLineEdit still handles every key/IME event.
    }

    bool quitting = false;

private:
    QLineEdit *first_;
    QLineEdit *second_;
    QWidget *root_;
    QString path_;
    QJsonObject counts_;
};

int main(int argc, char **argv) {
    QApplication app(argc, argv);
    const QString statePath = qEnvironmentVariable("TYPELAY_QT_STATE");
    if (statePath.isEmpty()) {
        qCritical("TYPELAY_QT_STATE is required");
        return 2;
    }

    QWidget root;
    root.setWindowTitle(QStringLiteral("TypeLay Qt cooperative fixture"));
    QVBoxLayout layout(&root);
    QLineEdit first;
    QLineEdit second;
    first.setObjectName(QStringLiteral("first"));
    second.setObjectName(QStringLiteral("second"));
    layout.addWidget(&first);
    layout.addWidget(&second);
    root.resize(560, 130);
    Observer observer(&first, &second, &root, statePath);
    first.installEventFilter(&observer);
    second.installEventFilter(&observer);

    const int stdinFlags = fcntl(STDIN_FILENO, F_GETFL);
    if (stdinFlags < 0 || fcntl(STDIN_FILENO, F_SETFL, stdinFlags | O_NONBLOCK) < 0) {
        qCritical("FAIL fixture stdin setup");
        return 2;
    }
    QSocketNotifier input(STDIN_FILENO, QSocketNotifier::Read);
    QByteArray commands;
    QObject::connect(&input, &QSocketNotifier::activated, &app, [&] {
        char buffer[1024];
        for (;;) {
            const ssize_t count = read(STDIN_FILENO, buffer, sizeof(buffer));
            if (count > 0) commands.append(buffer, count);
            else if (count == 0) { input.setEnabled(false); break; }
            else if (errno == EINTR) continue;
            else if (errno == EAGAIN || errno == EWOULDBLOCK) break;
            else { input.setEnabled(false); app.exit(2); return; }
            if (commands.size() > 4096) { input.setEnabled(false); app.exit(2); return; }
        }
        qsizetype newline;
        while ((newline = commands.indexOf('\n')) >= 0) {
            const QByteArray command = commands.left(newline).trimmed();
            commands.remove(0, newline + 1);
            if (command == "focus1") first.setFocus(Qt::OtherFocusReason);
            else if (command == "focus2") second.setFocus(Qt::OtherFocusReason);
            else if (command == "quit") { observer.quitting = true; app.quit(); }
            else if (!command.isEmpty()) observer.increment("unknownCommands");
        }
        observer.publish();
    });
    QTimer timer;
    timer.setInterval(20);
    QObject::connect(&timer, &QTimer::timeout, &observer, [&] { observer.publish(); });
    QObject::connect(&app, &QCoreApplication::aboutToQuit, &observer, [&] {
        observer.quitting = true;
        observer.publish();
    });
    root.show();
    first.setFocus();
    timer.start();
    observer.publish();
    return app.exec();
}
