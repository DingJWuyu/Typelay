"""Native GTK presentation primitives for the TypeLay design system.

These controls own no settings or source-field authority. Their callbacks carry
user intent; committed values are always rendered by the settings owner.
"""

import ctypes
import json
import os
import sys
from itertools import count
from pathlib import Path

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("PangoCairo", "1.0")
from gi.repository import Gdk, GLib, Gtk, Pango, PangoCairo  # noqa: E402

ACCENTS = {
    "clay": ("#d97757", "#ad4f2d", "#ec9a7c"),
    "ocean": ("#2f7fe0", "#2363ad", "#78b4fa"),
    "sage": ("#5d9a6e", "#386b46", "#8dcca0"),
    "iris": ("#8a6cd8", "#6849b3", "#b6a0ee"),
    "graphite": ("#77756f", "#5c5b56", "#c4c1b7"),
}

CSS = """
.typelay-surface.reduce-motion * { transition: none; animation: none; }
window.typelay-surface.quick-panel { background: rgba(251,250,247,.96); border: 1px solid rgba(20,20,19,.1); border-radius: 18px; }
window.typelay-surface.quick-panel.reduce-transparency { background: #fbfaf7; }
window.typelay-surface.quick-panel.settings-dark { background: rgba(44,43,40,.96); border-color: rgba(255,255,255,.1); }
window.typelay-surface.quick-panel.settings-dark.reduce-transparency { background: #2c2b28; }
.typelay-surface { background: #f7f6f2; color: #141413; font-family: 'Inter', 'Noto Sans SC', sans-serif; font-size: 13px; }
.typelay-surface label { color: inherit; }
.typelay-surface .settings-sidebar { background: #eeece5; border: 1px solid rgba(20,20,19,.085); border-radius: 12px; padding: 14px 10px 10px; margin: 8px; }
.typelay-surface .settings-brand { font-size: 13px; font-weight: 600; }
.typelay-surface .settings-navigation, .typelay-surface .settings-navigation list { background: transparent; border: 0; }
.typelay-surface .settings-navigation row { border: 0; border-radius: 8px; padding: 0 8px; min-height: 32px; margin: 0 0 1px; }
.typelay-surface .settings-navigation row:selected { background: rgba(20,20,19,.08); color: #141413; font-weight: 600; }
.typelay-surface .settings-navigation row:hover { background: rgba(20,20,19,.045); }
.typelay-surface .settings-search-results, .typelay-surface .settings-search-results viewport { background: transparent; }
.typelay-surface button.settings-search-result { background: transparent; border: 0; box-shadow: none; border-radius: 8px; padding: 6px 8px; min-height: 32px; font-size: 13px; }
.typelay-surface button.settings-search-result:hover { background: rgba(20,20,19,.045); }
.typelay-surface .search-result-page { font-size: 11px; color: #74726c; }
.typelay-surface .settings-search-empty { padding: 8px; color: #74726c; }
.typelay-surface .settings-search-highlight { background-color: alpha(@accent,.14); }
.typelay-surface.settings-dark button.settings-search-result:hover { background: rgba(255,255,255,.05); }
.typelay-surface.settings-dark .search-result-page, .typelay-surface.settings-dark .settings-search-empty { color: #b5b3ab; }
.typelay-surface .nav-icon { color: white; border-radius: 6px; padding: 4px; min-width: 14px; min-height: 14px; }
.typelay-surface .settings-page, .typelay-surface .settings-page viewport { background: transparent; }
.typelay-surface .settings-page-title { font-family: 'Source Serif 4', 'Noto Serif SC', serif; font-size: 22px; font-weight: 500; margin-bottom: 0; }
.typelay-surface .settings-section-title { font-size: 12px; font-weight: 600; color: #5c5b56; margin: 8px 4px 0; }
.typelay-surface .settings-group { background: #ffffff; border: 0; border-radius: 12px; box-shadow: inset 0 0 0 1px rgba(20,20,19,.085), 0 1px 2px rgba(20,20,19,.04); }
.typelay-surface .settings-row { padding: 8px 14px; min-height: 28px; }
.typelay-surface .settings-row + .settings-row { box-shadow: inset 0 1px rgba(20,20,19,.06); }
.typelay-surface .hero-row { min-height: 56px; }
.typelay-surface .headline { font-size: 15px; font-weight: 600; }
.typelay-surface .dim-label { color: #74726c; opacity: 1; font-size: 12px; }
.typelay-surface .settings-warning { color: #a5670e; font-size: 12px; }
.typelay-surface .settings-notice { padding: 12px 14px; background: #fff; border-radius: 12px; border: 1px solid rgba(20,20,19,.085); }
.typelay-surface button { background-image: none; background-color: #ffffff; color: #141413; border: 1px solid rgba(20,20,19,.16); border-radius: 8px; padding: 3px 12px; min-height: 18px; font-size: 12.5px; box-shadow: 0 1px 2px rgba(20,20,19,.04); }
.typelay-surface button:hover { background-color: #f2f0e9; }
.typelay-surface button:disabled { opacity: .5; }
.typelay-surface .flat { background: transparent; border-color: transparent; box-shadow: none; }
.typelay-surface .flat:hover { background: rgba(20,20,19,.08); }
.typelay-surface entry { background: #fff; color: #141413; border: 1px solid rgba(20,20,19,.16); border-radius: 8px; padding: 4px 8px; min-height: 22px; box-shadow: none; }
.typelay-surface entry:focus, .typelay-surface button:focus { border-color: @accent; outline-color: @accent; }
.typelay-surface .settings-search { background: rgba(20,20,19,.08); border: 0; min-height: 20px; }
.typelay-surface switch { min-width: 30px; min-height: 16px; border: 0; border-radius: 12px; padding: 2px; background: rgba(20,20,19,.16); box-shadow: none; }
.typelay-surface switch:checked { background: @accent; }
.typelay-surface switch slider { background: #fff; border: 0; border-radius: 50%; min-width: 16px; min-height: 16px; box-shadow: 0 1px 2px rgba(0,0,0,.16); }
.typelay-surface .segments { border-radius: 8px; padding: 2px; background: rgba(20,20,19,.08); }
.typelay-surface .segments button { background: transparent; border: 0; border-radius: 6px; box-shadow: none; padding: 3px 12px; min-height: 18px; min-width: 32px; font-size: 12px; font-weight: 500; }
.typelay-surface .segments button:checked { background: #fff; box-shadow: 0 1px 3px rgba(20,20,19,.12); }
.typelay-surface button.language-button { min-height: 42px; padding: 0 12px 0 14px; border-radius: 10px; background: rgba(20,20,19,.045); border: 1px solid rgba(20,20,19,.085); box-shadow: none; }
.typelay-surface button.language-swap { min-width: 36px; min-height: 36px; padding: 0; border: 0; border-radius: 50%; color: #74726c; }
.typelay-surface button.language-swap:disabled { opacity: .35; }
.typelay-surface .language-chevron, .typelay-surface .language-swap image { -gtk-icon-effect: none; -gtk-icon-shadow: none; }
.typelay-surface .language-button, .typelay-surface .language-option { text-shadow: none; }
.typelay-surface.settings-dark button.language-swap { color: #b5b3ab; }
.typelay-surface .language-chevron { color: #74726c; }
.typelay-surface.settings-dark .language-chevron { color: #9a988f; }
.typelay-surface .language-name { font-size: 15px; font-weight: 500; }
.typelay-surface .model-warning-row { min-height: 34px; }
.typelay-surface .language-popover { background: transparent; border: 0; box-shadow: none; padding: 0; }
/* GTK3 has no backdrop-filter. This native popover uses the matching opaque
   material instead of exposing unblurred text; the saved preference is unchanged. */
.typelay-surface .language-panel { padding: 6px; background: #fbfaf7; border-radius: 12px; box-shadow: 0 4px 12px rgba(20,20,19,.08), inset 0 0 0 1px rgba(255,255,255,.75); }
.typelay-surface .language-search, .typelay-surface .language-search:focus { min-height: 30px; padding: 0 9px; border: 0; border-radius: 8px; outline: 0; box-shadow: none; background: rgba(20,20,19,.045); }
.typelay-surface .language-search image { color: #74726c; -gtk-icon-shadow: none; -gtk-icon-transform: scale(.875); }
.typelay-surface .language-panel > label { font-size: 12px; }
.typelay-surface.reduce-transparency .language-panel { background: #fbfaf7; }
.typelay-surface .language-popover button.language-option { border: 0; box-shadow: none; background: transparent; padding: 0 8px; min-height: 30px; border-radius: 7px; }
.typelay-surface .language-popover button.language-option:hover, .typelay-surface .language-popover button.language-option.keyboard { background: @accent; color: white; }
.typelay-surface.settings-dark button.language-button { background: rgba(255,255,255,.05); border-color: rgba(255,255,255,.10); }
.typelay-surface.settings-dark .language-panel { background: #2c2b28; box-shadow: 0 4px 14px rgba(0,0,0,.3), inset 0 0 0 1px rgba(255,255,255,.1); }
.typelay-surface.settings-dark .language-search, .typelay-surface.settings-dark .language-search:focus { background: rgba(255,255,255,.05); }
.typelay-surface.settings-dark .language-search image { color: #9a988f; }
.typelay-surface.settings-dark.reduce-transparency .language-panel { background: #2c2b28; }
/* Glossary: the three read-only cells and their one inline editor share widths. */
.typelay-surface .glossary-search { min-height: 26px; padding: 0 8px; background: #fff; }
.typelay-surface .glossary-heading { padding: 10px 14px 8px; font-size: 11.5px; font-weight: 600; color: #74726c; }
.typelay-surface .glossary-table { background: transparent; }
.typelay-surface .glossary-table row { min-height: 38px; padding: 0; border-radius: 0; box-shadow: inset 0 1px rgba(20,20,19,.085); }
.typelay-surface .glossary-table row:hover { background: rgba(20,20,19,.045); }
.typelay-surface .glossary-table row:selected { background: alpha(@accent,.14); color: inherit; }
.typelay-surface .glossary-cell { padding: 0 14px; min-height: 38px; }
.typelay-surface .glossary-term { font-family: 'Source Serif 4','Noto Serif SC',serif; font-size: 14px; color: #141413; }
.typelay-surface .glossary-scope { font-size: 12px; color: #74726c; }
.typelay-surface .glossary-editor entry { min-height: 24px; margin: 6px 0; padding: 0 10px; font-size: 13px; }
.typelay-surface .glossary-editor combobox button { min-height: 24px; padding: 0 9px 0 10px; margin: 7px 0; border: 0; font-size: 12.5px; font-weight: 500; }
.typelay-surface .glossary-editor combobox arrow { -gtk-icon-source: url("__GLOSSARY_SCOPE_ARROWS__"); min-width: 10px; min-height: 12px; margin-left: 9px; -gtk-icon-shadow: none; }
.typelay-surface .glossary-listbar { padding: 4px; box-shadow: inset 0 1px rgba(20,20,19,.085); }
.typelay-surface button.glossary-action { min-width: 28px; min-height: 24px; padding: 0; border: 0; border-radius: 6px; background: transparent; box-shadow: none; color: #74726c; }
.typelay-surface button.glossary-action:hover { background: rgba(20,20,19,.08); color: #141413; }
.typelay-surface button.glossary-action:disabled { opacity: .35; }
.typelay-surface .glossary-empty { padding: 36px 20px; color: #74726c; }
.typelay-surface.settings-dark .glossary-search { background: #2f2e2b; }
.typelay-surface.settings-dark .glossary-term { color: #f4f3ee; }
.typelay-surface.settings-dark .glossary-heading, .typelay-surface.settings-dark .glossary-scope, .typelay-surface.settings-dark button.glossary-action, .typelay-surface.settings-dark .glossary-empty { color: #b5b3ab; }
.typelay-surface.settings-dark .glossary-table row, .typelay-surface.settings-dark .glossary-listbar { box-shadow: inset 0 1px rgba(255,255,255,.08); }
.typelay-surface.settings-dark .glossary-table row:hover, .typelay-surface.settings-dark button.glossary-action:hover { background: rgba(255,255,255,.05); }
.typelay-surface .tile { border: 2px solid transparent; background: transparent; box-shadow: none; padding: 4px; }
.typelay-surface .tile:checked { border-color: @accent; }
.typelay-surface .tile-art { border-radius: 9px; min-width: 68px; min-height: 44px; border: 1px solid rgba(20,20,19,.16); }
.typelay-surface .tile-system { background: linear-gradient(to right, #f1e3d3 50%, #2a221d 50%); }
.typelay-surface .tile-light { background: linear-gradient(135deg, #f1e3d3, #dfe5d6); }
.typelay-surface .tile-dark { background: linear-gradient(135deg, #3b2a22, #1e2620); }
.typelay-surface .font-art { font-size: 20px; background: #faf9f5; }
.typelay-surface .serif, .typelay-surface .translation-preview { font-family: 'Source Serif 4', 'Noto Serif SC', serif; }
.typelay-surface .swatch { border: 2px solid transparent; border-radius: 50%; min-width: 18px; min-height: 18px; padding: 0; margin: 3px; }
.typelay-surface .swatch:checked { border-color: #141413; box-shadow: 0 0 0 2px #fff; }
.typelay-surface .keycap { background: rgba(20,20,19,.08); border: 0; border-bottom: 1px solid rgba(20,20,19,.16); min-width: 110px; font-size: 12px; }
.typelay-surface .keycap.recording { background: rgba(217,119,87,.14); color: @accent_ink; border: 1px solid @accent; }
.typelay-surface .preview-canvas { padding: 24px; background: linear-gradient(135deg,#e9dfcf,#dfe5d6); border-radius: 12px; min-height: 140px; }
.typelay-surface .preview-overlay { background: rgba(251,250,247,.94); border: 1px solid rgba(20,20,19,.1); border-radius: 18px; padding: 13px 14px 9px; box-shadow: 0 8px 24px rgba(20,20,19,.1); }
.typelay-surface .translation-preview { font-size: 15px; }
.typelay-surface .preview-source { background: #fff; padding: 10px 16px; border-radius: 21px; font-family: 'Noto Serif SC',serif; }
.typelay-surface .settings-footer { padding: 8px 28px 12px; }
.typelay-surface .toast { background: #262624; color: #f4f3ee; border-radius: 18px; padding: 8px 14px; }
.typelay-surface .toast button { background: transparent; color: #ec9a7c; border: 0; box-shadow: none; }
.typelay-surface .status-dot { color: #3a8e62; font-size: 8px; min-width: 7px; }
.typelay-surface .status-dot.paused { color: #74726c; }
.typelay-surface .beta-badge { background: rgba(217,119,87,.14); color: #ad4f2d; border-radius: 9px; font-size: 10.5px; font-weight: 600; padding: 1px 7px; }
.typelay-surface.settings-dark .beta-badge { color: #ec9a7c; }
.typelay-surface .typelay-app-icon { border-radius: 11px; box-shadow: 0 2px 6px rgba(150,60,30,.3); }
.typelay-surface .app-mark { background: linear-gradient(160deg,#e48a69,#c4573a); color: #fff; border-radius: 11px; font-size: 25px; min-width: 44px; min-height: 44px; }
.typelay-surface .app-mark.large { min-width: 76px; min-height: 76px; border-radius: 18px; font-size: 38px; }
.typelay-surface list { background: transparent; }
.typelay-surface list row { padding: 0; }
.typelay-surface textview, .typelay-surface textview text { background: #fff; color: #141413; }
.typelay-surface separator { background: rgba(20,20,19,.085); min-height: 1px; }
.typelay-surface .quick-card { background: rgba(20,20,19,.045); border: 1px solid rgba(20,20,19,.085); border-radius: 12px; padding: 10px; }
.typelay-surface .quick-headline { font-size: 14px; font-weight: 600; }
.typelay-surface .quick-status { color: #5c5b56; font-size: 12px; padding: 2px 6px; }
.typelay-surface .model-mark { background: linear-gradient(160deg,#4fae77,#2a7a4b); color: white; padding: 10px; border-radius: 11px; min-width: 24px; min-height: 24px; }
.typelay-surface .connection-card { background: #fff; border: 1px solid rgba(20,20,19,.085); border-radius: 12px; padding: 12px 14px; }
.typelay-surface .model-details { padding: 10px 0; }
.typelay-surface .model-package { border: 1px solid rgba(20,20,19,.085); border-radius: 12px; padding: 12px 14px; background: #fff; }
.typelay-surface.settings-dark .connection-card, .typelay-surface.settings-dark .model-package { background: #2c2b28; border-color: rgba(255,255,255,.10); }
.typelay-surface .welcome-title { font-family: 'Source Serif 4','Noto Serif SC',serif; font-size: 30px; font-weight: 500; }
.typelay-surface.settings-dark { background: #262624; color: #f4f3ee; }
.typelay-surface.settings-dark .settings-sidebar { background: #2c2b28; border-color: rgba(255,255,255,.08); }
.typelay-surface.settings-dark .settings-navigation row { color: #f4f3ee; }
.typelay-surface.settings-dark .settings-navigation row:selected { background: rgba(255,255,255,.09); color: #f4f3ee; }
.typelay-surface.settings-dark .settings-group, .typelay-surface.settings-dark .settings-notice { background: #2f2e2b; border-color: rgba(255,255,255,.08); }
.typelay-surface.settings-dark .settings-group { box-shadow: inset 0 0 0 1px rgba(255,255,255,.08); }
.typelay-surface.settings-dark .settings-row + .settings-row { box-shadow: inset 0 1px rgba(255,255,255,.08); }
.typelay-surface.settings-dark .dim-label, .typelay-surface.settings-dark .settings-section-title, .typelay-surface.settings-dark .quick-status { color: #b5b3ab; }
.typelay-surface.settings-dark button, .typelay-surface.settings-dark entry { background: #3a3936; color: #f4f3ee; border-color: rgba(255,255,255,.15); }
.typelay-surface.settings-dark button:hover { background: #44433f; }
.typelay-surface.settings-dark .flat, .typelay-surface.settings-dark .tile { background: transparent; border-color: transparent; }
.typelay-surface.settings-dark .tile:checked { border-color: @accent; }
.typelay-surface.settings-dark .segments, .typelay-surface.settings-dark .keycap { background: rgba(255,255,255,.09); }
.typelay-surface.settings-dark .segments button { background: transparent; }
.typelay-surface.settings-dark .segments button:checked { background: #3a3936; }
.typelay-surface.settings-dark .font-art { background: #1f1e1c; }
.typelay-surface.settings-dark .preview-canvas { background: linear-gradient(135deg,#3b2a22,#1e2620); }
.typelay-surface.settings-dark .preview-overlay { background: #2c2b28; border-color: rgba(255,255,255,.1); }
.typelay-surface.settings-dark .preview-source, .typelay-surface.settings-dark textview, .typelay-surface.settings-dark textview text { background: #2f2e2b; color: #f4f3ee; }
.typelay-surface.settings-dark .settings-warning { color: #e5ad55; }
.typelay-surface.settings-dark .quick-card { background: rgba(255,255,255,.05); border-color: rgba(255,255,255,.08); }
"""


