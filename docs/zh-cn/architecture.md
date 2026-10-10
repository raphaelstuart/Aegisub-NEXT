# 架构

[English](../en/architecture.md) · [简体中文](architecture.md) · [全部指南](README.md)

## 查找职责归属

| 位置 | 职责 |
|---|---|
| `src/AegiNext.Core` | 有理时间、不可变工程/场景模型、验证、动画、蒙版和特效 DSL |
| `src/AegiNext.Application` | 编辑事务、Undo/Redo、存储/资源和字幕格式交换 |
| `src/AegiNext.Rendering` | 独立于 UI 的 Skia/HarfBuzz 排版、场景几何和线性 F16 合成 |
| `src/AegiNext.Media` | 探测、帧所有权、原生适配、播放/分析、预览和导出编排 |
| `src/AegiNext.Desktop` | Avalonia 工作区、面板、控件、菜单、偏好和平台宿主 |
| `src/AegiNext.ExportWorker` | 共享 Core 与 Rendering 的独立 `aegn-exporter` 进程 |
| `native/decoder`、`native/audio`、`native/export` | 独立 FFmpeg/SDL3 C ABI |
| `native/` | 可选 macOS HDR 诊断后端 |
| `Tests/`、`scripts/` | 定向验证、构建和打包工具 |

Core 不依赖 Avalonia、Dock、FFmpeg、Skia 或文件系统。Application 负责业务修改，Desktop 组合服务；Rendering 和 Media 使用显式契约与资源所有权。业务 ViewModel 不持有控件、Dock 对象或 Bitmap。

## 时间与存储

时间采用有理数 `MediaTime`，Clip 可见范围为 `[Start, End)`。解码与 VFR 使用真实呈现时间戳；字幕格式交换只在边界映射已确认的播放原点，保留动画/卡拉 OK 相对时间。

新 `.aeginext` 文件使用 **v12**，可读取 v3–v11。迁移将旧单值边距等值映射为左、右、垂直，保留原有排版位置和字幕／图层时间；未知播放原点保持未知直到确认。兼容缺失/null 的旧蒙版，不支持的非空旧局部蒙版会标明所属对象并拒绝读取；不支持 v1/v2。样式库使用 **v6**，可读取 v1–v5 并采用相同边距迁移。旧应用无法读取新保存的 v12 工程或 v6 样式库。

平面 Track → Clip 模型允许同一轨道容纳字幕、图片和形状。同轨片段不能重叠；轨道数组是唯一叠覆顺序，最上方轨道最后绘制。迁移尽量保留原轨道；跨轨片段在旧图层列表中交错不会导致拆轨，只有保持同时活动片段的前后关系或解决同轨重叠确实需要时才派生轨道。旧无效果组仅在时间与合成结果均可保留时展开；含变换、动画、透明度、蒙版、模糊或隔离混合的组会拒绝迁移并标明组名和标识。保存前不修改原文件。

读取校验字段、引用、重叠、几何和预算；保存通过同目录临时文件原子替换。视频只引用、不复制，托管资源使用受限相对路径与可选哈希。另存为重定位资源/引用；跨目录迁移清除可能恢复旧路径的历史。

自动保存通过串行持久化协调器捕获已提交内容和视图状态，不提交草稿、不抢焦点、不改变 Undo。保存旧快照时，较新的修改仍保持未保存。

## 应用级任务服务

`DesktopApplicationContext` 持有唯一 `AegiTaskService`；内核位于 `Application/Tasks`，不引用 Avalonia、Media 或 Rendering。业务模块的具体 `AegiTask` 声明名称、作用域、运行模式、取消、编辑限制和全部资源。运行模式与可取消性、编辑限制互相独立。

统一队列按提交序号启动，队首资源未就绪时后续任务等待。阻断任务等待此前全部任务结束并独占运行，后续任务不能越过屏障。默认并行上限为 4，可在“设置 → 任务”调至 1–32；降低上限不会终止正在运行的任务。工程写入、媒体控制器、个人库和规范化存储路径按资源互斥，全部资源一次取得。内部阶段复用父任务的名额、资源和取消令牌。

提交返回可等待的句柄；取消调用方的等待不取消任务，任务取消必须显式请求。任务进入取消中后，直到子进程退出与清理结束才释放资源及编辑租约。原子提交前检查取消和输入有效性，提交中关闭取消入口。排队不锁住工程，短时编辑租约默认只限制所属工程及其浮动面板。

