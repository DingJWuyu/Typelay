"""Exercise the Actions selector against real, isolated Git histories."""

import json
import os
import runpy
import subprocess
import sys
import tempfile
import unittest
from contextlib import chdir
from pathlib import Path
from unittest.mock import patch

SELECTOR = Path(__file__).resolve().parents[2] / "eng/shared/ci_scope.py"
FULL = {
    "windows": "true",
    "linux": "true",
    "qt": "false",
    "quality_only": "false",
    "practice": "false",
    "collect_quality": "false",
    "hy_decode_comparison": "false",
    "fixed_model_diagnostics": "false",
}
LINUX = {**FULL, "windows": "false"}
WINDOWS = {**FULL, "linux": "false"}
SHARED_ONLY = {**FULL, "windows": "false", "linux": "false"}
QT = {**LINUX, "linux": "false", "qt": "true"}
QUALITY_ONLY = {**LINUX, "quality_only": "true", "collect_quality": "true"}
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

    def commit(self, files, parents=None, message="scope fixture"):
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
        commit = self.git("commit-tree", tree, *arguments, "-m", message)
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
                "HY_DECODE_COMPARISON": "false",
                "FIXED_MODEL_DIAGNOSTICS": "false",
                "CREATED": "false",
                "FORCED": "false",
                **event,
            },
            check=True,
            capture_output=True,
            timeout=20,
        )
        return dict(line.split("=", 1) for line in output.read_text().splitlines())

    def test_manual_fixed_diagnostics_requires_collection_and_excludes_comparison(self):
        self.assertEqual(
            self.select(
                self.base,
                EVENT_NAME="workflow_dispatch",
                CHECK_MODE="quality-only",
                COLLECT_TRANSLATION_QUALITY="true",
                FIXED_MODEL_DIAGNOSTICS="true",
            ),
            {**QUALITY_ONLY, "fixed_model_diagnostics": "true"},
        )
        for event in (
            {"COLLECT_TRANSLATION_QUALITY": "false"},
            {"HY_DECODE_COMPARISON": "true"},
            {"FIXED_MODEL_DIAGNOSTICS": "TRUE"},
            {"FIXED_MODEL_DIAGNOSTICS": "yes"},
            {"EVENT_NAME": "push"},
        ):
            with self.subTest(event=event), self.assertRaises(subprocess.CalledProcessError):
                self.select(
                    self.base,
                    **{
                        "EVENT_NAME": "workflow_dispatch",
                        "COLLECT_TRANSLATION_QUALITY": "true",
                        "FIXED_MODEL_DIAGNOSTICS": "true",
                        **event,
                    },
                )

    def test_fixed_diagnostics_selects_only_explicit_authenticated_footer(self):
        quality = "TypeLay-Quality-Capture: true"
        diagnostic = "TypeLay-Fixed-Model-Diagnostics: true"
        payload = self.root / "event.json"
        for message, enabled in (
            (f"diagnose\n\n{quality}\n{diagnostic}", True),
            (f"diagnose\n\n{diagnostic}", False),
            (f"diagnose\n\n{diagnostic}\n{quality}", False),
            (f"diagnose\n\n{quality}\n{diagnostic}\n{diagnostic}", False),
            (f"diagnose\n\n{quality}\n{diagnostic}\nTypeLay-Hy-Decode-Comparison: true", False),
            (f"diagnose\n\n{quality}\nTypeLay-Fixed-Model-Diagnostics: false", False),
            (f"diagnose\n\n```\n{quality}\n{diagnostic}\n```", False),
        ):
            with self.subTest(message=message):
                self.reset(self.base)
                head = self.commit({"src/shared/diagnostic.py": "test"}, message=message)
                payload.write_text(json.dumps({"head_commit": {"id": head, "message": message}}))
                result = self.select(self.base, GITHUB_EVENT_PATH=str(payload))
                self.assertEqual(result["fixed_model_diagnostics"], str(enabled).lower())
                self.assertEqual(result["collect_quality"], str(enabled).lower())
                self.assertEqual(result["hy_decode_comparison"], "false")
                self.assertEqual((result["windows"], result["linux"]), ("true", "true"))
                if enabled:
                    for event in (
                        {"FORCED": "true"},
                        {"AFTER": self.base},
                        {"EVENT_NAME": "pull_request"},
                    ):
                        self.assertEqual(
                            self.select(self.base, GITHUB_EVENT_PATH=str(payload), **event), FULL
                        )
                    payload.write_text(
                        json.dumps({"head_commit": {"id": self.base, "message": message}})
                    )
                    self.assertEqual(self.select(self.base, GITHUB_EVENT_PATH=str(payload)), FULL)

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

    def test_existing_shared_test_edits_keep_windows_and_shared_checks(self):
        for project in ("Core", "Runtime", "Protocol"):
            path = f"tests/shared/TypeLay.{project}.Tests/BehaviorTests.cs"
            for windows_files in ({}, {"src/windows/current.cs": "Windows change"}):
                with self.subTest(project=project, windows_files=windows_files):
                    self.reset(self.base)
                    before = self.commit({path: "existing regression"})
                    self.commit({path: "updated regression", **windows_files})
                    for branch in ("work/linux-usability", "work/windows-experience"):
                        self.assertEqual(WINDOWS, self.select(before, branch))

    def test_shared_test_structure_changes_require_both(self):
        path = "tests/shared/TypeLay.Core.Tests/BehaviorTests.cs"
        renamed = "tests/shared/TypeLay.Core.Tests/RenamedTests.cs"
        for name, original, changes in (
            ("added", {}, [{path: "regression"}]),
            ("deleted", {path: "regression"}, [{path: None}]),
            ("renamed", {path: "regression"}, [{path: None, renamed: "regression"}]),
            ("reverted addition", {}, [{path: "regression"}, {path: None}]),
            ("reverted deletion", {path: "regression"}, [{path: None}, {path: "regression"}]),
        ):
            with self.subTest(change=name):
                self.reset(self.base)
                before = self.commit(original)
                for files in changes:
                    self.commit(files)
                for branch in ("work/linux-usability", "work/windows-experience"):
                    self.assertEqual(FULL, self.select(before, branch))

    def test_shared_test_type_changes_require_both(self):
        path = "tests/shared/TypeLay.Runtime.Tests/BehaviorTests.cs"
        before = self.commit({path: "regression"})
        target = self.git("hash-object", "-w", "--stdin", data=b"other.cs")
        self.git("update-index", "--cacheinfo", "120000", target, path)
        self.commit({})
        self.assertEqual(FULL, self.select(before))

    def test_shared_test_edits_do_not_exempt_other_paths(self):
        test_path = "tests/shared/TypeLay.Runtime.Tests/BehaviorTests.cs"
        for path in (
            "tests/shared/TypeLay.Runtime.Tests/SessionScenario.cs",
            "tests/shared/TypeLay.Core.Tests/nested/BehaviorTests.cs",
            "tests/shared/TypeLay.Core.Tests/support/BehaviorTests.cs",
            "tests/shared/TypeLay.Core.Tests/fixtures/BehaviorTests.cs",
            "tests/shared/support/BehaviorTests.cs",
            "tests/shared/TypeLay.Other.Tests/BehaviorTests.cs",
            "tests/shared/worker/BehaviorTests.cs",
            "tests/shared/TypeLay.Core.Tests/TypeLay.Core.Tests.csproj",
            "tests/shared/TypeLay.Core.Tests/packages.lock.json",
            "src/shared/core.cs",
            "src/linux/app.py",
            "tests/linux/test_app.py",
            "Directory.Build.props",
            "eng/shared/ci_scope.py",
            "other/file",
        ):
            with self.subTest(path=path):
                self.reset(self.base)
                before = self.commit({test_path: "regression", path: "existing"})
                self.commit({test_path: "updated regression", path: "updated"})
                for branch in ("work/linux-usability", "work/windows-experience"):
                    self.assertEqual(FULL, self.select(before, branch))
                self.commit({path: "existing"})
                self.assertEqual(FULL, self.select(before))  # Reverting does not hide this path.

    def test_shared_test_history_includes_every_merge_parent(self):
        path = "tests/shared/TypeLay.Protocol.Tests/ContractTests.cs"
        branch_point = self.commit({path: "original"})
        side = self.commit({path: "updated"})
        self.reset(branch_point)
        before = self.commit({"docs/before.md": "notes"})
        self.commit({path: "updated"}, parents=[before, side])
        self.assertEqual(WINDOWS, self.select(before))

        self.reset(branch_point)
        target = self.git("hash-object", "-w", "--stdin", data=b"other.cs")
        self.git("update-index", "--cacheinfo", "120000", target, path)
        link_base = self.commit({})
        before = self.commit({path: "regular file"})
        self.reset(link_base)
        side = self.commit({"docs/side.md": "notes"})
        self.reset(before)
        # The path is unchanged from the first parent, but changes type from the second.
        self.commit({"docs/side.md": "notes"}, parents=[before, side])
        self.assertEqual(FULL, self.select(before))

    def test_shared_test_edits_preserve_unusual_names_and_modification_reverts(self):
        for name in ("Tests.cs", "空 格\t\nContractTests.cs"):
            with self.subTest(name=name):
                self.reset(self.base)
                path = f"tests/shared/TypeLay.Protocol.Tests/{name}"
                before = self.commit({path: "original"})
                self.commit({path: "updated"})
                self.assertEqual(WINDOWS, self.select(before))
                self.commit({path: "original"})
                self.assertEqual(WINDOWS, self.select(before))

    def test_shared_test_edits_still_require_valid_push_evidence(self):
        path = "tests/shared/TypeLay.Core.Tests/BehaviorTests.cs"
        before = self.commit({path: "original"})
        self.commit({path: "updated"})
        for event in (
            {"CREATED": "true"},
            {"FORCED": "true"},
            {"BEFORE": "0" * 40},
            {"AFTER": before},
            {"EVENT_NAME": "pull_request"},
            {"EVENT_NAME": "workflow_dispatch", "CHECK_MODE": "full"},
            {"BRANCH": "trunk"},
            {"BRANCH": "work/other"},
        ):
            with self.subTest(event=event):
                self.assertEqual(FULL, self.select(before, **event))

    def test_shared_test_status_query_failure_keeps_both_checks(self):
        path = "tests/shared/TypeLay.Core.Tests/BehaviorTests.cs"
        before = self.commit({path: "original"})
        after = self.commit({path: "updated"})
        output = self.root / "failed-query-output"
        selector = runpy.run_path(str(SELECTOR))
        check_output = subprocess.check_output
        for failure in (subprocess.CalledProcessError(1, "git log"), OSError("git unavailable")):
            with self.subTest(failure=type(failure).__name__):
                output.write_text("")

                def fail_status_query(command, **kwargs):
                    if "--diff-filter=ACDRTUXB" in command:
                        raise failure
                    return check_output(command, **kwargs)

                with (
                    chdir(self.repo),
                    patch.dict(
                        os.environ,
                        {
                            **self.env,
                            "EVENT_NAME": "push",
                            "BRANCH": "work/linux-usability",
                            "BEFORE": before,
                            "AFTER": after,
                            "CREATED": "false",
                            "FORCED": "false",
                            "HY_DECODE_COMPARISON": "false",
                            "FIXED_MODEL_DIAGNOSTICS": "false",
                            "GITHUB_OUTPUT": str(output),
                        },
                    ),
                    patch("subprocess.check_output", side_effect=fail_status_query),
                ):
                    selector["main"]()
                self.assertEqual(
                    FULL, dict(line.split("=", 1) for line in output.read_text().splitlines())
                )

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

    def test_explicit_current_push_quality_trailer_keeps_full_checks(self):
        message = "对照风格保义约束\n\nTypeLay-Quality-Capture: true"
        head = self.commit({"docs/note.md": "explicit experiment"}, message=message)
        payload = self.root / "event.json"
        payload.write_text(json.dumps({"head_commit": {"id": head, "message": message}}))
        self.assertEqual(
            {**FULL, "collect_quality": "true"},
            self.select(self.base, GITHUB_EVENT_PATH=str(payload)),
        )

    def test_explicit_comparison_footer_keeps_all_checks(self):
        message = (
            "decode experiment\n\nTypeLay-Quality-Capture: true\nTypeLay-Hy-Decode-Comparison: true"
        )
        head = self.commit({"docs/note.md": "experiment"}, message=message)
        payload = self.root / "event.json"
        payload.write_text(json.dumps({"head_commit": {"id": head, "message": message}}))
        self.assertEqual(
            {**FULL, "collect_quality": "true", "hy_decode_comparison": "true"},
            self.select(self.base, GITHUB_EVENT_PATH=str(payload)),
        )

    def test_comparison_requires_both_exact_current_footers(self):
        quality = "TypeLay-Quality-Capture: true"
        comparison = "TypeLay-Hy-Decode-Comparison: true"
        payload = self.root / "event.json"
        for message, expected in (
            (f"experiment\n\n{comparison}", FULL),
            (f"experiment\n\n{comparison}\n{quality}", FULL),
            (f"experiment\n\n{quality}\n{comparison}\n{comparison}", FULL),
            (f"experiment\n\n{quality}\n{quality}\n{comparison}", FULL),
            (f"experiment\n\n{quality}\nTypeLay-Hy-Decode-Comparison: false", FULL),
            (f"experiment\n\n{quality}\nTypeLay-Hy-Decode-Comparison: yes", FULL),
            (f"experiment\n\n{quality}\nTypeLay-Hy-Decode-Comparison:", FULL),
            (f"experiment\n\n{quality}\n{comparison} ", FULL),
            (f"experiment\n\n{quality}\n{comparison}\n\nMore prose.", FULL),
            (f"experiment\n{quality}\n{comparison}", FULL),
            (f"Discuss {comparison}", SHARED_ONLY),
            (f"quoted\n\n> {comparison}", SHARED_ONLY),
            (f"indented\n\n {comparison}", SHARED_ONLY),
            (f"example\n\n```text\n{quality}\n{comparison}\n```", SHARED_ONLY),
            (f"unclosed example\n\n~~~\n\n{quality}\n{comparison}", SHARED_ONLY),
        ):
            with self.subTest(message=message):
                self.reset(self.base)
                head = self.commit({"docs/note.md": "example"}, message=message)
                payload.write_text(json.dumps({"head_commit": {"id": head, "message": message}}))
                self.assertEqual(expected, self.select(self.base, GITHUB_EVENT_PATH=str(payload)))

    def test_comparison_requires_verified_push_not_environment_or_old_history(self):
        message = "experiment\n\nTypeLay-Quality-Capture: true\nTypeLay-Hy-Decode-Comparison: true"
        head = self.commit({"docs/note.md": "experiment"}, message=message)
        payload = self.root / "event.json"
        payload.write_text(json.dumps({"head_commit": {"id": head, "message": message}}))
        for event in (
            {"CREATED": "true"},
            {"FORCED": "true"},
            {"AFTER": self.base},
            {"BEFORE": head},
            {"BEFORE": "0" * 40},
            {"BEFORE": "a" * 40},
            {"EVENT_NAME": "pull_request"},
            {"GITHUB_EVENT_PATH": str(self.root / "missing.json")},
        ):
            with self.subTest(event=event):
                self.assertEqual(
                    FULL, self.select(self.base, **{"GITHUB_EVENT_PATH": str(payload), **event})
                )
        for branch in ("trunk", "work/windows-experience", "other"):
            self.assertEqual(FULL, self.select(self.base, branch, GITHUB_EVENT_PATH=str(payload)))
        for value in (
            {},
            [],
            {"head_commit": None},
            {"head_commit": {"id": self.base, "message": message}},
            {"head_commit": {"id": head, "message": message + " not exact"}},
        ):
            payload.write_text(json.dumps(value))
            self.assertEqual(FULL, self.select(self.base, GITHUB_EVENT_PATH=str(payload)))
        for raw in ("{", " " * (4 * 1024 * 1024 + 1)):
            payload.write_text(raw)
            self.assertEqual(FULL, self.select(self.base, GITHUB_EVENT_PATH=str(payload)))
        self.commit({"docs/current.md": "ordinary follow-up"})
        self.assertEqual(SHARED_ONLY, self.select(head, GITHUB_EVENT_PATH=str(payload)))

    def test_manual_comparison_boolean_is_exact_and_requires_collection(self):
        for mode in ("full", "quality-only"):
            self.assertEqual(
                {
                    **(FULL if mode == "full" else QUALITY_ONLY),
                    "collect_quality": "true",
                    "hy_decode_comparison": "true",
                },
                self.select(
                    self.base,
                    EVENT_NAME="workflow_dispatch",
                    CHECK_MODE=mode,
                    COLLECT_TRANSLATION_QUALITY="true",
                    HY_DECODE_COMPARISON="true",
                ),
            )
        for event, collect, comparison in (
            ("workflow_dispatch", "false", "true"),
            ("workflow_dispatch", "", "true"),
            ("workflow_dispatch", "TRUE", "true"),
            ("push", "true", "true"),
            ("pull_request", "true", "true"),
            ("workflow_dispatch", "true", "yes"),
            ("workflow_dispatch", "true", "TRUE"),
            ("workflow_dispatch", "true", " true"),
        ):
            with self.subTest(event=event, collect=collect, comparison=comparison):
                with self.assertRaises(subprocess.CalledProcessError):
                    self.select(
                        self.base,
                        EVENT_NAME=event,
                        COLLECT_TRANSLATION_QUALITY=collect,
                        HY_DECODE_COMPARISON=comparison,
                    )

    def test_quality_push_requires_exact_current_event_and_valid_branch_history(self):
        message = "experiment\n\nTypeLay-Quality-Capture: true"
        head = self.commit({"src/linux/only.py": "change"}, message=message)
        payload = self.root / "event.json"
        payload.write_text(json.dumps({"head_commit": {"id": head, "message": message}}))
        for event in (
            {"CREATED": "true"},
            {"FORCED": "true"},
            {"AFTER": self.base},
            {"BEFORE": head},
            {"BEFORE": "0" * 40},
            {"BEFORE": "a" * 40},
            {"EVENT_NAME": "pull_request"},
            {"GITHUB_EVENT_PATH": str(self.root / "missing.json")},
        ):
            with self.subTest(event=event):
                self.assertEqual(
                    FULL,
                    self.select(self.base, **{"GITHUB_EVENT_PATH": str(payload), **event}),
                )
        self.assertEqual(
            FULL,
            self.select(self.base, "work/windows-experience", GITHUB_EVENT_PATH=str(payload)),
        )
        for value in (
            {},
            [],
            {"head_commit": None},
            {"head_commit": {"id": self.base, "message": message}},
            {"head_commit": {"id": head, "message": message + " not exact"}},
        ):
            with self.subTest(value=value):
                payload.write_text(json.dumps(value))
                self.assertEqual(FULL, self.select(self.base, GITHUB_EVENT_PATH=str(payload)))
        for raw in ("{", " " * (4 * 1024 * 1024 + 1)):
            payload.write_text(raw)
            self.assertEqual(FULL, self.select(self.base, GITHUB_EVENT_PATH=str(payload)))

    def test_old_quoted_or_duplicate_quality_markers_do_not_trigger_collection(self):
        marker = "TypeLay-Quality-Capture: true"
        old = self.commit({"docs/old.md": "old request"}, message=f"old\n\n{marker}")
        self.commit({"docs/current.md": "ordinary follow-up"})
        payload = self.root / "event.json"
        payload.write_text(json.dumps({"head_commit": {"id": old, "message": marker}}))
        self.assertEqual(SHARED_ONLY, self.select(old, GITHUB_EVENT_PATH=str(payload)))
        for message, expected in (
            (f"Discuss {marker}", SHARED_ONLY),
            (f"quoted\n\n> {marker}", SHARED_ONLY),
            (f"indented\n\n {marker}", SHARED_ONLY),
            (f"documentation example\n\n```text\n{marker}\n```\n\nNot a request.", SHARED_ONLY),
            (f"unclosed code example\n\n```text\n\n{marker}", SHARED_ONLY),
            (f"tilde code example\n\n~~~text\n\n{marker}", SHARED_ONLY),
            (f"middle paragraph\n\n{marker}\n\nNot a request.", SHARED_ONLY),
            (marker, SHARED_ONLY),
            (f"duplicate\n\n{marker}\n{marker}", FULL),
        ):
            with self.subTest(message=message):
                self.reset(self.base)
                head = self.commit({"docs/new.md": "new"}, message=message)
                payload.write_text(json.dumps({"head_commit": {"id": head, "message": message}}))
                self.assertEqual(expected, self.select(self.base, GITHUB_EVENT_PATH=str(payload)))

    def test_full_collection_preserves_full_platform_checks(self):
        for collect in ("true", "false", ""):
            self.assertEqual(
                {**FULL, "collect_quality": str(collect == "true").lower()},
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
