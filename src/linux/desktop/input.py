"""Linux target authority. All text policy and product decisions stay in Core.

Only the peer's GTK thread mutates a field. The desktop authenticates its process,
validates native anchors, obtains Core approval before reading, and accounts
for one-shot receipts. A late or missing receipt is never interpreted as success.
"""

import copy
import json
import math
import os
import socket
import sys
import uuid
from collections import Counter, OrderedDict
from dataclasses import dataclass

from geometry import anchor_geometry
from link import Link, RoutedReply
from niri import Niri
from paths import peer_credentials, process_identity, process_lineage
from wire import MAX_FRAME, now_ms


def _browser_draft(value):
    # Keep the JSON-safe representation until the .NET draft boundary. Decoding
    # lone units here would make the next UTF-8 pipe encode fail again.
    return isinstance(value, str) or (
        isinstance(value, dict)
        and set(value) == {"utf16"}
        and isinstance(value["utf16"], list)
        and len(value["utf16"]) < MAX_FRAME // 2
        and all(type(unit) is int and 0 <= unit <= 65535 for unit in value["utf16"])
    )


# Same presentation handoff as the browser hint and Windows owned precedent.
# This is not an input-latency guarantee and never alters a permit's deadline.
BROWSER_ACCEPTANCE_PRESENTATION_HANDOFF_MS = 25


def target_id(peer, value):
    return {"backend": f"linux-{peer.backend}", "value": value, "lifetime": peer.lifetime}


def target_key(target):
    return target["lifetime"], target["value"]


def observation_key(snapshot):
    return (*target_key(snapshot["target"]), snapshot["observedAtMs"])


def refused(unknown=False):
    return {
        "status": "OutcomeUnknown" if unknown else "RejectedBeforeMutation",
        "reason": "UnknownOutcome" if unknown else "StaleObservation",
    }


@dataclass(frozen=True)
class SourceConnection:
    """Opaque connection identity for presentation, never native authority."""

    connection_id: str
    backend: str


@dataclass(frozen=True)
class SourcePresentation:
    """A display snapshot with capabilities, without mutable native evidence."""

    connection: SourceConnection
    identity: str
    window_epoch: int
    foreground_epoch: int
    source_glossary_supported: bool
    editor_ready: bool
    editor_regenerate_supported: bool


@dataclass(frozen=True)
class SourceGlossary:
    connection: SourceConnection
    form_id: str
    token_json: str
    revision: int


@dataclass(frozen=True)
class SourceConnectionFacts:
    """Read-only diagnostics for application support, never capture authority."""

    lifetime: str | None
    connected: bool
    backend: str
    application: str
    pid: int
    observed_at_ms: int | None
    preview: bool | None
    tab_accept: bool | None
    direct_replace: bool | None


class Peer:
    def __init__(self, owner, connection, pid):
        self.owner = owner
        self.pid = pid
        self.application = process_identity(pid)
        self.backend = "gtk3"
        self.ancestors = {}
        self.started = None
        self.browser_origin = self.browser_home = None
        self.browser_registration = None
        self.browser_identity = None
        self.browser_suspended = False
        self.browser_had_v1 = False
        self.browser_retired = False
        self.browser_active_route = None
        self.lifetime = None
        self.connection_id = str(uuid.uuid4())
        self.source = None
        self.shortcut = None
        self.command_id = 0
        self.command_generation = None
        self.support_observation = None
        self.link = Link(
            owner.glib,
            connection.fileno(),
            connection.fileno(),
            self.event,
            None,
            lambda: owner.lost(self),
            connection,
            response_key="id",
        )
        self.hello_timer = owner.glib.timeout_add(1000, self.hello_timeout)

    def hello_timeout(self):
        self.hello_timer = 0
        if self.lifetime is None:
            self.link.close()
        return False

    def event(self, name, data, route=None):
        if self.lifetime is None:
            import uuid

            if name != "hello" or data["protocol"] != 2 or data["pid"] != self.pid:
                raise ValueError("native-handshake")
            self.backend = data.get("backend", "gtk3")
            if self.backend not in ("gtk3", "gtk4", "qt6", "browser"):
                raise ValueError("native-backend")
            if self.backend != "browser" and (
                type(data.get("presentationVersion")) is not int or data["presentationVersion"] != 1
            ):
                raise ValueError("native-presentation-version")
            if self.backend == "browser":
                self.ancestors = process_lineage(self.pid)
                self.started = self.ancestors.pop(self.pid, None)
                self.browser_origin, self.browser_home = data.get("origin"), data.get("home")
                self.browser_registration = self.owner.browser_registration(
                    self.browser_origin, self.browser_home, self.ancestors
                )
                self.browser_identity = self.browser_registration
                self.link.routed = True
            self.lifetime = str(uuid.UUID(data["lifetime"]))
            if self.hello_timer:
                self.owner.glib.source_remove(self.hello_timer)
                self.hello_timer = 0
            self.source = SourceConnection(self.connection_id, self.backend)
            self.owner.configure_peer(self)
            self.owner.changed()
        else:
            self.owner.event(
                self,
                name,
                RoutedReply(data, route)
                if self.backend == "browser" and isinstance(data, dict)
                else data,
                route=route,
            )

    def browser_alive(self):
        identity = self.browser_identity
        if not identity or not self.link.channel.alive:
            return False
        try:
            current = process_lineage(self.pid)
            return (
                current.get(self.pid) == self.started
                and current.get(identity["pid"]) == identity["started"]
            )
        except (OSError, ValueError, IndexError):
            return False

    def valid_browser_route(self, route, target=None):
        if not self.browser_identity or not isinstance(route, dict):
            return False
        try:
            token = route.get("documentToken")
            return (
                route.get("extensionId") == self.browser_origin[len("chrome-extension://") : -1]
                and isinstance(route.get("documentId"), str)
                and 0 < len(route["documentId"]) <= 128
                and isinstance(token, str)
                and str(uuid.UUID(token)) == token
                and all(
                    type(route.get(key)) is int and 0 <= route[key] < 2**53
                    for key in ("tabId", "windowId")
                )
                and type(route.get("frameId")) is int
                and route["frameId"] == 0
                and route.get("scheme") in ("http:", "https:")
                and (target is None or isinstance(target, str) and target.startswith(token + ":"))
            )
        except (ValueError, TypeError, AttributeError):
            return False

    @property
    def anchor_space(self):
        return {"browser": "browser-viewport", "gtk4": "gtk4-toplevel", "qt6": "qt-toplevel"}.get(
            self.backend, "gtk-toplevel"
        )

    def matches_process(self, pid):
        if self.backend in ("gtk3", "gtk4", "qt6"):
            return self.pid == pid
        if pid not in self.ancestors:
            return False
        try:
            if process_lineage(self.pid).get(pid) != self.ancestors[pid]:
                return False
            self.application = process_identity(pid)
            return True
        except (OSError, ValueError, IndexError):
            return False


