# 浏览器窗口身份探针

本页约束 `tests/windows/fixtures/TypeLay.BrowserIdentityFixture`。它是独立、metadata-only 的 Windows 实验，不接入 App、Broker、worker、产品支持标记或发行包。它不读取输入正文、不翻译、不写回、不获取 Tab 接受资格。生产浏览器故障和 B-01/B-02/B-03 的结论不随此探针改变。

## 要回答的问题

MV3 service worker 没有页面原生视图；仅有浏览器 PID 无法区分同一进程的多个窗口。待验证的替代路径是：网页内的可信扩展 iframe 自己调用 `runtime.connectNative`，由 Chrome 将源 frame 的原生根窗口作为 `--parent-window` 传给 host。不能由网页提供 HWND，也不能从 PID 猜目标窗口。

当前可执行切片只做一个正常窗口的初次握手：service worker 的基线为 0；iframe 的值非 0，Win32 校验后等于插入前已观测的自有前台 Chrome 根 HWND；隐藏 iframe 插入及握手后，原合成 input/textarea 保持焦点。构建通过不能证明这条链成立。

## 身份和寿命边界

- 顶层 isolated-world content script 先建立内部 port。worker 验证 Chrome 给出的 sender extension ID、URL、origin、tab/window/frame/document ID、active document，再通过 `tabs.get`、`webNavigation.getFrame` 核验当前顶层 document。
- worker 允许同一 tab 唯一 pending bridge。扩展 origin 的 `bridge.html` 建立第二条内部 port；其 `parentFrameId` 必须是 0，`parentDocumentId` 必须等于仍存活的顶层 document。异步核验前先保留唯一位置；重复桥撤销整条绑定。
- bridge 不接收 `postMessage`、URL 参数或网页命令。只有私有 worker port 发出的单次随机 challenge 能启动固定的本次 host。host 仅接收该 challenge，回传启动参数、`IsWindow`、根 HWND、owner PID、自身 PID、随机连接 ID 及握手时的连接存活毫秒数。第二条 native 请求直接拒绝。
- 导航开始、tab detach/remove、任一内部或 native port 断连撤销旧绑定。attach 后只要求当前顶层重新报到，不沿用旧 challenge/document。生命周期监听已写入实验代码，重建行为仍必须在真实 Chrome 中单独验证。
- coordinator 独立确认 HWND 当前存在、根窗口及 owner PID 一致、owner 进程仍在本次专属 Job 中，且映像路径等于本次 CfT。Chrome 以 suspended 方式创建，先加入 Job 再恢复执行；清理终止本次 Job，在 5 秒内查询其 active process 数归零后才释放句柄，不按进程名或 PID 搜索后杀进程。
- 使用独立临时 profile、每次随机扩展 ID 和独有 host 名。只创建本次 HKCU `Software\Google\Chrome\NativeMessagingHosts` 子键，已存在则拒绝。清理只删除仍只有本次精确 manifest 值的键；检测到外来变更时保留并报 cleanup 失败。
- 本地 loopback 服务只提供合成 HTML 和有随机能力令牌的 metadata 回报入口。回报必须来自本次扩展 origin，字段有白名单及长度限制。结果保存到 `artifacts/`，不保留正文、网页日志、profile 或任意页面错误字符串。

没有向网页开放 general-purpose bridge；本探针也不保护恶意同用户本地进程。跨进程消息和导航事件的时序仍是实验门槛，不能把第一次握手结果当成可写资格。

## 可核验的 Chromium 依据

以下链接固定到与实验浏览器一致的 `155.0.8059.39` tag；源码解释实验假设，真实 Windows 结果才证明行为。

1. [`MessageService::OpenChannelToNativeAppImpl`](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/extensions/browser/api/messaging/message_service.cc)：render-frame 来源传入对应 RFH；service worker 来源传入空指针，并检查 nativeMessaging 权限及策略。
2. [`ChromeMessagingDelegate::CreateReceiverForNativeApp`](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/chrome/browser/extensions/api/messaging/chrome_messaging_delegate.cc)：有来源时取 `source->GetNativeView()`，否则空视图。
3. [`RenderFrameHostImpl::GetNativeView`](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/content/browser/renderer_host/render_frame_host_impl.cc) 与 [`RenderWidgetHostViewChildFrame::GetNativeView`](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/content/browser/renderer_host/render_widget_host_view_child_frame.cc)：取 widget view，子 frame 继续取 parent view 的 native view。
4. [`NativeProcessLauncher::CreateDefault`](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/chrome/browser/extensions/api/messaging/native_process_launcher.cc) 与 [`views::HWNDForNativeView`](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/ui/views/win/hwnd_util.cc)：Windows 取视图宿主的 accelerated widget HWND。
5. [`LaunchContext`](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/chrome/browser/extensions/api/messaging/launch_context.cc)：将窗口句柄追加为 `--parent-window`。

