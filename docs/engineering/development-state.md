# 当前开发状态与安排

本页只维护现役任务。要求以[产品基线](../product/README.md)、[原型](../design/README.md)和[原型对照](../design/prototype-parity.md)为准；历史执行记录查 Git。

## 当前身份与范围

截至 **2026-11-03**，开发分支 `work/linux-usability` 已核提交 [`bf38af66844aade514389b997798f8d7d97c291a`](https://github.com/DingJWuyu/Typelay/commit/bf38af66844aade514389b997798f8d7d97c291a)，tree `19e506f9c4ccf3e3c698197bbd9e71d3ec3e2270`。Qt 操作图标、NativeFixture Compile Link 与快捷键稀疏配置修复已保存；这些是重新构建、重新审验的源码，不冒充此前不可恢复的候选原件。`main` 保持 `09fe613a606073876483b51446211030a66a9597`，`trunk` 保持 `438c49789777beca9b14136dbbbbf41426c4d888`；不更新两者、不 force、不自动 PR。

实施范围保持原型 I–III；IV 的 N06–N11 只做真实禁用前端与[能力缺口](../design/implementation-gaps.md)，V 演示不做。旧模型、同语、源选择、人工稿、Undo 与输入安全不降级。Index 仍默认，不扩模型。自然/正式风格和换种说法未达到质量要求，仍是未完成功能，不归前端例外，也不重启优化研究。

## 最近真实平台结果

[CI582](https://github.com/DingJWuyu/Typelay/actions/runs/41572846179) 精确 head bf38af66 已终态 **failure**。共享、Windows/Linux Index 均通过；两端完整产品检查仍失败。两端终态日志、Windows contracts/发行原件与 Linux small desktop 已核大小/SHA。局部通过不等于整端交付。

| 场景 | 当前事实 | 下一动作与边界 |
| --- | --- | --- |
| Windows 构建与真实主链 | 完整工作区构建通过，原漏 Link 已越过。Continuous 完整链、Hy ABI5 与 Settings 生命周期通过 | 不把编译及这些通过扩大到 Browser、物理写回或全部 UI |
| Windows 合同 | App 实际执行 1446，1442 通过、4 失败；Core 318/318、Runtime 1565/1565。LocalDraft 的 unarmed-prefix/真实失焦/重复/过期邻近及 FirstWake 本头通过 | 单轮通过不证明旧波动独因已消除；4 个失败分开处理 |
| Source 确认 Keep | 原一次 Enter 已回原字段，完整 PID/thread/root/cookie/focus 均满足、sheet 消失、宽 324；测试只认 raw foreground 为 root，拒了生产合同允许的原 Edit HWND | tests-only 将该等待改为现役完整原字段合同，仍拒同进程其它控件；原 3s、单输入和所有后置保持，候选待 Windows 重验 |
| General 工作模式动效 | en/light 的 Select 同步段 234.824ms，首几何已到超调；Begin 在保存前起钟、同步重建后可在未 Rendering 时直接 Draw。其它 10 场通过 | 新轨迹首有效 Rendering 起原 300ms，已启动轨迹跨重建不重播；不把整体 Select 耗时说成磁盘独耗，不放宽曲线/预算 |
| Windows 其它合同 | OwnedPractice 在新 ACK 后、原鼠标释放后的第三次 WM_GETTEXTLENGTH 未完成，错误码缺失，不能确认 timeout；尚未 Tab。Generation 首受控 child exit1，后锁删除失败是未证明退出时的保护 | 不改源许可或提前释放锁；受控 child 的 cmd 特殊解析依赖待最小可靠构造，第三次读取缺现场事实仍未决 |
| Windows Browser | 原 4 种 Keep 与首 click Replace 越过；第二 Enter 在 Describe 返回 PendingEditorMismatch，sent=false，随后 ContextLost，未派发 Return；后续场景未跑 | 核精确同稿迟到 Editing echo 对 pending 的退休；不是已证 500ms 到期、明确 false 或模型故障，不绕完整身份/fresh/后稿 ABA 门 |
| Notepad / 物理包 | 真实 Notepad 预览、按需取消/直接翻译/Undo、退出通过；两项可信物理写回仍未测。独立 physical-tab smoke 的非 automation 参数在 Preview 能力断言失败，未生成验证包 | 不把受控输入替代可信物理按键；新 smoke 失败仍未定因，不放宽产品门 |
| Linux 发行 / Settings / 教程 / Browser | finalize 已构建归档，distribution 随后失败且 runner 日志明确 ENOSPC。Settings/教程在复制隔离 runtime 即 ENOSPC，未到目标 UI。Browser 仅空 traceback、原 240s 无结果退出，不能证成同一原因 | 有界回收已完成、无后继消费者的三个私有 CMake build 树，保源码/运行时/包/诊断；容量未测，不保证根治。Browser 不计产品通过或已证产品回归 |
| GTK3 / Qt probe | 正式 relocated GTK3 与 VS Code 通过，稀疏快捷键 helper 不再阻塞。独立 GTK/Fcitx glossary 和 Qt probe 通过，旧选区/ordered 票问题未重现 | 本头真实通过有效，但不据未改相关产品而宣称旧根因消失 |
| GTK4 配置 | 内容盒 264、真实 Clear、Tab/Shift+Tab 和 Pinyin preedit 均越过；行点击后 search 保焦但提交“拟好”、Clear=true/save=0，原 Composing 后置再次失败 | row 原件只有 enter/leave，未证明收到 button。只补失败时两份已有 hover/pre-send 元数据；外点/自然越界/Escape 后继矩阵仍未跑 |
| Qt 正式链 | 语言、中文稿、收展/安全隐藏及前 8 次主题采样越过；paused-light 第 9 次 overlay 抓图样本为空，实际控件 palette 已浅色、Resume 可见 | 空样本不等于错误颜色；现采样只发一次，延长等待不会补图。需区分 grab 与身份/几何拒绝，不回退图标或松像素断言 |
| GIMP / Linux Hy | 受完整发行前置失败影响，本头未执行 | 保此前证据，不冒本头通过；先修资源峰值，再走原完整链 |

本头已逐张检查 8 张 Qt 主题图：全部为中文 UI，短稿 More 三点、长稿 Expand 对角箭头可见，原“展开”文字已消失；没有英文 UI 或展开态 Collapse 实图。26px/15px 几何与控件断言不代替逐像素 1:1。常驻 ×、hover 显隐、材质和完整动效仍未齐。既有 Settings 九图、S09 known/unknown 与 S08 静态图保留有效范围，多 DPI/中帧不能由静态图关闭。

## 当前工作与收束条件

| 模块 | 当前工作 | 收束条件 |
| --- | --- | --- |
| Windows Keep / General | 三文件已完成需求与工程静审；folder 格式通过，App.Tests 托管编译通过 | 下一真实 WPF 重跑原失败与慢保存/已画完再 ACK、身份失效邻近；不把托管编译当 GUI |
| Linux 构建资源 | 单 workflow 候选在合作包打包/搬迁/preflight 全步成功后回收固定三树；fd/no-follow 全校验，前后有界 df/inode/du；ccache/系统缓存不碰 | 10/10 合成树正负及原步骤字节不变检查通过，再完整 CI 实测；成功构建树内部 CMake 日志随中间树回收，失败构建不删 |
| Browser pending | 真实 Replace 后，完整身份/原稿版本不变的精确 echo 保原 pending 候选；原 500ms 不续，与后来稿/Return 结算生命周期分开 | 固定候选已完成静态语法/格式检查，需求与工程审查收口后进入真实 DOM/Windows；不得重建已退休关联、续 t0、吞后来稿或把现 gate 拒绝改成安全恢复 |
| GTK4 / Qt 后继 | GTK4 一文件有限失败投影已完成静态检查；Qt paused 抓图失败分支仍未确定 | 必要事实足够即最小修，不连环扩日志、不为新理论门拖成熟批 |

Qt 目标搜索仍未完成，现有 child 与配置退休链只作只读比较。GTK3 目标搜索保源选择和显式 Apply，不冒完整原型方向头/交换/即时保存。后继按真实产品缺口推进，不以一批补丁完成作为项目完成。

## 验证环境与未闭边界

本轮三文件候选在固定 984 输入下完成 folder whitespace 和 locked restore。首次 App.Tests 构建已产出托管 DLL，但 Linux 条件未把已恢复 NativeInput 复制到 Broker bin，命令在末端复制报 MSB3030；保留该失败。将本头已校发行包的同一 DLL 放入忽略的构建目录后，原命令增量通过，0 warnings / 0 errors（7.37s），输入无漂移、检查进程已退出。没有改 csproj 或平台条件，也未执行 Windows DLL。

当前合并候选为 7 个源码/测试/工作流文件与 4 个产品文档。两 Python 文件 Ruff/format/AST、生产 Browser JS 与 18 个新增公开 DOM 表达式语法、diff 检查均通过（0.55s）；执行输入无漂移，检查进程已退出。477 个托管构建输入逐字节与前述通过构建一致，未重新把静态 JS 检查称作 WPF 执行。Linux 回收脚本在独立合成树完成 10/10 正负路径，固定 workflow/脚本字节对应；没有删除本地真实产物，也未证明目标 runner 容量足够。Browser 回归和 GTK4 失败投影尚未执行目标 GUI。

SDK 10.0.401、Ruff 0.14.0/fontTools 4.61.1 仍按现锁；32 个字体从本头已校发行资源只读恢复，正式 verify_bundle 逐项 SHA 校验通过。工具与依赖恢复不计产品测试。当前没有可用 DISPLAY/WAYLAND、GTK/Qt 开发 SDK 或真实 WPF；本地只做适用托管编译/受控回归/格式，目标平台运行依赖 CI。

- 完整双端逐页、真实键鼠与 IME 验收未完。Browser safe-false→retry、混版 GUI、非 Editing 确认和 changed-rich bookmark 体验仍未闭。
- S08 材质/中间组合像素、多 DPI、跨页 Full 动效及 O02/O03/O04 余项仍有差距。
- 原 down+500ms、FirstWake 150ms、焦点/IME/源身份、接受资格、写回和 Undo 条件保持。安全退休与材质回退不能冒完整原型呈现。
