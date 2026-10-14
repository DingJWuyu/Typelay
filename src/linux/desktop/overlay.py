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
        self.visible = self.editing = self.acceptance_ready = False
        self.revision = 0
        self.edit_trace_count = 0
        self.pending = False
        self.lock = asyncio.Lock()
        self.input.ui_event = self.event
        self.timer = self.input.glib.timeout_add(200, self.renew)

    def configure(self, settings):
        self.settings = settings
        if self.value:
            self.schedule()

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

    def schedule(self):
        self.revision += 1
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
            if self.peer and self.peer is not peer and self.peer.link.channel.alive:
                self.peer.link.command("overlay-hide")
            self.peer = peer
            reply = await peer.link.ask(
                "overlay-render",
                {
                    "target": record["target"]["value"],
                    "authority": record["authority"],
                    "generation": self.input.shortcut_config.get("generation"),
                    "view": self.value,
                    "revision": revision,
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
            self.input.set_overlay_focus(value is True and peer is self.input.focused_peer())
        elif operation == "beginEdit":
            self.tasks(self.begin_edit(peer, event["target"], token))
        elif operation == "settings":
            self.settings_requested()
        elif operation == "languages":
            self.tasks(self.set_languages(value))
        elif operation == "expand":
            self.schedule()
        elif operation in ("return", "escape"):
            self.host.send(operation, token)
        elif operation in ("regenerate", "exclude"):
            self.host.send(operation)
        elif operation in ("draft", "composition"):
            self.host.send(operation, value)

    async def begin_edit(self, peer, target, token):
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
                peer.link.command(
                    "overlay-edit",
                    {
                        "target": target,
                        "allowed": True,
                        "revision": revision,
                        "generation": editor_token["commandGeneration"],
                    },
                )
        except (OSError, EOFError, TimeoutError):
            self.conceal("transport-unavailable")

    async def set_languages(self, value):
        if self.settings and isinstance(value, dict):
            await self.host.ask(
                "settings",
                {**self.settings, "source": value.get("source"), "target": value.get("target")},
            )

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
