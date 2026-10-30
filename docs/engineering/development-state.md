# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-30**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。`trunk` 已合入重构前的 `438c497`；`main` 未改。此开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 验证状态与当前阻塞

CI563已实际终态：Windows App1350执行/1347通过/3失败，仅owned两个后继按键环节及确认宽动画锚点前置；Appearance公开交互20/20、动效14/14、新应用picker11/11均通过。正式格式、continuous与physicalTab通过，但80分钟job前置约1216秒超过20分钟准入16秒，固定60分钟后链因此未运行，不能算Browser/Notepad/Hy通过或失败。Linux严格构建/tray/S07、Qt原始完整probe、教程和真实Browser通过；GTK4第三次More冻结frame47后，真按钮实移44px；现门未绑定当前完整render，窄修仍待真实SDK证伪，原hover正确拒错点。可选Qt包被旧5项fixture清单拒绝当前8项，实际metadata共同hash无变化，4源共享输入清单修已取得匹配bundle真实RED→GREEN及全41文件单元通过。settings已越旧clipboard几何门，当前仅旧“theme:”名称消费者找不到新“外观：”标签，精确两消费者迁移待下一CI；可选包失败使真实GTK3/GTK4/Qt后链跳过，O04真实16场仍未运行。

当前远端开发头为 [`311c7ae`](https://github.com/DingJWuyu/Typelay/commit/311c7ae1cdb77f6d82457e4264f63f427ed71f90)，[CI563](https://github.com/DingJWuyu/Typelay/actions/runs/41030040632) 已全部终态。main/trunk未改。双端Index生命周期已通过；它不代表桌面或翻译语义质量认证。

### 成熟后继与验证边界

下一批仅收已独审的有限修复：Qt生产者/打包者共用原8项精确fixture输入清单并补font-order二进制校验；GTK4首个More点只在当前完整render的自然layout/paint后固定；Linux两处旧公开Appearance名称消费者迁移；Windows owned测试真实扫描码与前一菜单测试keyup清理；确认宽动画自有窗口初始几何；应用picker截图前原3秒内正常绘制机会。没有降低真实输入、焦点、期限、尺寸或最终精确命中门。

Qt文件打包合同已取得真实匹配bundle RED→GREEN，全41项通过。GTK4纯几何合同76/76通过、953输入前后无漂；不能借纯合同称原生40例或O04配置16例通过。本批991输入独立聚合已于09:42:00 UTC实际结束0，前后身份一致：App/Tests、真实NativeFixture Release、DesktopFixture、Practice及LinuxHost编译通过，Runtime1497与Core310全部通过零跳过；4份差量C#实际folder扫描0/4变更，Python Ruff/32字体锁通过。真实WPF和原生SDK仍待下一精确头CI。当前没有以新完整tray或整体sheet未完成实现阻塞这些成熟修复。

已保留的产品子项：Windows Appearance公开交互20项、动效14项和应用picker11项实际通过；Linux Appearance单页Full×OSfalse显示层wrapper真实12场与单次slider邻近通过，原会改变viewport的margin方案未合。此前Hy完整15场景、Notepad所有可执行场景和正常退出实际通过；Notepad两项可信物理输入仍是重构前已明确的not-run边界，不用软件注入冒充。

仍未完成：owned Keep真实两条后半链，Replace尚未实现；S08整体确认sheet的原生模态、背景材质及完整入场；Linux完整T01托盘方向搜索/模型首层合法动作；GTK4 O04原生完整交互/几何；其他页面Full动效与剩余25项原型差距。B02风格/另述最终有界评估未通过，属于未交付功能，不混入用户批准仅前端的第四类。B01仅固定Index中英双向Glossary按既定profile准入，不泛化组合/其他方向。

公开术语handoff重叠已取得5项及全Runtime1491 RED→GREEN；真实Browser后链尚待，不能说已证旧失败唯一根因。连续链公开cue已通过，但不修复译文可见到输入资格发布的间隙，原150ms门保留。

当前原件保留在 `artifacts/ui-rebuild/ci563/`；早期失败定位和候选RED/GREEN保留各自目录，不重写旧结果。实际GUI截图、原型逐页交互与剩余实现范围以[固定25项](../design/prototype-parity.md)为准。2026-10-30环境替换丢失过旧未提交原件，后续候选均重新取得源码身份与验证，不拿摘要冒恢复成功。

## 当前负责人及下一动作

优先完整页面与真实用户流程。独立字体校准和模型研究不阻无依赖 UI；已通过子项保留，最终精确 head 的双端验收不降门。所有责任线目标配置为 `gpt-6-astra` / `xhigh`；恢复时沿原代理继续，当前工具没有重新报告实际配置，实际值标记未核实。

| 责任线 | 当前动作 | 完成依据 |
| --- | --- | --- |
| 集成 | CI563已终态；将有限已审修复冻结为CP23，独立树共同编译后再一次发布。完整tray/sheet后继独立，不微推取消新CI | 新源身份、独审、本地适用检查与精确head CI |
| Windows设置 | Appearance20/20、Motion14/14、应用picker11/11实际通过；键盘截图绘制后重核小片已审，整体Settings内模态sheet另行实现 | 公开操作、实际组合像素与同偏好页面图 |
| Linux设置 | Appearance Full单页真实12场/slider邻近通过；完整T01托盘空库存首层动作RED→首绿，后继完整公开搜索/合法动作待审与真实GUI | 真实Host/GTK/AT-SPI、实际字体与交互图 |
| Windows浮层 | owned Keep两后阶段真实按键仍红；fixture输入/清理后继已审，等待同条真实转绿再推进Replace；确认宽动画保原同锚门 | fresh source/Core/Bind、原期限与零误写负控 |
| 合作浮层 | GTK4同render首点自然帧修纯76项通过待SDK；Qt原probe通过，精确包清单修待正式打包；O04完整原生16场未运行 | 实际case、正反控与未改产品安全门 |
| root支持 | 跟踪下一精确头Windows；原Name消费者与确认宽初始几何后继已提供，持续独立Spec及原件判断 | 终态结构化结果，不能以阶段推进代通过 |
| 独立审查 | 对重建后实际 diff 做规范与安全双轴审查；旧候选审查不自动继承 | 新输入身份、实际源码及明确未验边界 |

合作浮层 O04 后继限定于 GTK4：CI563严格构建已通过，但早期More及可选包前置阻止后续原生16场执行；已发布的语言 tokens 与公开Swap/Enter/空搜索恢复场景仍待完整SDK链，不能用Host7/bridge10代替原生结果。首实际图先核264宽、冻结trigger左−4及上下gap6/底缘8/列表232上限，目前未证几何偏差；随后补当前确未消费的配置壳三态300ms入场（scale .94、Y−4、opacity），关闭保持原型即时，600ms握手与旧源永久退休不动。材质的surface/边/双阴影及真实backdrop仍未齐，局部呈现可继续收口，不抓屏伪造模糊、不改宿主全局设置，不横铺GTK3/Qt配置协议。

## 模型能力边界

B01采用已明确的前瞻feature-delta准入：术语兑现、非术语语义不劣化，任何新增或加重的主体/授权/否定/数值重大错误拒绝；共同默认基线错误保留披露。固定证据中术语有真实收益，G07源锚定窄格式修复的18译文独立评审未见新增重大退化；G08两模式均安全结构拒绝，仍是可用性缺口，旧超时未解释。不是同新bits一次40全绿或所有方向质量认证。

正式执行profile绑定已通过；Current中英双向Glossary两行及切旧模型消费合同已发布并独审，focused四类293/293与正常构建后的全Runtime1425/1425通过，相关源码前后无变化。项目模式formatter因BuildHost socket权限失败、源码未改；无需该IPC的官方folder格式/verify与Python采集合同11项分别通过，不绕过权限；实际UI仍待。只登记已审的固定模型/行为profile中英双向Glossary，不扩散到style、combined、alternate或其他方向。缺失/未知profile保持普通Ready支持、高级closed。B02风格与换种说法仍未完成，不以门禁关闭或前端存在替代交付；无收益的额外保义提示已淘汰。最终预注册16源采集已终态：62实际业务请求、2项因普通结果失败而未发送Alternative；46译文、8次无替代表达、6次结构拒绝、2次预期alternative-unavailable，另2项依赖未发送，owned worker正常退出。总456.6秒、原2CPU/2.5GiB预算，无超时、重试或追加轮次。双方语义评审及解盲裁决完成：两方向Style/Alternative均未达到预注册效用标准，en-zh Natural还出现新增接收者任务重大退化。Current不加行，不追加prompt/配置试验。B02仍是未可靠交付的功能债，不归入第四类已允许仅前端项目；B01独立资格不变。业务拒绝与共同默认基线错误保留，不以采集终态自动开放能力。

## 持续约束

输入来源、焦点、IME、租约、接受资格、写回及Undo不因重构放宽。动态桌面/浏览器验收与重CPU构建/模型采集统一单owner排程；READY不等于运行，先明确GO，收到后立即启动已准备脚本；若准备未完立刻退回READY而不空占窗口，终态先报END再分析图片。每次执行记录命令、独立cwd/源码身份、session、期限、日志与exit文件；不确定结果先查，不盲重跑。

字体通过固定来源/校验和构建，最终产品完整离线，无运行时字体联网。32面包已发布，不把CJK和Source Serif未完成项当用户允许留空的第四类。GTK3语言菜单确无背景模糊能力，采用对应浅深实底保证可读，并明确这一材质不等同原型；不扩大为其他UI/交互降级。

保留既有Argos/Hy显式选择及方向；新空库存默认Index，不自动下载/替换，失败保旧代。模型权重不进入源码。继续同开发分支，不更新main、稳定版或体验推荐；最终按精确head双端结果及同偏好页面/状态对照交付。
