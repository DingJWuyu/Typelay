"""Publish Actions step outcomes without treating absent required checks as passed."""

import json
import os
from pathlib import Path


def main():
    steps = json.loads(os.environ["CI_STEPS"])
    required = os.environ["CI_REQUIRED_STEPS"].split()
    if not required:
        raise ValueError("A check report needs an explicit required scope")
    statuses = {"success": "passed", "failure": "failed"}
    checks = {
        name: statuses.get(steps.get(name, {}).get("outcome"), "not-run") for name in required
    }
    report = {
        "job": os.environ["GITHUB_JOB"],
        "tested_commit": os.environ["GITHUB_SHA"],
        "run_id": os.environ.get("GITHUB_RUN_ID"),
        "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
        "checks": checks,
    }
    directory = Path("artifacts/ci")
    directory.mkdir(parents=True, exist_ok=True)
    (directory / f"{report['job']}.json").write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
        stream.write(f"## {report['job']}\n\nTested commit: `{report['tested_commit']}`\n\n")
        stream.write("| Check | Result |\n| --- | --- |\n")
        for name, status in checks.items():
            stream.write(f"| {name} | {status} |\n")
    return 0 if all(status == "passed" for status in checks.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
