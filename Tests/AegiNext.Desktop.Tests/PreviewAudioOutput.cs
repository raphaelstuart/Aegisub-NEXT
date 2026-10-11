using AegiNext.Media.Audio;

namespace AegiNext.Desktop.Tests;

internal sealed class PreviewAudioOutput : IAudioOutput
{
    private readonly Lock gate = new();
    private int queued;
    private int clockQuality = (int)AudioClockQuality.ESTIMATED;
    private long playedFrames;
    private readonly List<float> written = [];
    internal bool Paused { get; private set; } = true;
    internal float Gain { get; private set; } = 1;
    internal int DisposeCount { get; private set; }
    internal int ClearCount { get; private set; }
    internal int PauseCount { get; private set; }
    internal Action? PlaybackStarted { get; set; }
    internal bool ThrowOnPause { get; set; }
    internal AudioClockQuality ClockQuality
    {
        get => (AudioClockQuality)Volatile.Read(ref clockQuality);
        set => Volatile.Write(ref clockQuality, (int)value);
    }
    internal float[] Written
    {
        get
        {
            lock (gate)
            {
                return written.ToArray();
            }
        }
    }
    /// <summary>允许回归测试切换输出时钟的可用性。</summary>
    public AudioOutputClockSnapshot ReadClock()
    {
        lock (gate)
        {
            return new(playedFrames, 0, 1, "unknown", "test", 0, ClockQuality, queued);
        }
    }

    public int LatencyFrames => 480;
    public int QueuedFrames
    {
        get
        {
            lock (gate)
            {
                return queued;
            }
        }
    }

    internal void Consume(int frames)
    {
        lock (gate)
        {
            if (!Paused)
            {
                var consumed = Math.Min(queued, frames);
                queued -= consumed;
                playedFrames += consumed;
            }
        }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (gate)
        {
            queued += samples.Length / 2;
            written.AddRange(samples.ToArray());
            Assert.InRange(queued, 0, 12000);
        }
    }

    public void SetPaused(bool paused)
    {
        lock (gate)
        {
            if (paused && ThrowOnPause)
            {
                throw new InvalidOperationException("输出设备暂停失败。");
            }
            Paused = paused;
            if (paused)
            {
                PauseCount++;
            }
            if (!paused)
            {
                PlaybackStarted?.Invoke();
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            ClearCount++;
            queued = 0;
            playedFrames = 0;
            written.Clear();
        }
    }

    public void SetGain(float gain)
    {
        Gain = gain;
    }

    public void Dispose()
    {
        DisposeCount++;
    }
}