可取消并行任务可在内部 worker／阶段全部完成且未进入提交或取得编辑租约的检查点调用 `YieldIfWorkIsQueuedAsync`。让出时保留资源、任务身份与开始时间，只释放执行位；仅让队首连续的无冲突并行任务先执行，不跨越阻断任务或资源冲突。取消已让出任务仍须等待原执行栈、取消回调和清理完成，屏障不能越过未完成的执行。已启动的让出任务不能被连续写入合并替换。

工程创建对话框拥有其提交任务的取消权：关闭对话框会显式请求取消，并等待实际任务完成；提交中拒绝取消，已创建的工程继续交给工作台。应用退出在提交和清理完成后阻止新窗口激活。普通调用方的等待取消契约保持独立。

| 业务入口 | 任务策略 |
|---|---|
| 新建、打开、恢复、合并、媒体切换 | 后台准备，替换上下文时限制所属工程，失败回滚 |
| 时间后处理、关键帧索引 | 扫描时允许编辑，提交前校验输入版本及草稿，整批一次 Undo |
| 保存、另存为、资源重定位 | 使用提交时冻结的已提交快照；较新编辑和原始草稿继续保留 |
| 视频／字幕导出、字幕／字体／样式／效果导入准备 | 冻结导出输入；准备结果有效时短时提交 |
| 波形／频谱、系统字体 | 可观察的后台任务；视口替换和分析块留在内部管线 |
| 自动保存、备份、清理、个人库、首选项、最近工程、布局 | 声明存储资源；连续写入合并同目标最新待运行请求 |
| 设置传输、启动恢复与初始化 | 恢复遵守全局屏障，保留恢复日志和事务边界 |

具体入口清单用于检查新增业务是否绕过服务。下表中的任务均由所属协调器提交；资源准备、关键帧探测与原子写入保留底层实现。

| 所属模块／入口 | 任务类或内部阶段 |
|---|---|
| `Workspace` 工程工作流 | `CreateProjectTask`、`OpenProjectTask`、`MergeProjectsTask`、`SaveProjectTask`（含另存为与资源迁移）；失败回滚在父任务内收尾 |
| `Workspace` 媒体上下文 | `SwitchProjectMediaTask`、`SynchronizeProjectMediaTask`、`SwitchPreviewDecodeModeTask` |
| `Workspace` 时间后处理 | `TimingPostProcessingTask`；关键帧索引与缓存是同一任务的内部阶段 |
| `Workspace` 字幕交换／创建 | `ImportSubtitlesTask`、`ExportSubtitlesTask`、`CreateSubtitleClipsTask`、`BeginTimingCueTask` |
| `Workspace` 视频导出 | `VideoExportTask`；导出面板和任务列表取消同一句柄 |
| `Workspace` 字体／样式／效果准备 | `ImportSubtitleFontTask`、`PrepareSubtitleStyleTask`、`ApplySubtitleStyleTask`、`ApplySubtitleTrackStyleTask`、`CaptureSubtitleStyleTask`、`ApplyEffectScriptTask` |
| `Workspace` 个人资源交换 | `ImportStylePresetsTask`、`ExportStylePresetsTask`、`ImportEffectScriptsTask`、`ExportEffectScriptTask` |
| `Workspace` 音频分析／设备 | `AudioAnalysisBatchTask`、`AudioCacheMigrationTask`、`ApplyAudioCalibrationTask`、`RebuildAudioOutputTask` |
| `Workspace` 自动持久化 | `AutomaticProjectPersistenceTask`；备份清理通过 `ProjectPersistenceTask` 或父任务阶段执行 |
| `Startup`／`Editing` 应用服务 | `ApplicationInitializationTask`、`EnumerateSystemFontsTask`、`PreferencesWriteTask`、`RecentProjectsWriteTask`、`PersonalLibraryTask` |
| `Layouts` 布局持久化 | `LayoutWriteTask`；保留协调器对布局交互的编排 |
| `Settings/Presets`／`Settings/Export` | `SettingsStyleImportTask`、`SettingsStyleExportTask`、`SettingsEffectImportTask`、`SettingsEffectExportTask`、`SettingsExportPresetExportTask`；库增删改使用 `PersonalLibraryTask` |
| `Settings/Transfer` | `UserSettingsLayoutCaptureTask`、`UserSettingsExportTask`、`UserSettingsImportTask`、`UserSettingsRestoreStageTask`、`UserSettingsRestoreCancelTask` |

