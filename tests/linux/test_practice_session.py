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
        self.retry_save = True
        self.finish_gate = None
        self.finish_started = asyncio.Event()
        self.finish_error = None
        self.end_gate = None
        self.end_allowed = True

    async def ask(self, operation, data=None):
        self.requests.append((operation, data))
        if operation in ("onDemandPracticeProgress", "automaticPracticeProgress"):
            return self.progress
        if operation == "beginOnDemandPractice" and self.begin_gate:
            await self.begin_gate.wait()
        if operation == "bindAutomaticPractice":
            self.progress = {**self.progress, **data}
        if operation == "finishAutomaticPractice":
            self.finish_started.set()
            if self.finish_gate:
                await self.finish_gate.wait()
            if self.finish_error:
                raise self.finish_error
            return {
                "saved": self.finish_allowed,
                "retrySave": not self.finish_allowed and self.retry_save,
            }
        if operation == "endAutomaticPractice":
            if self.end_gate:
                await self.end_gate.wait()
            return self.end_allowed
        return (
            self.finish_allowed
            if operation in ("finishOnDemandPractice", "finishAutomaticPractice")
            else True
        )


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
        self.connection = str(uuid.uuid4())
        self.generation = str(uuid.uuid4())
        self.authorization = None
        self.retired = None
        self.continuing = False
        self.foreground_fresh = True
        self.binding_reads = 0

    async def practice_target(self, pid, target=None):
        return dict(self.target) if self.available else None

    async def practice_binding(self, pid, target=None):
        self.binding_reads += 1
        return (
            {
                "target": dict(self.target),
                "connectionId": self.connection,
                "commandGeneration": self.generation,
            }
            if self.available
            else None
        )

    def practice_waiting(self, run, *, acknowledged=True, require_fresh=True):
        return self.continuing and self.retired == run

    def fresh(self):
        return self.foreground_fresh

    def prepare_practice(self, binding, owns_process):
        return True

    def authorize_practice(self, binding, owns_process):
        self.authorization = binding
        return True

    def retire_practice(self, run, *, notify=True):
        if self.authorization and self.authorization["runId"] == run:
            self.authorization = None
            self.retired = run
        return self.retired == run

    async def confirm_practice_retirement(self, run):
        return self.retired == run


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

    async def test_automatic_begin_cannot_authorize_an_unowned_process(self):
        self.assertFalse(await self.practice.begin(200, True, lesson="automatic"))
        self.assertEqual(self.host.requests, [])

    async def automatic(self):
        self.assertTrue(await self.practice.begin(100, True, lesson="automatic"))
        run = next(
            data for op, data in reversed(self.host.requests) if op == "beginAutomaticPractice"
        )
        self.host.progress = {"runId": run, "target": None, "completed": False}
        return run

    async def test_automatic_progress_binds_only_owned_current_connection_and_generation(self):
        run = await self.automatic()
        self.assertIsNone(self.input.authorization, "Begin granted write permission without a peer")
        self.assertIsNone(await self.practice.progress(200, lesson="automatic"))
        self.input.available = False
        self.assertIsNone((await self.practice.progress(100, lesson="automatic"))["target"])
        self.assertIsNone(self.input.authorization)
        self.input.available = True
        progress = await self.practice.progress(100, lesson="automatic")
        self.assertEqual(progress["runId"], run)
        self.assertEqual(
            self.input.authorization,
            {
                "runId": run,
                "target": self.input.target,
                "connectionId": self.input.connection,
                "commandGeneration": self.input.generation,
            },
        )
        self.assertFalse(await self.practice.complete(100, lesson="automatic"))
        self.assertIsNone(self.input.authorization)
        self.assertIsNone(self.practice.finish_pending)
        self.assertIsNone(
            await self.practice.progress(100), "F9/F10 progress adopted an automatic run"
        )

    async def test_continuity_waits_for_new_capture_and_only_then_binds_the_same_run(self):
        run = await self.automatic()
        await self.practice.progress(100, lesson="automatic")
        first = self.input.authorization
        self.input.retire_practice(run, notify=False)
        self.input.continuing = True
        self.input.available = False
        self.assertIsNotNone(await self.practice.progress(100, lesson="automatic"))
        self.host.progress = {**self.host.progress, "awaitingFreshSource": True}
        self.assertIsNotNone(await self.practice.progress(100, lesson="automatic"))
        self.assertIsNone(self.input.authorization)
        self.input.foreground_fresh = False
        reads = self.input.binding_reads
        self.assertIsNotNone(await self.practice.progress(100, lesson="automatic"))
        self.assertEqual(self.input.binding_reads, reads)
        self.input.foreground_fresh = True
        self.input.available = True
        self.input.generation = str(uuid.uuid4())
        await self.practice.progress(100, lesson="automatic")
        self.assertEqual(self.input.authorization["runId"], run)
        self.assertNotEqual(
            self.input.authorization["commandGeneration"], first["commandGeneration"]
        )
        self.assertEqual(sum(name == "beginAutomaticPractice" for name, _ in self.host.requests), 1)

    async def test_automatic_restart_waits_for_native_ack_and_failed_ack_blocks_future_authority(
        self,
    ):
        await self.automatic()
        await self.practice.progress(100, lesson="automatic")
        self.host.end_gate = asyncio.Event()
        self.host.end_allowed = False
        restart = asyncio.create_task(self.practice.begin(100, False, lesson="automatic"))
        await asyncio.sleep(0)
        self.assertTrue(self.practice.ending)
        self.assertIsNone(self.input.authorization)
        self.assertFalse(await self.practice.begin(100, False, lesson="automatic"))
        self.host.end_gate.set()
        self.assertFalse(await restart)
        self.assertTrue(self.practice.end_unconfirmed)
        self.assertFalse(await self.practice.begin(100, True, lesson="automatic"))
        self.assertFalse(await self.practice.complete(100, lesson="automatic"))
        self.assertEqual(sum(op == "beginAutomaticPractice" for op, _ in self.host.requests), 1)

    async def test_automatic_finish_retries_only_completed_evidence_after_retirement(self):
        run = await self.automatic()
        await self.practice.progress(100, lesson="automatic")
        self.host.progress = {**self.host.progress, "completed": True}
        self.host.finish_allowed = False
        self.assertFalse(await self.practice.complete(100, lesson="automatic"))
        self.input.retire_practice(run)
        await self.practice.retire_invalidated(run)
        self.host.progress = None
        self.assertIsNone(await self.practice.progress(100, lesson="automatic"))
        self.assertIsNone(self.input.authorization)
        self.host.finish_allowed = True
        self.assertTrue(await self.practice.complete(100, lesson="automatic"))
        self.assertIsNone(self.input.authorization)
        self.assertEqual(sum(op == "bindAutomaticPractice" for op, _ in self.host.requests), 1)

    async def test_automatic_finish_refusal_or_timeout_retires_without_save_retry(self):
        for failure in ("completion-changed", "timeout"):
            with self.subTest(failure=failure):
                self.setUp()
                run = await self.automatic()
                await self.practice.progress(100, lesson="automatic")
                self.host.progress = {**self.host.progress, "completed": True}
                self.host.finish_gate = asyncio.Event()
                finishing = asyncio.create_task(self.practice.complete(100, lesson="automatic"))
                await asyncio.wait_for(self.host.finish_started.wait(), 1)
                # Runtime can receive BeginEdit after the completed progress
                # read; it then rejects Finish rather than failing persistence.
                self.host.progress = {**self.host.progress, "completed": False}
                self.host.finish_allowed = self.host.retry_save = False
                if failure == "timeout":
                    self.host.finish_error = TimeoutError("held-finish")
                self.host.finish_gate.set()
                self.assertFalse(await asyncio.wait_for(finishing, 1))
                self.assertIsNone(self.input.authorization)
                self.assertIsNone(self.practice.finish_pending)
                self.assertIsNone(self.practice.run)
                self.assertIn(("endAutomaticPractice", run), self.host.requests)
                self.assertFalse(self.practice.end_unconfirmed)

    async def test_final_host_check_rejects_a_settings_change_after_progress_read(self):
        await self.start(completed=True)
        self.host.finish_allowed = False
        self.assertFalse(await self.practice.complete(100))


if __name__ == "__main__":
    unittest.main()
