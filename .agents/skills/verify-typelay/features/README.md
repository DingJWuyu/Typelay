# TypeLay 功能验证地图

本目录维护用户可见行为的验证入口。先读 [SKILL.md](../SKILL.md)，再按本次改动选择功能。改变已映射行为时，同一变更必须更新对应文件。

## 共用前提

- 每条桌面旅程都通过 `private-session.py`，使用自己的 HOME、XDG、D-Bus、Xvfb 和 niri。
- 每条旅程默认离线，并统一复制 uv 与模型下载缓存。缓存来源、下载测试 opt-in 和失败清理遵循 [Network and caches](../SKILL.md#network-and-caches)。
- 主包和合作组件来自匹配源码，真实模型通过正式安装器准备。权重缓存不等于已安装模型。
- `TYPELAY_VERIFY_PACKAGE`、`TYPELAY_VERIFY_COOPERATIVE`、`TYPELAY_VERIFY_OUTPUT` 、`TYPELAY_VERIFY_INSTALL_CACHE`、`TYPELAY_VERIFY_MODEL_CACHE`、`TYPELAY_VERIFY_SCRATCH` 和 `TYPELAY_VERIFY_XDISPLAY` 由私有会话产生。下面功能文件中的命令只在该会话的旅程脚本内运行。
- 动作使用合成文本。AT-SPI 仅查找本次进程拥有的控件，XTest 仅使用本次 Xvfb。
- 地图中的其它平台入口也是待验证范围。Windows 在此机器只读取 Actions，Qt、浏览器或 VS Code 前提不足时如实记录未运行。

## 证据与入口覆盖

记录功能 ID、用户入口、包提交、命令、退出码、动作前后截图与实际状态。修改设置或模型时还要保存磁盘结果。每次失败和成功都检查清理回执，并确认原件仍存在。

一个入口通过只能证明该入口。CLI 准备不能代替模型页按钮，普通 GTK 预览不能代替教程、写回或浏览器。缺少前提的旅程记录失败原因，并补到相应 Gotchas，不降低条件。

## 功能索引

| 功能 | 用户入口和主要证据 |
| --- | --- |
| [预览与按需唤醒](preview.md) | 普通 GTK 输入框、自动模式、按需快捷键和 CLI 唤醒。 |
| [模型准备与恢复](models.md) | CLI、首次配置和本地模型页，观察正式安装、Ready 和库存。 |
| [设置与暂停](settings.md) | 设置窗口、托盘、快捷面板与 CLI，观察保存和恢复。 |
| [接受、编辑与直接替换](writeback.md) | Tab、浮层编辑和直接替换。普通网页 `browser-index` 串联真实默认模型、两次 Tab、选区和后台 Direct。 |
| [Linux 原生 Undo](undo.md) | 必过旅程 `browser-undo-index` 使用真实 Index 和 Tiptap，默认 Tab 替换后由宿主 Ctrl+Z 恢复原文。 |
| [应用接入与练习](applications.md) | 应用支持、教程、手动合作入口及扩展，观察实际连接。 |
| [Qt 交互与语言搜索](qt.md) | 分开运行合作 probe、语言搜索和真实模型桌面，保留各阶段结果及发行证明边界。 |

每个功能文件使用四个 H2：`Sub-features`、`How to get to it (user POV)`、`Driving it with private niri`、`Gotchas`。
