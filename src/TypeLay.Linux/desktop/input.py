"""Linux target authority. All text policy and product decisions stay in Core.

Only the peer's GTK thread mutates a field. The desktop authenticates its process,
validates native anchors, obtains Core approval before reading, and accounts
for one-shot receipts. A late or missing receipt is never interpreted as success.
"""

import socket
from collections import OrderedDict

from geometry import anchor_geometry
from link import Link
from niri import Niri
from paths import peer_credentials, process_identity
from wire import now_ms


def target_id(peer, value):
    return {"backend": "linux-gtk3", "value": value, "lifetime": peer.lifetime}


def target_key(target):
    return target["lifetime"], target["value"]


def observation_key(snapshot):
    return (*target_key(snapshot["target"]), snapshot["observedAtMs"])


def refused(unknown=False):
    return {
        "status": "OutcomeUnknown" if unknown else "RejectedBeforeMutation",
        "reason": "UnknownOutcome" if unknown else "StaleObservation",
    }


class Peer:
    def __init__(self, owner, connection, pid):
        self.owner = owner
        self.pid = pid
        self.application = process_identity(pid)
        self.lifetime = None
        self.shortcut = None
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

    def event(self, name, data):
        if self.lifetime is None:
            import uuid

            if name != "hello" or data["protocol"] != 2 or data["pid"] != self.pid:
                raise ValueError("native-handshake")
            self.lifetime = str(uuid.UUID(data["lifetime"]))
            if self.hello_timer:
                self.owner.glib.source_remove(self.hello_timer)
                self.hello_timer = 0
            self.owner.configure_peer(self)
            self.owner.changed()
        else:
            self.owner.event(self, name, data)


