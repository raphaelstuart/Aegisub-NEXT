# 特效脚本

[English](../en/effect-dsl.md) · [简体中文](effect-dsl.md) · [全部指南](README.md)

## 应用与编辑

1. 选择字幕，在**特效**中选择预设并点击**应用预设**。
2. 打开**设置 → 特效脚本**管理模板；内置模板只读，**另存为**创建个人副本。
3. 编辑后**验证**，再保存或导出 UTF-8 `.aegifx`；诊断提供行/列，**定位错误**移动光标。

输入时或 Cmd/Ctrl + Space 打开补全，方向键选择，Enter/Tab 插入，Esc 关闭。模板属于个人设置，应用时按当前 Clip 时长重新编译，并形成一条 Undo。

DSL 版本 1 保留已有的整目标时间段语法。版本 2 增加命名作用范围、文字分组、错峰启动、重复和 pingpong。同一预设入口提供内置**逐字弹跳**与**逐字放大往返**；另存为个人副本后可修改分组和时间。

## 组合应用预设

可以连续应用多个预设。每个预设只覆盖它在某个时间段内明确声明的完整属性目标；没有声明该目标的区间保留已有动画及其插值曲线。没有已有轨道时，空段沿用脚本的基础值或前值。段名 `stay`、`hold` 没有特殊含义，`flex` 段也可以包含实际动画。版本 2 生成的文字组还遵循下文的生成结果替换规则。

例如对 4 s Clip 先应用内置淡入，再应用内置淡出：前 300 ms 的淡入保留，中间动画保留，最后 300 ms 加入淡出。反向应用也得到相同结果。一次批量应用只有一条 Undo；保存并重新打开后仍能继续组合。

空停留段没有声明任何属性：

```text
segment stay flex 1
end
```

下面的停留段则明确要求将透明度固定为基础值，因此会覆盖已有透明度动画：

```text
segment stay flex 1
    at 0 opacity base hold
    at 1 opacity base
end
```

`hold` 是关键帧插值，不表示忽略该区间；`base` 始终读取静态基础属性或字幕样式，不读取旧动画在接合点的值。接合点两侧的值必须相等，否则整次应用失败，工程与 Undo/Redo 保持不变。可调整预设端点，或先清除对应轨道。

多个独立预设各自按 Clip 时长分配时间，不共同压缩固定段。两个 300 ms 进出场在 600 ms Clip 中可以直接接合；在 400 ms Clip 中会重叠，内置淡入接淡出因接合值不同而拒绝。在不超过 300 ms 的 Clip 中，每个预设都覆盖全片，后应用的预设完整替换前者。若希望短 Clip 同时保留进出场并共同压缩，请使用包含两段的单个预设，例如内置淡入淡出。

## 第一个脚本

```text
effect "fade-in-out" version 1
short-clip compress

segment enter fixed 300ms
    at 0 opacity 0 ease-out
    at 1 opacity base
end

segment stay flex 1
    at 0 opacity base hold
    at 1 opacity base
end

segment exit fixed 300ms
    at 0 opacity base ease-in
    at 1 opacity 0
end
```

`fixed` 保留进出场时长，`flex` 按权重分配剩余时间；至少需要一个弹性段。段内位置为 0–1，声明的每个属性必须覆盖两端且位置递增。

`short-clip compress` 在时长不足时按比例压缩固定段，`reject` 拒绝应用。上例 5 s Clip 停留 4.4 s，400 ms Clip 压缩为进出场各 200 ms；相邻共享端点必须相等，包括时长为零的弹性段。

## 版本 2：按组执行文字特效

下面的个人模板将每个字素放大再还原，每组比前一组晚 60 ms 开始：

```text
effect "personal-letter-pulse" version 2
short-clip compress

scope letters current
    unit grapheme
    stagger 60ms
    segment pulse fixed 150ms pingpong
        at 0 scale base ease-in-out
        at 1 scale factor(1.25, 1.25)
    end
    segment rest flex 1
    end
end
```

`fixed 150ms` 是单程时长，`pingpong` 补充按原曲线反向返回的单程，因此一次放大为 300 ms：局部缩放 1 → 1.25 → 1。`stagger 60ms` 让相邻动画重叠；改成 `stagger 300ms` 则等前一组完成后再启动下一组。个人弹跳可用同一结构，将 `scale` 改为 `position base` → `position offset(0, -12)`；内置逐字弹跳的位移为 −20 像素。这些是缓动曲线，不包含弹簧或弹性物理模拟。

每个 `scope` 和 `segment` 都用 `end` 结束。`unit`、`delay`、`stagger`、`order`、`state` 必须写在该作用域的首个时间段之前，且不能重复。作用域名在脚本内唯一，段名在作用域内唯一。每个作用域至少有一个 flex 段及一些关键帧。多个作用域共用同一个 Clip，各自分配时间；整句淡入淡出可与逐组放大同时执行。

