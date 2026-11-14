"""Synthetic installer peer for lifecycle diagnostics; no model is installed."""

import json
import sys
from pathlib import Path

package, data = map(Path, sys.argv[1:3])
scenario = json.loads((package / "scenario.json").read_text())
request = json.loads(sys.argv[4]) if len(sys.argv) > 4 else None
if request and request["expectedCurrent"] == "0" * 32:
    if scenario["failureStage"] == "failed-repair-retains-current":
        current = data / "components/current"
        current.unlink()
        current.symlink_to("private-generation")
    raise SystemExit(7)
print("TYPELAY_MODEL_PREPARED=" + ("b" if request else "a") * 32)
