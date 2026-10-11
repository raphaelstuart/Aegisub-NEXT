using System.Collections.Concurrent;

namespace AegiNext.Desktop.Tests.Updates;

internal sealed class UpdateHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly ConcurrentQueue<Uri> requests = new();

    internal IReadOnlyList<Uri> Requests => requests.ToArray();

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        requests.Enqueue(request.RequestUri!);
        return respond(request, cancellationToken);
    }
}
