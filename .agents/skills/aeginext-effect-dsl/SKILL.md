---
name: aeginext-effect-dsl
description: Write, review, debug, or extend AegiNext subtitle effect scripts (.aegifx), including whole-target timing, grouped rich-text animation, repeats, pingpong, and reusable templates. Use for effect authoring or parser/compiler/editor integration, not unrelated shader or scripting languages.
---

# AegiNext effect authoring

Use the repository's actual DSL v1/v2 parser and compiler as the authority. This is a declarative animation format, not C#, JavaScript, or an expression interpreter.

## Establish the authoring target

1. Locate the AegiNext checkout (`git rev-parse --show-toplevel` when already inside it). This repository skill lives at `.agents/skills/aeginext-effect-dsl/`; its resolved location is three directories below the checkout root. Resolve symlinks before deriving a root.
2. Read `docs/en/effect-dsl.md` (English) or `docs/zh-cn/effect-dsl.md` (Chinese), then the closest existing sample in `src/AegiNext.Core/Effects/Scripts/` or `docs/en/examples/effects/`. Read `references/authoring-checks.md` for failure cases and test entry points.
3. Determine the visual effect, source text scope, grouping, changed properties, start order, fixed/cyclic timing, and short-clip policy. Use an explicitly supplied policy; otherwise state the chosen `compress` policy with the delivered script. Ask only when a missing decision materially changes the effect.
4. Give personal scripts a unique lowercase stable identifier. Built-in IDs are reserved; retain an existing personal ID when editing that template. Display names belong to the template library and do not replace the source identifier.

## Write a duration-adaptive script

- Use version 1 for existing whole-target segment scripts; use version 2 for named scopes, grouped text, repeat, pingpong, or cycle. Begin with `effect "personal-id" version 1` or `version 2`, followed by explicit `short-clip compress` or `short-clip reject`. Do not retrofit scope directives into version 1.
- Put fixed transitions in `segment name fixed 300ms`; put the remaining duration in one or more `segment name flex 1` blocks. Close every block with `end`. At least one flex block and some keyframes are required.
- `at` positions are fractions of the source segment/leg, not seconds; repeating or cyclic segments reuse that source. For each property present in a segment, write an `at 0` and an `at 1`, with strictly increasing intermediate positions. Different properties may be interleaved.
- Keep a property's shared endpoints exactly equal. A noncyclic flex segment can become zero length under compression: its collapsed keyframes must all have the same value. Use the version-2 cycle allocation contract when a repeating animation must fit arbitrary positive-duration Clips.
- `base` reads the target's original base value, not the playhead, the previous segment, or accumulated effects. Prefer `offset(x, y)` for relative translation and `factor(x, y)` for relative scale; retain independent X/Y values.
- `position` and `scale` are complete two-component values. `fill` and `stroke` are complete straight linear RGBA values: `rgba(r, g, b, a)` or `base`. Colors do not accept `offset` or `factor`; RGB may be HDR, Alpha stays within 0–1.
- UI HEX/RGBA byte input is sRGB and differs from DSL linear color. Do not paste `#RRGGBB` into a value. Version 2 recognizes `#` comments outside quoted separator strings, so `split("#")` remains valid. Convert intended sRGB colors to linear values when necessary and explain that conversion.
- Use the guide's property table and current `AnimationPropertyMetadata` for limits. Subtitle typography and paint properties require the original subtitle and style. Interpolations are `hold`, `linear`, `ease-in`, `ease-out`, `ease-in-out`, and `power(positiveExponent)`.
- Do not add executable code, file access, general-purpose loops, custom functions, per-axis property names, or rectangle/stretch semantics. Path animation requires an existing path; the script does not create its points.

## Author a grouped effect