### 选择原文范围

| 目标 | 原文范围 |
|---|---|
| `scope letters current` | 特效面板当前选中的文字范围；未选范围时为整句字幕 |
| `scope letters subtitle` | 整句字幕，不受面板当前范围影响 |
| `scope letters range(4, 2)` | 从原始整句字幕的第 4 个字素起，取 2 个完整字素 |

`range(start, count)` 使用从 1 开始的字素序号，空格和硬换行也参与序号计数，完整 CRLF 算一个字素；它不是 UTF-16 码元索引，也不是相对于当前选区的索引。例如 `你好 世界` 的 `range(4, 2)` 是 `世界`。组合字符和 emoji 序列保持完整字素。范围越界时整次应用失败，不自动裁剪。

默认 `unit group` 将目标整体作为一组。`current` 选中已有文字范围时复用该范围；目标是整句时作用于图层，不创建文字范围，空字幕也可以执行整层特效。`range(...)` 创建持久文字范围。其他文字分组必须有匹配的字幕；空目标或纯空白目标不生成文字组，也不消耗错峰序号。

### 文字分组

| 单位 | 分组规则 |
|---|---|
| `group` | 整个目标为一组；默认值 |
| `grapheme` | 每个非空白完整字素为一组；跳过空格与硬换行 |
| `chunk(2)` | 每个硬行内连续两个字素一组；行内空格算字素且随组变换；末尾不足两个仍保留 |
| `word` | 连续非空白文字为一组，标点附着在词上；没有空格的中文整段为一个词组 |
| `line` | 每个显式 LF 或 CRLF 行为一组，不包含行末换行 |
| `paragraph` | 空行或纯空白硬行分段；段落内部硬换行保留在同一个连续范围中 |
| `split(" ", "，", "::")` | 在整个目标中匹配字面分隔符；分隔符保留在字幕中，但不属于动画组 |

定长文字组用 `chunk(N)`，`N` 是正数字素数量；真正的段落用 `paragraph`，硬行用 `line`。每组都对应原文中的一个连续范围，共用一个轴心；跨行段落也只有一个组轴心。

硬行识别 LF 和 CRLF。`chunk` 在每个硬行重新计数；纯空白分块、行、段落或 split 结果在分配组序号前跳过。排版自动折行不产生新组。显式作用范围截断了一个单词时，分组仍只在该作用范围内进行。

`split` 按区分大小写的字面文本匹配，同一位置优先最长分隔符，不重叠匹配。它覆盖整个目标，可跨硬行，因此分隔符可以包含完整 `\r\n`。空分隔符和重复分隔符非法。每组开头、结尾的水平空格和 Tab 不纳入范围，原始文字不删除；未命中分隔符时，目标中的非纯空白内容作为一组。命中若切开组合字、emoji 或 CRLF 的完整字素，整次应用失败。

分隔符字符串支持 `\"`、`\\`、`\n`、`\r`、`\t`，未知转义拒绝。版本 2 的 `#` 仅在引号外开始注释，所以 `split("#")` 合法。当前语言不提供正则、自然语言分词、自动折行分组、随机顺序或嵌套组时钟。

### 启动顺序与视觉状态

默认是 `delay 0ms`、`stagger 0ms`、`order forward`、`state normal`。过滤空白并确定顺序后，组序号 `i` 从零开始，该组启动时间为：

```text
Clip 内容原点 + delay + i × stagger
```

内容原点包含图层的 `AnimationOffset`。`order reverse` 反转组的启动顺序，不改变字幕文字。`delay` 和 `stagger` 必须非负。版本 2 使用作用域声明的状态；即使面板选中其他状态，省略 `state` 仍为普通状态。`state active`、`state inactive` 作用于已有卡拉 OK 绘制通道，不创建逐字计时，也不改变字形几何。

### 固定重复与弹性循环

`segment pulse fixed 150ms repeat 3 pingpong` 执行三次完整往返，压缩前共 900 ms。`repeat` 为正整数，仅用于固定段，默认一次。正向重复段必须回到起始值再接下一次。Pingpong 会反转原始曲线本身，包括 `power(...)`；不会把同一条正向缓动简单套到返程。

作用域内可写循环 flex 段：

```text
segment pulse flex 1 cycle 300ms pingpong
    at 0 scale base ease-in-out
    at 1 scale factor(1.25, 1.25)
end
```

`cycle 300ms` 是完整周期；有 pingpong 时包含两程。编译器用精确有理数向下取整，只输出完整周期，余下时间保持最后值。例如分配到 800 ms 时输出两个 300 ms 周期，再保持 200 ms。正向 cycle 的起止值必须相等，以便不同 Clip 时长下重复接合。flex 使用 pingpong 时必须同时指定 cycle；flex 不能使用 repeat，fixed 不能使用 cycle。

