using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SlackLineBridge.Services;
using Xunit;

namespace SlackLineBridge.Tests;

public sealed class OutboundSecurityTests
{
    // HTTPや別ホストにはBearer tokenを送らないことを確認する。
    [Fact]
    public async Task BearerTokenIsSentOnlyToSlackOverHttps()
    {
        var innerHandler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://future-cdn.example.test:9443/files/a") }
            },
            _ => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("http://files.slack.com/files/b") }
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new SlackMediaHandler("test-slack-token", innerHandler));

        using var response = await client.GetAsync("https://files.slack.com/files/a");
        using var plainHttpResponse = await client.GetAsync("http://files.slack.com/files/c");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, plainHttpResponse.StatusCode);
        Assert.Equal(4, innerHandler.Requests.Count);
        Assert.Equal("Bearer test-slack-token", innerHandler.Requests[0].Authorization);
        Assert.Null(innerHandler.Requests[1].Authorization);
        Assert.Equal(new Uri("https://future-cdn.example.test:9443/files/a"), innerHandler.Requests[1].Uri);
        Assert.Equal(new Uri("http://files.slack.com/files/b"), innerHandler.Requests[2].Uri);
        Assert.Null(innerHandler.Requests[2].Authorization);
        Assert.Equal(new Uri("http://files.slack.com/files/c"), innerHandler.Requests[3].Uri);
        Assert.Null(innerHandler.Requests[3].Authorization);
    }

    // 相対Locationを現在のSlack URLに対して解決して追従することを確認する。
    [Fact]
    public async Task RelativeRedirectIsResolvedSafely()
    {
        var innerHandler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("/files/b", UriKind.Relative) }
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new SlackMediaHandler("test-slack-token", innerHandler));

        using var response = await client.GetAsync("https://files.slack.com/files/a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Uri("https://files.slack.com/files/b"), innerHandler.Requests[1].Uri);
        Assert.Equal("Bearer test-slack-token", innerHandler.Requests[1].Authorization);
    }

    // redirectを5回までに制限し、上限後に通信しないことを確認する。
    [Fact]
    public async Task RedirectLoopStopsAtMaximumHopCount()
    {
        var innerHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("/next", UriKind.Relative) }
        });
        using var client = new HttpClient(new SlackMediaHandler("test-slack-token", innerHandler));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://files.slack.com/files/a"));

        Assert.Equal(6, innerHandler.Requests.Count);
    }

    private sealed class RecordingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _responseIndex;

        public List<RecordedRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri ?? throw new InvalidOperationException("Expected an absolute test request URI."),
                request.Headers.Authorization?.ToString()));
            var responseIndex = Math.Min(_responseIndex++, responses.Length - 1);
            return Task.FromResult(responses[responseIndex](request));
        }
    }

    private sealed record RecordedRequest(Uri Uri, string? Authorization);
}
