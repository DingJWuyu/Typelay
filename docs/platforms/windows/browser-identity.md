# 浏览器窗口身份探针

本页约束 `tests/windows/fixtures/TypeLay.BrowserIdentityFixture`。它是独立、metadata-only 的 Windows 实验，不接入 App、Broker、worker、产品支持标记或发行包。它不读取输入正文、不翻译、不写回、不获取 Tab 接受资格。它覆盖普通合成网页，不能代表 Chrome NTP（新标签页）中央搜索框的支持或修复。生产浏览器故障和 B-01/B-02/B-03 的结论不随此探针改变。

## 要回答的问题

MV3 service worker 没有页面原生视图；仅有浏览器 PID 无法区分同一进程的多个窗口。待验证的替代路径是：网页内的可信扩展 iframe 自己调用 `runtime.connectNative`，由 Chrome 将源 frame 的原生根窗口作为 `--parent-window` 传给 host。不能由网页提供 HWND，也不能从 PID 猜目标窗口。

当前可执行切片只做一个正常窗口的初次握手：service worker 的基线为 0；iframe 的值非 0，Win32 校验后等于插入前已观测的自有前台 Chrome 根 HWND；隐藏 iframe 插入及握手后，原合成 input/textarea 保持焦点。构建通过不能证明这条链成立。

## 身份和寿命边界

- 顶层 isolated-world content script 先建立内部 port。worker 验证 Chrome 给出的 sender extension ID、URL、origin、tab/window/frame/document ID、active document，再通过 `tabs.get`、`webNavigation.getFrame` 核验当前顶层 document。
- worker 允许同一 tab 唯一 pending bridge。扩展 origin 的 `bridge.html` 建立第二条内部 port；其 `parentFrameId` 必须是 0，`parentDocumentId` 必须等于仍存活的顶层 document。异步核验前先保留唯一位置；重复桥撤销整条绑定。
- bridge 不接收 `postMessage`、URL 参数或网页命令。只有私有 worker port 发出的单次随机 challenge 能启动固定的本次 host。host 仅接收该 challenge，回传启动参数、`IsWindow`、根 HWND、owner PID、自身 PID、随机连接 ID 及握手时的连接存活毫秒数。第二条 native 请求直接拒绝。
- 导航开始、tab detach/remove、任一内部或 native port 断连撤销旧绑定。attach 后只要求当前顶层重新报到，不沿用旧 challenge/document。生命周期监听已写入实验代码，重建行为仍必须在真实 Chrome 中单独验证。
- coordinator 独立确认 HWND 当前存在、根窗口及 owner PID 一致、owner 进程仍在本次专属 Job 中，且映像路径等于本次 CfT。Chrome 以 suspended 方式创建，先加入 Job 再恢复执行；清理终止本次 Job，在同一 5 秒预算内查询其 active process 数归零并等待原持有的 launcher 句柄 signaled 后才释放句柄，不按进程名或 PID 搜索后杀进程。
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
- `events` 中 `kind=navigation` 只读临时 profile 的 tabs，最多 12 次：记录目标/待导航目标匹配布尔、标签页数量、原始 `loading/complete/unloaded` 状态（其它值明确为 `unknown`）、固定页面类别与固定导航错误类别，不保存 URL/title。只在启动或实际 tab/navigation 事件时采样，不轮询或发导航命令。单次启动快照不能代表整个等待期；pending entry 也可能是初始导航失败后保留的地址，不能用它证明请求仍在加载。
- `windowDiagnostics` 是同一 35 秒阶段内的单次窗口观察：约第 31 秒开始，设 3 秒取消预算，新查询和图片发布前检查阶段绝对截止时间；最多两个自有顶层窗口，优先可见窗口。保存 visible/enabled、owner 是否同属本次 Job、owner enabled、modal candidate 和最多 200ms 的 `WM_NULL` 响应结果；没有标题或 UI 文本。modal candidate 只表示 enabled popup 与 disabled owned owner 的关系，不等于已证实某种对话；响应超时不等于已证明死锁。诊断不改变前台窗口、导航或验证门。同步系统调用和本地文件 I/O 不是可强制中断的实时操作，外层原 180 秒看门狗仍有效。
- 每窗至多一次 `PrintWindow`，调用方每窗至多等待 1 秒且受阶段剩余时间约束，尺寸不超过 4096×4096 且最多 400 万像素。只绘制已核验自有 HWND，不复制桌面；以无额外依赖的 BMP 保存到结果旁，例如 `result.owned-window-0.bmp`、`result.owned-window-1.bmp`。发布者须显式收集这些路径，JSON 只给固定状态和文件名，不嵌图片。后台任务持有独立复制的精确 process/Job 句柄，调用前后核对 HWND owner、原 process 存活和 Job。短查询门在清理前封闭，阻止迟到任务再查询 HWND；不可取消的在途 `PrintWindow` 本身不持门锁，返回后只能释放私有资源和丢弃字节，不能自行落盘。调用方仅在按时完成且身份仍匹配时创建旁文件；部分文件删除失败另记原错误、残留文件名和 cleanup 错误，不覆盖探针首错。
- `events` 中 `kind=diagnostic` 的固定阶段区分 worker 启动、内部 port 到达、sender 哪一项被拒、顶层 port 接受、焦点 ready、native 请求和回包。它们不会建立绑定，不改变原生验证门，也不引入浏览器控制通道。
- `coordinatorElevationType` 与 `browserBeforeResume`、`browserBeforeCleanup`、`browserAfterCleanup` 只保存自有句柄的 PID、退出状态/退出码、Job total/active、固定 token elevation 枚举及 Win32 查询错误码。清理前后必须分开；`TerminateJobObject(..., 2)` 产生的 2 不是启动失败证据。Job limit terminations 只计限制违规终止，不能当全部退出数。
- `cleanupErrors` 保留异常类型及 HRESULT，区分共享占用和其它失败；不覆盖首错。旁边 `.runner.json` 给出 build/browser 清理区域及 fixture 退出码，`.entry.json` 给出外层自有进程/Job 状态。目录删除失败或 Job 计数本身都不足以证明有 Job 外接力进程；Job 归零也不等于持有的进程句柄已 signaled。即使根句柄结束，仍不能据此宣称所有 DLL 锁已释放，删除失败继续如实报告。

