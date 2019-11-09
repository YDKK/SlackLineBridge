using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SlackLineBridge.Services
{
    public class BackgroundAccessingService : BackgroundService
    {
        private readonly ILogger<BackgroundAccessingService> _logger;
        private readonly IHttpClientFactory _clientFactory;

        public BackgroundAccessingService(ILogger<BackgroundAccessingService> logger, IHttpClientFactory clientFactory)
        {
            _logger = logger;
            _clientFactory = clientFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogDebug($"BackgroundAccessingService is starting.");

            stoppingToken.Register(() => _logger.LogDebug($" BackgroundAccessing background task is stopping."));

            var client = _clientFactory.CreateClient();
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var response = await client.PostAsync("http://localhost/line", new StringContent(""));
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("BackgroundAccessing failed.");

                    }
                }
                catch
                {
                    _logger.LogWarning("BackgroundAccessing failed.");
                }

                await Task.Delay(1000 * 60, stoppingToken);
            }

            _logger.LogDebug($"BackgroundAccessing background task is stopped.");
        }
    }
}
