using AegiNext.Core.Timing;
using AegiNext.Desktop.I18n;

namespace AegiNext.Desktop.Workspace;

internal sealed partial class WorkbenchSession
{
    internal PreviewInteractionDiagnostics InteractionDiagnostics { get; } = new();

    internal void SetInteractiveSeeking(bool value) => playback.SetInteractive(value);
    internal void CancelInteractiveSeeking() => playback.Invalidate();
    internal bool IsTransportPlaybackRequested => playback.IsPlaybackRequested;

    internal Task SeekForEditingAsync(MediaTime time) => playback.SeekForEditingAsync((controller.Snapshot.Start ?? MediaTime.Zero) + time);

    internal Task SeekFromUserAsync(MediaTime position) => playback.SeekFromUserAsync(position);
    internal Task SeekRelativeAsync(long seconds) => playback.SeekRelativeAsync(seconds);
    internal Task SeekProjectTimeAsync(MediaTime relative) => playback.SeekProjectTimeAsync(relative);

    private async Task SeekSelectedClipBoundaryAsync(bool end)
    {
        if (SelectedLayer is not { } layer)
        {
            return;
        }

        var target = end ? layer.End : layer.Start;
        ViewModel.CancelGestures();
        await SeekProjectTimeAsync(target);

        var timeline = ViewModel.Timeline;
        var seconds = ToSeconds(ProjectPosition);
        if (timeline.VisibleDuration > 0 &&
            (seconds < timeline.ViewStart || seconds >= timeline.ViewStart + timeline.VisibleDuration))
        {
            timeline.ViewStart = Math.Max(0, seconds - timeline.VisibleDuration / 5);
        }
    }

    private void OnPreviewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (IsUpdating)
        {
            return;
        }

        if (e.PropertyName == "IsScrubbing")
        {
            SetInteractiveSeeking(ViewModel.Preview.IsScrubbing);
        }
        else if (e.PropertyName == "Volume")
        {
            var value = (float)ViewModel.Preview.Volume;
            controller.SetVolume(value);
            UpdatePreferences(current => current with { Volume = value });
        }
        else if (e.PropertyName == "IsMuted")
        {
            controller.SetMuted(ViewModel.Preview.IsMuted);
            ViewModel.Preview.MuteLabel = Localization.Get("Preview." + (ViewModel.Preview.IsMuted ? "Unmute" : "Mute"));
        }
        else if (e.PropertyName == "SelectedQuality" && ViewModel.Preview.SelectedQuality is { } quality &&
                 quality.Id != Preferences.PreviewQuality)
        {
            UpdatePreferences(current => current with { PreviewQuality = quality.Id });
        }
    }
    private void OnExportPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Crf")
        {
            ViewModel.Export.CrfText = ViewModel.Export.Crf?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;
        }
        else if (e.PropertyName == "AudioBitrate")
        {
            ViewModel.Export.AudioBitrateText = ViewModel.Export.AudioBitrate?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;
        }
        else if (e.PropertyName == "VideoBitrate")
        {
            ViewModel.Export.VideoBitrateText = ViewModel.Export.VideoBitrate?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;
        }
    }
}
