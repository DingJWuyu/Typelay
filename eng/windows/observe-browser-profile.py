"""Observe fixed Chrome profile metadata without reading or changing user data."""

import argparse
import ctypes
import json
import os
import stat
from datetime import datetime, timezone
from pathlib import Path


def observe(root: Path, stage: str) -> dict:
    result = {"stage": stage, "observedAtUtc": datetime.now(timezone.utc).isoformat()}
    try:
        value = root.lstat()
    except FileNotFoundError:
        return result | {"exists": False}
    except OSError as error:
        return result | {
            "exists": None,
            "error": type(error).__name__,
            "winerror": getattr(error, "winerror", None),
        }
    reparse = bool(getattr(value, "st_file_attributes", 0) & 0x400) or stat.S_ISLNK(value.st_mode)
    result |= {
        "exists": True,
        "directory": stat.S_ISDIR(value.st_mode),
        "reparsePoint": reparse,
        "createdAtUtc": datetime.fromtimestamp(value.st_birthtime, timezone.utc).isoformat()
        if hasattr(value, "st_birthtime")
        else None,
        "modifiedAtUtc": datetime.fromtimestamp(value.st_mtime, timezone.utc).isoformat(),
    }
    if not reparse and stat.S_ISDIR(value.st_mode):
        # Fixed metadata only: never enumerate names or open browser files.
        for label, name in (
            ("defaultProfileExists", "Default"),
            ("localStateExists", "Local State"),
            ("crashpadExists", "Crashpad"),
            ("fixtureMarkerExists", "typelay-browser-fixture-owner"),
        ):
            try:
                (root / name).lstat()
                result[label] = True
            except FileNotFoundError:
                result[label] = False
            except OSError:
                result[label] = None
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    if os.name != "nt":
        parser.error("the default profile observation requires Windows")
    buffer = ctypes.create_unicode_buffer(32768)
    # The same current-user LocalApplicationData known folder used by the App.
    result = ctypes.windll.shell32.SHGetFolderPathW(None, 0x1C, None, 0, buffer)
    if result:
        raise OSError("LocalApplicationData lookup failed")
    observation = observe(Path(buffer.value) / "Google" / "Chrome" / "User Data", arguments.stage)
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(json.dumps(observation, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(observation))


if __name__ == "__main__":
    main()
