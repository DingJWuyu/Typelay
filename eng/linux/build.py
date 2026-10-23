"""Build the relocatable CachyOS x86_64 distribution; never touches Windows outputs."""

import argparse
import hashlib
import importlib.util
import json
import os
import platform
import posixpath
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from eng.linux.cooperative import inputs as cooperation  # noqa: E402

DESKTOP_ARCHIVE = "TypeLay-Fcitx-cooperative.tar.gz"
SOURCE_PATHS = (
    "src/linux",
    "src/shared",
    "eng/linux",
    "packaging/linux",
    "docs/platforms/linux",
    "tests/linux/fcitx_cooperative",
    "tests/linux/qt_cooperative",
    "tests/linux/fixtures/qt_native_fixture.cpp",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "global.json",
    "LICENSE",
    "NOTICE",
    "THIRD_PARTY_NOTICES.md",
)


def source_identity(*, require_clean=False):
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    changed = subprocess.check_output(
        ["git", "status", "--porcelain", "--untracked-files=all", "--", *SOURCE_PATHS],
        cwd=ROOT,
        text=True,
    )
    if not re.fullmatch(r"[0-9a-f]{40}", commit) or (require_clean and changed):
        raise ValueError("full-bundle-requires-clean-source-checkout")
    return {"schemaVersion": 1, "sourceCommit": commit, "sourceDirty": bool(changed)}


def run(*arguments):
    subprocess.run(list(map(str, arguments)), cwd=ROOT, check=True)


def package_documentation(output, identity):
    source = ROOT / "docs/platforms/linux"
    bundled = ("browser.md", "fcitx.md", "qt.md", "vscode.md")
    for name in bundled:
        shutil.copy2(source / name, output / name)
    text = (source / "README.md").read_text(encoding="utf-8")
    heading = "### 当前可验证候选\n"
    following = "## 安装、启动与首次配置\n"
    if text.count(heading) != 1 or text.count(following) != 1:
        raise ValueError("linux-readme-package-section")
    start, end = text.index(heading), text.index(following)
    if start >= end:
        raise ValueError("linux-readme-package-section-order")
    commit = identity["sourceCommit"]
    upstream = "https://github.com/DingJWuyu/Typelay/blob/"
    replacement = (
        heading + "\n这是你当前解压的包，不需要根据仓库上一次推荐重新下载。\n\n"
        f"- 本包源码基准：`{commit}`；`sourceDirty={str(identity['sourceDirty']).lower()}`。\n"
        "- 身份记录：[build-info.json](build-info.json)；完整性清单："
        "[MANIFEST.sha256](MANIFEST.sha256)。压缩包校验使用下载时随附的 `.sha256`。\n"
        f"- [对应源码说明]({upstream}{commit}/docs/platforms/linux/README.md)；"
        "[构建与检查记录](https://github.com/DingJWuyu/Typelay/actions/workflows/check.yml)。\n\n"
        "构建身份不代表检查通过；请核对对应运行的 Linux 结果。模型质量、物理输入法和真机体验仍须分别验收。\n\n"
    )
    if identity["sourceDirty"]:
        replacement += "此构建包含未提交改动；源码链接仅对应基准提交，不能代表包内全部修改。\n\n"
    text = text[:start] + replacement + text[end:]

    def link(match):
        target = match[2]
        if target.startswith("#") or ":" in target:
            return match[0]
        path, separator, anchor = target.partition("#")
        if path in (*bundled, "build-info.json", "MANIFEST.sha256"):
            return match[0]
        relative = posixpath.normpath("docs/platforms/linux/" + path)
        return match[1] + upstream + commit + "/" + relative + separator + anchor + match[3]

    text = re.sub(r"(\[[^\]]+\]\()([^\)]+)(\))", link, text)
    (output / "README.md").write_text(text, encoding="utf-8")


