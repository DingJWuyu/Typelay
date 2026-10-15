"""Select platform checks from verified push evidence, failing closed to full jobs."""

import os
import re
import subprocess

QT_BASELINE = "d607db1b8a3fc882cfd4a9c19a6e454d9b610189"
SETTINGS_BASELINE = "79965d49ea1ed8494fa917cd2a3ffb669b3e093a"
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


def settings_only_push(previous):
    # Preserve the temporary settings branch's reviewed first-parent exception.
    parents = git("rev-list", "--parents", "-n", "1", "HEAD").decode().split()[1:]
    wiring_unchanged = previous == "0" * 40 and parents == [SETTINGS_BASELINE]
    if commit_exists(previous) and succeeds("merge-base", "--is-ancestor", previous, "HEAD"):
        wiring_unchanged = not any(
            path.startswith(".github/") for path in touched_paths(previous, "HEAD")
        )
    return wiring_unchanged and succeeds(
        "diff",
        "--quiet",
        SETTINGS_BASELINE,
        "HEAD",
        "--",
        ".",
        ":!src/linux/desktop/ui.py",
        ":!tests/linux/desktop_e2e.py",
        ":!.github/workflows/check.yml",
        ":!docs/**",
    )


def select():
    windows, linux, qt, settings, practice = True, True, False, False, False
    branch = os.environ.get("BRANCH", "")
    before = os.environ.get("BEFORE", "")
    after = os.environ.get("AFTER", "")
    try:
        if os.environ.get("EVENT_NAME") != "push":
            return windows, linux, qt, settings, practice
        if branch in ("work/linux-usability", "work/windows-experience"):
            valid_push = (
                os.environ.get("CREATED") == "false"
                and os.environ.get("FORCED") == "false"
                and commit_exists(before)
                and commit_exists(after)
                and after == git("rev-parse", "HEAD").decode().strip()
                and succeeds("merge-base", "--is-ancestor", before, after)
            )
            if valid_push:
                paths = {path for path in touched_paths(before, after) if not neutral(path)}
                if (
                    paths
                    and branch == "work/linux-usability"
                    and all(path.startswith(LINUX_PATHS) for path in paths)
                ):
                    windows = False
                    practice = paths <= PRACTICE_PATHS
                    qt = not practice and qt_only_branch()
                    linux = not qt
                elif (
                    paths
                    and branch == "work/windows-experience"
                    and all(path.startswith(WINDOWS_PATHS) for path in paths)
                ):
                    linux = False
        elif branch == "work/linux-settings" and settings_only_push(before):
            windows, linux, qt, settings = False, True, False, True
    except (OSError, subprocess.CalledProcessError):
        # Missing Git objects or failed comparisons never authorize skipping jobs.
        return True, True, False, False, False
    return windows, linux, qt, settings, practice


def main():
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        for name, value in zip(("windows", "linux", "qt", "settings", "practice"), select()):
            stream.write(f"{name}={str(value).lower()}\n")


if __name__ == "__main__":
    main()
