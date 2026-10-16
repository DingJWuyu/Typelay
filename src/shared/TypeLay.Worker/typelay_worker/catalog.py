"""The bundled model directions shared by the worker and both installers."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit


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


def load_catalog(path: Path | None = None) -> tuple[ModelDirection, ...]:
    """Read only the package's catalog; never infer directions from model folders."""
    if path is None:
        path = Path(__file__).resolve().parent.parent / "model-catalog.json"
    try:
        with path.open("rb") as stream:
            encoded = stream.read(16385)
        if len(encoded) > 16384:
            raise ValueError("model-catalog")
        value = json.loads(encoded)
        if type(value["schema"]) is not int or value["schema"] != 1:
            raise ValueError("model-catalog")
        entries = value["directions"]
        if not isinstance(entries, list) or not entries:
            raise ValueError("model-catalog")
        directions = []
        seen = set()
        for entry in entries:
            keys = (
                "id",
                "source",
                "target",
                "sourceName",
                "targetName",
                "version",
                "url",
                "selfCheckText",
            )
            if any(not isinstance(entry[key], str) or not entry[key] for key in keys):
                raise ValueError("model-catalog")
            direction = ModelDirection(*(entry[key] for key in keys))
            url = urlsplit(direction.url)
            if (
                not re.fullmatch(r"[a-z]{2}", direction.source)
                or not re.fullmatch(r"[a-z]{2}", direction.target)
                or direction.source == direction.target
                or direction.id != direction.source + "_" + direction.target
                or direction.id in seen
                or not re.fullmatch(r"[0-9]+\.[0-9]+", direction.version)
                or url.scheme != "https"
                or url.hostname != "argos-net.com"
                or url.username is not None
                or url.password is not None
                or url.port not in (None, 443)
                or url.query
                or url.fragment
            ):
                raise ValueError("model-catalog")
            seen.add(direction.id)
            directions.append(direction)
        return tuple(directions)
    except (OSError, ValueError, KeyError, TypeError):
        raise ValueError("model-catalog") from None
