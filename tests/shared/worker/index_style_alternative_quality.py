"""One frozen 16-source Style/Alternative decision, not a prompt search or verdict."""

import hashlib
import json
import re
import time
from pathlib import Path

import index_quality
import styled_quality
from typelay_worker.engine import PROTECTED
from typelay_worker.index_assets import PACKAGE_ID, VERSION, read_json
from typelay_worker.protocol import valid_text

INPUTS = Path(__file__).with_name("index_style_alternative_inputs.json")
INPUT_SHA256 = "419606bdabec75a10df5de8b858a276fc8477b74d63ba561a7826bf2d070b96f"
PROFILE = styled_quality.ROOT / "src/shared/TypeLay.Runtime/IndexInferenceProfile.json"
PROFILE_SHA256 = "e57ecfbfd4681b174c91dfc38e93e5f4dd3dd3802d158d7626ff0c95e650e0b6"
PROFILE_ID = "index-v1-3b99612f3676f5ac8ef34c81e99bf8874a5ba5271207a15d39e947c9d42ef87d"
VARIANTS = ("ordinary", "natural", "formal", "alternative")
CAPABILITIES = ("styledTranslationVersion", "alternativeTranslationVersion")


def fixed_json(path, checksum):
    raw = path.read_bytes()
    if hashlib.sha256(raw).hexdigest() != checksum:
        raise styled_quality.CollectionError("fixed-decision-input-identity")
    return json.loads(raw)


def prepare_report():
    started = time.monotonic()
    document = fixed_json(INPUTS, INPUT_SHA256)
    if (
        set(document) != {"schema", "samples"}
        or type(document["schema"]) is not int
        or document["schema"] != 1
        or type(document["samples"]) is not list
        or len(document["samples"]) != 16
    ):
        raise styled_quality.CollectionError("fixed-decision-input-schema")
    for index, case in enumerate(document["samples"]):
        category = "utility" if index < 8 else "neutral" if index < 12 else "protected"
        if (
            set(case) != {"id", "direction", "category", "text", "semantic_anchors", "rationale"}
            or case["id"] != f"S{index + 1:02}"
            or case["direction"] != index_quality.DIRECTIONS[index % 2]
            or case["category"] != category
            or not valid_text(case["text"])
            or not valid_text(case["rationale"])
            or type(case["semantic_anchors"]) is not list
            or not case["semantic_anchors"]
            or not all(valid_text(a) for a in case["semantic_anchors"])
            or bool(PROTECTED.search(case["text"])) != (category == "protected")
        ):
            raise styled_quality.CollectionError("fixed-decision-case-schema")
    report = styled_quality.collection_report(INPUT_SHA256, 64, native="index")
    profile = fixed_json(PROFILE, PROFILE_SHA256)
    if (
        profile["id"] != PROFILE_ID
        or set(report["workerSourceSha256"])
        != set(profile["workerFiles"])
        | {"native/index/model.lock.json", "native/index/cpu-runtime.lock.json"}
        or any(
            report["workerSourceSha256"].get(name) != checksum
            for name, checksum in profile["workerFiles"].items()
        )
        or report["workerSourceSha256"]["native/index/model.lock.json"]
        != profile["modelLockSha256"]
    ):
        raise styled_quality.CollectionError("fixed-decision-worker-profile")
    report.update(
        method="production-index-style-alternative-fixed-sixteen",
        reviewedProfileId=PROFILE_ID,
        preparationStartedMonotonic=started,
        profileRecordSha256=PROFILE_SHA256,
        variants=list(VARIANTS),
        includeRefusalChecks=False,
        expectedPackage={"id": PACKAGE_ID, "version": VERSION},
        requiredCapabilities=dict.fromkeys(CAPABILITIES, 1),
        inputProvenance={
            "author": "independent-root",
            "preOutputRevision": 3,
            "priorUnexecutedSha256": "2dc3fcf0c7dec5b224065f4a6456856672d47355917eb57a8f6e714869ba4a6d",
            "priorSeparatedSourceSha256": "c791113b808735b3a499740f2d8d87eb337e0adeb5dc661660c94f7d3da27b49",
            "change": "v2 explicitly separates URLs; v3 keeps every source identical and clarifies if/window anchors. All before output; originals retained.",
        },
        reviewScope="Fixed four utility sources per direction; >=2 useful Style pairs and >=2 useful alternatives. "
        "All returned translations retain meaning redlines. Neutral no-alternative earns no benefit. "
        "Protected alternatives are boundary refusals, not translations. Human review only.",
        notTested=[
            "glossary",
            "styled-glossary",
            "other-directions",
            "Windows-language-quality",
            "release",
        ],
        cases=[
            {
                **case,
                "protectedTokens": PROTECTED.findall(case["text"]),
                "responses": dict.fromkeys(VARIANTS),
                "manualReview": dict.fromkeys(VARIANTS),
                "pairReview": None,
            }
            for case in document["samples"]
        ],
        refusals=[],
    )
    return report


