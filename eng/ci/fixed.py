"""Content-addressed CI build products in repository releases, shared by all branches."""

import argparse
import hashlib
import json
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))


def key(kind):
    if kind == "hy":
        lock = json.loads(
            (ROOT / "src/shared/TypeLay.Worker/native/hy/assets.lock.json").read_text()
        )
        downloads = {name: lock[name] for name in ("model", "model_license", "runtime_archive")}
        return "ci-hy-" + hashlib.sha256(json.dumps(downloads, sort_keys=True).encode()).hexdigest()
    if kind == "fonts":
        files = ["eng/fonts/prepare.py", "eng/fonts/fonts.lock.json", "eng/requirements.txt"]
    else:
        from eng.linux.cooperative.third_party import BUILD_INPUTS

        files = BUILD_INPUTS
    digest = hashlib.sha256()
    for name in sorted(files):
        digest.update(name.encode() + b"\0" + (ROOT / name).read_bytes() + b"\0")
    return f"ci-{kind}-{digest.hexdigest()}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("key", "lookup", "restore", "publish"))
    parser.add_argument("kind", choices=("fonts", "fcitx", "hy"))
    parser.add_argument("--tag")
    parser.add_argument("--wait-for-job", help="Wait for this run's named producer to publish")
    args = parser.parse_args()
    if args.wait_for_job and args.action != "restore":
        parser.error("--wait-for-job requires restore")
    tag = args.tag or key(args.kind)
    if args.action == "key":
        print(tag)
        return
    suffix = {"fonts": ".tar.gz", "fcitx": ".tar.zst", "hy": ".tar"}[args.kind]
    archive = ROOT / "artifacts" / (args.kind + suffix)
    archive.parent.mkdir(exist_ok=True)
    repo = os.environ["GITHUB_REPOSITORY"]
    if args.action in ("lookup", "restore"):
        while True:
            result = subprocess.run(
                ["gh", "api", f"repos/{repo}/releases/tags/{tag}"], capture_output=True, text=True
            )
            unavailable = (
                "HTTP 404" in result.stderr
                if result.returncode
                else json.loads(result.stdout).get("draft", False)
            )
            if not (unavailable and args.wait_for_job):
                break
            jobs = json.loads(
                subprocess.check_output(
                    [
                        "gh",
                        "api",
                        f"repos/{repo}/actions/runs/{os.environ['GITHUB_RUN_ID']}/jobs?per_page=100",
                    ],
                    text=True,
                )
            )["jobs"]
            producer = next(job for job in jobs if job["name"] == args.wait_for_job)
            if producer["conclusion"] is not None:
                raise RuntimeError("Fixed producer ended without publishing the requested build")
            time.sleep(5)
        if result.returncode:
            if "HTTP 404" not in result.stderr:
                raise RuntimeError(result.stderr)
            with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
                stream.write("hit=false\n")
            return
        release = json.loads(result.stdout)
        if release.get("draft", False):
            raise RuntimeError("Fixed build has not been published")
        asset = next(a for a in release["assets"] if a["name"] == archive.name)
        if args.action == "lookup":
            with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
                stream.write("hit=true\n")
            return
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
        with archive.open("rb") as stream:
            digest = "sha256:" + hashlib.file_digest(stream, "sha256").hexdigest()
        if digest != asset["digest"]:
            raise ValueError("Release asset digest mismatch")
        extract = {"fonts": ["-xzf"], "fcitx": ["--zstd", "-xf"], "hy": ["-xf"]}[args.kind]
        subprocess.run(
            ["tar", *extract, str(archive)],
            cwd=ROOT,
            check=True,
        )
        with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
            stream.write("hit=true\n")
    else:
        directories = {
            "fonts": ["artifacts/ui-fonts"],
            "fcitx": ["artifacts/linux/fcitx-cooperative-5.1.22"],
            "hy": ["artifacts/hy-download"],
        }[args.kind]
        subprocess.run(
            [
                "tar",
                *({"fonts": ["-z"], "fcitx": ["--zstd"], "hy": []}[args.kind]),
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
        # A draft is invisible to readers until its complete asset has been uploaded.
        subprocess.run(
            [
                "gh",
                "release",
                "create",
                tag,
                "--repo",
                repo,
                "--target",
                os.environ["GITHUB_SHA"],
                "--draft",
                "--prerelease",
                "--title",
                tag,
                "--notes",
                "Fixed CI build inputs. Tests run again on the consuming commit.",
                str(archive),
            ],
            check=True,
        )
        subprocess.run(
            ["gh", "release", "edit", tag, "--repo", repo, "--draft=false", "--latest=false"],
            check=True,
        )


if __name__ == "__main__":
    main()
