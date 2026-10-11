using System.Net;
using AegiNext.Desktop.Updates;

namespace AegiNext.Desktop.Tests.Updates;

public sealed class GitHubReleaseSourceTests
{
    [Fact]
    public async Task StableChannelUsesLatestAndPreservesMarkdown()
    {
        using var handler = new UpdateHttpMessageHandler((request, _) =>
        {
            Assert.Equal("/repos/Yohuke-no-Symphony/Aegisub-NEXT/releases/latest", request.RequestUri!.AbsolutePath);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/vnd.github+json");
            Assert.Contains(request.Headers.UserAgent, value => value.Product?.Name == "AegiNext");
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(UpdateTestData.Response(UpdateTestData.Json("v0.8.0", prerelease: false)));
        });
        using var client = new HttpClient(handler);
        var source = new GitHubReleaseSource(client);

        var release = await source.GetLatestAsync(UpdateChannel.STABLE, CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 8, 0, 0), release.Version);
        Assert.Equal("0.8.0 Beta", release.Name);
        Assert.Equal("## Changes\n- Fixed playback", release.ReleaseNotesMarkdown);
        Assert.Equal("https://github.com/Yohuke-no-Symphony/Aegisub-NEXT/releases/tag/v0.8.0", release.PageUri.AbsoluteUri);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PrereleaseChannelFollowsPagesAndSelectsHighestNumericVersion()
    {
        const string NEXT = "https://api.github.com/repos/Yohuke-no-Symphony/Aegisub-NEXT/releases?per_page=100&page=2";
        using var handler = new UpdateHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
                ? UpdateTestData.Response("[" + UpdateTestData.Json("v0.10.0", prerelease: false) + "]")
                : UpdateTestData.Response("[" + string.Join(',',
                    UpdateTestData.Json("rel/beta/0.9.0.0"),
                    UpdateTestData.Json("v99.0.0", draft: true),
                    UpdateTestData.Json("unrecognized")) + "]", NEXT)));
        using var client = new HttpClient(handler);

        var release = await new GitHubReleaseSource(client).GetLatestAsync(UpdateChannel.INCLUDE_PRERELEASE, CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 10, 0, 0), release.Version);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(NEXT, handler.Requests[1].AbsoluteUri);
    }

    [Fact]
    public async Task PrereleasesAreCandidatesAndMissingDescriptionUsesAnEmptyBody()
    {
        using var handler = new UpdateHttpMessageHandler((_, _) => Task.FromResult(
            UpdateTestData.Response("[" + UpdateTestData.Json(name: null, body: null) + "]")));
        using var client = new HttpClient(handler);

        var release = await new GitHubReleaseSource(client).GetLatestAsync(UpdateChannel.INCLUDE_PRERELEASE, CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("rel/beta/0.8.0.0", release.Name);
        Assert.Equal(string.Empty, release.ReleaseNotesMarkdown);
    }

    [Fact]
    public async Task StableReleaseWinsAnEqualNumericVersionTie()
    {
        using var handler = new UpdateHttpMessageHandler((_, _) => Task.FromResult(UpdateTestData.Response(
            "[" + UpdateTestData.Json() + "," + UpdateTestData.Json("v0.8.0", prerelease: false, name: "Stable") + "]")));
        using var client = new HttpClient(handler);

        var release = await new GitHubReleaseSource(client).GetLatestAsync(UpdateChannel.INCLUDE_PRERELEASE, CancellationToken.None);

        Assert.Equal("Stable", release!.Name);
    }

    [Fact]
    public async Task MissingLatestAndEmptyListAreReportedAsNoRelease()
    {
        using var handler = new UpdateHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : UpdateTestData.Response("[]")));
        using var client = new HttpClient(handler);
        var source = new GitHubReleaseSource(client);

        Assert.Null(await source.GetLatestAsync(UpdateChannel.STABLE, CancellationToken.None));
        Assert.Null(await source.GetLatestAsync(UpdateChannel.INCLUDE_PRERELEASE, CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LatestDoesNotAcceptDraftOrPrerelease(bool draft, bool prerelease)
    {
        using var handler = new UpdateHttpMessageHandler((_, _) => Task.FromResult(UpdateTestData.Response(
            UpdateTestData.Json(draft: draft, prerelease: prerelease))));
        using var client = new HttpClient(handler);

        Assert.Null(await new GitHubReleaseSource(client).GetLatestAsync(UpdateChannel.STABLE, CancellationToken.None));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailuresKeepStatusAndAreNotRetried(int status)
    {
        using var handler = new UpdateHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GitHubReleaseSource(client).GetLatestAsync(UpdateChannel.STABLE, CancellationToken.None));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UnrecognizedPublishedTagsAreNotMistakenForUpToDate()
    {
        using var handler = new UpdateHttpMessageHandler((_, _) => Task.FromResult(
            UpdateTestData.Response("[" + UpdateTestData.Json("nightly") + "]")));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubReleaseSource(client).GetLatestAsync(UpdateChannel.INCLUDE_PRERELEASE, CancellationToken.None));
    }

    [Fact]
    public async Task MalformedJsonIsAnInvalidResponse()
    {
        using var handler = new UpdateHttpMessageHandler((_, _) => Task.FromResult(UpdateTestData.Response("{broken")));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubReleaseSource(client).GetLatestAsync(UpdateChannel.STABLE, CancellationToken.None));
    }

    [Theory]
    [InlineData("file:///tmp/release")]
    [InlineData("https://example.com/release")]
    public async Task SelectedReleaseMustPointToThisGithubRepository(string url)
    {
        using var handler = new UpdateHttpMessageHandler((_, _) => Task.FromResult(
            UpdateTestData.Response(UpdateTestData.Json(prerelease: false, url: url))));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubReleaseSource(client).GetLatestAsync(UpdateChannel.STABLE, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://example.com/releases?page=2")]
    [InlineData("https://api.github.com/repos/Yohuke-no-Symphony/Aegisub-NEXT/releases?per_page=100")]
    public async Task PaginationRejectsForeignEndpointsAndLoops(string next)
    {
        using var handler = new UpdateHttpMessageHandler((_, _) => Task.FromResult(UpdateTestData.Response("[]", next)));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubReleaseSource(client).GetLatestAsync(UpdateChannel.INCLUDE_PRERELEASE, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TimeoutDuringPaginationIsAFailureRatherThanUserCancellation()
    {
        using var handler = new UpdateHttpMessageHandler(async (request, token) =>
        {
            if (!request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal))
            {
                return UpdateTestData.Response("[]",
                    "https://api.github.com/repos/Yohuke-no-Symphony/Aegisub-NEXT/releases?per_page=100&page=2");
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The request must be cancelled.");
        });
        using var client = new HttpClient(handler);
        var source = new GitHubReleaseSource(client, TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<TimeoutException>(() => source.GetLatestAsync(UpdateChannel.INCLUDE_PRERELEASE, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new UpdateHttpMessageHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The request must be cancelled.");
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var pending = new GitHubReleaseSource(client).GetLatestAsync(UpdateChannel.STABLE, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
