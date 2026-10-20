using System;
using System.Collections.Generic;
using System.Linq;

namespace SlackLineBridge.Models.Configurations
{
    public sealed class MediaCacheOptions
    {
        public bool Enabled { get; set; }

        public string Endpoint { get; set; }

        public string Region { get; set; }

        public string Bucket { get; set; }

        public string KeyPrefix { get; set; } = "slack-line-bridge/media";

        public bool ForcePathStyle { get; set; }

        public string AccessKey { get; set; }

        public string SecretKey { get; set; }

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (!Enabled)
            {
                return errors;
            }

            if (string.IsNullOrWhiteSpace(Region))
            {
                errors.Add("MediaCache:Region is required when the media cache is enabled.");
            }

            if (string.IsNullOrWhiteSpace(Bucket))
            {
                errors.Add("MediaCache:Bucket is required when the media cache is enabled.");
            }
            else if (Bucket.Contains('/') || Bucket.Contains('\\') || Bucket.Any(char.IsWhiteSpace))
            {
                errors.Add("MediaCache:Bucket must be a bucket name without path separators or whitespace.");
            }

            if (!string.IsNullOrWhiteSpace(Endpoint)
                && (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
                    || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
                    || !string.IsNullOrEmpty(endpoint.UserInfo)
                    || !string.IsNullOrEmpty(endpoint.Query)
                    || !string.IsNullOrEmpty(endpoint.Fragment)))
            {
                errors.Add("MediaCache:Endpoint must be an HTTP or HTTPS base URL without user info, query, or fragment.");
            }

            if (string.IsNullOrWhiteSpace(AccessKey) != string.IsNullOrWhiteSpace(SecretKey))
            {
                errors.Add("MediaCache:AccessKey and MediaCache:SecretKey must be set together.");
            }

            if ((KeyPrefix ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
            {
                errors.Add("MediaCache:KeyPrefix cannot contain dot path segments.");
            }

            return errors;
        }
    }
}
