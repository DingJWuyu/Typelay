"""Explicit frozen-fixture observer, never a shipped or general-purpose worker.

Delegates inference and selection to the production worker unchanged. Only this
entry point instruments the external CTranslate2 API and adds diagnostic frames.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
WORKER = ROOT / "src/shared/TypeLay.Worker/run_worker.py"
INPUTS = Path(__file__).with_name("alternative_quality_inputs.json")
INPUT_SHA256 = "87452b28c414bc964afbd84542480dd46a65c0b4e15c42ad36422270413bcbe5"


def frozen_samples():
    raw = INPUTS.read_bytes()
    if hashlib.sha256(raw).hexdigest() != INPUT_SHA256:
        raise ValueError("frozen-input-sha256")
    samples = json.loads(raw)["samples"]
    if len(samples) != 16:
        raise ValueError("frozen-input-count")
    return samples


class BoundaryObserver:
    def __init__(self, samples):
        self.samples = samples
        self.index = 0
        self.pending = None
        self.previous = None
        self.calls = []
        self.packages = []

    def accept(self, request):
        # Reject non-fixture content before it reaches a tokenizer or model.
        if self.pending is not None or self.index > 32:
            raise ValueError("fixture-sequence")
        expected = {"id": self.index + 1, "op": "hello"}
        if self.index:
            sample = self.samples[(self.index - 1) // 2]
            source, target = sample["direction"].split("_")
            expected.update(text=sample["text"], source=source, target=target)
            expected["op"] = "translate" if self.index % 2 else "translate-alternative"
            if not self.index % 2:
                expected["previousCandidate"] = self.previous
        if request != expected:
            raise ValueError("fixed-fixture-only")
        self.pending = request.copy()
        self.calls = []
        return request

    def response(self, response):
        if self.pending is None or response.get("id") != self.pending["id"]:
            raise ValueError("fixture-response")
        if self.pending["op"] == "translate":
            self.previous = response.get("text")
        result = dict(response)
        result["decodingBoundary"] = {
            "inputSha256": INPUT_SHA256,
            "calls": self.calls,
            "packages": self.packages if self.index == 0 else [],
        }
        self.pending = None
        self.index += 1
        return result

    def translator_factory(self, original):
        observer = self

        class Translator:
            def __init__(self, directory, *args, **kwargs):
                # Production inventory verification happens before constructing CT2.
                root = Path(directory).parent
                with (root / "metadata.json").open("rb") as stream:
                    raw = stream.read(16385)
                if len(raw) > 16384:
                    raise ValueError("model-metadata")
                metadata = json.loads(raw)
                observer.packages.append(
                    {
                        "metadataSha256": hashlib.sha256(raw).hexdigest(),
                        "metadata": {
                            key: metadata.get(key)
                            for key in ("from_code", "to_code", "package_version", "target_prefix")
                        },
                        "targetPrefixPresent": "target_prefix" in metadata,
                        "sentencepieceSha256": hashlib.sha256(
                            (root / "sentencepiece.model").read_bytes()
                        ).hexdigest(),
                    }
                )
                self.model = original(directory, *args, **kwargs)

            def translate_batch(self, source, **kwargs):
                if observer.pending is None or observer.pending["op"] == "hello":
                    raise ValueError("fixture-inference-only")
                # Copy before calling the external API; do not modify options or results.
                call = {
                    "sourcePieces": [list(pieces) for pieces in source],
                    "options": dict(kwargs),
                }
                observer.calls.append(call)
                results = self.model.translate_batch(source, **kwargs)
                call["results"] = [
                    {
                        "hypotheses": [list(tokens) for tokens in result.hypotheses],
                        "joinedHypotheses": [
                            "".join(tokens).replace("\u2581", " ").strip()
                            for tokens in result.hypotheses
                        ],
                    }
                    for result in results
                ]
                return results

            def unload_model(self):
                return self.model.unload_model()

        return Translator


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--fixed-fixture", action="store_true", required=True)
    parser.add_argument("--models", type=Path, required=True)
    args = parser.parse_args()
    observer = BoundaryObserver(frozen_samples())
    sys.path.insert(0, str(WORKER.parent))
    import ctranslate2
    import run_worker

    original_translator = ctranslate2.Translator
    original_read, original_write = run_worker.read_frame, run_worker.write_frame
    original_argv = sys.argv
    try:
        ctranslate2.Translator = observer.translator_factory(original_translator)
        run_worker.read_frame = lambda stream: observer.accept(original_read(stream))
        run_worker.write_frame = lambda stream, value: original_write(
            stream, observer.response(value)
        )
        sys.argv = [str(WORKER), "--models", str(args.models)]
        return run_worker.main()
    finally:
        ctranslate2.Translator = original_translator
        run_worker.read_frame, run_worker.write_frame = original_read, original_write
        sys.argv = original_argv


if __name__ == "__main__":
    raise SystemExit(main())
