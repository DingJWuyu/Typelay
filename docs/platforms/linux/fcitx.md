# 手动启用 Fcitx 合作组件

合作组件让 TypeLay 读取实际输入法事务状态，为标准 GTK 输入框提供预览和受限写回。Qt 使用同一私有 Fcitx 服务，但需要额外的同版插件，见[可选 Qt 构建与启动](#可选-qt-6112-构建与启动)。普通 TypeLay 安装不会启用这套手动会话。

推荐先用主包设置中的[受管接入](README.md#连接目标应用)。受管接入使用独立配置和监督进程，适合练习窗口及已注册应用。本页面向需要沿用日常输入法配置的手动使用者；两种会话不要混用，原输入法的切换与恢复都由你明确操作。

## 启用前确认范围与会话

GTK3、GTK4 的标准 Entry 和纯文本 TextView 已有合作实现。GTK4 需要单独的 `--gtk4` 入口。自定义控件、非纯文本结构和通用 Electron 不在当前范围。普通 GTK 模块连接未修改的系统 Fcitx 服务时，输入法状态仍为 Unknown，不授予正文读取或接受能力。

手动私有会话使用原 session bus。同一个总线上只能有一个 `org.fcitx.Fcitx5` 服务，启动器会拒绝与现有服务并行运行。它不会终止其他进程，也不会修改系统库、桌面自动启动或全局输入法设置。若桌面监督程序会自动重启原 daemon，需要先由你暂停原启动方式。

私有会话沿用原 HOME、XDG 和 FCITX 配置，因此保留输入法顺序、快捷键、皮肤和个人词库。学习结果按原 Fcitx 行为保存，恢复原服务不会回滚新学习。先结束组合输入并退出原 daemon，再备份自己的配置和数据。自定义 `FCITX_CONFIG_HOME` / `FCITX_DATA_HOME` 优先于默认的 `~/.config/fcitx5` / `~/.local/share/fcitx5`；两份 daemon 不得并发访问同一 profile。

## 预检

在匹配的 Arch/CachyOS x86_64 环境中使用此包。当前包固定 Core 5.1.22、Chinese Addons 5.1.14、GTK IM 5.1.7 和私有 LibIME 1.1.16。系统仍须提供 Chinese Addons 5.1.14、LibIME 1.1.16 及匹配的官方 pinyinhelper 数据，发行修订号可以不同。主包的独立受管会话也要求这些系统前置。

完整主包中的压缩包位于 `support/desktop/TypeLay-Fcitx-cooperative.tar.gz`。进入压缩包所在目录，解压到独立用户目录，然后预检：

```sh
tar -xzf TypeLay-Fcitx-cooperative.tar.gz
cd TypeLay-Fcitx-cooperative
python fcitx-cooperative.py preflight
```

成功时会显示 `Private Fcitx 5.1.22 runtime verified; no session or profile changed`。预检核对包文件、字体、实际 ELF 依赖、私有库解析路径和 daemon 版本，不启动输入法会话。缺失、版本不匹配或校验失败时，先解决具体错误，不忽略检查继续启动，也不为此盲目升级或降级系统。

私有词典和语言模型来自 LibIME 固定源码指定的归档及 SHA256，模型搜索不会回退系统模型。Chinese 的 `py_table.mb` 与 `py_stroke.mb` 从系统官方安装读取；包没有分发这两份数据及原始归档，因为其单独许可尚未核实。启动器按固定摘要核对 `/usr/share/fcitx5/pinyinhelper/` 中的文件。可先只读检查：

```sh
printf '%s  %s\n' \
  9b526eab3ef2679d63155c8304fc4b11f701c2bf5db7946e7d3e940f54f3aaa9 /usr/share/fcitx5/pinyinhelper/py_stroke.mb \
  19b3e593e5a211fe548f39c894d9a2e2e07a08e3edee0083087b1471e5ba026b /usr/share/fcitx5/pinyinhelper/py_table.mb | sha256sum -c -
```

## 明确切换与逐应用启用

先在所有应用中完成或取消组合输入，再从原 Fcitx 托盘退出输入法。已有应用可能在服务切换后重新连接，需要时重新启动目标应用。

在合作包目录启动私有 daemon：

```sh
python fcitx-cooperative.py daemon
```

终端应显示它正在使用原 session bus 和原 profile。保留这个前台终端，退出时使用 Ctrl+C。启动器会检查 Fcitx 与 IBus 冲突；活动 IBus 或无法确认其状态都会导致拒绝。私有命令排除 `ibusfrontend`，防止上游兼容前端接管 IBus 服务；仅使用 IBus 协议的应用不属于此会话范围。

另开终端，先正常退出旧 TypeLay，再通过同一包启动客户端：

```sh
python /完整路径/TypeLay-Fcitx-cooperative/fcitx-cooperative.py launch ~/.local/bin/typelay
```

GTK3 目标应用通过主包的 GTK 模块入口启动：

```sh
python /完整路径/TypeLay-Fcitx-cooperative/fcitx-cooperative.py launch ~/.local/bin/typelay launch 应用命令
```

GTK4 目标应用使用独立入口：

```sh
python /完整路径/TypeLay-Fcitx-cooperative/fcitx-cooperative.py launch --gtk4 应用命令
```

启动器会确认当前 Fcitx 服务确实来自同一私有包，只为请求的子进程设置模块路径和合作开关。GTK3 每次按当前解压位置生成 IM 缓存，并放在用户 `XDG_RUNTIME_DIR`，不修改系统缓存。GTK4 只在本入口检查 GTK4 依赖，普通 GTK3 会话不要求系统安装 GTK4。

先用非敏感合成文本检查普通输入、拼音候选、取消、Backspace 和切换输入法，再检查 TypeLay 预览及接受。普通合作应用的自动接受受默认关闭的“原生输入框替换”实验开关控制；自有受管练习另有独立授权。输入法未知或字段不支持时，应用应保持普通输入行为，TypeLay 不读取或写回正文。

默认 Tab 接受、Esc 退出。设置也可保存无修饰功能键，或双方已确认能力的 Ctrl+F1-F24。组合键固定原次 Ctrl 前的资格和期限，输入法输出、正文、目标或配置变化都会撤销。编辑译文后的第一次接受只回源；整组按键释放、重新捕获并取得新许可后，下一次独立按下才接受人工稿。旧组件不能凭同名接口继承组合键能力。

## 恢复原输入法

1. 在目标应用完成或取消组合输入，正常退出 TypeLay 和通过私有包启动的应用。
2. 在私有 daemon 终端按 Ctrl+C，等待进程退出并释放总线名称。
3. 通过原桌面启动方式或 `/usr/bin/fcitx5` 恢复原服务。若此前暂停了自动启动，按原方式恢复。
4. 正常启动应用，不再经过合作包启动器。配置与词库仍在原位置。

确认不再使用后，可以删除私有解压目录。保留自己的 Fcitx 配置和数据；无需还原系统库或更改全局输入法变量。

## 构建与打包

维护合作包时，先从 [README 的开发与验收导航](README.md#开发与验收)打开 Linux 开发指南，准备私有 Python 环境和 `eng/requirements.txt`。只在匹配的 Arch/CachyOS x86_64 开发环境构建。固定来源与哈希由 `eng/linux/cooperative/inputs.py`维护，生产配方和补丁位于同目录。

仅生成生产组件时运行：

```sh
python eng/linux/cooperative/fcitx.py --output artifacts/linux/fcitx-build
```

准备发行验收时，由既有回归调用同一生产构建，再封装对应产物：

```sh
python tests/linux/fcitx_cooperative/run.py --core-version 5.1.22
artifacts/build-tools/bin/python packaging/linux/fcitx-cooperative/pack.py \
  --build-output artifacts/linux/fcitx-cooperative-5.1.22 \
  --output artifacts/TypeLay-Fcitx-cooperative.tar.gz
```

每次使用新的输出目录。构建记录与测试记录分别绑定输入和二进制；源码变化后不能拿旧产物冒充新构建。打包成功也不等于发行验收通过，搬迁后的实际包还须完成对应桌面检查，命令从 [README 开发导航](README.md#开发与验收)中的 Linux 开发指南查阅。普通配置中的已加载组件、真实模型与目标应用行为不能由隔离白名单测试替代。

分发时保留完整包。包内包含修改后的对应源码、固定来源、补丁、重建脚本及原许可证，不得只抽取修改后的二进制。LibIME 的原始数据归档、固定 KenLM 子模块及对应许可随源码保留；Chinese pinyinhelper 的两份未核实许可数据只保留重建 recipe/hash，不随包分发。实际清单及校验规则见 `packaging/linux/fcitx-cooperative/pack.py`。

打包同时准备完整离线字体，保留 OFL 许可与来源。字体和静态光学角色以 `eng/fonts/fonts.lock.json`为准，缺失或篡改会使预检失败。启动器只为请求的 UI 子进程设置 `TYPELAY_UI_FONT_ROOT`，不会改父环境或主包 `TYPELAY_HOME`。GTK 使用私有 Fontconfig/Pango 映射，仅附到 TypeLay 浮层；选择系统字体时继承宿主映射。Qt 在应用进程注册受控字体，系统选项沿用应用默认字体。GTK 活控件 shaping 身份与 Qt 实际 glyph runs 需要分别验证，CSS 请求族名不能替代实际字体证据。TC/HK 专用面未捆绑，像素表现仍需实图验收。

## 当前能力边界与维护依据

合作能力来自真实加载模块和输入事务，不能由输入法名称或空候选窗推断。Core 守卫核对 `loadedAddonNames()`、官方 type/library/version 和 loader 实际成功加载的路径。未知插件可以按原 Fcitx 行为运行，但 TypeLay 保持 Unknown。当前审计组件清单以 `eng/linux/cooperative/patches/fcitx5.patch`中的 `audited` 为准；同名系统库回退或来源不符都不会继承权限。

修改上游版本、替换模块或增加输入输出插件时，重新检查以下实际行为。上游文件位于构建目录的 `sources/`，分发后的对应源码位于 `source/upstream/`。

| 维护对象 | 必须保留的判断 |
| --- | --- |
| `fcitx5/src/lib/fcitx/tempmodemanager.cpp` | 按真实 per-context 活动状态识别 QuickPhrase、剪贴板、Unicode 和输入法选择模式，空候选不等于完成。 |
| keyboard、pinyin 和 Core compose | 检查真实 pending；切换输入法后，原 pinyin 的延后标点提交仍可能存活。 |
| classicui 与 notificationitem | 候选点击重新读取当前列表，延迟菜单按实际动作审计，不能仅凭 timer 推断迟到正文提交。 |
| `clipboard/xcbclipboard.cpp`、`waylandclipboard.cpp` | 异步接收更新历史，实际提交来自新按键或当前候选选择；活动剪贴板模式仍拒绝。 |
| waylandim 与 xim | 输入按各自 context 路由，前端存在本身不授予 TypeLay 字段能力。 |
| Chinese chttrans、fullwidth、cloudpinyin | 保留同步过滤偏好，核对云结果的生命周期及真实选词提交。加载 cloudpinyin 不代表用户启用了网络查询，私有包不替用户改变云服务开关。 |

GTK3/GTK4 Entry 的同窗口后台直接替换有独立实现：原字段在输入输出退休后取得证明，写回不抢新字段焦点。它不外推到 TextView 或跨窗口、跨应用后台。后台回归应在同步正文变更回调中观察另一字段的焦点与选区，不能以写后全选或晚快照推断写入时状态。人工改文负控须证明修改发生在写入前，并观察到拒写与零额外改文。

源端同时维护写入结算。只有原许可已失去全部执行资格，且没有在途认领时，才报告 `writeRetired`。连接断开、撤权请求或显示退休都不能证明写入未发生。GTK 共用实现位于 `src/linux/native/common/write-settlement.h`，正文替换位于 `src/linux/native/common/gtk-textbuffer-write.inc`。

私有 daemon 的托盘重启沿实际可执行文件 exec，保留原参数和 IBus 排除项。配置菜单使用包内原 `fcitx5-configtool` 脚本寻找系统配置 GUI；缺少 GUI 时沿原脚本提示处理，不修改系统安装。

## GTK 浮层展开过渡

GTK3 的展开和收起复用原 popover、滚动区和编辑器，工具包只过渡几何。正文与资格立即更新，关闭系统动画时直接布局。新目标、布局变化或安全隐藏会结束过渡；隐藏立即清正文，不允许完成回调恢复旧浮层。

GTK4 当前使用即时高度布局，按当前人工稿测量。平滑展开尚未完成，既有展开重入和编辑焦点问题仍需定位。GTK4 只在译文可编辑时显示悬停细边框，进入编辑或失去资格后清除。两代视觉差距见原型对照，当前提交的 GUI 证据见开发状态；两者从 [README 开发与验收导航](README.md#开发与验收)进入。构建成功、公开无障碍动作或合成输入不代表物理鼠标、输入法和缩放已验收。

## 可选 Qt 6.11.2 构建与启动

Qt 合作插件固定为 Fcitx5-Qt 5.1.15 与 Qt 6.11.2，包不带 Qt 系统库。普通 GTK 入口不要求安装 Qt。启用 Qt 前，系统运行库、插件和私有头文件对应的 ABI 必须匹配；发行修订号可以不同，但不能据此宣称 Arch 与 CachyOS 二进制相同。

Qt 的生产构建见 [Qt 构建与验证](qt.md#构建与验证)。将 Qt 加入合作包前，运行同一构建的真实异步回归，再用 `--qt-output` 封装：

```sh
python tests/linux/qt_cooperative/run.py \
  --fcitx-root artifacts/linux/fcitx-cooperative-5.1.22
artifacts/build-tools/bin/python packaging/linux/fcitx-cooperative/pack.py \
  --build-output artifacts/linux/fcitx-cooperative-5.1.22 \
  --qt-output artifacts/linux/qt-cooperative \
  --output artifacts/TypeLay-Fcitx-cooperative-qt6.tar.gz
```

解压含 Qt 的包后，先检查运行库，再按前述流程启动同包 daemon，最后逐应用启动：

```sh
python fcitx-cooperative.py preflight --qt6
python fcitx-cooperative.py launch --qt6 应用命令
```

预检核对实际 `qVersion()`、编译版本、插件元数据和 ELF 依赖，混版或未知 ABI 会被拒绝。强制 `FCITX_QT_USE_SYNC=1` 不受支持。启动器只为本次子进程设置 Qt 插件路径、TypeLay generic 插件和 Fcitx 输入模块。

应用自带另一套 Qt、Qt5、QtQuick、自绘及富文本不在此入口范围；标准控件与后台路径的具体边界见 [Qt 实现范围](qt.md#实现范围)。撤回时退出该应用，再正常启动；整个私有会话的退出沿用[恢复原输入法](#恢复原输入法)。
