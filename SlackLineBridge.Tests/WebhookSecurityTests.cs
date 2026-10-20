using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Samples;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SlackLineBridge.Controllers;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Services;
using SlackLineBridge.Utils;
using Xunit;

namespace SlackLineBridge.Tests;

public sealed class WebhookSecurityTests
{
    private const string LineSecret = "test-line-secret";
    private const string SlackSecret = "test-slack-secret";
    private const int MaximumBodySize = 1024 * 1024;

    // 受信時の本文バイト列と時刻ヘッダー文字列を使ってSlack署名を検証することを確認する。
    [Fact]
    public async Task SlackAcceptsValidSignatureOverRawBomBodyAndOriginalTimestampText()
    {
        var json = Encoding.UTF8.GetBytes("{\"type\":\"url_verification\",\"challenge\":\"challenge-marker\"}");
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray();
        var timestamp = "0" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var controller = CreateController(body);
        SetSlackHeaders(controller, timestamp, SlackSignature(body, timestamp));

        var result = await controller.Slack2();

        Assert.Equal("challenge-marker", Assert.IsType<OkObjectResult>(result).Value);
    }

    // 本文改ざんと長さの異なる署名をSlackが拒否することを確認する。
    [Fact]
    public async Task SlackRejectsTamperedBodyAndMalformedSignature()
    {
        var original = Encoding.UTF8.GetBytes("{\"type\":\"url_verification\",\"challenge\":\"original\"}");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var tampered = Encoding.UTF8.GetBytes("{\"type\":\"url_verification\",\"challenge\":\"tampered\"}");
        var controller = CreateController(tampered);
        SetSlackHeaders(controller, timestamp, SlackSignature(original, timestamp));

        Assert.IsType<BadRequestResult>(await controller.Slack2());

        var shortSignature = CreateController(original);
        SetSlackHeaders(shortSignature, timestamp, "v0=bad");
        Assert.IsType<BadRequestResult>(await shortSignature.Slack2());
    }

    // Slackの必須認証ヘッダーが欠落または重複した要求を拒否することを確認する。
    [Fact]
    public async Task SlackRejectsMissingOrRepeatedRequiredHeaders()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var missing = CreateController(body);
        Assert.IsType<BadRequestResult>(await missing.Slack2());

        var repeated = CreateController(body);
        repeated.Request.Headers["X-Slack-Request-Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        repeated.Request.Headers["X-Slack-Signature"] = new Microsoft.Extensions.Primitives.StringValues(new[] { "v0=first", "v0=second" });
        Assert.IsType<BadRequestResult>(await repeated.Slack2());
    }

    // 極端な時刻値で差分計算がオーバーフローせず拒否されることを確認する。
    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    [InlineData("not-an-integer")]
    public async Task SlackRejectsExtremeOrInvalidTimestamps(string timestamp)
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var controller = CreateController(body);
        SetSlackHeaders(controller, timestamp, "v0=" + new string('a', 64));

