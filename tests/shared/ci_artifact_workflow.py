"""A bounded check.yml contract, supporting only its current upload syntax."""

import ast
import re
import tempfile
from pathlib import Path

import yaml
from ci_artifact_contracts import MATRICES, PAYLOADS, UPLOADS

ACTION = "actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9"


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def selected_files(root, patterns, windows=False):
    """Match the baseline's literals, directory descendants and segment-local *.

    The pinned action's src/shared/search.ts and bundled @actions/glob 0.6.1
    enable implicit descendants, exclude hidden files, use nocase on Windows,
    and restrict trailing-slash matches to directories. Reference:
    https://github.com/actions/upload-artifact/tree/cf430e030ddbb5b0abf93d22962f4752f3646cd9
    These fixtures contain regular files only; no symlink or archive layout is simulated.
    """
    selected = set()
    for pattern in patterns.splitlines():
        pattern = pattern.strip()
        require(
            re.fullmatch(r"artifacts/[a-zA-Z0-9_./* -]*", pattern)
            and "**" not in pattern
            and not {"", ".", ".."}.intersection(pattern.rstrip("/").split("/")),
            f"Unsupported upload glob: {pattern!r}",
        )
        expression = re.escape(pattern.rstrip("/")).replace(r"\*", "[^/]*")
        descendants = r"/.*\Z" if pattern.endswith("/") else r"(?:/.*)?\Z"
        matcher = re.compile(expression + descendants, re.I if windows else 0)
        for path in root.rglob("*"):
            relative = path.relative_to(root)
            if path.is_file() and not any(part.startswith(".") for part in relative.parts):
                if matcher.fullmatch(relative.as_posix()):
                    selected.add(relative.as_posix())
    return selected


def verify_workflow(path):
    workflow = yaml.safe_load(path.read_text(encoding="utf-8"))
    packages_input = workflow["on"]["workflow_dispatch"]["inputs"]["upload_packages"]
    require(
        packages_input.get("type") == "boolean" and packages_input.get("default") is False,
        "Installation packages must default to disabled",
    )
    jobs = workflow["jobs"]
    for job_id, job in jobs.items():
        require(isinstance(job.get("steps"), list), f"Unsupported job steps in {job_id}")
        require("if" not in job, f"Unsupported job if in {job_id}: {job.get('if')}")
    actual_jobs = {
        job_id
        for job_id, job in jobs.items()
        if any("upload-artifact" in step.get("uses", "") for step in job["steps"])
    }
    require(actual_jobs == set(UPLOADS), f"Uncovered upload jobs: {actual_jobs ^ set(UPLOADS)}")
    with tempfile.TemporaryDirectory(prefix="typelay-artifact-contract-") as temporary:
        root = Path(temporary)
        samples = set(PAYLOADS)
        for contracts in UPLOADS.values():
            for contract in contracts.values():
                samples.update(p for evidence in contract.evidence for p in evidence.paths)
        for sample in samples:
            target = root / sample
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text("synthetic evidence\n", encoding="utf-8")
        for job_id in sorted(actual_jobs):
            job = jobs[job_id]
            matrix = job.get("strategy", {}).get("matrix", {})
            require(
                len(matrix) <= 1 and set(matrix) <= {"group", "shard"},
                f"Unsupported matrix in {job_id}: {matrix}",
            )
            groups = tuple(next(iter(matrix.values()))) if matrix else (None,)
            require(groups == MATRICES[job_id], f"Uncovered matrix groups in {job_id}: {groups}")
            uploads = [step for step in job["steps"] if "upload-artifact" in step.get("uses", "")]
            names = [step["with"]["name"].split("-${{ github.sha }}")[0] for step in uploads]
            require(len(names) == len(set(names)), f"Duplicate upload in {job_id}")
            require(
                set(names) == set(UPLOADS[job_id]),
                f"Uncovered uploads in {job_id}: {set(names) ^ set(UPLOADS[job_id])}",
            )
            for name, upload in zip(names, uploads, strict=True):
                label = f"{job_id}/{name}"
                contract = UPLOADS[job_id][name]
                require(
                    upload["uses"] == ACTION,
                    f"Unsupported upload action in {label}: {upload['uses']}",
                )
                options = upload["with"]
                require(
                    set(options) == {"name", "path", "if-no-files-found", "retention-days"},
                    f"Unsupported upload options in {label}: {set(options)}",
                )
                require(
                    options["if-no-files-found"] == contract.missing
                    and options["retention-days"] == 14,
                    f"Upload options changed in {label}",
                )
                verify_conditions(job_id, job, name, upload, contract, groups)
                selected = selected_files(root, options["path"], "windows" in job_id)
                required = {p for evidence in contract.evidence for p in evidence.paths}
                require(
                    required <= selected,
                    f"Missing evidence in {label}: {sorted(required - selected)}",
                )
                forbidden = set(PAYLOADS) - required
                require(
                    not (selected & forbidden),
                    f"Payload included in {label}: {sorted(selected & forbidden)}",
                )


