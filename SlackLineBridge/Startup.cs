using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Amazon.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SlackLineBridge.Models.Configurations;
using SlackLineBridge.Services;

namespace SlackLineBridge
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(x =>
            {
                x.AddConfiguration(Configuration.GetSection("Logging"));
                x.AddConsole();
                if (Configuration.GetValue<bool>("Logging:UseCloudWatchLogs"))
                {
                    var awsLoggingConfig = Configuration.GetAWSLoggingConfigSection();
                    var awsConfig = Configuration.GetSection("AWS");
                    if (awsConfig.Exists())
                    {
                        var accessKey = Configuration.GetValue<string>("AWS:AccessKey");
                        var secretKey = Configuration.GetValue<string>("AWS:SecretKey");
                        awsLoggingConfig.Config.Credentials = new BasicAWSCredentials(accessKey, secretKey);
                    }
                    x.AddAWSProvider(awsLoggingConfig);
                }
            });
            services.AddControllers();
            services.AddHttpClient("Line", c =>
            {
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Configuration["lineAccessToken"]);
                c.BaseAddress = new Uri("https://api.line.me/v2/bot/");
            });
            services.Configure<SlackChannels>(x => x.Channels = Configuration.GetSection("slackChannels").Get<SlackChannel[]>());
            services.Configure<LineChannels>(x => x.Channels = Configuration.GetSection("lineChannels").Get<LineChannel[]>());
            services.Configure<SlackLineBridges>(x => x.Bridges = Configuration.GetSection("slackLineBridges").Get<Models.Configurations.SlackLineBridge[]>());
            services.AddSingleton<ConcurrentQueue<(string signature, string body, string host)>>(); //lineRequestQueue
            services.AddSingleton(Configuration["lineChannelSecret"]);
            services.AddHostedService<LineMessageProcessingService>();
            services.AddHostedService<BackgroundAccessingService>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}
