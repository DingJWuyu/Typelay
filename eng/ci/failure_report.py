"""Report the failed steps of one Actions attempt against its historical hitl baseline."""

import html
import json
import os
import re
import sys
from collections import Counter
from dataclasses import replace
from pathlib import Path

from failure_actions import (
    REPORTS,
    Actions,
    Attempt,
    MissingEvidence,
    fixed_baseline,
    historical,
    select_run,
)
from failure_evidence import ROOT, generic_assertion, observe_step

REPORT_JOB = "Failure comparison"
BAD = {"failure", "timed_out", "cancelled", "action_required", "startup_failure"}
ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")
TIMESTAMP = re.compile(r"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.\d+)?Z ")


def numbered_steps(job):
    seen = Counter()
    for step in job["steps"]:
        name = step["name"]
        seen[name] += 1
        yield (name, seen[name]), step


def compare(key, row, base, records):
    if not base or row.status != "failed":
        return "无法对照"
    if base["conclusion"] not in ("success", "failure"):
        return f"无法对照（基线分片 {base['conclusion']}）"
    previous = dict(numbered_steps(base)).get(key)
    if not previous:
        return "无法对照（基线没有该步骤）"
    if previous["conclusion"] not in ("success", "failure"):
        return f"无法对照（基线步骤 {previous['conclusion']}）"
    peers = records.get(previous["number"], [])
    if any(p.case and p.case == row.case and p.status in ("not-run", "skipped") for p in peers):
        return "无法对照（基线用例未执行）"
    if previous["conclusion"] == "success":
        verdict = (
            "同分片通过"
            if base["conclusion"] == "success"
            else "该步骤通过，同分片记录了另一条失败"
        )
        if row.case and any(p.case == row.case and p.status == "passed" for p in peers):
            return verdict
        return verdict + "，用例无法对照（缺少该用例的执行记录）"
    if row.signature() and any(p.signature() == row.signature() for p in peers):
        return "基线也记录了这条失败"
    failures = [p for p in peers if p.status == "failed"]
    if row.case and failures and all(different_failure(row, peer) for peer in failures):
        return "同分片记录了另一条失败"
    return "无法对照（用例、阶段或断言证据不足）"


def different_failure(row, peer):
    if row.case and peer.case and row.case != peer.case:
        return True
    if generic_assertion(row.assertion) or generic_assertion(peer.assertion):
        return False
    return any(
        a and b and a != b
        for a, b in (
            (row.stage, peer.stage),
            (row.assertion, peer.assertion),
            (row.exception, peer.exception),
            (row.category, peer.category),
            (row.reason, peer.reason),
        )
    )


def cell(value):
    return html.escape(str(value or "未知"), quote=False).replace("|", "&#124;").replace("\n", " ")


def step_link(job, step):
    return f"[{cell(step['name'])}]({job['html_url']}#step:{step['number']}:1)"


def table(job, records, base=None, previous=None):
    yield "| Actions 步骤 | 状态 | 用例 | 阶段 | 断言或失败码 | 异常 | 生产者分类 | 原因 | 对照 |"
    yield "| --- | --- | --- | --- | --- | --- | --- | --- | --- |"
    for key, step in numbered_steps(job):
        for row in records.get(step["number"], []):
            if row.status == "passed":
                continue
            fields = [
                step_link(job, step),
                cell(row.status),
                cell(row.case),
                cell(row.stage),
                cell(row.assertion),
                cell(row.exception),
                cell(row.category),
                cell(row.reason),
            ]
            verdict = (
                compare(key, row, base, previous or {}) if previous is not None else "基线记录"
            )
            yield "| " + " | ".join([*fields, verdict]) + " |"


def baseline_execution(base):
    for key, step in numbered_steps(base):
        status = step["conclusion"]
        start, end = step.get("started_at"), step.get("completed_at")
        if status in ("success", "failure", "timed_out") or (
            status == "cancelled" and start and end and start < end
        ):
            yield key
        # Steps after a baseline failure may run only to collect that failure's diagnostics.
        if status in BAD:
            break


