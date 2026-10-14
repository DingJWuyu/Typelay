"""Linux practice/control coordination with controlled Host and input ports.

These exercise ownership, run retirement and metadata routing, not Runtime's
translation success, native key routing, a model, or a GTK desktop.
"""

import asyncio
import sys
import unittest
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from practice_session import PracticeSession  # noqa: E402


class Host:
    def __init__(self):
        self.requests = []
        self.progress = None
        self.begin_gate = None
        self.finish_allowed = True

    async def ask(self, operation, data=None):
        self.requests.append((operation, data))
        if operation == "onDemandPracticeProgress":
            return self.progress
        if operation == "beginOnDemandPractice" and self.begin_gate:
            await self.begin_gate.wait()
        return self.finish_allowed if operation == "finishOnDemandPractice" else True


class Ownership:
    def __init__(self):
        self.pid = 100

    def owns_practice(self, pid):
        return pid is not None and pid == self.pid

    def release_practice(self, pid):
        if pid == self.pid:
            self.pid = None


class InputPort:
    def __init__(self):
        self.target = {
            "backend": "linux-gtk3",
            "value": str(uuid.uuid4()),
            "lifetime": str(uuid.uuid4()),
        }
        self.available = True

    async def practice_target(self, pid, target=None):
        return dict(self.target) if self.available else None


class PracticeSessionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.host, self.input, self.owner = Host(), InputPort(), Ownership()
        self.practice = PracticeSession(self.host, self.input, self.owner)

    async def start(self, *, completed=False):
        self.assertTrue(await self.practice.begin(100, False))
        run = next(
            data
            for operation, data in reversed(self.host.requests)
            if operation == "beginOnDemandPractice"
        )
        self.host.progress = {
            "runId": run,
            "target": dict(self.input.target),
            "commandGeneration": str(uuid.uuid4()),
            "commandSequence": 2,
            "revision": 3,
            "previewCompleted": completed,
            "directReplacementCompleted": completed,
            "completed": completed,
        }
        return run

    async def test_only_owned_process_can_begin_bind_or_complete(self):
        self.assertFalse(await self.practice.begin(200, True))
        self.assertEqual(self.host.requests, [])
        await self.start()
        self.assertIsNone(await self.practice.progress(200))
        self.assertFalse(await self.practice.complete(200))
        self.assertFalse(await self.practice.complete(100))
        self.assertIsNotNone(await self.practice.progress(100))
        binding = next(
            data for operation, data in self.host.requests if operation == "bindOnDemandPractice"
        )
        self.assertEqual(binding["target"], self.input.target)
        self.host.progress = {
            **self.host.progress,
            "previewCompleted": True,
            "directReplacementCompleted": True,
            "completed": True,
        }
        self.assertTrue(await self.practice.complete(100))

    async def test_settings_retirement_and_old_progress_cannot_be_revived_by_poll(self):
        old = await self.start(completed=True)
        self.assertTrue(await self.practice.can_complete(100))
        self.host.progress = None  # The real Runtime's settings invalidation response.
        self.assertIsNone(await self.practice.progress(100))
        self.host.progress = {"runId": old, "target": self.input.target, "completed": True}
        self.assertIsNone(await self.practice.progress(100))
        self.assertFalse(await self.practice.complete(100))
        self.assertEqual(sum(op == "beginOnDemandPractice" for op, _ in self.host.requests), 1)

    async def test_restart_target_change_exit_and_reopen_reject_old_completion(self):
        old = await self.start(completed=True)
        self.assertTrue(await self.practice.can_complete(100))
        await self.practice.begin(100, False)
        self.assertFalse(await self.practice.complete(100), "old run's receipt completed a restart")
        self.assertIn(("endOnDemandPractice", old), self.host.requests)
        await self.start(completed=True)
        self.assertTrue(await self.practice.can_complete(100))
        self.input.target = {**self.input.target, "value": str(uuid.uuid4())}
        self.assertFalse(await self.practice.complete(100))
        reopened = PracticeSession(self.host, self.input, self.owner)
        self.assertIsNone(await reopened.progress(100))
        await self.start(completed=True)
        self.owner.release_practice(100)
        self.assertFalse(await self.practice.complete(100))

    async def test_close_during_pending_begin_does_not_attach_a_new_run(self):
        self.host.begin_gate = asyncio.Event()
        start = asyncio.create_task(self.practice.begin(100, False))
        await asyncio.sleep(0)
        close = asyncio.create_task(self.practice.end(100))
        await asyncio.sleep(0)
        self.host.begin_gate.set()
        self.assertFalse(await start)
        self.assertTrue(await close)
        self.assertIsNone(await self.practice.progress(100))

    async def test_final_host_check_rejects_a_settings_change_after_progress_read(self):
        await self.start(completed=True)
        self.host.finish_allowed = False
        self.assertFalse(await self.practice.complete(100))


if __name__ == "__main__":
    unittest.main()
