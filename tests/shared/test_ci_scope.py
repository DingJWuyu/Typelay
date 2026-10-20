"""Exercise the Actions selector against real, isolated Git histories."""

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SELECTOR = Path(__file__).resolve().parents[2] / "eng/shared/ci_scope.py"
FULL = {
    "windows": "true",
    "linux": "true",
    "qt": "false",
    "quality_only": "false",
    "practice": "false",
}
LINUX = {**FULL, "windows": "false"}
WINDOWS = {**FULL, "linux": "false"}
SHARED_ONLY = {**FULL, "windows": "false", "linux": "false"}
QT = {**LINUX, "linux": "false", "qt": "true"}
QUALITY_ONLY = {**LINUX, "quality_only": "true"}
PRACTICE = {**LINUX, "practice": "true"}
QT_BASELINE = "d607db1b8a3fc882cfd4a9c19a6e454d9b610189"


class ScopeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        self.env = {
            **os.environ,
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_AUTHOR_NAME": "CI scope test",
            "GIT_AUTHOR_EMAIL": "ci-scope@example.invalid",
            "GIT_COMMITTER_NAME": "CI scope test",
            "GIT_COMMITTER_EMAIL": "ci-scope@example.invalid",
        }
        self.git("init", "-q")
        self.base = self.commit({"README.md": "base"})
        self.git("update-ref", "refs/remotes/origin/trunk", self.base)

    def git(self, *args, data=None):
        return (
            subprocess.check_output(
                ["git", *args], cwd=self.repo, env=self.env, input=data, timeout=20
            )
            .decode()
            .strip()
        )

    def reset(self, ref):
        self.git("update-ref", "HEAD", ref)
        self.git("read-tree", ref)

    def commit(self, files, parents=None):
        # Index-only files let Git's legal path names work on every test host.
        for path, content in files.items():
            if content is None:
                self.git("update-index", "--force-remove", "--", path)
            else:
                blob = self.git("hash-object", "-w", "--stdin", data=content.encode())
                self.git("update-index", "--add", "--cacheinfo", "100644", blob, path)
        tree = self.git("write-tree")
        if parents is None:
            head = subprocess.run(
                ["git", "rev-parse", "--verify", "HEAD"],
                cwd=self.repo,
                env=self.env,
                capture_output=True,
                timeout=20,
            )
            parents = [head.stdout.decode().strip()] if head.returncode == 0 else []
        arguments = [argument for parent in parents for argument in ("-p", parent)]
        commit = self.git("commit-tree", tree, *arguments, "-m", "scope fixture")
        self.git("update-ref", "HEAD", commit)
        return commit

    def select(self, before, branch="work/linux-usability", **event):
        output = self.root / "output"
        output.write_text("")
        subprocess.run(
            [sys.executable, str(SELECTOR)],
            cwd=self.repo,
            env={
                **self.env,
                "GITHUB_OUTPUT": str(output),
                "EVENT_NAME": "push",
                "BRANCH": branch,
                "BEFORE": before,
                "AFTER": self.git("rev-parse", "HEAD"),
                "CREATED": "false",
                "FORCED": "false",
                **event,
            },
            check=True,
            capture_output=True,
            timeout=20,
        )
        return dict(line.split("=", 1) for line in output.read_text().splitlines())

    def test_current_platform_push_ignores_old_cross_platform_history(self):
        for platform, branch, expected in (
            ("linux", "work/linux-usability", LINUX),
            ("windows", "work/windows-experience", WINDOWS),
            ("windows", "work/linux-usability", WINDOWS),
            ("linux", "work/windows-experience", LINUX),
        ):
            with self.subTest(platform=platform, branch=branch):
                self.reset(self.base)
                before = self.commit(
                    {
                        "src/shared/old.py": "old",
                        "src/windows/old.cs": "old",
                        "src/linux/old.py": "old",
                    }
                )
                self.commit(
                    {
                        **{
                            f"{directory}/{platform}/current.py": platform
                            for directory in ("src", "tests", "eng", "packaging")
                        },
                        "docs/guide.md": "notes",
                    }
                )
                self.assertEqual(expected, self.select(before, branch))

    def test_practice_partial_is_limited_to_its_current_ui_and_test_paths(self):
        before = self.commit({"src/shared/old.cs": "already checked"})
        self.commit(
            {
                "src/linux/desktop/practice.py": "practice UI",
                "tests/linux/practice_window.py": "widget regression",
                "docs/practice.md": "partial evidence",
            }
        )
        self.assertEqual(PRACTICE, self.select(before))
        self.assertEqual(LINUX, self.select(before, "work/windows-experience"))
        for evidence in (
            {"CREATED": "true"},
            {"FORCED": "true"},
            {"EVENT_NAME": "workflow_dispatch"},
        ):
            self.assertEqual(FULL, self.select(before, **evidence))
        for path, expected in (
            ("src/linux/desktop/input.py", LINUX),
            ("src/linux/desktop/practice_session.py", LINUX),
            ("tests/linux/qt_desktop.py", LINUX),
            ("tests/linux/desktop_e2e.py", LINUX),
            ("src/shared/runtime.cs", FULL),
            (".github/workflows/check.yml", FULL),
        ):
            with self.subTest(path=path):
                self.reset(before)
                self.commit({path: "temporary"})
                self.commit({path: None, "src/linux/desktop/practice.py": "practice"})
                self.assertEqual(expected, self.select(before))

    def test_verified_neutral_only_push_keeps_only_shared_checks(self):
        self.commit(
            {
                "docs/only.md": "docs",
                "README.md": "notes",
                "AGENTS.md": "guidance",
                "GLOSSARY.md": "terms",
            }
        )
        for branch in ("work/linux-usability", "work/windows-experience"):
            with self.subTest(branch=branch):
                self.assertEqual(SHARED_ONLY, self.select(self.base, branch))

    def test_shared_unknown_and_workflow_require_both(self):
        for branch in ("work/linux-usability", "work/windows-experience"):
            for path in (
                "src/shared/core.cs",
                "eng/shared/tool.py",
                "tests/shared/test.py",
                "global.json",
                "other/file",
                ".github/workflows/check.yml",
                ".github/CODEOWNERS",
            ):
                with self.subTest(branch=branch, path=path):
                    self.reset(self.base)
                    self.commit({path: "changed", "docs/notes.md": "docs"})
                    self.assertEqual(FULL, self.select(self.base, branch))

    def test_reverted_and_merged_changes_still_count(self):
        for path in ("src/shared/transient.cs", ".github/workflows/check.yml"):
            with self.subTest(path=path):
                self.reset(self.base)
                self.commit({path: "temporary"})
                self.commit({path: None, "docs/notes.md": "docs"})
                self.assertEqual(FULL, self.select(self.base))
        self.reset(self.base)
        side = self.commit({"src/windows/side.cs": "windows"})
        self.reset(self.base)
        before = self.commit({"tests/linux/before.py": "linux"})
        self.commit({"tests/linux/merged.py": "linux"}, parents=[before, side])
        self.assertEqual(FULL, self.select(before))

    def test_renames_deletions_and_unusual_path_names(self):
        before = self.commit({"tests/windows/old.cs": "same bytes"})
        self.commit({"tests/windows/old.cs": None, "tests/linux/new.py": "same bytes"})
        for branch in ("work/linux-usability", "work/windows-experience"):
            self.assertEqual(FULL, self.select(before, branch))
        self.reset(before)
        self.commit({"tests/windows/old.cs": None, "tests/linux/current.py": "linux"})
        self.assertEqual(FULL, self.select(before))
        self.reset(self.base)
        before = self.commit({"tests/linux/old.py": "same bytes"})
        self.commit({"tests/linux/old.py": None, "tests/linux/空 格\t\nfile.py": "same bytes"})
        self.assertEqual(LINUX, self.select(before))
        before = self.git("rev-parse", "HEAD")
        self.commit({"tests/linux/空 格\t\nfile.py": None})
        self.assertEqual(LINUX, self.select(before))

    def test_neutral_push_with_missing_or_invalid_evidence_requires_both(self):
        side = self.commit({"src/windows/side.cs": "side"})
        self.reset(self.base)
        after = self.commit({"docs/only.md": "docs"})
        blob = self.git("hash-object", "-w", "--stdin", data=b"not a commit")
        for field in ("BEFORE", "AFTER"):
            for value in ("", "0" * 40, "not-a-sha", "f" * 40, blob):
                with self.subTest(field=field, value=value):
                    event = {field: value}
                    before = event.pop("BEFORE", self.base)
                    self.assertEqual(FULL, self.select(before, **event))
        for event in (
            {"CREATED": "true"},
            {"FORCED": "true"},
            {"CREATED": ""},
            {"FORCED": ""},
            {"AFTER": self.base},
            {"AFTER": side},
        ):
            with self.subTest(event=event):
                self.assertEqual(FULL, self.select(self.base, **event))
        self.assertEqual(FULL, self.select(side, AFTER=after))
        self.assertEqual(FULL, self.select(after))  # Empty push.
        self.commit({})
        self.assertEqual(FULL, self.select(after))  # Empty commit.
        for branch in (
            "main",
            "trunk",
            "windows-preview",
            "cachyos-preview",
            "work/other",
            "other",
        ):
            self.assertEqual(FULL, self.select(self.base, branch))
        for event in ("pull_request", "workflow_dispatch"):
            self.assertEqual(FULL, self.select(self.base, EVENT_NAME=event))

    def test_qt_partial_needs_whole_branch_scope_and_fixed_production_bytes(self):
        # Test-only Git replacements model the pinned commits with real local trees.
        # They do not establish compatibility with the historical release artifact.
        self.git("update-ref", "refs/replace/" + QT_BASELINE, self.base)
        qt_file = "src/linux/native/qt6/adapter.cpp"
        self.commit({qt_file: "Qt"})
        self.assertEqual(QT, self.select(self.base))
        self.assertEqual(LINUX, self.select(self.base, "work/windows-experience"))
        self.git("update-ref", "-d", "refs/replace/" + QT_BASELINE)
        self.assertEqual(LINUX, self.select(self.base))  # Missing pinned object.
        self.git("update-ref", "refs/replace/" + QT_BASELINE, self.base)
        for old_path in (
            "src/linux/client.py",
            "tests/linux/client.py",
            "src/shared/core.cs",
            "src/windows/app.cs",
        ):
            with self.subTest(old_path=old_path):
                self.reset(self.base)
                self.commit({old_path: "old non-Qt change"})
                before = self.commit({old_path: None})
                self.commit({qt_file: "Qt"})
                self.assertEqual(LINUX, self.select(before))
        self.reset(self.base)
        before = self.commit({"src/shared/core.cs": "new trunk production"})
        self.git("update-ref", "refs/remotes/origin/trunk", before)
        self.commit({qt_file: "Qt"})
        self.assertEqual(LINUX, self.select(before))  # Qt-only history, changed baseline bytes.
        self.git("update-ref", "-d", "refs/remotes/origin/trunk")
        self.assertEqual(LINUX, self.select(before))  # No whole-branch evidence.

    def test_quality_only_requires_explicit_dispatch_and_collection(self):
        event = {"CHECK_MODE": "quality-only", "COLLECT_TRANSLATION_QUALITY": "true"}
        self.assertEqual(
            QUALITY_ONLY, self.select(self.base, EVENT_NAME="workflow_dispatch", **event)
        )
        for name in ("push", "pull_request"):
            self.assertEqual(FULL, self.select(self.base, EVENT_NAME=name, **event))
        for mode, collect in (("quality-only", "false"), ("quality-only", ""), ("unknown", "true")):
            with self.subTest(mode=mode, collect=collect):
                with self.assertRaises(subprocess.CalledProcessError):
                    self.select(
                        self.base,
                        EVENT_NAME="workflow_dispatch",
                        CHECK_MODE=mode,
                        COLLECT_TRANSLATION_QUALITY=collect,
                    )

    def test_full_collection_preserves_full_platform_checks(self):
        for collect in ("true", "false", ""):
            self.assertEqual(
                FULL,
                self.select(
                    self.base,
                    EVENT_NAME="workflow_dispatch",
                    CHECK_MODE="full",
                    COLLECT_TRANSLATION_QUALITY=collect,
                ),
            )

    def test_retired_settings_branch_has_no_partial_exception(self):
        before = self.base
        self.commit({"src/linux/desktop/ui.py": "settings"})
        self.assertEqual(FULL, self.select(before, "work/linux-settings"))


if __name__ == "__main__":
    unittest.main()
