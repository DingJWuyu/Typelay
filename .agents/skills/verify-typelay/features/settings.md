# 设置与暂停

用户通过设置、托盘或快捷面板调整翻译方式，暂停后停止跟随输入，恢复后继续工作。即时设置与待保存草稿具有不同的保存时机。

## Sub-features

- `settings/direction` 保存源语言与目标语言，并影响实际翻译。
- `settings/appearance` 调整浮层主题、字体和字号，重开后保留。
- `settings/apps-readability` 在英文 Apps 页读取连接名称、状态和按钮，并检查浅色侧栏文字。
- `settings/draft` 修改快捷键或排除列表，保存、取消和跨页保留草稿。
- `settings/pause` 暂停与恢复，并核对设置文件和真实浮层变化。

## How to get to it (user POV)

- 从托盘选择设置，或使用 `typelay --settings`。
- 从托盘调整暂停、模式、语言，或打开 `typelay --quick-panel`。
- 在设置页切换通用、外观、语言与风格、术语表、快捷键、应用、本地模型和关于。
- 暂停也可用 `typelay --toggle-pause`，或已保存的暂停快捷键。

## Driving it with private niri

Preconditions: 私有会话内已有真实桌面进程。语言功能需要正式模型 Ready；暂停前先在普通字段看到有效预览。

- **语言保存。** 执行 Launch 的 `gtk-preview.py` 旅程。它通过实际设置 UI 选择英文到中文，读取 `settings-saved.json`，并用该方向生成真实 GTK 译文。重开 `"$TYPELAY_VERIFY_PACKAGE/typelay" --settings` 后，还应核对所选语言的显示。
- **设置导航。** 使用公开辅助函数 `select_settings_page(pid, "外观")` 或 `select_settings_page(pid, "快捷键")`。`pid` 必须来自本次 niri 窗口记录。`visible_named(pid, "外观：浅色")` 定位显示控件，`Atspi.Action.do_action(node.get_action_iface(), 0)` 触发真实动作。读取 `$XDG_CONFIG_HOME/typelay/settings.json` 核对保存，再重开核对显示。
- **Apps 可读性。** 在新私有会话运行 `/usr/bin/python3 "$PWD/.agents/skills/verify-typelay/scripts/settings-apps.py"`。该旅程只用初始配置跳过教程，再从真实设置的外观页选择浅色和 English，经侧栏进入 Apps。它分别将窗口设为 900、740 像素，在浅色和深色下保存 `apps-<主题>-<宽度>.png`。`text-geometry.json` 检查名称和状态中的英文单词未被拆行。旅程还打开桌面连接按钮对应的组件选择或安装确认窗口，保存 `apps-connection-action.png`，取消后保存 `apps-after-action.png`，核对设置未变。逐张打开 PNG，检查名称、状态、按钮、图标和未选中的侧栏文字。此入口不需要模型，不证明组件安装或真实应用接入。
- **系统字体与侧栏回归。** 在私有会话中设置 `TYPELAY_UI_FONT_ROOT="$TYPELAY_VERIFY_PACKAGE/fonts"`，执行 `/usr/bin/python3 tests/linux/test_ui_fonts.py -v` 和 `/usr/bin/python3 tests/linux/test_settings_feedback.py -v`。字体检查读取真实 GTK 系统字体，并在同一测试进程验证 Adwaita Sans 和另一系统字体偏好；侧栏检查真实标签在浅色、深色下的墨色。无显示的字体检查只证明通用字体回退，不能代替这条桌面语义检查。
- **暂停 CLI。** 执行 `"$TYPELAY_VERIFY_PACKAGE/typelay" --toggle-pause`。先观察原浮层消失和持久化的 Enabled 变化，再执行相同命令恢复。重新输入合成文本确认翻译恢复，不能只看开关外观。
- **快捷面板。** 执行 `"$TYPELAY_VERIFY_PACKAGE/typelay" --quick-panel`，保存实际面板截图和控件树，通过面板中的暂停动作验证同一行为，单独记录该入口。
- **既有设置回归。** 在私有旅程脚本内执行 `DISPLAY="$TYPELAY_VERIFY_XDISPLAY" /usr/bin/python3 tests/linux/native_e2e.py --desktop --desktop-scope settings-ui --package "$TYPELAY_VERIFY_PACKAGE" --native "$PWD/artifacts/linux/native" --output "$XDG_RUNTIME_DIR/settings-ui"`。退出前将该输出目录复制到 `$TYPELAY_VERIFY_OUTPUT/settings-ui`，保留原退出码。入口会创建自己的捕获会话，并用 `python -I -B` 加载设置驱动；短输出路径用于避免捕获 socket 超过 Unix 路径长度限制。该测试使用正式包的真实 GTK 设置和受控宿主，报告其范围，不替代真实模型、普通字段排除或写回。
- **托盘合同。** 在私有旅程脚本内执行 `DISPLAY="$TYPELAY_VERIFY_XDISPLAY" GDK_BACKEND=x11 /usr/bin/python3 tests/linux/tray_e2e.py --package "$TYPELAY_VERIFY_PACKAGE" --output "$TYPELAY_VERIFY_OUTPUT/tray"`。该入口使用当前工作树的 GTK 代码与包内宿主，不需要模型，验证 GTK/Ayatana 注册、菜单、暂停恢复和持久化，不证明真实 Waybar 的点击和视觉。语言保存失败时检查 `language-<source/target>-save-failure.json` 和 PNG，确认底部反馈可见、磁盘旧值不变、控件回滚且焦点保留。

## Gotchas

- 即时开关、语言和外观保存，与快捷键、排除列表的草稿保存不能混为一谈。保存失败时应保留草稿，不能手写文件消除失败。
- 无面板的嵌套 niri 不显示真实 Waybar 托盘。CLI 和快捷面板通过不证明托盘点击、面板重启或图标视觉。
- 本次基础旅程只证明语言设置及其翻译效果，其它页面、暂停、草稿和托盘入口须独立运行。
- 公开 AT-SPI Action 不证明物理鼠标像素命中。视觉核对仍需实际截图。
