# 设置与暂停

用户通过设置、托盘或快捷面板调整翻译方式，暂停后停止跟随输入，恢复后继续工作。即时设置与待保存草稿具有不同的保存时机。

## Sub-features

- `settings/direction` 保存源语言与目标语言，并影响实际翻译。
- `settings/appearance` 调整浮层主题、字体和字号，重开后保留。
- `settings/apps-readability` 在英文 Apps 页读取连接名称、状态和按钮，并检查浅色侧栏文字。
- `settings/draft` 修改快捷键或排除列表，保存、取消和跨页保留草稿。
- `settings/pause` 暂停与恢复，并核对设置文件和真实浮层变化。
- `settings/gtk4-configuration` 验证源端语言配置的保存、退出、组合输入和失效拒绝。
- `settings/gtk4-glossary` 验证源端术语菜单、表单和原源保护。

## How to get to it (user POV)

- 从托盘选择设置，或使用 `typelay --settings`。
- 从托盘调整暂停、模式、语言，或打开 `typelay --quick-panel`。
- 在设置页切换通用、外观、语言与风格、术语表、快捷键、应用、本地模型和关于。
- 暂停也可用 `typelay --toggle-pause`，或已保存的暂停快捷键。

## Driving it with private niri

Preconditions: 私有会话内已有真实桌面进程。语言功能需要正式模型 Ready；暂停前先在普通字段看到有效预览。

