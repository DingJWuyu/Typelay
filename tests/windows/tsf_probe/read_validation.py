"""Validate bounded isolated read reports. Never grants IME or write permission."""

import argparse
import json
import re
from pathlib import Path


def environment_observed(report, sha):
    counters = (
        "registrationAttempts",
        "activationAttempts",
        "inputChanges",
        "targetApplicationStarts",
        "sourceTextReads",
    )
    return (
        bool(sha)
        and type(report.get("schemaVersion")) is int
        and report["schemaVersion"] == 1
        and report.get("kind") == "tsf-readonly-prerequisites"
        and report.get("status") == "observed-prerequisites-only"
        and report.get("runner", {}).get("GITHUB_SHA") == sha
        and report.get("productAcceptance") is False
        and all(type(report.get(key)) is int and report[key] == 0 for key in counters)
        and report.get("capabilities", {}).get("registrationAllowed") is False
        and report.get("capabilities", {}).get("writebackAllowed") is False
    )


def read_observed(report):
    expected = {
        "kind": "isolated-tsf-native",
        "status": "observed-not-acceptance",
        "reason": "single-read-observed",
        "targetStarted": True,
        "requestDispatched": True,
        "normalExit": True,
        "forcedCleanup": False,
        "ownedJobEmpty": True,
        "syntheticFileRemoved": True,
        "removalProvenByProcessExit": True,
        "liveUnloadProven": False,
        "imeVerified": False,
        "writebackAllowed": False,
        "readSnapshotValid": True,
        "readStatus": 1,
        "readReason": 0,
        "requestHr": 0,
        # msctf.h: MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_ITF, 0x0300).
        "sessionHr": 0x40300,
        "callbackCount": 1,
        "compositionCount": 0,
        "syntheticExactMatch": True,
    }
    extras = {
        "sessionDestroyed",
        "textLength",
        "selectionStart",
        "selectionLength",
        "noncryptographicFingerprint",
    }
    if set(report) != set(expected) | extras:
        return False
    if any(
        type(report[key]) is not type(value) or report[key] != value
        for key, value in expected.items()
    ):
        return False
    for key in ("sessionHr", "sessionDestroyed", "textLength", "selectionStart", "selectionLength"):
        if type(report[key]) is not int:
            return False
    length, start, selected = (
        report[key] for key in ("textLength", "selectionStart", "selectionLength")
    )
    return (
        report["sessionDestroyed"] in (0, 1)
        and 0 < length <= 4096
        and 0 <= start <= length
        and 0 <= selected <= length - start
        and isinstance(report["noncryptographicFingerprint"], str)
        and re.fullmatch(r"[0-9a-f]{16}", report["noncryptographicFingerprint"]) is not None
    )


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kind", choices=("environment", "read"), required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--sha")
    args = parser.parse_args(argv)
    try:
        if args.report.stat().st_size > 65536:
            raise ValueError("oversized-report")
        report = json.loads(args.report.read_text(encoding="utf-8-sig"))
        valid = (
            environment_observed(report, args.sha)
            if args.kind == "environment"
            else read_observed(report)
        )
    except (OSError, ValueError, TypeError, AttributeError, KeyError):
        valid = False
    print(
        "Bounded observation only; no IME/write acceptance."
        if valid
        else "Observation incomplete or report invalid; stop without retry."
    )
    return 0 if valid else 2


if __name__ == "__main__":
    raise SystemExit(main())
