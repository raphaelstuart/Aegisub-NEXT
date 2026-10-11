using System.Diagnostics.CodeAnalysis;
using AegiNext.Core.Timing;

namespace AegiNext.Media.Decoding;

/// <summary>
/// 根据独立显示时间选择帧；原始 PTS、best-effort 与明确推导依据保持可追溯。
/// 重复时间取最后一帧，无有效显示时间或倒退时间明确报错。
/// </summary>
public sealed class VideoFrameNavigator : IVideoFrameSource
{
    private const int DEFAULT_CACHED_FRAMES = 120;
    private const long DEFAULT_CACHED_BYTES = 256L * 1024 * 1024;
    private static readonly MediaTime maximumForwardScan = new(1, 4);
    private readonly Func<CancellationToken, IVideoDecoder> decoderFactory;
    private readonly VideoFrameCache cache;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock gate = new();
    private readonly Lock cancellationGate = new();
    private IVideoDecoder? decoder;
    private IVideoFrame? lookahead;
    private MediaTime? lastRawTime;
    private MediaTime? firstFrameTime;
    private bool atFileStart = true;
    private bool reachedEnd;
    private bool faulted;
    private bool disposed;
    private bool readingCachedSequence;
    private MediaTime? cachedNextTime;
    private long seekEpoch;

    /// <summary>
    /// 使用可重建的解码器工厂打开文件起点；每次工厂调用必须返回全新的解码器。
    /// 默认最多缓存 120 帧和 256 MiB 原始平面。
    /// </summary>
    public VideoFrameNavigator(Func<CancellationToken, IVideoDecoder> decoderFactory, CancellationToken cancellationToken = default)
        : this(decoderFactory, DEFAULT_CACHED_FRAMES, DEFAULT_CACHED_BYTES, cancellationToken)
    {
    }

