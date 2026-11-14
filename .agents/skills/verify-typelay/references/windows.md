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
gh api "repos/DingJWuyu/Typelay/actions/runs/$run_id/artifacts" \
  --jq '.artifacts[] | {name, size_in_bytes, expired}'
gh run download "$run_id" --repo DingJWuyu/Typelay \
  --name "typelay-windows-desktop-diagnostics-<sha>-$run_id-<attempt>" \
  --dir "artifacts/verification/windows-$run_id"
```

PR 工作流可能运行合成 merge commit。分别记录 PR head、运行的 `headSha` 与 base，不把旧 head、其它分支或整体绿色状态当作当前 Windows 证据。检查 `.github/workflows/check.yml` 中实际 Windows job 的步骤、跳过状态、TRX、截图和日志。`index-windows` 只证明它实际执行的模型生命周期；共享 Windows.Contracts 不证明 WPF、UIA、Hook、物理输入法或桌面视觉。

先核对 artifact 名称、大小和当前网络授权，再按精确名称下载所需原件。逐项读取 continuous 的七条旅程及其依赖结果，并将 Notepad Direct/Undo、自动接受、编辑回源和普通退出分开报告。公开能力在发键前拒绝或缺少可信输入时，接受属于 `not-run`；具体原因仍须保留。输入次数、期限及清理判据见 [Windows 测试](../../../../docs/platforms/windows/testing.md)。

Notepad 的 `requiredOutcome=passed` 只表示自动化必过范围通过。即使 CLI 返回 0，仍须报告整套 `outcome=not-run` 及两条可信输入分支的未验范围。只有精确的物理输入缺口允许这项区分，其它失败或必要前置不足继续阻塞必过结果。

对应提交没有运行、job 被跳过或 artifact 缺失时，报告“Windows 未验证”并写明原因。用户没有授权时，不为取证触发额外工作流，不更新 `main` 或 `trunk`。
