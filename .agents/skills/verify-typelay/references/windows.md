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
  --name "windows-desktop-ordinary-<sha>-$run_id-<attempt>" \
  --dir "artifacts/verification/windows-$run_id"
```

自动工作流由 push 触发。分别记录 PR head、运行的 `headSha`、源码 artifact 的 `tested_commit` 与紧邻 base，并确认被测 head 已包含当前 base。不把旧 head、其它分支或整体绿色状态当作当前 Windows 证据。检查 `.github/workflows/check.yml` 中实际 Windows job 的步骤、跳过状态、TRX、截图和日志。`Index / windows` 覆盖同包生命周期与默认 Index 浏览器，结果在 `typelay-index-windows-<sha>-<run_id>-<attempt>`。共享 Windows.Contracts 不证明 WPF、UIA、Hook、物理输入法或桌面视觉。

先核对 artifact 名称、大小和当前网络授权，再按精确名称下载所需原件。逐项读取 continuous 的七条旅程及其依赖结果，并将 Notepad Direct/Undo、自动接受、编辑回源和普通退出分开报告。公开能力在发键前拒绝或缺少可信输入时，接受属于 `not-run`；具体原因仍须保留。输入次数、期限及清理判据见 [Windows 测试](../../../../docs/platforms/windows/testing.md)。

Notepad 的 `requiredOutcome=passed` 只表示自动化必过范围通过。即使 CLI 返回 0，仍须报告整套 `outcome=not-run` 及两条可信输入分支的未验范围。只有精确的物理输入缺口允许这项区分，其它失败或必要前置不足继续阻塞必过结果。

对应提交没有运行、job 被跳过或 artifact 缺失时，报告“Windows 未验证”并写明原因。用户没有授权时，不为取证触发额外工作流，不更新 `main` 或 `trunk`。

核对默认 Index 浏览器时，读取 `Index / windows` job 的 `index-production/result.json` 和 `browser-cooperation/result.json`。两个现役入口依次消费该 job 一次构建的发行包。核对两份结果的 `testedCommit`、`runId`、`runAttempt` 和 `packageSha256` 相同，再逐项比较共同消费的程序集、worker 和 Index 载荷摘要及固定 profile。生命周期逐项要求通过，并核对 `allOwnedWorkersExited=true` 与 `cleanup=[]`。两个入口各自保留本次准备的安装代和退出证明，同包不等于同一安装代。

浏览器自身必须满足 `indexPreparation.workerExitConfirmed=true`、`appUsedSameGeneration=true`，且所有场景通过。结合 `index-installed.json`、`index-components.json` 核对正式安装代。检查 `draftObservation` 的第一次回源零写、按键释放和独立接受，要求最终字段精确等于人工稿且仅一次替换。打开四张 `draft-*.png` 检查真实浮层，并逐句核对合成输入与实际译文。自动断言非空不等于基本保义。最后核对三个进程退出标记、`cleanupErrors=[]` 与同次 `consumer-exit.json`，不能用独立模型生命周期替代桌面或清理结果。
