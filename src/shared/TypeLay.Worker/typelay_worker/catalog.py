"""Package metadata and installation intent, never verified runtime capability."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit

_LEGACY_PACKAGES = (("argos-zh-en", "zh_en"), ("argos-en-zh", "en_zh"))


@dataclass(frozen=True)
class ArchivePin:
    sha256: str
    bytes: int
    unpacked_bytes: int


@dataclass(frozen=True)
class ModelDirection:
    id: str
    source: str
    target: str
    source_name: str
    target_name: str
    version: str
    url: str
    self_check_text: str
    archive_pin: ArchivePin | None = None


@dataclass(frozen=True)
class ModelPackage:
    id: str
    version: str
    backend: str
    preview: bool
    url: str
    directions: tuple[ModelDirection, ...]


@dataclass(frozen=True)
class ModelFile:
    path: str
    sha256: str


@dataclass(frozen=True)
class ModelPackageSelection:
    package: ModelPackage
    directions: tuple[ModelDirection, ...]
    files: tuple[ModelFile, ...] = ()


@dataclass(frozen=True)
class ModelPackageCatalog:
    packages: tuple[ModelPackage, ...]

    @property
    def legacy_directions(self) -> tuple[ModelDirection, ...]:
        """The original Argos bundle, regardless of additional package metadata."""
        directions = []
        for package_id, direction_id in _LEGACY_PACKAGES:
            package = next((item for item in self.packages if item.id == package_id), None)
            if package is None or package.backend != "argos" or package.version != "1.9":
                raise ValueError("model-catalog")
            direction = next((item for item in package.directions if item.id == direction_id), None)
            if direction is None:
                raise ValueError("model-catalog")
            directions.append(direction)
        return tuple(directions)

    @property
    def default_directions(self) -> tuple[ModelDirection, ...]:
        return self.legacy_directions


def _text(value, key):
    result = value[key]
    if not isinstance(result, str) or not result.strip():
        raise ValueError("model-catalog")
    return result


def _unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("model-catalog")
        value[key] = item
    return value


def load_package_catalog(path: Path | None = None) -> ModelPackageCatalog:
    """Read catalog schema 2. Its schema is independent of installed.json's schema."""
    if path is None:
        path = Path(__file__).resolve().parent.parent / "model-catalog.json"
    try:
        with path.open("rb") as stream:
            encoded = stream.read(16385)
        if len(encoded) > 16384:
            raise ValueError("model-catalog")
        value = json.loads(encoded, object_pairs_hook=_unique_object)
        if (
            not isinstance(value, dict)
            or type(value["schema"]) is not int
            or value["schema"] != 2
            or "directions" in value
        ):
            raise ValueError("model-catalog")
        entries = value["packages"]
        if not isinstance(entries, list) or not entries:
            raise ValueError("model-catalog")
        packages = []
        seen_packages = set()
        for entry in entries:
            package_id, version, backend, address = (
                _text(entry, key) for key in ("id", "version", "backend", "url")
            )
            url = urlsplit(address)
            if (
                not re.fullmatch(r"[a-z][a-z0-9-]{0,63}", package_id)
                or package_id in seen_packages
                or backend not in ("argos", "hy-mt-llama-cpu")
                or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", version)
                or type(entry["preview"]) is not bool
                or url.scheme != "https"
                or not url.hostname
                or url.username is not None
                or url.password is not None
                or url.port not in (None, 443)
                or url.query
                or url.fragment
                or backend == "argos"
                and (
                    url.hostname != "argos-net.com" or not re.fullmatch(r"[0-9]+\.[0-9]+", version)
                )
                or not isinstance(entry["directions"], list)
                or not entry["directions"]
            ):
                raise ValueError("model-catalog")
            pin = None
            if "archivePin" in entry:
                raw = entry["archivePin"]
                if (
                    backend != "argos"
                    or len(entry["directions"]) != 1
                    or not isinstance(raw, dict)
                    or set(raw) != {"sha256", "bytes", "unpackedBytes"}
                    or not isinstance(raw["sha256"], str)
                    or not re.fullmatch(r"[0-9a-f]{64}", raw["sha256"])
                    or type(raw["bytes"]) is not int
                    or not 0 < raw["bytes"] <= 1024**3
                    or type(raw["unpackedBytes"]) is not int
                    or not 0 < raw["unpackedBytes"] <= 2 * 1024**3
                ):
                    raise ValueError("model-catalog")
                pin = ArchivePin(raw["sha256"], raw["bytes"], raw["unpackedBytes"])
            directions = []
            seen_directions = set()
            for item in entry["directions"]:
                identity, source, target, source_name, target_name, sample = (
                    _text(item, key)
                    for key in (
                        "id",
                        "source",
                        "target",
                        "sourceName",
                        "targetName",
                        "selfCheckText",
                    )
                )
                if (
                    not re.fullmatch(r"[a-z]{2}", source)
                    or not re.fullmatch(r"[a-z]{2}", target)
                    or source == target
                    or identity != source + "_" + target
                    or identity in seen_directions
                    or "version" in item
                    or "url" in item
                    or "archivePin" in item
                ):
                    raise ValueError("model-catalog")
                seen_directions.add(identity)
                directions.append(
                    ModelDirection(
                        identity,
                        source,
                        target,
                        source_name,
                        target_name,
                        version,
                        address,
                        sample,
                        pin,
                    )
                )
            seen_packages.add(package_id)
            packages.append(
                ModelPackage(
                    package_id, version, backend, entry["preview"], address, tuple(directions)
                )
            )
        return ModelPackageCatalog(tuple(packages))
    except (OSError, ValueError, KeyError, TypeError):
        raise ValueError("model-catalog") from None


