"""Keep default Linux discovery modules connected to actual Check commands."""

import fnmatch
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
# Only modules that require the independent verify-typelay private session belong
# here. Each entry must explain why CI cannot run it. No current module needs it.
PRIVATE_SESSION_ONLY = {}


def discovery_modules(root):
    """Find unittest's default module paths without importing desktop code."""
    directory = root / "tests/linux"
    return {
        path.relative_to(root).as_posix()
        for path in directory.rglob("test*.py")
        if path.is_file()
        and re.fullmatch(r"test\w*\.py", path.name)
        and all(
            (parent / "__init__.py").is_file()
            for parent in path.parents
            if parent != directory and directory in parent.parents
        )
    }


def ci_modules(root, modules):
    workflow = yaml.safe_load((root / ".github/workflows/check.yml").read_text(encoding="utf-8"))
    covered = set()
    for job in workflow["jobs"].values():
        if job.get("if") is False or job.get("continue-on-error"):
            continue
        for step in job.get("steps", []):
            if step.get("if") is False or step.get("continue-on-error"):
                continue
            for line in step.get("run", "").splitlines():
                try:
                    words = shlex.split(line, comments=True)
                except ValueError:
                    continue  # Unrelated shell blocks can span several lines.
                while words and re.fullmatch(r"[A-Z_][A-Z_0-9]*=.*", words[0]):
                    words.pop(0)
                if words[:2] == ["dbus-run-session", "--"]:
                    words = words[2:]
                if words[:1] != ["python"]:
                    continue
                if len(words) > 1 and words[1] in modules and words[2:] == ["-v"]:
                    covered.add(words[1])
                elif words[1:6] == ["-m", "unittest", "discover", "-s", "tests/linux"]:
                    if words[6:] == ["-v"]:
                        pattern = "test*.py"
                    elif len(words) == 9 and words[6] == "-p" and words[8] == "-v":
                        pattern = words[7]
                    else:
                        continue
                    covered.update(
                        path for path in modules if fnmatch.fnmatch(Path(path).name, pattern)
                    )
    return covered


class LinuxCiCoverageTests(unittest.TestCase):
    def check_coverage(self, root, private_session_only=PRIVATE_SESSION_ONLY):
        modules = discovery_modules(root)
        self.assertTrue(modules, "Linux discovery found no test modules")
        for path, reason in private_session_only.items():
            self.assertIn(path, modules, "Stale private-session entry")
            self.assertTrue(reason.strip(), f"Missing private-session reason: {path}")
        missing = modules - ci_modules(root, modules) - private_session_only.keys()
        self.assertFalse(missing, f"Linux test modules missing from CI: {sorted(missing)}")

    def test_default_discovery_modules_run_in_ci_or_have_a_private_session_reason(self):
        self.check_coverage(ROOT)

    def test_new_module_fails_until_a_ci_command_runs_it_without_importing_it(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            tests = root / "tests/linux"
            tests.mkdir(parents=True)
            (tests / "test_new_regression.py").write_text("raise RuntimeError('must not import')\n")
            workflow = root / ".github/workflows/check.yml"
            workflow.parent.mkdir(parents=True)
            shutil.copyfile(ROOT / ".github/workflows/check.yml", workflow)
            with self.assertRaisesRegex(AssertionError, "missing from CI.*test_new_regression"):
                self.check_coverage(root, {})
            value = yaml.safe_load(workflow.read_text())
            value["jobs"]["linux"]["steps"].append(
                {"run": "python tests/linux/test_new_regression.py -v"}
            )
            workflow.write_text(yaml.safe_dump(value))
            self.check_coverage(root, {})

    def test_root_ruff_discovers_every_verify_typelay_python_script(self):
        scripts = {
            str(path) for path in (ROOT / ".agents/skills/verify-typelay/scripts").rglob("*.py")
        }
        self.assertTrue(scripts)
        result = subprocess.run(
            [sys.executable, "-m", "ruff", "check", ".", "--show-files"],
            cwd=ROOT,
            check=True,
            capture_output=True,
            text=True,
            timeout=30,
        )
        missing = scripts - set(result.stdout.splitlines())
        self.assertFalse(missing, f"Verify-typelay scripts missing from Ruff: {sorted(missing)}")


if __name__ == "__main__":
    unittest.main()
