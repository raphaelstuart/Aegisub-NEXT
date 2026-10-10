# Effect scripts

[English](effect-dsl.md) · [简体中文](../zh-cn/effect-dsl.md) · [All guides](README.md)

## Apply and edit

1. Select a subtitle, choose a preset in **Effects**, and click **Apply Preset**.
2. Open **Settings → Effect Scripts** to manage templates. Builtins are read-only; **Save As** creates a personal copy.
3. Edit, **Validate**, then save or export UTF-8 `.aegifx`. Validation reports line/column errors; **Locate Error** moves the caret.

Completion opens while typing or with Cmd/Ctrl + Space. Arrows select, Enter/Tab insert, and Esc dismisses. Templates are personal settings; applying recompiles for the current Clip and creates one Undo transaction.

DSL version 1 keeps its existing whole-target segment syntax. Version 2 adds named scopes, text grouping, staggered starts, repeats, and pingpong. Choose **Letter bounce** or **Letter pulse** in the same preset selector for the built-in grouped effects; edit a personal copy to change grouping or timing.

## Combining presets

Apply presets successively to combine them. Each preset replaces only the intervals in which it explicitly declares a complete animation target. Undeclared intervals preserve existing animation and its interpolation curves. With no existing target track, empty intervals retain the script's base or preceding value. Segment names such as `stay` and `hold` have no special meaning, and a `flex` segment may contain animation. Version 2 generated text groups additionally have the replacement ownership described below.

For a 4 s Clip, applying builtin fade-in followed by fade-out preserves the first 300 ms entrance and the middle animation, then adds the last 300 ms exit. Reversing the order produces the same result. A batch application creates one Undo transaction. Composition also works after saving and reopening the project.

An empty stay declares no properties:

```text
segment stay flex 1
end
```

This stay explicitly fixes opacity at its base value, replacing existing opacity animation:

```text
segment stay flex 1
    at 0 opacity base hold
    at 1 opacity base
end
```

`hold` is keyframe interpolation, not a request to skip an interval. `base` always reads the static layer property or subtitle style, not the old animation value at the join. Values must agree where new animation meets retained animation; otherwise the entire application fails without changing the project or Undo/Redo. Adjust the preset endpoints or clear the corresponding track first.

Independent presets allocate their timing separately and do not jointly compress fixed segments. Two 300 ms transitions join on a 600 ms Clip. On a 400 ms Clip they overlap, and builtin fade-in followed by fade-out fails because the join values differ. On Clips of 300 ms or less, each preset covers the entire Clip, so the last preset replaces the first. To retain both transitions with joint compression on short Clips, use one preset containing both segments, such as builtin fade-in-out.

## A first script

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

`fixed` preserves entrance/exit duration; `flex` divides the remaining time by weight. At least one flexible segment is required. Positions run from 0 to 1 inside each segment; each declared property must cover both endpoints in increasing order.

`short-clip compress` shrinks fixed segments proportionally when needed; `reject` refuses insufficient duration. For this script, a 5 s Clip holds for 4.4 s; a 400 ms Clip compresses to 200 ms in and 200 ms out. Shared endpoints must agree, including a zero-duration flexible segment.

## Version 2: grouped text

This personal template scales each grapheme up and back down, starting each group 60 ms after the previous group:

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

`fixed 150ms` is one leg. `pingpong` adds the time-reversed return leg, so the pulse lasts 300 ms: local scale 1 → 1.25 → 1. `stagger 60ms` overlaps adjacent pulses; `stagger 300ms` starts the next group after the previous pulse completes. A personal bounce uses the same structure with `position base` → `position offset(0, -12)`; the built-in Letter bounce uses −20 pixels. Easing controls these curves; no spring or elastic-physics simulation is implied.

Every `scope` and `segment` ends with `end`. Put `unit`, `delay`, `stagger`, `order`, and `state` before the scope's first segment. Fields cannot repeat. Scope names are unique in the script; segment names are unique within their scope. Each scope requires at least one flex segment and some keyframes. Multiple scopes share the Clip but allocate timing independently; a whole-subtitle fade and grouped scale pulses can run together.

### Select the source text

| Target | Source |
|---|---|
| `scope letters current` | The text range selected in Effects, or the entire subtitle when no range is selected |
| `scope letters subtitle` | The entire subtitle, regardless of the selected range |
| `scope letters range(4, 2)` | Two complete graphemes starting at grapheme 4 of the original full subtitle |

