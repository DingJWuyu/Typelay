"""Prepare and verify TypeLay's pinned, offline UI font bundle."""

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from eng.fonts.bundle import prepare_bundle, verify_bundle  # noqa: E402


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
