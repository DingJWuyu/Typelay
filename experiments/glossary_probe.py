"""Throwaway Qwen whole-sentence probe; never integrate into the TypeLay product."""

import argparse
import hashlib
import json
import os
import platform
import re
import resource
import shutil
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

MODEL = "Qwen/Qwen2.5-0.5B-Instruct"
REVISION = "7ae557604adf67be50417f59c2c2f167def9a775"
WEIGHT_SIZE = 988097824
WEIGHT_SHA256 = "fdf756fa7fcbe7404d5c60e26bff1a0c8b8aa1f72ced49e7dd0210fe288fb7fe"
RUNTIME = dict(device="cpu", compute_type="int8", intra_threads=2, inter_threads=1)
EXPLANATION = re.compile(
    r"^(sure\b|here(?:'s| is)\b|translation:|note:|explanation:|翻译[：:]|译文[：:]|说明[：:])",
    re.I | re.M,
)
COPY_FILES = ["tokenizer.json", "tokenizer_config.json", "vocab.json", "merges.txt", "LICENSE"]
FILES = ["config.json", "generation_config.json", "model.safetensors", *COPY_FILES]
CASES = [
    ("项目方确认了预算。", "zh", "en", None),
    ("我们已经把预算发给项目方。", "zh", "en", None),
    ("请不要把这个 PR 发给项目方。", "zh", "en", None),
    ("项目方没有批准这个 PR。", "zh", "en", None),
    ("项目方说 TypeLay 不需要修改。", "zh", "en", None),
    ("明天由项目方确认，而不是我们。", "zh", "en", None),
    ("请让项目方和开发团队一起检查 TypeLay。", "zh", "en", None),
    (
        "请项目方查看 https://example.test/TypeLay/PR ，联系 @TypeLay，并检查 #123。\n不要修改 PR。",
        "zh",
        "en",
        None,
    ),
    ("Please ask the client team to review this PR in TypeLay.", "en", "zh", None),
    ("Do not send this PR to the client team.", "en", "zh", None),
    ("TypeLay and TypeLayer differ; this PR is APPROVED.", "en", "zh", None),
    ("然后请项目方检查 TypeLay。", "zh", "en", "My deliberate wording: 项目方 / TypeLay / PR."),
]
STYLE_INPUT = "请在周五前回复这封邮件，确认你是否可以参加下周的项目会议。"
STYLES = {
    "formal": "formal",
    "concise": "concise without omitting meaning",
    "casual": "conversational",
}


def save(output, report):
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")


def file_info(path):
    with path.open("rb") as stream:
        return {
            "bytes": path.stat().st_size,
            "sha256": hashlib.file_digest(stream, "sha256").hexdigest(),
        }


def cpu_info():
    blocks = Path("/proc/cpuinfo").read_text().strip().split("\n\n")
    cpus = [
        dict(line.split(":", 1) for line in block.splitlines() if ":" in line) for block in blocks
    ]
    cpus = [{key.strip(): value.strip() for key, value in cpu.items()} for cpu in cpus]
    cores = {
        (cpu["physical id"], cpu["core id"])
        for cpu in cpus
        if "physical id" in cpu and "core id" in cpu
    }
    return {
        "modelNames": sorted({cpu.get("model name", "unknown") for cpu in cpus}),
        "physicalCores": len(cores) or None,
        "logicalCpus": os.cpu_count(),
        "availableLogicalCpus": len(os.sched_getaffinity(0)),
        "platform": platform.platform(),
    }


