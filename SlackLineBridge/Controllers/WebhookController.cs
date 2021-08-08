using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Samples;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackLineBridge.Models;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Utils;
using static System.Text.Json.Serialization.Samples.JsonSerializerExtensions;

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
        private static readonly Regex _urlRegex = new Regex(@"(\<(?<url>http[^\|\>]+)\|?.*?\>)");
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly string _slackSigningSecret;

        public WebhookController(
            ILogger<WebhookController> logger,
            IOptionsSnapshot<SlackChannels> slackChannels,
            IOptionsSnapshot<LineChannels> lineChannels,
            IOptionsSnapshot<SlackLineBridges> bridges,
            ConcurrentQueue<(string signature, string body, string host)> lineRequestQueue,
            IHttpClientFactory clientFactory,
            SlackSigningSecret slackSigningSecret,
            JsonSerializerOptions jsonOptions)
        {
            _logger = logger;
            _slackChannels = slackChannels.Value;
            _lineChannels = lineChannels.Value;
            _bridges = bridges.Value;
            _clientFactory = clientFactory;
            _lineRequestQueue = lineRequestQueue;
            _slackSigningSecret = slackSigningSecret.Secret;
            _jsonOptions = jsonOptions;
        }

        [HttpPost("/slack2")]
        public async Task<IActionResult> Slack2()
        {
            //再送を無視する
            if (Request.Headers.ContainsKey("X-Slack-Retry-Reason") && Request.Headers["X-Slack-Retry-Reason"].First() == "http_timeout")
            {
                return Ok();
            }

            using var reader = new StreamReader(Request.Body);
            var json = await reader.ReadToEndAsync();
            _logger.LogInformation("Processing request from Slack: " + json);

            var timestampStr = Request.Headers["X-Slack-Request-Timestamp"].First();
            if (long.TryParse(timestampStr, out var timestamp))
                if (Math.Abs(DateTimeOffset.Now.ToUnixTimeSeconds() - timestamp) <= 60 * 5)
                {
                    var sigBaseStr = $"v0:{timestamp}:{json}";
                    var signature = $"v0={Crypt.GetHMACHex(sigBaseStr, _slackSigningSecret)}";
                    var slackSignature = Request.Headers["X-Slack-Signature"].First();

                    _logger.LogDebug($"Slack signature check (expected:{slackSignature}, calculated:{signature})");
                    if (signature == slackSignature)
                    {
                        // the request came from Slack!
                        dynamic data = JsonSerializer.Deserialize<dynamic>(json, _jsonOptions);
                        string type = data.type;
                        switch (type)
                        {
                            case "url_verification":
                                string challenge = data.challenge;
                                return Ok(challenge);
                            case "event_callback":
                                string eventType = data.@event.type;
                                switch (eventType)
                                {
                                    case "message":
                                        var slackChannels = _slackChannels.Channels;
                                        string teamId = data.team_id;
                                        string channelId = data.@event.channel;
                                        var slackChannel = slackChannels.FirstOrDefault(x => x.TeamId == teamId && x.ChannelId == channelId);
                                        if (slackChannel == null)
                                        {
                                            _logger.LogInformation($"message from unknown slack channel: {teamId}/{channelId}");
                                            return Ok();
                                        }

                                        string text = data.@event.text;
                                        string userId = data.@event.user;
                                        string userName = await GetSlackUserName(userId);

                                        JsonDynamicArray files = data.@event.files;
                                        SlackFile[] slackFiles = null;

                                        if (files?.Any() == true)
                                        {
                                            slackFiles = files.Cast<dynamic>().Select(x => new SlackFile
                                            {
                                                urlPrivate = x.url_private,
                                                thumb360 = x.thumb_360,
                                                mimeType = x.mimetype
                                            }).ToArray();
                                        }

                                        return await PushToLine(Request.Host.ToString(), slackChannel, userName, text, slackFiles);
                                }
                                break;
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Slack signature missmatch.");
                    }
                }

            return BadRequest();
        }

        private async Task<string> GetSlackUserName(string userId)
        {
            var client = _clientFactory.CreateClient("Slack");
            var result = await client.GetAsync($"https://slack.com/api/users.profile.get?user={userId}");
            var json = await result.Content.ReadAsStringAsync();
            dynamic data = JsonSerializer.Deserialize<dynamic>(json, _jsonOptions);
            string name = data.profile.display_name;

            return name;
        }

        [HttpPost("/slack")]
        public async Task<IActionResult> Slack([FromForm] SlackData data)
        {
            if (data.user_name == "slackbot") return Ok();

            var slackChannels = _slackChannels.Channels;

            var slackChannel = slackChannels.FirstOrDefault(x => x.Token == data.token && x.TeamId == data.team_id && x.ChannelId == data.channel_id);
            if (slackChannel == null)
            {
                _logger.LogInformation($"message from unknown slack channel: {data.team_id}/{data.channel_id} token={data.token}");
                return Ok();
            }

            return await PushToLine(Request.Host.ToString(), slackChannel, data.user_name, data.text);
        }

        private record SlackFile
        {
            public string urlPrivate { get; set; }
            public string thumb360 { get; set; }
            public string mimeType { get; set; }
        }

        private async Task<IActionResult> PushToLine(string host, SlackChannel slackChannel, string userName, string text, SlackFile[] files = null)
        {
            var bridges = GetBridges(slackChannel);
            if (!bridges.Any())
            {
                return Ok();
            }

            //URLタグを抽出
            var urls = _urlRegex.Matches(text);

            var client = _clientFactory.CreateClient("Line");
            foreach (var bridge in bridges)
            {
                var lineChannel = _lineChannels.Channels.FirstOrDefault(x => x.Name == bridge.Line);
                if (lineChannel == null)
                {
                    _logger.LogError($"bridge configured but cannot find target LineChannel: {bridge.Line}");
                    return Ok();
                }

                {
                    var message = new
                    {
                        type = "flex",
                        altText = $"{userName}\r\n「{text}」",
                        contents = new
                        {
                            type = "bubble",
                            size = "kilo",
                            body = new
                            {
                                type = "box",
                                layout = "vertical",
                                contents = new dynamic[]
                                {
                                    new
                                    {
                                        type = "text",
                                        text = userName,
                                        weight = "bold",
                                        wrap = true,
                                        size = "xs"
                                    },
                                    new
                                    {
                                        type = "separator",
                                        margin = "sm"
                                    },
                                    new
                                    {
                                        type = "text",
                                        text = text,
                                        wrap = true,
                                        margin = "sm"
                                    }
                                }
                            }
                        }
                    };
                    var urlMessages = urls.Select(x => x.Groups["url"].Value).Select(x => new
                    {
                        type = "text",
                        text = x
                    });

                    var json = new
                    {
                        to = lineChannel.Id,
                        messages = new dynamic[]
                        {
                            message
                        }.Concat(urlMessages).ToArray()
                    };
                    await client.PostAsync($"message/push", new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json"));
                }

                if (files != null)
                {
                    var messages = files.Where(x => x.mimeType.StartsWith("image")).Select(file =>
                    {
                        var urlPrivate = file.urlPrivate;
                        var urlThumb360 = file.thumb360;
                        return new
                        {
                            type = "image",
                            originalContentUrl = $"https://{host}/proxy/slack/{Crypt.GetHMACHex(urlPrivate, _slackSigningSecret)}/{HttpUtility.UrlEncode(urlPrivate)}",
                            previewImageUrl = $"https://{host}/proxy/slack/{Crypt.GetHMACHex(urlThumb360, _slackSigningSecret)}/{HttpUtility.UrlEncode(urlThumb360)}"
                        };
                    });
                    var json = new
                    {
                        to = lineChannel.Id,
                        messages = messages.ToArray()
                    };
                    var jsonStr = JsonSerializer.Serialize(json);
                    _logger.LogInformation("Push images to LINE: " + jsonStr);
                    await client.PostAsync($"message/push", new StringContent(jsonStr, Encoding.UTF8, "application/json"));
                }
            }
            return Ok();
        }

        [HttpPost("/line")]
        public async Task<IActionResult> LineAsync()
        {
            if (!Request.Headers.ContainsKey("X-Line-Signature"))
            {
                _logger.LogInformation("X-Line-Signature header missing.");

                return BadRequest();
            }

            string json = null;
            string host = null;
            try
            {
                using var reader = new StreamReader(Request.Body);
                json = await reader.ReadToEndAsync();
                host = Request.Host.ToString();
                return Ok();
            }
            finally
            {
                Response.OnCompleted(async () =>
                {
                    _lineRequestQueue.Enqueue((Request.Headers["X-Line-Signature"], json, host));
                });
            }

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
