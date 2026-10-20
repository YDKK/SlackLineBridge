using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Services;
using Xunit;

namespace SlackLineBridge.Tests;

public sealed class S3MediaCacheStoreTests
{
    // S3互換エンドポイントで署名付きの保存・取得・欠損・範囲取得が動作することを確認する。
    [Fact]
    public async Task AwsSdkV4UsesS3CompatibleWireOperationsForPutGetMissingAndRange()
    {
        await using var server = await S3Stub.StartAsync("private-bucket");
        using var client = MediaCacheS3ClientFactory.Create(new MediaCacheOptions
        {
            Enabled = true,
            Endpoint = server.Endpoint,
            Region = "ap-northeast-1",
            Bucket = "private-bucket",
            ForcePathStyle = true,
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key"
        });
        var store = new S3MediaCacheStore(client, "private-bucket");
        var key = "slack-line-bridge/media/slack/0123456789abcdef";
        var body = new byte[] { 1, 2, 3, 4, 5, 6 };

        Assert.Null(await store.GetAsync("missing-object", null, CancellationToken.None));

        await using (var source = new MemoryStream(body, writable: false))
        {
            await store.PutAsync(key, source, "video/mp4", body.Length, CancellationToken.None);
        }

        var full = Assert.IsType<MediaCacheObject>(await store.GetAsync(key, null, CancellationToken.None));
        Assert.Equal("video/mp4", full.ContentType);
        Assert.Equal(body.Length, full.ContentLength);
        Assert.Equal(body, await ReadAllAsync(full.Content));
        await full.Content.DisposeAsync();

        var range = Assert.IsType<MediaCacheObject>(await store.GetAsync(key, "bytes=1-3", CancellationToken.None));
        Assert.Equal("bytes 1-3/6", range.ContentRange);
        Assert.Equal(3, range.ContentLength);
        Assert.Equal(new byte[] { 2, 3, 4 }, await ReadAllAsync(range.Content));
        await range.Content.DisposeAsync();

        var openRange = Assert.IsType<MediaCacheObject>(await store.GetAsync(key, "bytes=2-", CancellationToken.None));
        Assert.Equal("bytes 2-5/6", openRange.ContentRange);
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, await ReadAllAsync(openRange.Content));
        await openRange.Content.DisposeAsync();

        var suffixRange = Assert.IsType<MediaCacheObject>(await store.GetAsync(key, "bytes=-2", CancellationToken.None));
        Assert.Equal("bytes 4-5/6", suffixRange.ContentRange);
        Assert.Equal(new byte[] { 5, 6 }, await ReadAllAsync(suffixRange.Content));
        await suffixRange.Content.DisposeAsync();

        var invalidRange = await Assert.ThrowsAsync<MediaCacheRangeNotSatisfiableException>(
            () => store.GetAsync(key, "bytes=99-100", CancellationToken.None));
        Assert.Equal(6, invalidRange.ContentLength);

