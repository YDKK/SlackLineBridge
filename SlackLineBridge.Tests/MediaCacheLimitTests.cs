using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Services;
using Xunit;

namespace SlackLineBridge.Tests;

[CollectionDefinition("Media cache size limit tests", DisableParallelization = true)]
public sealed class MediaCacheLimitTestCollection
{
}

[Collection("Media cache size limit tests")]
public sealed class MediaCacheLimitTests
{
    private const long MaxObjectBytes = 4;
    private const string UpstreamUrl = "https://media.example.test/object";

    // 上限の既定値が256MiBで、キャッシュ有効時は正数が必須であることを確認する。
    [Fact]
    public void MaxObjectBytesDefaultsTo256MiBAndMustBePositiveWhenCacheEnabled()
    {
        Assert.Equal(256L * 1024 * 1024, new MediaCacheOptions().MaxObjectBytes);

        foreach (var invalidLimit in new[] { 0L, -1L })
        {
            var options = new MediaCacheOptions
            {
                Enabled = true,
                Region = "test-region",
                Bucket = "test-bucket",
                MaxObjectBytes = invalidLimit
            };

            Assert.Contains(options.GetValidationErrors(), error => error.Contains("MediaCache:MaxObjectBytes", StringComparison.Ordinal));
        }
    }

    // 宣言された本文サイズが上限を超える場合は読み取りや一時ファイル作成をせず502にすることを確認する。
    [Fact]
    public async Task DeclaredOversizeResponseIsRejectedBeforeReadingOrCreatingTempFile()
    {
        var store = new RecordingMediaCacheStore();
        var upstream = new ReadTrackingStream(new byte[] { 1, 2, 3, 4, 5 });
        var handler = new StaticHandler(() => CreateResponse(upstream, MaxObjectBytes + 1));
        using var client = new HttpClient(handler);
        var service = CreateService(store);
        var tempFilesBefore = GetTempFiles();

        var result = await service.GetAsync(
            client,
            UpstreamUrl,
            MediaCacheSource.Slack,
            UpstreamUrl,
            null,
            false,
            CancellationToken.None);

        try
        {
            Assert.Equal((int)HttpStatusCode.BadGateway, result.StatusCode);
            Assert.Null(result.Content);
            Assert.Equal(0, upstream.BytesRead);
            Assert.Equal(0, store.PutCount);
            AssertTempFilesUnchanged(tempFilesBefore);
        }
        finally
        {
            if (result.Content != null)
            {
                await result.Content.DisposeAsync();
            }
        }
    }

    // Content-Lengthがない場合や実サイズより小さい場合も上限+1 byteで読み取りを止めて502にすることを確認する。
    [Theory]
    [InlineData(null)]
    [InlineData(3L)]
    public async Task OversizedResponseWithMissingOrUnderreportedLengthIsRejectedAndCleaned(long? declaredLength)
    {
        var store = new RecordingMediaCacheStore();
        var upstream = new ReadTrackingStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var handler = new StaticHandler(() => CreateResponse(upstream, declaredLength));
        using var client = new HttpClient(handler);
        var service = CreateService(store);
        var tempFilesBefore = GetTempFiles();

        var result = await service.GetAsync(
            client,
            UpstreamUrl,
            MediaCacheSource.Slack,
            UpstreamUrl,
            null,
            false,
            CancellationToken.None);

        try
        {
            Assert.Equal((int)HttpStatusCode.BadGateway, result.StatusCode);
            Assert.Null(result.Content);
            Assert.Equal(MaxObjectBytes + 1, upstream.BytesRead);
            Assert.Equal(0, store.PutCount);
            AssertTempFilesUnchanged(tempFilesBefore);
        }
        finally
        {
            if (result.Content != null)
            {
                await result.Content.DisposeAsync();
            }
        }
    }

    // 実データが上限と同じ長さなら保存して成功応答を返すことを確認する。
    [Fact]
    public async Task ResponseAtExactLimitIsCachedAndReturnedSuccessfully()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        var store = new RecordingMediaCacheStore();
        var upstream = new ReadTrackingStream(body);
        var handler = new StaticHandler(() => CreateResponse(upstream, MaxObjectBytes));
        using var client = new HttpClient(handler);
        var service = CreateService(store);
        var tempFilesBefore = GetTempFiles();

        var result = await service.GetAsync(
            client,
            UpstreamUrl,
            MediaCacheSource.Slack,
            UpstreamUrl,
            null,
            false,
            CancellationToken.None);

        try
        {
            Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
            Assert.Equal((long?)MaxObjectBytes, result.ContentLength);
            Assert.Equal(body, store.StoredBody);
            Assert.Equal(body, await ReadAllAsync(result.Content));
        }
        finally
        {
            if (result.Content != null)
            {
                await result.Content.DisposeAsync();
            }
        }

        Assert.Equal(1, store.PutCount);
        AssertTempFilesUnchanged(tempFilesBefore);
    }

    private static MediaProxyService CreateService(RecordingMediaCacheStore store)
    {
        return new MediaProxyService(
            store,
            Options.Create(new MediaCacheOptions
            {
                Enabled = true,
                Region = "test-region",
                Bucket = "test-bucket",
                MaxObjectBytes = MaxObjectBytes
            }));
    }

    private static HttpResponseMessage CreateResponse(ReadTrackingStream stream, long? contentLength)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ReadTrackingContent(stream, contentLength)
        };
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static string[] GetTempFiles()
    {
        return Directory.GetFiles(Path.GetTempPath(), "slacklinebridge-mediacache-*.tmp");
    }

    private static void AssertTempFilesUnchanged(string[] before)
    {
        var after = GetTempFiles();
        Assert.True(before.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(after.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)));
    }

    private sealed class RecordingMediaCacheStore : IMediaCacheStore
    {
        public int PutCount { get; private set; }

        public byte[] StoredBody { get; private set; } = Array.Empty<byte>();

        public Task<MediaCacheObject> GetAsync(string key, string singleByteRange, CancellationToken cancellationToken)
        {
            return Task.FromResult<MediaCacheObject>(null!);
        }

        public async Task PutAsync(string key, Stream content, string contentType, long contentLength, CancellationToken cancellationToken)
        {
            PutCount++;
            await using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            StoredBody = copy.ToArray();
        }
    }

    private sealed class StaticHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory());
        }
    }

    private sealed class ReadTrackingContent : HttpContent
    {
        private readonly ReadTrackingStream _stream;
        private readonly long? _contentLength;

        public ReadTrackingContent(ReadTrackingStream stream, long? contentLength)
        {
            _stream = stream;
            _contentLength = contentLength;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            Headers.ContentLength = contentLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            return _stream.CopyToAsync(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_contentLength is long contentLength)
            {
                length = contentLength;
                return true;
            }

            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(_stream);
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream>(_stream);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ReadTrackingStream(byte[] body) : Stream
    {
        private int _position;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = Math.Min(count, body.Length - _position);
            Array.Copy(body, _position, buffer, offset, bytesRead);
            _position += bytesRead;
            BytesRead += bytesRead;
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            var bytesRead = Math.Min(buffer.Length, body.Length - _position);
            body.AsSpan(_position, bytesRead).CopyTo(buffer);
            _position += bytesRead;
            BytesRead += bytesRead;
            return bytesRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = Math.Min(buffer.Length, body.Length - _position);
            body.AsMemory(_position, bytesRead).CopyTo(buffer);
            _position += bytesRead;
            BytesRead += bytesRead;
            return ValueTask.FromResult(bytesRead);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
