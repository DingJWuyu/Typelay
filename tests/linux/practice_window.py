"""Real GTK lesson controls with held replies on an isolated control socket.

Run by the setup desktop scenario in its existing Wayland display. This checks
presentation/lifecycle only, never native target ownership, translation or writes.
"""

import json
import os
import socket
import sys
import tempfile
import time
import uuid
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from niri import contents, request  # noqa: E402
from practice import GLib, Gtk, Practice  # noqa: E402
from wire import encode, receive_sync  # noqa: E402


def run(presentation):
    assert Gtk.init_check([])[0], "practice controls require the setup GTK display"
    context = GLib.MainContext.default()

    def until(condition):
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            while context.pending():
                context.iteration(False)
            result = condition()
            if result:
                return result
            time.sleep(0.005)
        raise AssertionError("practice controls did not reach the expected response state")

    with tempfile.TemporaryDirectory(prefix="typelay-practice-ui-") as directory:
        path = Path(directory) / "control.sock"
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as server:
            server.bind(str(path))
            path.chmod(0o600)
            server.listen(4)
            server.setblocking(False)
            connections, errors = [], []
            lesson = Practice(SimpleNamespace(control=path))

            def pending(operation):
                def accept():
                    try:
                        return server.accept()[0]
                    except BlockingIOError:
                        return None

                connection = until(accept)
                connections.append(connection)
                connection.settimeout(1)
                frame = receive_sync(connection)
                assert frame["op"] == operation, (operation, frame["op"])
                return connection

            def reply(connection, value):
                connection.sendall(encode({"id": 1, "data": value}))
                connection.close()

            def dispatch(connection, value):
                worker = lesson.request_thread
                reply(connection, value)
                worker.join(timeout=2)
                assert not worker.is_alive()
                while context.pending():
                    context.iteration(False)

            def state(run_id=None, *, bound=True, completed=False):
                notice = (
                    "按需练习已完成：已生成预览，并已验证直接替换。"
                    if completed
                    else "已识别练习输入框，尚未生成预览。"
                    if bound and run_id
                    else "练习输入框尚未连接，请点击输入框；若仍未连接，请检查应用支持。"
                )
                return {
                    "managedPractice": True,
                    "modelReady": True,
                    "practiceEnabled": True,
                    "presentation": {
                        **presentation,
                        "confirmationText": None,
                        "currentStateNotice": "",
                        "previewStep": presentation["previewStep"]
                        + (" 已生成预览。" if completed else ""),
                        "replaceStep": presentation["replaceStep"]
                        + (" 已验证直接替换。" if completed else ""),
                        "progressNotice": notice,
                        "completed": completed,
                    },
                    "progress": {
                        "runId": run_id,
                        "target": {"value": "controlled-field"} if bound else None,
                        "previewCompleted": completed,
                    }
                    if run_id
                    else None,
                }

            reset_notice = "练习进度已重置。请按上方第 1 步重新生成预览。"
            source = "User text stays selected"

            def unchanged():
                assert lesson.source.get_text() == source
                assert lesson.source.get_selection_bounds() == (2, 7)

            def cleared():
                assert not lesson.complete.get_sensitive()
                assert not lesson.reset_button.get_sensitive()
                assert lesson.preview_step.get_text() == lesson.replace_step.get_text() == ""
                assert not lesson.progress.get_text()
                assert "正在准备" in lesson.status.get_text()
                unchanged()

            def restart(previous):
                stale = pending("practiceStatus")
                lesson.reset_button.clicked()
                cleared()
                reply(stale, previous)
                begin = pending("startAutomaticPractice")
                cleared()
                return begin

            def exercise():
                try:
                    windows = until(
                        lambda: [
                            window
                            for window in contents(request("Windows"), "Windows")
                            if window.get("pid") == os.getpid()
                        ]
                    )
                    assert len(windows) == 1, "practice control test window is not unique"
                    request({"Action": {"FocusWindow": {"id": windows[0]["id"]}}})
                    until(lesson.window.is_active)
                    initial = state()
                    initial["presentation"]["confirmationText"] = "确认切换工作模式并开始练习？"
                    reply(pending("practiceStatus"), initial)
                    until(lambda: lesson.start.get_sensitive())
                    lesson.source.set_text(source)
                    lesson.source.select_region(2, 7)
                    lesson.start.clicked()
                    until(
                        lambda: lesson.confirmation is not None and lesson.confirmation.is_active()
                    )
                    lesson.confirmation.response(Gtk.ResponseType.CANCEL)
                    until(lesson.window.is_active)
                    assert lesson.confirmation is None
                    assert lesson.pending_start is None
                    unchanged()
                    lesson.start.clicked()
                    until(
                        lambda: lesson.confirmation is not None and lesson.confirmation.is_active()
                    )
                    lesson.confirmation.response(Gtk.ResponseType.OK)
                    begin = pending("startAutomaticPractice")
                    until(lesson.window.is_active)
                    unchanged()
                    reply(begin, True)
                    until(lesson.source.has_focus)
                    old = str(uuid.uuid4())
                    reply(pending("practiceStatus"), state(old, completed=True))
                    until(lambda: lesson.complete.get_sensitive())
                    assert lesson.complete.get_accessible().get_name() == "完成并关闭"
                    assert not lesson.status.get_text()
                    assert (
                        lesson.progress.get_text()
                        == state(old, completed=True)["presentation"]["progressNotice"]
                    )
                    technical = lesson.capability_note.get_parent().get_parent()
                    assert isinstance(technical, Gtk.Expander)
                    assert technical.get_label() == "连接与输入法说明"
                    assert not technical.get_expanded()
                    assert lesson.capability_note.get_text() == presentation["capabilityNote"]
                    lesson.finish("completed")
                    dispatch(pending("finishOnboarding"), False)
                    assert not lesson.complete.get_sensitive()
                    assert "正在确认" in lesson.status.get_text()
                    reply(
                        pending("practiceStatus"),
                        {**state(old, completed=True), "finishPending": True},
                    )
                    until(lambda: lesson.complete.get_sensitive())
                    assert "重试保存" in lesson.complete.get_label()
                    assert lesson.complete.get_accessible().get_name() == "重试保存"
                    reply(pending("practiceStatus"), state(old))
                    until(lambda: not lesson.complete.get_sensitive())
                    assert "重试保存" not in lesson.complete.get_label()
                    assert lesson.complete.get_accessible().get_name() == "完成并关闭"
                    reply(pending("practiceStatus"), state(old, completed=True))
                    until(lambda: lesson.complete.get_sensitive())
                    lesson.source.set_text(source)
                    lesson.source.select_region(2, 7)
                    reply(restart(state(old, completed=True)), True)
                    new = str(uuid.uuid4())
                    reply(pending("practiceStatus"), state(new, bound=False))
                    until(lambda: "等待确认" in lesson.status.get_text())
                    assert not lesson.progress.get_text()
                    assert lesson.status.get_text() != reset_notice
                    unchanged()
                    reply(pending("practiceStatus"), state(new))
                    until(lambda: lesson.progress.get_text() == reset_notice)
                    assert not lesson.status.get_text()
                    unchanged()
                    dispatch(pending("practiceStatus"), state(old, completed=True))
                    assert lesson.progress.get_text() == reset_notice
                    assert not lesson.status.get_text()
                    assert not lesson.complete.get_sensitive()

                    # A second restart before any preview clears the first receipt.
                    reply(restart(state(new)), True)
                    previous = str(uuid.uuid4())
                    reply(pending("practiceStatus"), state(previous))
                    until(lambda: lesson.progress.get_text() == reset_notice)
                    assert not lesson.status.get_text()
                    unchanged()

                    reply(restart(state(previous)), False)
                    refresh = pending("practiceStatus")
                    assert "未能开始" in lesson.status.get_text()
                    refreshed = state()
                    refreshed["presentation"]["sampleText"] = "新的原文示例"
                    reply(refresh, refreshed)
                    until(lambda: lesson.source.get_placeholder_text() == "新的原文示例")
                    assert lesson.reset_button.get_sensitive()
                    assert "未能开始" in lesson.status.get_text()
                    assert not lesson.complete.get_sensitive()
                    reply(restart(state()), True)
                    dispatch(pending("practiceStatus"), state(previous, completed=True))
                    assert not lesson.reset_button.get_sensitive()
                    assert not lesson.complete.get_sensitive()
                    assert lesson.preview_step.get_text() == lesson.replace_step.get_text() == ""
                    assert lesson.status.get_text() != reset_notice
                    reply(pending("practiceStatus"), state(str(uuid.uuid4())))
                    until(lambda: lesson.progress.get_text() == reset_notice)
                    assert not lesson.status.get_text()
                    unchanged()

                    # Switching the lesson cannot adopt a completed reply from
                    # the previous selection, nor replace the selected source.
                    stale = pending("practiceStatus")
                    lesson.lesson_choice.set_active_id("onDemand")
                    dispatch(stale, state(str(uuid.uuid4()), completed=True))
                    assert not lesson.complete.get_sensitive()
                    reply(pending("practiceStatus"), {**state(), "ending": True})
                    until(lambda: lesson.status.get_text() == "正在结束练习…")
                    assert not lesson.start.get_sensitive() and not lesson.complete.get_sensitive()
                    reply(pending("practiceStatus"), {**state(), "endUnconfirmed": True})
                    until(lambda: "未确认结束" in lesson.status.get_text())
                    assert not lesson.reset_button.get_sensitive()
                    unchanged()
                    late = pending("practiceStatus")
                    changes = []
                    lesson.status.connect("notify::label", lambda *_: changes.append(True))
                    lesson.close_button.clicked()
                    reply(pending("endOnDemandPractice"), True)
                    dispatch(late, state(str(uuid.uuid4()), completed=True))
                    assert not changes and not lesson.window.get_visible()
                except BaseException as error:
                    errors.append(error)
                    lesson.close()
                return False

            GLib.idle_add(exercise)
            try:
                lesson.run()
            finally:
                for connection in connections:
                    connection.close()
            if errors:
                raise errors[0]

            # Closing destroys the visible/native field immediately; the hidden
            # context may only finish its already-bounded control request. Both
            # EOF and the unchanged socket deadline must exit, without reopening.
            for failure in ("disconnect", "timeout"):
                lesson = Practice(SimpleNamespace(control=path))
                connections.clear()

                def close_without_reply():
                    try:
                        lesson.close()
                        worker = lesson.close_thread
                        lesson.close()
                        assert lesson.close_thread is worker
                        assert not lesson.window.get_visible() and lesson.source is None
                        connection = pending("endOnDemandPractice")
                        if failure == "disconnect":
                            connection.close()
                    except BaseException as error:
                        errors.append(error)
                    return False

                GLib.idle_add(close_without_reply)
                started = time.monotonic()
                try:
                    lesson.run()
                    assert time.monotonic() - started < 3
                    assert not lesson.close_thread.is_alive()
                    assert not lesson.window.get_visible()
                finally:
                    for connection in connections:
                        connection.close()
                if errors:
                    raise errors[0]


if __name__ == "__main__":
    run(json.loads(sys.argv[1]))
