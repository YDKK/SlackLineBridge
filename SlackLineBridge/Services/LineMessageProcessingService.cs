using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
        private readonly LineWebhookQueue _queue;
        private readonly string _lineChannelSecret;
        // key: LINE channel id, value: last reply token
        private readonly ConcurrentDictionary<string, string> _lastReplyTokens = [];

        public LineMessageProcessingService(
            IOptionsMonitor<SlackChannels> slackChannels,
            IOptionsMonitor<LineChannels> lineChannels,
            IOptionsMonitor<SlackLineBridges> bridges,
            IHttpClientFactory clientFactory,
            LineWebhookQueue lineRequestQueue,
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
            _logger.LogDebug("LineMessageProcessingService is starting.");
            try
            {
                await foreach (var request in _queue.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        await ProcessRequestAsync(request, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("LINE webhook processing failed; request skipped ({ExceptionType}).", ex.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The host shutdown deadline expired while waiting for more work.
            }
            _logger.LogDebug("LineMessageProcessing background task is stopped.");
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _queue.Complete();

            if (ExecuteTask is { } executeTask)
            {
                try
                {
                    await executeTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("LINE webhook queue did not drain before the shutdown deadline; cancelling active work.");
                    await base.StopAsync(cancellationToken);
                    return;
                }
            }

            await base.StopAsync(cancellationToken);
        }

        private async Task ProcessRequestAsync(LineWebhookRequest request, CancellationToken cancellationToken)
        {
            using var bodyStream = new MemoryStream(request.Body, writable: false);
            using var document = await JsonDocument.ParseAsync(bodyStream, cancellationToken: cancellationToken);
            var data = document.RootElement;
            foreach (var e in data.GetProperty("events").EnumerateArray())
            {
                switch (e.GetProperty("type").GetString())
                {
                    case "message":
                        {
                            LineChannel lineChannel = GetLineChannel(e);
                            if (lineChannel == null)
                            {
                                _logger.LogInformation("Message received from an unknown LINE channel.");
                                continue;
                            }

                            var bridges = GetBridges(lineChannel);
                            if (!bridges.Any())
                            {
                                continue;
                            }
                            if (e.TryGetProperty("replyToken", out var replyTokenElement))
                            {
                                var replyToken = replyTokenElement.GetString();
                                if (!string.IsNullOrEmpty(replyToken))
                                {
                                    _lastReplyTokens.AddOrUpdate(lineChannel.Id, replyToken, (key, value) => replyToken);
                                }
                            }
                            var (userName, pictureUrl) = await GetLineProfileAsync(e, cancellationToken);

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
                                    imageUrl = $"https://{request.Host}/proxy/line/{Crypt.GetHMACHex(id, _lineChannelSecret)}/{id}";
                                    break;
                                default:
                                    text = $"<{type}>";
                                    if (!string.IsNullOrEmpty(id))
                                    {
                                        text += $"\nhttps://{request.Host}/proxy/line/{Crypt.GetHMACHex(id, _lineChannelSecret)}/{id}";
                                    }
                                    break;
                            }

                            foreach (var bridge in bridges)
                            {
                                var slackChannel = _slackChannels.CurrentValue.Channels.FirstOrDefault(x => x.Name == bridge.Slack);
                                if (slackChannel == null)
                                {
                                    _logger.LogError("Bridge configuration refers to an unknown Slack channel.");
                                    continue;
                                }

                                await SendToSlack(slackChannel.WebhookUrl, slackChannel.ChannelId, pictureUrl, userName, text, imageUrl, cancellationToken);
                            }
                        }
                        break;

                    default:
                        {
                            var sourceId = GetLineEventSourceId(e);
                            if (sourceId == null)
                            {
                                var type = e.GetProperty("type").GetString();
                                _logger.LogInformation("Ignored LINE {EventType} event from an unknown source type.", type);
                                continue;
                            }
                        }
                        break;
                }
            }
        }

        public string GetReplyToken(string lineChannelID)
        {
            if (_lastReplyTokens.TryRemove(lineChannelID, out var token))
            {
                return token;
            }

            return null;
        }

        private async Task SendToSlack(string webhookUrl, string channelId, string pictureUrl, string userName, string text, string imageUrl, CancellationToken cancellationToken)
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

            using var content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>(message)), Encoding.UTF8, "application/json");
            using var result = await client.PostAsync(webhookUrl, content, cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                _logger.LogError("Slack webhook returned non-success status {StatusCode}.", result.StatusCode);
                throw new HttpRequestException("Slack webhook returned a non-success status.");
            }

            _logger.LogInformation("Post to Slack returned status {StatusCode}.", result.StatusCode);
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

        private async Task<(string userName, string pictureUrl)> GetLineProfileAsync(JsonElement e, CancellationToken cancellationToken)
        {
            var source = e.GetProperty("source");
            var type = source.GetProperty("type").GetString();
            var client = _clientFactory.CreateClient("Line");
            var resultProfile = (userName: "", pictureUrl: "");
            var userId = source.GetProperty("userId").GetString();
            string profilePath = null;
            switch (type)
            {
                case "user":
                    profilePath = $"profile/{userId}";
                    break;
                case "group":
                    var groupId = source.GetProperty("groupId").GetString();
                    profilePath = $"group/{groupId}/member/{userId}";
                    break;
                case "room":
                    var roomId = source.GetProperty("roomId").GetString();
                    profilePath = $"room/{roomId}/member/{userId}";
                    break;
                default:
                    _logger.LogError("Unknown LINE profile source type.");
                    throw new InvalidOperationException("Unknown LINE profile source type.");
            }

            try
            {
                using var result = await client.GetAsync(profilePath, cancellationToken);
                if (!result.IsSuccessStatusCode)
                {
                    _logger.LogWarning("LINE profile request returned non-success status {StatusCode}; using fallback profile.", result.StatusCode);
                    return ($"Unknown ({userId})", "");
                }

                await using var profileStream = await result.Content.ReadAsStreamAsync(cancellationToken);
                var profile = await JsonSerializer.DeserializeAsync<JsonElement>(profileStream, cancellationToken: cancellationToken);

                resultProfile.userName = profile.GetProperty("displayName").GetString();
                if (profile.TryGetProperty("pictureUrl", out var picture))
                {
                    resultProfile.pictureUrl = picture.GetString();
                }

                if (string.IsNullOrEmpty(resultProfile.userName))
                {
                    resultProfile.userName = $"Unknown ({userId})";
                }

                return resultProfile;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not retrieve LINE profile; using fallback profile ({ExceptionType}).", ex.GetType().Name);
                return ($"Unknown ({userId})", "");
            }
        }
    }
}
