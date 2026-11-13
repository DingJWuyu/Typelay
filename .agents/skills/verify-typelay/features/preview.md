# 预览与按需唤醒

用户在普通输入框写入正文，TypeLay 在字段旁显示译文。预览保留原文；按需模式只在明确唤醒后开始。

## Sub-features

- `preview/gtk3-entry` 在普通 GTK3 Entry 输入英文，观察中文浮层与原文未变。
- `preview/automatic` 在自动模式改变源文，观察译文随当前输入更新。
- `preview/on-demand` 在按需模式输入后保持安静，明确唤醒后才显示预览。
- `preview/dismiss` 退出浮层，保留原输入内容与普通键盘行为。

## How to get to it (user POV)

- 在设置的“通用”页或托盘选择工作模式，再聚焦已连接的普通输入框。
- 自动模式随输入开始；按需模式使用保存的唤醒快捷键，默认 `Ctrl+Shift+F9`。
- niri 绑定可调用 `typelay --wake`。浮层退出使用保存的退出键，默认 Esc。
- 浏览器网页和 Qt 有各自接入前提，见[应用接入](applications.md)。

## Driving it with private niri

Preconditions: Launch 已准备真实 Index 模型与匹配合作组件。`gtk3-demo` 来自系统 GTK3 demos，不含 TypeLay 测试 setter。

- **完整普通输入框旅程。** 执行 SKILL 的 Launch 命令，脚本依次选择“语言与风格”、`设置：源语言`、`语言选项：en`、`设置：目标语言`、`语言选项：zh`，再观察保存文件中的 `Source=en`、`Target=zh`。
- **打开普通字段。** 旅程通过 `/usr/bin/python3 "$TYPELAY_VERIFY_COOPERATIVE/fcitx-cooperative.py" launch "$TYPELAY_VERIFY_PACKAGE/typelay" launch gtk3-demo --run=entry_completion` 打开 `Entry Completion`。用 niri 的实际窗口 PID 限定 AT-SPI 查找范围，要求只有一个显示中的可编辑 TEXT 控件。
- **输入与观察。** 脚本用 `xdotool type --clearmodifiers --delay 60 'Please keep the backup until Friday.'`，但其 DISPLAY 必须显式设为 `TYPELAY_VERIFY_XDISPLAY`。确认该字段完整显示原文，再观察浮层中文、`ValidTranslation` 和 `sourceSupported=true`。查看 `translation.json/png`，核对保留备份到周五的含义。
- **按需入口。** 在自己的旅程脚本中，通过设置的 `设置：工作模式：按需` 控件选择按需模式。输入后先证明没有浮层，再执行 `"$TYPELAY_VERIFY_PACKAGE/typelay" --wake`。另一次独立运行用 XTest 投递 `ctrl+shift+F9`，两条入口分别记录结果。CLI 唤醒不能证明物理全局快捷键。
- **退出。** 在原字段保持焦点时，向私有 Xvfb 投递 `xdotool key --clearmodifiers Escape`。观察浮层消失、源文保持不变，并保存动作前后截图。

## Gotchas

- GTK `LanguagePicker` 的选项以 `语言选项：en` 和 `语言选项：zh` 命名，显示文字不是稳定的动作名称。导航是公开 Selection 接口，不能把标签当按钮。
- 私有 D-Bus 没有 systemd 用户服务。此机器默认的 AT-SPI dbus-broker 启动会导致控件树为空。会话脚本设置 `ATSPI_DBUS_IMPLEMENTATION=dbus-daemon`，使用上游支持的选择方式，见 [AT-SPI 启动实现](https://github.com/GNOME/at-spi2-core/blob/main/bus/at-spi-bus-launcher.c)。
- Source=auto、Target=en 的默认设置不能证明英文到中文预览。必须通过真实语言控件修改，不能手写设置文件假装完成 UI 流程。
- XTest 是合成输入证据。物理键盘、真实中文候选、缩放和多屏没有因此通过。
- Index 在本机 Btrfs 上的加载限制与 tmpfs 绕法见[模型 Gotchas](models.md#gotchas)。
