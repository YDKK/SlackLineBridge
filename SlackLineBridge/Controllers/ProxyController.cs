using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using SlackLineBridge.Services;

namespace SlackLineBridge.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class ProxyController : ControllerBase
    {
        IHttpClientFactory _clientFactory;
        string _lineChannelSecret;
        public ProxyController(IHttpClientFactory clientFactory, string lineChannelSecret)
        {
            _clientFactory = clientFactory;
            _lineChannelSecret = lineChannelSecret;
        }

        [HttpGet("line/{token}/{id}")]
        public async Task<IActionResult> Line(string id, string token)
        {
            if (token != LineMessageProcessingService.GetHMACHex(id, _lineChannelSecret))
            {
                return new StatusCodeResult((int)HttpStatusCode.Forbidden);
            }

            var client = _clientFactory.CreateClient("Line");

            var result = await client.GetAsync($"https://api-data.line.me/v2/bot/message/{id}/content");
            if (result.IsSuccessStatusCode)
            {
                using var stream = await result.Content.ReadAsStreamAsync();
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