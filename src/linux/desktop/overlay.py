"""Source-bound native overlay bridge for the shared session controller."""

import asyncio
import copy
import json
import os
import sys
import uuid

from settings_feedback import notice as commit_notice
from wire import now_ms


def presentation_identity(view):
    """Read the negotiated product identity; never infer it from display text."""
    context = view.get("viewContext") if isinstance(view, dict) else None
    if not isinstance(context, dict):
        return None
    identity, form = context.get("identity"), context.get("glossaryForm")
    if not isinstance(identity, dict) or set(identity) != {
        "version",
        "interactionId",
        "displaySequence",
        "draftRevision",
        "actionRevision",
    }:
        return None
    if (
        any(
            type(identity.get(key)) is not int
            for key in ("version", "displaySequence", "draftRevision", "actionRevision")
        )
        or identity["version"] != 1
    ):
        return None
    if not all(
        minimum <= identity[key] <= 2**53 - 1
        for key, minimum in (("displaySequence", 1), ("draftRevision", 0), ("actionRevision", 1))
    ):
        return None
    try:
        token = identity["interactionId"]
        if not isinstance(token, str) or str(uuid.UUID(token)) != token.lower():
            return None
        if form is not None:
            if not isinstance(form, dict) or set(form) != {"formId", "current"}:
                return None
            token = form["formId"]
            if (
                type(form["current"]) is not bool
                or not isinstance(token, str)
                or str(uuid.UUID(token)) != token.lower()
            ):
                return None
    except ValueError:
        return None
    return identity