def skipped_steps(job, base):
    ran_on_baseline = set(baseline_execution(base)) if base else set()
    after_failure = False
    blocked = []
    others = 0
    for key, step in numbered_steps(job):
        if step["conclusion"] in ("failure", "timed_out"):
            after_failure = True
        elif step["conclusion"] == "skipped":
            if after_failure and key in ran_on_baseline and not step["name"].startswith("Post "):
                blocked.append(step_link(job, step))
            else:
                others += 1
    if blocked:
        yield ""
        yield f"受阻检查（失败后 skipped，同分片基线执行过）：{len(blocked)} 项。"
        yield ""
        yield from ("- " + link for link in blocked)
    if others:
        yield ""
        yield f"其余 skipped 步骤共 {others} 项。"


def format_job(job, records, base, previous, problems, base_problems):
    lines = ["", f"### [{cell(job['name'])}]({job['html_url']}) · {cell(job['conclusion'])}", ""]
    lines.extend(problems)
    if job["conclusion"] not in ("success", "failure"):
        lines.append(f"该分片为 {cell(job['conclusion'])}，无法对照。")
    if records:
        if problems:
            lines.append("")
        lines.extend(table(job, records, base, previous))
    elif job["conclusion"] != "success":
        lines.append("未取得失败步骤的分项记录。用例、阶段、断言和原因均为未知。")
    lines.extend(skipped_steps(job, base))
    if base:
        lines.extend(["", f"基线分片 [{cell(base['conclusion'])}]({base['html_url']})。"])
        lines.extend(base_problems)
        if any(r.status != "passed" for rows in previous.values() for r in rows):
            lines.extend(["", *table(base, previous)])
    else:
        lines.append("缺少同工作流、同分片的基线记录，无法对照。")
    return lines


def publish(lines, page, cli):
    for line in lines:
        print(line, file=page)
        print("TYPELAY_REPORT " + line, file=cli)


def step_logs(raw, steps):
    """Actions rounds step times to seconds. Never borrow output from another failed step."""
    active = [s for s in steps if s["conclusion"] in BAD and s.get("started_at")]
    result = {s["number"]: [] for s in active}
    finished = set()
    in_command = False
    for line in raw.splitlines():
        timed = TIMESTAMP.match(line)
        if not timed:
            continue
        second = timed[1]
        body = ANSI.sub("", line[timed.end() :])
        if body.startswith("##[group]Run "):
            in_command = True
        elif body == "##[endgroup]":
            in_command = False
        elif not in_command:
            possible = [
                s
                for s in active
                if s["number"] not in finished
                and s["started_at"][:19] <= second <= (s.get("completed_at") or "9999")[:19]
            ]
            if possible:
                # Within a shared boundary second, the runner finishes the earlier step first.
                number = possible[0]["number"]
                result[number].append(body)
                if body.startswith("##[error]Process completed with exit code "):
                    finished.add(number)
    return {number: "\n".join(lines) for number, lines in result.items()}


def observations(attempt, job, relevant=()):
    steps = [
        s
        for key, s in numbered_steps(job)
        if s["conclusion"] in BAD or (key in relevant and s["conclusion"] == "success")
    ]
    notes = []
    reports, logs = {}, {}
    if steps:
        try:
            reports, notes = attempt.reports(job, {s["name"] for s in steps})
        except MissingEvidence as error:
            notes.append(str(error) + "。")
        if any(s["conclusion"] in BAD for s in steps):
            try:
                raw = attempt.actions.read(f"jobs/{job['id']}/logs", raw=True)
                logs = step_logs(raw.decode("utf-8-sig", errors="replace"), job["steps"])
            except MissingEvidence as error:
                notes.append("原始日志：" + str(error) + "。")
    records = {}
    for step in steps:
        success = step["conclusion"] == "success"
        name = step["name"]
        log = logs.get(step["number"], "")
        try:
            rows = observe_step(attempt.source, name, log, reports, successful=success)
        except (AttributeError, TypeError, ValueError):
            notes.append(f"{REPORTS.get(name, '分项报告')}：字段格式无效。")
            rows = observe_step(attempt.source, name, log, {}, successful=success)
        if not success and step["conclusion"] != "failure":
            rows = [
                replace(row, status=step["conclusion"]) if row.status == "failed" else row
                for row in rows
            ]
        if rows:
            records[step["number"]] = rows
    return records, notes


def reference(run):
    return f"[{run['id']}/{run['run_attempt']}]({run['html_url']}/attempts/{run['run_attempt']})"


