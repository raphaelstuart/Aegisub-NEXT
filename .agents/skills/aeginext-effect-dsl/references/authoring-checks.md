# Compiler checks and source map

Paths below are relative to the AegiNext checkout, not to this reference file.

| Responsibility | Source |
|---|---|
| Version dispatch, v1 grammar and diagnostics | `src/AegiNext.Core/Effects/EffectScriptParser.cs` |
| Scope grammar, quoted strings and comments | `src/AegiNext.Core/Effects/EffectScriptV2Parser.cs`, `src/AegiNext.Core/Effects/EffectScriptV2Lexer.cs` |
| Dimensions, budgets, segment invariants | `src/AegiNext.Core/Effects/EffectScriptValidator.cs` |
| Grapheme-safe scope selection and grouping | `src/AegiNext.Core/Effects/EffectScriptGroupResolver.cs` |
| Exact allocation, base resolution, endpoint merging | `src/AegiNext.Core/Effects/EffectScriptCompiler.cs`, `src/AegiNext.Core/Effects/EffectScriptCompiler.V2.cs`, `src/AegiNext.Core/Effects/EffectScriptCompiler.TimingAllocation.cs` |
| Generated families, stable grouping signatures and replacement | `src/AegiNext.Core/Effects/EffectScriptCompiler.Ownership.cs` |
| Partial animation composition | `src/AegiNext.Core/Effects/EffectScriptComposer.cs` |
| Range remapping, splitting and descendant pruning | `src/AegiNext.Application/SubtitleAnimationRangeEditing.cs`, `src/AegiNext.Application/ProjectEditingOperations.SubtitleAnimationRanges.cs` |
| Exact reverse curves and slicing | `src/AegiNext.Core/Editing/SceneEvaluator.cs`, `src/AegiNext.Core/Editing/AnimationTrackSlicer.cs` |
| Animation ranges after cropping | `src/AegiNext.Core/Editing/LayerAnimationTiming.cs` |
| Built-in script source | `src/AegiNext.Core/Effects/Scripts/*.aegifx` |
| Atomic workbench application | `src/AegiNext.Application/ProjectEditor.Effects.cs` |
| Complete frozen target result | `src/AegiNext.Application/ProjectEditingOperations.EffectScript.cs` |
| Personal library, conflicts, import/export | `src/AegiNext.Application/Presets/EffectScriptPresetService.cs` |
| Editor grammar projection | `src/AegiNext.Desktop/Controls/Editing/EffectScriptLanguage.cs` |
| Editor input and completion | `src/AegiNext.Desktop/Controls/Editing/EffectScriptEditor.axaml.cs` |

Identifiers/scope/segment names begin with a lowercase ASCII letter and allow lowercase ASCII letters, digits, dot, and hyphen, up to 64 characters. Scope names are unique per script and segment names per scope. Current budgets: 128 scopes, 128 total segments, 4,096 source keys, 262,144 source characters; fixed durations and cycle periods are positive and at most 24 hours; flex weights are positive and at most 1,000. Durations, progress, and weights use decimal precision of at most six places. A subtitle permits at most 256 native ranges and a layer at most 8,192 range/state tracks. Expanded keys must pass each property's entry budget before expansion; never truncate. Scalar/component limits come from `AnimationPropertyMetadata`; re-read them before proposing extremes.

## A short-clip-safe starting pattern

```text
effect "personal-soft-slide" version 1
short-clip compress

segment enter fixed 250ms
    at 0 position offset(-80, 0) ease-out
    at 0 opacity 0 ease-out
    at 1 position base
    at 1 opacity base
end

segment hold flex 1
    at 0 position base hold
    at 0 opacity base hold
    at 1 position base
    at 1 opacity base
end

segment exit fixed 250ms
    at 0 position base ease-in
    at 0 opacity base ease-in
    at 1 position offset(80, 0)
    at 1 opacity 0
end
```

At 3 seconds the fixed parts remain 250ms each and the hold takes 2.5s. At 500ms the hold disappears. At 200ms both fixed parts compress to 100ms. The zero-length hold still has identical values at both ends. With `reject`, 200ms is intentionally invalid.

Frequent mistakes: copying absolute clip times into `at`, forgetting one property's `at 1`, different shared endpoint values, a changing flex segment that collapses under compression, mistaking byte RGBA for linear RGBA, claiming `base` is an evaluated animation value, or using a built-in reserved ID for a personal import.

