# 外部 API 依据与证据边界

以下公开上游资料用于当前实现设计。查阅文档或版本元数据不能替代实际编译、下载、模型推理或 Windows 实机验证。用户的冻结规格/原型/PoC 原文见 reference；本文件属于额外工程研究。

- Microsoft UI Automation IUIAutomationTextEditPattern / GetActiveComposition：明确活跃 composition 范围接口。本实现使用原生 COM 补足托管 UIA 暴露，拿不到可靠状态即 Unknown。
  https://learn.microsoft.com/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationtexteditpattern
  https://learn.microsoft.com/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtexteditpattern-getactivecomposition
- Microsoft UI Automation ValuePattern.SetValue 与 TextPatternRange：目标定向写值/选区定位接口。文档没有赋予本实现跨调用 compare-and-swap 原子性；B-01 保持开放。
  https://learn.microsoft.com/dotnet/api/system.windows.automation.valuepattern.setvalue
  https://learn.microsoft.com/dotnet/api/system.windows.automation.text.textpatternrange
- Microsoft LowLevelKeyboardProc：回调必须迅速返回，不能等待 UIA/推理。当前 Hook 采用预先生成的许可，仍存在异步拒写时按键已被消费的问题。
  https://learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc
- Windows Job Objects：用于结束子进程和隔离故障生命周期，不自动等价为安全沙箱。
  https://learn.microsoft.com/windows/win32/procthread/job-objects
- IMM 上下文与组合长度：[ImmGetContext](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immgetcontext)、[ImmGetCompositionStringW](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immgetcompositionstringw)。2026-10-10 的正式 Broker 实测部分普通 Edit 上下文为 0；这是本机观察，不推导成所有应用都无法观测 IME，也不把未知状态改成 Clear。
- 通用编辑后端研究：[TSF 架构](https://learn.microsoft.com/en-us/windows/win32/tsf/architecture)、[编辑上下文](https://learn.microsoft.com/en-us/windows/win32/tsf/edit-contexts)、[文本存储](https://learn.microsoft.com/en-us/windows/win32/tsf/text-stores)。TSF 的文本服务是注册到框架的进程内 COM 服务，编辑会话协商文档读写锁。该机制值得评估目标文本版本与编辑的协调，但不能当作当前跨进程 UIA 已拥有锁，也不能在尚未实现并验证前宣称覆盖所有输入框；B-01/B-03 保持开放。
- CTranslate2 CPU 推理与安装：本地机器翻译执行路径和 Windows 运行库要求。
  https://opennmt.net/CTranslate2/python/ctranslate2.Translator.html
  https://opennmt.net/CTranslate2/installation.html
- GitHub 官方 .NET CI：用于随包工作流，不代表工作流已触发或通过。
  https://github.com/actions/setup-dotnet
  https://docs.github.com/actions/tutorials/build-and-test-code/net

查阅记录更新至 2026-10-10。精确版本与分发说明见 THIRD_PARTY_NOTICES.md。
