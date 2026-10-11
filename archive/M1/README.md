# TypeLay Windows MVP — M1 工程起点

日期：2026-09-29。交付状态：**已建立工程并开始第一阶段；未完成 Windows MVP，未通过 M1 真机验收。**

这不是旧 PoC 的修复包，也不是另一个 HTML 产品原型。客户端技术路线建议为 **C# / .NET 10 LTS / WPF**。本次先交付不依赖 UI 的会话与安全内核、限定自有 fixture 的 Win32 适配器、测试入口与工程设计。**当前没有后台常驻客户端、可用翻译浮层、模型 worker、全局键盘 Hook 或托盘程序。**

## 先看这些文件

- `docs/ENGINEERING_PLAN.md`：范围、正式技术路线、线程/进程、模块边界。
- `docs/BASELINE_AND_POC_AUDIT.md`：实际资料版本、PoC 审阅、规格覆盖关系。
- `docs/MILESTONES.md`：分阶段交付与验收门槛。
- `docs/TEST_MATRIX.md`：自动测试与真实 Windows 测试的边界。
- `docs/KNOWN_RISKS.md`：不能通过延长超时、回放 Tab 或原型结果掩盖的问题。
- `docs/VERIFICATION_STATUS.md`：本次实际执行记录，不能当作 Windows 通过报告。

## 已写入本包的实现

| 部分 | 内容 | 证据状态 |
|---|---|---|
| `TypeLay.Core` | 状态机、翻译请求版本、人工编辑与返回确认、UTF-16、替换计划与校验、自己的写回抑制、Tab permit 判定、文案投影、浮层几何、受限诊断类型 | 源码已编写；C# 测试未运行 |
| `TypeLay.Platform.Windows` | 原生 EDIT 的跨进程读写；只接受测试 runner 启动并注册的 fixture；敏感/只读在读取前排除；未知写回结果隔离 | 源码已编写；Win32 实测未运行 |
| Native fixture / native tests | 真实 HWND 控件、全文/选区/caret/emoji/CRLF/拒写/零提交/计数器正对照；自有 IME 状态注入仅测接口契约 | 源码已编写；Windows 测试未运行 |
| Core tests | 自包含的 .NET 控制台测试 runner，不依赖第三方测试框架 | 源码已编写；不是已通过测试 |
| Browser fixture | 原生 input/textarea、敏感和不支持控件、提交计数、延迟修改/换焦点、composition 事件计数 | JavaScript 语法检查可单独运行；不是浏览器兼容通过 |

## 构建与运行

需要安装**最新维护补丁的 .NET 10 SDK**。`global.json` 的 `10.0.100` 是 SDK 选择下界，允许最新稳定 10.0 feature band，并不是要求退回旧 SDK。项目暂不引入第三方 NuGet 包，`NuGet.config` 不配置远程 feed。

在工程根目录执行：

```powershell
dotnet --info
dotnet build TypeLay.slnx -c Release
# 注意：这是控制台测试 runner，不使用 dotnet test。
dotnet tests/TypeLay.Core.Tests/bin/Release/net10.0/TypeLay.Core.Tests.dll
```

Windows x64、当前普通用户、未锁屏的交互式桌面：

```powershell
# 会启动独立原生 fixture，执行真实 Win32 控件读写；不会启动全局 Hook。
dotnet tests/TypeLay.Windows.Tests/bin/Release/net10.0/TypeLay.Windows.Tests.dll fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll
```

或使用同一个取证入口：

```powershell
# 构建 + 核心测试；原生测试标记为未请求
.\tools\validate.ps1
# 构建 + 核心测试 + 当前 Windows 原生测试
.\tools\validate.ps1 -Native
```

脚本生成 `evidence/runs/<run>/run.json`、阶段日志、执行前后源码清单。退出码 0 仅表示**请求的子集**成功；1 表示失败；2 表示阻塞/不能运行。不要调整全机 PowerShell 执行策略来运行未知代码；组织策略不允许脚本时可用上述逐条命令。

手动打开原生 fixture：

```powershell
dotnet fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll
```

直接用 Edge 打开 `fixtures/browser/index.html`。本页是测试宿主，没有翻译功能。物理 Tab、真实中文 IME、WPF 焦点必须在后续客户端接入后另做验收，不能从页面事件计数或原生 smoke 推断通过。

## 当前验证限制

本次生成环境是 Linux，未安装 .NET SDK，也没有 Windows 桌面。SDK 获取受网络限制，因此**没有执行新项目的 C# 编译、C# 测试、Win32 smoke 或任何真机交互**。代码可能仍有编译/运行问题；第一项 Windows 工程任务是构建并修复新工程本身，而不是修旧 PoC。

`python tools/verify-source.py` 仅验证项目文件路径、配置语法、参考文件哈希、有限源码字符串规则及浏览器 fixture 的 JS 语法。它不执行、转译或替代 C# 测试。

## 不可越过的边界

当前 Win32 adapter 只能接触已启动的测试 fixture。没有自动扫描桌面、通用网页白名单、剪贴板替换、键盘注入或云翻译。不要删掉这个限制后把代码当成系统级客户端。

当前 fixture adapter 的 `InputEpoch/FocusEpoch` 为 0，占位用于契约接线；尚无真实 OS 事件 epoch。真实 Hook 与 broker 观察器接入并验收前，不应给任意应用发放接受许可。

Win32 多次调用不构成跨进程原子事务。写回结果不确定时禁止自动重试，保留目标隔离。`AcceptanceGate` 是逻辑类，不是已经解决异步按键吞失问题的全局 Hook。

旧 PoC 的“部分通过”报告完整保留在 `reference/`，不变更其结论。新工程必须建立自己的证据。
