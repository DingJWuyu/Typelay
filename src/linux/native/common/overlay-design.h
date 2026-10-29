#pragma once
#include <stdio.h>
#include <string.h>

/* Source-app presentation only. These tokens never own a source identity,
 * editor buffer, input event or acceptance permit. GTK and Qt share the same
 * warm-paper palette; the host supplies already-persisted appearance choices. */
typedef struct {
    const char *surface, *ink, *secondary, *tertiary, *card, *line, *hover;
    const char *accent, *accent_ink, *accent_soft, *warning;
} TlOverlayPalette;

typedef enum { TL_MOTION_SYSTEM, TL_MOTION_FULL, TL_MOTION_REDUCE } TlOverlayMotion;
static inline TlOverlayMotion tl_overlay_motion(const char *mode, int legacy_reduce) {
    if (mode && strcmp(mode, "full") == 0) return TL_MOTION_FULL;
    if (mode && strcmp(mode, "reduce") == 0) return TL_MOTION_REDUCE;
    if (mode && strcmp(mode, "system") == 0) return TL_MOTION_SYSTEM;
    return legacy_reduce ? TL_MOTION_REDUCE : TL_MOTION_SYSTEM;
}
static inline int tl_overlay_motion_reduced(TlOverlayMotion mode, int system_reduced) {
    return mode == TL_MOTION_REDUCE || (mode == TL_MOTION_SYSTEM && system_reduced);
}

/* Same bounded spring as the approved prototype. Retargeting keeps velocity;
 * callers own lifetime checks and may only apply the returned visual size. */
typedef struct { double value, target, velocity, stiffness, damping; } TlOverlaySpring;
static inline void tl_overlay_spring_snap(TlOverlaySpring *spring, double value) {
    spring->value = value; spring->target = value; spring->velocity = 0;
}
static inline void tl_overlay_spring_to(TlOverlaySpring *spring, double target) {
    const int growing = target > spring->value;
    spring->target = target;
    spring->stiffness = growing ? 340 : 560;
    spring->damping = growing ? 26 : 40;
}
static inline int tl_overlay_spring_step(TlOverlaySpring *spring, double dt) {
    if (dt <= 0) dt = 1.0 / 60.0;
    if (dt > 1.0 / 30.0) dt = 1.0 / 30.0;
    const int steps = (int)(dt * 240.0 + 0.999999);
    const double h = dt / (double)steps;
    for (int i = 0; i < steps; ++i) {
        spring->velocity += (-spring->stiffness * (spring->value - spring->target) - spring->damping * spring->velocity) * h;
        spring->value += spring->velocity * h;
    }
    const double distance = spring->value - spring->target;
    if (spring->velocity > -.02 && spring->velocity < .02 && distance > -.05 && distance < .05) {
        tl_overlay_spring_snap(spring, spring->target);
        return 0;
    }
    return 1;
}

static inline TlOverlayPalette tl_overlay_palette(int dark, const char *accent, int solid) {
    TlOverlayPalette p = dark
        ? (TlOverlayPalette){solid ? "#282724" : "rgba(40,39,36,0.94)", "#f4f3ee", "#b5b3ab", "#9a988f",
            "#2f2e2b", "rgba(255,255,255,0.10)", "rgba(255,255,255,0.09)",
            "#d97757", "#ec9a7c", "rgba(217,119,87,0.14)", "#e5ad55"}
        : (TlOverlayPalette){solid ? "#fbfaf7" : "rgba(251,250,247,0.96)", "#141413", "#5c5b56", "#74726c",
            "#ffffff", "rgba(20,20,19,0.10)", "rgba(20,20,19,0.08)",
            "#d97757", "#ad4f2d", "rgba(217,119,87,0.14)", "#a5670e"};
    if (accent && strcmp(accent, "ocean") == 0) {
        p.accent = "#2f7fe0"; p.accent_ink = dark ? "#7ab4ff" : "#1f63bb"; p.accent_soft = "rgba(47,127,224,0.13)";
    } else if (accent && strcmp(accent, "sage") == 0) {
        p.accent = "#5d9a6e"; p.accent_ink = dark ? "#8fcc9f" : "#3c7650"; p.accent_soft = "rgba(93,154,110,0.15)";
    } else if (accent && strcmp(accent, "iris") == 0) {
        p.accent = "#8a6cd8"; p.accent_ink = dark ? "#b9a4f5" : "#6a4cbc"; p.accent_soft = "rgba(138,108,216,0.14)";
    } else if (accent && strcmp(accent, "graphite") == 0) {
        p.accent = "#77756f"; p.accent_ink = dark ? "#c4c2bb" : "#55534e"; p.accent_soft = "rgba(119,117,111,0.16)";
    }
    return p;
}

