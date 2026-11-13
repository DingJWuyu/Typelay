# 从 GitHub Actions 读取 Windows 证据

这台 Linux 机器不能驱动 Windows。先按 `docs/agents/issue-tracker.md` 确认仓库为 `DingJWuyu/Typelay`，再查询对应提交的 Actions。优先使用机器提供的 `gh-axi`；标准 `gh` 等效命令如下。

```bash
gh run list --repo DingJWuyu/Typelay --commit "$(git rev-parse HEAD)" \
  --json databaseId,headSha,status,conclusion,url
```

选择实际 `headSha` 匹配的运行，以其返回的数字 ID 设置 `run_id`。然后读取 job 并下载 Windows 原件：

```bash
gh run view "$run_id" --repo DingJWuyu/Typelay \
  --json headSha,status,conclusion,jobs,url
gh run download "$run_id" --repo DingJWuyu/Typelay \
  --dir "artifacts/verification/windows-$run_id"
```

PR 工作流可能运行合成 merge commit。分别记录 PR head、运行的 `headSha` 与 base，不把旧 head、其它分支或整体绿色状态当作当前 Windows 证据。检查 `.github/workflows/check.yml` 中实际 Windows job 的步骤、跳过状态、TRX、截图和日志。`index-windows` 只证明它实际执行的模型生命周期；共享 Windows.Contracts 不证明 WPF、UIA、Hook、物理输入法或桌面视觉。

对应提交没有运行、job 被跳过或 artifact 缺失时，报告“Windows 未验证”并写明原因。用户没有授权时，不为取证触发额外工作流，不更新 `main` 或 `trunk`。
