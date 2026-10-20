using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using SlackLineBridge.Models.Configurations;

namespace SlackLineBridge.Services
{
    public sealed class MediaProxyResponse(
        int statusCode,
        Stream content = null,
        string contentType = null,
        long? contentLength = null,
        string contentRange = null,
        bool enableRangeProcessing = false,
        bool supportsRangeRequests = false)
    {
        public int StatusCode { get; } = statusCode;
        public Stream Content { get; } = content;
        public string ContentType { get; } = contentType;
        public long? ContentLength { get; } = contentLength;
        public string ContentRange { get; } = contentRange;
        public bool EnableRangeProcessing { get; } = enableRangeProcessing;
        public bool SupportsRangeRequests { get; } = supportsRangeRequests;
    }

    public sealed class MediaProxyService : IMediaProxyService
    {
        private const int BufferSize = 81920;
        private const int LockStripeCount = 64;

        private readonly IMediaCacheStore _cacheStore;
        private readonly MediaCacheOptions _options;
        private readonly SemaphoreSlim[] _lockStripes;

        public MediaProxyService(IMediaCacheStore cacheStore, IOptions<MediaCacheOptions> options)
        {
            _cacheStore = cacheStore;
            _options = options.Value;
            _lockStripes = Enumerable.Range(0, LockStripeCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
        }

        public async Task<MediaProxyResponse> GetAsync(
            HttpClient client,
            string url,
            MediaCacheSource source,
            string identity,
            string rangeHeader,
            bool hasIfRange,
            CancellationToken cancellationToken)
        {
            if (!_options.Enabled)
            {
                return await GetFromUpstreamAsync(client, url, cancellationToken);
            }

            var key = MediaCacheKeys.Create(_options.KeyPrefix, source, identity);
            var requestedRange = hasIfRange ? null : GetSingleByteRange(rangeHeader);
            var enableMissRangeProcessing = !hasIfRange
                && (string.IsNullOrWhiteSpace(rangeHeader) || requestedRange != null);
            MediaCacheObject cached;
            try
            {
                cached = await _cacheStore.GetAsync(key, requestedRange, cancellationToken);
            }
            catch (MediaCacheRangeNotSatisfiableException exception)
            {
                return RangeNotSatisfiable(exception);
            }

            if (cached != null)
            {
                return FromCached(cached, requestedRange != null);
            }

            var stripe = GetLockStripe(key);
            await _lockStripes[stripe].WaitAsync(cancellationToken);
            try
            {
                try
                {
                    cached = await _cacheStore.GetAsync(key, requestedRange, cancellationToken);
                }
                catch (MediaCacheRangeNotSatisfiableException exception)
                {
                    return RangeNotSatisfiable(exception);
                }

                if (cached != null)
                {
                    return FromCached(cached, requestedRange != null);
                }

                return await FetchAndCacheAsync(client, url, key, enableMissRangeProcessing, cancellationToken);
            }
            finally
            {
                _lockStripes[stripe].Release();
            }
        }

        private async Task<MediaProxyResponse> GetFromUpstreamAsync(HttpClient client, string url, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null;
            try
            {
                response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new MediaProxyResponse((int)response.StatusCode);
                }

                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var contentType = GetContentType(response.Content.Headers.ContentType);
                var contentLength = response.Content.Headers.ContentLength;
                var statusCode = (int)response.StatusCode;
                var ownedStream = new OwningStream(stream, response);
                response = null;
                return new MediaProxyResponse(statusCode, ownedStream, contentType, contentLength);
            }
            finally
            {
                response?.Dispose();
            }
        }

