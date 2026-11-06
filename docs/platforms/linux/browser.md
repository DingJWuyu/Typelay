# 在 Linux 浏览器中接入 TypeLay

浏览器扩展让 TypeLay 在网页输入框旁显示译文，并在当前字段允许时接受译文。它连接同一桌面会话中的本机 TypeLay，翻译仍由本地模型完成。先完成 [Linux 客户端安装](README.md)，再按本页接入浏览器。

本地主机不启动模型、不监听网络端口，也不保存正文。源文和译文只经过浏览器私有消息管道及同用户 socket。普通安装不会注册本地主机、加载扩展或批准网页访问。推荐使用设置中的“应用支持”，由你在浏览器中完成扩展安装和权限确认。

## 范围与限制

当前实现处理顶层 HTTP(S) 网页中的标准 `input[type=text/search]` 和 `textarea`。密码、信用卡、一次性验证码等敏感字段会在读取正文前被拒绝。隐藏、禁用、只读或已移除的字段也不可用。

Tiptap 另有单段富文本实现。源字段必须绑定实际编辑器的公开实例，文档只含一个 paragraph，段内只含文字及受支持的 marks。多段、列表、代码、mention、atom 和 hardBreak 不在此范围。回归使用 Tiptap 2.11.5；其他版本与自定义 dispatcher 需要分别验证。

iframe、Shadow DOM 内的源字段、其他 contenteditable、自绘编辑器、地址栏和浏览器内部页面尚无输入实现。Chrome 新标签页中央搜索框可能把焦点转到地址栏，普通 google.com 搜索框的结果不能代表它。此扩展也不自动接入 Electron 或 VS Code。

标准控件是能力范围，不是应用白名单。网页的受控组件、自定义事件和私有编辑状态可能影响结果；根元素变换导致坐标无法可靠映射时，扩展拒绝显示。实际网页仍需检查状态同步和撤销行为。

## 显式安装

保留解压后的发行目录。Firefox 文件位于 `browser/firefox`，Chromium 文件位于 `browser/chromium`；浏览器会继续引用这些文件。

1. 在 TypeLay 的“应用支持”中选择浏览器，打开接入说明。
2. 在浏览器中加载扩展，并批准网页访问。Chromium/Chrome 使用 `chrome://extensions/` 的“加载已解压的扩展程序”，选择发行包的 `browser/chromium`。管理页地址需要你粘贴到地址栏，TypeLay 不添加绕过浏览器限制的启动参数。
3. Firefox 使用 `about:debugging#/runtime/this-firefox` 的“临时载入附加组件”，选择 `browser/typelay-firefox-unsigned.xpi`，也可选择 `browser/firefox/manifest.json`。这个未签名临时扩展会在退出 Firefox 后失效；发行包未提供可永久安装的签名商店版本，也不修改签名校验设置。
4. 回到 TypeLay，点击“连接”。Chromium/Chrome 会查找标准配置中唯一且与当前组件目录匹配的扩展记录。记录缺失或不唯一时，连接保持未确认。
5. 保存网页中的工作，刷新目标网页。在允许的输入框中键入非敏感文本，等待真实连接和字段检查。注册成功只表示本地消息通道已登记；“应用支持”显示的是最近观察到的字段能力。

浏览器须与 TypeLay 处于同一 Wayland/niri 会话。容器、Flatpak 或其他进程命名空间可能无法访问本地主机，或无法确认同用户进程祖先；这种情况保持不可用，不绕过身份校验。

### 用命令行管理本地主机

若使用上述设置入口，无需再执行这些命令。手动方式在发行包目录运行 `install.py`，扩展加载和网页权限仍由你在浏览器中确认。

Firefox 的扩展 ID 固定为 `typelay@typelay.local`：

```sh
python install.py browser-install --browser firefox
```

Chromium 先加载扩展，再将管理页显示的 32 字母 ID 填入命令：

```sh
python install.py browser-install --browser chromium --extension-id 这里替换为浏览器显示的ID
```

Google Chrome 使用 `--browser google-chrome`。安装器只登记这个明确来源，不使用通配来源。扩展连接的本地主机名是 `org.typelay.browser`；主机从自身位置定位发行目录。

## 使用与卸载

自动模式跟随当前源字段；按需模式使用设置中的唤醒和直接替换快捷键。首次获焦时，输入法状态可能仍是 Unknown。扩展须观察到可信的非组合输入或 compositionend，才能确认状态；未知或组合期间不读取正文，也不授予接受许可。

明确接入的 Chrome/Chromium 标准字段，在当前文档确认版本 1 能力后，可以默认接受译文。Firefox、Tiptap 富文本和旧组件的自动接受仍受默认关闭的“原生输入框替换”实验开关控制。连接登记、其他标签页的确认或旧候选，都不能替当前字段授权。

接受和退出默认是 Tab/Esc。设置也提供无修饰 F1-F24，以及能力已确认的 Ctrl+F1-F24。浏览器和操作系统可能保留部分功能键；配置成功不保证按键能到达网页。

