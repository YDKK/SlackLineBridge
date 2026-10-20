using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SlackLineBridge.Controllers;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Services;
using SlackLineBridge.Utils;
using Xunit;

namespace SlackLineBridge.Tests;

public sealed class MediaProxyServiceTests
{
    private const string UpstreamUrl = "https://media.example.test/object";

    // 初回取得でMIME typeを問わず保存し、2回目はキャッシュから返すことを確認する。
    [Fact]
    public async Task FirstRequestStoresEveryMimeTypeAndSecondRequestUsesCache()
    {
        var body = new byte[] { 0, 1, 2, 3, 4, 5 };
        var store = new FakeMediaCacheStore();
        var handler = new StaticHandler(_ => Task.FromResult(Response(body, "application/pdf")));
        using var client = new HttpClient(handler);
        var service = CreateService(store);

        var first = await service.GetAsync(client, UpstreamUrl, MediaCacheSource.Slack, UpstreamUrl, null, false, CancellationToken.None);
        Assert.Equal((int)HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("application/pdf", first.ContentType);
        Assert.Equal(body.Length, first.ContentLength);
        Assert.True(first.EnableRangeProcessing);
        Assert.Equal(body, await ReadAllAsync(first.Content));
        await first.Content.DisposeAsync();

        var second = await service.GetAsync(client, UpstreamUrl, MediaCacheSource.Slack, UpstreamUrl, null, false, CancellationToken.None);
        Assert.Equal((int)HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("application/pdf", second.ContentType);
        Assert.Equal(body, await ReadAllAsync(second.Content));
        await second.Content.DisposeAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, store.PutCount);
        Assert.Single(store.Keys.Distinct());
    }

    // 無効なLINE署名ではキャッシュや上流へアクセスしないことを確認する。
    [Fact]
    public async Task InvalidLineSignatureDoesNotAccessCacheOrUpstream()
    {
        var store = new FakeMediaCacheStore();
        var handler = new StaticHandler(_ => Task.FromResult(Response([1], "image/png")));
        using var client = new HttpClient(handler);
        var service = CreateService(store);
        var controller = new ProxyController(
            NullLogger<ProxyController>.Instance,
            new TestHttpClientFactory(client),
            service,
            new LineChannelSecret("line-secret"),
            new SlackSigningSecret("slack-secret"));

        var result = await controller.Line("message-id", "invalid-signature");

        Assert.Equal((int)HttpStatusCode.Forbidden, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal(0, store.GetCount);
        Assert.Equal(0, handler.RequestCount);
    }

    // キャッシュキーがproviderごとに分かれ、元の識別情報を含まないことを確認する。
    [Fact]
    public void CacheKeysSeparateProvidersAndDoNotContainIdentity()
    {
        const string identity = "https://media.example.test/private?token=secret";

        var lineKey = MediaCacheKeys.Create("custom/prefix/", MediaCacheSource.Line, identity);
        var slackKey = MediaCacheKeys.Create("custom/prefix/", MediaCacheSource.Slack, identity);

        Assert.NotEqual(lineKey, slackKey);
        Assert.StartsWith("custom/prefix/line/", lineKey);
        Assert.StartsWith("custom/prefix/slack/", slackKey);
        Assert.DoesNotContain(identity, lineKey);
        Assert.DoesNotContain("secret", slackKey);
        Assert.EndsWith(lineKey.Split('/').Last(), slackKey);
    }

    // キャッシュ無効時は設定を許容し、有効時は不正な設定を拒否することを確認する。
    [Fact]
    public void CacheConfigurationIsValidatedOnlyWhenEnabled()
    {
        Assert.Empty(new MediaCacheOptions { Enabled = false }.GetValidationErrors());

        var errors = new MediaCacheOptions { Enabled = true }.GetValidationErrors();

        Assert.Contains(errors, error => error.Contains("MediaCache:Region", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("MediaCache:Bucket", StringComparison.Ordinal));
    }

    // キャッシュmiss/hitのRange応答と通常hitの本文・ヘッダーが正しいことを確認する。
    [Fact]
    public async Task MvcReturnsCorrectRangeHeadersForMissAndHitAndFullContentOnUnrangedHit()
    {
        var body = new byte[] { 10, 11, 12, 13, 14, 15 };
        var store = new FakeMediaCacheStore();
        var handler = new StaticHandler(_ => Task.FromResult(Response(body, "video/mp4")));
        using var client = new HttpClient(handler);
        var service = CreateService(store);
        var mvcServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();

        var miss = await ExecuteLineAsync(mvcServices, client, service, "bytes=1-3");
        Assert.True(Assert.IsType<FileStreamResult>(miss.Result).EnableRangeProcessing);
        Assert.Equal("bytes=1-3", miss.Context.Request.Headers["Range"].ToString());
        Assert.Equal((int)HttpStatusCode.PartialContent, miss.Context.Response.StatusCode);
        Assert.Equal("bytes 1-3/6", miss.Context.Response.Headers["Content-Range"].ToString());
        Assert.Equal("bytes", miss.Context.Response.Headers["Accept-Ranges"].ToString());
        Assert.Equal(3, miss.Context.Response.ContentLength);
        Assert.Equal(new byte[] { 11, 12, 13 }, ((MemoryStream)miss.Context.Response.Body).ToArray());

        var hit = await ExecuteLineAsync(mvcServices, client, service, "bytes=1-3");
        Assert.Equal((int)HttpStatusCode.PartialContent, hit.Context.Response.StatusCode);
        Assert.Equal("bytes 1-3/6", hit.Context.Response.Headers["Content-Range"].ToString());
        Assert.Equal("bytes", hit.Context.Response.Headers["Accept-Ranges"].ToString());
        Assert.Equal(3, hit.Context.Response.ContentLength);
        Assert.Equal(new byte[] { 11, 12, 13 }, ((MemoryStream)hit.Context.Response.Body).ToArray());

        var fullHit = await ExecuteLineAsync(mvcServices, client, service, null);
        Assert.Equal((int)HttpStatusCode.OK, fullHit.Context.Response.StatusCode);
        Assert.Equal(6, fullHit.Context.Response.ContentLength);
        Assert.Equal("bytes", fullHit.Context.Response.Headers["Accept-Ranges"].ToString());
        Assert.False(fullHit.Context.Response.Headers.ContainsKey("Content-Range"));
        Assert.Equal(body, ((MemoryStream)fullHit.Context.Response.Body).ToArray());

        var unsatisfied = await ExecuteLineAsync(mvcServices, client, service, "bytes=99-100");
        Assert.Equal((int)HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfied.Context.Response.StatusCode);
        Assert.Equal("bytes */6", unsatisfied.Context.Response.Headers["Content-Range"].ToString());
        Assert.Equal("bytes", unsatisfied.Context.Response.Headers["Accept-Ranges"].ToString());
        Assert.Equal(0, unsatisfied.Context.Response.ContentLength);
        Assert.Equal(1, handler.RequestCount);

        mvcServices.Dispose();
    }

    // キャッシュhitで単一byte rangeの内容と応答が正しいことを確認する。
    [Fact]
    public async Task CacheHitUsesSingleByteRangeFromS3Object()
    {
        var body = new byte[] { 10, 11, 12, 13, 14, 15 };
        var store = new FakeMediaCacheStore();
        var handler = new StaticHandler(_ => Task.FromResult(Response(body, "video/mp4")));
        using var client = new HttpClient(handler);
        var service = CreateService(store);

        var miss = await service.GetAsync(client, UpstreamUrl, MediaCacheSource.Line, "line-id", "bytes=1-3", false, CancellationToken.None);
        Assert.Equal(body, await ReadAllAsync(miss.Content));
        await miss.Content.DisposeAsync();

        var hit = await service.GetAsync(client, UpstreamUrl, MediaCacheSource.Line, "line-id", "bytes=1-3", false, CancellationToken.None);
        Assert.Equal((int)HttpStatusCode.PartialContent, hit.StatusCode);
        Assert.Equal("bytes 1-3/6", hit.ContentRange);
        Assert.Equal(3, hit.ContentLength);
        Assert.Equal(new byte[] { 11, 12, 13 }, await ReadAllAsync(hit.Content));
        await hit.Content.DisposeAsync();
        Assert.Equal(1, handler.RequestCount);
    }

    // S3保存失敗時に一時ファイルを削除し、成功応答を返さないことを確認する。
    [Fact]
    public async Task FailedPutDeletesTemporaryFileAndDoesNotReturnSuccess()
    {
        var store = new FakeMediaCacheStore { FailPut = true };
        var handler = new StaticHandler(_ => Task.FromResult(Response([1, 2, 3], "application/octet-stream")));
        using var client = new HttpClient(handler);
        var service = CreateService(store);
        var before = GetTempFiles();

        await Assert.ThrowsAsync<MediaCacheStorageException>(() => service.GetAsync(
            client, UpstreamUrl, MediaCacheSource.Slack, UpstreamUrl, null, false, CancellationToken.None));

        Assert.Equal(before, GetTempFiles());
        Assert.Equal(0, store.StoredObjectCount);
    }

    // 上流読み取りのキャンセル時に一時ファイルを削除することを確認する。
    [Fact]
    public async Task AbortedUpstreamReadDeletesTemporaryFile()
    {
        var store = new FakeMediaCacheStore();
        var blockingStream = new BlockingReadStream();
        var handler = new StaticHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(blockingStream)
        }));
        using var client = new HttpClient(handler);
        var service = CreateService(store);
        var before = GetTempFiles();
        using var cancellation = new CancellationTokenSource();

        var request = service.GetAsync(client, UpstreamUrl, MediaCacheSource.Line, "line-id", null, false, cancellation.Token);
        await blockingStream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(before, GetTempFiles());
        Assert.Equal(0, store.StoredObjectCount);
    }

