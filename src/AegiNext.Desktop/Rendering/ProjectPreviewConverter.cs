using AegiNext.Core.Timing;
using AegiNext.Core.Projects;
using AegiNext.Media.Decoding;
using AegiNext.Media.Preview;
using AegiNext.Rendering.Projects;
using AegiNext.Rendering.Fonts;
using AegiNext.Desktop.Settings;
using Avalonia.OpenGL;
using Avalonia.Platform;
using System.Diagnostics;

namespace AegiNext.Desktop.Rendering;

internal sealed class ProjectPreviewConverter : IVideoPreviewConverter
{
    private readonly Dictionary<PreviewQuality, SdrVideoConverter> converters = [];
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
    internal Action<string, long>? StageMeasured { get; set; }

    internal ProjectPreviewConverter(Func<ProjectPreviewState> getState, Action<Exception?>? reportError = null,
        PreviewFrameCatalog? previewFrames = null, Func<SystemFontCatalog?>? fontCatalog = null,
        Func<IOpenGlTextureSharingRenderInterfaceContextFeature?>? getGraphics = null)
    {
        this.getState = getState;
        this.reportError = reportError ?? (static _ => { });
        this.previewFrames = previewFrames;
        this.fontCatalog = fontCatalog;
        this.getGraphics = getGraphics;
    }

    public SdrVideoFrame Convert(IVideoFrame frame, CancellationToken cancellationToken = default)
    {
        var measured = StageMeasured;
        var started = measured is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            return ConvertCore(frame, cancellationToken);
        }
        finally
        {
            measured?.Invoke("ConvertAndCompose", Stopwatch.GetTimestamp() - started);
        }
    }

    private SdrVideoFrame ConvertCore(IVideoFrame frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var state = getState();
        var quality = PreviewQualityOptions.GetEffectiveQuality(state.Quality, state.IsInteractive);
        if (!converters.TryGetValue(quality, out var activeConverter))
        {
            activeConverter = new(PreviewQualityOptions.Get(quality));
            converters.Add(quality, activeConverter);
        }
        SdrVideoFrame background;
        var measured = StageMeasured;
        var started = measured is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            background = activeConverter.Convert(frame, cancellationToken);
        }
        finally
        {
            measured?.Invoke("BackgroundConversion", Stopwatch.GetTimestamp() - started);
        }
        var document = state.Document;
        var timestamp = frame.Info.PresentationTimestamp ?? frame.Info.BestEffortTimestamp
            ?? throw new InvalidDataException("视频帧缺少显示时间。");
        var time = state.IsInteractive && state.TargetTime is { } target ? target : timestamp.ToMediaTime() - (document.Media?.MediaOrigin ?? MediaTime.Zero);
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
        var measured = StageMeasured;
        var started = measured is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            return ComposeFrameCore(background, state, time, cancellationToken);
        }
        finally
        {
            measured?.Invoke("Compose", Stopwatch.GetTimestamp() - started);
        }
    }

    private SdrVideoFrame ComposeFrameCore(SdrVideoFrame background, ProjectPreviewState state, MediaTime time,
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
        return frame.Pixels.Length + (background is not null && !ReferenceEquals(background, frame) ? (long)background.Pixels.Length : 0);
    }

    internal static (int Width, int Height) GetPreviewSize(ProjectDocument document, int width, int height)
    {
        var scale = Math.Min((double)width / document.Width, (double)height / document.Height);
        return (Math.Max(1, (int)Math.Round(document.Width * scale)),
            Math.Max(1, (int)Math.Round(document.Height * scale)));
    }

    private SdrVideoFrame CompleteFrame(SdrVideoFrame presented, SdrVideoFrame background, ProjectPreviewState state, MediaTime time)
    {
        previewFrames?.Register(presented, background, ReferenceEquals(state.Document, failedDocument) ? null : state.Document,
            time, state.IsInteractive, state.QualityRevision);
        return presented;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
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