- Declare `scope name current`, `subtitle`, or `range(start, count)`. Current resolves the Effects range selection or the whole subtitle. Numeric ranges use one-based complete-grapheme indices in the original full subtitle, including whitespace and hard breaks; out-of-bounds input fails, without clipping.
- Choose `unit group` (default), `grapheme`, `chunk(N)`, `word`, `line`, `paragraph`, or literal `split(...)`. Read the guide's grouping table before changing whitespace or line behavior. In particular, chunk counts and transforms inline spaces; word does not segment unspaced Chinese; line uses LF/CRLF rather than layout wrapping. Split operates over the entire scope, excludes separators without deleting text, trims horizontal edge whitespace, and rejects matches that cut a complete grapheme.
- Place unit/delay/stagger/order/state before segments and close both scopes and segments with `end`. Fields cannot repeat. Version 2 defaults to Normal independently of the panel's selected visual state. Active/inactive changes painting channels without creating karaoke timing.
- A fixed duration is one leg; `fixed 150ms pingpong` totals 300ms. `repeat N` counts complete forward or forward/return repetitions and is fixed-only. `flex 1 cycle 300ms pingpong` uses 300ms for the full period. Only complete cycles are emitted; the remainder holds. Forward repeats/cycles need equal resolved first/last values. Pingpong reverses the original curve, including POWER, and does not imply spring physics.
- State short-clip behavior accurately: version 2 compresses delay, stagger, fixed legs, and cycle periods together using the scope envelope, including the minimum weighted flex allocation for one cycle. An empty flex hold contributes weight. See the guide's formula; do not allocate cycles after treating stagger as free time.
- Generated Normal groups start with local Offset `(0, 0)`, Scale `(1, 1)`, and Rotation `0`. Scoped position moves laid-out geometry; scale preserves occupancy, whereas font size reflows. Mixed paint/font bases reject `base`/`offset`/`factor`; do not pick the first style or secretly split a requested group.
- Reapplication replaces the exclusive generated block identified by effect ID, scope name, and parent range ID, including manual changes to those results. Changing grouping removes that block's stale groups. Deleting or renaming a source scope does not retire its old named block automatically. Text edits remap the existing result; reapply to regroup/retime.

## Validate the exact delivered source

Parsing alone cannot prove compatibility with a clip. Validate with `EffectScriptParser.Parse` and `EffectScriptCompiler.CompileTarget` against representative target layers and original subtitle content/styles, including nonuniform transforms and nondefault colors. The result carries Tracks, coverage Intervals, prepared layer, and subtitle ranges. Integration uses `EffectScriptComposer.ComposeTarget` and applies the complete result atomically. Tracks-only `Compile`/`Compose` reject version 2; never discard its generated ranges.

Check a long clip, the exact envelope duration, a shorter clip, and a tiny positive clip. For cyclic sources also check fractional leftover time and multiple flex weights; for grouping check whitespace, CRLF, combining characters/emoji, rich styles, and reverse ordering. For `reject`, insufficient durations must fail without modifying the project. Verify continuity, content-time bounds including `AnimationOffset`, and reapplication ownership. Report what actually ran; builtin tests do not validate arbitrary user-provided source.

In the app, use **Settings → Effect Scripts → Validate** for parser diagnostics, save/export the personal template, then apply it from the workbench's Effects preset selector. Settings manages templates and has no apply-to-current-subtitle action. An imported `.aegifx` must pass validation and ID-conflict checks before changing the library.

When changing the language itself, update parser, validator, compiler, metadata, editor completion/highlighting, import/export, documentation, and scoped regression tests together. Preserve one atomic `ProjectEditor.ApplyEffectScript` transaction and unrelated animation properties.

Project format 13 persists local range Offset, reversed main/component curves, and generated origins; older project migration defaults retain their prior behavior. ASS export omits range translation with `Ass.RangeTranslation`; reversed nonlinear curves may be sampled or approximated. Advanced ASS projection preserves unrepresentable native fields and tracks. Do not describe ASS output or headless tests as native visual parity.

## Deliver

Provide the complete UTF-8 `.aegifx` source or a clickable path to its saved file, policy and timing behavior, affected properties, and exact validation evidence. Use `docs/en/examples/effects/` for a requested reusable repository example; do not modify a built-in sample unless that is the requested scope.
