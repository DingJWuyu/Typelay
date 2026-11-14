"""Select platform checks from verified push evidence, failing closed to full jobs."""

import json
import os
import re
import subprocess
from pathlib import Path

LINUX_PATHS = ("src/linux/", "tests/linux/", "eng/linux/", "packaging/linux/")
WINDOWS_PATHS = ("src/windows/", "tests/windows/", "eng/windows/", "packaging/windows/")


def git(*args):
    return subprocess.check_output(["git", *args], stderr=subprocess.DEVNULL)


def succeeds(*args):
    return (
        subprocess.run(
            ["git", *args], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
        ).returncode
        == 0
    )


def commit_exists(value):
    return (
        re.fullmatch(r"[0-9a-f]{40}", value) is not None
        and value != "0" * 40
        and git("cat-file", "-t", value).strip() == b"commit"
    )


def touched_paths(before, after):
    # Include every commit and each merge parent, even edits later reverted.
    # No rename detection and NUL delimiters preserve both sides and unusual paths.
    return {
        os.fsdecode(path)
        for path in git(
            "log", "-m", "--format=", "--name-only", "--no-renames", "-z", f"{before}..{after}"
        ).split(b"\0")
        if path
    }


def valid_push(before, after):
    return (
        os.environ.get("CREATED") == "false"
        and os.environ.get("FORCED") == "false"
        and commit_exists(before)
        and commit_exists(after)
        and after == git("rev-parse", "HEAD").decode().strip()
        and succeeds("merge-base", "--is-ancestor", before, after)
    )


def quality_collection():
    """Validate explicit manual collection options; ordinary checks never start research."""
    event = os.environ.get("EVENT_NAME")
    comparison = os.environ.get("HY_DECODE_COMPARISON", "false")
    diagnostic = os.environ.get("FIXED_MODEL_DIAGNOSTICS", "false")
    if diagnostic not in ("", "false", "true"):
        raise ValueError("fixed_model_diagnostics must be an exact boolean")
    if diagnostic == "true" and (
        event != "workflow_dispatch"
        or os.environ.get("COLLECT_TRANSLATION_QUALITY") != "true"
        or comparison == "true"
    ):
        raise ValueError("fixed diagnostics requires collection and excludes comparison")
    if comparison not in ("", "false", "true"):
        raise ValueError("hy_decode_comparison must be an exact boolean")
    if comparison == "true" and (
        event != "workflow_dispatch" or os.environ.get("COLLECT_TRANSLATION_QUALITY") != "true"
    ):
        raise ValueError(
            "hy_decode_comparison input requires manual translation quality collection"
        )
    if event == "workflow_dispatch":
        return (
            os.environ.get("COLLECT_TRANSLATION_QUALITY") == "true",
            comparison == "true",
            diagnostic == "true",
        )
    return False, False, False


def neutral(path):
    return path.startswith("docs/") or path in ("README.md", "AGENTS.md", "GLOSSARY.md")


def select():
    windows, linux, quality_only = True, True, False
    branch = os.environ.get("BRANCH", "")
    before = os.environ.get("BEFORE", "")
    after = os.environ.get("AFTER", "")
    if os.environ.get("EVENT_NAME") == "workflow_dispatch":
        mode = os.environ.get("CHECK_MODE", "full")
        if mode not in ("full", "quality-only"):
            raise ValueError("Unknown explicit check mode")
        if mode == "quality-only":
            if os.environ.get("COLLECT_TRANSLATION_QUALITY") != "true":
                raise ValueError("quality-only requires explicit translation quality collection")
            return False, True, True
    try:
        if os.environ.get("EVENT_NAME") != "push":
            return windows, linux, quality_only
        if branch.startswith("work/"):
            if valid_push(before, after):
                touched = touched_paths(before, after)
                paths = {path for path in touched if not neutral(path)}
                if touched and not paths:
                    windows, linux = False, False
                elif paths and all(path.startswith(LINUX_PATHS) for path in paths):
                    windows = False
                elif paths and all(path.startswith(WINDOWS_PATHS) for path in paths):
                    linux = False
    except (OSError, subprocess.CalledProcessError):
        # Missing Git objects or failed comparisons never authorize skipping jobs.
        return True, True, False
    return windows, linux, quality_only


def main():
    if destination := os.environ.get("CI_EVIDENCE_PATH"):
        event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
        pull_request = event.get("pull_request")
        ref = os.environ["GITHUB_REF"]
        tested = git("rev-parse", "HEAD").decode().strip()
        if tested != os.environ["GITHUB_SHA"]:
            raise ValueError("The checkout does not match the event's tested commit")
        evidence = {
            "event": os.environ["EVENT_NAME"],
            "ref": ref,
            "run_id": os.environ.get("GITHUB_RUN_ID"),
            "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
            "tested_commit": tested,
            "pull_request": event.get("number") if pull_request else None,
            "head": (
                {key: pull_request["head"][key] for key in ("ref", "sha")}
                if pull_request
                else {"ref": ref, "sha": event.get("after", tested)}
            ),
            "base": (
                {key: pull_request["base"][key] for key in ("ref", "sha")}
                if pull_request
                else {"ref": ref, "sha": event.get("before")}
            ),
        }
        path = Path(destination)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
        if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(summary, "a", encoding="utf-8") as stream:
                stream.write("## Checked source\n\n```json\n")
                stream.write(json.dumps(evidence, indent=2) + "\n```\n")
    selection = select()
    collect, comparison, diagnostic = quality_collection()
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        for name, value in zip(("windows", "linux", "quality_only"), selection):
            stream.write(f"{name}={str(value).lower()}\n")
        stream.write(f"collect_quality={str(collect).lower()}\n")
        stream.write(f"hy_decode_comparison={str(comparison).lower()}\n")
        stream.write(f"fixed_model_diagnostics={str(diagnostic).lower()}\n")


if __name__ == "__main__":
    main()
