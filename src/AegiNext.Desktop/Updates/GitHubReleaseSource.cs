using System.Net;
using System.Text.Json;

namespace AegiNext.Desktop.Updates;

internal sealed class GitHubReleaseSource : IUpdateReleaseSource
{
    private const string REPOSITORY_PATH = "/Yohuke-no-Symphony/Aegisub-NEXT";
    private const string RELEASES_PATH = "/repos" + REPOSITORY_PATH + "/releases";
    private static readonly Uri latestUri = new("https://api.github.com" + RELEASES_PATH + "/latest");
    private static readonly Uri releasesUri = new("https://api.github.com" + RELEASES_PATH + "?per_page=100");
    private readonly HttpClient client;
    private readonly TimeSpan timeout;

    internal GitHubReleaseSource(HttpClient client, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
        this.timeout = timeout ?? TimeSpan.FromSeconds(15);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.timeout, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public async Task<UpdateRelease?> GetLatestAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(timeout);
        try
        {
            var release = channel == UpdateChannel.STABLE
                ? await GetStableAsync(cancellation.Token).ConfigureAwait(false)
                : await GetIncludingPrereleaseAsync(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            return release;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The GitHub release check timed out.", error);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("GitHub returned invalid release data.", error);
        }
    }

    private async Task<UpdateRelease?> GetStableAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(latestUri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        var release = await ReadAsync<GitHubReleaseResponse>(response, cancellationToken).ConfigureAwait(false);
        if (release.Draft || release.Prerelease)
        {
            return null;
        }
        if (!ReleaseVersion.TryParse(release.TagName, out var version))
        {
            throw new InvalidDataException("The GitHub release tag does not contain a supported numeric version.");
        }
        return CreateRelease(release, version);
    }

    private async Task<UpdateRelease?> GetIncludingPrereleaseAsync(CancellationToken cancellationToken)
    {
        GitHubReleaseResponse? selected = null;
        Version? selectedVersion = null;
        var hasPublishedRelease = false;
        var visited = new HashSet<Uri>();
        Uri? page = releasesUri;
        while (page is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(page))
            {
                throw new InvalidDataException("GitHub release pagination contains a repeated page.");
            }
            using var response = await SendAsync(page, cancellationToken).ConfigureAwait(false);
            var releases = await ReadAsync<GitHubReleaseResponse[]>(response, cancellationToken).ConfigureAwait(false);
            foreach (var release in releases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (release is null)
                {
                    throw new InvalidDataException("GitHub returned an empty release entry.");
                }
                if (release.Draft)
                {
                    continue;
                }
                hasPublishedRelease = true;
                if (ReleaseVersion.TryParse(release.TagName, out var version) &&
                    (selectedVersion is null || version > selectedVersion ||
                     version == selectedVersion && selected!.Prerelease && !release.Prerelease))
                {
                    selected = release;
                    selectedVersion = version;
                }
            }
            page = GetNextPage(response);
        }
        if (selected is not null)
        {
            return CreateRelease(selected, selectedVersion!);
        }
        if (hasPublishedRelease)
        {
            throw new InvalidDataException("The GitHub releases do not contain supported numeric versions.");
        }
        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("AegiNext");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub returned an empty release response.");
    }

    private static Uri? GetNextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var headers))
        {
            return null;
        }
        foreach (var link in headers.SelectMany(header => header.Split(',')))
        {
            var parts = link.Split(';', StringSplitOptions.TrimEntries);
            if (!parts.Skip(1).Contains("rel=\"next\"", StringComparer.Ordinal))
            {
                continue;
            }
            var target = parts[0];
            if (target.Length < 3 || target[0] != '<' || target[^1] != '>' ||
                !Uri.TryCreate(target[1..^1], UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.Host != "api.github.com" || !uri.IsDefaultPort ||
                uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != RELEASES_PATH)
            {
                throw new InvalidDataException("GitHub returned an invalid release pagination link.");
            }
            return uri;
        }
        return null;
    }

    private static UpdateRelease CreateRelease(GitHubReleaseResponse release, Version version)
    {
        if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.StartsWith(REPOSITORY_PATH + "/releases/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("GitHub returned an invalid release page address.");
        }
        return new(version, release.TagName!, string.IsNullOrWhiteSpace(release.Name) ? release.TagName! : release.Name,
            release.Body ?? string.Empty, uri);
    }
}
