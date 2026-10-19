"""Source-bound native overlay bridge for the shared session controller."""

import asyncio
import json
import os
import sys

from wire import now_ms


class Overlay:
    """The shared view rendered in its actual GTK parent, without global coordinates."""

    def __init__(self, host, input_service, tasks, settings_requested):
        self.host, self.input, self.tasks = host, input_service, tasks
        self.settings_requested = settings_requested
        self.value = self.settings = self.peer = None
        self.language_choices = None
        self.visible = self.editing = self.acceptance_ready = False
        self.revision = 0
        self.visual_expansion = None
        self.edit_trace_count = 0
        self.pending = False
        self.lock = asyncio.Lock()
        self.input.ui_event = self.event
        self.timer = self.input.glib.timeout_add(200, self.renew)

    def configure(self, settings):
        self.settings = settings
        if self.value:
            self.schedule()

    def render_guidance(self, guidance):
        def valid_text(text, maximum):
            return (
                isinstance(text, str)
                and "\0" not in text
                and not any(0xD800 <= ord(character) <= 0xDFFF for character in text)
                and 0 < len(text.encode("utf-16-le")) // 2 <= maximum
            )

        choices = guidance.get("languageChoices") if isinstance(guidance, dict) else None
        if not isinstance(choices, dict) or any(
            not isinstance(choices.get(key), list)
            or not 0 < len(choices[key]) <= 32
            or any(
                not isinstance(choice, dict)
                or not valid_text(choice.get("code"), 16)
                or not valid_text(choice.get("label"), 80)
                for choice in choices[key]
            )
            or len({choice["code"] for choice in choices[key]}) != len(choices[key])
            for key in ("source", "target")
        ):
            choices = None
        if choices != self.language_choices:
            self.language_choices = (
                {key: [dict(choice) for choice in choices[key]] for key in ("source", "target")}
                if choices is not None
                else None
            )
            if self.value:
                self.schedule()

    def language_label(self, code, source):
        for choice in (self.language_choices or {}).get("source" if source else "target", []):
            if choice["code"] == code:
                return choice["label"]
        return str(code or "") + "（当前不可用）"

    def session_token(self):
        context = self.value.get("viewContext") if self.value else None
        return context.get("token") if isinstance(context, dict) else None

    def current_record(self):
        paused = bool(self.value and self.value.get("state") == "Paused")
        record = self.input.current_pause_record() if paused else self.input.bound_observation
        if (
            not self.value
            or not record
            or record["target"] != self.value["source"]["target"]
            or record["peer"] not in self.input.peers
            or record["peer"] is not self.input.focused_peer()
            or not self.input.fresh()
            or (
                not paused
                and (
                    not self.session_token()
                    or self.session_token().get("commandGeneration")
                    != self.input.shortcut_config.get("generation")
                )
            )
        ):
            return None
        return record

    def schedule(self, visual_expansion_from=None):
        self.revision += 1
        # A later schedule replaces this one-use visual hint, even if its view
        # happens to have identical text. It grants no input/display authority.
        self.visual_expansion = (self.revision, visual_expansion_from)
        if not self.pending:
            self.pending = True
            self.tasks(self.update())

    def render(self, value):
        self.value = value
        self.schedule()

    async def send_view(self):
        async with self.lock:
            record = self.current_record()
            paused_view = bool(self.value and self.value.get("state") == "Paused")
            if paused_view:
                self.trace_edit("pause-view-request", hasRecord=record is not None)
            if record is None:
                self.conceal("observation-unavailable")
                return False
            peer = record["peer"]
            revision = self.revision
            visual_expansion, self.visual_expansion = self.visual_expansion, None
            if self.peer and self.peer is not peer and self.peer.link.channel.alive:
                self.peer.link.command("overlay-hide")
            self.peer = peer
            reply = await peer.link.ask(
                "overlay-render",
                {
                    "target": record["target"]["value"],
                    "authority": record["authority"],
                    "generation": self.input.shortcut_config.get("generation"),
                    "view": {
                        **self.value,
                        "languageChoices": self.language_choices,
                        "sourceLanguageLabel": self.language_label(
                            self.value.get("sourceLanguage"), True
                        ),
                        "targetLanguageLabel": self.language_label(
                            self.value.get("targetLanguage"), False
                        ),
                    },
                    "revision": revision,
                    "visualExpansionFrom": visual_expansion[1]
                    if visual_expansion and visual_expansion[0] == revision
                    else None,
                    "theme": (self.settings or {}).get("theme", "system"),
                    "expiresAtMs": now_ms() + 800,
                },
            )
            if paused_view:
                self.trace_edit(
                    "pause-view-reply",
                    requestRevision=revision,
                    current=self.peer is peer and self.revision == revision,
                    visible=isinstance(reply, dict) and reply.get("visible") is True,
                )
            if self.peer is peer and self.revision == revision:
                self.visible = bool(reply and reply.get("visible"))
                self.editing = bool(reply and reply.get("editing"))
                self.acceptance_ready = self.visible and self.value.get("canAccept", False)
            return True

    async def update(self):
        try:
            while self.value:
                revision = self.revision
                if not await self.send_view():
                    break
                if revision == self.revision:
                    break
        except (OSError, EOFError, TimeoutError):
            self.conceal("transport-unavailable")
        finally:
            self.pending = False

    def renew(self):
        record = self.current_record()
        if self.value and record:
            if self.peer is record["peer"] and self.visible:
                self.peer.link.command(
                    "overlay-renew",
                    {
                        "target": record["target"]["value"],
                        "authority": record["authority"],
                        "generation": self.input.shortcut_config.get("generation"),
                        "expiresAtMs": now_ms() + 800,
                    },
                )
            elif not self.pending:
                self.schedule()
        elif self.value:
            self.conceal("observation-unavailable")
        return not self.input.closing

    def trace_edit(self, stage, **fields):
        if os.environ.get("TYPELAY_TRACE") and self.edit_trace_count < 96:
            self.edit_trace_count += 1
            try:
                print(
                    "typelay-edit "
                    + json.dumps(
                        {"stage": stage, "atMs": now_ms(), "revision": self.revision, **fields}
                    ),
                    file=sys.stderr,
                    flush=True,
                )
            except OSError:
                pass

    def event(self, peer, event):
        if isinstance(event, dict) and event.get("operation") == "beginEdit":
            self.trace_edit(
                "event",
                eventRevision=(event["revision"] if type(event.get("revision")) is int else None),
                hasView=self.value is not None,
                peerMatches=peer is self.peer,
                targetMatches=bool(
                    self.value and event.get("target") == self.value["source"]["target"]["value"]
                ),
            )
        if (
            not self.value
            or peer is not self.peer
            or event["target"] != self.value["source"]["target"]["value"]
        ):
            return
        operation, value = event["operation"], event.get("value")
        if self.value.get("state") == "Paused":
            if operation == "resume":
                if (
                    type(event.get("revision")) is int
                    and event["revision"] == self.revision
                    and self.visible
                    and self.current_record() is not None
                ):
                    revision = self.value["pauseRevision"]
                    # Consume the displayed action before dispatching it. The
                    # controller reobserves metadata and owns the settings write.
                    self.hide()
                    self.host.send("resume", revision)
            elif operation == "visible":
                self.visible = value is True
            return
        token = self.session_token()
        if (
            not token
            or event.get("generation") != token.get("commandGeneration")
            or event.get("generation") != self.input.shortcut_config.get("generation")
            or type(event.get("revision")) is not int
            or event["revision"] != self.revision
            or self.current_record() is None
        ):
            return
        if operation == "visible":
            self.visible = value is True
            if not self.visible:
                self.editing = self.acceptance_ready = False
                self.input.set_overlay_focus(False)
        elif operation == "focus":
            editor = self.input.practice_editor
            if not editor or editor["phase"] == "editor":
                self.input.set_overlay_focus(value is True and peer is self.input.focused_peer())
        elif operation == "beginEdit":
            self.tasks(self.begin_edit(peer, event["target"], token))
        elif operation == "settings":
            self.settings_requested()
        elif operation == "languages":
            self.tasks(self.set_languages(value))
        elif operation == "expand":
            self.schedule(visual_expansion_from=event["revision"])
        elif operation in ("return", "escape"):
            self.host.send(operation, token)
        elif operation in ("regenerate", "exclude"):
            self.host.send(operation)
        elif operation in ("draft", "composition"):
            self.host.send(operation, value)

    async def begin_edit(self, peer, target, token):
        authorization = self.input.practice_authorization
        editor = None
        deadline = now_ms() + 500
        try:
            record = self.current_record()
            acknowledgement = self.input.current_session_acknowledgement(record)
            if (
                not acknowledgement
                or not acknowledgement["editorReady"]
                or self.session_token() != token
                or not self.value["viewContext"].get("canEdit")
            ):
                return
            requested_revision = self.revision
            response = await self.host.ask("beginEdit", token)
            allowed = isinstance(response, dict) and response.get("allowed") is True
            editor_token = response.get("token") if allowed else None
            self.trace_edit(
                "host-reply",
                requestedRevision=requested_revision,
                allowed=allowed is True,
                peerMatches=self.peer is peer,
                targetMatches=bool(
                    self.value and self.value["source"]["target"]["value"] == target
                ),
                editing=bool(self.value and self.value.get("state") == "EditingTranslation"),
            )
            if (
                allowed is not True
                or not isinstance(editor_token, dict)
                or editor_token.get("target") != token.get("target")
                or editor_token.get("commandGeneration") != token.get("commandGeneration")
                or self.session_token() != editor_token
                or self.current_record() is None
                or not self.value
                or self.peer is not peer
                or self.value["source"]["target"]["value"] != target
                or self.value.get("state") != "EditingTranslation"
            ):
                return
            if authorization is not None:
                editor = self.input.begin_practice_edit(authorization, editor_token, deadline)
                if editor is None:
                    return
            # The state transition's render precedes the reply on the host channel;
            # flush it before asking GTK to focus its editor. No source text is changed.
            value, revision = self.value, self.revision
            sent = await self.send_view()
            self.trace_edit(
                "render-reply",
                requestedRevision=revision,
                sent=sent,
                sameView=self.value is value,
                peerMatches=self.peer is peer,
                visible=self.visible,
            )
            if (
                sent
                and self.value is value
                and self.revision == revision
                and self.peer is peer
                and self.visible
                and self.session_token() == editor_token
                and self.current_record() is not None
            ):
                command = {
                    "target": target,
                    "allowed": True,
                    "revision": revision,
                    "generation": editor_token["commandGeneration"],
                }
                if editor is None:
                    peer.link.command("overlay-edit", command)
                elif await self.input.revoke_practice_editor(editor):
                    if (
                        self.value is not value
                        or self.revision != revision
                        or self.peer is not peer
                        or self.session_token() != editor_token
                    ):
                        return
                    editor["phase"] = "focusing"
                    reply = await peer.link.ask(
                        "overlay-edit",
                        {
                            **command,
                            "practiceHandoff": {
                                "token": editor_token,
                                "nativeFocusRevision": editor["nativeFocusRevision"],
                                "contextGeneration": editor["contextGeneration"],
                            },
                        },
                    )
                    if (
                        self.value is value
                        and self.revision == revision
                        and self.peer is peer
                        and self.session_token() == editor_token
                    ):
                        self.input.complete_practice_edit(editor, reply)
        except (OSError, EOFError, TimeoutError):
            self.conceal("transport-unavailable")
        finally:
            if editor is not None and editor["phase"] != "editor":
                self.input.retire_practice(editor["runId"])

    async def set_languages(self, value):
        if (
            self.settings
            and self.language_choices is not None
            and isinstance(value, dict)
            and all(
                isinstance(value.get(key), str)
                and value[key] in {choice["code"] for choice in self.language_choices[key]}
                for key in ("source", "target")
            )
        ):
            before, view = self.settings, self.value
            try:
                saved = await self.host.ask(
                    "traySettings", {key: value[key] for key in ("source", "target")}
                )
            except (OSError, ValueError, EOFError, TimeoutError):
                saved = None
            if saved is not True and self.settings is before and self.value is view and view:
                self.value = {
                    **view,
                    "status": "语言设置未保存，请在设置中检查语言方向或保存权限。"
                    if saved is False
                    else "语言设置结果未确认，请打开设置检查。",
                }
                self.schedule()

    def resume(self):
        if self.peer and self.value:
            self.peer.link.command(
                "overlay-resume",
                {
                    "target": self.value["source"]["target"]["value"],
                    "generation": (self.session_token() or {}).get("commandGeneration"),
                    "revision": self.revision,
                },
            )

    def conceal(self, reason):
        # A transient missing/stale native observation must hide the actual UI,
        # but cannot discard the host's desired view: DesktopView deduplicates
        # unchanged renders. Renew retries only after this same target regains
        # a current, focused native observation, with a new display authority.
        if self.value and self.peer and os.environ.get("TYPELAY_TRACE"):
            print(f"typelay-overlay conceal reason={reason} atMs={now_ms()}", file=sys.stderr)
        self.revision += 1
        if self.peer and self.peer.link.channel.alive:
            self.peer.link.command("overlay-hide")
        self.peer = None
        self.visible = self.editing = self.acceptance_ready = False
        self.input.set_overlay_focus(False)

    def hide(self):
        self.conceal("host-hide")
        self.value = None
