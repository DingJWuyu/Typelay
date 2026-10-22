"""Real Linux process ownership for the fixed Argos A/B; no model inference."""

import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/shared/worker"))


class ProcessTests(unittest.TestCase):
    def test_two_real_owned_processes_keep_all_outputs_and_stop_after_failure(self):
        import asyncio
        import shutil
        from types import SimpleNamespace
        from unittest.mock import patch

        import alternative_quality as quality
        import argos_repetition_comparison as experiment

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            models = root / "models"
            models.mkdir()
            (models / "installed.json").write_text(
                json.dumps({"schema": 2, "directions": ["zh_en", "en_zh"], "files": []})
            )
            runtime = root / "runtime"
            python = runtime / "venv/bin/python"
            python.parent.mkdir(parents=True)
            shutil.copy2(sys.executable, python)
            (runtime / "venv/pyvenv.cfg").write_text(f"home = {Path(sys.base_prefix) / 'bin'}\n")
            (runtime / "requirements.lock").write_text("controlled subprocess, not model evidence")
            worker = root / "controlled_worker.py"
            worker.write_text("""import json,struct,sys,time
from pathlib import Path
while True:
 header=sys.stdin.buffer.read(4)
 if not header:
  if Path(__file__).with_name('nonzero').exists(): sys.exit(7)
  break
 request=json.loads(sys.stdin.buffer.read(struct.unpack('<i',header)[0]))
 hello=request['op']=='hello'
 if not hello and Path(__file__).with_name('timeout').exists(): time.sleep(10)
 response=dict(id=request['id'],status='ready' if hello else 'translated',text='' if hello else 'controlled-'+str(request['id']),code='',elapsedMs=1)
 if hello: response['directions']=['en_zh','zh_en']
 if not hello and Path(__file__).with_name('fail').exists(): response.update(status='error',code='controlled-error')
 response['decodingBoundary']=dict(inputSha256='87452b28c414bc964afbd84542480dd46a65c0b4e15c42ad36422270413bcbe5',packages=[{},{}] if hello else [],calls=[] if hello else [dict(sourcePieces=[['x']],results=[{}],options=dict(beam_size=2,max_input_length=0,max_decoding_length=512,repetition_penalty=float(sys.argv[sys.argv.index('--ordinary-repetition')+1])))])
 data=json.dumps(response).encode();sys.stdout.buffer.write(struct.pack('<i',len(data))+data);sys.stdout.buffer.flush()
""")
            args = SimpleNamespace(
                python=python,
                models=models,
                capture_decoding_boundary=True,
                request_timeout=1,
                total_timeout=600,
            )
            for failure in ("", "fail", "nonzero", "timeout"):
                for marker in ("fail", "nonzero", "timeout"):
                    (root / marker).unlink(missing_ok=True)
                if failure:
                    (root / failure).touch()
                args.request_timeout = 0.1 if failure == "timeout" else 1
                report = experiment.prepare_report(args)
                with patch.object(quality, "BOUNDARY_WORKER", worker):
                    asyncio.run(experiment.collect(args, report))
                first, second = report["variants"]
                self.assertTrue(first["workerExitConfirmed"])
                if failure not in ("nonzero", "timeout"):
                    self.assertEqual(first["workerExitCode"], 0)
                if failure:
                    self.assertEqual(report["status"], "failed")
                    self.assertEqual(second["workerStarts"], 0)
                    if failure == "fail":
                        self.assertEqual(first["requests"][-1]["code"], "controlled-error")
                    elif failure == "nonzero":
                        self.assertEqual(first["workerExitCode"], 7)
                        self.assertEqual(len(first["requests"]), 25)
                    else:
                        self.assertEqual(first["failureCode"], "timeout")
                else:
                    self.assertEqual(report["status"], "completed", report)
                    self.assertEqual(report["quality"], "not-reviewed")
                    self.assertEqual([len(v["requests"]) for v in report["variants"]], [25, 25])
                    self.assertLess(second["totalTimeoutSeconds"], first["totalTimeoutSeconds"])
                    self.assertEqual(first["cases"][-1]["ordinary"]["text"], "controlled-25")
                    self.assertTrue(second["workerExitConfirmed"])
            report = experiment.prepare_report(args)
            with patch.object(experiment, "TOTAL_SECONDS", 0):
                asyncio.run(experiment.collect(args, report))
            self.assertEqual(report["failureCode"], "comparison-budget-exhausted")
            self.assertEqual([v["workerStarts"] for v in report["variants"]], [0, 0])


if __name__ == "__main__":
    unittest.main()