def style(widget, *classes):
    for name in classes:
        widget.get_style_context().add_class(name)
    return widget


def label(text="", *, muted=False, wrap=True):
    result = Gtk.Label(label=text, xalign=0)
    if wrap:
        result.set_line_wrap(True)
        result.set_line_wrap_mode(Pango.WrapMode.WORD_CHAR)
        result.set_max_width_chars(42)
    if muted:
        style(result, "dim-label")
    return result


def box(spacing=8):
    return Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=spacing)


def group():
    return style(box(0), "settings-group")


def row(title, control=None, detail=None, *, hero=False):
    result = style(Gtk.Box(spacing=12), "settings-row")
    if hero:
        style(result, "hero-row")
    captions = box(3)
    heading = label(title)
    if hero:
        style(heading, "headline")
    captions.pack_start(heading, False, False, 0)
    if detail is not None:
        captions.pack_start(
            detail if isinstance(detail, Gtk.Widget) else label(detail, muted=True), False, False, 0
        )
    heading.set_max_width_chars(28)
    captions.set_hexpand(True)
    captions.set_valign(Gtk.Align.CENTER)
    result.pack_start(captions, True, True, 0)
    if control is not None:
        control.set_valign(Gtk.Align.CENTER)
        result.pack_end(control, False, False, 0)
    return result, heading


