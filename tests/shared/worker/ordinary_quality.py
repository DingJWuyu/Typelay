"""Diagnose style conditioning using all eight frozen Hy sources without style.

Uses the installed production greedy adapter and ordinary translate operation.
The ordinary instruction also omits the styled fidelity clause, so this cannot
isolate the style name alone. Compare only with a styled capture with matching source, model inventory and
native adapter identities. This is not a quality verdict or release gate.
No model download, prompt edit, fallback, retry, or production behavior change.
"""

from __future__ import annotations

import sys

import styled_quality


def prepare_report():
    report = styled_quality.prepare_report(include_holdout=True)
    report.update(
        maximumTranslationRequests=8,
        method="production-ordinary-translate-style-ablation",
        hypothesis="styled-prompt-path-causes-meaning-drift",
        comparisonRequirement="same-inputs-worker-inventory-and-native-adapter-as-greedy-styled",
    )
    # Keep the same style-capable worker hello admission as the paired experiment.
    for case in report["cases"]:
        review = case["manualReview"]["natural"]
        del case["natural"], case["formal"], case["pairReview"]
        case["ordinary"] = None
        case["manualReview"] = {"ordinary": review}
    return report


async def collect_cases(process, report):
    for case in report["cases"]:
        report["stage"] = case["id"] + ":ordinary"
        await styled_quality.request(process, report, case, "ordinary", ordinary=True)


async def collect(args, report):
    await styled_quality.collect(args, report, collect_cases=collect_cases)


if __name__ == "__main__":
    if "--decode-comparison" in sys.argv:
        import hy_decode_comparison

        sys.argv.remove("--decode-comparison")
        raise SystemExit(hy_decode_comparison.ordinary_main())
    raise SystemExit(styled_quality.main(prepare_report, collect, __doc__))
