"""Invoked as python -I -B run_worker.py --models <private model folder>."""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

# Isolated mode ignores user site packages and PYTHONPATH. This package is an explicit code input.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from typelay_worker.engine import LocalEngine
from typelay_worker.protocol import read_frame, write_frame


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--self-check", action="store_true")
    args = parser.parse_args()
    try:
        engine = LocalEngine(args.models)
    except Exception:
        if args.self_check:
            print(json.dumps({"status": "failed", "code": "dependency-or-model-load"}))
        # No stack traces or exception messages cross the privacy boundary.
        return 2
    if args.self_check:
        samples = [
            ("你好，我想确认一下明天下午的会议时间。", "zh", "en"),
            ("Please send me the final version.", "en", "zh"),
        ]
        results = []
        for text, source, target in samples:
            start = time.perf_counter()
            result = engine.translate(text, source, target)
            results.append(
                {
                    "direction": source + "_" + target,
                    "status": result.status,
                    "nonempty": bool(result.text),
                    "differs": result.text != text,
                    "elapsedMs": round((time.perf_counter() - start) * 1000, 2),
                }
            )
        passed = all(
            r["status"] == "translated" and r["nonempty"] and r["differs"] for r in results
        )
        print(
            json.dumps(
                {"status": "passed" if passed else "failed", "device": "cpu", "results": results}
            )
        )
        return 0 if passed else 3
    last_id = 0
    while True:
        try:
            request = read_frame(sys.stdin.buffer)
            request_id = request.get("id")
            if type(request_id) is not int or request_id <= last_id:
                return 4
            last_id = request_id
            start = time.perf_counter()
            operation = request.get("op")
            if operation == "hello":
                response = {"id": request_id, "status": "ready"}
            elif operation == "translate":
                result = engine.translate(
                    request.get("text"),
                    request.get("source"),
                    request.get("target"),
                    request.get("preserved"),
                )
                response = {
                    "id": request_id,
                    "status": result.status,
                    "text": result.text,
                    "code": result.code,
                }
            else:
                response = {"id": request_id, "status": "error", "code": "unknown-operation"}
            response["elapsedMs"] = round((time.perf_counter() - start) * 1000, 2)
            write_frame(sys.stdout.buffer, response)
            # Do not keep the last request/result alive while waiting for the next pipe frame.
            request.clear()
            response.clear()
            if operation == "translate":
                del result
        except EOFError:
            return 0
        except (ValueError, UnicodeError, TypeError, RecursionError):
            return 4
        except Exception:
            return 5


if __name__ == "__main__":
    raise SystemExit(main())