def app_icon(size=44):
    image = Gtk.Image.new_from_file(str(Path(__file__).parent / "icons/typelay-active.svg"))
    image.set_pixel_size(size)
    image.set_size_request(size, size)
    # SVG's intrinsic dimensions are intentionally constrained via the pixbuf.
    from gi.repository import GdkPixbuf

    pixbuf = GdkPixbuf.Pixbuf.new_from_file_at_scale(
        str(Path(__file__).parent / "icons/typelay-active.svg"), size, size, True
    )
    image.set_from_pixbuf(pixbuf)
    style(image, "typelay-app-icon")
    image.set_valign(Gtk.Align.CENTER)
    return image


def design_icon(name, size=14, *, symbolic=False):
    from gi.repository import GdkPixbuf

    suffix = "-symbolic" if symbolic else ""
    pixbuf = GdkPixbuf.Pixbuf.new_from_file_at_scale(
        str(Path(__file__).parent / f"icons/settings-{name}{suffix}.svg"), size, size, True
    )
    image = Gtk.Image.new_from_pixbuf(pixbuf)
    if symbolic:
        # GTK recolors fills, not the original prototype's SVG strokes. Keep
        # their exact alpha mask and tint this small native image from its own
        # CSS foreground; no Python cairo bridge or global theme changes.
        source = pixbuf.get_pixels()
        width, height = pixbuf.get_width(), pixbuf.get_height()
        channels, stride = pixbuf.get_n_channels(), pixbuf.get_rowstride()
        previous = None

        def recolor(widget, *_args):
            nonlocal previous
            color = widget.get_style_context().get_color(widget.get_state_flags())
            rgba = tuple(
                round(component * 255)
                for component in (color.red, color.green, color.blue, color.alpha)
            )
            if rgba == previous:
                return
            previous = rgba
            pixels = bytearray(width * height * 4)
            for y in range(height):
                for x in range(width):
                    alpha = source[y * stride + x * channels + 3] if channels == 4 else 255
                    offset = (y * width + x) * 4
                    pixels[offset : offset + 4] = bytes((*rgba[:3], round(alpha * rgba[3] / 255)))
            widget.set_from_pixbuf(
                GdkPixbuf.Pixbuf.new_from_bytes(
                    GLib.Bytes.new(bytes(pixels)),
                    GdkPixbuf.Colorspace.RGB,
                    True,
                    8,
                    width,
                    height,
                    width * 4,
                )
            )

        image.connect("style-updated", recolor)
        image.connect("state-flags-changed", recolor)
        recolor(image)
    image.set_valign(Gtk.Align.CENTER)
    return image


