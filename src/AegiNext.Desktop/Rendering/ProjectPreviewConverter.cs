using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AegiNext.Core.Timing;
using AegiNext.Core.Projects;
using AegiNext.Media.Decoding;
using AegiNext.Media.Preview;
using AegiNext.Rendering.Projects;
using AegiNext.Rendering.Fonts;
using AegiNext.Desktop.Settings;
using Avalonia.OpenGL;
using Avalonia.Platform;

namespace AegiNext.Desktop.Rendering;

internal sealed class ProjectPreviewConverter : IVideoPreviewConverter, ICachedVideoPreviewConverter
{
    private readonly Dictionary<PreviewQuality, IVideoPreviewConverter> converters = [];
    private readonly PreviewBackgroundCache backgroundCache = new();
    private readonly Func<SdrPreviewOptions, IVideoPreviewConverter> createConverter;
    private readonly Func<ProjectPreviewState> getState;
    private readonly Action<Exception?> reportError;
    private readonly PreviewFrameCatalog? previewFrames;
    private readonly Func<SystemFontCatalog?>? fontCatalog;
    private readonly Func<IOpenGlTextureSharingRenderInterfaceContextFeature?>? getGraphics;
    private PreviewGraphicsContext? graphics;
    private bool graphicsDisabled;
    private ProjectSceneRenderer? renderer;
    private string? directory;
    private AegiNext.Core.Projects.ProjectDocument? failedDocument;
    private bool disposed;
    internal bool UsesGpu => graphics is not null;
    internal PreviewBackgroundCacheStatistics BackgroundCacheStatistics => backgroundCache.Statistics;
    internal Action<string, long>? StageMeasured { get; set; }

    internal ProjectPreviewConverter(Func<ProjectPreviewState> getState, Action<Exception?>? reportError = null,
        PreviewFrameCatalog? previewFrames = null, Func<SystemFontCatalog?>? fontCatalog = null,
        Func<IOpenGlTextureSharingRenderInterfaceContextFeature?>? getGraphics = null,
        Func<SdrPreviewOptions, IVideoPreviewConverter>? createConverter = null)
    {
        this.getState = getState;
        this.reportError = reportError ?? (static _ => { });
        this.previewFrames = previewFrames;
        this.fontCatalog = fontCatalog;
        this.getGraphics = getGraphics;
        this.createConverter = createConverter ?? (static options => new SdrVideoConverter(options));
    }

