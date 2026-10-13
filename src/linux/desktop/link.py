"""Request/reply ownership over inherited pipes and authenticated local streams."""

import asyncio

from wire import Channel


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
        self.tasks = set()
        self.channel = Channel(glib, read_fd, write_fd, self.receive, self.disconnected, owner)

    async def ask(self, operation, data=None, timeout=0.6):
        if len(self.pending) >= 16:
            raise OSError("request-capacity")
        self.sequence += 1
        identity = self.sequence
        future = asyncio.get_running_loop().create_future()
        self.pending[identity] = future
        try:
            self.channel.send({"id": identity, "op": operation, "data": data})
            return await asyncio.wait_for(future, timeout)
        finally:
            self.pending.pop(identity, None)

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