_registered_fonts = None
_theme_scopes = count(1)


def register_fonts():
    """Register the verified package's declared faces in this process only.

    The build verifies the authoritative lock. Runtime consumes its packaged
    manifest and does not search/install fonts or contact a font service.
    """
    global _registered_fonts
    if _registered_fonts is not None:
        return _registered_fonts
    _registered_fonts = ()
    source = Path(__file__).resolve()
    explicit = os.environ.get("TYPELAY_UI_FONT_ROOT")
    roots = (
        (Path(explicit),)
        if explicit
        else (source.parent.parent / "fonts", source.parents[3] / "artifacts/ui-fonts")
    )
    font_root = next((root for root in roots if (root / "manifest.json").is_file()), None)
    try:
        if font_root is None:
            raise ValueError("bundled font manifest unavailable")
        records = json.loads((font_root / "manifest.json").read_text())["files"]
        fonts = []
        for record in records:
            relative = Path(record["path"])
            if relative.suffix not in (".ttf", ".otf"):
                continue
            font = font_root / relative
            if (
                relative.is_absolute()
                or ".." in relative.parts
                or font.is_symlink()
                or not font.is_file()
                or not font.resolve().is_relative_to(font_root.resolve())
            ):
                raise ValueError("invalid bundled font path")
            fonts.append(font.resolve())
        if not fonts:
            raise ValueError("bundled font faces unavailable")
        fontconfig = ctypes.CDLL("libfontconfig.so.1")
        fontconfig.FcConfigGetCurrent.restype = ctypes.c_void_p
        fontconfig.FcConfigAppFontAddFile.argtypes = (ctypes.c_void_p, ctypes.c_char_p)
        fontconfig.FcConfigAppFontAddFile.restype = ctypes.c_int
        config = fontconfig.FcConfigGetCurrent()
        for font in fonts:
            if not fontconfig.FcConfigAppFontAddFile(config, str(font).encode()):
                raise ValueError("bundled font could not be registered")
        PangoCairo.FontMap.get_default().changed()
        _registered_fonts = tuple(fonts)
    except (OSError, ValueError, KeyError, TypeError) as error:
        # Keep a damaged development/package surface usable, but do not report
        # the platform fallback as successful bundled-font registration.
        print("TypeLay bundled UI fonts unavailable: " + str(error), file=sys.stderr)
    return _registered_fonts


