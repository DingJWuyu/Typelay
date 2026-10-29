"""Native GTK presentation primitives for the TypeLay design system.

These controls own no settings or source-field authority. Their callbacks carry
user intent; committed values are always rendered by the settings owner.
"""

import ctypes
from pathlib import Path

import gi

gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, GLib, Gtk, Pango  # noqa: E402

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
.typelay-surface { background: #f7f6f2; color: #141413; font-family: 'Inter', 'Noto Sans CJK SC', sans-serif; font-size: 13px; }
.typelay-surface label { color: inherit; }
.typelay-surface .settings-sidebar { background: #eeece5; border: 1px solid rgba(20,20,19,.085); border-radius: 12px; padding: 14px 10px 10px; margin: 8px; }
.typelay-surface .settings-brand { font-size: 17px; font-weight: 600; }
.typelay-surface .settings-navigation, .typelay-surface .settings-navigation list { background: transparent; border: 0; }
.typelay-surface .settings-navigation row { border: 0; border-radius: 8px; padding: 0 8px; min-height: 32px; margin: 1px 0; }
.typelay-surface .settings-navigation row:selected { background: rgba(20,20,19,.08); color: #141413; font-weight: 600; }
.typelay-surface .settings-navigation row:hover { background: rgba(20,20,19,.045); }
.typelay-surface .nav-icon { color: white; border-radius: 6px; padding: 4px; min-width: 14px; min-height: 14px; }
.typelay-surface .settings-page, .typelay-surface .settings-page viewport { background: transparent; }
.typelay-surface .settings-page-title { font-family: 'Source Serif 4', 'Noto Serif CJK SC', serif; font-size: 22px; font-weight: 500; margin-bottom: 8px; }
.typelay-surface .settings-section-title { font-size: 12px; font-weight: 600; color: #5c5b56; margin: 8px 4px 0; }
.typelay-surface .settings-group { background: #ffffff; border: 1px solid rgba(20,20,19,.085); border-radius: 12px; box-shadow: 0 1px 2px rgba(20,20,19,.04); }
.typelay-surface .settings-row { padding: 8px 14px; min-height: 28px; }
.typelay-surface .settings-row + .settings-row { border-top: 1px solid rgba(20,20,19,.06); }
.typelay-surface .hero-row { min-height: 56px; }
.typelay-surface .headline { font-size: 15px; font-weight: 600; }
.typelay-surface .dim-label { color: #74726c; opacity: 1; font-size: 12px; }
.typelay-surface .settings-warning { color: #a5670e; font-size: 12px; }
.typelay-surface .settings-notice { padding: 12px 14px; background: #fff; border-radius: 12px; border: 1px solid rgba(20,20,19,.085); }
.typelay-surface button { background-image: none; background-color: #ffffff; color: #141413; border: 1px solid rgba(20,20,19,.16); border-radius: 8px; padding: 4px 10px; min-height: 20px; box-shadow: 0 1px 2px rgba(20,20,19,.04); }
.typelay-surface button:hover { background-color: #f2f0e9; }
.typelay-surface button:disabled { opacity: .5; }
.typelay-surface .flat { background: transparent; border-color: transparent; box-shadow: none; }
.typelay-surface .flat:hover { background: rgba(20,20,19,.08); }
.typelay-surface entry { background: #fff; color: #141413; border: 1px solid rgba(20,20,19,.16); border-radius: 8px; padding: 4px 8px; min-height: 22px; box-shadow: none; }
.typelay-surface entry:focus, .typelay-surface button:focus { border-color: @accent; outline-color: @accent; }
.typelay-surface .settings-search { background: rgba(20,20,19,.08); border: 0; min-height: 20px; }
.typelay-surface switch { min-width: 36px; min-height: 21px; border: 0; border-radius: 12px; padding: 2px; background: rgba(20,20,19,.16); box-shadow: none; }
.typelay-surface switch:checked { background: @accent; }
.typelay-surface switch slider { background: #fff; border: 0; border-radius: 50%; min-width: 17px; min-height: 17px; box-shadow: 0 1px 2px rgba(0,0,0,.16); }
.typelay-surface .segments { border-radius: 8px; padding: 3px; background: rgba(20,20,19,.08); }
.typelay-surface .segments button { background: transparent; border: 0; border-radius: 6px; box-shadow: none; padding: 4px 12px; min-height: 18px; }
.typelay-surface .segments button:checked { background: #fff; box-shadow: 0 1px 3px rgba(20,20,19,.12); }
.typelay-surface .tile { border: 2px solid transparent; background: transparent; box-shadow: none; padding: 4px; }
.typelay-surface .tile:checked { border-color: @accent; }
.typelay-surface .tile-art { border-radius: 9px; min-width: 68px; min-height: 44px; border: 1px solid rgba(20,20,19,.16); }
.typelay-surface .tile-system { background: linear-gradient(to right, #f1e3d3 50%, #2a221d 50%); }
.typelay-surface .tile-light { background: linear-gradient(135deg, #f1e3d3, #dfe5d6); }
.typelay-surface .tile-dark { background: linear-gradient(135deg, #3b2a22, #1e2620); }
.typelay-surface .font-art { font-size: 20px; background: #faf9f5; }
.typelay-surface .serif, .typelay-surface .translation-preview { font-family: 'Source Serif 4', 'Noto Serif CJK SC', serif; }
.typelay-surface .swatch { border: 2px solid transparent; border-radius: 50%; min-width: 18px; min-height: 18px; padding: 0; margin: 3px; }
.typelay-surface .swatch:checked { border-color: #141413; box-shadow: 0 0 0 2px #fff; }
.typelay-surface .keycap { background: rgba(20,20,19,.08); border: 0; border-bottom: 1px solid rgba(20,20,19,.16); min-width: 110px; font-size: 12px; }
.typelay-surface .keycap.recording { background: rgba(217,119,87,.14); color: @accent_ink; border: 1px solid @accent; }
.typelay-surface .preview-canvas { padding: 24px; background: linear-gradient(135deg,#e9dfcf,#dfe5d6); border-radius: 12px; min-height: 140px; }
.typelay-surface .preview-overlay { background: rgba(251,250,247,.94); border: 1px solid rgba(20,20,19,.1); border-radius: 18px; padding: 13px 14px 9px; box-shadow: 0 8px 24px rgba(20,20,19,.1); }
.typelay-surface .translation-preview { font-size: 15px; }
.typelay-surface .preview-source { background: #fff; padding: 10px 16px; border-radius: 21px; font-family: 'Noto Serif CJK SC',serif; }
.typelay-surface .settings-footer { padding: 8px 28px 12px; }
.typelay-surface .toast { background: #262624; color: #f4f3ee; border-radius: 18px; padding: 8px 14px; }
.typelay-surface .toast button { background: transparent; color: #ec9a7c; border: 0; box-shadow: none; }
.typelay-surface .app-mark { background: linear-gradient(160deg,#e48a69,#c4573a); color: #fff; border-radius: 11px; font-size: 25px; min-width: 44px; min-height: 44px; }
.typelay-surface .app-mark.large { min-width: 76px; min-height: 76px; border-radius: 18px; font-size: 38px; }
.typelay-surface list { background: transparent; }
.typelay-surface list row { padding: 0; }
.typelay-surface textview, .typelay-surface textview text { background: #fff; color: #141413; }
.typelay-surface separator { background: rgba(20,20,19,.085); min-height: 1px; }
.typelay-surface .quick-card { background: rgba(20,20,19,.045); border: 1px solid rgba(20,20,19,.085); border-radius: 12px; padding: 10px; }
.typelay-surface .quick-status { color: #5c5b56; font-size: 12px; padding: 2px 6px; }
.typelay-surface .model-mark { background: linear-gradient(160deg,#4fae77,#2a7a4b); color: white; padding: 10px; border-radius: 11px; min-width: 24px; min-height: 24px; }
.typelay-surface .connection-card { background: #fff; border: 1px solid rgba(20,20,19,.085); border-radius: 12px; padding: 12px 14px; }
.typelay-surface .model-details { padding: 10px 0; }
.typelay-surface .model-package { border: 1px solid rgba(20,20,19,.085); border-radius: 12px; padding: 12px 14px; background: #fff; }
.typelay-surface.settings-dark .connection-card, .typelay-surface.settings-dark .model-package { background: #2c2b28; border-color: rgba(255,255,255,.10); }
.typelay-surface .welcome-title { font-family: 'Source Serif 4','Noto Serif CJK SC',serif; font-size: 30px; font-weight: 500; }
.typelay-surface.settings-dark { background: #262624; color: #f4f3ee; }
.typelay-surface.settings-dark .settings-sidebar { background: #2c2b28; border-color: rgba(255,255,255,.08); }
.typelay-surface.settings-dark .settings-navigation row { color: #f4f3ee; }
.typelay-surface.settings-dark .settings-navigation row:selected { background: rgba(255,255,255,.09); color: #f4f3ee; }
.typelay-surface.settings-dark .settings-group, .typelay-surface.settings-dark .settings-notice { background: #2f2e2b; border-color: rgba(255,255,255,.08); }
.typelay-surface.settings-dark .settings-row + .settings-row { border-color: rgba(255,255,255,.08); }
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
    return image


_fonts_registered = False


def register_fonts():
    """Register bundled fonts in this process only; never install system fonts."""
    global _fonts_registered
    if _fonts_registered:
        return
    _fonts_registered = True
    source = Path(__file__).resolve()
    roots = (source.parent.parent / "fonts", source.parents[2] / "shared/ui/fonts")
    font_root = next((root for root in roots if root.is_dir()), None)
    if font_root is None:
        return
    try:
        fontconfig = ctypes.CDLL("libfontconfig.so.1")
        fontconfig.FcConfigGetCurrent.restype = ctypes.c_void_p
        fontconfig.FcConfigAppFontAddFile.argtypes = (ctypes.c_void_p, ctypes.c_char_p)
        fontconfig.FcConfigAppFontAddFile.restype = ctypes.c_int
        config = fontconfig.FcConfigGetCurrent()
        for font in font_root.rglob("*.ttf"):
            fontconfig.FcConfigAppFontAddFile(config, str(font).encode())
    except OSError:
        # The platform font stack remains a usable, explicit fallback.
        return


def apply_theme(window, preferences, provider=None):
    register_fonts()
    context = window.get_style_context()
    style(window, "typelay-surface")
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
        Gtk.StyleContext.add_provider_for_screen(
            window.get_screen(), provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
        )
    provider.load_from_data(
        (
            f"@define-color accent {accent}; @define-color accent_ink {dark_ink if dark else ink};\n"
            + CSS
        ).encode()
    )
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
