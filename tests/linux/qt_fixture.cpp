#include <QApplication>
#include <QInputMethodEvent>
#include <QKeyEvent>
#include <QLineEdit>
#include <QPlainTextEdit>
#include <QTextEdit>
#include <QVBoxLayout>

// Test-only IME delivery to a real standard widget, not a production injection API.
class Controls final : public QObject {
public:
    using QObject::QObject;
    bool eventFilter(QObject *, QEvent *event) override {
        if (event->type() != QEvent::KeyPress) return false;
        auto *key = static_cast<QKeyEvent *>(event);
        if (key->key() != Qt::Key_F8 && key->key() != Qt::Key_F9) return false;
        QInputMethodEvent ime(key->key() == Qt::Key_F8 ? QStringLiteral("拼音") : QString(), {});
        auto *target = QApplication::focusWidget();
        if (!target) return false;
        // QPA key delivery first visits QWindow. IME targets the actual focused
        // editor, not that transport window.
        QApplication::sendEvent(target, &ime);
        return true;
    }
};
int main(int argc, char **argv) {
    QApplication app(argc, argv);
    QWidget window;
    window.setWindowTitle(QStringLiteral("TypeLay Qt contract fixture")); window.resize(650, 480);
    auto *layout = new QVBoxLayout(&window);
    auto *first = new QLineEdit(QStringLiteral("alpha 😀 omega")); first->setObjectName("fixture-first");
    auto *second = new QLineEdit(QStringLiteral("second field")); second->setObjectName("fixture-second");
    auto *password = new QLineEdit(QStringLiteral("fixture-only-private")); password->setObjectName("fixture-password"); password->setEchoMode(QLineEdit::Password);
    auto *plain = new QPlainTextEdit(QStringLiteral("first\u00a0line\nsecond line")); plain->setObjectName("fixture-plain"); plain->setTabChangesFocus(true);
    auto *text = new QTextEdit; text->setAcceptRichText(false); text->setPlainText(QStringLiteral("plain text editor")); text->setObjectName("fixture-text");
    layout->addWidget(first); layout->addWidget(second); layout->addWidget(password); layout->addWidget(plain); layout->addWidget(text);
    Controls controls; app.installEventFilter(&controls);
    window.show(); first->setFocus(); first->setCursorPosition(first->text().size());
    return app.exec();
}
