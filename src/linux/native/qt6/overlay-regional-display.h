#pragma once

#include <QSyntaxHighlighter>
#include <QTextBoundaryFinder>
#include <QTextCharFormat>
#include <QTextDocument>
#include <limits>

// Display-only formatting of the owned overlay document.
// Only complete Unicode grapheme clusters receive a family override. Other
// font properties and the document's text/format/Undo history are untouched.
// rehighlight DOES advance QTextDocument::revision; callers must retire old
// revision-bound input/form/motion before an explicit presentation change.
class OverlayRegionalDisplay final : public QSyntaxHighlighter {
public:
    explicit OverlayRegionalDisplay(QTextDocument *document) : QSyntaxHighlighter(document) {}
    // Repeated presentation is not a document edit. In particular, do not
    // advance its real revision (and invalidate its native input ticket) for
    // the same display family. Empty selects the inherited system font path.
    bool choose(const QString &family) {
        if (family_ == family) return false;
        family_ = family;
        rehighlight();
        return true;
    }
protected:
    void highlightBlock(const QString &text) override {
        // setFormat uses int offsets; reject an unrepresentable block before
        // converting the boundary API's qsizetype (including its -1 sentinel).
        if (family_.isEmpty() || text.size() > std::numeric_limits<int>::max()) return;
        struct Cluster { int start, end; char32_t base; bool emoji; };
        QList<Cluster> clusters;
        QTextBoundaryFinder boundaries(QTextBoundaryFinder::Grapheme, text);
        for (int start = 0, end = static_cast<int>(boundaries.toNextBoundary()); end >= 0;
             start = end, end = static_cast<int>(boundaries.toNextBoundary())) {
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