固定 Chromium 155 的[自动降权条件](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/chrome/browser/chrome_browser_main_win.cc#L1023-L1074)包括 elevated token、feature 与启动环境；[实际实现](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/base/win/elevation_util.cc#L69-L140)可能启动另一进程，原 launcher 最终[返回正常退出码](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/chrome/app/chrome_main.cc#L224-L229)。这些是待观测分支，不是“退出 0 等于降权”或“必然逃 Job”的依据。独立 `user-data-dir` 已[排除另一个 COM isolated-browser 分支](https://github.com/chromium/chromium/blob/3ff7ac5a9224be9156d7f8703a06e22890aafd34/chrome/browser/win/isolated_browser/isolated_browser_support.cc#L298-L344)。诊断不改变 Chrome flags/启动权限，不扫描或终止 Job 外进程，不延长 35/180 秒上限。

命令行核验依据是固定 155 的 [Windows CommandLineToArgvW 解析](https://github.com/chromium/chromium/blob/155.0.8059.39/base/command_line.cc#L609-L634)、[GetArgs 到启动标签页](https://github.com/chromium/chromium/blob/155.0.8059.39/chrome/browser/ui/startup/startup_tab_provider.cc#L149-L165)和[URL 转换](https://github.com/chromium/chromium/blob/155.0.8059.39/chrome/browser/ui/startup/startup_tab_provider.cc#L297-L350)。当前受限参数引用规则不包含内嵌引号或尾反斜杠，不能在没有 argv 反证时把 GET 缺失归咎于引号。现有 `--no-first-run` 也会[使 FirstRunService 不 eligible](https://github.com/chromium/chromium/blob/155.0.8059.39/chrome/browser/ui/startup/first_run_service.cc#L49-L59)；其它启动阻塞仍须实际元数据证明，不自动接受对话或增添绕过 flag。

状态解释依据是固定 155 的 [tabs 状态计算](https://github.com/chromium/chromium/blob/155.0.8059.39/chrome/browser/extensions/extension_tab_util.cc#L1250-L1263)和[首次空白页失败后保留 pending entry](https://github.com/chromium/chromium/blob/155.0.8059.39/content/browser/renderer_host/navigation_controller_impl.cc#L5352-L5392)。窗口图与 metadata 仍无可行动原因时，结论停在 CI Chrome 启动前置阻塞；不继续增设探针、加等待或绕过安全设置。真实 NTP 与普通网页支持仍各自待验。

## 扩展前的门槛

先取得上述单窗口真实结果，再补实际对抗和生命周期场景；不以 mock、源码字符串测试或首握手替代：

- 同 browser PID 两个窗口分别返回正确且不同的 HWND，不能只证明两个 PID。
- 页面伪桥、重复 bridge、重复 native 结果/challenge 重放不能绑定其他 document。
- 导航、tab 移入另一窗口、断连必须使旧身份失效，再由新握手恢复；需覆盖挂起回包和 worker 重启。
- 正常及严格 CSP 下的可用性、input/textarea 焦点；富文本不在此最小实验范围。
- 品牌 Chrome 的批准加载流程，随后才讨论产品正文读取、物理 IME、Tab 所有权或写回设计。它们均未被此探针验证。