def download(root, stage, report, output):
    snapshot = root / "snapshot"
    snapshot.mkdir(parents=True, exist_ok=True)
    disk_free = shutil.disk_usage(snapshot.parent).free
    meminfo = Path("/proc/meminfo").read_text().splitlines()
    memory_available = (
        int(next(line for line in meminfo if line.startswith("MemAvailable:")).split()[1]) * 1024
    )
    stage["preflight"] = {
        "diskFreeBytes": disk_free,
        "memoryAvailableBytes": memory_available,
        "diskBudgetBytes": 8 * 1024**3,
        "memoryBudgetBytes": 6 * 1024**3,
        "diskBudgetMet": disk_free >= 8 * 1024**3,
        "memoryBudgetMet": memory_available >= 6 * 1024**3,
    }
    save(output, report)
    if not stage["preflight"]["diskBudgetMet"]:
        raise RuntimeError("insufficient-disk-budget")
    stage["files"] = {}
    for name in FILES:
        url = f"https://huggingface.co/{MODEL}/resolve/{REVISION}/{name}"
        temporary = snapshot / (name + ".part")
        with urllib.request.urlopen(url, timeout=120) as response, temporary.open("wb") as stream:
            shutil.copyfileobj(response, stream, length=1024 * 1024)
        info = file_info(temporary)
        if name == "model.safetensors" and info != {"bytes": WEIGHT_SIZE, "sha256": WEIGHT_SHA256}:
            raise ValueError("model-weight-integrity-mismatch")
        temporary.replace(snapshot / name)
        stage["files"][name] = {"url": url, **info}
        save(output, report)


def convert(root, stage):
    # Official v4.8.2 module exposes __main__; no remote code or low-memory/accelerate path.
    command = [sys.executable, "-m", "ctranslate2.converters.transformers"]
    command += ["--model", str(root / "snapshot"), "--quantization", "int8"]
    command += ["--output_dir", str(root / "ct2"), "--copy_files", *COPY_FILES]
    stage["command"] = command
    process = subprocess.Popen(command)
    _, status, usage = os.wait4(process.pid, 0)
    process.returncode = os.waitstatus_to_exitcode(status)
    stage.update({"childPeakRssKiB": usage.ru_maxrss, "returnCode": process.returncode})
    if process.returncode:
        raise subprocess.CalledProcessError(process.returncode, command)
    stage["files"] = {p.name: file_info(p) for p in sorted((root / "ct2").iterdir()) if p.is_file()}


