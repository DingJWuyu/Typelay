"""Exercise the Actions selector against real, isolated Git histories."""

import json
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
    "quality_only": "false",
    "collect_quality": "false",
    "hy_decode_comparison": "false",
    "fixed_model_diagnostics": "false",
}
LINUX = {**FULL, "windows": "false"}
WINDOWS = {**FULL, "linux": "false"}
SHARED_ONLY = {**FULL, "windows": "false", "linux": "false"}
QUALITY_ONLY = {**LINUX, "quality_only": "true", "collect_quality": "true"}


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
        # Histories stay in the index/object database. Permit Git paths that
        # NTFS cannot check out, without changing the host's Git configuration.
        self.git("config", "--local", "core.protectNTFS", "false")
        self.base = self.commit({"README.md": "base"})

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

    def select(self, before, branch="work/feature", **event):
        output = self.root / "output"
        output.write_text("")
        subprocess.run(
            [sys.executable, str(SELECTOR)],
            cwd=self.repo,
            env={
                **self.env,
                "GITHUB_OUTPUT": str(output),
                "GITHUB_STEP_SUMMARY": str(self.root / "summary.md"),
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

    def test_current_platform_push_ignores_old_cross_platform_history(self):
        for platform, branch, expected in (
            ("linux", "work/linux-usability", LINUX),
            ("windows", "work/windows-experience", WINDOWS),
            ("windows", "work/new-feature", WINDOWS),
            ("linux", "work/feature/subtask", LINUX),
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
                "tests/shared/TypeLay.Core.Tests/BehaviorTests.cs",
                "global.json",
                "other/file",
                ".github/workflows/check.yml",
                ".github/CODEOWNERS",
            ):
                with self.subTest(branch=branch, path=path):
                    self.reset(self.base)
                    before = self.commit({path: "existing"})
                    self.commit({path: "changed", "docs/notes.md": "docs"})
                    self.assertEqual(FULL, self.select(before, branch))

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
            "other",
        ):
            self.assertEqual(FULL, self.select(self.base, branch))
        for event in ("pull_request", "workflow_dispatch"):
            self.assertEqual(FULL, self.select(self.base, EVENT_NAME=event))

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

    def test_pr_evidence_distinguishes_head_base_and_tested_merge(self):
        head = self.commit({"docs/change.md": "PR change"})
        self.reset(self.base)
        base = self.commit({"docs/base.md": "base change"})
        tested = self.commit({"docs/change.md": "PR change"}, parents=[base, head])
        evidence = self.root / "evidence.json"
        event = self.root / "event.json"
        for base_ref in ("hitl", "work/spec3-t01"):
            with self.subTest(base_ref=base_ref):
                event.write_text(
                    json.dumps(
                        {
                            "number": 42,
                            "pull_request": {
                                "head": {"ref": "work/spec3-t02", "sha": head},
                                "base": {"ref": base_ref, "sha": base},
                            },
                        }
                    )
                )
                self.assertEqual(
                    FULL,
                    self.select(
                        self.base,
                        EVENT_NAME="pull_request",
                        GITHUB_EVENT_PATH=str(event),
                        GITHUB_SHA=tested,
                        GITHUB_REF="refs/pull/42/merge",
                        CI_EVIDENCE_PATH=str(evidence),
                    ),
                )
                report = json.loads(evidence.read_text())
                self.assertEqual(report["tested_commit"], tested)
                self.assertEqual(report["head"], {"ref": "work/spec3-t02", "sha": head})
                self.assertEqual(report["base"], {"ref": base_ref, "sha": base})
                self.assertEqual(report["pull_request"], 42)

    def test_hitl_push_keeps_full_checks_and_records_actual_commits(self):
        after = self.commit({"docs/change.md": "docs only"})
        event = self.root / "event.json"
        event.write_text(json.dumps({"before": self.base, "after": after}))
        evidence = self.root / "evidence.json"
        self.assertEqual(
            FULL,
            self.select(
                self.base,
                branch="hitl",
                GITHUB_EVENT_PATH=str(event),
                GITHUB_REF="refs/heads/hitl",
                GITHUB_SHA=after,
                CI_EVIDENCE_PATH=str(evidence),
            ),
        )
        report = json.loads(evidence.read_text())
        self.assertEqual(report["tested_commit"], after)
        self.assertEqual(report["head"], {"ref": "refs/heads/hitl", "sha": after})
        self.assertEqual(report["base"], {"ref": "refs/heads/hitl", "sha": self.base})

    def test_evidence_rejects_a_checkout_different_from_the_event(self):
        event = self.root / "event.json"
        event.write_text("{}")
        evidence = self.root / "evidence.json"
        with self.assertRaises(subprocess.CalledProcessError):
            self.select(
                self.base,
                GITHUB_EVENT_PATH=str(event),
                GITHUB_REF="refs/heads/hitl",
                GITHUB_SHA="f" * 40,
                CI_EVIDENCE_PATH=str(evidence),
            )
        self.assertFalse(evidence.exists())

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


if __name__ == "__main__":
    unittest.main()
