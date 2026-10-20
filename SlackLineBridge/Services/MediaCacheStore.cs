using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlackLineBridge.Services
{
    public enum MediaCacheSource
    {
        Line,
        Slack
    }

    public sealed record MediaCacheObject(Stream Content, string ContentType, long ContentLength, string ContentRange = null);

    public interface IMediaCacheStore
    {
        Task<MediaCacheObject> GetAsync(string key, string singleByteRange, CancellationToken cancellationToken);

        Task PutAsync(string key, Stream content, string contentType, long contentLength, CancellationToken cancellationToken);
    }

    public sealed class MediaCacheStorageException : Exception
    {
        public MediaCacheStorageException(string message, Exception innerException = null)
            : base(message, innerException)
        {
        }
    }

    public sealed class MediaCacheRangeNotSatisfiableException(long contentLength) : Exception
    {
        public long ContentLength { get; } = contentLength;
    }

    public static class MediaCacheKeys
    {
        public static string Create(string keyPrefix, MediaCacheSource source, string identity)
        {
            var prefix = (keyPrefix ?? string.Empty).Trim('/');
            var provider = source.ToString().ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity ?? string.Empty))).ToLowerInvariant();
            return string.IsNullOrEmpty(prefix)
                ? $"{provider}/{hash}"
                : $"{prefix}/{provider}/{hash}";
        }
    }

    public sealed class NoOpMediaCacheStore : IMediaCacheStore
    {
        public Task<MediaCacheObject> GetAsync(string key, string singleByteRange, CancellationToken cancellationToken)
        {
            return Task.FromResult<MediaCacheObject>(null);
        }

        public Task PutAsync(string key, Stream content, string contentType, long contentLength, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
