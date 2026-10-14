"""Bind the managed practice process to Runtime's metadata-only lesson progress.

The desktop, not a control-socket caller, chooses the run and exact native field.
No progress is persisted or adopted from an earlier desktop process.
"""

import asyncio
import uuid


class PracticeSession:
    def __init__(self, host, input_service, support):
        self.host, self.input, self.support = host, input_service, support
        self.lock = asyncio.Lock()
        self.pid = self.run = self.target = None

    async def retire(self):
        run = self.run
        self.pid = self.run = self.target = None
        if run is not None:
            await self.host.ask("endOnDemandPractice", run)

    async def begin(self, pid, confirmed_switch):
        async with self.lock:
            if not self.support.owns_practice(pid):
                return False
            await self.retire()
            if await self.host.ask("startOnDemandPractice", confirmed_switch) is not True:
                return False
            if not self.support.owns_practice(pid):
                return False
            run = str(uuid.uuid4())
            if await self.host.ask("beginOnDemandPractice", run) is not True:
                return False
            if not self.support.owns_practice(pid):
                await self.host.ask("endOnDemandPractice", run)
                return False
            self.pid, self.run = pid, run
            return True

    async def end(self, pid):
        # Closing while a begin RPC is pending must prevent that begin from
        # attaching after the window has gone. Never stop the managed process.
        self.support.release_practice(pid)
        async with self.lock:
            if self.pid == pid:
                await self.retire()
        return True

    async def progress(self, pid):
        async with self.lock:
            if self.run is None:
                return None
            if not self.support.owns_practice(self.pid):
                await self.retire()
                return None
            if pid != self.pid:
                return None
            progress = await self.host.ask("onDemandPracticeProgress")
            if not isinstance(progress, dict) or progress.get("runId") != self.run:
                # Settings changes retire the Runtime run. Polling cannot begin
                # or revive it; the user must explicitly start a new practice.
                self.pid = self.run = self.target = None
                return None
            target = await self.input.practice_target(pid, self.target)
            if not self.support.owns_practice(pid):
                await self.retire()
                return None
            if self.target is not None and target != self.target:
                await self.retire()
                return None
            if self.target is None and target is not None:
                if (
                    await self.host.ask(
                        "bindOnDemandPractice", {"runId": self.run, "target": target}
                    )
                    is not True
                ):
                    await self.retire()
                    return None
                self.target = target
                progress = await self.host.ask("onDemandPracticeProgress")
            if (
                not isinstance(progress, dict)
                or progress.get("runId") != self.run
                or progress.get("target") != self.target
            ):
                await self.retire()
                return None
            return progress

    async def can_complete(self, pid):
        progress = await self.progress(pid)
        return bool(progress and self.target is not None and progress.get("completed") is True)

    async def complete(self, pid):
        if not await self.can_complete(pid):
            return False
        # Runtime checks the run again and saves only the dismissal preference
        # atomically on its owner loop. A settings change in between rejects it.
        async with self.lock:
            if pid != self.pid or not self.support.owns_practice(pid) or self.run is None:
                return False
            return await self.host.ask("finishOnDemandPractice", self.run) is True
