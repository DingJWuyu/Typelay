# TypeLay Windows 预览

这是 Windows x64 本地输入翻译工具的开发预览，尚未完成通用输入支持与完整产品验收。

**重要限制：Windows 原有实验性 Tab 写回默认关闭。** Windows 通用 UI Automation 的读取、校验和写入不能组成原子操作，仍有覆盖并发输入的风险。测试实验写回时，只使用已备份的非敏感文本。直接替换与 Linux 合作输入路径各自依赖经过校验的目标能力，不通过开启开关扩大兼容性。编译和自动化测试通过不等于真实中文输入法、物理 Tab 或所有应用都已验收。

## 安装与启动

在本仓库 Actions 中打开 `TypeLay checks` 的成功运行，下载对应提交的 artifact，取出其中的 `TypeLay-Windows-preview.zip`，完整解压后双击 `TypeLay.exe`。不要单独移动可执行文件。`Start-TypeLay.cmd` 是备用入口。

升级前先从托盘退出旧 TypeLay，再把完整 ZIP 解压到新目录启动。App 与 Broker 必须来自同一个包；旧版本的输入组件会被连接校验拒绝，不会降级读取或写回。若“应用支持”提示连接检查失败，先退出并从完整安装目录重开；仍失败时重新解压完整包，不要只替换其中一个程序。

预览包自带 .NET 运行时，不需要 SDK、Python 开发环境或源码。第一次使用时，在设置中明确同意安装本地翻译组件与模型。模型安装后，推理在本机 CPU 上运行。组件和设置存放在 `%LOCALAPPDATA%\TypeLay`，不会作为源码或发布包提交。托盘菜单提供设置、实操教程与退出入口。

普通输入框是产品目标，不是按应用维护的支持名单。未知输入法状态只允许只读预览；密码、敏感字段、终端和代码编辑器不能通过弱化安全限制来“扩大支持”。

## 工作模式与按需操作

在“设置 → 常规 → 工作模式”选择模式，重启后保留。升级时没有此偏好的用户仍使用自动模式，已有应用排除项不会因新增偏好失效而被清除。

| 模式或操作 | 行为 |
| --- | --- |
| 自动模式（默认） | 聚焦后按原有规则显示语言胶囊、生成译文与接受译文。两个新增快捷键均不启用。 |
| 按需模式 | 正常聚焦、输入时不显示翻译 UI，也不自动运行翻译。 |
| 唤醒翻译 UI：默认 `Ctrl+Shift+F9` | 为当前输入框开启持续翻译体验；空框显示语言胶囊。正常编辑 TypeLay 译文不算离开；转到其他输入框后结束，返回原框也需要重新唤醒。 |
| 直接翻译并替换：默认 `Ctrl+Shift+F10` | 有选区时只处理选区，否则处理当前输入框文本。一次性操作，不显示预览、不需要 Tab、不发送或提交。 |

两个快捷键可在“设置 → 快捷键”独立修改并同时使用。非法、重复组合不会部分保存；被其他应用占用的组合不会启用，状态显示在设置中。唤醒、直接替换与接受译文均不会更改工作模式。

直接替换绑定触发时的原输入框和文本。翻译期间可切换输入框或应用，任务不会仅因焦点变化取消；结果只尝试写回原控件，不抢回焦点，不修改后来聚焦的输入框。原目标失效、内容冲突、输入法状态不明确或无法确认结果时，不进行有风险的重试。操作状态沿用托盘快速面板反馈。

**直接替换的当前兼容范围是 Windows x64 中通过运行时校验的原生系统 `Edit` 控件。** 需要未被应用替换窗口过程、身份仍有效且能完成原文校验的控件。不支持该事务的浏览器、富文本或自定义控件不会回退到 UIA `SetValue`、模拟按键或抢焦点写回。普通只读预览控件仍可按需唤醒，但这不赋予写回权限。实现依据与边界见 [ADR-0002](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/adr/0002-on-demand-and-direct-write.md)。

## 开发与验收入口

仓库中的[Windows 开发](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/platforms/windows/development.md)、[测试说明](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/platforms/windows/testing.md)和[架构边界](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/platforms/windows/architecture.md)提供源码工作方法。[Windows 待办](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/platforms/windows/open-issues.md)保留当前用户问题及 CI 失败证据。Windows 与 Linux 按[共同应用支持与完整体验](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/product/application-support.md)同步推进，平台底层实现不同不降低产品要求。

共同产品要求见[产品验收基线](https://github.com/DingJWuyu/Typelay/blob/trunk/docs/product/README.md)。发行包中的本页保留完整使用步骤；源码文档可在 [trunk 文档目录](https://github.com/DingJWuyu/Typelay/tree/trunk/docs)查阅。
