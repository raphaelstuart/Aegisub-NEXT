using AegiNext.Core.Timing;

namespace AegiNext.Media.Decoding;

/// <summary>
/// 提供精确显示时间的原始帧源，成功取得的帧由调用方拥有。
/// </summary>
public interface IVideoFrameSource : IDisposable
{
    /// <summary>
    /// 顺序读取下一显示帧；同一 PTS 的重复帧已归并。
    /// </summary>
    PositionedVideoFrame? ReadFrame(CancellationToken cancellationToken = default);

    /// <summary>
    /// 定位目标时间正在显示的帧，未知或倒退的原始 PTS 明确报错。
    /// </summary>
    PositionedVideoFrame? SeekFrame(MediaTime target, CancellationToken cancellationToken = default);

    /// <summary>
    /// 定位时允许在完整帧读取之间放弃过时请求；谓词须快速且线程安全，不能终止底层解码器。
    /// 不支持帧间中止的实现默认只在开始前检查，调用方仍须拒绝返回的过时结果。
    /// </summary>
    PositionedVideoFrame? SeekFrame(MediaTime target, Func<bool> isSuperseded, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isSuperseded);
        cancellationToken.ThrowIfCancellationRequested();
        if (isSuperseded())
        {
            throw new OperationCanceledException("视频定位已被新请求替换。");
        }

        return SeekFrame(target, cancellationToken);
    }

    /// <summary>
    /// 通知当前定位请求已过时；必须允许与定位并发且不等待读锁，不终止会话。
    /// 不支持原生帧间中止的实现无需处理，调用方仍检查交付代际。
    /// </summary>
    void SupersedeSeek()
    {
    }

    /// <summary>
    /// 请求终端协作取消，不用于普通暂停或跳转。
    /// </summary>
    void Cancel();
}