身份字段的 API 合约见 [runtime.MessageSender](https://developer.chrome.com/docs/extensions/reference/api/runtime#type-MessageSender)、[webNavigation.getFrame](https://developer.chrome.com/docs/extensions/reference/api/webNavigation#method-getFrame) 和 [native messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)。extension frame 是否可用、页面 CSP 是否阻止其加载，都必须由探针观测；不能修改 CSP、使用 offscreen 页面或禁用安全功能来得到通过。

## 执行

需要交互式 Windows 桌面、仓库指定 .NET SDK，以及由执行者从官方来源取得并固定 SHA-256 的本地 CfT win64 ZIP。脚本本身不下载、不安装浏览器、不接入 CI。固定版本、revision 和官方地址由 fixture 的 `chrome-for-testing.json` 管理。

[官方版本 JSON](https://googlechromelabs.github.io/chrome-for-testing/155.0.8059.39.json) 给出版本和下载地址，不提供 SHA-256。执行者应从该 JSON 的官方 win64 URL 取得 ZIP，独立记录首次可信取得的 SHA-256，后续运行严格使用同一预期值；不能运行前对任意未知 ZIP 现算 hash 后就当成已验证来源。runner 校验 ZIP SHA-256、解压后的产品名称及固定版本，并把版本/来源/实际 hash 写到结果旁的 `.browser.json`。

```powershell
./eng/windows/probe-browser-identity.ps1 -ChromeArchive C:\approved\chrome-win64.zip -ExpectedSha256 <已固定的64位SHA256>
```

结果 `scenario=single-window-initial-handshake` 的 `passed` 仅表示这一切片。浏览器阶段等待总计 35 秒；runner 另用同一 fixture-only Job 实现约束完整子入口至 180 秒，覆盖解压和 build/restore。异常终止保存独立 `.entry.json`，不覆盖探针首错，也不把进程退出当成注册/目录清理已证实。不复用产品浏览器回归中固定 6 秒启动推测。每个结果列出尚未执行的门槛。非 Windows 执行直接拒绝，不能以 Linux crossbuild 或 JS 语法检查充当 Windows 验收。

相同脚本加 `-StrictCsp` 后，本地响应原样带 `default-src 'none'; frame-src 'none'; script-src 'none'; object-src 'none'`。它仍要求同样的真实握手；若加载受阻，保留失败阶段，不改 policy 或伪报成功。一次运行只覆盖所选 CSP 条件。

[Chrome 官方说明](https://groups.google.com/a/chromium.org/g/chromium-extensions/c/1-g8EFx2BBY/m/S0ET5wPjCAAJ)指出，Chrome 137 起 branded Chrome 去掉 `--load-extension`，CfT/Chromium 保留。此 runner 只接受 CfT。品牌 Chrome 的最终验收仍需真实的 Load unpacked 与用户批准，不能把 CfT 155 的结果等同于用户使用的品牌 Chrome 154。

## 无事件或清理失败时如何定位

诊断信号只缩小失败位置，不满足任何 HWND、baseline 或字段焦点门。先读 `result.json` 的首个 `failure`，再分别看：

- `loopback` 的固定计数区分 fixture GET 是否到达/响应、报告端点是否收到 OPTIONS/POST、Origin 缺失/不符、格式/schema 拒绝或已接受。只记录类别和数量，不保存 URL、原始 headers、被拒正文或页面错误文本。没有事件不能单独说明失焦。
- `events` 中 `kind=diagnostic` 的固定阶段区分 worker 启动、内部 port 到达、sender 哪一项被拒、顶层 port 接受、焦点 ready、native 请求和回包。它们不会建立绑定，不改变原生验证门，也不引入浏览器控制通道。
- `coordinatorElevationType` 与 `browserBeforeResume`、`browserBeforeCleanup`、`browserAfterCleanup` 只保存自有句柄的 PID、退出状态/退出码、Job total/active、固定 token elevation 枚举及 Win32 查询错误码。清理前后必须分开；`TerminateJobObject(..., 2)` 产生的 2 不是启动失败证据。Job limit terminations 只计限制违规终止，不能当全部退出数。
- `cleanupErrors` 保留异常类型及 HRESULT，区分共享占用和其它失败；不覆盖首错。旁边 `.runner.json` 给出 build/browser 清理区域及 fixture 退出码，`.entry.json` 给出外层自有进程/Job 状态。目录删除失败或 Job 计数本身都不足以证明有 Job 外接力进程。

固定 Chromium 155 的[自动降权条件](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/chrome/browser/chrome_browser_main_win.cc#L1023-L1074)包括 elevated token、feature 与启动环境；[实际实现](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/base/win/elevation_util.cc#L69-L140)可能启动另一进程，原 launcher 最终[返回正常退出码](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/chrome/app/chrome_main.cc#L224-L229)。这些是待观测分支，不是“退出 0 等于降权”或“必然逃 Job”的依据。独立 `user-data-dir` 已[排除另一个 COM isolated-browser 分支](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/chrome/browser/win/isolated_browser/isolated_browser_support.cc#L298-L344)。诊断不改变 Chrome flags/启动权限，不扫描或终止 Job 外进程，不延长 35/180 秒上限。

## 扩展前的门槛

先取得上述单窗口真实结果，再补实际对抗和生命周期场景；不以 mock、源码字符串测试或首握手替代：

- 同 browser PID 两个窗口分别返回正确且不同的 HWND，不能只证明两个 PID。
- 页面伪桥、重复 bridge、重复 native 结果/challenge 重放不能绑定其他 document。
- 导航、tab 移入另一窗口、断连必须使旧身份失效，再由新握手恢复；需覆盖挂起回包和 worker 重启。
- 正常及严格 CSP 下的可用性、input/textarea 焦点；富文本不在此最小实验范围。
- 品牌 Chrome 的批准加载流程，随后才讨论产品正文读取、物理 IME、Tab 所有权或写回设计。它们均未被此探针验证。
