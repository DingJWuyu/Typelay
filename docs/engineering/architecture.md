# 共享架构与平台边界

改模块边界、进程启动、会话或目录时读取。TypeLay.slnx 是工作区入口；[ADR-0001](../adr/0001-workspace-and-delivery.md)规定工作区与交付，[ADR-0003](../adr/0003-cross-platform-runtime.md)规定共享运行时与宿主契约。产品要求从[产品验收基线](../product/README.md)读取，平台现状不降低要求。

## 生产边界

Windows/Linux 在 trunk 共用 src/shared/ 下的一份 Core、Runtime、Protocol、Python worker 与浏览器脚本。src/windows/ 和 src/linux/ 只拥有各自宿主及本机适配；eng/ 拥有构建，packaging/ 拥有运行时包装与安装，tests/ 拥有受控工具和断言。

`TypeLay.Core` 拥有 Session、AcceptanceGate、WriteSafety、字段安全策略、UTF-16 选区映射和展示状态的纯逻辑。只接收显式快照、时间与结果；不读取操作系统、环境变量、网络或磁盘。回归通过这些公开边界观察，不断言私有字段。

`TypeLay.Runtime` 引用 Core/Protocol，拥有 SessionController 的时序编排、设置持久化及 ModelClient 的本地模型协议。宿主注入输入服务/输入观察、串行事件循环、视图、数据路径和进程所有权。这里没有 WPF、Win32、UIA、Registry、Windows Broker DTO 或 provider 名称判定。模型、设置、平台事件通过公开端口进入同一会话所有者，不另建线程、服务容器或消息总线。

GTK、Qt 和共享浏览器沿用现有视图状态，在 `Translating`、正文非空且 `editorOwnsText=false` 时以现有弱化文字色显示旧译文，与 Windows 更新态对齐；新结果恢复正常色，编辑与回源人工稿保持原色，不增加协议字段或接受资格。