def load_catalog(path: Path | None = None) -> tuple[ModelDirection, ...]:
    """Compatibility projection for current installers, settings and the Argos worker."""
    return load_package_catalog(path).legacy_directions


def _direction_ids(value):
    if (
        not isinstance(value, list)
        or not value
        or any(not isinstance(item, str) for item in value)
        or len(set(value)) != len(value)
    ):
        raise ValueError("model-inventory")
    return value


def read_installation_selection(
    inventory: object, catalog: ModelPackageCatalog | None = None
) -> tuple[ModelPackageSelection, ...]:
    """Normalize selection intent only; never verify installation or authorize Ready."""
    if catalog is None:
        catalog = load_package_catalog()
    try:
        if not isinstance(inventory, dict) or type(inventory["schema"]) is not int:
            raise ValueError("model-inventory")
        schema = inventory["schema"]
        if schema in (1, 2):
            if "packages" in inventory or "activePackages" in inventory:
                raise ValueError("model-inventory")
            legacy = catalog.legacy_directions
            if schema == 1 and "directions" not in inventory:
                ids = [direction.id for direction in legacy]
            else:
                ids = _direction_ids(inventory["directions"])
                if not set(ids).issubset(direction.id for direction in legacy) or (
                    schema == 1 and len(ids) != len(legacy)
                ):
                    raise ValueError("model-inventory")
            return tuple(
                ModelPackageSelection(
                    next(package for package in catalog.packages if package.id == package_id),
                    tuple(direction for direction in legacy if direction.id == direction_id),
                )
                for package_id, direction_id in _LEGACY_PACKAGES
                if direction_id in ids
            )
        if schema != 3 or "directions" in inventory or "files" in inventory:
            raise ValueError("model-inventory")
        entries = inventory["packages"]
        if not isinstance(entries, list) or not entries:
            raise ValueError("model-inventory")
        selected = {}
        for entry in entries:
            identity, version = _text(entry, "id"), _text(entry, "version")
            package = next(
                (package for package in catalog.packages if package.id == identity), None
            )
            ids = _direction_ids(entry["directions"])
            if (
                package is None
                or version != package.version
                or identity in selected
                or not set(ids).issubset(direction.id for direction in package.directions)
            ):
                raise ValueError("model-inventory")
            selected[identity] = (ids, read_file_manifest(entry["files"]))
        return tuple(
            ModelPackageSelection(
                package,
                tuple(
                    direction
                    for direction in package.directions
                    if direction.id in selected[package.id][0]
                ),
                selected[package.id][1],
            )
            for package in catalog.packages
            if package.id in selected
        )
    except (ValueError, KeyError, TypeError):
        raise ValueError("model-inventory") from None


def read_file_manifest(value: object) -> tuple[ModelFile, ...]:
    """Lexical inventory validation only, including inactive package entries."""
    if not isinstance(value, list) or not 1 <= len(value) <= 20000:
        raise ValueError("model-inventory")
    files, seen = [], set()
    for item in value:
        if not isinstance(item, dict):
            raise ValueError("model-inventory")
        relative, digest = item.get("path"), item.get("sha256")
        if not isinstance(relative, str) or not isinstance(digest, str):
            raise ValueError("model-inventory")
        parts = relative.split("/")
        if (
            any(
                not part
                or part in (".", "..")
                or part[-1:] in (" ", ".")
                or any(character in '\\:\x00<>"|?*' or ord(character) < 32 for character in part)
                or re.fullmatch(r"(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])", part.split(".")[0])
                for part in parts
            )
            or relative.casefold() in seen
            or not re.fullmatch(r"[a-fA-F0-9]{64}", digest)
        ):
            raise ValueError("model-inventory")
        seen.add(relative.casefold())
        files.append(ModelFile(relative, digest.lower()))
    return tuple(files)


def read_active_selection(
    inventory: object, catalog: ModelPackageCatalog | None = None
) -> tuple[ModelPackageSelection, ...]:
    """Select one supported active family. This never grants loaded capability."""
    selected = read_installation_selection(inventory, catalog)
    if inventory["schema"] in (1, 2):
        return selected
    try:
        active = _direction_ids(inventory["activePackages"])
        by_id = {item.package.id: item for item in selected}
        if any(identity not in by_id for identity in active):
            raise ValueError("model-inventory")
        result = tuple(item for item in selected if item.package.id in active)
        argos = all(
            item.package.id in {identity for identity, _ in _LEGACY_PACKAGES}
            and item.package.backend == "argos"
            and item.package.version == "1.9"
            for item in result
        )
        hy = len(result) == 1 and result[0].package.backend == "hy-mt-llama-cpu"
        directions = [direction.id for item in result for direction in item.directions]
        if not (argos or hy) or len(set(directions)) != len(directions):
            raise ValueError("model-inventory")
        return result
    except (KeyError, TypeError, ValueError):
        raise ValueError("model-inventory") from None
