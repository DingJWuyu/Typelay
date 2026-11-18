"""Read attempt-scoped Actions evidence. Only fetch the small diagnostics for each shard."""

import io
import json
import re
import subprocess
import xml.etree.ElementTree as ET
import zipfile
from functools import cached_property
from itertools import count
from urllib.parse import urlencode

from failure_evidence import SCENE_REPORTS, contracts_from_trx

LIMIT = 20_000_000
SHA = re.compile(r"[0-9a-f]{40}")


def git(folder, *args):
    command = subprocess.run(["git", *args], cwd=folder, capture_output=True, text=True)
    return command.stdout.strip() if command.returncode == 0 else ""


def historical(run, candidate):
    return (candidate["created_at"], candidate["id"]) < (run["created_at"], run["id"])


def fixed_baseline(folder, run, event, history):
    if pull := event.get("pull_request"):
        return pull["base"]["sha"]
    if event.get("candidate_base"):
        return event["candidate_base"]
    if run["head_branch"] == "hitl":
        before = event.get("before")
        return before if before and SHA.fullmatch(before) and int(before, 16) else None
    tip = next((r for r in history if historical(run, r)), None)
    if tip:
        return git(folder, "merge-base", tip["head_sha"], run["head_sha"]) or None
    return None


def select_run(folder, run, base_sha, history):
    if not base_sha or not SHA.fullmatch(base_sha):
        return None
    by_sha = {}
    for candidate in history:
        if historical(run, candidate):
            by_sha.setdefault(candidate["head_sha"], []).append(candidate)
    for sha in [base_sha, *git(folder, "rev-list", "--first-parent", base_sha).splitlines()]:
        if sha in by_sha:
            candidates = by_sha[sha]
            return next(
                (
                    r
                    for r in candidates
                    if r["status"] == "completed" and r.get("conclusion") != "cancelled"
                ),
                candidates[0],
            )
    return None


class MissingEvidence(Exception):
    """A known unavailable source, safe to describe in the summary."""


class Actions:
    def __init__(self, repository):
        self.prefix = f"repos/{repository}/actions/"

    def read(self, endpoint, *, raw=False):
        command = ["gh", "api"]
        if raw:
            command.append("--allow-escape-sequences")
        result = subprocess.run([*command, self.prefix + endpoint], capture_output=True)
        if result.returncode:
            status = re.search(rb"HTTP (\d{3})", result.stderr)
            code = status[1].decode() if status else "未知"
            raise MissingEvidence(f"Actions HTTP {code}（资料缺失、过期或不可读）")
        return result.stdout if raw else json.loads(result.stdout)

    def pages(self, path, key, **query):
        for page in count(1):
            items = self.read(path + "?" + urlencode(dict(query, per_page=100, page=page)))[key]
            yield items
            if len(items) < 100:
                return


class Attempt:
    def __init__(self, actions, run):
        self.actions = actions
        self.run = run
        self.id = run["id"]
        self.attempt = run["run_attempt"]
        self.sha = run["head_sha"]

    @cached_property
    def jobs(self):
        pages = self.actions.pages(f"runs/{self.id}/attempts/{self.attempt}/jobs", "jobs")
        return {j["name"]: j for page in pages for j in page}

    @cached_property
    def artifacts(self):
        pages = self.actions.pages(f"runs/{self.id}/artifacts", "artifacts")
        return {a["name"]: a for page in pages for a in page}

    def reports(self, job):
        name = job["name"]
        suffix = f"{self.sha}-{self.id}-{self.attempt}"
        if name == "Index / windows":
            key = "typelay-index-windows-" + suffix
        elif name == "Index / linux":
            key = "typelay-index-linux-" + self.sha
        elif name.startswith("Windows contracts / "):
            key = f"windows-trx-{name.rsplit(' ', 1)[1]}-{suffix}"
        elif name.startswith("Windows desktop / "):
            key = f"windows-desktop-{name.rsplit(' ', 1)[1]}-{suffix}"
        elif name == "shared":
            key = "shared-" + suffix
        else:
            return {}
        artifact = self.artifacts.get(key)
        if not artifact:
            raise MissingEvidence("结构化报告缺失")
        if artifact["expired"]:
            raise MissingEvidence("结构化报告已过期")
        if artifact["size_in_bytes"] > LIMIT:
            raise MissingEvidence("结构化报告超过 20 MB")
        data = self.actions.read(f"artifacts/{artifact['id']}/zip", raw=True)
        if len(data) > LIMIT:
            raise MissingEvidence("结构化报告超过 20 MB")
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                return self.read_reports(archive, name)
        except (ValueError, KeyError, TypeError, ET.ParseError, zipfile.BadZipFile) as error:
            raise MissingEvidence("结构化报告无法解析") from error

    def read_reports(self, archive, job):
        files = {item.filename: item for item in archive.infolist()}

        def read(name):
            if files[name].file_size > LIMIT:
                raise MissingEvidence("分项报告超过 20 MB")
            return archive.read(files[name])

        if not job.startswith("Windows contracts / "):
            source = json.loads(read("ci/source.json"))
            if (
                source.get("tested_commit"),
                str(source.get("run_id")),
                str(source.get("attempt")),
            ) != (self.sha, str(self.id), str(self.attempt)):
                raise MissingEvidence("结构化报告的源码或 attempt 不符")
        results = {}
        for path in (
            *SCENE_REPORTS.values(),
            "continuous-translation/result.json",
            "index-production/result.json",
        ):
            if path in files:
                results[path] = json.loads(read(path))
        trx = []
        if job.startswith("Windows contracts / "):
            pointer = json.loads(read("current-contracts.json"))
            current = pointer["runDirectory"]
            if not re.fullmatch(r"contracts-[a-f0-9]{32}", current):
                raise MissingEvidence("本次 TRX 指针无效")
            trx = [
                n
                for n in files
                if re.fullmatch(current + r"/(?:main|archive-body-timeout)/[^/]+\.trx", n)
            ]
            run_report = current + "/result.json"
            if run_report in files:
                results["contract-summary"] = json.loads(read(run_report))
        elif job == "shared":
            trx = [n for n in files if re.fullmatch(r"shared-test-results/[^/]+\.trx", n)]
        if trx:
            results["contracts"] = [
                r for filename in trx for r in contracts_from_trx(self.sha, read(filename))
            ]
        return results
