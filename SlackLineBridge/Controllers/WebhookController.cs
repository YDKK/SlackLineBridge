using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
using Microsoft.Extensions.Primitives;
using SlackLineBridge.Models;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Utils;
using static System.Text.Json.Serialization.Samples.JsonSerializerExtensions;

namespace SlackLineBridge.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public partial class WebhookController(
        ILogger<WebhookController> logger,
        IOptionsSnapshot<SlackChannels> slackChannels,
        IOptionsSnapshot<LineChannels> lineChannels,
        IOptionsSnapshot<SlackLineBridges> bridges,
        ConcurrentQueue<(string signature, string body, string host)> lineRequestQueue,
        IHttpClientFactory clientFactory,
        SlackSigningSecret slackSigningSecret,
        JsonSerializerOptions jsonOptions) : ControllerBase
    {
        private readonly SlackChannels _slackChannels = slackChannels.Value;
        private readonly LineChannels _lineChannels = lineChannels.Value;
        private readonly SlackLineBridges _bridges = bridges.Value;
        private readonly string _slackSigningSecret = slackSigningSecret.Secret;

        [HttpPost("/slack2")]
        public async Task<IActionResult> Slack2()
        {
            //再送を無視する
            if (Request.Headers.TryGetValue("X-Slack-Retry-Reason", out StringValues reason) && reason.First() == "http_timeout")
            {
                return Ok();
            }

            using var reader = new StreamReader(Request.Body);
            var json = await reader.ReadToEndAsync();
            logger.LogInformation("Processing request from Slack: {json}", json);

            var timestampStr = Request.Headers["X-Slack-Request-Timestamp"].First();
            if (long.TryParse(timestampStr, out var timestamp))
                if (Math.Abs(DateTimeOffset.Now.ToUnixTimeSeconds() - timestamp) <= 60 * 5)
                {
                    var sigBaseStr = $"v0:{timestamp}:{json}";
                    var signature = $"v0={Crypt.GetHMACHex(sigBaseStr, _slackSigningSecret)}";
                    var slackSignature = Request.Headers["X-Slack-Signature"].First();

                    logger.LogDebug("Slack signature check (expected:{slackSignature}, calculated:{signature})", slackSignature, signature);
                    if (signature == slackSignature)
                    {
                        // the request came from Slack!
                        dynamic data = JsonSerializer.Deserialize<dynamic>(json, jsonOptions);
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
                                        if (data.@event.subtype == "bot_message")
                                        {
                                            return Ok();
                                        }
                                        var slackChannels = _slackChannels.Channels;
                                        string teamId = data.team_id;
                                        string channelId = data.@event.channel;
                                        var slackChannel = slackChannels.FirstOrDefault(x => x.TeamId == teamId && x.ChannelId == channelId);
                                        if (slackChannel == null)
                                        {
                                            logger.LogInformation("message from unknown slack channel: {teamId}/{channelId}", teamId, channelId);
                                            return Ok();
                                        }

                                        string text = data.@event.text;
                                        string userId = data.@event.user;
                                        var (userName, icon) = await GetSlackUserNameAndIcon(userId);

                                        JsonDynamicArray files = data.@event.files;
                                        SlackFile[] slackFiles = null;

                                        if ((files?.Count ?? 0) > 0)
                                        {
                                            slackFiles = files.Cast<dynamic>().Select(x => new SlackFile
                                            {
                                                urlPrivate = x.url_private,
                                                thumb360 = x.thumb_360,
                                                mimeType = x.mimetype
                                            }).ToArray();
                                        }

                                        return await PushToLine(Request.Host.ToString(), slackChannel, icon, userName, text, slackFiles);
                                }
                                break;
                        }
                    }
                    else
                    {
                        logger.LogInformation("Slack signature missmatch.");
                    }
                }

            return BadRequest();
        }

        private async Task<(string userName, string icon)> GetSlackUserNameAndIcon(string userId)
        {
            var client = clientFactory.CreateClient("Slack");
            var result = await client.GetAsync($"https://slack.com/api/users.profile.get?user={userId}");
            var json = await result.Content.ReadAsStringAsync();
            dynamic data = JsonSerializer.Deserialize<dynamic>(json, jsonOptions);
            (string name, string icon) = (data.profile.display_name, data.profile.image_512);

            return (name, icon);
        }

        [SuppressMessage("Style", "IDE1006:命名スタイル", Justification = "<保留中>")]
        private record SlackFile
        {
            public string urlPrivate { get; set; }
            public string thumb360 { get; set; }
            public string mimeType { get; set; }
        }

        [GeneratedRegex(@"(\<(?<url>http[^\|\>]+)\|?.*?\>)")]
        private static partial Regex UrlRegex();

        private async Task<IActionResult> PushToLine(string host, SlackChannel slackChannel, string userIconUrl, string userName, string text, SlackFile[] files = null)
        {
            var bridges = GetBridges(slackChannel);
            if (!bridges.Any())
            {
                return Ok();
            }

            //URLタグを抽出
            var urls = UrlRegex().Matches(text);

            var client = clientFactory.CreateClient("Line");
            foreach (var bridge in bridges)
            {
                var lineChannel = _lineChannels.Channels.FirstOrDefault(x => x.Name == bridge.Line);
                if (lineChannel == null)
                {
                    logger.LogError("bridge configured but cannot find target LineChannel: {bridge.Line}", bridge.Line);
                    return Ok();
                }

                {
                    var message = new
                    {
                        type = "text",
                        altText = text,
                        text,
                        sender = new
                        {
                            name = userName,
                            iconUrl = $"https://{host}/proxy/slack/{Crypt.GetHMACHex(userIconUrl, _slackSigningSecret)}/{HttpUtility.UrlEncode(userIconUrl)}"
                        },
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
                    var jsonStr = JsonSerializer.Serialize(json);
                    logger.LogInformation("Push message to LINE: {jsonStr}", jsonStr);
                    var result = await client.PostAsync($"message/push", new StringContent(jsonStr, Encoding.UTF8, "application/json"));
                    logger.LogInformation("LINE API result [{result.StatusCode}]: {result.Content}", result.StatusCode, await result.Content.ReadAsStringAsync());
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
                            previewImageUrl = $"https://{host}/proxy/slack/{Crypt.GetHMACHex(urlThumb360, _slackSigningSecret)}/{HttpUtility.UrlEncode(urlThumb360)}",
                            sender = new
                            {
                                name = userName,
                                iconUrl = $"https://{host}/proxy/slack/{Crypt.GetHMACHex(userIconUrl, _slackSigningSecret)}/{HttpUtility.UrlEncode(userIconUrl)}"
                            },
                        };
                    });
                    var json = new
                    {
                        to = lineChannel.Id,
                        messages = messages.ToArray()
                    };
                    var jsonStr = JsonSerializer.Serialize(json);
                    logger.LogInformation("Push images to LINE: {jsonStr}", jsonStr);
                    var result = await client.PostAsync($"message/push", new StringContent(jsonStr, Encoding.UTF8, "application/json"));
                    logger.LogInformation("LINE API result [{result.StatusCode}]: {result.Content}", result.StatusCode, await result.Content.ReadAsStringAsync());
                }
            }
            return Ok();
        }

        [HttpPost("/line")]
        public async Task<IActionResult> LineAsync()
        {
            if (!Request.Headers.TryGetValue("X-Line-Signature", out StringValues signature))
            {
                logger.LogInformation("X-Line-Signature header missing.");

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
                Response.OnCompleted(() =>
                {
                    lineRequestQueue.Enqueue((signature, json, host));
                    return Task.CompletedTask;
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
