"""Content-addressed CI build products in repository releases, shared by all branches."""

import argparse
import hashlib
import json
import math
import os
import subprocess
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))


def key(kind):
    if kind in ("hy", "index"):
        filename = "assets.lock.json" if kind == "hy" else "model.lock.json"
        lock = json.loads((ROOT / "src/shared/TypeLay.Worker/native" / kind / filename).read_text())
        names = (
            ("model", "model_license", "runtime_archive")
            if kind == "hy"
            else ("model", "model_license")
        )
        downloads = {name: lock[name] for name in names}
        return (
            f"ci-{kind}-"
            + hashlib.sha256(json.dumps(downloads, sort_keys=True).encode()).hexdigest()
        )
    if kind == "fonts":
        files = ["eng/fonts/prepare.py", "eng/fonts/fonts.lock.json", "eng/requirements.txt"]
    elif kind == "linux-tools":
        files = ["eng/ci/arch-snapshot", ".github/actions/linux-tools/action.yml"]
    else:
        from eng.linux.cooperative.third_party import BUILD_INPUTS

        files = BUILD_INPUTS
    digest = hashlib.sha256()
    for name in sorted(files):
        digest.update(name.encode() + b"\0" + (ROOT / name).read_bytes() + b"\0")
    return f"ci-{kind}-{digest.hexdigest()}"


def release_asset(release, archive, *, published=True):
    if published and release.get("draft", False):
        raise RuntimeError("Fixed build has not been published")
    assets = [a for a in release["assets"] if a["name"] == archive.name]
    if len(assets) != 1 or not assets[0].get("digest", "").startswith("sha256:"):
        raise RuntimeError("Fixed build is missing its complete, checksummed asset")
    return assets[0]


def download_verified(repo, tag, archive, asset):
    subprocess.run(
        [
            "gh",
            "release",
            "download",
            tag,
            "--repo",
            repo,
            "--pattern",
            archive.name,
            "--dir",
            str(archive.parent),
            "--clobber",
        ],
        check=True,
    )
    if archive_digest(archive) != asset["digest"]:
        raise ValueError("Release asset digest mismatch")