### 短片段规则

版本 2 对每个非空作用域计算启动延迟、最后一组错峰、展开后的固定段，以及保证每个循环 flex 段至少一个完整周期所需的最小弹性时长：

```text
U = 组数量
S = max(U − 1, 0) × stagger
F = sum(固定单程时长 × repeat × (pingpong ? 2 : 1))
W = 所有 flex 权重之和，包含空停留段
Rmin = max(cycle 完整周期 × W / 该段权重)，没有 cycle 时为 0
E = delay + S + F + Rmin
k = Clip 时长 T < E 时取 T / E，否则为 1
```

`reject` 在 `T < E` 时拒绝应用。`compress` 将 delay、stagger、固定单程时长、cycle 周期统一乘以 `k`；再把 `T − k × (delay + S + F)` 按 flex 权重分配。`E = 0` 时无需压缩。这使组间距与脉冲时长保持比例，正时长 Clip 中每个循环段仍至少有一个完整周期。没有 cycle 的 flex 段仍可能压缩到零，折叠后所有已解析关键帧值必须一致。

### 可编辑结果与重新应用

应用版本 2 后，组被展开成持久可编辑的文字范围与普通动画轨道，包含多字幕批量应用时也只有一条 Undo。非法分组、混合基础值、冲突或预算超限会拒绝整次事务。

生成块由脚本 ID、作用域名、当前父范围 ID 标识。重新应用同一块会替换其所有生成范围和轨道，包括对生成结果做过的手工修改。分组定义和文字跨度不变时复用范围 ID 与覆盖顺序；从 `chunk(2)` 改成 `chunk(3)` 会移除旧组。其他手建范围及其他生成块保持独立。从源脚本删除或改名一个作用域，不会自动删除旧命名块；不再使用时需在特效面板明确删除旧生成范围。

修改文字时，已有范围按字幕编辑规则重新映射，不会重新执行分组、错峰或重复展开。需要适配新文字的分组与时序时重新应用模板。删除父范围会同时删除其生成后代及相关轨道。

## 属性与值

| 属性 | 值 |
|---|---|
| `position` | 像素向量：`base`、`(100, 20)`、`offset(-250, 0)` |
| `scale` | 向量：`base`、`(1, 1)`、`factor(0.2, 0.2)` |
| `rotation` | 角度：`0`、`base`、`offset(15)` |
| `opacity` | 0–1 或 `base` |
| `fill`、`stroke` | 线性 `rgba(r, g, b, a)` 或 `base` |
| `blur` | 0–512 像素或 `base` |
| `stroke-width` | 0–4096 像素或 `base` |
| `font-size` | 字幕字号，0.01–4096 像素或 `base` |
| `letter-spacing` | 字幕字素间距，−4096–4096 像素或 `base` |
| `fill-blur`、`stroke-blur` | 字幕填充／描边模糊，0–512 像素或 `base` |
| `shadow-offset` | 字幕阴影偏移向量：`base`、`(3, 4)`、`offset(2, -1)` |
| `shadow-blur` | 字幕阴影模糊，0–512 像素或 `base` |
| `shadow-color` | 字幕阴影线性 `rgba(r, g, b, a)` 或 `base` |
| `path-progress` | 显式 0–1 |
| `mask-rectangle-top-left`、`mask-rectangle-bottom-right` | 工程坐标的矩形角向量 |
| `mask-position`、`mask-scale`、`mask-rotation` | 独立蒙版变换 |
| `mask-node(c,n).position`、`.in-handle`、`.out-handle` | 已有节点位置 / 相对控制柄向量 |

`base` 读取应用前的目标值，`offset` 相加，`factor` 相乘。颜色采用非预乘线性 RGB，可表达 HDR，alpha 为 0–1；界面 HEX 使用 sRGB。脚本不使用 HEX 字面量；`#` 开始注释，在版本 2 中仅作用于引号外。

`font-size`、`letter-spacing`、`fill-blur`、`stroke-blur` 和三个阴影属性必须应用于字幕片段，并提供应用前的字幕样式，即使使用显式数值也一样。它们的 `base` 来自该样式。原有 `blur` 对整层合成结果模糊，两个分通道模糊分别影响填充和描边。换行模式是字幕样式中的静态设置，不是 DSL 动画属性。这些属性仍使用 DSL 版本 1。

版本 1 沿用特效面板当前的整行／文字范围和普通／已激活／未激活状态，版本 2 按上述规则分别声明原文范围及状态。模板不保存工程中的范围 ID，因此同一个模板可以应用到不同字幕的范围。当前文字范围一次只能应用于一个字幕；整句应用可以批量处理，分别读取每个字幕的分组与基础值。

