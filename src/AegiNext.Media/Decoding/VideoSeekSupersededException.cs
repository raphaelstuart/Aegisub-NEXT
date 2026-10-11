namespace AegiNext.Media.Decoding;

internal sealed class VideoSeekSupersededException : OperationCanceledException
{
    internal VideoSeekSupersededException(bool requiresSeek = false) : base("视频定位已被新请求替换。")
    {
        RequiresSeek = requiresSeek;
    }

    internal bool RequiresSeek { get; }
}