def build(output, fixture=False, cooperation_package=None, hy_runtime=None):
    if platform.system() != "Linux" or platform.machine() != "x86_64":
        raise RuntimeError("build-requires-linux-x86_64")
    identity = source_identity(require_clean=cooperation_package is not None)
    if cooperation_package is not None and not cooperation_package.is_file():
        raise ValueError("requested-cooperation-package-is-missing")
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
    package_worker(output, hy_runtime)
    package_browsers(output)
    package_editor(output)
    for name in ("typelay", "typelay-browser-host", "install.py"):
        shutil.copy2(ROOT / "packaging/linux" / name, output / name)
    for name in ("LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"):
        if (ROOT / name).is_file():
            shutil.copy2(ROOT / name, output / name)
    package_documentation(output, identity)
    if source_identity() != identity:
        raise ValueError("source-checkout-changed-during-build")
    (output / "build-info.json").write_text(json.dumps(identity) + "\n", encoding="utf-8")
    write_manifest(output)
    if cooperation_package is not None:
        finalize(output, cooperation_package)
    else:
        write_archive(output)
        print("Setup-only build: desktop cooperation payload is not included")


def package_worker(output, hy_runtime=None):
    # Only Python runtime modules and explicitly listed data are distributed.
    # native/hy also owns C++/headers/build locks in source, never copy that tree.
    source = ROOT / "src/shared/TypeLay.Worker"
    worker = output / "worker"
    (worker / "typelay_worker").mkdir(parents=True)
    for name in ("run_worker.py", "requirements.txt", "model-catalog.json"):
        shutil.copyfile(source / name, worker / name)
    for module in sorted((source / "typelay_worker").glob("*.py")):
        shutil.copyfile(module, worker / "typelay_worker" / module.name)
    native = worker / "native/hy"
    if hy_runtime is not None:
        from eng.linux.hy.build import build as build_hy

        build_hy(hy_runtime, native)
    else:
        # Argos-only builds remain supported. An installed active Hy package
        # fails closed until a distribution supplies its verified native closure.
        native.mkdir(parents=True)
        for name in ("model.lock.json", "cpu-runtime.lock.json"):
            shutil.copyfile(source / "native/hy" / name, native / name)
        shutil.copyfile(
            source / "native/hy/include/LICENSE.llama.cpp", native / "LICENSE.llama.cpp"
        )


def package_browsers(output):
    files = (
        "content.js",
        "background.js",
        "editor-bridge.js",
        "editor-adapter.js",
        "icons/typelay-16.png",
        "icons/typelay-48.png",
        "icons/typelay-128.png",
    )
    for browser in ("chromium", "firefox"):
        destination = output / "browser" / browser
        (destination / "icons").mkdir(parents=True)
        source = ROOT / "src/shared/browser"
        for name in files:
            shutil.copy2(source / name, destination / name)
        shutil.copy2(
            ROOT / "src/linux/browser" / f"manifest.{browser}.json", destination / "manifest.json"
        )
    # Firefox stable requires Mozilla signing for permanent installation. Ship
    # the exact unsigned payload for temporary loading or an explicit signing step.
    with zipfile.ZipFile(output / "browser/typelay-firefox-unsigned.xpi", "w") as addon:
        for name in ("manifest.json", *files):
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            addon.writestr(entry, (output / "browser/firefox" / name).read_bytes())


def package_files(output):
    result = {}
    for path in sorted(output.rglob("*")):
        if path.is_symlink() or not (path.is_file() or path.is_dir()):
            raise ValueError(f"Unsupported main-package entry: {path}")
        if path.is_file() and path != output / "MANIFEST.sha256":
            result[path.relative_to(output).as_posix()] = cooperation.sha256(path)
    return result


def write_manifest(output):
    (output / "MANIFEST.sha256").write_text(
        "".join(f"{checksum}  {name}\n" for name, checksum in package_files(output).items()),
        encoding="utf-8",
    )


def verify_main_package(output):
    recorded = {}
    for line in (output / "MANIFEST.sha256").read_text(encoding="utf-8").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  (.+)", line)
        if match is None or match[2] in recorded:
            raise ValueError("invalid-main-package-manifest")
        recorded[match[2]] = match[1]
    if not recorded or recorded != package_files(output):
        raise ValueError("main-package-files-changed-before-finalization")
    identity = source_identity(require_clean=True)
    if json.loads((output / "build-info.json").read_text(encoding="utf-8")) != identity:
        raise ValueError("main-package-source-identity-mismatch")
    return identity


