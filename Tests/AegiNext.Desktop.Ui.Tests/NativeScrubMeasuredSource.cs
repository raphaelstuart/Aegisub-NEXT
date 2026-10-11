using System.Diagnostics;
using AegiNext.Core.Timing;
using AegiNext.Media.Decoding;

namespace AegiNext.Desktop.Ui.Tests;

internal sealed class NativeScrubMeasuredSource(VideoFrameNavigator navigator, NativeScrubMetrics metrics) : IVideoFrameSource
{
    private readonly Action? supersede = typeof(VideoFrameNavigator).GetMethod("SupersedeSeek", Type.EmptyTypes)?.CreateDelegate<Action>(navigator);

    internal VideoDecodeSessionInfo? LastSessionInfo { get; private set; }

    /// <inheritdoc />
    public PositionedVideoFrame? ReadFrame(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var before = navigator.SessionInfo;
        try
        {
            return navigator.ReadFrame(cancellationToken);
        }
        finally
        {
            Record("Read", started, before);
        }
    }

    /// <inheritdoc />
    public PositionedVideoFrame? SeekFrame(MediaTime target, CancellationToken cancellationToken = default) =>
        SeekFrame(target, static () => false, cancellationToken);

    /// <inheritdoc />
    public PositionedVideoFrame? SeekFrame(MediaTime target, Func<bool> isSuperseded, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var before = navigator.SessionInfo;
        try
        {
            return navigator.SeekFrame(target, isSuperseded, cancellationToken);
        }
        finally
        {
            Record("Seek", started, before, target);
        }
    }

    /// <inheritdoc />
    public void Cancel() => navigator.Cancel();

    /// <summary>向支持请求替代的导航器透明转发；旧版本基准没有该能力。</summary>
    public void SupersedeSeek() => supersede?.Invoke();

    /// <inheritdoc />
    public void Dispose()
    {
        LastSessionInfo = navigator.SessionInfo ?? LastSessionInfo;
        navigator.Dispose();
    }

    private void Record(string stage, long started, VideoDecodeSessionInfo? before, MediaTime? target = null)
    {
        var after = navigator.SessionInfo;
        LastSessionInfo = after ?? LastSessionInfo;
        metrics.RecordStage(stage, started, target);
        if (before is not null && after is not null)
        {
            metrics.RecordNativeDelta(started, before, after);
        }
    }
}