def font_families(preferences, role="display"):
    """Select region-specific faces without enabling new translation languages."""
    choice = preferences.get("font", "claude")
    if choice == "system":
        settings = Gtk.Settings.get_default()
        description = settings.get_property("gtk-font-name") if settings else None
        family = (
            Pango.FontDescription.from_string(description).get_family() if description else None
        )
        return (family or "sans-serif",)
    if role == "ui":
        return ("Inter", "Noto Sans SC", "sans-serif")
    language = preferences.get("target" if role == "content" else "uiLanguage", "zh-CN")
    region = {"ja": "JP", "ko": "KR"}.get(str(language).split("-")[0].lower(), "SC")
    serif = choice == "claude"
    families = [
        "Source Serif 4" if serif else "Inter",
        f"Noto {'Serif' if serif else 'Sans'} {region}",
    ]
    if region != "SC":
        families.append("Noto Serif SC" if serif else "Noto Sans SC")
    return (*families, "serif" if serif else "sans-serif")


def css_font_family(preferences, role="display"):
    return ", ".join(
        family
        if family in ("serif", "sans-serif")
        else "'" + family.replace("\\", "\\\\").replace("'", "\\'") + "'"
        for family in font_families(preferences, role)
    )


def apply_theme(window, preferences, provider=None):
    register_fonts()
    context = window.get_style_context()
    if not hasattr(window, "typelay_theme_scope"):
        window.typelay_theme_scope = "typelay-window-" + str(next(_theme_scopes))
    scope = window.typelay_theme_scope
    style(window, "typelay-surface", scope)
    theme = preferences.get("theme", "system")
    found, color = context.lookup_color("theme_bg_color")
    dark = (
        theme == "dark"
        or theme == "system"
        and found
        and (color.red * 0.2126 + color.green * 0.7152 + color.blue * 0.0722) < 0.5
    )
    (context.add_class if dark else context.remove_class)("settings-dark")
    for key, name in (
        ("reduceMotion", "reduce-motion"),
        ("reduceTransparency", "reduce-transparency"),
    ):
        (context.add_class if preferences.get(key) else context.remove_class)(name)
    accent, ink, dark_ink = ACCENTS.get(preferences.get("accent"), ACCENTS["clay"])
    if provider is None:
        provider = Gtk.CssProvider()
        screen = window.get_screen()
        Gtk.StyleContext.add_provider_for_screen(
            screen, provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
        )
        window.connect(
            "destroy", lambda *_: Gtk.StyleContext.remove_provider_for_screen(screen, provider)
        )
    ui_font = css_font_family(preferences, "ui")
    display_font = css_font_family(preferences)
    content_font = css_font_family(preferences, "content")
    content_size = max(13, min(19, preferences.get("textSize", 15)))
    endonym_css = "".join(
        f"\n.typelay-surface .language-endonym-{language} {{ font-family: "
        + css_font_family(
            {
                **preferences,
                "font": "system" if preferences.get("font") == "system" else "sans",
                "target": language,
            },
            "content",
        )
        + "; }"
        for language in ("ja", "ko")
    )
    # GTK screen providers otherwise outlive transient windows and override
    # unrelated surfaces at the same priority. Scope selectors and colors to
    # this exact owner, then remove its provider when that owner is destroyed.
    rules = (
        CSS.replace(
            "__GLOSSARY_SCOPE_ARROWS__",
            (Path(__file__).parent / "icons/settings-scope-arrows.svg").resolve().as_uri(),
        )
        + endonym_css
        + f"\n.typelay-surface {{ font-family: {ui_font}; }}"
        + f"\n.typelay-surface .glossary-term {{ font-family: {content_font}; }}"
        + f"\n.typelay-surface .settings-page-title, .typelay-surface .welcome-title, .typelay-surface .language-name {{ font-family: {display_font}; }}"
        + f"\n.typelay-surface .translation-preview, .typelay-surface .translation-content, .typelay-surface .translation-content text {{ font-family: {content_font}; font-size: {content_size}px; font-weight: 400; }}"
    ).replace(".typelay-surface", ".typelay-surface." + scope)
    rules = rules.replace("@accent_ink", "@" + scope + "-ink").replace(
        "@accent", "@" + scope + "-accent"
    )
    payload = (
        f"@define-color {scope}-accent {accent}; @define-color {scope}-ink {dark_ink if dark else ink};\n"
        + rules
    ).encode()
    # An unrelated settings broadcast must not invalidate every GTK surface
    # by reloading an identical screen provider while a popover owns focus.
    if getattr(provider, "typelay_payload", None) != payload:
        provider.load_from_data(payload)
        provider.typelay_payload = payload
    return provider


