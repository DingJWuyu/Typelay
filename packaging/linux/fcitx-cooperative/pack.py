#!/usr/bin/env python3
"""Package an already tested private Fcitx build without installing or executing it."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import tarfile
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
BUNDLE = "TypeLay-Fcitx-cooperative"
CORE_VERSION = "5.1.22"
PATCHED_UPSTREAM = ("fcitx5", "fcitx5-chinese-addons", "fcitx5-gtk")
UPSTREAM = (*PATCHED_UPSTREAM, "libime")
SOURCE_VERSIONS = {
    "fcitx5": CORE_VERSION,
    "fcitx5-chinese-addons": "5.1.14",
    "fcitx5-gtk": "5.1.7",
    "libime": "1.1.16",
}
PRIVATE_VERSIONS = {
    **{f"Fcitx5{name}": CORE_VERSION for name in ("Core", "Config", "Utils")},
    **{f"LibIME{name}": "1.1.16" for name in ("Core", "Pinyin", "Table")},
}
LIBRARIES = ("libFcitx5GClient.so", "im-fcitx5.so", "libtypelay-gtk4.so")
CHINESE_ADDONS = (
    "pinyin",
    "table",
    "punctuation",
    "pinyinhelper",
    "chttrans",
    "fullwidth",
    "cloudpinyin",
)
TABLE_DATA = ("db", "erbi", "qxm", "wanfeng", "wbpy", "wbx", "zrm", "cj")
DATA_ARCHIVES = {
    "lm_sc.arpa-20260629.tar.zst": "06808333b9173e5374cf2cb5afc12d08f5625bf9abb536489cac376fc05f2e7f",
    "dict-20260703.tar.zst": "c686cab6df8964c48d596f57d205bac31fc72870b06a83017e44503df8c09697",
    "table-20240108.tar.zst": "3e9d87b04a393f131723472c8eaa860dd23c378a3d4f6a9005513b2a95b3614b",
}
CHINESE_DATA_ARCHIVES = {
    "py_stroke-20250329.tar.gz": "c1a7ca7225d3614ab83353fc827503006a980447762018f53760425d7b5303a6",
    "py_table-20121124.tar.gz": "42146ac97de6c13d55f9e99ed873915f4c66739e9c11532a34556badf9792c04",
}
RUNTIME_DIRS = {"bin", "lib", "libexec", "share", "etc"}
BUILD_INPUTS = (
    "tests/linux/fcitx_cooperative/run.py",
    "tests/linux/fcitx_cooperative/fixture.c",
    *(f"tests/linux/fcitx_cooperative/{project}.patch" for project in PATCHED_UPSTREAM),
    "src/TypeLay.Linux/native/fcitx5/typelay-fcitx-v1.h",
    "src/TypeLay.Linux/native/common/gtk-textbuffer-write.inc",
    *(
        str(p.relative_to(ROOT))
        for p in sorted((ROOT / "src/TypeLay.Linux/native/gtk4").iterdir())
        if p.suffix in {".c", ".h"} or p.name == "CMakeLists.txt"
    ),
)
SOURCE_CACHE_DIRS = {
    ".git",
    ".cache",
    ".ccache",
    "__pycache__",
    "CMakeFiles",
    "build",
    "_build",
    "_deps",
}
SOURCE_GENERATED_FILES = {
    "CMakeCache.txt",
    "CTestTestfile.cmake",
    "build.ninja",
    ".ninja_deps",
    ".ninja_log",
    "cmake_install.cmake",
    "install_manifest.txt",
    "compile_commands.json",
}


def read_object(path):
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path}")
    return value


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def metadata(build):
    versions = read_object(build / "versions.json")
    for key in (
        "server_source_version",
        "server_runtime_version",
        "linked_core_development_version",
    ):
        if versions.get(key) != CORE_VERSION:
            raise ValueError(f"{key} must be {CORE_VERSION}, got {versions.get(key)!r}")
    if versions.get("component_source_versions") != SOURCE_VERSIONS:
        raise ValueError("Explicit fixed component source versions are missing or mismatched")
    if versions.get("installed_private_versions") != PRIVATE_VERSIONS:
        raise ValueError("Installed private Fcitx/LibIME development versions do not match")
    pins = read_object(build / "pins.json")
    for project in (*UPSTREAM, "kenlm"):
        pin = pins.get(project)
        if (
            not isinstance(pin, list)
            or len(pin) != 2
            or not isinstance(pin[0], str)
            or not re.fullmatch(r"[0-9a-f]{40}", pin[0])
            or not isinstance(pin[1], str)
            or not re.fullmatch(r"[0-9a-f]{64}", pin[1])
        ):
            raise ValueError(f"Missing or invalid upstream source pin: {project}")
    if versions.get("server_source_commit") != pins["fcitx5"][0]:
        raise ValueError("Server source commit differs from the recorded upstream pin")
    return versions, pins


def original_prefixes(prefix):
    """Recognize links from either a local build or a relocated CI artifact."""
    originals = set()
    for component in ("Core", "Config", "Utils"):
        pc = prefix / "lib/pkgconfig" / f"Fcitx5{component}.pc"
        fields = pc.read_text(encoding="utf-8").splitlines()
        values = [line.removeprefix("prefix=") for line in fields if line.startswith("prefix=")]
        if len(values) != 1 or not Path(values[0]).is_absolute():
            raise ValueError(f"Cannot establish the original private installation prefix: {pc}")
        if f"Version: {CORE_VERSION}" not in fields:
            raise ValueError(f"Private development metadata is not {CORE_VERSION}: {pc}")
        originals.add(Path(os.path.normpath(values[0])))
    if len(originals) != 1:
        raise ValueError("Private Core/Config/Utils installation prefixes disagree")
    return (prefix, *originals)


def relative_link(source, source_root, destination, destination_root, absolute_roots):
    target = Path(os.readlink(source))
    if target.is_absolute():
        target = Path(os.path.normpath(target))
        for root in absolute_roots:
            if target.is_relative_to(root):
                relative = target.relative_to(root)
                break
        else:
            raise ValueError(f"Absolute symlink escapes its source tree: {source} -> {target}")
    else:
        target = Path(os.path.normpath(source.parent / target))
        if not target.is_relative_to(source_root):
            raise ValueError(f"Symlink escapes its source tree: {source}")
        relative = target.relative_to(source_root)
    return os.path.relpath(destination_root / relative, destination.parent)


def omitted(path, source_root, *, runtime):
    relative = path.relative_to(source_root)
    if path.name in {*CHINESE_DATA_ARCHIVES, "py_table.mb", "py_stroke.mb"}:
        return True  # User-installed matching helper data is not redistributed.
    if runtime:
        return (
            relative.parts[0] not in RUNTIME_DIRS
            or any(part in {"include", "pkgconfig", "cmake", "sdk"} for part in relative.parts)
            or path.suffix in {".a", ".la"}
        )
    return (
        any(part in SOURCE_CACHE_DIRS for part in relative.parts)
        or path.name in SOURCE_GENERATED_FILES
        or path.suffix in {".o", ".obj", ".a", ".la", ".pyc", ".pyo", ".exe", ".dll"}
        or re.search(r"\.so(?:\..*)?$", path.name) is not None
    )


def copy_tree(source_root, destination_root, *, runtime=False, absolute_roots=()):
    if not source_root.is_dir() or source_root.is_symlink():
        raise ValueError(f"Missing regular source directory: {source_root}")
    destination_root.mkdir(parents=True, exist_ok=True)

    def copy(directory):
        for source in sorted(directory.iterdir()):
            if omitted(source, source_root, runtime=runtime):
                continue
            relative = source.relative_to(source_root)
            destination = destination_root / relative
            mode = source.lstat().st_mode
            if stat.S_ISLNK(mode):
                target = relative_link(
                    source,
                    source_root,
                    destination,
                    destination_root,
                    absolute_roots or (source_root,),
                )
                destination.symlink_to(target)
            elif stat.S_ISDIR(mode):
                destination.mkdir()
                copy(source)
            elif stat.S_ISREG(mode):
                if not runtime:
                    with source.open("rb") as stream:
                        if stream.read(4) == b"\x7fELF":
                            raise ValueError(
                                f"Unexpected compiled ELF file in source tree: {source}"
                            )
                shutil.copyfile(source, destination)
                # GitHub artifact ZIPs flatten executable permissions. Only these
                # explicitly selected runtime entry directories regain them.
                executable = mode & 0o111 or (
                    runtime and any(part in {"bin", "libexec"} for part in relative.parts[:-1])
                )
                destination.chmod(0o755 if executable else 0o644)
            else:
                raise ValueError(f"Unsupported filesystem entry: {source}")

    copy(source_root)


def copy_core(build, bundle):
    prefix = build / "core-prefix"
    roots = original_prefixes(prefix)
    for directory in ("bin", "lib", "share"):
        source = prefix / directory
        if not source.is_dir():
            raise ValueError(f"Missing private core runtime directory: {source}")
    # Keep the entire prefix as the link boundary for cross-directory links;
    # the runtime walker visits only explicitly allowed top-level directories.
    copy_tree(prefix, bundle / "core", runtime=True, absolute_roots=roots)
    for required in (
        "bin/fcitx5",
        "lib/libFcitx5Core.so.7",
        "lib/libFcitx5Config.so.6",
        "lib/libFcitx5Utils.so.2",
        *(f"lib/libIME{name}.so.0" for name in ("Core", "Pinyin", "Table")),
        *(f"lib/fcitx5/lib{name}.so" for name in CHINESE_ADDONS),
        *(f"share/fcitx5/addon/{name}.conf" for name in CHINESE_ADDONS),
        "share/libime/sc.dict",
        "share/libime/extb.dict",
        *(f"share/libime/{name}.main.dict" for name in TABLE_DATA),
        "lib/libime/zh_CN.lm",
        "lib/libime/zh_CN.lm.predict",
    ):
        if not (bundle / "core" / required).is_file():
            raise ValueError(f"Private core runtime is incomplete: {required}")


def copy_libraries(build, bundle):
    source_root = build / "lib"
    destination_root = bundle / "lib"
    gtk4 = Path("gtk-4.0/4.0.0/immodules/libim-fcitx5.so")
    if not (source_root / gtk4).is_file():
        raise ValueError("Missing cooperating GTK4 module")
    (destination_root / gtk4).parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source_root / gtk4, destination_root / gtk4)
    destination_root.mkdir(exist_ok=True)
    for name in LIBRARIES:
        if not (source_root / name).is_file():
            raise ValueError(f"Missing patched runtime library: {name}")
    for source in sorted(source_root.iterdir()):
        if not any(source.name == name or source.name.startswith(name + ".") for name in LIBRARIES):
            continue
        destination = destination_root / source.name
        if source.is_symlink():
            destination.symlink_to(
                relative_link(source, source_root, destination, destination_root, (source_root,))
            )
        elif source.is_file():
            shutil.copyfile(source, destination)
            destination.chmod(0o644)
        else:
            raise ValueError(f"Expected a patched shared library file: {source}")


def copy_sources(build, bundle, pins, inputs):
    source = bundle / "source"
    libime = build / "sources/libime"
    for name, digest in DATA_ARCHIVES.items():
        if sha256(libime / "data" / name) != digest:
            raise ValueError(f"Missing or mismatched LibIME 1.1.16 data source: {name}")
    for name, digest in CHINESE_DATA_ARCHIVES.items():
        if sha256(build / "sources/fcitx5-chinese-addons/modules/pinyinhelper" / name) != digest:
            raise ValueError(f"Missing or mismatched Chinese Addons 5.1.14 data archive: {name}")
    kenlm = libime / "src/libime/core/kenlm"
    for relative in (
        "LICENSE",
        "COPYING",
        "COPYING.3",
        "COPYING.LESSER.3",
        "util/double-conversion/LICENSE",
        "lm/model.cc",
        "util/file.cc",
    ):
        if not (kenlm / relative).is_file():
            raise ValueError(f"Incomplete corresponding KenLM source or license: {relative}")
    for project in UPSTREAM:
        upstream = build / "sources" / project
        license_file = upstream / "LICENSES/LGPL-2.1-or-later.txt"
        if not license_file.is_file():
            raise ValueError(f"Missing original upstream LGPL text: {license_file}")
        copy_tree(upstream, source / "upstream" / project)
    files = [
        *BUILD_INPUTS,
        "packaging/linux/fcitx-cooperative/pack.py",
        "packaging/linux/fcitx-cooperative/fcitx-cooperative.py",
        "docs/fcitx-linux.md",
        "THIRD_PARTY_NOTICES.md",
    ]
    for relative in files:
        destination = source / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / relative, destination)
        expected = inputs["files"].get(relative)
        if expected is not None and sha256(destination) != expected:
            raise ValueError(f"Build input changed while packaging: {relative}")
    notices = [
        "# Private Fcitx cooperation bundle\n",
        "This opt-in bundle contains modified Fcitx components. Original copyright,",
        "license and attribution files are preserved in each source/upstream directory.",
        "The original LGPL-2.1-or-later terms apply to the modified libraries; consult",
        "the source-file notices for individual components and bundled data.\n",
        "Corresponding modified sources are included in source/upstream. TypeLay's",
        "modifications are also provided as patches in source/tests/linux/fcitx_cooperative.",
        "Build commands, configurations and test fixtures are in that directory's run.py.",
        "From source/, run: python3 tests/linux/fcitx_cooperative/run.py --core-version 5.1.22",
        "This requires the documented build dependencies and pinned upstream downloads.",
        "You may modify and rebuild these components under their applicable licenses.\n",
        "Pinned upstream revisions and source archive SHA-256 values:",
    ]
    for project in UPSTREAM:
        commit, digest = pins[project]
        notices += [
            f"- {project}: https://github.com/fcitx/{project}/tree/{commit}",
            f"  Archive SHA-256: {digest}",
            f"  Original LGPL text: source/upstream/{project}/LICENSES/LGPL-2.1-or-later.txt",
        ]
    notices += [
        "",
        f"- KenLM: https://github.com/kpu/kenlm/tree/{pins['kenlm'][0]}",
        f"  Archive SHA-256: {pins['kenlm'][1]}",
        "  Source and original license notices: source/upstream/libime/src/libime/core/kenlm/",
        "  Preserve LICENSE, COPYING*, util/double-conversion/LICENSE and source-file notices.",
        "",
        "LibIME data archives and their generated data are redistributed under LGPL-2.1-or-later.",
        "Copyright 2002-2005 Yuking <yuking_net@sohu.com>; 2014-2026 CSSlayer <wengxt@gmail.com>.",
        "File-specific distribution attribution: https://metadata.ftp-master.debian.org/",
        "changelogs/main/libi/libime/unstable_copyright (data/dict*, data/lm*, data/table*).",
        "The full license is in source/upstream/libime/LICENSES/LGPL-2.1-or-later.txt.",
        "Corresponding raw data archives and their hashes are retained in source/upstream/libime/data/",
        "and manifest.json. Chinese py_table/py_stroke data and archives are not redistributed;",
        "the launcher checks the user's matching official Chinese 5.1.14 helper files.",
        "The original build download recipes and hashes are retained for reproducibility.",
        "Official system GTK, Boost, OpenCC, zstd and other dynamic dependencies are not",
        "redistributed here. Build-machine package versions are recorded separately from",
        "the bundled Fcitx, Chinese Addons and LibIME versions in manifest.json.",
    ]
    (bundle / "THIRD_PARTY_NOTICES.md").write_text("\n".join(notices) + "\n", encoding="utf-8")


def verify_sources(build):
    """Do not silently ship newer checkout patches with an older build's sources."""
    record = read_object(build / "build-inputs.json")
    inputs = record.get("files")
    if not isinstance(inputs, dict) or set(inputs) != set(BUILD_INPUTS):
        raise ValueError("Build input record must identify every cooperation build input")
    for relative in BUILD_INPUTS:
        if sha256(ROOT / relative) != inputs[relative]:
            raise ValueError(f"Checkout differs from the recorded build input: {relative}")
    for project in PATCHED_UPSTREAM:
        source = build / "sources" / project
        if source.is_symlink() or not source.is_dir():
            raise ValueError(f"Missing regular patched source directory: {source}")
        result = subprocess.run(
            [
                "patch",
                "--dry-run",
                "--reverse",
                "--force",
                "--fuzz=0",
                "-p1",
                "-i",
                str(ROOT / "tests/linux/fcitx_cooperative" / f"{project}.patch"),
            ],
            cwd=source,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=30,
        )
        if result.returncode:
            raise ValueError(f"Build sources do not match {project}.patch:\n{result.stdout}")
    abi = Path("src/TypeLay.Linux/native/fcitx5/typelay-fcitx-v1.h")
    if sha256(ROOT / abi) != sha256(build / "sources/fcitx5-gtk" / abi.name):
        raise ValueError("Build GTK sources do not match the cooperation ABI header")
    return record