`range(start, count)` uses one-based grapheme indices, including spaces and hard line breaks in the index; a complete CRLF is one grapheme. It does not use UTF-16 code units or indices relative to the current selection. In `你好 世界`, `range(4, 2)` selects `世界`. An emoji sequence or a combining character remains one complete grapheme. Out-of-bounds ranges fail the entire application; the compiler does not clip them.

The default `unit group` treats the selected source as one group. With `current` and an existing text range it reuses that range. With a whole-subtitle target it applies to the layer and creates no text range, including when the subtitle is empty. `range(...)` creates a persistent text range. Text-specific units require a matching subtitle; an empty or all-whitespace selection produces no text groups and consumes no stagger slots.

### Group the source

| Unit | Grouping contract |
|---|---|
| `group` | One complete selected target; default |
| `grapheme` | One complete non-whitespace grapheme; spaces and hard breaks are skipped |
| `chunk(2)` | Consecutive pairs of graphemes within each hard line; inline spaces count and move with their group; a short final group is retained |
| `word` | Consecutive non-whitespace text; punctuation stays attached; Chinese without spaces forms one token |
| `line` | One group per explicit LF or CRLF line, excluding its line break |
| `paragraph` | Groups separated by empty or whitespace-only hard lines; internal hard breaks remain inside a paragraph's continuous range |
| `split(" ", "，", "::")` | Literal separators over the entire selected source; matched separators stay in the subtitle but are excluded from groups |

For a fixed-length text group, use `chunk(N)` with a positive grapheme count. For actual paragraphs use `paragraph`; hard lines use `line`. Each group is one continuous original-text range with one shared pivot, including groups spanning several lines.

Hard-line units recognize LF and CRLF. Chunking restarts after each hard line; whitespace-only chunks, lines, paragraphs, and split results are skipped before group indices are assigned. Layout wrapping does not create new groups. A word or group truncated by an explicit scope remains inside that scope.

`split` matches case-sensitive literal text, takes the longest matching separator at a position, and proceeds without overlapping matches. It operates across hard lines, so a separator can contain a complete `\r\n`. Empty or duplicate separators are invalid. Leading/trailing horizontal spaces and tabs are excluded from each emitted range; original subtitle text is preserved. With no match, the selected non-whitespace text becomes one group. A match that cuts a combining grapheme, emoji sequence, or CRLF rejects the entire application.

Quoted separators support `\"`, `\\`, `\n`, `\r`, and `\t`; unknown escapes fail validation. `#` begins a comment only outside quotes in version 2, so `split("#")` is valid. Regexes, natural-language tokenization, soft-layout-line grouping, random order, and nested group clocks are not language features.

### Start groups and choose a state

Defaults are `delay 0ms`, `stagger 0ms`, `order forward`, and `state normal`. For group index `i`, starting at zero after whitespace filtering and ordering, its start is:

```text
Clip content origin + delay + i × stagger
```

The content origin includes the layer's `AnimationOffset`. `order reverse` reverses group start order while preserving the subtitle's text. Delay and stagger are nonnegative. Unlike version 1, version 2 uses the scope's declared state and defaults to Normal even if another state is selected in Effects. `state active` and `state inactive` target the existing karaoke painting channels; they do not create karaoke timing or change geometry.

### Repeat and cycle

`segment pulse fixed 150ms repeat 3 pingpong` performs three full forward/return pairs, totaling 900 ms before compression. `repeat` is a positive integer and applies only to fixed segments; the default is one. A forward repeated segment must join back to its starting values. Pingpong reverses the source curve itself, including `power(...)`, rather than assigning the same forward easing to the return leg.

Inside a scope, a cyclic flex segment can be written as:

```text
segment pulse flex 1 cycle 300ms pingpong
    at 0 scale base ease-in-out
    at 1 scale factor(1.25, 1.25)
end
```

`cycle 300ms` is the complete period, including both legs when using pingpong. The compiler emits only complete periods using an exact rational floor; any remaining time holds the last value. An 800 ms allocation therefore emits two 300 ms cycles followed by a 200 ms hold. A forward cycle must have equal first and last values so it joins across different Clip lengths. `pingpong` on flex requires `cycle`; `repeat` on flex and `cycle` on fixed are invalid.

### Short Clips

For each nonempty scope, version 2 considers delay, the final group's stagger, expanded fixed segments, and the minimum flex allocation needed for one complete period in every cyclic flex segment. Let:

```text
U = number of groups
S = max(U − 1, 0) × stagger
F = sum(fixed leg duration × repeat × (pingpong ? 2 : 1))
W = sum(all flex weights, including empty stay segments)
Rmin = max(cycle period × W / that segment's weight), or 0 without cycles
E = delay + S + F + Rmin
k = T / E when Clip duration T < E; otherwise 1
```

`reject` refuses `T < E`. `compress` scales delay, stagger, fixed leg durations, and cycle periods together by `k`. The remaining `T − k × (delay + S + F)` is divided between flex segments by weight. When `E = 0`, no compression is needed. This keeps group spacing and pulse proportions together and reserves at least one complete cyclic period on a positive-duration Clip. Noncyclic flex segments can still collapse; their resolved keyframes must agree when their duration becomes zero.

### Editable results and reapplication

Applying version 2 bakes the groups into persistent editable text ranges and ordinary animation tracks. It creates one Undo transaction, including a multi-subtitle application. Invalid grouping, mixed base values, conflicts, or budgets reject the whole transaction.

A generated block is identified by the source effect ID, scope name, and current parent range ID. Reapplying that block replaces all its generated ranges and tracks, including manual edits to those results. Unchanged grouping and spans reuse range identities and coverage order; changing `chunk(2)` to `chunk(3)` removes stale groups. Other hand-created ranges and other generated blocks remain independent. Removing or renaming a scope in the source does not delete its old named block; delete those old generated ranges explicitly in Effects when retiring it.

Text edits remap existing ranges through the normal subtitle editing rules. They do not rerun grouping, stagger allocation, or repeats. Reapply the template after changing text when new grouping or timing is required. Deleting a parent range also deletes its generated descendants and their tracks.

## Properties and values

| Property | Value |
|---|---|
| `position` | Pixel vector: `base`, `(100, 20)`, `offset(-250, 0)` |
| `scale` | Vector: `base`, `(1, 1)`, `factor(0.2, 0.2)` |
| `rotation` | Degrees: `0`, `base`, `offset(15)` |
| `opacity` | 0–1 or `base` |
| `fill`, `stroke` | Linear `rgba(r, g, b, a)` or `base` |
| `blur` | 0–512 pixels or `base` |
| `stroke-width` | 0–4096 pixels or `base` |
| `font-size` | Subtitle font size, 0.01–4096 pixels or `base` |
| `letter-spacing` | Subtitle grapheme spacing, −4096–4096 pixels or `base` |
| `fill-blur`, `stroke-blur` | Subtitle fill / outline blur, 0–512 pixels or `base` |
| `shadow-offset` | Subtitle shadow offset vector: `base`, `(3, 4)`, `offset(2, -1)` |
| `shadow-blur` | Subtitle shadow blur, 0–512 pixels or `base` |
| `shadow-color` | Subtitle shadow linear `rgba(r, g, b, a)` or `base` |
| `path-progress` | Explicit 0–1 |
| `mask-rectangle-top-left`, `mask-rectangle-bottom-right` | Project-coordinate corner vectors |
| `mask-position`, `mask-scale`, `mask-rotation` | Independent mask transform |
| `mask-node(c,n).position`, `.in-handle`, `.out-handle` | Existing node position / relative handle vectors |

`base` reads the original target value; `offset` adds and `factor` multiplies it. Colors use straight linear RGB, permitting HDR values, with alpha 0–1; UI HEX is sRGB. HEX literals are not script values. `#` starts a comment, outside quoted strings in version 2.

`font-size`, `letter-spacing`, `fill-blur`, `stroke-blur`, and the three shadow properties require a subtitle Clip and its original subtitle style, including when using explicit numbers. Their `base` values come from that style. The existing `blur` applies to the composited layer; the two channel blurs affect fill and outline separately. Wrap mode is a static subtitle style setting and is not an animated DSL property. These properties remain part of DSL version 1.

Version 1 uses the Effects panel's current whole-line / text-range scope and Normal / Active / Inactive visual state. Version 2 selects source text and state per scope as described above. Templates do not store project range IDs, so the same template can target ranges in different subtitles. A current-range application targets one subtitle Clip; whole-subtitle application can target multiple subtitle Clips, each resolving its own groups and base values.

