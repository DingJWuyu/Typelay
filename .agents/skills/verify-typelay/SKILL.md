---
name: verify-typelay
description: 验证 TypeLay 的 Linux 原生桌面、普通 GTK 输入框翻译、模型准备、设置和写回。启动或驱动应用、复现桌面问题、采集验收证据时使用；所有应用操作限定在私有嵌套会话，Windows 只读取 GitHub Actions 证据。
---

# 验证 TypeLay

本技能用实际发行包、正式 Index 模型和 Fcitx 合作组件，驱动普通 GTK 输入框。先读[功能地图](features/README.md)，选择本次要证明的入口。修改已映射行为时，在同一变更中更新对应功能文件。

## Launch

所有 TypeLay、Fcitx、目标应用和模拟输入都通过下方的私有会话启动。脚本新建 HOME、XDG、D-Bus、Xvfb 和 niri，清除继承的桌面与输入法环境。不要在外层终端运行产品命令、复用用户的 DISPLAY，或停止用户的输入法。两个验证实例使用不同输出目录，不共享模型安装代、配置或会话 socket。

从新工作树开始，先按[构建与依赖准备](references/build.md)完成当前提交的主包与合作包，并检查本机下载缓存。该文档也说明如何发现这台机器上的用户级工具。启动前检查包的 `build-info.json`，将实际提交与本次代码差异一起记录。

在仓库根目录执行以下完整流程。`--output` 必须不存在，且解析符号链接后须位于源包目录外。重复执行时换一个名称。

```bash
skill="$PWD/.agents/skills/verify-typelay"
proof="$PWD/artifacts/verification/gtk-$(date +%Y%m%d-%H%M%S)"
/usr/bin/python3 "$skill/scripts/private-session.py" \
  --package artifacts/verification-build/package \
  --cooperative artifacts/verification-build/cooperation/TypeLay-Fcitx-cooperative \
  --output "$proof" \
  -- /usr/bin/python3 "$skill/scripts/gtk-preview.py"
```

`private-session.py` 把完整主包复制到输出目录的 `scratch/package`。输出目录使用正常文件系统，Btrfs 也必须通过正式加载校验。`gtk-preview.py` 从空数据目录调用正式 `typelay --install-models`，在最终路径安装私有 Python 和模型，再启动合作输入与设置。缓存字节仍须经过正式安装器的原有校验。不得搬迁已经安装的 Python 虚拟环境。

可用状态要求 `doctor-ready.json` 中 `session.modelReady=true`，设置窗口可操作。普通 GTK 字段接入后，还需观察 `sourceSupported=true`、实际源文和可见译文。仅 socket 存在、进程存活或 doctor 退出码为零，都不算就绪。单次流程默认限时 1200 秒，安装限时 600 秒，翻译限时 90 秒；超时保存日志和证据，停止本次自有进程并清理安装副本。

## Network and caches

默认每条私有会话离线运行。`private-session.py` 使用独立用户及网络命名空间，仅启用本次 loopback，供浏览器本地 fixture 使用。命名空间创建或核验失败时立即停止，不能回退到主机网络。正式安装器仅透传 `UV_OFFLINE` 与 `UV_PYTHON_INSTALL_MIRROR` 两个字节来源选项；私有会话同时设置 `UV_OFFLINE=1`，但所有下载路径仍由网络命名空间约束。

脚本通过 Git common directory 找到主检出，默认将其 `artifacts/install-cache/` 和 `artifacts/index-model-download/` 复制到本次 `scratch/cache/typelay/components/`。前者包含 `uv/`，也可包含其他模型的正式缓存布局；后者是 Index 权重和许可证。可以在外层用 `--install-cache PATH`、`--model-cache PATH` 指定来源。复制使用 `cp -a --reflink=auto`，每次拥有独立可写副本，不修改共享源缓存。只复制下载缓存；私有 Python、venv 和模型安装代必须在最终路径重新安装。

所有 runner 使用会话提供的 `TYPELAY_VERIFY_INSTALL_CACHE` 和 `TYPELAY_VERIFY_MODEL_CACHE`。需要独立安装路径的 runner 同时复制整份下载缓存，并把安装数据放在 `TYPELAY_VERIFY_SCRATCH` 下。新 runner 也须遵守这些规则，不得只复制权重后依赖网络补齐运行时。

缓存缺失时保存失败并报告缺项。uv 的 wheels 缓存不包含 CPython 原始归档。将已在本机的原始归档按上游 `<release>/<filename>` 布局放在 `install-cache/python/`，会话会复制它并设置 `UV_PYTHON_INSTALL_MIRROR=file://...`。正式 uv 仍校验归档并安装到最终路径。归档缺失则整条旅程失败，不能复制已有解释器或虚拟环境伪装成安装通过。构建、工具准备与缓存准备也受本次任务网络预算约束；无下载授权时，缺少本地前提就停止该步骤。

只有明确以下载为验收目标、且负责人已授权联网的旅程，才在外层显式添加 `--allow-downloads`。此选项会开放整条旅程的网络，仍优先复制已有缓存，并在 `network.json` 记录 opt-in。普通安装、预览和写回旅程保持默认离线，不得为绕过缓存缺失添加该选项。

检查 `cache-sources.json` 和 `network.json`，确认来源、独立 namespace 和唯一 `lo` 接口。`wlan0.json` 记录外层 `/proc/net/dev` 的接收字节差值，计算为结束值减开始值，单位为 byte。它包含主机其它流量，只能作为辅助证据；没有 wlan0 时记录 null，不能宣称零流量。

## Doctor

遇到异常先在**已启动的私有命令内**运行这一个只读检查：