def copy_qt(qt, bundle):
    versions = read_object(qt / "versions.json")
    if (
        versions.get("qt_build_version") != "6.11.2"
        or versions.get("fcitx5_qt_source_version") != "5.1.15"
    ):
        raise ValueError("Optional Qt bundle requires Qt 6.11.2 and Fcitx Qt 5.1.15")
    pin = "272b6b9eba97f0ed97e8a5caaf0bb22c6123f462"
    archive_hash = "b2c5f7c47009656ce79eac0c8f047a73b224210d0bc232150349577525fbd0d3"
    if versions.get("source_commit") != pin or versions.get("source_sha256") != archive_hash:
        raise ValueError("Unexpected Fcitx Qt source pin")
    if sha256(qt / "fcitx5-qt.tar.gz") != archive_hash:
        raise ValueError("Missing matching original Fcitx Qt archive")
    files = {
        "src/TypeLay.Linux/native/fcitx5/typelay-fcitx-qt-v1.h",
        "packaging/linux/fcitx-cooperative/qt-runtime-probe.cpp",
        "tests/linux/qt_native_fixture.cpp",
        *(
            f"tests/linux/qt_cooperative/{name}"
            for name in ("run.py", "fixture.cpp", "qpa_ack.cpp", "fcitx5-qt.patch")
        ),
        *(
            str(p.relative_to(ROOT))
            for p in (ROOT / "src/TypeLay.Linux/native/qt6").iterdir()
            if p.is_file()
        ),
    }
    inputs = versions.get("build_inputs", {})
    if set(inputs) != files or any(sha256(ROOT / name) != inputs[name] for name in files):
        raise ValueError("Qt binaries were built from different checkout inputs")
    probe = read_object(qt / "result.json")
    if probe.get("passed") is not True or not probe.get("default_async_required"):
        raise ValueError("Successful original async Qt regression evidence required")
    runtime = read_object(qt / "async-runtime.json")
    if runtime.get("build_version") != "6.11.2" or runtime.get("runtime_version") != "6.11.2":
        raise ValueError("Qt probe runtime is not version matched")
    binaries = {
        "plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so": "qt6/plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so",
        "native/qt6/generic/libtypelay-qt6.so": "qt6/plugins/generic/libtypelay-qt6.so",
        "native/qt-native-fixture": None,
        "qt-runtime-probe": "qt6/bin/qt-runtime-probe",
    }
    for name, destination in binaries.items():
        digest = sha256(qt / name)
        if versions.get("binary_sha256", {}).get(name) != digest:
            raise ValueError(f"Qt binary changed after its build: {name}")
        if destination:
            target = bundle / destination
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(qt / name, target)
    upstream = qt / f"fcitx5-qt-{pin}"
    abi = "src/TypeLay.Linux/native/fcitx5/typelay-fcitx-qt-v1.h"
    if sha256(upstream / "common" / Path(abi).name) != inputs[abi]:
        raise ValueError("Qt source ABI differs from the recorded input")
    subprocess.run(
        [
            "patch",
            "--dry-run",
            "--reverse",
            "--force",
            "--fuzz=0",
            "-p1",
            "-i",
            str(ROOT / "tests/linux/qt_cooperative/fcitx5-qt.patch"),
        ],
        cwd=upstream,
        check=True,
        timeout=30,
        stdout=subprocess.PIPE,
    )
    for license_name in ("BSD-3-Clause.txt", "LGPL-2.1-or-later.txt"):
        if not (upstream / "LICENSES" / license_name).is_file():
            raise ValueError("Missing original Fcitx Qt license")
    copy_tree(upstream, bundle / "source/upstream/fcitx5-qt")
    for name in files:
        target = bundle / "source" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / name, target)
    with (bundle / "THIRD_PARTY_NOTICES.md").open("a") as notices:
        notices.write(
            "\n## Optional Qt cooperation\nFcitx5-Qt 5.1.15 is modified by TypeLay. "
            "Original BSD-3-Clause/LGPL-2.1-or-later notices and complete modified "
            "sources are in source/upstream/fcitx5-qt; the dated patch and build "
            "recipe are in source/tests/linux/qt_cooperative. Build with the "
            "Qt 6.11.2 SDK using that run.py and the private Fcitx build. "
            "The Qt libraries themselves are supplied by the user system.\n"
        )
    return {
        "qtVersion": "6.11.2",
        "fcitxQtVersion": "5.1.15",
        "sourceCommit": pin,
        "sourceSha256": archive_hash,
        "buildInputs": inputs,
        "binarySha256": versions["binary_sha256"],
        "probeEvidenceSha256": sha256(qt / "result.json"),
        "modelValidation": "required after relocation",
    }


