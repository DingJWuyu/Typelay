# Windows 宿主与输入边界

共享规则与宿主端口见[工程架构](../../engineering/architecture.md)。以下内容仅属于 src/windows/ 下的 Windows 实现，不作为其它平台必须实现的协议或通用约束。

## 生产模块与平台内共享

TypeLay.Windows.Contracts 引用 Core，仅拥有 App 与 Broker 共用的 DTO、协议版本和 WindowsTarget 编解码。它可在普通 .NET 下测试，但其职责仍属于 Windows。

`TypeLay.App` 是 Windows 宿主。Windows/WindowsRuntime 组装共享运行时与 WPF 视图；WindowsInputService 把类型化输入操作转换为 Broker 请求，持有 HWND、焦点恢复和 IPC 期限。KeyboardHook/OnDemandHotkeys、Job Object、注册表启动项属于 Windows。UI 保持 WPF 展示与编辑，不直接写源字段。Infrastructure 的 AppPaths、ModelInstaller、SingleInstance 仍属于当前 Windows 分发/安装，不把 Windows Python wheel 选择冒充共享安装器。

`TypeLay.Broker` 引用 Core/Protocol/Windows.Contracts，在单一 MTA 线程上拥有 UIA COM 对象。Input 的 UiaInputProvider 不按应用白名单定义普通输入支持；Interop 只封装平台调用。生产入口是 Program 和标准输入输出管道，而不是 fixtures。TypeLay.NativeInput 的系统 Edit 协调写入仍是独立、受限的 Windows x64 路径。

`TypeLay.Launcher` 是 Windows 原生入口。发布时从 C 源码编译，调用发行目录 bootstrap/start.ps1；它不包含 SDK 下载和源码编译功能。

## 身份、时序与进程

WindowsTarget 保留 PID/进程启动时间、根/控件 HWND、provider、元素令牌与生命周期。Broker 协议 v2 隔离表示变化，v1 客户端/服务端不能混用；worker 帧与消息不变。

Windows 轮询、debounce、许可与执行期限不因分层改变。Hook 回调不解析不透明 ID，在发布租约时解码并保存本地 HWND。进程族继续由 Job Object 管理，不以共享 Process 类型替代；私有管道 token 不构成同用户恶意进程隔离。

## 分发契约

Windows 部署根包含 TypeLay.exe、app/、worker/、bootstrap/、assets/tutorial/、README 与第三方声明。AppPaths.Home 由启动脚本指向整个发行根；Broker 与 App 放在同一个 app/。开机启动指向发行根 TypeLay.exe。App 的安装流程在用户同意后准备运行时与模型，Windows wheel 选择不进入共享安装器。

## B-01：非原子写回与按键所有权

**B-01：非原子写回与按键所有权，发布阻塞。** UIA 读原文、比较与 SetValue 不是原子事务，写后核验不能恢复被并发覆盖的新文本。Hook 同步消费 Tab 后，异步 Broker 仍可能拒写。`ExperimentalWriteback` 默认关闭；测试开启时只使用已备份非敏感内容。关闭此项需要与目标文本变化可靠协调的写入机制及物理按键所有权证据，增加重读、缩短期限或盲目补发 Tab 均不足以解决。

按下时的短效接受许可与已消费按键的远程执行期限是两个边界，具体值以 Core 的 Limits 为准。派发前核验 Hook 输入/焦点版本，Broker 写前重读、期限检查与写后核验不能省略。浮层编辑按键与源字段输入版本必须区分，避免把译文编辑误判成原文变化。

## 通用 UIA 后端缺口

缺少可靠 IME 状态的普通字段只能提供只读预览，不能进入人工编辑或接受；值可能含未提交字符，不能当成已提交原文。缺少 ValuePattern 的富文本不能降级为整框 SetValue；未暴露可编辑文本接口的自绘控件仍缺后端。

SetValue 写的是计算后的整个字段，即使选区外文字保持相同，也须验证受控组件状态、撤销栈及事件语义。当前实现问题与修复顺序见 [Windows 待办](open-issues.md)，真实操作与受控证据见[测试说明](testing.md)。

## 修改底层接口时的依据

- UIA 组合态：[IUIAutomationTextEditPattern](https://learn.microsoft.com/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationtexteditpattern) 与 [GetActiveComposition](https://learn.microsoft.com/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtexteditpattern-getactivecomposition)。接口不可用时保留 Unknown，不推断已提交。
- 写回与按键：[ValuePattern.SetValue](https://learn.microsoft.com/dotnet/api/system.windows.automation.valuepattern.setvalue) 与 [LowLevelKeyboardProc](https://learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc)。Hook 不等待 UIA 或推理，许可的原子消费不代表目标写入原子性。
- IME 后备观测：[ImmGetContext](https://learn.microsoft.com/windows/win32/api/imm/nf-imm-immgetcontext) 与 [ImmGetCompositionStringW](https://learn.microsoft.com/windows/win32/api/imm/nf-imm-immgetcompositionstringw)。上下文取不到不能变成 Clear。
- 评估编辑锁协调：[TSF 架构](https://learn.microsoft.com/windows/win32/tsf/architecture) 与 [编辑上下文](https://learn.microsoft.com/windows/win32/tsf/edit-contexts)。这是待评估的后端方向，不是当前跨进程 UIA 已具有编辑锁或通用支持的证明。
