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
        self.close_thread = None
        self.presentation = None
        self.confirmation = None
        self.completion_failed = False
        self.request_running = False
        self.request_thread = None
        self.request_generation = 0
        self.practice_run = None
        self.restart_from = None
        self.restarting = False
        self.awaiting_binding = False
        self.start_error = None
        self.lesson = "automatic"
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

        self.lesson_choice = Gtk.ComboBoxText()
        self.lesson_choice.append("automatic", "自动翻译与编辑")
        self.lesson_choice.append("onDemand", "按需快捷键")
        self.lesson_choice.set_active_id(self.lesson)
        self.lesson_choice.get_accessible().set_name("练习内容")
        self.lesson_choice.connect("changed", self.choose_lesson)
        content.pack_start(self.lesson_choice, False, False, 0)
        self.heading = note("")
        self.heading.get_style_context().add_class("title")
        self.summary = note("")
        self.current_state = note("")
        self.preview_step = note("")
        self.replace_step = note("")
        self.input_label = note("")
        self.source = Gtk.Entry()
        Gtk.Buildable.set_name(self.source, "typelay-practice-source")
        self.source.set_max_length(2048)
        self.source.get_accessible().set_name("练习输入框")
        content.pack_start(self.source, False, False, 0)
        self.start = Gtk.Button(label="…")
        self.start.set_sensitive(False)
        self.start.connect("clicked", self.start_clicked)
        content.pack_start(self.start, False, False, 0)
        self.status = note("等待连接当前 Wayland 会话的 TypeLay；请聚焦上面的练习框。")
        self.progress = note("尚未开始本次练习。")
        settings = Gtk.Button(label="打开 TypeLay 设置")
        settings.connect("clicked", lambda *_: self.request_settings())
        content.pack_start(settings, False, False, 0)
        technical = Gtk.Expander(label="连接与输入法说明")
        technical.set_expanded(False)
        details = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14)
        details.set_margin_top(8)
        technical.add(details)
        self.capability_note = note("", details)
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
        note("以后不再自动提示；你仍可随时从设置打开练习。")
        actions = Gtk.Box(spacing=8)
        outer.pack_end(actions, False, False, 0)
        self.reset_button = Gtk.Button.new_with_mnemonic("重新练习(_R)")
        self.reset_button.set_sensitive(False)
        self.reset_button.connect("clicked", self.reset)
        actions.pack_start(self.reset_button, False, False, 0)
        self.complete = Gtk.Button.new_with_mnemonic("完成并关闭(_D)")
        self.complete.get_accessible().set_name("完成并关闭")
        self.complete.set_sensitive(False)
        self.complete.set_focus_on_click(False)
        self.complete.connect("clicked", lambda *_: self.finish("completed"))
        actions.pack_start(self.complete, False, False, 0)
        self.skip = Gtk.Button.new_with_mnemonic("跳过教程(_S)")
        self.skip.connect("clicked", lambda *_: self.finish("skipped"))
        actions.pack_start(self.skip, False, False, 0)
        self.close_button = Gtk.Button(label="关闭")
        self.close_button.connect("clicked", self.close)
        actions.pack_end(self.close_button, False, False, 0)
        self.timer = GLib.timeout_add(1000, self.poll)

    def choose_lesson(self, widget):
        lesson = widget.get_active_id()
        if self.closed or lesson == self.lesson:
            return
        self.lesson = lesson
        self.completion_failed = False
        self.complete.set_label("完成并关闭(_D)")
        self.complete.get_accessible().set_name("完成并关闭")
        self.request_generation += 1
        self.practice_run = self.presentation = self.restart_from = None
        self.awaiting_binding = self.restarting = False
        self.start_error = None
        self.set_start_sensitive(False)
        self.complete.set_sensitive(False)
        self.preview_step.set_text("")
        self.replace_step.set_text("")
        self.status.set_text("正在读取练习说明…")
        self.poll()

    def set_start_sensitive(self, enabled):
        self.start.set_sensitive(enabled)
        self.reset_button.set_sensitive(enabled)

    def start_clicked(self, *_):
        if (
            self.closed
            or self.presentation is None
            or self.pending_start is not None
            or self.pending_outcome is not None
            or not self.start.get_sensitive()
        ):
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
            if self.confirmation is not widget:
                return
            self.confirmation = None
            timestamp = Gtk.get_current_event_time()
            widget.destroy()
            if not self.closed:
                # Return to this top-level when the user dismisses the dialog.
                # A later Host reply must not activate a window they have left.
                self.window.present_with_time(timestamp)
                if response == Gtk.ResponseType.OK:
                    self.begin(True)

        dialog.connect("response", respond)
        dialog.show_all()

    def begin(self, confirmed_switch):
        if (
            self.closed
            or self.pending_start is not None
            or self.pending_outcome is not None
            or not self.start.get_sensitive()
        ):
            return
        self.completion_failed = False
        self.complete.set_label("完成并关闭(_D)")
        self.complete.get_accessible().set_name("完成并关闭")
        self.request_generation += 1
        self.restart_from = self.practice_run or self.restart_from
        self.restarting = self.restarting or self.practice_run is not None
        self.practice_run = None
        self.awaiting_binding = True
        self.start_error = None
        self.presentation = {**self.presentation, "completed": False}
        self.pending_start = {"confirmedSwitch": confirmed_switch}
        self.lesson_choice.set_sensitive(False)
        self.set_start_sensitive(False)
        self.complete.set_sensitive(False)
        self.preview_step.set_text("")
        self.replace_step.set_text("")
        self.status.set_text("正在准备本次练习，请稍候再按快捷键。")
        self.progress.set_text("")
        self.poll()

    def request_settings(self):
        self.pending_settings = True
        self.poll()

    def reset(self, *_):
        # Restart only Runtime progress; the user keeps ownership of their text.
        self.start_clicked()

    def finish(self, outcome):
        if self.closed or self.pending_outcome is not None:
            return
        if outcome == "completed" and not (self.presentation or {}).get("completed"):
            return
        self.pending_outcome = outcome
        self.completion_failed = False
        self.complete.set_sensitive(False)
        self.skip.set_sensitive(False)
        self.status.set_text("正在关闭首次使用提示…")
        self.poll()

    def poll(self):
        if self.closed:
            return False
        if self.request_running:
            return True
        if self.pending_outcome is not None:
            self.request(
                "finishOnboarding", {"outcome": self.pending_outcome, "lesson": self.lesson}
            )
        elif self.pending_start is not None:
            self.request(
                "startAutomaticPractice" if self.lesson == "automatic" else "startOnDemandPractice",
                self.pending_start,
            )
        elif self.pending_settings:
            self.pending_settings = False
            self.request("settings", None)
        elif self.window.is_active() and self.completion_failed != "skipped":
            self.request("practiceStatus", {"lesson": self.lesson})
        return True

    def request(self, operation, data):
        self.request_running = True
        generation = self.request_generation

        def request():
            try:
                result = control(self.paths, operation, data)
            except (OSError, EOFError, ValueError):
                result = None
            GLib.idle_add(self.respond, operation, result, generation)

        # Socket waits never block the widget/IME thread. Only one bounded
        # control request can be in flight; it never includes practice text.
        self.request_thread = threading.Thread(target=request, name="typelay-practice-status")
        self.request_thread.start()

    def respond(self, operation, result, generation):
        self.request_running = False
        if self.closed:
            return False
        if generation != self.request_generation:
            # A status request may already be in flight when Restart is clicked.
            # Its old run must not restore a ready/completed lesson while Begin waits.
            self.poll()
            return False
        if operation in ("startOnDemandPractice", "startAutomaticPractice"):
            self.pending_start = None
            self.lesson_choice.set_sensitive(True)
            if result is True:
                if self.window.is_active():
                    self.source.grab_focus_without_selecting()
                self.status.set_text("等待确认本次练习框连接，请稍候再按快捷键。")
                self.progress.set_text("")
            else:
                self.start_error = "未能开始本次练习。请检查当前模式、启用状态、模型和语言设置，再点击开始按钮重试。"
                self.awaiting_binding = False
                self.set_start_sensitive(True)
                self.status.set_text(self.start_error)
                self.progress.set_text("本次练习尚未开始，未继承之前的进度。")
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
            self.completion_failed = self.pending_outcome
            self.pending_outcome = None
            self.complete.set_sensitive(False)
            self.skip.set_sensitive(True)
            self.status.set_text(
                "未能完成，正在确认练习状态…"
                if self.completion_failed == "completed"
                else "未能关闭提示，请重试跳过教程。"
            )
            self.poll()
        elif (
            self.pending_outcome is not None
            or self.pending_start is not None
            or self.pending_settings
        ):
            self.poll()
        elif not isinstance(result, dict):
            self.complete.set_sensitive(False)
            self.set_start_sensitive(False)
            self.status.set_text("未连接当前会话的 TypeLay。请先从应用菜单启动客户端。")
        else:
            if self.completion_failed == "completed":
                if result.get("ending") or result.get("endUnconfirmed"):
                    self.complete.set_sensitive(False)
                    self.set_start_sensitive(False)
                    self.status.set_text(
                        "未确认结束练习，请关闭练习并重新启动 TypeLay 后重试。"
                        if result.get("endUnconfirmed")
                        else "正在结束练习…"
                    )
                    return False
                if result.get("finishPending"):
                    self.complete.set_sensitive(True)
                    self.complete.set_label("重试保存(_D)")
                    self.complete.get_accessible().set_name("重试保存")
                    self.status.set_text("完成尚未保存。可重试保存，或重新开始练习。")
                    return False
                self.completion_failed = False
                self.complete.set_label("完成并关闭(_D)")
                self.complete.get_accessible().set_name("完成并关闭")
            view = result.get("presentation")
            if not isinstance(view, dict):
                self.complete.set_sensitive(False)
                self.set_start_sensitive(False)
                self.status.set_text("无法读取练习说明，请检查 TypeLay 客户端版本后重新打开。")
                return False
            progress = result.get("progress")
            if isinstance(progress, dict) and progress.get("runId") == self.restart_from:
                return False
            current = isinstance(progress, dict) and progress.get("runId") and not self.start_error
            if current:
                self.practice_run = progress["runId"]
                if progress.get("target") is not None:
                    self.awaiting_binding = False
            elif self.awaiting_binding or self.start_error:
                # Failed or uncertain Begin must not readopt the retired run.
                view = {**view, "completed": False, "previewStep": "", "replaceStep": ""}
            else:
                self.practice_run = None
            if self.awaiting_binding or self.start_error:
                view = {**view, "completed": False}
            self.presentation = view
            self.complete.set_sensitive(view.get("completed") is True)
            if not self.start_error:
                self.progress.set_text(view["progressNotice"] if not self.awaiting_binding else "")
            self.reset_button.set_label(view["restartLabel"] + "(_R)")
            self.reset_button.get_accessible().set_name(view["restartLabel"])
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
            self.start.set_label(
                view["restartLabel"] if result.get("progress") else view["startLabel"]
            )
            self.close_button.set_label(view["closeLabel"])
            ending = result.get("ending") or result.get("endUnconfirmed")
            self.set_start_sensitive(
                not ending
                and result.get("managedPractice", False)
                and result.get("modelReady", False)
                and result.get("practiceEnabled", False)
                and not view["languageNeedsChange"]
            )
            if ending:
                self.progress.set_text("")
                self.complete.set_sensitive(False)
                self.status.set_text(
                    "未确认结束练习，请关闭练习并重新启动 TypeLay 后重试。"
                    if result.get("endUnconfirmed")
                    else "正在结束练习…"
                )
                return False
            if not result.get("managedPractice"):
                self.complete.set_sensitive(False)
                self.status.set_text(
                    "此窗口未绑定当前客户端的受管练习。请从设置的应用支持重新连接并打开练习；历史教程偏好不代表完成。"
                )
                return False
            if not result.get("modelReady"):
                self.status.set_text("本地模型尚未就绪。请在 TypeLay 设置中完成组件安装。")
                return False
            if view["currentStateNotice"]:
                self.status.set_text("")
                return False
            if self.start_error:
                self.status.set_text(self.start_error)
                return False
            if self.awaiting_binding:
                self.status.set_text("等待确认本次练习框连接，请稍候再按快捷键。")
                return False
            if current and self.restarting and not progress.get("previewCompleted"):
                self.progress.set_text("练习进度已重置。请按上方第 1 步重新生成预览。")
                self.status.set_text("")
                return False
            if current and progress.get("previewCompleted"):
                self.restarting = False
            self.status.set_text("")
        return False

    def close(self, *_):
        if not self.closed:
            self.closed = True

            def retire():
                try:
                    control(self.paths, "endOnDemandPractice")
                except (OSError, EOFError, ValueError):
                    pass
                finally:
                    # The window is already destroyed. Keep the existing GTK
                    # context alive only for the bounded native revoke response.
                    GLib.idle_add(lambda: (Gtk.main_quit(), False)[1])

            if self.confirmation is not None:
                self.confirmation.destroy()
                self.confirmation = None
            if self.timer:
                GLib.source_remove(self.timer)
                self.timer = 0
            self.window.destroy()
            self.source = None
            self.close_thread = threading.Thread(target=retire, name="typelay-practice-close")
            self.close_thread.start()
        return True

    def run(self):
        self.window.show_all()
        self.source.grab_focus()
        GLib.idle_add(lambda: (self.poll(), False)[1])
        try:
            Gtk.main()
        finally:
            self.closed = True
            for thread in (self.request_thread, self.close_thread):
                if thread:
                    thread.join(timeout=2)


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
