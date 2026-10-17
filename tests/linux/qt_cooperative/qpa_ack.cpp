// Isolated Qt private-ABI delivery test, compiled against the pinned SDK.
#include <QApplication>
#include <QAbstractEventDispatcher>
#include <QLineEdit>
#include <QVBoxLayout>
#include <QTimer>
#include <QKeyEvent>
#include <QContextMenuEvent>
#include <QDebug>
#include <qpa/qwindowsysteminterface_p.h>
#include <memory>
#include <cstdlib>
#include <functional>

struct State { int pending = 0; int acknowledgements = 0; int delivered = 0; std::function<void()> onRetired; };
template<class Base> struct Tracked final : Base {
    std::shared_ptr<State> state;
    template<class... Args> Tracked(std::shared_ptr<State> s, Args&&... args)
        : Base(std::forward<Args>(args)...), state(std::move(s)) { ++state->pending; }
    ~Tracked() override {
        --state->pending; ++state->acknowledgements;
        if (state->pending == 0 && state->onRetired) {
            auto complete = std::move(state->onRetired);
            QTimer::singleShot(0, qApp, std::move(complete));
        }
    }
};
static void require(bool good, const char* message) {
    if (!good) { qCritical() << "FAIL" << message; std::exit(2); }
}
class Observer final : public QObject {
public:
    std::shared_ptr<State> state;
    explicit Observer(std::shared_ptr<State> s) : state(std::move(s)) {}
    bool eventFilter(QObject* object, QEvent* event) override {
        if (qobject_cast<QLineEdit*>(object) &&
            (event->type() == QEvent::KeyPress || event->type() == QEvent::ContextMenu)) {
            require(state->pending > 0, "pending covers actual widget callback");
            ++state->delivered;
            if (event->type() == QEvent::KeyPress && state->delivered <= 2)
                require(static_cast<QKeyEvent *>(event)->timestamp() == ulong(state->delivered),
                        "queued native key preserves its original timestamp");
            // Exercise the previously missed processEvents reentry boundary.
            QCoreApplication::processEvents(QEventLoop::ExcludeUserInputEvents);
            require(state->pending > 0, "pending survives nested processEvents");
            if (event->type() == QEvent::ContextMenu) return true; // avoid a modal native menu
        }
        return false;
    }
};
int main(int argc, char** argv) {
    QApplication app(argc, argv);
    QWidget root;
    QVBoxLayout layout(&root);
    QLineEdit first, second;
    layout.addWidget(&first); layout.addWidget(&second);
    auto state = std::make_shared<State>();
    Observer observer(state);
    first.installEventFilter(&observer); second.installEventFilter(&observer);
    root.show(); first.setFocus();
    QTimer::singleShot(0, [&] {
        require(root.windowHandle(), "real window exists");
        auto* event = new Tracked<QWindowSystemInterfacePrivate::KeyEvent>(state,
            root.windowHandle(), ulong(1), QEvent::KeyPress, int(Qt::Key_A),
            Qt::KeyboardModifiers(Qt::NoModifier), quint32(38), quint32('a'), quint32(0),
            QStringLiteral("a"), false, ushort(1));
        QWindowSystemInterfacePrivate::windowSystemEventQueue.append(event);
        QAbstractEventDispatcher::instance()->wakeUp();
        require(state->pending == 1 && first.text().isEmpty(), "queued is not delivered");
        state->onRetired = [&] {
            require(state->pending == 0 && state->acknowledgements == 1 && state->delivered == 1,
                    "actual key delivery then destructor acknowledgement");
            require(first.text() == "a" && second.text().isEmpty(), "first actual field");
            auto* key = new Tracked<QWindowSystemInterfacePrivate::KeyEvent>(state,
                root.windowHandle(), ulong(2), QEvent::KeyPress, int(Qt::Key_B),
                Qt::KeyboardModifiers(Qt::NoModifier), quint32(56), quint32('b'), quint32(0),
                QStringLiteral("b"), false, ushort(1));
            QWindowSystemInterfacePrivate::windowSystemEventQueue.append(key);
            second.setFocus(); // same IC window is not the identity of the recipient
            QAbstractEventDispatcher::instance()->wakeUp();
            state->onRetired = [&] {
                require(state->pending == 0 && first.text() == "a" && second.text() == "b",
                        "queued old key follows changed actual focus: generation is essential");
                auto* menu = new Tracked<QWindowSystemInterfacePrivate::ContextMenuEvent>(state,
                    root.windowHandle(), false, QPoint(), QPoint(), Qt::KeyboardModifiers(Qt::NoModifier));
                QWindowSystemInterfacePrivate::windowSystemEventQueue.append(menu);
                QAbstractEventDispatcher::instance()->wakeUp();
                state->onRetired = [&] {
                    require(state->pending == 0 && state->acknowledgements == 3 && state->delivered == 3,
                            "menu completion acknowledged");
                    ++state->pending;
                    QWindowSystemInterface::handleKeyEvent<QWindowSystemInterface::SynchronousDelivery>(
                        root.windowHandle(), QEvent::KeyPress, Qt::Key_C, Qt::NoModifier, "c");
                    --state->pending;
                    require(second.text() == "bc" && state->pending == 0 && state->delivered == 4,
                            "original synchronous branch completes before return");
                    qInfo() << "PASS Qt" << qVersion() << "async/menu/sync/focus/reentrant-dispatch";
                    app.quit();
                };
            };
        };
    });
    QTimer::singleShot(3000, [] { require(false, "fixture deadline"); });
    return app.exec();
}
