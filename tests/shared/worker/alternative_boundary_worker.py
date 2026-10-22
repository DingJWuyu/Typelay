"""Explicit frozen-fixture observer, never a shipped or general-purpose worker.

Delegates inference and selection to the production worker. The default observer
is read-only; explicit ordinary comparison alters only repetition_penalty.
This test entry point is never shipped.
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
    def __init__(self, samples, ordinary_repetition=None):
        self.samples = samples
        self.ordinary_repetition = ordinary_repetition
        self.index = 0
        self.pending = None
        self.previous = None
        self.calls = []
        self.packages = []

    def accept(self, request):
        # Reject non-fixture content before it reaches a tokenizer or model.
        maximum = len(self.samples) * (1 if self.ordinary_repetition is not None else 2)
        if self.pending is not None or self.index > maximum:
            raise ValueError("fixture-sequence")
        expected = {"id": self.index + 1, "op": "hello"}
        if self.index:
            sample = self.samples[
                self.index - 1 if self.ordinary_repetition is not None else (self.index - 1) // 2
            ]
            source, target = sample["direction"].split("_")
            expected.update(text=sample["text"], source=source, target=target)
            expected["op"] = (
                "translate"
                if self.ordinary_repetition is not None or self.index % 2
                else "translate-alternative"
            )
            if self.ordinary_repetition is None and not self.index % 2:
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
                if observer.ordinary_repetition is not None:
                    observer.packages[-1]["translatorOptions"] = dict(kwargs)
                self.model = original(directory, *args, **kwargs)

            def translate_batch(self, source, **kwargs):
                if observer.pending is None or observer.pending["op"] == "hello":
                    raise ValueError("fixture-inference-only")
                # Only the explicit ordinary experiment may change one fixed option.
                if observer.ordinary_repetition is not None:
                    expected = dict(
                        beam_size=2,
                        max_input_length=0,
                        max_decoding_length=512,
                        repetition_penalty=1.05,
                    )
                    if kwargs != expected:
                        raise ValueError("ordinary-baseline-options-changed")
                    kwargs = {**kwargs, "repetition_penalty": observer.ordinary_repetition}
                # Capture the exact effective API options and unchanged results.
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
    parser.add_argument("--ordinary-repetition", type=float, choices=(1.05, 1.0))
    args = parser.parse_args()
    samples = frozen_samples()
    if args.ordinary_repetition is not None:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        from argos_repetition_comparison import holdout_samples

        samples += holdout_samples()
    observer = BoundaryObserver(samples, args.ordinary_repetition)
    sys.path.insert(0, str(WORKER.parent))
    import run_worker

    try:
        output = run_worker.isolate_protocol_output()
    except Exception:
        return 2
    try:
        with output:
            # The observer imports CT2 earlier than LocalEngine; isolate that import too.
            try:
                import ctranslate2
            except Exception:
                return 2

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
                return run_worker.main(output)
            finally:
                ctranslate2.Translator = original_translator
                run_worker.read_frame, run_worker.write_frame = original_read, original_write
                sys.argv = original_argv
    except OSError:
        return 5


if __name__ == "__main__":
    raise SystemExit(main())