def infer(root, stage, report, output):
    cold_started = time.perf_counter()
    import ctranslate2
    import transformers

    tokenizer = transformers.AutoTokenizer.from_pretrained(
        root / "snapshot", local_files_only=True, trust_remote_code=False
    )
    generator = ctranslate2.Generator(str(root / "ct2"), **RUNTIME)
    stage["coldLoadMs"] = round((time.perf_counter() - cold_started) * 1000, 3)
    stage["coldLoadScope"] = (
        "Dependency imports, local tokenizer and CT2 model load; OS cache not flushed."
    )
    stage["versions"] = {
        "python": platform.python_version(),
        "ctranslate2": ctranslate2.__version__,
        "transformers": transformers.__version__,
    }
    eos_ids = json.loads((root / "snapshot/generation_config.json").read_text())["eos_token_id"]
    if isinstance(eos_ids, int):
        eos_ids = [eos_ids]
    eos_tokens = tokenizer.convert_ids_to_tokens(eos_ids)
    stage["eosTokenIds"] = eos_ids
    stage["eosTokens"] = eos_tokens
    stage["actualComputeType"] = generator.compute_type
    report["runs"] = []
    jobs = [
        (f"{i:02}", case, variant, None)
        for i, case in enumerate(CASES, 1)
        for variant in ("baseline", "glossary")
    ]
    jobs += [("style", (STYLE_INPUT, "zh", "en", None), "style", style) for style in STYLES]
    save(output, report)
    for case_id, (text, source, target, prefix), variant, style in jobs:
        languages = {"zh": "Chinese", "en": "English"}
        system = (
            f"Translate the user's complete text from {languages[source]} to {languages[target]}. "
            "Return only the translation, without explanations or headings. Preserve meaning and "
            "negation. Preserve URLs, @mentions, #references and line breaks."
        )
        if variant == "glossary":
            system += " Terminology rules: preserve the exact standalone terms TypeLay and PR. Do not apply these rules inside longer words."
            if (source, target) == ("zh", "en"):
                system += " Translate 项目方 as client team."
        if style:
            system += f" Use {STYLES[style]} wording."
        # The manual prefix is never included in messages or rewritten by the model.
        messages = [{"role": "system", "content": system}, {"role": "user", "content": text}]
        started = time.perf_counter()
        prompt_ids = tokenizer.apply_chat_template(
            messages, tokenize=True, add_generation_prompt=True
        )
        prompt_tokens = tokenizer.convert_ids_to_tokens(prompt_ids)
        result = generator.generate_batch(
            [prompt_tokens],
            max_length=256,
            beam_size=1,
            sampling_topk=1,
            end_token=eos_tokens,
            return_end_token=True,
            include_prompt_in_result=False,
        )[0]
        generated_ids = tokenizer.convert_tokens_to_ids(result.sequences[0])
        raw = tokenizer.decode(
            generated_ids, skip_special_tokens=False, clean_up_tokenization_spaces=False
        )
        generated = tokenizer.decode(
            generated_ids, skip_special_tokens=True, clean_up_tokenization_spaces=False
        )
        elapsed_ms = round((time.perf_counter() - started) * 1000, 3)
        eos_reached = bool(generated_ids and generated_ids[-1] in eos_ids)
        merged = generated
        if prefix is not None:
            separator = "" if prefix[-1:].isspace() or generated[:1].isspace() else " "
            merged = prefix + separator + generated
        report["runs"].append(
            {
                "caseId": case_id,
                "variant": variant,
                "style": style,
                "source": source,
                "target": target,
                "input": text,
                "manualPrefix": prefix,
                "systemPrompt": system,
                "rawGeneratedText": raw,
                "generatedText": generated,
                "mergedText": merged,
                "promptTokens": len(prompt_ids),
                "generatedTokensIncludingEos": len(generated_ids),
                "eosReached": eos_reached,
                "hitMaxTokens": len(generated_ids) >= 256,
                "truncated": len(generated_ids) >= 256 and not eos_reached,
                "tokenizationThroughDecodeMs": elapsed_ms,
                "inputVerbatimInOutput": text in generated,
                "systemPromptVerbatimInOutput": system in generated,
                "chatRoleMarkerInOutput": "<|im_start|>" in raw,
                "explanationMarker": bool(EXPLANATION.search(generated)),
                "manualAssessment": "pending",
            }
        )
        stage["completedGenerations"] = len(report["runs"])
        stage["selfPeakRssKiB"] = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
        save(output, report)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--phase", choices=("download", "convert", "infer"), required=True)
    parser.add_argument("--root", type=Path, default=Path("artifacts/qwen-probe-runtime"))
    parser.add_argument("--output", type=Path, default=Path("artifacts/qwen-probe/results.json"))
    args = parser.parse_args()
    report = {
        "model": MODEL,
        "revision": REVISION,
        "runtime": RUNTIME,
        "scope": "Synthetic complete sentences; no product integration or quality claims.",
        "timingNote": "No TTFT. Sentence latency includes tokenization through decoding.",
        "rules": {"global": {"TypeLay": "TypeLay", "PR": "PR"}, "zh-en": {"项目方": "client team"}},
        "generation": dict(max_length=256, beam_size=1, sampling_topk=1),
        "qualityVerdict": "Human review pending; echo/explanation marker checks are incomplete.",
        "stages": {},
    }
    stage = {"status": "running"}
    report["stages"][args.phase] = stage
    started = time.perf_counter()
    try:
        if args.output.exists() and args.phase != "download":
            report = json.loads(args.output.read_text())
            report["stages"][args.phase] = stage
        if sys.platform != "linux":
            raise RuntimeError("linux-required-for-rss-measurements")
        stage["cpu"] = cpu_info()
        save(args.output, report)
        if args.phase == "download":
            download(args.root, stage, report, args.output)
        else:
            os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1")
            os.environ.update(
                OMP_NUM_THREADS="2", MKL_NUM_THREADS="2", TOKENIZERS_PARALLELISM="false"
            )
            previous = "download" if args.phase == "convert" else "convert"
            if report["stages"].get(previous, {}).get("status") != "completed":
                raise RuntimeError("previous-phase-not-completed")
            if args.phase == "convert":
                convert(args.root, stage)
            else:
                infer(args.root, stage, report, args.output)
        stage["status"] = "completed"
    except Exception as error:
        stage.update(
            {"status": "failed", "errorType": type(error).__name__, "error": "probe-stage-failed"}
        )
        raise
    finally:
        stage["elapsedMs"] = round((time.perf_counter() - started) * 1000, 3)
        if args.phase == "infer":
            stage["selfPeakRssKiB"] = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
        save(args.output, report)


if __name__ == "__main__":
    main()
