# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-30**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

CP28已发布为 [`af03071c`](https://github.com/DingJWuyu/Typelay/commit/af03071cc686ab4228b54bee622a1e2dfbdf7f37)，[CI569](https://github.com/DingJWuyu/Typelay/actions/runs/41077943229) 已双端终态失败。31文件精确树91bb经独审、共同编译与远端回读，main/trunk未改；本地同步保971文件原字节并核clean。以下CI568是前一精确head终态原件，不冒新头结果；不为后继微批取消本轮。

### CI569 已取得的子项

精确头 af03071c 的 CI569 已双端终态，以下按实际原件保留通过与失败，不把子项绿色当完整交付。Windows原件已核：App1422执行1414通过8失败，Runtime1507/Core310/Protocol28/WinContracts210及独立2全部通过。Appearance15/15、sheet8/8、按钮120ms六例全部通过；步骤480ms为10/11，剩首次Show重入隐藏后目标窗口未active，未证明旧尾重新显示。五个owned初始mouse例统一缺对应up receipt，另普通source返回和FirstWake各一红。FirstWake真实capture102.89ms（managed-focus58.58ms），响应后15ms取出，原150ms期限超8ms安全拒；不延期限或预热。正式format/Python、native、continuous步骤已成功；Browser实际2通过/1失败/5未跑，术语按钮在当前复核未完成时disabled；fixture失败后16ms同身份复核确认，后继只按公开enabled等待评审，不改资格。Notepad五可执行通过/两可信物理输入未跑，Hy15/15、continuous14个子结果通过。Windows Index实际12/12、固定profile及cleanup均通过，不代表桌面或质量认证。

新头Linux严格构建成功；小artifact的完整托盘、S07公开录制、三处真实HTTP 5/11进度通过，圆端0/1/3/5/6/中值/满值、真实Atk、6px/400ms显示与退休合同也通过。Browser/routing/VSCode/rich/Firefox/Fcitx请求探针及隔离GTK步骤成功。GTK3完整21条实际通过，GTK3/GTK4各40合作合同通过（非真实模型质量）；原GTK3 OS减少动效属性不可用的子项仍not-run。Linux主剩六红：Quit、Settings、教程、GIMP、GTK4配置入口与Qt。Settings本次已显示明确保存失败，旧consumer误要求未知状态“未确认”，后继仅迁当前精确公开全文；教程隐藏scrollbar状态仍需与零range合法no-op分开。Qt新父子合成色已过，下一light偏好card高度89→99需定位；GTK4仍首layout内unmap、begin0，不冒配置完成。T02 page退休后dialog/intent/jobs均清理，但旧fixture在新用户请求前要求Settings自动active而失败；后继仅测试沿新一次公开show，原共享3秒不变，本机完整20秒合同已通过、127无漂，bare Xvfb仍待下一CI。生产不抢焦。

### CI568 当前结果

Linux严格原生构建已通过，上一头deprecated诊断API回归关闭。原件已证明完整托盘及关闭重开合同、真实HTTP当前文件5/11进度在三处界面/未知total/旧task拒绝通过。T02首次确认、默认取消和首show不复活已过，后续等待返回Settings活动窗口的3秒条件失败；Host正常退出，L按本次实际阶段定位，不延期限。LinuxIndex12项和六个固定profile通过；Linux主原件已终态：Quit、Fcitx请求探针、真实Settings、教程、GIMP、GTK3、GTK4配置入口及Qt主题八场失败；隔离GTK事务、原Qt输入探针和Firefox步骤通过。各场按本头原件定位，不继承旧根因。Windows已终态：App1405执行1401通过4失败，Runtime1507/Core310/Protocol28/WinContracts210及独立2全部通过；剩owned初始mouse、普通source确认返回、Appearance retarget和sheet hide后重开。Browser正式8场首次全部通过，含术语窗口真实新源退休，旧空capture交接修已取得整链证据；Hy15场通过，Notepad五可执行通过、两可信物理输入not-run。continuous前owned六段通过，普通源启动EM_SETSEL在100ms原限内超时，后续动态预览仍通过；C只读归因，不延期限。

### CI567 已终态基线与未关闭项

Windows早期实际App1405执行1400通过5失败，Runtime1501/Core310/Protocol28/WinContracts210全通过。普通owned Keep与PromptTab两项完整fresh Bind链真实通过；窄源初始mouse与新tap current claim仍红，另Appearance三项均未取得原中帧触发条件；其中resize尚未执行Width变更，不称退休语义退化。此前四sheet取消、N01两项和Setup两项本头不再失败。85分钟外层准入与pack已实际通过。Browser自动预览与真实Tab替换通过，随后打开editor的术语场30秒取消，未到More观测；实际旧空capture在焦点转Overlay后清除新编辑器，公开回归已RED→focused11与Runtime1507通过，真实Browser已在CI568完整8场通过。Notepad五个可执行场通过、两项原可信物理输入not-run，正常退出与cleanup通过；不能把它们算整体通过。

双端Index独立生命周期12项均通过，profile一致，仍不代表桌面或质量认证。Linux主已在构建失败：本批新增GTK4 map诊断调用正式SDK已deprecated的 `gtk_widget_get_allocation`，被原 `-Werror` 拒绝；全部本头Linux UI/桌面后链未运行。正式只读compute_bounds最小修复已独审合入后继，不关闭警告；本机无对应SDK，CI568正式严格构建已通过该修复。Windows主也已终态，不能继承旧头实际UI绿到本头Linux。

### 当前后继批边界

CP29独立并集于15:47:15 UTC终态0，原210+5秒限内完成：1009源码/字体输入前后及终态重核无漂，App/Tests/真实NativeFixture/DesktopFixture/Practice/LinuxHost均0警告/错误；Runtime1507、Core310零skip，全部Python Ruff/format、32字体、滚动13与预算7个CLI边界通过。6份C#实际扫描，3份机械格式经Roslyn token与非布局trivia逐字等价后才同步live。正式solution格式与实际平台行为仍须下一精确head CI。

本批只含成熟13文件差量：既有S08 A/B/C真实组合截图取证、owned up有限观察、Source返回完整原生身份谓词、步骤首次Show重入诊断、Browser同一公开术语按钮在原scene期限内等待enabled后唯一点击；Linux新用户退出请求前置、当前已知保存失败文案消费者、可见零range只读no-op及快捷键失败分类可选字段。Browser不等待私有ACK/不续票，零range没有scroll setter；所有源许可、IME、原期限与模型行为保持。最后三类诊断不称产品修复。

新shader资源/渲染、Linux录制pulse、Qt固定宽HFW候选、O03新公开RED及B完整键盘确认均独立未入，不为其阻本批已完成子项继续真实验收。双端完整稳定交付尚未完成。

### Linux 最近真实桌面证据与编排边界

最新原件为CI568。完整托盘、三处真实下载进度和快捷键公开合同通过。T02首次确认/默认取消通过，后继等Settings恢复active的条件失败，已补固定阶段诊断。Settings的SearchEntry实际AT-SPI角色TEXT与fixture旧ENTRY不符；教程需区分合法无滚动范围与失效scrollbar，不能删除焦点/完整clip门。GIMP断在横向完整clip，原件没有双方数值，只追加已有rect失败信息。GTK3仍须保持PNG捕获前后都处于原中帧范围，不能放容361px；GTK4配置入口未到begin，初map/layout内即unmap；Qt几何已通过，透明viewport单独grab未获得实际合成背景色。合作owner按这些具体边界做下一窄候选，原期限/输入权限均不变。

Windows外层85分钟=前段25分钟+后链60分钟，本头准入和pack实际通过，不再把旧预算拒绝列作当前产品失败。CP28当前已审合入Windows步骤480ms与按钮120ms局部呈现、sheet重开公开就绪等待、Appearance有限布局观察；Linux进度6px圆角/400ms显示在174固定输入、14场及7宽边界真实通过后合入。所有新WPF作者用例已共同编译，真实执行仍待下一Windows运行，诊断不是修复声明，B完整浏览器确认仍独立未合。

### 仍未关闭的交付边界

保留已通过页面子项，双端完整25项尚未验收。S08整体模态材质与入场实际组合像素、owned初始mouse/普通source返回及Replace、Browser完整键盘确认、GTK4 O04和其他页面Full动效均未关闭。B02风格/另述有界评估失败属于未交付功能，不混入第四类前端范围；Notepad两项可信物理输入仍不能用软件注入冒充。

Shader为非阻塞研究：CI568固定FXC曾因ps_2_0合法legacy sampler2D与可选strict模式冲突而无输出；CI569仅去不兼容可选模式后首次双编逐字一致352字节，status generated-twice-identical-not-rendered。尚未实际渲染/生产接线，不声称180%饱和度已交付。Browser确认专用aux/Keep/Replace/pending已有Linux Host和受控DOM各自公开RED→GREEN；Windows真实seam还有跨Begin异步返回边界待闭，整个候选独立，不发布半套功能。

## 当前负责人及下一动作

优先完整页面与真实用户流程。独立字体校准和模型研究不阻无依赖 UI；已通过子项保留，最终精确 head 的双端验收不降门。所有责任线目标配置为 `gpt-6-astra` / `xhigh`；恢复时沿原代理继续，当前工具没有重新报告实际配置，实际值标记未核实。

| 责任线 | 当前动作 | 完成依据 |
| --- | --- | --- |
| 集成 | CP27 ee7f/CI568双端终态已取原件，CP28 af03071c已发布，CI569已双端终态，CP29成熟修正共同验证通过待发布；不把诊断当修复 | 新源身份、独审、本地适用检查与精确head CI |
| Windows设置 | CI569 Sheet8/8、Appearance15/15、Button6/6；Step10/11，Show重入失活需精确阶段诊断；实际中帧合成仍待 A/B/C 后继 | 公开操作、实际组合像素与同偏好页面图 |
| Linux设置 | CI568托盘/三面真实进度/快捷键通过；caps-v4显示14场+7宽通过且174无漂已合；Settings公开TEXT角色与零range滚动合同已审合，继续catalog前置与Quit实际阶段 | 真实Host/GTK/AT-SPI、实际字体与交互图 |
| Windows浮层 | 本轮owned初始mouse及普通source返回两红，原pair dispatch receipt和失败身份诊断优先；保持原3秒/源身份/零误写门 | fresh source/Core/Bind、原down500期限与零误写负控 |
| 合作浮层 | 原生构建与隔离GTK事务已过；按本头GTK3中帧捕获/GTK4首次layout退出/Qt透明合成采样逐项收敛，不猜旧根因 | 实际case、正反控与未改产品安全门 |
| 浏览器确认 | 独立Host/DOM Keep/Undo/Replace/pending已绿，Windows异步seam未闭；已保存边界暂释slot给现有回归，未合live | 先安全设计与真实RED，再最小纵向 |
| root支持 | 暂时capacity错误，原代理未换模型；集成接管CI568 Windows原件，Browser8/8和Hy15/15已核，continuous前置EM_SETSEL超时待有限因果证据 | 终态结构化结果，不能以阶段推进代通过 |
| 独立审查 | 对重建后实际 diff 做规范与安全双轴审查；旧候选审查不自动继承 | 新输入身份、实际源码及明确未验边界 |

合作浮层 O04 后继限定于 GTK4：565曾有fixture日志未接inside的观察缺口，路由已在CP25恢复。566真实首trigger只有map后unmap、未paint且begin为零，尚未进入配置握手；CP26仅补原map/unmap固定记录，不新增hook/measure或更改输入。16场尚未完成，实际图仍需核264宽、冻结trigger左−4及上下gap6/底缘8/列表232上限；配置壳三态300ms入场与完整材质仍未完成，不抓屏伪造模糊、不改宿主全局设置，不横铺GTK3/Qt配置协议。

## 模型能力边界

B01采用已明确的前瞻feature-delta准入：术语兑现、非术语语义不劣化，任何新增或加重的主体/授权/否定/数值重大错误拒绝；共同默认基线错误保留披露。固定证据中术语有真实收益，G07源锚定窄格式修复的18译文独立评审未见新增重大退化；G08两模式均安全结构拒绝，仍是可用性缺口，旧超时未解释。不是同新bits一次40全绿或所有方向质量认证。

正式执行profile绑定已通过；Current中英双向Glossary两行及切旧模型消费合同已发布并独审，focused四类293/293与正常构建后的全Runtime1425/1425通过，相关源码前后无变化。项目模式formatter因BuildHost socket权限失败、源码未改；无需该IPC的官方folder格式/verify与Python采集合同11项分别通过，不绕过权限；实际UI仍待。只登记已审的固定模型/行为profile中英双向Glossary，不扩散到style、combined、alternate或其他方向。缺失/未知profile保持普通Ready支持、高级closed。B02风格与换种说法仍未完成，不以门禁关闭或前端存在替代交付；无收益的额外保义提示已淘汰。最终预注册16源采集已终态：62实际业务请求、2项因普通结果失败而未发送Alternative；46译文、8次无替代表达、6次结构拒绝、2次预期alternative-unavailable，另2项依赖未发送，owned worker正常退出。总456.6秒、原2CPU/2.5GiB预算，无超时、重试或追加轮次。双方语义评审及解盲裁决完成：两方向Style/Alternative均未达到预注册效用标准，en-zh Natural还出现新增接收者任务重大退化。Current不加行，不追加prompt/配置试验。B02仍是未可靠交付的功能债，不归入第四类已允许仅前端项目；B01独立资格不变。业务拒绝与共同默认基线错误保留，不以采集终态自动开放能力。

## 持续约束

输入来源、焦点、IME、租约、接受资格、写回及Undo不因重构放宽。CP25已将 Start-TypeLay.cmd 的仓库 blob 恢复为 .gitattributes 要求的规范 LF，工作树仍 CRLF；发布区分原工作树校验与按 path clean 后的 Git blob，不改命令或另开微批。动态桌面/浏览器验收与重CPU构建/模型采集统一单owner排程；READY不等于运行，先明确GO，收到后立即启动已准备脚本；若准备未完立刻退回READY而不空占窗口，终态先报END再分析图片。每次执行记录命令、独立cwd/源码身份、session、期限、日志与exit文件；不确定结果先查，不盲重跑。

字体通过固定来源/校验和构建，最终产品完整离线，无运行时字体联网。32面包已发布，不把CJK和Source Serif未完成项当用户允许留空的第四类。GTK3语言菜单确无背景模糊能力，采用对应浅深实底保证可读，并明确这一材质不等同原型；不扩大为其他UI/交互降级。

保留既有Argos/Hy显式选择及方向；新空库存默认Index，不自动下载/替换，失败保旧代。模型权重不进入源码。继续同开发分支，不更新main、稳定版或体验推荐；最终按精确head双端结果及同偏好页面/状态对照交付。
