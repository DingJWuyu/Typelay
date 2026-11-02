"""Admit same-build Qt interaction evidence after an explicitly failed probe.

This does not package anything or grant distribution acceptance. The caller must
select the existing unbundled desktop route explicitly; bundle fallback is forbidden.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests/linux"))
from qt_cooperative.run import probe_build_identity, verify_fcitx_inputs  # noqa: E402

from eng.linux.cooperative.inputs import (  # noqa: E402
    QT_BUILD_INPUTS,
    QT_PIN,
    QT_SHA256,
    QT_SOURCE_VERSION,
    QT_TEST_INPUTS,
    QT_VERSION,
    hashes,
    sha256,
)


def read_object(path):
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path.name}")
    return value


def verify_qt_desktop_inputs(qt, fcitx):
    """Verify current Qt bytes and completed async evidence, not a release probe."""
    versions = read_object(qt / "versions.json")
    if any(
        versions.get(name) != value
        for name, value in (
            ("qt_build_version", QT_VERSION),
            ("fcitx5_qt_source_version", QT_SOURCE_VERSION),
            ("source_commit", QT_PIN),
            ("source_sha256", QT_SHA256),
        )
    ):
        raise ValueError("Qt build version or fixed source identity differs")
    if versions.get("build_inputs") != hashes(QT_BUILD_INPUTS):
        raise ValueError("Qt production build inputs differ from this checkout")
    if versions.get("test_inputs") != hashes(QT_TEST_INPUTS):
        raise ValueError("Qt regression inputs differ from this checkout")
    for key, names in (
        (
            "binary_sha256",
            (
                "plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so",
                "native/qt6/generic/libtypelay-qt6.so",
                "qt-runtime-probe",
            ),
        ),
        (
            "test_binary_sha256",
            ("fixture", "qpa_ack", "native/qt-native-fixture", "font-order"),
        ),
    ):
        recorded = versions.get(key)
        if not isinstance(recorded, dict) or set(recorded) != set(names):
            raise ValueError(f"Qt {key} must identify every required binary")
        for name in names:
            if recorded[name] != sha256(qt / name):
                raise ValueError(f"Qt binary changed after its build: {name}")
    identity = probe_build_identity(qt, fcitx)
    result = read_object(qt / "async-result.json")
    if (
        result.get("passed") is not True
        or result.get("required") is not True
        or result.get("mode") != "async"
        or result.get("build_identity") != identity
    ):
        raise ValueError("Completed default async evidence is missing or not bound to this build")
    runtime = read_object(qt / "async-runtime.json")
    if (
        runtime.get("build_version") != QT_VERSION
        or runtime.get("runtime_version") != QT_VERSION
        or runtime.get("platform") != "xcb"
    ):
        raise ValueError("Default async runtime identity differs")
    return identity


def admit_desktop(qt, fcitx, probe_outcome):
    """The full admission entry used by CI; it never accepts an explicit bundle."""
    if probe_outcome != "failure":
        raise ValueError("Build-input admission requires an explicit failed Qt probe")
    if os.path.lexists(fcitx / "manifest.json"):
        raise ValueError("An explicit cooperation bundle cannot fall back to build inputs")
    verify_fcitx_inputs(fcitx)
    return verify_qt_desktop_inputs(qt, fcitx)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--qt-output", type=Path, required=True)
    parser.add_argument("--fcitx-root", type=Path, required=True)
    parser.add_argument("--probe-outcome", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = {
        "admission": "not-run",
        "scope": "unbundled-build-input-interaction",
        "distributionAcceptance": False,
        "probeOutcome": args.probe_outcome,
        "reason": None,
    }
    try:
        report["build_identity"] = admit_desktop(
            args.qt_output, args.fcitx_root, args.probe_outcome
        )
        report["admission"] = "admitted-build-input"
    except (
        OSError,
        ValueError,
        RuntimeError,
        KeyError,
        TypeError,
        subprocess.SubprocessError,
    ) as error:
        report["reason"] = str(error)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report))
    return 0 if report["admission"] == "admitted-build-input" else 1


if __name__ == "__main__":
    raise SystemExit(main())
