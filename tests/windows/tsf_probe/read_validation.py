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


def module_observation_valid(detail):
    bounds = {
        "schemaVersion": (1, 1),
        "stopReason": (0, 11),
        "win32Error": (0, 2**32 - 1),
        "startedTick": (0, 2**64 - 1),
        "endedTick": (0, 2**64 - 1),
    }
    booleans = {"attempted", "completed", "observerPathSeen"}
    if type(detail) is not dict or set(detail) != set(bounds) | booleans:
        return False
    if any(type(detail[k]) is not bool for k in booleans) or any(
        type(detail[k]) is not int or not lo <= detail[k] <= hi for k, (lo, hi) in bounds.items()
    ):
        return False
    stop = detail["stopReason"]
    if not detail["attempted"]:
        return not any(detail[k] for k in detail if k != "schemaVersion")
    return (
        stop != 0
        and detail["endedTick"] >= detail["startedTick"]
        and detail["completed"] == (stop in (1, 2))
        and detail["observerPathSeen"] == (stop == 1)
        and (stop in (3, 5, 7, 10) or detail["win32Error"] == 0)
    )


def diagnostics_valid(detail):
    bounds = {
        "protocolVersion": (3, 3),
        "targetPhases": (0, 31),
        "signatureError": (0, 2**32 - 1),
        "signatureFlags": (0, 31),
        "dynamicError": (0, 2**32 - 1),
        "dynamicFlags": (0, 15),
        "sendReturn": (-(2**63), 2**63 - 1),
        "sendLastError": (0, 2**32 - 1),
        "sendStartedTick": (0, 2**64 - 1),
        "sendEndedTick": (0, 2**64 - 1),
    }
    booleans = {
        "policyQueried",
        "signatureKnown",
        "dynamicKnown",
        "sendAttempted",
        "sendReturned",
        "messageResultValid",
    }
    if type(detail) is not dict or set(detail) != set(bounds) | booleans | {
        "messageResult",
        "moduleObservation",
    }:
        return False
    if any(type(detail[key]) is not bool for key in booleans):
        return False
    if any(
        type(detail[key]) is not int or not lo <= detail[key] <= hi
        for key, (lo, hi) in bounds.items()
    ):
        return False
    observation = detail["moduleObservation"]
    if not module_observation_valid(observation):
        return False
    if observation["attempted"] != detail["sendReturned"]:
        return False
    if observation["attempted"] and observation["startedTick"] < detail["sendEndedTick"]:
        return False
    if detail["targetPhases"] not in (0, 1, 3, 7, 15, 31):
        return False
    if not detail["policyQueried"] and any(
        detail[key]
        for key in (
            "signatureKnown",
            "dynamicKnown",
            "signatureError",
            "dynamicError",
            "signatureFlags",
            "dynamicFlags",
        )
    ):
        return False
    if not detail["signatureKnown"] and detail["signatureFlags"] != 0:
        return False
    if not detail["dynamicKnown"] and detail["dynamicFlags"] != 0:
        return False
    if detail["sendAttempted"]:
        if not (detail["policyQueried"] and detail["signatureKnown"] and detail["dynamicKnown"]):
            return False
        if detail["signatureFlags"] & 7 or detail["sendEndedTick"] < detail["sendStartedTick"]:
            return False
    elif any(
        detail[key]
        for key in (
            "targetPhases",
            "sendReturn",
            "sendLastError",
            "sendStartedTick",
            "sendEndedTick",
        )
    ):
        return False
    if detail["sendReturned"] and not detail["sendAttempted"]:
        return False
    if not detail["sendReturned"] and any(
        detail[key] for key in ("sendReturn", "sendLastError", "sendEndedTick")
    ):
        return False
    valid = detail["sendAttempted"] and detail["sendReturned"] and detail["sendReturn"] != 0
    if detail["messageResultValid"] != valid:
        return False
    result = detail["messageResult"]
    return (type(result) is int and 0 <= result < 2**64) if valid else result is None


