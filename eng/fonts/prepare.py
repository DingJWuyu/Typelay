"""Prepare and verify TypeLay's pinned, offline UI font bundle."""

import argparse
import hashlib
import json
import os
import shutil
import tempfile
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LOCK_PATH = Path(__file__).with_name("fonts.lock.json")
MANIFEST = "manifest.json"


# Only actual prototype roles, not a weight-by-size Cartesian product. The
# optical axis values are separately pinned after comparison with Chrome auto.
SERIF_ROLE_SIZES = {
    400: (13, 14, 15, 16, 17, 18, 19),
    500: (11, 13.5, 15, 17, 18, 20, 22, 26, 30),
}


def serif_instance_names(item):
    weight, size = item["weight"], item["cssSize"]
    if (
        weight not in SERIF_ROLE_SIZES
        or size not in SERIF_ROLE_SIZES[weight]
        or set(item["instance"]) != {"wght", "opsz"}
        or item["instance"]["wght"] != weight
        or item["instance"]["opsz"] != size
    ):
        raise ValueError("unsupported-serif-role-instance")
    family = f"TypeLay Serif {size:g}"
    style = "Regular" if weight == 400 else "Medium"
    if item["family"] != family:
        raise ValueError("unexpected-serif-instance-family")
    full_name = f"{family} {style}"
    axes = f"wght={weight};opsz={item['instance']['opsz']:g}"
    return {
        1: family if weight == 400 else full_name,
        2: "Regular",
        3: f"TypeLay;SourceSerif4.004;{axes};fontTools4.61.1",
        4: full_name,
        5: "Version 4.004;TypeLay static optical instance 1",
        6: f"TypeLaySerif-{size:g}-{style}",
        10: f"Static {axes} instance of Source Serif 4.004, produced by TypeLay "
        "using fontTools 4.61.1. Not an upstream original. No subsetting or rehinting.",
        16: family,
        17: style,
    }


def load_lock():
    lock = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
    if lock["schemaVersion"] != 1:
        raise ValueError("unsupported-font-lock-version")
    paths = [item["path"] for item in lock["files"]]
    if len(paths) != len(set(paths)):
        raise ValueError("duplicate-font-output")
    for path in paths:
        if Path(path).is_absolute() or ".." in Path(path).parts or "\\" in path:
            raise ValueError("invalid-font-output-path")
    return lock


def check_file(path, item):
    if not path.is_file() or path.is_symlink():
        raise ValueError(f"font-file-missing-or-symlink: {path}")
    if path.stat().st_size != item["bytes"]:
        raise ValueError(f"font-file-size-mismatch: {path}")
    with path.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    if actual != item["sha256"]:
        raise ValueError(f"font-file-sha256-mismatch: {path}")


def bundle_manifest(lock):
    return {
        "schemaVersion": 1,
        "files": [
            {key: value for key, value in item.items() if key not in ("source", "instance")}
            for item in lock["files"]
        ],
    }


