"""Check owned fixture exits; never turn a failed scene into a passing scene.

The default scope requires all three consumers before Hy. The fixed
before-notepad scope requires continuous and browser before Notepad starts.

The workflow caller must supply run/attempt/head from the current
Actions context and each nonce from that invocation's GITHUB_OUTPUT, NOT from
these reports. workflow-cancelled must come from the actual cancellation state;
the workflow also guards execution with !cancelled(). This checks
execution retirement only, not all resource reclamation or product acceptance.
"""

import argparse
import json
import re
import sys
from pathlib import Path

FIELDS = {
    "schemaVersion",
    "mode",
    "runId",
    "runAttempt",
    "head",
    "nonce",
    "rootProcessId",
    "rootExitCode",
    "rootKernelSignaled",
    "activeProcesses",
    "assignedBeforeResume",
    "interrupted",
    "cleanupMode",
    "failure",
    "noActiveOwnedConsumers",
}


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate-field")
        result[key] = value
    return result


def matches(pattern, value):
    return isinstance(value, str) and re.fullmatch(pattern, value) is not None


def valid_report(path, mode, nonce, args):
    # One bounded read; a partial atomic-publish failure is not evidence.
    report = Path(path)
    if not report.is_file():
        return False
    with report.open("rb") as stream:
        raw = stream.read(65537)
    if len(raw) > 65536:
        return False
    proof = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object)
    if not isinstance(proof, dict) or proof.keys() != FIELDS:
        return False
    return (
        type(proof["schemaVersion"]) is int
        and proof["schemaVersion"] == 1
        and proof["mode"] == mode
        and proof["runId"] == args.run_id
        and proof["runAttempt"] == args.run_attempt
        and proof["head"] == args.head
        and proof["nonce"] == nonce
        and type(proof["rootProcessId"]) is int
        and 0 < proof["rootProcessId"] < 2**32
        and type(proof["rootExitCode"]) is int
        and -(2**31) <= proof["rootExitCode"] < 2**31
        and proof["rootKernelSignaled"] is True
        and type(proof["activeProcesses"]) is int
        and proof["activeProcesses"] == 0
        and proof["assignedBeforeResume"] is True
        and proof["interrupted"] is False
        and proof["cleanupMode"] in ("natural", "terminated")
        and proof["failure"] is None
        and proof["noActiveOwnedConsumers"] is True
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scope", choices=("all", "before-notepad"), default="all")
    for name in (
        "continuous-proof",
        "browser-proof",
        "continuous-nonce",
        "browser-nonce",
        "run-id",
        "run-attempt",
        "head",
        "workflow-cancelled",
    ):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--notepad-proof")
    parser.add_argument("--notepad-nonce")
    args = parser.parse_args()
    if args.scope == "all" and (args.notepad_proof is None or args.notepad_nonce is None):
        parser.error("the all scope requires --notepad-proof and --notepad-nonce")
    nonces = [args.continuous_nonce, args.browser_nonce]
    if args.scope == "all":
        nonces.append(args.notepad_nonce)
    try:
        valid = (
            args.workflow_cancelled == "false"
            and matches(r"[1-9][0-9]*", args.run_id)
            and matches(r"[1-9][0-9]*", args.run_attempt)
            and matches(r"[0-9a-f]{40}", args.head)
            and args.head != "0" * 40
            and all(matches(r"[0-9a-f]{32}", nonce) for nonce in nonces)
            and len(set(nonces)) == len(nonces)
            and valid_report(
                args.continuous_proof, "--verify-continuous", args.continuous_nonce, args
            )
            and valid_report(
                args.browser_proof, "--verify-browser-cooperation", args.browser_nonce, args
            )
            and (
                args.scope == "before-notepad"
                or valid_report(
                    args.notepad_proof, "--verify-ordinary-notepad", args.notepad_nonce, args
                )
            )
        )
    except (OSError, ValueError, RecursionError):
        valid = False
    if not valid:
        print("consumer-exit-proof-rejected", file=sys.stderr)
        return 1
    print("no-active-owned-consumers")
    return 0


if __name__ == "__main__":
    sys.exit(main())
