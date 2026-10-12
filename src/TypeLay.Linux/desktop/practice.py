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

        def note(text):
            label = Gtk.Label(label=text, xalign=0)
            label.set_line_wrap(True)
            label.set_max_width_chars(70)
            content.pack_start(label, False, False, 0)
            return label

        heading = note("在真实输入框中练习")
        heading.get_style_context().add_class("title")
        note(
            "这里是 TypeLay 自己的普通 GTK 输入框。胶囊、译文、编辑和 Tab 都由正在运行的"
            "正式客户端处理。仅使用练习文字；本窗口没有发送或提交功能。"
        )
        note(
            "开始前：在设置中选择自动模式、语言方向，并明确开启原生输入框替换。"
            "目标为英语时输入中文，目标为中文时输入英语。"
        )
        note(
            "1. 在下面正常输入，等待真实翻译胶囊展开；按 Tab 应只替换练习框内容。\n"
            "2. 再输入一句，点击实际浮层里的译文并修改；第一次 Tab 只返回练习框，"
            "第二次 Tab 才接受修改。也可以用鼠标返回练习框后按 Tab。"
        )
        self.source = Gtk.Entry()
        self.source.set_max_length(2048)
        self.source.set_placeholder_text("例如：你好，我们明天见。 / Please review this note.")
        self.source.get_accessible().set_name("练习输入框")
        content.pack_start(self.source, False, False, 0)
        self.status = note("等待连接当前 Wayland 会话的 TypeLay；请聚焦上面的练习框。")
        note(
            "实际 Fcitx 合作组件必须已由你明确启用。普通未合作的 GTK 环境尚无安全读取/替换"
            "能力，只有安装模型还不够。本窗口不会启动、停止或切换输入法，也不会修改个人配置。"
        )
        note(
            "若未出现胶囊，请按发行包说明启用合作组件，并通过该包的 launch 入口打开本练习。"
            "组合输入尚未完成时，Tab 应交给输入法；未知状态不会被当成可接受。"
            "完成这里的练习仍不代表其他应用或物理输入法已经通过验收。"
        )
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
        self.timer = GLib.timeout_add(1000, self.poll)

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
        if self.request_running or (
            self.pending_outcome is None and (not self.window.is_active() or self.completion_failed)
        ):
            return True
        operation = "finishOnboarding" if self.pending_outcome is not None else "status"
        data = {"outcome": self.pending_outcome} if self.pending_outcome is not None else None
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
        return True

    def respond(self, operation, result):
        self.request_running = False
        if self.closed:
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
        elif self.pending_outcome is not None:
            self.poll()
        elif not isinstance(result, dict):
            self.status.set_text("未连接当前会话的 TypeLay。请先从应用菜单启动客户端。")
        elif not result.get("modelReady"):
            self.status.set_text("本地模型尚未就绪。请在 TypeLay 设置中完成组件安装。")
        elif self.source.has_focus() and self.window.is_active():
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
                    "本地模型与原生输入连接已就绪。能否接受，以当前真实浮层的提示为准。"
                )
        return False

    def close(self, *_):
        if not self.closed:
            self.closed = True
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
