using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Utils;

namespace SlackLineBridge.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class ProxyController : ControllerBase
    {
        private readonly ILogger<ProxyController> _logger;
        IHttpClientFactory _clientFactory;
        string _lineChannelSecret;
        string _slackSigningSecret;
        public ProxyController(
            ILogger<ProxyController> logger,
            IHttpClientFactory clientFactory,
            LineChannelSecret lineChannelSecret,
            SlackSigningSecret slackSigningSecret
        )
        {
            _logger = logger;
            _clientFactory = clientFactory;
            _lineChannelSecret = lineChannelSecret.Secret;
            _slackSigningSecret = slackSigningSecret.Secret;
        }

        [HttpGet("line/{token}/{id}")]
        public async Task<IActionResult> Line(string id, string token)
        {
            if (token != Crypt.GetHMACHex(id, _lineChannelSecret))
            {
                return new StatusCodeResult((int)HttpStatusCode.Forbidden);
            }

            var client = _clientFactory.CreateClient("Line");
            var url = $"https://api-data.line.me/v2/bot/message/{id}/content";

            return await ProxyContent(client, url);
        }

        [HttpGet("slack/{token}/{encodedUrl}")]
        public async Task<IActionResult> Slack(string encodedUrl, string token)
        {
            _logger.LogInformation($"Proxy request to Slack: {encodedUrl}, {token}");

            if (token != Crypt.GetHMACHex(encodedUrl, _slackSigningSecret))
            {
                return new StatusCodeResult((int)HttpStatusCode.Forbidden);
            }

            var client = _clientFactory.CreateClient("Slack");

            var url = HttpUtility.UrlDecode(encodedUrl);
            return await ProxyContent(client, url);
        }

        private static async Task<IActionResult> ProxyContent(HttpClient client, string url)
        {
            var result = await client.GetAsync(url);
            if (result.IsSuccessStatusCode)
            {
                var stream = await result.Content.ReadAsStreamAsync();
                var contentType = result.Content.Headers.GetValues("Content-Type").First();

                return new FileStreamResult(stream, contentType);
            }
            else
            {
                return new StatusCodeResult((int)result.StatusCode);
            }
        }
    }
}