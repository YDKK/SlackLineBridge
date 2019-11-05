using System;
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

        public WebhookController(
            ILogger<WebhookController> logger,
            IOptionsSnapshot<SlackChannels> slackChannels,
            IOptionsSnapshot<LineChannels> lineChannels,
            IOptionsSnapshot<SlackLineBridges> bridges,
            IHttpClientFactory clientFactory)
        {
            _logger = logger;
            _slackChannels = slackChannels.Value;
            _lineChannels = lineChannels.Value;
            _bridges = bridges.Value;
            _clientFactory = clientFactory;
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
        public async Task<OkResult> Line()
        {
            // TODO: check signature
            var data = await JsonSerializer.DeserializeAsync<JsonElement>(Request.Body);

            foreach (var e in data.GetProperty("events").EnumerateArray())
            {
                switch (e.GetProperty("type").GetString())
                {
                    case "message":
                        {
                            LineChannel lineChannel = GetLineChannel(e);
                            if (lineChannel == null)
                            {
                                _logger.LogInformation($"message from unknown line channel: {GetLineEventSourceId(e)}");
                                continue;
                            }

                            var bridges = GetBridges(lineChannel);
                            if (!bridges.Any())
                            {
                                continue;
                            }
                            string userName = null;
                            if (e.GetProperty("source").TryGetProperty("userId", out var userId))
                            {
                                var client = _clientFactory.CreateClient("Line");

                                try
                                {
                                    var result = await client.GetAsync($"profile/{userId}");
                                    if (result.IsSuccessStatusCode)
                                    {
                                        var profile = await JsonSerializer.DeserializeAsync<JsonElement>(await result.Content.ReadAsStreamAsync());
                                        userName = profile.GetProperty("displayName").GetString();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "get profile data failed");
                                }

                                if (userName == null)
                                {
                                    userName = $"Unknown ({userId})";
                                }
                            }
                            else
                            {
                                userName = "Unknown";
                            }

                            var message = e.GetProperty("message");
                            var type = message.GetProperty("type").GetString();
                            var text = type switch
                            {
                                "text" => message.GetProperty("text").GetString(),
                                _ => $"<{type}>",
                            };

                            foreach (var bridge in bridges)
                            {
                                var slackChannel = _slackChannels.Channels.FirstOrDefault(x => x.Name == bridge.Slack);
                                if (slackChannel == null)
                                {
                                    _logger.LogError($"bridge configured but cannot find target slackChannel: {bridge.Slack}");
                                    continue;
                                }

                                await SendToSlack(slackChannel.WebhookUrl, userName, text);
                            }
                        }
                        break;
                    default:
                        {
                            var sourceId = GetLineEventSourceId(e);
                            if (sourceId == null)
                            {
                                var type = e.GetProperty("type").GetString();
                                _logger.LogInformation($"{type} event from sourceId: {e.GetProperty("source").GetProperty("type").GetString()}");
                                continue;
                            }
                        }
                        break;
                }
            }

            _logger.LogInformation("Receive request from line: " + data.GetRawText());

            return Ok();
        }

        private async Task SendToSlack(string webhookUrl, string userName, string text)
        {
            var client = _clientFactory.CreateClient();

            var message = new
            {
                username = userName,
                icon_emoji = ":line:",
                text
            };

            _ = await client.PostAsync(webhookUrl, new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json"));
        }

        private string GetLineEventSourceId(JsonElement e)
        {
            var source = e.GetProperty("source");
            var type = source.GetProperty("type").GetString();
            switch (type)
            {
                case "user":
                    return source.GetProperty("userId").GetString();
                case "group":
                    return source.GetProperty("groupId").GetString();
                case "room":
                    return source.GetProperty("roomId").GetString();
                default:
                    _logger.LogError($"unknown source type: {type}");
                    return null;
            }
        }

        private LineChannel GetLineChannel(JsonElement e)
        {
            var sourceId = GetLineEventSourceId(e);
            return _lineChannels.Channels.FirstOrDefault(x => x.Id == sourceId);
        }

        private IEnumerable<Models.Configurations.SlackLineBridge> GetBridges(LineChannel channel)
        {
            return _bridges.Bridges.Where(x => x.Line == channel.Name);
        }
        private IEnumerable<Models.Configurations.SlackLineBridge> GetBridges(SlackChannel channel)
        {
            return _bridges.Bridges.Where(x => x.Slack == channel.Name);
        }
    }
}
