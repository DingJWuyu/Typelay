# 从新工作树准备 Linux 验证

本页只准备工具、包和下载缓存，不在用户桌面启动应用。所有命令从仓库根目录执行。生产构建规则仍由 [Linux 开发指南](../../../../docs/platforms/linux/development.md)和 `eng/linux/` 维护。

## 网络预算与本机缓存

先按任务授权检查网络预算。没有下载授权时，仅使用已存在的工具、SDK、字体、运行库与下载缓存；下方任何 curl、安装或首次构建步骤缺少本地字节时，停止并报告，不能自行下载。构建也可放进 `unshare --user --map-current-user --net`，以防工具隐式联网。

NuGet 漏洞审计由每次 push 的 CI 执行。本机验证构建可仅关闭本次 restore 的审计，在断网命名空间内使用本地 NuGet 包缓存。其余警告仍视为错误，CI 配置保持审计开启。先准备本页要求的工具与原始下载缓存，再执行下方[构建当前源码](#构建当前源码)的离线命令。

包缺失时停止并报告，不增加远端源。这里的成功只证明本机验证包构建，不代表 CI 漏洞审计通过。

私有会话默认从 Git common directory 所属主检出的 `artifacts/` 复制下载缓存。在当前机器上，uv 位于 `/home/liam_t/项目/Typelay/artifacts/install-cache/uv`，Index 位于 `/home/liam_t/项目/Typelay/artifacts/index-model-download`。其他位置通过外层 `--install-cache` 与 `--model-cache` 指定。缓存无需复制进每个工作树，更不能复制已安装 venv。

uv 不把 CPython 原始归档放入 wheels 缓存。已有原始归档应按上游 `<release>/<filename>` 布局放入 `artifacts/install-cache/python/`，私有会话会将其作为正式 uv 的本地文件镜像。只有 wheels 与模型仍不足以证明离线安装可行；运行正式安装器检查，缺少原始 Python 下载字节时保留失败。网络规则、opt-in 和证据见 [SKILL.md](../SKILL.md#network-and-caches)。

## 找到依赖

系统依赖以 `.github/workflows/check.yml` 的 Linux 安装步骤和 `docs/platforms/linux/fcitx.md` 的固定版本预检为准。GTK 旅程还需要 `gtk3-demos`、`xorg-server-xvfb`、`xdotool`、`xorg-xauth`、`grim`、`at-spi2-core`。先只读检查：

```bash
pacman -Q niri gtk3 gtk4 python python-gobject at-spi2-core \
  fcitx5 fcitx5-chinese-addons libime cmake pkgconf
command -v dotnet uv dbus-run-session gdbus xauth
/usr/bin/python3 -c 'import gi; gi.require_version("Atspi", "2.0"); from gi.events import GLibEventLoop'
dotnet --version
cat global.json
```

这台机器曾把缺少的桌面工具、GCC 15 和匹配系统的 GLib 开发工具解包到用户目录。下面从实际文件发现路径，不读取旧工作树的环境文件。只使用本人维护的工具目录。

```bash
desktop_tool=$(find "$HOME/.local/opt" -path '*/usr/bin/Xvfb' -type f -print -quit)
glib_tool=$(find "$HOME/.local/opt" -path '*/usr/bin/glib-genmarshal' -type f -print -quit)
gcc_tool=$(find "$HOME/.local/opt" -path '*/usr/bin/gcc-15' -type f -print -quit)
test -n "$desktop_tool" && test -n "$glib_tool" && test -n "$gcc_tool"
desktop_prefix=${desktop_tool%/bin/Xvfb}
glib_prefix=${glib_tool%/bin/glib-genmarshal}
export PATH="$glib_prefix/bin:$desktop_prefix/bin:$PATH"
export LD_LIBRARY_PATH="$desktop_prefix/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export CMAKE_PREFIX_PATH="$desktop_prefix${CMAKE_PREFIX_PATH:+:$CMAKE_PREFIX_PATH}"
export PKG_CONFIG_PATH="$desktop_prefix/lib/pkgconfig:$desktop_prefix/share/pkgconfig${PKG_CONFIG_PATH:+:$PKG_CONFIG_PATH}"
export CC="$gcc_tool"
export CXX="${gcc_tool%gcc-15}g++-15"
mkdir -p artifacts/verification-tools/compiler
ln -sf "$CC" artifacts/verification-tools/compiler/cc
ln -sf "$CXX" artifacts/verification-tools/compiler/c++
export PATH="$PWD/artifacts/verification-tools/compiler:$PATH"
command -v Xvfb xvfb-run xdotool grim gtk3-demo glib-genmarshal
"$CC" --version
```

如果没有这些用户级工具，按以下步骤产生新的前缀。先核对仓库软件包版本与已装 GTK、GLib、Boost 的 ABI 相符，不能用解包绕过版本要求。`pacman -Sp` 只查询下载地址，不安装系统包。下面使用发行包签名验证工具来源：

```bash
mkdir -p artifacts/verification-tools/downloads artifacts/verification-tools/native
native_prefix="$PWD/artifacts/verification-tools/native"
for name in xorg-server-xvfb xdotool grim gtk3-demos extra-cmake-modules \
  boost boost-libs nlohmann-json wayland-protocols plasma-wayland-protocols glib2-devel gcc15; do
  url=$(pacman -Sp --print-format '%l' "extra/$name" | tail -n 1)
  archive="$PWD/artifacts/verification-tools/downloads/${url##*/}"
  curl --fail --location "$url" --output "$archive"
  curl --fail --location "$url.sig" --output "$archive.sig"
  gpgv --keyring /etc/pacman.d/gnupg/pubring.gpg "$archive.sig" "$archive"
  tar -xf "$archive" -C "$native_prefix"
done
export PATH="$native_prefix/usr/bin:$PATH"
export LD_LIBRARY_PATH="$native_prefix/usr/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export CMAKE_PREFIX_PATH="$native_prefix/usr${CMAKE_PREFIX_PATH:+:$CMAKE_PREFIX_PATH}"
export PKG_CONFIG_PATH="$native_prefix/usr/lib/pkgconfig:$native_prefix/usr/share/pkgconfig${PKG_CONFIG_PATH:+:$PKG_CONFIG_PATH}"
export CC="$native_prefix/usr/bin/gcc-15"
export CXX="$native_prefix/usr/bin/g++-15"
```

解包后，`wayland-protocols.pc` 可能仍将 `pkgdatadir` 指向 `/usr`。只修正**自有解包目录**里的路径，然后检查协议文件可读：

```bash
sed -i "s|^prefix=/usr$|prefix=$native_prefix/usr|" \
  "$native_prefix/usr/share/pkgconfig/wayland-protocols.pc"
test -r "$(pkg-config --variable=pkgdatadir wayland-protocols)/stable/xdg-shell/xdg-shell.xml"
```

这一备用步骤需要发行仓库和本机 keyring 可用。若系统 GTK/GLib/Boost 版本与仓库不匹配，使用签名可验证的匹配版本包，或报告环境阻塞；不要升级用户正在使用的桌面。缺失系统 PyGObject、niri 或输入法固定数据时，也应报告具体前提。

## 创建本工作树的工程环境

```bash
uv python list --only-installed
uv venv --offline --python 3.12 artifacts/build-tools
uv pip install --offline --python artifacts/build-tools/bin/python \
  -r eng/requirements.txt ninja==1.13.2
export PATH="$PWD/artifacts/build-tools/bin:$PATH"
```

本机缓存中的固定 fonttools wheel 适配 CPython 3.12。其他机器按本地 wheel 的 Python ABI 选择已安装的解释器。正式包在 `support/uv/` 携带模型安装器使用的固定 uv。新工作树还须复制字体与 Index CPU 运行库的构建输入。主包会校验这些本地字节。

```bash
shared="$(dirname "$(git rev-parse --path-format=absolute --git-common-dir)")/artifacts"
mkdir -p artifacts/linux
cp -a --reflink=auto "$shared/font-sources" "$shared/ui-fonts" artifacts/
cp -a --reflink=auto "$shared/linux/uv-download" "$shared/linux/index-cpu-download" artifacts/linux/
```

普通 GTK 旅程使用系统 `/usr/bin/python3`。浏览器旅程还需要能在同一解释器加载的 Playwright，见下方的[浏览器工具](#浏览器工具)。

.NET SDK 必须满足 `global.json`。如果 `dotnet --version` 不匹配，在本工作树生成工具目录：

```bash
mkdir -p artifacts/verification-tools/dotnet
curl --fail --location https://dot.net/v1/dotnet-install.sh \
  --output artifacts/verification-tools/dotnet-install.sh
sdk_version=$(/usr/bin/python3 -c 'import json; print(json.load(open("global.json"))["sdk"]["version"])')
bash artifacts/verification-tools/dotnet-install.sh --version "$sdk_version" \
  --install-dir "$PWD/artifacts/verification-tools/dotnet"
export PATH="$PWD/artifacts/verification-tools/dotnet:$PATH"
dotnet --version
```

### 离线运行共享检查

`dev.ps1 shared-check` 使用 PATH 上的 `python` 发现共享测试，其中的工作流检查用 PyYAML 读取真实工作流。用工程解释器安装仓库固定的共享测试依赖。VSTest 的测试宿主通过本地 TCP 回连，新的网络命名空间默认关闭回环接口。只在新命名空间内启用 `lo`，然后在同一个命名空间执行完整检查。

```bash
uv pip install --offline --python artifacts/build-tools/bin/python -r tests/shared/requirements.txt
export PATH="$PWD/artifacts/build-tools/bin:$PATH"
python -c 'import yaml; print(yaml.__version__)'
unshare --user --map-current-user --keep-caps --net \
  env NuGetAudit=false RestoreSources="$HOME/.nuget/packages" \
  sh -ec 'ip link set lo up; exec pwsh -File ./dev.ps1 shared-check'
```

### 浏览器工具

在外层终端准备能导入系统 PyGObject 的解释器。下面使用本机已有的 Playwright 1.65.1 及其对应的 Chromium。其他机器可选择支持同一 CDP 参数且有本地浏览器的版本。Playwright 的包和浏览器分别缓存，单独安装 Python 包不足以启动浏览器。

```bash
uv venv --offline --system-site-packages --python /usr/bin/python3 artifacts/desktop-test-tools
browser_python="$PWD/artifacts/desktop-test-tools/bin/python"
uv pip install --offline --python "$browser_python" playwright==1.65.1
browser_exe=$("$browser_python" - <<'PY'
from inspect import signature
from gi.events import GLibEventLoop
from playwright.sync_api import BrowserType, sync_playwright

assert "no_defaults" in signature(BrowserType.connect_over_cdp).parameters
with sync_playwright() as driver:
    print(driver.chromium.executable_path)
PY
)
test -x "$browser_exe" || { echo "缺少 Chromium 可执行文件 $browser_exe" >&2; exit 1; }
case "$(basename "$(realpath "$browser_exe")")" in
  chrome|google-chrome) browser_kind=google-chrome ;;
  chromium|chromium-browser) browser_kind=chromium ;;
  *) echo "请核对浏览器进程身份：$browser_exe" >&2; exit 1 ;;
esac
```

若使用本机已安装的 Chromium，可将 `browser_exe` 设为其实际绝对路径，再检查文件并选择匹配的注册类型。保留这三个 shell 变量供[网页写回](../features/writeback.md#driving-it-with-private-niri)和[原生 Undo](../features/undo.md#driving-it-with-private-niri)命令传参。私有 HOME 有自己的缓存目录，须在进入会话前确定浏览器路径。缺少本地 wheel 或可执行文件时，报告解析器给出的包名、版本或上述路径。

## 构建当前源码

主包和合作包都从当前工作树生成。保留构建日志；构建失败时保留输出，不把旧包填进新输出目录。若复用按下列命令生成的合作归档，必须经过 `pack.py` 的输入与二进制核验，以及 `--finalize` 对当前源码和归档闭包的核验；记录它的来源。

先准备一份[平铺原始归档缓存](../../../../docs/platforms/linux/development.md#合作构建的本地原始归档)。已有完整构建目录时，下面命令只复制原始归档，输出目录不参与缓存。两种构建器随后共用 `archives`。缓存缺项时停止，报告具体文件路径。

```bash
archives="$PWD/artifacts/cooperative-archives"
mkdir -p "$archives"
find /home/liam_t/项目/Typelay/artifacts/linux/fcitx-build-gcc15-cpu10 \
  -type f \( -name '*.tar.gz' -o -name '*.tar.zst' \) \
  -exec cp --reflink=auto -t "$archives" -- {} +
cp --reflink=auto /home/liam_t/项目/Typelay/artifacts/linux/qt-cooperative/fcitx5-qt.tar.gz \
  "$archives/"
```

本机所有合作构建，包括测试 fixture 和 Qt 插件，都使用 GCC 15 和 `taskset -c 10`。同一台机器上有多个任务并行时，所有任务须用同一把锁串行合作构建。锁的绝对路径由派任务的人提供，执行前将它赋给 `TYPELAY_COOP_LOCK`。下方合作构建命令使用这一路径。私有桌面内编译时，先在外层记录工具的绝对路径，再在旅程脚本中设置构建环境；私有 HOME 中没有宿主的工具目录。

```bash
mkdir -p artifacts/verification-build
unshare --user --map-current-user --net artifacts/build-tools/bin/python eng/fonts/prepare.py
unshare --user --map-current-user --net \
  env NuGetAudit=false RestoreSources="$HOME/.nuget/packages" \
  artifacts/build-tools/bin/python eng/linux/build.py --with-test-fixture --compression-level 1 \
  --output artifacts/verification-build/package \
  > artifacts/verification-build/main.log 2>&1
flock "${TYPELAY_COOP_LOCK:?请先设置派任务者提供的共享锁绝对路径}" taskset -c 10 \
  unshare --user --map-current-user --net \
  artifacts/build-tools/bin/python \
  eng/linux/cooperative/fcitx.py --archive-cache "$archives" \
  --output artifacts/verification-build/fcitx \
  > artifacts/verification-build/fcitx.log 2>&1
flock "${TYPELAY_COOP_LOCK:?请先设置派任务者提供的共享锁绝对路径}" taskset -c 10 \
  unshare --user --map-current-user --net \
  artifacts/build-tools/bin/python tests/linux/fcitx_cooperative/run.py \
  --build-only --reuse-build --scope core --output artifacts/verification-build/fcitx \
  > artifacts/verification-build/fcitx-fixtures.log 2>&1
unshare --user --map-current-user --net \
  artifacts/build-tools/bin/python packaging/linux/fcitx-cooperative/pack.py \
  --build-output artifacts/verification-build/fcitx \
  --output artifacts/verification-build/TypeLay-Fcitx-cooperative.tar.gz \
  --compression-level 1 \
  > artifacts/verification-build/pack.log 2>&1
unshare --user --map-current-user --net \
  artifacts/build-tools/bin/python eng/linux/build.py --finalize --compression-level 1 \
  --output artifacts/verification-build/package \
  --cooperation-package artifacts/verification-build/TypeLay-Fcitx-cooperative.tar.gz
mkdir -p artifacts/verification-build/cooperation
tar -xzf artifacts/verification-build/TypeLay-Fcitx-cooperative.tar.gz \
  -C artifacts/verification-build/cooperation
```

CPU 10 是这台机器已成功完整构建的环境绕法，使用前以 `taskset -pc $$` 确认它在允许集合内。GCC 16、GCC 15 和 clang 都曾发生间歇性编译器崩溃，原因尚未确认。不能把编译器崩溃当作产品编译错误，也不能修改固定源码或补丁让环境绕法通过。其它机器不要求使用 CPU 10。

正式构建器从缓存复制、校验并解压原始归档，再应用当前补丁。缓存只读，缺项、未知归档或摘要不符均明确失败。拼写字典也由正式构建器供给并校验，无需向输出源码树预放文件。`cc` 与 `c++` 均须解析为 GCC 15。

上方 `--reuse-build --build-only --scope core` 复验生产字节并编译核心 fixtures，供 Qt 的构建身份检查使用。这一步不执行桌面场景，也不证明合作测试通过。

需要 Qt 时，在全新输出目录调用相同的缓存参数：

```bash
flock "${TYPELAY_COOP_LOCK:?请先设置派任务者提供的共享锁绝对路径}" taskset -c 10 \
  unshare --user --map-current-user --net \
  artifacts/build-tools/bin/python eng/linux/cooperative/qt.py \
  --archive-cache "$archives" --out artifacts/linux/qt-cooperative \
  > artifacts/verification-build/qt.log 2>&1
```

随后按 [Qt 功能地图](../features/qt.md#driving-it-with-private-niri)运行 `--reuse-build` 完整 probe。该参数复验生产源码、ABI、补丁和二进制，并重新编译测试 fixture。

`--finalize` 要求生产输入干净且与主包身份一致。文档或技能变更应先提交再构建，以便记录明确 SHA。生产代码未提交时，可以构建 setup-only 主包配合独立合作包定位问题，但报告必须保留 `sourceDirty`，不能宣称完整发行包已封装。

## 明确准备模型缓存

仅当本机没有固定缓存、且负责人明确授权模型下载时，才执行本节命令。首次准备需要约 1.22 GiB 的 Index 权重下载，安装器还会准备私有 Python、依赖、候选代和缓存。确认本次任务允许模型准备后执行；本技能的完整预览证明需要该步骤。

```bash
artifacts/build-tools/bin/python eng/shared/prepare_index_model.py \
  --output artifacts/index-model-download
```

该入口按仓库固定摘要下载权重和许可证。可以复用相同内容的下载缓存；实际模型必须由 Launch 在新的最终数据路径安装，不能复制其它工作树的 `components/current` 或虚拟环境。

回到 [Launch](../SKILL.md#launch)运行。主包与模型默认留在正常文件系统，tmpfs 对照的范围见[模型 Gotchas](../features/models.md#gotchas)。