    /// <inheritdoc />
    public SdrVideoFrame Convert(IVideoFrame frame, CancellationToken cancellationToken = default)
    {
        var measure = StageMeasured;
        var started = measure is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            ArgumentNullException.ThrowIfNull(frame);
            var timestamp = frame.Info.DisplayTiming?.Timestamp ?? frame.Info.PresentationTimestamp ?? frame.Info.BestEffortTimestamp
                ?? throw new InvalidDataException("视频帧缺少显示时间。");
            return ConvertFrame(frame, timestamp.ToMediaTime(), null, false, false, CaptureState(), cancellationToken);
        }
        finally
        {
            measure?.Invoke("ConvertAndCompose", Stopwatch.GetTimestamp() - started);
        }
    }

    /// <inheritdoc />
    public SdrVideoFrame Convert(PositionedVideoFrame frame, CancellationToken cancellationToken = default)
    {
        return Convert(frame, CaptureState(), cancellationToken);
    }

    /// <inheritdoc />
    public SdrVideoFrame Convert(PositionedVideoFrame frame, ProjectPreviewState state,
        CancellationToken cancellationToken = default)
    {
        var measure = StageMeasured;
        var started = measure is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentNullException.ThrowIfNull(state);
            return ConvertFrame(frame.Frame, frame.Time, frame.NextFrameTime, frame.ReachedEnd, true, state, cancellationToken);
        }
        finally
        {
            measure?.Invoke("ConvertAndCompose", Stopwatch.GetTimestamp() - started);
        }
    }

    /// <inheritdoc />
    public ProjectPreviewState CaptureState()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return getState();
    }

    /// <inheritdoc />
    public bool HasCachedFrame(MediaTime mediaTarget, MediaTime maximumDistance, ProjectPreviewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return !Volatile.Read(ref disposed) && state.IsInteractive && backgroundCache.Contains(mediaTarget, maximumDistance,
            PreviewQualityOptions.Get(state.Quality, state.IsInteractive));
    }

    /// <inheritdoc />
    public bool TryConvertCached(MediaTime mediaTarget, MediaTime maximumDistance, ProjectPreviewState state,
        CancellationToken cancellationToken, [NotNullWhen(true)] out CachedVideoPreviewFrame? result)
    {
        var measure = StageMeasured;
        var started = measure is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(state);
            cancellationToken.ThrowIfCancellationRequested();
            result = null;
            if (!state.IsInteractive || !backgroundCache.TryFind(mediaTarget, maximumDistance,
                PreviewQualityOptions.Get(state.Quality, state.IsInteractive), out var entry, out var approximate))
            {
                return false;
            }

            var time = state.TargetTime ?? mediaTarget - (state.Document.Media?.MediaOrigin ?? MediaTime.Zero);
            var frame = ComposeBackground(entry.Background, state, time, cancellationToken);
            result = new(frame, entry.Time, entry.NextFrameTime, entry.ReachedEnd, approximate);
            return true;
        }
        finally
        {
            measure?.Invoke("CachedConvertAndCompose", Stopwatch.GetTimestamp() - started);
        }
    }

    private SdrVideoFrame ConvertFrame(IVideoFrame frame, MediaTime sourceTime, MediaTime? nextFrameTime,
        bool reachedEnd, bool hasPosition, ProjectPreviewState state, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var quality = PreviewQualityOptions.GetEffectiveQuality(state.Quality, state.IsInteractive);
        var options = PreviewQualityOptions.Get(quality);
        if (!backgroundCache.TryGet(frame.Info, options, out var background))
        {
            if (!converters.TryGetValue(quality, out var activeConverter))
            {
                activeConverter = createConverter(options);
                converters.Add(quality, activeConverter);
            }
            var measure = StageMeasured;
            var started = measure is null ? 0 : Stopwatch.GetTimestamp();
            try
            {
                background = activeConverter.Convert(frame, cancellationToken);
            }
            finally
            {
                measure?.Invoke("BackgroundConversion", Stopwatch.GetTimestamp() - started);
            }
            cancellationToken.ThrowIfCancellationRequested();
            backgroundCache.Add(frame.Info, options, background, sourceTime, nextFrameTime, reachedEnd);
        }
        else if (hasPosition)
        {
            backgroundCache.Add(frame.Info, options, background, sourceTime, nextFrameTime, reachedEnd);
        }
        var time = (state.IsInteractive || state.EvaluateAtTarget) && state.TargetTime is { } target ? target :
            sourceTime - (state.Document.Media?.MediaOrigin ?? MediaTime.Zero);
        return ComposeBackground(background, state, time, cancellationToken);
    }

    private SdrVideoFrame ComposeBackground(SdrVideoFrame background, ProjectPreviewState state, MediaTime time,
        CancellationToken cancellationToken)
    {
        var measure = StageMeasured;
        var started = measure is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            return ComposeBackgroundCore(background, state, time, cancellationToken);
        }
        finally
        {
            measure?.Invoke("Compose", Stopwatch.GetTimestamp() - started);
        }
    }

    private SdrVideoFrame ComposeBackgroundCore(SdrVideoFrame background, ProjectPreviewState state, MediaTime time,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = state.Document;
        if (ReferenceEquals(document, failedDocument))
        {
            return CompleteFrame(background, background, state, time);
        }

        reportError(null);
        try
        {
            if (graphics is null && !graphicsDisabled && getGraphics?.Invoke() is { } feature)
            {
                DisposeRenderer();
                graphics = PreviewGraphicsContext.TryCreate(feature);
                graphicsDisabled = graphics is null;
            }
            try
            {
                return ComposeFrame(background, state, time, cancellationToken);
            }
            catch (Exception error) when (graphics is not null && error is
                OpenGlException or PlatformGraphicsContextLostException or InvalidOperationException or NotSupportedException)
            {
                System.Diagnostics.Trace.TraceWarning("GPU project preview failed; retrying with CPU: {0}", error.Message);
                DisposeRenderer();
                graphics.Dispose();
                graphics = null;
                graphicsDisabled = true;
                return ComposeFrame(background, state, time, cancellationToken);
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            failedDocument = document;
            reportError(error);
            return CompleteFrame(background, background, state, time);
        }
    }

    private SdrVideoFrame ComposeFrame(SdrVideoFrame background, ProjectPreviewState state, MediaTime time,
        CancellationToken cancellationToken)
    {
        using var current = graphics?.MakeCurrent();
        if (renderer is null || directory != state.Directory)
        {
            renderer?.Dispose();
            directory = state.Directory;
            renderer = new(new DirectoryProjectAssetResolver(directory), fontCatalog?.Invoke(), graphics?.Context);
        }
        var size = GetPreviewSize(state.Document, background.Width, background.Height);
        if (size.Width == background.Width && size.Height == background.Height && !renderer.HasPreviewLayers(state.Document, time))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CompleteFrame(background, background, state, time);
        }
        var pixels = renderer.ComposePreview(state.Document, time, background.Pixels.Span,
            background.Width, background.Height, background.Width * 4, size.Width, size.Height, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CompleteFrame(new(size.Width, size.Height, pixels), background, state, time);
    }

    private void DisposeRenderer()
    {
        using var current = graphics?.MakeCurrentForDisposal();
        renderer?.Dispose();
        renderer = null;
    }

    /// <inheritdoc />
    public long GetRetainedBytes(SdrVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var background = previewFrames?.FindBackground(frame);
        return frame.Pixels.Length + (background is not null && !background.Pixels.Equals(frame.Pixels) ? (long)background.Pixels.Length : 0);
    }

    internal static (int Width, int Height) GetPreviewSize(ProjectDocument document, int width, int height)
    {
        var scale = Math.Min((double)width / document.Width, (double)height / document.Height);
        return (Math.Max(1, (int)Math.Round(document.Width * scale)),
            Math.Max(1, (int)Math.Round(document.Height * scale)));
    }

    private SdrVideoFrame CompleteFrame(SdrVideoFrame presented, SdrVideoFrame background, ProjectPreviewState state, MediaTime time)
    {
        var delivered = presented.CreateView();
        previewFrames?.Register(delivered, ReferenceEquals(presented, background) ? delivered : background,
            ReferenceEquals(state.Document, failedDocument) ? null : state.Document,
            time, state.IsInteractive, state.QualityRevision);
        return delivered;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        backgroundCache.Clear();
        try
        {
            DisposeRenderer();
        }
        finally
        {
            try
            {
                graphics?.Dispose();
            }
            finally
            {
                foreach (var converter in converters.Values)
                {
                    converter.Dispose();
                }
            }
        }
    }
}