def manifest(bundle, versions, pins, inputs, qt=None):
    files = {}
    for path in sorted(bundle.rglob("*")):
        relative = path.relative_to(bundle).as_posix()
        if path.is_symlink():
            target = path.resolve(strict=True)
            if not target.is_relative_to(bundle):
                raise ValueError(f"Packaged symlink escapes the bundle: {relative}")
            files[relative] = {"symlink": os.readlink(path)}
        elif path.is_file():
            files[relative] = {"sha256": sha256(path)}
    packages = {}
    for line in versions.get("packages", "").splitlines():
        parts = line.split()
        if len(parts) == 2:
            packages[parts[0]] = parts[1]
    return {
        "schemaVersion": 1,
        "bundleName": BUNDLE,
        "coreVersion": CORE_VERSION,
        "versions": versions,
        "pins": pins,
        "buildInputs": inputs,
        "libimeDataSources": DATA_ARCHIVES,
        "chineseDataSources": CHINESE_DATA_ARCHIVES,
        "systemDependencies": {
            "packages": packages,
            "gtk3": versions.get("gtk3"),
            "gtk4": versions.get("gtk4"),
            "requiredUserPackageVersions": {"fcitx5-chinese-addons": "5.1.14", "libime": "1.1.16"},
            "chineseMetadataDirectory": "core/share/fcitx5",
            "libimeDataDirectory": "core/share/libime",
            "libimeModelDirectory": "core/lib/libime",
        },
        "qt6": qt,
        "files": files,
    }


