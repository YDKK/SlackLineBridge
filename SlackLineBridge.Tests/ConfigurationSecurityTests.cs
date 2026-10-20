using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentry;
using SlackLineBridge.Models.Configurations;
using Xunit;

namespace SlackLineBridge.Tests;

public sealed class ConfigurationSecurityTests
{
    // Sentry SDKのevent JSONにURL、query、header、cookieを残さない。
    [Fact]
    public void SentryPrivacyFilterRemovesRequestMetadata()
    {
        const string urlMarker = "private-request-url-marker";
        const string queryMarker = "private-query-marker";
        const string headerMarker = "private-header-marker";
        const string cookieMarker = "private-cookie-marker";
        var request = new SentryRequest
        {
            Url = $"https://bridge.example.com/proxy/{urlMarker}?value={queryMarker}",
            QueryString = queryMarker,
            Cookies = cookieMarker
        };
        request.Headers["Authorization"] = headerMarker;
        var sentryEvent = new SentryEvent { Request = request };

        var filtered = SentryPrivacyFilter.RemoveRequestMetadata(sentryEvent);
        using var stream = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);
        filtered.WriteTo(writer, null);
        writer.Flush();
        var serialized = Encoding.UTF8.GetString(stream.ToArray());

        Assert.DoesNotContain(urlMarker, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(queryMarker, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(headerMarker, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(cookieMarker, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"request\"", serialized, StringComparison.Ordinal);
    }

    // SentryのHTTP breadcrumbを落とし、通常のbreadcrumbは維持する。
    [Fact]
    public void SentryPrivacyFilterDropsHttpBreadcrumbs()
    {
        var httpBreadcrumb = new Breadcrumb(
            "GET private URL",
            "http",
            new Dictionary<string, string> { ["url"] = "https://bridge.example.com/private-token" },
            "http",
            BreadcrumbLevel.Info);
        var appBreadcrumb = new Breadcrumb("application event", "default", null, "app", BreadcrumbLevel.Info);

        Assert.Null(SentryPrivacyFilter.DropHttpBreadcrumb(httpBreadcrumb));
        Assert.Same(appBreadcrumb, SentryPrivacyFilter.DropHttpBreadcrumb(appBreadcrumb));
    }

    // DI経由の既定、Line、Slack、SlackMedia clientのログからURL tokenを除く。
    [Fact]
    public async Task HttpClientFactoryDoesNotLogSecretUrls()
    {
        const string secretMarker = "private-url-token-marker";
        var settings = CreateValidSettings();
        settings["Logging:LogLevel:Default"] = "Trace";
        settings["Logging:LogLevel:Microsoft"] = "Trace";
        var loggerProvider = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        new Startup(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()).ConfigureServices(services);
        services.AddLogging(logging => logging.AddProvider(loggerProvider));

        foreach (var name in new[] { "", "Line", "Slack", "SlackMedia" })
        {
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
        }

        using var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var requests = new[]
        {
            (Client: "", Url: $"https://default.example/{secretMarker}"),
            (Client: "Line", Url: $"https://api.line.me/v2/bot/{secretMarker}"),
            (Client: "Slack", Url: $"https://slack.example/{secretMarker}"),
            (Client: "SlackMedia", Url: $"https://files.slack.com/{secretMarker}")
        };

        foreach (var request in requests)
        {
            using var client = factory.CreateClient(request.Client);
            using var response = await client.GetAsync(request.Url);
            Assert.Equal(200, (int)response.StatusCode);
        }

        Assert.DoesNotContain(loggerProvider.Entries, entry => entry.Contains(secretMarker, StringComparison.Ordinal));
    }

    // LINEとSlackのsecret型が空白値を拒否することを確認する。
    [Fact]
    public void SecretTypesRejectBlankValues()
    {
        Assert.Throws<ArgumentException>(() => new LineChannelSecret("  "));
        Assert.Throws<ArgumentException>(() => new SlackSigningSecret("\t"));
    }

    // 必須secretやaccess tokenが欠けた設定ではサービス登録を失敗させる。
    [Theory]
    [InlineData("lineChannelSecret")]
    [InlineData("slackSigningSecret")]
    [InlineData("lineAccessToken")]
    [InlineData("slackAccessToken")]
    public void StartupRejectsBlankSecretsAndTokens(string key)
    {
        var settings = CreateValidSettings();
        settings[key] = " ";
        var startup = new Startup(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        var exception = Assert.Throws<InvalidOperationException>(() => startup.ConfigureServices(new ServiceCollection()));

        Assert.DoesNotContain("test-", exception.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> CreateValidSettings()
    {
        return new Dictionary<string, string?>
        {
            ["lineChannelSecret"] = "test-line-secret",
            ["slackSigningSecret"] = "test-slack-secret",
            ["lineAccessToken"] = "test-line-token",
            ["slackAccessToken"] = "test-slack-token",
            ["Logging:UseCloudWatchLogs"] = "false"
        };
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> entries, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var formatted = formatter(state, exception);
                var details = state is IEnumerable<KeyValuePair<string, object?>> values
                    ? string.Join(" ", values.Select(value => $"{value.Key}={value.Value}"))
                    : state is null ? null : state.ToString();
                entries.Enqueue($"{categoryName} {formatted} {details} {exception}");
            }
        }
    }
}
