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
#include <QInputMethodEvent>
#include <QSyntaxHighlighter>
#include <QTextBoundaryFinder>
#include <QTextCharFormat>
#include <QTextCursor>
#include <QPlainTextEdit>
#include <QTextBlock>
#include <QTextDocument>
#include <QTextLayout>
#include <QTimer>
#include <QVBoxLayout>
#include <functional>
#include <memory>
#include "../../../src/linux/native/common/overlay-design.h"

class PaintedEditor final : public QPlainTextEdit {
public:
    using QPlainTextEdit::QPlainTextEdit;
    std::function<void()> observed;
    bool armed = false;
    int paints = 0;
    int inputMethodEvents = 0;
    int commits = 0;
protected:
    void paintEvent(QPaintEvent *event) override {
        QPlainTextEdit::paintEvent(event);
        ++paints;
        if (!armed) return;
        armed = false;
        QTimer::singleShot(0, this, [this] {
            // A completed observer may install its successor synchronously.
            // Keep this invocation's callable alive until it returns.
            const auto callback = observed;
            if (callback) callback();
        });
    }
    void inputMethodEvent(QInputMethodEvent *event) override {
        ++inputMethodEvents;
        if (!event->commitString().isEmpty()) ++commits;
        QPlainTextEdit::inputMethodEvent(event);
    }
};

static QJsonArray families(const QFont &font) {
    return QJsonArray::fromStringList(font.families());
}

// Experimental display-only candidate. No change to the production module.
// Only complete Unicode grapheme clusters receive a family override. Other
// font properties and the document's text/format/Undo history are untouched.
class RegionalDisplay final : public QSyntaxHighlighter {
public:
    explicit RegionalDisplay(QTextDocument *document) : QSyntaxHighlighter(document) {}
    void choose(const QString &family) { family_ = family; rehighlight(); }
protected:
    void highlightBlock(const QString &text) override {
        if (family_.isEmpty()) return;
        struct Cluster { int start, end; char32_t base; bool emoji; };
        QList<Cluster> clusters;
        QTextBoundaryFinder boundaries(QTextBoundaryFinder::Grapheme, text);
        for (int start = 0, end = boundaries.toNextBoundary(); end >= 0;
             start = end, end = boundaries.toNextBoundary()) {
            char32_t base = 0;
            bool emoji = false;
            for (char32_t cp : text.mid(start, end - start).toUcs4()) {
                // These presentations stay on the existing emoji/fallback path,
                // including U+3030/U+303D followed by an emoji selector.
                emoji |= cp == 0xfe0f || cp == 0x200d || cp == 0x20e3;
                const auto category = QChar::category(cp);
                if (!base && category != QChar::Mark_NonSpacing
                    && category != QChar::Mark_SpacingCombining
                    && category != QChar::Mark_Enclosing) base = cp;
            }
            clusters.append({start, end, base, emoji});
        }
        const auto kana = [](char32_t cp) {
            return cp != 0x30fc && (QChar::script(cp) == QChar::Script_Hiragana
                || QChar::script(cp) == QChar::Script_Katakana);
        };
        QTextCharFormat format;
        format.setFontFamilies(QStringList{family_});
        int runStart = -1, runEnd = -1;
        for (qsizetype i = 0; i < clusters.size(); ++i) {
            const auto &cluster = clusters[i];
            const auto cp = cluster.base;
            const auto script = QChar::script(cp);
            bool cjk = script == QChar::Script_Han || script == QChar::Script_Hiragana
                || script == QChar::Script_Katakana || script == QChar::Script_Hangul
                || script == QChar::Script_Bopomofo
                || (cp >= 0x3000 && cp <= 0x303f)
                || (cp >= 0xff01 && cp <= 0xff60)
                || (cp >= 0xffe0 && cp <= 0xffe6);
            // U+30FC is CJK only beside an immediate kana cluster in this block;
            // spaces and other prolonged-sound marks do not supply context.
            if (cp == 0x30fc) cjk = (i > 0 && kana(clusters[i - 1].base))
                || (i + 1 < clusters.size() && kana(clusters[i + 1].base));
            cjk &= !cluster.emoji;
            if (cjk) {
                if (runStart < 0) runStart = cluster.start;
                runEnd = cluster.end;
            } else if (runStart >= 0) {
                setFormat(runStart, runEnd - runStart, format);
                runStart = -1;
            }
        }
        if (runStart >= 0) setFormat(runStart, runEnd - runStart, format);
    }
private:
    QString family_;
};

