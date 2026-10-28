"""Platform-neutral fixed build-input verification for the Windows Index adapter."""

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
from eng.linux.index.prepare_assets import verify_build_inputs  # noqa: E402

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(verify_build_inputs(args.output)))