def write_archive(bundle, output):
    epoch = int(os.environ.get("SOURCE_DATE_EPOCH", "0"))
    if epoch < 0:
        raise ValueError("SOURCE_DATE_EPOCH must be nonnegative")
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            dir=output.parent, prefix=".fcitx-bundle-", delete=False
        ) as raw:
            temporary = Path(raw.name)
            with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=epoch) as compressed:
                with tarfile.open(mode="w", fileobj=compressed) as archive:
                    for path in [bundle, *sorted(bundle.rglob("*"))]:
                        info = archive.gettarinfo(path, arcname=path.relative_to(bundle.parent))
                        info.uid = info.gid = 0
                        info.uname = info.gname = ""
                        info.mtime = epoch
                        info.mode = 0o755 if info.isdir() or info.mode & 0o111 else 0o644
                        if info.isfile():
                            with path.open("rb") as stream:
                                archive.addfile(info, stream)
                        else:
                            archive.addfile(info)
        temporary.replace(output)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-output", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="Destination .tar.gz archive")
    parser.add_argument("--qt-output", type=Path, help="Fresh matching Qt build output (optional)")
    args = parser.parse_args()
    build = args.build_output.resolve(strict=True)
    output = args.output.resolve()
    if not output.name.endswith(".tar.gz"):
        parser.error("--output must end in .tar.gz")
    versions, pins = metadata(build)
    inputs = verify_sources(build)
    with tempfile.TemporaryDirectory(prefix="typelay-fcitx-pack-") as temporary:
        bundle = Path(temporary) / BUNDLE
        bundle.mkdir()
        copy_core(build, bundle)
        copy_libraries(build, bundle)
        copy_sources(build, bundle, pins, inputs)
        shutil.copyfile(ROOT / "docs/fcitx-linux.md", bundle / "README.md")
        launcher = bundle / "fcitx-cooperative.py"
        shutil.copyfile(ROOT / "packaging/linux/fcitx-cooperative/fcitx-cooperative.py", launcher)
        launcher.chmod(0o755)
        qt = copy_qt(args.qt_output.resolve(strict=True), bundle) if args.qt_output else None
        (bundle / "manifest.json").write_text(
            json.dumps(manifest(bundle, versions, pins, inputs, qt), indent=2, ensure_ascii=False)
            + "\n",
            encoding="utf-8",
        )
        write_archive(bundle, output)
    print(f"{output}\nSHA-256: {sha256(output)}")


if __name__ == "__main__":
    main()
