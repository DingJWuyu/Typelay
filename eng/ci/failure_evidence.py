"""Extract fixed diagnostic fields and assertion source, never runtime message bodies."""

import ast
import hashlib
import json
import re
import subprocess
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REPO_FILE = r"(?:tests|eng|src)/[\w./-]+"
PY_FRAME = re.compile(rf'  File "(?:[^"]*/)?({REPO_FILE}\.py)", line (\d+), in ([\w.<>]+)')
TOKEN = re.compile(r"[\w][\w.-]{0,179}", re.ASCII)


@dataclass(frozen=True)
class Observation:
    case: str | None = None
    stage: str | None = None
    assertion: str | None = None
    exception: str | None = None
    reason: str | None = None
    status: str = "failed"
    category: str | None = None

    def signature(self):
        if self.case and self.stage and self.assertion:
            return self.case, self.stage, self.assertion, self.exception, self.reason, self.category
        return None


def token(value):
    return value if isinstance(value, str) and TOKEN.fullmatch(value) else None


@lru_cache(maxsize=128)
def source_lines(sha, filename):
    if not re.fullmatch(r"[0-9a-f]{40}", sha) or ".." in filename.split("/"):
        return ()
    result = subprocess.run(
        ["git", "show", f"{sha}:{filename}"],
        cwd=ROOT,
        capture_output=True,
        encoding="utf-8",
        errors="replace",
    )
    return result.stdout.splitlines() if result.returncode == 0 else ()


def statement(sha, filename, number):
    lines = source_lines(sha, filename)
    return lines[number - 1].strip() if 0 < number <= len(lines) else None


@lru_cache(maxsize=128)
def python_statements(sha, filename):
    lines = source_lines(sha, filename)
    try:
        tree = ast.parse("\n".join(lines))
    except SyntaxError:
        return [], set()
    assertions = [node for node in ast.walk(tree) if isinstance(node, ast.Assert)]
    literals = {
        node.value
        for node in ast.walk(tree)
        if isinstance(node, ast.Constant) and isinstance(node.value, str)
    }
    return assertions, literals


def python_failures(sha, log):
    frames = []
    case = None
    for line in log.splitlines():
        heading = re.match(r"(?:FAIL|ERROR): (\w+) \(([\w.]+)\)", line)
        if heading:
            case = heading[2]
        if line == "Traceback (most recent call last):":
            frames = []
        match = PY_FRAME.fullmatch(line.replace("\\", "/"))
        if match:
            frames.append(match.groups())
        error = re.fullmatch(r"([\w.]+(?:Error|Exception))(?:: (.*))?", line)
        if not error or not frames:
            continue
        path, number, function = frames[-1]
        code = statement(sha, path, int(number))
        assertion_nodes, _ = python_statements(sha, path)
        for node in assertion_nodes:
            if node.lineno <= int(number) <= node.end_lineno:
                lines = source_lines(sha, path)
                code = " ".join(line.strip() for line in lines[node.lineno - 1 : node.end_lineno])
                break
        if function in {"wait", "wait_for", "click", "publish"} and len(frames) > 1:
            function = frames[-2][2] + "." + function
        # A parent runner can reprint the child traceback and then surface its exit code.
        # Only the original test, not that wrapper, observed the failing condition.
        if error[1] == "subprocess.CalledProcessError" or (
            code and "nested niri/GTK contract failed; inspect nested-niri.log" in code
        ):
            frames = []
            continue
        # These codes are emitted by the capture/publication contracts. Other exception
        # messages and the object printed after assert admitted may contain source text.
        if error[2] == "native-read-declined" and code and "capture_status" in code:
            code += " (native-read-declined)"
        elif code == "assert admitted, failure" and (error[2] or "").startswith(
            "{'status': 'failed-before-publication',"
        ):
            code += " (failed-before-publication)"
        elif error[2] and any(error[2] in python_statements(sha, file)[1] for file, _, _ in frames):
            if not code or error[2] not in code:
                code = error[2]
        elif not code or not (code.startswith("assert ") or code.startswith("self.assert")):
            code = None
        scenario = next(
            (
                frame[2]
                for frame in reversed(frames[:-1])
                if frame[0] == path
                and frame[2].startswith(("test_", "verify_", "run_"))
                and frame[2] not in ("run_case", "run_session")
            ),
            None,
        )
        yield Observation(
            case or (f"{path}::{scenario}" if scenario else path), function, code, error[1]
        )
        frames = []


