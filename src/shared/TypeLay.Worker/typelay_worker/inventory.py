"""Disk inventory verification. Inactive packages never cause filesystem reads."""

from __future__ import annotations

import hashlib
import json
import re
import stat
from dataclasses import dataclass
from pathlib import Path

from .catalog import (
    ModelPackageSelection,
    load_package_catalog,
    read_active_selection,
    read_file_manifest,
)


@dataclass(frozen=True)
class VerifiedPackage:
    selection: ModelPackageSelection
    root: Path
    native: dict | None = None


@dataclass(frozen=True)
class VerifiedInventory:
    schema: int
    packages: tuple[VerifiedPackage, ...]
    identity_verified: bool


def verify_inventory(models: Path, catalog=None) -> VerifiedInventory:
    try:
        if catalog is None:
            catalog = load_package_catalog()
        with (models / "installed.json").open("rb") as stream:
            raw = stream.read(8 * 1024 * 1024 + 1)
        if len(raw) > 8 * 1024 * 1024:
            raise ValueError("model-inventory")
        inventory = json.loads(raw)
        selected = read_active_selection(inventory, catalog)
        if inventory["schema"] in (1, 2):
            _verify_legacy(models, inventory, catalog.legacy_directions)
            return VerifiedInventory(
                inventory["schema"],
                tuple(VerifiedPackage(item, models / item.directions[0].id) for item in selected),
                _verify_legacy_identity(models, inventory, selected),
            )
        verified = []
        # Complete all file checks before any native import or model load.
        for item in selected:
            root = models / "packages" / item.package.id / item.package.version
            expected = {entry.path: entry.sha256 for entry in item.files}
            _verify_files(root, expected)
            if item.package.backend == "argos":
                _verify_argos_metadata(root, expected, item)
                native = None
            else:
                # Admission only; the fixed production gate below still verifies the
                # exact OS/architecture, full native closure and model hashes.
                import sys

                if sys.platform not in ("linux", "win32"):
                    raise ValueError("hy-platform-unavailable")
                if item.package.backend == "index-translate-llama-cpu":
                    from .index_assets import verify_production
                else:
                    from .hy_assets import verify_production

                native = verify_production(root, verified_files=expected)
            verified.append(VerifiedPackage(item, root, native))
        return VerifiedInventory(3, tuple(verified), identity_verified=True)
    except (OSError, KeyError, TypeError, UnicodeError):
        raise ValueError("model-inventory") from None


def _verify_argos_metadata(root, expected, item):
    required = {
        "metadata.json",
        "sentencepiece.model",
        "model/config.json",
        "model/model.bin",
        "model/shared_vocabulary.json",
    }
    if not required.issubset(expected):
        raise ValueError("model-inventory")
    direction = item.directions[0]
    with (root / "metadata.json").open("rb") as stream:
        metadata_raw = stream.read(16385)
    if len(metadata_raw) > 16384:
        raise ValueError("model-inventory")
    metadata = json.loads(metadata_raw)
    if not isinstance(metadata, dict) or (
        metadata.get("from_code"),
        metadata.get("to_code"),
        metadata.get("package_version"),
    ) != (direction.source, direction.target, item.package.version):
        raise ValueError("model-inventory")


def _verify_legacy_identity(models, inventory, selected):
    # Optional read-only attestation. Legacy ordinary loading keeps its existing
    # requirements; special operations require the same complete files/metadata
    # proof as schema3, never just the catalog's inferred legacy selection.
    try:
        consumed = 0
        for item in selected:
            prefix = item.directions[0].id + "/"
            files = read_file_manifest(
                [
                    {"path": entry["path"][len(prefix) :], "sha256": entry["sha256"]}
                    for entry in inventory["files"]
                    if entry["path"].startswith(prefix)
                ]
            )
            expected = {entry.path: entry.sha256 for entry in files}
            consumed += len(expected)
            root = models / item.directions[0].id
            _verify_files(root, expected)
            _verify_argos_metadata(root, expected, item)
        return consumed == len(inventory["files"])
    except ValueError as error:
        if str(error) == "model-integrity":
            raise
        return False
    except (OSError, KeyError, TypeError, AttributeError):
        return False


