# Linux 浏览器输入适配器

此适配器通过浏览器扩展连接当前 Wayland 会话中的 TypeLay。代码实现不等于浏览器、真实桌面或物理输入法已经通过验收。本地 native messaging host 不启动模型、不监听网络端口、不保存正文；源文和译文只经过浏览器私有消息管道与 TypeLay 同用户 socket。普通安装不会自动注册 host、安装扩展或授予网页权限。

## 范围与限制

现有实现覆盖顶层 HTTP(S) 网页里的标准 `input[type=text/search]` 和 `textarea`。字段仍须通过共享敏感元数据、应用排除及编辑能力检查。密码、信用卡/一次性验证码元数据字段不读取正文；隐藏、禁用、只读或已经移除的控件被拒绝。

另有 Tiptap 的单段富文本路径：仅支持实际编辑器公开实例绑定的一个 paragraph，段内允许普通文字及 marks；多段、列表、代码、mention、atom 和 hardBreak 不猜测映射。替换经编辑器原生事务完成，保留范围外结构和宿主默认撤销分组。MAIN-world 小桥只操作该页面编辑器，不提供模型或 native host 命令；隔离脚本仍负责可信 IME、许可与元数据校验。公开 beforeTransaction 阶段前后复核原状态及 DOM，遇回调重入变化拒绝继续，进入页面 dispatcher 后无法确认结果则隔离。Tiptap 2.11.5 是当前真实回归版本，自定义 dispatcher 和其他版本仍需实际验证。

iframe、Shadow DOM 源字段、其他 contenteditable / 富文本、自绘编辑器、浏览器地址栏和内部页面没有输入实现。浏览器扩展不会自动为 Electron 或 VS Code 提供支持。普通网页上的标准控件不是应用白名单；网页可能使用自定义事件处理、受控组件、CSS 根变换或私有编辑状态，这些情况必须分别验证，不能凭最终 DOM 字符串宣称兼容。

Chrome 新标签页中央的 Google / Ask Google 搜索框与 google.com 普通网页搜索框不是同一验收目标。前者尚未验证，输入焦点可能转到浏览器地址栏；普通网页扩展通过不证明新标签页或其他受保护内部页面可用。应用支持页的浏览器状态只表示最近检查到的具体网页字段能力。

输入法状态初始为 Unknown。只有可信的非组合键盘/输入事件或 compositionend 才建立明确状态；普通窗口/标签页失焦保留持续监听的已知状态并撤销许可；页面冻结/恢复或丢失连接后重新确认。组合期和未知状态不读取正文、不发布可接受候选，不通过“未看到候选窗”推断 Clear。因此刚获焦、尚无可靠输入观察的字段可能暂时没有胶囊。

标准输入框写回使用浏览器原生 `setRangeText`，在同一 JavaScript 事件循环中核对精确 DOM 对象、生命周期、元数据、内容/输入/焦点版本、选区、原文和短期许可，然后同步替换。不会写剪贴板、模拟粘贴、补发 Tab、自动发送或提交表单。替换后发送标准 input 通知；网页处理器若再次改变值、选区、身份或编辑能力，则回报 OutcomeUnknown 并隔离该字段，绝不重试或覆盖网页的新结果。此机制不保证所有框架的内部状态或撤销栈语义；需要目标网页的实际验收。

浮层使用文档视口坐标，在当前页面的封闭 Shadow Root 中呈现，不推算 Wayland 全局坐标、不推动页面布局。浏览器窗口/标签页失焦、字段隐藏、租约到期或 host 断开时撤下。根元素经过变换而无法可靠映射固定定位时拒绝使用。

## 显式安装

先安装 Linux 发行包并启动 TypeLay。扩展源码随包放在 `browser/firefox` 与 `browser/chromium`，两者均包含对应 `manifest.json`。保持这个发行目录可用；浏览器加载解压扩展后会引用其文件。

优先使用设置中的“应用支持”：选择浏览器，按接入说明打开浏览器，并复制管理页地址和组件目录。Chromium 不允许外部启动命令直接打开扩展管理内部页，需要用户把 `chrome://extensions/` 粘贴到地址栏后打开；TypeLay 不添加安全绕过参数。两个“复制”按钮只在明确点击时写入相应固定内容，不代表扩展已经安装。

在浏览器加载扩展并批准网页访问后，回到 TypeLay 选择“连接”。Chromium/Chrome 只采用标准浏览器配置中与当前包目录完全匹配的唯一扩展记录，不需要用户手填扩展 ID；找不到或存在多个记录时保持未连接，并说明下一步。登记成功也不等于已经连接，需保存工作并刷新目标网页，等待真实握手和字段检查。

下面的命令是手动管理方式，与设置入口二选一，不需要两套都执行。

Firefox：

```sh
python install.py browser-install --browser firefox
```

