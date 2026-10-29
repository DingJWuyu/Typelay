# 离线界面字体

字体仅在构建时准备，完整随包分发；运行时不访问字体服务、不安装系统字体。
`fonts.lock.json` 是文件集合、来源、字节数、SHA-256、族名与字重的唯一打包依据。
生成的 `manifest.json` 便于检查，但不能替代仓库中的锁。

```sh
python -m pip install -r eng/requirements.txt
python eng/fonts/prepare.py --output artifacts/ui-fonts
python eng/fonts/prepare.py --output artifacts/ui-fonts --verify
```

已有完整输出直接校验；不完整或损坏输出明确失败，不以平台回退代替缺失字体。
需要重新准备时指定尚不存在的输出路径（已存在空目录也会拒绝）。`--offline` 只使用已验证的本地源缓存，
不会下载；`--verify` 只需 Python 标准库。构建入口和组件打包应调用同一准备接口，
最终包保留所有锁定字体与许可证。修改此说明后也需更新锁中的说明文件哈希。

## 字体来源与范围

- Inter 4.1，400/500/600，保留仓库的上游原始静态文件。
- [Source Serif 4.004R](https://github.com/google/fonts/tree/7b203a635ebe80801c80f29633d4fc467cd1214e/ofl/sourceserif4)
  的固定原始变量源在构建时实例化为16个静态角色。正文400使用13–19共7个逻辑字号；
  标题、语言和样例500使用11、13.5、15、17、18、20、22、26、30共9个逻辑字号。
  两轴固定为 `wght=字重`、`opsz=逻辑字号`，不乘显示器DPI，不在产品中运行变量字体。
  用户族名为 `TypeLay Serif <字号>`，文件名为 `TypeLaySerif-<字号>-Regular/Medium.ttf`。
  原型没有600衬线角色，不以合成字重冒充500。
- [Noto Sans CJK 2.004](https://github.com/notofonts/noto-cjk/tree/523d033d6cb47f4a80c58a35753646f5c3608a78)
  与 [Noto Serif CJK 2.003](https://github.com/notofonts/noto-cjk/tree/9b0f1436e455d902de067a2501422e5dc71ad16b)：
  SC/JP/KR 各400/500，采用上游 region-specific SubsetOTF 原件，不再自行删字形。
- SC 无衬线600由同一固定上游 `NotoSansSC-VF.ttf` 使用 fontTools 4.61.1，
  固定唯一轴 `wght=600` 实例化为静态 TrueType。`recalcTimestamp=False` 保留固定时间，
  不额外 subset 或 rehint；源与输出均验证 SHA-256。产品不使用变量字体。
- 系统字体模式仍使用平台字体；未捆绑 TC/HK，不能声称其地域字形与原型一致。

SC600 是 TypeLay 生成的修改版本，不是上游原件，也不暗示上游背书。
其 PostScript 名为 `TypeLayNotoSansSC-SemiBold`，name3/5/10 记录生成来源，
family16 保留 `Noto Sans SC` 以匹配原始400/500；字重为600。
原始版权、OFL、name0/13/14 均保留。此上游 VF 声明的 Reserved Font Name
是 `Source`，并非 `Noto`；派生用户族名不使用该保留名称。
[OFL FAQ 5.7](https://openfontlicense.org/ofl-faq/#5-7) 说明没有默认保留名。
`Noto-CJK-LICENSE.txt` 是两固定上游版本相同的原始 OFL 文本，未经改写。

该资源验证不证明实际排版通过。各平台仍需验证实际选中的随包 face/字重、
无合成粗体、各语言断行，以及 WPF、GTK、Qt、浏览器各自的真实呈现。

## 衬线静态角色与验证边界

Source Serif 派生面使用同一固定 fontTools 4.61.1、完整字形集与原始hinting；不subset或rehint。
原始版权与name0/13/14、原版4.004 OFL随包保留；name3/5/10标明修改来源，
name1/4/6/16/17不使用保留字体名Source，不暗示上游对派生面的认可。
保留有效的静态STAT值；只有实例化后为空的STAT轴描述才删除。

16角色加Inter3面和Noto13面共32面。界面代码使用固定字号族及原区域CJK回退，
系统字体模式仍走平台字体。没有对应角色的调用使用明确有限回退，不称精确光学匹配。
生成与锁校验只证明资产身份；Pango live-widget-context shaping、Qt实际document glyph runs、
WPF实际GlyphTypeface和浏览器实际textarea字体检查分别证明自己的边界，不互相替代。
光学轴映射依据固定本地上游源与Chromium auto对照；原始参考HTML远程字体加载失败的截图
只用于布局，不作为已加载该字体的证明。静态角色与变量auto的逐像素比较未完成时须如实注明。
