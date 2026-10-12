# Linux 原生父窗口锚点

状态：已采用，影响 `cachyos-preview` 的 GTK3、Qt6 与浏览器输入后端。

## 问题

原 layer-shell 浮层依赖 niri 提供输入应用的绝对窗口位置。实际嵌套 niri 26.04 的普通平铺窗口返回 `tile_pos_in_workspace_view: null`；GTK 客户端装饰尺寸也不等同于 niri 的窗口几何。此前的原生 E2E 和生产桌面 E2E 均在此失败。不能增加等待、猜测平铺列位置、放宽坐标校验或要求用户改用浮动窗口来掩盖问题。

niri 的 [上游问题 #2381](https://github.com/niri-wm/niri/issues/2381) 记录了平铺窗口位置缺失。窗口移动和工作区滚动期间的准确全局坐标不属于当前可用的客户端契约。

## 决策

对已有 GTK3 原生输入适配器，直接在目标控件的 GTK 线程中创建非模态 `GtkPopover`。它与真正的原输入框关联，不产生独立平铺窗口，不调用键盘抓取，不要求全局屏幕坐标。GTK 负责跟随、可见区域裁剪和父窗口缩放；点击译文获得共享控制器的编辑授权后，才显式将焦点交给译文控件。返回原框只操作仍处于活动状态的原 GTK 父窗口，不激活其他窗口。

输入观察中的 `RectPx` 是此目标 GTK 父窗口坐标系中的物理像素，不是桌面绝对坐标。原生协议显式标注 `space: gtk-toplevel`；只有绑定同一目标的原生视图能消费该锚点，不与 niri 输出坐标混合，也不送入独立 layer-shell 窗口的定位函数。客户端装饰不再靠边距反推。

Qt6 的标准控件采用父窗口内的普通子控件，坐标标为 `qt-toplevel`；浏览器浮层使用顶层文档内的隔离 shadow 内容，坐标标为 `browser-viewport`。每种后端只解释自己的坐标，不能在后端间复用锚点。Qt generic plugin 采用应用启动时的附加加载，不替换已有输入法上下文。浏览器扩展和 native host 由用户另行明确注册。

`Core`、`Runtime` 和模型 worker 不变。共享控制器仍决定状态、译文、人工草稿、选区及写入许可；Linux 桌面只转发视图模型和交互。原生视图不获得新的写入捷径，Tab 的一次性许可、未知 IME 拒绝、写前版本校验和不明结果隔离保持独立。视图自身的输入控件不被原生适配器再次发现。

本机 IPC 协议提升为 v2；旧模块必须随目标应用重新启动，不误用 v1 的坐标含义。视图有短期续租，连接断开、父窗口失活、源控件销毁或续租失效时撤下，不在宿主失效后留下可编辑的失联浮层。

## 限制与验证

这是各已实现后端的定位机制；GTK3、Qt6 标准 Widgets 和浏览器标准 DOM 分别有输入能力门控，不代表 GTK4、Qt Quick、Electron 或任意自绘字段获得支持，也不代表物理 CachyOS 已验收。保留普通平铺窗口上的真实输入、模型翻译和 Tab E2E；编辑返回、父窗口移动/缩放、滚动裁剪和视觉细节需分别验证，不用构建成功代替。

参考 GTK 官方的 [非模态语义](https://docs.gtk.org/gtk3/method.Popover.set_modal.html)、[控件相对锚点](https://docs.gtk.org/gtk3/method.Popover.set_pointing_to.html) 和 [位置约束](https://docs.gtk.org/gtk3/method.Popover.set_constrain_to.html)。不依赖未合并的 niri 补丁或不公开的 GTK ABI。
