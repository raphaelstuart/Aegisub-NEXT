using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AegiNext.Application;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Layouts;
using AegiNext.Desktop.Panels.Timeline;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Views;
using AegiNext.Desktop.Workspace;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class TimelineAnimationRowCollapseWorkflowUiTests
{
    [AvaloniaFact]
    public async Task NewAnimationRowsStartCompactAndPointerExpansionSurvivesCurveEdits()
    {
        using var environment = new UiTestEnvironment();
        var document = CreateDocument();
        document = document with { Layers = [document.Layers[0] with { Tracks = [] }] };
        await using var session = await CreateSessionAsync(environment, new(), document);
        var row = GetRowId(document, TimelineRowScope.TRACK);
        var layerId = document.Layers[0].Id;
        var window = ShowWindow(session);
        try
        {
            var timeline = UiTestActions.Find<SubtitleTimelineControl>(window, "Timeline");
            session.Editor.SetKeyframe(layerId, AnimationProperty.OPACITY, new(new(0), 0.25));
            Flush(window);

            Assert.True(timeline.IsAnimationRowCollapsed(row));
            Assert.Equal(40, timeline.GetAnimationRowRectangle(row)!.Value.Height);
            ClickAnimationExpander(window, timeline, row);
            session.Editor.SetKeyframe(layerId, AnimationProperty.OPACITY, new(new(0), 0.5));
            Flush(window);

            Assert.False(timeline.IsAnimationRowCollapsed(row));
            Assert.Equal(76, timeline.GetAnimationRowRectangle(row)!.Value.Height);
            ClickAnimationExpander(window, timeline, row);
            session.Editor.SetKeyframe(layerId, AnimationProperty.OPACITY, new(new(0), 0.75));
            Flush(window);

            Assert.True(timeline.IsAnimationRowCollapsed(row));
            Assert.Equal(40, timeline.GetAnimationRowRectangle(row)!.Value.Height);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(TimelineRowScope.TRACK)]
    public async Task PointerCollapsePreservesContentSnapshotRedoAndTheCurrentViewAcrossContentUndo(TimelineRowScope scope)
    {
        using var environment = new UiTestEnvironment();
        var dialogs = new StartupTestDialogService();
        await using var session = await CreateSessionAsync(environment, dialogs, CreateDocument());
        var original = session.DocumentSnapshot;
        var row = GetRowId(original, scope);
        session.Editor.Apply("Resize project", document => document with { Width = 1280 });
        await session.WaitForProjectIdleAsync();
        Assert.True(session.Editor.Undo());
        await session.WaitForProjectIdleAsync();
        var redoLabel = session.Editor.RedoLabel;
        var window = ShowWindow(session);
        try
        {
            var timeline = UiTestActions.Find<SubtitleTimelineControl>(window, "Timeline");
            Assert.Equal(76, timeline.GetAnimationRowRectangle(row)!.Value.Height);

            ClickAnimationExpander(window, timeline, row);

            Assert.True(timeline.IsAnimationRowCollapsed(row));
            Assert.Equal(40, timeline.GetAnimationRowRectangle(row)!.Value.Height);
            Assert.Equal(row, Assert.Single(session.TimelineViewState.CollapsedAnimationRows));
            Assert.Same(original, session.DocumentSnapshot);
            Assert.True(session.HasUnsavedChanges);
            Assert.False(session.Editor.HasUnsavedChanges);
            Assert.False(session.Editor.CanUndo);
            Assert.True(session.Editor.CanRedo);
            Assert.Equal(redoLabel, session.Editor.RedoLabel);

            Assert.True(session.Editor.Redo());
            await session.WaitForProjectIdleAsync();
            Flush(window);

            Assert.Equal(1280, session.DocumentSnapshot.Width);
            Assert.True(timeline.IsAnimationRowCollapsed(row));
            Assert.Equal(40, timeline.GetAnimationRowRectangle(row)!.Value.Height);
            Assert.True(session.Editor.Undo());
            await session.WaitForProjectIdleAsync();
            Flush(window);
            Assert.Same(original, session.DocumentSnapshot);
            Assert.True(timeline.IsAnimationRowCollapsed(row));
            Assert.True(session.HasUnsavedChanges);

            ClickAnimationExpander(window, timeline, row);

            Assert.False(timeline.IsAnimationRowCollapsed(row));
            Assert.Equal(76, timeline.GetAnimationRowRectangle(row)!.Value.Height);
            Assert.Empty(session.TimelineViewState.CollapsedAnimationRows);
            Assert.False(session.HasUnsavedChanges);
            Assert.False(session.Editor.CanUndo);
            Assert.True(session.Editor.CanRedo);
            Assert.Equal(redoLabel, session.Editor.RedoLabel);
            Assert.Same(original, session.DocumentSnapshot);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PointerCollapseKeepsFocusedValidOrInvalidInspectorTextAndItsLastValidPreview(bool invalid, bool useTouch)
    {
        using var environment = new UiTestEnvironment();
        var dialogs = new StartupTestDialogService();
        await using var session = await CreateSessionAsync(environment, dialogs, CreateDocument());
        var original = session.DocumentSnapshot;
        var row = GetRowId(original, TimelineRowScope.TRACK);
        var window = ShowWindow(session);
        try
        {
            var timeline = UiTestActions.Find<SubtitleTimelineControl>(window, "Timeline");
            window.Layouts.Activate(WorkbenchPanelIds.STYLES);
            var input = UiTestActions.Find<NumericDraftInput>(window, "FontSizeInput");
            input.BringIntoView();
            Flush(window);
            var box = Assert.Single(input.GetVisualDescendants().OfType<TextBox>());
            EnterText(window, box, "72.50");
            var preview = session.PreviewDocument;
            Assert.Equal(72.5, preview.Subtitles[0].Style.FontSize);
            Assert.NotEqual(72.5, original.Subtitles[0].Style.FontSize);
            if (invalid)
            {
                EnterText(window, box, "7e-");
                Assert.Same(preview, session.PreviewDocument);
            }
            var rawText = invalid ? "7e-" : "72.50";
            Assert.True(box.IsFocused);

            ClickAnimationExpander(window, timeline, row, useTouch);

            Assert.True(timeline.IsAnimationRowCollapsed(row));
            Assert.Equal(40, timeline.GetAnimationRowRectangle(row)!.Value.Height);
            Assert.True(box.IsFocused);
            Assert.Equal(rawText, box.Text);
            Assert.Equal(rawText, session.ViewModel.Styles.FontSizeText);
            Assert.Same(original, session.DocumentSnapshot);
            Assert.Same(preview, session.PreviewDocument);
            Assert.Same(preview, session.ViewModel.Preview.Scene.Document);
            Assert.True(session.HasUnsavedChanges);
            Assert.False(session.Editor.HasUnsavedChanges);
            Assert.False(session.Editor.CanUndo);
            Assert.False(session.Editor.CanRedo);

            ClickAnimationExpander(window, timeline, row, useTouch);

            Assert.False(timeline.IsAnimationRowCollapsed(row));
            Assert.True(box.IsFocused);
            Assert.Equal(rawText, box.Text);
            Assert.Equal(rawText, session.ViewModel.Styles.FontSizeText);
            Assert.Same(original, session.DocumentSnapshot);
            Assert.Same(preview, session.PreviewDocument);
            Assert.True(session.HasProjectDrafts);
            Assert.True(session.HasUnsavedChanges);
            Assert.Empty(session.ViewModel.Timeline.TimelineViewState.CollapsedAnimationRows);
            Assert.False(session.Editor.CanUndo);
        }
        finally
        {
            session.ViewModel.Styles.FontSizeText = original.Subtitles[0].Style.FontSize.ToString(CultureInfo.CurrentCulture);
            Dispatcher.UIThread.RunJobs();
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EffectsScalarDraftAndExistingRedoSurviveMouseCollapseAndExpansion(bool invalid)
    {
        using var environment = new UiTestEnvironment();
        var dialogs = new StartupTestDialogService();
        await using var session = await CreateSessionAsync(environment, dialogs, CreateDocument());
        var original = session.DocumentSnapshot;
        var row = GetRowId(original, TimelineRowScope.TRACK);
        session.Editor.Apply("Resize project", document => document with { Width = 1280 });
        await session.WaitForProjectIdleAsync();
        Assert.True(session.Editor.Undo());
        await session.WaitForProjectIdleAsync();
        var undoLabel = session.Editor.UndoLabel;
        var redoLabel = session.Editor.RedoLabel;
        var window = ShowWindow(session);
        try
        {
            var timeline = UiTestActions.Find<SubtitleTimelineControl>(window, "Timeline");
            window.Layouts.Activate(WorkbenchPanelIds.EFFECTS);
            var input = UiTestActions.Find<NumericDraftInput>(window, "RotationInput");
            input.BringIntoView();
            Flush(window);
            var box = Assert.Single(input.GetVisualDescendants().OfType<TextBox>());
            EnterText(window, box, "24.25");
            var preview = session.PreviewDocument;
            Assert.Equal(24.25, preview.Layers[0].Transform.Rotation);
            Assert.Equal(0, original.Layers[0].Transform.Rotation);
            if (invalid)
            {
                EnterText(window, box, "7e-");
                Assert.Same(preview, session.PreviewDocument);
            }
            var rawText = invalid ? "7e-" : "24.25";

            foreach (var collapsed in new[] { true, false })
            {
                ClickAnimationExpander(window, timeline, row);

                Assert.Equal(collapsed, timeline.IsAnimationRowCollapsed(row));
                Assert.Equal(collapsed ? 40 : 76, timeline.GetAnimationRowRectangle(row)!.Value.Height);
                Assert.True(box.IsFocused);
                Assert.Equal(rawText, box.Text);
                Assert.Equal(rawText, session.ViewModel.Effects.RotationText);
                Assert.Same(original, session.DocumentSnapshot);
                Assert.Same(preview, session.PreviewDocument);
                Assert.Same(preview, session.ViewModel.Preview.Scene.Document);
                Assert.True(session.HasProjectDrafts);
                Assert.True(session.HasUnsavedChanges);
                Assert.Equal(collapsed, session.ViewModel.Timeline.TimelineViewState.CollapsedAnimationRows.Contains(row));
                Assert.False(session.Editor.HasUnsavedChanges);
                Assert.False(session.Editor.CanUndo);
                Assert.True(session.Editor.CanRedo);
                Assert.Equal(undoLabel, session.Editor.UndoLabel);
                Assert.Equal(redoLabel, session.Editor.RedoLabel);
            }
        }
        finally
        {
            session.ViewModel.Effects.RestoreField("RotationInput");
            Dispatcher.UIThread.RunJobs();
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ReleasingCollapseOutsideThePanelDoesNotSwallowTheNextKeyframeDragRelease()
    {
        using var environment = new UiTestEnvironment();
        var dialogs = new StartupTestDialogService();
        await using var session = await CreateSessionAsync(environment, dialogs, CreateDocument());
        var original = session.DocumentSnapshot;
        var row = GetRowId(original, TimelineRowScope.TRACK);
        var layerId = original.Subtitles[0].Id;
        session.ViewModel.Timeline.IsSnapEnabled = false;
        var window = ShowWindow(session);
        try
        {
            var timeline = UiTestActions.Find<SubtitleTimelineControl>(window, "Timeline");
            var collapse = timeline.TranslatePoint(timeline.GetAnimationRowExpanderRectangle(row)!.Value.Center, window)!.Value;
            Assert.Same(timeline, window.InputHitTest(collapse));
            window.MouseDown(collapse, MouseButton.Left);
            Flush(window);
            Assert.True(timeline.IsAnimationRowCollapsed(row));
            var styles = window.Panels[WorkbenchPanelIds.STYLES];
            var outside = styles.TranslatePoint(new Point(styles.Bounds.Width / 2, styles.Bounds.Height / 2), window)!.Value;
            Assert.NotNull(window.InputHitTest(outside));
            Assert.NotSame(timeline, window.InputHitTest(outside));

            window.MouseMove(outside);
            window.MouseUp(outside, MouseButton.Left);
            Flush(window);

            Assert.Same(original, session.DocumentSnapshot);
            Assert.False(session.Editor.CanUndo);
            var commits = 0;
            timeline.KeyframeMoved += (_, _) => commits++;
            var start = timeline.TranslatePoint(timeline.GetKeyframePoint(layerId, AnimationProperty.OPACITY,
                new(2), 0.75)!.Value, window)!.Value;
            Assert.Same(timeline, window.InputHitTest(start));
            var destination = start + new Vector(timeline.PixelsPerSecond, 0);

            window.MouseDown(start, MouseButton.Left);
            Assert.True(timeline.HasActiveDrag);
            window.MouseMove(destination);
            window.MouseUp(destination, MouseButton.Left);
            await session.WaitForProjectIdleAsync();
            Flush(window);

            Assert.False(timeline.HasActiveDrag);
            Assert.Equal(1, commits);
            var keys = session.DocumentSnapshot.Layers.Single(layer => layer.Id == layerId).Tracks[0].Keyframes;
            Assert.Equal(new MediaTime(3), keys[1].Time);
            Assert.Equal(0.75, keys[1].Value.Scalar);
            Assert.Equal(row, Assert.Single(session.TimelineViewState.CollapsedAnimationRows));
            Assert.True(session.Editor.CanUndo);
            Assert.True(session.Editor.Undo());
            await session.WaitForProjectIdleAsync();
            Assert.Same(original, session.DocumentSnapshot);
            Assert.False(session.Editor.CanUndo);
            Assert.True(session.Editor.CanRedo);
            Assert.True(timeline.IsAnimationRowCollapsed(row));
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(TimelineRowScope.TRACK)]
    public async Task ManualSaveAndProjectSwitchRestoreCompactRowsWhileLegacyEmptyStateOpensExpanded(TimelineRowScope scope)
    {
        using var environment = new UiTestEnvironment();
        var dialogs = new StartupTestDialogService();
        var document = CreateDocument();
        await using var session = await CreateSessionAsync(environment, dialogs, document);
        var originalPath = session.ProjectPath!;
        var original = session.DocumentSnapshot;
        var row = GetRowId(original, scope);
        var secondPath = Path.Combine(environment.DirectoryPath, "legacy-expanded.aeginext");
        var legacy = JsonNode.Parse(ProjectStore.Serialize(document with { Id = Guid.NewGuid(), Name = "Second project" }))!.AsObject();
        Assert.True(legacy.Remove("timelineViewState"));
        await File.WriteAllBytesAsync(secondPath, Encoding.UTF8.GetBytes(legacy.ToJsonString()));
        var window = ShowWindow(session);
        try
        {
            var timeline = UiTestActions.Find<SubtitleTimelineControl>(window, "Timeline");
            ClickAnimationExpander(window, timeline, row);
            Assert.True(session.HasUnsavedChanges);

            await session.ExecuteCommandAsync(WorkbenchCommand.SAVE_PROJECT);
            Flush(window);

            Assert.Null(session.LastError);
            Assert.False(session.HasUnsavedChanges);
            Assert.False(session.Editor.CanUndo);
            Assert.Same(original, session.DocumentSnapshot);
            Assert.Equal(row, Assert.Single((await ProjectStore.LoadAsync(originalPath)).TimelineViewState.CollapsedAnimationRows));

            Assert.Equal(ProjectOpenStatus.OPENED, (await session.OpenProjectAsync(secondPath)).Status);
            await session.WaitForProjectIdleAsync();
            Flush(window);

            Assert.Empty(session.TimelineViewState.CollapsedAnimationRows);
            Assert.Empty(timeline.TimelineViewState.CollapsedAnimationRows);
            Assert.False(timeline.IsAnimationRowCollapsed(row));
            Assert.Equal(76, timeline.GetAnimationRowRectangle(row)!.Value.Height);
            Assert.False(session.HasUnsavedChanges);

            Assert.Equal(ProjectOpenStatus.OPENED, (await session.OpenProjectAsync(originalPath)).Status);
            await session.WaitForProjectIdleAsync();
            Flush(window);

            Assert.Equal(row, Assert.Single(session.TimelineViewState.CollapsedAnimationRows));
            Assert.True(timeline.IsAnimationRowCollapsed(row));
            Assert.Equal(40, timeline.GetAnimationRowRectangle(row)!.Value.Height);
            Assert.False(session.HasUnsavedChanges);
            Assert.False(session.Editor.CanUndo);
            Assert.False(session.Editor.CanRedo);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task OuterTrackPointerTogglePreservesItsInnerAnimationRowCollapseState()
    {
        using var environment = new UiTestEnvironment();
        var dialogs = new StartupTestDialogService();
        await using var session = await CreateSessionAsync(environment, dialogs, CreateDocument());
        var original = session.DocumentSnapshot;
        var row = GetRowId(original, TimelineRowScope.TRACK);
        var window = ShowWindow(session);
        try
        {
            var timeline = UiTestActions.Find<SubtitleTimelineControl>(window, "Timeline");
            ClickAnimationExpander(window, timeline, row);
            Assert.Equal(40, timeline.GetAnimationRowRectangle(row)!.Value.Height);

            ClickOuterTrackExpander(window, timeline, row.OwnerId);

            Assert.True(timeline.IsTrackCollapsed(row.OwnerId));
            Assert.Null(timeline.GetAnimationRowRectangle(row));
            Assert.Equal(row, Assert.Single(session.TimelineViewState.CollapsedAnimationRows));
            Assert.True(session.HasUnsavedChanges);
            Assert.Same(original, session.DocumentSnapshot);

            ClickOuterTrackExpander(window, timeline, row.OwnerId);

            Assert.False(timeline.IsTrackCollapsed(row.OwnerId));
            Assert.True(timeline.IsAnimationRowCollapsed(row));
            Assert.Equal(40, timeline.GetAnimationRowRectangle(row)!.Value.Height);
            Assert.Equal(row, Assert.Single(session.TimelineViewState.CollapsedAnimationRows));
            Assert.False(session.Editor.CanUndo);

            ClickAnimationExpander(window, timeline, row);

            Assert.False(timeline.IsAnimationRowCollapsed(row));
            Assert.Empty(session.TimelineViewState.CollapsedAnimationRows);
            Assert.False(session.HasUnsavedChanges);
            Assert.Same(original, session.DocumentSnapshot);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task DisposedPanelStopsWritingViewStateAndARecreatedPanelReceivesOnePointerRequest()
    {
        using var environment = new UiTestEnvironment();
        var dialogs = new StartupTestDialogService();
        await using var session = await CreateSessionAsync(environment, dialogs, CreateDocument());
        var original = session.DocumentSnapshot;
        var row = GetRowId(original, TimelineRowScope.TRACK);
        var panel = new TimelinePanelView(session.ViewModel.Timeline, session);
        var window = new Window { Width = 1000, Height = 300, Content = panel };
        window.Show();
        try
        {
            Flush(window);
            var disposedTimeline = UiTestActions.Find<SubtitleTimelineControl>(window, "Timeline");
            var disposedPoint = disposedTimeline.GetAnimationRowExpanderRectangle(row)!.Value.Center;
            panel.Dispose();
            window.Content = null;
            Flush(window);

            Assert.True(disposedTimeline.TryRequestAnimationRowCollapse(disposedPoint));
            Assert.Empty(session.TimelineViewState.CollapsedAnimationRows);
            Assert.False(session.HasUnsavedChanges);
            Assert.Same(original, session.DocumentSnapshot);

            panel = new(session.ViewModel.Timeline, session);
            window.Content = panel;
            Flush(window);
            var timeline = UiTestActions.Find<SubtitleTimelineControl>(window, "Timeline");
            Assert.NotSame(disposedTimeline, timeline);
            var requests = new List<TimelineAnimationRowCollapseEventArgs>();
            timeline.AnimationRowCollapseRequested += (_, e) => requests.Add(e);
            var point = timeline.TranslatePoint(timeline.GetAnimationRowExpanderRectangle(row)!.Value.Center, window)!.Value;
            Assert.Same(timeline, window.InputHitTest(point));

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Flush(window);

            var request = Assert.Single(requests);
            Assert.Equal(row, request.Id);
            Assert.True(request.IsCollapsed);
            Assert.Equal(row, Assert.Single(session.TimelineViewState.CollapsedAnimationRows));
            Assert.True(timeline.IsAnimationRowCollapsed(row));
            Assert.Equal(40, timeline.GetAnimationRowRectangle(row)!.Value.Height);
            Assert.False(disposedTimeline.IsAnimationRowCollapsed(row));
            Assert.Empty(disposedTimeline.TimelineViewState.CollapsedAnimationRows);
            Assert.Same(original, session.DocumentSnapshot);
            Assert.False(session.Editor.CanUndo);
            Assert.True(session.HasUnsavedChanges);
        }
        finally
        {
            panel.Dispose();
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible);
        }
    }

    private static async Task<WorkbenchSession> CreateSessionAsync(UiTestEnvironment environment,
        StartupTestDialogService dialogs, ProjectDocument document)
    {
        var path = Path.Combine(environment.DirectoryPath, "collapsed-rows.aeginext");
        await ProjectStore.SaveAsync(document, path);
        var session = new WorkbenchSession(dialogs, preferencesStore: new(environment.DirectoryPath),
            initialPreferences: new()
            {
                Language = "en-US",
                Projects = new()
                {
                    WorkspaceRoot = environment.DirectoryPath, AutoSaveEnabled = false, BackupEnabled = false
                }
            });
        try
        {
            await session.Styles.Completion;
            Assert.Equal(ProjectOpenStatus.OPENED, (await session.OpenProjectAsync(path)).Status);
            session.SelectCue(document.Subtitles[0].Id);
            await session.WaitForProjectIdleAsync();
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static ProjectDocument CreateDocument()
    {
        var cue = new SubtitleLine { End = new(4), Text = "Collapse 中文 ABC 123" };
        return new()
        {
            Name = "Animation row collapse",
            Subtitles = [cue],
            Layers =
            [
                new()
                {
                    Id = cue.Id, Kind = LayerKind.SUBTITLE, SubtitleId = cue.Id, Start = cue.Start, End = cue.End,
                    Tracks = [new(AnimationProperty.OPACITY, [new(new(0), 0.25), new(new(2), 0.75)])]
                }
            ]
        };
    }

    private static TimelineAnimationRowId GetRowId(ProjectDocument document, TimelineRowScope scope)
    {
        return new(scope, Assert.Single(document.Tracks).Id, AnimationProperty.OPACITY);
    }

    private static MainWindow ShowWindow(WorkbenchSession session)
    {
        var window = new MainWindow(session) { Width = 1600, Height = 1040 };
        window.Show();
        Flush(window);
        return window;
    }

    private static void ClickAnimationExpander(MainWindow window, SubtitleTimelineControl timeline, TimelineAnimationRowId row,
        bool useTouch = false)
    {
        Flush(window);
        var rectangle = timeline.GetAnimationRowExpanderRectangle(row)!.Value;
        if (rectangle.Center.Y < timeline.RulerHeight || rectangle.Center.Y > timeline.Bounds.Height)
        {
            window.ViewModel.Timeline.Viewport = window.ViewModel.Timeline.Viewport with
            {
                VerticalOffset = Math.Max(0, timeline.Viewport.VerticalOffset + rectangle.Top - timeline.RulerHeight)
            };
            Flush(window);
            rectangle = timeline.GetAnimationRowExpanderRectangle(row)!.Value;
        }
        Assert.True(rectangle.Center.Y >= timeline.RulerHeight);
        Assert.True(rectangle.Center.Y < timeline.Bounds.Height);
        if (useTouch)
        {
            var focused = window.FocusManager!.GetFocusedElement();
            var point = timeline.TranslatePoint(rectangle.Center, window)!.Value;
            Assert.Same(timeline, window.InputHitTest(point));
            using var touch = window.TouchBegin(point);
            Flush(window);
            Assert.Same(focused, window.FocusManager.GetFocusedElement());
            window.TouchEnd(touch, point);
            Flush(window);
            Assert.Same(focused, window.FocusManager.GetFocusedElement());
        }
        else
        {
            ClickTimelinePoint(window, timeline, rectangle.Center);
        }
    }

    private static void ClickOuterTrackExpander(MainWindow window, SubtitleTimelineControl timeline, Guid trackId)
    {
        Flush(window);
        var rectangle = timeline.GetTrackHeaderRectangle(trackId)!.Value;
        ClickTimelinePoint(window, timeline, new(14, rectangle.Top + 14));
    }

    private static void ClickTimelinePoint(MainWindow window, SubtitleTimelineControl timeline, Point localPoint)
    {
        var point = timeline.TranslatePoint(localPoint, window)!.Value;
        Assert.Same(timeline, window.InputHitTest(point));
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Flush(window);
    }

    private static void EnterText(MainWindow window, TextBox box, string text)
    {
        Assert.True(box.Focus());
        box.SelectAll();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
    }

    private static async Task CloseWindowAsync(MainWindow window)
    {
        await window.DisposeAsync();
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
    }
}
