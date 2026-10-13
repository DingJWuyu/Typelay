"""Build the relocatable CachyOS x86_64 distribution; never touches Windows outputs."""

import argparse
import hashlib
import json
import os
import platform
import shutil
import subprocess
import tarfile
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


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
        ROOT / "src/linux/TypeLay.Linux/TypeLay.Linux.csproj",
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
    package_runtime_notices(output)
    run(
        "cmake",
        "-S",
        ROOT / "src/linux/native",
        "-B",
        native,
        "-G",
        "Ninja",
        "-DCMAKE_BUILD_TYPE=Release",
        f"-DTYPELAY_NATIVE_TESTS={'ON' if fixture else 'OFF'}",
    )
    run("cmake", "--build", native)
    (output / "native").mkdir()
    for name in ("libtypelay-gtk3.so", "libtypelay-gtk4.so", "typelay-supervise"):
        shutil.copy2(native / name, output / "native" / name)
    shutil.copytree(
        ROOT / "src/linux/desktop",
        output / "desktop",
        ignore=shutil.ignore_patterns("__pycache__", "*.pyc"),
    )
    shutil.copytree(
        ROOT / "src/shared/TypeLay.Worker",
        output / "worker",
        ignore=shutil.ignore_patterns("__pycache__", "*.pyc"),
    )
    for browser in ("chromium", "firefox"):
        destination = output / "browser" / browser
        destination.mkdir(parents=True)
        source = ROOT / "src/linux/browser"
        for name in ("content.js", "background.js", "editor-bridge.js", "editor-adapter.js"):
            shutil.copy2(source / name, destination / name)
        shutil.copy2(source / f"manifest.{browser}.json", destination / "manifest.json")
    # Firefox stable requires Mozilla signing for permanent installation. Ship
    # the exact unsigned payload for temporary loading or an explicit signing step.
    with zipfile.ZipFile(output / "browser/typelay-firefox-unsigned.xpi", "w") as addon:
        for name in (
            "manifest.json",
            "background.js",
            "content.js",
            "editor-bridge.js",
            "editor-adapter.js",
        ):
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            addon.writestr(entry, (output / "browser/firefox" / name).read_bytes())
    package_editor(output)
    for name in ("typelay", "typelay-browser-host", "install.py"):
        shutil.copy2(ROOT / "packaging/linux" / name, output / name)
    for name in ("LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"):
        if (ROOT / name).is_file():
            shutil.copy2(ROOT / name, output / name)
    for name in ("README.md", "browser.md", "fcitx.md", "qt.md", "vscode.md"):
        shutil.copy2(ROOT / "docs/platforms/linux" / name, output / name)
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


def package_runtime_notices(output):
    # Resolve the same SDK/runtime selection used by publish, then retain only
    # packs actually present in this published application's dependency graph.
    metadata = json.loads(
        subprocess.check_output(
            [
                "dotnet",
                "msbuild",
                "src/linux/TypeLay.Linux/TypeLay.Linux.csproj",
                "-nologo",
                "-verbosity:quiet",
                "-target:ResolveFrameworkReferences",
                "-getItem:ResolvedRuntimePack",
                "-property:Configuration=Release",
                "-property:RuntimeIdentifier=linux-x64",
                "-property:SelfContained=true",
            ],
            cwd=ROOT,
            text=True,
        )
    )
    resolved = {
        f"runtimepack.{pack['NuGetPackageId']}/{pack['NuGetPackageVersion']}": pack
        for pack in metadata["Items"]["ResolvedRuntimePack"]
    }
    dependencies = json.loads((output / "app/TypeLay.Linux.deps.json").read_text())
    shipped = [name for name in dependencies["libraries"] if name.startswith("runtimepack.")]
    if not shipped or any(name not in resolved for name in shipped):
        raise RuntimeError("published-runtime-pack-resolution-mismatch")
    for name in shipped:
        pack = resolved[name]
        source = Path(pack["PackageDirectory"])
        notices = [
            path
            for path in source.rglob("*")
            if path.is_file()
            and any(
                word in path.name.upper() for word in ("LICENSE", "LICENCE", "COPYING", "NOTICE")
            )
        ]
        if not any("LICENSE" in path.name.upper() for path in notices) or not any(
            "NOTICE" in path.name.upper() for path in notices
        ):
            raise RuntimeError(f"runtime-pack-notices-missing: {name}")
        destination = (
            output / "app/licenses" / pack["NuGetPackageId"].lower() / pack["NuGetPackageVersion"]
        )
        for notice in notices:
            target = destination / notice.relative_to(source)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(notice, target)


def package_editor(output):
    # Standard VS Code VSIX schema; installation remains an explicit user action.
    destination = output / "vscode"
    destination.mkdir()
    names = ("package.json", "extension.js", "transaction.js", "host-client.js")
    for name in names:
        shutil.copy2(ROOT / "src/linux/vscode" / name, destination / name)
    metadata = json.loads((destination / "package.json").read_text(encoding="utf-8"))
    manifest = ET.Element(
        "PackageManifest",
        {"Version": "2.0.0", "xmlns": "http://schemas.microsoft.com/developer/vsx-schema/2011"},
    )
    details = ET.SubElement(manifest, "Metadata")
    ET.SubElement(
        details,
        "Identity",
        {
            "Id": metadata["name"],
            "Version": metadata["version"],
            "Publisher": metadata["publisher"],
            "Language": "en-US",
        },
    )
    ET.SubElement(details, "DisplayName").text = metadata["displayName"]
    ET.SubElement(details, "Description").text = metadata["description"]
    properties = ET.SubElement(details, "Properties")
    for name, value in (
        ("Microsoft.VisualStudio.Code.Engine", metadata["engines"]["vscode"]),
        ("Microsoft.VisualStudio.Code.ExtensionKind", "ui"),
        ("Microsoft.VisualStudio.Code.ExecutesCode", "true"),
    ):
        ET.SubElement(properties, "Property", {"Id": name, "Value": value})
    installation = ET.SubElement(manifest, "Installation")
    ET.SubElement(installation, "InstallationTarget", {"Id": "Microsoft.VisualStudio.Code"})
    ET.SubElement(manifest, "Dependencies")
    assets = ET.SubElement(manifest, "Assets")
    ET.SubElement(
        assets,
        "Asset",
        {
            "Type": "Microsoft.VisualStudio.Code.Manifest",
            "Path": "extension/package.json",
            "Addressable": "true",
        },
    )
    types = ET.Element(
        "Types", {"xmlns": "http://schemas.openxmlformats.org/package/2006/content-types"}
    )
    for extension, content in (
        ("json", "application/json"),
        ("js", "text/javascript"),
        ("vsixmanifest", "text/xml"),
        ("txt", "text/plain"),
    ):
        ET.SubElement(types, "Default", {"Extension": extension, "ContentType": content})
    payload = {
        "extension.vsixmanifest": ET.tostring(manifest, encoding="utf-8", xml_declaration=True),
        "[Content_Types].xml": ET.tostring(types, encoding="utf-8", xml_declaration=True),
    }
    payload.update({f"extension/{name}": (destination / name).read_bytes() for name in names})
    with zipfile.ZipFile(destination / "typelay-editor.vsix", "w") as archive:
        for name, content in sorted(payload.items()):
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, content)


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