WINDOWS_INDEX = "Verify Index default install, real inference and owned package transitions"
LINUX_INDEX = "Verify Index default preparation, actual host and package transitions"
BROWSER = "Verify cooperative browser input with the shipped app and real model"
CONTRACTS = "Verify Windows behavior and contracts"


def index_rows(report, include_passed):
    failure, _, code = str(report.get("failure") or "").partition(": ")
    for name, status in (report.get("outcomes") or report.get("stages") or {}).items():
        if status == "failed":
            yield Observation(
                token(name),
                token(report.get("setupStage")) or token(name),
                token(report.get("failureReason")) or token(code),
                token(failure),
                category=token(report.get("setupCategory")),
            )
        elif status == "not-run" or (include_passed and status == "passed"):
            yield Observation(token(name), token(name), status=status)
    for item in report.get("cleanup") or []:
        yield Observation("cleanup", "cleanup", token(item))


def browser_rows(report, include_passed):
    for scene in report.get("scenes") or []:
        if not isinstance(scene, dict):
            continue
        status = scene.get("outcome")
        if status in {"failed", "not-run"} or (include_passed and status == "passed"):
            yield Observation(
                token(scene.get("name")),
                token(scene.get("stage")),
                token(scene.get("failure")) if status == "failed" else None,
                token(scene.get("errorType")) if status == "failed" else None,
                category=token(scene.get("errorCategory")) if status == "failed" else None,
                status=status,
            )
    for item in report.get("cleanupErrors") or []:
        if isinstance(item, str):
            yield Observation("cleanup", "cleanup", token(item))
            continue
        yield Observation(
            "cleanup",
            token(item.get("resource")),
            "cleanup-failed",
            token(item.get("errorType") or item.get("error")),
        )


SCENE_REPORTS = {
    BROWSER: "browser-cooperation/result.json",
    "Verify the shipped Hy ABI5 worker and production process owner": "hy-production/result.json",
    "Verify Hy install select preview and remove through Settings": "hy-packages/result.json",
    "Verify real classic Notepad through shipped app Settings": "ordinary-notepad/result.json",
}
CONTINUOUS = "Verify continuous translation with the real local model"


def contract_case(sha, name):
    method, separator, parameters = name.partition("(")
    if not token(method):
        return None
    if not separator:
        return method
    if re.fullmatch(r"(?:[A-Za-z_]\w*: (?:True|False|-?\d+)(?:, )?)+\)", parameters):
        return name
    literal = re.fullmatch(r'\w+: ("[A-Za-z0-9_-]+")\)', parameters)
    owner = method.rsplit(".", 2)
    if literal and len(owner) == 3:
        namespace, cls, _ = owner
        for platform in ("windows", "shared"):
            path = f"tests/{platform}/{namespace}/{cls}.cs"
            if any(f"[InlineData({literal[1]})]" in line for line in source_lines(sha, path)):
                return name
    # Test data may contain source text. Keep distinct theory cases without publishing it.
    return method + " [" + hashlib.sha256(parameters.encode()).hexdigest()[:12] + "]"


def contract_row(sha, name, diagnostic):
    stage = assertion = exception = None
    match = re.search(r"Assert\.\w+\(\)", diagnostic)
    if match:
        assertion = match[0]
    match = re.search(r"\b([\w.]+Exception)\s*:", diagnostic)
    if match:
        exception = match[1].rsplit(".", 1)[-1]
    frame = re.search(
        rf" in (?:.*[/\\])?({REPO_FILE}\.cs):line (\d+)", diagnostic.replace(chr(92), "/")
    )
    if frame:
        stage = frame[1].rsplit("/", 1)[-1]
        line = statement(sha, frame[1], int(frame[2]))
        if line and ("Assert." in line or "Require(" in line):
            assertion = line
        elif not assertion:
            stage += ":" + frame[2]
    if re.search(r'"operation"\s*:\s*"source-click-focus-not-delivered"', diagnostic):
        stage, assertion = "focus-wait", "source-click-focus-not-delivered"
    return Observation(
        contract_case(sha, name), stage or ("assert" if assertion else None), assertion, exception
    )


