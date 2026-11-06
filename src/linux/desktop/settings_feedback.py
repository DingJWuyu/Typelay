"""Render the shared host's commit facts without reconstructing a commit point."""

UNKNOWN_SAVE = "上次设置操作结果未确认，请打开设置检查。"


def saved(result):
    return isinstance(result, dict) and result.get("saved") is True


def notice(result, fallback=UNKNOWN_SAVE):
    if isinstance(result, dict):
        failed_notification = result.get("state") == "CommittedNotificationFailed" or (
            isinstance(result.get("code"), str) and result["code"].endswith("notification-failed")
        )
        if saved(result) and not failed_notification:
            return ""
        if isinstance(result.get("message"), str):
            return result["message"]
    return fallback
