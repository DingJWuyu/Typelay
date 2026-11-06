"""Project save facts in existing behavior assertions; report-contract tests read the full result."""


def saved(result):
    return result["saved"] if isinstance(result, dict) else result


def practice_allowed(result):
    return (
        result.get("started", result.get("completed", False))
        if isinstance(result, dict)
        else result
    )
