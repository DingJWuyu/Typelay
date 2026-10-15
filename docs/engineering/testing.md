# 工程验证协议

修改测试、排障或声明验收结果时读本文件和 tests/AGENTS.md。检查范围按[共享开发流程](development.md)选择，产品要求以[产品验收基线](../product/README.md)为准。

## 共享行为与协议

./dev.ps1 shared-check 在普通 .NET 与 Python 环境执行共享 Core、Runtime、worker 帧协议及 Python worker 回归，不运行 WPF、UIA、Hook 或真实模型推理。Windows.Contracts 的可移植契约也由 shared-check 执行，保留非 Windows 覆盖；它仍归 tests/windows/TypeLay.Windows.Contracts.Tests，不能作为共享产品协议。

`tests/shared/TypeLay.Runtime.Tests` 用确定性串行宿主和受控输入/模型端口执行生产控制器，覆盖激活、异步结果隔离、草稿、直接替换和能力拒绝；设置测试使用真实临时文件。它们不证明桌面、真实模型或原子写入。浮层编辑入口提示复用共享展示与 Session 原有编辑判定：完整候选、可用源框及当前编辑确认同时满足才提示，短期接受许可到期不应使提示闪烁；翻译中、部分预览、IME 锁定和回源中保留原状态。Windows 的 OverlayRendering 回归另检查窄宽浮层中的完整文本可见性及已有状态覆盖优先，仍需在 Windows 桌面实际执行。`shared-check` 在非 Windows 环境构建并运行共享层，完整 Windows check/pack 保留。

设置保存回归区分真实持久化与配置变化通知：规范化后的有效值完全相同时仍写入文件并报告写入失败，但不重配会话；覆盖人工草稿、按需激活、练习进度及待派发直接替换。排除列表按原顺序和大小写比较，外部修改数组后再次保存仍应识别变化。任何实际设置变化与真实焦点离开仍执行原失效规则；这不是跨窗口保留人工稿的承诺。

共享生产规则与协议回归位于 tests/shared/；平台测试分别位于 tests/windows/ 和 tests/linux/。fixtures 保存受控宿主/资源，support 保存本平台多个测试消费的库；不为目录对称建立没有实际消费者的共享 fixture/support。

## 平台验证入口

- [Windows 测试](../platforms/windows/testing.md)：Windows 构建与桌面回归、Win32 适配器、Hook、真实 Broker、搬迁 ZIP 与物理微软拼音验收。
- [Linux 开发与验证](../platforms/linux/development.md#验证)：实际 tar.gz、CLI、安装生命周期、托盘、嵌套 niri 和目标桌面验收。浏览器、Fcitx、Qt 与 VS Code 的独立用例由各自现役说明维护。

受控端口、构建、进程、嵌套桌面、真实模型与物理桌面是不同层次的证据，不能互相替代。控制器状态与原生焦点是异步观察，断言等待实际用户结果，不以任一状态通知代替另一个进程已经完成操作。

测试失败保留命令、提交 SHA、平台、退出码和最小失败输出。只使用合成测试文本；TRX、临时包、日志与截图进入 artifacts/ 或 CI artifact，不将执行快照、阶段报告提交到源码树。

## 结果表述

在任务回复或 CI 结果中分别说明构建、行为/协议、分发/进程、模型推理、真实桌面交互与视觉结果，使用 passed/failed/not-run 并明确范围。结果属于执行时的提交，不能外推到其它版本；不新增仓库内验收报告或空白验收模板。