目前没有独立图片导入命令或工程备份恢复入口；已有工程中的图片资源随打开、合并和资源迁移处理，设置恢复遵守上述恢复任务边界。播放循环、单帧解码、拖动定位、音频时钟与实时预览继续走内部实时管线，其所属耗时业务任务负责取消和完成。分析视口请求及每个分析块不会各自产生任务记录。

连续写入合并保留原任务身份、句柄与排队位置，不替换已运行请求，也不跨越屏障或中间的冲突写入。自动保存仅捕获已提交内容和视图状态，不提交草稿、不改变焦点或 Undo。另存为分别重定位保存快照和最新编辑状态，跨目录迁移清除会恢复旧路径的历史，保留无效输入草稿。

工程关闭先询问用户；取消关闭不影响任务。确认后封闭该作用域、取消可取消任务并等待真实清理及提交，再通过受控通道执行最终保存。失败时先恢复接单再恢复持久化计时器，其他工程继续运行。应用退出取消可取消后台工作并排空必须完成的持久化。

启动先异步完成设置恢复与初始化，再构造欢迎窗口并主动显示，保证异步返回后不会留下仅有 Dock 图标的应用。

任务入口位于工程主窗口标题栏最右侧，在 Windows 上位于按原生测量预留的窗口按钮区域左侧。通用标题栏提供由宿主填充的内容插槽，不读取任务服务。各窗口共享任务数据并独立持有右缘对齐的 Flyout。任务卡片采用两行：首行是名称、阶段／状态和方形取消图标按钮，第二行是进度条；不展示工程名或“应用”等上下文文字。未知总量在运行时显示不定进度，历史进度静止。弹层使用统一字体与卡片样式，宽度随可用空间约束，无横向滚动。

运行／排队项按提交顺序，历史按结束时间倒序，保留本次启动最近 50 条轻量记录，“清空已完成”图标按钮只移除历史。状态终结立即通知，进度合并约每秒 10 次；历史不保存快照、执行闭包或原生资源，也不持久化。

原生 macOS／Windows 的标题栏拖动、窗口缩放及平台按钮命中仍需真实窗口验收。扫描本身的耗时需使用实际媒体另行测量。

## 编辑与呈现

`ProjectEditor` 提交不可变快照，多 Clip 编辑/导入使用原子事务；校验失败不改变工程和历史。布局变化保留同一会话与固定面板实例。个人布局、样式、脚本、偏好和日志与工程内容独立。

特效 DSL 版本 2 在 Core 解析命名作用域和字素安全的文字组，由 `CompileTarget`／`ComposeTarget` 同时返回准备后的字幕范围与动画轨道，Application 一次事务应用完整冻结结果。生成块以脚本 ID、作用域名、父范围 ID 匹配，稳定分组签名与文字跨度决定是否复用身份；重新应用替换该块结果。Rendering 与 Desktop 消费原生范围和轨道，不在播放时解释脚本，详见[特效脚本](effect-dsl.md)。

工程格式 13 保存范围 Offset、主曲线／分量曲线的 Reverse 和生成来源。`ProjectStore` 严格迁移版本 3–12；新可选字段默认为零 Offset、false Reverse、无生成来源，版本 12 保留既有文字动画字段。旧版本按字段所属对象拒绝新能力，已有字幕位置 Offset 仍合法。生成父引用必须属于同一句字幕且不形成环；手工编辑或文字映射后不要求子范围跨度包含于父范围。

预览复用编辑/导出的场景几何，显示 SDR 派生图；渲染器在显示/编码前保持线性 F16，worker 导出读取原始媒体帧。异步结果交付前检查时间、修订、请求身份和所有权。

## 参与开发

C# 使用 Allman 花括号、文件级命名空间、单文件单顶层类型，适当使用 `var` 和目标类型 `new()`。开启可空检查、分析器和警告视为错误，公开/受保护 API 文档描述契约。

测试放在所属 Tests 项目并定向运行。修改原生契约时同步托管绑定、worker 协议、能力/版本检查与布局测试。提交围绕明确范围，采用简短英文 `feat:`、`fix:` 或 `chore:` 标题；排除生成产物，执行 `git diff --check` 和受影响测试。

继续阅读[工作区集成](composable-workspace.md)、[媒体](media.md)、[渲染](rendering.md)或[构建](building.md)。

原生样式保存字距、独立填充/描边模糊和换行模式；从旧版本迁移时新增数值为零、换行模式为按字素换行，保留原有外观。新版本要求这些字段完整，旧版本夹带新增字段会被拒绝。字距及双模糊可作为仅适用于字幕的动画轨道和 DSL 属性；整层模糊与阴影模糊保留独立含义。
