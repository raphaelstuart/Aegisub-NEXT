using System.Collections.Immutable;
using System.Buffers;
using System.Globalization;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Media.Decoding;
using AegiNext.Desktop.Settings.Projects;
using AegiNext.Desktop.Settings.TimingPostProcessor;
using AegiNext.Desktop.Settings.Media;
using AegiNext.Desktop.Settings.AudioAnalysis;
using AegiNext.Desktop.Updates;

namespace AegiNext.Desktop.Settings;

/// <summary>可持久化并立即应用的桌面偏好。</summary>
public sealed record WorkbenchPreferences
{
    private static readonly SearchValues<char> hexadecimalCharacters = SearchValues.Create("0123456789abcdefABCDEF");
    public int Version { get; init; } = 1;
    public string Language { get; init; } = "system";
    public WorkbenchTheme Theme { get; init; }
    public string AccentColor { get; init; } = "#5273E8";
    public AudioGraphPalette AudioGraph { get; init; } = new();
    public AudioAnalysisPreferences AudioAnalysis { get; init; } = new();
    public TimelineClipPalette TimelineClips { get; init; } = new();
    public ImmutableArray<ShortcutBinding> ShortcutBindings { get; init; } = ShortcutDefaults.CreateBindings();
    public float Volume { get; init; } = 1;
    public bool WindowMenuOnMac { get; init; }
    public PreviewQuality PreviewQuality { get; init; } = PreviewQuality.LOW;
    public VideoDecodeMode PreviewDecodeMode { get; init; } = VideoDecodeMode.Auto;
    public int SubtitleAuditionMilliseconds { get; init; } = 500;
    public int MaximumConcurrentTasks { get; init; } = 4;
    public bool AutoCheckUpdates { get; init; } = true;
    public UpdateChannel UpdateChannel { get; init; } = UpdateChannel.INCLUDE_PRERELEASE;
    public bool TimelineClassicTimingEnabled { get; init; }
    public bool TimelineSnapEnabled { get; init; } = true;
    public bool TimelineStepEnabled { get; init; }
    public bool TimelineSpectrumVisible { get; init; } = true;
    public bool TimelineWaveformVisible { get; init; } = true;
    public ImmutableArray<string> CollapsedEffectCategories { get; init; } = ["clip", "fill", "stroke", "shadow", "composite", "path", "animation"];
    public ProjectPreferences Projects { get; init; } = new();
    public TimingPostProcessorPreferences TimingPostProcessor { get; init; } = new();
    public ImmutableArray<AudioDeviceCalibration> AudioCalibrations { get; init; } = [];

    /// <summary>拒绝未知设置版本、语言、主题、非法音量或非正试听时长。</summary>
    public void Validate()
    {
        if (Version != 1 || !IsValidLanguage(Language) ||
            !Enum.IsDefined(Theme) || !Enum.IsDefined(PreviewQuality) || !Enum.IsDefined(PreviewDecodeMode) || !Enum.IsDefined(UpdateChannel) || !float.IsFinite(Volume) || Volume is < 0 or > 1 ||
            SubtitleAuditionMilliseconds < 1 || MaximumConcurrentTasks is < 1 or > 32 || AccentColor is null || AccentColor.Length != 7 || AccentColor[0] != '#' ||
            AccentColor.AsSpan(1).ContainsAnyExcept(hexadecimalCharacters) || ShortcutBindings.IsDefault || AudioGraph is null || AudioAnalysis is null || TimelineClips is null || Projects is null || TimingPostProcessor is null)
        {
            throw new InvalidDataException("桌面偏好无效或版本不受支持。");
        }

        if (CollapsedEffectCategories.IsDefault || CollapsedEffectCategories.Distinct().Count() != CollapsedEffectCategories.Length ||
            CollapsedEffectCategories.Any(category => category is not ("clip" or "transform" or "typography" or "fill" or "stroke" or "shadow" or "composite" or "mask" or "path" or "animation")))
        {
            throw new InvalidDataException("特效面板分类偏好无效。");
        }
        AudioGraph.Validate();
        AudioAnalysis.Validate();
        TimelineClips.Validate();
        Projects.Validate();
        TimingPostProcessor.Validate();
        if (AudioCalibrations.IsDefault || AudioCalibrations.Length > 32 || AudioCalibrations.Any(value => value is null) ||
            AudioCalibrations.Select(value => (value.DeviceId, value.Backend, value.SampleRate, value.Channels)).Distinct().Count() != AudioCalibrations.Length)
        {
            throw new InvalidDataException("音频设备校准配置无效或存在重复设备。");
        }
        foreach (var calibration in AudioCalibrations)
        {
            calibration.Validate();
        }
        ShortcutConfiguration.Validate(ShortcutBindings);
        if (ShortcutBindings.Length != Enum.GetValues<WorkbenchCommand>().Length)
        {
            throw new InvalidDataException("快捷键设置必须包含所有命令，禁用请使用空手势。");
        }
    }

