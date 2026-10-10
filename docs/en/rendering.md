# Rendering integration

[English](rendering.md) · [简体中文](../zh-cn/rendering.md) · [All guides](README.md)

## Use the scene renderer

`src/AegiNext.Rendering` provides UI-independent shaping and scene rendering. Evaluate an immutable project, prepare its resources, and use `ProjectSceneRenderer` for preview/export. Each thread owns its renderer and disposable surfaces; Core remains free of graphics dependencies.

The scene supports subtitles, retained shapes/images/groups, keyframes, cubic paths, karaoke, Clip masks, blur, and extended blending. The workbench edits subtitles/masks; general shape/image/group tools are not exposed there.

## Pixel contract

| Field | Meaning |
|---|---|
| Storage | Tight top-down RGBA Half/F16 |
| Color | Extended linear BT.709/sRGB primaries; finite negative/>1 RGB allowed |
| Alpha | Premultiplied, 0–1; zero alpha requires zero RGB |
| White | Explicit `ReferenceWhiteNits`, default 203; channel 1 is reference white |
| Coordinates | Top-left origin, X right/Y down, target pixels |
| Composition | Linear source-over; surfaces must agree on reference white |

Copy pixels into caller-owned storage; do not expose borrowed native memory. Preview converts only the final composed image to SDR BGRA8. HDR export composites original frames with F16 overlays; display pixels never feed encoding.

## Text and editing geometry

`TextShaper` takes explicit font bytes, size, direction, language, and optional letter spacing in pixels. Spacing is applied between shaped clusters, preserving combining marks, ligatures, and Arabic joining. Glyph clusters use UTF-16 indices, not character counts. A line has no trailing spacing; inline/font run boundaries add one gap. Negative spacing is supported even when the total advance becomes negative. Its run API requires valid single-line/single-script text; callers perform segmentation, and mixed bidi/per-run fallback remain incomplete.

Scene rendering adds basic multiline/wrapping and grapheme karaoke. Actual glyph ink bounds determine geometry; stroke/shadow/blur do not change Pivot. Blank text uses a logical editing box. Anchor is canvas-relative, Pivot ink-relative, and Offset is pixels. `GetLayerGeometry` shares bounds and transforms with drawing, hit testing, and drag.

`SubtitleStyle.WrapMode` defaults to `GRAPHEME`, preserving existing projects. `NATURAL` prefers Unicode line breaks from [Uax14Net 1.1.0](https://github.com/routersys/Uax14Net), with grapheme fallback for oversized words. Groups joined by NBSP, narrow NBSP, or word joiner stay together and may overflow the canvas. `NO_WRAP` uses only explicit newlines. Negative-spacing wrapping measures paragraph cluster geometry once and shapes the selected lines. Actual line ink is checked again after reshaping; an overflowing line uses a bounded binary search of earlier breaks, preserving indivisible groups. It does not repeatedly shape every growing prefix.

`FillBlur` and `StrokeBlur` independently blur their paints. They retain the existing separate `ShadowBlur` and whole-layer `Blur`; padding includes the paint blur extent, while editing bounds and Pivot remain based on unblurred ink. Animated letter spacing, fill blur, and stroke blur override the corresponding inline values. The `EvaluatedLayer` overloads of `MeasureSubtitleTextLayout` and `MeasureSubtitlePlacement` use the same animated spacing as drawing. The `SubtitleLine` overloads and `RenderSubtitlePreview` remain static style previews.

The renderer keeps at most 256 layouts in an LRU cache and one latest animated layout per layer. Eviction releases owned text blobs without discarding font shapers; document changes and renderer disposal release all layout and font resources. Returned editing geometry snapshots do not borrow these native resources.

## Text range property animation

`AnimationTrackTarget` identifies a property, an optional text range ID, and a normal/active/inactive painting state. Mask nodes and text ranges are mutually exclusive. `SubtitleLine.AnimationRanges` stores grapheme-safe UTF-16 half-open ranges with local pixel Offset, scale, Z rotation, and a pivot mode. Ranges may overlap: later ranges override the same painting property, and local matrices compose in saved order. A scoped Normal `POSITION` track evaluates the range Offset, whose default is `(0, 0)`.

Font size and letter spacing reshape and reflow text at each evaluated time. Fill, stroke, and shadow color/offset/blur apply after layout. Karaoke states share normal-state geometry: normal animations precede static karaoke appearance, state animations follow it, and outline-step inactive stroke hiding applies last.

Local translation, scale, and rotation preserve layout occupancy. A range center uses the current untransformed layout, with one shared center across multiple lines; imported ASS ranges can use the subtitle anchor. Offset translates the transformed range geometry in subtitle-local pixels before layer composition. Partial ligature selections retain the original shaping and transform only owned ink. Zero scale remains editable in the property panel, and singular matrices do not cause hit-test errors.

`MeasureSubtitleTextLayout(document, evaluatedLayer)` includes transformed grapheme geometry. `GetLayerGeometry` reports visible bounds and provides `ContainsWorldPoint` for precise picking. Static subtitle editing retains the `SubtitleLine` overload. Typography changes invalidate layout; paint and local transform changes reuse shaping. Frame cache identity includes values for every complete animation target.

Native color animation defaults to linear RGB interpolation. ASS-origin tracks can use `SRGB` interpolation while stored and evaluated colors remain linear; alpha is never encoded. Ordered operations retain source order and component masks for RGB, alpha, or shadow axes. Relative font size uses multiplication. Font-size operations must be provably valid throughout; conservative validation may reject combinations that rely on synchronized cancellation.

`Keyframe.Reverse` and `AnimationCurve.Reverse` preserve a time-reversed interpolation curve, including clipped phases and component curves. Pingpong therefore reverses a POWER leg exactly rather than replaying its forward acceleration. Curve slicing and composition retain this flag; frame-cache identity includes the resulting evaluated values. DSL version 2 bakes grouping into native ranges and tracks at application time; rendering does not tokenize text or run template clocks.

## Clip masks

Render the complete subtitle and its own blur first, then clip in project coordinates before parent composition. Masks affect only their Clip. Subtitle/parent transforms do not transform the mask; the mask's own fixed-pivot transform does. Preserve contour direction/nonzero winding and inversion. Preview cache identity includes evaluated mask geometry.

## Optional macOS HDR diagnostic

Build `-Target All`, then launch the matching desktop output with `--hdr-probe`. This separate diagnostic presents F16 through Media → Vulkan/MoltenVK/libplacebo → FP16 Linear Display P3/CAMetalLayer EDR. It is independent of the SDR workbench and video export; Windows HDR display is deferred.

The upload uses explicit reference-white normalization and current display headroom. Nominal 203 nits is an application scale, not measured screen brightness. Creation/presentation/destruction obey main-thread ownership; close awaits native destruction. Dependency locks are in `native/dependencies.json`.

## Verify

```powershell
dotnet test Tests/AegiNext.Rendering.Tests/AegiNext.Rendering.Tests.csproj -c Release
```

Tests load real Skia/HarfBuzz libraries and pinned [font fixtures](../../Tests/AegiNext.Rendering.Tests/Fixtures/README.md). Check pixels, alpha, geometry, and extended values. Offscreen readback and tags do not establish physical HDR appearance; display validation needs the target screen.