def context_metadata_valid(metadata):
    keys = {
        "attempted",
        "complete",
        "stable",
        "count",
        "overflow",
        "topEqualsBase",
        "failureStage",
        "hresult",
        "contexts",
    }
    row_keys = {
        "equalsTop",
        "equalsBase",
        "belongsToFocusedDocument",
        "transitory",
        "windowMatchesOwnedEdit",
    }
    if type(metadata) is not dict or set(metadata) != keys:
        return False
    if any(type(metadata[key]) is not bool for key in ("attempted", "complete", "overflow")):
        return False
    limits = {
        "stable": (0, 2),
        "count": (0, 2),
        "topEqualsBase": (0, 2),
        "failureStage": (0, 9),
        "hresult": (-(2**31), 2**31 - 1),
    }
    if any(
        type(metadata[key]) is not int or not lo <= metadata[key] <= hi
        for key, (lo, hi) in limits.items()
    ):
        return False
    rows = metadata["contexts"]
    if type(rows) is not list or len(rows) != 2:
        return False
    if any(
        type(row) is not dict
        or set(row) != row_keys
        or any(type(value) is not int or value not in (0, 1, 2) for value in row.values())
        for row in rows
    ):
        return False
    if not metadata["complete"]:
        if any(metadata[key] != 0 for key in ("stable", "count", "topEqualsBase")) or any(
            any(row.values()) for row in rows
        ):
            return False
        if not metadata["attempted"]:
            return (
                not metadata["overflow"]
                and metadata["failureStage"] == 0
                and metadata["hresult"] == 0
            )
        return (
            metadata["failureStage"] != 0
            and metadata["hresult"] < 0
            and (not metadata["overflow"] or metadata["failureStage"] == 4)
        )
    if (
        not metadata["attempted"]
        or metadata["stable"] != 2
        or metadata["overflow"]
        or metadata["failureStage"] != 0
        or metadata["hresult"] != 0
        or metadata["count"] not in (1, 2)
        or metadata["topEqualsBase"] != (2 if metadata["count"] == 1 else 1)
    ):
        return False
    active = rows[: metadata["count"]]
    if any(any(row.values()) for row in rows[metadata["count"] :]):
        return False
    if any(
        row["equalsTop"] not in (1, 2)
        or row["equalsBase"] not in (1, 2)
        or row["belongsToFocusedDocument"] != 2
        or row["transitory"] not in (1, 2)
        for row in active
    ):
        return False
    if (
        sum(row["equalsTop"] == 2 for row in active) != 1
        or sum(row["equalsBase"] == 2 for row in active) != 1
    ):
        return False
    if metadata["count"] == 2 and any(row["equalsTop"] == row["equalsBase"] for row in active):
        return False
    return all(row["transitory"] == 2 for row in active if row["equalsTop"] == 2)


def read_observed(report):
    return native_observed(report, read=True)


def lifecycle_observed(report):
    return native_observed(report, read=False)


def native_observed(report, *, read):
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
    if not read:
        expected.update(
            reason="lifecycle-observed",
            requestDispatched=False,
            readSnapshotValid=False,
            readStatus=0,
            sessionHr=0,
            callbackCount=0,
            syntheticExactMatch=False,
        )
    extras = {
        "diagnostics",
        "contextMetadata",
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
    if (
        not context_metadata_valid(report["contextMetadata"])
        or report["contextMetadata"]["attempted"]
    ):
        return False  # Transitory metadata never upgrades a read/lifecycle to success.
    detail = report["diagnostics"]
    if not diagnostics_valid(detail):
        return False
    if read:
        if (
            not detail["sendAttempted"]
            or not detail["sendReturned"]
            or detail["targetPhases"] != 31
        ):
            return False
    elif (
        detail["policyQueried"]
        or detail["sendAttempted"]
        or report["sessionDestroyed"] != 0
        or any(report[key] != 0 for key in ("textLength", "selectionStart", "selectionLength"))
        or report["noncryptographicFingerprint"] != "0000000000000000"
    ):
        return False
    length, start, selected = (
        report[key] for key in ("textLength", "selectionStart", "selectionLength")
    )
    return (
        report["sessionDestroyed"] in (0, 1)
        and (0 < length <= 4096 if read else length == 0)
        and 0 <= start <= length
        and 0 <= selected <= length - start
        and isinstance(report["noncryptographicFingerprint"], str)
        and re.fullmatch(r"[0-9a-f]{16}", report["noncryptographicFingerprint"]) is not None
    )


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kind", choices=("environment", "read", "lifecycle"), required=True)
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
            else (read_observed(report) if args.kind == "read" else lifecycle_observed(report))
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
