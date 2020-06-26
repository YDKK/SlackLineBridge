using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackLineBridge.Models;
using SlackLineBridge.Models.Configurations;

namespace SlackLineBridge.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WebhookController : ControllerBase
    {
        private readonly ILogger<WebhookController> _logger;
        private readonly SlackChannels _slackChannels;
        private readonly LineChannels _lineChannels;
        private readonly SlackLineBridges _bridges;
        private readonly IHttpClientFactory _clientFactory;
        private readonly ConcurrentQueue<(string signature, string body, string host)> _lineRequestQueue;

        public WebhookController(
            ILogger<WebhookController> logger,
            IOptionsSnapshot<SlackChannels> slackChannels,
            IOptionsSnapshot<LineChannels> lineChannels,
            IOptionsSnapshot<SlackLineBridges> bridges,
            ConcurrentQueue<(string signature, string body, string host)> lineRequestQueue,
            IHttpClientFactory clientFactory)
        {
            _logger = logger;
            _slackChannels = slackChannels.Value;
            _lineChannels = lineChannels.Value;
            _bridges = bridges.Value;
            _clientFactory = clientFactory;
            _lineRequestQueue = lineRequestQueue;
        }

        [HttpPost("/slack")]
        public async Task<OkResult> Slack([FromForm]SlackData data)
        {
            if (data.user_name == "slackbot") return Ok();

            var slackChannels = _slackChannels.Channels;

            var slackChannel = slackChannels.FirstOrDefault(x => x.Token == data.token && x.TeamId == data.team_id && x.ChannelId == data.channel_id);
            if (slackChannel == null)
            {
                _logger.LogInformation($"message from unknown slack channel: {data.team_id}/{data.channel_id} token={data.token}");
                return Ok();
            }

            var bridges = GetBridges(slackChannel);
            if (!bridges.Any())
            {
                return Ok();
            }
            foreach (var bridge in bridges)
            {
                var lineChannel = _lineChannels.Channels.FirstOrDefault(x => x.Name == bridge.Line);
                if (lineChannel == null)
                {
                    _logger.LogError($"bridge configured but cannot find target LineChannel: {bridge.Line}");
                    return Ok();
                }

                var json = new
                {
                    to = lineChannel.Id,
                    messages = new[]
                    {
                        new
                        {
                            type = "text",
                            text = $"{data.user_name}\r\n「{data.text}」"
                        }
                    }
                };
                await _clientFactory.CreateClient("Line").PostAsync($"message/push", new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json"));
            }
            return Ok();
        }

        [HttpPost("/line")]
        public async Task<StatusCodeResult> Line()
        {
            if (!Request.Headers.ContainsKey("X-Line-Signature"))
            {
                _logger.LogInformation("X-Line-Signature header missing.");

                return BadRequest();
            }

            using var reader = new StreamReader(Request.Body);

            _lineRequestQueue.Enqueue((Request.Headers["X-Line-Signature"], await reader.ReadToEndAsync(), Request.Host.ToString()));

            return Ok();
        }

        [HttpGet("/health")]
        public OkResult Health()
        {
            return Ok();
        }


        private IEnumerable<Models.Configurations.SlackLineBridge> GetBridges(SlackChannel channel)
        {
            return _bridges.Bridges.Where(x => x.Slack == channel.Name);
        }
    }
}