    /// <summary>
    /// 缓存同时受帧数与原始平面 stride 字节预算约束；任一预算为零关闭缓存。
    /// 预算不包括解码器内部缓冲和已交付但尚未释放的帧租约。
    /// </summary>
    public VideoFrameNavigator(Func<CancellationToken, IVideoDecoder> decoderFactory, int maximumCachedFrames,
        long maximumCachedBytes = DEFAULT_CACHED_BYTES, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoderFactory);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCachedFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCachedBytes);
        this.decoderFactory = decoderFactory;
        cache = new(maximumCachedFrames, maximumCachedBytes);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            decoder = CreateDecoder(linked.Token);
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 打开本地文件的指定视频流，使用相同文件与流重建必要的起点扫描。
    /// </summary>
    public static VideoFrameNavigator Open(string filePath, int videoStreamIndex, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        var path = Path.GetFullPath(filePath);
        return new(token => FfmpegVideoDecoder.Open(path, videoStreamIndex, token), cancellationToken);
    }

    /// <summary>实际解码会话快照；读取不等待正在进行的帧读取。</summary>
    public VideoDecodeSessionInfo? SessionInfo => (Volatile.Read(ref decoder) as FfmpegVideoDecoder)?.SessionInfo;

    /// <summary>使用相同不可变策略重新创建文件解码器。</summary>
    public static VideoFrameNavigator Open(string filePath, int videoStreamIndex, VideoDecoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        ArgumentNullException.ThrowIfNull(options);
        var path = Path.GetFullPath(filePath);
        return new(token => FfmpegVideoDecoder.Open(path, videoStreamIndex, options, token), cancellationToken);
    }

    /// <inheritdoc />
    public PositionedVideoFrame? ReadFrame(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (faulted)
            {
                throw new InvalidOperationException("帧源发生错误，需重新定位后才能继续读取。");
            }

            try
            {
                if (readingCachedSequence)
                {
                    if (cachedNextTime is not { } nextTime)
                    {
                        return null;
                    }

                    if (cache.FindExact(nextTime) is { } cached)
                    {
                        cachedNextTime = cached.NextFrameTime;
                        return cached;
                    }

                    readingCachedSequence = false;
                    return SeekCore(nextTime, null, linked.Token);
                }

                return ReadGroup(linked.Token);
            }
            catch
            {
                faulted = true;
                ClearLookahead();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public PositionedVideoFrame? SeekFrame(MediaTime target, CancellationToken cancellationToken = default)
    {
        return SeekCore(target, null, cancellationToken);
    }

    /// <inheritdoc />
    public PositionedVideoFrame? SeekFrame(MediaTime target, Func<bool> isSuperseded, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isSuperseded);
        return SeekCore(target, isSuperseded, cancellationToken);
    }

    private PositionedVideoFrame? SeekCore(MediaTime target, Func<bool>? isSuperseded, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            var requestEpoch = unchecked((ulong)Volatile.Read(ref seekEpoch));
            ThrowIfSuperseded(isSuperseded);
            PositionedVideoFrame? candidate = null;
            try
            {
                if (faulted)
                {
                    Restart(linked.Token);
                }

                readingCachedSequence = false;
                var cached = cache.FindContaining(target);
                if (cached is null && firstFrameTime is { } firstTime && target < firstTime)
                {
                    var firstCached = cache.FindExact(firstTime);
                    if (firstCached is not null)
                    {
                        cached = MarkBeforeFirst(firstCached);
                    }
                }

                if (cached is not null)
                {
                    readingCachedSequence = true;
                    cachedNextTime = cached.NextFrameTime;
                    return cached;
                }

                var knownBeginning = firstFrameTime.HasValue;
                if (!knownBeginning)
                {
                    using var first = ReadGroup(linked.Token, isSuperseded);
                    if (first is null)
                    {
                        return null;
                    }
                }

                if (target < firstFrameTime!.Value)
                {
                    Restart(linked.Token);
                    candidate = ReadGroup(linked.Token, isSuperseded);
                    if (candidate is null)
                    {
                        return null;
                    }

                    return MarkBeforeFirst(candidate);
                }

                ThrowIfSuperseded(isSuperseded);
                if (knownBeginning && CanScanForward(target))
                {
                    candidate = ReadGroup(linked.Token, isSuperseded);
                }
                else
                {
                    ClearLookahead();
                    decoder!.SeekToKeyFrame(target, linked.Token);
                    atFileStart = false;
                    reachedEnd = false;
                    lastRawTime = null;
                    try
                    {
                        candidate = ReadGroup(linked.Token, isSuperseded, target, requestEpoch);
                    }
                    catch (VideoDisplayTimingUnavailableException)
                    {
                        faulted = true;
                        linked.Token.ThrowIfCancellationRequested();
                        ThrowIfSuperseded(isSuperseded);
                        Restart(linked.Token);
                        candidate = ReadGroup(linked.Token, isSuperseded);
                    }
                }

                ThrowIfSuperseded(isSuperseded);
                if (candidate is null || candidate.Time > target)
                {
                    candidate?.Dispose();
                    candidate = null;
                    Restart(linked.Token);
                    candidate = ReadGroup(linked.Token, isSuperseded);
                    if (candidate is not null && candidate.Time > target)
                    {
                        return MarkBeforeFirst(candidate);
                    }
                }

                while (candidate?.NextFrameTime is { } next && next <= target)
                {
                    ThrowIfSuperseded(isSuperseded);
                    candidate.Dispose();
                    candidate = null;
                    candidate = ReadGroup(linked.Token, isSuperseded);
                }

                ThrowIfSuperseded(isSuperseded);
                return candidate;
            }
            catch (VideoSeekSupersededException error)
            {
                candidate?.Dispose();
                if (error.RequiresSeek)
                {
                    ClearLookahead();
                    lastRawTime = null;
                }
                throw;
            }
            catch
            {
                candidate?.Dispose();
                faulted = true;
                ClearLookahead();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void SupersedeSeek()
    {
        var epoch = unchecked((ulong)Interlocked.Increment(ref seekEpoch));
        try
        {
            if (Volatile.Read(ref decoder) is FfmpegVideoDecoder native)
            {
                native.SetSeekEpoch(epoch);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <inheritdoc />
    [SuppressMessage("ReSharper", "InconsistentlySynchronizedField", Justification = "Cancellation must bypass the decoding lock; a separate lock serializes CancellationTokenSource.Cancel and Dispose.")]
    public void Cancel()
    {
        lock (cancellationGate)
        {
            try
            {
                lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Cancel();
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ClearLookahead();
            cache.Dispose();
            decoder?.Dispose();
            decoder = null;
            lock (cancellationGate)
            {
                lifetime.Dispose();
            }
        }
    }

    private PositionedVideoFrame? ReadGroup(CancellationToken cancellationToken, Func<bool>? isSuperseded = null,
        MediaTime? seekTarget = null, ulong? requestEpoch = null)
    {
        if (reachedEnd)
        {
            return null;
        }

        var current = lookahead;
        lookahead = null;
        IVideoFrame? nextFrame = null;
        try
        {
            ThrowIfSuperseded(isSuperseded);
            if (current is null)
            {
                if (seekTarget is { } target && requestEpoch is { } epoch && decoder is FfmpegVideoDecoder native)
                {
                    native.SetSeekEpoch(unchecked((ulong)Volatile.Read(ref seekEpoch)));
                    ThrowIfSuperseded(isSuperseded);
                    current = native.ReadFrameForSeek(target, epoch, cancellationToken);
                }
                else
                {
                    current = decoder!.ReadFrame(cancellationToken);
                }
            }
            if (current is null)
            {
                reachedEnd = true;
                return null;
            }

            var time = ReadTime(current);
            if (atFileStart)
            {
                firstFrameTime = time;
                atFileStart = false;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfSuperseded(isSuperseded);
                nextFrame = decoder!.ReadFrame(cancellationToken);
                if (nextFrame is null)
                {
                    reachedEnd = true;
                    var frame = new PositionedVideoFrame(current, time, null, reachedEnd: true);
                    current = null;
                    return cache.Store(frame);
                }

                var nextTime = ReadTime(nextFrame);
                if (nextTime == time)
                {
                    current.Dispose();
                    current = nextFrame;
                    nextFrame = null;
                    continue;
                }

                lookahead = nextFrame;
                nextFrame = null;
                var selected = new PositionedVideoFrame(current, time, nextTime);
                current = null;
                return cache.Store(selected);
            }
        }
        catch (VideoSeekSupersededException)
        {
            nextFrame?.Dispose();
            lookahead = current;
            throw;
        }
        catch
        {
            nextFrame?.Dispose();
            current?.Dispose();
            throw;
        }
    }

    private bool CanScanForward(MediaTime target)
    {
        if (lookahead?.Info.DisplayTiming?.Timestamp is not { } timestamp)
        {
            return false;
        }

        var next = timestamp.ToMediaTime();
        if (target < next)
        {
            return false;
        }

        try
        {
            return target - next <= maximumForwardScan;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void ThrowIfSuperseded(Func<bool>? isSuperseded)
    {
        if (isSuperseded?.Invoke() == true)
        {
            throw new VideoSeekSupersededException();
        }
    }

    private MediaTime ReadTime(IVideoFrame frame)
    {
        var timestamp = frame.Info.DisplayTiming?.Timestamp ?? throw new InvalidDataException("时间线不可用：视频帧缺少有效时间戳及显示时间推导依据。");
        var time = timestamp.ToMediaTime();
        if (lastRawTime is { } previous && time < previous)
        {
            throw new InvalidDataException("时间线不可用：解码显示顺序中的显示时间倒退。");
        }

        lastRawTime = time;
        return time;
    }

    private void Restart(CancellationToken cancellationToken)
    {
        ClearLookahead();
        cache.Dispose();
        readingCachedSequence = false;
        cachedNextTime = null;
        decoder?.Dispose();
        decoder = null;
        decoder = CreateDecoder(cancellationToken);
        reachedEnd = false;
        lastRawTime = null;
        faulted = false;
        atFileStart = true;
    }

    private IVideoDecoder CreateDecoder(CancellationToken cancellationToken)
    {
        return decoderFactory(cancellationToken) ?? throw new InvalidOperationException("解码器工厂返回了空实例。");
    }

    private void ClearLookahead()
    {
        lookahead?.Dispose();
        lookahead = null;
    }

    private static PositionedVideoFrame MarkBeforeFirst(PositionedVideoFrame candidate)
    {
        var first = new PositionedVideoFrame(candidate.DetachFrame(), candidate.Time,
            candidate.NextFrameTime, isBeforeFirst: true, reachedEnd: candidate.ReachedEnd);
        candidate.Dispose();
        return first;
    }
}