static inline const char *tl_overlay_content_font(const char *font, const char *language) {
    if (font && strcmp(font, "system") == 0) return NULL;
    const int sans = font && strcmp(font, "sans") == 0;
    if (language && strncmp(language, "ja", 2) == 0)
        return sans ? "'Inter', 'Noto Sans JP', sans-serif" : "'Source Serif 4', 'Noto Serif JP', serif";
    if (language && strncmp(language, "ko", 2) == 0)
        return sans ? "'Inter', 'Noto Sans KR', sans-serif" : "'Source Serif 4', 'Noto Serif KR', serif";
    if (language && (strcmp(language, "zh-TW") == 0 || strcmp(language, "zh-HK") == 0 || strcmp(language, "zh-Hant") == 0))
        return sans ? "'Inter', 'Noto Sans TC', sans-serif" : "'Source Serif 4', 'Noto Serif TC', serif";
    return sans ? "'Inter', 'Noto Sans SC', sans-serif" : "'Source Serif 4', 'Noto Serif SC', serif";
}

static inline const char *tl_overlay_ui_font(const char *font) {
    return font && strcmp(font, "system") == 0 ? NULL : "'Inter', 'Noto Sans SC', sans-serif";
}

/* Fixed files from the verified offline bundle, never a wire-supplied path or
 * a recursive search through the source application's fonts. */
static inline const char *const *tl_overlay_font_files(size_t *count) {
    static const char *const files[] = {
        "inter/Inter-Regular.ttf", "inter/Inter-Medium.ttf", "inter/Inter-SemiBold.ttf",
        "source-serif/SourceSerif4-Regular.ttf", "source-serif/SourceSerif4-Semibold.ttf",
        "noto-sans/NotoSansSC-Regular.otf", "noto-sans/NotoSansSC-Medium.otf",
        "noto-sans/TypeLayNotoSansSC-SemiBold.ttf",
        "noto-sans/NotoSansJP-Regular.otf", "noto-sans/NotoSansJP-Medium.otf",
        "noto-sans/NotoSansKR-Regular.otf", "noto-sans/NotoSansKR-Medium.otf",
        "noto-serif/NotoSerifSC-Regular.otf", "noto-serif/NotoSerifSC-Medium.otf",
        "noto-serif/NotoSerifJP-Regular.otf", "noto-serif/NotoSerifJP-Medium.otf",
        "noto-serif/NotoSerifKR-Regular.otf", "noto-serif/NotoSerifKR-Medium.otf"
    };
    *count = sizeof files / sizeof files[0];
    return files;
}

/* Exact UI messages only. Unknown model/host diagnostics remain untouched. */
static inline const char *tl_overlay_ui(const char *value, int english) {
    static const char *const messages[][2] = {
        {"重译", "Retranslate"}, {"重新翻译", "Retranslate"}, {"展开", "Expand"}, {"收起", "Collapse"},
        {"恢复", "Resume"}, {"已暂停", "Paused"}, {"更多", "More"}, {"源语言", "From"}, {"目标语言", "To"},
        {"应用语言方向", "Apply languages"}, {"交换语言", "Swap languages"}, {"当前应用风格", "Style in this app"},
        {"继承", "Use default"}, {"自然", "Natural"}, {"正式", "Formal"}, {"✓ 继承", "✓ Use default"},
        {"✓ 自然", "✓ Natural"}, {"✓ 正式", "✓ Formal"}, {"换种说法", "Rephrase"},
        {"加入术语表", "Add to glossary"}, {"复制译文", "Copy translation"}, {"排除此应用", "Hide in this app"},
        {"不要在这个应用中显示", "Hide in this app"}, {"设置…", "Settings…"}, {"点击译文编辑", "Click to edit translation"},
        {"编辑译文", "Edit translation"}, {"翻译中", "Translating"}, {"更新中", "Updating"}, {"未能更新", "Couldn't update"},
        {"输入中", "Typing"}, {"仅预览", "Preview only"}, {"已修改", "Edited"}, {"编辑中", "Editing"},
        {"保存", "Save"}, {"取消", "Cancel"}, {"原文", "Source"}, {"译文", "Translation"}
    };
    if (!value) return "";
    for (size_t i = 0; i < sizeof messages / sizeof messages[0]; ++i)
        if (strcmp(value, messages[i][0]) == 0 || strcmp(value, messages[i][1]) == 0)
            return messages[i][english ? 1 : 0];
    return value;
}

