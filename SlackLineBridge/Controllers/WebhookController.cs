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
        private readonly IOptionsSnapshot<SlackChannels> _slackChannels;
        private readonly IOptionsSnapshot<LineChannels> _lineChannels;
        private readonly IOptionsSnapshot<SlackLineBridges> _bridges;
        private readonly IHttpClientFactory _clientFactory;

        public WebhookController(
            ILogger<WebhookController> logger,
            IOptionsSnapshot<SlackChannels> slackChannels,
            IOptionsSnapshot<LineChannels> lineChannels,
            IOptionsSnapshot<SlackLineBridges> bridges,
            IHttpClientFactory clientFactory)
        {
            _logger = logger;
            _slackChannels = slackChannels;
            _lineChannels = lineChannels;
            _bridges = bridges;
            _clientFactory = clientFactory;
        }

        [HttpPost]
        public async Task<OkResult> Slack(SlackData data)
        {
            if (data.UserName == "slackbot") return Ok();

            var slackChannels = _slackChannels.Value.Channels;
            var bridges = _bridges.Value.Bridges;

            var slackChannel = slackChannels.FirstOrDefault(x => x.Token == data.Token && x.TeamId == data.TeamId && x.ChannelId == data.ChannelId);
            if (slackChannel == null) return Ok();

            var bridge = GetBridge(slackChannel);
            if (bridge == null) return Ok();

            var lineChannel = _lineChannels.Value.Channels.FirstOrDefault(x => x.Name == bridge.Line);
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
                    text = $"{data.UserName}\r\n「{data.Text}」"
                }
            };
            var result = await _clientFactory.CreateClient("Line").PostAsync($"message/push", new StringContent(JsonConvert.SerializeObject(json), Encoding.UTF8, "application/json"));
            return Ok();
        }

        public async Task<OkResult> Line()
        {
            using (var reader = new StreamReader(Request.Body))
            {
                var data = JsonConvert.DeserializeObject<dynamic>(await reader.ReadToEndAsync());

                foreach (var e in data.events)
                {
                    switch (e.type)
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
                                var slackChannel = _slackChannels.Value.Channels.FirstOrDefault(x => x.Name == bridge.Slack);
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
                                switch (e.message.type)
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
        }

        private static bool IsPropertyExist(dynamic data, string name)
        {
            if (data is ExpandoObject)
                return ((IDictionary<string, object>)data).ContainsKey(name);

            return data.GetType().GetProperty(name) != null;
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
            switch (e.source.type)
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
            return _lineChannels.Value.Channels.FirstOrDefault(x => x.Id == sourceId);
        }

        private Models.Configurations.SlackLineBridge GetBridge(LineChannel channel)
        {
            return _bridges.Value.Bridges.FirstOrDefault(x => x.Line == channel.Name);
        }
        private Models.Configurations.SlackLineBridge GetBridge(SlackChannel channel)
        {
            return _bridges.Value.Bridges.FirstOrDefault(x => x.Slack == channel.Name);
        }
    }
}
