using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace SlackLineBridge.Services
{
    public sealed class S3MediaCacheStore(IAmazonS3 s3Client, string bucket) : IMediaCacheStore
    {
        public async Task<MediaCacheObject> GetAsync(string key, string singleByteRange, CancellationToken cancellationToken)
        {
            GetObjectResponse response;
            try
            {
                var request = new GetObjectRequest
                {
                    BucketName = bucket,
                    Key = key
                };
                if (!string.IsNullOrEmpty(singleByteRange))
                {
                    request.ByteRange = new ByteRange(singleByteRange);
                }

                response = await s3Client.GetObjectAsync(request, cancellationToken);
            }
            catch (AmazonS3Exception exception) when (string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal))
            {
                return null;
            }
            catch (AmazonS3Exception exception) when (IsRangeNotSatisfiable(exception))
            {
                try
                {
                    var metadata = await s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                    {
                        BucketName = bucket,
                        Key = key
                    }, cancellationToken);
                    throw new MediaCacheRangeNotSatisfiableException(metadata.ContentLength);
                }
                catch (MediaCacheRangeNotSatisfiableException)
                {
                    throw;
                }
                catch (OperationCanceledException metadataException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new MediaCacheStorageException("Media cache metadata read timed out.", metadataException);
                }
                catch (Exception metadataException) when (IsStorageFailure(metadataException))
                {
                    throw new MediaCacheStorageException("Media cache metadata read failed.", metadataException);
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MediaCacheStorageException("Media cache read timed out.", exception);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                throw new MediaCacheStorageException("Media cache read failed.", exception);
            }

            if (!string.IsNullOrEmpty(singleByteRange) && string.IsNullOrEmpty(response.ContentRange))
            {
                response.Dispose();
                throw new MediaCacheStorageException("The object store did not return the requested byte range.");
            }

            var contentType = string.IsNullOrWhiteSpace(response.Headers.ContentType)
                ? "application/octet-stream"
                : response.Headers.ContentType;
            return new MediaCacheObject(
                new OwningStream(response.ResponseStream, response),
                contentType,
                response.ContentLength,
                response.ContentRange);
        }

        public async Task PutAsync(string key, Stream content, string contentType, long contentLength, CancellationToken cancellationToken)
        {
            try
            {
                var request = new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = content,
                    ContentType = contentType,
                    AutoCloseStream = false,
                    AutoResetStreamPosition = false,
                    UseChunkEncoding = false
                };
                request.Headers.ContentLength = contentLength;
                await s3Client.PutObjectAsync(request, cancellationToken);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                throw new MediaCacheStorageException("Media cache write failed.", exception);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MediaCacheStorageException("Media cache write timed out.", exception);
            }
        }

        private static bool IsStorageFailure(Exception exception)
        {
            return exception is AmazonServiceException
                or AmazonClientException
                or HttpRequestException
                or IOException;
        }

        private static bool IsRangeNotSatisfiable(AmazonS3Exception exception)
        {
            return string.Equals(exception.ErrorCode, "InvalidRange", StringComparison.Ordinal)
                || exception.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable;
        }
    }

    internal sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    inner.Dispose();
                }
                finally
                {
                    owner.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