        private async Task<MediaProxyResponse> FetchAndCacheAsync(
            HttpClient client,
            string url,
            string key,
            bool enableRangeProcessing,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null;
            FileStream tempFile = null;
            string tempPath = null;
            try
            {
                response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new MediaProxyResponse((int)response.StatusCode);
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var upstreamStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var contentType = GetContentType(response.Content.Headers.ContentType);
                    var contentLength = response.Content.Headers.ContentLength;
                    var statusCode = (int)response.StatusCode;
                    var passthrough = new OwningStream(upstreamStream, response);
                    response = null;
                    return new MediaProxyResponse(statusCode, passthrough, contentType, contentLength);
                }

                var responseContentType = GetContentType(response.Content.Headers.ContentType);
                tempPath = Path.Combine(Path.GetTempPath(), $"slacklinebridge-mediacache-{Guid.NewGuid():N}.tmp");
                try
                {
                    tempFile = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new MediaCacheStorageException("Could not create a media cache temporary file.", exception);
                }

                await using (var upstreamStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                {
                    await CopyToTempFileAsync(upstreamStream, tempFile, cancellationToken);
                }

                try
                {
                    await tempFile.FlushAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new MediaCacheStorageException("Could not flush a media cache temporary file.", exception);
                }

                var contentLengthOnDisk = tempFile.Length;
                tempFile.Position = 0;
                await _cacheStore.PutAsync(key, tempFile, responseContentType, contentLengthOnDisk, cancellationToken);

                tempFile.Position = 0;
                var resultStream = new OwningStream(tempFile, new DeleteFileOwner(tempPath));
                tempFile = null;
                tempPath = null;

                return new MediaProxyResponse(
                    (int)HttpStatusCode.OK,
                    resultStream,
                    responseContentType,
                    contentLengthOnDisk,
                    enableRangeProcessing: enableRangeProcessing,
                    supportsRangeRequests: true);
            }
            finally
            {
                response?.Dispose();
                if (tempFile != null)
                {
                    await tempFile.DisposeAsync();
                }

                if (tempPath != null)
                {
                    TryDeleteFile(tempPath);
                }
            }
        }

        private static MediaProxyResponse FromCached(MediaCacheObject cached, bool rangeRequested)
        {
            if (rangeRequested)
            {
                return new MediaProxyResponse(
                    (int)HttpStatusCode.PartialContent,
                    cached.Content,
                    cached.ContentType,
                    cached.ContentLength,
                    cached.ContentRange,
                    supportsRangeRequests: true);
            }

            return new MediaProxyResponse(
                (int)HttpStatusCode.OK,
                cached.Content,
                cached.ContentType,
                cached.ContentLength,
                supportsRangeRequests: true);
        }

        private static MediaProxyResponse RangeNotSatisfiable(MediaCacheRangeNotSatisfiableException exception)
        {
            return new MediaProxyResponse(
                (int)HttpStatusCode.RequestedRangeNotSatisfiable,
                contentLength: 0,
                contentRange: $"bytes */{exception.ContentLength}",
                supportsRangeRequests: true);
        }

        private static async Task CopyToTempFileAsync(Stream source, FileStream destination, CancellationToken cancellationToken)
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (bytesRead == 0)
                {
                    return;
                }

                try
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new MediaCacheStorageException("Could not write a media cache temporary file.", exception);
                }
            }
        }

        private static string GetSingleByteRange(string rangeHeader)
        {
            if (string.IsNullOrWhiteSpace(rangeHeader)
                || !RangeHeaderValue.TryParse(rangeHeader, out var parsed)
                || !string.Equals(parsed.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                || parsed.Ranges.Count != 1)
            {
                return null;
            }

            var range = parsed.Ranges.Single();
            if (range.From == null && range.To == null)
            {
                return null;
            }

            return $"bytes={range}";
        }

        private static int GetLockStripe(string key)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var stripe = BitConverter.ToUInt32(digest, 0) % LockStripeCount;
            return (int)stripe;
        }

        private static string GetContentType(MediaTypeHeaderValue contentType)
        {
            return string.IsNullOrWhiteSpace(contentType?.ToString())
                ? "application/octet-stream"
                : contentType.ToString();
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private sealed class DeleteFileOwner(string path) : IDisposable
        {
            public void Dispose() => TryDeleteFile(path);
        }
    }

    public interface IMediaProxyService
    {
        Task<MediaProxyResponse> GetAsync(
            HttpClient client,
            string url,
            MediaCacheSource source,
            string identity,
            string rangeHeader,
            bool hasIfRange,
            CancellationToken cancellationToken);
    }
}
