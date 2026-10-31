# 当前开发状态与安排

本页只记录当前项目状态；产品要求以[产品基线](../product/README.md)和[新界面原型](../design/README.md)为准。

## 当前开发范围

截至 **2026-10-31**，正在 `work/linux-usability` 重构原生 Windows WPF、Linux GTK 设置与合作浮层。本批成熟候选基于 [`80185abd`](https://github.com/DingJWuyu/Typelay/commit/80185abd6b34ce06b99a0ad32ac9372da4dd6cfc)，最近完整平台 CI 仍对应该父基线；本页随新批次发布后不把父基线结果外推到新提交。恢复基线为 `94c1c2f9ac3665985dbe4d7493f2b6b156a8c200`。`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`，`main` 保持 `09fe613a606073876483b51446211030a66a9597`；本开发候选不是稳定发布或已验收体验包。

既有功能、新术语管理、按应用设置、即时保存、外观、中英界面、搜索、快捷键录制与撤销均属于实现范围。更多语言、新风格、设备选择、模型常驻与更新只提供真实标明不可用的前端，见[能力缺口](../design/implementation-gaps.md)。原型中的模拟桌面和定时译文不进入产品。用户已取消两小时目标，按实际依赖持续开发到稳定交付，不以时间目标替代验收。

## 当前身份与最近 CI

10月30日环境替换后曾从远端重新 clone；该次丢失的未提交源码、截图和模型原始证据仍不能用摘要代替。10月31日本轮维护只中断进程，现有 live 并集与 ignored 候选文件均在，已核976文件与中断前冻结清单逐字相同；没有恢复旧进程或继承不完整检查结果。候选仍按实际文件、base与新验证集成。

[CI571](https://github.com/DingJWuyu/Typelay/actions/runs/41150753330) 对应精确提交 `80185abd6b34ce06b99a0ad32ac9372da4dd6cfc`，已全终态：shared、scope与双端 Index 独立生命周期成功；Windows 与 Linux 主检查失败。以下仅陈述本头原件覆盖边界，不继承其它提交的统计。

- Windows：Root 已核早期契约原件并解析 TRX；Browser/Notepad 后链由 Root 按本头完整 artifact 独立核对。
- Linux：已核早期、桌面小诊断与 Index artifact 的 SHA 并安全解压，终态 job 日志可读。完整 artifact 为700249617字节，超过512MiB工具上限，未下载，不换路径规避。
- 当前执行机有官方 .NET SDK 10.0.401、校验官方 SHA 的 PowerShell 7.6.6 与仓库锁定 Ruff/fontTools 私有环境；完整32面字体已按官方锁准备校验。系统 Python 有 GTK3/Gdk/Atk/cairo，缺 GTK4、AT-SPI typelib、Xvfb与原合作 SDK。
- 本机私有 Xorg dummy 与真实 headless Chromium 各一次启动都因本地 socket 权限失败，已终态并清理；未改 TCP/flag绕过。系统包只读检查和一次正式提权亦受执行器限制，命令未完成，不重试绕过。实际桌面和Windows行为仍依赖精确提交CI。
- 旧未合候选仅存本地的风险仍在。最小恢复包已在ignored目录构造并用独立index核exact base→完整patch→tree/文件SHA；它只恢复冻结源码，不恢复环境或旧GREEN原件。将候选保存到具名私有文件属于新增存储目标，Root已询问用户，尚待明确授权；目前没有上传，不把孤立Git blob的SHA当持久保留承诺。

### CI571 Windows 实测结果

App TRX metadata total1444，实际执行1422，1418通过、4失败；独立2项另行通过。Runtime1507、Core310、Protocol28、Windows.Contracts211均通过。

- 五个 owned 初始点击/Keep/PromptTab/narrow/两ABA路径与 Appearance retarget 本头逐名实际Passed。32位fixture鼠标标签改动已越过原首阻塞，不外推尚未完成的完整Replace/全部UI交付；retarget单轮通过不解释所有旧稀疏帧波动。
- FirstWake True实际Passed，False总链157ms，超过原150ms而安全拒绝。新分段中首次UIA/cache65.35ms，fresh字段复核0.83ms，admission到Core17ms仍混合；write-start到response约13ms。不能把旧大段主要归为身份复核，也不延期限、预热或删除检查求绿。
- Onboarding原件确认旧Show尾复活：回调Hide后原生窗口不可见、目标已active；ShowOrActivate返回时旧窗口native visible/active又为true，前台被其夺回，尽管WPF IsVisible仍false。独立准备提交前撤销机制，禁止返回后Hide/抢回焦点掩盖，实际Windows仍待验。
- Queue两分支停在初始ReadText的WM_GETTEXTLENGTH(14)，100ms/Win32Error1460；源进程、HWND与focus仍匹配，cleanup只是重抛。已收到exact install ACK，但其后源GUI仍做trace与RefreshSettings，ACK不等消息泵空闲；没有证据归因下游注入/Tab或放宽100ms。

本头 Browser 的 setup、automatic-preview-tab 通过，第三场 glossary 在原30秒内始终 stale/disabled；实际editor持续focused/可编辑/菜单可见，普通成功Overlay capture替换了Current snapshot引用，pending handoff仍认旧对象而按source-identity退休。Root负责同公开overlap回归与最小修复，不与完整Browser确认候选混合；前头后继Settings错误本头未到，不能算关闭。Notepad五个可执行场景通过、两项trusted-source-input-required未运行，cleanup为空，软件注入不替代可信物理输入。双端Index成功均仅包生命周期：各12阶段passed、cleanup为空、自有worker/supervisor已退出，desktop=not-run、quality=not-reviewed。

### CI571 Linux 实测结果

真实模型准备、普通生产桌面/Chromium-Firefox路径、Fcitx/GTK3合作步骤成功；GTK3不再是本头首阻塞，但不据单轮绿色宣称旧detach波动原因已消失。五个首失败分别归因：

| 场景 | 本头原件 | 当前动作 |
| --- | --- | --- |
| Settings | selector正常 `(315,335,444,31)`，viewport却为 `(-2147483648,-2147483648,558,405)`；不是已证真实按钮横向裁切 | 仅当前精确viewport sentinel复用现役首屏校准证明；失效/无校准仍拒绝，原contains/fullclip不放宽 |
| Tutorial | 合并断言报fresh editing candidate未ready，但原图与controller已有普通ValidTranslation；此前describe/approval/capture链过龄并practice-continuity-lost，ExperimentalWriteback仍false | 定位原协议耗时/合法continuity证明，保150/500门；关窗后child None render异常另列生命周期问题，不倒置为主因 |
| GIMP | 最后断开仍报 `(257,177,1,1)` 对 viewport `(258,178,456,613)`；之前实际翻译、接受/编辑与按需路径已过 | 本地已合精确内层“查看详情”单次展开消费者，等待下一头真实GTK验证，不盲等1×1 |
| GTK4 | 4.22.5 map后22ms在clock+surface layout中unmap；父/root仍mapped，surface448×100仍mapped，子件零allocation/未paint | 官方popover最小尺寸拒绝分支是最高解释，仍缺当时min值，不能猜修/再present/主动measure冒因果 |
| Qt6 | 已越过旧快捷键确认，publicChordSelectionAndDefaultRestore为true；后续dark→light时card440×89变440×99，editor412×39原位不变，失败overlayGeometry | 沿实际定宽布局与Qt6.11.2 height-for-width审因，不删几何断言、不继承丢失Qt候选结果 |

Qt的真实模型、source Accept/Undo、Exit零写、editor return/fresh accept/default Tab/Undo子项仍均true，不把主题几何失败说成翻译未工作。相关正式场景尚未整端绿色。

## 当前发布与验证

15 文件成熟并集已通过官方 GitHub connector 发布到唯一开发分支，逐文件 blob 与最终 tree `a9462a1ffde2a3202706af2c8b4ec5b21e5ab3c3` 均与本地已审树相等，parent 为 `94c1c2f9`。实际远端提交为 `80185abd`，不是本地提交 `a44fd2a`；main/trunk 已回读确认未变。普通 git push 已获执行审查通过，但因执行机没有 GitHub 凭据退出128，未建立或寻找新 token。

本头完整终态与原件见上节；下一批以本地成熟并集重新冻结身份、审查、验证后发布，不把CI571算到未提交差量上。

CI571 Linux early中九张快捷键PNG均为同一900×660全透明RGBA，viewer显示黑底，不是可用页面/主题像素证据。控件与持久化检查通过，不外推视觉完成或产品窗口未绘制；其它tray/progress图有正常像素。首图的client staging与owned compositor双观察窄诊断候选已独审，真实GTK/grim尚未运行。

已发布80185的适用本地检查为App.Tests和DesktopFixture完整托管构建、119项Python、全Ruff/format、portable Contracts 211项及受影响C#格式检查。它们不是WPF、GTK4或Qt真实桌面通过；本头正式结果以上节为准，后继差量必须重新验证。

本机Windows托管构建的后置复制复用已校验CI570发行包中的真实32768字节native DLL，SHA `b9ce5e6446c6fcb948b85e544d8d192a645f361d2ca775c1d49709d2ad47a85b`；对应C源和构建脚本未变，完整32面字体另按官方锁校验。这个输入不代表本机编译了Windows native或执行了Windows行为。

## 当前负责人及下一动作

所有责任线的目标配置为 `gpt-6-astra` / `xhigh`。本次新代理已显式按目标请求，但工具仅返回任务标识，实际模型/思考强度没有独立确认，均标为未核实。

| 责任线 / 当前 owner | 当前动作与依赖 | 完成依据 |
| --- | --- | --- |
| 集成 / resume_typelay_ui_delivery | 唯一 live 合入、文档与发布；剔除未闭 Onboarding 后974文件成熟并集已重编译/格式通过，准备精确远端回读与本头CI | 新树身份、双轴审查、适用检查、精确远端回读与本头 CI |
| Windows Show / union_standards | 独立 Standards 审查已发现全程改 ShowActivated 会影响 nested inherited Show；三文件已退回隔离候选，研究仅退休 opening 的窄 coercion，保留原属性策略 | 原RED与 nested-current/nested-show、Close、异常和策略改写的真实 WPF 回归；编译不替代行为 |
| 浏览器确认 / browser_confirmation_recovery | 只推进独立 Linux Host 候选；原Input221项已证22 errors+2fail，现将browser门限制到真正browser调用后仅余route-mismatch五项错误，继续普通preview/身份退休隔离修复；Windows候选接线文件实与80185相同，尚未实现新确认链 | exact Peer+document/route+target/origin、原Input/native/容量邻近、已发receipt及两端完整确认链 |
| 独立 Spec / union_spec | 成熟并集 Spec 已独审，Onboarding 阻塞保留；当前为 browser scope/普通读取及receipt边界给独立意见 | 现役规格、实际消费者与正反证据；不凭局部受控Host开放完整链 |
| Qt 菜单 / qt_menu_product_slice | 在独立候选拆分 More/语言 child 菜单，保现役入口、风格门、取消/新view失效与输入安全；搜索等仍列 O04 缺口 | 现有 Qt fixture/desktop 公开操作与几何、焦点、编辑稿回归；本机无SDK/GUI，真实证据待正式CI |
| owned Replace / owned_replace_product_slice | 从已通过 Keep/fresh绑定路径核最小 Replace 方案，独立候选；保原 down+500ms、新reader/真实源与旧按钮退休 | 公开 owned practice 完整 Replace/取消/改稿/新view链，真实 WPF 尚未运行 |
| 根主控 / Root | 用户沟通与产品/Spec；守住 owned Replace、Qt菜单及其余原型缺口，不以当前CI列表替代完整交付 | 同偏好逐页/状态真实证据、精确提交CI，模型研究本轮不重启 |

本地下一批包含 GIMP 两文件、Settings combo 精确校准、首图 owned compositor 诊断、Qt 定宽 HFW、Queue 失败 trace 与 Runtime glossary 重叠读最小修复；workflow 保留两个新增 suite。146项 Python 与全 Ruff/format（179文件）原件有效，相关Python字节未变，owned清理为空。维护后976文件冻结树已实际完成 App.Tests/DesktopFixture 托管构建（0警告/错误）、portable Contracts211和 Runtime1520；C#格式只因未闭 Onboarding 新测试的184行自动属性失败，没有放宽检查。双轴独审均发现该 Show 候选影响继承 Show 重开策略，因此三文件已从 live 排除并保留原件，随后974文件成熟树已重新完成 App.Tests/DesktopFixture 编译及四个受影响 C# 文件格式，全部退出0、0警告/错误，前后输入无漂、owned清理为空。另核182项Python相关输入和233项Runtime/Contracts相关输入与前述实际通过树逐字相同，未将被排除Show的旧结果转移为已修。构建/受控端口不是 WPF、GTK、Qt或Browser真实桌面通过。
- GIMP：祖先EXPANDED可能先于真实allocation，受控端口不证明GTK没有该竞态，不盲等或宽clip。
- Combo：仅双INT_MIN sentinel且同冻结direct pair校准可恢复clip，原full-clip/ACTIVE/身份/期限不变，真实GTK仍待验。
- 首图：pixbuf与grim是两个顺序观察，不是同帧；只允许自有隔离niri输出，诊断失败不新增产品门。
- Onboarding：原native抢焦已证，未闭候选本批不发布。WM_WINDOWPOSCHANGING 内更改 SWP_NOACTIVATE 会被系统忽略，不能用该替代方案冒充修复；coercion候选仍待研究和真实WPF。Queue仅失败时保存精确已存在trace，不称100ms超时已修。
- Runtime：只丢弃exact pending handoff且basis不变的普通成功Overlay读，不更新ACK或重绑旧观察，下一fresh recheck仍唯一授予词汇表入口；真实Browser尚未重验。
- Qt：最终fixedWidth经6.11.2静态核验，HFW高度以该宽计算，非HFW/负返回沿原adjustSize；没有本机Qt native编译或GUI结果，需下一正式SDK/主题/长文/展开/菜单/动效验证。

CI571仍对应已发布80185，而非本地未提交差量。

发布只包含已经达到当前适用验收边界的成熟差量，真实平台尚未走到绿色，仍标明未验，不自动将诊断写成已修；CI 等待期间继续现役独立缺口，不追加无新假设的重复运行。

## 仍未关闭的交付边界

完整双端 25 项与同偏好逐页验收尚未完成。S08 整体模态材质与入场实际组合像素、owned 完整确认/Replace、Browser 完整键盘确认、GTK4 O04 和其他页面 Full 动效仍有缺口。旧未提交 shader 接线/渲染、Linux pulse、Qt HFW、O03 与浏览器确认候选均不可按旧摘要直接合入；需要时从现基线重新准备。CI570 的两次官方 FXC shader 字节原件已恢复且各352字节一致，状态仍为 generated-twice-identical-not-rendered；不称饱和度材质已交付。字体包已有 32 面锁定资源，不重新发明字体体系，当前已按官方prepare从锁定缓存重建并验证完整本地bundle，首轮不完整输出未用于编译；实际跨平台页面/状态仍逐项核对。[原型对照](../design/prototype-parity.md)维护产品关闭条件。

## 模型能力边界

B01 固定 Index profile 的中英双向 Glossary 发布行已在提交源码中，仍仅按已批准 feature-delta 条件准入。B02 自然/正式风格与换种说法此前有界裁决未达标准，属于本轮未可靠交付功能，不归入第四类仅前端范围；保持门禁，不新增 prompt、采样、模型研究或质量试验。旧模型原始证据当前本地缺失，不声称重新评审或恢复；实现与既有裁决不据此自动扩权。

## 持续约束

- 输入来源、焦点、IME、租约、接受资格、写回及 Undo 不因重构放宽。原 down+500 ms、FirstWake 150 ms 等预算保持；诊断不产生新的许可。
- 动态桌面/浏览器与重 CPU 构建统一单 owner：READY 不是运行，fresh GO 后立即 START，终态先 END 并清理再分析。记录命令、独立 cwd/源码身份、session、期限、日志与 exit；不确定结果先查，不盲重跑。
- 字体只按固定来源和校验和在构建时准备，产品离线；GTK3 语言菜单没有背景模糊时用对应浅深实底保证可读，继续披露材质差异，不扩大为交互降级。
- 保留 Argos/Hy 显式兼容与方向，新空库存默认 Index；不自动下载/替换模型，失败保旧代。模型权重不进入源码。
- 唯一发布分支是 `work/linux-usability`，非 force、推送前核远端身份；不更新 main/trunk、不自动 PR、不发布稳定版或体验推荐。
