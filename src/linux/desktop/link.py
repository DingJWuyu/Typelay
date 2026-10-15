"""Request/reply ownership over inherited pipes and authenticated local streams."""

import asyncio

from wire import Channel


def capture_failure_trace(value):
    """Allow diagnostic scalars only; this never supplies an input result."""
    if not isinstance(value, dict) or value.get("reason") not in (
        "ime-unavailable",
        "focus-before",
        "stamp-before",
        "age-before",
        "metadata-before",
        "text-unavailable",
        "description-unavailable",
        "ime-unknown",
        "ime-composing",
        "stamp-after",
        "age-after",
        "focus-after",
        "storage-changed",
        "input-changed",
        "focus-revision-changed",
        "metadata-after",
    ):
        return {}
    result = {"reason": value["reason"]}
    for key in (
        "ageBeforeMs",
        "ageAfterMs",
        "beforeStorageRevision",
        "beforeInputRevision",
        "beforeFocusRevision",
        "afterStorageRevision",
        "afterInputRevision",
        "afterFocusRevision",
    ):
        item = value.get(key)
        if type(item) is int and -(2**53) < item < 2**53 and (key.startswith("age") or item >= 0):
            result[key] = item
    for key in ("targetMatchesBefore", "targetMatchesAfter"):
        if type(value.get(key)) is bool:
            result[key] = value[key]
    return result


class Link:
    def __init__(
        self,
        glib,
        read_fd,
        write_fd,
        notification,
        request,
        closed,
        owner=None,
        response_key="reply",
    ):
        self.notification = notification
        self.request = request
        self.on_closed = closed
        self.response_key = response_key
        self.sequence = 0
        self.pending = {}
        self.capture_traces = {}
        self.capture_continuities = {}
        self.tasks = set()
        self.channel = Channel(glib, read_fd, write_fd, self.receive, self.disconnected, owner)

    async def ask(
        self, operation, data=None, timeout=0.6, *, capture_trace=None, capture_continuity=None
    ):
        if len(self.pending) >= 16:
            raise OSError("request-capacity")
        self.sequence += 1
        identity = self.sequence
        future = asyncio.get_running_loop().create_future()
        self.pending[identity] = future
        if operation == "capture" and type(capture_trace) is dict:
            self.capture_traces[identity] = capture_trace
        if operation == "capture" and type(capture_continuity) is dict:
            self.capture_continuities[identity] = capture_continuity
        try:
            self.channel.send({"id": identity, "op": operation, "data": data})
            return await asyncio.wait_for(future, timeout)
        finally:
            self.pending.pop(identity, None)
            self.capture_traces.pop(identity, None)
            self.capture_continuities.pop(identity, None)

    def send(self, operation, data=None):
        self.channel.send({"event": operation, "data": data})

    def command(self, operation, data=None):
        # Native commands with id zero are deliberately fire-and-forget. Write
        # operations are never sent this way: their receipt must be accounted for.
        self.channel.send({"id": 0, "op": operation, "data": data or {}})

    def receive(self, frame):
        if self.response_key in frame and "op" not in frame:
            future = self.pending.get(frame[self.response_key])
            if future is not None and not future.done():
                future.set_result(frame.get("data"))
                continuity = self.capture_continuities.get(frame[self.response_key])
                value = frame.get("captureContinuity")
                if continuity is not None and frame.get("data") is None and isinstance(value, dict):
                    if (
                        isinstance(value.get("target"), str)
                        and len(value["target"]) <= 128
                        and all(
                            type(value.get(key)) is int and 0 < value[key] < 2**53
                            for key in ("nativeFocusRevision", "contextGeneration", "observedAtMs")
                        )
                    ):
                        continuity.update(
                            {
                                key: value[key]
                                for key in (
                                    "target",
                                    "nativeFocusRevision",
                                    "contextGeneration",
                                    "observedAtMs",
                                )
                            }
                        )
                trace = self.capture_traces.get(frame[self.response_key])
                if trace is not None and frame.get("data") is None:
                    # A diagnostic cannot alter the reply, reject a connection,
                    # or attach to a timed-out/different operation's request.
                    try:
                        trace.update(capture_failure_trace(frame.get("captureTrace")))
                    except Exception:
                        pass
        elif "event" in frame:
            self.notification(frame["event"], frame.get("data"))
        elif self.request is not None and len(self.tasks) < 16:
            task = asyncio.create_task(self.serve(frame))
            self.tasks.add(task)
            task.add_done_callback(self.tasks.discard)
        else:
            self.channel.close()

    async def serve(self, frame):
        try:
            result = await self.request(frame["op"], frame.get("data"))
        except (OSError, EOFError, ValueError, KeyError, TypeError, TimeoutError):
            result = None
        if self.channel.alive:
            self.channel.send({"reply": frame["id"], "data": result})

    def disconnected(self):
        for future in self.pending.values():
            if not future.done():
                future.set_exception(EOFError("peer-disconnected"))
        for task in self.tasks:
            task.cancel()
        self.on_closed()

    def close(self):
        self.channel.close()
