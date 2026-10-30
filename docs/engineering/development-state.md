# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-30**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

当前远端开发头是 [`f0360c9`](https://github.com/DingJWuyu/Typelay/commit/f0360c93284a92c59a5316fd78b7e3e71621fe10)，[CI558](https://github.com/DingJWuyu/Typelay/actions/runs/41004277684) 已全部终态。共享与双端Index生命周期通过，Windows/Linux主链仍失败，不是稳定体验包。58文件发布已逐树/提交/远端读回核对；`main`、`trunk` 未改。下一成熟批只纳已审、适用验证完成的独立切片，不等待未完成新协议，也不用微批取消同头完整CI。

| 当前实际证据 | 未闭合问题与下一动作 |
| --- | --- |
| Windows App1269执行、1251通过、18失败；Runtime1474/Core302/Protocol28/WindowsContracts210及独立计时2例通过 | 设置8例区分公开旧label、模板属性与真实保存/反馈退化；保用户已入场Mode在导航后完成且不抢焦的原合同。普通source9例错用专项路由事件，未到业务链；owned1例真实handoff/稿Undo后More操作失败，构成有限首纵向RED但未实证精确LostFocus因果。两owner分别窄修，不改期限/输入门。 |
| Windows Hy完整15场景通过 | 冷启动公开14方向、两向实际预览、切回/移除/正常退出均通过，旧隐藏标签等待已关闭。不是其他模型所有语义质量认证。 |
| Windows格式仅余SetupRenderingTests.cs五行缩进 | 旧initializer错误已消除。五行纯排版已审、词法等价和正常编译通过。正式solution formatter本地受IPC/环境限制未完成，不重试受限动作；正常folder检查保实际文件计数，下轮Windows原正式门不变。 |
| Windows Browser失败在glossary按钮disabled；continuous在ordinary-source-control-exit失败 | Browser编辑器真实focus但实际资格原因未记录，增加同一DOM读取的有限固定reason枚举，不猜profile或放宽disabled。continuous唯一Ctrl+B到达时源观察160ms超过原150ms，随后旧epoch捕获不能授权；保单press/期限与身份，不把呈现Ready冒可执行grant。 |
| Notepad所有可执行场景通过，含普通退出；两项可信物理输入仍not-run | 重构前已明确的物理边界，不能用软件注入冒充，也不阻停止其他工作。 |
| Linux主构建、IPC/unknownIME、完整tray、Browser公开适配/路由及真实Browser链通过；Index12阶段通过且cleanup空、owned全退 | 长名单NextStep可见的tray修已进入远端通过；Index生命周期不是桌面/语义质量评审。 |
| Linux教程及真实VSCode链通过；settings已越旧应用入口，停在新owned clipboard窗口发现 | 子进程仍活，原同PID+精确title匹配未找到；增加现有一次Windows读取的first/last有界owned-only观察，保原15秒、唯一性和清理，不把未知map原因叫已修。 |
| GTK4合作第一例More前后同候选高44→42，主desktop仍尺寸门失败 | 真实natural帧及同candidate/buffer已记录；ScrolledWindow最小高度受原vscrollbar制约。追加原8帧内≤4节点公开CSS/几何，不measure/queue/加帧或放宽原等式。 |
| GTK3五种motion/展开收起已有通过子项，后继新长文Expand仍在click前租约过期；Qt ja仍选错地区face | 按本轮实际准备/观察时序查GTK3，保800ms。Qt独立显示实验两个fresh子进程崩溃，已修测试callback自身被替换时的生命周期，并先保baseline；不称字体策略有效或生产已修，下一正式SDK链验证。 |

### 当前本地后继与证据边界

本轮已发布源码已取得CI558实际结果，不能继续把失败场景标记为待首次运行。S02通用页出现8项公开行为/呈现回归，已按原接受事务与旧peer边界修正；普通source O05的9项WPF入口失败已定位到fixture错误的路由事件并窄修。owned首纵向已执行真实编辑/Undo与More输入后失败，构成公开RED；仅一个Keep→Tab→fresh Core/Bind纵向在独立候选实现，未完成整链验证，不扩散新协议或冒称全部确认交互完成。

下一成熟候选包含Linux S07完整页、Windows设置回归修与300ms模式thumb、普通source入口修、Qt实验callback生命周期修、GTK3展开首点真实分配就绪和有限诊断。S07本地完整公开合同与九张图已通过；pulse/shake和部分视觉细节仍未完成，专用本地入口不冒称CI已有覆盖。GTK3几何纯合同71项通过，真实原生重入仍待新CI。GTK4语言配置Host独立3项合同通过，但其Python/native纵向未完成，不混入本批。

continuous固定stock Edit场景只补既有公开快捷键就绪cue，与原FreshSource及ReadyPresentation合用后仍唯一按键、原150ms与原截止。它不修复译文已可见而源控制资格尚未发布的160ms间隙，不作为立即退出UX已修声明。最终本批独立树968输入编译App/Tests/Fixture/Practice全部通过，全Runtime1474/1474、零跳过；全部172个tracked Python的Ruff检查与格式检查通过。14份本批C#被正常folder工具实际发现，仅一份排版改变且Roslyn token/非布局trivia相同，verify为0/14；本地正式solution格式检查仍受限，正常folder工具必须核实际扫描计数，CI正式格式门不变。

Linux toast容器裁影已取得真实RED→修复证据：仅扩留白会吞底层点击，已拒绝该候选；最终局部绘制窗穿透、pill独立输入窗及按GTK实际核范围留白，真实halo/普通pill/Undo/hidden/旧peer和浅深四图均通过。pill距底28及原6秒不变；自然窗口边界裁切不是容器内部截影。这是本地真实UI证据，现CI没有自动执行新增toast专用入口。

2026-10-30环境替换后，旧未提交候选/图片/日志丢失；不能拿摘要或旧SHA冒充原件。当前源码从远端恢复，SDK10.0.401、锁定工具与32面字体已重新准备；旧session80867终态仍未知。新S08单页与默认nullable配置的完整邻近合同已实际通过，新S09按实际源重新审编译/取得CI图；不把它们说成丢失旧候选直接恢复通过。

当前CI合成原件在 `artifacts/ui-rebuild/ci558/`，已按GitHub digest核验：`contracts`、`windows-full`由root解析，`linux-diagnostics`、`gtk4-glossary`、`qt-early`及`index-linux`用于Linux定位；不执行下载包中的二进制。原失败保留，不靠重复运行或降低期限求绿。原型全量逐页状态与剩余实现缺口仍以[固定25项](../design/prototype-parity.md)为准，不把范围遗漏包装成只差测试。

## 当前负责人及下一动作

优先完整页面与真实用户流程。独立字体校准和模型研究不阻无依赖 UI；已通过子项保留，最终精确 head 的双端验收不降门。所有责任线目标配置为 `gpt-6-astra` / `xhigh`；恢复时沿原代理继续，当前工具没有重新报告实际配置，实际值标记未核实。

| 责任线 | 当前动作 | 完成依据 |
| --- | --- | --- |
| 集成 | CI558已终态；已合入S07成熟页、W设置回归修与有限fixture观察，冻结下一完整编译/发布候选 | 新源身份、独审、本地适用检查与精确 head CI |
| Windows设置 | S09已有真实图；S02的CI558回归修已审并纳下一整批，300ms动效待真帧；完整S03外观页独立实现，修UI仍不能选择Full的实质缺口 | 官方编译、公开操作和真实同偏好页面图 |
| Linux设置 | S08名单布局/完整tray及toast四图已通过；S07 v5完整合同与九图通过，pulse/shake仍未完；S03三态首纵向已绿，完整页面与局部Full动效继续 | 真实 host/GTK/AT-SPI、完整新锁字体与交互图 |
| Windows浮层 | 普通source确认CI558入口失败已窄修；owned公开首纵向RED成立，单Keep→Tab→fresh Bind独立实现中，未整链验 | fresh source/Core/Bind、原期限与零误写负控 |
| 合作浮层 | GTK4几何/Qt实验生命周期与Browser资格按558原件收敛；GTK4语言搜索Host首公开RED已成立，纯配置退休ACK单纵向在独立候选中，不改三端通用框架 | 实际 case、正反控与未改产品安全门 |
| root支持 | 已核CI558 Windows原件，Hy15场景真实通过；普通源退出fixture补现有公开就绪cue，真实preview资格间隙仍保留，不扩大输入租约 | 终态结构化结果，不能以阶段推进代通过 |
| 独立审查 | 对重建后实际 diff 做规范与安全双轴审查；旧候选审查不自动继承 | 新输入身份、实际源码及明确未验边界 |

## 模型能力边界

B01采用已明确的前瞻feature-delta准入：术语兑现、非术语语义不劣化，任何新增或加重的主体/授权/否定/数值重大错误拒绝；共同默认基线错误保留披露。固定证据中术语有真实收益，G07源锚定窄格式修复的18译文独立评审未见新增重大退化；G08两模式均安全结构拒绝，仍是可用性缺口，旧超时未解释。不是同新bits一次40全绿或所有方向质量认证。

正式执行profile绑定已通过；Current中英双向Glossary两行及切旧模型消费合同已发布并独审，focused四类293/293与正常构建后的全Runtime1425/1425通过，相关源码前后无变化。项目模式formatter因BuildHost socket权限失败、源码未改；无需该IPC的官方folder格式/verify与Python采集合同11项分别通过，不绕过权限；实际UI仍待。只登记已审的固定模型/行为profile中英双向Glossary，不扩散到style、combined、alternate或其他方向。缺失/未知profile保持普通Ready支持、高级closed。B02风格与换种说法仍未完成，不以门禁关闭或前端存在替代交付；无收益的额外保义提示已淘汰。最终预注册16源采集已终态：62实际业务请求、2项因普通结果失败而未发送Alternative；46译文、8次无替代表达、6次结构拒绝、2次预期alternative-unavailable，另2项依赖未发送，owned worker正常退出。总456.6秒、原2CPU/2.5GiB预算，无超时、重试或追加轮次。双方语义评审及解盲裁决完成：两方向Style/Alternative均未达到预注册效用标准，en-zh Natural还出现新增接收者任务重大退化。Current不加行，不追加prompt/配置试验。B02仍是未可靠交付的功能债，不归入第四类已允许仅前端项目；B01独立资格不变。业务拒绝与共同默认基线错误保留，不以采集终态自动开放能力。

## 持续约束

输入来源、焦点、IME、租约、接受资格、写回及Undo不因重构放宽。动态桌面/浏览器验收与重CPU构建/模型采集统一单owner排程；READY不等于运行，先明确GO，收到后立即启动已准备脚本；若准备未完立刻退回READY而不空占窗口，终态先报END再分析图片。每次执行记录命令、独立cwd/源码身份、session、期限、日志与exit文件；不确定结果先查，不盲重跑。

字体通过固定来源/校验和构建，最终产品完整离线，无运行时字体联网。32面包已发布，不把CJK和Source Serif未完成项当用户允许留空的第四类。GTK3语言菜单确无背景模糊能力，采用对应浅深实底保证可读，并明确这一材质不等同原型；不扩大为其他UI/交互降级。

保留既有Argos/Hy显式选择及方向；新空库存默认Index，不自动下载/替换，失败保旧代。模型权重不进入源码。继续同开发分支，不更新main、稳定版或体验推荐；最终按精确head双端结果及同偏好页面/状态对照交付。
