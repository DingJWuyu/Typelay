"""Fail before preparation unless the Windows package chain fits its job budget."""

import argparse
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--elapsed-seconds", required=True, type=int)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    # Preparation, Index lifecycle, browser, ordinary Notepad, Hy production,
    # Settings, cache/artifact tail, and packaging retain their own deadlines.
    # Add the 11-minute lifecycle slot and preserve the prior 25-minute prefix.
    required = (10 + 11 + 10 + 10 + 6 + 16 + 7 + 1) * 60
    remaining = 96 * 60 - args.elapsed_seconds
    admitted = args.elapsed_seconds >= 0 and remaining >= required
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(
        json.dumps(
            dict(
                admitted=admitted,
                elapsedSeconds=args.elapsed_seconds,
                remainingSeconds=remaining,
                requiredSeconds=required,
            ),
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    if not admitted:
        raise SystemExit("Insufficient or invalid 96-minute Windows package-chain budget")


if __name__ == "__main__":
    main()
