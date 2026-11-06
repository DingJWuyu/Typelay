# 浏览器窗口身份探针

这个探针研究扩展能否取得精确的 Chrome 原生窗口身份。它只处理元数据，不读取输入正文、不翻译、不写回，也不授予 Tab 接受资格。实现位于 [BrowserIdentityFixture](../../../tests/windows/fixtures/TypeLay.BrowserIdentityFixture/Program.cs)，不进入 App、Broker、worker 或发行包。

当前真实握手尚未成立。既有运行停在合成网页导航前置，窗口诊断未提供可行动的原因。默认工作流不再重复运行此探针；只有新线索能被本入口验证时，才在获准的临时 Windows 环境执行。普通 Chrome 产品回归与 NTP 问题仍各自保留。

## 要回答的问题

仅有浏览器 PID 无法区分同进程的多个窗口。实验比较两个来源：MV3 service worker 调用 native messaging 时的窗口参数，以及页面中可信扩展 iframe 自己连接 native host 时的参数。

当前只验证一个普通窗口的初次握手。通过条件必须同时成立：service worker 的 `--parent-window` 为 0；iframe 的值非零；Win32 核验后等于插入前已观察的自有 Chrome 前台 root；隐藏 iframe 插入与握手后，合成 input/textarea 仍保持焦点。网页不能提交 HWND，coordinator 也不能从 PID 猜窗口。

构建、JS 语法检查或 metadata 到达不满足这些条件。一次初握手即使成功，也不能证明后续导航、字段或写入权限。

## 身份和寿命边界

[worker](../../../tests/windows/fixtures/TypeLay.BrowserIdentityFixture/extension/worker.js)先验证顶层 isolated-world content script 的内部 port。身份来自 Chrome sender，并经 tabs 与 webNavigation 核对当前 tab、window、frame 和 document，不能信任网页声明。

同一 tab 只允许一个 pending bridge。扩展 origin 的 `bridge.html` 必须属于当前顶层 document；异步核验前就占住唯一位置，重复 bridge 会撤销整条绑定。bridge 不接收 postMessage、URL 参数或网页命令，只接受私有 worker port 的单次随机 challenge。

[native host](../../../tests/windows/fixtures/TypeLay.BrowserIdentityFixture/NativeHost.cs)只接收一次 challenge，返回启动参数、窗口存在性、root、owner PID、自身 PID、随机连接 ID 和握手时存活时间。第二次 native 请求直接拒绝。导航、tab detach/remove 或任一 port 断连撤销旧绑定；attach 后必须重新报到，不能复用旧 challenge。

coordinator 独立确认 HWND、root、owner PID、进程仍在本次 Job，且映像路径等于固定 CfT。浏览器挂起创建，加入 Job 后再恢复；清理只终止自己的 Job，并在同一 5 秒预算内确认 active=0 和原 launcher 句柄退出。它不按进程名或 PID 扫描杀进程。

每次运行使用独立临时 profile、随机扩展 ID 与 host 名。[注册所有者](../../../tests/windows/fixtures/TypeLay.BrowserIdentityFixture/OwnedRegistration.cs)只创建本次 HKCU 子键，已存在则拒绝；清理时只删除仍含本次精确 manifest 值的键。外来改动保留并报告失败。

[loopback 服务](../../../tests/windows/fixtures/TypeLay.BrowserIdentityFixture/ProbeServer.cs)只提供合成页面与带随机能力令牌的报告入口。报告要求本次扩展 origin，字段有白名单和长度上限。结果不保存正文、profile、网页日志或任意错误字符串。该私有通道不承诺隔离恶意同用户本地进程。

## 可核验的 Chromium 依据

实验固定到 [CfT 版本锁](../../../tests/windows/support/chrome-for-testing.json)中的 Chromium `155.0.8059.39`。该版本的 [MessageService](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/extensions/browser/api/messaging/message_service.cc)为 frame 来源传入 RenderFrameHost，为 service worker 传空指针。[ChromeMessagingDelegate](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/chrome/browser/extensions/api/messaging/chrome_messaging_delegate.cc)据此取得 native view。

子 frame 的 [GetNativeView](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/content/browser/renderer_host/render_widget_host_view_child_frame.cc)继续取 parent view。[NativeProcessLauncher](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/chrome/browser/extensions/api/messaging/native_process_launcher.cc)在 Windows 转为宿主 HWND，[LaunchContext](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/chrome/browser/extensions/api/messaging/launch_context.cc)把它写入 `--parent-window`。这条源码路径支持实验假设，实际 Windows 结果才证明当前环境的行为。

