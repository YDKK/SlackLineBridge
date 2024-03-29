﻿using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackLineBridge.Models;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Policy;
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
        private readonly ConcurrentQueue<(string signature, string body, string host)> _queue;
        private readonly string _lineChannelSecret;

        public LineMessageProcessingService(
            IOptionsMonitor<SlackChannels> slackChannels,
            IOptionsMonitor<LineChannels> lineChannels,
            IOptionsMonitor<SlackLineBridges> bridges,
            IHttpClientFactory clientFactory,
            ConcurrentQueue<(string signature, string body, string host)> lineRequestQueue,
            LineChannelSecret lineChannelSecret,
            ILogger<LineMessageProcessingService> logger)
        {
            _slackChannels = slackChannels;
            _lineChannels = lineChannels;
            _bridges = bridges;
            _clientFactory = clientFactory;
            _logger = logger;
            _queue = lineRequestQueue;
            _lineChannelSecret = lineChannelSecret.Secret;
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

                    var signature = Crypt.GetHMACBase64(request.body, _lineChannelSecret);
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
                                    var (userName, pictureUrl) = await GetLineProfileAsync(e);

                                    var message = e.GetProperty("message");
                                    var type = message.GetProperty("type").GetString();
                                    var text = "";
                                    string imageUrl = null;
                                    var id = "";
                                    if (message.TryGetProperty("id", out var idElement))
                                    {
                                        id = idElement.GetString();
                                    }

                                    switch (type)
                                    {
                                        case "text":
                                            text = message.GetProperty("text").GetString();
                                            break;
                                        case "sticker":
                                            var stickerId = message.GetProperty("stickerId").GetString();
                                            imageUrl = $"https://stickershop.line-scdn.net/stickershop/v1/sticker/{stickerId}/android/sticker.png";
                                            break;
                                        case "image":
                                            imageUrl = $"https://{request.host}/proxy/line/{Crypt.GetHMACHex(id, _lineChannelSecret)}/{id}";
                                            break;
                                        default:
                                            text = $"<{type}>";
                                            if (!string.IsNullOrEmpty(id))
                                            {
                                                text += $"\nhttps://{request.host}/proxy/line/{Crypt.GetHMACHex(id, _lineChannelSecret)}/{id}";
                                            }
                                            break;
                                    }

                                    foreach (var bridge in bridges)
                                    {
                                        var slackChannel = _slackChannels.CurrentValue.Channels.FirstOrDefault(x => x.Name == bridge.Slack);
                                        if (slackChannel == null)
                                        {
                                            _logger.LogError($"bridge configured but cannot find target slackChannel: {bridge.Slack}");
                                            continue;
                                        }

                                        await SendToSlack(slackChannel.WebhookUrl, slackChannel.ChannelId, pictureUrl, userName, text, imageUrl);
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

        private async Task SendToSlack(string webhookUrl, string channelId, string pictureUrl, string userName, string text, string imageUrl)
        {
            var client = _clientFactory.CreateClient();

            dynamic message = new ExpandoObject();
            message.channel = channelId;
            message.username = userName;
            message.text = text;
            if (string.IsNullOrEmpty(pictureUrl))
            {
                message.icon_emoji = ":line:";
            }
            else
            {
                message.icon_url = pictureUrl;
            }

            if (!string.IsNullOrEmpty(imageUrl))
            {
                message.blocks = new[]{new
                    {
                        type = "image",
                        image_url = imageUrl,
                        alt_text = "image"
                    } };
            }

            var result = await client.PostAsync(webhookUrl, new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>(message)), Encoding.UTF8, "application/json"));
            _logger.LogInformation($"Post to Slack: {result.StatusCode} {await result.Content.ReadAsStringAsync()}");
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

        private async Task<(string userName, string pictureUrl)> GetLineProfileAsync(JsonElement e)
        {
            var source = e.GetProperty("source");
            var type = source.GetProperty("type").GetString();
            var client = _clientFactory.CreateClient("Line");
            var resultProfile = (userName: "", pictureUrl: "");
            var userId = source.GetProperty("userId");
            HttpResponseMessage result = null;

            try
            {
                switch (type)
                {
                    case "user":
                        result = await client.GetAsync($"profile/{userId}");
                        break;
                    case "group":
                        var groupId = source.GetProperty("groupId");
                        result = await client.GetAsync($"group/{groupId}/member/{userId}");
                        break;
                    case "room":
                        var roomId = source.GetProperty("roomId");
                        result = await client.GetAsync($"room/{roomId}/member/{userId}");
                        break;
                    default:
                        _logger.LogError($"unknown source type: {type}");
                        break;
                }

                var profile = await JsonSerializer.DeserializeAsync<JsonElement>(await result.Content.ReadAsStreamAsync());
                _logger.LogInformation($"get profile: {profile}");

                resultProfile.userName = profile.GetProperty("displayName").GetString();
                if (profile.TryGetProperty("pictureUrl", out var picture))
                {
                    resultProfile.pictureUrl = picture.GetString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "get profile data failed");
            }

            if (string.IsNullOrEmpty(resultProfile.userName))
            {
                resultProfile.userName = $"Unknown ({userId})";
            }
            return resultProfile;
        }
    }
}
