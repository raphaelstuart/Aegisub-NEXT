using System.Collections.Immutable;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AegiNext.Media.Preview;
using AegiNext.Rendering.Projects;
using SkiaSharp;
using AegiNext.Desktop.Rendering;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Windowing;
using PathGeometry = AegiNext.Core.Projects.PathGeometry;

namespace AegiNext.Desktop.Controls;

/// <summary>工程坐标画布；可拖拽层位置、贝塞尔锚点及控制柄，组变换参与坐标换算。</summary>
public sealed partial class EffectCanvasControl : Control, IDisposable, IWorkbenchFocusCommandTarget
{
    private ProjectDocument document = new();
    private readonly VideoFrameSurface sceneSurface = new();
    private ProjectSceneRenderer? renderer;
    private ScenePreviewScheduler? previewScheduler;
    private Task previewDrain = Task.CompletedTask;
    private long previewSequence;
    private long presentedPreviewSequence;
    private long sceneRevision;
    private MediaTime? compositeTime;
    private MediaTime? compositePresentationTime;
    private MediaTime? compositePresentationEnd;
    private MediaTime? videoTime;
    private ProjectLayer? scheduledDraft;
    private ClipMask? scheduledMaskDraft;
    private bool scheduledEditingPose;
    private Guid? scheduledEditingLayerId;
    private bool scheduledInteractive;
    private bool interactivePreview;
    private bool playbackActive;
    private PixelSize maximumPreviewSize = new(960, 540);
    private string directory = Path.GetTempPath();
    private SdrVideoFrame? video;
    private SdrVideoFrame? compositeFrame;
    private ProjectDocument? compositeDocument;
    private bool editingPose;
    private bool compositeInteractive;
    private ProjectDocument? renderedDocument;
    private MediaTime renderedPosition;
    private SdrVideoFrame? renderedVideo;
    private PixelSize renderedSize;
    private Point basePosition;
    private Point pivot;
    private Point[] corners = [];
    private ProjectLayerGeometry? selectedGeometry;
    private readonly HashSet<(Type ErrorType, string Message)> reportedFailures = [];
    private bool disposed;
    private ProjectLayer? selected;
    private ProjectLayer? draft;
    private Matrix parentMatrix = Matrix.Identity;
    private Matrix layerMatrix = Matrix.Identity;
    private Point dragStart;
    private int handle = -1;
    private bool dragging;
    private MediaTime position;
    private CanvasEditMode editMode;
    private IPointer? capturedPointer;
    private bool isRendering;
    private bool renderingFaulted;
    private bool gestureCancellationPending;

    public event EventHandler<CanvasLayerEditEventArgs>? LayerEdited;
    public event EventHandler<CanvasGestureStartingEventArgs>? GestureStarting;
    public event EventHandler? GestureCancelled;
    public event EventHandler? RenderingRecovered;
    public event EventHandler<CanvasRenderingFailedEventArgs>? RenderingFailed;

    internal CanvasEditMode EditMode
    {
        get => editMode;
        set
        {
            if (editMode != value)
            {
                CancelDrag();
                editMode = value;
                UpdateBezierCursor();
                InvalidateVisual();
            }
        }
    }

    internal bool HasActiveDrag => dragging || pathTopologyEdit is not null || maskDragging || !openContour.IsEmpty || maskDeletionHandle is not null || maskInsertionHandle is not null;
    internal long PreviewSequence => previewSequence;
    internal long PresentedPreviewSequence => presentedPreviewSequence;
    internal Task PreviewCompletion => previewScheduler?.Completion ?? previewDrain;

    /// <inheritdoc />
    public bool CanExecuteFocusCommand(WorkbenchCommand command, IInputElement focusedElement) =>
        command == WorkbenchCommand.END_TEXT_INPUT && IsMaskMode;

