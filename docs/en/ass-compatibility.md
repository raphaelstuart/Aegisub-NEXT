# ASS interoperability

[English](ass-compatibility.md) · [简体中文](../zh-cn/ass-compatibility.md) · [All guides](README.md)

AegiNext uses native subtitle, text-range, karaoke, animation, and mask models. ASS is an exchange format: import converts supported content into native data, while export produces a target file and loss diagnostics. Project persistence does not pass through ASS, so exchange limits do not rewrite the native project.

See the [ASS tag audit](ass-tag-audit.md) for native relationships, conversion in each direction, and implementation status. See [subtitle editing](subtitle-editing.md) for the workflow.

## Corresponding content

| Content | Native mapping and limits |
|---|---|
| Text, fonts, and emphasis | Family, size, spacing, bold, italic, underline, strikeout, and inline overrides; embedded fonts, named variants, and custom line height cannot be fully stored in ASS |
| Color and alpha | Fill, stroke, shadow, and karaoke fill before and after activation; ASS uses 8-bit sRGB, so native linear colors, HDR, and independent channels can incur quantization or range loss |
| Layout and placement | Nine-way alignment, three margins, static position, nonnegative 2D scale, and Z rotation; font metrics and the order of scaling and wrapping differ |
| Rotation-origin exchange | Whole-line rotation with explicit placement and fixed positive scale, or static rotation with a single constant-speed move, can map `\org` through existing local pivots and positions; no native fixed-canvas rotation mode is added |
| Karaoke | `\k`, `\kf`/`\K`, `\ko`, and `\kt`; complete group timing and range appearance are stored independently without automatic per-grapheme splitting |
| Animation | Supported numeric, color, shadow-offset, straight-motion, and opacity-envelope animations; power curves or ordered operations convert directly, while other evaluable text tracks are sampled as needed |
| Masks | Rectangular and supported vector clips; representable rectangle animation uses `\t`, while other native mask animation expands into per-frame events |

Parameter correspondence does not mean pixel equivalence. Each renderer retains its own shaping, rasterization, shadow contours, blur, and fade composition. Differing shadow conditions produce `Ass.ShadowComposition`; blur has corresponding appearance and channel-coupling diagnostics.

## Exchange limits

ASS dialogue time uses centiseconds and transform time uses integer milliseconds; the project retains exact time internally. Export rounds dialogue starts down and ends up, quantizes karaoke endpoints independently, and retains at least one centisecond for collapsed positive durations. Numbers use at most nine decimal places; actual numeric, color, and alpha rounding produces separate diagnostics.

Independent native fill, stroke, and shadow blur cannot fully map to the single ASS `\blur` channel. Nonuniform border or blur resampling uses an approximation with a loss diagnostic. Missing valid `LayoutRes` values require an estimate from available resolution information.

Native mirroring, independent highlight-edge animation, overlapping range geometry, complex motion paths, layer blur, images, and shape composition may be approximated or omitted. Export changes only the output file; diagnostics identify omitted content and its reason.

Independent text-range translation, including grouped bounce POSITION tracks and static range Offset, has no ASS equivalent. Export reports `Ass.RangeTranslation` and omits it rather than moving the entire subtitle. Reversed nonlinear curves require text-animation sampling, numeric/movement/opacity approximation, or mask event expansion according to the affected property; native project curves retain their exact timing.

ASS 3D rotation, shear, independent X/Y borders, repeated box edge blur, character-encoding overrides, and inline drawing lack complete current adapters. The tag audit distinguishes missing native representation, ASS format limits, and unfinished adapters.

## Rotation pivots in file exchange

Native tools continue to edit local pivots, positions, and animation. File import maps `\org` into existing pixel pivots and positions when placement is explicit through `\pos` or a convertible `\move`, scale is fixed and positive, and geometry is consistent across the line. Fixed placement permits whole-line rotation animation; simultaneous movement and animated rotation do not use this mapping. The first valid origin in an event wins, survives `\r`, and acts as a fixed event property even when it appears after text or inside `\t`.

