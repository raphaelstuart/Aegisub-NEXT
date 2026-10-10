# 渲染集成

[English](../en/rendering.md) · [简体中文](rendering.md) · [全部指南](README.md)

## 使用场景渲染器

`src/AegiNext.Rendering` 提供独立于 UI 的排版与场景渲染。求值不可变工程并准备资源后，通过 `ProjectSceneRenderer` 生成预览/导出画面。各线程拥有自己的渲染器和可释放表面，Core 不增加图形依赖。

场景支持字幕、保留的形状/图片/组、关键帧、三次路径、卡拉 OK、Clip 蒙版、模糊和扩展混合。工作台编辑字幕/蒙版，尚无通用形状/图片/组工具。

## 像素契约

| 字段 | 含义 |
|---|---|
| 存储 | 紧密、从上到下的 RGBA Half/F16 |
| 色彩 | 扩展线性 BT.709/sRGB 原色，允许有限负值和大于 1 的 RGB |
| Alpha | 预乘、0–1；零 alpha 要求零 RGB |
| 参考白 | 显式 `ReferenceWhiteNits`，默认 203；通道 1 对应参考白 |
| 坐标 | 左上原点，X 向右/Y 向下，目标像素 |
| 合成 | 线性 source-over，表面参考白必须相同 |

像素复制到调用方自有存储，不暴露借用原生内存。预览只在最终合成后转换成 SDR BGRA8；HDR 导出使用原始帧与 F16 叠层，显示像素不参与编码。

## 文字与编辑几何

`TextShaper` 使用显式字体字节、字号、方向、语言及可选的像素字距。字距加在塑形后的 cluster 之间，保留组合附标、连字与阿拉伯连接形。cluster 使用 UTF-16 索引而非字符数；行末不额外加字距，行内样式/字体 run 边界只加一次。支持负字距，包括总 advance 为负的情况。run API 要求有效单行/单文字系统文本，调用方负责分段，混合双向/逐 run 字体回退仍不完整。

场景渲染增加基础多行/换行和字素卡拉 OK。实际字形墨迹边界决定几何，描边/阴影/模糊不改变 Pivot；空文本使用逻辑编辑框。Anchor 相对画布、Pivot 相对墨迹、Offset 使用像素。`GetLayerGeometry` 与绘制、命中和拖拽共享边界/变换。

