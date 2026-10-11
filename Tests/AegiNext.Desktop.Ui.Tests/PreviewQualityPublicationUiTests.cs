using System.ComponentModel;
using AegiNext.Desktop.Panels.Preview;
using AegiNext.Desktop.Settings;
using Avalonia.Headless.XUnit;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class PreviewQualityPublicationUiTests
{
    [AvaloniaTheory]
    [InlineData(PreviewQuality.HIGH)]
    [InlineData(PreviewQuality.LOWEST)]
    public async Task QualityStateIsPublishedBeforeTheSelectorAndSceneNotifyObservers(PreviewQuality quality)
    {
        await using var context = new MainWindowTestContext();
        var previous = context.Session.GetPreviewState();
        var observed = new List<(string PropertyName, PreviewQuality Quality, long Revision)>();
        context.ViewModel.Preview.PropertyChanged += OnPreviewChanged;
        try
        {
            context.Session.UpdatePreferences(context.Session.Preferences with { PreviewQuality = quality });
        }
        finally
        {
            context.ViewModel.Preview.PropertyChanged -= OnPreviewChanged;
        }

        Assert.Contains(observed, value => value.PropertyName == nameof(PreviewPanelViewModel.SelectedQuality));
        Assert.Contains(observed, value => value.PropertyName == nameof(PreviewPanelViewModel.Scene));
        Assert.All(observed, value =>
        {
            Assert.Equal(quality, value.Quality);
            Assert.Equal(previous.QualityRevision + 1, value.Revision);
        });

        void OnPreviewChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(PreviewPanelViewModel.SelectedQuality) or nameof(PreviewPanelViewModel.Scene))
            {
                var state = context.Session.GetPreviewState();
                observed.Add((args.PropertyName, state.Quality, state.QualityRevision));
            }
        }
    }
}