class Segments(Gtk.Box):
    def __init__(self, choices, changed):
        super().__init__(spacing=0)
        style(self, "segments")
        self.buttons = {}
        self.updating = False
        group_button = None
        for value, title in choices:
            button = Gtk.RadioButton.new_from_widget(group_button)
            group_button = button
            button.set_mode(False)
            button.add(Gtk.Label(label=title))
            button.connect(
                "toggled",
                lambda item, key=value: changed(key)
                if item.get_active() and not self.updating
                else None,
            )
            self.buttons[value] = button
            self.pack_start(button, True, True, 0)

    def set_active_id(self, value):
        self.updating = True
        try:
            if value in self.buttons:
                self.buttons[value].set_active(True)
        finally:
            self.updating = False

    def get_active_id(self):
        return next((key for key, button in self.buttons.items() if button.get_active()), None)


class ShortcutRecorder(Gtk.Button):
    """A focused key recorder, never a global hook or a source-field key sink."""

    def __init__(self, changed, *, session=False):
        super().__init__()
        self.value = ""
        self.recording = False
        self.changed = changed
        self.session = session
        style(self, "keycap")
        self.connect("clicked", self.start)
        # GTK buttons expose no separate label child through ATK once named.
        # Keep the current chord/recording prompt available to screen readers.
        self.connect(
            "notify::label",
            lambda *_: self.get_accessible().set_description(self.get_label() or ""),
        )
        self.connect("key-press-event", self.key)
        self.connect("focus-out-event", lambda *_: self.cancel())
        self.set_text("")

    def get_text(self):
        return self.value

    def set_text(self, value):
        self.value = value or ""
        if not self.recording:
            self.set_label(self.value or "未设置")

    def start(self, *_):
        self.recording = True
        self.set_label("按下快捷键…")
        style(self, "recording")
        self.grab_focus()

    def cancel(self):
        self.recording = False
        self.get_style_context().remove_class("recording")
        self.set_text(self.value)
        return False

    def key(self, _widget, event):
        if not self.recording:
            return False
        name = Gdk.keyval_name(event.keyval)
        if name in (
            "Shift_L",
            "Shift_R",
            "Control_L",
            "Control_R",
            "Alt_L",
            "Alt_R",
            "Super_L",
            "Super_R",
            "Meta_L",
            "Meta_R",
        ):
            return True
        if name == "Escape":
            self.cancel()
            return True
        if name in ("BackSpace", "Delete") and not self.session:
            value = ""
        else:
            state = event.state
            pieces = [
                title
                for mask, title in (
                    (Gdk.ModifierType.CONTROL_MASK, "Ctrl"),
                    (Gdk.ModifierType.MOD1_MASK, "Alt"),
                    (Gdk.ModifierType.SHIFT_MASK, "Shift"),
                    (Gdk.ModifierType.SUPER_MASK, "Super"),
                )
                if state & mask
            ]
            key = {"ISO_Left_Tab": "Tab", "space": "Space", "Return": "Enter"}.get(name, name)
            if key and len(key) == 1:
                key = key.upper()
            value = "+".join([*pieces, key]) if key else ""
        self.cancel()
        self.set_text(value)
        self.changed(value)
        return True