def manifest_bytes(lock):
    return (json.dumps(bundle_manifest(lock), ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def verify_bundle(output, lock=None):
    """Verify the full bundle against the repository lock, not its own manifest."""
    if output.is_symlink():
        raise ValueError("font-bundle-root-is-symlink")
    lock = load_lock() if lock is None else lock
    expected = {item["path"] for item in lock["files"]} | {MANIFEST}
    actual = {path.relative_to(output).as_posix() for path in output.rglob("*") if path.is_file()}
    if expected != actual or any(path.is_symlink() for path in output.rglob("*")):
        raise ValueError("font-bundle-file-set-mismatch")
    for item in lock["files"]:
        check_file(output / item["path"], item)
    if (output / MANIFEST).read_bytes() != manifest_bytes(lock):
        raise ValueError("font-bundle-manifest-mismatch")


def acquire(source, cache, offline):
    if "repositoryPath" in source:
        path = ROOT / source["repositoryPath"]
        check_file(path, source)
        return path
    path = cache / source["sha256"]
    if path.exists():
        check_file(path, source)
        return path
    if offline:
        raise ValueError(f"font-source-not-cached: {source['url']}")
    cache.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="download-", dir=cache) as temporary:
        download = Path(temporary) / "font"
        with urllib.request.urlopen(source["url"], timeout=120) as response:
            with download.open("wb") as stream:
                size = 0
                while chunk := response.read(1024 * 1024):
                    size += len(chunk)
                    if size > source["bytes"]:
                        raise ValueError("font-download-exceeds-locked-size")
                    stream.write(chunk)
        check_file(download, source)
        os.replace(download, path)
    return path


def instantiate(source, output, item, version):
    import fontTools
    from fontTools.ttLib import TTFont
    from fontTools.varLib.instancer import instantiateVariableFont

    if fontTools.__version__ != version:
        raise ValueError(f"fontTools-version-mismatch: expected {version}")
    font = TTFont(source, recalcTimestamp=False)
    names = {
        1: "Noto Sans SC SemiBold",
        2: "Regular",
        3: "TypeLay;NotoSansSC2.004;wght=600;fontTools4.61.1",
        4: "Noto Sans SC SemiBold",
        5: "Version 2.004;TypeLay static instance 1",
        6: "TypeLayNotoSansSC-SemiBold",
        10: "Static wght=600 instance of Noto Sans SC 2.004, produced by TypeLay using "
        "fontTools 4.61.1. Not an upstream original. No subsetting or rehinting.",
        16: "Noto Sans SC",
        17: "SemiBold",
    }
    expected_axes = [("wght", 100, 100, 900)]
    if "opsz" in item["instance"]:
        expected_axes = [("wght", 200, 400, 900), ("opsz", 8, 20, 60)]
        if font["name"].getDebugName(1) != "Source Serif 4" or not font["name"].getDebugName(
            5
        ).startswith("Version 4.004;"):
            raise ValueError("unexpected-serif-source")
        names = serif_instance_names(item)
    elif item["instance"] != {"wght": 600}:
        raise ValueError("unsupported-font-instance")
    if [
        (axis.axisTag, axis.minValue, axis.defaultValue, axis.maxValue)
        for axis in font["fvar"].axes
    ] != expected_axes:
        raise ValueError("unexpected-font-axes")
    cmap = font.getBestCmap()
    font = instantiateVariableFont(font, item["instance"], inplace=True, optimize=True)
    # WPF can select a localized legacy family when the preferred family is
    # missing for that language. Keep every existing Windows language's family
    # and subfamily records complete, not just the English records.
    windows_languages = {
        (record.platformID, record.platEncID, record.langID)
        for record in font["name"].names
        if record.platformID == 3
    } | {(3, 1, 0x409)}
    for name_id, value in names.items():
        for name in font["name"].names:
            if name.nameID == name_id:
                name.string = value.encode(name.getEncoding())
        for platform, encoding, language in sorted(windows_languages):
            font["name"].setName(value, name_id, platform, encoding, language)
    font["name"].removeNames(nameID=25)
    font["name"].removeUnusedNames(font)
    # A fully instantiated face can retain a STAT axis with no style values.
    # Keep the static OS/2 weight and complete family names, as in the static
    # siblings, instead of an empty variable-style description. Real Windows
    # glyph tests still verify that the resulting face is selected at 600.
    if not {"fvar", "gvar"}.intersection(font.keys()) and "STAT" in font:
        stat = font["STAT"].table
        if stat.AxisValueCount == 0 and (
            stat.AxisValueArray is None or not stat.AxisValueArray.AxisValue
        ):
            del font["STAT"]
    if font.getBestCmap() != cmap or font["OS/2"].usWeightClass != item["instance"]["wght"]:
        raise ValueError("font-instance-character-or-weight-mismatch")
    if {"fvar", "gvar", "avar", "HVAR", "MVAR", "VVAR"}.intersection(font.keys()):
        raise ValueError("font-instance-is-still-variable")
    if not {"GSUB", "GPOS"}.issubset(font.keys()):
        raise ValueError("font-instance-layout-tables-missing")
    font.save(output)
    font.close()


def prepare_bundle(output, cache, offline=False):
    """Create a complete build-time bundle; this is not a runtime downloader."""
    lock = load_lock()
    if output.exists():
        verify_bundle(output, lock)
        return
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".fonts-", dir=output.parent) as temporary:
        staging = Path(temporary) / "bundle"
        staging.mkdir()
        for item in lock["files"]:
            target = staging / item["path"]
            target.parent.mkdir(parents=True, exist_ok=True)
            source = acquire(item["source"], cache, offline)
            if "instance" in item:
                instantiate(source, target, item, lock["fontToolsVersion"])
            else:
                shutil.copyfile(source, target)
            check_file(target, item)
        (staging / MANIFEST).write_bytes(manifest_bytes(lock))
        verify_bundle(staging, lock)
        staging.rename(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/ui-fonts")
    parser.add_argument("--cache", type=Path, default=ROOT / "artifacts/font-sources")
    parser.add_argument("--offline", action="store_true", help="Use only pinned local source cache")
    parser.add_argument(
        "--verify", action="store_true", help="Verify without downloading or generating"
    )
    args = parser.parse_args()
    if args.verify:
        verify_bundle(args.output)
    else:
        prepare_bundle(args.output, args.cache, args.offline)
    print(f"Verified offline UI font bundle: {args.output}")


if __name__ == "__main__":
    main()
