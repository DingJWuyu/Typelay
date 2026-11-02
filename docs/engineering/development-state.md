# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-02**，远端开发分支仍为 `work/linux-usability`，已核提交 [`9d763c5be218c42a5c5ef9aac6a1cb70dbc1347c`](https://github.com/DingJWuyu/Typelay/commit/9d763c5be218c42a5c5ef9aac6a1cb70dbc1347c)，tree `a5244d3dadc00b406c348718e8d9d87de7c4262f`。新 13 文件状态时界/桌面回归观察批已逐项核远端 blob/tree/parent/ref；Browser Editing 两端消费者、GIMP 导航校准等前序成果保持。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

本次环境恢复时，未发布 tree `05c838e0` 与独立 S09 候选不可读；现远端成果是在已提交源码与真实失败原件上重新实现、审验后发布，不继承丢失候选的通过。本地目录不承诺跨环境保留，成熟后继及时保存开发分支并跟精确 CI。

实施范围保持原型 I–III；IV 的 N06–N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型；自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

[CI580](https://github.com/DingJWuyu/Typelay/actions/runs/41409173021) 精确 head 9d763c5b 已终态 failure。范围、共享与双端 Index 通过，两主平台失败。Windows contracts/完整包、Linux small desktop 与终态日志已核大小/SHA；Linux 完整包 702,961,104 bytes 超读取上限，未取。局部通过不等于整端交付。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows contracts | App 实际执行 1446，1444 过、2 失败，22 未执行；Runtime 1565 通过。CI579 的 Appearance、owned 空稿 Replace、Hy receiver 三红本头均过 | 当前为 FirstWake 超过 150ms 和 LocalDraft unarmed-prefix 退休；此前无 xUnit 同桌面并发证据，不改全局调度 |
| FirstWake / LocalDraft | Wake 响应 146ms 已有观测、实际 notice 158ms，原过期拒绝正确；Broker 106.89ms，其中一次真实 managed-focus 58.8ms。LocalDraft 原 callback 撤销后当前 foreground 为 overlay，不能排除真实离开再返回 | 未证可删重复读取，不预热或放宽 150ms。候选测试先安装原 hook、再原单次激活并观察 exact HWND callback 处理完成的开始前置；不忽略外焦或伪造激活 |
| Hy 包生命周期 | Windows 正式 15 场全部通过，包含原托盘冷启点、同代新 worker、两向普通预览、切回 Argos、移除与退出 | 旧托盘失败根因未因此证明；真实链不等于质量放行，不扩模型研究 |
| Windows Browser | setup/自动预览通过；确认场景已完成 4 种 Keep、click/Enter 两种 Replace，第三 Space 在等待新译文阶段超时，后 6 场未跑。CI579 的完整 9 场为此前证据，不覆盖本头失败 | 沿本头 action→return→fresh→重译原件定位，不松 500ms/身份/IME。完整物理 IME、混版 GUI、safe-false 重试与 rich 变更体验仍未齐 |
| Linux Browser / GIMP | Browser DOM 与正式 desktop 通过；GIMP 正式包六场及清理通过 | 不冒全部普通应用/物理输入覆盖 |
| GTK3 / tutorial | GTK3 正式整链通过，旧第二次编辑 revision 拒绝本头未重现；教程完整新源、编辑回源、第二次 Verified Tab、Finish 持久化和重启链通过 | 单次 state 自洽修已有公开红绿与本头完整教程实证，但不能认定为 CI579 唯一因果；不再追加泛化时序诊断 |
| GTK4 配置 | 同实例 paint 的内容 264×227、child 252×215、含阴影外层 342×305 与完整不裁切均过；源已退休。第一 Tab 尚未发，search 已 focused，但实际 IME Clear/Composing 均 false | 原初始同一 wait 增加实际 Clear 就绪前置，保持 same opening/search、原 600ms/总界与发送前即时复核；Unknown 仍不发键。搜索/四生命周期后置尚未执行 |
| Qt6 | 正式 bundle、Ctrl/F7/Undo、safety-hide、语言选择、中文展开/settled 收起、编辑浅深/选区/Undo/两段接受均越过；新默认语言正负入口已随成功 probe 执行，独立结果 JSON 未在 small 包中。六主题与中文编辑浅深共 8 图 | 后继设置从 (F1,F24) 改 (F24,F12)，helper 先改接受制造冲突 (F24,F24)，生产正确拒绝。仅修测试合法保存顺序，不改主键校验或输入映射 |
| Notepad / Continuous | Continuous 全链通过；Notepad 5 场通过，接受/Undo 两项可信物理输入依赖未测 | 不以软件注入代替可信输入；Notepad 套件总体未通过 |
| S09 / S08 静态 | 已核 known/unknown 独立暂停图及同偏好 S08 指定图，未发现新的可独立静态修补 | S08 end 为真实组合、quit 为无 owner Content 图；材质、透明边缘、中帧与多 DPI 仍未闭 |

仍有效的邻近依据：Onboarding 退休 opening 的旧 Show 复活及 nested/inherited/属性策略/Close/异常已有真实 WPF 回归通过；owned Replace click/Enter/Space、空稿、held/repeat 后新动作与 Keep 稿/选区/Undo 已有 CI574 证据。Queue 后续通过未解释原超时根因。Settings 深色标题窄修已有有效九图与样式节点通过，不等于整页材质/动效 1:1。

## 当前候选与恢复动作

| 模块 | 当前工作 | 收束条件 |
| --- | --- | --- |
| 发布与集成 | 下一批 8 个代码/测试文件与 3 份产品文档已完成适用检查，尚未发布 | 冻结实际字节后保存，跟下一精确 CI；不把本地检查计入真实桌面通过 |
| Browser Space Replace | 原件确认受理后 9ms 在 Return 阶段 ContextLost，尚未进入 receipt/fresh/重译；候选只在原失败出口记录固定 stage/reason、Sent 与原已算 remaining | 下一原件区分未派发拒绝、明确 false/lost 与 unknown；Sent 仅派发尝试，不是页面 receipt。不增查询、权限、日志预算或 500ms |
| GTK4 输入前置 | 原初始同 wait 现等待实际 Clear，保同 opening/search 与发送前即时复核 | 真实后继键盘与失权矩阵下一运行；不把 focus/paint 或 Composing=false 当 Clear |
| Qt 设置录制 | helper 已按当前 pair 选择原两项保存的合法顺序，公开旧实现 RED/同测试候选 10/10 GREEN | 每项原 ACK/风险确认保持；无合法中间态提前拒绝，不添占位键。真实 Settings 后继下一运行 |
| Windows LocalDraft | 新 opt-in 回执仅在原 callback 最外层成功处理、最后 foreground 匹配本 owned HWND 后一次完成；fixture 在原单次激活回执后 Begin | 回执不授持续焦点或清空整个队列资格；原真实离开再返回仍退休。重入仅静审，实际 WPF 待验；FirstWake 无已证可安全优化点 |

Qt 目标搜索旧壳仍未恢复；现只读比较现有 child 补搜索与复用 Host 配置退休链的最小范围，未开始新实现。GTK3 当前只补现有语言子层的目标搜索与本地键盘选择，不冒完整原型方向头/交换/即时保存。后继按实际原件补有价值缺口，不重启模型研究或为忙碌扩诊断。

## 验证环境与未闭产品边界

本批 8 个代码/测试文件已完成独立工程与需求审查。LocalDraft 三文件先完成 folder-only whitespace 与 App.Tests 构建；合入 Browser 诊断后，两新 C# 文件的 folder-only 检查、三个 Python 文件 Ruff/格式/AST、diff、App.Tests 与 DesktopFixture Release 增量构建全部通过，两个构建均 0 warnings / 0 errors。最终检查窗口 54.98s、984 个源码/配置输入无漂移，检查进程已退出；原唯一 C# 布局失败保留，修订仅换行缩进，反向替换精确恢复已审字节。

Qt helper 使用相同最终测试：原 helper 的旧 5 项通过，新 5 项出现失败；候选 10/10 通过。受控端口证明两次真实配置操作的保存顺序与拒绝条件，不是 GTK 投递实证。GTK4 只改原测试就绪谓词；Browser 只投影原拒绝决定，不称根因已修。当前共享 C# 与 Linux 生产均与 9d763c5b 相同，本批不重复无改动共享套件；CI580 Runtime 1565 通过不能代替新 Windows 消费者的真实运行。本机未运行 WPF/GTK/Qt GUI，下一目标平台结果仍是闭环条件。

- 本地 SDK 10.0.401 已按官方元数据核完整 SHA512 恢复，Ruff 0.14.0/fontTools 4.61.1 按现锁安装；从已核正式包恢复 32 面字体并通过官方 verify，native DLL 核 SHA。工具恢复不是产品测试。本次 Runtime.Tests 与 LinuxHost 的 locked restore 已过，未改依赖或锁文件；NuGet 使用官方环境变量指定工作区缓存。
- 当前没有可用 DISPLAY/WAYLAND 或 GTK/Qt 开发 SDK、真实 WPF。本地可执行正式共享测试、受控 Python/stdio、folder 格式检查与托管编译；DOM、GTK/Qt 与 WPF 运行依赖目标平台。Linux 完整 artifact 超过可读取的大小上限，当前采用独立 desktop/probe 原件。
- 完整双端逐页/状态/真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭；S08 材质/中间组合像素、多 DPI 与跨页 Full 动效仍有差距。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒原型完整呈现；下一真实结果决定后继修复。
