// Diagnostic only: actual QPlainTextEdit paint/layout using public Qt APIs.
// No TypeLay input plugin, source authority, cache flush or application fallback override.
#include <QApplication>
#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFontDatabase>
#include <QFrame>
#include <QGlyphRun>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QPaintEvent>
#include <QPlainTextEdit>
#include <QTextBlock>
#include <QTextDocument>
#include <QTextLayout>
#include <QTimer>
#include <QVBoxLayout>
#include <functional>
#include "../../../src/linux/native/common/overlay-design.h"

class PaintedEditor final : public QPlainTextEdit {
public:
    using QPlainTextEdit::QPlainTextEdit;
    std::function<void()> observed;
    bool armed = false;
protected:
    void paintEvent(QPaintEvent *event) override {
        QPlainTextEdit::paintEvent(event);
        if (!armed) return;
        armed = false;
        QTimer::singleShot(0, this, [this] { if (observed) observed(); });
    }
};

static QJsonArray families(const QFont &font) {
    return QJsonArray::fromStringList(font.families());
}

int main(int argc, char **argv) {
    QApplication app(argc, argv);
    // Verified complete font root, first region, output JSON.
    if (app.arguments().size() != 4) return 2;
    const auto args = app.arguments();
    const QDir root(args[1]);
    const QString primary = QStringLiteral("TypeLay Serif 15"), first = args[2];
    if (!root.isAbsolute() || (first != "SC" && first != "JP")) return 2;
    QJsonArray registered;
    size_t count = 0;
    const auto files = tl_overlay_font_files(&count);
    for (size_t index = 0; index < count; ++index) {
        const auto file = QString::fromUtf8(files[index]);
        const auto path = root.filePath(file);
        const int id = QFontDatabase::addApplicationFont(path);
        if (id < 0) return 3;
        registered.append(QJsonObject{{"file", file},
            {"families", QJsonArray::fromStringList(QFontDatabase::applicationFontFamilies(id))}});
    }
    QFrame card;
    card.setObjectName("typelay-overlay");
    auto *layout = new QVBoxLayout(&card);
    auto *editor = new PaintedEditor(&card);
    editor->setObjectName("typelay-editor");
    editor->setReadOnly(true);
    editor->setFocusPolicy(Qt::NoFocus);
    layout->addWidget(editor);
    card.resize(440, 140);
    const QStringList regions{first, first == "SC" ? "JP" : "SC"};
    QJsonArray observations;
    int scene = 0;
    auto save = [&](bool complete) {
        QFile output(args[3]);
        if (!output.open(QIODevice::WriteOnly)) return false;
        const QJsonObject result{{"qtVersion", QString::fromLatin1(qVersion())},
            {"qtBuildVersion", QStringLiteral(QT_VERSION_STR)},
            {"platform", QGuiApplication::platformName()}, {"primary", primary},
            {"registered", registered}, {"complete", complete}, {"observations", observations}};
        const auto bytes = QJsonDocument(result).toJson();
        return output.write(bytes) == bytes.size() && output.flush();
    };
    auto render = [&] {
        // Same production order: real text first, then parent QSS font update.
        editor->setPlainText(regions[scene] == "SC" ? QStringLiteral("汉字翻译") : QStringLiteral("日本語翻訳"));
        char font[128], css[16384];
        const auto family = tl_overlay_content_font("claude", regions[scene] == "SC" ? "zh-CN" : "ja",
            15, font, sizeof font);
        const int size = tl_overlay_qt_css(css, sizeof css, tl_overlay_palette(0, "clay", 0),
            family, tl_overlay_ui_font("claude"), 15, 0, 0, 0);
        if (!family || size < 0 || size_t(size) >= sizeof css) { save(false); app.exit(7); return; }
        card.setStyleSheet(QString::fromUtf8(css));
        editor->armed = true;
        editor->viewport()->update();
    };
    editor->observed = [&] {
        QJsonArray runs;
        for (auto block = editor->document()->begin(); block.isValid(); block = block.next()) {
            if (block.length() <= 1 || !block.layout()) continue;
            for (const auto &run : block.layout()->glyphRuns(0, block.length() - 1)) {
                const auto font = run.rawFont();
                int missing = 0;
                for (const auto glyph : run.glyphIndexes()) if (!glyph) ++missing;
                QJsonObject actual{{"family", font.familyName()}, {"weight", font.weight()},
                    {"glyphCount", qint64(run.glyphIndexes().size())}, {"missingGlyphs", missing}};
                for (const char *tag : {"name", "OS/2", "cmap", "CFF ", "glyf"}) {
                    const auto table = font.fontTable(tag);
                    if (!table.isEmpty()) actual.insert(QString::fromLatin1(tag),
                        QString::fromLatin1(QCryptographicHash::hash(table, QCryptographicHash::Sha256).toHex()));
                }
                runs.append(actual);
            }
        }
        observations.append(QJsonObject{{"region", regions[scene]},
            {"widgetFamilies", families(editor->font())},
            {"documentFamilies", families(editor->document()->defaultFont())},
            {"pixels", editor->font().pixelSize()}, {"visible", editor->isVisible()}, {"runs", runs}});
        if (runs.isEmpty()) { save(false); app.exit(4); return; }
        if (++scene == regions.size()) { app.exit(save(true) ? 0 : 5); return; }
        render();
    };
    QTimer::singleShot(2000, &app, [&] { save(false); app.exit(6); });
    render();
    card.show();
    return app.exec();
}