    /// <inheritdoc />
    public bool TryExecuteFocusCommand(WorkbenchCommand command, IInputElement focusedElement)
    {
        if (!CanExecuteFocusCommand(command, focusedElement))
        {
            return false;
        }
        EditMode = CanvasEditMode.POSITION;
        MaskEditingExited?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void SetScene(ProjectDocument value, ProjectLayer? layer, MediaTime time, string? assetDirectory = null, bool editorPose = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var directoryChanged = false;
        if (assetDirectory is { } nextDirectory && directory != nextDirectory)
        {
            renderer?.Dispose();
            renderer = null;
            renderedDocument = null;
            directory = nextDirectory;
            reportedFailures.Clear();
            directoryChanged = true;
        }

        if (!directoryChanged && ReferenceEquals(document, value) && ReferenceEquals(selected, layer) && position == time && editingPose == editorPose)
        {
            return;
        }

        if (!ReferenceEquals(document, value) || selected?.Id != layer?.Id ||
            pathTopologyEdit is not null && (!ReferenceEquals(selected, layer) || position != time ||
                pathTopologyEdit.EditingEndpoint != (editorPose && layer is not null && time == layer.End)))
        {
            CancelDrag();
        }

        if (!ReferenceEquals(document, value) || position != time)
        {
            reportedFailures.Clear();
        }

        if (!ReferenceEquals(document, value))
        {
            document = value;
            renderedDocument = null;
        }

        sceneRevision++;
        selected = layer;
        position = time;
        editingPose = editorPose;

        InvalidateVisual();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (disposed)
        {
            return;
        }

        isRendering = true;
        try
        {
            RenderScene(context);
        }
        finally
        {
            isRendering = false;
        }
    }

    private void RenderScene(DrawingContext context)
    {
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#11151C")), null, new Rect(Bounds.Size));
        var board = ProjectRectangle;
        context.DrawRectangle(Brushes.Black, new Pen(new SolidColorBrush(Color.Parse("#66758A"))), board);
        if (board.Width <= 0 || board.Height <= 0)
        {
            return;
        }

        var sceneDocument = EditorDocument();
        if (draft is { } layerDraft)
        {
            sceneDocument = ReplaceLayer(sceneDocument, PrepareRenderedDraft(layerDraft));
            if (editingPose && selected is not null && position == selected.End)
            {
                sceneDocument = sceneDocument with { Layers = ExtendEditorEndpoint(sceneDocument.Layers, selected.Id) };
            }
        }
        if (maskDraft is { } clipDraft && selected is not null)
        {
            sceneDocument = ReplaceLayer(sceneDocument, selected with
            {
                Mask = clipDraft, Tracks = selected.Tracks.Where(track => !AnimationPropertyMetadata.IsMaskProperty(track.Property)).ToImmutableArray()
            });
        }
        PresentScene(sceneDocument);
        if (sceneSurface.Bitmap is { } bitmap)
        {
            context.DrawImage(bitmap, new Rect(bitmap.Size), board);
        }

        if (selected is not null && (IsMaskMode || selected.Mask is not null))
        {
            using (context.PushClip(board))
            {
                DrawClipMask(context);
            }
            if (IsMaskMode)
            {
                return;
            }
        }

        using var overlayClip = context.PushClip(EditMode == CanvasEditMode.PATH ? new Rect(Bounds.Size) : board);
        if (selected is null || !RefreshGeometry(sceneDocument))
        {
            return;
        }

        var layer = draft ?? selected;
        var fit = Fit();
        var pen = new Pen(Brushes.DeepSkyBlue, 1.5);
        var origin = pivot * fit;
        for (var index = 0; index < corners.Length; index++)
        {
            context.DrawLine(pen, corners[index] * fit, corners[(index + 1) % corners.Length] * fit);
        }

        DrawSubtitleAnchor(context, sceneDocument, layer, fit, origin);

        if (EditMode == CanvasEditMode.POSITION)
        {
            context.DrawEllipse(null, pen, origin, 9, 9);
            context.DrawLine(pen, origin - new Vector(15, 0), origin + new Vector(15, 0));
            context.DrawLine(pen, origin - new Vector(0, 15), origin + new Vector(0, 15));
            return;
        }

        var path = layer.MotionPath?.Path;
        if (path is null)
        {
            return;
        }

        var matrix = PathOrigin(layer) * parentMatrix * fit;
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(ToPoint(path.Start) * matrix, false);
            foreach (var segment in path.Segments)
            {
                stream.CubicBezierTo(ToPoint(segment.Control1) * matrix, ToPoint(segment.Control2) * matrix,
                    ToPoint(segment.End) * matrix);
            }

            stream.EndFigure(path.Closed);
        }

        context.DrawGeometry(null, pen, geometry);
        var previous = path.Start;
        foreach (var segment in path.Segments)
        {
            context.DrawLine(new Pen(Brushes.Gray), ToPoint(previous) * matrix, ToPoint(segment.Control1) * matrix);
            context.DrawLine(new Pen(Brushes.Gray), ToPoint(segment.Control2) * matrix,
                ToPoint(segment.End) * matrix);
            previous = segment.End;
        }

        var points = Points(path);
        for (var index = 0; index < points.Length; index++)
        {
            context.DrawEllipse(index % 3 == 0 ? Brushes.DeepSkyBlue : Brushes.White, pen,
                ToPoint(points[index]) * matrix, 4, 4);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (selected is null || gestureCancellationPending || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (IsMaskMode)
        {
            MaskPointerPressed(e);
            return;
        }

        bezierCursorModifiers = e.KeyModifiers;
        UpdateBezierCursor();
        var point = e.GetPosition(this);
        if (!RefreshGeometry(EditorDocument()))
        {
            return;
        }

        var fit = Fit();
        if (EditMode != CanvasEditMode.POSITION)
        {
            var path = selected.MotionPath?.Path;
            if (path is null)
            {
                return;
            }

            var matrix = PathOrigin(selected) * parentMatrix * fit;
            if (PathTopologyPointerPressed(e, point, matrix))
            {
                return;
            }
            var points = Points(path);
            handle = Array.FindIndex(points, value => DistanceSquared(ToPoint(value) * matrix, point) <= 100);
            if (handle < 0)
            {
                if (EditMode == CanvasEditMode.PATH && e.ClickCount == 2 && matrix.TryInvert(out var inverse))
                {
                    var starting = new CanvasGestureStartingEventArgs();
                    GestureStarting?.Invoke(this, starting);
                    if (!starting.Cancel)
                    {
                        var local = point * inverse;
                        var edited = AegiNext.Core.Editing.PathOperations.AppendPoint(path, new(local.X, local.Y));
                        LayerEdited?.Invoke(this, new(selected.Id, selected.Transform,
                            selected.MotionPath! with { Path = edited }));
                        e.Handled = true;
                    }
                }
                return;
            }
        }
        else
        {
            var origin = pivot * fit;
            if (DistanceSquared(origin, point) > 484 && !ContainsLayer(point, fit))
            {
                return;
            }
        }

        if (!BeginDrag(point, handle))
        {
            return;
        }

        capturedPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (IsMaskMode)
        {
            MaskPointerMoved(e);
            return;
        }
        bezierCursorModifiers = e.KeyModifiers;
        UpdateBezierCursor();
        if (!dragging || selected is null)
        {
            return;
        }

        var fit = Fit();
        var matrix = parentMatrix * fit;
        if (!matrix.TryInvert(out var inverse))
        {
            return;
        }

        var current = e.GetPosition(this) * inverse;
        var initial = dragStart * inverse;
        var delta = new Vector(current.X - initial.X, current.Y - initial.Y);
        if (EditMode == CanvasEditMode.POSITION)
        {
            draft = selected with
            {
                Transform = selected.Transform with
                {
                    X = selected.Transform.X + delta.X, Y = selected.Transform.Y + delta.Y
                }
            };
        }
        else
        {
            var source = selected.MotionPath!.Path;
            var edited = MoveHandle(source, handle, delta);
            draft = selected with { MotionPath = selected.MotionPath! with { Path = edited } };
        }

        sceneRevision++;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (IsMaskMode)
        {
            MaskPointerReleased(e);
            return;
        }
        bezierCursorModifiers = e.KeyModifiers;
        UpdateBezierCursor();
        if (pathTopologyEdit is not null)
        {
            PathTopologyPointerReleased(e);
            return;
        }
        var result = draft;
        var commit = dragging;
        CancelDrag(false);
        if (commit && result is not null)
        {
            LayerEdited?.Invoke(this, new(result.Id, result.Transform, result.MotionPath));
        }

        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!ignoreMaskCaptureLoss)
        {
            CancelDrag();
        }
    }

    internal bool BeginDrag(Point point, int handleIndex = -1)
    {
        if (gestureCancellationPending || disposed)
        {
            return false;
        }

        CancelDrag();
        if (selected is null)
        {
            return false;
        }

        if (EditMode != CanvasEditMode.POSITION)
        {
            var path = selected.MotionPath?.Path;
            if (path is null || handleIndex < 0 || handleIndex > (long)path.Segments.Length * 3)
            {
                return false;
            }
        }

        var starting = new CanvasGestureStartingEventArgs();
        GestureStarting?.Invoke(this, starting);
        if (starting.Cancel)
        {
            return false;
        }
        handle = handleIndex;
        draft = selected;
        dragStart = point;
        dragging = true;
        return true;
    }

    internal void CancelGesture() => CancelDrag();

    private void CancelDrag(bool notifyCancellation = true)
    {
        CancelMaskGesture();
        var cancelled = dragging || pathTopologyEdit is not null;
        if (draft is not null)
        {
            sceneRevision++;
        }
        dragging = false;
        pathTopologyEdit = null;
        draft = null;
        handle = -1;
        if (cancelled && notifyCancellation)
        {
            GestureCancelled?.Invoke(this, EventArgs.Empty);
        }
        var pointer = capturedPointer;
        capturedPointer = null;
        if (isRendering)
        {
            if (!gestureCancellationPending)
            {
                gestureCancellationPending = true;
                Dispatcher.UIThread.Post(() => CompleteGestureCancellation(pointer), DispatcherPriority.Normal);
            }
        }
        else if (gestureCancellationPending && pointer is null)
        {
            if (!disposed)
            {
                InvalidateVisual();
            }
        }
        else
        {
            CompleteGestureCancellation(pointer);
        }
    }

    private void CompleteGestureCancellation(IPointer? pointer)
    {
        pointer?.Capture(null);
        gestureCancellationPending = false;
        if (!disposed)
        {
            InvalidateVisual();
        }
    }

    internal Rect ProjectRectangle
    {
        get
        {
            var available = new Size(Math.Max(0, Bounds.Width), Math.Max(0, Bounds.Height));
            var scale = Math.Min(available.Width / document.Width, available.Height / document.Height);
            return new((Bounds.Width - document.Width * scale) / 2, (Bounds.Height - document.Height * scale) / 2,
                document.Width * scale, document.Height * scale);
        }
    }

    /// <summary>复用播放会话合成帧及已校验的工程相对半开帧区间；原始帧供暂存手势或编辑端点重绘。</summary>
    public void PresentComposite(SdrVideoFrame frame, SdrVideoFrame background, MediaTime? evaluationTime = null,
        ProjectDocument? evaluationDocument = null, bool interactive = false, MediaTime? presentationTime = null,
        MediaTime? presentationEnd = null)
    {
        compositeTime = videoTime = evaluationTime ?? position;
        compositePresentationTime = presentationTime;
        compositePresentationEnd = presentationEnd;
        compositeFrame = frame;
        compositeDocument = evaluationDocument;
        compositeInteractive = interactive;
        PresentVideo(background);
    }

    private ProjectDocument EditorDocument()
    {
        if (!editingPose || selected is null || position != selected.End)
        {
            return document;
        }
        return document with { Layers = ExtendEditorEndpoint(document.Layers, selected.Id) };
    }

    private ImmutableArray<ProjectLayer> ExtendEditorEndpoint(ImmutableArray<ProjectLayer> layers, Guid id)
    {
        return layers.Select(layer => layer.Id == id && layer.End == position
            ? layer with { End = layer.End + new MediaTime(1, 1000000) } : layer).ToImmutableArray();
    }

    private ProjectLayer PrepareRenderedDraft(ProjectLayer layerDraft)
    {
        if (selected is null || EditMode != CanvasEditMode.POSITION)
        {
            return layerDraft;
        }
        var local = position - selected.Start + selected.AnimationOffset;
        var track = selected.Tracks.FirstOrDefault(track => track.Property == AnimationProperty.POSITION);
        var evaluated = track is null ? selected.Transform.Position : SceneEvaluator.EvaluateVectorTrack(track, local);
        return layerDraft with
        {
            Transform = layerDraft.Transform with
            {
                Position = new(evaluated.X + layerDraft.Transform.X - selected.Transform.X,
                    evaluated.Y + layerDraft.Transform.Y - selected.Transform.Y)
            },
            Tracks = layerDraft.Tracks.Where(track => track.Property != AnimationProperty.POSITION).ToImmutableArray()
        };
    }

    internal bool HasVideo => video is not null;
    internal bool HasPresentation => sceneSurface.Bitmap is not null;
    internal bool PlaybackActive
    {
        get => playbackActive;
        set
        {
            if (playbackActive != value)
            {
                playbackActive = value;
                InvalidateVisual();
            }
        }
    }

    /// <summary>接收同一播放会话提供的原始 SDR 帧；同步复制到呈现资源，无新增解码器。</summary>
    public void PresentVideo(SdrVideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        sceneRevision++;
        video = frame;
        if (sceneSurface.Bitmap is null)
        {
            sceneSurface.Present(frame);
        }
        renderedDocument = null;
        InvalidateVisual();
    }

    internal bool InteractivePreview
    {
        get => interactivePreview;
        set
        {
            if (interactivePreview != value)
            {
                interactivePreview = value;
                sceneRevision++;
                renderedDocument = null;
                InvalidateVisual();
            }
        }
    }

    internal PixelSize MaximumPreviewSize
    {
        get => maximumPreviewSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Height);
            if (maximumPreviewSize != value)
            {
                maximumPreviewSize = value;
                sceneRevision++;
                previewSequence++;
                compositeDocument = null;
                renderedDocument = null;
                InvalidateVisual();
            }
        }
    }

    /// <summary>清除视频背景和当前呈现资源，工程底板随后重新绘制。</summary>
    public void ClearVideo()
    {
        video = null;
        previewSequence++;
        compositeFrame = null;
        compositeDocument = null;
        compositePresentationTime = null;
        compositePresentationEnd = null;
        renderedVideo = null;
        renderedDocument = null;
        sceneSurface.Clear();
        InvalidateVisual();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            CancelDrag();
            video = null;
            DetachBezierCursorHost();
            bezierCursors?.Dispose();
            bezierCursors = null;
            compositeFrame = null;
            compositeDocument = null;
            renderedVideo = null;
            renderedDocument = null;
            previewDrain = previewScheduler?.Completion ?? Task.CompletedTask;
            previewScheduler?.Dispose();
            previewScheduler = null;
            sceneSurface.Dispose();
            reportedFailures.Clear();
            renderer?.Dispose();
            renderer = null;
            LayerEdited = null;
            GestureStarting = null;
            GestureCancelled = null;
            RenderingFailed = null;
            RenderingRecovered = null;
        }
    }

    private Matrix PathOrigin(ProjectLayer layer)
    {
        var local = position - layer.Start + layer.AnimationOffset;
        var track = layer.Tracks.FirstOrDefault(track => track.Property == AnimationProperty.POSITION);
        var value = track is null ? layer.Transform.Position : SceneEvaluator.EvaluateVectorTrack(track, local);
        return Matrix.CreateTranslation(basePosition.X + value.X, basePosition.Y + value.Y);
    }

    private Matrix Fit()
    {
        var board = ProjectRectangle;
        return Matrix.CreateScale(board.Width / document.Width, board.Height / document.Height) *
               Matrix.CreateTranslation(board.X, board.Y);
    }

    private void DrawSubtitleAnchor(DrawingContext context, ProjectDocument sceneDocument, ProjectLayer layer, Matrix fit, Point origin)
    {
        if (layer.SubtitleId is not { } subtitleId || sceneDocument.Subtitles.FirstOrDefault(line => line.Id == subtitleId) is not { } cue)
        {
            return;
        }

        var placement = cue.Style.Position ?? SubtitlePosition.FromAlignment(cue.Style.Alignment, cue.Style.Margins);
        var anchor = new Point(placement.Anchor.X * sceneDocument.Width, placement.Anchor.Y * sceneDocument.Height) * parentMatrix * fit;
        var markerPen = new Pen(Brushes.Gold, 1.5);
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#80FFD700"))), anchor, origin);
        context.DrawEllipse(null, markerPen, anchor, 4.5, 4.5);
        context.DrawLine(markerPen, anchor - new Vector(7, 0), anchor + new Vector(7, 0));
        context.DrawLine(markerPen, anchor - new Vector(0, 7), anchor + new Vector(0, 7));
    }

    private bool RefreshGeometry(ProjectDocument sceneDocument)
    {
        if (selected is null || GetGeometry(sceneDocument, selected.Id) is not { } geometry)
        {
            corners = [];
            selectedGeometry = null;
            return false;
        }

        parentMatrix = ToMatrix(geometry.ParentToWorld);
        layerMatrix = ToMatrix(geometry.LocalToWorld);
        basePosition = new(geometry.BasePosition.X, geometry.BasePosition.Y);
        pivot = new(geometry.WorldPivot.X, geometry.WorldPivot.Y);
        corners = geometry.WorldCorners.Select(value => new Point(value.X, value.Y)).ToArray();
        selectedGeometry = geometry;
        return true;
    }

    private ProjectLayerGeometry? GetGeometry(ProjectDocument sceneDocument, Guid layerId)
    {
        try
        {
            renderer ??= new(new DirectoryProjectAssetResolver(directory));
            return renderer.GetLayerGeometry(sceneDocument, position, layerId);
        }
        catch (Exception error) when (IsRenderingFailure(error))
        {
            ReportRenderingFailure(error);
            return null;
        }
    }

    private bool ContainsLayer(Point point, Matrix fit)
    {
        if (!fit.TryInvert(out var inverse))
        {
            return false;
        }
        var world = point * inverse;
        return selectedGeometry?.ContainsWorldPoint(new((float)world.X, (float)world.Y)) == true;
    }

    private void PresentScene(ProjectDocument sceneDocument)
    {
        if (CanReuseComposite(sceneDocument) && compositeFrame is { } presentedFrame)
        {
            if (!ReferenceEquals(renderedVideo, presentedFrame))
            {
                previewSequence++;
                sceneSurface.Present(presentedFrame);
                presentedPreviewSequence = previewSequence;
                renderedVideo = presentedFrame;
            }
            return;
        }
        var interactive = interactivePreview || dragging || maskDragging;
        var maximumWidth = interactive ? Math.Min(960, maximumPreviewSize.Width) : maximumPreviewSize.Width;
        var maximumHeight = interactive ? Math.Min(540, maximumPreviewSize.Height) : maximumPreviewSize.Height;
        var scale = Math.Min(1, Math.Min((double)maximumWidth / document.Width, (double)maximumHeight / document.Height));
        var size = new PixelSize(Math.Max(1, (int)Math.Round(document.Width * scale)), Math.Max(1, (int)Math.Round(document.Height * scale)));
        var editingLayerId = editingPose ? selected?.Id : null;
        if (ReferenceEquals(renderedDocument, document) && ReferenceEquals(scheduledDraft, draft) && ReferenceEquals(scheduledMaskDraft, maskDraft) && renderedPosition == position &&
            ReferenceEquals(renderedVideo, video) && renderedSize == size && scheduledEditingPose == editingPose &&
            scheduledEditingLayerId == editingLayerId && scheduledInteractive == interactive)
        {
            return;
        }
        renderedDocument = document;
        scheduledDraft = draft;
        scheduledMaskDraft = maskDraft;
        renderedPosition = position;
        renderedVideo = video;
        renderedSize = size;
        scheduledEditingPose = editingPose;
        scheduledEditingLayerId = editingLayerId;
        scheduledInteractive = interactive;
        previewScheduler ??= new(result => Dispatcher.UIThread.Post(() =>
        {
            if (disposed || result.Request.Sequence != previewSequence || result.Request.SceneRevision != sceneRevision)
            {
                return;
            }
            if (result.Error is { } error)
            {
                if (result.Frame is { } fallback)
                {
                    sceneSurface.Present(fallback);
                }
                ReportRenderingFailure(error);
            }
            else if (result.Frame is { } frame)
            {
                sceneSurface.Present(frame);
                if (renderingFaulted)
                {
                    renderingFaulted = false;
                    RenderingRecovered?.Invoke(this, EventArgs.Empty);
                }
            }
            presentedPreviewSequence = result.Request.Sequence;
            InvalidateVisual();
        }, DispatcherPriority.Render));
        previewScheduler.Submit(new(++previewSequence, sceneRevision, sceneDocument, position, videoTime, video,
            size.Width, size.Height, directory, interactive));
    }

    private bool CanReuseComposite(ProjectDocument sceneDocument)
    {
        if (draft is not null || maskDraft is not null || editingPose ||
            interactivePreview != compositeInteractive || !ReferenceEquals(compositeDocument, sceneDocument))
        {
            return false;
        }

        if (!playbackActive)
        {
            return compositeTime == position;
        }

        return compositePresentationTime is { } start && compositePresentationEnd is { } end
            ? start <= position && position < end
            : compositeTime == position;
    }

    private void ReportRenderingFailure(Exception error)
    {
        renderingFaulted = true;
        if (HasActiveDrag)
        {
            CancelDrag();
        }

        if (reportedFailures.Add((error.GetType(), error.Message)))
        {
            var failedDocument = document;
            var failedPosition = position;
            var failure = new CanvasRenderingFailedEventArgs(document.Id, position, error);
            if (isRendering)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!disposed && ReferenceEquals(document, failedDocument) && position == failedPosition)
                    {
                        RenderingFailed?.Invoke(this, failure);
                    }
                }, DispatcherPriority.Normal);
            }
            else
            {
                RenderingFailed?.Invoke(this, failure);
            }
        }
    }

    private static bool IsRenderingFailure(Exception error) => error is InvalidDataException or IOException or UnauthorizedAccessException or
        InvalidOperationException or NotSupportedException or ArgumentException;

    private static ProjectDocument ReplaceLayer(ProjectDocument source, ProjectLayer replacement)
    {
        return source with { Layers = ReplaceLayers(source.Layers, replacement) };
    }

    private static ImmutableArray<ProjectLayer> ReplaceLayers(ImmutableArray<ProjectLayer> layers, ProjectLayer replacement)
    {
        return layers.Select(layer => layer.Id == replacement.Id ? replacement : layer).ToImmutableArray();
    }

    private static Matrix ToMatrix(SKMatrix value) => new(value.ScaleX, value.SkewY, value.SkewX, value.ScaleY, value.TransX, value.TransY);

    private static Point ToPoint(ScenePoint point)
    {
        return new(point.X, point.Y);
    }

    private static double DistanceSquared(Point first, Point second)
    {
        return Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2);
    }

    private static ScenePoint[] Points(PathGeometry path)
    {
        return new[] { path.Start }
            .Concat(path.Segments.SelectMany(segment => new[] { segment.Control1, segment.Control2, segment.End }))
            .ToArray();
    }

    internal static PathGeometry MoveHandle(PathGeometry path, int index, Vector delta)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (path.Segments.IsDefault || index > (long)path.Segments.Length * 3)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (!double.IsFinite(delta.X) || !double.IsFinite(delta.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }

        static ScenePoint Move(ScenePoint value, Vector offset)
        {
            var moved = new ScenePoint(value.X + offset.X, value.Y + offset.Y);
            if (!double.IsFinite(moved.X) || !double.IsFinite(moved.Y))
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            return moved;
        }

        if (index == 0)
        {
            return path with { Start = Move(path.Start, delta) };
        }

        var segmentIndex = (index - 1) / 3;
        var segment = path.Segments[segmentIndex];
        var changed = ((index - 1) % 3) switch
        {
            0 => segment with { Control1 = Move(segment.Control1, delta) },
            1 => segment with { Control2 = Move(segment.Control2, delta) },
            _ => segment with { End = Move(segment.End, delta) }
        };
        return path with { Segments = path.Segments.SetItem(segmentIndex, changed) };
    }
}
