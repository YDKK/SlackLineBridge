using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
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
            var bridges = _bridges.Bridges;

            var slackChannel = slackChannels.FirstOrDefault(x => x.Token == data.token && x.TeamId == data.team_id && x.ChannelId == data.channel_id);
            if (slackChannel == null)
            {
                _logger.LogInformation($"message from unknown slack channel: {data.team_id}/{data.channel_id} token={data.token}");
                return Ok();
            }

            var bridge = GetBridge(slackChannel);
            if (bridge == null)
            {
                return Ok();
            }

            var lineChannel = _lineChannels.Channels.FirstOrDefault(x => x.Name == bridge.Line);
            if (lineChannel == null)
            {
                _logger.LogError($"bridge configured but cannot find target LineChannel: {bridge.Line}");
                return Ok();
            }

            dynamic json = new ExpandoObject();
            json.to = lineChannel.Id;
            json.messages = new[]
            {
                new
                {
                    type = "text",
                    text = $"{data.user_name}\r\n「{data.text}」"
                }
            };
            var result = await _clientFactory.CreateClient("Line").PostAsync($"message/push", new StringContent(JsonConvert.SerializeObject(json), Encoding.UTF8, "application/json"));
            return Ok();
        }

        [HttpPost("/line")]
        public async Task<OkResult> Line()
        {
            // TODO: check signature
            using (var reader = new StreamReader(Request.Body))
            {
                var json = await reader.ReadToEndAsync();
                _logger.LogInformation("Receive request from line: " + json);
                var data = JsonConvert.DeserializeObject<dynamic>(json);

                foreach (var e in data.events)
                {
                    switch ((string)e.type)
                    {
                        case "message":
                            {
                                LineChannel lineChannel = GetLineChannel(e);
                                if (lineChannel == null)
                                {
                                    _logger.LogInformation($"message from unknown line channel: {GetLineEventSourceId(e)}");
                                    continue;
                                }

                                var bridge = GetBridge(lineChannel);
                                if (bridge == null)
                                {
                                    continue;
                                }
                                var slackChannel = _slackChannels.Channels.FirstOrDefault(x => x.Name == bridge.Slack);
                                if (slackChannel == null)
                                {
                                    _logger.LogError($"bridge configured but cannot find target slackChannel: {bridge.Slack}");
                                    continue;
                                }

                                string userName = null;
                                if (IsPropertyExist(e.source, "userId"))
                                {
                                    var userId = e.source.userId;
                                    var client = _clientFactory.CreateClient("Line");

                                    try
                                    {
                                        var result = await client.GetAsync($"profile/{userId}");
                                        if (result.IsSuccessStatusCode)
                                        {
                                            var profile = JsonConvert.DeserializeObject<dynamic>(await result.Content.ReadAsStringAsync());
                                            userName = profile.displayName;
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

                                string text;
                                switch ((string)e.message.type)
                                {
                                    case "text":
                                        text = e.message.text;
                                        break;
                                    default:
                                        text = $"<{e.message.type}>";
                                        break;
                                }

                                await SendToSlack(slackChannel.WebhookUrl, userName, text);
                            }
                            break;
                        default:
                            {
                                var sourceId = GetLineEventSourceId(e);
                                if (sourceId == null)
                                {
                                    _logger.LogInformation($"{e.type} event from sourceId: {e.source.type}");
                                    continue;
                                }
                            }
                            break;
                    }
                }
            }

            return Ok();
        }

        private static bool IsPropertyExist(dynamic data, string name)
        {
            if (data is ExpandoObject)
                return ((IDictionary<string, object>)data).ContainsKey(name);

            return data[name] != null;
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

            var json = JsonConvert.SerializeObject(message);
            _ = await client.PostAsync(webhookUrl, new StringContent(json.ToString(), Encoding.UTF8, "application/json"));
        }

        private string GetLineEventSourceId(dynamic e)
        {
            string sourceId = null;
            switch ((string)e.source.type)
            {
                case "user":
                    sourceId = e.source.userId;
                    break;
                case "group":
                    sourceId = e.source.groupId;
                    break;
                case "room":
                    sourceId = e.source.roomId;
                    break;
                default:
                    _logger.LogError($"unknown source type: {e.source.type}");
                    break;
            }
            return sourceId;
        }

        private LineChannel GetLineChannel(dynamic e)
        {
            var sourceId = GetLineEventSourceId(e);
            return _lineChannels.Channels.FirstOrDefault(x => x.Id == sourceId);
        }

        private Models.Configurations.SlackLineBridge GetBridge(LineChannel channel)
        {
            return _bridges.Bridges.FirstOrDefault(x => x.Line == channel.Name);
        }
        private Models.Configurations.SlackLineBridge GetBridge(SlackChannel channel)
        {
            return _bridges.Bridges.FirstOrDefault(x => x.Slack == channel.Name);
        }
    }
}
