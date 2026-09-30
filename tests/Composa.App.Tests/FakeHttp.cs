using System.Net;

namespace Composa.App.Tests;

/// <summary>Answers HTTP requests from a function, so nothing about updates ever reaches the network in a test.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttp(IReadOnlyDictionary<string, byte[]> files)
        : this((request, _) => Task.FromResult(files.TryGetValue(request.RequestUri!.ToString(), out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound)))
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        lock (Requests) Requests.Add(request);
        return answer(request, cancel);
    }
}
