# 共享架构与平台边界

改模块边界、进程启动、会话或目录时读取。TypeLay.slnx 是工作区入口；[ADR-0001](../adr/0001-workspace-and-delivery.md)规定工作区与交付，[ADR-0003](../adr/0003-cross-platform-runtime.md)规定共享运行时与宿主契约。产品要求从[产品验收基线](../product/README.md)读取，平台现状不降低要求。

## 生产边界

Windows/Linux 在 trunk 共用 src/shared/ 下的一份 Core、Runtime、Protocol、Python worker 与浏览器脚本。src/windows/ 和 src/linux/ 只拥有各自宿主及本机适配；eng/ 拥有构建，packaging/ 拥有运行时包装与安装，tests/ 拥有受控工具和断言。

`TypeLay.Core` 拥有 Session、AcceptanceGate、WriteSafety、字段安全策略、UTF-16 选区映射和展示状态的纯逻辑。只接收显式快照、时间与结果；不读取操作系统、环境变量、网络或磁盘。回归通过这些公开边界观察，不断言私有字段。

`TypeLay.Runtime` 引用 Core/Protocol，拥有 SessionController 的时序编排、设置持久化及 ModelClient 的本地模型协议。宿主注入输入服务/输入观察、串行事件循环、视图、数据路径和进程所有权。这里没有 WPF、Win32、UIA、Registry、Windows Broker DTO 或 provider 名称判定。模型、设置、平台事件通过公开端口进入同一会话所有者，不另建线程、服务容器或消息总线。

GTK、Qt 和共享浏览器沿用现有视图状态，在 `Translating`、正文非空且 `editorOwnsText=false` 时以现有弱化文字色显示旧译文，与 Windows 更新态对齐；新结果恢复正常色，编辑与回源人工稿保持原色，不增加协议字段或接受资格。

[本轮呈现检查](https://github.com/DingJWuyu/Typelay/actions/runs/39095613424)已通过 GTK3、GTK4 和 Qt 的实际控件颜色状态断言，以及浏览器浅色 320 像素场景的状态序列；Qt 真实模型编辑与回源链也通过。浏览器其余三个主题与宽度子例因测试编辑请求误用旧视图代次而失败，修正后的消费者尚待验证；整套平台检查仍有其它失败，不能将颜色检查作为完整输入验收。

TypeLay.Protocol 不引用 Core，拥有长度前缀帧和 worker 消息。Windows Broker 私有协议另属 Windows.Contracts，不是全平台共享协议。

TypeLay.Worker 是独立 Python 进程，拥有本地模型校验、文本结构保护与推理。源码及 worker 协议共享，原生 Python 依赖的目标平台发行与真实推理分别验证；worker 不自动下载、不监听端口，不向协议输出原始异常或用户文本诊断。模型安装与用户同意流程由各平台宿主负责。

模型方向目录由 worker 随包的单份 `model-catalog.json` 定义，Runtime 嵌入同一文件；两端安装器和设置选项消费可提供目录，当前仍只有中英双向。运行能力另由 worker 完成库存文件校验和模型加载后的 `hello` 返回，停止进程即清除，不能以安装标记存在代替验证。旧 `installed.json` schema1 仍要求完整双向，缺少方向字段时沿原文件校验兼容，成功加载后才确认原双向能力。schema2 读取支持明确的非空方向子集，逐项验证所选目录全部文件并全部加载后才报告能力；Auto 与同语、人工追加判断先于缺方向反馈，不做静默中转。安装默认仍准备中英双向，设置可选 schema2 的非空方向子集；补装取已提交方向与本次请求的并集，自检、实际握手与发布集合须精确一致。未就绪不删除已保存的语言偏好。目录不隐含其他方向或中转翻译，新增语言与各浮层选择器适配另行验收。

[Windows 架构](../platforms/windows/architecture.md)描述 WPF、UIA Broker、Hook、原生 Edit、Windows.Contracts 与安装；[Linux 架构](../platforms/linux/architecture.md)描述 GTK/Qt、浏览器、niri、合作输入和 XDG。平台内共用的实现留在本平台，不因多个本机模块使用就进入 src/shared/。

## 平台边界不变量

Core 的 TargetId 只比较 backend、opaque value 和 lifetime；各后端解释自己的身份与寿命。共享逻辑不解包本机句柄。

观察就绪不等于拥有 Tab，也不等于能写回。InputSnapshot 的两种写能力默认 false，生产后端校验通过后显式声明；共享控制器还要求 CanInterceptAcceptance 才发布 Tab 许可。未知 IME、敏感元数据、焦点/输入版本、时效、字段寿命及未知写入结果继续独立否决，不能用单一“支持平台”开关覆盖它们。

IInputService 的实现负责操作前重新确认目标与权限，不能把 WritePlan 当授权。无法安全写入返回明确拒绝；可能已经变更返回 OutcomeUnknown，不换目标、不模拟粘贴、不自动重试。直接写回保持绑定触发源，不通过前台 return/write 组合实现。缺失输入权限、可靠目标或几何信息时安全不可用，不伪造快照。

所有会话回调在宿主串行事件循环执行；平台回调先排队，不直接并发调用 Session。时间映射到 ISessionLoop 的单调毫秒域，输入/源输入/焦点版本分开保留。

控制器释放时停止循环并释放其输入服务/观察器；模型客户端、设置和视图由宿主拥有。平台子进程工厂保证创建失败无遗留、stderr 不阻塞且不保存正文、退出清理整个自有进程族，具体操作系统实现归宿主。

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
