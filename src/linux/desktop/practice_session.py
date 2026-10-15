"""Own the managed lesson and its temporary, exact-field acceptance authority.

Control callers choose a lesson, never a target, connection or command generation.
Progress is metadata only; the authenticated native peer supplies the binding.
"""

import asyncio
import uuid


class PracticeSession:
    def __init__(self, host, input_service, support, schedule=asyncio.create_task):
        self.host, self.input, self.support = host, input_service, support
        self.schedule = schedule
        self.lock = asyncio.Lock()
        self.pid = self.run = self.target = None
        self.lesson = "onDemand"
        self.ending = self.end_unconfirmed = False
        self.finish_pending = None
        self.input.practice_retired = self.invalidated
        self.input.practice_settings_changed = self.clear_completion

    def clear_completion(self):
        self.finish_pending = None

    def operation(self, action):
        name = "AutomaticPractice" if self.lesson == "automatic" else "OnDemandPractice"
        return action + name if action else name[0].lower() + name[1:]

    def invalidated(self, run):
        if run == self.run:
            self.ending = True
            self.schedule(self.retire_invalidated(run))

    async def retire_invalidated(self, run):
        async with self.lock:
            if run == self.run:
                if self.finish_pending and self.finish_pending["run"] == run:
                    # Finish already owns the Runtime retirement and save. Its
                    # failed save keeps only evidence for retry, never authority.
                    self.pid = self.run = self.target = None
                    self.ending = False
                else:
                    await self.retire()

    async def retire(self):
        run, automatic = self.run, self.lesson == "automatic"
        if self.end_unconfirmed:
            return False
        if run is None:
            return True
        self.ending = automatic
        needs_ack = False
        if automatic:
            # Reject new publications before yielding. The existing native
            # permit is retired only when its original connection acknowledges.
            needs_ack = self.input.retire_practice(run, notify=False)
        self.pid = self.run = self.target = None
        try:
            ended = await self.host.ask(self.operation("end"), run)
            confirmed = not automatic or ended is True
        except (OSError, EOFError, ValueError, TimeoutError):
            confirmed = False
        if needs_ack:
            acknowledged = await self.input.confirm_practice_retirement(run)
            confirmed = confirmed and acknowledged
        self.end_unconfirmed = not confirmed
        self.ending = False
        return confirmed

    async def begin(self, pid, confirmed_switch, *, lesson="onDemand"):
        if lesson not in ("onDemand", "automatic") or self.ending or self.end_unconfirmed:
            return False
        async with self.lock:
            if not self.support.owns_practice(pid) or not await self.retire():
                return False
            self.finish_pending = None
            self.lesson = lesson
            if await self.host.ask(self.operation("start"), confirmed_switch) is not True:
                return False
            if not self.support.owns_practice(pid):
                return False
            run = str(uuid.uuid4())
            if await self.host.ask(self.operation("begin"), run) is not True:
                return False
            if not self.support.owns_practice(pid):
                await self.host.ask(self.operation("end"), run)
                return False
            self.pid, self.run = pid, run
            return True

    async def end(self, pid):
        # The window destroys its native source immediately. Releasing ownership
        # also prevents a pending Begin attaching after the process has closed.
        self.support.release_practice(pid)
        async with self.lock:
            pending = self.finish_pending
            self.finish_pending = None
            if self.pid == pid:
                return await self.retire()
            if pending and pending["pid"] == pid:
                return await self.host.ask(self.operation("end"), pending["run"]) is True
        return not self.end_unconfirmed

    async def progress(self, pid, *, lesson="onDemand"):
        async with self.lock:
            if self.finish_pending is not None:
                return None
            if self.run is None or self.ending or lesson != self.lesson:
                return None
            if not self.support.owns_practice(self.pid):
                await self.retire()
                return None
            if pid != self.pid:
                return None
            progress = await self.host.ask(self.operation("") + "Progress")
            if not isinstance(progress, dict) or progress.get("runId") != self.run:
                await self.retire()
                return None
            automatic = self.lesson == "automatic"
            binding = await self.input.practice_binding(pid, self.target) if automatic else None
            target = (
                (binding["target"] if binding else None)
                if automatic
                else await self.input.practice_target(pid, self.target)
            )
            if not self.support.owns_practice(pid):
                await self.retire()
                return None
            if self.target is not None and target != self.target:
                await self.retire()
                return None
            if self.target is None and target is not None:
                data = (
                    {"runId": self.run, **binding}
                    if automatic
                    else {"runId": self.run, "target": target}
                )
                if automatic and not self.input.prepare_practice(data, self.support.owns_practice):
                    await self.retire()
                    return None
                if await self.host.ask(self.operation("bind"), data) is not True:
                    await self.retire()
                    return None
                if automatic and not self.input.authorize_practice(
                    data, self.support.owns_practice
                ):
                    await self.retire()
                    return None
                self.target = target
                progress = await self.host.ask(self.operation("") + "Progress")
            if (
                not isinstance(progress, dict)
                or progress.get("runId") != self.run
                or progress.get("target") != self.target
            ):
                await self.retire()
                return None
            return progress

    async def can_complete(self, pid, *, lesson="onDemand"):
        progress = await self.progress(pid, lesson=lesson)
        return bool(progress and self.target is not None and progress.get("completed") is True)

    async def complete(self, pid, *, lesson="onDemand"):
        if self.finish_pending is None and not await self.can_complete(pid, lesson=lesson):
            if lesson == "automatic":
                async with self.lock:
                    if pid == self.pid and self.lesson == lesson:
                        await self.retire()
            return False
        async with self.lock:
            pending = self.finish_pending
            if pending is None:
                if pid != self.pid or self.run is None or lesson != self.lesson:
                    return False
                pending = {"pid": pid, "run": self.run, "lesson": lesson}
                self.finish_pending = pending
            if (
                pending["pid"] != pid
                or pending["lesson"] != lesson
                or not self.support.owns_practice(pid)
                or self.ending
                or self.end_unconfirmed
            ):
                return False
            if lesson != "automatic":
                saved = await self.host.ask(self.operation("finish"), pending["run"]) is True
                if saved:
                    self.finish_pending = None
                return saved
            result = None
            try:
                result = await self.host.ask(self.operation("finish"), pending["run"])
            except (OSError, EOFError, ValueError, TimeoutError):
                pass
            finally:
                # Even a rejected/timed-out Finish must synchronously stop new
                # publications before waiting for Runtime or native retirement.
                self.input.retire_practice(pending["run"], notify=False)
            saved = isinstance(result, dict) and result.get("saved") is True
            retry = (
                isinstance(result, dict)
                and result.get("retrySave") is True
                and self.finish_pending is pending
                and not saved
            )
            self.ending = True
            ended = saved or retry
            if not ended:
                try:
                    ended = await self.host.ask(self.operation("end"), pending["run"]) is True
                except (OSError, EOFError, ValueError, TimeoutError):
                    ended = False
            acknowledged = await self.input.confirm_practice_retirement(pending["run"])
            self.end_unconfirmed = not (ended and acknowledged)
            self.finish_pending = pending if retry and not self.end_unconfirmed else None
            if self.run == pending["run"]:
                self.pid = self.run = self.target = None
            self.ending = False
            return saved and not self.end_unconfirmed