点击译文后，扩展先申请编辑授权。编辑后的第一次接受只返回原输入框，不写回。释放原按键后，宿主须重新观察原字段并授予新许可，下一次独立按下才接受人工稿。Ctrl+功能键要求整组释放后再按；重复键、合成按键和旧按次的释放不会生成新接受。配置、草稿、焦点或输入法变化会撤销原资格，显示续租不会延长按键票据的原期限。

移除接入时，在“应用支持”点击“移除”，再到浏览器移除扩展。版本 1 的 Chrome/Chromium 连接会停止未来命令和自动接受，已派发操作仍按原回执结算。Firefox 等旧连接需要保存工作并完整退出浏览器，删除注册或关闭单个标签页不保证立即断开。

手动注销使用对应命令：

```sh
python install.py browser-uninstall --browser firefox
python install.py browser-uninstall --browser chromium
python install.py browser-uninstall --browser google-chrome
```

完整卸载 TypeLay 也会清理本安装拥有的注册和 wrapper。浏览器扩展仍由你移除，其他安装拥有的注册不会被覆盖。

## 接受时效与诊断

可见译文不保证按下 Tab 时仍有有效许可。许可失效会保留原文，并让原键继续按浏览器规则处理。Firefox 的稳定候选曾出现续期空窗；浏览器主线程停顿也可能延迟提示重画。扩展不会自行延长旧许可、补发 Tab 或重试写入。

标准字段在同一文档事件循环中检查目标对象、配置、版本、选区、原文、输入法和期限，然后用原生 `setRangeText` 替换并发送一次 input 通知。网页回调若再次改变结果，扩展返回 `OutcomeUnknown` 并隔离原字段。这不能保证每个框架的内部状态或原生撤销栈都与 DOM 一致。

Tiptap 通过编辑器事务替换，保留范围外结构及宿主默认撤销分组。隔离脚本持有输入法与许可检查，MAIN-world 桥只操作当前编辑器。桥在 beforeTransaction 前后复核状态；一旦本次写入已被认领，页面无回复、异常或声称未修改都不能证明未写入，结果未知时隔离且不重试。页面回调之间没有额外的原子事务保证。

许可到期、断连和撤权请求本身都不是“未用许可退休”的证明。源端须确认原许可已失去全部执行资格，且没有被认领，才向宿主报告 `writeRetired`；已经开始的写入继续交付原计划回执。呈现更新和浮层隐藏不能替代写入结算。

浮层在顶层文档的封闭 Shadow Root 中显示，使用 `browser-viewport` 坐标。失焦、隐藏、租约到期或断连时撤下并清空正文。展开和收起复用同一编辑器；新视图立即更新正文，几何变化或安全隐藏取消旧动画，减少动态效果时直接落位。快速反转的既有失败仍需对应实绘与真实点击证据，不能用单次展开通过代替。当前提交的已测与未测范围见“当前开发状态”，可从 [README 的开发与验收导航](README.md#开发与验收)打开。

诊断 trace 的页面耗时是局部 duration，主机时间使用同机 BOOTTIME。`snapshotAtMs` 是校准下界，不能相减后当成精确回程延迟。现有长尾证据不足以区分浏览器发送、路由和调度，不应据此放宽许可期限。

## 开发验证

先从 [README 的开发与验收导航](README.md#开发与验收)打开 Linux 开发指南，准备私有开发环境。以下测试使用独立 HOME/profile 和合成字段；它们不接触用户网页，也不下载翻译模型。

检查生产内容脚本的 DOM 行为时运行：

```sh
xvfb-run -a python tests/linux/browser_e2e.py --chromium /usr/bin/chromium -v
```

检查真实扩展、本地主机注册与发行包消息路由时，按浏览器选择一个入口：

```sh
xvfb-run -a python tests/linux/browser_native_e2e.py --package artifacts/linux/package --chromium /usr/bin/chromium -v
xvfb-run -a python tests/linux/firefox_native_e2e.py --package artifacts/linux/package --firefox /usr/bin/firefox --geckodriver /usr/bin/geckodriver -v
```

DOM 测试替换扩展消息管道，由 Chromium 执行实际 DOM、选区和事件。native messaging 测试加载实际发行扩展与 host，但 socket 对端只提供测试元数据批准。两者都不证明 niri 前台身份、本地模型和完整桌面链路通过；完整链路从 Linux 开发指南的桌面验收入口执行，入口见 [README 导航](README.md#开发与验收)。浏览器不能启动属于失败，不能跳过后记为通过。

维护时按职责查源码：

| 需要检查的行为 | 源码入口 |
| --- | --- |
| 字段、输入法、按键所有权、写回与浮层 | `src/shared/browser/content.js` |
| Tiptap 事务与隔离脚本通信 | `src/shared/browser/editor-adapter.js`、`src/shared/browser/editor-bridge.js` |
| 文档路由与本地主机 | `src/shared/browser/background.js`、`src/linux/desktop/browser_host.py` |
| 注册、当前字段能力与移除 | `packaging/linux/install.py`、`src/linux/desktop/application_support.py`、`src/linux/desktop/input.py` |

物理键盘、完整中文输入法、任意网页撤销语义及跨环境视觉仍需真实目标验收。测试覆盖与当前结果分开记录，旧运行通过不代表当前提交已通过。