def archive_digest(archive):
    with archive.open("rb") as stream:
        return "sha256:" + hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("key", "lookup", "restore", "publish"))
    parser.add_argument("kind", choices=("fonts", "fcitx", "hy", "index", "linux-tools"))
    parser.add_argument("--tag")
    parser.add_argument("--wait-for-job", help="Wait for this run's named producer to publish")
    parser.add_argument(
        "--wait-seconds",
        type=float,
        default=600,
        help="Maximum producer wait (default: 600 seconds)",
    )
    args = parser.parse_args()
    if args.wait_for_job and args.action != "restore":
        parser.error("--wait-for-job requires restore")
    if not math.isfinite(args.wait_seconds) or args.wait_seconds <= 0:
        parser.error("--wait-seconds must be positive and finite")
    tag = args.tag or key(args.kind)
    if args.action == "key":
        print(tag)
        return
    suffix = {"fonts": ".tar.gz", "fcitx": ".tar.zst"}.get(args.kind, ".tar")
    compression = {"fonts": ["-z"], "fcitx": ["--zstd"]}.get(args.kind, [])
    archive = ROOT / "artifacts" / (args.kind + suffix)
    archive.parent.mkdir(exist_ok=True)
    repo = os.environ["GITHUB_REPOSITORY"]
    if args.action in ("lookup", "restore"):
        deadline = time.monotonic() + args.wait_seconds if args.wait_for_job else None
        producer_ended = None

        def remaining():
            if deadline is None:
                return None
            seconds = deadline - time.monotonic()
            if seconds <= 0:
                raise TimeoutError(
                    f"Timed out waiting for fixed build {tag} from {args.wait_for_job}"
                )
            return seconds

        while True:
            try:
                result = subprocess.run(
                    ["gh", "api", f"repos/{repo}/releases/tags/{tag}"],
                    capture_output=True,
                    text=True,
                    timeout=remaining(),
                )
            except subprocess.TimeoutExpired as error:
                raise TimeoutError(
                    f"Timed out waiting for fixed build {tag} from {args.wait_for_job}"
                ) from error
            unavailable = (
                "HTTP 404" in result.stderr
                if result.returncode
                else json.loads(result.stdout).get("draft", False)
            )
            if not (unavailable and args.wait_for_job):
                break
            if producer_ended is not None:
                raise RuntimeError(
                    f"Fixed producer ended ({producer_ended}) without publishing {tag}"
                )
            try:
                jobs = json.loads(
                    subprocess.check_output(
                        [
                            "gh",
                            "api",
                            f"repos/{repo}/actions/runs/{os.environ['GITHUB_RUN_ID']}/jobs?per_page=100",
                        ],
                        text=True,
                        timeout=remaining(),
                    )
                )["jobs"]
            except subprocess.TimeoutExpired as error:
                raise TimeoutError(
                    f"Timed out waiting for fixed build {tag} from {args.wait_for_job}"
                ) from error
            producer = next((job for job in jobs if job["name"] == args.wait_for_job), None)
            if producer is None:
                raise RuntimeError(f"Fixed producer job not found: {args.wait_for_job}")
            producer_ended = producer["conclusion"]
            # Re-read after completion: publication may have raced our first lookup.
            if producer_ended is None:
                time.sleep(min(5, remaining()))
        if result.returncode:
            if "HTTP 404" not in result.stderr:
                raise RuntimeError(result.stderr)
            with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
                stream.write("hit=false\n")
            return
        release = json.loads(result.stdout)
        asset = release_asset(release, archive)
        if args.action == "lookup":
            with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
                stream.write("hit=true\n")
            return
        download_verified(repo, tag, archive, asset)
        subprocess.run(
            ["tar", *compression, "-xf", str(archive)],
            cwd=ROOT,
            check=True,
        )
        archive.unlink()
        with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
            stream.write("hit=true\n")
    else:
        directories = {
            "fonts": ["artifacts/ui-fonts"],
            "fcitx": ["artifacts/linux/fcitx-cooperative-5.1.22"],
            "hy": ["artifacts/hy-download"],
            "index": ["artifacts/index-model-download"],
            "linux-tools": ["artifacts/linux-tools-cache"],
        }[args.kind]
        subprocess.run(
            [
                "tar",
                *compression,
                "--exclude=core-build",
                "--exclude=libime-build",
                "--exclude=fcitx5-chinese-addons-build",
                "-cf",
                str(archive),
                *directories,
            ],
            cwd=ROOT,
            check=True,
        )
        # Each producer owns only its temporary draft. Publishing the complete draft
        # under the content tag is the atomic race; no producer edits the winner.
        draft_tag = tag + "-draft-" + uuid.uuid4().hex
        draft = json.loads(
            subprocess.check_output(
                [
                    "gh",
                    "api",
                    f"repos/{repo}/releases",
                    "--method",
                    "POST",
                    "-f",
                    f"tag_name={draft_tag}",
                    "-f",
                    f"target_commitish={os.environ['GITHUB_SHA']}",
                    "-F",
                    "draft=true",
                    "-F",
                    "prerelease=true",
                    "-f",
                    f"name={tag}",
                    "-f",
                    "body=Fixed CI build inputs. Tests run again on the consuming commit.",
                ],
                text=True,
            )
        )
        delete_draft = True
        try:
            subprocess.run(
                ["gh", "release", "upload", draft_tag, str(archive), "--repo", repo], check=True
            )
            uploaded = json.loads(
                subprocess.check_output(
                    ["gh", "api", f"repos/{repo}/releases/{draft['id']}"], text=True
                )
            )
            if release_asset(uploaded, archive, published=False)["digest"] != archive_digest(
                archive
            ):
                raise ValueError("Release asset digest mismatch")
            # A lost response does not prove publication failed. Do not delete a
            # potentially public release when this request is interrupted.
            delete_draft = False
            result = subprocess.run(
                [
                    "gh",
                    "api",
                    f"repos/{repo}/releases/{draft['id']}",
                    "--method",
                    "PATCH",
                    "-f",
                    f"tag_name={tag}",
                    "-F",
                    "draft=false",
                    "-f",
                    "make_latest=false",
                ],
                capture_output=True,
                text=True,
            )
            delete_draft = result.returncode != 0 and any(
                status in result.stderr for status in ("HTTP 422", "HTTP 403")
            )
            if not result.returncode:
                return
            if "HTTP 422" in result.stderr and any(
                error.get("resource") == "Release"
                and error.get("code") == "already_exists"
                and error.get("field") == "tag_name"
                for error in json.loads(result.stdout).get("errors", [])
                if isinstance(error, dict)
            ):
                winner = json.loads(
                    subprocess.check_output(
                        ["gh", "api", f"repos/{repo}/releases/tags/{tag}"], text=True
                    )
                )
                download_verified(repo, tag, archive, release_asset(winner, archive))
                print(f"Reused verified fixed build {tag} from release {winner['id']}")
            else:
                raise RuntimeError(result.stderr)
        finally:
            if delete_draft:
                subprocess.run(
                    ["gh", "api", f"repos/{repo}/releases/{draft['id']}", "--method", "DELETE"],
                    check=True,
                )


if __name__ == "__main__":
    main()
