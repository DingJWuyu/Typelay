#!/usr/bin/env python3
"""Build or verify the pinned third-party Fcitx archive."""

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
from eng.linux.cooperative import third_party_recipe as recipe  # noqa: E402
from eng.linux.cooperative.third_party_recipe import run  # noqa: E402


def progress(stage, project):
    if project == "fcitx5":
        messages = {
            "configure": f"Configuring complete Fcitx {recipe.CORE_VERSION} with official frontends",
            "build": f"Building complete Fcitx {recipe.CORE_VERSION} (parallel=2)",
            "install": f"Installing Fcitx {recipe.CORE_VERSION} into the private build prefix",
        }
    else:
        messages = {
            "configure": f"Configuring private {project} and its fixed upstream data",
            "build": f"Building private {project} (parallel=2)",
        }
    print(messages[stage], flush=True)


def build(out, archive_cache=None):
    recipe.build(out, archive_cache, progress)


def verify(out):
    """Reject stale pins, changed installed bytes or mismatched upstream sources."""
    recorded = json.loads((out / "third-party.json").read_text())
    if recorded["inputs"] != recipe.hashes(recipe.BUILD_INPUTS):
        raise ValueError("Third-party build pins differ from this checkout")
    installed = recorded["installed_sha256"]
    if not installed or any(
        recipe.sha256(out / name) != digest for name, digest in installed.items()
    ):
        raise ValueError("Third-party installed bytes changed after their build")
    for project, (commit, digest) in recipe.FCITX_PINS.items():
        if recipe.sha256(out / f"{project}-{commit}.tar.gz") != digest:
            raise ValueError(f"Third-party source archive changed: {project}")
    for directory, name, digest in (*recipe.DATA, recipe.SPELL):
        if recipe.sha256(out / "sources" / directory / name) != digest:
            raise ValueError(f"Third-party data archive changed: {name}")
    for project in recipe.PATCH_PROJECTS:
        run(
            [
                "patch",
                "--dry-run",
                "--reverse",
                "--force",
                "--fuzz=0",
                "-p1",
                "-i",
                recipe.PATCHES / f"{project}.patch",
            ],
            cwd=out / "sources" / project,
        )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--archive-cache",
        type=Path,
        help="Read only this flat raw-archive directory; never download",
    )
    args = parser.parse_args()
    out = (
        args.output or ROOT / "artifacts/linux" / f"fcitx-cooperative-{recipe.CORE_VERSION}"
    ).resolve()
    build(out, archive_cache=args.archive_cache)


if __name__ == "__main__":
    main()
