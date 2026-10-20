using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace SlackLineBridge.Services
{
    public sealed class SlackMediaHandler : DelegatingHandler
    {
        private const int MaximumRedirects = 5;
        private readonly string _slackAccessToken;

        public SlackMediaHandler(string slackAccessToken, HttpMessageHandler innerHandler = null)
        {
            if (string.IsNullOrWhiteSpace(slackAccessToken))
            {
                throw new ArgumentException("Slack access token must be configured.", nameof(slackAccessToken));
            }

            _slackAccessToken = slackAccessToken;
            if (innerHandler != null)
            {
                InnerHandler = innerHandler;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.Content != null || request.RequestUri == null || !request.RequestUri.IsAbsoluteUri)
            {
                throw new HttpRequestException("Slack media requests must use an absolute GET URL.");
            }

            var currentUri = request.RequestUri;
            var redirectsFollowed = 0;
            while (true)
            {
                using var outgoingRequest = CreateRequest(request, currentUri);
                var response = await base.SendAsync(outgoingRequest, cancellationToken);
                if (!IsRedirect(response.StatusCode) || response.Headers.Location == null)
                {
                    return response;
                }

                if (redirectsFollowed >= MaximumRedirects)
                {
                    response.Dispose();
                    throw new HttpRequestException("Slack media redirect limit was exceeded.");
                }

                if (!Uri.TryCreate(currentUri, response.Headers.Location, out var nextUri))
                {
                    response.Dispose();
                    throw new HttpRequestException("Slack media redirect URL is invalid.");
                }

                response.Dispose();
                currentUri = nextUri;
                redirectsFollowed++;
            }
        }

        private HttpRequestMessage CreateRequest(HttpRequestMessage original, Uri uri)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = original.Version,
                VersionPolicy = original.VersionPolicy
            };

            foreach (var header in original.Headers)
            {
                if (IsSafeToForward(header.Key))
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && uri.IdnHost.Equals("files.slack.com", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _slackAccessToken);
            }

            return request;
        }

        private static bool IsSafeToForward(string headerName)
        {
            return headerName.Equals("Accept", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("If-Range", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("Range", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("User-Agent", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            return statusCode is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect;
        }
    }
}
