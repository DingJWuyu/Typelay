"""Ordinary GTK practice fields, served by the existing production input pipeline.

Launch through ``typelay launch`` inside an explicitly enabled cooperative Fcitx
session. This window has no translator, native protocol client, IME authority or
Tab handler. The loaded production GTK module owns all capture and acceptance.
"""

import os
import sys
import threading
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
os.environ["GDK_BACKEND"] = "wayland"

import gi  # noqa: E402
from main import control  # noqa: E402
from paths import Paths  # noqa: E402

gi.require_version("Gtk", "3.0")
from gi.repository import GLib, Gtk  # noqa: E402


class Practice:
    def __init__(self, paths):
        self.paths = paths
        self.closed = False
        self.pending_outcome = None
        self.pending_start = None
        self.pending_settings = False
        self.presentation = None
        self.confirmation = None
        self.completion_failed = False
        self.request_running = False
        self.request_thread = None
        self.window = Gtk.Window(title="TypeLay 实操练习")
        self.window.set_default_size(680, 500)
        self.window.set_border_width(24)
        self.window.connect("delete-event", self.close)
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=16)
        self.window.add(outer)
        scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        outer.pack_start(scroll, True, True, 0)
        content = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14)
        content.set_margin_end(10)
        scroll.add(content)

        def note(text, container=content):
            label = Gtk.Label(label=text, xalign=0)
            label.set_line_wrap(True)
            label.set_max_width_chars(70)
            container.pack_start(label, False, False, 0)
            return label

        self.heading = note("")
        self.heading.get_style_context().add_class("title")
        self.summary = note("")
        self.current_state = note("")
        self.preview_step = note("")
        self.replace_step = note("")
        self.input_label = note("")
        self.source = Gtk.Entry()
        self.source.set_max_length(2048)
        self.source.get_accessible().set_name("练习输入框")
        content.pack_start(self.source, False, False, 0)
        self.start = Gtk.Button(label="…")
        self.start.set_sensitive(False)
        self.start.connect("clicked", self.start_clicked)
        content.pack_start(self.start, False, False, 0)
        self.status = note("等待连接当前 Wayland 会话的 TypeLay；请聚焦上面的练习框。")
        self.capability_note = note("")
        settings = Gtk.Button(label="打开 TypeLay 设置")
        settings.connect("clicked", lambda *_: self.request_settings())
        content.pack_start(settings, False, False, 0)
        technical = Gtk.Expander(label="连接与输入法说明")
        technical.set_expanded(False)
        details = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14)
        details.set_margin_top(8)
        technical.add(details)
        content.pack_start(technical, False, False, 0)
        note(
            "实际 Fcitx 合作组件必须已由你明确启用。普通未合作的 GTK 环境尚无安全读取/替换"
            "能力，只有安装模型还不够。本窗口不会启动、停止或切换输入法，也不会修改个人输入法配置。",
            details,
        )
        note(
            "若未出现胶囊，请在设置的“应用支持”中检查桌面组件和练习连接；"
            "高级手动启动也可按发行包说明通过合作包的 launch 入口打开本练习。"
            "组合输入尚未完成时，Tab 应交给输入法；未知状态不会被当成可接受。"
            "完成这里的练习仍不代表其他应用或物理输入法已经通过验收。",
            details,
        )
        automatic = Gtk.Expander(label="已有自动模式与 Tab 练习")
        instructions = Gtk.Label(
            label="如需练习已有自动流程，请自行在设置中选择自动模式、语言方向，并明确开启原生输入框替换。\n"
            "1. 在上方输入，等待正式客户端的真实译文；按 Tab 只替换练习框。\n"
            "2. 再输入一句，点击浮层译文并修改；第一次 Tab 只返回原框，第二次 Tab 接受修改。",
            xalign=0,
        )
        instructions.set_line_wrap(True)
        instructions.set_max_width_chars(70)
        automatic.add(instructions)
        content.pack_start(automatic, False, False, 0)
        actions = Gtk.Box(spacing=8)
        outer.pack_end(actions, False, False, 0)
        reset = Gtk.Button.new_with_mnemonic("重新练习(_R)")
        reset.connect("clicked", self.reset)
        actions.pack_start(reset, False, False, 0)
        self.complete = Gtk.Button.new_with_mnemonic("我已完成练习(_D)")
        self.complete.connect("clicked", lambda *_: self.finish("completed"))
        actions.pack_start(self.complete, False, False, 0)
        self.skip = Gtk.Button.new_with_mnemonic("跳过教程(_S)")
        self.skip.connect("clicked", lambda *_: self.finish("skipped"))
        actions.pack_start(self.skip, False, False, 0)
        self.close_button = Gtk.Button(label="关闭")
        self.close_button.connect("clicked", self.close)
        actions.pack_end(self.close_button, False, False, 0)
        self.timer = GLib.timeout_add(1000, self.poll)

    def start_clicked(self, *_):
        if self.closed or self.presentation is None or self.pending_start is not None:
            return
        if self.confirmation is not None:
            self.confirmation.present()
            return
        view = self.presentation
        if not view["confirmationText"]:
            self.begin(False)
            return
        dialog = Gtk.MessageDialog(
            transient_for=self.window,
            modal=True,
            message_type=Gtk.MessageType.QUESTION,
            text=view["title"],
        )
        dialog.format_secondary_text(view["confirmationText"])
        dialog.add_buttons(
            view["cancelLabel"], Gtk.ResponseType.CANCEL, view["startLabel"], Gtk.ResponseType.OK
        )
        dialog.set_default_response(Gtk.ResponseType.CANCEL)
        self.confirmation = dialog

        def respond(widget, response):
            self.confirmation = None
            widget.destroy()
            if response == Gtk.ResponseType.OK and not self.closed:
                self.begin(True)

        dialog.connect("response", respond)
        dialog.show_all()

    def begin(self, confirmed_switch):
        self.pending_start = {"confirmedSwitch": confirmed_switch}
        self.start.set_sensitive(False)
        self.poll()

    def request_settings(self):
        self.pending_settings = True
        self.poll()

    def reset(self, *_):
        # This is an explicit local-field reset, not a production writeback.
        self.completion_failed = False
        self.source.set_text("")
        self.source.grab_focus()

    def finish(self, outcome):
        if self.closed or self.pending_outcome is not None:
            return
        self.pending_outcome = outcome
        self.completion_failed = False
        self.complete.set_sensitive(False)
        self.skip.set_sensitive(False)
        self.status.set_text("正在保存你明确选择的完成状态…")
        self.poll()

    def poll(self):
        if self.closed:
            return False
        if self.request_running:
            return True
        if self.pending_outcome is not None:
            self.request("finishOnboarding", {"outcome": self.pending_outcome})
        elif self.pending_start is not None:
            self.request("startOnDemandPractice", self.pending_start)
        elif self.pending_settings:
            self.pending_settings = False
            self.request("settings", None)
        elif self.window.is_active() and not self.completion_failed:
            self.request("practiceStatus", None)
        return True

    def request(self, operation, data):
        self.request_running = True

        def request():
            try:
                result = control(self.paths, operation, data)
            except (OSError, EOFError, ValueError):
                result = None
            GLib.idle_add(self.respond, operation, result)

        # Socket waits never block the widget/IME thread. Only one bounded
        # control request can be in flight; it never includes practice text.
        self.request_thread = threading.Thread(target=request, name="typelay-practice-status")
        self.request_thread.start()

    def respond(self, operation, result):
        self.request_running = False
        if self.closed:
            return False
        if operation == "startOnDemandPractice":
            self.pending_start = None
            if result is True:
                self.source.grab_focus()
            else:
                self.status.set_text(
                    "尚未开始练习。请检查当前模式、启用状态、模型和语言设置后重试。"
                )
            self.poll()
            return False
        if operation == "settings":
            if result is not True:
                self.status.set_text("暂时无法打开设置，请从 TypeLay 应用菜单重试。")
            return False
        if operation == "finishOnboarding":
            if result is True:
                self.close()
                return False
            self.pending_outcome = None
            self.completion_failed = True
            self.complete.set_sensitive(True)
            self.skip.set_sensitive(True)
            self.status.set_text(
                "完成状态未保存。请确认 TypeLay 仍在运行后重试；关闭窗口不会标记完成。"
            )
        elif (
            self.pending_outcome is not None
            or self.pending_start is not None
            or self.pending_settings
        ):
            self.poll()
        elif not isinstance(result, dict):
            self.start.set_sensitive(False)
            self.status.set_text("未连接当前会话的 TypeLay。请先从应用菜单启动客户端。")
        else:
            view = result.get("presentation")
            if not isinstance(view, dict):
                self.start.set_sensitive(False)
                self.status.set_text("无法读取练习说明，请检查 TypeLay 客户端版本后重新打开。")
                return False
            self.presentation = view
            for label, key in (
                (self.heading, "title"),
                (self.summary, "summary"),
                (self.current_state, "currentStateNotice"),
                (self.preview_step, "previewStep"),
                (self.replace_step, "replaceStep"),
                (self.input_label, "inputLabel"),
                (self.capability_note, "capabilityNote"),
            ):
                label.set_text(view[key])
            # A settings refresh must never replace the user's text or selection.
            self.source.set_placeholder_text(view["sampleText"])
            self.source.get_accessible().set_name(view["inputLabel"])
            self.start.set_label(view["startLabel"])
            self.close_button.set_label(view["closeLabel"])
            self.start.set_sensitive(
                result.get("modelReady", False)
                and result.get("practiceEnabled", False)
                and not view["languageNeedsChange"]
            )
            if not result.get("modelReady"):
                self.status.set_text("本地模型尚未就绪。请在 TypeLay 设置中完成组件安装。")
                return False
            if view["currentStateNotice"]:
                self.status.set_text(view["currentStateNotice"])
                return False
            if not self.source.has_focus() or not self.window.is_active():
                return False
            diagnostic = result.get("input") or {}
            if not result.get("sourceSupported"):
                self.status.set_text(
                    "本练习尚未连接原生输入支持。请使用合作包的 launch 入口，并确认处于同一 niri 会话。"
                )
            elif diagnostic.get("captureStatus") in ("native-read-declined", "no-native-field"):
                self.status.set_text(
                    "当前尚未取得读取/替换能力。请确认合作组件已启用；输入法候选未结束时先完成或取消。"
                )
            else:
                self.status.set_text(
                    "本地模型与原生输入连接已就绪。请按上方快捷键练习；能否预览或替换，以真实操作反馈为准。"
                )
        return False

    def close(self, *_):
        if not self.closed:
            self.closed = True
            if self.confirmation is not None:
                self.confirmation.destroy()
                self.confirmation = None
            if self.timer:
                GLib.source_remove(self.timer)
                self.timer = 0
            self.window.destroy()
            Gtk.main_quit()
        return True

    def run(self):
        self.window.show_all()
        self.source.grab_focus()
        GLib.idle_add(lambda: (self.poll(), False)[1])
        try:
            Gtk.main()
        finally:
            self.closed = True
            if self.request_thread:
                self.request_thread.join(timeout=2)


def main():
    if len(sys.argv) != 1:
        raise ValueError("practice-arguments")
    if not os.environ.get("WAYLAND_DISPLAY") or not os.environ.get("NIRI_SOCKET"):
        raise RuntimeError("practice-wayland-session-required")
    initialized, _ = Gtk.init_check([])
    if not initialized:
        raise RuntimeError("practice-display-unavailable")
    Practice(Paths()).run()
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError):
        print(
            "TypeLay: 无法打开实操练习，请在当前 niri Wayland 会话通过 TypeLay 入口启动。",
            file=sys.stderr,
        )
        raise SystemExit(1) from None
