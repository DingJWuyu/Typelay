#!/usr/bin/env python3
"""Build pinned third-party Fcitx dependencies, independently of TypeLay native modules."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
PATCHES = HERE / "patches"
CORE_VERSION = "5.1.22"
SOURCE_VERSIONS = {
    "fcitx5": CORE_VERSION,
    "fcitx5-chinese-addons": "5.1.14",
    "fcitx5-gtk": "5.1.7",
    "libime": "1.1.16",
}
PRIVATE_VERSIONS = {
    **{f"Fcitx5{name}": CORE_VERSION for name in ("Core", "Config", "Utils")},
    **{f"LibIME{name}": SOURCE_VERSIONS["libime"] for name in ("Core", "Pinyin", "Table")},
}
FCITX_PINS = {
    "fcitx5": (
        "c7ecdb931d8b378ccdcd87382a3ef7ff0bd10def",
        "3743c26ee354823c3f808afd6e17af32ed5e6460edc562854e95d5b0f4d8cd4d",
    ),
    "fcitx5-chinese-addons": (
        "fa65a9b46f805fc491b88c53f6b0159fd6f756af",
        "97196e7f922c70cc18eb30e38a146abb34cf70f6ea4b37492b78bcfc2c5598f2",
    ),
    "fcitx5-gtk": (
        "3b18d2ab7401d4233daf38bbc5896f1703685a43",
        "d0afcf4188ce630a5824a10f4d2ba501dcbe3440687cebedba323c95dcc4ba9c",
    ),
    "libime": (
        "901a73898df3bf5990d6535bf8e7e02b190aee2e",
        "d423e691816a3d7229e44bb2e3fa5423513ec64251bc3aafa5389983155378b8",
    ),
    "kenlm": (
        "4cb443e60b7bf2c0ddf3c745378f76cb59e254e5",
        "11df4f929b175f0b3bd26be7a5a83b3c17cdefa83baecab0b6e69f77430184ea",
    ),
}
PATCH_PROJECTS = ("fcitx5", "fcitx5-chinese-addons", "fcitx5-gtk")
DATA = (
    (
        "libime/data",
        "lm_sc.arpa-20260629.tar.zst",
        "06808333b9173e5374cf2cb5afc12d08f5625bf9abb536489cac376fc05f2e7f",
    ),
    (
        "libime/data",
        "dict-20260703.tar.zst",
        "c686cab6df8964c48d596f57d205bac31fc72870b06a83017e44503df8c09697",
    ),
    (
        "libime/data",
        "table-20240108.tar.zst",
        "3e9d87b04a393f131723472c8eaa860dd23c378a3d4f6a9005513b2a95b3614b",
    ),
    (
        "fcitx5-chinese-addons/modules/pinyinhelper",
        "py_stroke-20250329.tar.gz",
        "c1a7ca7225d3614ab83353fc827503006a980447762018f53760425d7b5303a6",
    ),
    (
        "fcitx5-chinese-addons/modules/pinyinhelper",
        "py_table-20121124.tar.gz",
        "42146ac97de6c13d55f9e99ed873915f4c66739e9c11532a34556badf9792c04",
    ),
)


BUILD_INPUTS = (
    "eng/linux/cooperative/third_party.py",
    "eng/ci/arch-snapshot",
    *(f"eng/linux/cooperative/patches/{project}.patch" for project in PATCH_PROJECTS),
)


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def hashes(files):
    return {relative: sha256(ROOT / relative) for relative in files}


def download(url, destination, digest):
    if not destination.exists():
        with urllib.request.urlopen(url, timeout=180) as response:
            destination.write_bytes(response.read())
    if sha256(destination) != digest:
        raise RuntimeError(f"Official source/data checksum mismatch: {destination.name}")


def run(args, **kwargs):
    try:
        return subprocess.check_output(
            [str(a) for a in args], text=True, timeout=kwargs.pop("timeout", 180), **kwargs
        ).strip()
    except subprocess.CalledProcessError as error:
        if error.output:
            print(error.output, file=sys.stderr)
        raise


def verify(out):
    """Reject stale pins, changed installed bytes or mismatched upstream sources."""
    recorded = json.loads((out / "third-party.json").read_text())
    if recorded["inputs"] != hashes(BUILD_INPUTS):
        raise ValueError("Third-party build pins differ from this checkout")
    installed = recorded["installed_sha256"]
    if not installed or any(sha256(out / name) != digest for name, digest in installed.items()):
        raise ValueError("Third-party installed bytes changed after their build")
    for project, (commit, digest) in FCITX_PINS.items():
        if sha256(out / f"{project}-{commit}.tar.gz") != digest:
            raise ValueError(f"Third-party source archive changed: {project}")
    for project in PATCH_PROJECTS:
        run(
            [
                "patch",
                "--dry-run",
                "--reverse",
                "--force",
                "--fuzz=0",
                "-p1",
                "-i",
                PATCHES / f"{project}.patch",
            ],
            cwd=out / "sources" / project,
        )


def build(out):
    inputs = hashes(BUILD_INPUTS)
    (out / "third-party.json").unlink(missing_ok=True)
    core_version = CORE_VERSION
    pins = FCITX_PINS
    sources = out / "sources"
    sources.mkdir(parents=True, exist_ok=True)
    for repo, (commit, digest) in pins.items():
        archive = out / f"{repo}-{commit}.tar.gz"
        owner = "kpu" if repo == "kenlm" else "fcitx"
        download(f"https://codeload.github.com/{owner}/{repo}/tar.gz/{commit}", archive, digest)
        target = sources / repo
        if target.exists():
            shutil.rmtree(target)
        with tarfile.open(archive) as bundle:
            bundle.extractall(sources, filter="data")
        (sources / f"{repo}-{commit}").rename(target)
        if repo in PATCH_PROJECTS:
            run(
                ["patch", "--batch", "--fuzz=0", "-p1", "-i", PATCHES / f"{repo}.patch"], cwd=target
            )
    core, chinese = (sources / name for name in PATCH_PROJECTS[:2])
    kenlm = sources / "libime/src/libime/core/kenlm"
    kenlm.rmdir()  # Empty gitlink directory from the official source archive.
    (sources / "kenlm").rename(kenlm)
    for directory, name, digest in DATA:
        download(f"https://download.fcitx-im.org/data/{name}", sources / directory / name, digest)
    prefix = out / "core-prefix"
    print(f"Configuring complete Fcitx {core_version} with official frontends", flush=True)
    run(
        [
            "cmake",
            "-S",
            core,
            "-B",
            out / "core-build",
            "-G",
            "Ninja",
            "-DCMAKE_BUILD_TYPE=RelWithDebInfo",
            f"-DCMAKE_INSTALL_PREFIX={prefix}",
            "-DCMAKE_INSTALL_LIBDIR=lib",
            f"-DCMAKE_CXX_FLAGS=-I{chinese / 'im/pinyin'} -I{core / 'src/im/keyboard'}",
            "-DENABLE_X11=ON",
            "-DENABLE_WAYLAND=ON",
            "-DENABLE_KEYBOARD=ON",
            "-DENABLE_DBUS=ON",
            "-DENABLE_SERVER=ON",
            "-DENABLE_ENCHANT=ON",
            "-DENABLE_TEST=OFF",
            "-DENABLE_TESTING_ADDONS=OFF",
            "-DENABLE_DOC=OFF",
            "-DUSE_SYSTEM_YOGA=ON",
            "-DUSE_SYSTEM_PLASMA_WAYLAND_PROTOCOLS=ON",
            "-DEVENT_LOOP_BACKEND=systemd",
        ]
    )
    print(f"Building complete Fcitx {core_version} (parallel=2)", flush=True)
    subprocess.run(
        ["cmake", "--build", out / "core-build", "--parallel", "2"], check=True, timeout=900
    )
    print(f"Installing Fcitx {core_version} into the private build prefix", flush=True)
    run(["cmake", "--install", out / "core-build"])
    os.environ["PKG_CONFIG_PATH"] = f"{prefix}/lib/pkgconfig:{prefix}/share/pkgconfig"
    os.environ["LD_LIBRARY_PATH"] = str(prefix / "lib")
    for package in ("Fcitx5Core", "Fcitx5Config", "Fcitx5Utils"):
        assert run(["pkg-config", "--modversion", package]) == core_version
        assert Path(run(["pkg-config", "--variable=prefix", package])).resolve() == prefix
    private_versions = {
        name: core_version for name in ("Fcitx5Core", "Fcitx5Config", "Fcitx5Utils")
    }
    for project, options in (
        ("libime", ["-DENABLE_DATA=ON", "-DENABLE_TOOLS=ON", "-DENABLE_DOC=OFF"]),
        (
            "fcitx5-chinese-addons",
            [
                "-DENABLE_DATA=ON",
                "-DENABLE_TOOLS=ON",
                "-DENABLE_GUI=OFF",
                "-DENABLE_OPENCC=ON",
                "-DENABLE_CLOUDPINYIN=ON",
            ],
        ),
    ):
        print(f"Configuring private {project} and its fixed upstream data", flush=True)
        run(
            [
                "cmake",
                "-S",
                sources / project,
                "-B",
                out / (project + "-build"),
                "-G",
                "Ninja",
                "-DCMAKE_BUILD_TYPE=RelWithDebInfo",
                f"-DCMAKE_INSTALL_PREFIX={prefix}",
                "-DCMAKE_INSTALL_LIBDIR=lib",
                f"-DCMAKE_PREFIX_PATH={prefix}",
                *[
                    f"-DFcitx5{name}_DIR={prefix}/lib/cmake/Fcitx5{name}"
                    for name in (("Utils",) if project == "libime" else ("Utils", "Config", "Core"))
                ],
                *(
                    []
                    if project == "libime"
                    else [
                        f"-DLibIME{name}_DIR={prefix}/lib/cmake/LibIME{name}"
                        for name in ("Core", "Pinyin", "Table")
                    ]
                ),
                "-DBUILD_SHARED_LIBS=ON",
                "-DENABLE_TEST=OFF",
                *options,
            ]
        )
        print(f"Building private {project} (parallel=2)", flush=True)
        subprocess.run(
            ["cmake", "--build", out / (project + "-build"), "--parallel", "2"],
            check=True,
            timeout=900,
        )
        run(["cmake", "--install", out / (project + "-build")])
        if project == "libime":
            for name in ("Core", "Pinyin", "Table"):
                config = prefix / f"lib/cmake/LibIME{name}/LibIME{name}ConfigVersion.cmake"
                assert re.search(
                    rf'set\(PACKAGE_VERSION "{re.escape(SOURCE_VERSIONS["libime"])}"\)',
                    config.read_text(),
                ), config
                assert (prefix / f"lib/libIME{name}.so.{SOURCE_VERSIONS['libime']}").is_file()
                private_versions["LibIME" + name] = SOURCE_VERSIONS["libime"]
    (out / "installed-private-versions.json").write_text(
        json.dumps(private_versions, indent=2) + "\n"
    )
    # Only generated system table entries are made relocatable. User files are untouched.
    table_data = prefix / "share/fcitx5/table"
    table_data.mkdir(exist_ok=True)
    for dictionary in (prefix / "share/libime").glob("*.main.dict"):
        link = table_data / dictionary.name
        link.unlink(missing_ok=True)
        link.symlink_to("../../libime/" + dictionary.name)
    for config in (prefix / "share/fcitx5/inputmethod").glob("*.conf"):
        text = config.read_text()
        config.write_text(text.replace(str(prefix / "share/libime") + "/", "table/"))
    data_files = [
        *sorted((prefix / "share/libime").glob("*.dict")),
        *sorted((prefix / "lib/libime").glob("zh_CN.lm*")),
        *sorted((prefix / "share/fcitx5/pinyinhelper").glob("*.mb")),
    ]
    assert len(data_files) >= 14, "Private dictionaries/model/helper data missing"
    (out / "installed-private-data.json").write_text(
        json.dumps(
            {str(path.relative_to(prefix)): sha256(path) for path in data_files},
            indent=2,
        )
        + "\n"
    )

    if hashes(BUILD_INPUTS) != inputs:
        raise RuntimeError("Third-party inputs changed during compilation")
    (out / "third-party.json").write_text(
        json.dumps(
            {
                "inputs": inputs,
                "installed_sha256": {
                    str(path.relative_to(out)): sha256(path)
                    for path in sorted(prefix.rglob("*"))
                    if path.is_file()
                },
            },
            indent=2,
        )
        + "\n"
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    out = (args.output or ROOT / "artifacts/linux" / f"fcitx-cooperative-{CORE_VERSION}").resolve()
    out.mkdir(parents=True, exist_ok=True)
    build(out)


if __name__ == "__main__":
    main()
