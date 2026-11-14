"""Project save facts in existing behavior assertions; report-contract tests read the full result."""

SAVE_UNCONFIRMED_MESSAGE = "设置保存未能确认。请检查设置目录权限或磁盘空间，再重新选择需要的设置。"


def saved(result):
    return result["saved"] if isinstance(result, dict) else result


def practice_allowed(result):
    return (
        result.get("started", result.get("completed", False))
        if isinstance(result, dict)
        else result
    )
