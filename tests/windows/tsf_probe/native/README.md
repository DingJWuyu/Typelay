# 原生只读实验源码（不是产品支持）

范围仅限本次独占创建的 System32 x64 经典 Notepad、唯一标准 Edit 与固定合成文件。生产 NativeInput、solution 及产品包不引用这些文件；独立 TSF workflow 仅按显式 Lifecycle/Read 隔离级别编译并执行所选操作，不接入产品验收路径。没有现成 Windows SDK/编译器时停止，不安装依赖。

`build_native.py --output <全新目录>` 仅在已经配置好的 x64 MSVC developer 环境编译，不加载 DLL、不执行 runner。固定源码已在隔离 Windows 上通过 MSVC 三目标编译、纯合同测试和 no-DLL Notepad 生命周期；单次只读模块尚未加载执行。

## 执行必须另审

runner 需要经 root/lead 审核的一次性 Windows VM 命令及 `TYPELAY_ISOLATED_TSF_NATIVE=1`；环境标签不是授权证明。入口有：

- `--protocol-selftest`：本机 ABI/header 长度及版本拒绝、纯期限/取消/父进程存活/目标销毁判定和长度越界，只证明这些函数；已执行通过，但不证明真实回调时序。
- `--lifecycle-only <artifact-dir> <绝对DLL路径>`：只创建合成文件及独占 Notepad，正常关闭并确认 owned job 归零，不加载 DLL。
- `--single-read <artifact-dir> <绝对DLL路径>`：在上述独占目标加载 test-only hook，提交一次异步只读会话。不能自动挂到只读环境 preflight。

路径必须由已审执行器提供。artifact-dir 必须已存在且不是 reparse point；合成文件随机命名并独占创建。目标不在 owned job、不是预期 System32 映像、转交其它进程或无唯一标准 Edit 均拒绝，不跟随新进程。无激活/创建 TSF context、无 TIP 注册、无输入法切换、无按键注入、无 SetText/SetSelection/写回。

## 读取与对象所有权

目标线程只取得已有 manager、自有 client ID 与已有 context；失败明确 unsupported。仅在 ASYNC|READ 会话 cookie 内读取正文、选区、组合态；使用通用 ITfRange，不使用仅 context owner 应调用的 ITfRangeACP::GetExtent。通过有界全文/前缀/选区读取计 UTF-16 code unit 长度，不按 Unicode 标量数计；固定正文含中文和非 BMP 字符。GetWnd 必须准确等于目标 Edit。读前检查父进程、取消、期限、context 身份与焦点；目标 WM_DESTROY/WM_NCDESTROY 永久失效。查询期间无文件操作、模型等待或 IPC 等待，只做有界本地读取与共享映射结果发布。出现组合立即拒绝正文读取；无组合不代表已通过中文 IME 验收。

会话对象在目标堆上，用 COM 引用计数，不引用同步 hook 栈；mapping/父进程句柄与普通模块引用保留至该独占进程结束，未使用永久 PIN。取消/超时不谎称请求已取消；晚到回调在读取前拒绝。析构与回调分别记录，不假设一定进入回调。

首原型不尝试运行中卸载。只有主动请求本次 Notepad 正常关闭、正常退出码及 owned job 活动进程归零，才报告 `removalProvenByProcessExit`。强制 owned-job 兜底只防泄漏，必定不是通过；进程提前退出也不能伪装为正常关闭。结果快照只在 owned job 归零后读取，避免与迟到回调/析构竞态。

IPC 不携带正文，只有有界状态、长度、选区和 fingerprint。fingerprint 明确是非密码学诊断；读取匹配必须同时通过与编译内固定合成全文的逐字符比较，不依赖 hash 碰撞假设。日志不含正文/窗口标题/账户身份。合成文件只在 owned job 归零后删除，失败保留证据，不清其它文件。

## 证据边界与待验证

本版本不是支持能力。当前 v3 已在隔离 Windows 完成真实编译、纯合同测试和 no-DLL 正常启动/退出；目标侧 v3 模块加载与栈元数据采集尚未执行；该结果不证明目标侧 hook、策略查询或读取会话成功；缺 manager/context、取消/过期、父进程退出、目标销毁与晚到回调等只读阶段仍未在 Windows 上验证。缺中文 IME 环境只能建立无组合时的单次合成文档观察，不能建立中文组合/共存/连续观察或任何 Tab/写回能力。

