namespace AegiNext.Media.Preview;

/// <summary>
/// 独立的正方形像素 sRGB、不透明 BGRA8 显示图像；行紧密排列且从上到下。
/// </summary>
public sealed class SdrVideoFrame
{
    /// <summary>
    /// 验证显示布局并复制输入；后续修改输入数组不会影响图像。
    /// </summary>
    public SdrVideoFrame(int width, int height, byte[] pixels) : this(width, height, pixels, false)
    {
    }

    private SdrVideoFrame(int width, int height, byte[] pixels, bool takeOwnership)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)width * height > SdrPreviewOptions.MAXIMUM_PIXELS)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "预览图像超过像素上限。");
        }

        if (pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("BGRA8 数据长度与图像尺寸不一致。", nameof(pixels));
        }

        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != byte.MaxValue)
            {
                throw new ArgumentException("SDR 视频预览必须为不透明图像。", nameof(pixels));
            }
        }

        Width = width;
        Height = height;
        Pixels = takeOwnership ? pixels : pixels.ToArray();
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> Pixels { get; }

    private SdrVideoFrame(SdrVideoFrame source)
    {
        Width = source.Width;
        Height = source.Height;
        Pixels = source.Pixels;
    }

    /// <summary>创建独立的图像身份并共享不可变像素，供各次预览交付记录各自的时间与合成状态。</summary>
    public SdrVideoFrame CreateView()
    {
        return new(this);
    }

    internal static SdrVideoFrame FromOwnedPixels(int width, int height, byte[] pixels)
    {
        return new(width, height, pixels, true);
    }
}