def condition_matches(
    expression, *, group, step_ids, status="success", packages=False, outcomes=None, ready=True
):
    """Evaluate only current check.yml atoms, rejecting unknown syntax before evaluation."""
    original = expression or "success()"
    expression = original.strip()
    if expression.startswith("${{") and expression.endswith("}}"):
        expression = expression[3:-2].strip()
    has_status = bool(re.search(r"\b(always|cancelled|success)\(\)", expression))
    expression = (
        expression.replace("always()", "True")
        .replace("cancelled()", str(status == "cancelled"))
        .replace("success()", str(status == "success"))
    )
    expression = expression.replace(
        "startsWith(matrix.group, 'native')",
        str(isinstance(group, str) and group.startswith("native")),
    )
    expression = expression.replace(
        "hashFiles('artifacts/linux/qt-cooperative/fcitx5-qt.tar.gz')",
        repr("hash" if ready else ""),
    )
    expression = expression.replace("inputs.upload_packages", str(packages))
    expression = re.sub(r"matrix\.(group|shard)\b", repr(group), expression)

    def step_value(match):
        step_id, field = match.groups()
        require(step_id in step_ids, f"Unknown producer step in if: {original}")
        if field == "outcome":
            return repr((outcomes or {}).get(step_id, "success"))
        require(step_id == "fcitx" and field == "outputs.core-ready", f"Unsupported if: {original}")
        return repr("true" if ready else "")

    expression = re.sub(
        r"steps\.([a-zA-Z0-9-]+)\.(outcome|outputs\.[a-zA-Z0-9-]+)", step_value, expression
    )
    expression = expression.replace("&&", " and ").replace("||", " or ")
    expression = re.sub(r"!(?!=)", "not ", expression)
    try:
        tree = ast.parse(expression.strip(), mode="eval")
    except SyntaxError as error:
        raise AssertionError(f"Unsupported if: {original}") from error

    def evaluate(node):
        if isinstance(node, ast.Constant) and isinstance(node.value, (str, bool, int, type(None))):
            return node.value
        if isinstance(node, ast.BoolOp) and isinstance(node.op, (ast.And, ast.Or)):
            values = [evaluate(value) for value in node.values]
            return all(values) if isinstance(node.op, ast.And) else any(values)
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.Not):
            return not evaluate(node.operand)
        if (
            isinstance(node, ast.Compare)
            and len(node.ops) == 1
            and isinstance(node.ops[0], (ast.Eq, ast.NotEq))
        ):
            left, right = evaluate(node.left), evaluate(node.comparators[0])
            # Actions string comparisons ignore case.
            if isinstance(left, str) and isinstance(right, str):
                left, right = left.lower(), right.lower()
            return (left == right) if isinstance(node.ops[0], ast.Eq) else (left != right)
        raise AssertionError(f"Unsupported if: {original}")

    result = evaluate(tree.body)
    return bool(result) and (has_status or status == "success")


def verify_conditions(job_id, job, name, upload, contract, groups):
    steps = {step.get("id", step.get("name")): step for step in job["steps"]}
    step_ids = {step["id"] for step in job["steps"] if "id" in step}
    label = f"{job_id}/{name}"
    for group in groups:
        active = not contract.groups or group in contract.groups
        for status in ("success", "failure", "cancelled"):
            for packages in (False, True):
                for outcome in ("success", "failure", "skipped", "cancelled"):
                    expected = active
                    if contract.packages:
                        expected = expected and packages and status == "success"
                    elif contract.outcome:
                        expected = (
                            expected and status != "cancelled" and outcome in ("success", "failure")
                        )
                    actual = condition_matches(
                        upload.get("if"),
                        group=group,
                        step_ids=step_ids,
                        status=status,
                        packages=packages,
                        outcomes={contract.outcome: outcome},
                    )
                    require(
                        actual == expected,
                        f"Upload condition mismatch in {label}, group={group}, status={status}, packages={packages}, producer={outcome}: expected {expected}, got {actual}",
                    )
        for evidence in contract.evidence:
            require(evidence.producer in steps, f"Missing producer {evidence.producer} for {label}")
            producer = steps[evidence.producer]
            require(
                job["steps"].index(producer) < job["steps"].index(upload),
                f"Upload precedes producer in {label}/{evidence.producer}",
            )
            expected = not evidence.groups or group in evidence.groups
            # fcitx's script selects GTK scenarios after the shared core step.
            if evidence.producer == "fcitx":
                expected = str(group).startswith("native")
                require(
                    "native-gtk) scope=gtk3 ;;" in producer["run"]
                    and "native-gtk4) scope=gtk4 ;;" in producer["run"],
                    "Unsupported fcitx production scope",
                )
            if evidence.producer == "build" and evidence.groups:
                # The build step runs everywhere but builds Fcitx only in native groups.
                expected = True
                require(
                    'if [[ "$CI_LINUX_GROUP" == native* ]]; then' in producer["run"]
                    and "python eng/linux/cooperative/fcitx.py --reuse-third-party &"
                    in producer["run"],
                    "Unsupported Fcitx build production scope",
                )
            if evidence.producer == "qt-language-options":
                # A failed Qt probe is intentionally still allowed to produce language evidence.
                for outcome in ("success", "failure", "skipped", "cancelled"):
                    actual = condition_matches(
                        producer.get("if"), group=group, step_ids=step_ids, outcomes={"qt": outcome}
                    )
                    require(
                        actual == (expected and outcome in ("success", "failure")),
                        f"Producer condition mismatch in {label}/{evidence.producer}",
                    )
                require(
                    not condition_matches(
                        producer.get("if"), group=group, step_ids=step_ids, ready=False
                    ),
                    f"Producer missing dependency guard in {label}",
                )
            actual = condition_matches(producer.get("if"), group=group, step_ids=step_ids)
            require(
                actual == expected,
                f"Producer group mismatch in {label}/{evidence.producer}, group={group}: expected {expected}, got {actual}",
            )
