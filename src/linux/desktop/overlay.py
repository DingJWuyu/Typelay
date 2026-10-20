"""Source-bound native overlay bridge for the shared session controller."""

import asyncio
import copy
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
        self.glossary = None
        self.glossary_begin_pending = None
        self.lock = asyncio.Lock()
        self.input.ui_event = self.event
        self.input.glossary_focus_transfer = self.consume_glossary_focus_transfer
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

    def schedule(self, visual_expansion_from=None, *, retain_revision=False):
        if not retain_revision:
            self.revision += 1
        # A later schedule replaces this one-use visual hint, even if its view
        # happens to have identical text. It grants no input/display authority.
        self.visual_expansion = (self.revision, visual_expansion_from)
        if not self.pending:
            self.pending = True
            self.tasks(self.update())

    def render(self, value):
        def editor_view(view):
            if not isinstance(view, dict) or not isinstance(view.get("viewContext"), dict):
                return view
            return {
                **view,
                "viewContext": {
                    key: item
                    for key, item in view["viewContext"].items()
                    if key != "translationActions"
                },
            }

        # Menu availability/feedback is read-only presentation, not a new editor
        # authority. Every older view field still participates in its identity.
        retain_revision = (
            self.value is not None
            and self.value != value
            and editor_view(self.value) == editor_view(value)
        )
        attempt = self.glossary_begin_pending
        if attempt and attempt["nativeSource"] and not self.glossary:
            # Only the original explicit Begin may retain its transport identity
            # across its one synchronous permit/menu revocation render. Compare
            # every other field, including unknown translation action fields.
            previous, following = attempt["view"], copy.deepcopy(value)
            if previous.get("canAccept") is True and following.get("canAccept") is False:
                following["canAccept"] = True
                accept = previous.get("viewContext", {}).get("bindings", {}).get("accept")
                old_hint = previous.get("presentation", {}).get("tabHint")
                new_hint = following.get("presentation", {}).get("tabHint")
                if (
                    isinstance(accept, str)
                    and old_hint == f"{accept} 接受译文"
                    and new_hint == f"{accept} 交还应用"
                ):
                    following["presentation"]["tabHint"] = old_hint
            old_actions = previous.get("viewContext", {}).get("translationActions")
            new_actions = following.get("viewContext", {}).get("translationActions")
            if (
                isinstance(old_actions, dict)
                and isinstance(new_actions, dict)
                and old_actions.get("canOpenGlossary") is True
                and new_actions.get("canOpenGlossary") is False
                and old_actions.get("glossaryUnavailableReason") is None
                and new_actions.get("glossaryUnavailableReason") == "请先完成当前术语表单"
            ):
                new_actions["canOpenGlossary"] = True
                new_actions["glossaryUnavailableReason"] = None
            record = self.current_record()
            retain_revision = (
                not attempt["rendered"]
                and self.peer is attempt["peer"]
                and self.revision == attempt["revision"]
                and self.value == previous
                and value != previous
                and following == previous
                and record is not None
                and record["authority"] == attempt["authority"]
                and (self.input.window_epoch, self.input.foreground_epoch) == attempt["window"]
            )
            if retain_revision:
                attempt["rendered"] = copy.deepcopy(value)
            else:
                self.glossary_begin_pending = None
        native_form = (
            self.glossary
            and self.glossary["peer"].backend in ("gtk3", "qt6")
            and self.glossary["entry"].get("sourceOrigin") is True
        )
        if native_form:
            previous, following = self.glossary["sourceView"], copy.deepcopy(value)
            context = following.get("viewContext", {})
            old_context = previous.get("viewContext", {})
            # Match the native source-form contract: only the actual auxiliary
            # ACK's one-way withdrawal; preserve every other action/view field.
            if previous.get("editable") is True and following.get("editable") is False:
                following["editable"] = True
            if old_context.get("canEdit") is True and context.get("canEdit") is False:
                context["canEdit"] = True
            old_actions = old_context.get("translationActions", {})
            actions = context.get("translationActions", {})
            if (
                isinstance(old_actions, dict)
                and isinstance(actions, dict)
                and old_actions.get("glossaryUnavailableReason") == "请先完成当前术语表单"
                and actions.get("glossaryUnavailableReason") == "内容或设置已变化，请重新打开表单"
            ):
                actions["glossaryUnavailableReason"] = "请先完成当前术语表单"
            current_actions = self.value.get("viewContext", {}).get("translationActions")
            next_actions = value.get("viewContext", {}).get("translationActions")
            retain_revision = (
                previous == following
                and not (self.value.get("editable") is False and value.get("editable") is True)
                and not (
                    self.value.get("viewContext", {}).get("canEdit") is False
                    and value.get("viewContext", {}).get("canEdit") is True
                )
                and not (
                    isinstance(current_actions, dict)
                    and isinstance(next_actions, dict)
                    and current_actions.get("glossaryUnavailableReason")
                    == "内容或设置已变化，请重新打开表单"
                    and next_actions.get("glossaryUnavailableReason") == "请先完成当前术语表单"
                )
            )
        elif self.glossary and not retain_revision and self.value:
            previous, following = (
                copy.deepcopy(editor_view(self.value)),
                copy.deepcopy(editor_view(value)),
            )
            for projection in (previous, following):
                projection.pop("editable", None)
                projection.get("viewContext", {}).pop("canEdit", None)
            # Auxiliary focus intentionally withdraws the editor ACK. That is
            # not a replacement form, source token, or translation buffer.
            retain_revision = previous == following
        if self.glossary and (
            value.get("state") != self.glossary["state"]
            or value.get("viewContext", {}).get("token") != self.glossary["entry"]["token"]
            or not retain_revision
            and value != self.value
        ):
            self.close_glossary()
        self.value = value
        self.schedule(retain_revision=retain_revision)

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
                revision, view = self.revision, self.value
                if not await self.send_view():
                    break
                if revision == self.revision and view is self.value:
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
                if value is not True and not (
                    self.glossary
                    and self.glossary["entry"].get("sourceOrigin") is True
                    and (
                        peer.backend not in ("gtk3", "qt6")
                        or (self.glossary.get("transfer") or {}).get("phase") == "Returning"
                    )
                ):
                    self.close_glossary()
        elif operation == "sessionGlossaryBegin":
            if value is None and self.glossary_origin_supported():
                self.tasks(self.begin_glossary(peer, event["revision"], copy.deepcopy(token)))
        elif operation == "sessionGlossaryFocus":
            form = self.glossary
            if form and (value is None or self.glossary_focus_valid(value, form)):
                form["focus"] = copy.deepcopy(value)
                if value is not None:
                    self.input.set_overlay_focus(True)
                self.host.send("sessionGlossaryFocus", value)
        elif operation == "sessionGlossaryClose":
            if self.glossary and value == self.glossary["entry"]["formId"]:
                self.close_glossary(notify_peer=False)
        elif operation == "sessionGlossaryAdd":
            form = self.glossary
            if (
                form
                and not form["pending"]
                and not form["unknown"]
                and form["focus"]
                and form["focus"]["ime"] == "Clear"
                and isinstance(value, dict)
                and set(value) == {"formId", "token", "settingsRevision", "rule"}
                and all(
                    value[key] == form["entry"][key]
                    for key in ("formId", "token", "settingsRevision")
                )
                and isinstance(value["rule"], dict)
                and set(value["rule"])
                == {"sourceTerm", "targetTerm", "sourceLanguage", "targetLanguage"}
            ):
                form["pending"] = True
                self.tasks(self.add_glossary(form, copy.deepcopy(value)))
        elif operation == "beginEdit":
            self.tasks(self.begin_edit(peer, event["target"], token))
        elif operation == "settings":
            self.settings_requested()
        elif operation == "languages":
            self.tasks(self.set_languages(value))
        elif operation == "alternative":
            self.host.send("alternative", copy.deepcopy(token))
        elif operation == "style":
            if (
                isinstance(value, dict)
                and set(value) == {"settingsRevision", "style"}
                and type(value["settingsRevision"]) is int
                and 0 <= value["settingsRevision"] <= 9223372036854775807
                and (value["style"] is None or value["style"] in ("Natural", "Formal"))
            ):
                self.tasks(
                    self.set_session_style(
                        peer, event["revision"], copy.deepcopy(token), dict(value)
                    )
                )
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

    def glossary_origin_supported(self):
        return bool(
            self.value
            and (
                self.value.get("state") == "EditingTranslation"
                or self.value.get("state") in ("ValidTranslation", "EditedTranslationPending")
                and self.input.source_glossary_supported(self.current_record())
            )
        )

    def glossary_current(self, form):
        return (
            self.glossary is form
            and self.peer is form["peer"]
            and self.revision == form["revision"]
            and self.session_token() == form["entry"]["token"]
            and self.value.get("state") == form["state"]
            and self.current_record() is not None
            and (
                form["entry"].get("sourceOrigin") is not True
                or self.input.source_glossary_supported(self.current_record())
                and self.current_record().get("route") == form["route"]
            )
            and (
                form["peer"].backend not in ("gtk3", "qt6")
                or form["entry"].get("sourceOrigin") is not True
                or (form["window"], form["foreground"])
                == (self.input.window_epoch, self.input.foreground_epoch)
            )
        )

    def consume_glossary_focus_transfer(self, peer, value, route):
        form = self.glossary
        transfer = form.get("transfer") if form else None
        marker = value.get("glossaryTransfer")
        if (
            not transfer
            or transfer["consumed"]
            or not self.glossary_current(form)
            or form["entry"].get("sourceOrigin") is not True
            or peer is not form["peer"]
            or route != form["route"]
            or form["entry"]["token"]["target"] != self.input.bound
            or value.get("target") != form["entry"]["token"]["target"]["value"]
            or value.get("textChanged") is not False
            or value.get("focusChanged") is not True
            or (transfer["window"], transfer["foreground"])
            != (self.input.window_epoch, self.input.foreground_epoch)
        ):
            return False
        expected = {
            "formId": form["entry"]["formId"],
            "token": form["entry"]["token"],
            "phase": transfer["phase"],
        }
        if peer.backend in ("gtk3", "qt6"):
            if not isinstance(marker, dict) or set(marker) != {*expected, "ordinal", "count"}:
                return False
            ordinal, count = marker["ordinal"], marker["count"]
            if (
                type(ordinal) is not int
                or type(count) is not int
                or not 1 <= ordinal <= count <= 8
                or ordinal != transfer.get("next", 1)
                or count != transfer.get("count", count)
                or any(marker[key] != value for key, value in expected.items())
                or type(value.get("focused")) is not bool
                or any(
                    value.get(key) != form["sourceAuthority"].get(key)
                    for key in ("inputRevision", "contentRevision")
                )
                or ordinal == count
                and value["focused"] is not (transfer["phase"] == "Returning")
            ):
                return False
            transfer["next"], transfer["count"] = ordinal + 1, count
            transfer["consumed"] = ordinal == count
            return True
        if (
            peer.backend != "browser"
            or marker != expected
            or value.get("focused") is not (transfer["phase"] == "Returning")
        ):
            return False
        # The isolated document emits this marker only after its synchronous
        # trusted blur/focus pair reached the exact requested control. Consume
        # it once; it preserves window identity, never a capture/write permit.
        transfer["consumed"] = True
        return True

    @staticmethod
    def glossary_focus_valid(value, form):
        return (
            isinstance(value, dict)
            and set(value) == {"formId", "token", "ime"}
            and value["formId"] == form["entry"]["formId"]
            and value["token"] == form["entry"]["token"]
            and value["ime"] in ("Clear", "Composing", "Unknown")
        )

    def glossary_command(self, form, operation, **payload):
        form["peer"].link.command(
            operation,
            {
                "target": form["entry"]["token"]["target"]["value"],
                "generation": form["entry"]["token"]["commandGeneration"],
                "revision": form["revision"],
                "formId": form["entry"]["formId"],
                **payload,
            },
        )

    def close_glossary(self, *, notify_peer=True):
        self.glossary_begin_pending = None
        form, self.glossary = self.glossary, None
        if form:
            if (
                self.input.source_glossary_return
                and self.input.source_glossary_return["formId"] == form["entry"]["formId"]
            ):
                self.input.source_glossary_return = None
            self.host.send("sessionGlossaryFocus", None)
            self.host.send("sessionGlossaryClose", form["entry"]["formId"])
            if notify_peer and form["peer"].link.channel.alive:
                self.glossary_command(form, "overlay-glossary-close")

    async def begin_glossary(self, peer, revision, token):
        if (
            self.glossary
            or self.glossary_begin_pending
            or self.peer is not peer
            or self.revision != revision
            or self.session_token() != token
            or not self.glossary_origin_supported()
            or self.current_record() is None
        ):
            return
        attempt = self.glossary_begin_pending = {
            "nativeSource": peer.backend in ("gtk3", "qt6")
            and self.value.get("state") in ("ValidTranslation", "EditedTranslationPending"),
            "peer": peer,
            "revision": revision,
            "view": copy.deepcopy(self.value),
            "authority": copy.deepcopy(self.current_record()["authority"]),
            "window": (self.input.window_epoch, self.input.foreground_epoch),
            "rendered": None,
        }
        try:
            entry = await self.host.ask("sessionGlossaryBegin", token)
            if not isinstance(entry, dict) or not isinstance(entry.get("formId"), str):
                if (
                    self.peer is peer
                    and self.revision == revision
                    and self.session_token() == token
                ):
                    peer.link.command(
                        "overlay-glossary-open",
                        {
                            "target": token["target"]["value"],
                            "generation": token["commandGeneration"],
                            "revision": revision,
                            "entry": None,
                        },
                    )
                return
            if (
                self.glossary_begin_pending is not attempt
                or entry.get("token") != token
                or self.peer is not peer
                or self.revision != revision
                or self.session_token() != token
                or not self.glossary_origin_supported()
                or (entry.get("sourceOrigin") is True)
                != (self.value.get("state") != "EditingTranslation")
                or self.current_record() is None
            ):
                self.host.send("sessionGlossaryClose", entry["formId"])
                return
            self.glossary = {
                "entry": copy.deepcopy(entry),
                "peer": peer,
                "revision": revision,
                "state": self.value["state"],
                "route": copy.deepcopy(self.current_record().get("route")),
                "sourceAuthority": copy.deepcopy(self.current_record()["authority"]),
                "sourceView": copy.deepcopy(self.value),
                "window": self.input.window_epoch,
                "foreground": self.input.foreground_epoch,
                "focus": None,
                "pending": False,
                "unknown": False,
            }
            form = self.glossary
            transfer = None
            if entry.get("sourceOrigin") is True:
                transfer = form["transfer"] = {
                    "phase": "Opening",
                    "consumed": False,
                    "window": self.input.window_epoch,
                    "foreground": self.input.foreground_epoch,
                }
            try:
                response = await peer.link.ask(
                    "overlay-glossary-open",
                    {
                        "target": token["target"]["value"],
                        "generation": token["commandGeneration"],
                        "revision": revision,
                        "entry": entry,
                    },
                )
            finally:
                form["transfer"] = None
            if self.glossary is form and (
                not self.glossary_current(form)
                or transfer is not None
                and (
                    not transfer["consumed"]
                    or getattr(response, "route", None) != form["route"]
                    or (transfer["window"], transfer["foreground"])
                    != (self.input.window_epoch, self.input.foreground_epoch)
                )
                or not isinstance(response, dict)
                or response.get("opened") is not True
            ):
                self.close_glossary()
        except (OSError, ValueError, EOFError, TimeoutError):
            if (
                self.glossary
                and self.glossary["peer"] is peer
                and self.glossary["entry"]["token"] == token
            ):
                self.close_glossary()
            elif self.peer is peer and self.revision == revision and self.session_token() == token:
                peer.link.command(
                    "overlay-glossary-open",
                    {
                        "target": token["target"]["value"],
                        "generation": token["commandGeneration"],
                        "revision": revision,
                        "entry": None,
                    },
                )
        finally:
            if self.glossary_begin_pending is attempt:
                self.glossary_begin_pending = None

    async def return_glossary_to_source(self, request):
        form = self.glossary
        if (
            not isinstance(request, dict)
            or set(request) != {"formId", "token"}
            or not form
            or form["entry"].get("sourceOrigin") is not True
            or not self.glossary_current(form)
            or not form["pending"]
            or request["formId"] != form["entry"]["formId"]
            or request["token"] != form["entry"]["token"]
            or not form["focus"]
            or form["focus"]["ime"] != "Clear"
            or self.input.focus() != "Overlay"
        ):
            return None
        window, foreground = self.input.window_epoch, self.input.foreground_epoch
        transfer = form["transfer"] = {
            "phase": "Returning",
            "consumed": False,
            "window": window,
            "foreground": foreground,
        }
        try:
            response = await form["peer"].link.ask(
                "overlay-glossary-return",
                {
                    **request,
                    "target": request["token"]["target"]["value"],
                    "generation": request["token"]["commandGeneration"],
                    "revision": form["revision"],
                },
            )
        except (OSError, ValueError, EOFError, TimeoutError):
            return None
        finally:
            form["transfer"] = None
        if (
            not transfer["consumed"]
            or not self.glossary_current(form)
            or not isinstance(response, dict)
            or set(response) != {"formId", "token", "returned"}
            or response.get("formId") != request["formId"]
            or response.get("token") != request["token"]
            or response.get("returned") is not True
            or (window, foreground) != (self.input.window_epoch, self.input.foreground_epoch)
            or getattr(response, "route", None) != form["route"]
        ):
            return None
        # This proves only the native/browser focus transfer. The controller obtains
        # its own fresh source capture before any persistence or new permission.
        form["focus"] = None
        self.input.source_glossary_return = {
            **copy.deepcopy(request),
            "peer": form["peer"],
            "route": copy.deepcopy(form["route"]),
            "windowEpoch": window,
            "foregroundEpoch": foreground,
            "sourceAuthority": copy.deepcopy(form["sourceAuthority"]),
        }
        self.input.set_overlay_focus(False)
        self.host.send("sessionGlossaryFocus", None)
        return dict(response)

    async def add_glossary(self, form, request):
        if not self.glossary_current(form):
            form["pending"] = False
            return
        # A later focus/IME notification is rechecked by the controller. Return
        # its definite refusal so the form can unlock the original inputs.
        try:
            result = await self.host.ask("sessionGlossaryAdd", request)
        except (OSError, ValueError, EOFError, TimeoutError):
            result = None
        if self.glossary_current(form):
            form["pending"] = False
            form["unknown"] = (
                not isinstance(result, dict)
                or type(result.get("saved")) is not bool
                or result.get("code") in ("glossary-unconfirmed", "glossary-outcome-unknown")
            )
        # Complete the original request even when the successful commit rendered
        # a new session and retired this form. The browser matches the exact old
        # identity and cannot reopen it or attach this result to a newer form.
        self.glossary_command(form, "overlay-glossary-result", result=result)

    async def set_session_style(self, peer, revision, token, value):
        if (
            self.peer is not peer
            or self.revision != revision
            or self.session_token() != token
            or self.current_record() is None
        ):
            return
        try:
            # The shared controller owns qualification, persistence and feedback.
            # Never rebuild this request from a newer token after an await.
            await self.host.ask("sessionStyle", {"token": token, **value})
        except (OSError, ValueError, EOFError, TimeoutError):
            if self.peer is peer and self.revision == revision and self.session_token() == token:
                self.conceal("transport-unavailable")

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
        self.close_glossary()
        self.revision += 1
        if self.peer and self.peer.link.channel.alive:
            self.peer.link.command("overlay-hide")
        self.peer = None
        self.visible = self.editing = self.acceptance_ready = False
        self.input.set_overlay_focus(False)

    def hide(self):
        self.conceal("host-hide")
        self.value = None