    // 上流エラーのstatus codeを保持し、エラー本文をキャッシュしないことを確認する。
    [Fact]
    public async Task UpstreamErrorStatusIsKeptAndErrorBodyIsNotCached()
    {
        var store = new FakeMediaCacheStore();
        var handler = new StaticHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("private upstream error")
        }));
        using var client = new HttpClient(handler);
        var service = CreateService(store);

        var response = await service.GetAsync(client, UpstreamUrl, MediaCacheSource.Line, "line-id", null, false, CancellationToken.None);

        Assert.Equal((int)HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Content);
        Assert.Equal(0, store.PutCount);
    }

    // キャッシュ無効時にストレージへアクセスせず上流から取得することを確認する。
    [Fact]
    public async Task DisabledCacheDoesNotAccessStorage()
    {
        var store = new FakeMediaCacheStore();
        var body = new byte[] { 4, 5, 6 };
        var handler = new StaticHandler(_ => Task.FromResult(Response(body, "image/jpeg")));
        using var client = new HttpClient(handler);
        var service = CreateService(store, enabled: false);

        var response = await service.GetAsync(client, UpstreamUrl, MediaCacheSource.Line, "line-id", null, false, CancellationToken.None);

        Assert.Equal(body, await ReadAllAsync(response.Content));
        await response.Content.DisposeAsync();
        Assert.Equal(0, store.GetCount);
        Assert.Equal(0, store.PutCount);
        Assert.Equal(1, handler.RequestCount);
    }

    // ストレージ障害時に503を返し、上流へアクセスしないことを確認する。
    [Fact]
    public async Task StorageFailureReturns503WithoutCallingUpstream()
    {
        var store = new FakeMediaCacheStore { FailGet = true };
        var handler = new StaticHandler(_ => Task.FromResult(Response([1], "image/png")));
        using var client = new HttpClient(handler);
        var service = CreateService(store);
        var mvcServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();

        var response = await ExecuteLineAsync(mvcServices, client, service, null);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, response.Context.Response.StatusCode);
        Assert.Equal(0, handler.RequestCount);
        mvcServices.Dispose();
    }

    // 同じオブジェクトへの並行missで上流取得と保存を一度だけ行うことを確認する。
    [Fact]
    public async Task ConcurrentMissesForSameObjectOnlyFetchAndStoreOnce()
    {
        var store = new FakeMediaCacheStore();
        var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFetchToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StaticHandler(async _ =>
        {
            fetchStarted.TrySetResult();
            await allowFetchToFinish.Task;
            return Response([7, 8, 9], "application/pdf");
        });
        using var client = new HttpClient(handler);
        var service = CreateService(store);

        var first = service.GetAsync(client, UpstreamUrl, MediaCacheSource.Slack, UpstreamUrl, null, false, CancellationToken.None);
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.GetAsync(client, UpstreamUrl, MediaCacheSource.Slack, UpstreamUrl, null, false, CancellationToken.None);
        allowFetchToFinish.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, store.PutCount);
        foreach (var result in results)
        {
            Assert.Equal(new byte[] { 7, 8, 9 }, await ReadAllAsync(result.Content));
            await result.Content.DisposeAsync();
        }
    }

    private static MediaProxyService CreateService(FakeMediaCacheStore store, bool enabled = true)
    {
        return new MediaProxyService(store, Options.Create(new MediaCacheOptions
        {
            Enabled = enabled,
            KeyPrefix = "test-cache"
        }));
    }

    private static HttpResponseMessage Response(byte[] body, string contentType)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new MediaTypeHeaderValue(contentType) }
            }
        };
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static string[] GetTempFiles()
    {
        return Directory.GetFiles(Path.GetTempPath(), "slacklinebridge-mediacache-*.tmp").OrderBy(path => path).ToArray();
    }

    private static async Task<(IActionResult Result, DefaultHttpContext Context)> ExecuteLineAsync(
        IServiceProvider services,
        HttpClient client,
        IMediaProxyService mediaProxyService,
        string? range)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
        context.Request.Method = "GET";
        if (range != null)
        {
            context.Request.Headers["Range"] = range;
        }

        var controller = new ProxyController(
            NullLogger<ProxyController>.Instance,
            new TestHttpClientFactory(client),
            mediaProxyService,
            new LineChannelSecret("line-secret"),
            new SlackSigningSecret("slack-secret"));
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var token = Crypt.GetHMACHex("line-id", "line-secret");
        var result = await controller.Line("line-id", token);
        var actionContext = new ActionContext(context, new RouteData(), new ActionDescriptor());
        await result.ExecuteResultAsync(actionContext);
        return (result, context);
    }

    private sealed class FakeMediaCacheStore : IMediaCacheStore
    {
        private readonly ConcurrentDictionary<string, StoredMedia> _items = new();

        public int GetCount { get; private set; }
        public int PutCount { get; private set; }
        public bool FailPut { get; init; }
        public bool FailGet { get; init; }
        public int StoredObjectCount => _items.Count;
        public System.Collections.Generic.List<string> Keys { get; } = [];

        public Task<MediaCacheObject> GetAsync(string key, string singleByteRange, CancellationToken cancellationToken)
        {
            GetCount++;
            Keys.Add(key);
            cancellationToken.ThrowIfCancellationRequested();
            if (FailGet)
            {
                throw new MediaCacheStorageException("Fake read failure.");
            }

            if (!_items.TryGetValue(key, out var stored))
            {
                return Task.FromResult<MediaCacheObject>(null!);
            }

            if (singleByteRange == null)
            {
                return Task.FromResult(new MediaCacheObject(new MemoryStream(stored.Body, writable: false), stored.ContentType, stored.Body.Length));
            }

            var interval = singleByteRange["bytes=".Length..].Split('-');
            long from;
            long to;
            if (interval[0].Length == 0)
            {
                var suffixLength = long.Parse(interval[1]);
                from = Math.Max(0, stored.Body.Length - suffixLength);
                to = stored.Body.Length - 1;
            }
            else
            {
                from = long.Parse(interval[0]);
                to = interval[1].Length == 0
                    ? stored.Body.Length - 1
                    : Math.Min(long.Parse(interval[1]), stored.Body.Length - 1);
            }

            if (from >= stored.Body.Length)
            {
                throw new MediaCacheRangeNotSatisfiableException(stored.Body.Length);
            }

            var length = checked((int)(to - from + 1));
            var body = stored.Body.Skip((int)from).Take(length).ToArray();
            return Task.FromResult(new MediaCacheObject(
                new MemoryStream(body, writable: false),
                stored.ContentType,
                body.Length,
                $"bytes {from}-{to}/{stored.Body.Length}"));
        }

        public async Task PutAsync(string key, Stream content, string contentType, long contentLength, CancellationToken cancellationToken)
        {
            PutCount++;
            if (FailPut)
            {
                throw new MediaCacheStorageException("Fake write failure.");
            }

            await using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            _items[key] = new StoredMedia(copy.ToArray(), contentType);
        }

        private sealed record StoredMedia(byte[] Body, string ContentType);
    }

    private sealed class StaticHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return responseFactory(request);
        }
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadStarted => _readStarted;
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
            _readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }
    }
}