class Input:
    def __init__(self, glib, gdk, paths, host, tasks, *, browser_registration=None):
        self.glib, self.gdk, self.paths, self.host, self.tasks = glib, gdk, paths, host, tasks
        self.browser_registration = browser_registration or (lambda *_: None)
        self._peers = set()
        self.observations = OrderedDict()
        self.frozen = OrderedDict()
        self.issued = OrderedDict()
        self.bound = None
        self._bound_observation = None
        self.pause_observation = None
        self.pause_generation = 0
        self.enabled = None
        self.settings_generation = 0
        self.overlay_focused = False
        self.ui_event = lambda *_: None
        self._language_configuration = None
        self._source_glossary = None
        self._glossary_opening = None
        self._source_glossary_return = None
        self._editor_regenerate = None
        self._presentation = None
        self._editor_openings = {}
        self.user_epoch = self.source_epoch = self.foreground_epoch = 0
        self.window_epoch = self.content_epoch = 0
        self.last_focus = None
        self.last_state = None
        self.capture_status = "not-started"
        self.capture_counts = Counter()
        self.capture_started_at = 0
        self.capture_failures = []
        self.native_capture_timing = None
        self.timing_metrics = {}
        self.browser_diagnostics = {}
        self.browser_capture = None
        self.browser_capture_generation = 0
        self.browser_capture_counts = Counter()
        self.browser_capture_round = 0
        self.browser_rounds = []
        self.browser_round_owners = {}
        self.tab_capture_rounds = []
        self.session_publication = None
        self.native_publication = None
        self.publication_generation = 0
        self.published_plan = None
        self.publication_epochs = None
        self.shortcut_config = {"enabled": False}
        self.shortcut_configured_at = now_ms()
        self.shortcut_cli_at = -1
        self.shortcut_cli_peer = None
        self.shortcut_sequence = 0
        self.shortcut_ordered = False
        self.shortcut_command = None
        self.shortcut_diagnostic_count = 0
        self.publish_diagnostics = []
        self.practice_failures = []
        self.practice_approval_trace_run = None
        self.practice_authorization = None
        self.practice_pending = None
        self.practice_retirement = None
        self.practice_retirement_task = None
        self.practice_retirement_ack = None
        self.practice_retirement_binding = None
        self.practice_continuity = None
        self.practice_editor = None
        self.practice_return = None
        self.practice_retired = lambda _run: None
        self.practice_settings_changed = lambda: None
        self.settings_commit_reported = lambda _result: None
        self.closing = False
        self.niri = Niri(glib, self.changed)
        self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        # The caller holds the per-session lock before touching either socket.
        paths.native.unlink(missing_ok=True)
        self.listener.bind(str(paths.native))
        paths.native.chmod(0o600)
        self.listener.listen(16)
        self.listener.setblocking(False)
        self.listener_watch = glib.io_add_watch(self.listener.fileno(), glib.IO_IN, self.accept)
        self.expiry_timer = glib.timeout_add(75, self.expire)

    def accept(self, _fd, _condition):
        try:
            connection, _ = self.listener.accept()
        except BlockingIOError:
            return True
        try:
            if len(self._peers) >= 64:
                raise OSError("native-capacity")
            pid, _, _ = peer_credentials(connection)
            peer = Peer(self, connection, pid)
            self._peers.add(peer)
            self.clear_browser_capture()
        except OSError:
            connection.close()
        return True

    def clear_browser_capture(self):
        self.browser_capture = None
        self.browser_capture_generation += 1

    def browser_capture_identity(self, peer):
        return (
            peer,
            peer.lifetime,
            self.window_epoch,
            self.user_epoch,
            self.foreground_epoch,
            self.content_epoch,
        )

    def lost(self, peer):
        self._retire_source_operations(peer)
        if self._bound_observation and self._bound_observation["peer"] is peer:
            self._bound_observation = None
        self.language_peer_lost(peer)
        practice = (
            self.practice_authorization
            or self.practice_pending
            or self.practice_continuity
            or self.practice_editor
            or self.practice_return
        )
        if practice and practice["peer"] is peer:
            self.retire_practice(practice["runId"])
        self.invalidate_pause()
        peer.support_observation = None
        self.clear_browser_capture()
        if self.published_plan and self.published_plan[0] is peer:
            self.invalidate_publication()
        self._peers.discard(peer)
        for plan_id, issued in list(self.issued.items()):
            if issued["peer"] is peer:
                self.finish_issued(plan_id, refused(True), quarantine=True)
        for collection in (self.observations, self.frozen):
            for key, value in list(collection.items()):
                if value["peer"] is peer:
                    collection.pop(key)
        if not self.closing:
            self.changed()

    @staticmethod
    def valid_opening_id(value):
        try:
            return (
                isinstance(value, str)
                and str(uuid.UUID(value)) == value
                and uuid.UUID(value).int != 0
            )
        except ValueError:
            return False

    def language_owner(self, configuration):
        # connection_id is minted by Peer, never accepted from native JSON.
        return {
            "connectionId": configuration["peer"].connection_id,
            "openingId": configuration["openingId"],
        }

    def cancel_language_configuration(self, *, notify_host=True, notify_peer=True):
        configuration, self._language_configuration = self._language_configuration, None
        if not configuration:
            return
        configuration["phase"] = "Retired"  # Before callbacks or sending cancellation.
        if notify_host:
            try:
                self.host.send("cancelLanguageConfiguration", self.language_owner(configuration))
            except (OSError, EOFError):
                pass  # This local opening is already retired; a dead Host link cannot save it.
        peer = configuration["peer"]
        if notify_peer and peer in self._peers and peer.link.channel.alive:
            try:
                peer.link.command(
                    "overlay-language-configuration-close",
                    {"openingId": configuration["openingId"]},
                )
            except (OSError, EOFError):
                pass  # Transport teardown cannot restore the retired configuration.

    def language_peer_lost(self, peer):
        if self._language_configuration and self._language_configuration["peer"] is peer:
            self.cancel_language_configuration(notify_peer=False)

    def language_configuration_closed(self, value):
        configuration = self._language_configuration
        if (
            configuration
            and isinstance(value, dict)
            and all(
                value.get(key) == expected
                for key, expected in self.language_owner(configuration).items()
            )
        ):
            self.cancel_language_configuration(notify_host=False)

    def language_configuration_current(self, configuration):
        peer = configuration["peer"]
        return (
            self._language_configuration is configuration
            and configuration["phase"] != "Retired"
            and peer in self._peers
            and peer.link.channel.alive
            and self.focused_peer() is peer
        )

    @property
    def language_configuration_open(self):
        return self._language_configuration is not None

    def start_language_configuration(self, source, opening_id, token, appearance, tasks):
        self.cancel_language_configuration()
        peer = self._source_peer(source, focused=True)
        if peer is None or not self.valid_opening_id(opening_id):
            return False
        configuration = {
            "peer": peer,
            "openingId": opening_id,
            "phase": "Pending",
            "expiresAtMs": now_ms() + 600,
            "appearance": copy.deepcopy(appearance),
            "tasks": tasks,
        }
        self._language_configuration = configuration
        self.set_overlay_focus(False)
        tasks(self.begin_language_configuration(configuration, token))
        return True

    async def begin_language_configuration(self, configuration, token):
        # The task may start after a synchronous cancel/peer loss. Do not send a
        # request whose opening retired before dispatch, even on the same pipe.
        if (
            not self.language_configuration_current(configuration)
            or configuration["phase"] != "Pending"
            or now_ms() > configuration["expiresAtMs"]
        ):
            if self._language_configuration is configuration:
                self.cancel_language_configuration()
            return
        try:
            reply = await self.host.ask(
                "beginLanguageConfiguration", {**self.language_owner(configuration), "token": token}
            )
            if not self.language_configuration_current(configuration):
                if self._language_configuration is configuration:
                    self.cancel_language_configuration()
                return
            if (
                now_ms() > configuration["expiresAtMs"]
                or not isinstance(reply, dict)
                or reply.get("opened") is not True
                or any(
                    reply.get(key) != value
                    for key, value in self.language_owner(configuration).items()
                )
                or type(reply.get("settingsRevision")) is not int
                or not 0 <= reply["settingsRevision"] <= 9223372036854775807
            ):
                self.cancel_language_configuration()
                return
            configuration["settingsRevision"] = reply["settingsRevision"]
            # No original target, token, candidate or permit is sent to the shell.
            presentation = {
                key: reply.get(key)
                for key in (
                    "openingId",
                    "settingsRevision",
                    "source",
                    "target",
                    "sourceLabel",
                    "targetLabel",
                    "swapAllowed",
                    "choices",
                )
            }
            presentation.update(configuration["appearance"])
            remaining = (configuration["expiresAtMs"] - now_ms()) / 1000
            if remaining <= 0:
                self.cancel_language_configuration()
                return
            shown = await configuration["peer"].link.ask(
                "overlay-language-configuration-open", presentation, timeout=remaining
            )
            if not self.language_configuration_current(configuration):
                if self._language_configuration is configuration:
                    self.cancel_language_configuration()
                return
            if (
                now_ms() > configuration["expiresAtMs"]
                or not isinstance(shown, dict)
                or shown.get("opened") is not True
                or shown.get("openingId") != configuration["openingId"]
            ):
                self.cancel_language_configuration()
                return
            configuration["phase"] = "Open"
        except (OSError, EOFError, TimeoutError):
            if self._language_configuration is configuration:
                self.cancel_language_configuration()

    def language_configuration_event(self, source, event):
        peer = self._source_peer(source)
        configuration = self._language_configuration
        value = event.get("value")
        if (
            not configuration
            or configuration["peer"] is not peer
            or not isinstance(value, dict)
            or value.get("openingId") != configuration["openingId"]
        ):
            return
        if event["operation"] == "languageConfigurationCancel":
            if set(value) == {"openingId"}:
                self.cancel_language_configuration(notify_peer=False)
            return
        if (
            set(value) != {"openingId", "source", "target"}
            or not self.language_configuration_current(configuration)
            or configuration["phase"] != "Open"
            or any(
                not isinstance(value.get(key), str) or not 0 < len(value[key]) <= 16
                for key in ("source", "target")
            )
        ):
            return
        configuration["phase"] = "Saving"
        configuration["tasks"](self.save_language_configuration(configuration, dict(value)))

    async def save_language_configuration(self, configuration, value):
        # The task may start after a synchronous cancel/peer loss. Do not send a
        # request whose opening retired before dispatch, even on the same pipe.
        if (
            not self.language_configuration_current(configuration)
            or configuration["phase"] != "Saving"
        ):
            if self._language_configuration is configuration:
                self.cancel_language_configuration()
            return
        try:
            reply = await self.host.ask(
                "saveLanguageConfiguration",
                {
                    **value,
                    **self.language_owner(configuration),
                    "expectedRevision": configuration["settingsRevision"],
                },
            )
            if isinstance(reply, dict) and reply.get("state") == "CommittedNotificationFailed":
                self.settings_commit_reported(reply)
            if self._language_configuration is not configuration:
                return  # A committed save may already have retired via settings notification.
            if not self.language_configuration_current(configuration):
                self.cancel_language_configuration()
                return
            if isinstance(reply, dict) and reply.get("saved") is True:
                self.cancel_language_configuration(notify_host=False)
            elif isinstance(reply, dict) and reply.get("reason") in (
                "unsupported-direction",
                "save-unconfirmed",
            ):
                configuration["phase"] = "Open"
                configuration["peer"].link.command(
                    "overlay-language-configuration-result",
                    {
                        "openingId": configuration["openingId"],
                        "saved": False,
                        "reason": reply["reason"],
                    },
                )
            else:
                self.cancel_language_configuration()
        except (OSError, EOFError, TimeoutError):
            # Unknown persistence is not success, and never restores source authority.
            if self._language_configuration is configuration:
                self.cancel_language_configuration()

    def renew_language_configuration(self):
        configuration = self._language_configuration
        if configuration and not self.language_configuration_current(configuration):
            self.cancel_language_configuration()

    def connection_facts(self):
        facts = []
        for peer in self._peers:
            observation = peer.support_observation or {}
            facts.append(
                SourceConnectionFacts(
                    peer.lifetime,
                    peer.link.channel.alive,
                    peer.backend,
                    peer.application,
                    peer.pid,
                    observation.get("atMs"),
                    observation.get("preview"),
                    observation.get("tabAccept"),
                    observation.get("directReplace"),
                )
            )
        return tuple(facts)

    def source_connected(self, source, *, focused=False):
        return self._source_peer(source, focused=focused) is not None

    def _source_peer(self, source, *, focused=False):
        for peer in self._peers:
            if peer.source is source and peer.link.channel.alive:
                return peer if not focused or self.focused_peer() is peer else None
        return None

    def current_presentation(self, view):
        paused = bool(view and view.get("state") == "Paused")
        record = self.current_pause_record() if paused else self._bound_observation
        token = view.get("viewContext", {}).get("token") if view else None
        if (
            not view
            or not record
            or record["target"] != view["source"]["target"]
            or record["peer"] not in self._peers
            or record["peer"] is not self.focused_peer()
            or not self.fresh()
            or not paused
            and (
                not token
                or token.get("commandGeneration") != self.shortcut_config.get("generation")
            )
        ):
            return None
        if "presentationId" not in record:
            record["presentationId"] = str(uuid.uuid4())
        identity = record["presentationId"]
        acknowledgement = self.current_session_acknowledgement(record)
        return SourcePresentation(
            record["peer"].source,
            identity,
            self.window_epoch,
            self.foreground_epoch,
            self.source_glossary_supported(record),
            bool(acknowledgement and acknowledgement["editorReady"]),
            record.get("editorRegenerateSupported") is True,
        )

    def _presentation_record(self, presentation):
        if presentation is None:
            return None
        for record in (self._bound_observation, self.current_pause_record()):
            if (
                record
                and record.get("presentationId") == presentation.identity
                and record["peer"].source is presentation.connection
                and self.source_connected(presentation.connection, focused=True)
                and (presentation.window_epoch, presentation.foreground_epoch)
                == (self.window_epoch, self.foreground_epoch)
            ):
                return record
        return None

    def presentation_current(self, presentation):
        return self._presentation_record(presentation) is not None

    async def present_source(self, presentation, payload):
        record = self._presentation_record(presentation)
        if record is None:
            return None
        return await record["peer"].link.ask(
            "overlay-render",
            {
                **payload,
                "target": record["target"]["value"],
                "authority": record["authority"],
                "generation": self.shortcut_config.get("generation"),
                "expiresAtMs": now_ms() + 800,
            },
        )

    def renew_source_presentation(self, presentation):
        record = self._presentation_record(presentation)
        if record is not None:
            record["peer"].link.command(
                "overlay-renew",
                {
                    "target": record["target"]["value"],
                    "authority": record["authority"],
                    "generation": self.shortcut_config.get("generation"),
                    "expiresAtMs": now_ms() + 800,
                },
            )

    def conceal_source(self, source):
        peer = self._source_peer(source)
        if peer is not None:
            peer.link.command("overlay-hide")

    def resume_source(self, source, token, revision):
        peer = self._source_peer(source)
        if peer is not None:
            peer.link.command(
                "overlay-resume",
                {
                    "target": token["target"]["value"],
                    "generation": token.get("commandGeneration"),
                    "revision": revision,
                },
            )

    def fresh(self, *, now=None):
        return (
            self.niri.ready
            and 0 <= (now_ms() if now is None else now) - self.niri.observed_at <= 150
        )

    def foreground_expired(self, *, now=None):
        return self.niri.ready and (now_ms() if now is None else now) - self.niri.observed_at > 150

    def focused_peer(self, *, now=None):
        if not self.fresh(now=now) or not self.niri.focused:
            return None
        pid = self.niri.focused.get("pid")
        peers = [
            peer
            for peer in self._peers
            if peer.lifetime and not peer.browser_retired and peer.matches_process(pid)
        ]
        # niri chooses the process, not a guessed window-to-widget mapping. The
        # authenticated native peer then proves which GTK parent actually owns
        # keyboard focus. Its opaque field and parent-local view identify the
        # operation even when this process has several compositor windows.
        return peers[0] if len(peers) == 1 else None

    async def practice_target(self, pid, target=None):
        """Metadata-only identity of the designated field in an already owned process.

        The caller proves managed process ownership. This grants no capture,
        shortcut, IME, acceptance or write capability.
        """
        peers = [
            peer
            for peer in self._peers
            if peer.pid == pid
            and peer.backend == "gtk3"
            and peer.lifetime
            and peer.link.channel.alive
        ]
        if len(peers) != 1:
            return None
        peer = peers[0]
        if target is None:
            if self.focused_peer() is not peer or self.overlay_focused:
                return None
        elif target.get("backend") != "linux-gtk3" or target.get("lifetime") != peer.lifetime:
            return None
        epoch = self.window_epoch
        try:
            description = await peer.link.ask(
                "describe", {"target": target["value"]} if target else {}
            )
            if not isinstance(description, dict):
                return None
            metadata, authority = description.get("metadata"), description.get("authority")
            if not isinstance(metadata, dict) or not isinstance(authority, dict):
                return None
            native = description.get("target")
            if (
                metadata.get("automationId") != "typelay-practice-source"
                or metadata.get("aria") != "GtkEntry"
                or metadata.get("password") is not False
                or metadata.get("readOnly") is not False
                or metadata.get("enabled") is not True
                or not isinstance(native, str)
                or str(uuid.UUID(native)) != native
                or authority.get("target") != native
                or peer not in self._peers
                or not peer.link.channel.alive
            ):
                return None
            result = target_id(peer, native)
            if target is not None:
                return result if result == target else None
            if (
                description.get("focused") is not True
                or epoch != self.window_epoch
                or self.focused_peer() is not peer
                or self.overlay_focused
            ):
                return None
            return result
        except (OSError, EOFError, ValueError, TypeError, TimeoutError):
            return None

    async def practice_binding(self, pid, target=None):
        """Return a host-owned connection and acknowledged, exact source binding."""
        if (self.practice_continuity is not None or self.practice_return is not None) and (
            self.practice_retirement is not None or not self.fresh()
        ):
            self.check_practice()
            return None
        native = await self.practice_target(pid, target)
        if native is None:
            return None
        peers = [
            peer for peer in self._peers if peer.pid == pid and peer.lifetime == native["lifetime"]
        ]
        if len(peers) != 1:
            return None
        peer = peers[0]
        record = self._bound_observation
        acknowledgement = self.current_session_acknowledgement(record)
        if (
            not record
            or record["peer"] is not peer
            or record["target"] != native
            or not acknowledgement
            or not acknowledgement["sourceReady"]
            or self.bound != native
            or self.focus() not in ("Source", "Overlay")
        ):
            return None
        if self.practice_continuity is not None:
            waiting = self.practice_continuity
            if (
                not self.practice_waiting(waiting["runId"])
                or record.get("commandGeneration") != self.shortcut_config.get("generation")
                or record.get("commandGeneration") == waiting["commandGeneration"]
                or record["authority"].get("focusRevision") != waiting["nativeFocusRevision"]
                or record["authority"].get("contextStamp", {}).get("context")
                != waiting["contextGeneration"]
                or not 0 <= now_ms() - record["authority"].get("observedAtMs", 0) <= 150
            ):
                return None
        returned = self.practice_return
        if returned is not None and (
            not self.practice_waiting(returned["runId"])
            or record.get("commandGeneration") != self.shortcut_config.get("generation")
            or record.get("commandGeneration") == returned["commandGeneration"]
            or record["authority"].get("focusRevision") != returned["returnedFocusRevision"]
            or record["authority"].get("contextStamp", {}).get("context")
            != returned["contextGeneration"]
            or not 0 <= now_ms() - record["authority"].get("observedAtMs", 0) <= 150
        ):
            return None
        return {
            "target": native,
            "connectionId": peer.connection_id,
            "commandGeneration": acknowledgement["generation"],
            "nativeFocusRevision": record["authority"].get("focusRevision", 0),
            "contextGeneration": record["authority"].get("contextStamp", {}).get("context", 0),
        }

    def prepare_practice(self, binding, owns_process):
        if (
            self.practice_retirement is not None
            or self.practice_pending is not None
            or self.practice_authorization is not None
            or self.practice_editor is not None
        ):
            return False
        peers = [peer for peer in self._peers if peer.connection_id == binding["connectionId"]]
        if len(peers) != 1:
            return False
        peer = peers[0]
        if (
            peer.backend != "gtk3"
            or not owns_process(peer.pid)
            or not peer.link.channel.alive
            or target_id(peer, binding["target"]["value"]) != binding["target"]
            or self.bound != binding["target"]
            or self.shortcut_config.get("generation") != binding["commandGeneration"]
            or self.focus() not in ("Source", "Overlay")
        ):
            return False
        continuity = self.practice_continuity
        if continuity is not None and (
            not self.practice_waiting(binding["runId"])
            or binding["commandGeneration"] == continuity["commandGeneration"]
            or any(
                binding.get(key) != continuity.get(key)
                for key in (
                    "runId",
                    "target",
                    "connectionId",
                    "nativeFocusRevision",
                    "contextGeneration",
                )
            )
            or self._bound_observation is None
            or self._bound_observation.get("commandGeneration") != binding["commandGeneration"]
        ):
            return False
        returned = self.practice_return
        if returned is not None and (
            not self.practice_waiting(binding["runId"])
            or binding["commandGeneration"] == returned["commandGeneration"]
            or binding != self.practice_return_binding(returned)
            or self._bound_observation is None
            or self._bound_observation.get("commandGeneration") != binding["commandGeneration"]
        ):
            return False
        self.practice_return = None
        self.practice_continuity = None
        # A pending binding is an ACK destination, never publication authority.
        # The source must remain continuously valid across Runtime's bind await.
        self.practice_retirement_ack = None
        self.practice_pending = {
            **binding,
            "peer": peer,
            "ownsProcess": owns_process,
            "windowEpoch": self.window_epoch,
            "settingsGeneration": self.settings_generation,
        }
        return True

    def record_practice_failure(self, stage, reason):
        # Fixed local call-site codes only. Keep the last failures across ordinary
        # captures so a later working preview cannot erase a lost lesson binding.
        pending = self.practice_pending
        self.practice_failures.append(
            {
                "atMs": now_ms(),
                "stage": stage,
                "reason": reason,
                "windowEpoch": self.window_epoch,
                "pendingWindowEpoch": pending["windowEpoch"] if pending else None,
                "pending": pending is not None,
                "authorized": self.practice_authorization is not None,
                "retiring": self.practice_retirement is not None,
            }
        )
        del self.practice_failures[:-16]

    def authorize_practice(self, binding, owns_process, expected_pending=None):
        pending = self.practice_pending
        # Preserve the original short-circuit gate order. The first failed gate
        # is evidence, not permission to refresh, retry or adopt a newer identity.
        if pending is None:
            reason = "pending-missing"
        elif expected_pending is not None and pending is not expected_pending:
            reason = "pending-replaced"
        elif self.practice_retirement is not None:
            reason = "retirement-pending"
        elif any(
            binding.get(key) != pending.get(key)
            for key in (
                "runId",
                "target",
                "connectionId",
                "commandGeneration",
                "nativeFocusRevision",
                "contextGeneration",
            )
        ):
            reason = "binding-changed"
        elif pending["settingsGeneration"] != self.settings_generation:
            reason = "settings-changed"
        elif pending["windowEpoch"] != self.window_epoch:
            reason = "window-changed"
        elif pending["peer"] not in self._peers:
            reason = "peer-lost"
        elif not pending["peer"].link.channel.alive:
            reason = "channel-closed"
        elif not owns_process(pending["peer"].pid):
            reason = "process-not-owned"
        elif self.bound != pending["target"]:
            reason = "target-changed"
        elif self.shortcut_config.get("generation") != pending["commandGeneration"]:
            reason = "command-changed"
        elif self.focus() not in ("Source", "Overlay"):
            reason = "focus-left"
        else:
            self.practice_pending = None
            self.practice_authorization = pending
            return True
        self.record_practice_failure("authorize", reason)
        return False

    def retire_practice(self, run, *, notify=True):
        returned = self.practice_return
        if returned is not None and returned["runId"] == run:
            self.practice_return = None
            if notify:
                self.practice_retired(run)
        continuity = self.practice_continuity
        if continuity is not None and continuity["runId"] == run:
            self.practice_continuity = None
            if notify:
                self.practice_retired(run)
        authorization = self.practice_authorization or self.practice_pending or self.practice_editor
        if authorization is None or authorization["runId"] != run:
            return (
                self.practice_retirement is not None and self.practice_retirement["runId"] == run
            ) or (self.practice_retirement_ack is not None and self.practice_retirement_ack == run)
        # This synchronous point closes new host publications. An already
        # published permit still belongs to native until its FIFO revoke ACK.
        self.practice_authorization = self.practice_pending = self.practice_editor = None
        self.practice_retirement = authorization
        self.practice_retirement_task = None
        self.practice_retirement_ack = None
        self.invalidate_publication()
        if notify:
            self.practice_retired(run)
        return True

    async def confirm_practice_retirement(self, run):
        retirement = self.practice_retirement
        if retirement is None:
            return self.practice_retirement_ack is not None and self.practice_retirement_ack == run
        if retirement["runId"] != run:
            return False
        if self.practice_retirement_task is None:
            import asyncio

            async def revoke():
                peer = retirement["peer"]
                if peer not in self._peers or not peer.link.channel.alive:
                    return False
                try:
                    # Fixed Peer/connection plus the ordered stream associates
                    # this response with clearing that connection's publication.
                    # New/late practice publications have already been rejected.
                    reply = await peer.link.ask("revoke", {"shortcuts": 1})
                except (OSError, EOFError, ValueError, TimeoutError):
                    return False
                return (
                    reply is None
                    and peer in self._peers
                    and peer.link.channel.alive
                    and peer.connection_id == retirement["connectionId"]
                )

            self.practice_retirement_task = asyncio.create_task(revoke())
        confirmed = await self.practice_retirement_task
        if confirmed and self.practice_retirement is retirement:
            self.practice_retirement_ack = run
            self.practice_retirement_binding = retirement
            self.practice_retirement = None
        return confirmed

    def practice_return_binding(self, value):
        return {
            **{key: value[key] for key in ("runId", "target", "connectionId", "contextGeneration")},
            "nativeFocusRevision": value["returnedFocusRevision"],
            "commandGeneration": self.shortcut_config.get("generation"),
        }

    def practice_waiting(self, run, *, acknowledged=True, require_fresh=True, now=None):
        returned = self.practice_return
        if returned is not None:
            return bool(
                returned["runId"] == run
                and (
                    not acknowledged
                    or (
                        self.practice_retirement is None
                        and self.practice_retirement_binding is returned
                    )
                )
                and returned["peer"] in self._peers
                and returned["peer"].link.channel.alive
                and returned["ownsProcess"](returned["peer"].pid)
                and returned["settingsGeneration"] == self.settings_generation
                and returned["windowEpoch"] == self.window_epoch
                and returned["foregroundEpoch"] == self.foreground_epoch
                and self.bound == returned["target"]
                and self.focused_peer(now=now) is returned["peer"]
                and not self.overlay_focused
                and self.fresh(now=now)
                and self.enabled is True
            )
        value = self.practice_continuity
        return bool(
            value
            and value["runId"] == run
            and (
                (
                    not acknowledged
                    and (
                        self.fresh(now=now)
                        or (not require_fresh and self.foreground_expired(now=now))
                    )
                )
                or (self.practice_retirement is None and self.practice_retirement_ack == run)
            )
            and value["peer"] in self._peers
            and value["peer"].link.channel.alive
            and value["ownsProcess"](value["peer"].pid)
            and value["settingsGeneration"] == self.settings_generation
            and value["windowEpoch"] == self.window_epoch
            and value["foregroundEpoch"] == self.foreground_epoch
            and self.bound == value["target"]
            and (
                (self.fresh(now=now) and self.focused_peer(now=now) is value["peer"])
                or (
                    not require_fresh
                    and self.foreground_expired(now=now)
                    and self.niri.focused
                    and [
                        peer
                        for peer in self._peers
                        if peer.lifetime and peer.matches_process(self.niri.focused.get("pid"))
                    ]
                    == [value["peer"]]
                )
            )
            and not self.overlay_focused
            and self.enabled is True
        )

    def suspend_practice_source(self, authorization, *, foreground=False):
        self.retire_practice(authorization["runId"], notify=False)
        waiting = {
            **authorization,
            "settingsGeneration": self.settings_generation,
            "windowEpoch": self.window_epoch,
            "foregroundEpoch": self.foreground_epoch,
            "foregroundSuspension": authorization if foreground else None,
        }
        self.practice_continuity = waiting
        for key, record in list(self.observations.items()):
            if (
                record["peer"] is authorization["peer"]
                and record["target"] == authorization["target"]
            ):
                self.observations.pop(key)
        self._bound_observation = None
        return waiting

    async def suspend_owned_source(self, binding):
        waiting = self.practice_continuity
        owner = waiting.get("foregroundSuspension") if waiting else None
        if (
            owner is None
            or any(
                binding.get(key) != owner.get(key)
                for key in (
                    "runId",
                    "target",
                    "connectionId",
                    "commandGeneration",
                    "nativeFocusRevision",
                    "contextGeneration",
                )
            )
            or self.shortcut_config.get("generation") != owner["commandGeneration"]
            or not self.practice_waiting(owner["runId"], acknowledged=False, require_fresh=False)
            or (
                self.practice_retirement is not owner
                and self.practice_retirement_binding is not owner
            )
        ):
            return False
        confirmed = await self.confirm_practice_retirement(owner["runId"])
        if (
            not confirmed
            or self.practice_continuity is not waiting
            or self.practice_retirement_binding is not owner
            or self.shortcut_config.get("generation") != owner["commandGeneration"]
            or not self.practice_waiting(owner["runId"], require_fresh=False)
        ):
            if self.practice_continuity is waiting:
                self.retire_practice(owner["runId"])
            return False
        return True

    async def context_advanced(
        self,
        authorization,
        proof,
        window_epoch,
        foreground_epoch,
        settings_generation,
        command_generation,
    ):
        def declined(reason):
            self.record_practice_failure("context-continuity", reason)
            return None

        if authorization is None:
            return declined("owner-missing")
        if not proof:
            return declined("proof-missing")
        if command_generation != self.shortcut_config.get("generation"):
            return declined("command-changed")
        if proof.get("target") != authorization["target"]["value"]:
            return declined("target-changed")
        for key, reason in (
            ("nativeFocusRevision", "native-focus-changed"),
            ("contextGeneration", "context-changed"),
        ):
            if (
                proof.get(key) != authorization.get(key)
                or type(proof.get(key)) is not int
                or proof[key] <= 0
            ):
                return declined(reason)
        if type(proof.get("observedAtMs")) is not int:
            return declined("observation-invalid")
        if not 0 < proof["observedAtMs"] <= now_ms():
            return declined("observation-time-invalid")
        if window_epoch != self.window_epoch:
            return declined("window-changed")
        if foreground_epoch != self.foreground_epoch:
            return declined("foreground-changed")
        if settings_generation != self.settings_generation:
            return declined("settings-changed")
        waiting = self.practice_continuity
        if waiting is not None and waiting.get("foregroundSuspension") is authorization:
            peer = authorization["peer"]
            if not (
                self.practice_retirement is authorization
                or (
                    self.practice_retirement is None
                    and self.practice_retirement_binding is authorization
                    and self.practice_retirement_ack == authorization["runId"]
                )
            ):
                return declined("fenced-retirement-changed")
            if peer.connection_id != authorization["connectionId"]:
                return declined("fenced-connection-changed")
            if command_generation != authorization["commandGeneration"]:
                return declined("fenced-command-changed")
            if self.practice_editor is not None:
                return declined("fenced-editor-active")
            if self.practice_return is not None:
                return declined("fenced-return-active")
            if not self.practice_waiting(
                authorization["runId"], acknowledged=False, require_fresh=False
            ):
                return declined("fenced-waiting-invalid")
            # This exact old request lost to its age fence. Its native proof
            # may have aged in transit; it supplies no new observation or ACK.
            return {
                "snapshot": None,
                "fencedPracticeRead": {
                    key: authorization[key]
                    for key in (
                        "runId",
                        "target",
                        "connectionId",
                        "commandGeneration",
                        "nativeFocusRevision",
                        "contextGeneration",
                    )
                },
            }
        # An already-ACKed waiting token can be replaced solely to fence an
        # in-flight read at a foreground age gap. Discard that exact old read,
        # never turn its continuity proof into a fresh observation or authority.
        if (
            waiting is not None
            and waiting is not authorization
            and authorization.get("foregroundExpired") is True
            and waiting["peer"] is authorization["peer"]
            and all(
                waiting.get(key) == authorization.get(key)
                for key in (
                    "runId",
                    "target",
                    "connectionId",
                    "commandGeneration",
                    "nativeFocusRevision",
                    "contextGeneration",
                )
            )
            and self.practice_waiting(authorization["runId"], require_fresh=False)
        ):
            expired = self.expired_practice_capture(authorization)
            if expired is not None:
                return expired
        active = authorization is self.practice_authorization
        if not active and authorization is not self.practice_continuity:
            return declined("owner-replaced")
        if not 0 <= now_ms() - proof["observedAtMs"] <= 150:
            return declined("observation-expired")
        if self.focused_peer() is not authorization["peer"]:
            return declined("focused-peer-unavailable")
        if self.overlay_focused:
            return declined("overlay-focused")
        if not self.fresh():
            return declined("foreground-expired")
        if not active:
            if not self.practice_waiting(authorization["runId"]):
                return declined("waiting-invalid")
            binding = {
                key: authorization[key]
                for key in (
                    "runId",
                    "target",
                    "connectionId",
                    "nativeFocusRevision",
                    "contextGeneration",
                )
            }
            binding["commandGeneration"] = command_generation
            return {
                "snapshot": None,
                "focus": "Source",
                "ownedSource": {
                    "binding": binding,
                    "observedAtMs": proof["observedAtMs"],
                    "contextAdvanced": True,
                },
            }
        waiting = self.suspend_practice_source(authorization)
        reason = None
        if not await self.confirm_practice_retirement(authorization["runId"]):
            reason = "retirement-unconfirmed"
        elif self.practice_continuity is not waiting:
            reason = "waiting-replaced"
        elif not self.practice_waiting(authorization["runId"]):
            reason = "retired-waiting-invalid"
        elif self.shortcut_config.get("generation") != authorization["commandGeneration"]:
            reason = "retired-command-changed"
        elif not 0 <= now_ms() - proof["observedAtMs"] <= 150:
            reason = "retired-observation-expired"
        if reason is not None:
            self.retire_practice(authorization["runId"])
            return declined(reason)
        return {
            "snapshot": None,
            "focus": "Source",
            "ownedSource": {
                "binding": {
                    key: authorization[key]
                    for key in (
                        "runId",
                        "target",
                        "connectionId",
                        "commandGeneration",
                        "nativeFocusRevision",
                        "contextGeneration",
                    )
                },
                "observedAtMs": proof["observedAtMs"],
                "contextAdvanced": True,
            },
        }

    def practice_editing(self, run, *, now=None):
        value = self.practice_editor
        return bool(
            value
            and value["runId"] == run
            and value["peer"] in self._peers
            and value["peer"].link.channel.alive
            and value["ownsProcess"](value["peer"].pid)
            and value["settingsGeneration"] == self.settings_generation
            and value["commandGeneration"] == self.shortcut_config.get("generation")
            and value["foregroundEpoch"] == self.foreground_epoch
            and self.bound == value["target"]
            and self.focused_peer(now=now) is value["peer"]
            and self.enabled is not False
            and (
                value["phase"] == "editor" or (now_ms() if now is None else now) < value["deadline"]
            )
        )

    def begin_practice_edit(self, authorization, token, deadline):
        self.check_practice()
        if (
            authorization is not self.practice_authorization
            or token.get("target") != authorization["target"]
            or token.get("commandGeneration") != authorization["commandGeneration"]
            or self.focus() != "Source"
            or now_ms() > deadline
        ):
            return None
        self.practice_authorization = None
        self.practice_editor = {
            **authorization,
            "token": token,
            "deadline": deadline,
            "phase": "revoking",
            "settingsGeneration": self.settings_generation,
            "foregroundEpoch": self.foreground_epoch,
        }
        self.invalidate_publication()
        for identity, record in list(self.observations.items()):
            if (
                record["peer"] is authorization["peer"]
                and record["target"] == authorization["target"]
            ):
                self.observations.pop(identity)
        return self.practice_editor

    async def revoke_practice_editor(self, value):
        # This operation ACK is deliberately not a whole-run retirement receipt.
        # A later close/settings change still has to revoke on the original peer.
        try:
            # GTK replies with null only after clearing both publication and
            # shortcut authority. Link correlates this nonzero request on this
            # fixed connection; an unexpected payload is not that ACK.
            reply = await value["peer"].link.ask("revoke", {"shortcuts": 1})
        except (OSError, EOFError, ValueError, TimeoutError):
            return False
        return (
            reply is None
            and self.practice_editor is value
            and self.practice_editing(value["runId"])
        )

    def complete_practice_edit(self, value, reply):
        if (
            self.practice_editor is not value
            or not self.practice_editing(value["runId"])
            or not isinstance(reply, dict)
            or reply.get("handoff") is not True
        ):
            return False
        value["phase"] = "editor"
        self.set_overlay_focus(True)
        return True

    def check_practice(self, *, now=None):
        returned = self.practice_return
        if returned is not None and not self.practice_waiting(
            returned["runId"], acknowledged=False, now=now
        ):
            self.retire_practice(returned["runId"])
        editor = self.practice_editor
        if editor is not None and not self.practice_editing(editor["runId"], now=now):
            self.retire_practice(editor["runId"])
        waiting = self.practice_continuity
        if waiting is not None:
            if not self.practice_waiting(
                waiting["runId"], acknowledged=False, require_fresh=False, now=now
            ):
                self.retire_practice(waiting["runId"])
            elif self.foreground_expired(now=now) and self.practice_retirement is None:
                if not waiting.get("foregroundExpired"):
                    # Replace the waiting token to fence reads begun before this
                    # gap. Keep only the immutable baseline and verified steps.
                    waiting["foregroundExpired"] = True
                    self.practice_continuity = dict(waiting)
                    for key, record in list(self.observations.items()):
                        if (
                            record["peer"] is waiting["peer"]
                            and record["target"] == waiting["target"]
                        ):
                            self.observations.pop(key)
                    self._bound_observation = None
            else:
                waiting["foregroundExpired"] = False
        authorization = self.practice_authorization or self.practice_pending
        if authorization is None:
            return
        peer = authorization["peer"]
        if (
            peer not in self._peers
            or not peer.link.channel.alive
            or not authorization["ownsProcess"](peer.pid)
            or self.enabled is False
            or self.shortcut_config.get("generation") != authorization["commandGeneration"]
            or (
                self.fresh(now=now)
                and (
                    self.bound != authorization["target"]
                    or self.focus(now=now) not in ("Source", "Overlay")
                )
            )
        ):
            self.retire_practice(authorization["runId"])
            return
        if (
            authorization is self.practice_authorization
            and self.foreground_expired(now=now)
            and not self.overlay_focused
            and self.bound == authorization["target"]
            and authorization["settingsGeneration"] == self.settings_generation
            and authorization.get("nativeFocusRevision", 0) > 0
            and authorization.get("contextGeneration", 0) > 0
            and self.niri.focused
            and [
                candidate
                for candidate in self._peers
                if candidate.lifetime and candidate.matches_process(self.niri.focused.get("pid"))
            ]
            == [peer]
        ):
            # Age is not fresh identity. Fence the old grant before reporting
            # only its pending retirement metadata to Runtime.
            self.suspend_practice_source(authorization, foreground=True)

    def practice_publication_allowed(self, lease, peer):
        editor = self.practice_editor or self.practice_return
        if editor and editor["peer"] is peer and editor["target"] == lease["source"]["target"]:
            return False
        run, connection = lease.get("practiceRunId"), lease.get("practiceConnectionId")
        if run is None and connection is None:
            return True
        self.check_practice()
        authorization = self.practice_authorization
        return bool(
            authorization
            and authorization["runId"] == run
            and authorization["connectionId"] == connection
            and authorization["peer"] is peer
            and authorization["target"] == lease["source"]["target"]
            and authorization["commandGeneration"] == lease.get("commandGeneration")
        )

    def focus(self, *, now=None):
        if self.bound and self.overlay_focused and self.fresh(now=now):
            return "Overlay"
        peer = self.focused_peer(now=now)
        if peer and self.bound and peer.lifetime == self.bound["lifetime"]:
            return "Source"
        return "Other"

    def state(self):
        # One synchronous publication must not mix opposite sides of the
        # compositor age limit. This timestamp is never cached or sent as authority.
        now = now_ms()
        self.check_practice(now=now)
        paused = self.current_pause_record(now=now)
        value = {
            "ready": self.fresh(now=now),
            "foregroundExpired": self.foreground_expired(now=now),
            "practiceForegroundSuspension": {
                key: self.practice_continuity[key]
                for key in (
                    "runId",
                    "target",
                    "connectionId",
                    "commandGeneration",
                    "nativeFocusRevision",
                    "contextGeneration",
                )
            }
            if self.practice_continuity and self.practice_continuity.get("foregroundSuspension")
            else None,
            "connectionId": self._bound_observation["peer"].connection_id
            if self._bound_observation
            and self._bound_observation["peer"] in self._peers
            and self._bound_observation["target"] == self.bound
            else self.practice_return["connectionId"]
            if self.practice_return
            and self.practice_waiting(self.practice_return["runId"], acknowledged=False, now=now)
            else self.practice_continuity["connectionId"]
            if self.practice_continuity
            and self.practice_waiting(
                self.practice_continuity["runId"], acknowledged=False, require_fresh=False, now=now
            )
            else "00000000-0000-0000-0000-000000000000",
            "sourceGlossaryTarget": self.bound
            if self.source_glossary_supported(self._bound_observation, now=now)
            else None,
            "sourceGlossaryGeneration": self.shortcut_config.get("generation"),
            "nativeReady": self.focused_peer(now=now) is not None,
            "sessionShortcuts": self.current_session_acknowledgement(self._bound_observation)
            if self._bound_observation
            and self._bound_observation["peer"] is self.focused_peer(now=now)
            and self._bound_observation["target"] == self.bound
            else None,
            "target": paused["target"] if paused else self.bound,
            "focus": "Source" if paused else self.focus(now=now),
            "userEpoch": self.user_epoch,
            "sourceUserEpoch": self.source_epoch,
            "foregroundEpoch": self.foreground_epoch,
            "windowEpoch": self.window_epoch,
            "contentEpoch": self.content_epoch,
        }
        if value != self.last_state:
            self.last_state = value
            self.host.send("state", value)

    def changed(self):
        self.invalidate_pause()
        self.clear_browser_capture()
        window = self.niri.focused or {}
        identity = (self.niri.ready, window.get("id"))
        if identity != self.last_focus:
            self.last_focus = identity
            self.foreground_epoch += 1
            self.user_epoch += 1
            self.revoke(shortcuts=True)
        self.window_epoch += 1
        self.state()

    def set_overlay_focus(self, active):
        self.clear_browser_capture()
        self.overlay_focused = active
        self.state()

    def expire(self):
        self.check_browser_registrations()
        stamp = now_ms()
        for collection, age in (
            (self.observations, 2000),
            (self.frozen, 120_000),
        ):
            for key, value in list(collection.items()):
                if stamp - value["time"] > age:
                    collection.pop(key)
        for plan_id, issued in list(self.issued.items()):
            if stamp > issued["permit"]["expiresAtMs"] + 2000:
                self.finish_issued(plan_id, refused(True), quarantine=True)
        self.state()
        return not self.closing

    def check_browser_registrations(self):
        # Reuse the existing maintenance cycle and explicit settings action.
        # Revocation retires grants, not the accounting for an already executed write.
        for peer in list(self._peers):
            if peer.backend != "browser" or not peer.lifetime:
                continue
            registration = (
                self.browser_registration(peer.browser_origin, peer.browser_home, peer.ancestors)
                if not peer.browser_suspended and not peer.browser_retired and peer.browser_alive()
                else None
            )
            if registration != peer.browser_identity:
                registration = None
            if registration == peer.browser_registration:
                continue
            peer.browser_registration = registration
            self.retire_browser_document(peer)
            self.configure_peer(peer)

    def suspend_browser(self, browser, suspended=True):
        for peer in list(self._peers):
            if peer.browser_identity and peer.browser_identity["browser"] == browser:
                peer.browser_suspended = suspended
                if suspended and peer.browser_had_v1:
                    # Explicit Remove retires this exact granted connection.
                    # Reconnecting explicitly creates a new peer; old issued
                    # receipts still settle through the original connection.
                    peer.browser_retired = True
                    self.retire_browser_document(peer)
        self.check_browser_registrations()

    def retire_browser_document(self, peer, route=None):
        self._retire_source_operations(peer, route)
        self.clear_browser_capture()
        if self.published_plan and self.published_plan[0] is peer:
            issued = self.issued.get(self.published_plan[1])
            if route is None or issued and issued.get("route") == route:
                self.invalidate_publication()
        record = self._bound_observation
        if record and record["peer"] is peer and (route is None or record.get("route") == route):
            self._bound_observation = None
            self.window_epoch += 1
        peer.link.command("revoke", {"shortcuts": 1})
        peer.shortcut = None
        self.state()

    def browser_source_local(self, peer, native):
        route = getattr(native, "route", None)
        return bool(
            peer.browser_registration
            and not peer.browser_retired
            and peer.browser_alive()
            and self.niri.focused
            and self.niri.focused.get("pid") == peer.browser_registration["pid"]
            and peer.valid_browser_route(route, native.get("target"))
            and route == peer.browser_active_route
        )

    def remember(self, peer, native, window, geometry, frozen=False):
        # Diagnostic capability evidence only; never used to authorize capture or writes.
        if not frozen:
            peer.support_observation = {
                "atMs": now_ms(),
                "preview": True,
                "tabAccept": native["writeSupported"] is True,
                "directReplace": native["writeSupported"] is True,
            }
        snapshot = {
            "target": target_id(peer, native["target"]),
            "text": native["text"],
            "selection": native["selection"],
            "bounds": geometry["bounds"],
            "multiline": native["metadata"]["multiline"],
            "access": "Supported",
            "ime": native["ime"],
            "observedAtMs": native["observedAtMs"],
            "inputEpoch": native["authority"]["inputRevision"],
            "focusEpoch": native["authority"]["focusRevision"],
            "writeSupported": native["writeSupported"],
            "directWriteSupported": native["writeSupported"],
        }
        record = {
            "peer": peer,
            "authority": native["authority"],
            "documentGeneration": native["metadata"].get("documentGeneration"),
            "window": window,
            "geometry": geometry,
            "time": now_ms(),
            "target": snapshot["target"],
            "route": getattr(native, "route", None),
        }
        # Do not retain captured source strings in this bookkeeping. Core owns them.
        collection = self.frozen if frozen else self.observations
        collection[observation_key(snapshot)] = record
        while len(collection) > (4 if frozen else 128):
            collection.popitem(last=False)
        return snapshot, record

    def begin_glossary_opening(self, presentation):
        record = self._presentation_record(presentation)
        if record is None:
            return None
        identity = str(uuid.uuid4())
        self._glossary_opening = {
            "identity": identity,
            "peer": record["peer"],
            "route": copy.deepcopy(record.get("route")),
            "authority": copy.deepcopy(record["authority"]),
            "window": self.window_epoch,
            "foreground": self.foreground_epoch,
        }
        return identity

    def glossary_opening_current(self, identity):
        opening, record = self._glossary_opening, self._bound_observation
        return bool(
            opening
            and opening["identity"] == identity
            and record
            and record["peer"] is opening["peer"]
            and record["authority"] == opening["authority"]
            and self.source_connected(opening["peer"].source, focused=True)
            and (opening["window"], opening["foreground"])
            == (self.window_epoch, self.foreground_epoch)
        )

    def finish_glossary_opening(self, identity):
        if self._glossary_opening and self._glossary_opening["identity"] == identity:
            self._glossary_opening = None

    def bind_source_glossary(self, presentation, entry, revision):
        record = self._presentation_record(presentation)
        if (
            record is None
            or entry.get("sourceOrigin") is True
            and not self.source_glossary_supported(record)
        ):
            return None
        handle = SourceGlossary(
            presentation.connection, entry["formId"], json.dumps(entry["token"]), revision
        )
        self._source_glossary = {
            "handle": handle,
            "entry": copy.deepcopy(entry),
            "peer": record["peer"],
            "revision": revision,
            "route": copy.deepcopy(record.get("route")),
            "sourceAuthority": copy.deepcopy(record["authority"]),
            "sourceDocumentGeneration": record.get("documentGeneration"),
            "window": self.window_epoch,
            "foreground": self.foreground_epoch,
            "state": self._presentation["state"],
            "focus": None,
            "transfer": None,
        }
        return handle

    def source_glossary_current(self, handle):
        form = self._source_glossary
        display = self._presentation
        record = self._bound_observation
        return bool(
            form
            and form["handle"] is handle
            and display
            and record
            and display["revision"] == handle.revision
            and display["token"] == form["entry"]["token"]
            and display["state"] == form["state"]
            and record["target"] == form["entry"]["token"]["target"]
            and record["peer"] is form["peer"]
            and self.source_connected(handle.connection, focused=True)
            and (
                form["entry"].get("sourceOrigin") is not True
                or (
                    self.source_glossary_supported(record)
                    and record.get("route") == form["route"]
                    and (
                        form["peer"].backend not in ("gtk3", "gtk4", "qt6")
                        or (form["window"], form["foreground"])
                        == (self.window_epoch, self.foreground_epoch)
                    )
                )
            )
        )

    def source_glossary_returning(self, handle):
        form = self._source_glossary
        return bool(
            form
            and form["handle"] is handle
            and (form.get("transfer") or {}).get("phase") == "Returning"
        )

    def observe_glossary_focus(self, handle, value):
        if not self.source_glossary_current(handle):
            return False
        form = self._source_glossary
        if value is not None and not (
            isinstance(value, dict)
            and set(value) == {"formId", "token", "ime"}
            and value["formId"] == handle.form_id
            and value["token"] == form["entry"]["token"]
            and value["ime"] in ("Clear", "Composing", "Unknown")
        ):
            return False
        form["focus"] = copy.deepcopy(value)
        if value is not None:
            self.set_overlay_focus(True)
        return True

    def _glossary_payload(self, handle, **payload):
        token = json.loads(handle.token_json)
        return {
            "target": token["target"]["value"],
            "generation": token["commandGeneration"],
            "revision": handle.revision,
            "formId": handle.form_id,
            **payload,
        }

    def decline_source_glossary(self, source, token, revision):
        peer = self._source_peer(source)
        if peer is not None:
            peer.link.command(
                "overlay-glossary-open",
                {
                    "target": token["target"]["value"],
                    "generation": token["commandGeneration"],
                    "revision": revision,
                    "entry": None,
                },
            )

    async def open_source_glossary(self, handle):
        if not self.source_glossary_current(handle):
            return False
        form = self._source_glossary
        transfer = None
        if form["entry"].get("sourceOrigin") is True:
            transfer = form["transfer"] = {
                "phase": "Opening",
                "consumed": False,
                "window": self.window_epoch,
                "foreground": self.foreground_epoch,
            }
        token = form["entry"]["token"]
        try:
            response = await form["peer"].link.ask(
                "overlay-glossary-open",
                {
                    "target": token["target"]["value"],
                    "generation": token["commandGeneration"],
                    "revision": handle.revision,
                    "entry": form["entry"],
                },
            )
        finally:
            form["transfer"] = None
        return bool(
            self.source_glossary_current(handle)
            and (
                transfer is None
                or (
                    transfer["consumed"]
                    and getattr(response, "route", None) == form["route"]
                    and (transfer["window"], transfer["foreground"])
                    == (self.window_epoch, self.foreground_epoch)
                )
            )
            and isinstance(response, dict)
            and response.get("opened") is True
        )

    def close_source_glossary(self, handle, *, notify_peer=True):
        form = self._source_glossary
        if form and form["handle"] is handle:
            self._source_glossary = None
            if (
                self._source_glossary_return
                and self._source_glossary_return["formId"] == handle.form_id
            ):
                self._source_glossary_return = None
        peer = self._source_peer(handle.connection)
        if notify_peer and peer is not None:
            peer.link.command("overlay-glossary-close", self._glossary_payload(handle))

    def complete_source_glossary(self, handle, result):
        # A retired form still receives its original persistence result. This
        # never attaches it to another form or restores source authority.
        peer = self._source_peer(handle.connection)
        if peer is not None:
            peer.link.command(
                "overlay-glossary-result", self._glossary_payload(handle, result=result)
            )

    def consume_glossary_focus_transfer(self, peer, value, route):
        form = self._source_glossary
        transfer = form.get("transfer") if form else None
        marker = value.get("glossaryTransfer")
        if (
            not transfer
            or transfer["consumed"]
            or not self.source_glossary_current(form["handle"])
            or form["entry"].get("sourceOrigin") is not True
            or peer is not form["peer"]
            or route != form["route"]
            or form["entry"]["token"]["target"] != self.bound
            or value.get("target") != form["entry"]["token"]["target"]["value"]
            or value.get("textChanged") is not False
            or value.get("focusChanged") is not True
            or (transfer["window"], transfer["foreground"])
            != (self.window_epoch, self.foreground_epoch)
        ):
            return False
        expected = {
            "formId": form["entry"]["formId"],
            "token": form["entry"]["token"],
            "phase": transfer["phase"],
        }
        if peer.backend in ("gtk3", "gtk4", "qt6"):
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

    async def return_glossary_to_source(self, handle, request):
        form = self._source_glossary
        if (
            not self.source_glossary_current(handle)
            or form["entry"].get("sourceOrigin") is not True
            or request != {"formId": handle.form_id, "token": form["entry"]["token"]}
            or not form["focus"]
            or form["focus"]["ime"] != "Clear"
            or self.focus() != "Overlay"
        ):
            return None
        window, foreground = self.window_epoch, self.foreground_epoch
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
            or not self.source_glossary_current(handle)
            or not isinstance(response, dict)
            or set(response) != {"formId", "token", "returned"}
            or response.get("formId") != request["formId"]
            or response.get("token") != request["token"]
            or response.get("returned") is not True
            or (window, foreground) != (self.window_epoch, self.foreground_epoch)
            or getattr(response, "route", None) != form["route"]
        ):
            return None
        # This proves only the native/browser focus transfer. The controller obtains
        # its own fresh source capture before any persistence or new permission.
        form["focus"] = None
        self._source_glossary_return = {
            **copy.deepcopy(request),
            "peer": form["peer"],
            "route": copy.deepcopy(form["route"]),
            "windowEpoch": window,
            "foregroundEpoch": foreground,
            "sourceAuthority": copy.deepcopy(form["sourceAuthority"]),
            "sourceDocumentGeneration": form["sourceDocumentGeneration"],
        }
        self.set_overlay_focus(False)
        return dict(response)

    def source_glossary_supported(self, record, *, now=None):
        # Display capability only. Native and browser origins keep separate
        # identity checks; neither can borrow the other's route or readiness.
        current = bool(
            record
            and record is self._bound_observation
            and record.get("sourceGlossarySupported") is True
            and record["target"] == self.bound
            and record.get("commandGeneration") == self.shortcut_config.get("generation")
            and record["peer"] in self._peers
            and record["peer"] is self.focused_peer(now=now)
            and self.fresh(now=now)
        )
        if not current:
            return False
        peer = record["peer"]
        if peer.backend in ("gtk3", "gtk4", "qt6"):
            return bool(
                record.get(
                    "gtkSourceGlossary" if peer.backend in ("gtk3", "gtk4") else "qt6SourceGlossary"
                )
                is True
                and record.get("route") is None
                and record["target"]["lifetime"] == peer.lifetime
                and record["window"]["id"] == self.niri.focused["id"]
            )
        return bool(
            peer.backend == "browser"
            and self.browser_source_local(
                record["peer"],
                RoutedReply({"target": record["target"]["value"]}, record.get("route")),
            )
        )

    @staticmethod
    def gtk_source_glossary(peer, native):
        metadata, authority = native.get("metadata", {}), native.get("authority", {})
        context = authority.get("contextStamp", {}).get("context")
        field = (
            metadata.get("aria") == "GtkEntry"
            and metadata.get("multiline") is False
            or metadata.get("aria") == "GtkTextView"
            and metadata.get("multiline") is True
        )
        return bool(
            peer.backend in ("gtk3", "gtk4")
            and getattr(native, "route", None) is None
            and native.get("sourceGlossarySupported") is True
            and field
            and metadata.get("password") is False
            and metadata.get("readOnly") is False
            and metadata.get("enabled") is True
            and authority.get("target") == native.get("target")
            and type(context) is int
            and context > 0
        )

    @staticmethod
    def qt6_source_glossary(peer, native):
        metadata, authority = native.get("metadata", {}), native.get("authority", {})
        stamp = authority.get("contextStamp", {})
        # Qt's field/provider generations are not GTK context identities.
        # Multiline sources also require the native adapter's tracked document
        # identity. QTextEdit and subclasses remain outside this capability.
        document = metadata.get("documentGeneration")
        field = type(document) is int and (
            metadata.get("aria") == "QLineEdit"
            and metadata.get("multiline") is False
            and document == 0
            or metadata.get("aria") == "QPlainTextEdit"
            and metadata.get("multiline") is True
            and document > 0
        )
        return bool(
            peer.backend == "qt6"
            and getattr(native, "route", None) is None
            and native.get("sourceGlossarySupported") is True
            and field
            and metadata.get("password") is False
            and metadata.get("readOnly") is False
            and metadata.get("enabled") is True
            and authority.get("target") == native.get("target")
            and isinstance(stamp, dict)
            and all(type(stamp.get(key)) is int and stamp[key] > 0 for key in ("context", "field"))
            and type(stamp.get("input")) is int
            and stamp["input"] >= 0
        )

    def _source_glossary_return_current(self, receipt, peer, native):
        # This exact, live form receipt narrows its return capture only. Ordinary
        # Linux preview captures retain their existing read-only ACK semantics.
        acknowledgement = self.session_acknowledgement(peer, native)
        current = bool(
            receipt is not None
            and self._source_glossary_return is receipt
            and receipt["peer"] is peer
            and receipt["windowEpoch"] == self.window_epoch
            and receipt["foregroundEpoch"] == self.foreground_epoch
            and receipt["token"]["commandGeneration"] == self.shortcut_config.get("generation")
            and receipt["token"]["target"] == self.bound
            and native.get("target") == self.bound["value"]
            and native.get("glossarySource")
            == {"formId": receipt["formId"], "token": receipt["token"]}
            and getattr(native, "route", None) == receipt["route"]
            and native.get("focused") is True
            and native.get("sourceGlossarySupported") is True
            and not self.overlay_focused
            and self.focused_peer() is peer
            and self.fresh()
            and acknowledgement
            and not acknowledgement["sourceReady"]
            and not acknowledgement["editorReady"]
        )
        if not current:
            return False
        if peer.backend in ("gtk3", "gtk4"):
            basis, authority = receipt.get("sourceAuthority", {}), native.get("authority", {})
            return bool(
                self.gtk_source_glossary(peer, native)
                and receipt["token"]["target"]["lifetime"] == peer.lifetime
                and all(
                    authority.get(key) == basis.get(key)
                    for key in ("target", "metadataKey", "inputRevision", "contentRevision")
                )
                and authority.get("contextStamp", {}).get("context")
                == basis.get("contextStamp", {}).get("context")
            )
        if peer.backend == "qt6":
            basis, authority = receipt.get("sourceAuthority", {}), native.get("authority", {})
            return bool(
                self.qt6_source_glossary(peer, native)
                and native["metadata"]["documentGeneration"]
                == receipt.get("sourceDocumentGeneration")
                and receipt["token"]["target"]["lifetime"] == peer.lifetime
                and all(
                    authority.get(key) == basis.get(key)
                    for key in ("target", "metadataKey", "inputRevision", "contentRevision")
                )
                and all(
                    authority.get("contextStamp", {}).get(key)
                    == basis.get("contextStamp", {}).get(key)
                    for key in ("context", "field", "input")
                )
            )
        return peer.backend == "browser" and self.browser_source_local(peer, native)

    def session_acknowledgement(self, peer, native):
        acknowledgement = native.get("sessionBindings")
        authority = native.get("authority", {})
        config = self.shortcut_config
        if (
            config.get("sessionEnabled") is not True
            or peer.browser_retired
            or not isinstance(acknowledgement, dict)
            or not config.get("generation")
            or acknowledgement.get("generation") != config["generation"]
            or acknowledgement.get("accept") != config.get("accept")
            or acknowledgement.get("exit") != config.get("exit")
            or type(acknowledgement.get("sourceReady")) is not bool
            or type(acknowledgement.get("editorReady")) is not bool
            or peer.backend == "browser"
            and (
                type(acknowledgement.get("presentationVersion")) is not int
                or acknowledgement["presentationVersion"] != 1
            )
            or native.get("target") != authority.get("target")
            or ("sessionBindings" in authority and authority["sessionBindings"] != acknowledgement)
        ):
            return None
        return {
            "target": target_id(peer, native["target"]),
            "generation": config["generation"],
            "bindings": {"accept": config["accept"], "exit": config["exit"]},
            "sourceReady": acknowledgement["sourceReady"],
            "editorReady": acknowledgement["editorReady"],
            "sourceLocalAcceptanceVersion": 1
            if peer.backend == "browser"
            and self.browser_source_local(peer, native)
            and acknowledgement.get("shortcutDispatch") == "page"
            and native.get("metadata", {}).get("inDocument") is True
            and native.get("metadata", {}).get("hasValue") is True
            and acknowledgement.get("target") == native["target"]
            and type(acknowledgement.get("sourceLocalAcceptanceVersion")) is int
            and acknowledgement["sourceLocalAcceptanceVersion"] == 1
            and acknowledgement["sourceReady"]
            and acknowledgement["editorReady"]
            else 0,
        }

    def current_session_acknowledgement(self, record):
        acknowledgement = record.get("sessionShortcuts") if record else None
        config = self.shortcut_config
        if (
            config.get("sessionEnabled") is not True
            or record
            and record["peer"].browser_retired
            or not acknowledgement
            or acknowledgement["target"] != record["target"]
            or acknowledgement["generation"] != config.get("generation")
            or acknowledgement["bindings"]
            != {"accept": config.get("accept"), "exit": config.get("exit")}
            or acknowledgement.get("sourceLocalAcceptanceVersion") == 1
            and not self.browser_source_local(
                record["peer"],
                RoutedReply({"target": record["target"]["value"]}, record.get("route")),
            )
        ):
            return None
        return acknowledgement

    async def approve(self, peer, description, *, owned_approval_timing=None):
        if not description:
            return False
        metadata = {**description["metadata"], "process": peer.application}
        if owned_approval_timing is not None:
            return (
                await self.host.ask(
                    "allowField", metadata, owned_approval_timing=owned_approval_timing
                )
                is True
            )
        return await self.host.ask("allowField", metadata) is True

    def timing(self, name, value):
        # Session-local aggregates only. No target IDs, text or per-frame logs.
        value = max(-10000, min(10000, int(value)))
        metric = self.timing_metrics.setdefault(
            name, {"count": 0, "minMs": value, "maxMs": value, "lastMs": value}
        )
        metric["count"] = min(metric["count"] + 1, 2**31 - 1)
        metric["minMs"] = min(metric["minMs"], value)
        metric["maxMs"] = max(metric["maxMs"], value)
        metric["lastMs"] = value

    def captured(self, reason, result=None, *, native_failure=None):
        # Fixed reason codes and counts only: no source, title or field metadata.
        self.capture_status = reason
        self.capture_counts[reason] = min(self.capture_counts[reason] + 1, 2**31 - 1)
        if result is not None:
            stamp = now_ms()
            self.timing("captureElapsed", stamp - self.capture_started_at)
            self.timing("snapshotAge", stamp - result["snapshot"]["observedAtMs"])
        if result is None:
            self.clear_browser_capture()
            self.invalidate_publication()
            stamp = now_ms()
            self.capture_failures.append(
                {
                    "reason": reason,
                    "atMs": stamp,
                    "elapsedMs": max(0, stamp - self.capture_started_at),
                    "compositorAgeMs": stamp - self.niri.observed_at,
                }
            )
            if native_failure:
                self.capture_failures[-1]["nativeFailure"] = native_failure
            if self.native_capture_timing is not None:
                # Caller await boundaries, not native execution times. No text or identities.
                self.capture_failures[-1]["nativeCallerTiming"] = dict(self.native_capture_timing)
            if (
                self.browser_rounds
                and self.browser_rounds[-1]["startedMs"] == self.capture_started_at
            ):
                # Preserve this failed round before later captures evict it from the ring.
                # Round telemetry is bounded numbers/booleans, never source text or IDs.
                self.capture_failures[-1]["browserRound"] = dict(self.browser_rounds[-1])
            del self.capture_failures[:-16]
            if os.environ.get("TYPELAY_TRACE"):
                print(
                    "typelay-capture "
                    + json.dumps(self.capture_failures[-1], separators=(",", ":")),
                    file=sys.stderr,
                    flush=True,
                )
        return result

    def remember_browser_diagnostics(self, value):
        # Only bounded numeric telemetry and fixed codes may leave the peer.
        if not isinstance(value, dict):
            return
        reasons = (
            "supported",
            "metadata",
            "ime",
            "clockUnavailable",
            "clockExpired",
            "clockDiscontinuity",
            "authorityMissing",
            "authorityChanged",
            "authorityExpired",
            "authorityClock",
            "hostAge",
            "timerPrecision",
            "intervalConflict",
            "hostFuture",
            "invalidClock",
            "richUnavailable",
            "invalidText",
            "postcheck",
        )
        result = {}
        counts = value.get("captureCounts")
        if isinstance(counts, dict):
            result["captureCounts"] = {
                key: count
                for key in reasons
                if type(count := counts.get(key)) is int and 0 <= count <= 1000000
            }
        for key in ("lastCaptureReason", "lastClockReason"):
            if value.get(key) in reasons:
                result[key] = value[key]
        for key in (
            "authorityAgeMs",
            "hostAgeMs",
            "timerTickMs",
            "timerProbeMs",
            "clockWidthMs",
            "clockAgeMs",
        ):
            number = value.get(key)
            if type(number) in (int, float) and math.isfinite(number) and -10000 <= number <= 10000:
                result[key] = number
        self.browser_diagnostics = result

    def invalidate_publication(self):
        self.publication_generation += 1
        self.session_publication = None
        self.native_publication = self.published_plan = self.publication_epochs = None

    def remember_publication(self, peer, description, generation):
        # A rendered candidate is not proof that its one-shot permit arrived.
        # Only recognize this peer's report of a permit issued by this host.
        if generation != self.publication_generation or self.publication_epochs != (
            self.window_epoch,
            self.user_epoch,
        ):
            return
        value = description.get("publication")
        if not isinstance(value, dict):
            return
        current = self.published_plan
        if not current or current[0] is not peer:
            return
        issued = self.issued.get(current[1])
        if (
            not issued
            or issued["peer"] is not peer
            or issued["lease"]["source"]["target"] != target_id(peer, description["target"])
            or issued.get("route") is not None
            and issued["route"] != getattr(description, "route", None)
        ):
            return
        if value.get("ready") is not True:
            self.native_publication = None
            return
        plan_id = value.get("planId")
        if not isinstance(plan_id, str) or current != (peer, plan_id):
            return
        self.native_publication = None
        until = value.get("readyUntilHostMs")
        if (
            type(until) not in (int, float)
            or not math.isfinite(until)
            or not now_ms() < until <= issued["permit"]["expiresAtMs"]
        ):
            return
        round_timing = issued.get("roundTiming")
        if round_timing is not None and "previousPermitRemainingMs" in value:
            remaining = value["previousPermitRemainingMs"]
            if remaining is None or (
                type(remaining) in (int, float)
                and math.isfinite(remaining)
                and -10000 <= remaining <= 10000
            ):
                round_timing.setdefault("firstAckAtMs", now_ms())
                round_timing.setdefault("readyUntilMs", until)
                round_timing.setdefault("previousPermitRemainingMs", remaining)
        self.timing("ackRemaining", until - now_ms())
        self.native_publication = {
            "peer": peer,
            "planId": plan_id,
            "until": until,
            "windowEpoch": self.window_epoch,
            "userEpoch": self.user_epoch,
        }

    def acceptance_timing(self):
        """Original permission and public hint time remaining, from one observation."""
        peer = self.focused_peer()
        if not peer:
            return 0, 0
        value = self.native_publication
        if (
            not value
            or value["peer"] is not peer
            or self.overlay_focused
            or value["windowEpoch"] != self.window_epoch
            or value["userEpoch"] != self.user_epoch
            or value["planId"] not in self.issued
        ):
            return 0, 0
        remaining = max(0, int(value["until"] - now_ms()))
        hint_remaining = (
            max(0, remaining - BROWSER_ACCEPTANCE_PRESENTATION_HANDOFF_MS)
            if peer.backend == "browser"
            else remaining
        )
        return remaining, hint_remaining

    def diagnostics(self):
        acknowledgement = (
            self.current_session_acknowledgement(self._bound_observation)
            if self._bound_observation
            and self._bound_observation["peer"] is self.focused_peer()
            and self._bound_observation["target"] == self.bound
            else None
        )
        return {
            "sessionShortcuts": {
                "bindings": dict(acknowledgement["bindings"]),
                "sourceReady": acknowledgement["sourceReady"],
                "editorReady": acknowledgement["editorReady"],
                "sourceLocalAcceptanceVersion": acknowledgement.get(
                    "sourceLocalAcceptanceVersion", 0
                ),
            }
            if acknowledgement
            else None,
            "captureStatus": self.capture_status,
            "languageConfiguration": {
                key: self._language_configuration[key]
                for key in ("openingId", "phase", "expiresAtMs")
            }
            if self._language_configuration
            else None,
            "captureCounts": dict(self.capture_counts),
            "captureFailures": list(self.capture_failures),
            "practiceFailures": [dict(value) for value in self.practice_failures],
            "timingMs": {key: dict(value) for key, value in self.timing_metrics.items()},
            "nativePublications": [dict(value) for value in self.publish_diagnostics],
            "browser": self.browser_diagnostics,
            "browserCaptureCounts": dict(self.browser_capture_counts),
            "browserRounds": [dict(value) for value in self.browser_rounds],
            "tabCaptureRounds": [dict(value) for value in self.tab_capture_rounds],
            "compositorReady": self.niri.ready,
            "compositorAgeMs": max(0, now_ms() - self.niri.observed_at),
            "focus": self.focus(),
            "windowEpoch": self.window_epoch,
            "foregroundEpoch": self.foreground_epoch,
        }

    def invalidate_pause(self):
        self.pause_generation += 1
        self.pause_observation = None

    def current_pause_record(self, *, now=None):
        record = self.pause_observation
        if (
            self.enabled is not False
            or not record
            or record["generation"] != self.pause_generation
            or record["windowEpoch"] != self.window_epoch
            or record["foregroundEpoch"] != self.foreground_epoch
            or record["userEpoch"] != self.user_epoch
            or not 0 <= (now_ms() if now is None else now) - record["anchor"]["observedAtMs"] <= 150
            or record["peer"] not in self._peers
            or self.focused_peer(now=now) is not record["peer"]
            or self.overlay_focused
        ):
            return None
        return record

    async def observe_pause(self, context):
        # This separate route never captures a body, selection, IME text, or
        # write capability. A prior translation observation cannot authorize it.
        peer, window = self.focused_peer(), self.niri.focused
        if (
            self.enabled is not False
            or context.get("boundTarget") is not None
            or context.get("captureDirect", False) is not False
            or peer not in self._peers
            or not window
            or self.overlay_focused
        ):
            self.invalidate_pause()
            self.state()
            return None
        generation = self.pause_generation
        epochs = self.window_epoch, self.foreground_epoch, self.user_epoch
        try:
            description = await peer.link.ask("describe", {})
            if not isinstance(description, dict) or description.get("focused") is not True:
                raise ValueError("pause-focus")
            metadata, authority = description.get("metadata"), description.get("authority")
            if not isinstance(metadata, dict) or not isinstance(authority, dict):
                raise ValueError("pause-metadata")
            # Missing metadata is unknown, never an empty harmless label or a
            # guessed editable field. The shared policy owns exclusion rules.
            if (
                any(
                    type(metadata.get(key)) is not str
                    for key in ("process", "label", "automationId", "help", "aria")
                )
                or any(
                    type(metadata.get(key)) is not bool
                    for key in (
                        "password",
                        "enabled",
                        "readOnly",
                        "offscreen",
                        "inDocument",
                        "multiline",
                        "hasValue",
                        "hasTextEdit",
                    )
                )
                or not (metadata["hasValue"] or metadata["hasTextEdit"])
            ):
                raise ValueError("pause-metadata")
            target, stamp = description.get("target"), authority.get("observedAtMs")
            if (
                not isinstance(target, str)
                or not target
                or authority.get("target") != target
                or type(stamp) is not int
                or not 0 <= now_ms() - stamp <= 150
                or type(authority.get("focusRevision")) is not int
                or type(authority.get("inputRevision")) is not int
                or not isinstance(authority.get("metadataKey"), str)
            ):
                raise ValueError("pause-authority")
            geometry = anchor_geometry(description.get("geometry"), peer.anchor_space)
            if (
                geometry is None
                or await self.host.ask("allowPauseField", {**metadata, "process": peer.application})
                is not True
            ):
                raise ValueError("pause-policy")
            if (
                self.enabled is not False
                or generation != self.pause_generation
                or epochs != (self.window_epoch, self.foreground_epoch, self.user_epoch)
                or peer not in self._peers
                or self.focused_peer() is not peer
                or self.overlay_focused
                or not 0 <= now_ms() - stamp <= 150
            ):
                raise ValueError("pause-changed")
            anchor = {
                "target": target_id(peer, target),
                "bounds": geometry["bounds"],
                "access": "Supported",
                "observedAtMs": stamp,
                "focusEpoch": self.foreground_epoch,
            }
            self.pause_observation = {
                "peer": peer,
                "authority": authority,
                "target": anchor["target"],
                "anchor": anchor,
                "generation": generation,
                "windowEpoch": self.window_epoch,
                "foregroundEpoch": self.foreground_epoch,
                "userEpoch": self.user_epoch,
            }
            self.state()
            return {
                "anchor": anchor,
                "focus": "Source",
                "application": peer.application,
                "foregroundEpoch": self.foreground_epoch,
            }
        except (OSError, EOFError, TimeoutError, ValueError, KeyError, TypeError):
            if generation == self.pause_generation:
                self.invalidate_pause()
            self.state()
            return None

    def expired_practice_capture(self, practice, native=None):
        self.check_practice()
        waiting = self.practice_continuity
        if (
            practice is not None
            and waiting is not None
            and practice["runId"] == waiting["runId"]
            and self.practice_waiting(waiting["runId"], require_fresh=False)
            and (
                native is None
                or (
                    native.get("focused") is True
                    and native.get("target") == waiting["target"]["value"]
                    and native.get("authority", {}).get("focusRevision")
                    == waiting["nativeFocusRevision"]
                    and native.get("authority", {}).get("contextStamp", {}).get("context")
                    == waiting["contextGeneration"]
                )
            )
            and (
                self.foreground_expired()
                or (practice is not waiting and practice.get("foregroundExpired"))
            )
        ):
            return {"snapshot": None, "foregroundExpired": True}
        return None

    async def capture(self, context):
        self.check_practice()
        self.capture_started_at = now_ms()
        self.native_capture_timing = None
        settings_generation = self.settings_generation
        command_generation = self.shortcut_config.get("generation")
        glossary_return = self._source_glossary_return
        editor_return = self._editor_regenerate if context.get("editorReturn") is not None else None
        if context.get("editorReturn") is not None and (
            not self.browser_regenerate_current(editor_return)
            or context["editorReturn"] != editor_return["operation"]
            or not editor_return["returned"]
            or self.overlay_focused
        ):
            return self.captured("editor-return-identity-lost")
        editor = self.practice_editor
        returning = self.practice_return
        practice = self.practice_authorization or self.practice_continuity or returning
        expected_practice = self.practice_return_binding(returning) if returning else practice
        eligible = context.get("practiceRead")
        if (
            not isinstance(eligible, dict)
            or practice is None
            or eligible.get("commandGeneration") != command_generation
            or any(
                eligible.get(key) != expected_practice.get(key)
                for key in (
                    "runId",
                    "target",
                    "connectionId",
                    "nativeFocusRevision",
                    "contextGeneration",
                )
            )
        ):
            practice = None
        if self.enabled is False:
            return self.captured("paused")
        window_epoch = self.window_epoch
        foreground_epoch = self.foreground_epoch
        focus = "Overlay" if self.overlay_focused else "Source"
        if (
            focus == "Overlay"
            and self._bound_observation
            and context.get("boundTarget") == self.bound
        ):
            record = self._bound_observation
            peer, window = record["peer"], self.niri.by_id(record["window"]["id"])
            target = self.bound["value"]
        else:
            peer, window, target = self.focused_peer(), self.niri.focused, None
        if editor_return is not None:
            peer = editor_return["peer"]
            window = self.niri.by_id(editor_return["record"]["window"]["id"])
            target = editor_return["action"]["token"]["target"]["value"]
        if not self.fresh():
            self.captured("compositor-stale")
            return self.expired_practice_capture(practice)
        if window is None:
            return self.captured("no-window")
        if peer not in self._peers or peer.browser_retired:
            return self.captured("unsupported-application")
        publication_generation = self.publication_generation
        cached, self.browser_capture = self.browser_capture, None
        capture_generation = self.browser_capture_generation
        identity = self.browser_capture_identity(peer)
        browser_source = peer.backend == "browser" and focus == "Source"
        reuse = bool(
            browser_source
            and editor_return is None
            and cached
            and cached["identity"] == identity
            and self.bound == target_id(peer, cached["description"]["target"])
            and 0 <= now_ms() - cached["description"]["authority"]["observedAtMs"] < 150
        )
        counter = "cachedDescribeUsed" if reuse else "describeRpcCount"
        round_timing = None
        if browser_source:
            self.browser_capture_round = min(self.browser_capture_round + 1, 2**31 - 1)
            round_timing = {
                "round": self.browser_capture_round,
                "cached": reuse,
                "startedMs": self.capture_started_at,
            }
            self.browser_rounds.append(round_timing)
            del self.browser_rounds[:-16]
            self.browser_round_owners[id(round_timing)] = peer
            retained = {id(item) for item in self.browser_rounds}
            self.browser_round_owners = {
                key: owner for key, owner in self.browser_round_owners.items() if key in retained
            }
            self.browser_capture_counts[counter] = min(
                self.browser_capture_counts[counter] + 1, 1000000
            )
        native_timing = None
        if peer.backend == "gtk3" and os.environ.get("TYPELAY_TRACE"):
            native_timing = {"startedMs": self.capture_started_at, "describeStartedMs": now_ms()}
            self.native_capture_timing = native_timing
        description = (
            cached["description"]
            if reuse
            else await peer.link.ask("describe", {"target": target} if target else {})
        )
        if native_timing is not None:
            native_timing["describeReturnedMs"] = now_ms()
        if round_timing is not None:
            round_timing["describedMs"] = now_ms()
        if peer.browser_retired:
            return self.captured("browser-connection-removed")
        if not description:
            return self.captured("no-native-field")
        if editor_return is not None and (
            not self.browser_regenerate_current(editor_return)
            or description.get("target") != target
            or getattr(description, "route", None) != editor_return["route"]
        ):
            return self.captured("editor-return-identity-lost")
        if peer.backend == "browser":
            self.remember_browser_diagnostics(description.get("diagnostics"))
        if not reuse:
            self.remember_publication(peer, description, publication_generation)
        if glossary_return is not None and not self._source_glossary_return_current(
            glossary_return, peer, description
        ):
            return self.captured("glossary-return-identity-lost")
        policy_user_epoch = self.user_epoch
        policy_record = peer.shortcut
        if native_timing is not None:
            native_timing["approvalStartedMs"] = now_ms()
        approval_timing = None
        if (
            native_timing is not None
            and practice is not None
            and practice is self.practice_continuity
            and focus == "Source"
            and editor is None
            and returning is None
            and practice["runId"] != self.practice_approval_trace_run
        ):
            # One existing allowField request in this run's first waiting read.
            # Timing is not continuity evidence and never grants or retries a read.
            self.practice_approval_trace_run = practice["runId"]
            approval_timing = {}
            native_timing["ownedApproval"] = approval_timing
        approved = await self.approve(peer, description, owned_approval_timing=approval_timing)
        if native_timing is not None:
            native_timing["approvalReturnedMs"] = now_ms()
        if not approved:
            return self.captured("excluded-or-sensitive")
        if peer.browser_retired:
            return self.captured("browser-connection-removed")
        if round_timing is not None:
            round_timing["approvedMs"] = now_ms()
        if reuse and (
            capture_generation != self.browser_capture_generation
            or identity != self.browser_capture_identity(peer)
        ):
            return self.captured("window-changing")
        if self.enabled is False or settings_generation != self.settings_generation:
            return self.captured("settings-changed")
        # Reading a rapidly changing body can fail after allowField has already
        # approved fresh metadata. Establish the GTK3 command target from this
        # approval, including its first observation, without body/write rights.
        # This is a new Core approval with its original observation time, never a
        # renewed old lease. The native key still captures synchronously and checks
        # its original 150 ms age, IME, metadata and command-local revisions.
        authority = description.get("authority", {})
        policy_stamp = authority.get("observedAtMs")
        if (
            peer.backend == "gtk3"
            and peer.shortcut is policy_record
            and practice is None
            and self.practice_authorization is None
            and glossary_return is None
            and returning is None
            and publication_generation == self.publication_generation
            and self.shortcut_config.get("enabled")
            and command_generation == self.shortcut_config.get("generation")
            and focus == "Source"
            and not self.overlay_focused
            and peer in self._peers
            and self.focused_peer() is peer
            and description.get("focused") is True
            and window_epoch == self.window_epoch
            and foreground_epoch == self.foreground_epoch
            and policy_user_epoch == self.user_epoch
            and self.fresh()
            and type(policy_stamp) is int
            and 0 <= now_ms() - policy_stamp <= 150
        ):
            peer.shortcut = {
                "target": target_id(peer, description["target"]),
                "window": window,
                "authority": authority,
            }
            peer.link.command(
                "publish-shortcut",
                {
                    "target": description["target"],
                    "authority": authority,
                    "generation": command_generation,
                },
            )
        capture_data = {
            "target": description["target"],
            "authority": description["authority"],
            "requireFocused": browser_source,
        }
        if round_timing is not None and os.environ.get("TYPELAY_TRACE"):
            capture_data["traceRound"] = round_timing["round"]
        native_failure = {} if peer.backend == "gtk3" and os.environ.get("TYPELAY_TRACE") else None
        continuity = (
            {}
            if practice is not None
            and returning is None
            and focus == "Source"
            and practice["peer"] is peer
            else None
        )
        if continuity is not None:
            capture_data["ownedSource"] = True
        if native_timing is not None:
            native_timing["captureStartedMs"] = now_ms()
        # Evidence for this new bound Overlay read, never a replacement for the
        # snapshot clock or a timestamp supplied by the document.
        source_read_started_at = (
            now_ms()
            if peer.backend == "browser"
            and focus == "Overlay"
            and target is not None
            and context.get("boundTarget") == self.bound
            and description.get("target") == target
            and description.get("focused") is False
            else None
        )
        native = await peer.link.ask(
            "capture", capture_data, capture_trace=native_failure, capture_continuity=continuity
        )
        if native_timing is not None:
            native_timing["captureReturnedMs"] = now_ms()
        if peer.browser_retired:
            return self.captured("browser-connection-removed")
        if round_timing is not None:
            round_timing["capturedMs"] = now_ms()
            trace = native.get("captureTrace") if isinstance(native, dict) else None
            if (
                isinstance(trace, dict)
                and type(trace.get("round")) is int
                and trace["round"] == round_timing["round"]
            ):
                duration = trace.get("pageCaptureMs")
                if (
                    type(duration) in (int, float)
                    and 0 <= duration <= 10000
                    and math.isfinite(duration)
                ):
                    round_timing["pageCaptureMs"] = duration
                for key in ("nativeReplyReceivedMs", "desktopReplyDispatchedMs"):
                    stamp = trace.get(key)
                    if type(stamp) is int and 0 <= stamp < 2**53:
                        round_timing[key] = stamp
        if self.enabled is False or settings_generation != self.settings_generation:
            return self.captured("settings-changed")
        if editor is not self.practice_editor or returning is not self.practice_return:
            return self.captured("practice-edit-changed")
        if not native:
            if continuity:
                resumed = await self.context_advanced(
                    practice,
                    continuity,
                    window_epoch,
                    foreground_epoch,
                    settings_generation,
                    command_generation,
                )
                if resumed is not None:
                    return resumed
            return self.captured("native-read-declined", native_failure=native_failure)
        if editor_return is not None and (
            not self.browser_regenerate_current(editor_return)
            or native.get("target") != target
            or getattr(native, "route", None) != editor_return["route"]
        ):
            return self.captured("editor-return-identity-lost")
        if glossary_return is not None and (
            not self._source_glossary_return_current(glossary_return, peer, native)
            or native.get("ime") != "Clear"
            or native.get("writeSupported") is not False
        ):
            return self.captured("glossary-return-identity-lost")
        if not self.fresh():
            self.captured("compositor-stale")
            return self.expired_practice_capture(practice, native)
        if window_epoch != self.window_epoch:
            return self.captured("window-changing")
        if peer not in self._peers:
            return self.captured("native-disconnected")
        if focus == "Source" and (not native["focused"] or self.focused_peer() is not peer):
            return self.captured("native-focus-changed")
        authorization = self.practice_authorization
        if authorization is not None and authorization["peer"] is peer:
            authority = native.get("authority", {})
            baseline = authorization.get("contextGeneration", 0)
            context_changed = (
                baseline > 0 and authority.get("contextStamp", {}).get("context") != baseline
            )
            focus_changed = authorization.get("nativeFocusRevision", 0) > 0 and authority.get(
                "focusRevision"
            ) != authorization.get("nativeFocusRevision")
            if context_changed or focus_changed:
                self.retire_practice(authorization["runId"])
                return self.captured(
                    "practice-identity-lost",
                    native_failure={
                        "contextChanged": context_changed,
                        "focusChanged": focus_changed,
                    },
                )
        if practice is not None and practice.get("foregroundExpired"):
            expired = self.expired_practice_capture(practice, native)
            if expired is not None:
                self.captured("compositor-stale")
                return expired
        geometry = anchor_geometry(native["geometry"], peer.anchor_space)
        if geometry is None:
            return self.captured("no-visible-anchor")
        waiting = self.practice_continuity
        owned_source = None
        if returning is not None:
            authority = native.get("authority", {})
            if (
                practice is not returning
                or not self.practice_waiting(returning["runId"])
                or returning["peer"] is not peer
                or target_id(peer, native["target"]) != returning["target"]
                or authority.get("focusRevision") != returning["returnedFocusRevision"]
                or authority.get("contextStamp", {}).get("context")
                != returning["contextGeneration"]
                or command_generation == returning["commandGeneration"]
            ):
                return self.captured("practice-return-identity-lost")
            owned_source = {
                "binding": self.practice_return_binding(returning),
                "observedAtMs": native["observedAtMs"],
                "contextAdvanced": False,
            }
        if waiting is not None:
            authority = native.get("authority", {})
            # Keep the original short-circuit order and report only its first
            # failed gate. Do not re-query a dynamic predicate for diagnostics.
            if practice is not waiting:
                reason = "read-owner-mismatch"
            elif not self.practice_waiting(waiting["runId"]):
                reason = "waiting-invalid"
            elif waiting["peer"] is not peer:
                reason = "peer-changed"
            elif target_id(peer, native["target"]) != waiting["target"]:
                reason = "target-changed"
            elif authority.get("focusRevision") != waiting["nativeFocusRevision"]:
                reason = "native-focus-changed"
            elif authority.get("contextStamp", {}).get("context") != waiting["contextGeneration"]:
                reason = "context-changed"
            elif command_generation == waiting["commandGeneration"]:
                reason = "command-not-rotated"
            else:
                reason = None
            if reason is not None:
                return self.captured(
                    "practice-identity-lost",
                    native_failure={
                        "stage": "practice-continuity",
                        "reason": reason,
                        "practicePresent": practice is not None,
                        "foregroundSuspension": waiting.get("foregroundSuspension") is not None,
                    },
                )
            owned_source = {
                "binding": {
                    key: waiting[key]
                    for key in (
                        "runId",
                        "target",
                        "connectionId",
                        "nativeFocusRevision",
                        "contextGeneration",
                    )
                },
                "observedAtMs": native["observedAtMs"],
                "contextAdvanced": False,
            }
            owned_source["binding"]["commandGeneration"] = command_generation
        snapshot, record = self.remember(peer, native, window, geometry)
        acknowledgement = self.session_acknowledgement(peer, native)
        if (
            command_generation != self.shortcut_config.get("generation")
            or acknowledgement != self.session_acknowledgement(peer, description)
            or (
                peer.backend == "browser"
                and getattr(description, "route", None) != getattr(native, "route", None)
            )
        ):
            acknowledgement = None
        record["gtkSourceGlossary"] = self.gtk_source_glossary(
            peer, description
        ) and self.gtk_source_glossary(peer, native)
        record["qt6SourceGlossary"] = self.qt6_source_glossary(
            peer, description
        ) and self.qt6_source_glossary(peer, native)
        record["sourceGlossarySupported"] = (
            record["gtkSourceGlossary"]
            or record["qt6SourceGlossary"]
            or (
                peer.backend == "browser"
                and description.get("sourceGlossarySupported") is True
                and native.get("sourceGlossarySupported") is True
                and getattr(description, "route", None) == getattr(native, "route", None)
                and self.browser_source_local(peer, native)
            )
        )
        record["editorRegenerateSupported"] = bool(
            peer.backend == "browser"
            and acknowledgement
            and self.browser_source_local(peer, native)
            and all(
                type(item.get("sessionBindings", {}).get("editorRegenerateVersion")) is int
                and item["sessionBindings"]["editorRegenerateVersion"] == 1
                for item in (description, native)
            )
        )
        record["sessionShortcuts"] = acknowledgement
        if acknowledgement and acknowledgement["sourceLocalAcceptanceVersion"] == 1:
            peer.browser_had_v1 = True
        record["commandGeneration"] = command_generation
        self.bound, self._bound_observation = snapshot["target"], record
        if browser_source:
            round_timing["snapshotAtMs"] = native["observedAtMs"]
            if os.environ.get("TYPELAY_TRACE"):
                # Project only values from this already-approved capture. The
                # existing bounded round ring never stores text or target IDs.
                round_timing["observation"] = {
                    key: value
                    for key, value in {
                        "inputRevision": snapshot["inputEpoch"],
                        "focusRevision": snapshot["focusEpoch"],
                        "contentRevision": record["authority"]["contentRevision"],
                        "selectionStart": snapshot["selection"]["start"],
                        "selectionEnd": snapshot["selection"]["end"],
                    }.items()
                    if type(value) is int and 0 <= value < 2**53
                }
            record["roundTiming"] = round_timing
            self.remember_browser_diagnostics(native.get("diagnostics"))
            self.remember_publication(peer, native, publication_generation)
            if (
                capture_generation == self.browser_capture_generation
                and identity == self.browser_capture_identity(peer)
                and native["target"] == description["target"]
            ):
                # Retain metadata approval inputs only, never the source body.
                self.browser_capture = {
                    "identity": identity,
                    "description": RoutedReply(
                        {
                            key: native[key]
                            for key in ("target", "metadata", "authority", "sessionBindings")
                            if key in native
                        },
                        getattr(native, "route", None),
                    ),
                }
        self.state()
        return self.captured(
            "supported",
            {
                "snapshot": snapshot,
                "ownedSource": owned_source,
                "sessionShortcuts": acknowledgement,
                "focus": focus,
                "application": peer.application,
                "userEpoch": self.user_epoch,
                "foregroundEpoch": self.foreground_epoch,
                **(
                    {"sourceReadStartedAtMs": source_read_started_at}
                    if source_read_started_at is not None
                    and capture_generation == self.browser_capture_generation
                    and identity == self.browser_capture_identity(peer)
                    and self.overlay_focused
                    and native.get("focused") is False
                    and native.get("target") == target
                    and native.get("ime") == "Clear"
                    and native.get("metadata") == description.get("metadata")
                    and all(
                        key in native["authority"]
                        and key in description["authority"]
                        and native["authority"][key] == description["authority"][key]
                        for key in (
                            "target",
                            "metadataKey",
                            "contentRevision",
                            "inputRevision",
                            "focusRevision",
                        )
                    )
                    and acknowledgement
                    and acknowledgement["sourceReady"]
                    and acknowledgement["editorReady"]
                    and self.browser_source_local(peer, native)
                    else {}
                ),
            },
        )

    def configure_peer(self, peer):
        configuration = self.shortcut_config
        if peer.backend == "browser":
            configuration = {
                **configuration,
                "shortcutDispatch": "page",
                "sourceLocalAcceptanceVersion": 1 if peer.browser_registration else 0,
                "editorRegenerateVersion": 1,
                "presentationVersion": 1,
            }
            if peer.browser_retired:
                configuration = {
                    **configuration,
                    "enabled": False,
                    "sessionEnabled": False,
                    "sourceLocalAcceptanceVersion": 0,
                    "editorRegenerateVersion": 0,
                }
        if peer.backend not in ("qt6", "gtk3", "gtk4", "browser") and any(
            isinstance(configuration.get(name), str) and configuration[name].startswith("Ctrl+")
            for name in ("accept", "exit")
        ):
            # Parsing a saved preference does not enable an unverified route.
            configuration = {**configuration, "sessionEnabled": False}
        peer.link.command("configure-shortcuts", configuration)
        peer.shortcut = None
        generation = self.shortcut_config.get("generation")
        if peer.command_generation != generation:
            peer.command_generation = generation
            peer.command_id = 0

    def ordered_shortcut_time(self, peer, stamp):
        if (
            type(stamp) is not int
            or not 0 <= stamp <= now_ms()
            or stamp < self.shortcut_cli_at
            or (stamp == self.shortcut_cli_at and peer is not self.shortcut_cli_peer)
        ):
            return False
        previous = self.shortcut_command
        return (
            not previous
            or stamp > previous["triggeredAtMs"]
            or (stamp == previous["triggeredAtMs"] and previous["peer"] is peer)
        )

    def start_shortcut(self, peer, value):
        if (
            peer.browser_retired
            or not isinstance(value, dict)
            or value.get("kind") not in ("wake", "direct")
            or not self.shortcut_config.get("enabled")
            or value.get("generation") != self.shortcut_config.get("generation")
        ):
            return
        identity = value.get("commandId")
        if type(identity) is not int or not peer.command_id < identity <= 2**53 - 1:
            return
        peer.command_id = identity
        stamp = value.get("triggeredAtMs")
        if self.focused_peer() is not peer or not self.ordered_shortcut_time(peer, stamp):
            return
        self.shortcut_ordered = True
        self.shortcut_sequence += 1
        # Allocate ordering when the actual command begins, not when a delayed
        # cancellation arrives. One monitor owns ordering across all peers.
        self.shortcut_command = {
            "peer": peer,
            "id": identity,
            "kind": value["kind"],
            "generation": value["generation"],
            "sequence": self.shortcut_sequence,
            "triggeredAtMs": stamp,
            "record": peer.shortcut,
            "completed": False,
        }
        peer.shortcut = None

    def finish_shortcut(self, peer, value, rejected=False):
        if (
            peer.browser_retired
            or not isinstance(value, dict)
            or value.get("kind") not in ("wake", "direct")
            or not self.shortcut_config.get("enabled")
            or value.get("generation") != self.shortcut_config.get("generation")
        ):
            return
        command = self.shortcut_command
        if "commandId" in value:
            if (
                not command
                or command["completed"]
                or command["peer"] is not peer
                or type(value["commandId"]) is not int
                or value["commandId"] != command["id"]
                or value["kind"] != command["kind"]
                or value["generation"] != command["generation"]
                or command["triggeredAtMs"] < self.shortcut_cli_at
            ):
                return
        elif rejected or peer.command_id:
            return  # A rejection can never invent a command or its ordering.
        else:
            # Older Qt adapters can queue before reporting their result. Once a
            # newer ordered command exists, their missing start identity cannot
            # be reconstructed from the arrival time of a late result.
            if peer.backend == "qt6" and self.shortcut_ordered:
                if os.environ.get("TYPELAY_TRACE"):
                    try:
                        print("typelay-shortcut legacy-qt-unordered", file=sys.stderr)
                    except OSError:
                        pass
                return
            stamp = value.get("acceptedAtMs")
            if not self.ordered_shortcut_time(peer, stamp):
                return
            if peer.backend != "qt6":
                self.shortcut_ordered = True
            # Existing GTK/browser adapters complete their synchronous command
            # in one event. They cannot synthesize rejection feedback.
            self.shortcut_sequence += 1
            command = {
                "peer": peer,
                "id": None,
                "kind": value["kind"],
                "generation": value["generation"],
                "sequence": self.shortcut_sequence,
                "triggeredAtMs": stamp,
                "record": peer.shortcut,
                "completed": False,
            }
            self.shortcut_command = command
            peer.shortcut = None
        command["completed"] = True
        notice = {
            "kind": command["kind"],
            "commandGeneration": command["generation"],
            "commandSequence": command["sequence"],
            "userEpoch": self.user_epoch,
            "foregroundEpoch": self.foreground_epoch,
        }
        if rejected:
            reason = value.get("reason")
            reasons = {
                "Unsupported",
                "Sensitive",
                "ReadOnly",
                "ImeUnclear",
                "NotSourceFocus",
                "TargetChanged",
                "SourceChanged",
                "SelectionChanged",
                "EpochChanged",
                "StaleObservation",
                "PlatformFailure",
                "InvalidText",
                "BadPlan",
            }
            # Native errors are fixed codes. Never forward free text, metadata,
            # or a supplied body with a rejected command.
            notice["rejection"] = (
                reason if isinstance(reason, str) and reason in reasons else "Unsupported"
            )
        else:
            record = command["record"]
            native = value.get("native")
            if not record or not isinstance(native, dict):
                notice["rejection"] = "Unsupported"
            elif native.get("target") != record["target"]["value"]:
                notice["rejection"] = "TargetChanged"
            elif (geometry := anchor_geometry(native.get("geometry"), peer.anchor_space)) is None:
                notice["rejection"] = "Unsupported"
            else:
                snapshot, _ = self.remember(
                    peer, native, record["window"], geometry, frozen=command["kind"] == "direct"
                )
                notice["source"] = snapshot
                notice["acceptedAtMs"] = value["acceptedAtMs"]
                if peer.application is not None:
                    notice["application"] = peer.application
        self.host.send("notice", notice)
        if rejected and os.environ.get("TYPELAY_TRACE"):
            diagnostic = value.get("diagnostic")
            if not isinstance(diagnostic, dict) or set(diagnostic) not in (
                {"phase", "gate"},
                {"phase", "gate", "probe"},
            ):
                return
            phase, gate = diagnostic["phase"], diagnostic["gate"]
            phases = {"authority", "ime", "describe", "capture"}
            gates = {
                "connection",
                "configuration",
                "superseded",
                "target-missing",
                "target-destroyed",
                "focus",
                "unavailable",
                "ime",
                "authority-missing",
                "authority-revoked",
                "authority-generation",
                "authority-target",
                "source-revision",
                "input-revision",
                "authority-age",
                "metadata",
                "description",
                "capture",
            }
            if (
                type(phase) is not str
                or phase not in phases
                or type(gate) is not str
                or gate not in gates
                or self.shortcut_diagnostic_count >= 96
            ):
                return
            probe = diagnostic.get("probe")
            if "probe" in diagnostic:
                fields = {
                    "inputRevision",
                    "contentRevision",
                    "focusRevision",
                    "context",
                    "inputEpoch",
                    "epoch",
                }
                if (
                    not isinstance(probe, dict)
                    or set(probe) != {"before", "after"}
                    or any(
                        not isinstance(value, dict)
                        or set(value) != fields
                        or any(
                            type(count) is not int or not 0 <= count < 2**53
                            for count in value.values()
                        )
                        for value in probe.values()
                    )
                ):
                    return
            self.shortcut_diagnostic_count += 1
            try:
                print(
                    "typelay-shortcut-diagnostic "
                    + json.dumps(
                        {
                            "sequence": command["sequence"],
                            "atMs": now_ms(),
                            "phase": phase,
                            "gate": gate,
                            "reason": notice["rejection"],
                            **({"probe": probe} if probe is not None else {}),
                        },
                        separators=(",", ":"),
                    ),
                    file=sys.stderr,
                )
            except OSError:
                pass  # Diagnostics cannot replace or interrupt the original refusal.

    async def trigger(self, kind, request):
        """Compositor commands may use only the already-bound native shortcut lease."""
        if (
            not isinstance(request, dict)
            or set(request) != {"windowId", "triggeredAtMs"}
            or kind not in ("wake", "direct")
        ):
            return False
        stamp = request.get("triggeredAtMs")
        window = self.niri.focused
        if (
            type(stamp) is not int
            or not 0 <= now_ms() - stamp <= 150
            or not self.shortcut_config.get("enabled")
            or not self.fresh()
            or not window
            or type(request.get("windowId")) is not int
            or request.get("windowId") != window["id"]
            or stamp <= max(self.shortcut_configured_at, self.shortcut_cli_at)
            or (self.shortcut_command and stamp <= self.shortcut_command["triggeredAtMs"])
        ):
            return False
        # The authenticated CLI has no generation or command ID. Its original
        # time must be strictly newer, including within the same millisecond.
        # Reserve that time before awaiting native input, but let the native
        # started event allocate its one monitor sequence.
        peer = self.focused_peer()
        self.shortcut_cli_at = stamp
        self.shortcut_cli_peer = peer
        record = peer.shortcut if peer else None
        reason = None
        if self.overlay_focused or (record and record["window"]["id"] != window["id"]):
            reason = "NotSourceFocus"
        elif not record:
            reason = "Unsupported"
        if reason:
            self.shortcut_ordered = True
            self.shortcut_sequence += 1
            self.shortcut_command = {
                "peer": peer,
                "id": None,
                "kind": kind,
                "generation": self.shortcut_config["generation"],
                "sequence": self.shortcut_sequence,
                "triggeredAtMs": stamp,
                "record": None,
                "completed": True,
            }
            self.host.send(
                "notice",
                {
                    "kind": kind,
                    "commandGeneration": self.shortcut_config["generation"],
                    "commandSequence": self.shortcut_sequence,
                    "userEpoch": self.user_epoch,
                    "foregroundEpoch": self.foreground_epoch,
                    "rejection": reason,
                },
            )
            return False
        response = await peer.link.ask(
            "trigger-shortcut",
            {
                "kind": kind,
                "target": record["target"]["value"],
                "authority": record["authority"],
                "generation": self.shortcut_config.get("generation"),
                "triggeredAtMs": stamp,
            },
        )
        return bool(response and response.get("triggered"))

    def configure(self, value):
        def binding(chord):
            parts = chord.split("+")
            modifiers = sum({"Ctrl": 4, "Alt": 8, "Shift": 1}.get(part, 0) for part in parts[:-1])
            return {"keyval": self.gdk.keyval_from_name(parts[-1]), "modifiers": modifiers}

        if value.get("generation") != self.shortcut_config.get("generation"):
            self.shortcut_configured_at = now_ms()
            self.shortcut_cli_at = -1
            self.shortcut_cli_peer = None
            self.shortcut_sequence = 0
            self.shortcut_ordered = False
            self.shortcut_command = None
        accept, exit_key = value.get("accept"), value.get("exit")
        functions = {f"{prefix}F{index}" for prefix in ("", "Ctrl+") for index in range(1, 25)}
        valid_session = (
            isinstance(accept, str)
            and isinstance(exit_key, str)
            and accept in functions | {"Tab"}
            and exit_key in functions | {"Esc"}
            and accept.removeprefix("Ctrl+") != exit_key.removeprefix("Ctrl+")
            and isinstance(value.get("generation"), str)
            and bool(value["generation"])
        )
        self.shortcut_config = {
            **value,
            "sessionEnabled": value.get("sessionEnabled") is True and valid_session,
            "wake": binding(value["wake"]),
            "direct": binding(value["direct"]),
            "trace": bool(os.environ.get("TYPELAY_TRACE")),
        }
        self.frozen.clear()
        self.revoke()
        for peer in list(self._peers):
            peer.support_observation = None
            if peer.lifetime:
                self.configure_peer(peer)
        self.state()

    def retire_unpublished(self, lease, permit):
        if permit:
            self.host.send(
                "notice",
                {
                    "kind": "write-retired",
                    "retiredUnusedPlanId": permit["plan"]["id"],
                    "target": lease["source"]["target"],
                    "commandGeneration": lease["commandGeneration"],
                },
            )

    def finish_issued(self, plan_id, receipt=None, accepted_at=0, *, quarantine=False):
        issued = self.issued.pop(plan_id, None)
        if issued is None:
            return
        if receipt is None:
            self.retire_unpublished(issued["lease"], issued["permit"])
            return
        target = issued["lease"]["source"]["target"]
        if quarantine and issued["peer"].link.channel.alive:
            issued["peer"].link.command("quarantine", {"target": target["value"]})
        self.host.send(
            "notice",
            {
                "kind": "accept",
                "commandGeneration": issued["lease"].get("commandGeneration"),
                "practiceRunId": issued["lease"].get("practiceRunId"),
                "practiceConnectionId": issued["lease"].get("practiceConnectionId"),
                "target": target,
                "permit": issued["permit"],
                "completedReceipt": receipt,
                "acceptedAtMs": accepted_at,
                "userEpoch": issued["lease"]["userEpoch"],
                "foregroundEpoch": issued["lease"]["foregroundEpoch"],
            },
        )

    def publish(self, value, shortcut=False):
        received_at = (
            now_ms()
            if os.environ.get("TYPELAY_TRACE") and len(self.publish_diagnostics) < 192
            else None
        )
        lease = value["lease"]
        permit = None if shortcut else value.get("permit")
        record = self.observations.get(observation_key(lease["source"]))
        if not record or record["peer"] not in self._peers or not self.fresh():
            self.retire_unpublished(lease, permit)
            return
        peer = record["peer"]
        if peer.browser_retired or self.focused_peer() is not peer or self.overlay_focused:
            self.retire_unpublished(lease, permit)
            return
        if not shortcut and not self.practice_publication_allowed(lease, peer):
            self.retire_unpublished(lease, permit)
            return
        if shortcut:
            if not self.shortcut_config.get("enabled") or value[
                "generation"
            ] != self.shortcut_config.get("generation"):
                return
            peer.shortcut = record
            peer.link.command(
                "publish-shortcut",
                {
                    "target": record["target"]["value"],
                    "authority": record["authority"],
                    "generation": value["generation"],
                },
            )
        else:
            acknowledgement = self.current_session_acknowledgement(record)
            if (
                not acknowledgement
                or not acknowledgement["sourceReady"]
                or lease.get("commandGeneration") != acknowledgement["generation"]
            ):
                self.retire_unpublished(lease, permit)
                return
            if permit and len(self.issued) >= 32:
                self.retire_unpublished(lease, permit)
                self.revoke()
                return
            round_timing = record.get("roundTiming")
            if round_timing is not None and permit:
                previous = self.issued.get(self.published_plan[1]) if self.published_plan else None
                old = previous["permit"]["plan"] if previous else None
                plan = permit["plan"]
                round_timing.update(
                    {
                        "publishedMs": now_ms(),
                        "permitUntilMs": permit["expiresAtMs"],
                        "stableCandidate": bool(
                            old
                            and all(
                                old.get(key) == plan.get(key)
                                for key in ("replacement", "scope", "caret")
                            )
                            and all(
                                old["expected"].get(key) == plan["expected"].get(key)
                                for key in ("target", "text", "selection")
                            )
                        ),
                    }
                )
            self.invalidate_publication()
            self.session_publication = {
                "peer": peer,
                "target": record["target"],
                "generation": acknowledgement["generation"],
                "revision": lease.get("sessionRevision"),
            }
            if permit:
                self.timing("publishRemaining", permit["expiresAtMs"] - now_ms())
                self.published_plan = (peer, permit["plan"]["id"])
                self.publication_epochs = (self.window_epoch, self.user_epoch)
            if permit:
                self.issued[permit["plan"]["id"]] = {
                    "peer": peer,
                    "permit": permit,
                    "lease": lease,
                    "time": now_ms(),
                    "roundTiming": round_timing,
                    "route": record.get("route")
                    if acknowledgement.get("sourceLocalAcceptanceVersion") == 1
                    else None,
                }
            send_started = now_ms() if received_at is not None else None
            peer.link.command(
                "publish",
                {
                    "target": record["target"]["value"],
                    "authority": record["authority"],
                    "lease": lease,
                    "permit": permit,
                    "generation": acknowledgement["generation"],
                    **(
                        {"traceRound": round_timing["round"]}
                        if round_timing is not None and os.environ.get("TYPELAY_TRACE")
                        else {}
                    ),
                },
            )
            send_returned = now_ms() if received_at is not None else None
            if (
                received_at is not None
                and permit
                and peer.backend in ("gtk3", "gtk4", "qt6")
                and len(self.publish_diagnostics) < 192
            ):
                identity = permit.get("plan", {}).get("id")
                until = permit.get("expiresAtMs")
                try:
                    canonical = str(uuid.UUID(identity)) if type(identity) is str else None
                except ValueError:
                    canonical = None
                if canonical != identity or canonical is None or type(until) is not int:
                    return
                # Keep bounded metadata in memory: synchronous logging here could
                # delay Channel backpressure or the native reply. The existing
                # status request serializes it outside this publication call.
                # Returned means queue handoff, not native acknowledgement.
                self.publish_diagnostics.append(
                    {
                        "planId": canonical,
                        "receivedAtMs": received_at,
                        "sendStartedAtMs": send_started,
                        "sendReturnedAtMs": send_returned,
                        "permitUntilMs": until,
                    }
                )

    def revoke(self, shortcuts=False):
        self.clear_browser_capture()
        self.invalidate_publication()
        for peer in list(self._peers):
            if peer.lifetime and peer.link.channel.alive:
                peer.link.command("revoke", {"shortcuts": int(shortcuts)})
                if shortcuts:
                    peer.shortcut = None
        # Hiding/revoking a preview does not suspend the separately approved
        # on-demand command. Its metadata approval still expires after 150 ms and
        # native focus/IME/configuration checks remain mandatory at the real key.
        # Retain issued receipts and frozen commands across foreground-session
        # teardown: they belong to already accepted physical input, not the UI.

    def event(self, peer, name, value, *, route=None):
        if peer not in self._peers or peer.browser_retired and name != "accepted":
            return
        if name in ("document-focus", "document-closed") and peer.backend == "browser":
            if not peer.valid_browser_route(route):
                return
            if name == "document-closed":
                if peer.browser_active_route == route:
                    peer.browser_active_route = None
                self.retire_browser_document(peer, route)
            elif value is True:
                previous = peer.browser_active_route
                peer.browser_active_route = dict(route)
                if previous and previous != route:
                    self.retire_browser_document(peer, previous)
            elif value is False and peer.browser_active_route == route:
                peer.browser_active_route = None
                self.retire_browser_document(peer, route)
        elif name == "tab-diagnostic" and peer.backend == "browser":
            if not os.environ.get("TYPELAY_TRACE") or not isinstance(value, dict):
                return
            reasons = {
                "target-unfocused",
                "composing",
                "ime-unknown",
                "no-publication",
                "authority-changed",
                "no-permit",
                "capacity",
                "held",
                "repeat",
                "BadPlan",
                "StaleObservation",
                "Sensitive",
                "ImeUnclear",
                "SourceChanged",
                "EpochChanged",
                "SelectionChanged",
                "InvalidText",
                "UnknownOutcome",
                "None",
            }
            remaining = value.get("remainingMs")
            reason = value.get("reason")
            round_id = value.get("publicationRound")
            round_id = round_id if type(round_id) is int and 0 < round_id < 2**31 else None
            # Keep at most two already-started rounds by reference. Their later
            # capture/publication completion survives eviction from the live ring.
            peer_rounds = [
                item
                for item in self.browser_rounds
                if self.browser_round_owners.get(id(item)) is peer
            ]
            self.tab_capture_rounds = peer_rounds[-2:]
            recent_commands = []
            commands = value.get("recentCommands")
            if isinstance(commands, list) and len(commands) <= 4:
                for item in commands:
                    if not isinstance(item, dict):
                        continue
                    sequence = item.get("sequence")
                    operation, outcome = item.get("operation"), item.get("outcome")
                    timing = {
                        key: item.get(key)
                        for key in ("startFromEventMs", "endFromEventMs", "durationMs")
                    }
                    if (
                        type(sequence) is not int
                        or not 0 < sequence <= 1000000
                        or not isinstance(operation, str)
                        or operation
                        not in {
                            "capture",
                            "publish",
                            "describe",
                            "overlay-render",
                            "overlay-renew",
                            "configure-shortcuts",
                            "other",
                        }
                        or not isinstance(outcome, str)
                        or outcome not in {"returned", "null", "exception"}
                        or any(
                            type(number) not in (int, float)
                            or not math.isfinite(number)
                            or not (-10000 if key != "durationMs" else 0) <= number <= 10000
                            for key, number in timing.items()
                        )
                        or timing["startFromEventMs"] > timing["endFromEventMs"]
                    ):
                        continue
                    recent_commands.append(
                        {"sequence": sequence, "operation": operation, "outcome": outcome, **timing}
                    )
            detail = {
                "receivedAtMs": now_ms(),
                "reason": reason if isinstance(reason, str) and reason in reasons else "unknown",
                "recentCommands": recent_commands,
                "remainingMs": max(-1000, min(1000, remaining))
                if type(remaining) in (int, float) and math.isfinite(remaining)
                else None,
                **{
                    key: value.get(key)
                    if type(value.get(key)) in (int, float)
                    and math.isfinite(value[key])
                    and 0 <= value[key] <= (2**53 - 1 if "Host" in key else 10000)
                    else None
                    for key in (
                        "eventToTabCheckMs",
                        "eventToKeyEntryMs",
                        "keyEntryToTabCheckMs",
                        "tabCheckToWriteMs",
                        "eventHostLowerMs",
                        "eventHostUpperMs",
                        "tabCheckHostLowerMs",
                        "tabCheckHostUpperMs",
                    )
                },
                "publicationRound": round_id,
                "publicationTiming": next(
                    (dict(item) for item in peer_rounds if item["round"] == round_id), None
                ),
                "browserRounds": [dict(item) for item in self.tab_capture_rounds],
                **{
                    key: value.get(key) is True
                    for key in (
                        "publication",
                        "imeClear",
                        "targetMatches",
                        "inputMatches",
                        "focusMatches",
                    )
                },
            }
            try:
                print("typelay-tab " + json.dumps(detail, separators=(",", ":")), file=sys.stderr)
            except OSError:
                pass  # Diagnostic output cannot interrupt the native event channel.
        elif name == "publication":
            if isinstance(value, dict) and isinstance(value.get("target"), str):
                self.remember_publication(
                    peer,
                    RoutedReply({"target": value["target"], "publication": value}, route),
                    self.publication_generation,
                )
        elif name == "ui":
            editor = self.practice_editor
            if (
                editor
                and editor["peer"] is peer
                and value.get("operation") == "focus"
                and value.get("target") == editor["target"]["value"]
                and value.get("generation") == editor["commandGeneration"]
                and value.get("family") == "Other"
            ):
                self.retire_practice(editor["runId"])
            self.ui_event(peer.source, value)
        elif name == "input":
            authorization = (
                self.practice_authorization
                or self.practice_pending
                or self.practice_continuity
                or self.practice_editor
                or self.practice_return
            )
            if (
                authorization
                and authorization["peer"] is peer
                and value.get("target") == authorization["target"]["value"]
                and value.get("focusChanged") is True
                and value.get("focused") is False
                and not self.overlay_focused
                and not (
                    authorization is self.practice_editor
                    and authorization["phase"] in ("focusing", "editor")
                )
            ):
                # Only the bounded, already-revoked handoff may wait for native's
                # exact synchronous source-to-editor proof.
                self.retire_practice(authorization["runId"])
            self.invalidate_pause()
            self.clear_browser_capture()
            if self.published_plan and self.published_plan[0] is peer:
                self.invalidate_publication()
            if peer is self.focused_peer() or (
                self.bound
                and peer.lifetime == self.bound["lifetime"]
                and value["target"] == self.bound["value"]
            ):
                self.user_epoch += 1
                if not self.overlay_focused:
                    self.source_epoch += 1
                if value["textChanged"]:
                    self.content_epoch += 1
                # Only the current form's one-use, browser-proved synchronous
                # transfer is internal to this window. Every other field focus
                # event retains the ordinary window invalidation semantics.
                before_window = self.window_epoch
                if value["focusChanged"] and not self.consume_glossary_focus_transfer(
                    peer, value, route
                ):
                    self.window_epoch += 1
                if value["focusChanged"]:
                    self.observe_browser_return_focus(peer, value, route, before_window)
                if value["focused"] and self.enabled is not False:
                    self.bound = target_id(peer, value["target"])
                self.state()
        elif name == "destroy":
            paused = self.pause_observation
            self.invalidate_pause()
            self.clear_browser_capture()
            if self.bound == target_id(peer, value["target"]) or (
                paused and paused["target"] == target_id(peer, value["target"])
            ):
                self.bound = self._bound_observation = None
                self.window_epoch += 1
                self.revoke()
                self.state()
        elif name == "writeRetired":
            issued = self.issued.get(value.get("planId")) if isinstance(value, dict) else None
            if (
                not issued
                or value.keys() != {"planId", "target", "generation"}
                or issued["peer"] is not peer
                or issued.get("route") is not None
                and issued["route"] != route
                or value["target"] != issued["lease"]["source"]["target"]["value"]
                or value["generation"] != issued["lease"]["commandGeneration"]
            ):
                return
            self.finish_issued(value["planId"])
        elif name == "accepted":
            self.clear_browser_capture()
            if self.published_plan and self.published_plan[0] is peer:
                self.invalidate_publication()
            issued = self.issued.get(value.get("planId")) if isinstance(value, dict) else None
            if (
                not issued
                or issued["peer"] is not peer
                or issued.get("route") is not None
                and issued["route"] != route
            ):
                return
            # A receiptless native notification cannot cause a deferred write or
            # claim success. The original one-shot operation is now uncertain.
            receipt = value.get("receipt")
            if not isinstance(receipt, dict):
                receipt = refused(True)
            self.finish_issued(
                value["planId"],
                receipt,
                value["acceptedAtMs"],
                quarantine=not isinstance(value.get("receipt"), dict),
            )
        elif name == "escape":
            record = self._bound_observation
            acknowledgement = self.current_session_acknowledgement(record)
            publication = self.session_publication
            if (
                not isinstance(value, dict)
                or not acknowledgement
                or not acknowledgement["sourceReady"]
                or not publication
                or publication["peer"] is not peer
                or publication["target"] != record["target"]
                or type(value.get("sessionRevision")) is not int
                or value["sessionRevision"] != publication["revision"]
                or record["peer"] is not peer
                or self.focused_peer() is not peer
                or self.overlay_focused
                or value.get("target") != record["target"]["value"]
                or record["target"] != self.bound
                or value.get("generation") != acknowledgement["generation"]
            ):
                return
            self.host.send(
                "notice",
                {
                    "kind": "escape",
                    "target": record["target"],
                    "commandGeneration": acknowledgement["generation"],
                    "sessionRevision": publication["revision"],
                    "userEpoch": self.user_epoch,
                    "foregroundEpoch": self.foreground_epoch,
                },
            )
        elif name == "shortcut-started":
            self.start_shortcut(peer, value)
        elif name in ("shortcut", "shortcut-rejected"):
            self.finish_shortcut(peer, value, rejected=name == "shortcut-rejected")

    async def direct(self, plan):
        record = self.frozen.pop(observation_key(plan["expected"]), None)
        if not record or record["peer"] not in self._peers or record["peer"].browser_retired:
            return refused()
        peer, target = record["peer"], record["target"]["value"]
        generation = self.shortcut_config.get("generation")
        description = await peer.link.ask("describe", {"target": target})
        if peer.browser_retired or not await self.approve(peer, description):
            return refused()
        if peer.browser_retired or generation != self.shortcut_config.get("generation"):
            return refused()
        try:
            reply = await peer.link.ask(
                "direct",
                {
                    "target": target,
                    "plan": plan,
                    "authority": record["authority"],
                    "approved": description["authority"],
                    "generation": generation,
                    "expiresAtMs": now_ms() + 250,
                    **({"diagnostic": True} if os.environ.get("TYPELAY_TRACE") else {}),
                },
            ) or refused(True)
            if isinstance(reply, dict):
                diagnostic = reply.pop("diagnostic", None)
                fields = {"observed"} | {
                    prefix + field
                    for prefix in ("basis", "approved")
                    for field in (
                        "Context",
                        "Epoch",
                        "Input",
                        "Server",
                        "Applied",
                        "Flags",
                        "InputRevision",
                        "ContentRevision",
                        "FocusRevision",
                    )
                }
                if (
                    peer.backend == "gtk3"
                    and os.environ.get("TYPELAY_TRACE")
                    and getattr(self, "direct_trace_count", 0) < 16
                    and reply == {"status": "RejectedBeforeMutation", "reason": "EpochChanged"}
                    and isinstance(diagnostic, dict)
                    and (
                        (
                            diagnostic.keys() == fields | {"phase"}
                            and diagnostic.get("phase")
                            in (
                                "generation-before-ime",
                                "generation-after-ime",
                                "authority",
                                "input-or-focus",
                                "entry-focus",
                            )
                            and all(type(diagnostic[key]) is bool for key in fields)
                        )
                        or (
                            diagnostic.keys() == {"phase", "check"}
                            and (
                                diagnostic.get("phase") == "after-publication-take"
                                and diagnostic.get("check")
                                in (
                                    "ticket",
                                    "generation",
                                    "destroyed",
                                    "quarantined",
                                    "buffer",
                                    "storage-revision",
                                    "im-local",
                                    "im-state",
                                    "input-revision",
                                    "focus",
                                    "metadata",
                                    "selection",
                                    "body",
                                    "expiry",
                                )
                                or diagnostic.get("phase") in ("before-delete", "before-insert")
                                and diagnostic.get("check") == "ticket-claim"
                            )
                        )
                    )
                ):
                    self.direct_trace_count = getattr(self, "direct_trace_count", 0) + 1
                    try:
                        print(
                            "typelay-direct-diagnostic "
                            + json.dumps(
                                {
                                    "atMs": now_ms(),
                                    "reason": "EpochChanged",
                                    "diagnostic": diagnostic,
                                },
                                separators=(",", ":"),
                            ),
                            file=sys.stderr,
                        )
                    except OSError:
                        pass
            return reply
        except (OSError, EOFError, TimeoutError):
            if peer.link.channel.alive:
                peer.link.command("quarantine", {"target": target})
            return refused(True)

    def begin_editor_opening(self, presentation):
        record = self._presentation_record(presentation)
        if record is None or not presentation.editor_ready:
            return None
        identity = str(uuid.uuid4())
        self._editor_openings[identity] = {
            "peer": record["peer"],
            "target": copy.deepcopy(record["target"]),
            "authorization": self.practice_authorization,
            "deadline": now_ms() + 500,
            "route": copy.deepcopy(record.get("route")),
            "editor": None,
            "token": None,
        }
        return identity

    def prepare_editor_opening(self, identity, token):
        opening = self._editor_openings.get(identity)
        if opening is None or opening["target"] != token.get("target"):
            return False
        opening["token"] = copy.deepcopy(token)
        authorization = opening["authorization"]
        if authorization is not None:
            opening["editor"] = self.begin_practice_edit(authorization, token, opening["deadline"])
            if opening["editor"] is None:
                return False
        return True

    def _editor_opening_current(self, identity, revision):
        opening = self._editor_openings.get(identity)
        display = self._presentation
        return bool(
            opening
            and display
            and opening["token"] == display["token"]
            and display["revision"] == revision
            and display["state"] == "EditingTranslation"
            and self.source_connected(opening["peer"].source, focused=True)
            and self.bound == opening["target"]
        )

    async def focus_source_editor(self, identity, revision):
        if not self._editor_opening_current(identity, revision):
            return
        opening = self._editor_openings[identity]
        peer, token, editor = opening["peer"], opening["token"], opening["editor"]
        command = {
            "target": token["target"]["value"],
            "allowed": True,
            "revision": revision,
            "generation": token["commandGeneration"],
        }
        if editor is None:
            peer.link.command("overlay-edit", command)
        elif await self.revoke_practice_editor(editor):
            if not self._editor_opening_current(identity, revision):
                return
            editor["phase"] = "focusing"
            reply = await peer.link.ask(
                "overlay-edit",
                {
                    **command,
                    "practiceHandoff": {
                        "token": token,
                        "nativeFocusRevision": editor["nativeFocusRevision"],
                        "contextGeneration": editor["contextGeneration"],
                    },
                },
            )
            if self._editor_opening_current(identity, revision):
                self.complete_practice_edit(editor, reply)

    def finish_editor_opening(self, identity):
        opening = self._editor_openings.pop(identity, None)
        editor = opening["editor"] if opening else None
        if editor is not None and editor["phase"] != "editor":
            self.retire_practice(editor["runId"])

    def observe_overlay_focus(self, source, focused):
        editor = self.practice_editor
        if editor and editor["phase"] != "editor":
            return False
        self.set_overlay_focus(focused is True and self.source_connected(source, focused=True))
        return True

    def presented(self, value, revision):
        """Observe display association without acquiring source authority."""
        self._presentation = {
            "token": copy.deepcopy(value.get("viewContext", {}).get("token")),
            "state": value.get("state"),
            "revision": revision,
        }
        owner = self._editor_regenerate
        if owner is not None:
            token = value.get("viewContext", {}).get("token")
            actual = owner["operation"]
            owner["viewToken"] = copy.deepcopy(token)
            owner["viewState"] = value.get("state")
            if token != owner["action"]["token"] and not (
                value.get("state") == "ReturningToSource"
                and actual is not None
                and token == actual["operation"]
            ):
                # A render can precede its actual Return request. Keep only the
                # receipt, never grant it authority from this unassociated view.
                if (
                    value.get("state") != "ReturningToSource"
                    or not isinstance(token, dict)
                    or token.get("target") != owner["action"]["token"]["target"]
                    or token.get("commandGeneration")
                    != owner["action"]["token"]["commandGeneration"]
                ):
                    self._editor_regenerate = None

    def observe_editor_event(self, source, event):
        peer = self._source_peer(source)
        operation, value = event["operation"], event.get("value")
        presentation = self._presentation or {}
        owner = self._editor_regenerate
        if operation in ("draft", "focus", "composition") and owner is not None:
            action = owner["action"]
            actual = owner["operation"]
            current_token = presentation["token"]
            owner_revision = type(event.get("revision")) is int and (
                event["revision"] == owner["revision"]
                or event["revision"] == presentation["revision"]
                and (
                    actual is not None
                    and current_token == actual["operation"]
                    or actual is None
                    and presentation["state"] == "ReturningToSource"
                    and current_token == owner["viewToken"]
                )
            )
            if (
                self.browser_regenerate_current(owner, pending_focus=True, pending_view=True)
                and peer is owner["peer"]
                and getattr(event, "route", None) == owner["route"]
                and owner_revision
                and event.get("generation") == action["token"]["commandGeneration"]
            ):
                if operation == "draft":
                    text = value
                    if isinstance(value, dict) and set(value) == {"actionId", "text"}:
                        text = value["text"] if value["actionId"] == action["actionId"] else None
                    if _browser_draft(text):
                        owner["draft"] = text
                        if actual is None:
                            # The UI task and Returning render each precede the
                            # actual Return RPC. Keep only this exact owner's
                            # observation locally; do not invent an operation.
                            owner["laterDraftPending"] = True
                        else:
                            self.host.send("draft", text)
                elif operation == "composition" and type(value) is bool:
                    self.host.send("composition", value)
                elif operation == "focus" and type(value) is bool and not owner["returned"]:
                    # The original DOM blur can precede its queued new render.
                    # Consume it normally before the real source focus event;
                    # a completed receipt never restores the old editor role.
                    self.set_overlay_focus(value and peer is self.focused_peer())
            return True  # Never retarget an old action's event to a new view.
        return False

    async def regenerate_editor(self, action_id):
        owner = self._editor_regenerate
        if owner is None or owner["action"]["actionId"] != action_id:
            return
        # The source may have retired before the scheduled task ever runs.
        if not self.browser_regenerate_current(owner):
            if self._editor_regenerate is owner:
                self._editor_regenerate = None
            return
        try:
            accepted = await self.host.ask(
                "editorRegenerate", {"action": owner["action"], "draft": owner["initialDraft"]}
            )
            if accepted is not True and self._editor_regenerate is owner:
                if (
                    self.browser_regenerate_current(owner)
                    and (self._presentation or {}).get("state") == "EditingTranslation"
                    and self.overlay_focused
                    and (not owner["dispatched"] or owner.get("safeFailure"))
                ):
                    if owner["laterDraftPending"]:
                        owner["laterDraftPending"] = False
                        self.host.send("draft", owner["draft"])
                    owner["peer"].link.command(
                        "overlay-resume",
                        {
                            "target": owner["action"]["token"]["target"]["value"],
                            "generation": owner["action"]["token"]["commandGeneration"],
                            "revision": self._presentation["revision"],
                            "editorRegenerate": owner["action"],
                        },
                    )
                self._editor_regenerate = None
        except (OSError, EOFError, TimeoutError):
            if self._editor_regenerate is owner:
                self._editor_regenerate = None

    def withdraw_presentation(self):
        self._presentation = None
        self._editor_regenerate = None
        self._glossary_opening = None
        self._source_glossary = None
        self._source_glossary_return = None
        for identity in list(self._editor_openings):
            self.finish_editor_opening(identity)

    def _retire_source_operations(self, peer, route=None):
        for name in (
            "_editor_regenerate",
            "_glossary_opening",
            "_source_glossary",
            "_source_glossary_return",
        ):
            operation = getattr(self, name)
            if (
                operation
                and operation["peer"] is peer
                and (route is None or operation.get("route") == route)
            ):
                setattr(self, name, None)
        for identity, opening in list(self._editor_openings.items()):
            if opening["peer"] is peer and (route is None or opening["route"] == route):
                self.finish_editor_opening(identity)

    def begin_browser_regenerate(self, source, event, token):
        peer = self._source_peer(source, focused=True)
        if peer is None:
            return None
        record = self._bound_observation
        value = event.get("value")
        acknowledgement = self.current_session_acknowledgement(record)
        if (
            self._editor_regenerate is not None
            or peer.backend != "browser"
            or not record
            or record["peer"] is not peer
            or record["target"] != token.get("target")
            or record.get("route") != getattr(event, "route", None)
            or not self.browser_source_local(peer, event)
            or not self.overlay_focused
            or not self.fresh()
            or not acknowledgement
            or not acknowledgement["editorReady"]
            or token.get("commandGeneration") != acknowledgement["generation"]
            or not isinstance(value, dict)
            or set(value) != {"draft", "queuedAtMs"}
            or not _browser_draft(value["draft"])
            or type(value["queuedAtMs"]) is not int
        ):
            return None
        if (
            not record.get("editorRegenerateSupported")
            or not isinstance(value["draft"], str)
            or "\0" in value["draft"]
            or any(0xD800 <= ord(character) <= 0xDFFF for character in value["draft"])
            or len(value["draft"].encode("utf-16-le")) // 2 > 8192
            or value["queuedAtMs"] < 0
            or not 0 <= now_ms() - value["queuedAtMs"] < 500
        ):
            # This exact UI event was never admitted or dispatched. Clear only
            # its still-null local pending record, never a different/accepted receipt.
            peer.link.command(
                "overlay-resume",
                {
                    "target": token["target"]["value"],
                    "generation": token["commandGeneration"],
                    "revision": event["revision"],
                    "editorRegenerate": {
                        "actionId": None,
                        "token": token,
                        "queuedAtMs": value["queuedAtMs"],
                    },
                },
            )
            return None
        action = {"actionId": str(uuid.uuid4()), "token": token, "queuedAtMs": value["queuedAtMs"]}
        owner = {
            "action": action,
            "peer": peer,
            "record": record,
            "route": dict(record["route"]),
            "revision": event["revision"],
            "initialDraft": value["draft"],
            "draft": value["draft"],
            "laterDraftPending": False,
            "operation": None,
            "dispatched": False,
            "returned": False,
            "settings": self.settings_generation,
            "window": self.window_epoch,
            "foreground": self.foreground_epoch,
            "focusCount": 0,
            "focusWindow": None,
            "viewToken": token,
            "viewState": "EditingTranslation",
        }
        self._editor_regenerate = owner
        return action["actionId"]

    def browser_regenerate_current(
        self, owner, *, request=None, pending_focus=False, pending_view=False
    ):
        return bool(
            owner is not None
            and self._editor_regenerate is owner
            and owner["settings"] == self.settings_generation
            and owner["foreground"] == self.foreground_epoch
            and (
                owner["window"] == self.window_epoch
                or pending_focus
                and owner["dispatched"]
                and owner["focusCount"] == 1
                and owner["focusWindow"] == self.window_epoch
            )
            and (
                owner["viewToken"]
                in (
                    owner["action"]["token"],
                    (request or owner["operation"] or {}).get("operation"),
                )
                or pending_view
                and owner["operation"] is None
                and owner["viewState"] == "ReturningToSource"
                and isinstance(owner["viewToken"], dict)
                and all(
                    owner["viewToken"].get(key) == owner["action"]["token"].get(key)
                    for key in ("target", "commandGeneration")
                )
            )
            and owner["action"]["token"]["target"] == self.bound
            and owner["action"]["token"]["commandGeneration"]
            == self.shortcut_config.get("generation")
            and owner["peer"] in self._peers
            and self.focused_peer() is owner["peer"]
            and self.fresh()
            and self.browser_source_local(
                owner["peer"], RoutedReply({"target": self.bound["value"]}, owner["route"])
            )
        )

    def observe_browser_return_focus(self, peer, value, route, before_window):
        owner = self._editor_regenerate
        if owner is None or not owner["dispatched"] or owner["returned"]:
            return
        owner["focusCount"] += 1
        if (
            owner["focusCount"] == 1
            and peer is owner["peer"]
            and route == owner["route"]
            and value.get("target") == owner["action"]["token"]["target"]["value"]
            and value.get("textChanged") is False
            and value.get("focused") is True
            and before_window == owner["window"]
            and self.window_epoch > before_window
        ):
            # Record the real observation after the normal epoch update. This
            # is only pending evidence; no epoch/readiness is suppressed or adopted.
            owner["focusWindow"] = self.window_epoch
        else:
            owner["focusWindow"] = None

    async def return_browser_editor(self, context):
        owner, request = self._editor_regenerate, context.get("editorReturn")
        lost = {"returned": False, "contextLost": True}
        if (
            not isinstance(request, dict)
            or not self.browser_regenerate_current(owner, request=request)
            or owner["operation"] is not None
            or not isinstance(request, dict)
            or set(request) != {"action", "operation", "expiresAtMs"}
            or request.get("action") != owner["action"]
            or request.get("expiresAtMs") != owner["action"]["queuedAtMs"] + 500
            or not isinstance(request.get("operation"), dict)
            or request["operation"].get("target") != owner["action"]["token"]["target"]
            or request["operation"].get("commandGeneration")
            != owner["action"]["token"]["commandGeneration"]
            or type(request["operation"].get("revision")) is not int
            or request["operation"]["revision"] <= owner["action"]["token"]["revision"]
            or context.get("boundTarget") != self.bound
        ):
            return lost
        owner["operation"] = request
        if owner["laterDraftPending"]:
            # Only the actual accepted Return operation associates these real
            # earlier observations with Runtime's intent. ABA still emits once.
            owner["laterDraftPending"] = False
            self.host.send("draft", owner["draft"])
        peer, target = owner["peer"], owner["action"]["token"]["target"]["value"]
        try:
            description = await peer.link.ask("describe", {"target": target})
            if (
                not self.browser_regenerate_current(owner)
                or not description
                or getattr(description, "route", None) != owner["route"]
                or description.get("target") != target
                or not await self.approve(peer, description)
                or not self.browser_regenerate_current(owner)
            ):
                return lost
            proof = description.get("editorRegenerate", {})
            acknowledgement = self.session_acknowledgement(peer, description)
            if (
                not isinstance(proof, dict)
                or set(proof) != {"token", "queuedAtMs", "actionId", "editorFocused"}
                or proof.get("token") != owner["action"]["token"]
                or proof.get("queuedAtMs") != owner["action"]["queuedAtMs"]
                or proof.get("actionId") is not None
                or proof.get("editorFocused") is not True
                or type(description.get("authority", {}).get("observedAtMs")) is not int
                or not 0 <= now_ms() - description["authority"]["observedAtMs"] <= 150
                or not acknowledgement
                or not acknowledgement["editorReady"]
                or type(description.get("sessionBindings", {}).get("editorRegenerateVersion"))
                is not int
                or description["sessionBindings"]["editorRegenerateVersion"] != 1
                or not self.overlay_focused
            ):
                return lost
            if now_ms() >= request["expiresAtMs"]:
                # A fresh exact editor proof plus no dispatch is the only harmless
                # expiry. Flush the latest real draft before the false receipt.
                self.host.send("draft", owner["draft"])
                owner["safeFailure"] = True
                return {"returned": False}
            owner["dispatched"] = True
            response = await peer.link.ask(
                "return",
                {
                    "target": target,
                    "authority": description["authority"],
                    "generation": owner["action"]["token"]["commandGeneration"],
                    "revision": owner["revision"],
                    "editorReturn": request,
                },
            )
            if (
                not self.browser_regenerate_current(owner, pending_focus=True)
                or not isinstance(response, dict)
                or getattr(response, "route", None) != owner["route"]
                or response.get("editorReturn") != request
                or response.get("contextLost") is not False
            ):
                return lost
            if (
                response.get("returned") is True
                and owner["focusCount"] == 1
                and owner["focusWindow"] == self.window_epoch
                and now_ms() < request["expiresAtMs"]
            ):
                owner["returned"] = True
                owner["window"] = self.window_epoch
                self.overlay_focused = False
                self.clear_browser_capture()
                self.state()
                return {
                    "returned": True,
                    "editorReturn": {
                        "request": request,
                        "windowEpoch": self.window_epoch,
                    },
                }
            if (
                response.get("returned") is False
                and response.get("contextLost") is False
                and response.get("editorFocused") is True
                and self.browser_regenerate_current(owner)
                and owner["focusCount"] == 0
            ):
                self.host.send("draft", owner["draft"])
                owner["safeFailure"] = True
                return {"returned": False}
            return lost
        except (OSError, EOFError, TimeoutError):
            return lost  # Unknown dispatched work is never invented as a safe retry.

    async def return_to_source(self, context):
        trace = {"phase": "request"}
        try:
            return await self._return_to_source(context, trace)
        finally:
            # One terminal record per request. Diagnostics cannot change the
            # return result, and never read focus, a provider, or source content.
            if os.environ.get("TYPELAY_TRACE") and getattr(self, "return_trace_count", 0) < 32:
                self.return_trace_count = getattr(self, "return_trace_count", 0) + 1
                try:
                    trace["atMs"] = now_ms()
                    print(
                        "typelay-return " + json.dumps(trace, separators=(",", ":")),
                        file=sys.stderr,
                    )
                except (OSError, TypeError, ValueError):
                    pass

    async def _return_to_source(self, context, trace):
        if context.get("editorReturn") is not None:
            return await self.return_browser_editor(context)
        record = self._bound_observation
        configuration = context.get("sessionShortcuts")
        request = context.get("practiceReturn")
        editor = self.practice_editor if isinstance(request, dict) else None
        keys = (
            "runId",
            "target",
            "connectionId",
            "commandGeneration",
            "nativeFocusRevision",
            "contextGeneration",
        )
        if request is not None and (
            editor is None
            or not self.practice_editing(editor["runId"])
            or any(request.get("binding", {}).get(key) != editor[key] for key in keys)
            or request.get("token", {}).get("target") != editor["target"]
            or request.get("token", {}).get("commandGeneration") != editor["commandGeneration"]
            or type(request.get("token", {}).get("revision")) is not int
            or request["token"]["revision"] <= editor["token"]["revision"]
        ):
            return False

        def current():
            acknowledgement = self.current_session_acknowledgement(record)
            return bool(
                record
                and (
                    editor is None
                    or (self.practice_editor is editor and self.practice_editing(editor["runId"]))
                )
                and record is self._bound_observation
                and self.bound == context.get("boundTarget")
                and record["peer"] in self._peers
                and self.focused_peer() is record["peer"]
                and self.fresh()
                and acknowledgement
                and acknowledgement["editorReady"]
                and isinstance(configuration, dict)
                and configuration.get("generation") == acknowledgement["generation"]
                and configuration.get("bindings") == acknowledgement["bindings"]
            )

        trace["phase"] = "pre-current"
        if not current():
            return False
        peer, target = record["peer"], record["target"]["value"]
        trace_browser = (
            peer.backend == "browser"
            and os.environ.get("TYPELAY_TRACE")
            and getattr(self, "return_trace_count", 0) < 32
        )
        if trace_browser:
            basis_round = record.get("roundTiming", {})
            trace["basisRound"] = basis_round.get("round")
            trace["basisObservation"] = dict(basis_round.get("observation", {}))
        trace["phase"] = "describe"
        description = await peer.link.ask("describe", {"target": target})
        trace["phase"] = "describe-current"
        if not current():
            return False
        trace["phase"] = "approval"
        if not await self.approve(peer, description) or not current():
            return False
        trace["phase"] = "ack-match"
        if (
            self.session_acknowledgement(peer, description)
            != self.current_session_acknowledgement(record)
            or description["authority"]["contentRevision"] != record["authority"]["contentRevision"]
        ):
            return False
        if trace_browser:
            trace["beforeReturn"] = {
                key: value
                for key in ("inputRevision", "focusRevision", "contentRevision")
                if type(value := description["authority"].get(key)) is int and 0 <= value < 2**53
            }
        # Return only within the same active parent; the native consumer checks
        # this generation again after focus/provider callbacks, before return.
        trace["phase"] = "native-request"
        response = await peer.link.ask(
            "return",
            {
                "target": target,
                "authority": record["authority"],
                "generation": configuration["generation"],
                **(
                    {"practiceReturn": {"editorToken": editor["token"], "token": request["token"]}}
                    if editor is not None
                    else {}
                ),
            },
        )
        native_trace = response.get("returnTrace") if isinstance(response, dict) else None
        if isinstance(response, dict) and type(response.get("returned")) is bool:
            trace["nativeReturned"] = response["returned"]
        if isinstance(native_trace, dict):
            if native_trace.get("phase") in (
                "private-precondition",
                "native-return",
                "private-identity",
                "pending-identity",
                "pending-unready",
                "unready",
                "ready",
            ):
                trace["nativePhase"] = native_trace["phase"]
            for key in ("same", "ready"):
                if type(native_trace.get(key)) is bool:
                    trace[key] = native_trace[key]
        still_current = current()
        trace["phase"] = "native-reply" if still_current else "post-current"
        if still_current and response and response.get("returned"):
            self.overlay_focused = False
            if editor is not None:
                trace["phase"] = "proof"
                proof = response.get("practiceReturn")
                if (
                    not isinstance(proof, dict)
                    or proof.get("token") != request["token"]
                    or proof.get("target") != target
                    or type(proof.get("nativeFocusRevision")) is not int
                    or proof["nativeFocusRevision"] <= editor["nativeFocusRevision"]
                    or proof.get("contextGeneration") != editor["contextGeneration"]
                    or type(proof.get("observedAtMs")) is not int
                    or not 0 <= now_ms() - proof["observedAtMs"] <= 150
                ):
                    self.retire_practice(editor["runId"])
                    return {"returned": True}
                # Permanently retire G1, then obtain a new operation's real ACK.
                # The exact old binding stays intact; F2 belongs only to this intent.
                self.retire_practice(editor["runId"], notify=False)
                editor.update(
                    {
                        "returnedFocusRevision": proof["nativeFocusRevision"],
                        "windowEpoch": self.window_epoch,
                    }
                )
                self.practice_return = editor
                for key, value in list(self.observations.items()):
                    if value["peer"] is peer and value["target"] == editor["target"]:
                        self.observations.pop(key)
                self._bound_observation = None
                trace["phase"] = "revoke-ack"
                acknowledged = await self.confirm_practice_retirement(editor["runId"])
                if acknowledged:
                    trace["phase"] = "post-ack"
                if (
                    not acknowledged
                    or self.practice_return is not editor
                    or not self.practice_waiting(editor["runId"])
                    or self.shortcut_config.get("generation") != editor["commandGeneration"]
                    or not 0 <= now_ms() - proof["observedAtMs"] <= 150
                ):
                    self.retire_practice(editor["runId"])
                    return {"returned": True}
                self.state()
                trace["phase"] = "complete"
                return {
                    "returned": True,
                    "practice": {
                        "binding": {key: editor[key] for key in keys},
                        "token": request["token"],
                        "nativeFocusRevision": proof["nativeFocusRevision"],
                        "observedAtMs": proof["observedAtMs"],
                    },
                }
            self.state()
            trace["phase"] = "complete"
            return True
        return False

    async def request(self, operation, value):
        if operation == "suspendOwnedSource":
            return await self.suspend_owned_source(value)
        if operation == "retireAutomaticPractice":
            keys = (
                "runId",
                "target",
                "connectionId",
                "commandGeneration",
                "nativeFocusRevision",
                "contextGeneration",
            )
            acknowledged = self.practice_retirement_binding
            if acknowledged and all(value.get(key) == acknowledged.get(key) for key in keys):
                # A prepared successor must not hide this exact completed ACK.
                # Answer only the old binding: never confirm or retire its successor.
                waiting = self.practice_continuity or self.practice_return
                if waiting and all(waiting.get(key) == acknowledged.get(key) for key in keys):
                    self.practice_continuity = self.practice_return = None
                    self.practice_retired(acknowledged["runId"])
                return True
            authorization = (
                self.practice_authorization
                or self.practice_pending
                or self.practice_retirement
                or self.practice_editor
            )
            if not authorization or any(value.get(key) != authorization.get(key) for key in keys):
                return False
            self.retire_practice(authorization["runId"])
            return await self.confirm_practice_retirement(authorization["runId"])
        if operation == "observePause":
            return await self.observe_pause(value)
        if operation == "capture":
            return await self.capture(value)
        if operation == "direct":
            return await self.direct(value)
        if operation == "return":
            return await self.return_to_source(value)
        return None

    def notification(self, operation, value):
        if operation == "settings":
            self.practice_settings_changed()
            self.settings_generation += 1
            self.enabled = value.get("enabled")
            self.invalidate_pause()
            if self.enabled is not True:
                self.bound = self._bound_observation = None
                self.observations.clear()
                self.revoke(shortcuts=True)
            self.state()
        elif operation == "hide":
            self.invalidate_pause()
            self.state()
        elif operation == "shortcuts":
            self.configure(value)
        elif operation in ("publish", "publishShortcut"):
            self.publish(value, operation == "publishShortcut")
        elif operation in ("clear", "revoke"):
            self.revoke()

    def close(self):
        self.invalidate_pause()
        self.closing = True
        self.glib.source_remove(self.expiry_timer)
        self.glib.source_remove(self.listener_watch)
        self.niri.close()
        for peer in list(self._peers):
            peer.link.close()
        self.listener.close()
        self.paths.native.unlink(missing_ok=True)
        self.observations.clear()
        self.frozen.clear()
        self.issued.clear()