def find_baseline(actions, run, event):
    history = []
    expected = None
    query = actions.pages(
        f"workflows/{run['workflow_id']}/runs",
        "workflow_runs",
        branch="hitl",
        event="push",
        created="<=" + run["created_at"],
    )
    for page in query:
        history.extend(page)
        expected = fixed_baseline(ROOT, run, event, history)
        found = select_run(ROOT, run, expected, history)
        if found:
            while found["run_attempt"] > 1 and found["run_started_at"] > run["created_at"]:
                found = actions.read(f"runs/{found['id']}/attempts/{found['run_attempt'] - 1}")
            return expected, Attempt(actions, found)
        if expected is None and history and any(historical(run, r) for r in history):
            break
    return expected, None


def candidate_jobs(jobs, candidate_job, head):
    return {
        name: job
        for name, job in jobs.items()
        if name == candidate_job or name.endswith(f" at {head} / {candidate_job}")
    }


def report(
    actions, run, event, *, candidate_job=None, candidate_head=None, tested=None, source_tree=None
):
    current = Attempt(actions, run)
    if candidate_job:
        current.sha = tested or "未知"
        current.source = source_tree or ""
    lines = [
        "## 必过检查失败对照",
        "",
        f"被测提交 `{cell(current.sha)}`，run/attempt {reference(run)}。",
    ]
    base = None
    if candidate_job:
        anchor = fixed_baseline(ROOT, run, event, [])
        lines.append(
            f"本次 hitl 基线 `{cell(anchor)}`。同一工作流仅测试 PR 合并候选，没有独立执行此基线，无法对照。"
        )
        if not current.source:
            lines.append("未取得可验证的被测源码树，源码断言未知。")
    else:
        try:
            anchor, base = find_baseline(actions, run, event)
            lines.append(f"本次 hitl 基线 `{cell(anchor)}`。")
            if base:
                role = "按同线祖先回溯" if base.sha != anchor else "命中指定基线"
                lines.append(
                    f"{role}：`{base.sha}`，run/attempt {reference(base.run)}，"
                    f"运行状态 {cell(base.run['status'])} / {cell(base.run['conclusion'])}。"
                )
            else:
                lines.append("没有找到同工作流的历史基线运行，无法对照。")
        except MissingEvidence as error:
            anchor = fixed_baseline(ROOT, run, event, [])
            lines.append(
                f"本次 hitl 基线 `{cell(anchor)}`。基线资料不可读，无法对照。" + str(error)
            )
    try:
        jobs = current.jobs
    except MissingEvidence as error:
        return [*lines, "当前 attempt 的 job 清单缺失。" + str(error)]
    if not jobs:
        return [*lines, "当前 attempt 的 job 清单缺失，无法对照。"]
    if candidate_job:
        jobs = candidate_jobs(jobs, candidate_job, candidate_head)
        if len(jobs) != 1:
            return [*lines, "当前合并候选的 job 身份未知，无法对照。"]
    failed = [
        j
        for j in jobs.values()
        if j["name"] != REPORT_JOB and j["status"] == "completed" and j["conclusion"] != "success"
    ]
    if not failed:
        return [*lines, "", "当前 attempt 未取得已完成的失败分片记录，无法判定完整运行的结论。"]
    for job in sorted(failed, key=lambda j: j["name"]):
        records, notes = observations(current, job)
        other = None
        previous, base_notes = {}, []
        if base:
            try:
                other = base.jobs.get(job["name"])
                if other:
                    relevant = {
                        key for key, step in numbered_steps(job) if step["number"] in records
                    }
                    previous, base_notes = observations(base, other, relevant)
            except MissingEvidence as error:
                notes.append("基线 job 资料缺失，无法对照。" + str(error))
        lines.extend(format_job(job, records, other, previous, notes, base_notes))
    return lines


if __name__ == "__main__":
    actions = Actions(os.environ["GITHUB_REPOSITORY"])
    run = actions.read(
        f"runs/{int(os.environ['GITHUB_RUN_ID'])}/attempts/{int(os.environ['GITHUB_RUN_ATTEMPT'])}"
    )
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
    if os.environ.get("CANDIDATE_BASE"):
        event["candidate_base"] = os.environ["CANDIDATE_BASE"]
    lines = report(
        actions,
        run,
        event,
        candidate_job=os.environ.get("CANDIDATE_JOB"),
        candidate_head=os.environ.get("HEAD_SHA"),
        tested=os.environ.get("CANDIDATE_TESTED_COMMIT"),
        source_tree=os.environ.get("CANDIDATE_SOURCE_TREE"),
    )
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as page:
        publish(lines, page, sys.stdout)
