using System.Text;

namespace AegiNext.Media.Decoding;

internal static class NativeDecodeError
{
    internal static void ThrowIfFailed(int code, ReadOnlySpan<byte> error, CancellationToken cancellationToken = default)
    {
        if (code == 0)
        {
            return;
        }

        var message = $"原生视频解码错误 {code}：{ReadText(error)}";
        switch (code)
        {
            case NativeDecodeMethods.CANCELLED:
                throw new OperationCanceledException(message, cancellationToken);
            case NativeDecodeMethods.INVALID_ARGUMENT:
                throw new ArgumentException(message);
            case NativeDecodeMethods.UNSUPPORTED:
                throw new NotSupportedException(message);
            case NativeDecodeMethods.INVALID_STATE:
                throw new InvalidOperationException(message);
            case NativeDecodeMethods.DISPLAY_TIMING_UNAVAILABLE:
                throw new VideoDisplayTimingUnavailableException(message);
            case NativeDecodeMethods.SEEK_SUPERSEDED:
                throw new VideoSeekSupersededException(requiresSeek: true);
            default:
                throw new InvalidDataException(message);
        }
    }

    internal static string ReadText(ReadOnlySpan<byte> value)
    {
        var terminator = value.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException("原生解码文本缺少终止符。");
        }

        return System.Text.Encoding.UTF8.GetString(value[..terminator]);
    }
}
