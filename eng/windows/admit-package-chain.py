"""Fail before preparation unless the Windows package chain fits its job budget."""

import argparse
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--elapsed-seconds", required=True, type=int)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    # Preparation, browser, ordinary Notepad, Hy production, Settings,
    # cache/artifact tail, and packaging. Preserve this 60-minute chain;
    # allow 20 minutes for the preceding build and contracts in an 80-minute job.
    required = (10 + 10 + 10 + 6 + 16 + 7 + 1) * 60
    remaining = 80 * 60 - args.elapsed_seconds
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
        raise SystemExit("Insufficient or invalid 80-minute Windows package-chain budget")


if __name__ == "__main__":
    main()
