"""Render a Check summary from this job's fixed artifact producer records."""

import json
import os

QUERY_STEPS = {"fonts": "restore", "hy": "hy-cache", "index": "index-cache", "fcitx": "restore"}
PUBLICATIONS = {"published": "本轮已发布", "reused": "已验证其他生产者发布的产物"}


def summary_row(kind, steps, records):
    def outcome(step):
        return steps.get(step, {}).get("outcome", "未知")

    prefix = f"FIXED_{kind.replace('-', '_')}_"
    tag = records.get(prefix + "tag")
    query = records.get(prefix + "query")
    publication = records.get(prefix + "publication")
    verification = "不适用"
    if kind == "linux-tools":
        action = outcome("linux-tools")
        missing = "skipped" if action == "skipped" else f"未知（合成步骤 {action}）"
        outputs = steps.get("linux-tools", {}).get("outputs", {})
        lookup = outputs.get("restore") or missing
        sdk = outputs.get("sdk") or missing
        desktop = outputs.get("desktop") or missing
        build = f"SDK {sdk}，桌面 {desktop}"
        prepared = sdk == desktop == "success"
        publish = outputs.get("publish") or missing
    else:
        lookup = outcome(QUERY_STEPS[kind])
        build = outcome(f"{kind}-build")
        prepared = build == "success"
        publish = outcome(f"{kind}-publish")
        if kind == "fonts":
            verification = outcome("fonts-verify")
            prepared = prepared and verification == "success"
    if tag and query == "hit":
        state = "命中已发布"
    elif tag and query == "miss" and prepared and publication in PUBLICATIONS:
        state = "本轮冷建"
    else:
        state = "未知"
    lookup = {"hit": "命中", "miss": "未命中", "restoring": f"已找到产物，恢复 {lookup}"}.get(
        query, lookup
    )
    publication_result = PUBLICATIONS.get(publication, "尚无确认结果")
    cleanup = records.get(prefix + "cleanup", "无记录")
    tag = f"`{tag}`" if tag else "未知"
    return (
        f"| `{kind}` | {tag} | {state} | {lookup} | {build} | {verification} | "
        f"{publish} | {publication_result} | {cleanup} |"
    )


def main():
    steps = json.loads(os.environ["FIXED_STEPS"])
    rows = [
        f"### 固定产物（`{os.environ['GITHUB_JOB']}`，{os.environ['PRODUCER_STATUS']}）",
        "",
        "| 产物 | 完整 tag | 本轮状态 | 查询或恢复 | 准备或构建 | 字体验证 | 发布步骤 | 发布结果 | 草稿清理 |",
        "| --- | --- | --- | --- | --- | --- | --- | --- | --- |",
    ]
    rows.extend(
        summary_row(kind, steps, os.environ) for kind in os.environ["TYPELAY_FIXED_KINDS"].split()
    )
    summary = "\n".join(rows)
    print(summary)
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
        print(summary, file=stream)


if __name__ == "__main__":
    main()