[本轮呈现检查](https://github.com/DingJWuyu/Typelay/actions/runs/39097718674)已通过 GTK3、GTK4 和 Qt 的实际控件颜色状态断言，以及浏览器浅深色、320/520 像素四个场景的状态序列；Qt 真实模型编辑与回源链也通过。浏览器测试已明确传入当前视图代次，并在实际进入编辑后才输入人工稿，保留原源文字不变断言。双端搬迁后发行包也已验证扩展图标；整套平台检查仍有其它失败，不能将这些检查作为完整输入验收。

TypeLay.Protocol 不引用 Core，拥有长度前缀帧和 worker 消息。Windows Broker 私有协议另属 Windows.Contracts，不是全平台共享协议。

TypeLay.Worker 是独立 Python 进程，拥有本地模型校验、文本结构保护与推理。源码及 worker 协议共享，原生 Python 依赖的目标平台发行与真实推理分别验证；worker 不自动下载、不监听端口，不向协议输出原始异常或用户文本诊断。模型安装与用户同意流程由各平台宿主负责。

模型包目录由 worker 随包的单份 `model-catalog.json` 定义，Runtime 嵌入同一文件。目录 schema2 以包描述 backend、版本、下载地址和直接方向；正式目录包含原 Argos 中英双向与固定 revision 的 Hy-MT2 Q4_K_M 中文与日语、韩语、法语、西班牙语、德语、葡萄牙语互译的十二方向预览包。LegacyDirections、默认安装集合和原语言选项继续保留中英方向；设置接受目录已知的直接方向、Auto 与同语，不随安装或离线状态重置。Runtime 接受显式 schema3 加载计划。Linux 准备卡消费精确库存、当前选择与实际 hello 的包/版本/方向子集，提供明确确认的下载并使用、切换、修复及非当前包移除；Windows 保持原安装流程，仅呈现 Hy 当前未开放的说明。

模型包呈现复用 `TranslationPreparationView` 的 `PackageRows` 与 `CurrentModelText`。目录覆盖不是已安装或已加载方向；缺少当前方向时只提示，不更改语言或暗中切包。Linux 的可选语言取实际安装且平台可用的包方向，同时保留默认中英和已保存值；Windows 对已保存的 Hy 语言只读回显未开放，不能从新选项启用 Hy。

浮层语言呈现复用准备状态中的 `languageChoices`，GTK3、GTK4 和 Qt 按 code 映射现有菜单，浏览器与 Windows 显示真实语言名称；未知值不再回退显示为英语或自动识别。原生浮层缺失或不合法的选项数据时关闭语言修改。浮层保存复用现有 `traySettings` 的 source/target 局部更新，失败保留最后确认值，结果未知不声称保存成功；不再提交旧的全量设置。原生模块须由目标应用重启后加载同期版本。

界面动作携带完整、冻结的 format1 请求与 `expectedCurrent`，确认后仍校验视图是否过期，再由同一准备任务传给安装器；暂停、继续及重试不会读取最新控件重建请求。已有包库存不再使用旧方向修复入口；损坏库存不提供任何修改动作。Hy 可用说明来自 Linux 安装器的只读 native 文件闭包检查，真正准备仍独立重验；Windows 无下载或使用按钮。下载大小取嵌入的同一 `model.lock.json`，不将下载大小误作安装峰值空间。

目录版本与 `installed.json` 版本独立。`ReadInstallationSelection` 只读库存意图；schema3 的 packages 每项含精确 id/version、非空方向子集和包根相对 files 清单，允许库存备选覆盖同一方向。`ReadActiveSelection` 单独要求非空、无重复的 activePackages ID 列表，只接受原 Argos 包的非空子集或恰一个 Hy 包，拒绝混合家族、多 Hy 与方向冲突。schema3 包根是 `models/packages/<id>/<version>/`；原 schema1/2 的默认选择、文件位置与校验要求保持兼容。读取意图不校验磁盘，也不授予 Ready。

worker 先规范校验全部库存元数据，只扫描及完整 SHA 校验 active 包的精确文件集合，全部验证后才导入并加载相应模型；inactive 包不读取模型文件、不导入后端。schema3 Argos 还核对 metadata 的来源/目标/版本及安装器五项必需文件。普通 hello 不做推理，只在全部 active 加载成功后返回方向；schema3 另返回已加载包 identity，旧 schema1/2 hello 不变。停止或启动失败释放已加载资源，不以安装标记存在代替验证。Auto、同语和人工追加判断继续先于缺方向反馈，无静默中转。

Hy 生产路径仅支持 Linux x86_64、ABI3 的固定十二方向，实际选择的方向共享一个模型 handle；Windows 在加载前明确拒绝。打开模型前必须解析唯一版本化导出 `hy_open_v3`，旧 ABI2 adapter 不得进入加载或 hello。新安装登记完整十二方向；已有合法方向子集在选择和修复时保持原意图，hello 只报告实际加载子集。可信 native 与 CPU 闭包随 worker 发布，GGUF 和模型许可证属于安装包。生产门只消费随包固定锁和 production-adapter 清单，不依赖源码、header、原下载 ZIP 或测试报告；CPU 选择使用官方 score，并检查实际已映射库的精确路径与字节身份，拒绝异源预加载库。共享 native 源码在 `src/shared/TypeLay.Worker/native/hy/`，构建测试与生产共用，audit 仅 fixture 宏启用。构建入口和 runtime-only 交付见 [Linux 开发](../platforms/linux/development.md#hy-worker-可选构建)。

Hy 保留通用 8192 UTF-16 文本上限，并在结构分段前对整请求加 8 KiB UTF-8 上限；每次 native segment 固定 context 1024、最多192新 token，所有片段共用一个60秒 deadline。超限明确拒绝，无截断、无部分输出；只有真实 EOG 才返回结果。可信模板/控制 token 与原文 literal tokenization 分离，ABI仅对模型输入应用 CRLF→LF，孤立 CR 拒绝；外层保留原文与原换行结构。Hy --self-check 最多运行一个实际样句并独立报告全部已加载方向，不把语言质量矩阵放入启动；原 Argos 自检逐方向行为保持。

云端 Linux 已通过 ABI3 真实模型的生产长度帧 hello，精确报告 active 十二方向且不推理；随后一个中译法请求以真实 EOG 完成，进程资源已回收。该单句不代表十二方向质量验收。Runtime 与 Linux 宿主另有受控进程回归，包管理源码与共享呈现回归不代表真实 Hy 安装或 UI 已验收，也不代表 Windows 已实际运行。受限云机采样见 [Linux 开发](../platforms/linux/development.md#hy-worker-可选构建)。

[Windows 架构](../platforms/windows/architecture.md)描述 WPF、UIA Broker、Hook、原生 Edit、Windows.Contracts 与安装；[Linux 架构](../platforms/linux/architecture.md)描述 GTK/Qt、浏览器、niri、合作输入和 XDG。平台内共用的实现留在本平台，不因多个本机模块使用就进入 src/shared/。

## 平台边界不变量

Core 的 TargetId 只比较 backend、opaque value 和 lifetime；各后端解释自己的身份与寿命。共享逻辑不解包本机句柄。

观察就绪不等于拥有 Tab，也不等于能写回。InputSnapshot 的两种写能力默认 false，生产后端校验通过后显式声明；共享控制器还要求 CanInterceptAcceptance 才发布 Tab 许可。未知 IME、敏感元数据、焦点/输入版本、时效、字段寿命及未知写入结果继续独立否决，不能用单一“支持平台”开关覆盖它们。

IInputService 的实现负责操作前重新确认目标与权限，不能把 WritePlan 当授权。无法安全写入返回明确拒绝；可能已经变更返回 OutcomeUnknown，不换目标、不模拟粘贴、不自动重试。直接写回保持绑定触发源，不通过前台 return/write 组合实现。缺失输入权限、可靠目标或几何信息时安全不可用，不伪造快照。

所有会话回调在宿主串行事件循环执行；平台回调先排队，不直接并发调用 Session。时间映射到 ISessionLoop 的单调毫秒域，输入/源输入/焦点版本分开保留。

模型加载使用不可变 ModelLoadPlan 冻结精确路径、库存 schema 与 active 包期望，实际 hello 必须逐项吻合后才授予 Ready。旧路径 API 仅接受显式合法 schema1/2。schema3 请求语法按目录已知语言校验，Auto、同语和人工追加仍交 worker 按原顺序判断。

旧或新计划含 Hy 时，宿主先校验候选，再由 PrepareModelChangeAsync 等待练习退役 ACK、隔离尚未派发的请求和完成回调、撤销许可；已派发写入按原 receipt 收束一次。随后客户端撤回 Ready，取消旧交换并等待自有进程退出证明，才启动唯一候选。发布结果分 Confirmed、Unchanged、Unconfirmed；仅候选已回收且持有原安装 lease 确认指针仍为精确旧代时，允许重新启动旧代并验证 hello。未知发布一律不可用；Hy 串行切换未确认退出时保持不可用。Argos 热替换失败可续用已确认旧连接，但下一次加载须先获同一进程所有者的退出证明。锁序保持安装 lease、lifecycle、serial。

控制器释放时停止循环并释放其输入服务/观察器；模型客户端、设置和视图由宿主拥有。进程创建后清理未获证明时，工厂必须返回携带所有者的专用异常，客户端保留退出障碍；Dispose 本身不是退出证明。Linux 模型专用 supervisor 仅在无待收子进程且自有组不存在时确认退出；Windows 仅证明已分配 Job 与根进程退出，现有 Argos 启动前分配窗口不等同全局后代保证。Windows Hy 在计划创建前拒绝，开放前还需闭合该窗口并实机验证。stderr 持续排空且不保存正文。

## 分发与测试依赖

源码目录与部署目录不同。客户端打包从明确的运行时清单复制，不带 .git、skills、源码、测试、归档资料、用户设置或模型缓存。可选合作包保留许可证要求的对应源码、修改与生产重建输入，详见 [Fcitx 分发](../platforms/linux/fcitx.md)。改变部署路径须验证搬迁后的实际发行包，不能只检查编译。

共享测试准备仅留在 tests 内，不向生产代码导出额外的“方便测试”接口。平台端口有 Windows/Linux 生产使用者；不为未开发平台保留空项目或假实现。证据边界统一见[测试协议](testing.md)。

## 开放风险与安全边界

- B-01：Windows 通用 UIA 非原子写回与物理按键所有权仍是发布阻塞，定义及关闭条件见 [Windows 写回风险](../platforms/windows/architecture.md#b-01非原子写回与按键所有权)。Linux 合作路径通过不关闭此项。
- B-02：真实桌面验收不完整。独立网站、物理 Tab、完整中文 IME、多显示器/DPI 与视觉一致性需要对应用户路径的证据；构建、fixture、软件注入和模型自检不能替代。具体流程见[测试协议](testing.md)。
- B-03：通用输入后端不完整。未知 IME 不授予编辑/接受资格，不能当成 Clear；已知组合态只使用可靠的已提交前缀，状态恢复不自动把旧预览升级为候选。Windows 只读预览与 Linux 未合作路径的正文读取边界各见平台架构；未暴露安全编辑能力的自绘/富文本仍缺后端，补齐的是控件能力，不是应用名单。

字段元数据可能错误或缺失。敏感、密码、终端、代码等排除须先于正文读取，不能宣称对伪装字段零误判。控件事件、撤销栈及受控状态必须按实际写入路径验证，不能仅比对最终文字。

文本和模型输入/输出限制以 Core 与 worker 的实现为准，超过限制拒绝而非静默截断。结构保护分段保留 URL、邮箱、mention、编号、emoji 和换行，可能影响上下文质量；人工追加保留人工前缀，不保证全文语义重新融合。

worker 没有网络调用路径不等于 OS 网络沙箱；无正文日志不代表能控制系统崩溃转储或内存分页。发布仍须审查实际组件/模型许可、供应链及签名，见根目录 THIRD_PARTY_NOTICES.md。
