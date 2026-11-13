# 模型准备与恢复

用户明确下载本地模型，看到准备进度，完成后获得可用翻译。切换或准备失败时，应用保留已有可用状态并显示实际错误。

## Sub-features

- `models/default-cli` 从空数据目录安装默认 Index，观察真实握手和 Ready。
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
- **生命周期回归。** 在私有旅程文件内执行 `/usr/bin/python3 tests/linux/index_package_e2e.py --package "$TYPELAY_VERIFY_PACKAGE" --cache "$PWD/artifacts/index-model-download" --output "$TYPELAY_VERIFY_OUTPUT/index-lifecycle"`。输出目录须不存在。逐项读取 `result.json` 的 12 个阶段和清理结果，不能用整体退出码抹去 not-run。
- **模型页。** 启动 `"$TYPELAY_VERIFY_PACKAGE/typelay" --settings`，通过 `tests/linux/support/gtk_accessibility.py` 的 `select_settings_page(pid, "本地模型")` 进入实际页面。对当次控件树中的下载或切换动作调用公开 AT-SPI Action。保留确认对话框、取消后库存不变、明确确认后进度与库存变化的证据。
- **完整模型 UI 回归。** 既有 `tests/linux/native_e2e.py --inside --desktop --desktop-scope model-packages` 需要单独准备 Hy/Argos 库存和缓存。按 `tests/linux/hy_package_e2e.py` 的参数与仓库模型事务要求准备后，仍在本技能私有会话中调用，并提供 `--package "$TYPELAY_VERIFY_PACKAGE" --native "$PWD/artifacts/linux/native" --output "$TYPELAY_VERIFY_OUTPUT/model-packages"`。这不是默认 Index 旅程的附带通过项。

## Gotchas

- **当前 Btrfs 事实。** Index 的加载校验比较 `stat` 与 `/proc/self/maps` 的设备号。本机 Btrfs 子卷曾对同一路径、同一 inode 返回 `(0,57)` 与 `(0,35)`，因此拒绝加载，激活返回 `unchanged`、`model-start-failed`。这不是模型未下载。`private-session.py` 将**完整原样主包**复制到新建、可执行的用户私有 tmpfs 后启动，保留全部校验。本技能不修产品代码，不修改设备号检查或激活标记。
- `/dev/shm` 可能带 `noexec`。脚本使用 `/run/user/<当前 UID>` 下新建的目录并检查文件系统类型；不要硬编码旧运行路径。tmpfs 容量不足时先停止本次流程，保留失败证据。
- 私有 Python 在最终模型数据目录安装。搬迁已有 `components` 或 venv 会使解释器前缀失效。
- uv 0.12.13 找不到固定 CPython 的问题已在 build.md 记录。空控件树、模型未就绪和原生字段未接入是三种不同失败。
- 默认缓存只有 Index，不满足 Hy/Argos 全量 UI 旅程。缺少这些权重时，该入口应报告前提失败，不能以默认 CLI 通过代替。