A Normal text range supports font size, letter spacing, fill / stroke colors and blurs, stroke width, shadow, and independent position, scale, and Z rotation. Local `position` is the range's pixel Offset, with a default of `(0, 0)`; it translates that group's laid-out geometry. Font size and spacing reflow on every frame. Local translation, scale, and rotation retain layout space and use the range's configured pivot. Active / Inactive targets support colors, stroke width, fill / stroke blur, and shadow, sharing Normal geometry. Opacity, composited layer blur, paths, and masks cannot target text ranges. Unsupported properties produce a source location diagnostic and reject the entire application.

Inherited base values may be mixed within a range or a whole-line visual state because of rich text or karaoke overrides. Reading mixed values with `base`, `offset`, or `factor` rejects the entire application. With no existing target track, an undeclared leading interval also requires a uniform base. Unify the property first, or use explicit values starting at the Clip origin. Color scripts interpolate in linear RGB. If an existing matching target uses sRGB, cover its complete duration or clear the track first; partial overlays are rejected to preserve the retained intervals' interpolation.

Generated groups begin with local scale `(1, 1)`, position `(0, 0)`, and rotation `0`. A scale pulse therefore works across mixed font sizes without changing their layout. A whole word or paragraph using mixed font-size or color `base` values still fails; the compiler does not pick the first style or silently split the requested group.

Easing is `hold`, `linear`, `ease-in`, `ease-out`, `ease-in-out`, or `power(positiveExponent)`. A point controls interpolation to the next point; default is linear.

## Masks and validation

Create mask geometry before applying mask scripts. Node selectors are one-based and resolve to stable IDs. Node/handle animation locks topology; clear those tracks before adding/removing/reordering nodes. Scripts combine declared intervals for matching complete targets, preserve other tracks, and never create geometry. Clear an ordered ASS transform target before replacing it with script keyframes.

IDs, scope names, and segment names use lowercase ASCII letters, digits, `.` and `-`, begin with a letter, and have at most 64 characters. Limits: 128 scopes, 128 total segments, 4,096 source points, 262,144 source characters, fixed durations and cycle periods up to 24 h, and flex weights in (0, 1,000]. The resulting subtitle permits at most 256 text ranges, and a layer at most 8,192 range/state tracks; expanded keys must also fit their property budget. Budgets reject the application instead of truncating results. Invalid input or conflicting endpoints leave the project and Undo unchanged. Different scopes cannot declare overlapping time intervals on the same complete target; different overlapping ranges retain the existing saved-order composition rules.

## Native project and ASS exchange

Project format 13 stores local Offset, reversed curves, and generated-block origins. Versions 3–12 migrate through the normal project loader; version 12 ranges retain their identities, with new Offset `(0, 0)`, Reverse `false`, and no generated origin. Older files cannot declare version-13 fields while claiming an older schema.

ASS cannot represent independent range translation; export reports `Ass.RangeTranslation` and omits that movement. Range scale/pivots have the existing layout differences, and reversed nonlinear curves use sampling or explicit approximation diagnostics. Advanced ASS source edits retain native Offset, generated ownership, and unrepresentable reversed tracks. Save `.aeginext` to preserve the complete editable result; see [ASS interoperability](ass-compatibility.md).

## Examples

- [Builtin fade](../../src/AegiNext.Core/Effects/Scripts/fade-in-out.aegifx)
- [Slide/pop](examples/effects/slide-pop.aegifx)
- [Linear color cycle](examples/effects/color-cycle.aegifx)
- [Mask slide](examples/effects/mask-slide.aegifx)
- [Mask morph](examples/effects/mask-morph.aegifx)
- [Builtin letter bounce](../../src/AegiNext.Core/Effects/Scripts/letter-bounce.aegifx)
- [Builtin letter pulse](../../src/AegiNext.Core/Effects/Scripts/letter-pulse.aegifx)
- [Grouped words](examples/effects/grouped-words.aegifx)
- [Pairs of graphemes](examples/effects/grouped-pairs.aegifx)
- [Literal separators](examples/effects/grouped-split.aegifx)
- [Hard lines](examples/effects/grouped-lines.aegifx)
- [Whole-subtitle fade with grouped pulses](examples/effects/scoped-fade-pulse.aegifx)

Parser/compiler: `src/AegiNext.Core/Effects/`. Integrations use `EffectScriptCompiler.CompileTarget` or `EffectScriptComposer.ComposeTarget` and apply both the returned subtitle ranges and tracks; the tracks-only API rejects version 2. For assisted authoring, use the repository [effect DSL skill](../../.agents/skills/aeginext-effect-dsl/SKILL.md).
