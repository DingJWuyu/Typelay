"""Select platform checks from verified push evidence, failing closed to full jobs."""

import json
import os
import re
import subprocess
from pathlib import Path

QT_BASELINE = "d607db1b8a3fc882cfd4a9c19a6e454d9b610189"
PRACTICE_PATHS = {
    "src/linux/desktop/practice.py",
    "tests/linux/practice_window.py",
}
LINUX_PATHS = ("src/linux/", "tests/linux/", "eng/linux/", "packaging/linux/")
WINDOWS_PATHS = ("src/windows/", "tests/windows/", "eng/windows/", "packaging/windows/")
QT_PATHS = re.compile(
    r"^(src/linux/native/qt6/|src/linux/native/fcitx5/typelay-fcitx-qt-v1\.h$|"
    r"eng/linux/cooperative/(qt\.py|qt-runtime-probe\.cpp|patches/fcitx5-qt\.patch)$|"
    r"tests/linux/qt_cooperative/|tests/linux/qt_desktop\.py$|"
    r"tests/linux/fixtures/qt_native_fixture\.cpp$)"
)


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
    """Return collection, comparison, diagnostics, and full-check requirements for this event."""
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
            False,
        )
    if event != "push":
        return False, False, False, False
    try:
        message = git("show", "-s", "--format=%B", "HEAD").decode("utf-8")
        marker = "TypeLay-Quality-Capture: true"
        comparison_key = "TypeLay-Hy-Decode-Comparison:"
        comparison_marker = comparison_key + " true"
        lines = message.rstrip("\n").split("\n")
        # Fenced/quoted/indented examples are documentation, not requests.
        if "```" in message or "~~~" in message:
            return False, False, False, False
        comparison_lines = [line for line in lines if line.startswith(comparison_key)]
        diagnostic_key = "TypeLay-Fixed-Model-Diagnostics:"
        diagnostic_marker = diagnostic_key + " true"
        diagnostic_lines = [line for line in lines if line.startswith(diagnostic_key)]
        diagnose = bool(diagnostic_lines)
        compare = bool(comparison_lines)
        if diagnose:
            if (
                compare
                or diagnostic_lines != [diagnostic_marker]
                or lines.count(marker) != 1
                or len(lines) < 4
                or lines[-3:] != ["", marker, diagnostic_marker]
            ):
                return False, False, False, True
        elif compare:
            if (
                comparison_lines != [comparison_marker]
                or lines.count(marker) != 1
                or len(lines) < 4
                or lines[-3:] != ["", marker, comparison_marker]
            ):
                return False, False, False, True
        else:
            if marker not in lines or lines[-1] != marker:
                return False, False, False, False
            if lines.count(marker) != 1:
                return False, False, False, True
            if len(lines) < 3 or lines[-2] != "":
                return False, False, False, False
        # An unverified explicit request never narrows normal checks.
        if os.environ.get("BRANCH") != "work/linux-usability":
            return False, False, False, True
        before, after = os.environ.get("BEFORE", ""), os.environ.get("AFTER", "")
        if before == after or not valid_push(before, after):
            return False, False, False, True
        event_path = Path(os.environ["GITHUB_EVENT_PATH"])
        if event_path.stat().st_size > 4 * 1024 * 1024:
            return False, False, False, True
        payload = json.loads(event_path.read_text(encoding="utf-8"))
        head = payload.get("head_commit")
        verified = (
            isinstance(head, dict)
            and head.get("id") == after
            and isinstance(head.get("message"), str)
            and head["message"].rstrip("\n") == message.rstrip("\n")
        )
        return verified, verified and compare, verified and diagnose, True
    except (OSError, ValueError, KeyError, AttributeError, subprocess.CalledProcessError):
        return False, False, False, True


def neutral(path):
    return path.startswith("docs/") or path in ("README.md", "AGENTS.md", "GLOSSARY.md")


def qt_only_branch():
    try:
        base = git("merge-base", "origin/trunk", "HEAD").decode().strip()
        # Partial acceptance still needs the whole branch's original Qt-only scope.
        paths = {
            path
            for path in touched_paths(base, "HEAD")
            if not neutral(path) and not path.startswith(".github/")
        }
        return (
            bool(paths)
            and all(QT_PATHS.search(path) for path in paths)
            and succeeds(
                "diff",
                "--quiet",
                QT_BASELINE,
                "HEAD",
                "--",
                ".",
                ":!docs/**",
                ":!tests/**",
                ":!.github/**",
                ":!README.md",
                ":!AGENTS.md",
                ":!GLOSSARY.md",
                ":!src/linux/native/qt6/**",
                ":!src/linux/native/fcitx5/typelay-fcitx-qt-v1.h",
                ":!eng/linux/cooperative/qt.py",
                ":!eng/linux/cooperative/qt-runtime-probe.cpp",
                ":!eng/linux/cooperative/patches/fcitx5-qt.patch",
            )
        )
    except (OSError, subprocess.CalledProcessError):
        return False


def select():
    windows, linux, qt, quality_only, practice = True, True, False, False, False
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
            return False, True, False, True, False
    try:
        if os.environ.get("EVENT_NAME") != "push":
            return windows, linux, qt, quality_only, practice
        if branch in ("work/linux-usability", "work/windows-experience"):
            if valid_push(before, after):
                touched = touched_paths(before, after)
                paths = {path for path in touched if not neutral(path)}
                if touched and not paths:
                    windows, linux = False, False
                elif paths and all(path.startswith(LINUX_PATHS) for path in paths):
                    windows = False
                    if branch == "work/linux-usability":
                        practice = paths <= PRACTICE_PATHS
                        qt = not practice and qt_only_branch()
                        linux = not qt
                elif paths and all(path.startswith(WINDOWS_PATHS) for path in paths):
                    linux = False
    except (OSError, subprocess.CalledProcessError):
        # Missing Git objects or failed comparisons never authorize skipping jobs.
        return True, True, False, False, False
    return windows, linux, qt, quality_only, practice


def main():
    selection = select()
    collect, comparison, diagnostic, require_full = quality_collection()
    if require_full:
        selection = True, True, False, False, False
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        for name, value in zip(("windows", "linux", "qt", "quality_only", "practice"), selection):
            stream.write(f"{name}={str(value).lower()}\n")
        stream.write(f"collect_quality={str(collect).lower()}\n")
        stream.write(f"hy_decode_comparison={str(comparison).lower()}\n")
        stream.write(f"fixed_model_diagnostics={str(diagnostic).lower()}\n")


if __name__ == "__main__":
    main()
