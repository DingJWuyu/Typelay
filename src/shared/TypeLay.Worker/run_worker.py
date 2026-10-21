"""Invoked as python -I -B run_worker.py --models <private model folder>."""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

# Isolated mode ignores user site packages and PYTHONPATH. This package is an explicit code input.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from typelay_worker.engine import LocalEngine, Result
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
    try:
        return serve(engine, args.self_check)
    finally:
        engine.close()


def serve(engine, self_check):
    if self_check:
        results = []
        for direction in engine.self_check_directions:
            if direction.id not in engine.directions:
                continue
            text, source, target = direction.self_check_text, direction.source, direction.target
            start = time.perf_counter()
            result = engine.translate(text, source, target)
            results.append(
                {
                    "direction": direction.id,
                    "status": result.status,
                    "nonempty": bool(result.text),
                    "differs": result.text != text,
                    "elapsedMs": round((time.perf_counter() - start) * 1000, 2),
                }
            )
        passed = bool(results) and all(
            r["status"] == "translated" and r["nonempty"] and r["differs"] for r in results
        )
        report = {"status": "passed" if passed else "failed", "device": "cpu", "results": results}
        if engine.schema == 3:
            report.update(packages=engine.packages, directions=engine.directions)
        print(json.dumps(report))
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
                response = {"id": request_id, "status": "ready", "directions": engine.directions}
                if engine.packages:
                    response["packages"] = engine.packages
                if engine.styled_translation_version is not None:
                    response["styledTranslationVersion"] = engine.styled_translation_version
                if engine.glossary_translation_version is not None:
                    response["glossaryTranslationVersion"] = engine.glossary_translation_version
                if engine.styled_glossary_translation_version is not None:
                    response["styledGlossaryTranslationVersion"] = (
                        engine.styled_glossary_translation_version
                    )
            elif operation in (
                "translate",
                "translate-alternative",
                "translate-styled-v1",
                "translate-glossary-v1",
                "translate-styled-glossary-v1",
            ):
                if operation == "translate-styled-glossary-v1":
                    if set(request) - {
                        "id",
                        "op",
                        "text",
                        "source",
                        "target",
                        "preserved",
                        "style",
                        "glossary",
                    }:
                        result = Result("error", code="invalid-request")
                    else:
                        result = engine.translate_styled_glossary(
                            request.get("text"),
                            request.get("source"),
                            request.get("target"),
                            request.get("style"),
                            request.get("glossary"),
                            request.get("preserved"),
                        )
                elif operation == "translate-glossary-v1":
                    if set(request) - {
                        "id",
                        "op",
                        "text",
                        "source",
                        "target",
                        "preserved",
                        "glossary",
                    }:
                        result = Result("error", code="invalid-request")
                    else:
                        result = engine.translate_glossary(
                            request.get("text"),
                            request.get("source"),
                            request.get("target"),
                            request.get("glossary"),
                            request.get("preserved"),
                        )
                elif "glossary" in request or "terms" in request:
                    result = Result("error", code="invalid-request")
                elif operation == "translate-styled-v1":
                    if set(request) - {
                        "id",
                        "op",
                        "text",
                        "source",
                        "target",
                        "style",
                        "preserved",
                    }:
                        result = Result("error", code="invalid-request")
                    else:
                        result = engine.translate_styled(
                            request.get("text"),
                            request.get("source"),
                            request.get("target"),
                            request.get("style"),
                            request.get("preserved"),
                        )
                elif "style" in request:
                    result = Result("error", code="invalid-request")
                elif operation == "translate-alternative":
                    result = engine.translate_alternative(
                        request.get("text"),
                        request.get("source"),
                        request.get("target"),
                        request.get("previousCandidate"),
                        request.get("preserved"),
                    )
                else:
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
            if operation in (
                "translate",
                "translate-alternative",
                "translate-styled-v1",
                "translate-glossary-v1",
                "translate-styled-glossary-v1",
            ):
                del result
        except EOFError:
            return 0
        except (ValueError, UnicodeError, TypeError, RecursionError):
            return 4
        except Exception:
            return 5


if __name__ == "__main__":
    raise SystemExit(main())
