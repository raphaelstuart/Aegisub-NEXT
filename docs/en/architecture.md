# Architecture

[English](architecture.md) · [简体中文](../zh-cn/architecture.md) · [All guides](README.md)

## Find the owner

| Location | Responsibility |
|---|---|
| `src/AegiNext.Core` | Rational time, immutable project/scene models, validation, animation, masks, and effect DSL |
| `src/AegiNext.Application` | Editing transactions, Undo/Redo, storage/resources, and subtitle format exchange |
| `src/AegiNext.Rendering` | UI-independent Skia/HarfBuzz shaping, scene geometry, and linear F16 composition |
| `src/AegiNext.Media` | Probing, frame ownership, native adapters, playback/analysis, preview, and export orchestration |
| `src/AegiNext.Desktop` | Avalonia workspace, panels, controls, menus, preferences, and platform hosts |
| `src/AegiNext.ExportWorker` | Independent `aegn-exporter` process sharing Core and Rendering |
| `native/decoder`, `native/audio`, `native/export` | Separate FFmpeg/SDL3 C ABIs |
| `native/` | Optional macOS HDR diagnostic backend |
| `Tests/`, `scripts/` | Focused verification, build, and packaging tools |

Core has no Avalonia, Dock, FFmpeg, Skia, or filesystem dependency. Application owns business edits; Desktop composes services. Rendering and Media use explicit contracts and resources; business view models do not hold controls, Dock objects, or bitmaps.

## Time and storage

Time is rational `MediaTime`; Clip visibility is `[Start, End)`. Decoding and VFR use actual presentation timestamps. Subtitle-format exchange maps confirmed playback origin only at the boundary; relative animation/karaoke time is retained.

New `.aeginext` files use **v12** and read v3–v11. Migration maps the old scalar margin equally to Left, Right, and Vertical, preserving existing placement and subtitle/layer times; an unknown playback origin remains unknown until confirmed. Missing/null legacy masks are accepted, but unsupported nonempty legacy local masks reject loading with the affected object identified. v1/v2 are unsupported. Style libraries use **v6** and read v1–v5 with the same margin migration. Older applications cannot read newly saved v12 projects or v6 style libraries.

The flat Track → Clip model permits subtitles, images, and shapes on the same track. Clips cannot overlap within a track, and the track array is the sole compositing order: the top track is drawn last. Migration retains existing tracks where possible; interleaving clips in the legacy layer list does not split tracks. Derived tracks are created only when needed to preserve the order of simultaneously active clips or resolve overlap within a track. Legacy neutral groups can be flattened only when their timing and compositing are preserved; groups with transforms, animation, opacity, masks, blur, or blend isolation reject migration with the affected group identified. Original files remain unchanged until saving.

Loading validates fields, references, overlaps, geometry, and budgets. Saving uses same-directory temporary files and atomic replacement. Videos are referenced, not copied; managed resources use confined relative paths and optional hashes. Save As rebases resources/references; a cross-directory relocation clears history that could restore obsolete paths.

Autosave captures committed content and view state through a serialized persistence coordinator. It does not commit drafts, steal focus, or change Undo. Saving a captured snapshot keeps newer edits dirty.

## Application task service

`DesktopApplicationContext` owns one `AegiTaskService`. Its kernel lives in `Application/Tasks` without Avalonia, Media, or Rendering references. Concrete business tasks declare their scope, execution mode, cancellation, editing restriction, and complete resource set independently.

The shared queue starts tasks in submission order. An unavailable resource at the head holds later submissions. A blocking task waits for all predecessors, runs exclusively, and holds every successor behind its barrier. The default limit is four; Settings → Tasks allows 1–32. Lowering it leaves running operations intact. Project writes, media controllers, personal libraries, and normalized storage paths are exclusive resources acquired together. Internal stages reuse their parent's slot, resources, and cancellation token.

A handle supports awaiting completion; cancelling the wait does not cancel its operation. Explicit cancellation keeps resources and editing leases until subprocess exit and cleanup finish. Atomic commit validates cancellation and input first, then disables cancellation. Queued tasks acquire no editing lease. Scope restrictions include that project's floating panels.