def verify_build(args):
    profile = fixed_json(PROFILE, PROFILE_SHA256)
    native = args.worker.parent / "native/index"
    manifest = read_json(native / "production-adapter.json")
    cpu = read_json(native / "cpu-runtime.lock.json")
    cpu["files"] = sorted(cpu["files"], key=lambda item: item["file"])

    def canonical(value):
        return json.dumps(value, sort_keys=True, separators=(",", ":"))

    if (
        manifest.get("platform") != "linux-x86_64"
        or manifest.get("native_sources") != profile["nativeSources"]
        or manifest.get("compile_options") != profile["compileOptions"]["linux-x86_64"]
        or canonical(cpu) != canonical(profile["cpuLocks"]["linux-x86_64"])
    ):
        raise styled_quality.CollectionError("fixed-decision-build-profile")
    # These are immutable formal-installer records, not a fresh interpreter attestation.
    with (args.models / "installed.json").open("rb") as stream:
        inventory_bytes = stream.read(8 * 1024 * 1024 + 1)
    with (args.models.parent / "runtime/requirements.lock").open("rb") as stream:
        lock = stream.read(512 * 1024 + 1)
    if len(inventory_bytes) > 8 * 1024 * 1024 or len(lock) > 512 * 1024:
        raise styled_quality.CollectionError("fixed-decision-runtime-metadata-size")
    inventory = json.loads(inventory_bytes)
    requirements = [
        line.split("#", 1)[0].strip()
        for line in re.sub(r"\\\s*\n", " ", lock.decode()).splitlines()
    ]
    lingua = [line for line in requirements if line.startswith("lingua-language-detector")]
    if (
        inventory.get("pythonVersion") != "3.13.16"
        or inventory.get("requirementsSha256") != hashlib.sha256(lock).hexdigest()
        or len(lingua) != 1
        or re.fullmatch(
            r"lingua-language-detector==2\.1\.1(?:\s+--hash=sha256:[a-f0-9]{64})+", lingua[0]
        )
        is None
    ):
        raise styled_quality.CollectionError("fixed-decision-runtime-metadata")


async def collect_cases(process, report):
    await index_quality.collect_cases(
        process,
        report,
        variants=VARIANTS,
        capabilities=CAPABILITIES,
        allowed_errors=("structure-not-preserved", "alternative-unavailable"),
    )
    for case in report["cases"]:
        alternative = case["responses"]["alternative"]
        case["alternativeOutcome"] = (
            "dependency-not-run"
            if alternative is None
            else "expected-boundary-refusal"
            if alternative["status"] == "error" and case["category"] == "protected"
            else "unexpected-unavailable"
            if alternative["status"] == "error"
            else "neutral-no-alternative"
            if alternative["status"] == "no-alternative" and case["category"] == "neutral"
            else "utility-no-alternative-zero-benefit"
            if alternative["status"] == "no-alternative"
            else "translated-not-reviewed"
        )


async def collect(args, report):
    deadline = report["preparationStartedMonotonic"] + styled_quality.TOTAL_TIMEOUT
    verify_build(args)
    await styled_quality.collect(
        args,
        report,
        collect_cases=collect_cases,
        required_directions=index_quality.DIRECTIONS,
        capability="alternativeTranslationVersion",
        work_deadline=deadline,
        native="index",
    )
    if report["status"] == "completed" and (
        report.get("businessRefusals")
        or report.get("dependencyNotRun")
        or any(
            case.get("alternativeOutcome") == "unexpected-unavailable" for case in report["cases"]
        )
    ):
        report["status"] = "completed-with-refusals"


if __name__ == "__main__":
    raise SystemExit(styled_quality.main(prepare_report, collect, __doc__, native="index"))