## Grouped pulse pattern and checks

```text
effect "personal-pair-pulse" version 2
short-clip compress

scope pairs current
    unit chunk(2)
    stagger 60ms
    segment pulse fixed 150ms pingpong
        at 0 scale base ease-in-out
        at 1 scale factor(1.25, 1.25)
    end
    segment rest flex 1
    end
end
```

Each non-whitespace pair occupies one original-text range and keeps one shared center. Inline spaces count within pairs; chunking restarts at LF/CRLF; a shorter tail is retained. Generated local scale starts at `(1, 1)` even across mixed font sizes. To animate a font-size or color base, verify uniform inherited values rather than substituting the first style.

With three groups, no delay, a 300ms pulse and 60ms stagger, the envelope is 420ms. A shorter Clip uniformly scales pulse and stagger. For flex cycles, the envelope must also reserve `max(period × sum(all flex weights) / cycle segment weight)`; empty stays contribute weight. Read the maintained guide's exact formula before changing allocation.

Check these observable boundaries when they are relevant to the source:

- Numeric ranges use full-subtitle one-based grapheme indices; whitespace and CRLF participate. Emoji/combining text and delimiter matches must stay boundary-safe. Test full CRLF separators and reject partial CR or LF matches within CRLF.
- Word grouping is whitespace tokenization; Chinese without spaces is one token. Split chooses the longest literal match, trims horizontal edge whitespace, preserves source separators, and operates across hard lines. Layout wrapping is not grouping.
- Reverse order changes starts, not text. Scope delay/stagger are nonnegative; default version-2 state is Normal independently of panel context.
- Fixed repeat counts full repetitions; fixed duration is a leg, cycle duration a full period. Compare a pingpong POWER return at intermediate times with the reversed original curve. Reject open forward repeats/cycles, including a cyclic flex allocation that fits only one period.
- Compile the exact source against a long Clip, its exact envelope, a shorter Clip, and a tiny positive Clip. Test complete-period floor plus remainder hold with weighted flex segments. Reject policy must leave the full batch and history unchanged.
- Reapply chunk(2), edit generated values, then reapply chunk(3): owned stale groups/tracks disappear, manual result edits reset, other blocks remain. Text edits only remap; reapply to regroup. Scope removal/renaming leaves the retired named block until explicit deletion. Copy/split/merge must remap generated parent identities.
- Read the complete `CompileTarget` / `ComposeTarget` result. Tracks-only APIs reject version 2 to prevent dropped ranges. Local POSITION belongs to the Normal range Offset; active/inactive remains painting-only.
- Project-13 save/reload preserves Offset, Reverse and origin, with old-version defaults. ASS reports local translation loss; advanced projection retains native fields and unrepresentable reverse tracks.

Reference examples are shared under `docs/en/examples/effects/`: `grouped-words.aegifx`, `grouped-pairs.aegifx`, `grouped-split.aegifx`, `grouped-lines.aegifx`, and `scoped-fade-pulse.aegifx`. Reuse their structure with a unique personal ID; builtin IDs remain reserved.

## Scoped regression entry points

Run serially from the checkout root; adjust configuration/RID to the established platform build. Do not install or change dependencies merely to run these commands.

```sh
dotnet test Tests/AegiNext.Core.Tests/AegiNext.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~EffectScript|FullyQualifiedName~ReverseAnimationCurve'
dotnet test Tests/AegiNext.Application.Tests/AegiNext.Application.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~EffectScript|FullyQualifiedName~EffectScope|FullyQualifiedName~GeneratedRangeLifecycle'
dotnet test Tests/AegiNext.Desktop.Tests/AegiNext.Desktop.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~EffectScriptLanguageTests
dotnet test Tests/AegiNext.Desktop.Ui.Tests/AegiNext.Desktop.Ui.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~EffectScriptSettingsUiTests|FullyQualifiedName~BuiltinEffectScriptUiTests|FullyQualifiedName~RangePositionEditingUiTests'
dotnet test Tests/AegiNext.Rendering.Tests/AegiNext.Rendering.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SubtitleRangeAnimation|FullyQualifiedName~SubtitleRangePosition'
```

Add a behavior test for the exact new source when changing application code or a reusable shipped example. Existing tests exercise their own sources, not arbitrary user-provided files. Real UI validation must also check input method preedit, keyboard completion, caret placement, read-only built-ins, and invalid-source preservation; close every test window.