源代码内 guard 不是实测证据；后续具体只读生命周期反例的执行编排仍须单独审查，不使用模拟 TSF 替代。

## 失败返回与验证边界

- 入口缺隔离标签/显式开关、参数不合格：exit 2，未启动；环境标签本身不能替代审批。
- System32 进程/owned job/唯一 Edit/真实焦点不足：exit 2，无有效快照，不转交别的应用。
- 已有 manager/client/context 缺失、GetWnd 不精确、transitory、API 失败：unsupported/rejected，`readSnapshotValid=false`。
- 取消、到期、父进程退出、目标销毁或身份/焦点变化：读取前拒绝；不得将初始零值解释为无组合。
- 活跃组合、文本超界、全文不等于固定合成内容：拒绝，不输出部分有效快照。
- 异步回调未到：Pending/incomplete；期限不是取消 API，不释放还可能被使用的 target 状态。
- 正常关闭失败、强制兜底、owned job 未归零或合成文件删除失败：exit 2，不报告整体成功；模块移除只由进程正常结束且 job 归零证明。
- 符合全部条件：exit 0 仅 `observed-not-acceptance`，不是产品支持/IME/写回验收。

本地可运行 `python -m unittest discover -s tests/windows/tsf_probe -p test_build_native.py -v`，仅验证构建命令的帮助、参数与非 Windows 拒绝不会创建输出；没有模拟编译器或 TSF。

API 合同参考：[通用 GetText](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfrange-gettext)、[GetExtent 的 owner 限定](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfrangeacp-getextent)、[异步只读请求](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontext-requesteditsession)。链接用于约束源码，不是实际 Notepad 支持证明。

### 未证明的加载身份边界

SetWindowsHookEx 使用 TID，不接受持有的线程句柄/创建时间。runner 持有并核对目标进程及字段线程存活，observer 在读前再核实际进程创建时间；这些检查不能构成“检查与挂 hook 之间绝无 TID 重用”的原子保证。错身份必须不读，但不宣称 DLL 绝无可能先被装入重用该 TID 的线程。独占隔离 VM 也不是 API 原子性证明。首原型只允许全新隔离 VM、无用户应用/私人数据，身份失败即停止本轮，不重找 TID 或重试注入。即使在此条件成功，加载原子归属仍是生产采用未满足条件，不能靠无复现推广到用户应用。当前没有获准实际加载。

## 分级验证入口（每一级另审，不自动串联）

1. 已有 x64 developer 环境中：`python tests/windows/tsf_probe/build_native.py --output <全新绝对输出目录>`。只编译三个 test 二进制，不启动目标、不加载 observer。
2. 审核后执行 `<输出目录>/runner.exe --protocol-selftest` 与 `<输出目录>/test_outcome.exe`。前者仅 ABI/纯判定，后者是 runner 实用的生命周期报告合同反例：即使清理成功，启动/唯一字段验证失败也不得成功。这些不是实际 TSF/生命周期事件证明。
3. 独立审批后在新隔离 VM 设置明确开关，执行 `runner.exe --lifecycle-only <现存测试输出目录> <observer.dll绝对路径>`。不加载 observer，只验证真实 Notepad 独占启动/目标准入/正常关闭/job 归零/合成文件删除。未通过不得进下一步。
4. 再审核该结果及环境后，才能批准同样隔离条件下的 `--single-read`，不能由 exit 0 自动授权。

`test_outcome.cpp` 无 Windows 依赖，可用现成 Linux C++ 编译器在临时输出目录单独编译并运行，实际断言同一 `LifecycleObserved` 函数；这只证明报告逻辑。当前 v3 Windows runner/observer 已编译，但 v3 模块加载与 TSF 栈元数据采集未执行。`lifecycleValidated` 仅在输入就绪和唯一字段检查全部完成后置真，清理成功不能弥补先前失败。

## 发送与入口诊断