static QString digest(const QString &text) {
    return QString::fromLatin1(QCryptographicHash::hash(text.toUtf8(), QCryptographicHash::Sha256).toHex());
}

static QJsonArray actualRuns(QPlainTextEdit *editor, int start = 0, int length = -1) {
    QJsonArray runs;
    const auto block = editor->document()->begin();
    if (!block.isValid() || block.length() <= 1 || !block.layout()) return runs;
    if (length < 0) length = block.length() - 1;
    for (const auto &run : block.layout()->glyphRuns(start, length)) {
        if (runs.size() == 32) return {}; // Fail closed, never compare a truncated glyph list.
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
    return runs;
}

static QJsonObject draftState(PaintedEditor *editor) {
    const auto *document = editor->document();
    const auto *layout = document->begin().layout();
    return {{"textHash", digest(editor->toPlainText())}, {"length", editor->toPlainText().size()},
        {"position", editor->textCursor().position()}, {"anchor", editor->textCursor().anchor()},
        {"revision", document->revision()}, {"undoSteps", document->availableUndoSteps()},
        {"redoSteps", document->availableRedoSteps()}, {"commits", editor->commits},
        {"preeditPosition", layout ? layout->preeditAreaPosition() : -1},
        {"preeditHash", digest(layout ? layout->preeditAreaText() : QString())}};
}

// A fixed finite continuation of the existing two-order observer, not a new
// renderer or a replacement for the original cooperative/session assertions.
class DisplayExperiment final {
public:
    DisplayExperiment(QFrame &card, PaintedEditor *editor, const QString &first,
                      QJsonArray &observations, std::function<void(bool)> finish)
        : card_(card), editor_(editor), display_(editor->document()),
          regions_{first, first == "SC" ? "JP" : "SC", "KR"},
          observations_(observations), finish_(std::move(finish)) {
        latin_ = QStringLiteral("TypeLay e\u0301 123 | ");
        sc_ = QStringLiteral("汉字翻译"); jp_ = QStringLiteral("日本語翻訳"); kr_ = QStringLiteral("한글번역");
        kana_ = QStringLiteral("か\u3099ー"); han_ = QString::fromUcs4(U"\U0002000b\U000e0100");
        emoji_ = QString::fromUcs4(U"\u3030\ufe0f #\u20e3 \U0001f469\u200d\U0001f4bb");
        text_ = latin_ + sc_ + " " + jp_ + " " + kr_ + " " + kana_ + " " + han_
            + " " + emoji_ + QStringLiteral(" ー END");
        editor_->setReadOnly(false);
        editor_->setFocusPolicy(Qt::StrongFocus);
        editor_->setPlainText(text_);
        auto cursor = editor_->textCursor();
        cursor.movePosition(QTextCursor::End);
        cursor.insertText(QStringLiteral("!"));
        cursor.setPosition(2); cursor.setPosition(5, QTextCursor::KeepAnchor);
        editor_->setTextCursor(cursor);
        host_ = std::make_unique<PaintedEditor>();
        // Separate owned window: no inherited overlay QSS, like an unrelated
        // host control in the cooperating application.
        host_->setPlainText(text_);
        host_->setFont(QApplication::font());
        host_->setReadOnly(true);
        host_->setFocusPolicy(Qt::NoFocus);
        host_->setAttribute(Qt::WA_ShowWithoutActivating);
        host_->resize(600, 120);
        host_->show();
        card_.resize(600, 220);
        editor_->observed = [this] { observed(); };
        host_->observed = [this] { observed(); };
        setTheme(true, "SC");
        before_ = draftState(editor_);
        requestPaint();
    }
private:
    void setTheme(bool system, const QString &region) {
        char font[128], css[16384];
        auto family = QApplication::font().family();
        family.replace('\\', QStringLiteral("\\\\")); family.replace('\'', QStringLiteral("\\'"));
        const auto systemFont = ("'" + family + "'").toUtf8();
        const auto language = region == "JP" ? "ja" : region == "KR" ? "ko" : "zh-CN";
        const auto content = system ? systemFont.constData()
            : tl_overlay_content_font("claude", language, 15, font, sizeof font);
        const int size = tl_overlay_qt_css(css, sizeof css, tl_overlay_palette(0, "clay", 0),
            content, system ? systemFont.constData() : tl_overlay_ui_font("claude"), 15, 0, 1, 0);
        if (!content || size < 0 || size_t(size) >= sizeof css) { finish_(false); return; }
        card_.setStyleSheet(QString::fromUtf8(css));
        display_.choose(system ? QString() : QStringLiteral("Noto Serif ") + region);
    }
    void requestPaint() {
        editorPaint_ = editor_->paints + 1; hostPaint_ = host_->paints + 1; pendingPaint_ = true;
        host_->armed = true; editor_->armed = true;
        host_->viewport()->update(); editor_->viewport()->update();
    }
    QJsonObject ruleState() const {
        const auto *layout = editor_->document()->begin().layout();
        const auto formats = layout->formats();
        auto untouched = [&](int start, int length) {
            for (const auto &range : formats)
                if (range.start < start + length && range.start + range.length > start) return false;
            return true;
        };
        auto whole = [&](int start, int length) {
            for (const auto &range : formats)
                if (range.start <= start && range.start + range.length >= start + length) return true;
            return false;
        };
        bool boundariesOnly = true, familyOnly = true;
        QTextBoundaryFinder finder(QTextBoundaryFinder::Grapheme, editor_->toPlainText());
        QJsonArray spans;
        for (const auto &range : formats) {
            finder.setPosition(range.start); boundariesOnly &= finder.isAtBoundary();
            finder.setPosition(range.start + range.length); boundariesOnly &= finder.isAtBoundary();
            familyOnly &= range.format.properties().size() == 1 && range.format.hasProperty(QTextFormat::FontFamilies);
            if (spans.size() < 16) spans.append(QJsonObject{{"start", range.start}, {"length", range.length},
                {"families", QJsonArray::fromStringList(range.format.fontFamilies().toStringList())}});
        }
        return {{"graphemeBoundariesOnly", boundariesOnly}, {"familyPropertyOnly", familyOnly},
            {"latinUntouched", untouched(0, latin_.size())},
            {"emojiUntouched", untouched(text_.indexOf(emoji_), emoji_.size())},
            {"standaloneLongMarkUntouched", untouched(text_.lastIndexOf(QChar(0x30fc)), 1)},
            {"kanaCombiningAndLongMarkWhole", whole(text_.indexOf(kana_), kana_.size())},
            {"supplementaryHanAndSelectorWhole", whole(text_.indexOf(han_), han_.size())}, {"spans", spans}};
    }
    void observed() {
        // Both callbacks are actual widget paints. Whichever arrives second
        // consumes this one observation; no synthetic layout or added deadline.
        if (!pendingPaint_ || editor_->paints < editorPaint_ || host_->paints < hostPaint_) return;
        pendingPaint_ = false;
        const auto runs = actualRuns(editor_);
        if (runs.isEmpty() || !host_->paints) { finish_(false); return; }
        const auto hostRuns = actualRuns(host_.get());
        if (hostRuns.isEmpty()) { finish_(false); return; }
        QJsonObject record{{"phase", phase_}, {"editorInstance", QString::number(quintptr(editor_), 16)},
            {"documentInstance", QString::number(quintptr(editor_->document()), 16)},
            {"before", before_}, {"after", draftState(editor_)}, {"unchanged", before_ == draftState(editor_)},
            {"runs", runs}, {"hostRuns", hostRuns}, {"paintCount", editor_->paints}, {"hostPaintCount", host_->paints}};
        if (phase_ == 0) {
            systemRuns_ = runs; hostRuns_ = hostRuns;
        } else if (phase_ <= 3) {
            const auto region = regions_[phase_ - 1];
            const auto part = region == "SC" ? sc_ : region == "JP" ? jp_ : kr_;
            record.insert("region", region);
            record.insert("latinRuns", actualRuns(editor_, 0, latin_.size()));
            record.insert("regionalRuns", actualRuns(editor_, text_.indexOf(part), part.size()));
            record.insert("rules", ruleState());
        } else if (phase_ == 4) {
            record.insert("syntheticPreeditOnly", true);
            record.insert("inputMethodEventCount", editor_->inputMethodEvents);
        } else {
            record.insert("systemRunsRestored", runs == systemRuns_);
            record.insert("rangesCleared", editor_->document()->begin().layout()->formats().isEmpty());
        }
        record.insert("hostRunsUnchanged", hostRuns == hostRuns_);
        observations_.append(record);
        if (phase_ == 5) {
            editor_->undo();
            const bool undoExact = editor_->toPlainText() == text_;
            editor_->redo();
            const bool redoExact = editor_->toPlainText() == text_ + "!";
            observations_.append(QJsonObject{{"phase", "undo-redo"}, {"undoExact", undoExact},
                {"redoExact", redoExact}, {"commits", editor_->commits}});
            finish_(true); return;
        }
        ++phase_;
        if (phase_ <= 3) {
            before_ = draftState(editor_);
            setTheme(false, regions_[phase_ - 1]);
        } else if (phase_ == 4) {
            // Controlled public QInputMethodEvent, not a real IME-clear proof.
            auto cursor = editor_->textCursor();
            cursor.clearSelection();
            editor_->setTextCursor(cursor);
            QInputMethodEvent preedit(QStringLiteral("か\u3099"), {});
            QApplication::sendEvent(editor_, &preedit);
            before_ = draftState(editor_);
            display_.choose(QStringLiteral("Noto Serif JP"));
        } else {
            QInputMethodEvent clear;
            QApplication::sendEvent(editor_, &clear);
            before_ = draftState(editor_);
            setTheme(true, "SC");
        }
        requestPaint();
    }
    QFrame &card_;
    PaintedEditor *editor_;
    std::unique_ptr<PaintedEditor> host_;
    RegionalDisplay display_;
    QStringList regions_;
    QJsonArray &observations_;
    std::function<void(bool)> finish_;
    QString latin_, sc_, jp_, kr_, kana_, han_, emoji_, text_;
    QJsonObject before_;
    QJsonArray systemRuns_, hostRuns_;
    int phase_ = 0, editorPaint_ = 0, hostPaint_ = 0;
    bool pendingPaint_ = false;
};

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
    QJsonArray observations, displayObservations;
    std::unique_ptr<DisplayExperiment> experiment;
    int scene = 0;
    auto save = [&](bool complete) {
        QFile output(args[3]);
        if (!output.open(QIODevice::WriteOnly)) return false;
        const QJsonObject result{{"qtVersion", QString::fromLatin1(qVersion())},
            {"qtBuildVersion", QStringLiteral(QT_VERSION_STR)},
            {"platform", QGuiApplication::platformName()}, {"primary", primary},
            {"registered", registered}, {"complete", complete}, {"observations", observations},
            {"displayExperiment", displayObservations}};
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
        if (++scene == regions.size()) {
            // Preserve the two completed baseline paints if the independent
            // display experiment fails. This is not completed experiment evidence.
            if (!save(false)) { app.exit(5); return; }
            experiment = std::make_unique<DisplayExperiment>(card, editor, first, displayObservations,
                [&](bool complete) { app.exit(save(complete) && complete ? 0 : 5); });
            return;
        }
        render();
    };
    QTimer::singleShot(2000, &app, [&] { save(false); app.exit(6); });
    render();
    card.show();
    return app.exec();
}
