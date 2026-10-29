"""Small integrity-only font payloads; real font bytes are tested by bundle relocation tests."""

import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


def inert_font_payload():
    """Keep the full lock schema/roles, but never imply these inert bytes can render."""
    lock = json.loads((ROOT / "eng/fonts/fonts.lock.json").read_text(encoding="utf-8"))
    sources, payload = {}, {}
    for item in lock["files"]:
        if Path(item["path"]).suffix in (".ttf", ".otf"):
            content = ("Inert integrity-fixture font: " + item["path"] + "\n").encode()
        else:
            # Preserve upstream legal text rather than fabricating a replacement license.
            content = (ROOT / item["source"]["repositoryPath"]).read_bytes()
        identity = {"bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}
        relative = item["source"].get("repositoryPath", "src/shared/ui/fonts/" + item["path"])
        item.update(identity)
        item["source"] = {"repositoryPath": relative, **identity}
        item.pop("instance", None)
        sources[relative] = content
        payload["fonts/" + item["path"]] = content
    encoded = (json.dumps(lock, ensure_ascii=False, indent=2) + "\n").encode()
    sources["eng/fonts/fonts.lock.json"] = encoded
    payload["source/eng/fonts/fonts.lock.json"] = encoded
    manifest = {
        "schemaVersion": 1,
        "files": [
            {key: value for key, value in item.items() if key != "source"} for item in lock["files"]
        ],
    }
    payload["fonts/manifest.json"] = (
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    ).encode()
    return sources, payload