当前协议版本与注册消息名均为 v3。SendMessageTimeout 前记录单调时刻并 SetLastError(0)，返回后紧邻保存 GetLastError，再记录返回时刻。sendAttempted/sendReturned/sendReturn 区分尝试、API 返回与 API 成败；messageResult 仅在非零 API 返回时有效，否则为 null。有效消息结果 0 不是失败，API 返回 0 且 LastError 0 只能表示通用失败。API 结果不能替代异步目标观察：发送超时后目标可能已经完成有效读取；反之发送成功不证明读取发生。[官方合同](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw)

目标仅在 header/版本/大小与当前目标进程创建身份、线程、字段匹配后原子 OR OwnedEntry(1)；父进程创建身份与模块引用取得后加 ValidationPassed(2)；进入 Submit 加 SubmitEntered(4)，调用/返回 RequestEditSession 分别加 RequestCalled(8)/RequestReturned(16)。阶段位不清零、不覆盖，允许值为 0/1/3/7/15/31。现有 BasicGuard、TSF READ、3 秒期限及正文/选区读取不变。入口位 0 仍无法区分 DLL 未进入、映射失败或 header 拒绝，绝不向未验证映射写日志。完成 callback、完整快照与正常清理仍是原验收条件。

在 hook 前，使用 held 自有目标进程句柄只读查询签名与动态代码 policy，记录 query/known、数值错误及已定义标志位。任何查询失败停止；MicrosoftSignedOnly/StoreSignedOnly/MitigationOptIn 不兼容本次未签名实验 DLL 时停止。动态代码禁止位只约束动态生成/修改可执行代码，不等于普通 DLL 加载被禁止，也不是加载安全证明；不调用策略修改/放宽接口，不提权或绕过。[查询合同](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessmitigationpolicy)、[签名策略](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-process_mitigation_binary_signature_policy)、[动态代码策略](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-process_mitigation_dynamic_code_policy)

所有新增字段都是固定有界元数据，无正文/地址/句柄。报告消费者严格校验版本、阶段、类型与范围；Lifecycle 要求全部发送/阶段为未开始且原清理成立，不能将全零诊断当读取成功。新候选先经独立 Lifecycle 编译、selftests、no-DLL 验证并保留六个源码 blob；Read 固定经审的 v3 生命周期基线 `d83f04a2fead9c3dd2978e6d3ce7f949f2c6329e`；未来源码变更仍须重新验证、审定固定基线并另批单次 Read。没有自动重试或写锁升级。

## 单次 Transitory 栈元数据（v3）

仅在原 top 会话已通过目标 guard、GetStatus 返回 Transitory 时，拒绝前做一次有界元数据采集，仍以 Unsupported/Transitory 结束，readSnapshotValid 始终为 false。协议/消息名升 v3，不把既有 v2 Lifecycle 或 Read 报告当作 v3 证据。现 workflow 的 Read 锁定经审的 v3 no-DLL Lifecycle 基线；一次目标侧观察仍须另批精确 manifest 与 Read 标记，不因基线更新自动执行。

仅查询当前 focused document 的 GetTop/GetBase/EnumContexts，一次 Next(3) 只保留最多两项，第三项立即 release 并判 overflow。逐项仅做规范 IUnknown 身份比较、GetDocumentMgr 归属、GetStatus、GetActiveView/GetWnd；不读任何项正文、不申请 base 会话、不跨 document 搜索或访问 parent compartment、不 Push/Pop/Create/Activate，不等待/重试到期望栈顶。每个阶段检查原目标与期限，前后比较 focused document/top/base；释放全部元数据 COM 引用后再做 Guard，并以无 COM 调用的 BasicGuard 封口；失败整块清空为 attempted + MetadataGuard/E_ABORT。stable 只表示前后采样一致，不是栈不会发生 ABA 变化的原子性证明。

contextMetadata 只输出 attempted/complete、最多两项的 count、overflow、topEqualsBase、固定失败阶段与 HRESULT。stable 与各项 equalsTop/equalsBase/belongsToFocusedDocument/transitory/windowMatchesOwnedEdit 使用 0=unknown、1=no、2=yes；不输出对象指针、窗口句柄或正文。任何查询失败/归属变化/身份不稳定都清空部分结果为 unknown，仅保固定失败字段。complete 仅指元数据采集完成，不表示完整正文或读取能力；GetWnd 成功返回 NULL 时窗口对应仍 unknown。无采集时所有字段为初值、两项均 unknown，Lifecycle 必须严格符合这个默认状态。