Export converts supported native geometry into a fixed `\org` and equivalent placement, movement, and rotation tags. The origin is independent of the clipped first movement value, and events produced by mask expansion share it. Unedited imports within this scope can recover their effective origin and motion after project save/load. Other native pivots produce geometrically equivalent ASS expressions without promising identical native fields after reimport. Original tag presence, order, repetition, and spelling are not preserved.

Importing origins with automatic placement, dynamic scale, effective range translation/scale/rotation, zero scale axes, and nonuniform canvas resampling combined with rotation are outside the current exact adapter scope. Color-only ranges and grouping provenance do not prevent conversion. Exporting automatic placement or custom layout pivots requires complete fixed layout measurements; one static measurement cannot compensate for changing font size or spacing. Unsupported combinations or derived coordinates outside native limits discard only the origin adapter and retain other convertible content, without extending the native transform model or sampling origin compensation.

Numeric, timing, and layout losses remain independent diagnostics. When both integer-millisecond `\move` times are nonpositive, import uses the complete dialogue duration. A move with only its start before zero retains its true visible phase after clipping.

## Advanced ASS editing and file import

The advanced ASS editor provides a temporary text projection of native data. Unchanged timing, color precision, font resources, independent appearances, and preservable native tracks recover their original values. Ordinary text or inline-style changes do not automatically reduce the project to ASS precision. Editing a tag updates its corresponding representable property.

Range Offset, generated-block origins and their parent references, local POSITION tracks, and unrepresentable reversed text curves are retained through this projection. Sampled ASS tags do not become the authoritative replacement for a preserved native reverse curve. To regroup or retime a generated effect after text edits, reapply its template as described in [Effect scripts](effect-dsl.md#editable-results-and-reapplication).

File import follows external ASS semantics, including source-operation order, empty-parameter resets, the karaoke clock, and resolution conversion. The advanced projection and external files have different boundaries. Projection text is not a complete project backup; save `.aeginext` to retain native capabilities.

The `\org` coordinate adapter is limited to file exchange. The advanced ASS editor continues to manage geometry through native pivot properties and does not accept external `\org` placement semantics.

## Diagnostics and budgets

Conversion results include diagnostics associated with subtitles, source tags, or properties. Unsupported tags, tracks, and some invalid values or transform times discard only the affected part. Invalid file structure, dialogue endpoints, UTF-8, or hard resource budgets reject the operation. Canceling import or export does not commit the conversion.

| Diagnostic category | Meaning |
|---|---|
| `Ass.UnsupportedTag`, `Ass.Drawing` | Source content lacks a current native adapter |
| `Ass.TransformLayout`, `Ass.TextRangeGeometry` | Parameters survive, but layout and transform order can change appearance |
| `Ass.RangeTranslation` | Output omits independent text-range translation |
| `Ass.RotationOrigin` | Origin parameters or geometry do not meet the current adapter conditions; other convertible content is retained |
| `Ass.KaraokeVisual`, `Ass.KaraokeAnimation` | Independent highlight edges or state tracks cannot be fully represented |
| `Ass.NumberPrecision`, `Ass.ColorPrecision`, `Ass.AlphaPrecision` | Output values undergo numeric or 8-bit quantization |
| `Ass.TimeQuantization`, `Ass.TransformTimeQuantization`, `Ass.KaraokeQuantization` | Output time undergoes quantization |
| `Ass.AnimationSampling`, `Ass.AnimationSamplingLimit` | Interpolation becomes samples; reaching a limit can exceed the sampling error target in some intervals |
| `Ass.MaskAnimationExpanded`, `Ass.MaskQuantization`, `Ass.MaskTimeQuantization` | Mask event expansion, coordinate or time quantization |
| `Subtitle.Composition` | Output omits image or shape clips |

Text-animation sampling uses at most 4096 points per track on a 1 ms grid, and export allows at most 100,000 events. These are exchange budgets. The limit of 256 animated text ranges per subtitle is also a native project validation constraint.

Implementation resides in [Application/SubtitleFormats](../../src/AegiNext.Application/SubtitleFormats/). [Core/Projects](../../src/AegiNext.Core/Projects/) defines native capabilities, and [Rendering](../../src/AegiNext.Rendering/) evaluates and composites the scene.
