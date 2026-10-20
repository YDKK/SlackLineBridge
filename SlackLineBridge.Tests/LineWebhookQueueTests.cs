using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SlackLineBridge.Controllers;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Services;
using SlackLineBridge.Utils;
using Xunit;

namespace SlackLineBridge.Tests;

public sealed class LineWebhookQueueTests
{
    private const string LineSecret = "test-line-channel-secret";

    // 署名ヘッダー欠落または不正時に要求をキューへ登録しないことを確認する。
    [Fact]
    public async Task MissingAndInvalidSignaturesDoNotEnqueueRequests()
    {
        var queue = new LineWebhookQueue(4);
        var missing = CreateController(queue, "{}");
        var missingResult = await missing.LineAsync();
        Assert.IsType<BadRequestResult>(missingResult);

        var invalid = CreateController(queue, "{}", "not-a-valid-signature");
        var invalidResult = await invalid.LineAsync();
        Assert.IsType<BadRequestResult>(invalidResult);

        var marker = QueueRequest("marker", "test-host");
        Assert.True(queue.TryEnqueue(marker));
        Assert.Equal(marker, await ReadNextAsync(queue));
    }

    // 正しい署名の要求をキューへ登録してからHTTP 200を返すことを確認する。
    [Fact]
    public async Task ValidSignatureIsEnqueuedBeforeHttpOkIsReturned()
    {
        const string body = "{\"events\":[]}";
        var queue = new LineWebhookQueue(4);
        var controller = CreateController(queue, body, Crypt.GetHMACBase64(body, LineSecret));

        var result = await controller.LineAsync();

        Assert.IsType<OkResult>(result);
        var request = await ReadNextAsync(queue);
        Assert.Equal(body, Encoding.UTF8.GetString(request.Body));
        Assert.Equal("bridge.example", request.Host);
    }

    // LINE署名を本文の受信時バイト列そのもので検証することを確認する。
    [Fact]
    public async Task SignatureValidationUsesExactRawRequestBytes()
    {
        var json = Encoding.UTF8.GetBytes("{\"events\":[]}");
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray();
        var signature = Convert.ToBase64String(Crypt.CalcHMAC(body, LineSecret));
        var queue = new LineWebhookQueue(4);
        var controller = CreateController(queue, body, signature);

        var result = await controller.LineAsync();

        Assert.IsType<OkResult>(result);
        Assert.Equal(body, (await ReadNextAsync(queue)).Body);
    }

    // 本文読み取り中に要求が中断された場合はキューへ登録しないことを確認する。
    [Fact]
    public async Task AbortedRequestBodyReadDoesNotEnqueue()
    {
        var queue = new LineWebhookQueue(4);
        var body = Encoding.UTF8.GetBytes("{}");
        var controller = CreateController(queue, body, Convert.ToBase64String(Crypt.CalcHMAC(body, LineSecret)));
        var blockingBody = new CancellationReadStream();
        controller.HttpContext.Request.Body = blockingBody;
        using var aborted = new CancellationTokenSource();
        controller.HttpContext.RequestAborted = aborted.Token;

        var receiving = controller.LineAsync();
        await blockingBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        aborted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving);