static inline const char *tl_overlay_phase(const char *state, const char *text, const char *status, int english) {
    if (!state) return status ? status : "";
    if (strcmp(state, "TranslationFailed") == 0) return tl_overlay_ui("未能更新", english);
    if (status && *status && strcmp(status, "译文就绪") != 0 && strcmp(status, "正在翻译") != 0
        && strcmp(status, "编辑中") != 0 && strcmp(status, "输入中") != 0 && strcmp(status, "已暂停") != 0)
        return status;
    if (strcmp(state, "Translating") == 0) return tl_overlay_ui(text && *text ? "更新中" : "翻译中", english);
    if (strcmp(state, "EditedTranslationPending") == 0) return tl_overlay_ui("已修改", english);
    if (strcmp(state, "Invalid") == 0) return tl_overlay_ui("输入中", english);
    if (strcmp(state, "Paused") == 0) return tl_overlay_ui("已暂停", english);
    return "";
}

/* CSS is compiled into each module: no runtime stylesheet lookup.
 * Native popovers cannot sample/blur the source application, so glass uses a
 * high-opacity readable fallback rather than pretending to provide backdrop blur. */
static inline int tl_overlay_gtk_css(char *out, size_t capacity, TlOverlayPalette p,
        const char *font, const char *ui_font, int text_size, int capsule, int stale, int reduced, int gtk4) {
    return snprintf(out, capacity,
        "%s { background: %s; color: %s; border: 1px solid %s; border-radius: %dpx; padding: 0;"
        " box-shadow: 0 12px 32px rgba(20,20,19,0.18); }"
        "#typelay-overlay { %s%s%s font-size: 12px; }"
        "#typelay-overlay #typelay-header { padding: %s; color: %s; min-height: 26px; }"
        "#typelay-overlay #typelay-footer { margin: 0; padding: 0; border: 0; color: %s; font-size: 12px; }"
        "#typelay-overlay button { padding: 0 6px; min-height: 26px; min-width: 0; border: 0; border-radius: 7px;"
        " background: transparent; color: %s; box-shadow: none; text-shadow: none; font-weight: normal; }"
        "#typelay-overlay button:hover { background: %s; color: %s; }"
        "#typelay-overlay button:disabled { opacity: 0.4; }"
        "#typelay-overlay #typelay-menu-trigger { border-radius: 13px; padding: 0 5px; }"
        "#typelay-overlay #typelay-resume { color: %s; font-weight: 600; border-radius: 13px; }"
        "#typelay-overlay #typelay-dot { color: %s; font-size: 9px; min-width: 7px; }"
        "#typelay-overlay #typelay-status { color: %s; font-size: 11px; }"
        "#typelay-overlay #typelay-status.pending { color: %s; }"
        "#typelay-overlay #typelay-candidate, #typelay-overlay #typelay-candidate text { background: transparent;"
        " color: %s; %s%s%s font-size: %dpx; caret-color: %s; }"
        "#typelay-overlay #typelay-candidate { border: 1px solid transparent; border-radius: 10px; outline: none; box-shadow: none; }"
        "#typelay-overlay #typelay-candidate.editable-preview:hover { background: %s; }"
        "#typelay-overlay #typelay-candidate:focus, #typelay-overlay #typelay-candidate:focus > text {"
        " background: %s; border-color: %s; border-radius: 10px; }"
        "#typelay-overlay #typelay-language-menu { border-top: 1px solid %s; padding: 8px; }"
        "#typelay-overlay #typelay-language-menu button { min-height: 30px; }"
        "#typelay-overlay #typelay-skeleton { padding: 12px 16px; }"
        "#typelay-overlay .typelay-skeleton-line { min-height: 9px; border-radius: 5px; background: %s; }"
        "@keyframes typelay-breathe { 0%% { opacity: 0.45; } 50%% { opacity: 1; } 100%% { opacity: 0.45; } }"
        "#typelay-overlay .typelay-busy { animation: %s; }",
        gtk4 ? "#typelay-overlay > contents" : "#typelay-overlay", p.surface, p.ink, p.line, 18,
        ui_font ? "font-family:" : "", ui_font ? ui_font : "", ui_font ? ";" : "",
        capsule ? "4px 8px" : "0 10px 8px 14px", p.secondary, p.secondary, p.secondary, p.hover, p.ink,
        p.accent_ink, capsule ? p.tertiary : p.accent, p.tertiary, p.warning,
        stale ? p.tertiary : p.ink, font ? "font-family:" : "", font ? font : "", font ? ";" : "",
        text_size, p.accent, p.hover, p.card, p.accent,
        p.line, p.hover, reduced ? "none" : "typelay-breathe 1200ms ease-in-out infinite");
}