def cooperation_metadata(archive):
    # Only consume archives produced by the controlled same-build packaging job.
    # Matching self-reported hashes are integrity checks, not a publisher signature.
    recipe_path = ROOT / "packaging/linux/fcitx-cooperative/pack.py"
    spec = importlib.util.spec_from_file_location("typelay_cooperation_pack", recipe_path)
    recipe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(recipe)
    files, entries = {}, {}
    manifest_bytes = None
    total = 0
    with tarfile.open(archive, "r|gz") as package:
        for entry in package:
            name = entry.name
            parts = name.split("/")
            if (
                len(entries) >= 20000
                or len(name) > 4096
                or entry.size < 0
                or name in entries
                or parts[0] != recipe.BUNDLE
                or any(part in ("", ".", "..") for part in parts)
                or "\\" in name
                or not (entry.isdir() or entry.isfile() or entry.issym())
                or entry.mode & 0o7000
            ):
                raise ValueError(f"Unsafe cooperation archive entry: {name}")
            entries[name] = entry
            relative = "/".join(parts[1:])
            if not relative:
                if not entry.isdir():
                    raise ValueError("Cooperation archive root must be a directory")
                continue
            if entry.isdir():
                continue
            if entry.issym():
                target = entry.linkname
                resolved = posixpath.normpath(posixpath.join(posixpath.dirname(name), target))
                if (
                    not target
                    or target.startswith("/")
                    or "\\" in target
                    or not resolved.startswith(recipe.BUNDLE + "/")
                ):
                    raise ValueError(f"Unsafe cooperation symlink: {relative}")
                files[relative] = {"symlink": target}
                continue
            total += entry.size
            if total > 2 * 1024**3:
                raise ValueError("Cooperation archive exceeds the unpacked size limit")
            with package.extractfile(entry) as stream:
                if relative == "manifest.json":
                    if entry.size > 8 * 1024**2:
                        raise ValueError("Cooperation manifest exceeds the size limit")
                    manifest_bytes = stream.read()
                else:
                    files[relative] = {"sha256": hashlib.file_digest(stream, "sha256").hexdigest()}
    for name, entry in entries.items():
        for parent in Path(name).parents:
            parent_entry = entries.get(parent.as_posix())
            if parent_entry is not None and not parent_entry.isdir():
                raise ValueError(f"Cooperation archive parent is not a directory: {name}")
        if entry.issym():
            seen = {name}
            target = name
            while entries[target].issym():
                target = posixpath.normpath(
                    posixpath.join(posixpath.dirname(target), entries[target].linkname)
                )
                if target in seen or target not in entries:
                    raise ValueError(f"Broken or cyclic cooperation symlink: {name}")
                seen.add(target)
    if manifest_bytes is None:
        raise ValueError("Missing cooperation manifest")
    metadata = json.loads(manifest_bytes)
    if (
        metadata.get("schemaVersion") != 1
        or metadata.get("bundleName") != recipe.BUNDLE
        or metadata.get("coreVersion") != cooperation.CORE_VERSION
        or metadata.get("files") != files
        or metadata.get("pins") != {name: list(pin) for name, pin in cooperation.FCITX_PINS.items()}
        or metadata.get("libimeDataSources") != cooperation.DATA_ARCHIVES
        or metadata.get("chineseDataSources") != cooperation.CHINESE_DATA_ARCHIVES
    ):
        raise ValueError("Cooperation manifest, pins or archive files mismatch")
    required = (
        "fcitx-cooperative.py",
        "THIRD_PARTY_NOTICES.md",
        "core/bin/fcitx5",
        *(f"lib/{name}" for name in recipe.LIBRARIES),
        "lib/gtk-4.0/4.0.0/immodules/libim-fcitx5.so",
        *(f"core/lib/lib{name.removeprefix('Lib')}.so" for name in cooperation.PRIVATE_VERSIONS),
        *(f"core/lib/fcitx5/lib{name}.so" for name in recipe.CHINESE_ADDONS),
        *(f"core/share/fcitx5/addon/{name}.conf" for name in recipe.CHINESE_ADDONS),
        "core/share/libime/sc.dict",
        "core/share/libime/extb.dict",
        *(f"core/share/libime/{name}.main.dict" for name in recipe.TABLE_DATA),
        "core/lib/libime/zh_CN.lm",
        "core/lib/libime/zh_CN.lm.predict",
        *(f"source/upstream/{name}/LICENSES/LGPL-2.1-or-later.txt" for name in recipe.UPSTREAM),
    )

    def archived_file(relative):
        entry = files.get(relative, {})
        while "symlink" in entry:
            relative = posixpath.normpath(
                posixpath.join(posixpath.dirname(relative), entry["symlink"])
            )
            entry = files.get(relative, {})
        return entry

    for name in required:
        if "sha256" not in archived_file(name):
            raise ValueError(f"Incomplete cooperation payload: {name}")
    versions = metadata.get("versions", {})
    if (
        versions.get("component_source_versions") != cooperation.SOURCE_VERSIONS
        or versions.get("installed_private_versions") != cooperation.PRIVATE_VERSIONS
        or versions.get("server_source_commit") != cooperation.FCITX_PINS["fcitx5"][0]
        or any(
            versions.get(key) != cooperation.CORE_VERSION
            for key in (
                "server_source_version",
                "server_runtime_version",
                "linked_core_development_version",
            )
        )
    ):
        raise ValueError("Cooperation component versions mismatch")
    binaries = versions.get("binary_sha256")
    if not isinstance(binaries, dict) or not {
        "fcitx5",
        *(f"lib/{name}" for name in recipe.LIBRARIES),
        "lib/gtk-4.0/4.0.0/immodules/libim-fcitx5.so",
    }.issubset(binaries):
        raise ValueError("Missing cooperation binary build record")
    for name, checksum in binaries.items():
        if name == "fcitx5":
            relative = "core/bin/fcitx5"
        elif name.startswith("core-prefix/"):
            relative = "core/" + name.removeprefix("core-prefix/")
        elif name.startswith("lib/"):
            relative = name
        else:
            raise ValueError(f"Unknown cooperation build binary path: {name}")
        if archived_file(relative) != {"sha256": checksum}:
            raise ValueError(f"Cooperation binary differs from its build record: {name}")
    expected = cooperation.hashes(cooperation.FCITX_BUILD_INPUTS)
    if metadata.get("buildInputs", {}).get("files") != expected:
        raise ValueError("Cooperation build inputs differ from the current checkout")
    test_inputs = metadata.get("testInputs")
    if test_inputs is not None:
        if test_inputs.get("files") != cooperation.hashes(cooperation.FCITX_TEST_INPUTS):
            raise ValueError("Cooperation test inputs differ from the current checkout")
        expected.update(test_inputs["files"])
    qt = metadata.get("qt6")
    if qt is not None:
        if (
            qt.get("qtVersion") != cooperation.QT_VERSION
            or qt.get("fcitxQtVersion") != cooperation.QT_SOURCE_VERSION
            or qt.get("sourceCommit") != cooperation.QT_PIN
            or qt.get("sourceSha256") != cooperation.QT_SHA256
            or qt.get("buildInputs") != cooperation.hashes(cooperation.QT_BUILD_INPUTS)
            or qt.get("testInputs") != cooperation.hashes(recipe.QT_TEST_INPUTS)
        ):
            raise ValueError("Cooperation Qt inputs differ from the current checkout")
        for name, relative in (
            (
                "plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so",
                "qt6/plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so",
            ),
            ("native/qt6/generic/libtypelay-qt6.so", "qt6/plugins/generic/libtypelay-qt6.so"),
            ("qt-runtime-probe", "qt6/bin/qt-runtime-probe"),
        ):
            if files.get(relative) != {"sha256": qt.get("binarySha256", {}).get(name)}:
                raise ValueError(f"Cooperation Qt binary differs from its build record: {name}")
        for license_name in ("BSD-3-Clause.txt", "LGPL-2.1-or-later.txt"):
            if "sha256" not in archived_file("source/upstream/fcitx5-qt/LICENSES/" + license_name):
                raise ValueError("Incomplete cooperation Qt source licenses")
        expected.update(qt["buildInputs"])
        expected.update(qt["testInputs"])
    expected.update(
        cooperation.hashes(
            (
                "packaging/linux/fcitx-cooperative/pack.py",
                "packaging/linux/fcitx-cooperative/fcitx-cooperative.py",
                "docs/platforms/linux/fcitx.md",
                "docs/platforms/linux/qt.md",
                "THIRD_PARTY_NOTICES.md",
            )
        )
    )
    for name, checksum in expected.items():
        if files.get("source/" + name) != {"sha256": checksum}:
            raise ValueError(
                f"Packaged cooperation source differs from the current checkout: {name}"
            )
    launcher = expected["packaging/linux/fcitx-cooperative/fcitx-cooperative.py"]
    if files.get("fcitx-cooperative.py") != {"sha256": launcher}:
        raise ValueError("Cooperation launcher differs from the current checkout")
    return {
        "schemaVersion": 1,
        "archive": DESKTOP_ARCHIVE,
        "archiveSha256": cooperation.sha256(archive),
        "manifestSha256": hashlib.sha256(manifest_bytes).hexdigest(),
        "sourceVersions": cooperation.SOURCE_VERSIONS,
        "privateVersions": cooperation.PRIVATE_VERSIONS,
        "qtVersion": cooperation.QT_VERSION if qt is not None else None,
    }


