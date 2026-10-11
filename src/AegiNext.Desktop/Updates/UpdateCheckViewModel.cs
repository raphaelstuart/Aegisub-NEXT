using System.Net;
using AegiNext.Desktop.I18n;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AegiNext.Desktop.Updates;

internal sealed class UpdateCheckViewModel : ObservableObject, IDisposable
{
    private readonly Func<Uri, Task<bool>> launchUri;
    private UpdateCheckResult result;
    private UpdateChannel channel;
    private string? linkErrorKey;
    private bool disposed;

    internal UpdateCheckViewModel(UpdateCheckResult result, UpdateChannel channel, Func<Task> retry,
        Func<Uri, Task<bool>> launchUri)
    {
        this.result = result;
        this.channel = channel;
        this.launchUri = launchUri;
        RetryCommand = new(retry, () => CanRetry && !disposed);
        OpenReleaseCommand = new(() => OpenLinkAsync(this.result.Release?.PageUri.AbsoluteUri), () => HasRelease && !disposed);
        OpenLinkCommand = new(OpenLinkAsync);
        Localization.LanguageChanged += OnLanguageChanged;
    }

    public string CurrentVersion => result.CurrentVersion;
    public string AvailableVersion => result.Release is { } release ? $"{release.Name} ({release.TagName})" : string.Empty;
    public string ReleaseNotesMarkdown => result.Release?.ReleaseNotesMarkdown ?? string.Empty;
    public bool HasRelease => result.Status == UpdateCheckStatus.UPDATE_AVAILABLE && result.Release is not null;
    public bool CanRetry => result.Status == UpdateCheckStatus.FAILED;
    public bool HasError => linkErrorKey is not null;
    public string? Error => linkErrorKey is { } key ? Localization.Get(key) : null;
    public string Message => Localization.Get(result.Status switch
    {
        UpdateCheckStatus.UPDATE_AVAILABLE => "Updates.Available",
        UpdateCheckStatus.UP_TO_DATE => "Updates.UpToDate",
        UpdateCheckStatus.NO_RELEASE => channel == UpdateChannel.STABLE ? "Updates.NoStableRelease" : "Updates.NoRelease",
        _ => GetFailureKey(result.Error)
    });
    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand OpenReleaseCommand { get; }
    public AsyncRelayCommand<string> OpenLinkCommand { get; }

    internal void Update(UpdateCheckResult value, UpdateChannel selectedChannel)
    {
        result = value;
        channel = selectedChannel;
        linkErrorKey = null;
        OnPropertyChanged(string.Empty);
        RetryCommand.NotifyCanExecuteChanged();
        OpenReleaseCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        disposed = true;
        Localization.LanguageChanged -= OnLanguageChanged;
    }

    private async Task OpenLinkAsync(string? address)
    {
        if (disposed)
        {
            return;
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            SetLinkError("Updates.UnsupportedLink");
            return;
        }
        try
        {
            SetLinkError(await launchUri(uri) ? null : "Updates.BrowserFailed");
        }
        catch (Exception)
        {
            SetLinkError("Updates.BrowserFailed");
        }
    }

    private void SetLinkError(string? key)
    {
        if (disposed)
        {
            return;
        }
        linkErrorKey = key;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Error));
    }

    private static string GetFailureKey(Exception? error) => error switch
    {
        TimeoutException => "Updates.TimedOut",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => "Updates.RateLimited",
        InvalidDataException => "Updates.InvalidRelease",
        _ => "Updates.CheckFailed"
    };
}