- **语言保存。** 执行 Launch 的 `gtk-preview.py` 旅程。它通过实际设置 UI 选择英文到中文，读取 `settings-saved.json`，并用该方向生成真实 GTK 译文。重开 `"$TYPELAY_VERIFY_PACKAGE/typelay" --settings` 后，还应核对所选语言的显示。
- **设置导航。** 使用公开辅助函数 `select_settings_page(pid, "外观")` 或 `select_settings_page(pid, "快捷键")`。`pid` 必须来自本次 niri 窗口记录。`visible_named(pid, "外观：浅色")` 定位显示控件，`Atspi.Action.do_action(node.get_action_iface(), 0)` 触发真实动作。读取 `$XDG_CONFIG_HOME/typelay/settings.json` 核对保存，再重开核对显示。
- **Apps 可读性。** 在新私有会话运行 `/usr/bin/python3 "$PWD/.agents/skills/verify-typelay/scripts/settings-apps.py"`。该旅程只用初始配置跳过教程，再从真实设置的外观页选择浅色和 English，经侧栏进入 Apps。它分别将窗口设为 900、740 像素，在浅色和深色下保存 `apps-<主题>-<宽度>.png`。`text-geometry.json` 检查名称和状态中的英文单词未被拆行。`*-connection-card.json` 和 `*-buttons.json` 记录连接入口与卡片的边界，以及两个操作按钮的尺寸。`layout-issues.json` 必须为空：连接入口下方须有卡片内边距，两个按钮须等宽，100% 缩放下按钮不能出现三行文案，顶部托盘提示须为英文。旅程还打开桌面连接按钮对应的组件选择或安装确认窗口，保存 `apps-connection-action.png`，取消后保存 `apps-after-action.png`，核对设置未变。逐张打开 PNG，检查名称、状态、按钮、图标和未选中的侧栏文字。此入口不需要模型，不证明组件安装或真实应用接入。
- **Apps 原型对照。** 在本地浏览器打开 `docs/design/TypeLay_Tray_Settings_Prototype.html?page=apps&uiLang=en&theme=light&connection=disconnected`，再检查 `theme=dark`。离线时删除临时预览副本中的外部字体链接，并用本机已有字体字节声明同名字体；不修改确认原型。浏览器也放在断网命名空间。保存完整原型截图，按相同窗口宽度与上述产品截图并排比较。逐项核对连接卡片、状态、操作按钮、应用列表和侧栏。原型使用演示应用与模拟连接状态，产品使用真实安装应用及连接事实；保留这些差异，并注明产品额外显示的准备提示和连接入口。
- **系统字体与侧栏回归。** 在私有会话中设置 `TYPELAY_UI_FONT_ROOT="$TYPELAY_VERIFY_PACKAGE/fonts"`，执行 `/usr/bin/python3 tests/linux/test_ui_fonts.py -v` 和 `/usr/bin/python3 tests/linux/test_settings_feedback.py -v`。字体检查读取真实 GTK 系统字体，并在同一测试进程验证 Adwaita Sans 和另一系统字体偏好；侧栏检查真实标签在浅色、深色下的墨色。无显示的字体检查只证明通用字体回退，不能代替这条桌面语义检查。
- **暂停 CLI。** 执行 `"$TYPELAY_VERIFY_PACKAGE/typelay" --toggle-pause`。先观察原浮层消失和持久化的 Enabled 变化，再执行相同命令恢复。重新输入合成文本确认翻译恢复，不能只看开关外观。
- **快捷面板。** 执行 `"$TYPELAY_VERIFY_PACKAGE/typelay" --quick-panel`，保存实际面板截图和控件树，通过面板中的暂停动作验证同一行为，单独记录该入口。
- **既有设置回归。** 在私有旅程脚本内执行 `DISPLAY="$TYPELAY_VERIFY_XDISPLAY" /usr/bin/python3 tests/linux/native_e2e.py --desktop --desktop-scope settings-ui --package "$TYPELAY_VERIFY_PACKAGE" --native "$PWD/artifacts/linux/native" --output "$XDG_RUNTIME_DIR/settings-ui"`。退出前将该输出目录复制到 `$TYPELAY_VERIFY_OUTPUT/settings-ui`，保留原退出码。入口会创建自己的捕获会话，并用 `python -I -B` 加载设置驱动；短输出路径用于避免捕获 socket 超过 Unix 路径长度限制。该测试使用正式包的真实 GTK 设置和受控宿主，报告其范围，不替代真实模型、普通字段排除或写回。
- **Apps 交互合同。** 在私有会话中，以系统 GTK Python 执行 `tests/linux/settings_ui.py --dotnet <本机 dotnet 绝对路径> --host "$TYPELAY_VERIFY_PACKAGE/app/TypeLay.Linux.dll" --applications-only --output "$TYPELAY_VERIFY_OUTPUT/apps"`，再用新输出目录执行 `--connection-only`。保留 Wayland 环境并设置 `TYPELAY_UI_FONT_ROOT="$TYPELAY_VERIFY_PACKAGE/fonts"`。先在本次 `$TYPELAY_VERIFY_OUTPUT/niri.kdl` 增加 `window-rule { match title="^TypeLay.*"; open-floating true; }`，执行 `niri msg action load-config-file`，使合同取得其要求的 900×660 窗口和真实模态焦点。裸 Xvfb 没有窗口管理器，不能证明模态激活。这两个入口使用当前工作树的 GTK 代码、包内生产宿主、受控应用目录及明确缺少组件的目录，分别验证应用列表操作、组件选择器及连接入口的焦点交接。它们不证明真实应用接入；包内 GTK 的实际 Apps 页仍由上述 `settings-apps.py` 旅程取证。
- **托盘合同。** 在私有旅程脚本内执行 `DISPLAY="$TYPELAY_VERIFY_XDISPLAY" GDK_BACKEND=x11 /usr/bin/python3 tests/linux/tray_e2e.py --package "$TYPELAY_VERIFY_PACKAGE" --output "$TYPELAY_VERIFY_OUTPUT/tray"`。该入口使用当前工作树的 GTK 代码与包内宿主，不需要模型，验证 GTK/Ayatana 注册、菜单、暂停恢复和持久化，不证明真实 Waybar 的点击和视觉。语言保存失败时检查 `language-<source/target>-save-failure.json` 和 PNG，确认底部反馈可见、磁盘旧值不变、控件回滚且焦点保留。
- **GTK4 配置与完整交互。** 采用[GTK4 独立交互命令](writeback.md#driving-it-with-private-niri)，把 scope 改为 `gtk4`，输出改为新的 `gtk4` 目录。这个完整入口先执行八个独立配置场景，再执行语言搜索、组合输入及失效拒绝，随后进入真实模型交互。配置使用真实 Input、Overlay、GTK4 和 Fcitx，但 Host 配置 ACK/结果受控；模型 Ready 不把它变成真实 Host 保存证明。每个 `gtk-reentry-*-unchanged-click.json` 保留动作、时间线、Begin/Save/Cancel 计数，配对 JSON 保留实际原字段、输入法和控件状态。失败后未执行的配置与交互记录为 `not-run`。组合输入时，候选窗可能遮住语言行中央。保持原 Composing 状态，在同一已绘制行的可见左侧固定点投递一次点击，并核对原生 hover。后续单独运行交互 scope 时，保留两次运行各自的边界。
- **GTK4 术语。** 先通过 `tests/linux/fcitx_cooperative/run.py --build-only --output artifacts/linux/fcitx-cooperative-5.1.22` 准备匹配的生产组件和受控 fixtures。复用构建时核对 `build-inputs.json`、`test-inputs.json` 与当前输入；生产归档仍按[构建准备](../references/build.md)执行 pack/finalize。随后在技能的私有旅程中运行：

  ```bash
  TMPDIR=/tmp TYPELAY_COOPERATIVE_OWNED_XVFB=1 timeout 145 \
    dbus-run-session -- xvfb-run -a -s '-screen 0 1280x960x24' \
    sh -c 'exec /usr/bin/python3 tests/linux/fcitx_cooperative/run.py \
      --session --glossary-toolkit gtk4 \
      --session-deadline "$(/usr/bin/python3 -c "import time; print(time.monotonic() + 115)")" \
      --output "$1"' sh "$PWD/artifacts/linux/fcitx-cooperative-5.1.22"
  ```

  该入口需要自己的 Xvfb 和 D-Bus，因为它会再启动 niri，且要求该 Xvfb 只有一个 niri。`TMPDIR=/tmp` 避免嵌套 runtime 超过 Unix socket 路径上限。用户级桌面工具需要的 `LD_LIBRARY_PATH` 仍须可用。四十个场景共享原 115 秒预算，保留 800 毫秒呈现 lease 和原动作次数；受控候选不证明模型术语质量。复制构建目录中的 `gtk4-glossary-*` 原件到本次证据目录，保留结果、失败场景、原字段状态、命令时序、菜单 PNG、启动与内部清理回执。`cases` 仅列已完成场景，`failed_case` 是实际失败场景，其余是 `not-run`。

## Gotchas

- 即时开关、语言和外观保存，与快捷键、排除列表的草稿保存不能混为一谈。保存失败时应保留草稿，不能手写文件消除失败。
- 无面板的嵌套 niri 不显示真实 Waybar 托盘。CLI 和快捷面板通过不证明托盘点击、面板重启或图标视觉。
- 本次基础旅程只证明语言设置及其翻译效果，其它页面、暂停、草稿和托盘入口须独立运行。
- 公开 AT-SPI Action 不证明物理鼠标像素命中。视觉核对仍需实际截图。
- GTK4 三条入口分别记录源码、测试改动、包、合作组件和模型身份；受控配置、术语合同与真实模型交互各自取证。检查技能 `cleanup.json` 的 `remainingPids=[]`，未使用 `--keep-scratch` 时，每次运行后均确认 scratch/runtime 已删除，原件仍存在。保留历史失败须先在对应 Issue 附复现、影响边界、原件与后续票，逐项取得负责人批准；未获批准的场景仍保留原 CI 必过约束。