        Assert.IsType<BadRequestResult>(await controller.Slack2());
    }

    // Slackの再送を署名検証後にだけ早期応答することを確認する。
    [Fact]
    public async Task SlackRetryAcknowledgementRequiresValidAuthentication()
    {
        var body = Encoding.UTF8.GetBytes("not-json");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var rejected = CreateController(body);
        SetSlackHeaders(rejected, timestamp, "v0=" + new string('a', 64));
        rejected.Request.Headers["X-Slack-Retry-Reason"] = "http_timeout";
        Assert.IsType<BadRequestResult>(await rejected.Slack2());

        var accepted = CreateController(body);
        SetSlackHeaders(accepted, timestamp, SlackSignature(body, timestamp));
        accepted.Request.Headers["X-Slack-Retry-Reason"] = "http_timeout";
        Assert.IsType<OkResult>(await accepted.Slack2());
    }

    // 正しい署名が付いていてもJSONとして壊れたSlack本文を拒否することを確認する。
    [Fact]
    public async Task SlackRejectsMalformedJsonAfterAuthentication()
    {
        var body = Encoding.UTF8.GetBytes("{");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var controller = CreateController(body);
        SetSlackHeaders(controller, timestamp, SlackSignature(body, timestamp));

        Assert.IsType<BadRequestResult>(await controller.Slack2());
    }

    // 署名エラー時に本文や署名値をログへ出さないことを確認する。
    [Fact]
    public async Task SlackAuthenticationFailureDoesNotLogBodyOrSignature()
    {
        const string bodyMarker = "private-slack-body-marker";
        const string signatureMarker = "private-slack-signature-marker";
        using var provider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var body = Encoding.UTF8.GetBytes($"{{\"type\":\"url_verification\",\"challenge\":\"{bodyMarker}\"}}");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var controller = CreateController(body, logger: loggerFactory.CreateLogger<WebhookController>());
        SetSlackHeaders(controller, timestamp, signatureMarker);

        Assert.IsType<BadRequestResult>(await controller.Slack2());

        var logs = string.Join("\n", provider.Messages);
        Assert.DoesNotContain(bodyMarker, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(signatureMarker, logs, StringComparison.Ordinal);
    }

    // LINEの署名はBOMを含む受信時バイト列で検証し、そのバイト列をキューへ渡すことを確認する。
    [Fact]
    public async Task LinePreservesRawBomBodyAfterSignatureValidation()
    {
        var json = Encoding.UTF8.GetBytes("{\"events\":[]}");
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray();
        var queue = new LineWebhookQueue(4);
        var controller = CreateController(body, queue);
        controller.Request.Headers["X-Line-Signature"] = LineSignature(body);

        Assert.IsType<OkResult>(await controller.LineAsync());
        Assert.Equal(body, (await ReadNextAsync(queue)).Body);
    }

    // LINEの必須署名ヘッダーが欠落または重複した要求を拒否することを確認する。
    [Fact]
    public async Task LineRejectsMissingOrRepeatedSignatureHeaders()
    {
        var body = Encoding.UTF8.GetBytes("{\"events\":[]}");
        var missing = CreateController(body);
        Assert.IsType<BadRequestResult>(await missing.LineAsync());

        var repeated = CreateController(body);
        repeated.Request.Headers["X-Line-Signature"] = new Microsoft.Extensions.Primitives.StringValues(new[] { LineSignature(body), LineSignature(body) });
        Assert.IsType<BadRequestResult>(await repeated.LineAsync());
    }

    // 正しいLINE署名が付いていても構文不正なJSONをキューへ登録しないことを確認する。
    [Fact]
    public async Task LineRejectsMalformedJsonAfterAuthentication()
    {
        var body = Encoding.UTF8.GetBytes("{");
        var queue = new LineWebhookQueue(4);
        var controller = CreateController(body, queue);
        controller.Request.Headers["X-Line-Signature"] = LineSignature(body);

        Assert.IsType<BadRequestResult>(await controller.LineAsync());

        var marker = new LineWebhookRequest(Encoding.UTF8.GetBytes("marker"), "marker-host");
        Assert.True(queue.TryEnqueue(marker));
        Assert.Equal(marker, await ReadNextAsync(queue));
    }

    // Content-Lengthの有無にかかわらずLINE本文を1MiBへ制限することを確認する。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LineRejectsOversizedBodyWithOrWithoutContentLength(bool hasContentLength)
    {
        var body = new byte[MaximumBodySize + 1];
        var controller = CreateController(body);
        controller.Request.Headers["X-Line-Signature"] = LineSignature(body);
        if (hasContentLength)
        {
            controller.Request.ContentLength = body.Length;
        }

        var result = Assert.IsType<StatusCodeResult>(await controller.LineAsync());

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.StatusCode);
    }

    // Slack本文にもContent-Lengthなしで1MiBの上限を適用することを確認する。
    [Fact]
    public async Task SlackRejectsOversizedBodyWithoutContentLength()
    {
        var body = new byte[MaximumBodySize + 1];
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var controller = CreateController(body);
        SetSlackHeaders(controller, timestamp, SlackSignature(body, timestamp));

        var result = Assert.IsType<StatusCodeResult>(await controller.Slack2());

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.StatusCode);
    }

    // LINEの本文が上限ちょうどなら正常に受理され、バイト列も保たれることを確認する。
    [Fact]
    public async Task LineAcceptsBodyAtMaximumSize()
    {
        var prefix = Encoding.UTF8.GetBytes("{\"events\":[]}");
        var body = new byte[MaximumBodySize];
        Array.Copy(prefix, body, prefix.Length);
        Array.Fill(body, (byte)' ', prefix.Length, body.Length - prefix.Length);
        var queue = new LineWebhookQueue(4);
        var controller = CreateController(body, queue);
        controller.Request.Headers["X-Line-Signature"] = LineSignature(body);

        Assert.IsType<OkResult>(await controller.LineAsync());
        Assert.Equal(body, (await ReadNextAsync(queue)).Body);
    }

    // LINE送信本文と応答本文、Slack本文を処理ログへ記録しないことを確認する。
    [Fact]
    public async Task SlackForwardingLogsOnlyLineResponseStatus()
    {
        const string bodyMarker = "private-forwarded-text-marker";
        const string nameMarker = "private-user-name-marker";
        const string responseMarker = "private-line-response-marker";
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("users.profile.get", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"profile\":{\"display_name\":\"" + nameMarker + "\",\"image_512\":\"https://private.example.test/icon\"}}"));
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.OK, responseMarker));
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://line.example.test/") };
        var queue = new LineWebhookQueue(4);
        var channels = new SlackChannels
        {
            Channels = [new SlackChannel { Name = "slack", TeamId = "team", ChannelId = "channel" }]
        };
        var lineChannels = new LineChannels { Channels = [new LineChannel { Name = "line", Id = "line-target" }] };
        var bridges = new SlackLineBridges { Bridges = [new global::SlackLineBridge.Models.Configurations.SlackLineBridge { Slack = "slack", Line = "line" }] };
        using var provider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var controller = CreateController(
            Encoding.UTF8.GetBytes("{}"),
            queue,
            loggerFactory.CreateLogger<WebhookController>(),
            new TestHttpClientFactory(client),
            channels,
            lineChannels,
            bridges);
        var body = Encoding.UTF8.GetBytes("{\"type\":\"event_callback\",\"team_id\":\"team\",\"event\":{\"type\":\"message\",\"channel\":\"channel\",\"text\":\"" + bodyMarker + "\",\"user\":\"user-id\"}}");
        controller.Request.Body = new MemoryStream(body, writable: false);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        SetSlackHeaders(controller, timestamp, SlackSignature(body, timestamp));

        Assert.IsType<OkResult>(await controller.Slack2());

        var logs = string.Join("\n", provider.Messages);
        Assert.DoesNotContain(bodyMarker, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(nameMarker, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(responseMarker, logs, StringComparison.Ordinal);
        Assert.Contains("OK", logs, StringComparison.Ordinal);
    }

    // リンクラベルの有無と複数リンク、改行を含む本文の変換結果を確認する。
    [Fact]
    public async Task SlackLinkReplacementHandlesLabelsMultipleLinksAndNewlines()
    {
        const string text = "first <https://one.example/path|one>\nsecond <https://two.example/path>\nthird <https://three.example/path|three>";
        string? lineRequestJson = null;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("users.profile.get", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, "{\"profile\":{\"display_name\":\"Test User\",\"image_512\":\"https://private.example.test/icon\"}}");
            }

            lineRequestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(HttpStatusCode.OK, "ok");
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://line.example.test/") };
        var channels = new SlackChannels
        {
            Channels = [new SlackChannel { Name = "slack", TeamId = "team", ChannelId = "channel" }]
        };
        var lineChannels = new LineChannels { Channels = [new LineChannel { Name = "line", Id = "line-target" }] };
        var bridges = new SlackLineBridges
        {
            Bridges = [new global::SlackLineBridge.Models.Configurations.SlackLineBridge { Slack = "slack", Line = "line" }]
        };
        var queue = new LineWebhookQueue(4);
        var controller = CreateController(
            Encoding.UTF8.GetBytes("{}"),
            queue,
            clientFactory: new TestHttpClientFactory(client),
            slackChannels: channels,
            lineChannels: lineChannels,
            bridges: bridges);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "event_callback",
            team_id = "team",
            @event = new { type = "message", channel = "channel", text, user = "profile-user" }
        });
        controller.Request.Body = new MemoryStream(body, writable: false);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        SetSlackHeaders(controller, timestamp, SlackSignature(body, timestamp));

        Assert.IsType<OkResult>(await controller.Slack2());
        Assert.NotNull(lineRequestJson);
        using var lineRequest = JsonDocument.Parse(lineRequestJson!);
        var forwardedText = lineRequest.RootElement.GetProperty("messages")[0].GetProperty("text").GetString();
        Assert.Equal(
            "first https://one.example/path\nsecond https://two.example/path\nthird https://three.example/path",
            forwardedText);
    }

    private static WebhookController CreateController(
        byte[] body,
        LineWebhookQueue? queue = null,
        ILogger<WebhookController>? logger = null,
        IHttpClientFactory? clientFactory = null,
        SlackChannels? slackChannels = null,
        LineChannels? lineChannels = null,
        SlackLineBridges? bridges = null)
    {
        queue ??= new LineWebhookQueue(4);
        slackChannels ??= new SlackChannels { Channels = [] };
        lineChannels ??= new LineChannels { Channels = [] };
        bridges ??= new SlackLineBridges { Bridges = [] };
        clientFactory ??= new TestHttpClientFactory(new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")))));
        var service = new LineMessageProcessingService(
            new TestOptions<SlackChannels>(slackChannels),
            new TestOptions<LineChannels>(lineChannels),
            new TestOptions<SlackLineBridges>(bridges),
            clientFactory,
            queue,
            new LineChannelSecret(LineSecret),
            NullLogger<LineMessageProcessingService>.Instance);
        var options = new JsonSerializerOptions();
        options.EnableDynamicTypes();

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body, writable: false);
        context.Request.Host = new HostString("bridge.example");
        var controller = new WebhookController(
            logger ?? NullLogger<WebhookController>.Instance,
            new TestOptions<SlackChannels>(slackChannels),
            new TestOptions<LineChannels>(lineChannels),
            new TestOptions<SlackLineBridges>(bridges),
            service,
            queue,
            clientFactory,
            new LineChannelSecret(LineSecret),
            new SlackSigningSecret(SlackSecret),
            options);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static void SetSlackHeaders(WebhookController controller, string timestamp, string signature)
    {
        controller.Request.Headers["X-Slack-Request-Timestamp"] = timestamp;
        controller.Request.Headers["X-Slack-Signature"] = signature;
    }

    private static string SlackSignature(byte[] body, string timestamp)
    {
        var prefix = Encoding.UTF8.GetBytes($"v0:{timestamp}:");
        var signedBody = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, signedBody, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, signedBody, prefix.Length, body.Length);
        return "v0=" + Crypt.GetHMACHex(signedBody, SlackSecret);
    }

    private static string LineSignature(byte[] body) => Convert.ToBase64String(Crypt.CalcHMAC(body, LineSecret));

    private static async Task<LineWebhookRequest> ReadNextAsync(LineWebhookQueue queue)
    {
        await using var reader = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        return reader.Current;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class TestOptions<T>(T value) : IOptionsSnapshot<T>, IOptionsMonitor<T> where T : class
    {
        public T Value => value;
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public string[] Messages => _messages.ToArray();
        public ILogger CreateLogger(string categoryName) => new TestLogger(_messages);
        public void Dispose() { }

        private sealed class TestLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
