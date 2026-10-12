"""Build the relocatable CachyOS x86_64 distribution; never touches Windows outputs."""

import argparse
import hashlib
import os
import platform
import shutil
import subprocess
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def run(*arguments):
    subprocess.run(list(map(str, arguments)), cwd=ROOT, check=True)


def build(output, fixture=False):
    if platform.system() != "Linux" or platform.machine() != "x86_64":
        raise RuntimeError("build-requires-linux-x86_64")
    output = output.resolve()
    if output == ROOT or not output.is_relative_to(ROOT / "artifacts"):
        raise ValueError("output-must-be-in-artifacts")
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True)
    native = ROOT / "artifacts/linux/native"
    run(
        "dotnet",
        "publish",
        ROOT / "src/TypeLay.Linux/TypeLay.Linux.csproj",
        "-c",
        "Release",
        "-r",
        "linux-x64",
        "--self-contained",
        "true",
        "--nologo",
        "-o",
        output / "app",
    )
    run(
        "cmake",
        "-S",
        ROOT / "src/TypeLay.Linux/native",
        "-B",
        native,
        "-G",
        "Ninja",
        "-DCMAKE_BUILD_TYPE=Release",
        f"-DTYPELAY_NATIVE_TESTS={'ON' if fixture else 'OFF'}",
    )
    run("cmake", "--build", native)
    (output / "native").mkdir()
    for name in ("libtypelay-gtk3.so", "typelay-supervise"):
        shutil.copy2(native / name, output / "native" / name)
    shutil.copytree(
        ROOT / "src/TypeLay.Linux/desktop",
        output / "desktop",
        ignore=shutil.ignore_patterns("__pycache__", "*.pyc"),
    )
    shutil.copytree(
        ROOT / "src/TypeLay.Worker",
        output / "worker",
        ignore=shutil.ignore_patterns("__pycache__", "*.pyc"),
    )
    for browser in ("chromium", "firefox"):
        destination = output / "browser" / browser
        destination.mkdir(parents=True)
        source = ROOT / "src/TypeLay.Linux/browser"
        for name in ("content.js", "background.js"):
            shutil.copy2(source / name, destination / name)
        shutil.copy2(source / f"manifest.{browser}.json", destination / "manifest.json")
    shutil.copy2(ROOT / "docs/browser-linux.md", output / "browser-linux.md")
    for name in ("typelay", "typelay-browser-host", "install.py"):
        shutil.copy2(ROOT / "packaging/linux" / name, output / name)
    for name in ("LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"):
        if (ROOT / name).is_file():
            shutil.copy2(ROOT / name, output / name)
    shutil.copy2(ROOT / "docs/linux.md", output / "README.md")
    manifest = []
    for path in sorted(output.rglob("*")):
        if path.is_file():
            with path.open("rb") as stream:
                checksum = hashlib.file_digest(stream, "sha256").hexdigest()
            manifest.append(f"{checksum}  {path.relative_to(output).as_posix()}\n")
    (output / "MANIFEST.sha256").write_text("".join(manifest), encoding="utf-8")
    archive = ROOT / "artifacts/TypeLay-Linux-preview-x86_64.tar.gz"
    with tarfile.open(archive, "w:gz", format=tarfile.PAX_FORMAT) as package:
        package.add(output, arcname="TypeLay-Linux-preview", filter=normalize_owner)
    with archive.open("rb") as stream:
        checksum = hashlib.file_digest(stream, "sha256").hexdigest()
    archive.with_suffix(archive.suffix + ".sha256").write_text(f"{checksum}  {archive.name}\n")
    print(f"Built {archive.relative_to(ROOT)}")


def normalize_owner(info):
    info.uid = info.gid = 0
    info.uname = info.gname = ""
    if "SOURCE_DATE_EPOCH" in os.environ:
        info.mtime = int(os.environ["SOURCE_DATE_EPOCH"])
    return info


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/linux/package")
    parser.add_argument("--with-test-fixture", action="store_true")
    args = parser.parse_args()
    build(args.output, args.with_test_fixture)