        var marker = QueueRequest("marker", "test-host");
        Assert.True(queue.TryEnqueue(marker));
        Assert.Equal(marker, await ReadNextAsync(queue));
    }

    // LINE webhookの応答が外部プロフィール取得の完了を待たないことを確認する。
    [Fact]
    public async Task HttpAcknowledgementDoesNotWaitForExternalProfileRequest()
    {
        var profileStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishProfile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/profile/", StringComparison.Ordinal))
            {
                profileStarted.TrySetResult();
                await finishProfile.Task.WaitAsync(cancellationToken);
                return JsonResponse(HttpStatusCode.OK, "{\"displayName\":\"Test User\"}");
            }

            return JsonResponse(HttpStatusCode.OK, "ok");
        });
        using var client = CreateClient(handler);
        var factory = new TestHttpClientFactory(client);
        var queue = new LineWebhookQueue(4);
        var service = CreateProcessingService(queue, factory, NullLogger<LineMessageProcessingService>.Instance);
        await service.StartAsync(CancellationToken.None);

        var body = MessageEvent();
        var controller = CreateController(queue, body, Crypt.GetHMACBase64(body, LineSecret), factory, service);
        var result = await controller.LineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await profileStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<OkResult>(result);
        finishProfile.TrySetResult();
        await StopAsync(service);
    }

    // Kestrel経由で早期応答し、不正署名と満杯キューを拒否することを確認する。
    [Fact]
    public async Task KestrelPipelineAcknowledgesBeforeProcessingAndRejectsInvalidOrFullQueue()
    {
        var profileStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirstProfile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSlackPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slackBodies = new ConcurrentQueue<string>();
        var profileRequests = 0;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/profile/", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref profileRequests) == 1)
                {
                    profileStarted.TrySetResult();
                    await finishFirstProfile.Task.WaitAsync(cancellationToken);
                }
                return JsonResponse(HttpStatusCode.OK, "{\"displayName\":\"Test User\"}");
            }

            slackBodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (slackBodies.Count == 2)
            {
                secondSlackPost.TrySetResult();
            }
            return JsonResponse(HttpStatusCode.OK, "ok");
        });
        using var outboundClient = CreateClient(handler);
        var clientFactory = new TestHttpClientFactory(outboundClient);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(new LineWebhookQueue(1));
        builder.Services.AddSingleton(new LineChannelSecret(LineSecret));
        builder.Services.AddSingleton(new SlackSigningSecret("test-slack-signing-secret"));
        builder.Services.AddSingleton(new JsonSerializerOptions());
        builder.Services.AddSingleton<IHttpClientFactory>(clientFactory);
        builder.Services.Configure<SlackChannels>(options => options.Channels = CreateSlackChannels().Channels);
        builder.Services.Configure<LineChannels>(options => options.Channels = CreateLineChannels().Channels);
        builder.Services.Configure<SlackLineBridges>(options => options.Bridges = CreateBridges().Bridges);
        builder.Services.AddSingleton<LineMessageProcessingService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<LineMessageProcessingService>());
        builder.Services.AddControllers().AddApplicationPart(typeof(WebhookController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();

        try
        {
            var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses;
            var endpoint = addresses.Single(address => address.StartsWith("http://", StringComparison.Ordinal));
            using var httpClient = new HttpClient();

            var firstBody = MessageEvent("first-raw-body-marker");
            using var firstResponse = await PostWebhookAsync(httpClient, endpoint, firstBody, sign: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            await profileStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(finishFirstProfile.Task.IsCompleted);

            var queuedBody = MessageEvent("second-raw-body-marker");
            using var queuedResponse = await PostWebhookAsync(httpClient, endpoint, queuedBody, sign: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, queuedResponse.StatusCode);

            using var invalidResponse = await PostWebhookAsync(httpClient, endpoint, MessageEvent("invalid"), sign: false).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

            using var fullResponse = await PostWebhookAsync(httpClient, endpoint, MessageEvent("queue-full"), sign: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, fullResponse.StatusCode);

            finishFirstProfile.TrySetResult();
            await secondSlackPost.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await app.StopAsync();

            Assert.Contains(slackBodies, body => body.Contains("first-raw-body-marker", StringComparison.Ordinal));
            Assert.Contains(slackBodies, body => body.Contains("second-raw-body-marker", StringComparison.Ordinal));
            Assert.Equal(2, slackBodies.Count);
        }
        finally
        {
            finishFirstProfile.TrySetResult();
        }
    }

    // キュー内の連続した要求を待機遅延なく処理することを確認する。
    [Fact]
    public async Task ConsumerProcessesConsecutiveRequestsWithoutPollingDelay()
    {
        var secondSlackPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slackPosts = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/profile/", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"displayName\":\"Test User\"}"));
            }

            if (Interlocked.Increment(ref slackPosts) == 2)
            {
                secondSlackPost.TrySetResult();
            }
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "ok"));
        });
        using var client = CreateClient(handler);
        var queue = new LineWebhookQueue(4);
        Assert.True(queue.TryEnqueue(QueueRequest(MessageEvent("first"), "bridge.example")));
        Assert.True(queue.TryEnqueue(QueueRequest(MessageEvent("second"), "bridge.example")));
        var service = CreateProcessingService(queue, new TestHttpClientFactory(client), NullLogger<LineMessageProcessingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await secondSlackPost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StopAsync(service);

        Assert.Equal(2, slackPosts);
    }

    // 不正形式や処理失敗の後も次の要求を処理し、ログに秘密を含めないことを確認する。
    [Fact]
    public async Task MalformedAndFailedRequestsDoNotStopTheNextRequestAndLogsOmitSecrets()
    {
        var profileRequests = 0;
        var slackAttempts = 0;
        var successfulSlackPosts = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/profile/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref profileRequests);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"displayName\":\"Test User\"}"));
            }

            if (Interlocked.Increment(ref slackAttempts) == 1)
            {
                throw new HttpRequestException("sensitive webhook URL should not be logged");
            }

            Interlocked.Increment(ref successfulSlackPosts);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "ok"));
        });
        using var client = CreateClient(handler);
        var queue = new LineWebhookQueue(4);
        Assert.True(queue.TryEnqueue(QueueRequest("malformed-private-message", "private-host")));
        Assert.True(queue.TryEnqueue(QueueRequest(MessageEvent("private-message"), "private-host")));
        Assert.True(queue.TryEnqueue(QueueRequest(MessageEvent("private-message"), "private-host")));
        using var provider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var service = CreateProcessingService(queue, new TestHttpClientFactory(client), loggerFactory.CreateLogger<LineMessageProcessingService>());

        await service.StartAsync(CancellationToken.None);
        await StopAsync(service);

        Assert.Equal(2, profileRequests);
        Assert.Equal(2, slackAttempts);
        Assert.Equal(1, successfulSlackPosts);
        var logs = string.Join("\n", provider.Messages);
        Assert.DoesNotContain("malformed-private-message", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("private-message", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("private-host", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive webhook URL", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("slack-webhook-token", logs, StringComparison.Ordinal);
    }

    // LINEプロフィール取得失敗時に代替プロフィールを使って転送を続けることを確認する。
    [Fact]
    public async Task NonSuccessProfileResponseUsesFallbackAndStillForwardsMessage()
    {
        var slackPosts = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/profile/", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.BadGateway, "upstream error"));
            }

            Interlocked.Increment(ref slackPosts);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "ok"));
        });
        using var client = CreateClient(handler);
        var queue = new LineWebhookQueue(4);
        Assert.True(queue.TryEnqueue(QueueRequest(MessageEvent(), "bridge.example")));
        using var provider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var service = CreateProcessingService(queue, new TestHttpClientFactory(client), loggerFactory.CreateLogger<LineMessageProcessingService>());

        await service.StartAsync(CancellationToken.None);
        await StopAsync(service);

        Assert.Equal(1, slackPosts);
        Assert.Contains(provider.Messages, message => message.Contains("BadGateway", StringComparison.Ordinal));
    }

    // Slack送信失敗をログに記録し、後続要求の処理を続けることを確認する。
    [Fact]
    public async Task NonSuccessSlackResponseIsLoggedAsFailureAndNextRequestContinues()
    {
        var slackPosts = 0;
        var secondSlackPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/profile/", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"displayName\":\"Test User\"}"));
            }

            var count = Interlocked.Increment(ref slackPosts);
            if (count == 2)
            {
                secondSlackPost.TrySetResult();
            }
            return Task.FromResult(JsonResponse(count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, "ok"));
        });
        using var client = CreateClient(handler);
        var queue = new LineWebhookQueue(4);
        Assert.True(queue.TryEnqueue(QueueRequest(MessageEvent("first"), "bridge.example")));
        Assert.True(queue.TryEnqueue(QueueRequest(MessageEvent("second"), "bridge.example")));
        var service = CreateProcessingService(queue, new TestHttpClientFactory(client), loggerFactory.CreateLogger<LineMessageProcessingService>());

        await service.StartAsync(CancellationToken.None);
        await secondSlackPost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StopAsync(service);

        Assert.Equal(2, slackPosts);
        Assert.Contains(provider.Messages, message => message.Contains("ServiceUnavailable", StringComparison.Ordinal));
        Assert.Single(provider.Messages, message => message.Contains("Post to Slack returned status", StringComparison.Ordinal));
    }

    // キュー満杯時に待たずにHTTP 503を返すことを確認する。
    [Fact]
    public async Task FullQueueReturnsServiceUnavailableWithoutWaiting()
    {
        var queue = new LineWebhookQueue(1);
        var queued = QueueRequest("queued", "test-host");
        Assert.True(queue.TryEnqueue(queued));
        var body = "{\"events\":[]}";
        var controller = CreateController(queue, body, Crypt.GetHMACBase64(body, LineSecret));

        var result = await controller.LineAsync();

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        Assert.Equal(queued, await ReadNextAsync(queue));
    }

    // 正常停止時に受付済み要求を処理し、新しい要求を受け付けないことを確認する。
    [Fact]
    public async Task GracefulStopDrainsAcceptedRequestsAndClosesQueue()
    {
        var slackPosts = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/profile/", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"displayName\":\"Test User\"}"));
            }
            Interlocked.Increment(ref slackPosts);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "ok"));
        });
        using var client = CreateClient(handler);
        var queue = new LineWebhookQueue(4);
        Assert.True(queue.TryEnqueue(QueueRequest(MessageEvent(), "bridge.example")));
        var service = CreateProcessingService(queue, new TestHttpClientFactory(client), NullLogger<LineMessageProcessingService>.Instance);
        await service.StartAsync(CancellationToken.None);

        await StopAsync(service);

        Assert.Equal(1, slackPosts);
        Assert.False(queue.TryEnqueue(QueueRequest("after-stop", "bridge.example")));
    }

    // 停止期限に達したとき実行中の外部要求をキャンセルすることを確認する。
    [Fact]
    public async Task ShutdownDeadlineCancelsActiveExternalRequest()
    {
        var profileStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profileCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/profile/", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, "ok");
            }

            profileStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                profileCancelled.TrySetResult();
                throw;
            }
            return JsonResponse(HttpStatusCode.OK, "{\"displayName\":\"Test User\"}");
        });
        using var client = CreateClient(handler);
        var queue = new LineWebhookQueue(4);
        Assert.True(queue.TryEnqueue(QueueRequest(MessageEvent(), "bridge.example")));
        var service = CreateProcessingService(queue, new TestHttpClientFactory(client), NullLogger<LineMessageProcessingService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await profileStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var shutdownDeadline = new CancellationTokenSource();
        var stopping = service.StopAsync(shutdownDeadline.Token);
        shutdownDeadline.Cancel();
        await stopping;
        await profileCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (service.ExecuteTask is { } executeTask)
        {
            await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static WebhookController CreateController(
        LineWebhookQueue queue,
        string body,
        string? signature = null,
        TestHttpClientFactory? clientFactory = null,
        LineMessageProcessingService? processingService = null)
    {
        return CreateController(queue, Encoding.UTF8.GetBytes(body), signature, clientFactory, processingService);
    }

    private static WebhookController CreateController(
        LineWebhookQueue queue,
        byte[] body,
        string? signature,
        TestHttpClientFactory? clientFactory = null,
        LineMessageProcessingService? processingService = null)
    {
        var channels = CreateLineChannels();
        var bridges = CreateBridges();
        var slackChannels = CreateSlackChannels();
        var factory = clientFactory ?? new TestHttpClientFactory(CreateClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, "ok")))));
        processingService ??= CreateProcessingService(queue, factory, NullLogger<LineMessageProcessingService>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Body = new System.IO.MemoryStream(body, writable: false);
        context.Request.Host = new HostString("bridge.example");
        if (signature != null)
        {
            context.Request.Headers["X-Line-Signature"] = signature;
        }

        var controller = new WebhookController(
            NullLogger<WebhookController>.Instance,
            new TestOptions<SlackChannels>(slackChannels),
            new TestOptions<LineChannels>(channels),
            new TestOptions<SlackLineBridges>(bridges),
            processingService,
            queue,
            factory,
            new LineChannelSecret(LineSecret),
            new SlackSigningSecret("test-slack-signing-secret"),
            new JsonSerializerOptions());
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static LineMessageProcessingService CreateProcessingService(
        LineWebhookQueue queue,
        IHttpClientFactory clientFactory,
        ILogger<LineMessageProcessingService> logger)
    {
        return new LineMessageProcessingService(
            new TestOptions<SlackChannels>(CreateSlackChannels()),
            new TestOptions<LineChannels>(CreateLineChannels()),
            new TestOptions<SlackLineBridges>(CreateBridges()),
            clientFactory,
            queue,
            new LineChannelSecret(LineSecret),
            logger);
    }

    private static SlackChannels CreateSlackChannels() => new()
    {
        Channels =
        [
            new SlackChannel
            {
                Name = "slack-channel",
                ChannelId = "slack-channel-id",
                WebhookUrl = "https://hooks.slack.test/services/slack-webhook-token"
            }
        ]
    };

    private static LineChannels CreateLineChannels() => new()
    {
        Channels = [new LineChannel { Name = "line-channel", Id = "user-123" }]
    };

    private static SlackLineBridges CreateBridges() => new()
    {
        Bridges = [new global::SlackLineBridge.Models.Configurations.SlackLineBridge { Slack = "slack-channel", Line = "line-channel" }]
    };

    private static string MessageEvent(string marker = "hello") => JsonSerializer.Serialize(new
    {
        events = new[]
        {
            new
            {
                type = "message",
                replyToken = "reply-token",
                source = new { type = "user", userId = "user-123" },
                message = new { type = "text", id = "message-id", text = marker }
            }
        }
    });

    private static LineWebhookRequest QueueRequest(string body, string host) =>
        new(Encoding.UTF8.GetBytes(body), host);

    private static async Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, string endpoint, string body, bool sign)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/line")
        {
            Content = new ByteArrayContent(bodyBytes)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var signature = sign
            ? Convert.ToBase64String(Crypt.CalcHMAC(bodyBytes, LineSecret))
            : "not-a-valid-signature";
        request.Headers.TryAddWithoutValidation("X-Line-Signature", signature);
        return await client.SendAsync(request);
    }

    private static async Task<LineWebhookRequest> ReadNextAsync(LineWebhookQueue queue)
    {
        await using var reader = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        return reader.Current;
    }

    private static async Task StopAsync(LineMessageProcessingService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(timeout.Token);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.line.test/v2/bot/")
    };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestOptions<T>(T value) : IOptionsSnapshot<T>, IOptionsMonitor<T> where T : class
    {
        public T Value => value;
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return sendAsync(request, cancellationToken);
        }
    }

    private sealed class CancellationReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }
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
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
            }
        }
    }
}
