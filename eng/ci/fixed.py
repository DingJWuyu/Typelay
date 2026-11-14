"""Content-addressed CI build products in repository releases, shared by all branches."""

import argparse
import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))


def key(kind):
    if kind == "fonts":
        files = ["eng/fonts/prepare.py", "eng/fonts/fonts.lock.json", "eng/requirements.txt"]
        environment = b""
    else:
        from eng.linux.cooperative.inputs import FCITX_BUILD_INPUTS

        files = [*FCITX_BUILD_INPUTS, "eng/ci/fixed.py", ".github/actions/linux-tools/action.yml"]
        # The exact compiler and linked system libraries are build inputs too.
        environment = subprocess.check_output(["pacman", "-Q"])
    digest = hashlib.sha256(environment)
    for name in sorted(files):
        digest.update(name.encode() + b"\0" + (ROOT / name).read_bytes() + b"\0")
    return f"ci-{kind}-{digest.hexdigest()}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("key", "restore", "publish"))
    parser.add_argument("kind", choices=("fonts", "fcitx"))
    parser.add_argument("--tag")
    args = parser.parse_args()
    tag = args.tag or key(args.kind)
    if args.action == "key":
        print(tag)
        return
    archive = ROOT / "artifacts" / (args.kind + (".tar.gz" if args.kind == "fonts" else ".tar.zst"))
    archive.parent.mkdir(exist_ok=True)
    repo = os.environ["GITHUB_REPOSITORY"]
    if args.action == "restore":
        result = subprocess.run(
            ["gh", "api", f"repos/{repo}/releases/tags/{tag}"], capture_output=True, text=True
        )
        if result.returncode:
            if "HTTP 404" not in result.stderr:
                raise RuntimeError(result.stderr)
            with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
                stream.write("hit=false\n")
            return
        release = json.loads(result.stdout)
        asset = next(a for a in release["assets"] if a["name"] == archive.name)
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
        subprocess.run(
            ["tar", *(["-xzf"] if args.kind == "fonts" else ["--zstd", "-xf"]), str(archive)],
            cwd=ROOT,
            check=True,
        )
        with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
            stream.write("hit=true\n")
    else:
        directory = (
            "artifacts/ui-fonts"
            if args.kind == "fonts"
            else "artifacts/linux/fcitx-cooperative-5.1.22"
        )
        subprocess.run(
            [
                "tar",
                *(["-z"] if args.kind == "fonts" else ["--zstd"]),
                "--exclude=core-build",
                "--exclude=libime-build",
                "--exclude=fcitx5-chinese-addons-build",
                "-cf",
                str(archive),
                directory,
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
