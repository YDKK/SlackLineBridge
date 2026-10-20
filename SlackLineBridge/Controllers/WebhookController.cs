using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Services;
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
        LineMessageProcessingService lineMessageProcessingService,
        LineWebhookQueue lineRequestQueue,
        IHttpClientFactory clientFactory,
        LineChannelSecret lineChannelSecret,
        SlackSigningSecret slackSigningSecret,
        JsonSerializerOptions jsonOptions) : ControllerBase
    {
        private const int MaximumWebhookBodySize = 1024 * 1024;
        private const int SlackTimestampToleranceSeconds = 5 * 60;

        private readonly SlackChannels _slackChannels = slackChannels.Value;
        private readonly LineChannels _lineChannels = lineChannels.Value;
        private readonly SlackLineBridges _bridges = bridges.Value;
        private readonly string _slackSigningSecret = slackSigningSecret.Secret;
        private readonly string _lineChannelSecret = lineChannelSecret.Secret;
        private readonly LineMessageProcessingService _lineMessageProcessingService = lineMessageProcessingService;
        private readonly LineWebhookQueue _lineRequestQueue = lineRequestQueue;

        [HttpPost("/slack2")]
        public async Task<IActionResult> Slack2()
        {
            if (!TryGetSingleHeaderValue("X-Slack-Request-Timestamp", out var timestampHeader)
                || !TryGetSingleHeaderValue("X-Slack-Signature", out var providedSignature)
                || !long.TryParse(timestampHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
            {
                return BadRequest();
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (timestamp < now - SlackTimestampToleranceSeconds || timestamp > now + SlackTimestampToleranceSeconds)
            {
                return BadRequest();
            }

            var body = await ReadBoundedBodyAsync(Request, HttpContext.RequestAborted);
            if (body == null)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var signaturePrefix = Encoding.UTF8.GetBytes($"v0:{timestampHeader}:");
            var signedBody = new byte[signaturePrefix.Length + body.Length];
            Buffer.BlockCopy(signaturePrefix, 0, signedBody, 0, signaturePrefix.Length);
            Buffer.BlockCopy(body, 0, signedBody, signaturePrefix.Length, body.Length);
            var expectedSignature = $"v0={Crypt.GetHMACHex(signedBody, _slackSigningSecret)}";
            if (!Crypt.FixedTimeEqualsAscii(expectedSignature, providedSignature))
            {
                logger.LogInformation("Slack request rejected because its signature is invalid.");
                return BadRequest();
            }

            // Slackの再送応答は、署名と時刻を検証した後に返す。
            if (Request.Headers.ContainsKey("X-Slack-Retry-Reason"))
            {
                if (!TryGetSingleHeaderValue("X-Slack-Retry-Reason", out var retryReason))
                {
                    return BadRequest();
                }

                if (retryReason == "http_timeout")
                {
                    return Ok();
                }
            }

            string json;
            dynamic data;
            try
            {
                json = DecodeJsonBody(body);
                using var document = JsonDocument.Parse(json);
                data = JsonSerializer.Deserialize<dynamic>(json, jsonOptions);
            }
            catch (JsonException)
            {
                return BadRequest();
            }
            catch (DecoderFallbackException)
            {
                return BadRequest();
            }

            if (data == null)
            {
                return BadRequest();
            }

            logger.LogInformation("Processing authenticated request from Slack.");
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
                                logger.LogInformation("Ignoring a message from an unconfigured Slack channel.");
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

            return BadRequest();
        }

        private bool TryGetSingleHeaderValue(string headerName, out string value)
        {
            var values = Request.Headers[headerName];
            if (values.Count == 1 && !string.IsNullOrEmpty(values[0]))
            {
                value = values[0];
                return true;
            }

            value = null;
            return false;
        }

        private static async Task<byte[]> ReadBoundedBodyAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ContentLength > MaximumWebhookBodySize)
            {
                return null;
            }

            using var bodyBuffer = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var remainingWithOverflowCheck = MaximumWebhookBodySize + 1 - (int)bodyBuffer.Length;
                var read = await request.Body.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remainingWithOverflowCheck)),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0)
                {
                    return bodyBuffer.ToArray();
                }

                if (bodyBuffer.Length + read > MaximumWebhookBodySize)
                {
                    return null;
                }

                bodyBuffer.Write(buffer, 0, read);
            }
        }

        private static string DecodeJsonBody(byte[] body)
        {
            var offset = HasUtf8Bom(body) ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(body, offset, body.Length - offset);
        }

        private static bool HasUtf8Bom(byte[] body) =>
            body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF;

        private static bool IsValidJson(byte[] body)
        {
            try
            {
                var offset = HasUtf8Bom(body) ? 3 : 0;
                using var document = JsonDocument.Parse(body.AsMemory(offset));
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private async Task<(string userName, string icon)> GetSlackUserNameAndIcon(string userId)
        {
            var client = clientFactory.CreateClient("Slack");
            using var result = await client.GetAsync($"https://slack.com/api/users.profile.get?user={Uri.EscapeDataString(userId)}");
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

        [GeneratedRegex(@"\<(?<url>http[^|>]+)[^>\n]*\>", RegexOptions.NonBacktracking)]
        private static partial Regex UrlRegex();

        private async Task<IActionResult> PushToLine(string host, SlackChannel slackChannel, string userIconUrl, string userName, string text, SlackFile[] files = null)
        {
            var bridges = GetBridges(slackChannel);
            if (!bridges.Any())
            {
                return Ok();
            }

            //URLタグを抽出
            text = UrlRegex().Replace(text, "${url}");

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
                    var messages = new List<dynamic>
                    {
                        new
                        {
                            type = "text",
                            altText = text,
                            text,
                            sender = new
                            {
                                name = userName,
                                iconUrl = $"https://{host}/proxy/slack/{Crypt.GetHMACHex(userIconUrl, _slackSigningSecret)}/{HttpUtility.UrlEncode(userIconUrl)}"
                            },
                        }
                    };

                    if (files != null)
                    {
                        var fileMessages = files.Where(x => x.mimeType.StartsWith("image")).Select(file =>
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
                        messages.AddRange(fileMessages);
                    }

                    var replyToken = _lineMessageProcessingService.GetReplyToken(lineChannel.Id);
                    if (replyToken != null)
                    {
                        var json = new
                        {
                            replyToken,
                            messages = messages.ToArray()
                        };
                        var jsonStr = JsonSerializer.Serialize(json);
                        using var result = await client.PostAsync($"message/reply", new StringContent(jsonStr, Encoding.UTF8, "application/json"));
                        logger.LogInformation("LINE reply request returned status {StatusCode}.", result.StatusCode);
                        if (result.IsSuccessStatusCode)
                        {
                            continue;
                        }
                    }

                    // 通常のプッシュメッセージにフォールバック
                    {
                        var json = new
                        {
                            to = lineChannel.Id,
                            messages = messages.ToArray()
                        };
                        var jsonStr = JsonSerializer.Serialize(json);
                        using var result = await client.PostAsync($"message/push", new StringContent(jsonStr, Encoding.UTF8, "application/json"));
                        logger.LogInformation("LINE push request returned status {StatusCode}.", result.StatusCode);
                    }
                }
            }
            return Ok();
        }

        [HttpPost("/line")]
        public async Task<IActionResult> LineAsync()
        {
            if (!TryGetSingleHeaderValue("X-Line-Signature", out var signature))
            {
                logger.LogInformation("LINE webhook rejected because its signature header is missing or repeated.");
                return BadRequest();
            }

            var body = await ReadBoundedBodyAsync(Request, HttpContext.RequestAborted);
            if (body == null)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            if (!IsValidLineSignature(body, signature))
            {
                logger.LogInformation("LINE webhook rejected because its signature is invalid.");
                return BadRequest();
            }

            if (!IsValidJson(body))
            {
                logger.LogInformation("LINE webhook rejected because its JSON body is invalid.");
                return BadRequest();
            }

            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            if (!_lineRequestQueue.TryEnqueue(new LineWebhookRequest(body, Request.Host.ToString())))
            {
                logger.LogWarning("LINE webhook queue is full or closed; rejecting request.");
                return StatusCode(503);
            }

            return Ok();
        }

        private bool IsValidLineSignature(byte[] body, string providedSignature)
        {
            Span<byte> providedHash = stackalloc byte[32];
            if (!Convert.TryFromBase64String(providedSignature, providedHash, out var bytesWritten) || bytesWritten != providedHash.Length)
            {
                return false;
            }

            var expectedHash = Crypt.CalcHMAC(body, _lineChannelSecret);
            return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
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