/* A separately allocated GTK3 menu must not participate in candidate sizing. */
static inline int tl_overlay_gtk_more_css(char *out, size_t capacity, TlOverlayPalette p,
        const char *ui_font, const char *accent) {
    const char *strong = accent && strcmp(accent, "ocean") == 0 ? "#1f63bb"
        : accent && strcmp(accent, "sage") == 0 ? "#3c7650"
        : accent && strcmp(accent, "iris") == 0 ? "#6a4cbc"
        : accent && strcmp(accent, "graphite") == 0 ? "#55534e" : "#b5552f";
    return snprintf(out, capacity,
        "#typelay-more-popup { background:%s; color:%s; border:1px solid %s; border-radius:12px; padding:5px;"
        " box-shadow:0 12px 32px rgba(20,20,19,0.18); %s%s%s font-size:13px; }"
        "#typelay-more-popup button { background:transparent; color:%s; border:0; border-radius:7px;"
        " padding:0 10px; min-height:30px; min-width:0; box-shadow:none; text-shadow:none; font-weight:normal; }"
        "#typelay-more-popup button:hover { background:%s; color:%s; }"
        "#typelay-more-popup button:disabled { color:%s; opacity:0.55; }"
        "#typelay-more-popup #typelay-style-row { padding:6px 10px 8px; }"
        "#typelay-more-popup #typelay-style-segments { background:%s; border-radius:6px; padding:2px; }"
        "#typelay-more-popup #typelay-style-segments button { min-height:22px; padding:0 8px; font-size:11.5px; }"
        "#typelay-more-popup #typelay-style-segments button.selected { background:%s; color:%s; }"
        "#typelay-more-popup #typelay-action-feedback { color:%s; padding:4px 10px; font-size:11px; }"
        "#typelay-more-popup separator { background:%s; min-height:1px; margin:5px 10px; }",
        p.surface, p.ink, p.line, ui_font ? "font-family:" : "", ui_font ? ui_font : "", ui_font ? ";" : "",
        p.ink, strong, "#ffffff", p.tertiary, p.hover, p.card, p.ink, p.secondary, p.line);
}

static inline int tl_overlay_qt_css(char *out, size_t capacity, TlOverlayPalette p,
        const char *font, const char *ui_font, int text_size, int stale, int editing, int editable) {
    return snprintf(out, capacity,
        "QFrame#typelay-overlay { background:%s; color:%s; border:1px solid %s; border-radius:18px;"
        " font-family:%s; font-size:12px; }"
        "QFrame#typelay-overlay QLabel, QFrame#typelay-overlay QRadioButton { color:%s; background:transparent; }"
        "QFrame#typelay-overlay QLabel#typelay-status { color:%s; font-size:11px; }"
        "QFrame#typelay-overlay QLabel#typelay-hint { color:%s; font-size:12px; }"
        "QFrame#typelay-overlay QLabel#typelay-dot { color:%s; font-size:9px; }"
        "QFrame#typelay-overlay QPushButton { color:%s; background:transparent; border:0; border-radius:7px;"
        " padding:0 6px; min-height:26px; font-size:12px; }"
        "QFrame#typelay-overlay QPushButton:hover, QFrame#typelay-overlay QPushButton:pressed { color:%s; background:%s; }"
        "QFrame#typelay-overlay QPushButton#typelay-menu-trigger { border-radius:13px; padding:0 5px; }"
        "QFrame#typelay-overlay QPushButton#typelay-resume { color:%s; font-weight:600; }"
        "QFrame#typelay-overlay QWidget#typelay-menu { border-top:1px solid %s; }"
        "QFrame#typelay-overlay QPlainTextEdit#typelay-editor { background:%s; color:%s; border:1px solid %s;"
        " border-radius:10px; padding:4px; font-family:%s; font-size:%dpx; selection-background-color:%s; selection-color:%s; }"
        "QFrame#typelay-overlay QPlainTextEdit#typelay-editor:hover { background:%s; }"
        "QFrame#typelay-overlay QPlainTextEdit#typelay-editor:focus { border-color:%s; background:%s; }"
        "QFrame#typelay-overlay QWidget#typelay-glossary QLineEdit { background:%s; color:%s; border:1px solid %s;"
        " border-radius:8px; padding:5px; selection-background-color:%s; selection-color:%s; }"
        "QFrame#typelay-overlay QWidget#typelay-glossary QLineEdit:focus { border-color:%s; }",
        p.surface, p.ink, p.line, ui_font, p.ink, p.tertiary, p.secondary, p.accent, p.secondary,
        p.ink, p.hover, p.accent_ink, p.line, editing ? p.card : "transparent", stale ? p.tertiary : p.ink,
        editing ? p.accent : "transparent", font, text_size, p.accent_ink, p.card,
        editing ? p.card : editable ? p.hover : "transparent", p.accent, p.card,
        p.card, p.ink, p.line, p.accent_ink, p.card, p.accent);
}

