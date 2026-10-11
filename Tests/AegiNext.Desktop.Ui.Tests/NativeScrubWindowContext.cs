using System.Diagnostics;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Desktop.Rendering;
using AegiNext.Desktop.Views;
using AegiNext.Media.Decoding;
using AegiNext.Media.Playback;
using Avalonia.Threading;

namespace AegiNext.Desktop.Ui.Tests;

internal sealed class NativeScrubWindowContext : IAsyncDisposable
{
    private readonly UiTestEnvironment environment = new();
    private readonly NativeScrubMetrics metrics;

    internal NativeScrubWindowContext(NativeScrubMetrics metrics)
    {
        this.metrics = metrics;
        Window = new(present =>
        {
            Controller = new(VideoPreviewProbe.ProbeAsync, (path, index, clock, options) => new(token =>
            {
                Source = new(VideoFrameNavigator.Open(path, index, options, token), metrics);
                return Source;
            }, TimeProvider.System, externalPosition: clock), () =>
            {
                Converter = new(() => Window!.Session.GetPreviewState(),
                    error => RenderingError = error, Window!.Session.PreviewFrames, () => Window.Session.Fonts.Catalog)
                {
                    StageMeasured = (stage, elapsed) => metrics.RecordDuration(stage,
                        Stopwatch.GetTimestamp() - elapsed, elapsed * 1000d / Stopwatch.Frequency)
                };
                return Converter;
            }, DispatchAsync, update =>
            {
                var started = Stopwatch.GetTimestamp();
                present(update);
                metrics.RecordStage("UiApply", started);
            });
            return Controller;
        });
        Window.Session.InteractionDiagnostics.Enabled = true;
        Window.Session.InteractionDiagnostics.Recorded += metrics.RecordInteraction;
        Window.Session.PreviewUpdated += OnPreviewUpdated;
        Window.Width = 1280;
        Window.Height = 800;
        Window.Show();
        Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    internal MainWindow Window { get; }
    internal VideoPreviewController Controller { get; private set; } = null!;
    internal NativeScrubMeasuredSource? Source { get; private set; }
    internal ProjectPreviewConverter? Converter { get; private set; }
    internal Exception? RenderingError { get; private set; }
    internal PreviewFrameRecord? LastPresentedIdentity { get; private set; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            Window.Session.InteractionDiagnostics.Recorded -= metrics.RecordInteraction;
            Window.Session.PreviewUpdated -= OnPreviewUpdated;
            Window.Close();
            var started = Stopwatch.GetTimestamp();
            while (Window.IsVisible)
            {
                foreach (var dialog in Window.OwnedWindows.OfType<UnsavedProjectDialog>().ToArray())
                {
                    dialog.Close(2);
                }
                Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15), "Native scrub benchmark window did not close.");
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
            await Window.DisposeAsync();
        }
        finally
        {
            environment.Dispose();
        }
    }

    private async Task DispatchAsync(Action action, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            metrics.RecordStage("UiQueue", started);
            action();
        }, DispatcherPriority.Normal, cancellationToken);
    }

    private void OnPreviewUpdated(object? sender, VideoPreviewUpdate update)
    {
        if (update.Frame is { } frame)
        {
            var identity = Window.Session.PreviewFrames.FindIdentity(frame);
            LastPresentedIdentity = update.IsTransientPreview ? null : identity;
            metrics.RecordPresentation(update, identity);
        }
    }
}