普通文字范围支持字号、字距、填充／描边颜色与模糊、描边宽度、阴影以及独立位置、缩放和 Z 旋转。局部 `position` 是范围的像素 Offset，默认 `(0, 0)`，平移该组已排版的几何。字号与字距逐帧重排；局部位移、缩放和旋转保留排版占位，并使用范围的轴心设置。已激活／未激活状态只支持颜色、描边宽度、填充／描边模糊及阴影，几何沿用普通状态。`opacity`、整层 `blur`、路径及蒙版属性不能应用到文字范围；不支持的属性会定位到脚本行并拒绝整次应用。

范围或整行视觉状态中的基础值可能因富文本或卡拉 OK 覆盖而混合。`base`、`offset`、`factor` 读取混合值时整次应用失败；无已有轨道且脚本从片段中途开始时，前面的未声明区间同样需要一致基础值。可以先统一该属性，或从片段起点使用显式数值。颜色脚本始终在线性 RGB 中插值；若现有同目标轨道使用 sRGB，必须覆盖完整时长或先清除该轨道，部分覆盖会拒绝以保持原有区间的插值语义。

新生成组的局部缩放为 `(1, 1)`，位置为 `(0, 0)`，旋转为 `0`。因此缩放脉冲可以作用于混合字号而不改变排版；整个单词或段落读取混合字号、颜色 `base` 仍会失败，编译器不会取第一种样式或暗中拆组。

插值支持 `hold`、`linear`、`ease-in`、`ease-out`、`ease-in-out` 和 `power(正指数)`。当前点控制到下一点的插值，默认线性。

## 蒙版与校验

先创建几何再应用蒙版脚本。节点选择器从 1 开始，并解析为稳定 ID；节点/控制柄动画锁定拓扑，添加、删除或重排节点前清除对应轨道。脚本按完整目标组合声明区间，保留其他轨道，不创建几何。用脚本关键帧替换有序 ASS 变换前须先清除该目标。

ID、作用域名、段名使用小写 ASCII 字母、数字、`.` 和 `-`，以字母开头，最多 64 字符。上限为 128 个作用域、总计 128 段、4,096 个源关键帧、262,144 个源字符，固定时长及 cycle 周期不超过 24 h；弹性权重为 (0, 1,000]。输出后每字幕最多 256 个文字范围，每层最多 8,192 条范围／状态轨道，展开关键帧也受属性预算限制。超限直接拒绝，不截断输出。无效输入或端点冲突不改变工程和 Undo。不同作用域不能在相同完整目标上声明重叠时间区间；不同文字范围的重叠继续按已有保存顺序合成。

## 原生工程与 ASS 交换

工程格式 13 保存局部 Offset、反向曲线及生成块来源。版本 3–12 由正常工程加载流程迁移；版本 12 的范围保留身份，新增 Offset 默认为 `(0, 0)`、Reverse 默认为 `false`、无生成来源。旧工程不能在旧版本号下声明版本 13 的新字段。

ASS 无法表示独立范围平移，导出返回 `Ass.RangeTranslation` 并省略该移动；范围缩放／轴心保留已有排版差异，反向非线性曲线使用采样或明确的近似提示。高级 ASS 源码编辑保留原生 Offset、生成来源及不可表达的反向轨道。保存 `.aeginext` 才能保留完整可编辑结果，详见 [ASS 互转换能力](ass-compatibility.md)。

## 示例

- [内置淡入淡出](../../src/AegiNext.Core/Effects/Scripts/fade-in-out.aegifx)
- [滑入/弹出](../en/examples/effects/slide-pop.aegifx)
- [线性颜色循环](../en/examples/effects/color-cycle.aegifx)
- [蒙版滑动](../en/examples/effects/mask-slide.aegifx)
- [蒙版形变](../en/examples/effects/mask-morph.aegifx)
- [内置逐字弹跳](../../src/AegiNext.Core/Effects/Scripts/letter-bounce.aegifx)
- [内置逐字放大往返](../../src/AegiNext.Core/Effects/Scripts/letter-pulse.aegifx)
- [按单词分组](../en/examples/effects/grouped-words.aegifx)
- [两个字素一组](../en/examples/effects/grouped-pairs.aegifx)
- [按字面分隔符分组](../en/examples/effects/grouped-split.aegifx)
- [按硬行分组](../en/examples/effects/grouped-lines.aegifx)
- [整句淡入淡出与逐组放大](../en/examples/effects/scoped-fade-pulse.aegifx)

解析/编译入口为 `src/AegiNext.Core/Effects/`。集成使用 `EffectScriptCompiler.CompileTarget` 或 `EffectScriptComposer.ComposeTarget`，同时应用返回的字幕范围与轨道；只返回轨道的旧接口拒绝版本 2。辅助编写可使用仓库[特效 DSL Skill](../../.agents/skills/aeginext-effect-dsl/SKILL.md)。