发送方身份合同见 [MessageSender](https://developer.chrome.com/docs/extensions/reference/api/runtime#type-MessageSender)和 [webNavigation.getFrame](https://developer.chrome.com/docs/extensions/reference/api/webNavigation#method-getFrame)。iframe 是否能加载、CSP 是否阻止连接，都由实际探针观察。不能修改 CSP、改用 offscreen 页面或禁用浏览器安全功能来取得通过。

## 执行

执行者需要交互式 Windows 桌面、仓库指定 SDK，以及已从官方固定来源取得的 CfT win64 ZIP。脚本不下载或安装浏览器，也不自动接入 CI。

固定版本、revision、地址和项目核定 SHA-256 由[共用版本锁](../../../tests/windows/support/chrome-for-testing.json)管理。官方版本 JSON 提供地址，不提供该 SHA；项目 hash 是已固定的下载身份。用锁中预期值校验，不能对任意未知 ZIP 临时计算摘要后就当成可信来源。

```powershell
./eng/windows/probe-browser-identity.ps1 -ChromeArchive C:\approved\chrome-win64.zip -ExpectedSha256 <已固定的64位SHA256>
```

[runner](../../../eng/windows/probe-browser-identity.ps1)核 ZIP 摘要、解压后的产品名称与版本，将来源写入结果旁的 `.browser.json`。浏览器阶段共 35 秒，外层自有 Job 限完整入口 180 秒，包含解压和构建。异常终止保留 `.entry.json`，不能把子进程退出当成注册和目录清理已经完成。

结果中 `scenario=single-window-initial-handshake` 的 `passed` 只代表本节定义的初握手。每次另列尚未执行条件。非 Windows 直接拒绝；crossbuild 不能代替桌面结果。

需要验证严格 CSP 时，在同一命令加 `-StrictCsp`。响应保持 `default-src 'none'; frame-src 'none'; script-src 'none'; object-src 'none'`，通过条件仍相同。加载受阻就保留失败阶段，不改策略。一次运行只覆盖所选 CSP。

本 runner 只接受 CfT。品牌 Chrome 的用户批准加载或商店安装必须另外验证，CfT 开发加载不能替代真实用户安装流程。

## 无事件或清理失败时如何定位

先读 `result.json` 的首个 `failure`，再按以下证据定位。诊断不会建立 HWND 绑定，也不会放宽 baseline 或焦点条件。

| 证据 | 可以回答的问题 | 不能推出的结论 |
| --- | --- | --- |
| `loopback` | 合成 GET、报告 OPTIONS/POST 是否到达，格式或 origin 是否被拒。 | 没事件不等于失焦，也不证明端口错配。 |
| navigation 事件 | 最多 12 次实际启动或导航观察中的页面类别、目标匹配与状态。 | 单次快照不代表整个等待期；pending entry 不证明仍在加载。 |
| diagnostic 事件 | worker、内部 port、sender 检查、焦点 ready 和 native 请求到哪一步。 | 诊断到达不等于完成身份握手。 |
| `windowDiagnostics` | 自有窗口可见、启用、owner、modal candidate 和消息响应。 | modal candidate 不证明特定对话，WM_NULL 超时不证明死锁。 |
| 进程与 Job 状态 | 原句柄退出码、Job total/active、token elevation 和查询错误。 | 清理产生的退出码不是启动失败原因，Job 归零不等于所有 DLL 锁已释放。 |
| `cleanupErrors` 与 runner/entry 旁文件 | 注册、目录、build/browser 或外层 owner 的具体清理失败。 | 删除失败不能单独证明 Job 外接力进程；空错误列表也不能替代退出证明。 |

窗口诊断仍在原 35 秒阶段内执行，约第 31 秒开始，取消预算 3 秒。最多两个已核身份的自有顶层窗口，WM_NULL 最多等待 200ms；不读标题或 UI 文本，不改前台或导航。

[OwnedWindowDiagnostics](../../../tests/windows/fixtures/TypeLay.BrowserIdentityFixture/OwnedWindowDiagnostics.cs)每窗至多调用一次 PrintWindow，调用方最多等 1 秒且受阶段剩余时间限制。尺寸最多 4096×4096、总计 400 万像素，只画自有 HWND，不截桌面。按时完成且身份仍匹配才保存 `.owned-window-0.bmp` 或 `.owned-window-1.bmp`，发布者须显式收集这些文件。

后台查询持有独立复制的精确 process/Job 句柄，调用前后重核身份。清理先关闭查询准入；在途不可取消调用返回后只能释放资源和丢弃字节，不能迟到落盘。部分文件删除失败保留原错误和文件名，不覆盖首错。同步系统调用与文件 I/O 不保证实时可中断，外层 180 秒约束仍有效。

固定 Chromium 的自动降权、命令行解析和首次导航状态只能提出待验证分支。退出 0 不证明发生降权，目录占用也不证明逃出 Job。没有 argv 或窗口原证据时，不添加绕过 flag、自动接受对话、扫描外部进程或延长等待。

已有窗口图和 metadata 未给出可行动原因时，结论停在 Chrome 启动前置阻塞。保持停止，不用更多探针堆叠替代新证据。

## 扩展前的门槛

只有初握手真实通过后，才讨论下一阶段，并分别取得以下证据：

- 同一 browser PID 的两个窗口返回正确且不同的 HWND。
- 页面伪桥、重复 bridge、challenge 重放和重复回包不能绑定其它 document。
- 导航、tab 移窗、断连、挂起回包与 worker 重启都使旧身份失效，新身份必须重新握手。
- 正常与严格 CSP 下分别验证 iframe 可用性和 input/textarea 焦点。
- 品牌 Chrome 完成用户批准安装，之后再讨论正文读取、物理 IME、Tab 所有权和写回。

富文本和 NTP 不属于当前初握手范围，任何阶段都不能据此扩大产品支持声明。