class Overlay:
    """The shared view rendered in its actual GTK parent, without global coordinates."""

    def __init__(self, host, input_service, tasks, settings_requested, languages_requested=None):
        self.host, self.input, self.tasks = host, input_service, tasks
        self.settings_requested = settings_requested
        self.languages_requested = languages_requested or settings_requested
        self.value = self.settings = self.peer = None
        self.language_choices = None
        self.visible = self.editing = self.acceptance_ready = False
        self.revision = 0
        self.visual_expansion = None
        self.edit_trace_count = 0
        self.edit_click_trace_count = 0
        self.pending = False
        self.glossary = None
        self.glossary_begin_pending = None
        self.lock = asyncio.Lock()
        self.input.ui_event = self.event
        self.timer = self.input.glib.timeout_add(200, self.renew)

    def configure(self, settings):
        if self.input.language_configuration_open and settings != self.settings:
            self.input.cancel_language_configuration()
        self.settings = settings
        if self.value:
            self.schedule(retain_revision=True)

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
                self.schedule(retain_revision=True)

    def language_label(self, code, source):
        for choice in (self.language_choices or {}).get("source" if source else "target", []):
            if choice["code"] == code:
                return choice["label"]
        return str(code or "") + "（当前不可用）"

    def session_token(self):
        context = self.value.get("viewContext") if self.value else None
        return context.get("token") if isinstance(context, dict) else None

    def current_record(self):
        return self.input.current_presentation(self.value)

    def schedule(self, visual_expansion_from=None, *, retain_revision=False):
        if not retain_revision:
            self.revision += 1
        # A later schedule replaces this one-use visual hint, even if its view
        # happens to have identical text. It grants no input/display authority.
        self.visual_expansion = (self.revision, visual_expansion_from)
        if self.value:
            self.input.presented(self.value, self.revision)
        if not self.pending:
            self.pending = True
            self.tasks(self.update())

    def render(self, value):
        identity = presentation_identity(value)
        if identity is None:
            self.hide()
            return
        previous = presentation_identity(self.value)
        if previous and identity["displaySequence"] < previous["displaySequence"]:
            return
        if (
            previous
            and identity["displaySequence"] == previous["displaySequence"]
            and identity != previous
        ):
            return
        # Native tickets retain their own authority and deadlines. A display
        # update must not replace the transport identity of a live interaction.
        retain_revision = bool(previous and previous["interactionId"] == identity["interactionId"])
        self.input.cancel_language_configuration()
        attempt = self.glossary_begin_pending
        if attempt and not self.glossary:
            current = (
                retain_revision
                and self.peer is attempt["peer"]
                and self.revision == attempt["revision"]
                and self.session_token() == value.get("viewContext", {}).get("token")
                and all(
                    attempt["identity"][key] == identity[key]
                    for key in ("interactionId", "draftRevision", "actionRevision")
                )
                and self.input.glossary_opening_current(attempt["opening"])
            )
            if not current:
                self.input.finish_glossary_opening(attempt["opening"])
                self.glossary_begin_pending = None
        form = self.glossary
        if form:
            projection = value.get("viewContext", {}).get("glossaryForm")
            if (
                not retain_revision
                or value.get("state") != form["state"]
                or value.get("viewContext", {}).get("token") != form["entry"]["token"]
                or not projection
                or projection["formId"] != form["entry"]["formId"]
                or not form["current"]
                and projection["current"]
            ):
                self.close_glossary()
            elif not projection["current"]:
                form["current"] = False
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
            peer = record.connection
            revision = self.revision
            visual_expansion, self.visual_expansion = self.visual_expansion, None
            if self.peer and self.peer is not peer:
                self.input.conceal_source(self.peer)
            self.peer = peer
            reply = await self.input.present_source(
                record,
                {
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
                    "appearance": {
                        key: (self.settings or {}).get(key, default)
                        for key, default in (
                            ("font", "claude"),
                            ("textSize", 15),
                            ("accent", "clay"),
                            ("uiLanguage", "system"),
                            ("reduceMotion", False),
                            (
                                "motion",
                                "reduce" if (self.settings or {}).get("reduceMotion") else "system",
                            ),
                            ("reduceTransparency", False),
                        )
                    },
                },
            )
            if paused_view:
                self.trace_edit(
                    "pause-view-reply",
                    requestRevision=revision,
                    current=self.peer is peer and self.revision == revision,
                    visible=isinstance(reply, dict) and reply.get("visible") is True,
                )
            if (
                self.peer is peer
                and self.revision == revision
                and self.input.presentation_current(record)
            ):
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
        self.input.renew_language_configuration()
        record = self.current_record()
        if self.value and record:
            if self.peer is record.connection and self.visible:
                self.input.renew_source_presentation(record)
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

    def trace_resume(self, stage, **fields):
        if os.environ.get("TYPELAY_TRACE") and getattr(self, "resume_trace_count", 0) < 8:
            self.resume_trace_count = getattr(self, "resume_trace_count", 0) + 1
            self.trace_edit(stage, **fields)

    def trace_edit_click(self, peer, event):
        if not os.environ.get("TYPELAY_TRACE") or self.edit_click_trace_count >= 16:
            return
        detail = event.get("value")
        reasons = {
            "pointer": {"received"},
            "begin-edit": {
                "wrong-widget",
                "non-primary",
                "already-editing",
                "paused",
                "not-editable",
                "pending",
                "configuration-changed",
                "context-changed",
                "cannot-edit",
                "emitted",
            },
        }
        if (
            not isinstance(detail, dict)
            or set(detail) != {"phase", "reason", "elapsedMs", "candidateHit"}
            or type(detail["phase"]) is not str
            or type(detail["reason"]) is not str
            or detail["reason"] not in reasons.get(detail["phase"], set())
            or type(detail["elapsedMs"]) is not int
            or not 0 <= detail["elapsedMs"] <= 9223372036854775807
            or type(detail["candidateHit"]) is not bool
        ):
            return
        peer_matches = peer is self.peer
        target_matches = bool(
            self.value and event.get("target") == self.value["source"]["target"]["value"]
        )
        revision_matches = type(event.get("revision")) is int and event["revision"] == self.revision
        self.edit_click_trace_count += 1
        self.trace_edit(
            "native-click",
            **detail,
            peerMatches=peer_matches,
            targetMatches=target_matches,
            revisionMatches=revision_matches,
        )

    def event(self, peer, event):
        if isinstance(event, dict) and event.get("operation") in (
            "languageConfigurationSave",
            "languageConfigurationCancel",
        ):
            self.input.language_configuration_event(peer, event)
            return  # Configuration messages can never fall through to source actions.
        if isinstance(event, dict) and event.get("operation") == "editClickDiagnostic":
            self.trace_edit_click(peer, event)
            return  # Even malformed diagnostics can never become an edit action.
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
            if isinstance(event, dict) and event.get("operation") == "resume":
                self.trace_resume(
                    "resume-reject-owner",
                    hasView=self.value is not None,
                    peerMatches=peer is self.peer,
                )
            return
        operation, value = event["operation"], event.get("value")
        if self.value.get("state") == "Paused":
            if operation == "resume":
                revision_matches = (
                    type(event.get("revision")) is int and event["revision"] == self.revision
                )
                visible = self.visible if revision_matches else None
                record_current = (
                    self.current_record() is not None if revision_matches and visible else None
                )
                self.trace_resume(
                    "resume-event",
                    revisionMatches=revision_matches,
                    visible=visible,
                    recordCurrent=record_current,
                )
                if revision_matches and visible and record_current:
                    revision = self.value["pauseRevision"]
                    # Consume the displayed action before dispatching it. The
                    # controller reobserves metadata and owns the settings write.
                    self.hide()
                    self.host.send("resume", revision)
            elif operation == "visible":
                self.visible = value is True
            return
        if self.input.observe_editor_event(peer, event):
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
            if self.input.observe_overlay_focus(peer, value):
                if value is not True and not (
                    self.glossary
                    and self.glossary["entry"].get("sourceOrigin") is True
                    and (
                        peer.backend not in ("gtk3", "gtk4", "qt6")
                        or self.input.source_glossary_returning(self.glossary["source"])
                    )
                ):
                    self.close_glossary()
        elif operation == "sessionGlossaryBegin":
            if value is None and self.glossary_origin_supported():
                self.tasks(self.begin_glossary(peer, event["revision"], copy.deepcopy(token)))
        elif operation == "sessionGlossaryFocus":
            form = self.glossary
            if form and self.input.observe_glossary_focus(form["source"], value):
                form["focus"] = copy.deepcopy(value)
                self.host.send("sessionGlossaryFocus", value)
        elif operation == "sessionGlossaryClose":
            if self.glossary and value == self.glossary["entry"]["formId"]:
                self.close_glossary(notify_peer=False)
        elif operation == "sessionGlossaryAdd":
            form = self.glossary
            if (
                form
                and form["current"]
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
        elif operation == "languageConfigurationBegin":
            if (
                peer.backend in ("gtk4", "qt6")
                and isinstance(value, dict)
                and set(value) == {"openingId"}
                and self.input.valid_opening_id(value["openingId"])
            ):
                self.start_language_configuration(peer, value["openingId"], copy.deepcopy(token))
        elif operation == "languagesPicker":
            # Opening a desktop picker retires this source session. It does not
            # transfer or manufacture source-field focus or write permission.
            self.host.send("escape", copy.deepcopy(token))
            self.languages_requested()
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
        elif operation == "regenerate" and peer.backend == "browser":
            # The render ACK precedes overlay-edit, so its editing bool may still
            # be false after the real editor focused. Host state plus the exact
            # Input owner/focus/ACK gates admission, not that old presentation fact.
            editing = self.value.get("state") == "EditingTranslation"
            if value is None:
                if not editing or not self.current_record().editor_regenerate_supported:
                    self.host.send("regenerate")
            elif editing:
                action_id = self.input.begin_browser_regenerate(peer, event, copy.deepcopy(token))
                if action_id is not None:
                    self.tasks(self.input.regenerate_editor(action_id))
            # Nonplain Browser payloads never downgrade, including outside Editing.
        elif operation in ("regenerate", "exclude"):
            self.host.send(operation)
        elif operation in ("draft", "composition"):
            self.host.send(operation, value)

    def start_language_configuration(self, peer, opening_id, token):
        self.close_glossary()
        appearance = {
            "theme": (self.settings or {}).get("theme", "system"),
            "appearance": {
                key: (self.settings or {}).get(key, default)
                for key, default in (
                    ("font", "claude"),
                    ("textSize", 15),
                    ("accent", "clay"),
                    ("uiLanguage", "system"),
                    ("reduceMotion", False),
                    ("reduceTransparency", False),
                    ("motion", "system"),
                )
            },
        }
        if self.input.start_language_configuration(peer, opening_id, token, appearance, self.tasks):
            # The native source UI retired before the independent shell opened.
            self.revision += 1
            self.input.withdraw_presentation()
            self.value = self.peer = None
            self.visible = self.editing = self.acceptance_ready = False

    async def begin_edit(self, peer, target, token):
        opening = None
        try:
            record = self.current_record()
            if (
                not record
                or not record.editor_ready
                or self.session_token() != token
                or not self.value["viewContext"].get("canEdit")
            ):
                return
            opening = self.input.begin_editor_opening(record)
            if opening is None:
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
            if not self.input.prepare_editor_opening(opening, editor_token):
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
                await self.input.focus_source_editor(opening, revision)
        except (OSError, EOFError, TimeoutError):
            self.conceal("transport-unavailable")
        finally:
            self.input.finish_editor_opening(opening)

    def glossary_origin_supported(self):
        return bool(
            self.value
            and (
                self.value.get("state") == "EditingTranslation"
                or self.value.get("state") in ("ValidTranslation", "EditedTranslationPending")
                and bool(self.current_record() and self.current_record().source_glossary_supported)
            )
        )

    def glossary_current(self, form):
        return bool(
            self.glossary is form
            and self.peer is form["peer"]
            and self.revision == form["revision"]
            and self.session_token() == form["entry"]["token"]
            and self.value.get("state") == form["state"]
            and self.input.source_glossary_current(form["source"])
        )

    def close_glossary(self, *, notify_peer=True):
        if self.glossary_begin_pending:
            self.input.finish_glossary_opening(self.glossary_begin_pending["opening"])
        self.glossary_begin_pending = None
        form, self.glossary = self.glossary, None
        if form:
            self.input.close_source_glossary(form["source"], notify_peer=notify_peer)
            self.host.send("sessionGlossaryFocus", None)
            self.host.send("sessionGlossaryClose", form["entry"]["formId"])

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
        opening = self.input.begin_glossary_opening(self.current_record())
        if opening is None:
            return
        attempt = self.glossary_begin_pending = {
            "nativeSource": peer.backend in ("gtk3", "gtk4", "qt6")
            and self.value.get("state") in ("ValidTranslation", "EditedTranslationPending"),
            "peer": peer,
            "revision": revision,
            "identity": dict(presentation_identity(self.value)),
            "opening": opening,
        }
        try:
            entry = await self.host.ask("sessionGlossaryBegin", token)
            if not isinstance(entry, dict) or not isinstance(entry.get("formId"), str):
                if (
                    self.peer is peer
                    and self.revision == revision
                    and self.session_token() == token
                ):
                    self.input.decline_source_glossary(peer, token, revision)
                return
            projection = (
                self.value.get("viewContext", {}).get("glossaryForm") if self.value else None
            )
            if (
                self.glossary_begin_pending is not attempt
                or entry.get("token") != token
                or not projection
                or projection["formId"] != entry["formId"]
                or not projection["current"]
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
            source = self.input.bind_source_glossary(self.current_record(), entry, revision)
            if source is None:
                self.host.send("sessionGlossaryClose", entry["formId"])
                return
            self.glossary = {
                "entry": copy.deepcopy(entry),
                "peer": peer,
                "revision": revision,
                "state": self.value["state"],
                "source": source,
                "current": True,
                "focus": None,
                "pending": False,
                "unknown": False,
            }
            form = self.glossary
            opened = await self.input.open_source_glossary(source)
            if self.glossary is form and (not opened or not self.glossary_current(form)):
                self.close_glossary()
        except (OSError, ValueError, EOFError, TimeoutError):
            if (
                self.glossary
                and self.glossary["peer"] is peer
                and self.glossary["entry"]["token"] == token
            ):
                self.close_glossary()
            elif self.peer is peer and self.revision == revision and self.session_token() == token:
                self.input.decline_source_glossary(peer, token, revision)
        finally:
            self.input.finish_glossary_opening(opening)
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
        ):
            return None
        response = await self.input.return_glossary_to_source(form["source"], request)
        if response is not None:
            form["focus"] = None
            self.host.send("sessionGlossaryFocus", None)
        return response

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
        self.input.complete_source_glossary(form["source"], result)

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
            if isinstance(saved, dict) and saved.get("state") == "CommittedNotificationFailed":
                self.input.settings_commit_reported(saved)
            if commit_notice(saved) and self.settings is before and self.value is view and view:
                self.value = {
                    **view,
                    "status": commit_notice(saved),
                }
                self.schedule(retain_revision=True)

    def resume(self):
        if self.peer and self.value:
            self.input.resume_source(self.peer, self.session_token(), self.revision)

    def conceal(self, reason):
        # A transient missing/stale native observation must hide the actual UI,
        # but cannot discard the host's desired view: DesktopView deduplicates
        # unchanged renders. Renew retries only after this same target regains
        # a current, focused native observation, with a new display authority.
        if self.value and self.peer and os.environ.get("TYPELAY_TRACE"):
            print(f"typelay-overlay conceal reason={reason} atMs={now_ms()}", file=sys.stderr)
        self.close_glossary()
        self.input.withdraw_presentation()
        self.revision += 1
        if self.peer:
            self.input.conceal_source(self.peer)
        self.peer = None
        self.visible = self.editing = self.acceptance_ready = False
        self.input.set_overlay_focus(False)

    def hide(self):
        if self.input.language_configuration_open:
            # StopSession's hide acknowledges source retirement, not cancellation
            # of the separately identified configuration request.
            self.input.withdraw_presentation()
            self.value = self.peer = None
            self.visible = self.editing = self.acceptance_ready = False
            self.input.set_overlay_focus(False)
            return
        self.conceal("host-hide")
        self.value = None