class Input:
    def __init__(self, glib, gdk, paths, host, tasks):
        self.glib, self.gdk, self.paths, self.host, self.tasks = glib, gdk, paths, host, tasks
        self.peers = set()
        self.observations = OrderedDict()
        self.frozen = OrderedDict()
        self.issued = OrderedDict()
        self.receipts = OrderedDict()
        self.bound = None
        self.bound_observation = None
        self.overlay_focused = False
        self.ui_event = lambda *_: None
        self.user_epoch = self.source_epoch = self.foreground_epoch = 0
        self.window_epoch = self.content_epoch = 0
        self.last_focus = None
        self.last_state = None
        self.shortcut_config = {"enabled": False}
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
            if len(self.peers) >= 64:
                raise OSError("native-capacity")
            pid, _, _ = peer_credentials(connection)
            peer = Peer(self, connection, pid)
            self.peers.add(peer)
        except OSError:
            connection.close()
        return True

    def lost(self, peer):
        self.peers.discard(peer)
        for collection in (self.observations, self.frozen, self.issued):
            for key, value in list(collection.items()):
                if value["peer"] is peer:
                    collection.pop(key)
        if not self.closing:
            self.changed()

    def fresh(self):
        return self.niri.ready and 0 <= now_ms() - self.niri.observed_at <= 150

    def focused_peer(self):
        if not self.fresh() or not self.niri.focused:
            return None
        pid = self.niri.focused.get("pid")
        peers = [peer for peer in self.peers if peer.pid == pid and peer.lifetime]
        # niri chooses the process, not a guessed window-to-widget mapping. The
        # authenticated native peer then proves which GTK parent actually owns
        # keyboard focus. Its opaque field and parent-local view identify the
        # operation even when this process has several compositor windows.
        return peers[0] if len(peers) == 1 else None

    def focus(self):
        if self.bound and self.overlay_focused and self.fresh():
            return "Overlay"
        peer = self.focused_peer()
        if peer and self.bound and peer.lifetime == self.bound["lifetime"]:
            return "Source"
        return "Other"

    def state(self):
        value = {
            "ready": self.fresh(),
            "nativeReady": self.focused_peer() is not None,
            "target": self.bound,
            "focus": self.focus(),
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
        self.overlay_focused = active
        self.state()

    def expire(self):
        stamp = now_ms()
        for collection, age in (
            (self.observations, 2000),
            (self.frozen, 120_000),
            (self.issued, 1000),
            (self.receipts, 1000),
        ):
            for key, value in list(collection.items()):
                if stamp - value["time"] > age:
                    collection.pop(key)
        self.state()
        return not self.closing

    def remember(self, peer, native, window, geometry, frozen=False):
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
            "window": window,
            "geometry": geometry,
            "time": now_ms(),
            "target": snapshot["target"],
        }
        # Do not retain captured source strings in this bookkeeping. Core owns them.
        collection = self.frozen if frozen else self.observations
        collection[observation_key(snapshot)] = record
        while len(collection) > (4 if frozen else 128):
            collection.popitem(last=False)
        return snapshot, record

    async def approve(self, peer, description):
        if not description:
            return False
        metadata = {**description["metadata"], "process": peer.application}
        return await self.host.ask("allowField", metadata) is True

    async def capture(self, context):
        window_epoch = self.window_epoch
        focus = "Overlay" if self.overlay_focused else "Source"
        if (
            focus == "Overlay"
            and self.bound_observation
            and context.get("boundTarget") == self.bound
        ):
            record = self.bound_observation
            peer, window = record["peer"], self.niri.by_id(record["window"]["id"])
            target = self.bound["value"]
        else:
            peer, window, target = self.focused_peer(), self.niri.focused, None
        if peer not in self.peers or window is None or not self.fresh():
            return None
        description = await peer.link.ask("describe", {"target": target} if target else {})
        if not await self.approve(peer, description):
            return None
        native = await peer.link.ask(
            "capture", {"target": description["target"], "authority": description["authority"]}
        )
        if (
            not native
            or not self.fresh()
            or window_epoch != self.window_epoch
            or peer not in self.peers
        ):
            return None
        if focus == "Source" and (not native["focused"] or self.focused_peer() is not peer):
            return None
        geometry = anchor_geometry(native["geometry"])
        if geometry is None:
            return None
        snapshot, record = self.remember(peer, native, window, geometry)
        self.bound, self.bound_observation = snapshot["target"], record
        self.state()
        return {
            "snapshot": snapshot,
            "focus": focus,
            "application": peer.application,
            "userEpoch": self.user_epoch,
            "foregroundEpoch": self.foreground_epoch,
        }

    def configure_peer(self, peer):
        peer.link.command("configure-shortcuts", self.shortcut_config)
        peer.shortcut = None

    def configure(self, value):
        def binding(chord):
            parts = chord.split("+")
            modifiers = sum({"Ctrl": 4, "Alt": 8, "Shift": 1}.get(part, 0) for part in parts[:-1])
            return {"keyval": self.gdk.keyval_from_name(parts[-1]), "modifiers": modifiers}

        self.shortcut_config = {
            **value,
            "wake": binding(value["wake"]),
            "direct": binding(value["direct"]),
        }
        self.frozen.clear()
        self.revoke()
        for peer in list(self.peers):
            if peer.lifetime:
                self.configure_peer(peer)

    def publish(self, value, shortcut=False):
        lease = value["lease"]
        record = self.observations.get(observation_key(lease["source"]))
        if not record or record["peer"] not in self.peers or not self.fresh():
            return
        peer = record["peer"]
        if self.focused_peer() is not peer or self.overlay_focused:
            return
        if shortcut:
            if value["generation"] != self.shortcut_config.get("generation"):
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
            permit = value.get("permit")
            if permit:
                self.issued[permit["plan"]["id"]] = {
                    "peer": peer,
                    "permit": permit,
                    "lease": lease,
                    "time": now_ms(),
                }
                while len(self.issued) > 32:
                    self.issued.popitem(last=False)
            peer.link.command(
                "publish",
                {
                    "target": record["target"]["value"],
                    "authority": record["authority"],
                    "lease": lease,
                    "permit": permit,
                },
            )

    def revoke(self, shortcuts=False):
        for peer in list(self.peers):
            if peer.lifetime and peer.link.channel.alive:
                peer.link.command("revoke", {"shortcuts": int(shortcuts)})
                if shortcuts:
                    peer.shortcut = None
        # Hiding/revoking a preview does not suspend the separately approved
        # on-demand command. Its metadata approval still expires after 150 ms and
        # native focus/IME/configuration checks remain mandatory at the real key.
        # Retain issued receipts and frozen commands across foreground-session
        # teardown: they belong to already accepted physical input, not the UI.

    def event(self, peer, name, value):
        if name == "ui":
            self.ui_event(peer, value)
        elif name == "input":
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
                if value["focusChanged"]:
                    self.window_epoch += 1
                if value["focused"]:
                    self.bound = target_id(peer, value["target"])
                self.state()
        elif name == "destroy":
            if self.bound == target_id(peer, value["target"]):
                self.bound = self.bound_observation = None
                self.window_epoch += 1
                self.revoke()
                self.state()
        elif name == "accepted":
            issued = self.issued.pop(value["planId"], None)
            if not issued or issued["peer"] is not peer:
                peer.link.command(
                    "quarantine", {"target": self.bound["value"] if self.bound else ""}
                )
                return
            receipt = value["receipt"]
            self.receipts[value["planId"]] = {"value": receipt, "time": now_ms()}
            self.host.send(
                "notice",
                {
                    "kind": "accept",
                    "permit": issued["permit"],
                    "acceptedAtMs": value["acceptedAtMs"],
                    "userEpoch": issued["lease"]["userEpoch"],
                    "foregroundEpoch": issued["lease"]["foregroundEpoch"],
                },
            )
        elif name == "escape":
            self.host.send(
                "notice",
                {
                    "kind": "escape",
                    "userEpoch": self.user_epoch,
                    "foregroundEpoch": self.foreground_epoch,
                },
            )
        elif name == "shortcut":
            record = peer.shortcut
            peer.shortcut = None
            if not record or value["generation"] != self.shortcut_config.get("generation"):
                return
            native = value["native"]
            if native["target"] != record["target"]["value"]:
                return
            window = record["window"]
            geometry = anchor_geometry(native["geometry"])
            if geometry is None:
                return
            snapshot, _ = self.remember(
                peer, native, window, geometry, frozen=value["kind"] == "direct"
            )
            self.host.send(
                "notice",
                {
                    "kind": value["kind"],
                    "source": snapshot,
                    "commandGeneration": value["generation"],
                    "acceptedAtMs": value["acceptedAtMs"],
                    "userEpoch": self.user_epoch,
                    "foregroundEpoch": self.foreground_epoch,
                },
            )

    async def direct(self, plan):
        record = self.frozen.pop(observation_key(plan["expected"]), None)
        if not record or record["peer"] not in self.peers:
            return refused()
        peer, target = record["peer"], record["target"]["value"]
        generation = self.shortcut_config.get("generation")
        description = await peer.link.ask("describe", {"target": target})
        if not await self.approve(peer, description) or generation != self.shortcut_config.get(
            "generation"
        ):
            return refused()
        try:
            return await peer.link.ask(
                "direct",
                {
                    "target": target,
                    "plan": plan,
                    "authority": record["authority"],
                    "approved": description["authority"],
                    "expiresAtMs": now_ms() + 250,
                },
            ) or refused(True)
        except (OSError, EOFError, TimeoutError):
            if peer.link.channel.alive:
                peer.link.command("quarantine", {"target": target})
            return refused(True)

    async def return_to_source(self, context):
        record = self.bound_observation
        if (
            not record
            or self.bound != context.get("boundTarget")
            or record["peer"] not in self.peers
            or not self.fresh()
        ):
            return False
        peer, target = record["peer"], record["target"]["value"]
        description = await peer.link.ask("describe", {"target": target})
        if not await self.approve(peer, description):
            return False
        if description["authority"]["contentRevision"] != record["authority"]["contentRevision"]:
            return False
        # The nonmodal native editor shares the source's real GTK toplevel.
        # Return only within that active parent, never activate another window.
        response = await peer.link.ask(
            "return", {"target": target, "authority": record["authority"]}
        )
        if response and response.get("returned"):
            self.overlay_focused = False
            self.state()
            return True
        return False

    async def request(self, operation, value):
        if operation == "capture":
            return await self.capture(value)
        if operation == "direct":
            return await self.direct(value)
        if operation == "return":
            return await self.return_to_source(value)
        if operation == "receipt":
            receipt = self.receipts.pop(value["id"], None)
            return receipt["value"] if receipt else refused(True)
        return None

    def notification(self, operation, value):
        if operation == "shortcuts":
            self.configure(value)
        elif operation in ("publish", "publishShortcut"):
            self.publish(value, operation == "publishShortcut")
        elif operation in ("clear", "revoke"):
            self.revoke()

    def close(self):
        self.closing = True
        self.glib.source_remove(self.expiry_timer)
        self.glib.source_remove(self.listener_watch)
        self.niri.close()
        for peer in list(self.peers):
            peer.link.close()
        self.listener.close()
        self.paths.native.unlink(missing_ok=True)
        self.observations.clear()
        self.frozen.clear()
        self.issued.clear()
        self.receipts.clear()