```bash
"$TYPELAY_VERIFY_PACKAGE/typelay" --doctor
```

核对 `hostInstalled`、`desktopDependencies`、`niriVersion`，以及 `session.modelReady`、`modelFailureCode`、`nativePeers`、`sourceSupported`。将结果与该次 `session.json`、`package-identity.json` 对照，确认包身份、私有 socket 和数据目录。`gtk-preview.py` 已在输入前后保存这份检查。doctor 不包含正文，必须另看真实 GTK 控件和截图。Unknown 输入法、未知焦点或写入拒绝都是诊断结果，不能猜测成可用。

## Drive

`gtk-preview.py` 使用公开 AT-SPI 动作选择设置中的语言，然后用 XTest 通过自有 Xvfb 给嵌套 niri 输入文字。它不调用内部 setter，也不调用翻译测试端点。具体控件名称和证明标准见[预览](features/preview.md)。

需要其它旅程时，将对应功能文件的命令保存为 `artifacts/verification-journey.sh`，再通过同一个会话入口运行。命令文件中的环境变量由私有会话提供；文件最后一个命令的退出码必须代表整条旅程。

```bash
chmod +x artifacts/verification-journey.sh
/usr/bin/python3 "$skill/scripts/private-session.py" \
  --package artifacts/verification-build/package \
  --cooperative artifacts/verification-build/cooperation/TypeLay-Fcitx-cooperative \
  --output "$PWD/artifacts/verification/custom-$(date +%Y%m%d-%H%M%S)" \
  -- bash "$PWD/artifacts/verification-journey.sh"
```

文件采用 `set -euo pipefail`，并从功能地图选择实际命令。需要 XTest 的既有 runner 使用 `DISPLAY="$TYPELAY_VERIFY_XDISPLAY"`；其余应用保持 Wayland。引用现有测试时，记录它使用的受控 fixture、初始设置或替身，不能把测试状态注入当作用户路径证据。模型、浏览器扩展、Qt 插件等前提缺失时，记录该入口失败和缺少的条件，不能换成方便的入口宣告通过。

## Evidence

证据保存在本次 `artifacts/verification/<运行名>/`，属于被忽略的本地原件，不提交模型、个人目录或历史快照。

| 证据 | 用途 |
| --- | --- |
| `invocation.json`、`package-identity.json`、`session.json` | 记录命令、产品提交和隔离身份。 |
| `cache-sources.json`、`network.json`、`wlan0.json` | 记录下载缓存来源、离线隔离和主机接收字节差值。 |
| `actions.json`、`before-typing.json/png` | 记录真实 UI 动作、输入正文和操作前状态。 |
| `translation.json/png`、`doctor-after.json`、`result.json` | 记录可见译文、源文未变和生产状态。 |
| `settings-saved.json`、`installed.json`、`model-install.log` | 核对实际设置持久化与正式模型库存。 |
| `journey.log`、`niri.log`、`typelay.log`、`fcitx.log` | 保留进程输出和失败定位信息。 |
| `exit.json`、`cleanup.json` | 记录命令退出码、残留进程和临时目录清理。 |

完整证明需要动作和结果两端。写回旅程还须核对原字段正文、选区和原生撤销；模型切换须核对实际库存。只观察到译文不等于 Tab 接受、F10、物理输入法或语言质量已通过。替身仅用于生产已有的外部边界，并明确标注。任何称为 dry-run 的入口，都要观察文件、网络与 Git refs 的实际变化。

打开 `translation.png` 检查浮层文字、裁剪、位置和焦点。将显示的译文与输入逐句核对，不能仅凭中文字符或 `ValidTranslation` 判定语义正确。报告本次实际驱动的功能 ID 和入口，其余入口标为未运行。

Windows 无法在这台机器驱动。按[Windows 证据](references/windows.md)读取对应提交的 GitHub Actions job 和 artifact，不把 Linux 或共享契约结果算作 Windows 桌面验收。

## Cleanup

旅程结束时先请求 `typelay --quit`，再请求本次合作 Fcitx 退出。外层脚本作为 subreaper 回收自己的后代，包括 D-Bus 激活进程；它不按进程名称结束程序。成功、失败或超时后均删除该次 HOME、XDG、缓存和包副本，保留输出目录中的日志、截图和 JSON。确需诊断安装目录时，在运行前显式传入 `--keep-scratch`。若仍有自有进程存活，则保留目录并报告清理失败。

脚本返回后检查 `cleanup.json` 的 `remainingPids=[]`。未使用 `--keep-scratch` 时，成功、失败和超时都要求 `scratchRemoved=true`、`runtimeRemoved=true`。成功预览还须确认 `translation.png`、`translation.json`、`result.json` 仍存在，这一步是技能证明的一部分。

显式保留的安装副本查明并完成验收后，只删除各次 `cleanup.json` 记录的自有 scratch/runtime 目录，以及本次临时旅程脚本。保留日志、截图、结果和清理回执。若外层被 SIGKILL 或机器重启中断，先核对遗留进程的父子关系和目录身份，不能照抄旧 PID 杀进程。

## Helpers

- `scripts/private-session.py --package PATH --cooperative PATH --output PATH -- COMMAND...` 运行一条私有桌面旅程并负责清理。
- `scripts/gtk-preview.py` 仅在上述会话内部运行，完成正式模型准备、语言设置和普通 GTK 预览。

两个脚本都可执行，并在 Launch 中给出完整调用。它们不构建产品、不安装系统包，也不改产品代码。需要更新地图时使用 `/maintain-verification-skill`；若机器未安装该技能，直接按当前功能文件更新行为和证据要求。
