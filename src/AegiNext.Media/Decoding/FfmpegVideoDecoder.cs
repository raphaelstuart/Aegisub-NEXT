using System.Diagnostics.CodeAnalysis;
using AegiNext.Core.Timing;

namespace AegiNext.Media.Decoding;

/// <summary>
/// 本地文件的顺序视频解码器；读取在调用线程执行，取消为协作式且会终止已开始的会话。
/// </summary>
public sealed class FfmpegVideoDecoder : IVideoDecoder
{
    private readonly VideoDecoderHandle handle;
    private readonly Lock gate = new();
    private VideoDecodeSessionInfo sessionInfo = new(VideoDecodeMode.Auto, VideoDecoderBackend.Software, false, "", 0, 0);

    private FfmpegVideoDecoder(VideoDecoderHandle handle, MediaTimeBase streamTimeBase)
    {
        this.handle = handle;
        StreamTimeBase = streamTimeBase;
        RefreshSessionInfo();
    }

    public MediaTimeBase StreamTimeBase { get; }

    /// <summary>最新已完成原生操作的会话快照；读取不等待解码。</summary>
    public VideoDecodeSessionInfo SessionInfo => Volatile.Read(ref sessionInfo);

    /// <summary>
    /// 核验 C ABI 与编译／运行 FFmpeg 版本，返回后端身份。
    /// </summary>
    public static unsafe DecoderBackendInfo GetBackendInfo()
    {
        if (NativeDecodeMethods.AbiVersion() != NativeDecodeMethods.ABI_VERSION)
        {
            throw new InvalidOperationException("原生视频解码 ABI 版本不匹配。");
        }

        RequireCore();
        var info = new NativeDecodeBackendInfo { structSize = (uint)sizeof(NativeDecodeBackendInfo), abiVersion = NativeDecodeMethods.ABI_VERSION };
        Span<byte> error = stackalloc byte[NativeDecodeMethods.ERROR_CAPACITY];
        error.Clear();
        fixed (byte* errorPointer = error)
        {
            NativeDecodeError.ThrowIfFailed(NativeDecodeMethods.GetBackendInfo(ref info, errorPointer, (uint)error.Length), error);
        }

        if (info.compileAvformat != info.runtimeAvformat || info.compileAvcodec != info.runtimeAvcodec || info.compileAvutil != info.runtimeAvutil)
        {
            throw new InvalidOperationException("原生解码库编译与运行版本不一致。");
        }

        return new(NativeDecodeError.ReadText(new(info.releaseVersion, 32)), ReadVersion(info.runtimeAvformat), ReadVersion(info.runtimeAvcodec), ReadVersion(info.runtimeAvutil));
    }

    /// <summary>
    /// 打开本地文件的指定绝对视频流索引；打开失败或取消时释放全部已分配资源。
    /// </summary>
    public static FfmpegVideoDecoder Open(string filePath, int videoStreamIndex, CancellationToken cancellationToken = default)
    {
        return Open(filePath, videoStreamIndex, new(), cancellationToken);
    }