    private static bool IsValidLanguage(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return !CultureInfo.GetCultureInfo(value).Equals(CultureInfo.InvariantCulture);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>按设置值和快捷键内容比较偏好快照。</summary>
    public bool Equals(WorkbenchPreferences? other)
    {
        return other is not null && Version == other.Version && Language == other.Language && Theme == other.Theme &&
               AccentColor == other.AccentColor && AudioGraph == other.AudioGraph && AudioAnalysis == other.AudioAnalysis && TimelineClips == other.TimelineClips && Volume.Equals(other.Volume) && WindowMenuOnMac == other.WindowMenuOnMac && PreviewQuality == other.PreviewQuality && PreviewDecodeMode == other.PreviewDecodeMode &&
               SubtitleAuditionMilliseconds == other.SubtitleAuditionMilliseconds && MaximumConcurrentTasks == other.MaximumConcurrentTasks && TimelineClassicTimingEnabled == other.TimelineClassicTimingEnabled &&
               AutoCheckUpdates == other.AutoCheckUpdates && UpdateChannel == other.UpdateChannel &&
               TimelineSnapEnabled == other.TimelineSnapEnabled && TimelineStepEnabled == other.TimelineStepEnabled &&
               TimelineSpectrumVisible == other.TimelineSpectrumVisible && TimelineWaveformVisible == other.TimelineWaveformVisible &&
               CollapsedEffectCategories.AsSpan().SequenceEqual(other.CollapsedEffectCategories.AsSpan()) &&
               Projects == other.Projects && TimingPostProcessor == other.TimingPostProcessor &&
               AudioCalibrations.AsSpan().SequenceEqual(other.AudioCalibrations.AsSpan()) &&
               ShortcutBindings.AsSpan().SequenceEqual(other.ShortcutBindings.AsSpan());
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(Language);
        hash.Add(Theme);
        hash.Add(AccentColor);
        hash.Add(AudioGraph);
        hash.Add(AudioAnalysis);
        hash.Add(TimelineClips);
        hash.Add(Volume);
        hash.Add(WindowMenuOnMac);
        hash.Add(PreviewQuality);
        hash.Add(PreviewDecodeMode);
        hash.Add(SubtitleAuditionMilliseconds);
        hash.Add(MaximumConcurrentTasks);
        hash.Add(AutoCheckUpdates);
        hash.Add(UpdateChannel);
        hash.Add(TimelineClassicTimingEnabled);
        hash.Add(TimelineSnapEnabled);
        hash.Add(TimelineStepEnabled);
        hash.Add(TimelineSpectrumVisible);
        hash.Add(TimelineWaveformVisible);
        foreach (var category in CollapsedEffectCategories)
        {
            hash.Add(category);
        }
        hash.Add(Projects);
        hash.Add(TimingPostProcessor);
        foreach (var calibration in AudioCalibrations)
        {
            hash.Add(calibration);
        }
        foreach (var binding in ShortcutBindings)
        {
            hash.Add(binding);
        }

        return hash.ToHashCode();
    }
}