Cancellable parallel work may call `YieldIfWorkIsQueuedAsync` at a checkpoint after all internal workers and stages finish, before commit or editing leases. Yielding retains resources, identity, and start time while releasing only the execution slot. Only the queue's consecutive independent parallel prefix runs first; yielding never crosses a blocking task or resource conflict. Cancelling yielded work still drains its original execution stack, callbacks, and cleanup before a barrier can proceed. Started yielded work cannot be replaced by continuous-write coalescing.

Project creation dialogs own cancellation of their submitted task: closing the dialog explicitly requests cancellation and waits for actual completion. Commit rejects cancellation, and the created project still transfers to the workbench. Application exit prevents activating a new window after commit and cleanup finish. Ordinary callers retain the independent wait-cancellation contract.

| Business entry | Policy |
|---|---|
| Create, open, restore, merge, media replacement | Prepare asynchronously; restrict the owning project while replacing context; preserve rollback |
| Timing post-processing and keyframe indexing | Allow edits during scanning; validate revisions and drafts before one Undo commit |
| Save, Save As, resource relocation | Freeze committed save input at submission; retain newer edits and raw drafts |
| Video/subtitle export; subtitle/font/style/effect preparation | Freeze export input; briefly commit valid prepared results |
| Waveform/spectrum and system fonts | Observable background tasks; retain internal viewport replacement and analysis blocks |
| Autosave, backups, cleanup, libraries, preferences, recent projects, layouts | Serialize storage resources; coalesce compatible pending continuous writes |
| Settings transfer, startup recovery and initialization | Keep recovery barriers, journals, and transaction boundaries |

Use this entry inventory when checking that new business operations use the service. Owning coordinators submit these tasks; resource preparation, keyframe probing, and atomic storage retain their lower-level implementations.

| Module / entry | Task classes or internal stages |
|---|---|
| `Workspace` project workflows | `CreateProjectTask`, `OpenProjectTask`, `MergeProjectsTask`, `SaveProjectTask` (including Save As and resource relocation); failed preparation rolls back inside its parent task |
| `Workspace` media contexts | `SwitchProjectMediaTask`, `SynchronizeProjectMediaTask`, `SwitchPreviewDecodeModeTask` |
| `Workspace` timing | `TimingPostProcessingTask`; keyframe indexing and cache reuse are internal stages of that task |
| `Workspace` subtitle exchange / creation | `ImportSubtitlesTask`, `ExportSubtitlesTask`, `CreateSubtitleClipsTask`, `BeginTimingCueTask` |
| `Workspace` video export | `VideoExportTask`; the export panel and task list cancel the same handle |
| `Workspace` font / style / effect preparation | `ImportSubtitleFontTask`, `PrepareSubtitleStyleTask`, `ApplySubtitleStyleTask`, `ApplySubtitleTrackStyleTask`, `CaptureSubtitleStyleTask`, `ApplyEffectScriptTask` |
| `Workspace` personal resource exchange | `ImportStylePresetsTask`, `ExportStylePresetsTask`, `ImportEffectScriptsTask`, `ExportEffectScriptTask` |
| `Workspace` audio analysis / devices | `AudioAnalysisBatchTask`, `AudioCacheMigrationTask`, `ApplyAudioCalibrationTask`, `RebuildAudioOutputTask` |
| `Workspace` automatic persistence | `AutomaticProjectPersistenceTask`; backup pruning uses `ProjectPersistenceTask` or a parent stage |
| `Startup` / `Editing` application services | `ApplicationInitializationTask`, `EnumerateSystemFontsTask`, `PreferencesWriteTask`, `RecentProjectsWriteTask`, `PersonalLibraryTask` |
| `Layouts` persistence | `LayoutWriteTask`; the coordinator retains layout interaction orchestration |
| `Settings/Presets` / `Settings/Export` | `SettingsStyleImportTask`, `SettingsStyleExportTask`, `SettingsEffectImportTask`, `SettingsEffectExportTask`, `SettingsExportPresetExportTask`; library mutations use `PersonalLibraryTask` |
| `Settings/Transfer` | `UserSettingsLayoutCaptureTask`, `UserSettingsExportTask`, `UserSettingsImportTask`, `UserSettingsRestoreStageTask`, `UserSettingsRestoreCancelTask` |