    /// <summary>按不可变后端策略打开本地视频，首帧前自动模式可回退。</summary>
    public static unsafe FfmpegVideoDecoder Open(string filePath, int videoStreamIndex, VideoDecoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.Workload))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegative(videoStreamIndex);
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(filePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("视频文件不存在。", path);
        }

        _ = GetBackendInfo();
        RequireSeekFeature();
        Span<byte> error = stackalloc byte[NativeDecodeMethods.ERROR_CAPACITY];
        error.Clear();
        fixed (byte* errorPointer = error)
        {
            var nativeOptions = new NativeDecoderOptions
            {
                structSize = (uint)sizeof(NativeDecoderOptions), abiVersion = NativeDecodeMethods.ABI_VERSION,
                mode = (uint)options.Mode, workload = (uint)options.Workload
            };
            var code = NativeDecodeMethods.CreateWithOptions(in nativeOptions, out var pointer, errorPointer, (uint)error.Length);
            var handle = new VideoDecoderHandle(pointer);
            try
            {
                NativeDecodeError.ThrowIfFailed(code, error, cancellationToken);
                if (handle.IsInvalid)
                {
                    throw new InvalidDataException("原生解码器没有返回有效句柄。");
                }

                using var registration = cancellationToken.UnsafeRegister(static state => NativeDecodeMethods.Cancel((VideoDecoderHandle)state!), handle);
                code = NativeDecodeMethods.Open(handle, path, videoStreamIndex, errorPointer, (uint)error.Length);
                cancellationToken.ThrowIfCancellationRequested();
                NativeDecodeError.ThrowIfFailed(code, error, cancellationToken);
                NativeDecodeError.ThrowIfFailed(NativeDecodeMethods.GetTimeBase(handle, out var timeBase, errorPointer, (uint)error.Length), error, cancellationToken);
                return new(handle, new(timeBase.numerator, timeBase.denominator));
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }

    IVideoFrame? IVideoDecoder.ReadFrame(CancellationToken cancellationToken)
    {
        return ReadFrame(cancellationToken);
    }

    /// <summary>
    /// 向前定位来源时间对应的关键帧并清空解码缓存；目标按流刻度向下取整，不自动归零。
    /// </summary>
    public unsafe void SeekToKeyFrame(MediaTime target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = target.ToTimestamp(StreamTimeBase, MediaTimeRounding.FLOOR).Value;
        if (timestamp == long.MinValue)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "定位时间不能使用未定义时间戳哨兵。");
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            cancellationToken.ThrowIfCancellationRequested();
            using var registration = cancellationToken.UnsafeRegister(static state => NativeDecodeMethods.Cancel((VideoDecoderHandle)state!), handle);
            Span<byte> error = stackalloc byte[NativeDecodeMethods.ERROR_CAPACITY];
            error.Clear();
            fixed (byte* errorPointer = error)
            {
                var code = NativeDecodeMethods.Seek(handle, timestamp, errorPointer, (uint)error.Length);
                cancellationToken.ThrowIfCancellationRequested();
                NativeDecodeError.ThrowIfFailed(code, error, cancellationToken);
                RefreshSessionInfo();
            }
        }
    }

    /// <summary>
    /// 顺序取得独立帧，正常 EOF 返回 null；估算时间戳不会替代原始 PTS。
    /// </summary>
    public DecodedVideoFrame? ReadFrame(CancellationToken cancellationToken = default)
    {
        return ReadFrameCore(null, cancellationToken);
    }

    internal DecodedVideoFrame? ReadFrameForSeek(MediaTime target, CancellationToken cancellationToken)
    {
        return ReadFrameCore(target, cancellationToken);
    }

    internal DecodedVideoFrame? ReadFrameForSeek(MediaTime target, ulong epoch, CancellationToken cancellationToken)
    {
        return ReadFrameCore(target, cancellationToken, epoch);
    }

    [SuppressMessage("ReSharper", "InconsistentlySynchronizedField", Justification = "Seek supersession bypasses the read lock; SafeHandle marshalling pins lifetime and the native method only updates an atomic epoch.")]
    internal void SetSeekEpoch(ulong epoch)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        NativeDecodeMethods.SetSeekEpoch(handle, epoch);
    }

    private unsafe DecodedVideoFrame? ReadFrameCore(MediaTime? target, CancellationToken cancellationToken, ulong? seekEpoch = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            cancellationToken.ThrowIfCancellationRequested();
            using var registration = cancellationToken.UnsafeRegister(static state => NativeDecodeMethods.Cancel((VideoDecoderHandle)state!), handle);
            Span<byte> error = stackalloc byte[NativeDecodeMethods.ERROR_CAPACITY];
            error.Clear();
            fixed (byte* errorPointer = error)
            {
                var code = target is { } selectedTime && seekEpoch is { } epoch
                    ? NativeDecodeMethods.ReadForSeekEpoch(handle, selectedTime.ToTimestamp(StreamTimeBase, MediaTimeRounding.FLOOR).Value,
                        epoch, out var pointer, errorPointer, (uint)error.Length)
                    : target is { } time && (NativeDecodeMethods.Features() & NativeDecodeMethods.SEEK_SELECTION_FEATURE) != 0
                    ? NativeDecodeMethods.ReadForSeek(handle, time.ToTimestamp(StreamTimeBase, MediaTimeRounding.FLOOR).Value,
                        out pointer, errorPointer, (uint)error.Length)
                    : NativeDecodeMethods.ReadNext(handle, out pointer, errorPointer, (uint)error.Length);
                var frameHandle = new DecodedFrameHandle(pointer);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RefreshSessionInfo();
                    if (code == NativeDecodeMethods.EOF)
                    {
                        if (!frameHandle.IsInvalid)
                        {
                            throw new InvalidDataException("原生解码器在 EOF 返回了帧句柄。");
                        }

                        frameHandle.Dispose();
                        return null;
                    }

                    NativeDecodeError.ThrowIfFailed(code, error, cancellationToken);
                    if (frameHandle.IsInvalid)
                    {
                        throw new InvalidDataException("原生解码器返回了空帧。");
                    }

                    return new(frameHandle);
                }
                catch
                {
                    frameHandle.Dispose();
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// 从任意线程请求终止会话；该请求不会释放正在使用的句柄，且不能撤销。
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentlySynchronizedField", Justification = "Cancellation deliberately bypasses the read lock; SafeHandle marshalling pins lifetime and native cancellation only sets an atomic flag.")]
    public void Cancel()
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        NativeDecodeMethods.Cancel(handle);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (!handle.IsClosed)
            {
                NativeDecodeMethods.Cancel(handle);
            }
        }
        catch (ObjectDisposedException)
        {
        }

        lock (gate)
        {
            handle.Dispose();
        }
    }

    /// <summary>
    /// 查询存活原生解码器数量，用于生命周期验证。
    /// </summary>
    public static uint GetLiveDecoderCount()
    {
        return NativeDecodeMethods.LiveDecoders();
    }

    /// <summary>
    /// 查询存活独立原生帧数量，用于生命周期验证。
    /// </summary>
    public static uint GetLiveFrameCount()
    {
        return NativeDecodeMethods.LiveFrames();
    }

    internal static void RequireCore()
    {
        try
        {
            if ((NativeDecodeMethods.Features() & NativeDecodeMethods.CORE_FEATURE) == 0 ||
                NativeDecodeMethods.CoreVersion() != NativeDecodeMethods.CORE_VERSION ||
                (NativeDecodeMethods.CoreCapabilities() & NativeDecodeMethods.CORE_CAPABILITIES) != NativeDecodeMethods.CORE_CAPABILITIES)
            {
                throw new NotSupportedException("原生解码库缺少当前共享媒体核心，请重新构建 Decoder。");
            }
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new NotSupportedException("原生解码库缺少共享媒体核心版本查询，请重新构建 Decoder。", exception);
        }
    }

    private unsafe void RefreshSessionInfo()
    {
        var info = new NativeDecoderSessionInfo { structSize = (uint)sizeof(NativeDecoderSessionInfo), abiVersion = NativeDecodeMethods.ABI_VERSION };
        Span<byte> error = stackalloc byte[NativeDecodeMethods.ERROR_CAPACITY];
        error.Clear();
        fixed (byte* errorPointer = error)
        {
            NativeDecodeError.ThrowIfFailed(NativeDecodeMethods.GetSessionInfo(handle, ref info, errorPointer, (uint)error.Length), error);
        }
        var value = new VideoDecodeSessionInfo((VideoDecodeMode)info.requestedMode, (VideoDecoderBackend)info.activeBackend,
            info.hardwareConfirmed != 0, NativeDecodeError.ReadText(new(info.fallbackReason, 256)), info.generation,
            info.deliveredFrames, info.decodeNanoseconds, info.downloadNanoseconds);
        Volatile.Write(ref sessionInfo, value);
    }

    private static Version ReadVersion(uint value)
    {
        return new(checked((int)(value >> 16)), checked((int)((value >> 8) & 255)), checked((int)(value & 255)));
    }

    private static void RequireSeekFeature()
    {
        try
        {
            const uint REQUIRED_FEATURES = NativeDecodeMethods.SEEK_FEATURE | NativeDecodeMethods.DISPLAY_TIMING_FEATURE |
                NativeDecodeMethods.SEEK_SUPERSESSION_FEATURE;
            if ((NativeDecodeMethods.Features() & REQUIRED_FEATURES) != REQUIRED_FEATURES)
            {
                throw new NotSupportedException("原生解码库未提供定位、显示时间或可恢复定位中止能力，请重新构建 Decoder。");
            }
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new NotSupportedException("原生解码库缺少定位能力查询，请重新构建 Decoder。", exception);
        }
    }
}
