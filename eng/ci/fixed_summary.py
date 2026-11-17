"""Render a Check summary from this job's fixed artifact producer records."""

import json
import os

QUERY_STEPS = {"fonts": "restore", "hy": "hy-cache", "index": "index-cache", "fcitx": "restore"}
PUBLICATIONS = {"published": "本轮已发布", "reused": "已验证其他生产者发布的产物"}


def report(kind, steps):
    def outcome(step):
        return steps.get(step, {}).get("outcome", "未知")

    prefix = f"FIXED_{kind.replace('-', '_')}_"
    tag = os.environ.get(prefix + "tag")
    query = os.environ.get(prefix + "query")
    publication = os.environ.get(prefix + "publication")
    if kind == "linux-tools":
        # The composite invokes publish only after both package installs succeed.
        action = outcome("linux-tools")
        incomplete = f"结果未知（合成步骤 {action}）"
        lookup = incomplete if query in ("started", "restoring") else "未知"
        if publication:
            build, publish = "success", incomplete
        elif query == "miss":
            build, publish = f"完成未知（合成步骤 {action}）", "未见发布记录"
        elif query == "hit":
            build = publish = "skipped"
        else:
            build = publish = "未知"
    else:
        lookup = outcome(QUERY_STEPS[kind])
        build = outcome(f"{kind}-build")
        publish = outcome(f"{kind}-publish")
    if query == "hit":
        state = "命中已发布"
    elif query == "miss" and build == "success" and publication in PUBLICATIONS:
        state = "本轮冷建"
    else:
        state = "未知"
    lookup = {"hit": "命中", "miss": "未命中", "restoring": f"已找到产物，恢复 {lookup}"}.get(
        query, lookup
    )
    publish = PUBLICATIONS.get(publication, publish)
    tag = f"`{tag}`" if tag else "未知"
    print(f"| `{kind}` | {tag} | {state} | {lookup} | {build} | {publish} |")


def main():
    job = os.environ["GITHUB_JOB"]
    steps = json.loads(os.environ["FIXED_STEPS"])
    print(f"### 固定产物（`{job}`，{os.environ['PRODUCER_STATUS']}）\n")
    print("| 产物 | 完整 tag | 本轮状态 | 查询或恢复 | 准备或构建 | 发布 |")
    print("| --- | --- | --- | --- | --- | --- |")
    for kind in ("fonts",) if job == "fonts" else ("linux-tools", "hy", "index", "fcitx"):
        report(kind, steps)


if __name__ == "__main__":
    main()
