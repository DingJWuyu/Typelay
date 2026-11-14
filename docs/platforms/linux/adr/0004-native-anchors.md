# Linux 原生父窗口锚点

状态：已采用。适用于 Linux 的 GTK3、GTK4、Qt6 与浏览器输入后端。

TypeLay 的浮层由源应用或网页持有，使用对应父窗口或视口内的锚点。客户端不依赖合成器提供输入窗口的桌面绝对位置，也不把不同后端的坐标混用。

## 问题

独立 layer-shell 浮层需要输入窗口的全局位置，当前可用契约不能稳定提供这一信息。嵌套 niri 的普通平铺窗口曾返回 `tile_pos_in_workspace_view: null`，客户端装饰尺寸也不能替代窗口几何。窗口移动和工作区滚动进一步增加坐标失真的风险，相关限制见 [niri 问题 #2381](https://github.com/niri-wm/niri/issues/2381)。

增加等待、猜测平铺列位置或由装饰边距反推坐标都不能补齐契约。要求用户改用浮动窗口也会改变产品使用条件。

## 决策

GTK3 适配器在目标控件的 GTK 线程中创建非模态 `GtkPopover`，关联真实源输入框。GTK4 使用同样的父控件内定位原则。工具包处理跟随、可见区域裁剪和缩放，浮层不产生独立平铺窗口，也不抓取键盘。GTK3 的公开 API 提供[非模态语义](https://docs.gtk.org/gtk3/method.Popover.set_modal.html)、[控件相对锚点](https://docs.gtk.org/gtk3/method.Popover.set_pointing_to.html)和[位置约束](https://docs.gtk.org/gtk3/method.Popover.set_constrain_to.html)。

Qt6 使用源窗口拥有的子控件浮层。浏览器使用顶层文档内的隔离 Shadow Root。浏览器扩展与 native host 仍由用户单独明确注册。

各后端在原生协议中声明坐标空间，只有绑定同一源目标的视图可以消费：

| 后端 | 协议坐标空间 | 原生几何的含义 |
| --- | --- | --- |
| GTK3 | `gtk-toplevel` | GTK 父窗口内的逻辑坐标。 |
| GTK4 | `gtk4-toplevel` | GTK4 父窗口内的逻辑坐标。 |
| Qt6 | `qt-toplevel` | Qt 父窗口内的逻辑坐标。 |
| 浏览器 | `browser-viewport` | 当前顶层文档视口内的 CSS 坐标。 |

原生消息另带 `scale`。Linux 的 `anchor_geometry` 校验空间、缩放和裁剪范围后，转换为共享 `RectPx` 的物理像素。转换后的值仍属于原父窗口或视口，不是桌面绝对坐标。它不能与 niri 输出坐标混合，也不能送入独立 layer-shell 窗口的定位函数。

共享 Core、Runtime 和模型 worker 的职责保持不变。共享控制器决定状态、译文、人工草稿、选区及写入许可。Linux 桌面把呈现和交互路由到相应的原生视图。点击译文取得编辑授权后，原生视图才把焦点交给译文控件；回源只操作仍处于活动状态的原父窗口，不激活其他窗口。适配器排除浮层自己的输入控件，避免递归发现。

本机协议使用 v2。旧模块须随目标应用重新启动，不能按 v1 坐标含义解释新消息。视图持有短期租约，断连、父窗口失活、源控件销毁或租约失效时撤下，不能留下失联的可编辑浮层。

## 限制与验证

此决策只改变定位，不授予新的写入能力。按次身份、输入法未知时拒绝、短期许可、写前版本复核及结果未知隔离继续独立成立。视图退休也不结束已派发写入，结果结算和未用许可退休仍由各自执行者证明。

通用未合作 GTK 路径保持 Unknown。合作组件的当前控件范围与版本条件见 [Fcitx](../fcitx.md)、[Qt](../qt.md)和[浏览器说明](../browser.md)，不能外推到任意自绘字段或 Electron。

验收需要分别观察编辑回源、父窗口移动与缩放、滚动裁剪和实际视觉。编译、协议回归或其他提交的桌面证据都不能替代当前目标环境；当前已测与未测范围见[GitHub Issues](https://github.com/DingJWuyu/Typelay/issues)。

维护坐标契约时，核对 [`gtk3/gtk-target.c`](../../../../src/linux/native/gtk3/gtk-target.c)、[`gtk4/gtk-target.c`](../../../../src/linux/native/gtk4/gtk-target.c)、[`qt-module.cpp`](../../../../src/linux/native/qt6/qt-module.cpp)和 [`content.js`](../../../../src/shared/browser/content.js)的几何输出，以及 [`geometry.py`](../../../../src/linux/desktop/geometry.py)的转换。此方案不依赖未合并的 niri 补丁或不公开的 GTK ABI。