class Toast(Gtk.Revealer):
    def __init__(self):
        super().__init__()
        self.set_transition_type(Gtk.RevealerTransitionType.SLIDE_UP)
        self.set_halign(Gtk.Align.CENTER)
        self.set_valign(Gtk.Align.END)
        self.set_margin_bottom(18)
        self.timer = None
        self.action = None
        self.identity = None
        self.translate = lambda value: value
        self.content = style(Gtk.Box(spacing=10), "toast")
        self.text = label()
        self.undo = Gtk.Button(label="撤销")
        self.content.pack_start(self.text, True, True, 0)
        self.content.pack_end(self.undo, False, False, 0)
        self.add(self.content)

    def show_message(self, text, undo=None):
        self.expire()
        identity = object()
        self.identity = identity
        self.text.set_text(text)
        self.action = undo
        self.content.remove(self.undo)
        self.undo = Gtk.Button(label=self.translate("撤销"))
        self.undo.connect("clicked", lambda *_: self.perform(identity))
        self.content.pack_end(self.undo, False, False, 0)
        self.undo.set_visible(undo is not None)
        self.set_reveal_child(True)
        self.timer = GLib.timeout_add(6000, lambda: self.expire(identity, from_timer=True))

    def expire(self, identity=None, *, from_timer=False):
        if identity is not None and identity is not self.identity:
            return False
        if self.timer is not None and not from_timer:
            GLib.source_remove(self.timer)
        self.timer = None
        self.identity = None
        self.action = None
        self.set_reveal_child(False)
        return False

    def perform(self, identity):
        if identity is not self.identity:
            return
        action = self.action
        self.expire(identity)
        if action:
            action()