#if defined(GTK_MAJOR_VERSION)
#include <pango/pangocairo.h>
#include <pango/pangofc-fontmap.h>
#include <fontconfig/fontconfig.h>
static inline gboolean tl_overlay_gtk_motion_reduced(GtkWidget *widget, TlOverlayMotion mode) {
    if (mode != TL_MOTION_SYSTEM) return mode == TL_MOTION_REDUCE;
    GtkSettings *settings = gtk_widget_get_settings(widget);
    gboolean enabled = FALSE;
    gint reduced = 0;
    g_object_get(settings, "gtk-enable-animations", &enabled, NULL);
    if (g_object_class_find_property(G_OBJECT_GET_CLASS(settings), "gtk-interface-reduced-motion"))
        g_object_get(settings, "gtk-interface-reduced-motion", &reduced, NULL);
    return !enabled || reduced != 0;
}
/* Only TypeLay's widget subtree uses this private map. In particular, no call
 * changes Fontconfig's current config or Pango's process-default map. */
static inline PangoFontMap *tl_overlay_font_map(void) {
    /* GTK UI thread only. Retain one private map for this module's lifetime. */
    static PangoFontMap *map;
    static gboolean attempted;
    if (attempted) return map;
    attempted = TRUE;
    const char *explicit_root = g_getenv("TYPELAY_UI_FONT_ROOT");
    const char *home = g_getenv("TYPELAY_HOME");
    char *root = explicit_root ? g_strdup(explicit_root)
        : home && g_path_is_absolute(home) ? g_build_filename(home, "fonts", NULL) : NULL;
    if (!root || !g_path_is_absolute(root)) { g_free(root); return NULL; }
    size_t count;
    const char *const *files = tl_overlay_font_files(&count);
    /* Build one private config, rather than rebuilding the system font set
     * after every add_font_file. This API also supports Pango before 1.56. */
    FcConfig *config = FcInitLoadConfig();
    gboolean loaded = config != NULL;
    for (size_t i = 0; config && i < count; i++) {
        char *path = g_build_filename(root, files[i], NULL);
        if (!FcConfigAppFontAddFile(config, (const FcChar8 *)path)) loaded = FALSE;
        g_free(path);
    }
    if (loaded) loaded = FcConfigBuildFonts(config);
    if (loaded) {
        PangoFontMap *candidate = pango_cairo_font_map_new_for_font_type(CAIRO_FONT_TYPE_FT);
        if (candidate && PANGO_IS_FC_FONT_MAP(candidate)) {
            pango_fc_font_map_set_config(PANGO_FC_FONT_MAP(candidate), config);
            map = candidate;
        } else { if (candidate) g_object_unref(candidate); loaded = FALSE; }
    }
    if (config) FcConfigDestroy(config);
    if (!loaded) g_warning("TypeLay offline font bundle could not be loaded");
    g_free(root);
    return map;
}
static inline void tl_overlay_install_fonts(GtkWidget *card, const char *font) {
    /* The connection-time cache may exist, but system never attaches it. */
    PangoFontMap *map = font && strcmp(font, "system") == 0 ? NULL : tl_overlay_font_map();
    gtk_widget_set_font_map(card, map);
}
#endif