在 Firefox 的 `about:debugging#/runtime/this-firefox` 选择“临时载入附加组件”，打开发行包 `browser/typelay-firefox-unsigned.xpi`，也可选择 `browser/firefox/manifest.json`。开发模式临时扩展在浏览器退出后失效；XPI 是包含实际发行脚本的可复现未签名包，可用于临时加载或交给 Mozilla 签名流程；这里没有提供已签名的商店扩展，不能承诺稳定 Firefox 中的永久安装，也不会修改浏览器签名校验设置。扩展 ID 固定为 `typelay@typelay.local`。

Chromium：先在 `chrome://extensions` 打开开发者模式，加载发行包 `browser/chromium` 文件夹。复制浏览器显示的 32 字母扩展 ID，再执行：

```sh
python install.py browser-install --browser chromium --extension-id 这里替换为浏览器显示的ID
```

Google Chrome 使用 `--browser google-chrome`。不会猜测扩展 ID，也不注册通配来源。加载扩展和网页访问权限必须由用户在浏览器中明确批准。安装后重新载入目标网页；浏览器应继承与 TypeLay 相同的 Wayland/niri 会话环境，否则不能连接另一个会话的服务。

扩展连接 host 的名称为 `org.typelay.browser`。host 程序从自身文件位置定位发行根，不依赖浏览器传入 TYPELAY_HOME。若浏览器通过容器、Flatpak 或另外的进程命名空间启动，系统 host 或同用户父进程链可能不可达；这类情况保持不可用，不绕开认证。

## 使用与卸载

翻译模式、语言方向、排除项及原生写回开关沿用 Linux 设置。已确认输入法状态后，自动模式跟随源字段；按需模式使用设置中的快捷键。只有当前有效许可才拦截真实裸 Tab。点击候选先请求编辑授权；编辑后第一次 Tab 只返回原输入框，下一次 Tab 才接受。Ctrl/Alt/Shift/Meta 修饰键、重复键、组合输入和不可信合成按键不发起新的接受。

设置中的“移除”只撤销本安装拥有的本地通信注册，不操作浏览器扩展权限，也不结束浏览器进程。若现有会话仍在线，页面会提示需要保存工作并完整退出浏览器；删除注册本身不会立即断开旧连接，关闭单个标签页也不保证会话结束。无需继续使用时，再到浏览器移除扩展。

也可以手动注销指定 host：

```sh
python install.py browser-uninstall --browser firefox
python install.py browser-uninstall --browser chromium
python install.py browser-uninstall --browser google-chrome
```

完整 TypeLay 卸载会清理本安装拥有的 host 注册和 wrapper，不改其他扩展。浏览器中的扩展本身仍需用户移除。连接断开会撤下页面浮层并撤销许可。

## 开发验证

生产内容脚本的真实 DOM 回归只替换扩展消息管道，DOM、选区、事件循环、键盘事件及组合事件由 Chromium 执行。测试使用独立临时 HOME 和浏览器 profile，不安装扩展、不接触用户网页、不下载模型：

```sh
python -m pip install playwright
xvfb-run -a python tests/linux/browser_e2e.py --chromium /usr/bin/chromium -v
```

真实扩展和发行包 host 的最小链路另行运行：

```sh
xvfb-run -a python tests/linux/browser_native_e2e.py --package artifacts/linux/package --chromium /usr/bin/chromium -v
```

Firefox 的真实临时扩展与本地主机检查使用官方 Firefox/geckodriver，不依赖 Playwright 的 Firefox 扩展支持：

```sh
xvfb-run -a python tests/linux/firefox_native_e2e.py --package artifacts/linux/package --firefox /usr/bin/firefox --geckodriver /usr/bin/geckodriver -v
```

它使用独立临时 HOME/profile，通过 WebDriver 安装本次测试的临时附加组件，并连接发行包原生 host。该范围是实际加载、协议、字段读写与身份校验，不等于 Firefox+niri+真实模型完整链路或物理输入法验收。

该检查在临时 HOME 安装发行包，显式加载测试扩展，再通过生产连接操作读取唯一的实际扩展记录并注册，使用真实后台脚本、native messaging、host wrapper 与 socket，验证首次握手、重连、跨标签页路由及控件销毁身份；完成后关闭整个测试浏览器并删除临时目录。socket 对端只提供测试用元数据批准，不是假翻译器，也不代表 niri 前台身份或模型链路验收。

DOM 检查覆盖未知 IME、可信输入后的接受、旧原文、DOM 重建、组合撤销、UTF-16 选区、网页重入改写、密码字段和合成 Tab 拒绝。单独的 DOM 检查不验证 Firefox、native messaging 安装路由、niri 前台身份、物理中文 IME、真实键盘、撤销栈或视觉一致性。Chromium 进程无法启动时检查失败，不跳过成成功。上述未覆盖项仍需对应真实环境验证。

底层依据：[Chromium 启动地址校验](https://github.com/chromium/chromium/blob/154.0.8037.97/chrome/browser/ui/startup/url_util.cc)、[native messaging](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Native_messaging)、[Chrome native messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)、[标准字段 setRangeText](https://developer.mozilla.org/en-US/docs/Web/API/HTMLInputElement/setRangeText)、[compositionstart](https://developer.mozilla.org/en-US/docs/Web/API/Element/compositionstart_event)。
