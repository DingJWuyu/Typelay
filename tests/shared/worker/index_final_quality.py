"""One independent, pre-output frozen 16-source Index final capture.

The coordinating parent authored these sources before reading the development
capture. This entry does not tune prompts or expand the existing resource budget.
Protected-source alternative refusals are recorded, never counted as translations.
"""

from pathlib import Path

import index_quality
import styled_quality

INPUTS = Path(__file__).with_name("index_quality_holdout.json")
INPUT_SHA256 = "80f511ff73069405ccee8f0b33b24ccd00f083ac17544a0e214e09e0198123d8"


def prepare_report():
    report = index_quality.prepare_fixed_inputs(
        INPUTS, INPUT_SHA256, 16, include_refusal_checks=False
    )
    report["method"] = "production-index-independent-six-operations-fixed-zh-en-capture"
    report["reviewScope"] = (
        "Sixteen independently authored frozen sources; no inferred acceptance for other "
        "directions, unsupported alternative inputs, or unexecuted cases."
    )
    return report


if __name__ == "__main__":
    raise SystemExit(
        styled_quality.main(prepare_report, index_quality.collect, __doc__, native="index")
    )