def contracts_from_log(sha, log):
    failures = list(re.finditer(r"^ +Failed (TypeLay\.[^\n]+?) \[[^\n]+\]$", log, re.MULTILINE))
    for index, match in enumerate(failures):
        end = failures[index + 1].start() if index + 1 < len(failures) else len(log)
        yield contract_row(sha, match[1], log[match.end() : end])


def contracts_from_trx(sha, data):
    for element in ET.fromstring(data).iter():
        if element.tag.rsplit("}", 1)[-1] != "UnitTestResult":
            continue
        name = element.get("testName", "")
        if element.get("outcome") == "Failed":
            yield contract_row(sha, name, "\n".join(element.itertext()))
        else:
            status = "passed" if element.get("outcome") == "Passed" else "not-run"
            yield Observation(contract_case(sha, name), status=status)


def setup_rows(log):
    for match in re.finditer(r"^TYPELAY_SETUP_FAILURE=(\{[^\n]+\})$", log, re.MULTILINE):
        try:
            item = json.loads(match[1])
        except ValueError:
            continue
        if not isinstance(item, dict):
            continue
        http = item.get("httpStatus")
        yield Observation(
            token(item.get("resource")),
            token(item.get("stage")),
            f"HTTP {http}"
            if type(http) is int and 100 <= http <= 599
            else token(item.get("networkReason")),
            reason=token(item.get("networkReason")),
            category=token(item.get("category")),
        )


def observe_step(sha, step_name, log, reports, *, successful=False):
    if step_name in (WINDOWS_INDEX, LINUX_INDEX):
        rows = list(index_rows(reports.get("index-production/result.json") or {}, successful))
    elif step_name in SCENE_REPORTS:
        rows = list(browser_rows(reports.get(SCENE_REPORTS[step_name]) or {}, successful))
        result = reports.get(SCENE_REPORTS[step_name]) or {}
        if result.get("outcome") == "failed" and not any(row.status == "failed" for row in rows):
            rows.append(
                Observation(
                    stage=token(result.get("stage")), assertion=token(result.get("failure"))
                )
            )
        if step_name == BROWSER and not rows:
            match = re.search(
                r"Browser cooperation: failed; stage=([\w-]+); failure=([\w-]+)\.", log
            )
            if match:
                rows.append(Observation(stage=token(match[1]), assertion=token(match[2])))
    elif step_name == CONTINUOUS:
        result = reports.get("continuous-translation/result.json") or {}
        scenes = [
            dict(value, name=name)
            for name, value in result.items()
            if isinstance(value, dict) and "outcome" in value
        ]
        rows = list(
            browser_rows(
                {"scenes": scenes, "cleanupErrors": result.get("cleanupErrors", [])}, successful
            )
        )
    else:
        rows = list(contracts_from_log(sha, log))
        if step_name in (CONTRACTS, "Verify shared behavior without Windows"):
            by_case = {row.case: row for row in rows}
            for row in reports.get("contracts", []):
                if successful or row.status != "passed":
                    by_case[row.case] = row
            if not by_case and step_name == CONTRACTS:
                for case in reports.get("contract-summary", {}).get("failedTests", []):
                    name = contract_case(sha, case.get("testName", ""))
                    by_case[name] = Observation(name)
            rows = list(by_case.values())
    if not successful:
        rows.extend([*setup_rows(log), *python_failures(sha, log)])
    if not successful and not any(row.status == "failed" for row in rows):
        rows.insert(0, Observation())
    return list(dict.fromkeys(rows))