如果单层 top=base 且 Transitory，此次当前栈无替代入口，停止这条探针线并转架构选择；如有不同 base，也只证明下层 main context 存在，不可绕过 top 读它，不自动继续探测或重跑。官方规定文本服务只与栈顶交互，且栈最多两项；Transitory 仅表示预计短使用周期，不能据此断言已经证明正文截断。[Push 合同](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfdocumentmgr-push)、[状态定义](https://learn.microsoft.com/en-us/windows/win32/api/textstor/ns-textstor-ts_status)

### runner 单次模块路径观察（非加载成功证明）

`diagnostics.moduleObservation` 是 runner JSON 的 schemaVersion 1 元数据，不修改目标 Exchange、协议 v3、注册消息或 observer。它只在原 SendMessageTimeout 的返回值、LastError、返回时刻保存后执行。`startedTick/endedTick` 是这次观察的单调起止时刻，须晚于或等于 sendEndedTick；并不是模块实际加载时刻。Lifecycle 全部为未执行零值。新 producer 每条 sendReturned 路径均紧接调用 Observe，消费者要求 attempted 与 sendReturned 相等，不接受发送已返回而观察全零的报告。

沿用 CreateProcess 返回并一直持有的独占目标 process 句柄，不重新 OpenProcess、不提高访问权限。用 runner 本轮已加载 observer 的完整模块路径作为比较值，不重新读取文件或 hash。K32EnumProcessModulesEx 固定256槽、至多一次，超容量不扩容不重试；最多256次 K32GetModuleFileNameExW，路径缓冲固定32768字符，失败或可能截断立即停止。完整路径逐字符比较，只产生保守的路径匹配观察，不按 basename 或模糊路径匹配。HMODULE 仅是查询值，不 CloseHandle，不向目标新增任何写入/消息。

每次查询前检查原3秒绝对 deadline 与同一进程存活，查询时间消耗原剩余窗口，不增加期限或补等待。系统调用是同步 API，没有独立硬实时超时保证；最后一次调用也可能跨过期限。原500ms发送、取消、退出与 job 归零后 snapshot 规则不变。模块查询失败不替代既有读取结果，也不改变任何接受条件。

只输出 attempted/completed/observerPathSeen、固定 stopReason、数值 win32Error 和时刻，无路径、模块名单、地址、句柄、标题或正文。stopReason：0未执行；1匹配路径；2扫描完未匹配；3目标退出或存活查询失败；4原期限到达；5本地模块路径失败；6本地路径可能截断；7枚举失败；8容量不足；9枚举长度无效；10目标模块路径失败；11目标模块路径可能截断。completed 仅对应1/2，表示本次查询流程完成，不表示原子快照；observerPathSeen 仅对应1。错误码只用于3/5/7/10，API失败且LastError=0仍保留失败分类。现消费者严格要求新增结构；旧报告保留为旧证据，不自动补零冒称新查询。

模块列表可在枚举或路径查询间变化，系统 API 也可能返回不准确信息。匹配只表示一次查询返回了相同路径，不证明 DLL 初始化、Hook 进入、mapping/header 成功或已加载相同文件字节。路径重用、同路径文件替换及映像身份仍未证明；现有 source-baseline 编译输入锁不能消除这些运行期限制。未匹配/失败/超容量也不能证明从未加载。即使 observerPathSeen=true，最终 OwnedEntry=0 仍是“没有已验证入口发布证据”，callback/完整读取门保持不变，不升级 IME、普通输入框或写回能力。

API：[EnumProcessModulesEx](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-enumprocessmodulesex)、[GetModuleFileNameExW](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getmodulefilenameexw)。runner 明确 PSAPI_VERSION=2，使用现有 Kernel32 的 K32 导出，不新增库/SDK。当前修改仅完成本地报告合同验证，Windows 编译、实际模块查询及 Read/Lifecycle 均需另审，不由合同测试推断。
