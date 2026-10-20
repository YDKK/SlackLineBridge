using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Services;
using SlackLineBridge.Utils;

namespace SlackLineBridge.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class ProxyController(
        ILogger<ProxyController> logger,
        IHttpClientFactory clientFactory,
        IMediaProxyService mediaProxyService,
        LineChannelSecret lineChannelSecret,
        SlackSigningSecret slackSigningSecret
        ) : ControllerBase
    {
        private readonly string _lineChannelSecret = lineChannelSecret.Secret;
        private readonly string _slackSigningSecret = slackSigningSecret.Secret;

        [HttpGet("line/{token}/{id}")]
        public async Task<IActionResult> Line(string id, string token)
        {
            if (token != Crypt.GetHMACHex(id, _lineChannelSecret))
            {
                return new StatusCodeResult((int)HttpStatusCode.Forbidden);
            }

            var client = clientFactory.CreateClient("Line");
            var url = $"https://api-data.line.me/v2/bot/message/{id}/content";

            return await ProxyContent(client, url, MediaCacheSource.Line, id);
        }

        [HttpGet("slack/{token}/{encodedUrl}")]
        public async Task<IActionResult> Slack(string encodedUrl, string token)
        {
            var url = HttpUtility.UrlDecode(encodedUrl);
            if (token != Crypt.GetHMACHex(url, _slackSigningSecret))
            {
                return new StatusCodeResult((int)HttpStatusCode.Forbidden);
            }

            var client = clientFactory.CreateClient("Slack");

            return await ProxyContent(client, url, MediaCacheSource.Slack, url);
        }

        private async Task<IActionResult> ProxyContent(HttpClient client, string url, MediaCacheSource source, string identity)
        {
            MediaProxyResponse result;
            try
            {
                result = await mediaProxyService.GetAsync(
                    client,
                    url,
                    source,
                    identity,
                    Request.Headers[HeaderNames.Range].ToString(),
                    Request.Headers.ContainsKey("If-Range"),
                    Request.HttpContext.RequestAborted);
            }
            catch (MediaCacheStorageException)
            {
                logger.LogError("Media cache storage operation failed.");
                return StatusCode((int)HttpStatusCode.ServiceUnavailable);
            }
            catch (HttpRequestException)
            {
                logger.LogWarning("Upstream media request failed.");
                return StatusCode((int)HttpStatusCode.BadGateway);
            }
            catch (IOException)
            {
                logger.LogWarning("Upstream media stream failed.");
                return StatusCode((int)HttpStatusCode.BadGateway);
            }
            catch (TaskCanceledException) when (!Request.HttpContext.RequestAborted.IsCancellationRequested)
            {
                logger.LogWarning("Upstream media request timed out.");
                return StatusCode((int)HttpStatusCode.GatewayTimeout);
            }

            Response.StatusCode = result.StatusCode;
            if (!result.EnableRangeProcessing && result.ContentLength.HasValue)
            {
                Response.ContentLength = result.ContentLength.Value;
            }

            if (!string.IsNullOrEmpty(result.ContentRange))
            {
                Response.Headers[HeaderNames.ContentRange] = result.ContentRange;
            }

            if (result.SupportsRangeRequests)
            {
                Response.Headers[HeaderNames.AcceptRanges] = "bytes";
            }

            if (result.Content == null)
            {
                return new StatusCodeResult(result.StatusCode);
            }

            return new FileStreamResult(result.Content, result.ContentType)
            {
                EnableRangeProcessing = result.EnableRangeProcessing
            };
        }
    }
}