There is currently no separate image import command or project backup restoration entry; images already in projects follow open, merge, and resource relocation workflows, while settings recovery follows the restoration tasks above. Playback loops, single-frame decoding, drag seeking, the audio clock, and live preview remain internal real-time pipelines whose owning business tasks manage cancellation and completion. Viewport requests and individual analysis blocks do not create separate task records.

Coalescing preserves the original identity, handle, and queue position. It never replaces running work or crosses a barrier or intervening conflicting write. Autosave reads committed content and view state without committing drafts, changing focus, or modifying Undo. Save As separately relocates the frozen save and current editor snapshots; obsolete-path history is cleared while invalid raw input remains editable.

Closing first asks the user. Cancelling that prompt leaves tasks running. A confirmed close seals its scope, requests cancellation, drains cleanup and commit, then submits final persistence through a controlled channel. Failure reopens intake before restarting persistence timers; other projects continue. Application exit cancels optional background work and drains required persistence.

Startup completes asynchronous settings recovery and initialization before constructing and explicitly showing the welcome window, preventing a running application with only a Dock icon.

The task button sits at the right edge of the main project title bar, before the measured native caption-button area on Windows. The generic title bar exposes host-owned content slots and does not access the task service. Each window owns its right-aligned Flyout while sharing task data. Task cards have two rows: name, stage/status, and a square cancel icon on the first; progress on the second. Project names and application context labels are omitted. Unknown totals use indeterminate progress while active; finished progress stays static. The Flyout uses shared typography and card styles, with its width constrained to available space and no horizontal scrolling.

Current items follow submission order; the latest 50 finished records follow reverse completion order. The Clear completed icon removes only history. Progress updates are merged at about 10 Hz; final states notify immediately. History retains only lightweight status and error summaries, without execution closures, native resources, project snapshots, or persistence.

Native macOS/Windows title-bar dragging, scaling, and platform-button hit testing require actual-window acceptance. Measure scan duration separately with real media.

## Editing and presentation

`ProjectEditor` commits immutable snapshots. Multi-Clip edits/imports are atomic; failed validation leaves the project and history unchanged. Layout changes retain one session and fixed panel instances. Personal layouts, styles, scripts, preferences, and logs are separate from project content.

Effect DSL version 2 resolves named scopes and grapheme-safe groups in Core, then `CompileTarget` / `ComposeTarget` returns both prepared subtitle ranges and animation tracks. Application applies the complete frozen result in one transaction. A generated family matches effect ID, scope name, and parent range ID; its stable grouping signature and text span determine identity reuse. Reapplication replaces that family's results. Renderer and Desktop consume native ranges and tracks rather than interpreting scripts during playback. See [Effect scripts](effect-dsl.md).

Project format 13 persists range Offset, reversed main/component curves, and generated origins. `ProjectStore` accepts versions 3–12 through strict migrations; the new optional values default to zero Offset, false Reverse, and no origin. Version 12 keeps its existing text-animation fields. Older schemas reject the new fields by their owning object, allowing existing subtitle-position Offset fields. Generated parent references must stay within one subtitle and form no cycle; validation does not require child-span containment after manual edits or text remapping.

Preview uses the same scene geometry as editing/export and presents SDR derivatives. The renderer retains linear F16 until display/encoding; worker export reads original media frames. Every asynchronous result checks time, revision, request identity, and ownership before delivery.

## Contribute

Use Allman braces, file-scoped namespaces, one top-level type per file, `var`, and target-typed `new()` where suitable. Nullable checking, analyzers, and warnings-as-errors are enabled. Public/protected API docs describe the contract.

Put focused tests in the owning Tests project. Change a native contract together with managed bindings, worker protocol, capability/version checks, and layout tests. Keep commits scoped with short English `feat:`, `fix:`, or `chore:` titles. Exclude generated artifacts and run `git diff --check` plus affected tests.

Continue with [Workspace integration](composable-workspace.md), [Media](media.md), [Rendering](rendering.md), or [Building](building.md).

Native styles store letter spacing, independent fill/outline blur, and wrap mode. Older files migrate with zero for the new numeric values and grapheme wrapping, preserving their existing appearance. Current files require the complete fields; legacy files containing the new fields are rejected. Letter spacing and both blur values are subtitle-only animation and DSL properties. Layer blur and shadow blur retain their independent meanings.
