# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-30**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

CP25已发布为 [`a07d96e9`](https://github.com/DingJWuyu/Typelay/commit/a07d96e9ba939fd17d6a0508f5a8b335d2470ee6)，[CI566](https://github.com/DingJWuyu/Typelay/actions/runs/41052014046) 已全部终态。main/trunk未改。双端Index生命周期通过，不代表桌面或翻译质量认证。

### CI566 实际证据与剩余阻塞

Windows App 1404执行、1391通过、13失败。此前35个pressScale模板错误已消失，其中31项实际通过，4项改为后续关闭确认观察失败；另有Appearance动效2项、N01两项、旧Setup滚动条2项、owned返回3项失败。owned三项均已真实完成离开编辑器及SourceAck，但随后同步重画退休，尚未完成fresh Bind，不能开放Replace。FirstWake12项和旧Queue两分支本头通过，不以一次绿解释旧时序。Runtime1497/Core310/Protocol28/WinContracts210及独立下载超时两项全部通过；正式格式、controlled native、continuous与物理包smoke通过。

本头80分钟job的后链准入在前段1307秒时拒绝：剩3493秒不足原固定3600秒，Browser/Notepad/Hy没有执行。CP26按实际增长的合同规模一次校准外层85分钟=前段25分钟+后链60分钟；原模型、单场、输入授权与清理期限不变，不删测试或重试。新预算在下一CI才产生实际后链证据。

Linux完整原型托盘与快捷键公开合同通过。T02当前3秒默认焦点失败已作同请求原生呈现窄修，本地完整确认/取消/草稿/旧peer/托盘通过，111输入无漂，仍待CI窗口环境。隔离GTK4术语37/40通过，第38场More移动后按钮横移17px，原点命中Label，严格拒绝且未点击；后继修正actions-only状态投影与完整render不一致，不宣称已证明唯一几何根因。settings/tutorial在新应用页滚动后目标不可见；后继按真实公开scrollbar有限分页导航，保原15秒、焦点与完整clip门。GIMP仍有独立离屏操作缺口，本批未改。GTK4 O04未进入begin、首map后即unmap，下一仅固定map/unmap已有状态诊断；Qt reduced/safety-hide通过但主题前后几何仍红，后继收紧同一compact基线等待，不放宽几何相等。

### CP26 已成熟候选与验证边界

已精确并入独审候选，共同冻结编译已于12:55:56 UTC终态0：1004源码/字体输入无漂，App/Tests/真实NativeFixture/DesktopFixture/Practice/LinuxHost全部0，Runtime1501/Core310、9滚动合同及7预算CLI边界通过；11份C#实际扫描4份机械格式，Roslyn token与非布局trivia等价，END后才同步。正式solution格式与WPF仍待CI。本批包含：Windows owned return旋转事务修复、Appearance有限实际层缓存、sheet同一Closed receipt与原取消竞争、N01队列/显示进度测试前置和真实小窗无溢出合同。owned原公开回归实际RED渲染6→7，最小修后focused22与完整Runtime1501全部通过，503输入无漂；仍需真实WPF三项转绿，不能先宣称返回链已交付。

Linux真实下载进度已有五个合同和三surface中英GUI通过：当前文件bytes/total及attempt/seq严格隔离，不伪造总包百分比，暂停/未知/失败与旧回调保留；实际英文暂停按钮及说明已补并重新验证。About正文四语言主题×四工作状态与禁用更新控件真实通过。进度条原型6px/颜色/400ms显示动画仍为明确后继，不把数据接通称像素1:1。GTK4状态投影、Qt紧基线、设置公开滚动和已有生命周期诊断按各自边界合入。

Shader只新增官方固定FXC的非阻塞研究构建与反汇编原件，不接生产资源，不宣称180%饱和度已交付。Browser确认专用aux/Keep已有独立Host和真实DOM控制端口测试通过，包括默认焦点、Esc及同编辑器Undo；双端完整权限链与Replace尚未闭合，所有候选仍独立，未发布部分功能。

持续保留已通过页面子项，但双端完整25项尚未验收。S08整体模态材料/入场、owned完整Keep返回及Replace、Browser完整确认、GTK4 O04、其他页面Full动效均未关闭。B02风格/另述最终有界评估失败属于未交付功能，不混入第四类仅前端范围；Notepad两项可信物理输入为重构前明确的not-run边界，不能用软件注入冒充。

## 当前负责人及下一动作

优先完整页面与真实用户流程。独立字体校准和模型研究不阻无依赖 UI；已通过子项保留，最终精确 head 的双端验收不降门。所有责任线目标配置为 `gpt-6-astra` / `xhigh`；恢复时沿原代理继续，当前工具没有重新报告实际配置，实际值标记未核实。

| 责任线 | 当前动作 | 完成依据 |
| --- | --- | --- |
| 集成 | CP26共同树已END0，精确同步机械格式、最终身份审与正常发布；保CI566真实13个WPF及Linux7步红 | 新源身份、独审、本地适用检查与精确head CI |
| Windows设置 | 566四sheet关闭观察、Appearance两项、N01两项、旧Setup scrollbar两项回归；shader仅固定工具生成诊断，未接产品 | 公开操作、实际组合像素与同偏好页面图 |
| Linux设置 | CI566完整tray焦点与旧请求拒绝已真实通过；新T02在已显示dialog.is_active/Cancel原3秒门失败，优先同请求native呈现窄修；真实下载数据/三surface及英文说明已合CP26，本地172无漂；后继原型进度条样式与400ms | 真实Host/GTK/AT-SPI、实际字体与交互图 |
| Windows浮层 | 566三项已到SourceAck/CompleteAttempt，Delivered但未Bind；root公开return rotation回归RED→GREEN22+完整Runtime1501，已合CP26待实际WPF，不推进Replace | fresh source/Core/Bind、原down500期限与零误写负控 |
| 合作浮层 | 566 isolated37/40后More重排；actions-only phase不一致窄修。O04首trigger仍未paint；Qt已越reduced后theme几何等式新红，保原件 | 实际case、正反控与未改产品安全门 |
| 浏览器确认 | Keep默认焦点与Host Begin公开RED已证，首Begin路由GREEN；独立候选继续双端aux/Keep/Esc，未合生产 | 先安全设计与真实RED，再最小纵向 |
| root支持 | Windows回归分流与owned return rotation窄修；About指导正文已真实GREEN并合。所有新动态统一排程 | 终态结构化结果，不能以阶段推进代通过 |
| 独立审查 | 对重建后实际 diff 做规范与安全双轴审查；旧候选审查不自动继承 | 新输入身份、实际源码及明确未验边界 |

合作浮层 O04 后继限定于 GTK4：565曾有fixture日志未接inside的观察缺口，路由已在CP25恢复。566真实首trigger只有map后unmap、未paint且begin为零，尚未进入配置握手；CP26仅补原map/unmap固定记录，不新增hook/measure或更改输入。16场尚未完成，实际图仍需核264宽、冻结trigger左−4及上下gap6/底缘8/列表232上限；配置壳三态300ms入场与完整材质仍未完成，不抓屏伪造模糊、不改宿主全局设置，不横铺GTK3/Qt配置协议。

## 模型能力边界

B01采用已明确的前瞻feature-delta准入：术语兑现、非术语语义不劣化，任何新增或加重的主体/授权/否定/数值重大错误拒绝；共同默认基线错误保留披露。固定证据中术语有真实收益，G07源锚定窄格式修复的18译文独立评审未见新增重大退化；G08两模式均安全结构拒绝，仍是可用性缺口，旧超时未解释。不是同新bits一次40全绿或所有方向质量认证。

正式执行profile绑定已通过；Current中英双向Glossary两行及切旧模型消费合同已发布并独审，focused四类293/293与正常构建后的全Runtime1425/1425通过，相关源码前后无变化。项目模式formatter因BuildHost socket权限失败、源码未改；无需该IPC的官方folder格式/verify与Python采集合同11项分别通过，不绕过权限；实际UI仍待。只登记已审的固定模型/行为profile中英双向Glossary，不扩散到style、combined、alternate或其他方向。缺失/未知profile保持普通Ready支持、高级closed。B02风格与换种说法仍未完成，不以门禁关闭或前端存在替代交付；无收益的额外保义提示已淘汰。最终预注册16源采集已终态：62实际业务请求、2项因普通结果失败而未发送Alternative；46译文、8次无替代表达、6次结构拒绝、2次预期alternative-unavailable，另2项依赖未发送，owned worker正常退出。总456.6秒、原2CPU/2.5GiB预算，无超时、重试或追加轮次。双方语义评审及解盲裁决完成：两方向Style/Alternative均未达到预注册效用标准，en-zh Natural还出现新增接收者任务重大退化。Current不加行，不追加prompt/配置试验。B02仍是未可靠交付的功能债，不归入第四类已允许仅前端项目；B01独立资格不变。业务拒绝与共同默认基线错误保留，不以采集终态自动开放能力。

## 持续约束

输入来源、焦点、IME、租约、接受资格、写回及Undo不因重构放宽。CP25已将 Start-TypeLay.cmd 的仓库 blob 恢复为 .gitattributes 要求的规范 LF，工作树仍 CRLF；发布区分原工作树校验与按 path clean 后的 Git blob，不改命令或另开微批。动态桌面/浏览器验收与重CPU构建/模型采集统一单owner排程；READY不等于运行，先明确GO，收到后立即启动已准备脚本；若准备未完立刻退回READY而不空占窗口，终态先报END再分析图片。每次执行记录命令、独立cwd/源码身份、session、期限、日志与exit文件；不确定结果先查，不盲重跑。

字体通过固定来源/校验和构建，最终产品完整离线，无运行时字体联网。32面包已发布，不把CJK和Source Serif未完成项当用户允许留空的第四类。GTK3语言菜单确无背景模糊能力，采用对应浅深实底保证可读，并明确这一材质不等同原型；不扩大为其他UI/交互降级。

保留既有Argos/Hy显式选择及方向；新空库存默认Index，不自动下载/替换，失败保旧代。模型权重不进入源码。继续同开发分支，不更新main、稳定版或体验推荐；最终按精确head双端结果及同偏好页面/状态对照交付。
