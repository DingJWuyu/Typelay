# Qt 交互与语言搜索

Qt 的真实模型桌面交互和语言搜索使用不同入口。分别保留每个入口的结果，前序失败后的场景记为 `not-run`。

## Sub-features

- `qt/cooperative` 验证真实 Fcitx provider、原按次所有权、捕获、编辑、写回及未用许可退休。
- `qt/language-search` 验证语言列表、搜索、保存和输入法组合期间的拒绝，使用受控配置 Host，不加载模型。
- `qt/desktop` 验证真实 Host、模型和 Wayland 浮层，包括人工稿、两次接受、选区、直接替换与原生撤销。

## How to get to it (user POV)

- 用户通过合作包的 `launch --qt6` 打开应用，在标准 `QLineEdit` 或 `QPlainTextEdit` 输入。
- 用户点击译文编辑，第一次接受回源，释放后第二次独立接受才写入。
- 用户在按需模式唤醒空字段，打开语言菜单，进入目标语言搜索。组合输入期间，点击语言行或按 Return 都不能保存。

## Driving it with private niri

Preconditions: 按[构建说明](../references/build.md)准备主包和 Fcitx 构建目录，并按 [Qt 平台说明](../../../../docs/platforms/linux/qt.md#构建与验证)准备匹配的 Qt SDK。以下命令都放入 [Launch](../SKILL.md#launch) 的私有旅程脚本，使用默认离线会话。`fcitx` 指向本工作树的完整 Fcitx 构建目录，不能传只含 GTK 的合作发行包。

```bash
set -euo pipefail
fcitx="$PWD/artifacts/verification-build/fcitx"
```

**完整合作 probe。** 将本机已有的固定源码归档放入新的 `artifacts/linux/qt-cooperative/fcitx5-qt.tar.gz`，再运行下列命令。归档身份由 `eng/linux/cooperative/inputs.py` 定义，构建器复验摘要。归档或 SDK 缺失时停止该步，报告缺少的路径，不下载替代字节。

```bash
/usr/bin/python3 tests/linux/qt_cooperative/run.py \
  --fcitx-root "$fcitx" --verify-fcitx-build \
  --out "$PWD/artifacts/linux/qt-cooperative" \
  2>&1 | tee "$TYPELAY_VERIFY_OUTPUT/qt-cooperative.log"
```

该入口在私有会话内再建 Xvfb、D-Bus 和 HOME，运行真实异步 Fcitx 输入。它使用 XCB 与受控源字段，不证明 Wayland 或模型交互。保留 `versions.json`、`async-*.json`、`ordered-*.json`、各模式日志和命令退出码。`rapid-control-result.json` 记录首个快速接受的原许可、投递时刻及动作后的 fixture，不补发输入。完整结果只在整条链成功后产生；缺少后续结果文件表示尚无执行完成证据。

**独立语言搜索。** 完整 probe 成功时，其中的语言入口已经完成。CI 将本次 `language-options-*.json`、日志、配置截图和 `versions.json` 复制到独立证据目录，不再构建或重复输入。完整 probe 失败时仍执行下列独立入口。使用新的输出目录，复制同一固定源码归档，然后通过相同生产配方重新构建。不要在完整 probe 的输出上重跑 `--session`，该入口会清除原来的异步资格结果。

```bash
mkdir "$PWD/artifacts/linux/qt-language-options"
cp --reflink=auto "$PWD/artifacts/linux/qt-cooperative/fcitx5-qt.tar.gz" \
  "$PWD/artifacts/linux/qt-language-options/fcitx5-qt.tar.gz"
/usr/bin/python3 tests/linux/qt_cooperative/run.py \
  --fcitx-root "$fcitx" --verify-fcitx-build --language-options-only \
  --out "$PWD/artifacts/linux/qt-language-options" \
  2>&1 | tee "$TYPELAY_VERIFY_OUTPUT/qt-language-options.log"
```

保留 `language-options-result.json`、`language-options-native-state.json`、`language-options-native.log` 和 `language-configuration-*.png`。命令日志中的 `qt-configuration-click-request` 记录本次点击使用的快照。核对实际按下、释放和 clicked 回执；保留单次动作、原 15 秒观察期限和真实 pinyin 条件。Composing 点击、组合 Return、按住重复、释放后的独立 Return 分别判断。Unicode 搜索使用私有剪贴板粘贴，不是物理 Unicode 输入证据。

同一原生版本内，设置推进正式 `actionRevision` 后，旧“更多”菜单必须关闭；仅反馈更新应保留当前菜单。无关翻译动作更新不能关闭语言菜单。三种情况都要保留源字段、编辑器正文与几何、焦点迁移记录的对照。

原生编辑控件就绪只改变 `canEdit` 时，正式交互和动作身份可以保持不变。语言和“更多”菜单继续显示，编辑器仍只读，源正文、焦点和写入记录保持不变。检查必须使用本次 render 返回后观察到的新快照。旧菜单按下后的视图版本变化仍须退休原动作，释放不得借用新视图执行。

**真实模型桌面。** Qt probe 成功后，按平台说明把 Qt 插件封入合作包，再将该包传给私有会话。`qt` 指向同源码构建的 fixture 目录。下列命令保留现役 Argos 桌面基线，不代表默认 Index 已通过。

```bash
qt="$PWD/artifacts/linux/qt-cooperative"
mkdir "$TYPELAY_VERIFY_OUTPUT/qt-desktop"
"$TYPELAY_VERIFY_PACKAGE/typelay" --install-models argos \
  > "$TYPELAY_VERIFY_OUTPUT/model-install.log" 2>&1
DISPLAY="$TYPELAY_VERIFY_XDISPLAY" timeout --kill-after=10s 240s \
  /usr/bin/python3 tests/linux/native_e2e.py \
  --inside --desktop --desktop-scope qt \
  --package "$TYPELAY_VERIFY_PACKAGE" \
  --fcitx-cooperative "$TYPELAY_VERIFY_COOPERATIVE" \
  --qt-cooperative "$qt" --native "$PWD/artifacts/linux/native" \
  --output "$TYPELAY_VERIFY_OUTPUT/qt-desktop"
```

此入口注入初始语言、模式和实验写回设置，并使用标准 Qt 控件 fixture。读取 `qt-desktop-runtime.json` 核对实际 Qt 版本、加载库和 Wayland 平台。保留候选、编辑、按键时序、源字段和唯一写回回执。失败时保留 `failure-status.json`、`failure.png`、`inside.log` 和原始 controller trace，后续未执行项目记为 `not-run`。

核对人工稿恢复时，同时检查完整来源版本和新计划。过期接受保持零写入；新捕获只能在原生内容、选区、文档、焦点、上下文、字段和完整性版本均相等时恢复稿件，新接受必须是独立动作。合作 probe 的 `sourceObservationVersions` 记录原生捕获及正文、选区、只读和焦点 ABA 的版本变化。共享控制器回归不代替这组原生证据。

人工稿二次编辑仍由编辑焦点交接和回源确认路径负责。待接受状态的来源连续性检查不得提前结束这两个过程；回源未确认时不能发布接受许可。

减少动画与恢复应用样式只改变显示，`sourceObservationVersions` 应保持相等。真实桌面中的长人工稿在两次样式变化前后都必须保留；机器译文替代稿件属于丢稿，不能解释成高度采样差异。

人工编辑器收起后，需要同一编辑器的最新紧凑帧，以及原次点击对应动画的 `started`、连续显示版本和 `settled`。`started` 日志可以在时钟重启后跨过毫秒边界，但其后的耗时必须保持单调。反向点击可以先停止旧展开动画，随后仍须观察本次收起的完整终态。旧动画的停止、访问过紧凑高度或未结束日志都不能作为收起完成。

同一交互的接受提示更新保持原动画。只有 `canAccept`、`tabHint` 和 `displaySequence` 的变化可以沿用同版本动画，正文、身份、布局和实际控件依据仍须相同。`language-options-motion.json` 记录真实 Qt 正例，以及交互、正文和字号变化时的退休反例。真实模型桌面的原 800ms 采样和高度断言保持不变。

若完整 probe 失败，先运行 `tests/linux/qt_cooperative/desktop_admission.py`，参数沿 [Qt 平台说明](../../../../docs/platforms/linux/qt.md#部分-qt-检查不构成发行验收)。仅 `admitted-build-input` 允许独立桌面观察，此时把上方 `--fcitx-cooperative` 改为 `"$fcitx"`。保留 `distributionAcceptance=false`，该观察不改变完整 probe 的失败，也不证明搬迁发行包通过。

每条私有旅程结束后，按 [Cleanup](../SKILL.md#cleanup)检查进程和安装副本清理，确认日志、截图和 JSON 仍在。Actions 使用同一 probe 与桌面 runner，并在独立 `qt-language-options` 步骤执行语言搜索。分别读取步骤 outcome、实际被测提交和小型 Qt artifact，不用工作流总状态替代场景结果。

## Gotchas

- 系统 Qt 插件没有合作补丁，不能替代固定 provider。Qt、Fcitx、主包和 fixture 的源码与二进制身份必须匹配。
- 语言搜索的受控 Host 回执不能证明正式设置持久化。真实 Host 的保存由 `qt/desktop` 另验。
- 原 100ms 接受许可、150ms 命令期限和单次按键保持不变。译文可见但安全拒绝仍是本次失败，不能补发按键或续期。
- 旧成功、只编译成功和部分 probe 都不能抵消本次失败。历史例外须逐项取得负责人批准，原件和后续 issue 随申请保留。
