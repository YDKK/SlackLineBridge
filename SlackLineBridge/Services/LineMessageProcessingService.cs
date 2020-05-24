using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackLineBridge.Models;
using SlackLineBridge.Models.Configurations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SlackLineBridge.Services
{
    public class LineMessageProcessingService : BackgroundService
    {
        private readonly IOptionsMonitor<SlackChannels> _slackChannels;
        private readonly IOptionsMonitor<LineChannels> _lineChannels;
        private readonly IOptionsMonitor<SlackLineBridges> _bridges;
        private readonly IHttpClientFactory _clientFactory;
        private readonly ILogger<LineMessageProcessingService> _logger;
        private readonly ConcurrentQueue<(string signature, string body)> _queue;
        private readonly string _lineChannelSecret;

        public LineMessageProcessingService(
            IOptionsMonitor<SlackChannels> slackChannels,
            IOptionsMonitor<LineChannels> lineChannels,
            IOptionsMonitor<SlackLineBridges> bridges,
            IHttpClientFactory clientFactory,
            ConcurrentQueue<(string signature, string body)> lineRequestQueue,
            string lineChannelSecret,
            ILogger<LineMessageProcessingService> logger)
        {
            _slackChannels = slackChannels;
            _lineChannels = lineChannels;
            _bridges = bridges;
            _clientFactory = clientFactory;
            _logger = logger;
            _queue = lineRequestQueue;
            _lineChannelSecret = lineChannelSecret;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogDebug($"LineMessageProcessingService is starting.");

            stoppingToken.Register(() => _logger.LogDebug($" LineMessageProcessing background task is stopping."));

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out var request))
                {
                    _logger.LogInformation("Processing request from line: " + request.body);

                    var signature = GetHMAC(request.body, _lineChannelSecret);
                    _logger.LogDebug($"LINE signature check (expected:{request.signature}, calculated:{signature})");
                    if (request.signature != signature)
                    {
                        _logger.LogInformation("LINE signature missmatch.");
                        continue;
                    }

                    var data = JsonSerializer.Deserialize<JsonElement>(request.body);

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
                                    string pictureUrl = null;
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
                                                if (profile.TryGetProperty("pictureUrl", out var picture))
                                                {
                                                    pictureUrl = picture.GetString();
                                                }
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

                                    string stickerUrl = null;
                                    if (type == "sticker")
                                    {
                                        var stickerId = message.GetProperty("stickerId").GetString();
                                        stickerUrl = $"https://stickershop.line-scdn.net/stickershop/v1/sticker/{stickerId}/android/sticker.png";
                                    }

                                    foreach (var bridge in bridges)
                                    {
                                        var slackChannel = _slackChannels.CurrentValue.Channels.FirstOrDefault(x => x.Name == bridge.Slack);
                                        if (slackChannel == null)
                                        {
                                            _logger.LogError($"bridge configured but cannot find target slackChannel: {bridge.Slack}");
                                            continue;
                                        }

                                        await SendToSlack(_logger, slackChannel.WebhookUrl, slackChannel.ChannelId, pictureUrl, userName, text, stickerUrl);
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
                }

                await Task.Delay(1000, stoppingToken);
            }

            _logger.LogDebug($"LineMessageProcessing background task is stopped.");
        }

        private async Task SendToSlack(ILogger logger, string webhookUrl, string channelId, string pictureUrl, string userName, string text, string stickerUrl)
        {
            var client = _clientFactory.CreateClient();

            object message;
            if (string.IsNullOrEmpty(stickerUrl))
            {
                message = new
                {
                    channel = channelId,
                    username = userName,
                    icon_emoji = !string.IsNullOrEmpty(pictureUrl) ? pictureUrl : ":line:",
                    text
                };
            }
            else
            {
                message = new
                {
                    channel = channelId,
                    username = userName,
                    icon_emoji = !string.IsNullOrEmpty(pictureUrl) ? pictureUrl : ":line:",
                    text,
                    blocks = new
                    {
                        type = "image",
                        image_url = stickerUrl,
                        alt_text = "sticker"
                    }
                };
            }

            var result = await client.PostAsync(webhookUrl, new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json"));
            logger.LogInformation($"Post to Slack: {result.StatusCode} {await result.Content.ReadAsStringAsync()}");
        }

        private LineChannel GetLineChannel(JsonElement e)
        {
            var sourceId = GetLineEventSourceId(e);
            return _lineChannels.CurrentValue.Channels.FirstOrDefault(x => x.Id == sourceId);
        }

        private IEnumerable<Models.Configurations.SlackLineBridge> GetBridges(LineChannel channel)
        {
            return _bridges.CurrentValue.Bridges.Where(x => x.Line == channel.Name);
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

        private static string GetHMAC(string text, string key)
        {
            var encoding = new UTF8Encoding();

            var textBytes = encoding.GetBytes(text);
            var keyBytes = encoding.GetBytes(key);

            byte[] hashBytes;

            using (var hash = new HMACSHA256(keyBytes))
            {
                hashBytes = hash.ComputeHash(textBytes);
            }

            return Convert.ToBase64String(hashBytes);
        }
    }
}
