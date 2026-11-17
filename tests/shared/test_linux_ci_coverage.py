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
        if not runnable_condition(job.get("if"), job, {}) or job.get("continue-on-error"):
            continue
        previous = {}
        for step in job.get("steps", []):
            runnable = runnable_condition(step.get("if"), job, previous) and not step.get(
                "continue-on-error"
            )
            if "id" in step:
                previous[step["id"]] = runnable
            if not runnable:
                continue
            lines = step.get("run", "").splitlines()
            # The workflow uses straight-line commands. Do not infer execution
            # from text inside shell branches, functions or other control flow.
            if any(not straight_line_command(line) for line in lines):
                continue
            for line in lines:
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
                if words[1:6] == ["-m", "unittest", "discover", "-s", "tests/linux"]:
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


def runnable_condition(condition, job, previous):
    """Recognize this workflow's success-path guards; unknown guards fail closed."""
    if condition is None or condition is True:
        return True
    if not isinstance(condition, str):
        return False
    condition = condition.removeprefix("${{").removesuffix("}}").strip()
    for term in condition.split("&&"):
        term = term.strip()
        if term in ("!cancelled()", "success()", "always()", "true"):
            continue
        if match := re.fullmatch(r"steps\.([\w-]+)\.outcome == 'success'", term):
            if previous.get(match[1]):
                continue
        if match := re.fullmatch(r"matrix\.(\w+) == '([^']+)'", term):
            matrix = job.get("strategy", {}).get("matrix", {})
            if match[2] in matrix.get(match[1], []) and not any(
                entry.get(match[1]) == match[2] for entry in matrix.get("exclude", [])
            ):
                continue
        return False
    return True


def straight_line_command(line):
    line = line.strip()
    if not line or line.startswith("#"):
        return True
    # The resource-isolation test temporarily changes a runner sysctl and
    # restores it with an EXIT trap. These are its existing setup commands.
    if re.fullmatch(r"namespace_policy=\$\(sysctl -n [\w.]+\)", line):
        return True
    try:
        words = shlex.split(line, comments=True)
    except ValueError:
        return False
    if words[:3] == ["sudo", "sysctl", "-w"] and len(words) == 4:
        return True
    if len(words) == 3 and words[0] == "trap" and words[2] == "EXIT":
        return True
    while words and re.fullmatch(r"[A-Z_][A-Z_0-9]*=.*", words[0]):
        words.pop(0)
    if words[:2] == ["dbus-run-session", "--"]:
        words = words[2:]
    return words[:1] == ["python"] and words[-1:] == ["-v"]


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
                {"run": "python -m unittest discover -s tests/linux -p test_new_regression.py -v"}
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

    def test_disabled_or_conditional_commands_do_not_count_as_execution(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            workflow = root / ".github/workflows/check.yml"
            workflow.parent.mkdir(parents=True)
            module = "tests/linux/test_example.py"
            command = "python -m unittest discover -s tests/linux -p test_example.py -v"
            cases = (
                {"if": "${{ false }}", "run": command},
                {"if": "${{ matrix.group == 'missing' }}", "run": command},
                {"run": f"if false; then\n{command}\nfi"},
                {"run": f"function unused() {{\n{command}\n}}"},
            )
            for step in cases:
                with self.subTest(step=step):
                    workflow.write_text(yaml.safe_dump({"jobs": {"contracts": {"steps": [step]}}}))
                    self.assertEqual(ci_modules(root, {module}), set())

    def test_script_without_unittest_main_cannot_silently_count_as_coverage(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            module = root / "tests/linux/test_without_main.py"
            module.parent.mkdir(parents=True)
            module.write_text(
                "import unittest\nclass Regression(unittest.TestCase):\n"
                "    def test_failure(self): self.fail('must execute this regression')\n"
            )
            workflow = root / ".github/workflows/check.yml"
            workflow.parent.mkdir(parents=True)
            command = "python tests/linux/test_without_main.py -v"
            workflow.write_text(
                yaml.safe_dump({"jobs": {"contracts": {"steps": [{"run": command}]}}})
            )
            result = subprocess.run(
                [sys.executable, str(module), "-v"], capture_output=True, text=True, timeout=10
            )
            self.assertEqual(result.returncode, 0)
            self.assertEqual(result.stderr, "")
            with self.assertRaisesRegex(AssertionError, "missing from CI.*test_without_main"):
                self.check_coverage(root, {})

    def test_private_session_exceptions_require_a_current_module_and_reason(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            module = "tests/linux/test_private_example.py"
            (root / module).parent.mkdir(parents=True)
            (root / module).write_text("raise RuntimeError('must not import')\n")
            workflow = root / ".github/workflows/check.yml"
            workflow.parent.mkdir(parents=True)
            workflow.write_text("jobs: {}\n")
            self.check_coverage(root, {module: "Requires the private nested display"})
            with self.assertRaisesRegex(AssertionError, "Missing private-session reason"):
                self.check_coverage(root, {module: "  "})
            with self.assertRaisesRegex(AssertionError, "Stale private-session entry"):
                self.check_coverage(root, {"tests/linux/test_deleted.py": "Private display"})


if __name__ == "__main__":
    unittest.main()
