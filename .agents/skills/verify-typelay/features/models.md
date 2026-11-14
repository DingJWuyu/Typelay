# 模型准备与恢复

用户明确下载本地模型，看到准备进度，完成后获得可用翻译。切换或准备失败时，应用保留已有可用状态并显示实际错误。

## Sub-features

- `models/default-cli` 从空数据目录安装默认 Index，观察真实握手和 Ready。
- `models/index-lifecycle` 从正式包执行连续推理、冷启、失败与取消保旧代、Repair、切换、移除和进程退出。
- `models/ui` 在首次配置或本地模型页确认、取消、重试准备。
- `models/switch` 切换库存中的模型，检查实际 current 与就绪状态。
- `models/recovery` 准备失败或取消时保留旧代，移除操作只清理允许删除的包。

## How to get to it (user POV)

- 首次配置进入“下载本地翻译模型”。
- 从设置的“本地模型”页选择目录中的模型及可用操作。
- 终端运行 `typelay --install-models`，或明确选择 `typelay --install-models argos`。
- 托盘的模型状态通向相应准备或恢复入口。

## Driving it with private niri

Preconditions: 本次任务允许准备模型，固定模型缓存已按 build.md 生成。安装目录为空且位于最终使用路径。

- **默认 CLI。** Launch 的 GTK 旅程执行 `"$TYPELAY_VERIFY_PACKAGE/typelay" --install-models`。观察真实双向自检和服务握手成功、`doctor-ready.json` 的 `modelReady=true`，并保存 `installed.json`。正式安装器自行生成激活记录。
- **生命周期回归。** 在私有旅程文件内执行 `/usr/bin/python3 tests/linux/index_package_e2e.py --package "$TYPELAY_VERIFY_PACKAGE" --cache "$PWD/artifacts/index-model-download" --output "$TYPELAY_VERIFY_OUTPUT/index-lifecycle"`。缓存可替换为已验证下载缓存的绝对路径，输出目录须不存在。逐项读取 `result.json` 的阶段状态，核对正式安装、激活、连续推理和结构、冷启、失败与取消保旧代、Repair、安装 Argos、切回 Index、再切回 Argos 和移除 Index。要求 `cleanup=[]`、`allOwnedSupervisorsExited=true`，再核对外层 `cleanup.json` 的 `remainingPids=[]`。不能用整体退出码抹去 not-run。
- **失败定位。** `failureContext.modelPhase` 区分 `host-start`、`activation`、`generation` 与 `ready`；`activationOutcome` 和 `modelFailureCode` 只保留固定枚举。冷启、失败修复与取消候选后的代身份分别保留固定失败原因，Ready 超时记录 `model-ready-timeout`。`protocolPhase` 与退出码记录清理前观察。安装失败另读 `preparationFailures`，其内容限于正式安装器已验证的阶段诊断，不把 stdout、异常原文或模型正文塞入结果。
- **模型页。** 启动 `"$TYPELAY_VERIFY_PACKAGE/typelay" --settings`，通过 `tests/linux/support/gtk_accessibility.py` 的 `select_settings_page(pid, "本地模型")` 进入实际页面。对当次控件树中的下载或切换动作调用公开 AT-SPI Action。保留确认对话框、取消后库存不变、明确确认后进度与库存变化的证据。
- **完整模型 UI 回归。** 既有 `tests/linux/native_e2e.py --inside --desktop --desktop-scope model-packages` 需要单独准备 Hy/Argos 库存和缓存。按 `tests/linux/hy_package_e2e.py` 的参数与仓库模型事务要求准备后，仍在本技能私有会话中调用，并提供 `--package "$TYPELAY_VERIFY_PACKAGE" --native "$PWD/artifacts/linux/native" --output "$TYPELAY_VERIFY_OUTPUT/model-packages"`。这不是默认 Index 旅程的附带通过项。

## Gotchas

- **正常文件系统是验收路径。** Index 从文件描述符建立临时私有映射，按内核映射中的设备与 inode 核对已加载库，保留准确路径和完整摘要校验。Btrfs 子卷的 `stat` 设备号可能不同于映射设备号，不能据此放弃身份验证。
- **tmpfs 仅作诊断。** 在外层 `private-session.py` 显式加 `--package-filesystem tmpfs`，脚本才把完整原样主包放到自有可执行 tmpfs。记录本次选项与文件系统。对照成功不满足正常路径验收。`/dev/shm` 可能带 `noexec`，不要硬编码运行路径；空间不足时保留证据并停止。
- 私有 Python 在最终模型数据目录安装。搬迁已有 `components` 或 venv 会使解释器前缀失效。
- uv 0.12.13 找不到固定 CPython 的问题已在 build.md 记录。空控件树、模型未就绪和原生字段未接入是三种不同失败。
- 默认缓存只有 Index，不满足 Hy/Argos 全量 UI 旅程。缺少这些权重时，该入口应报告前提失败，不能以默认 CLI 通过代替。
