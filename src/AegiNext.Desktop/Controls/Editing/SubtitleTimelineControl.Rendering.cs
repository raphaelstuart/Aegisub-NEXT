using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Editing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AegiNext.Desktop.Controls;

public sealed partial class SubtitleTimelineControl
{
    private const int VIEWPORT_CACHE_IDLE_MILLISECONDS = 100;
    private readonly DispatcherTimer viewportCacheTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(VIEWPORT_CACHE_IDLE_MILLISECONDS)
    };
    private readonly TimelineDrawingCache audioDrawing = new();
    private readonly TimelineDrawingCache clipDrawing = new();
    private readonly TimelineDrawingCache chromeDrawing = new();
    private readonly TimelineDrawingCache markerDrawing = new();
    private readonly TimelineDrawingCache previewDrawing = new();
    private Dictionary<Guid, TimelineVisibleClipIndex> rowClipIndexes = [];
    private Dictionary<Guid, ProjectLayer> subtitleLayersByCue = [];
    private readonly Dictionary<Guid, IReadOnlyList<ProjectLayer>> visibleClipsByRow = [];
    private IReadOnlyList<TimelineRow>? drawingRowsSnapshot;
    private TimelineViewport drawingViewport = new();
    private Guid? drawingPreviewLayerId;
    private Guid[] drawingSelection = [];
    private double drawingOrigin;
    private (TimelineDragMode Mode, Guid Id, MediaTime Start, MediaTime End, MediaTime Key, Guid? Track,
        Guid? Operation, bool OperationStart, bool Valid, bool Stretching) drawingDrag;

    internal long VisibleClipIndexBuildCount { get; private set; }
    internal long StaticDrawingBuildCount { get; private set; }
    internal long TextLayoutBuildCount { get; private set; }
    internal long CurveSampleCount { get; private set; }
    internal int VisibleClipProjectionCount { get; private set; }
    internal long VisibleClipQueryWorkCount { get; private set; }
    internal bool IsViewportDrawingDeferred => viewportCacheTimer.IsEnabled;
    internal long CachedDrawingBytes => audioDrawing.AllocatedBytes + clipDrawing.AllocatedBytes + chromeDrawing.AllocatedBytes +
        markerDrawing.AllocatedBytes + previewDrawing.AllocatedBytes;

    private Guid? PreviewLayerId => timingPreview is { } preview
        ? subtitleLayersByCue.GetValueOrDefault(preview.CueId)?.Id : null;

    private void RenderCachedTimeline(DrawingContext context)
    {
        var host = TopLevel.GetTopLevel(this);
        var origin = host is null ? 0 : this.TranslatePoint(new(0, 0), host)?.X ?? 0;
        if (!drawingOrigin.Equals(origin))
        {
            drawingOrigin = origin;
            audioDrawing.Dispose();
        }
        RefreshDrawingState();
        RefreshClipRangeProjection();
        RefreshVisibleClipRanges();
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var cacheEnabled = !IsViewportDrawingDeferred && TimelineDrawingCache.CanCache(Bounds.Size, scaling, 5);
        var body = BodyRectangle();
        audioDrawing.Draw(context, Bounds.Size, scaling, cacheEnabled, drawing =>
        {
            drawing.DrawRectangle(drawingPalette.Surface, null, new(Bounds.Size));
            using var clip = drawing.PushClip(body);
            if (IsSpectrumVisible)
            {
                DrawSpectrogram(drawing, body, spectrumOverview, spectrumOverviewBitmap);
                DrawSpectrogram(drawing, body, spectrum, spectrumBitmap);
            }
            DrawAnimationRowBackgrounds(drawing, body);
            DrawMediaRangeFill(drawing, body);
            if (IsWaveformVisible)
            {
                DrawWaveform(drawing, body);
            }
        });
        using (context.PushClip(body))
        {
            DrawClipRangeFills(context, body);
        }
        clipDrawing.Draw(context, Bounds.Size, scaling, cacheEnabled, drawing =>
        {
            StaticDrawingBuildCount++;
            DrawTimelineRows(drawing, false);
        });
        if (PreviewLayerId.HasValue)
        {
            previewDrawing.Draw(context, Bounds.Size, scaling, cacheEnabled, drawing => DrawTimelineRows(drawing, true));
        }
        chromeDrawing.Draw(context, Bounds.Size, scaling, cacheEnabled, DrawTimelineChrome);
        DrawClipBoundaries(context);
        DrawSnapIndicator(context);
        markerDrawing.Draw(context, Bounds.Size, scaling, cacheEnabled, drawing => DrawKeyframeMarkers(drawing, false));
        DrawKeyframeMarkers(context, true);
        var playhead = X(Seconds(position));
        if (playhead >= HeaderWidth)
        {
            context.DrawLine(drawingPalette.Playhead, new(playhead, 0), new(playhead, Bounds.Height));
        }
        if (ContentHeight > viewport.Height && viewport.Height > 0)
        {
            var thumbHeight = Math.Max(12, viewport.Height * viewport.Height / ContentHeight);
            var thumbY = RulerHeight + viewport.VerticalOffset / (ContentHeight - viewport.Height) * (viewport.Height - thumbHeight);
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#998B9AB1")), null,
                new(Bounds.Width - 5, thumbY, 4, thumbHeight), 2, 2);
        }
        DrawHoveredLabel(context, drawingPalette.Foreground, ActualThemeVariant == ThemeVariant.Dark);
        DrawTrackInsertion(context);
    }

    private void RefreshDrawingState()
    {
        var drag = (dragMode == TimelineDragMode.SEEK ? TimelineDragMode.NONE : dragMode, dragId, pendingStart,
            pendingEnd, pendingKey, pendingTrackId, dragOperationId, dragOperationStart, validDrop, stretching);
        if (ReferenceEquals(drawingRowsSnapshot, rows) && drawingViewport == viewport && drawingDrag == drag &&
            drawingPreviewLayerId == PreviewLayerId && selectedIds.SetEquals(drawingSelection))
        {
            return;
        }
        InvalidateSceneDrawing();
        drawingRowsSnapshot = rows;
        drawingViewport = viewport;
        drawingDrag = drag;
        drawingPreviewLayerId = PreviewLayerId;
        drawingSelection = selectedIds.ToArray();
    }

    private void DeferViewportDrawingCache()
    {
        viewportCacheTimer.Stop();
        viewportCacheTimer.Start();
    }

    private void OnViewportDrawingSettled(object? sender, EventArgs e)
    {
        viewportCacheTimer.Stop();
        InvalidateVisual();
    }

    private void InvalidateSceneDrawing()
    {
        DisposeDrawingCaches();
        visibleClipsByRow.Clear();
        VisibleClipProjectionCount = 0;
        markersDirty = true;
        previewMarkersSnapshot = null;
    }

    private void DisposeDrawingCaches()
    {
        audioDrawing.Dispose();
        clipDrawing.Dispose();
        chromeDrawing.Dispose();
        markerDrawing.Dispose();
        previewDrawing.Dispose();
    }

    private IReadOnlyList<ProjectLayer> VisibleClipsForRow(TimelineRow row)
    {
        if (visibleClipsByRow.TryGetValue(row.TrackId, out var result))
        {
            return result;
        }
        var padding = 9 / PixelsPerSecond;
        var start = new MediaTime((long)Math.Floor((ViewStart - padding) * 1000000), 1000000);
        var end = new MediaTime((long)Math.Ceiling((ViewStart + VisibleDuration + padding) * 1000000), 1000000);
        if (dragMode is not (TimelineDragMode.NONE or TimelineDragMode.SEEK or TimelineDragMode.TRACK_REORDER))
        {
            result = ClipsForRow(row).Where(clip =>
            {
                VisibleClipQueryWorkCount++;
                var displayed = DisplayedLayer(clip);
                return displayed.Start < end && displayed.End > start;
            }).ToArray();
        }
        else
        {
            if (rowClipIndexes.TryGetValue(row.TrackId, out var index))
            {
                result = index.Query(start, end);
                VisibleClipQueryWorkCount += index.LastVisitedNodeCount;
            }
            else
            {
                result = [];
            }
        }
        VisibleClipProjectionCount += result.Count;
        visibleClipsByRow.Add(row.TrackId, result);
        return result;
    }

    private IReadOnlyList<ProjectLayer> RenderingClipsForRow(TimelineRow row, bool previewOnly) => previewOnly
        ? PreviewLayerId is { } id && rowsByLayer.GetValueOrDefault(id)?.TrackId == row.TrackId ? [layersById[id]] : []
        : VisibleClipsForRow(row);

    private void DrawAnimationRowBackgrounds(DrawingContext context, Rect body)
    {
        foreach (var row in rows)
        {
            if (row.CurveHeight <= 0)
            {
                continue;
            }

            var rectangle = new Rect(body.Left, RowY(row), body.Width, row.CurveHeight).Intersect(body);
            if (rectangle.Width > 0 && rectangle.Height > 0)
            {
                context.DrawRectangle(drawingPalette.AnimationSurface, null, rectangle);
            }
        }
    }

    private void DrawTimelineRows(DrawingContext context, bool previewOnly)
    {
        var grid = drawingPalette.Grid;
        IReadOnlyList<TimelineRow> drawingRows = previewOnly
            ? PreviewLayerId is { } id && rowsByLayer.TryGetValue(id, out var previewRow) ? new[] { previewRow } : []
            : rows;
        using (context.PushClip(BodyRectangle()))
        {
            foreach (var row in drawingRows)
            {
                var y = RowY(row);
                if (y + row.Height < RulerHeight || y > Bounds.Height)
                {
                    continue;
                }

                if (!previewOnly)
                {
                    context.DrawLine(grid, new(HeaderWidth, y + row.Height), new(Bounds.Width, y + row.Height));
                }
                if (row.CurveHeight > 0)
                {
                    foreach (var clip in RenderingClipsForRow(row, previewOnly))
                    {
                        if ((clip.Id == PreviewLayerId) != previewOnly)
                        {
                            continue;
                        }
                        var layer = DisplayedLayer(clip);
                        var tracks = TracksFor(layer);
                        foreach (var animation in row.Animations)
                        {
                            foreach (var target in animation.TargetsFor(clip.Id).OrderBy(target => IsSelectedMaskTarget(clip.Id, target)))
                            {
                                if (CurveRectangle(clip.Id, target) is { } curve &&
                                    curve.Bottom >= RulerHeight && curve.Top <= Bounds.Height)
                                {
                                    DrawEffects(context, clip, layer, DisplayedTrack(layer, target, tracks), curve, animation.IsCollapsed);
                                }
                            }
                        }
                    }
                }

                foreach (var clip in RenderingClipsForRow(row, previewOnly))
                {
                    if ((clip.Id == PreviewLayerId) != previewOnly)
                    {
                        continue;
                    }
                    var rectangle = ClipRectangle(clip, row);
                    if (rectangle.Right < HeaderWidth || rectangle.Left > Bounds.Width)
                    {
                        continue;
                    }

                    var active = selectedIds.Contains(clip.Id);
                    var invalid = IsInvalidClipDrag(clip.Id);
                    var appearance = (Inactive: inactiveClipBrush, Selected: selectedClipBrush, Border: inactiveClipBorder);
                    var hasColorTag = clip.SubtitleId is { } subtitleId && cuesById[subtitleId].ColorTagId is { } tagId &&
                        colorTagAppearances.TryGetValue(tagId, out appearance);
                    var background = invalid ? drawingPalette.InvalidClip
                        : hasColorTag ? active ? appearance.Selected : appearance.Inactive
                        : active ? selectedClipBrush : inactiveClipBrush;
                    var border = hasColorTag ? active ? selectedColorTagBorder : appearance.Border
                        : active ? selectedClipBorder : inactiveClipBorder;
                    context.DrawRectangle(background, border, rectangle, 3, 3);
                    using var clipOpacity = context.PushOpacity(active || invalid ? 1 : 0.6);
                    var maskBadge = ClipMaskBadgeRectangle(clip, rectangle);
                    if (maskBadge is { } badge)
                    {
                        if (badge.Width < badge.Height)
                        {
                            context.DrawRectangle(drawingPalette.ClipForeground, null, badge, 1, 1);
                        }
                        else
                        {
                            using (context.PushTransform(Matrix.CreateScale(badge.Width / 24, badge.Height / 24) *
                                Matrix.CreateTranslation(badge.Left, badge.Top)))
                            {
                                context.DrawGeometry(drawingPalette.ClipForeground, null, clipMaskBadgeIcon);
                            }
                        }
                    }
                    var textLeft = maskBadge?.Right + 4 ?? rectangle.Left + 6;
                    if (rectangle.Right - 3 > textLeft && rectangle.Height >= 10)
                    {
                        var text = clip.SubtitleId is { } cueId ? cuesById[cueId].Text.Replace('\n', ' ') : clip.Name;
                        using (context.PushClip(new Rect(textLeft, rectangle.Top + 3, rectangle.Right - 3 - textLeft, rectangle.Height - 6)))
                        {
                            DrawText(context, text, new(textLeft, rectangle.Y + 3), drawingPalette.ClipForeground, 11);
                        }
                    }
                }
            }
        }
    }

    private void DrawTimelineChrome(DrawingContext context)
    {
        var foreground = drawingPalette.Foreground;
        var grid = drawingPalette.Grid;
        foreach (var row in rows)
        {
            var y = RowY(row);
            if (y + row.Height < RulerHeight || y > Bounds.Height)
            {
                continue;
            }

            using (context.PushClip(new Rect(0, RulerHeight, HeaderWidth, Math.Max(0, Bounds.Height - RulerHeight))))
            {
                context.DrawRectangle(row.TrackId == selectedTrack && selectedTrack.HasValue
                        ? drawingPalette.SelectedTrack : drawingPalette.Track, null,
                    new(0, y, HeaderWidth, row.Height));
                DrawExpander(context, TimelineRow.ExpanderRectangle(y), row.IsCollapsed);

                var nameRectangle = GetTrackHeaderNameRectangle(row, y);
                using (context.PushClip(nameRectangle))
                {
                    DrawCenteredText(context, row.Name, nameRectangle, foreground, 11);
                }
                DrawTrackSoloToggle(context, row, y);
                if (row.StyleBadgeRectangle(y, HeaderWidth) is { } badge)
                {
                    using (context.PushOpacity(row.AutoApplyStyle ? 1 : 0.5))
                    {
                        context.DrawRectangle(drawingPalette.StyleBadge, null, badge, 3, 3);
                        using (context.PushClip(badge.Deflate(new Thickness(4, 0))))
                        {
                            DrawCenteredText(context, row.StylePresetName!, badge.Deflate(new Thickness(4, 0)), foreground, 10);
                        }
                    }
                }
            }
        }

        context.DrawRectangle(drawingPalette.Surface, null,
            new(0, 0, Bounds.Width, RulerHeight));
        var step = TimelineTimeScale.MajorStep(PixelsPerSecond);
        var minor = Seconds(TimelineTimeScale.MinorStep(PixelsPerSecond));
        for (var index = Math.Ceiling(ViewStart / minor); index * minor <= ViewStart + VisibleDuration; index++)
        {
            var tick = index * minor;
            var x = X(tick);
            if (x >= HeaderWidth)
            {
                context.DrawLine(grid, new(x, RulerHeight - 3), new(x, RulerHeight));
            }
        }
        for (var index = Math.Ceiling(ViewStart / step); index * step <= ViewStart + VisibleDuration; index++)
        {
            var tick = index * step;
            var x = X(tick);
            if (x >= HeaderWidth)
            {
                context.DrawLine(grid, new(x, RulerHeight - 6), new(x, Bounds.Height));
                DrawText(context, TimelineTimeScale.Label(tick, step), new(x + 3, 2), foreground, 10);
            }
        }

        DrawPropertyTitles(context, foreground);
    }
}