def _verify_files(root: Path, expected: dict[str, str]) -> None:
    for path in (root, *root.parents):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("model-inventory")
    actual = set()
    for path in root.rglob("*"):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("model-inventory")
        if stat.S_ISREG(info.st_mode):
            if info.st_nlink != 1:
                raise ValueError("model-inventory")
            actual.add(path.relative_to(root).as_posix())
        elif not stat.S_ISDIR(info.st_mode):
            raise ValueError("model-inventory")
    if actual != set(expected):
        raise ValueError("model-inventory")
    for relative, digest in expected.items():
        with (root / relative).open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != digest:
                raise ValueError("model-integrity")


class InventoryError(ValueError):
    pass


def _verify_legacy(models: Path, inventory, catalog):
    try:
        if not isinstance(inventory, dict):
            raise InventoryError("model-inventory")
        schema = inventory.get("schema")
        if type(schema) is not int or schema not in (1, 2):
            raise InventoryError("model-inventory")
        selected = catalog
        if schema == 2 or "directions" in inventory:
            directions = inventory["directions"]
            if (
                not isinstance(directions, list)
                or not directions
                or any(not isinstance(value, str) for value in directions)
                or len(set(directions)) != len(directions)
                or not set(directions).issubset(direction.id for direction in catalog)
                or (schema == 1 and len(directions) != len(catalog))
            ):
                raise InventoryError("model-inventory")
            selected = tuple(direction for direction in catalog if direction.id in directions)
        entries = inventory["files"]
        if not isinstance(entries, list) or not 2 * len(selected) <= len(entries) <= 20000:
            raise InventoryError("model-inventory")
        seen: set[str] = set()
        for item in entries:
            relative, expected = item["path"], item["sha256"]
            if schema == 2:
                if not isinstance(relative, str) or not isinstance(expected, str):
                    raise InventoryError("model-inventory")
                parts = relative.split("/")
                if (
                    len(parts) < 2
                    or parts[0] not in {direction.id for direction in selected}
                    or any(part in ("", ".", "..") or "\\" in part or ":" in part for part in parts)
                    or not re.fullmatch(r"[0-9a-fA-F]{64}", expected)
                ):
                    raise InventoryError("model-inventory")
            path = (models / relative).resolve()
            if not path.is_relative_to(models.resolve()) or relative in seen:
                raise InventoryError("model-inventory")
            seen.add(relative)
            with path.open("rb") as file:
                actual = hashlib.file_digest(file, "sha256").hexdigest()
            if actual.lower() != str(expected).lower():
                raise InventoryError("model-integrity")
        required = {
            f"{p}/{f}"
            for p in (direction.id for direction in selected)
            for f in ("model/model.bin", "sentencepiece.model")
        }
        if not required.issubset(seen):
            raise InventoryError("model-inventory")
        if schema == 2:
            # Every file the selected native models can read must be inventoried;
            # leftover unselected folders never grant or extend capability.
            actual_files: set[str] = set()
            for direction in selected:
                directory = models / direction.id
                if directory.is_symlink():
                    raise InventoryError("model-inventory")
                for path in directory.rglob("*"):
                    if path.is_symlink():
                        raise InventoryError("model-inventory")
                    if path.is_file():
                        actual_files.add(path.relative_to(models).as_posix())
            if actual_files != seen:
                raise InventoryError("model-inventory")
        # A legacy inventory without directions still requires the full bundle.
        # Native loading must also finish before any hello can advertise it.
        return selected
    except InventoryError:
        raise
    except (OSError, KeyError, ValueError, TypeError):
        raise InventoryError("model-inventory") from None