`SubtitleStyle.WrapMode` 默认为 `GRAPHEME`，保留旧工程行为。`NATURAL` 使用 [Uax14Net 1.1.0](https://github.com/routersys/Uax14Net) 提供的 Unicode 自然断行位置，超宽普通单词可回退到字素断行。由 NBSP、窄 NBSP、Word Joiner 连接的组保持完整，允许超出画布。`NO_WRAP` 只按显式换行分行。负字距换行先测量一次整段 cluster 几何，再塑形选定的各行；重新塑形后复核实际墨迹，超宽时二分查找更早的断点，仍保留不可拆组。不反复塑形每个增长的前缀。

`FillBlur` 与 `StrokeBlur` 分别模糊填充与描边，保留独立的 `ShadowBlur` 和整层 `Blur`。绘制 padding 包含两种模糊的扩散范围，编辑边界与 Pivot 仍按原始墨迹计算。字距、填充模糊、描边模糊动画覆盖对应行内值。`MeasureSubtitleTextLayout` 和 `MeasureSubtitlePlacement` 的 `EvaluatedLayer` 重载与绘制使用同一动画字距；`SubtitleLine` 重载和 `RenderSubtitlePreview` 保持静态样式预览契约。

渲染器使用最多 256 项的 LRU 排版缓存，每层只保留最近一次动画排版。淘汰排版会释放其 text blob，不清除字体塑形器；工程变化或渲染器释放时统一清理排版和字体资源。返回的编辑几何快照不借用这些原生资源。

## 文字范围属性动画

`AnimationTrackTarget` 以属性、可选文字范围 ID 和普通／激活／未激活状态定位目标；蒙版节点与文字范围互斥。字幕的 `AnimationRanges` 保存完整字素的 UTF-16 半开区间及局部像素 Offset、缩放、Z 旋转和轴心类型。范围可以重叠：后面的范围覆盖同一绘制属性，局部矩阵按保存顺序相乘。普通状态下带范围 ID 的 `POSITION` 轨道求值为范围 Offset，默认 `(0, 0)`。

字号和字距在每次求值后重新塑形、换行和定位。填充、描边及阴影颜色／偏移／模糊在排版后绘制；激活和未激活状态共用普通状态的字形几何。先应用普通属性动画，再解析卡拉 OK 静态外观，最后应用对应状态动画；轮廓逐字模式的未激活描边隐藏规则最后生效。

局部平移、缩放和旋转保留排版占位。范围中心从当前未变换布局计算，跨行范围共用一个中心；ASS 导入范围可以采用字幕锚点。Offset 以字幕局部像素平移变换后的范围几何，再参与图层合成。仅选中连字的一部分时保留原塑形结果，裁切该范围拥有的墨迹后变换。零缩放仍可从属性面板编辑，奇异矩阵不会导致画布命中异常。

`MeasureSubtitleTextLayout(document, evaluatedLayer)` 返回包含局部变换的字素几何，`GetLayerGeometry` 包含可见边界并提供 `ContainsWorldPoint` 精确命中。静态字幕编辑预览仍使用 `SubtitleLine` 重载。排版缓存只跟踪字号／字距变化，绘制属性与局部变换动画复用已有塑形；帧缓存比较完整目标的求值结果。

原生颜色动画默认在线性 RGB 中插值。ASS 来源可使用 `SRGB` 插值空间，存储与输出颜色仍是线性值，alpha 不进行色彩编码。有序操作可按分量掩码修改 RGB、alpha 或阴影轴，并保留源顺序；字号相对变化使用倍率操作。字号操作必须可证明全程处于有效区间，依赖同步抵消才合法的组合可能被保守校验拒绝。

`Keyframe.Reverse` 和 `AnimationCurve.Reverse` 保存按时间反向的插值曲线，包含裁剪相位和独立分量曲线；因此 pingpong 能精确反转 POWER 单程，而不是重放正向加速度。曲线裁剪和组合保留此标记，帧缓存比较最终求值结果。DSL 版本 2 在应用时展开成原生范围及轨道，渲染时不重新分词或执行模板时钟。

## Clip 蒙版

先渲染完整字幕和自身模糊，再在工程坐标裁切，随后合成到父层。蒙版仅影响所属 Clip；字幕/父层变换不移动蒙版，蒙版自身固定 Pivot 变换负责移动。保留轮廓方向、非零环绕规则和反相；预览缓存身份包含求值后的蒙版几何。

## 可选 macOS HDR 诊断

构建 `-Target All`，再以 `--hdr-probe` 启动匹配桌面产物。独立诊断通过 Media → Vulkan/MoltenVK/libplacebo → FP16 Linear Display P3/CAMetalLayer EDR 呈现 F16；与 SDR 工作台和视频导出独立，Windows HDR 显示暂未实现。

上传采用显式参考白归一化和当前显示器 headroom。标称 203 nits 是应用尺度，不是实测屏幕亮度。创建/呈现/销毁遵守主线程所有权，关闭等待原生销毁；依赖锁定见 `native/dependencies.json`。

## 验证

```powershell
dotnet test Tests/AegiNext.Rendering.Tests/AegiNext.Rendering.Tests.csproj -c Release
```

测试加载真实 Skia/HarfBuzz 和固定[字体素材](../../Tests/AegiNext.Rendering.Tests/Fixtures/README.zh-CN.md)，检查像素、alpha、几何和扩展值。离屏回读和标签不能证明物理 HDR 外观，显示验收需要目标屏幕。
