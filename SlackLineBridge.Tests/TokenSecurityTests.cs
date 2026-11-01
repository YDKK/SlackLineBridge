using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SlackLineBridge.Controllers;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Services;
using SlackLineBridge.Utils;
using Xunit;

namespace SlackLineBridge.Tests;

public sealed class TokenSecurityTests
{
    // ASCII署名は一致時のみ許可し、長さ不一致や非ASCII文字列を拒否することを確認する。
    [Fact]
    public void FixedTimeAsciiComparisonRejectsMismatchesAndNonAscii()
    {
        Assert.True(Crypt.FixedTimeEqualsAscii("0123456789abcdef", "0123456789abcdef"));
        Assert.False(Crypt.FixedTimeEqualsAscii("0123456789abcdef", "0123456789abcdeg"));
        Assert.False(Crypt.FixedTimeEqualsAscii("0123456789abcdef", "short"));
        Assert.False(Crypt.FixedTimeEqualsAscii("0123456789abcdef", "0123456789abcd€f"));
        Assert.False(Crypt.FixedTimeEqualsAscii("expected", null!));
    }

    // 生成する互換トークンが従来どおり小文字64桁の16進数であることを確認する。
    [Fact]
    public void HmacHexPreservesLowercaseHexTokenFormat()
    {
        var token = Crypt.GetHMACHex("message-id", "line-secret");

        Assert.Equal(64, token.Length);
        Assert.Equal(token.ToLowerInvariant(), token);
        Assert.Matches("^[0-9a-f]{64}$", token);
    }

    // LINEとSlackの不正トークンを拒否し、HTTPクライアントも上流サービスも使わないことを確認する。
    [Fact]
    public async Task ProxyRejectsInvalidTokensBeforeCreatingClientOrAccessingMedia()
    {
        var clientFactory = new CountingHttpClientFactory();
        var mediaService = new CountingMediaProxyService();
        var controller = new ProxyController(
            NullLogger<ProxyController>.Instance,
            clientFactory,
            mediaService,
            new LineChannelSecret("line-secret"),
            new SlackSigningSecret("slack-secret"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var lineResult = await controller.Line("message-id", "short-token");
        var slackResult = await controller.Slack("https%3A%2F%2Fmedia.example.test%2Ffile", new string('a', 64));
        var nonAsciiToken = new string('a', 20) + "€" + new string('a', 43);
        var nonAsciiSlackResult = await controller.Slack("https%3A%2F%2Fmedia.example.test%2Ffile", nonAsciiToken);

        Assert.Equal((int)HttpStatusCode.Forbidden, Assert.IsType<StatusCodeResult>(lineResult).StatusCode);
        Assert.Equal((int)HttpStatusCode.Forbidden, Assert.IsType<StatusCodeResult>(slackResult).StatusCode);
        Assert.Equal((int)HttpStatusCode.Forbidden, Assert.IsType<StatusCodeResult>(nonAsciiSlackResult).StatusCode);
        Assert.Equal(0, clientFactory.CreateCount);
        Assert.Equal(0, mediaService.RequestCount);
    }

    // HTML・SVG・不明な形式には同一オリジン権限を与えず、専用クライアントと安全なヘッダーを使うことを確認する。
    [Theory]
    [InlineData(null)]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    [InlineData("video/mp4; codecs=\"unterminated")]
    public async Task ProxyUsesSlackMediaClientAndKeepsNonMediaContentIsolated(string? contentType)
    {
        const string url = "https://files.slack.com/files-private/file";
        var clientFactory = new CountingHttpClientFactory();
        var mediaService = new CountingMediaProxyService { ContentType = contentType };
        var controller = CreateController(clientFactory, mediaService);

        await controller.Slack(Uri.EscapeDataString(url), Crypt.GetHMACHex(url, "slack-secret"));

        Assert.Equal("SlackMedia", clientFactory.LastName);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("sandbox; default-src 'none'", controller.Response.Headers["Content-Security-Policy"].ToString());
    }

    private static ProxyController CreateController(CountingHttpClientFactory clientFactory, CountingMediaProxyService mediaService)
    {
        var controller = new ProxyController(
            NullLogger<ProxyController>.Instance,
            clientFactory,
            mediaService,
            new LineChannelSecret("line-secret"),
            new SlackSigningSecret("slack-secret"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private sealed class CountingHttpClientFactory : IHttpClientFactory
    {
        public int CreateCount { get; private set; }
        public string? LastName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateCount++;
            LastName = name;
            return new HttpClient();
        }
    }

    private sealed class CountingMediaProxyService : IMediaProxyService
    {
        public int RequestCount { get; private set; }
        public string? Url { get; private set; }
        public string? ContentType { get; init; }

        public Task<MediaProxyResponse> GetAsync(
            HttpClient client,
            string url,
            MediaCacheSource source,
            string identity,
            string rangeHeader,
            bool hasIfRange,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Url = url;
            return Task.FromResult(new MediaProxyResponse((int)HttpStatusCode.OK, contentType: ContentType));
        }
    }
}