        Assert.True(server.SawSignedRequest);
        Assert.EndsWith("/ap-northeast-1/s3/aws4_request", server.CredentialScope);
        Assert.Equal(body, server.Objects[key].Body);
        Assert.Equal("video/mp4", server.Objects[key].ContentType);
        Assert.False(server.SawOptionalChecksumHeaders);
    }

    // バケット不存在をキャッシュミスではなくストレージ障害として扱うことを確認する。
    [Fact]
    public async Task NoSuchBucketIsAStorageFailureAndNeverACacheMiss()
    {
        await using var server = await S3Stub.StartAsync("another-bucket");
        using var client = MediaCacheS3ClientFactory.Create(new MediaCacheOptions
        {
            Enabled = true,
            Endpoint = server.Endpoint,
            Region = "us-east-1",
            Bucket = "missing-bucket",
            ForcePathStyle = true,
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key"
        });
        var store = new S3MediaCacheStore(client, "missing-bucket");

        await Assert.ThrowsAsync<MediaCacheStorageException>(() => store.GetAsync("any-object", null, CancellationToken.None));
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private sealed class S3Stub : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _bucket;

        private S3Stub(WebApplication app, string bucket)
        {
            _app = app;
            _bucket = bucket;
        }

        public string Endpoint { get; private set; } = string.Empty;
        public ConcurrentDictionary<string, StubObject> Objects { get; } = new(StringComparer.Ordinal);
        public bool SawSignedRequest { get; private set; }
        public bool SawOptionalChecksumHeaders { get; private set; }
        public string CredentialScope { get; private set; } = string.Empty;

        public static async Task<S3Stub> StartAsync(string bucket)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var stub = new S3Stub(app, bucket);
            app.Run(stub.HandleAsync);
            await app.StartAsync();

            var addressesFeature = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
                ?? throw new InvalidOperationException("The S3 stub has no bound address.");
            var addresses = addressesFeature.Addresses;
            stub.Endpoint = addresses.Single(address => address.StartsWith("http://", StringComparison.Ordinal));
            return stub;
        }

        private async Task HandleAsync(HttpContext context)
        {
            var authorization = context.Request.Headers["Authorization"].ToString();
            SawSignedRequest |= authorization.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal);
            var credentialStart = authorization.IndexOf("Credential=", StringComparison.Ordinal);
            if (credentialStart >= 0)
            {
                var scopeStart = credentialStart + "Credential=".Length;
                var scopeEnd = authorization.IndexOf(',', scopeStart);
                var credential = scopeEnd >= 0
                    ? authorization[scopeStart..scopeEnd]
                    : authorization[scopeStart..];
                var credentialParts = credential.Split('/');
                if (credentialParts.Length == 5)
                {
                    CredentialScope = string.Join('/', credentialParts.Skip(1));
                }
            }

            if (context.Request.Headers.Keys.Any(header => header.StartsWith("x-amz-sdk-checksum-algorithm", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("x-amz-checksum-", StringComparison.OrdinalIgnoreCase)))
            {
                SawOptionalChecksumHeaders = true;
            }

            var path = Uri.UnescapeDataString(context.Request.Path.Value ?? string.Empty).Trim('/');
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || segments[0] != _bucket)
            {
                await WriteS3ErrorAsync(context, StatusCodes.Status404NotFound, "NoSuchBucket");
                return;
            }

            var key = string.Join('/', segments.Skip(1));
            if (HttpMethods.IsPut(context.Request.Method))
            {
                await using var data = new MemoryStream();
                await context.Request.Body.CopyToAsync(data, context.RequestAborted);
                Objects[key] = new StubObject(data.ToArray(), context.Request.ContentType ?? "application/octet-stream");
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentLength = 0;
                return;
            }

            if (HttpMethods.IsHead(context.Request.Method))
            {
                if (!Objects.TryGetValue(key, out var headObject))
                {
                    await WriteS3ErrorAsync(context, StatusCodes.Status404NotFound, "NoSuchKey");
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = headObject.ContentType;
                context.Response.ContentLength = headObject.Body.Length;
                return;
            }

            if (!HttpMethods.IsGet(context.Request.Method) || !Objects.TryGetValue(key, out var stored))
            {
                await WriteS3ErrorAsync(context, StatusCodes.Status404NotFound, "NoSuchKey");
                return;
            }

            var rangeHeader = context.Request.Headers["Range"].ToString();
            if (string.IsNullOrEmpty(rangeHeader))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = stored.ContentType;
                context.Response.ContentLength = stored.Body.Length;
                await context.Response.Body.WriteAsync(stored.Body, context.RequestAborted);
                return;
            }

            if (!TryGetRange(rangeHeader, stored.Body.Length, out var start, out var end))
            {
                await WriteS3ErrorAsync(context, StatusCodes.Status416RangeNotSatisfiable, "InvalidRange");
                return;
            }

            var rangeContent = stored.Body.AsMemory((int)start, checked((int)(end - start + 1)));
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentType = stored.ContentType;
            context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{stored.Body.Length}";
            context.Response.ContentLength = rangeContent.Length;
            await context.Response.Body.WriteAsync(rangeContent, context.RequestAborted);
        }

        private static bool TryGetRange(string value, int length, out long start, out long end)
        {
            start = 0;
            end = 0;
            if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var terms = value[6..].Split('-');
            if (terms.Length != 2)
            {
                return false;
            }

            if (terms[0].Length == 0)
            {
                if (!long.TryParse(terms[1], out var suffixLength) || suffixLength <= 0 || length == 0)
                {
                    return false;
                }

                start = Math.Max(0, length - suffixLength);
                end = length - 1;
                return true;
            }

            if (!long.TryParse(terms[0], out start) || start < 0 || start >= length)
            {
                return false;
            }

            if (terms[1].Length == 0)
            {
                end = length - 1;
                return true;
            }

            if (!long.TryParse(terms[1], out end) || end < start)
            {
                return false;
            }

            end = Math.Min(end, length - 1);
            return true;
        }

        private static async Task WriteS3ErrorAsync(HttpContext context, int statusCode, string code)
        {
            var body = Encoding.UTF8.GetBytes($"<Error><Code>{code}</Code><Message>Stubbed S3 response.</Message><RequestId>local</RequestId></Error>");
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/xml";
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        public sealed record StubObject(byte[] Body, string ContentType);
    }
}