def finalize(output, cooperation_package):
    output = output.resolve()
    if not output.is_relative_to(ROOT / "artifacts") or not output.is_dir():
        raise ValueError("finalization-requires-existing-package-in-artifacts")
    identity = verify_main_package(output)
    support = output / "support"
    support.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".desktop-", dir=support) as temporary:
        pending = Path(temporary)
        shutil.copyfile(cooperation_package, pending / DESKTOP_ARCHIVE)
        metadata = cooperation_metadata(pending / DESKTOP_ARCHIVE)
        metadata["sourceCommit"] = identity["sourceCommit"]
        if source_identity(require_clean=True) != identity:
            raise ValueError("Source identity changed while attaching cooperation")
        (pending / "component.json").write_text(
            json.dumps(metadata, indent=2) + "\n",
            encoding="utf-8",
        )
        destination = support / "desktop"
        if destination.exists():
            shutil.rmtree(destination)
        pending.rename(destination)
    write_manifest(output)
    write_archive(output)


def write_archive(output):
    archive = ROOT / "artifacts/TypeLay-Linux-preview-x86_64.tar.gz"
    with tempfile.NamedTemporaryFile(
        dir=archive.parent, prefix=".main-package-", delete=False
    ) as raw:
        pending = Path(raw.name)
    try:
        with tarfile.open(pending, "w:gz", format=tarfile.PAX_FORMAT) as package:
            package.add(output, arcname="TypeLay-Linux-preview", filter=normalize_owner)
        pending.replace(archive)
    finally:
        pending.unlink(missing_ok=True)
    checksum = cooperation.sha256(archive)
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
    parser.add_argument(
        "--cooperation-package",
        type=Path,
        help="Attach the trusted same-build Fcitx cooperation archive; omission builds setup-only",
    )
    parser.add_argument(
        "--finalize",
        action="store_true",
        help="Attach cooperation to --output and rearchive without rebuilding the main application",
    )
    parser.add_argument(
        "--hy-runtime",
        type=Path,
        help="Optionally bundle Hy using an already prepared fixed CPU-only runtime; no downloads",
    )
    args = parser.parse_args()
    if args.finalize:
        if (
            args.cooperation_package is None
            or args.with_test_fixture
            or args.hy_runtime is not None
        ):
            parser.error(
                "--finalize requires --cooperation-package and cannot rebuild fixtures or Hy"
            )
        finalize(args.output, args.cooperation_package)
    else:
        build(args.output, args.with_test_fixture, args.cooperation_package, args.hy_runtime)
